using System.Text.Json.Nodes;
using Campfire.Application.Rooms;
using Campfire.Application.Security;
using Campfire.Application.Serialization;
using Campfire.Web.Cable;
using Campfire.Web.Presenters;
using Campfire.Web.Views;
using Campfire.Web.Views.Models;

namespace Campfire.Web.Channels;

/// <summary>
/// The broadcasts the app makes, with the stream names and payloads its Turbo and Action Cable calls produce
/// (Rust <c>channels/broadcasts.rs</c> at <c>3fe9f61</c>). Stream names: a record is its GlobalID param (an STI room
/// names its class: <c>gid://campfire/Rooms::Open/1</c>), a symbol itself; <c>turbo_stream_from @room, :messages</c>
/// is <c>&lt;room gid param&gt;:messages</c>. Broadcasts happen synchronously in the request, after commit.
/// </summary>
public sealed partial class RoomBroadcasts( CableServer cable, TurboStreamNames streamNames )
{
    /// <summary><c>:messages</c>.</summary>
    public const string MessagesStream = "messages";

    /// <summary><c>:rooms</c>.</summary>
    public const string RoomsStream = "rooms";

    public CableServer Cable { get; } = cable;

    /// <summary>A room's GlobalID param, which names its STI class.</summary>
    public static string RoomGlobalIdParam( Room room )
    {
        ArgumentNullException.ThrowIfNull( room );

        return GlobalId.For( RoomTypes.ClassName( room.Type ), room.Id ).ToParam( );
    }

    /// <summary><c>[ room, :messages ]</c>.</summary>
    public static string[] RoomMessages( Room room ) => [RoomGlobalIdParam( room ), MessagesStream];

    /// <summary><c>Turbo::StreamsChannel.signed_stream_name([ room, :messages ])</c>, for <c>turbo_stream_from</c>.</summary>
    public string SignedRoomMessages( Room room ) => streamNames.Sign( RoomMessages( room ) );

    /// <summary><c>signed_stream_name(:rooms)</c>.</summary>
    public string SignedRooms( ) => streamNames.Sign( RoomsStream );

    /// <summary><c>signed_stream_name([ Current.user, :rooms ])</c>.</summary>
    public string SignedUserRooms( long userId ) => streamNames.Sign( CableNaming.UserGlobalIdParam( userId ), RoomsStream );

    /// <summary>
    /// <c>Message#broadcast_create</c>: the rendered <c>messages/_message</c> appended to <c>[ room, :messages ]</c>,
    /// returning <c>messages/create.turbo_stream</c> as rendered (the
    /// template's own trailing newline included); the broadcast is the same <c>&lt;turbo-stream&gt;</c> without it, as
    /// <c>turbo_stream_action_tag</c> builds it.
    /// </summary>
    public async Task<byte[]> MessageCreatedAsync(
        Room room,
        ViewContext broadcastContext,
        MessageItem message,
        CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( room );
        var html = await Broadcasts.MessageCreatedUtf8( broadcastContext, message, RoomPresentation.Kind( room.Type ), cancellationToken );
        Cable.BroadcastStreamToUtf8( RoomMessages( room ), WithoutTrailingNewline( html ) );
        return html;
    }

    /// <summary>
    /// <c>broadcast_unread_room</c>: one timestamped payload to each current unread or connected member's unread stream.
    /// </summary>
    public async Task UnreadRoomAsync( long roomId, DateTime createdAt, IAsyncEnumerable<long> memberUserIds, CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( memberUserIds );

        var payload = JsonPayload.Encode( new JsonObject
        {
            ["roomId"] = roomId,
            ["at"] = (createdAt - DateTime.UnixEpoch).TotalSeconds,
        } );
        await foreach ( var userId in memberUserIds.WithCancellation( cancellationToken ) )
        {
            cancellationToken.ThrowIfCancellationRequested( );
            Cable.BroadcastEncoded( UnreadRoomsChannel.StreamNameFor( userId ), payload );
        }
    }

    /// <summary><c>Message#broadcast_remove</c>: <c>broadcast_remove_to room, :messages</c>.</summary>
    public void MessageRemoved( Room room, string clientMessageId ) =>
        Cable.BroadcastStreamTo( RoomMessages( room ), Broadcasts.MessageRemoved( clientMessageId ).Html );

    /// <summary><c>PresenceChannel#broadcast_read_room</c>: <c>{ room_id:, at: }</c> to <c>user_&lt;id&gt;_reads</c>.</summary>
    public void ReadRoom( long userId, long roomId, DateTimeOffset at ) =>
        Cable.Broadcast( ReadRoomsChannel.StreamNameFor( userId ), new JsonObject
        {
            ["room_id"] = roomId,
            ["at"] = (at - DateTimeOffset.UnixEpoch).TotalSeconds,
        } );

    /// <summary>The rendered template without its own trailing newline, as <c>turbo_stream_action_tag</c> builds the tag.</summary>
    private static ReadOnlySpan<byte> WithoutTrailingNewline( byte[] html )
    {
        ReadOnlySpan<byte> content = html;
        if ( content.EndsWith( ( byte ) '\n' ) )
        {
            return content[..^1];
        }

        return content;
    }
}
