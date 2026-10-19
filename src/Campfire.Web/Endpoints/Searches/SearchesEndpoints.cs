using Campfire.Application.Integrations.Search;
using Campfire.Web.App;
using Campfire.Web.Http;
using Campfire.Web.Http.Inputs;
using Campfire.Web.Paths;
using Campfire.Web.Policy;
using Campfire.Web.Presenters;
using Campfire.Web.Views.Models;

namespace Campfire.Web.Endpoints.Searches;

/// <summary>
/// <c>SearchesController</c> (<c>app/controllers/searches_controller.rb</c>): full-text search over the messages of
/// the rooms the user belongs to, and the user's recent searches. The query is sanitized by
/// <see cref="SearchQuery"/> (<c>params[:q]&amp;.gsub(/[^[:word:]]/, " ")</c>) and matched with FTS5 by
/// <see cref="MessageSearch"/>.
/// </summary>
/// <remarks>
/// <c>before_action :set_messages</c> runs for every action, <c>create</c> and <c>clear</c> included. Each search word
/// becomes a literal FTS5 term. A non-string <c>q</c> raises <c>NoMethodError</c> in <c>gsub</c>: 500.
/// </remarks>
public static partial class SearchesEndpoints
{
    /// <summary>
    /// <c>set_messages</c>: <c>Message.search_reachable(user, query)</c> when the sanitized query is
    /// present, else <c>Message.none</c>.
    /// </summary>
    private static async ValueTask<FrontendResult?> SetMessagesAsync( RequestContext requestContext )
    {
        var userId = requestContext.RequireCurrentUser( UndefinedMethodErrors.SearchesForNil ).Id;
        var query = RawQuery( requestContext );

        var database = requestContext.Database( );
        var results = await database.ReadAsync( db => MessageSearch.Find( db, userId, query ), requestContext.RequestAborted );
        requestContext.SetCurrent( new CurrentSearch( query, results ) );

        return null;
    }

    /// <summary>
    /// <c>index</c>: <c>@query</c>, the messages found, <c>@recent_searches = Current.user.searches.ordered</c> and
    /// <c>@return_to_room = last_room_visited</c>, rendered as <c>searches/index</c>.
    /// </summary>
    private static async Task<FrontendResult> IndexAsync( RequestContext requestContext )
    {
        requestContext.RespondTo( Mime.Html );
        var app = requestContext.App( );
        var userId = requestContext.RequireCurrentUser( UndefinedMethodErrors.SearchesForNil ).Id;
        var search = SearchOf( requestContext );
        var ffmpeg = await app.Storage.Tools.FfmpegExistsAsync( );
        var viewContext = await Pages.ContextAsync( requestContext, cacheFragments: true );

        // room_path(@return_to_room) raises for a user in no room at all (ActionController::UrlGenerationError: 500).
        var returnToRoomId = viewContext.LastRoomVisitedId
            ?? throw new InvalidOperationException( "No route matches {:action=>\"show\", :controller=>\"rooms\", :id=>nil}: last_room_visited is nil" );

        var page = await app.Database.ReadAsync( db =>
        {
            var items = new MessagePresenter( app, db, requestContext.Request.Host, ffmpeg ).Items( viewContext, search.Results.Messages );
            var recent = MessageSearch.Recent( db, userId ).Select( recentSearch => recentSearch.Query ).ToList( );

            return new SearchesPage( viewContext, search.Results.Query, search.SubmittedQuery, items, recent, returnToRoomId );
        }, requestContext.RequestAborted );

        return await Pages.RenderAsync( requestContext, viewContext, Views.searches.index.Create( page ) );
    }

    /// <summary>
    /// <c>create</c>: <c>Current.user.searches.record(query)</c> (the sanitized query, blank or not; without a <c>q</c>
    /// the <c>NOT NULL</c> column rejects it: 500), then <c>redirect_to searches_url(q: query)</c>.
    /// </summary>
    private static async Task<FrontendResult> CreateAsync( RequestContext requestContext )
    {
        var userId = requestContext.RequireCurrentUser( UndefinedMethodErrors.SearchesForNil ).Id;
        var query = SearchOf( requestContext ).SubmittedQuery;

        var database = requestContext.Database( );
        var recorded = await database.WriteAsync( scope => MessageSearch.Record( scope, userId, query ), requestContext.RequestAborted );

        return requestContext.RedirectTo( requestContext.UrlFor( Routes.SearchesFor( recorded.Query ) ) );
    }

    /// <summary><c>clear</c>: <c>Current.user.searches.destroy_all</c>, then <c>redirect_to searches_url</c>.</summary>
    private static async Task<FrontendResult> ClearAsync( RequestContext requestContext )
    {
        var userId = requestContext.RequireCurrentUser( UndefinedMethodErrors.SearchesForNil ).Id;

        var database = requestContext.Database( );
        await database.WriteAsync( scope => MessageSearch.Clear( scope, userId ), requestContext.RequestAborted );

        return requestContext.RedirectTo( requestContext.UrlFor( Routes.Searches ) );
    }

    /// <summary>
    /// <c>params[:q]</c> as <c>gsub</c> receives it: a string, or nil when absent or null. Anything else has no
    /// <c>gsub</c> (<c>NoMethodError</c>, 500).
    /// </summary>
    private static string? RawQuery( RequestContext requestContext ) => requestContext.Params[ParamNames.Searches.Query] switch
    {
        null or InputNull => null,
        InputText text => text.Value,
        var other => throw new InvalidOperationException( $"undefined method 'gsub' for {other.GetType( ).Name} (params[:q])" ),
    };

    private static CurrentSearch SearchOf( RequestContext requestContext ) =>
        requestContext.Required<CurrentSearch>( "set_messages" );
}
