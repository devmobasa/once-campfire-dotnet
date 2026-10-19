using Campfire.Application.Messages;
using Campfire.Application.Rooms;
using Campfire.Web.App;
using Campfire.Web.Http;
using Campfire.Web.Paths;
using Campfire.Web.Policy;
using Campfire.Web.Presenters;
using Campfire.Web.Views.Models;

namespace Campfire.Web.Endpoints.Rooms;

/// <summary>
/// <c>RoomsController</c> (<c>app/controllers/rooms_controller.rb</c>): the room page, the jump to the last room, and
/// deleting a room. Its subclasses (<c>Rooms::OpensController</c> and friends) reuse its room helpers and re-declare
/// <c>set_room</c> with their own <c>room_scope</c>; the callbacks they share are in
/// <c>RoomsEndpoints.Callbacks.cs</c>.
/// </summary>
public static partial class RoomsEndpoints
{
    public const string RoomNotFoundAlert = "Room not found or inaccessible";

    /// <summary>
    /// <c>index</c> (inherited by every room controller): <c>redirect_to room_url(Current.user.rooms.last)</c>. With no
    /// rooms, <c>room_url(nil)</c> raises <c>ActionController::UrlGenerationError</c> (500).
    /// </summary>
    public static async Task<FrontendResult> IndexAsync( RequestContext requestContext )
    {
        ArgumentNullException.ThrowIfNull( requestContext );

        var user = requestContext.RequireCurrentUser( UndefinedMethodErrors.RoomsForNil );
        var database = requestContext.Database( );
        var room = await database.ReadAsync( db => RoomQueries.LastForUser( db, user.Id ), requestContext.RequestAborted );
        if ( room is null )
        {
            throw new InvalidOperationException( "No route matches room_url(nil): missing required keys: [:id]" );
        }

        return RedirectToRoom( requestContext, room.Id );
    }

    /// <summary>
    /// Removes access and records recoverable room destruction. Sidebar removal follows the committed membership change.
    /// Redirects to the root after acknowledging the broadcast.
    /// </summary>
    public static async Task<FrontendResult> DestroyAsync( RequestContext requestContext )
    {
        ArgumentNullException.ThrowIfNull( requestContext );

        var room = RoomOrNil( requestContext ) ?? throw new InvalidOperationException( "undefined method 'destroy' for nil" );
        var app = requestContext.App( );
        await app.Database.WriteAsync( scope => RoomDeletions.Start( scope, room.Id ), requestContext.RequestAborted );
        await RoomDeletions.BroadcastAsync( app.Database, room.Id,
            removed => app.Broadcasts.RoomRemoved( RoomPresentation.Kind( removed.Type ), removed.Id ), requestContext.RequestAborted );

        return requestContext.RedirectTo( requestContext.UrlFor( Routes.Root ) );
    }

    /// <summary>
    /// <c>show</c> (also <c>GET /rooms/:room_id/@:message_id</c>): the page of messages around
    /// <c>params[:message_id]</c> when it names one of the room's messages, else the last page.
    /// </summary>
    public static async Task<FrontendResult> ShowAsync( RequestContext requestContext, long? messageId = null )
    {
        requestContext.RespondTo( Mime.Html );
        var app = requestContext.App( );
        var room = RoomOf( requestContext );
        var user = requestContext.RequireCurrentUserRecord( UndefinedMethodErrors.RoomsForNil );
        var presenters = await requestContext.MessagePresentersAsync( );
        var viewContext = await Pages.ContextAsync( requestContext, cacheFragments: true );

        var page = await app.Database.ReadAsync( db =>
        {
            var shown = messageId is { } id ? MessageQueries.FindByIdInRoom( db, room.Id, id ) : null;
            var messages = shown is not null ? MessagePages.PageAround( db, room.Id, shown ) : MessagePages.LastPage( db, room.Id );
            var items = presenters.For( db, room ).Items( viewContext, messages );
            var original = RoomQueries.Original( db )?.Id == room.Id;

            return new RoomsShowPage(
                viewContext,
                RoomPresentation.View( db, room, user ),
                UserPresentation.AsUtc( room.UpdatedAt ),
                UserPresentation.View( app, user ),
                items,
                original && !MessagePages.IsPaged( db, room.Id ),
                viewContext.Account.JoinCode,
                app.Broadcasts.SignedRoomMessages( room ) );
        }, requestContext.RequestAborted );

        return await Pages.RenderAsync( requestContext, viewContext, Views.rooms.show_html.Create( page ) );
    }
}
