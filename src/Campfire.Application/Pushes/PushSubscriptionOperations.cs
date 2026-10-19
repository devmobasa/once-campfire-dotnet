using Campfire.Application.Data;
using Campfire.Application.Data.Hooks;
using Campfire.Application.Memberships;
using Campfire.Application.Messages;
using Campfire.Application.Rooms;
using Campfire.Application.Users;

using Dapper;

namespace Campfire.Application.Pushes;

/// <summary>Writes and reads of <c>push_subscriptions</c>.</summary>
public static class PushSubscriptionOperations
{
    /// <summary>
    /// <c>relevant_subscriptions.merge(Membership.involved_in_*)</c>: subscriptions joined to their user's memberships
    /// in the room, active, visible, disconnected, and not the creator's, in id order.
    /// </summary>
    private const string RelevantSql =
        $"SELECT {Columns.PushSubscriptions} FROM push_subscriptions " +
        "INNER JOIN users ON push_subscriptions.user_id = users.id INNER JOIN memberships ON users.id = memberships.user_id " +
        "WHERE (memberships.connected_at IS NULL OR memberships.connected_at < @cutoff) AND memberships.room_id = @roomId " +
        "AND memberships.user_id <> @creatorId AND memberships.involvement = @involvement AND users.status = @active";

    /// <summary><c>Current.user.push_subscriptions.destroy_by(id:)</c>.</summary>
    public static void Destroy( WriteScope scope, long userId, long subscriptionId )
    {
        ArgumentNullException.ThrowIfNull( scope );

        scope.Db.Connection.Execute(
            "DELETE FROM push_subscriptions WHERE user_id = @userId AND id = @subscriptionId", new
            {
                userId,
                subscriptionId
            }, scope.Db.Transaction );
    }

    /// <summary><c>Push::Subscription.destroy_by(endpoint:, user_id:)</c> (sign-out).</summary>
    public static void DestroyByEndpoint( WriteScope scope, long userId, string endpoint )
    {
        ArgumentNullException.ThrowIfNull( scope );

        scope.Db.Connection.Execute(
            "DELETE FROM push_subscriptions WHERE endpoint = @endpoint AND user_id = @userId", new
            {
                endpoint,
                userId
            }, scope.Db.Transaction );
    }

    /// <summary>The delivery pool's invalid-subscription handler: <c>Push::Subscription.destroy_by(id:)</c>.</summary>
    public static void DestroyInvalid( WriteScope scope, long subscriptionId )
    {
        ArgumentNullException.ThrowIfNull( scope );

        scope.Db.Connection.Execute( "DELETE FROM push_subscriptions WHERE id = @subscriptionId", new
        {
            subscriptionId
        }, scope.Db.Transaction );
    }

    /// <summary><c>user.push_subscriptions</c>, in id order.</summary>
    public static List<PushSubscription> ForUser( CampfireDb db, long userId )
    {
        ArgumentNullException.ThrowIfNull( db );

        return db.Connection.Query<PushSubscription>(
                $"SELECT {Columns.PushSubscriptions} FROM push_subscriptions " +
                "WHERE push_subscriptions.user_id = @userId ORDER BY push_subscriptions.id",
                new
                {
                    userId
                },
                db.Transaction )
            .AsList( );
    }

    /// <summary>
    /// <c>Room::PushMessageJob</c> → <c>Room::MessagePusher#push</c>: the payload, the visible, disconnected members
    /// other than the creator who follow everything, then those who follow mentions and were mentioned.
    /// </summary>
    public static MessagePush PushFor( CampfireDb db, IMessageBodyText bodyText, long messageId, DateTime now )
    {
        ArgumentNullException.ThrowIfNull( db );

        var message = MessageQueries.Find( db, messageId );
        var cutoff = Membership.ConnectionCutoff( now );
        var everything = db.Connection.Query<PushSubscription>(
                $"{RelevantSql} ORDER BY push_subscriptions.id",
                new
                {
                    cutoff,
                    roomId = message.RoomId,
                    creatorId = message.CreatorId,
                    active = UserStatus.Active,
                    involvement = Involvement.Everything
                },
                db.Transaction )
            .AsList( );
        var mentioneeIds = MessageBodies.Mentionees( db, bodyText, message ).Select( user => user.Id ).ToList( );
        var mentions = mentioneeIds.Count == 0
            ? []
            : db.Connection.Query<PushSubscription>(
                    $"{RelevantSql} AND push_subscriptions.user_id IN (SELECT value FROM json_each(@mentioneeIds)) ORDER BY push_subscriptions.id",
                    new
                    {
                        cutoff,
                        roomId = message.RoomId,
                        creatorId = message.CreatorId,
                        active = UserStatus.Active,
                        involvement = Involvement.Mentions,
                        mentioneeIds = IdLists.Json( mentioneeIds ),
                    },
                    db.Transaction )
                .AsList( );
        if ( everything.Count == 0 && mentions.Count == 0 )
        {
            return new MessagePush( null, everything, mentions );
        }

        var room = RoomQueries.Find( db, message.RoomId );
        var creator = UserQueries.Find( db, message.CreatorId );
        var body = MessageBodies.PlainText( db, bodyText, message.Id );
        var payload = room.IsDirect
            ? new PushPayload( creator.Name, body, $"/rooms/{room.Id}" )
            : new PushPayload( room.Name ?? "", $"{creator.Name}: {body}", $"/rooms/{room.Id}" );
        return new MessagePush( payload, everything, mentions );
    }
}
