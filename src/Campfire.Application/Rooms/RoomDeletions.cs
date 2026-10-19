using Campfire.Application.Data;
using Campfire.Application.Messages;
using Dapper;

namespace Campfire.Application.Rooms;

/// <summary>Removes access atomically, then resumes ordered room destruction through bounded transactions.</summary>
public static class RoomDeletions
{
    /// <summary>Captures reset recipients before membership removal. Broadcast delivery repeats safely after interrupted acknowledgement.</summary>
    public static Room Start( WriteScope scope, long roomId )
    {
        var room = RoomQueries.Find( scope.Db, roomId );
        var db = scope.Db;
        if ( db.Connection.ExecuteScalar<bool>( "SELECT EXISTS(SELECT 1 FROM campfire_room_deletions WHERE room_id=@roomId)", new { roomId }, db.Transaction ) )
        {
            return room;
        }

        if ( room.IsOpen )
        {
            room = RoomOperations.Update( scope, roomId, new RoomChanges { Type = RoomType.Closed } );
        }

        db.Connection.Execute( "INSERT INTO campfire_room_deletions (room_id,phase,created_at,next_attempt_at) VALUES (@roomId,@phase,@now,@now)",
            new { roomId, phase = 0, now = scope.Now }, db.Transaction );
        db.Connection.Execute( "INSERT INTO campfire_room_deletion_members (room_id,user_id) SELECT room_id,user_id FROM memberships WHERE room_id=@roomId", new { roomId }, db.Transaction );
        db.Connection.Execute( "DELETE FROM memberships WHERE room_id=@roomId", new { roomId }, db.Transaction );
        return room;
    }

    /// <summary>Rejects stale mutations after logical removal, using the current writer transaction.</summary>
    internal static void EnsureActive( CampfireDb db, long roomId )
    {
        if ( db.Connection.ExecuteScalar<bool>( "SELECT NOT EXISTS(SELECT 1 FROM rooms WHERE id=@roomId) OR EXISTS(SELECT 1 FROM campfire_room_deletions WHERE room_id=@roomId)", new { roomId }, db.Transaction ) )
        {
            throw new RecordNotFoundException( ModelNames.Room );
        }
    }

    /// <summary>Broadcasts outside database leases, then acknowledges the durable delivery phase.</summary>
    public static async Task BroadcastAsync( CampfireDatabase database, long roomId, Action<Room> broadcast, CancellationToken cancellationToken )
    {
        var room = await database.ReadAsync( db => RoomQueries.FindById( db, roomId ), cancellationToken );
        if ( room is not null )
        {
            broadcast( room );
        }

        await database.WriteAsync( scope => scope.Db.Connection.Execute(
            "UPDATE campfire_room_deletions SET phase=1,next_attempt_at=@now WHERE room_id=@roomId AND phase=0",
            new { roomId, now = scope.Now }, scope.Db.Transaction ), cancellationToken );
    }

    /// <summary>Advances at most sixteen rooms. Each message commits before its FTS and purge callbacks run.</summary>
    public static async Task<int> DrainAsync( CampfireDatabase database, Action<Room> broadcast, CancellationToken cancellationToken,
        RoomDeletionCursor? cursor = null, Action<long, Exception>? failed = null )
    {
        var afterRoomId = cursor?.AfterRoomId ?? 0;
        var rooms = await database.ReadAsync( db => db.Connection.Query<long>(
            "SELECT room_id FROM campfire_room_deletions WHERE next_attempt_at<=@now AND room_id>@afterRoomId ORDER BY room_id LIMIT 16",
            new { now = database.Clock.GetUtcNow( ).UtcDateTime, afterRoomId }, db.Transaction ).AsList( ), cancellationToken );
        if ( rooms.Count == 0 && afterRoomId > 0 )
        {
            cursor!.AfterRoomId = 0;
            return await DrainAsync( database, broadcast, cancellationToken, cursor, failed );
        }

        var advanced = 0;
        foreach ( var roomId in rooms )
        {
            if ( cursor is not null )
            {
                cursor.AfterRoomId = roomId;
            }

            try
            {
                advanced += await AdvanceAsync( database, roomId, broadcast, cancellationToken );
            }
            catch ( OperationCanceledException ) when ( cancellationToken.IsCancellationRequested )
            {
                throw;
            }
            catch ( Exception error )
            {
                await database.WriteAsync( scope => scope.Db.Connection.Execute(
                    "UPDATE campfire_room_deletions SET attempts=MIN(attempts+1,20),next_attempt_at=@next WHERE room_id=@roomId",
                    new { roomId, next = scope.Now.AddSeconds( 30 ) }, scope.Db.Transaction ), cancellationToken );
                failed?.Invoke( roomId, error );
            }
        }

        return advanced;
    }

    private static async Task<int> AdvanceAsync( CampfireDatabase database, long roomId, Action<Room> broadcast, CancellationToken cancellationToken )
    {
        var phase = await database.ReadAsync( db => db.Connection.ExecuteScalar<int>(
            "SELECT phase FROM campfire_room_deletions WHERE room_id=@roomId", new { roomId }, db.Transaction ), cancellationToken );
        if ( phase == 0 )
        {
            await BroadcastAsync( database, roomId, broadcast, cancellationToken );
            return 1;
        }

        if ( phase == 1 )
        {
            var users = await database.ReadAsync( db => db.Connection.Query<long>(
                "SELECT user_id FROM campfire_room_deletion_members WHERE room_id=@roomId ORDER BY user_id LIMIT 64", new { roomId }, db.Transaction ).AsList( ), cancellationToken );
            foreach ( var userId in users )
            {
                cancellationToken.ThrowIfCancellationRequested( );
                database.Hooks.RemoteConnections.Disconnect( userId, reconnect: true );
                await database.WriteAsync( scope => scope.Db.Connection.Execute(
                    "DELETE FROM campfire_room_deletion_members WHERE room_id=@roomId AND user_id=@userId", new { roomId, userId }, scope.Db.Transaction ), cancellationToken );
            }

            if ( users.Count == 0 )
            {
                await database.WriteAsync( scope => scope.Db.Connection.Execute( "UPDATE campfire_room_deletions SET phase=2 WHERE room_id=@roomId", new { roomId }, scope.Db.Transaction ), cancellationToken );
            }

            return 1;
        }

        return await database.WriteAsync( scope =>
        {
            var db = scope.Db;
            if ( db.Connection.ExecuteScalar<bool>( "SELECT EXISTS(SELECT 1 FROM campfire_cleanup WHERE kind=0 AND room_id=@roomId)", new { roomId }, db.Transaction ) )
            {
                return 0;
            }

            var messageId = db.Connection.ExecuteScalar<long?>( "SELECT MIN(id) FROM messages WHERE room_id=@roomId", new { roomId }, db.Transaction );
            if ( messageId is { } id )
            {
                MessageOperations.Destroy( scope, id );
            }
            else
            {
                db.Connection.Execute( "DELETE FROM memberships WHERE room_id=@roomId", new { roomId }, db.Transaction );
                db.Connection.Execute( "DELETE FROM rooms WHERE id=@roomId", new { roomId }, db.Transaction );
                db.Connection.Execute( "DELETE FROM campfire_room_deletions WHERE room_id=@roomId", new { roomId }, db.Transaction );
            }

            return 1;
        }, cancellationToken );
    }
}
