using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json.Nodes;
using Campfire.Application.Integrations.WebPush;
using Campfire.Application.Memberships;
using Campfire.Application.Messages;
using Campfire.Application.Tests.Support;
using Microsoft.Extensions.Logging;

namespace Campfire.Application.Tests.Integrations.WebPush;

/// <summary>
/// <c>WebPush::Pool</c> and <c>Room::MessagePusher</c>, as <c>test/models/room/push_test.rb</c> exercises them: who
/// gets pushed, what they receive, and which subscriptions a push service's refusals destroy.
/// </summary>
public sealed class WebPushPoolTests
{
    private static readonly string[] Subscribers = ["david_chrome", "jason_chrome", "jz_chrome", "kevin_chrome"];

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>50 running and 10,000 waiting are accepted; the rest are dropped and counted.</summary>
    [Fact]
    public async Task Drops_deliveries_past_its_queue( )
    {
        var release = new TaskCompletionSource( TaskCreationOptions.RunContinuationsAsynchronously );
        var pool = new WebPushPool( async ( _, token ) =>
        {
            await release.Task.WaitAsync( token );
            return 201;
        }, ( _, _ ) => Task.CompletedTask, null );
        var notification = new WebPushNotification( "t", "b", "/", 0, 1, "https://fcm.googleapis.com/fcm/send/abc", "k", "a" );

        var accepted = Enumerable.Range( 0, WebPushPool.MaxConcurrency + WebPushPool.MaxQueue + 5 ).Count( _ => pool.DeliverLater( notification ) );

        Assert.Equal( 10_050, accepted );
        Assert.Equal( 10_050, pool.Pending );
        Assert.Equal( 5, pool.Dropped );
        release.SetResult( );
        await pool.ShutdownAsync( TimeSpan.FromSeconds( 30 ) );
        Assert.Equal( 0, pool.Pending );
        Assert.False( pool.DeliverLater( notification ) );
    }

    /// <summary>The destruction worker cannot accumulate more than 10,050 invalid subscriptions.</summary>
    [Fact]
    public async Task Drops_invalidations_past_the_bound_and_exports_the_loss( )
    {
        var release = new TaskCompletionSource( TaskCreationOptions.RunContinuationsAsynchronously );
        var delivered = new TaskCompletionSource( TaskCreationOptions.RunContinuationsAsynchronously );
        var overflow = new TaskCompletionSource( TaskCreationOptions.RunContinuationsAsynchronously );
        var deliveries = 0;
        var destructions = 0;
        long dropped = 0;
        using var listener = new MeterListener( );
        listener.InstrumentPublished = ( instrument, meterListener ) =>
        {
            if ( instrument.Meter.Name == "Campfire.WebPush" && instrument.Name == "campfire.web_push.invalidations_dropped" )
            {
                meterListener.EnableMeasurementEvents( instrument );
            }
        };
        listener.SetMeasurementEventCallback<long>( ( _, count, _, _ ) =>
        {
            if ( Interlocked.Add( ref dropped, count ) == 5 )
            {
                overflow.TrySetResult( );
            }
        } );
        listener.Start( );
        await using var pool = new WebPushPool( ( _, _ ) =>
        {
            if ( Interlocked.Increment( ref deliveries ) == 10_050 )
            {
                delivered.TrySetResult( );
            }

            return Task.FromException<int?>( new WebPushDeliveryException(
                "WebPush::ExpiredSubscription",
                "gone",
                invalidatesSubscription: true,
                status: 410 ) );
        }, async ( _, token ) =>
        {
            await release.Task.WaitAsync( token );
            Interlocked.Increment( ref destructions );
        }, null );

        try
        {
            pool.Queue( Enumerable.Range( 1, 10_050 ).Select( id => new WebPushNotification( "t", "b", "/", 0, id, null, null, null ) ) );
            await delivered.Task.WaitAsync( TimeSpan.FromSeconds( 10 ), Cancellation );
            pool.Queue( Enumerable.Range( 10_051, 5 ).Select( id => new WebPushNotification( "t", "b", "/", 0, id, null, null, null ) ) );
            await overflow.Task.WaitAsync( TimeSpan.FromSeconds( 10 ), Cancellation );
        }
        finally
        {
            release.TrySetResult( );
        }

        await pool.ShutdownAsync( TimeSpan.FromSeconds( 30 ) );

        Assert.Equal( 10_050, destructions );
        Assert.Equal( 5, dropped );
        Assert.Equal( 0, pool.Dropped );
    }

    [Fact]
    public async Task Deletes_only_gone_service_subscriptions_and_logs_local_failures( )
    {
        var invalidated = new List<long>( );
        var log = new DeliveryLog( );
        var pool = new WebPushPool( ( notification, _ ) => notification.SubscriptionId switch
        {
            1 => throw new WebPushDeliveryException( "WebPush::ExpiredSubscription", "gone", invalidatesSubscription: true, status: 410 ),
            2 => throw new WebPushDeliveryException( "OpenSSL::PKey::EC::Point::Error", "bad key", invalidatesSubscription: true ),
            3 => throw new WebPushDeliveryException( "WebPush::InvalidSubscription", "not found", invalidatesSubscription: true, status: 404 ),
            4 => throw new InvalidOperationException( "boom" ),
            _ => Task.FromResult<int?>( 201 ),
        }, ( id, _ ) =>
        {
            lock ( invalidated )
            {
                invalidated.Add( id );
            }

            return Task.CompletedTask;
        }, log );

        pool.Queue( Enumerable.Range( 1, 5 ).Select( id => new WebPushNotification( "t", "b", "/", 0, id, null, null, null ) ) );
        await pool.ShutdownAsync( TimeSpan.FromSeconds( 30 ) );

        Assert.Equal( [1, 3], invalidated.Order( ) );
        Assert.Contains( log.Messages, message => message.Contains( "OpenSSL::PKey::EC::Point::Error", StringComparison.Ordinal ) );
        Assert.Contains( log.Messages, message => message.Contains( "InvalidOperationException", StringComparison.Ordinal ) );
    }

    private sealed class DeliveryLog : ILogger
    {
        public ConcurrentQueue<string> Messages { get; } = new( );

        public IDisposable? BeginScope<TState>( TState state ) where TState : notnull => null;

        public bool IsEnabled( LogLevel logLevel ) => true;

        public void Log<TState>( LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter ) =>
            Messages.Enqueue( formatter( state, exception ) );
    }

    [Fact]
    public async Task Abandons_what_it_cannot_drain_by_the_deadline( )
    {
        var pool = new WebPushPool( async ( _, token ) =>
        {
            await Task.Delay( Timeout.Infinite, token );
            return 201;
        }, ( _, _ ) => Task.CompletedTask, null );
        pool.Queue( Enumerable.Range( 1, 60 ).Select( id => new WebPushNotification( "t", "b", "/", 0, id, null, null, null ) ) );

        var started = TimeProvider.System.GetTimestamp( );
        await pool.ShutdownAsync( TimeSpan.FromMilliseconds( 200 ) );

        Assert.True( TimeProvider.System.GetElapsedTime( started ) < TimeSpan.FromSeconds( 5 ) );
        Assert.Equal( 0, pool.Pending );
    }

    /// <summary>"deliver new message ..." and "notifies subscribed users": two subscribers in designers, encrypted for each.</summary>
    [Fact]
    public async Task Pushes_a_message_to_the_rooms_subscribers( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        var receivers = await SubscribeAsync( testDatabase );
        await using var service = FakePushService.Start( );
        using var sender = service.Sender( );
        var destroyed = new List<long>( );
        var pool = new WebPushPool( sender, ( id, _ ) =>
        {
            destroyed.Add( id );
            return Task.CompletedTask;
        } );
        var pusher = new MessagePusher( testDatabase.Database, testDatabase.BodyText, pool );

        var payload = await pusher.PushAsync( (await PostAsync( testDatabase, "This is from earth" )).Id, Cancellation );
        await pool.ShutdownAsync( TimeSpan.FromSeconds( 30 ) );

        Assert.NotNull( payload );
        Assert.Equal( ("Designers", "David: This is from earth"), (payload.Title, payload.Body) );
        Assert.Equal( 2, service.Server.Received.Count );
        foreach ( var request in service.Server.Received )
        {
            var receiver = receivers[request.Target];
            var message = JsonNode.Parse( receiver.Open( request.Body ) )!;
            Assert.Equal( "Designers", message["title"]!.GetValue<string>( ) );
            Assert.Equal( payload.Path, message["options"]!["data"]!["path"]!.GetValue<string>( ) );
        }

        Assert.Empty( destroyed );
    }

    /// <summary>"destroys invalid subscriptions": each 410 destroys its subscription through the pool's handler.</summary>
    [Fact]
    public async Task Destroys_expired_subscriptions( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        await SubscribeAsync( testDatabase );
        await testDatabase.WriteAsync( scope => MembershipOperations.UpdateInvolvement( scope, TestDatabase.Id( "kevin_designers" ), Involvement.Invisible ) );
        await using var service = FakePushService.Start( 410, "Gone" );
        using var sender = service.Sender( );
        var pool = new WebPushPool( sender, MessagePusher.DestroySubscriptionHandler( testDatabase.Database ) );
        var before = await testDatabase.CountAsync( "push_subscriptions" );

        await new MessagePusher( testDatabase.Database, testDatabase.BodyText, pool ).PushAsync(
            (await PostAsync( testDatabase, "Hey @kevin" )).Id,
            Cancellation );
        await pool.ShutdownAsync( TimeSpan.FromSeconds( 30 ) );

        Assert.Equal( before - 2, await testDatabase.CountAsync( "push_subscriptions" ) );
    }

    private static Task<Message> PostAsync( TestDatabase testDatabase, string body ) =>
        testDatabase.PostMessageAsync( "designers", "david", body, clientMessageId: "earth" );

    /// <summary>Gives each fixture subscription real keys, by its endpoint path.</summary>
    private static async Task<Dictionary<string, PushReceiver>> SubscribeAsync( TestDatabase testDatabase )
    {
        var receivers = new Dictionary<string, PushReceiver>( );
        foreach ( var label in Subscribers )
        {
            var receiver = new PushReceiver( );
            var id = TestDatabase.Id( label );
            var endpoint = (await testDatabase.ScalarAsync<string>( "SELECT endpoint FROM push_subscriptions WHERE id = @p0", id ))!;
            receivers[new Uri( endpoint ).AbsolutePath] = receiver;
            await testDatabase.ExecuteAsync(
                "UPDATE push_subscriptions SET p256dh_key = @p0, auth_key = @p1 WHERE id = @p2",
                receiver.P256dh,
                receiver.AuthKey,
                id );
        }

        return receivers;
    }
}
