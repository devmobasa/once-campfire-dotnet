using System.Net;
using Campfire.IntegrationTests.Support;
using Campfire.Web.Paths;

namespace Campfire.IntegrationTests.Slice;

/// <summary>
/// Native room index, typed sibling routes and deletion. Each mutation owns a fresh fixture.
/// </summary>
public sealed class RoomsIndexAndDestroyTests
{
    private static readonly long Designers = SliceAppFactory.Id( "designers" );

    /// <summary>The typed routes that delete Designers: <c>rooms/opens#destroy</c> and <c>rooms/closeds#destroy</c>.</summary>
    public static TheoryData<string> TypedRoomPaths { get; } = new( ) { Routes.RoomsOpen( Designers ), Routes.RoomsClosed( Designers ) };

    [Fact]
    public async Task Index_goes_to_the_users_last_room( )
    {
        using var shared = new SharedSliceApp( );
        var client = await shared.SignedInAsync( "david@37signals.com" );
        var last = LastRoomOf( shared, "david" );

        var reply = await client.GetAsync( Routes.Rooms );

        Assert.Equal( HttpStatusCode.Found, reply.Status );
        Assert.Equal( $"{shared.BaseAddress}rooms/{last}", reply.Location );
    }

    [Theory]
    [InlineData( Routes.RoomsOpens )]
    [InlineData( Routes.RoomsCloseds )]
    [InlineData( Routes.RoomsDirects )]
    public async Task Explicit_room_type_indexes_redirect_to_the_users_last_room( string path )
    {
        using var shared = new SharedSliceApp( );
        var client = await shared.SignedInAsync( "david@37signals.com" );
        var last = LastRoomOf( shared, "david" );

        var reply = await client.GetAsync( path );

        Assert.Equal( HttpStatusCode.Found, reply.Status );
        Assert.Equal( $"{shared.BaseAddress}rooms/{last}", reply.Location );
    }

    [Theory]
    [MemberData( nameof( TypedRoomPaths ) )]
    public async Task Room_type_delete_routes_remove_the_room( string path )
    {
        using var shared = new SharedSliceApp( );
        var (client, token) = await shared.SignedInWithTokenAsync( "david@37signals.com" );

        var reply = await client.SubmitFormAsync( HttpMethod.Delete, path, token, accept: "text/html" );

        Assert.Equal( HttpStatusCode.Found, reply.Status );
        await SliceWait.UntilAsync( ( ) => shared.Scalar<long>( $"SELECT count(*) FROM rooms WHERE id = {Designers}" ) == 0 );
        Assert.Equal( 0, shared.Scalar<long>( $"SELECT count(*) FROM rooms WHERE id = {Designers}" ) );
    }

    [Theory]
    [InlineData( Routes.NewRoom, HttpStatusCode.NotFound )]
    [InlineData( "/rooms/1/edit", HttpStatusCode.NotFound )]
    [InlineData( "/rooms/1/messages/new", HttpStatusCode.NotFound )]
    [InlineData( "/messages/1/boosts/1", HttpStatusCode.MethodNotAllowed )]
    public async Task Missing_or_wrong_method_routes_terminate_before_handlers( string path, HttpStatusCode status )
    {
        using var shared = new SharedSliceApp( );
        var client = await shared.SignedInAsync( "david@37signals.com" );

        var reply = await client.GetAsync( path );

        Assert.Equal( status, reply.Status );
    }

    [Fact]
    public async Task The_retired_settings_route_is_not_found( )
    {
        using var shared = new SharedSliceApp( );
        var client = await shared.SignedInAsync( "david@37signals.com" );

        var reply = await client.GetAsync( Routes.RoomSettings( Designers ) );

        Assert.Equal( HttpStatusCode.NotFound, reply.Status );
    }

    [Fact]
    public async Task Direct_room_routes_do_not_expose_non_direct_rooms( )
    {
        using var factory = new SliceAppFactory( );
        using var client = factory.Client( );
        client.SignInAs( factory, SliceAppFactory.Id( "david" ) );

        var rejected = await client.GetAsync( Routes.RoomsDirect( Designers ) );

        Assert.Equal( HttpStatusCode.Found, rejected.Status );
        Assert.Equal( factory.BaseAddress.ToString( ), rejected.Location );
    }

    [Fact]
    public async Task Destroying_removes_the_room_its_memberships_and_messages_then_tells_every_sidebar( )
    {
        using var factory = new SliceAppFactory( );
        var (client, token) = await factory.SignedInWithPageTokenAsync( "david@37signals.com", Routes.Room( Designers ) );
        using var taps = StreamTaps.On( factory, StreamTaps.Rooms );
        var messageIds = factory.Scalar<string>( $"SELECT group_concat(id) FROM messages WHERE room_id = {Designers}" )!;
        factory.Events.Clear( );

        var reply = await client.SubmitFormAsync( HttpMethod.Delete, Routes.Room( Designers ), token );

        Assert.Equal( HttpStatusCode.Found, reply.Status );
        Assert.Equal( factory.BaseAddress.ToString( ), reply.Location );
        Assert.Equal( 0, factory.Scalar<long>( $"SELECT count(*) FROM memberships WHERE room_id = {Designers}" ) );
        await SliceWait.UntilAsync( ( ) => factory.Scalar<long>( $"SELECT count(*) FROM rooms WHERE id = {Designers}" ) == 0 );
        Assert.Equal( 0, factory.Scalar<long>( $"SELECT count(*) FROM messages WHERE id IN ({messageIds})" ) );
        Assert.Equal( 0, factory.Scalar<long>( $"SELECT count(*) FROM boosts WHERE message_id IN ({messageIds})" ) );
        var removals = factory.Events.Only( "stream" );
        Assert.Contains( $"stream rooms remove list_rooms_closed_{Designers}", removals );
        Assert.Contains( $"stream rooms remove list_rooms_open_{Designers}", removals );
        client.Dispose( );
    }

    [Fact]
    public async Task Destroying_an_open_room_removes_its_original_sidebar_target( )
    {
        using var factory = new SliceAppFactory( );
        var roomId = SliceAppFactory.Id( "pets" );
        var (client, token) = await factory.SignedInWithPageTokenAsync( "david@37signals.com", Routes.Room( roomId ) );
        using var ownedClient = client;
        var page = await client.GetAsync( Routes.UserSidebar );
        var target = $"list_rooms_open_{roomId}";
        Assert.Contains( $"id=\"{target}\"", page.Body, StringComparison.Ordinal );
        using var taps = StreamTaps.On( factory, StreamTaps.Rooms );

        var reply = await client.SubmitFormAsync( HttpMethod.Delete, Routes.Room( roomId ), token );

        Assert.Equal( HttpStatusCode.Found, reply.Status );
        Assert.Contains( taps.Payloads, payload => payload.Payload.Contains( $"target=\"{target}\"", StringComparison.Ordinal ) );
        Assert.Contains( taps.Payloads, payload => payload.Payload.Contains( $"target=\"list_rooms_closed_{roomId}\"", StringComparison.Ordinal ) );
        await SliceWait.UntilAsync( ( ) => factory.Scalar<long>( $"SELECT COUNT(*) FROM rooms WHERE id={roomId}" ) == 0 );
    }

    [Fact]
    public async Task Only_administrators_and_the_creator_can_destroy( )
    {
        using var factory = new SliceAppFactory( );
        var (client, token) = await factory.SignedInWithPageTokenAsync( "jz@37signals.com", Routes.Room( Designers ) );
        using ( client )
        {
            var forbidden = await client.SubmitFormAsync( HttpMethod.Delete, Routes.Room( Designers ), token );
            Assert.Equal( HttpStatusCode.Forbidden, forbidden.Status );
            Assert.Equal( 1, factory.Scalar<long>( $"SELECT count(*) FROM rooms WHERE id = {Designers}" ) );

            factory.Execute( $"UPDATE rooms SET creator_id = {SliceAppFactory.Id( "jz" )} WHERE id = {Designers}" );
            var allowed = await client.SubmitFormAsync( HttpMethod.Delete, Routes.Room( Designers ), token );

            Assert.Equal( HttpStatusCode.Found, allowed.Status );
            await SliceWait.UntilAsync( ( ) => factory.Scalar<long>( $"SELECT count(*) FROM rooms WHERE id = {Designers}" ) == 0 );
            Assert.Equal( 0, factory.Scalar<long>( $"SELECT count(*) FROM rooms WHERE id = {Designers}" ) );
        }
    }

    [Fact]
    public async Task Index_without_any_room_cannot_build_the_room_url( )
    {
        using var factory = new SliceAppFactory( );
        using var client = await factory.SignedInClientAsync( "jz@37signals.com" );
        factory.Execute( $"DELETE FROM memberships WHERE user_id = {SliceAppFactory.Id( "jz" )}" );

        var reply = await client.GetAsync( Routes.Rooms );

        Assert.Equal( HttpStatusCode.InternalServerError, reply.Status );
    }

    /// <summary>The newest room <paramref name="user"/> is a member of: where the rooms index sends them.</summary>
    private static long LastRoomOf( SliceAppFactory factory, string user ) => factory.Scalar<long>(
        "SELECT MAX(rooms.id) FROM rooms JOIN memberships ON memberships.room_id = rooms.id "
        + $"WHERE memberships.user_id = {SliceAppFactory.Id( user )}" );
}
