using System.Globalization;
using System.Text;

using Campfire.Application.Data;
using Campfire.Application.Rooms;

using Dapper;

namespace Campfire.Application.Memberships;

/// <summary>
/// <c>Membership.insert_all</c>: one statement, skipping rows that already exist, with timestamps SQLite writes
/// itself (<see cref="RailsTimestamp.SqliteNow"/>, millisecond precision), exactly as Rails 1a02651 does.
/// </summary>
internal static class MembershipGrants
{
    /// <summary>
    /// <c>room.memberships.grant_to(users)</c>: the room's default involvement for every user, existing members skipped.
    /// </summary>
    public static void GrantTo( CampfireDb db, long roomId, Involvement involvement, IEnumerable<long> userIds ) =>
        InTransaction( db, ( ) => GrantToInTransaction( db, roomId, involvement, userIds ) );

    private static void GrantToInTransaction( CampfireDb db, long roomId, Involvement involvement, IEnumerable<long> userIds )
    {
        RoomDeletions.EnsureActive( db, roomId );
        var rows = userIds.Select( userId =>
            $"({RailsTimestamp.SqliteNow}, @involvement, {Integer( roomId )}, {RailsTimestamp.SqliteNow}, {Integer( userId )})" ).ToList( );
        if ( rows.Count == 0 )
        {
            return;
        }

        var sql = new StringBuilder( "INSERT INTO \"memberships\" (\"created_at\",\"involvement\",\"room_id\",\"updated_at\",\"user_id\") VALUES " )
            .AppendJoin( ", ", rows )
            .Append( " ON CONFLICT  DO NOTHING" )
            .ToString( );
        DataWrites.Save( ( ) => db.Connection.Execute( sql, new { involvement }, db.Transaction ) );
    }

    /// <summary>
    /// <c>User#grant_membership_to_open_rooms</c>: a membership in each room, leaving involvement to the column default.
    /// </summary>
    public static void GrantRoomsTo( CampfireDb db, long userId, IEnumerable<long> roomIds ) =>
        InTransaction( db, ( ) => GrantRoomsToInTransaction( db, userId, roomIds ) );

    private static void GrantRoomsToInTransaction( CampfireDb db, long userId, IEnumerable<long> roomIds )
    {
        var rows = roomIds.Select( roomId =>
        {
            RoomDeletions.EnsureActive( db, roomId );
            return $"({RailsTimestamp.SqliteNow}, {Integer( roomId )}, {RailsTimestamp.SqliteNow}, {Integer( userId )})";
        } ).ToList( );
        if ( rows.Count == 0 )
        {
            return;
        }

        var sql = new StringBuilder( "INSERT INTO \"memberships\" (\"created_at\",\"room_id\",\"updated_at\",\"user_id\") VALUES " )
            .AppendJoin( ", ", rows )
            .Append( " ON CONFLICT  DO NOTHING" )
            .ToString( );
        DataWrites.Save( ( ) => db.Connection.Execute( sql, transaction: db.Transaction ) );
    }

    private static void InTransaction( CampfireDb db, Action grant )
    {
        if ( db.Transaction is not null )
        {
            grant( );
            return;
        }

        using var transaction = db.Connection.BeginTransaction( );
        db.Transaction = transaction;
        try
        {
            grant( );
            transaction.Commit( );
        }
        finally
        {
            db.Transaction = null;
        }
    }

    // Ids go inline, as Rails quotes insert_all values into the statement: no bind-variable limit applies.
    private static string Integer( long value ) => value.ToString( CultureInfo.InvariantCulture );
}
