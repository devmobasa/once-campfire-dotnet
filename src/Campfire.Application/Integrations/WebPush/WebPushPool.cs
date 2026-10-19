using System.Diagnostics.Metrics;
using System.Threading.Channels;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Campfire.Application.Integrations.WebPush;

/// <summary>
/// <c>WebPush::Pool</c> (<c>lib/web_push/pool.rb</c>): up to <see cref="MaxConcurrency"/> deliveries at once and
/// <see cref="MaxQueue"/> more waiting, as <c>Concurrent::ThreadPoolExecutor.new(max_threads: 50, max_queue: 10000)</c>;
/// a notification past that is dropped (Rails drops it silently on <c>RejectedExecutionError</c>; this port counts it
/// in <c>campfire.web_push.dropped</c>). One worker runs the invalid-subscription handler in order, as the gem's
/// <c>FixedThreadPool(1)</c> does, for subscriptions that the push service rejected with 404 or 410.
/// </summary>
/// <remarks>
/// A process crash loses what's queued, as a Rails restart does. <see cref="ShutdownAsync"/> stops taking
/// work, drains what's queued within its deadline, then abandons the rest and logs how much.
/// </remarks>
public sealed class WebPushPool : IAsyncDisposable
{
    /// <summary><c>max_threads: 50</c>.</summary>
    public const int MaxConcurrency = 50;

    /// <summary><c>max_queue: 10000</c>.</summary>
    public const int MaxQueue = 10_000;

    private static readonly Meter Meter = new( "Campfire.WebPush" );

    private static readonly Counter<long> DroppedDeliveries = Meter.CreateCounter<long>(
        "campfire.web_push.dropped",
        description: "Push deliveries dropped because the pool was full" );

    private static readonly Counter<long> DroppedInvalidations = Meter.CreateCounter<long>(
        "campfire.web_push.invalidations_dropped",
        description: "Invalid subscriptions not destroyed because the invalidation queue was full" );

    private readonly Func<WebPushNotification, CancellationToken, Task<int?>> _deliver;

    private readonly Func<long, CancellationToken, Task> _invalidSubscriptionHandler;

    private readonly ILogger _logger;

    private readonly Channel<WebPushNotification> _deliveries = Channel.CreateUnbounded<WebPushNotification>( );

    private readonly Channel<long> _invalidations = Channel.CreateUnbounded<long>( new UnboundedChannelOptions { SingleReader = true } );

    private readonly CancellationTokenSource _abandon = new( );

    private readonly Task[] _workers;

    private readonly Task _invalidator;

    private int _pending;

    private int _pendingInvalidations;

    private long _dropped;

    private int _shutDown;

    /// <param name="sender">Delivers each notification.</param>
    /// <param name="invalidSubscriptionHandler">
    /// <c>Push::Subscription.find_by(id:)&amp;.destroy</c> (<see cref="MessagePusher.DestroySubscriptionHandler"/>).
    /// </param>
    /// <param name="logger">Where delivery errors go (<c>Rails.logger.error</c>).</param>
    public WebPushPool( WebPushSender sender, Func<long, CancellationToken, Task> invalidSubscriptionHandler, ILogger<WebPushPool>? logger = null )
        : this( sender.DeliverAsync, invalidSubscriptionHandler, logger )
    {
        ArgumentNullException.ThrowIfNull( sender );
    }

    internal WebPushPool(
        Func<WebPushNotification, CancellationToken, Task<int?>> deliver,
        Func<long, CancellationToken, Task> invalidSubscriptionHandler,
        ILogger? logger )
    {
        ArgumentNullException.ThrowIfNull( deliver );
        ArgumentNullException.ThrowIfNull( invalidSubscriptionHandler );

        _deliver = deliver;
        _invalidSubscriptionHandler = invalidSubscriptionHandler;
        _logger = logger ?? NullLogger.Instance;
        _workers = [.. Enumerable.Range( 0, MaxConcurrency ).Select( _ => Task.Run( DeliverQueuedAsync ) )];
        _invalidator = Task.Run( InvalidateQueuedAsync );
    }

    /// <summary>Deliveries queued or running.</summary>
    public int Pending => Volatile.Read( ref _pending );

    /// <summary>Deliveries dropped because the pool was full or shut down.</summary>
    public long Dropped => Interlocked.Read( ref _dropped );

    /// <summary><c>queue(payload, subscriptions)</c>: each notification in order (<c>find_each</c> goes by id).</summary>
    public void Queue( IEnumerable<WebPushNotification> notifications )
    {
        ArgumentNullException.ThrowIfNull( notifications );

        foreach ( var notification in notifications )
        {
            DeliverLater( notification );
        }
    }

    /// <summary><c>deliver_later</c>: false when the notification was dropped.</summary>
    public bool DeliverLater( WebPushNotification notification )
    {
        ArgumentNullException.ThrowIfNull( notification );

        if ( Volatile.Read( ref _shutDown ) != 0 )
        {
            return Drop( );
        }

        if ( Interlocked.Increment( ref _pending ) > MaxConcurrency + MaxQueue || !_deliveries.Writer.TryWrite( notification ) )
        {
            Interlocked.Decrement( ref _pending );
            return Drop( );
        }

        return true;
    }

    private bool Drop( )
    {
        Interlocked.Increment( ref _dropped );
        DroppedDeliveries.Add( 1 );
        return false;
    }

    /// <summary>
    /// <c>shutdown</c>: no new work; queued deliveries, then queued invalidations, each get up to
    /// <paramref name="drainTimeout"/> (Rails: <c>wait_for_termination(1)</c>, then <c>kill</c>). What's left is abandoned
    /// and logged.
    /// </summary>
    public async Task ShutdownAsync( TimeSpan drainTimeout )
    {
        if ( Interlocked.Exchange( ref _shutDown, 1 ) != 0 )
        {
            return;
        }

        _deliveries.Writer.TryComplete( );
        if ( !await DrainedAsync( Task.WhenAll( _workers ), drainTimeout ) )
        {
            _logger.LogWarning( "Web Push pool shut down with {Pending} deliveries undelivered", Pending );
            await _abandon.CancelAsync( );
            await DrainedAsync( Task.WhenAll( _workers ), drainTimeout );
        }

        _invalidations.Writer.TryComplete( );
        if ( !await DrainedAsync( _invalidator, drainTimeout ) )
        {
            _logger.LogWarning( "Web Push pool shut down with {Pending} invalid subscriptions not destroyed", Volatile.Read( ref _pendingInvalidations ) );
            await _abandon.CancelAsync( );
        }
    }

    /// <summary>Shuts down with Rails' one second of grace.</summary>
    public async ValueTask DisposeAsync( )
    {
        await ShutdownAsync( TimeSpan.FromSeconds( 1 ) );
        _abandon.Dispose( );
    }

    private static async Task<bool> DrainedAsync( Task work, TimeSpan timeout )
    {
        try
        {
            await work.WaitAsync( timeout );
            return true;
        }
        catch ( TimeoutException )
        {
            return false;
        }
    }

    private async Task DeliverQueuedAsync( )
    {
        await foreach ( var notification in _deliveries.Reader.ReadAllAsync( ) )
        {
            try
            {
                if ( !_abandon.IsCancellationRequested )
                {
                    await DeliverAsync( notification );
                }
            }
            finally
            {
                Interlocked.Decrement( ref _pending );
            }
        }
    }

    /// <summary><c>deliver(notification, id)</c>: invalid subscriptions go to the handler, other errors to the log.</summary>
    private async Task DeliverAsync( WebPushNotification notification )
    {
        try
        {
            await _deliver( notification, _abandon.Token );
        }
        catch ( WebPushDeliveryException error ) when ( error.InvalidatesSubscription && error.Status is 404 or 410 )
        {
            InvalidateLater( notification.SubscriptionId );
        }
        catch ( OperationCanceledException ) when ( _abandon.IsCancellationRequested )
        {
            // abandoned at shutdown
        }
        catch ( Exception error )
        {
            var rubyClass = error is WebPushDeliveryException push ? push.RubyClass : error.GetType( ).Name;
            _logger.LogError( error, "Error in WebPush::Pool.deliver: {Class} {Message}", rubyClass, error.Message );
        }
    }

    /// <summary><c>invalidate_subscription_later(id)</c>.</summary>
    private void InvalidateLater( long subscriptionId )
    {
        if ( Interlocked.Increment( ref _pendingInvalidations ) > MaxConcurrency + MaxQueue || !_invalidations.Writer.TryWrite( subscriptionId ) )
        {
            Interlocked.Decrement( ref _pendingInvalidations );
            DroppedInvalidations.Add( 1 );
        }
    }

    private async Task InvalidateQueuedAsync( )
    {
        await foreach ( var subscriptionId in _invalidations.Reader.ReadAllAsync( ) )
        {
            try
            {
                if ( !_abandon.IsCancellationRequested )
                {
                    await _invalidSubscriptionHandler( subscriptionId, _abandon.Token );
                }
            }
            catch ( OperationCanceledException ) when ( _abandon.IsCancellationRequested )
            {
                // abandoned at shutdown
            }
            catch ( Exception error )
            {
                _logger.LogError( error, "Error in WebPush::Pool.invalid_subscription_handler: {Class} {Message}", error.GetType( ).Name, error.Message );
            }
            finally
            {
                Interlocked.Decrement( ref _pendingInvalidations );
            }
        }
    }
}
