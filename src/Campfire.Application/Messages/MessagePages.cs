using Campfire.Application.Data;

using Dapper;

namespace Campfire.Application.Messages;

/// <summary>
/// <c>Message::Pagination</c>: pages of 40, ordered by <c>created_at</c> alone, each bounded in SQL. Ties keep
/// whatever order SQLite returns for the same statement, as in Rails: a newest-first page is read with
/// <c>ORDER BY created_at DESC</c> and reversed.
/// </summary>
public static class MessagePages
{
    /// <summary><c>Message::Pagination::PAGE_SIZE</c>.</summary>
    public const int PageSize = 40;

    /// <summary>
    /// The statements behind the pages: each filters to one room, orders by <c>created_at</c> and stops at
    /// <see cref="PageSize"/> rows in SQLite (<c>LIMIT @limit</c>), so a page never reads more of a room than it shows.
    /// </summary>
    internal const string InRoom = $"SELECT {Columns.Messages} FROM messages WHERE messages.room_id = @roomId";

    internal const string NewestFirst = "ORDER BY messages.created_at DESC LIMIT @limit";

    internal const string OldestFirst = "ORDER BY messages.created_at LIMIT @limit";

    /// <summary><c>room.messages.last_page</c>: the newest 40, oldest first.</summary>
    public static List<Message> LastPage( CampfireDb db, long roomId )
    {
        ArgumentNullException.ThrowIfNull( db );

        return Reversed( db.Connection.Query<Message>( $"{InRoom} {NewestFirst}", new
        {
            roomId,
            limit = PageSize
        }, db.Transaction ) );
    }

    /// <summary><c>room.messages.first_page</c>.</summary>
    public static List<Message> FirstPage( CampfireDb db, long roomId )
    {
        ArgumentNullException.ThrowIfNull( db );

        return db.Connection.Query<Message>( $"{InRoom} {OldestFirst}", new
        {
            roomId,
            limit = PageSize
        }, db.Transaction ).AsList( );
    }

    /// <summary><c>room.messages.page_before(message)</c>.</summary>
    public static List<Message> PageBefore( CampfireDb db, long roomId, Message message )
    {
        ArgumentNullException.ThrowIfNull( db );
        ArgumentNullException.ThrowIfNull( message );

        return Reversed( db.Connection.Query<Message>(
            $"{InRoom} AND messages.created_at < @createdAt {NewestFirst}", new
            {
                roomId,
                createdAt = message.CreatedAt,
                limit = PageSize
            }, db.Transaction ) );
    }

    /// <summary><c>room.messages.page_after(message)</c>.</summary>
    public static List<Message> PageAfter( CampfireDb db, long roomId, Message message )
    {
        ArgumentNullException.ThrowIfNull( message );

        return PageCreatedSince( db, roomId, message.CreatedAt );
    }

    /// <summary><c>room.messages.page_around(message)</c>: up to 40 before, the message, up to 40 after (at most 81).</summary>
    public static List<Message> PageAround( CampfireDb db, long roomId, Message message ) =>
        [.. PageBefore( db, roomId, message ), message, .. PageAfter( db, roomId, message )];

    /// <summary><c>room.messages.page_created_since(time)</c>.</summary>
    public static List<Message> PageCreatedSince( CampfireDb db, long roomId, DateTime time )
    {
        ArgumentNullException.ThrowIfNull( db );

        return db.Connection.Query<Message>(
                $"{InRoom} AND messages.created_at > @time {OldestFirst}", new
                {
                    roomId,
                    time,
                    limit = PageSize
                }, db.Transaction )
            .AsList( );
    }

    /// <summary><c>room.messages.without(excluding).page_updated_since(time)</c>.</summary>
    public static List<Message> PageUpdatedSince( CampfireDb db, long roomId, DateTime time, IReadOnlyCollection<long> excluding )
    {
        ArgumentNullException.ThrowIfNull( db );
        ArgumentNullException.ThrowIfNull( excluding );

        var order = db.HasUpdatedMessageIndex ? "ORDER BY +messages.created_at DESC LIMIT @limit" : NewestFirst;
        return Reversed( db.Connection.Query<Message>(
            $"{InRoom} AND messages.id NOT IN (SELECT value FROM json_each(@excluding)) AND messages.updated_at > @time {order}",
            new
            {
                roomId,
                excluding = IdLists.Json( excluding ),
                time,
                limit = PageSize
            },
            db.Transaction ) );
    }

    /// <summary><c>room.messages.before(message).exists?</c>.</summary>
    public static bool ExistsBefore( CampfireDb db, long roomId, Message message )
    {
        ArgumentNullException.ThrowIfNull( db );
        ArgumentNullException.ThrowIfNull( message );

        return db.Connection.ExecuteScalar<bool>(
            "SELECT EXISTS (SELECT 1 FROM messages WHERE messages.room_id = @roomId AND messages.created_at < @createdAt)",
            new
            {
                roomId,
                createdAt = message.CreatedAt
            },
            db.Transaction );
    }

    /// <summary><c>room.messages.after(message).exists?</c>.</summary>
    public static bool ExistsAfter( CampfireDb db, long roomId, Message message )
    {
        ArgumentNullException.ThrowIfNull( db );
        ArgumentNullException.ThrowIfNull( message );

        return db.Connection.ExecuteScalar<bool>(
            "SELECT EXISTS (SELECT 1 FROM messages WHERE messages.room_id = @roomId AND messages.created_at > @createdAt)",
            new
            {
                roomId,
                createdAt = message.CreatedAt
            },
            db.Transaction );
    }

    /// <summary>
    /// <c>room.messages.paged?</c>: more than one page (<c>count &gt; PAGE_SIZE</c>). Counting stops at the first row
    /// past a page, which answers the same question without reading the whole room.
    /// </summary>
    public static bool IsPaged( CampfireDb db, long roomId )
    {
        ArgumentNullException.ThrowIfNull( db );

        return db.Connection.ExecuteScalar<bool>(
            "SELECT COUNT(*) > @pageSize FROM (SELECT 1 FROM messages WHERE messages.room_id = @roomId LIMIT @pageSize + 1)",
            new
            {
                roomId,
                pageSize = PageSize
            },
            db.Transaction );
    }

    private static List<Message> Reversed( IEnumerable<Message> newestFirst )
    {
        var page = newestFirst.AsList( );
        page.Reverse( );
        return page;
    }
}
