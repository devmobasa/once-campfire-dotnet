using Campfire.Application.Data;
using Campfire.Application.Data.Hooks;

using Dapper;

namespace Campfire.Application.Messages;

/// <summary>
/// <c>Message::Searchable</c>: the FTS5 table <c>message_search_index</c> (<c>tokenize=porter</c>), keyed by message id
/// and holding <c>plain_text_body</c>. Message creation indexes inside its transaction.
/// Updates retain their after-commit indexing order.
/// </summary>
public static class MessageSearchIndex
{
    /// <summary>How many matches the search page shows.</summary>
    public const int SearchLimit = 100;

    /// <summary><c>create_in_index</c>.</summary>
    public static void Insert( CampfireDb db, IMessageBodyText bodyText, long messageId )
    {
        var body = MessageBodies.PlainText( db, bodyText, messageId );
        db.Connection.Execute( "insert into message_search_index(rowid, body) values (@id, @body)", new
        {
            id = messageId,
            body
        }, db.Transaction );
    }

    /// <summary><c>update_in_index</c>: a message that was never indexed stays unindexed, as in Rails.</summary>
    public static void Update( CampfireDb db, IMessageBodyText bodyText, long messageId )
    {
        var body = MessageBodies.PlainText( db, bodyText, messageId );
        db.Connection.Execute( "update message_search_index set body = @body where rowid = @id", new
        {
            body,
            id = messageId
        }, db.Transaction );
    }

    /// <summary><c>remove_from_index</c>.</summary>
    public static void Delete( CampfireDb db, long messageId )
    {
        ArgumentNullException.ThrowIfNull( db );

        db.Connection.Execute( "delete from message_search_index where rowid = @id", new
        {
            id = messageId
        }, db.Transaction );
    }

    /// <summary>
    /// <c>room.messages.search(query)</c>: quoted FTS5 terms within one room, ordered by creation time.
    /// </summary>
    public static List<Message> SearchInRoom( CampfireDb db, long roomId, string query )
    {
        ArgumentNullException.ThrowIfNull( db );

        return db.Connection.Query<Message>(
                $"SELECT {Columns.Messages} FROM messages join message_search_index idx on messages.id = idx.rowid " +
                "WHERE messages.room_id = @roomId AND (idx.body match @query) ORDER BY messages.created_at ASC",
                new
                {
                    roomId,
                    query = MatchTerms( query )
                },
                db.Transaction )
            .AsList( );
    }

    /// <summary>
    /// <c>Message.search_reachable(user, query)</c>: the newest 100 reachable matches by message id, in ascending id order.
    /// </summary>
    public static List<Message> SearchReachable( CampfireDb db, long userId, string query )
    {
        ArgumentNullException.ThrowIfNull( db );

        var newestFirst = db.Connection.Query<Message>(
                $"SELECT {Columns.Messages} FROM messages {MessageQueries.ReachableJoin} " +
                "join message_search_index idx on messages.id = idx.rowid " +
                $"WHERE {MessageQueries.ReachableWhere} AND (idx.body match @query) " +
                "ORDER BY idx.rowid DESC LIMIT @limit",
                new
                {
                    userId,
                    query = MatchTerms( query ),
                    limit = SearchLimit
                },
                db.Transaction )
            .AsList( );
        newestFirst.Reverse( );
        return newestFirst;
    }

    private static string MatchTerms( string query ) => string.Join( ' ',
        query.Split( [' ', '\t', '\n', '\v', '\f', '\r'], StringSplitOptions.RemoveEmptyEntries )
            .Select( word => '"' + word.Replace( "\"", "\"\"", StringComparison.Ordinal ) + '"' ) );

    /// <summary>The indexed text of a message, or null when it has no row.</summary>
    public static string? IndexedBody( CampfireDb db, long messageId )
    {
        ArgumentNullException.ThrowIfNull( db );

        return db.Connection.QueryFirstOrDefault<string?>( "SELECT body FROM message_search_index WHERE rowid = @id", new
        {
            id = messageId
        }, db.Transaction );
    }
}
