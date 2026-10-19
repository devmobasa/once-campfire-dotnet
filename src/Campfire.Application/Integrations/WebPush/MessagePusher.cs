using Campfire.Application.Data;
using Campfire.Application.Data.Hooks;
using Campfire.Application.Memberships;
using Campfire.Application.Pushes;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Campfire.Application.Integrations.WebPush;

/// <summary>
/// <c>Room::PushMessageJob#perform(room, message)</c>: <c>Room::MessagePusher.new(room:, message:).push</c>
/// (<c>app/models/room/message_pusher.rb</c>). The payload is the room's name and "creator: body" for a shared room,
/// or the creator's name and the body for a direct one; it goes to the subscriptions of the visible, disconnected
/// members other than the creator who follow everything, then to those who follow mentions and were mentioned. Each
/// notification is built here, with its subscriber's badge, and delivered on the <see cref="WebPushPool"/>.
/// </summary>
public sealed class MessagePusher( CampfireDatabase database, IMessageBodyText bodyText, WebPushPool pool )
{
    /// <summary>Builds and queues notifications on a reader. Returns no payload when no recipient is eligible.</summary>
    public async Task<PushPayload?> PushAsync( long messageId, CancellationToken cancellationToken )
    {
        var now = RailsTimestamp.Truncate( database.Clock.GetUtcNow( ).UtcDateTime );
        var (payload, notifications) = await database.ReadAsync( db => Notifications( db, bodyText, messageId, now ), cancellationToken );
        pool.Queue( notifications );
        return payload;
    }

    /// <summary>
    /// Builds the payload only for eligible subscriptions, then each subscriber's notification, everything-involved
    /// subscribers first, each group in id order. Rails counts each subscriber's badge as it builds that notification;
    /// nothing writes in between, so one statement counts them all.
    /// </summary>
    public static (PushPayload? Payload, IReadOnlyList<WebPushNotification> Notifications) Notifications(
        CampfireDb db, IMessageBodyText bodyText, long messageId, DateTime now )
    {
        var push = PushSubscriptionOperations.PushFor( db, bodyText, messageId, now );
        if ( push.Payload is not { } payload )
        {
            return (null, []);
        }

        var subscriptions = push.Everything.Concat( push.Mentions ).ToList( );
        List<long> subscriberIds = [.. subscriptions.Select( subscription => subscription.UserId ).Distinct( )];
        var badges = subscriberIds.Count == 0 ? [] : MembershipQueries.UnreadCounts( db, subscriberIds );
        var notifications = subscriptions
            .Select( subscription => WebPushNotification.For(
                subscription,
                payload.Title,
                payload.Body,
                payload.Path,
                badge: badges.GetValueOrDefault( subscription.UserId ) ) )
            .ToList( );
        return (push.Payload, notifications);
    }

    /// <summary>
    /// The pool's invalid-subscription handler from <c>config/initializers/web_push.rb</c>: logs
    /// "Destroying push subscription: id" and runs <c>Push::Subscription.find_by(id:)&amp;.destroy</c>.
    /// </summary>
    public static Func<long, CancellationToken, Task> DestroySubscriptionHandler( CampfireDatabase database, ILogger? logger = null )
    {
        ArgumentNullException.ThrowIfNull( database );

        var log = logger ?? NullLogger.Instance;
        return ( subscriptionId, cancellationToken ) =>
        {
            if ( log.IsEnabled( LogLevel.Information ) )
            {
                log.LogInformation( "Destroying push subscription: {SubscriptionId}", subscriptionId );
            }

            return database.WriteAsync( scope => PushSubscriptionOperations.DestroyInvalid( scope, subscriptionId ), cancellationToken );
        };
    }
}
