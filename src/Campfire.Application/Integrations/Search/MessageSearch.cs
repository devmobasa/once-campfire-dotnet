using Campfire.Application.Data;
using Campfire.Application.Messages;
using Campfire.Application.Search;

namespace Campfire.Application.Integrations.Search;

/// <summary>
/// <c>SearchesController</c> (<c>app/controllers/searches_controller.rb</c>) over <c>Search</c> and
/// <c>Message::Searchable</c>: the query sanitized by <see cref="SearchQuery"/>, matched with FTS5 in the rooms the
/// user belongs to (<c>reachable_messages</c>), and the user's recent searches recorded, trimmed to ten, and cleared.
/// </summary>
/// <remarks>
/// <c>set_messages</c> runs before every action, <c>create</c> and <c>clear</c> included. Search words are quoted
/// as literal FTS5 terms before those actions run.
/// </remarks>
public static class MessageSearch
{
    /// <summary>
    /// <c>set_messages</c>: <c>Message.search_reachable(user, query)</c> when the sanitized query is
    /// present, else no messages. <paramref name="rawQuery"/> is the raw <c>params[:q]</c>.
    /// </summary>
    public static SearchResults Find( CampfireDb db, long userId, string? rawQuery )
    {
        ArgumentNullException.ThrowIfNull( db );

        var query = SearchQuery.Sanitize( rawQuery );
        if ( !SearchQuery.IsPresent( query ) )
        {
            return new SearchResults( null, [] );
        }

        return new SearchResults( query, MessageSearchIndex.SearchReachable( db, userId, query! ) );
    }

    /// <summary>
    /// <c>create</c>: <c>Current.user.searches.record(query)</c> with the sanitized query, blank or not. Without a
    /// <c>q</c> parameter the query is nil, which the <c>NOT NULL</c> column rejects (Rails: 500).
    /// </summary>
    public static RecentSearch Record( WriteScope scope, long userId, string? rawQuery ) =>
        SearchOperations.Record( scope, userId, SearchQuery.Sanitize( rawQuery )! );

    /// <summary><c>clear</c>: <c>Current.user.searches.destroy_all</c>.</summary>
    public static void Clear( WriteScope scope, long userId ) => SearchOperations.Clear( scope, userId );

    /// <summary><c>index</c>'s <c>@recent_searches</c>: <c>Current.user.searches.ordered</c>.</summary>
    public static IReadOnlyList<RecentSearch> Recent( CampfireDb db, long userId ) => SearchOperations.Ordered( db, userId );
}
