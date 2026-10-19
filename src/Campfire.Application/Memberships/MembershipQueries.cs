using Campfire.Application.Data;
using Campfire.Application.Rooms;
using Campfire.Application.Users;

using Dapper;

namespace Campfire.Application.Memberships;

/// <summary>Reads of <c>memberships</c>.</summary>
public static class MembershipQueries
{
    private const int UnreadRecipientPageSize = 128;

    /// <summary><c>Users::SidebarsController::DIRECT_PLACEHOLDERS</c>.</summary>
    public const int DirectPlaceholders = 20;

    /// <summary>A membership and its room in one row, the columns of each aliased apart (<see cref="MembershipRoomRow"/>).</summary>
    private const string WithRoomSql =
        "SELECT memberships.id AS membership_id, memberships.connected_at, memberships.connections, " +
        "memberships.created_at AS membership_created_at, memberships.involvement, memberships.room_id, memberships.unread_at, " +
        "memberships.updated_at AS membership_updated_at, memberships.user_id, " +
        "rooms.created_at AS room_created_at, rooms.creator_id, rooms.name AS room_name, rooms.type AS room_type, " +
        "rooms.updated_at AS room_updated_at " +
        "FROM memberships INNER JOIN rooms ON memberships.room_id = rooms.id WHERE memberships.user_id = @userId";

    /// <summary><c>Membership.find(id)</c>.</summary>
    /// <exception cref="RecordNotFoundException">No such membership.</exception>
    public static Membership Find( CampfireDb db, long membershipId )
    {
        ArgumentNullException.ThrowIfNull( db );

        return db.Connection.QuerySingleOrDefault<Membership>(
            $"SELECT {Columns.Memberships} FROM memberships WHERE memberships.id = @membershipId", new
            {
                membershipId
            }, db.Transaction ).OrNotFound( ModelNames.Membership );
    }

    /// <summary><c>Current.user.memberships.find_by(room_id:)</c> (<c>RoomScoped</c> uses <c>find_by!</c>).</summary>
    public static Membership? FindByRoomAndUser( CampfireDb db, long roomId, long userId )
    {
        ArgumentNullException.ThrowIfNull( db );

        return db.Connection.QueryFirstOrDefault<Membership>(
            $"SELECT {Columns.Memberships} FROM memberships WHERE memberships.user_id = @userId AND memberships.room_id = @roomId LIMIT 1",
            new
            {
                userId,
                roomId
            },
            db.Transaction );
    }

    public static List<Membership> ForRoom( CampfireDb db, long roomId )
    {
        ArgumentNullException.ThrowIfNull( db );

        return db.Connection.Query<Membership>(
                $"SELECT {Columns.Memberships} FROM memberships WHERE memberships.room_id = @roomId ORDER BY memberships.id", new
                {
                    roomId
                }, db.Transaction )
            .AsList( );
    }

    public static List<Membership> ForUser( CampfireDb db, long userId )
    {
        ArgumentNullException.ThrowIfNull( db );

        return db.Connection.Query<Membership>(
                $"SELECT {Columns.Memberships} FROM memberships WHERE memberships.user_id = @userId ORDER BY memberships.id", new
                {
                    userId
                }, db.Transaction )
            .AsList( );
    }

    /// <summary><c>room.memberships.pluck(:user_id)</c>: who gets the unread-room broadcast.</summary>
    public static List<long> UserIdsInRoom( CampfireDb db, long roomId )
    {
        ArgumentNullException.ThrowIfNull( db );

        return db.Connection.Query<long>( "SELECT memberships.user_id FROM memberships WHERE memberships.room_id = @roomId", new
        {
            roomId
        }, db.Transaction ).AsList( );
    }

    /// <summary>Members who still have the room unread or have a current connection when notification work runs.</summary>
    public static List<UnreadRoomRecipient> UnreadOrConnectedPage( CampfireDb db, long roomId, DateTime now, long afterMembershipId )
    {
        ArgumentNullException.ThrowIfNull( db );

        return db.Connection.Query<UnreadRoomRecipient>(
            "SELECT memberships.id AS MembershipId, memberships.user_id AS UserId FROM memberships " +
            "WHERE memberships.room_id = @roomId AND memberships.id > @afterMembershipId " +
            "AND (memberships.unread_at IS NOT NULL OR memberships.connected_at >= @cutoff) ORDER BY memberships.id LIMIT @limit",
            new { roomId, afterMembershipId, cutoff = Membership.ConnectionCutoff( now ), limit = UnreadRecipientPageSize }, db.Transaction ).AsList( );
    }

    /// <summary>
    /// <c>user.memberships.visible.with_ordered_room</c>: ordered by <c>LOWER(rooms.name)</c>. <c>visible</c> is
    /// <c>where.not(involvement: :invisible)</c>, which leaves a NULL involvement out too.
    /// </summary>
    public static List<MembershipWithRoom> VisibleWithOrderedRoom( CampfireDb db, long userId )
    {
        ArgumentNullException.ThrowIfNull( db );

        var rows = db.Connection.Query<MembershipRoomRow>(
            $"{WithRoomSql} AND memberships.involvement <> @invisible ORDER BY LOWER(rooms.name)",
            new
            {
                userId,
                invisible = Involvement.Invisible
            },
            db.Transaction );
        return [.. rows.Select( row => row.ToMembershipWithRoom( ) )];
    }

    /// <summary><c>user.memberships.with_ordered_room</c>, invisible ones included.</summary>
    public static List<MembershipWithRoom> WithOrderedRoom( CampfireDb db, long userId )
    {
        ArgumentNullException.ThrowIfNull( db );

        var rows = db.Connection.Query<MembershipRoomRow>( $"{WithRoomSql} ORDER BY LOWER(rooms.name)", new
        {
            userId
        }, db.Transaction );
        return [.. rows.Select( row => row.ToMembershipWithRoom( ) )];
    }

    /// <summary><c>user.memberships.without_direct_rooms.count</c>.</summary>
    public static int CountWithoutDirectRooms( CampfireDb db, long userId )
    {
        ArgumentNullException.ThrowIfNull( db );

        return checked(( int ) db.Connection.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM memberships INNER JOIN rooms ON memberships.room_id = rooms.id " +
            "WHERE memberships.user_id = @userId AND rooms.type <> @direct",
            new
            {
                userId,
                direct = RoomType.Direct
            },
            db.Transaction ));
    }

    /// <summary><c>user.memberships.unread.count</c>: the push notification badge.</summary>
    public static int UnreadCount( CampfireDb db, long userId )
    {
        ArgumentNullException.ThrowIfNull( db );

        return checked(( int ) db.Connection.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM memberships WHERE memberships.user_id = @userId AND memberships.unread_at IS NOT NULL", new
            {
                userId
            }, db.Transaction ));
    }

    /// <summary>
    /// <see cref="UnreadCount"/> for several users in one statement (a push fan-out's badges); a user with no unread
    /// room is absent.
    /// </summary>
    public static Dictionary<long, int> UnreadCounts( CampfireDb db, IReadOnlyCollection<long> userIds )
    {
        ArgumentNullException.ThrowIfNull( db );
        ArgumentNullException.ThrowIfNull( userIds );

        return db.Connection.Query<UserCount>(
                "SELECT memberships.user_id, COUNT(*) AS count FROM memberships " +
                "WHERE memberships.user_id IN (SELECT value FROM json_each(@userIds)) AND memberships.unread_at IS NOT NULL " +
                "GROUP BY memberships.user_id",
                new
                {
                    userIds = IdLists.Json( userIds )
                },
                db.Transaction )
            .ToDictionary( row => row.UserId, row => checked(( int ) row.Count) );
    }

    /// <summary>
    /// <c>Users::SidebarsController#show</c>: visible memberships split into direct rooms (most recently updated first)
    /// and the rest (by name), and up to 20 active users, oldest first, to offer as new direct rooms.
    /// </summary>
    public static SidebarMemberships Sidebar( CampfireDb db, long userId )
    {
        ArgumentNullException.ThrowIfNull( db );

        var all = VisibleWithOrderedRoom( db, userId );

        // `select { direct }.sort_by { room.updated_at }.reverse`
        var direct = all.Where( row => row.Room.IsDirect ).OrderBy( row => row.Room.UpdatedAt ).Reverse( ).ToList( );
        var other = all.Where( row => !row.Room.IsDirect ).ToList( );

        // Rails uniquifies the direct-room members, then `including(Current.user.id)` appends self again.
        // Preserve that count for the limit even when self was already among the members; the WHERE clause
        // still excludes self only once. Invisible direct rooms also contribute to the exclusion set.
        var placeholders = db.Connection.Query<User>(
                "WITH excluded(user_id) AS (SELECT memberships.user_id FROM memberships WHERE memberships.room_id IN " +
                "(SELECT rooms.id FROM rooms INNER JOIN memberships AS mine ON rooms.id = mine.room_id " +
                "WHERE mine.user_id = @userId AND rooms.type = @direct) GROUP BY memberships.user_id) " +
                $"SELECT {Columns.Users} FROM users " +
                "WHERE users.status = @active AND users.id <> @userId AND users.id NOT IN (SELECT user_id FROM excluded) ORDER BY users.created_at " +
                "LIMIT MAX(@maximum - (SELECT COUNT(*) FROM excluded) - 1, 0)",
                new
                {
                    active = UserStatus.Active,
                    userId,
                    direct = RoomType.Direct,
                    maximum = DirectPlaceholders
                },
                db.Transaction )
            .AsList( );

        return new SidebarMemberships( direct, other, placeholders );
    }
}
