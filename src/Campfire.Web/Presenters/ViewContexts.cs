using Campfire.Application.Accounts;
using Campfire.Application.Data;
using Campfire.Application.Rooms;
using Campfire.Application.Users;
using Campfire.Web.App;
using Campfire.Web.Endpoints.Rooms;
using Campfire.Web.Http;
using Campfire.Web.Paths;
using Campfire.Web.Policy;
using Campfire.Web.Views.Caching;
using Campfire.Web.Views.Helpers;
using Campfire.Web.Views.Models;

namespace Campfire.Web.Presenters;

/// <summary>
/// Builds the <see cref="ViewContext"/> every page renders with: what the application layout and the helpers read from
/// <c>Current</c>, <c>request</c>, <c>flash</c>, the session and the configuration (Rust
/// <c>presenters_a/view_context.rs</c> at <c>3fe9f61</c>), and the one <c>ApplicationController.renderer</c> gives
/// broadcasts.
/// </summary>
public sealed class ViewContexts( CampfireApp app, LayoutAssets layoutAssets )
{
    private static AccountSummary MessageAccount { get; } = new( "", LogoPath( null ), false );

    /// <summary>The <c>last_room</c> cookie <c>TrackedRoomVisit</c> keeps.</summary>
    public const string LastRoomCookie = "last_room";

    /// <summary>
    /// The request's context: <c>Current.account</c>, <c>Current.user</c>, <c>last_room_visited</c>, the platform, the
    /// flash and CSRF tokens. <paramref name="layout"/> is <see cref="LayoutKind.TurboFrame"/> when turbo-rails would
    /// pick its frame layout (<c>layout -&gt; { "turbo_rails/frame" if turbo_frame_request? }</c>).
    /// </summary>
    public async Task<ViewContext> ForRequestAsync( RequestContext requestContext, LayoutKind layout = LayoutKind.Application, bool cacheFragments = false )
    {
        ArgumentNullException.ThrowIfNull( requestContext );

        var cache = cacheFragments ? ForRequestCache( requestContext ) : null;
        var user = requestContext.CurrentUserRecord( );
        var lastRoomCookie = requestContext.Cookies[LastRoomCookie];
        var (account, hasLogo, lastRoomId) = await app.Database.ReadAsync(
            db =>
            {
                var (found, logo) = LoadAccount( db );
                var lastRoom = LastRoomVisitedId( db, user, requestContext.Current<CurrentRoom>( ), lastRoomCookie );
                return (found, logo, lastRoom);
            },
            requestContext.RequestAborted );

        var platform = ToView( new ApplicationPlatform( requestContext.Request.UserAgent ) );
        return RequestViewContext( requestContext, user, Summary( account, hasLogo ), platform, layout, lastRoomId, account?.CustomStyles, cache );
    }

    /// <summary>
    /// A bare message template consumes request URLs, assets, cache and CSRF, but no layout account, platform or
    /// last-room data. Browser pages retain empty token tags for the unchanged frontend.
    /// </summary>
    public ViewContext ForMessageRequest( RequestContext requestContext )
    {
        ArgumentNullException.ThrowIfNull( requestContext );

        return RequestViewContext(
            requestContext,
            requestContext.CurrentUserRecord( ),
            MessageAccount,
            Platform.Unknown,
            LayoutKind.Application,
            lastRoomId: null,
            customStyles: null,
            ForRequestCache( requestContext ) );
    }

    /// <summary>A request's view context, from what <see cref="ForRequestAsync"/> or <see cref="ForMessageRequest"/> loaded.</summary>
    private ViewContext RequestViewContext(
        RequestContext requestContext,
        User? user,
        AccountSummary account,
        Platform platform,
        LayoutKind layout,
        long? lastRoomId,
        string? customStyles,
        FragmentCache? cache ) => new( )
        {
            CurrentUser = user is null ? null : UserPresentation.Current( app, user ),
            Account = account,
            IncludeCsrfTags = true,
            FlashNotice = requestContext.Flash.Notice,
            FlashAlert = requestContext.Flash.Alert,
            Platform = platform,
            VapidPublicKey = app.Options.VapidPublicKey,
            Assets = layoutAssets.ViewAssets,
            CustomStyles = customStyles,
            BaseUrl = requestContext.UrlFor( "" ),
            PublicBaseUrl = app.Options.PublicOrigin,
            RequestUrl = requestContext.Request.Url,
            Referrer = requestContext.Request.Referer,
            LastRoomVisitedId = lastRoomId,
            AppVersion = app.Options.AppVersion,
            Layout = layout,
            Cache = cache,
        };

    /// <summary>
    /// Creates a broadcast context without request-local user or CSRF state.
    /// During a request, <paramref name="baseUrl"/> holds the configured public origin or the request origin, including its port.
    /// </summary>
    public ViewContext ForBroadcast( CampfireDb db, string baseUrl, FragmentCache? cache )
    {
        var (account, hasLogo) = LoadAccount( db );

        return new ViewContext
        {
            Account = Summary( account, hasLogo ),
            VapidPublicKey = app.Options.VapidPublicKey,
            Assets = layoutAssets.ViewAssets,
            BaseUrl = baseUrl,
            RequestUrl = baseUrl + "/",
            AppVersion = app.Options.AppVersion,
            Cache = cache,
        };
    }

    /// <summary>Detached work must call this before acquiring its reader or loading any rendered dependencies.</summary>
    public FragmentCache? CaptureBroadcastCache( string baseUrl ) =>
        app.DatabaseGeneration.Capture( ) is { } generation ? app.Fragments.ForScope( generation, baseUrl ) : null;

    /// <summary>
    /// The fragment store's namespace for the generation captured before this request's reads, keyed by the
    /// configured public origin or else the request's own; <see langword="null"/> renders uncached.
    /// </summary>
    public FragmentCache? ForRequestCache( RequestContext requestContext )
    {
        if ( RenderGeneration.Of( requestContext.HttpContext ) is not { } generation )
        {
            return null;
        }

        return app.Fragments.ForScope( generation, app.Options.PublicOrigin ?? requestContext.UrlFor( "" ) );
    }

    /// <summary>
    /// The base of URLs in a broadcast rendered outside a request (a job's): <c>ApplicationController.renderer</c>'s
    /// default env, <c>http://example.org</c>.
    /// </summary>
    public const string DetachedBaseUrl = "http://example.org";

    /// <summary>
    /// Uses the configured public origin for shared broadcast URLs, keeping a request's Host out of that origin.
    /// Without one, retains the request's scheme, host and port, as current Rails' <c>default_url_options</c> does.
    /// </summary>
    public static string RendererBaseUrl( RequestContext requestContext )
    {
        ArgumentNullException.ThrowIfNull( requestContext );

        return requestContext.App( ).Options.PublicOrigin ?? requestContext.UrlFor( "" );
    }

    /// <summary>
    /// <c>last_room_visited</c>'s id for <paramref name="user"/> (none without one): the room the request was admitted
    /// to when the cookie names it, which needs no query, else <see cref="LastRoomVisited"/>.
    /// </summary>
    private static long? LastRoomVisitedId( CampfireDb db, User? user, CurrentRoom? admitted, string? lastRoomCookie )
    {
        if ( user is null )
        {
            return null;
        }

        if ( admitted is not null && admitted.AuthorizedUserId == user.Id && RailsIntegers.Cast( lastRoomCookie ) == admitted.Room.Id )
        {
            return admitted.Room.Id;
        }

        return LastRoomVisited( db, user.Id, lastRoomCookie )?.Id;
    }

    /// <summary>
    /// <c>last_room_visited</c>: <c>Current.user.rooms.find_by(id: cookies[:last_room]) || Current.user.rooms.original</c>.
    /// The cookie is cast as an integer column casts it.
    /// </summary>
    public static Room? LastRoomVisited( CampfireDb db, long userId, string? lastRoomCookie )
    {
        if ( RailsIntegers.Cast( lastRoomCookie ) is { } roomId && RoomQueries.FindByIdForUser( db, userId, roomId ) is { } room )
        {
            return room;
        }

        return RoomQueries.OriginalForUser( db, userId );
    }

    /// <summary><c>Current.account</c> and <c>logo.attached?</c>.</summary>
    public static (Account? Account, bool HasLogo) LoadAccount( CampfireDb db )
    {
        var account = AccountQueries.First( db );
        var hasLogo = account is not null && AccountQueries.HasLogo( db, account.Id );

        return (account, hasLogo);
    }

    /// <summary><c>fresh_account_logo_path(size:)</c>.</summary>
    public static string LogoPath( Account? account, string? size = null ) =>
        Routes.FreshAccountLogo( account is null ? null : RubyText.NumberTimestamp( UserPresentation.AsUtc( account.UpdatedAt ) ), size );

    private static AccountSummary Summary( Account? account, bool hasLogo ) => new( account?.Name ?? "", LogoPath( account ), hasLogo,
        account is not null && AccountSettings.Read( account.Settings ).RestrictRoomCreationToAdministrators )
    { JoinCode = account?.JoinCode ?? "" };

    private static Platform ToView( ApplicationPlatform platform ) => new( )
    {
        Ios = platform.IsIos,
        Android = platform.IsAndroid,
        Mac = platform.IsMac,
        Windows = platform.IsWindows,
        Chrome = platform.IsChrome,
        Firefox = platform.IsFirefox,
        Safari = platform.IsSafari,
        Edge = platform.IsEdge,
        Mobile = platform.IsMobile,
        Desktop = platform.IsDesktop,
        AppleMessages = platform.IsAppleMessages,
        Browser = platform.Browser,
        OperatingSystem = platform.OperatingSystem ?? "",
    };
}
