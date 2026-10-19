using Campfire.Application.Data;
using Campfire.Application.Memberships;
using Campfire.Application.Messages;
using Campfire.Application.Users;

using Dapper;

namespace Campfire.Application.Rooms;

/// <summary>Writes to <c>rooms</c>, following <c>app/models/room.rb</c> and <c>rooms/*.rb</c>.</summary>
public static class RoomOperations
{
    /// <summary><c>Rooms::Open</c>'s <c>after_save_commit :grant_access_to_all_users</c>, its only commit callback.</summary>
    internal const int GrantAccessToAllUsersOrder = 1;

    private const string InsertSql =
        "INSERT INTO rooms (created_at, creator_id, name, type, updated_at) VALUES (@CreatedAt, @CreatorId, @Name, @Type, @UpdatedAt) RETURNING id";

    /// <summary>
    /// <c>Rooms::&lt;Type&gt;.create!(name:, creator:)</c>. An open room grants itself to every active user after commit.
    /// </summary>
    public static Room Create( WriteScope scope, RoomType type, string? name, long creatorId )
    {
        var room = Insert( scope, type, name, creatorId );
        RegisterSaveCallbacks( scope, room, typeChangedToOpen: type == RoomType.Open );
        return room;
    }

    /// <summary>
    /// <c>Room.create_for(attributes, users:)</c>: the room, then memberships for <paramref name="userIds"/> with the
    /// room's default involvement, in one transaction.
    /// </summary>
    public static Room CreateFor( WriteScope scope, RoomType type, string? name, long creatorId, IEnumerable<long> userIds )
    {
        var room = Create( scope, type, name, creatorId );
        RoomMemberships.GrantTo( scope, room, userIds );
        return room;
    }

    /// <summary>
    /// <c>Rooms::Direct.find_or_create_for(users)</c>: the direct room whose members are exactly the existing users
    /// among <paramref name="userIds"/>, created by <paramref name="creatorId"/> (<c>Current.user</c>) if none is.
    /// </summary>
    public static Room FindOrCreateDirectFor( WriteScope scope, IReadOnlyCollection<long> userIds, long creatorId )
    {
        ArgumentNullException.ThrowIfNull( scope );

        var existing = RoomQueries.FindDirectByUserIds( scope.Db, userIds );
        return existing ?? CreateFor( scope, RoomType.Direct, name: null, creatorId, userIds );
    }

    /// <summary>
    /// <c>room.update!(attributes)</c>, after <c>becomes!</c> when <see cref="RoomChanges.Type"/> is set. A direct room
    /// keeps its type (<c>direct_rooms_keep_their_type</c>); a room that becomes open grants itself to every active
    /// user after commit. Only changed columns are written, plus <c>updated_at</c>.
    /// </summary>
    /// <exception cref="RecordInvalidException">A direct room would change type.</exception>
    public static Room Update( WriteScope scope, long roomId, RoomChanges changes )
    {
        ArgumentNullException.ThrowIfNull( scope );
        ArgumentNullException.ThrowIfNull( changes );

        var room = RoomQueries.Find( scope.Db, roomId );
        RoomDeletions.EnsureActive( scope.Db, roomId );
        var original = room.Copy( );
        var typeChanged = changes.Type is { } type && type != room.Type;
        if ( typeChanged && room.Type == RoomType.Direct )
        {
            var errors = new ValidationErrors( );
            errors.Add( "type", "can't be changed for a direct room" );
            errors.ThrowIfAny( );
        }

        if ( changes.Name.IsSet )
        {
            room.Name = changes.Name.Value;
        }

        room.Type = changes.Type ?? room.Type;
        var key = RecordKeys.Room( room.Id );
        scope.Enlist( key );

        var columns = new ChangedColumns( TableNames.Rooms );
        columns.Compare( "name", nameof( Room.Name ), original.Name, room.Name );
        columns.Compare( "type", nameof( Room.Type ), original.Type, room.Type );
        if ( columns.Any )
        {
            room.UpdatedAt = scope.Now;
            DataWrites.ExpectOneRow( scope.Db.Connection.Execute( columns.UpdateSql( ), room, scope.Db.Transaction ), key );
        }

        RegisterSaveCallbacks( scope, room, typeChangedToOpen: typeChanged && room.Type == RoomType.Open );
        return room;
    }

    /// <summary>Removes room access now and records recoverable background destruction.</summary>
    public static void Destroy( WriteScope scope, long roomId ) => RoomDeletions.Start( scope, roomId );

    /// <summary>
    /// <c>belongs_to :room, touch: true</c>: <c>touch_later</c> defers the room's <c>updated_at</c> write to just
    /// before commit, with the time of the latest request.
    /// </summary>
    public static void TouchLater( WriteScope scope, long roomId )
    {
        ArgumentNullException.ThrowIfNull( scope );

        var touchedAt = scope.Now;
        scope.BeforeCommit( RecordKeys.Room( roomId ), CommitOrder.Touch, commitScope =>
            commitScope.Db.Connection.Execute( "UPDATE rooms SET updated_at = @at WHERE id = @roomId", new
            {
                at = touchedAt,
                roomId
            }, commitScope.Db.Transaction ) );
    }

    /// <summary>
    /// Marks visible, disconnected members other than the creator unread inside the message creation transaction.
    /// </summary>
    /// <remarks>
    /// <c>involvement &lt;&gt; 'invisible'</c> leaves a NULL involvement out, as Rails' <c>where.not(involvement:
    /// :invisible)</c> does (SQL three-valued logic).
    /// </remarks>
    internal static void ReceiveInTransaction( WriteScope scope, Message message ) => MarkUnread( scope.Db, message, scope.Now );

    private static void MarkUnread( CampfireDb db, Message message, DateTime now )
    {
        var cutoff = Membership.ConnectionCutoff( now );
        db.Connection.Execute(
            "UPDATE memberships SET unread_at = @unreadAt, updated_at = @now " +
            "WHERE memberships.room_id = @roomId AND memberships.involvement <> @invisible " +
            "AND (memberships.connected_at IS NULL OR memberships.connected_at < @cutoff) AND memberships.user_id <> @creatorId " +
            "AND (memberships.unread_at IS NULL OR EXISTS (SELECT 1 FROM rooms WHERE rooms.id = @roomId AND rooms.type = @direct))",
            new
            {
                unreadAt = message.CreatedAt,
                now,
                roomId = message.RoomId,
                invisible = Involvement.Invisible,
                cutoff,
                creatorId = message.CreatorId,
                direct = RoomType.Direct,
            },
            db.Transaction );
    }

    /// <summary>Inserts the row alone; the caller enlists it (see <see cref="Accounts.FirstRun"/>).</summary>
    internal static Room Insert( WriteScope scope, RoomType type, string? name, long creatorId )
    {
        var now = scope.Now;
        var room = new Room { Name = name, Type = type, CreatorId = creatorId, CreatedAt = now, UpdatedAt = now };
        room.Id = DataWrites.Save( ( ) => scope.Db.Connection.ExecuteScalar<long>( InsertSql, room, scope.Db.Transaction ) );
        return room;
    }

    /// <summary>
    /// Enlists the saved room and, when it became open, <c>grant_access_to_all_users</c>
    /// (<c>if type_previously_changed?(to: "Rooms::Open")</c>).
    /// </summary>
    internal static void RegisterSaveCallbacks( WriteScope scope, Room room, bool typeChangedToOpen )
    {
        var key = RecordKeys.Room( room.Id );
        scope.Enlist( key );
        if ( typeChangedToOpen )
        {
            var roomId = room.Id;
            scope.AfterCommit( key, GrantAccessToAllUsersOrder, "grant_access_to_all_users", context => GrantAccessToAllUsers( context.Db, roomId ) );
        }
    }

    /// <summary><c>memberships.grant_to(User.active)</c>, in id order as SQLite returns them.</summary>
    private static void GrantAccessToAllUsers( CampfireDb db, long roomId )
    {
        var activeUserIds = db.Connection.Query<long>(
                "SELECT users.id FROM users WHERE users.status = @active ORDER BY users.id", new
                {
                    active = UserStatus.Active
                }, db.Transaction )
            .AsList( );
        MembershipGrants.GrantTo( db, roomId, RoomTypes.DefaultInvolvement( RoomType.Open ), activeUserIds );
    }
}
