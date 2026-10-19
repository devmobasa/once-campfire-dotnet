using Campfire.Application.Memberships;
using Campfire.Application.Tests.Support;

namespace Campfire.Application.Tests.Memberships;

/// <summary><c>test/models/membership_test.rb</c> (<c>Membership::Connectable</c>) and Rust's <c>membership_test.rs</c>.</summary>
public sealed class MembershipTests
{
    private static readonly long DavidWatercooler = TestDatabase.Id( "david_watercooler" );

    private static readonly TimeSpan TtlPlusOne = Membership.ConnectionTtl + TimeSpan.FromSeconds( 1 );

    private static Task<Membership> ConnectedAsync( TestDatabase testDatabase ) =>
        testDatabase.WriteAsync( scope => MembershipPresence.Connected( scope, DavidWatercooler ) );

    private static Task<Membership> DisconnectedAsync( TestDatabase testDatabase ) =>
        testDatabase.WriteAsync( scope => MembershipPresence.Disconnected( scope, DavidWatercooler ) );

    private static Task<Membership> StoredAsync( TestDatabase testDatabase ) => testDatabase.ReadAsync( db => MembershipQueries.Find( db, DavidWatercooler ) );

    [Fact]
    public async Task Connected_is_false_when_the_connection_is_stale( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        var membership = await ConnectedAsync( testDatabase );

        testDatabase.Clock.Travel( TtlPlusOne );

        Assert.False( membership.IsConnected( testDatabase.Clock.Now ) );
    }

    [Fact]
    public async Task Connecting_marks_the_membership_connected_and_counts_each_connection( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );

        var membership = await ConnectedAsync( testDatabase );
        Assert.True( membership.IsConnected( testDatabase.Clock.Now ) );
        Assert.Equal( 1, membership.Connections );

        membership = await ConnectedAsync( testDatabase );
        Assert.Equal( 2, membership.Connections );
        Assert.Equal( 2, (await StoredAsync( testDatabase )).Connections );
    }

    [Fact]
    public async Task Connecting_resets_a_stale_connection_count( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        await ConnectedAsync( testDatabase );
        Assert.Equal( 2, (await ConnectedAsync( testDatabase )).Connections );

        testDatabase.Clock.Travel( TtlPlusOne );

        Assert.Equal( 1, (await ConnectedAsync( testDatabase )).Connections );
    }

    [Fact]
    public async Task Disconnecting_counts_down_and_clears_connected_at_after_the_last_connection( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        await ConnectedAsync( testDatabase );
        await ConnectedAsync( testDatabase );

        var membership = await DisconnectedAsync( testDatabase );
        Assert.True( membership.IsConnected( testDatabase.Clock.Now ) );
        Assert.Equal( 1, membership.Connections );

        membership = await DisconnectedAsync( testDatabase );
        Assert.False( membership.IsConnected( testDatabase.Clock.Now ) );
        Assert.Equal( 0, membership.Connections );
        Assert.Null( (await StoredAsync( testDatabase )).ConnectedAt );
    }

    [Fact]
    public async Task Disconnecting_resets_a_stale_connection_count( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        await ConnectedAsync( testDatabase );
        await ConnectedAsync( testDatabase );

        testDatabase.Clock.Travel( TtlPlusOne );

        Assert.Equal( 0, (await DisconnectedAsync( testDatabase )).Connections );
    }

    [Fact]
    public async Task Refreshing_the_connection_makes_a_stale_membership_connected_again( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        var membership = await ConnectedAsync( testDatabase );

        testDatabase.Clock.Travel( TtlPlusOne );
        Assert.False( membership.IsConnected( testDatabase.Clock.Now ) );

        membership = await testDatabase.WriteAsync( scope => MembershipPresence.RefreshConnection( scope, DavidWatercooler ) );
        Assert.True( membership.IsConnected( testDatabase.Clock.Now ) );
    }

    [Fact]
    public async Task Present_marks_the_room_read_and_counts_connections_without_touching_updated_at( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        await testDatabase.ExecuteAsync( "UPDATE memberships SET unread_at = '2026-01-01 00:00:00' WHERE id = @p0", DavidWatercooler );
        var before = await StoredAsync( testDatabase );

        await testDatabase.WriteAsync( scope => MembershipPresence.Present( scope, DavidWatercooler ) );

        var stored = await StoredAsync( testDatabase );
        Assert.Equal( 1, stored.Connections );
        Assert.Null( stored.UnreadAt );
        Assert.Equal( before.UpdatedAt, stored.UpdatedAt );
    }

    [Fact]
    public async Task Disconnect_all_resets_connected_memberships( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        await ConnectedAsync( testDatabase );

        var count = await testDatabase.WriteAsync( MembershipPresence.DisconnectAll );

        var stored = await StoredAsync( testDatabase );
        Assert.Equal( 1, count );
        Assert.Equal( (0L, ( DateTime? ) null), (stored.Connections, stored.ConnectedAt) );
    }

    [Fact]
    public async Task Removing_a_membership_resets_the_users_connections( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );

        await testDatabase.WriteAsync( scope => MembershipOperations.Destroy( scope, DavidWatercooler ) );

        Assert.Equal( [$"disconnect user {TestDatabase.Id( "david" )} reconnect=true"], testDatabase.Hooks.Events );
    }

    [Fact]
    public async Task Reading_is_a_no_op_when_already_read( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        var before = await StoredAsync( testDatabase );
        testDatabase.Clock.Travel( TimeSpan.FromSeconds( 1 ) );

        await testDatabase.WriteAsync( scope => MembershipOperations.Read( scope, DavidWatercooler ) );

        Assert.Equal( before.UpdatedAt, (await StoredAsync( testDatabase )).UpdatedAt );
    }

    [Fact]
    public async Task Updating_involvement_writes_only_a_change_and_returns_the_previous_value( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        var before = await StoredAsync( testDatabase );
        testDatabase.Clock.Travel( TimeSpan.FromSeconds( 1 ) );

        var previous = await testDatabase.WriteAsync( scope => MembershipOperations.UpdateInvolvement( scope, DavidWatercooler, Involvement.Invisible ) );
        var unchanged = await testDatabase.WriteAsync( scope => MembershipOperations.UpdateInvolvement( scope, DavidWatercooler, Involvement.Invisible ) );

        var stored = await StoredAsync( testDatabase );
        Assert.Equal( Involvement.Everything, previous );
        Assert.Equal( Involvement.Invisible, stored.Involvement );
        Assert.Equal( "invisible", await testDatabase.ScalarAsync<string>( "SELECT involvement FROM memberships WHERE id = @p0", DavidWatercooler ) );
        Assert.True( stored.UpdatedAt > before.UpdatedAt );
        Assert.Equal( Involvement.Invisible, unchanged );
        Assert.Equal( stored.UpdatedAt, (await StoredAsync( testDatabase )).UpdatedAt );
    }

    [Fact]
    public async Task The_sidebar_splits_direct_rooms_and_offers_placeholder_users( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );

        var sidebar = await testDatabase.ReadAsync( db => MembershipQueries.Sidebar( db, TestDatabase.Id( "david" ) ) );

        Assert.Equal( 2, sidebar.DirectMemberships.Count );
        Assert.All( sidebar.DirectMemberships, row => Assert.True( row.Room.IsDirect ) );
        Assert.Equal( ["All Pets", "All Talk", "Designers", "HQ"], sidebar.OtherMemberships.Select( row => row.Room.Name ) );
        Assert.Equal( [TestDatabase.Id( "bender" ), TestDatabase.Id( "jz" )], sidebar.DirectPlaceholderUsers.Select( user => user.Id ).Order( ) );
    }
}
