using System.Globalization;
using System.Net;
using Campfire.IntegrationTests.Support;
using Campfire.Web.Paths;

namespace Campfire.IntegrationTests.Slice;

/// <summary><c>Rooms::DirectsController</c> (<c>test/controllers/rooms/directs_controller_test.rb</c>).</summary>
public sealed class DirectRoomTests( SharedSliceApp shared ) : IClassFixture<SharedSliceApp>
{
    private static readonly string JzId = SliceAppFactory.Id( "jz" ).ToString( CultureInfo.InvariantCulture );

    [Fact]
    public async Task Creating_finds_or_creates_one_room_per_set_of_users_and_prepends_it_to_each_members_pings( )
    {
        using var factory = new SliceAppFactory( );
        var (client, token) = await factory.SignedInWithPageTokenAsync( "david@37signals.com", Routes.NewRoomsDirect );
        using var taps = StreamTaps.On( factory, StreamTaps.UserRooms( "david" ), StreamTaps.UserRooms( "jz" ) );
        var rooms = factory.Scalar<long>( "SELECT count(*) FROM rooms" );
        factory.Events.Clear( );

        var first = await client.SubmitFormAsync( HttpMethod.Post, Routes.RoomsDirects, token, [("user_ids[]", JzId)] );
        var second = await client.SubmitFormAsync( HttpMethod.Post, Routes.RoomsDirects, token, [("user_ids[]", JzId)] );

        var room = factory.Scalar<long>( "SELECT MAX(id) FROM rooms" );
        var roomUrl = $"{factory.BaseAddress}rooms/{room}";
        var type = factory.Scalar<string>( $"SELECT type FROM rooms WHERE id = {room}" );
        var involvement = factory.Scalar<string>( $"SELECT DISTINCT involvement FROM memberships WHERE room_id = {room}" );
        Assert.Equal( rooms + 1, factory.Scalar<long>( "SELECT count(*) FROM rooms" ) );
        Assert.Equal( (first.Location, second.Location), (roomUrl, roomUrl) );
        Assert.Equal( ("Rooms::Direct", "everything"), (type, involvement) );
        Assert.Equal(
            [
                "stream david prepend direct_rooms",
                "stream david prepend direct_rooms",
                "stream jz prepend direct_rooms",
                "stream jz prepend direct_rooms",
            ],
            factory.Events.Only( "stream" ).Order( StringComparer.Ordinal ) );
        Assert.All( taps.Payloads, tap => Assert.Contains( $"data-room-id=\"{room}\"", tap.Payload, StringComparison.Ordinal ) );
        client.Dispose( );
    }

    [Fact]
    public async Task A_single_user_id_is_no_list_to_add_yourself_to( )
    {
        using var factory = new SliceAppFactory( );
        var (client, token) = await factory.SignedInWithPageTokenAsync( "david@37signals.com", Routes.NewRoomsDirect );
        var rooms = factory.Scalar<long>( "SELECT count(*) FROM rooms" );

        var reply = await client.SubmitFormAsync( HttpMethod.Post, Routes.RoomsDirects, token, [("user_ids", JzId)] );

        Assert.Equal( HttpStatusCode.InternalServerError, reply.Status );
        Assert.Equal( rooms, factory.Scalar<long>( "SELECT count(*) FROM rooms" ) );
        client.Dispose( );
    }

    [Fact]
    public async Task The_edit_page_shows_the_other_members( )
    {
        var client = await shared.SignedInAsync( "david@37signals.com" );

        var page = await client.GetAsync( Routes.EditRoomsDirect( SliceAppFactory.Id( "david_and_jason" ) ) );

        Assert.Equal( HttpStatusCode.OK, page.Status );
        Assert.Contains( "<title>Edit settings for Jason</title>", page.Body, StringComparison.Ordinal );
        Assert.Contains( "<strong>Jason</strong>", page.Body, StringComparison.Ordinal );
        Assert.DoesNotContain( "<strong>David</strong>", page.Body, StringComparison.Ordinal );
    }

    [Fact]
    public async Task Every_member_can_delete_a_ping_but_no_other_room( )
    {
        using var factory = new SliceAppFactory( );
        var direct = SliceAppFactory.Id( "david_and_kevin" );
        var designers = SliceAppFactory.Id( "designers" );
        var hq = SliceAppFactory.Id( "hq" );
        var (client, token) = await factory.SignedInWithPageTokenAsync( "kevin@37signals.com", Routes.Room( direct ) );
        using var taps = StreamTaps.On( factory, StreamTaps.Rooms );
        factory.Events.Clear( );

        var closed = await client.SubmitFormAsync( HttpMethod.Delete, Routes.RoomsDirect( designers ), token );
        var open = await client.SubmitFormAsync( HttpMethod.Delete, Routes.RoomsDirect( hq ), token );
        var ping = await client.SubmitFormAsync( HttpMethod.Delete, Routes.RoomsDirect( direct ), token );

        var root = factory.BaseAddress.ToString( );
        Assert.Equal( (root, root, root), (closed.Location, open.Location, ping.Location) );
        await SliceWait.UntilAsync( ( ) => factory.Scalar<long>( $"SELECT count(*) FROM rooms WHERE id = {direct}" ) == 0 );
        Assert.Equal( 2, factory.Scalar<long>( $"SELECT count(*) FROM rooms WHERE id IN ({designers}, {hq})" ) );
        Assert.Equal( 0, factory.Scalar<long>( $"SELECT count(*) FROM rooms WHERE id = {direct}" ) );
        Assert.Equal( [$"stream rooms remove list_rooms_direct_{direct}"], factory.Events.Only( "stream" ) );
        client.Dispose( );
    }
}
