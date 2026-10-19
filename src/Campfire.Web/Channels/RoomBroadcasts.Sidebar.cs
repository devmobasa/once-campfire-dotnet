using Campfire.Web.Cable;
using Campfire.Web.Views;
using Campfire.Web.Views.Models;

namespace Campfire.Web.Channels;

/// <summary>
/// The sidebar's room list broadcasts: rooms created, renamed, converted, deleted, hidden and shown again. Shared rooms
/// go to the global <c>:rooms</c> stream (open rooms, deletions) or to each member's <c>[ user, :rooms ]</c> (closed
/// rooms, direct rooms, involvement changes), as the rooms controllers send them.
/// </summary>
public sealed partial class RoomBroadcasts
{
    /// <summary><c>[ user, :rooms ]</c>.</summary>
    public static string[] UserRooms( long userId ) => [CableNaming.UserGlobalIdParam( userId ), RoomsStream];

    /// <summary>
    /// <c>Rooms::OpensController#broadcast_create_room</c>: <c>broadcast_prepend_to :rooms, target: :shared_rooms,
    /// partial: "users/sidebars/rooms/shared"</c>.
    /// </summary>
    public async Task OpenRoomCreatedAsync( ViewContext broadcastContext, SidebarRoom room, CancellationToken cancellationToken )
    {
        var html = await Broadcasts.SharedRoomPrepended( broadcastContext, room, cancellationToken );
        Cable.BroadcastStreamTo( [RoomsStream], html.Html );
    }

    /// <summary>
    /// <c>Rooms::OpensController#broadcast_update_room</c>: <c>broadcast_replace_to :rooms, target: [ @room, :list ]</c>,
    /// the target named after the room's type as it is now.
    /// </summary>
    public async Task OpenRoomUpdatedAsync( ViewContext broadcastContext, SidebarRoom room, CancellationToken cancellationToken )
    {
        var html = await Broadcasts.SharedRoomReplaced( broadcastContext, room, cancellationToken );
        Cable.BroadcastStreamTo( [RoomsStream], html.Html );
    }

    /// <summary>
    /// <c>Rooms::ClosedsController#broadcast_create_room</c>: the shared partial rendered once, prepended to each
    /// member's <c>[ user, :rooms ]</c> (<c>room.users</c>, in the order given).
    /// </summary>
    public async Task ClosedRoomCreatedAsync(
        ViewContext broadcastContext,
        SidebarRoom room,
        IEnumerable<long> memberUserIds,
        CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( memberUserIds );

        var html = await Broadcasts.SharedRoomPrepended( broadcastContext, room, cancellationToken );
        foreach ( var userId in memberUserIds )
        {
            Cable.BroadcastStreamTo( UserRooms( userId ), html.Html );
        }
    }

    /// <summary>
    /// <c>Rooms::ClosedsController#broadcast_update_room</c>: the shared partial rendered once, replacing
    /// <c>[ @room, :list ]</c> in each remaining member's <c>[ user, :rooms ]</c>.
    /// </summary>
    public async Task ClosedRoomUpdatedAsync(
        ViewContext broadcastContext,
        SidebarRoom room,
        IEnumerable<long> memberUserIds,
        CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( memberUserIds );

        var html = await Broadcasts.SharedRoomReplaced( broadcastContext, room, cancellationToken );
        foreach ( var userId in memberUserIds )
        {
            Cable.BroadcastStreamTo( UserRooms( userId ), html.Html );
        }
    }

    /// <summary>
    /// <c>Rooms::DirectsController#broadcast_create_room</c>: for each membership (<c>room.memberships.each</c>), its
    /// <c>users/sidebars/rooms/_direct</c> prepended to <c>direct_rooms</c> in its user's <c>[ user, :rooms ]</c>.
    /// </summary>
    public async Task DirectRoomCreatedAsync(
        ViewContext broadcastContext,
        IEnumerable<(long UserId, SidebarDirectItem Item)> memberships,
        CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( memberships );

        foreach ( var (userId, item) in memberships )
        {
            var html = await Broadcasts.DirectRoomPrepended( broadcastContext, item, cancellationToken );
            Cable.BroadcastStreamTo( UserRooms( userId ), html.Html );
        }
    }

    /// <summary>Removes both shared presentations because logical deletion closes an open room before delivery.</summary>
    public void RoomRemoved( RoomKind kind, long roomId )
    {
        Cable.BroadcastStreamTo( [RoomsStream], Broadcasts.RoomListItemRemoved( kind, roomId ).Html );
        if ( kind != RoomKind.Direct )
        {
            var other = kind == RoomKind.Open ? RoomKind.Closed : RoomKind.Open;
            Cable.BroadcastStreamTo( [RoomsStream], Broadcasts.RoomListItemRemoved( other, roomId ).Html );
        }
    }

    /// <summary>
    /// <c>Rooms::InvolvementsController</c> hiding a room: <c>broadcast_remove_to @membership.user, :rooms, target:
    /// [ @room, :list ]</c>.
    /// </summary>
    public void RoomHidden( long userId, RoomKind kind, long roomId ) =>
        Cable.BroadcastStreamTo( UserRooms( userId ), Broadcasts.RoomListItemRemoved( kind, roomId ).Html );

    /// <summary>
    /// <c>Rooms::InvolvementsController</c> showing a hidden room again: <c>broadcast_prepend_to @membership.user,
    /// :rooms, target: :shared_rooms, partial: "users/sidebars/rooms/shared", locals: { room: @room }</c>.
    /// </summary>
    public async Task RoomShownAsync( ViewContext broadcastContext, long userId, SidebarRoom room, CancellationToken cancellationToken )
    {
        var html = await Broadcasts.SharedRoomPrepended( broadcastContext, room, cancellationToken );
        Cable.BroadcastStreamTo( UserRooms( userId ), html.Html );
    }
}
