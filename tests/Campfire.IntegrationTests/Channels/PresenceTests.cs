using System.Globalization;
using System.Text.Json.Nodes;
using Campfire.Application.Memberships;
using Campfire.IntegrationTests.Support;
using Campfire.Web.Cable;
using Campfire.Web.Paths;
using Campfire.Web.Tests.Cable.Support;
using Microsoft.AspNetCore.Hosting;

namespace Campfire.IntegrationTests.Channels;

/// <summary>
/// <c>PresenceChannel</c> over <c>Membership::Connectable</c> with the app's <see cref="TimeProvider"/> moving: the
/// connection count, the 60 s TTL, <c>refresh</c>, the tab-visibility actions, reconnects after gaps, and what the TTL
/// means for unread marking (<c>room.receive</c> marks only disconnected members). Also the heartbeat's clock and
/// <c>Membership.disconnect_all</c> at boot.
/// </summary>
public sealed class PresenceTests
{
    private static readonly long Designers = SliceAppFactory.Id( "designers" );

    private static readonly long Jason = SliceAppFactory.Id( "jason" );

    private static readonly long JasonDesigners = SliceAppFactory.Id( "jason_designers" );

    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds( 60 );

    [Fact]
    public async Task Connections_are_counted_until_the_ttl_expires_then_start_again( )
    {
        await using var factory = new SliceAppFactory( );
        using var jason = await factory.SignedInClientAsync( "jason@37signals.com" );
        var presence = Presence( );

        await using var first = await Realtime.ConnectAsync( jason );
        await Realtime.ExpectConfirmedAsync( first, presence );
        await using var second = await Realtime.ConnectAsync( jason );
        await Realtime.ExpectConfirmedAsync( second, presence );
        var both = await MembershipAsync( factory );

        factory.Clock.Advance( Ttl + TimeSpan.FromSeconds( 1 ) );
        await using var third = await Realtime.ConnectAsync( jason );
        await Realtime.ExpectConfirmedAsync( third, presence );
        var afterTtl = await MembershipAsync( factory );

        Assert.Equal( 2, both.Connections );
        Assert.Equal( SliceAppFactory.VectorNow.UtcDateTime, both.ConnectedAt );
        Assert.Equal( 1, afterTtl.Connections );
        Assert.Equal( factory.Clock.GetUtcNow( ).UtcDateTime, afterTtl.ConnectedAt );
    }

    [Fact]
    public async Task Refresh_keeps_the_member_connected_and_unread_marking_starts_once_the_ttl_lapses( )
    {
        await using var factory = new SliceAppFactory( );
        using var jason = await factory.SignedInClientAsync( "jason@37signals.com" );
        using var david = await factory.SignedInClientAsync( "david@37signals.com" );
        var token = await david.CsrfTokenAsync( Routes.Room( Designers ) );
        await using var cable = await Realtime.ConnectAsync( jason );
        await Realtime.ExpectConfirmedAsync( cable, Presence( ) );

        // The presence controller refreshes every 50 s.
        factory.Clock.Advance( TimeSpan.FromSeconds( 50 ) );
        await cable.PerformAsync( Presence( ), new JsonObject { ["action"] = "refresh" } );
        var refreshed = await WaitForMembershipAsync( factory, membership => membership.ConnectedAt == factory.Clock.GetUtcNow( ).UtcDateTime );
        factory.Clock.Advance( TimeSpan.FromSeconds( 50 ) );
        await david.PostMessageAsync( Designers, token, body: "<p>Still here?</p>" );
        var whileConnected = await MembershipAsync( factory );

        factory.Clock.Advance( Ttl + TimeSpan.FromSeconds( 1 ) );
        await david.PostMessageAsync( Designers, token, body: "<p>Gone quiet</p>" );
        var afterTtl = await MembershipAsync( factory );

        Assert.Equal( 1, refreshed.Connections );
        Assert.Null( whileConnected.UnreadAt );
        Assert.NotNull( afterTtl.UnreadAt );
    }

    [Fact]
    public async Task Present_after_the_membership_disappears_does_not_broadcast_a_read( )
    {
        await using var factory = new SliceAppFactory( );
        using var jason = await factory.SignedInClientAsync( "jason@37signals.com" );
        await using var cable = await Realtime.ConnectAsync( jason );
        var reads = CableTestClient.Identifier( ("channel", "ReadRoomsChannel") );
        await Realtime.ExpectConfirmedAsync( cable, reads );
        await Realtime.SubscribeAsync( cable, Presence( ) );

        // The channel still has its subscribed room, but present's database update can find no membership.
        factory.Execute( $"DELETE FROM memberships WHERE id = {JasonDesigners}" );
        await cable.PerformAsync( Presence( ), new JsonObject { ["action"] = "present" } );
        var heartbeat = CableTestClient.Identifier( ("channel", "HeartbeatChannel") );
        await Realtime.ExpectConfirmedAsync( cable, heartbeat );
        await cable.AssertSilentAsync( );

        Assert.Equal( 0, factory.Scalar<long>( $"SELECT count(*) FROM memberships WHERE id = {JasonDesigners}" ) );
    }

    [Fact]
    public async Task Absent_and_present_follow_the_tabs_visibility( )
    {
        await using var factory = new SliceAppFactory( );
        using var jason = await factory.SignedInClientAsync( "jason@37signals.com" );
        await using var cable = await Realtime.ConnectAsync( jason );
        var reads = CableTestClient.Identifier( ("channel", "ReadRoomsChannel") );
        await Realtime.ExpectConfirmedAsync( cable, reads );
        var (_, readOnSubscribe) = await Realtime.SubscribeAsync( cable, Presence( ) );

        await cable.PerformAsync( Presence( ), new JsonObject { ["action"] = "absent" } );
        var absent = await WaitForMembershipAsync( factory, membership => membership.ConnectedAt is null );
        factory.Execute( $"UPDATE memberships SET unread_at = '2026-09-26 13:00:00' WHERE id = {JasonDesigners}" );
        factory.Clock.Advance( TimeSpan.FromMilliseconds( 125 ) );
        await cable.PerformAsync( Presence( ), new JsonObject { ["action"] = "present" } );
        var readOnPresent = await cable.NextTextAsync( );
        var present = await MembershipAsync( factory );

        var subscribedRead = JsonNode.Parse( Assert.Single( readOnSubscribe ) )!["message"]!;
        var presentRead = JsonNode.Parse( readOnPresent )!["message"]!;
        Assert.Equal( Designers, subscribedRead["room_id"]!.GetValue<long>( ) );
        Assert.NotNull( subscribedRead["at"] );
        Assert.Equal( 1790428492d, subscribedRead["at"]!.GetValue<double>( ) );
        Assert.Equal( Designers, presentRead["room_id"]!.GetValue<long>( ) );
        Assert.Equal( 1790428492.125d, presentRead["at"]!.GetValue<double>( ) );
        Assert.Equal( 0, absent.Connections );
        Assert.Equal( 1, present.Connections );
        Assert.NotNull( present.ConnectedAt );
        Assert.Null( present.UnreadAt );
    }

    [Fact]
    public async Task A_dropped_socket_disconnects_and_a_reconnect_after_a_gap_is_present_again( )
    {
        await using var factory = new SliceAppFactory( );
        using var jason = await factory.SignedInClientAsync( "jason@37signals.com" );
        var dropped = await Realtime.ConnectAsync( jason );
        await Realtime.ExpectConfirmedAsync( dropped, Presence( ) );

        await dropped.DisposeAsync( ); // aborts: no close handshake, as when a network drops
        var gone = await WaitForMembershipAsync( factory, membership => membership.ConnectedAt is null );
        factory.Clock.Advance( TimeSpan.FromSeconds( 30 ) );
        await using var back = await Realtime.ConnectAsync( jason );
        await Realtime.ExpectConfirmedAsync( back, Presence( ) );
        var present = await MembershipAsync( factory );

        Assert.Equal( 0, gone.Connections );
        Assert.Equal( 1, present.Connections );
        Assert.Equal( factory.Clock.GetUtcNow( ).UtcDateTime, present.ConnectedAt );
    }

    [Fact]
    public async Task Counts_left_by_a_process_that_died_expire_with_the_ttl( )
    {
        await using var factory = new SliceAppFactory( );
        var twoMinutesAgo = SliceAppFactory.VectorNow.AddMinutes( -2 ).UtcDateTime.ToString( "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture );
        factory.Execute( $"UPDATE memberships SET connections = 3, connected_at = '{twoMinutesAgo}' WHERE id = {JasonDesigners}" );
        using var jason = await factory.SignedInClientAsync( "jason@37signals.com" );

        // Membership.disconnect_all at boot resets only the connected scope, so this stale row is left as it was.
        var atBoot = await MembershipAsync( factory );
        await using var cable = await Realtime.ConnectAsync( jason );
        await Realtime.ExpectConfirmedAsync( cable, Presence( ) );
        var present = await MembershipAsync( factory );

        Assert.Equal( 3, atBoot.Connections );
        Assert.Equal( 1, present.Connections );
    }

    [Fact]
    public async Task Pings_carry_the_apps_clock( )
    {
        await using var factory = new FastBeatAppFactory( );
        using var jason = await factory.SignedInClientAsync( "jason@37signals.com" );
        await using var cable = await Realtime.ConnectAsync( jason );

        var frozen = await NextPingAsync( cable );
        factory.Clock.Advance( TimeSpan.FromHours( 1 ) );
        var advanced = await NextPingAsync( cable );
        while ( advanced == frozen )
        {
            advanced = await NextPingAsync( cable ); // a ping already queued before the clock moved
        }

        Assert.Equal( SliceAppFactory.VectorNow.ToUnixTimeSeconds( ), frozen );
        Assert.Equal( SliceAppFactory.VectorNow.AddHours( 1 ).ToUnixTimeSeconds( ), advanced );
    }

    private static string Presence( ) => CableTestClient.Identifier( ("channel", "PresenceChannel"), ("room_id", Designers) );

    private static Task<Membership> MembershipAsync( SliceAppFactory factory ) =>
        factory.App.Database.ReadAsync( db => MembershipQueries.FindByRoomAndUser( db, Designers, Jason )!, CancellationToken.None );

    /// <summary>The membership once <paramref name="condition"/> holds; the server writes it after the frame that caused it.</summary>
    private static Task<Membership> WaitForMembershipAsync( SliceAppFactory factory, Func<Membership, bool> condition ) =>
        SliceWait.UntilAsync( ( ) => MembershipAsync( factory ), condition );

    private static async Task<long> NextPingAsync( CableTestClient cable )
    {
        while ( true )
        {
            var frame = await cable.NextIncludingPingsAsync( );
            if ( frame.IsPing )
            {
                return JsonNode.Parse( frame.Text! )!["message"]!.GetValue<long>( );
            }
        }
    }

    /// <summary>The app with a 100 ms heartbeat, so pings arrive while the test waits.</summary>
    private sealed class FastBeatAppFactory : SliceAppFactory
    {
        protected override void ConfigureWebHost( IWebHostBuilder builder )
        {
            base.ConfigureWebHost( builder );
            builder.UseSetting( "Campfire:Cable:BeatInterval", "00:00:00.100" );
        }
    }
}
