using Campfire.Application.Data;
using Campfire.Application.Rooms;

using Dapper;

namespace Campfire.Application.Messages;

/// <summary>
/// <c>app/models/boost.rb</c>: <c>belongs_to :message, touch: true</c>. Creating or destroying a boost defers a touch
/// of the message (and so the room) to just before commit, and the touched message reindexes after commit.
/// </summary>
public static class BoostOperations
{
    /// <summary><c>@message.boosts.create!(content:, booster:)</c>.</summary>
    /// <exception cref="RecordNotFoundException">The message does not exist, or its room is gone or being deleted.</exception>
    public static Boost Create( WriteScope scope, long messageId, long boosterId, string content )
    {
        ArgumentNullException.ThrowIfNull( scope );

        var message = MessageQueries.Find( scope.Db, messageId );
        RoomDeletions.EnsureActive( scope.Db, message.RoomId );
        var now = scope.Now;
        var boost = new Boost { MessageId = messageId, BoosterId = boosterId, Content = content, CreatedAt = now, UpdatedAt = now };
        boost.Id = DataWrites.Save( ( ) => scope.Db.Connection.ExecuteScalar<long>(
            "INSERT INTO boosts (booster_id, content, created_at, message_id, updated_at) " +
            "VALUES (@BoosterId, @Content, @CreatedAt, @MessageId, @UpdatedAt) RETURNING id",
            boost,
            scope.Db.Transaction ) );
        scope.Enlist( RecordKeys.Boost( boost.Id ) );

        MessageOperations.TouchLater( scope, message );
        return boost;
    }

    /// <summary><c>boost.destroy!</c>.</summary>
    /// <exception cref="RecordNotFoundException">The boost does not exist.</exception>
    public static Boost Destroy( WriteScope scope, long boostId )
    {
        ArgumentNullException.ThrowIfNull( scope );

        var db = scope.Db;
        var boost = db.Connection.QuerySingleOrDefault<Boost>(
            $"SELECT {Columns.Boosts} FROM boosts WHERE boosts.id = @boostId", new
            {
                boostId
            }, db.Transaction ).OrNotFound( ModelNames.Boost );

        var key = RecordKeys.Boost( boost.Id );
        scope.Enlist( key );
        DataWrites.ExpectOneRow( db.Connection.Execute( "DELETE FROM boosts WHERE id = @boostId", new
        {
            boostId
        }, db.Transaction ), key );

        MessageOperations.TouchLater( scope, MessageQueries.Find( db, boost.MessageId ) );
        return boost;
    }

    /// <summary>
    /// <c>boosts.ordered</c> of several messages at once (a page's <c>with_boosts</c> preload), by creation then id.
    /// </summary>
    public static List<Boost> ForMessages( CampfireDb db, IReadOnlyCollection<long> messageIds )
    {
        ArgumentNullException.ThrowIfNull( db );
        ArgumentNullException.ThrowIfNull( messageIds );

        return db.Connection.Query<Boost>(
                $"SELECT {Columns.Boosts} FROM boosts WHERE boosts.message_id IN (SELECT value FROM json_each(@messageIds)) " +
                "ORDER BY boosts.created_at, boosts.id",
                new
                {
                    messageIds = IdLists.Json( messageIds )
                },
                db.Transaction )
            .AsList( );
    }

    /// <summary><c>@message.boosts.find_by!(id:, booster: Current.user)</c>.</summary>
    /// <exception cref="RecordNotFoundException">The user has no such boost on the message.</exception>
    public static Boost FindOwn( CampfireDb db, long messageId, long boostId, long boosterId )
    {
        ArgumentNullException.ThrowIfNull( db );

        return db.Connection.QueryFirstOrDefault<Boost>(
            $"SELECT {Columns.Boosts} FROM boosts " +
            "WHERE boosts.message_id = @messageId AND boosts.id = @boostId AND boosts.booster_id = @boosterId LIMIT 1",
            new
            {
                messageId,
                boostId,
                boosterId
            },
            db.Transaction ).OrNotFound( ModelNames.Boost );
    }
}
