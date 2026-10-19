using Campfire.Application.Data;
using Campfire.Application.Memberships;
using Campfire.Application.Rooms;
using Campfire.Application.Tests.Support;
using Campfire.Application.Users;

namespace Campfire.Application.Tests.Rooms;

/// <summary><c>test/models/room_test.rb</c>, <c>rooms/direct_test.rb</c>, <c>rooms/open_test.rb</c>, and Rust's <c>room_test.rs</c>.</summary>
public sealed class RoomTests
{
    private static Task<List<long>> MemberIdsAsync( TestDatabase testDatabase, long roomId ) =>
        testDatabase.ReadAsync( db => RoomQueries.UserIds( db, roomId ) );

    private static Task<Room> RoomAsync( TestDatabase testDatabase, string label ) =>
        testDatabase.ReadAsync( db => RoomQueries.FindById( db, TestDatabase.Id( label ) )! );

    [Fact]
    public async Task Granting_membership_adds_the_user_to_the_room( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        var watercooler = await RoomAsync( testDatabase, "watercooler" );

        await testDatabase.WriteAsync( scope => RoomMemberships.GrantTo( scope, watercooler, [TestDatabase.Id( "kevin" )] ) );

        Assert.Contains( TestDatabase.Id( "kevin" ), await MemberIdsAsync( testDatabase, watercooler.Id ) );
    }

    [Fact]
    public async Task Revoke_membership_from_user_resets_their_connections_after_commit( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        var watercooler = await RoomAsync( testDatabase, "watercooler" );

        await testDatabase.WriteAsync( scope => RoomMemberships.RevokeFrom( scope, watercooler.Id, [TestDatabase.Id( "david" )] ) );

        Assert.DoesNotContain( TestDatabase.Id( "david" ), await MemberIdsAsync( testDatabase, watercooler.Id ) );
        Assert.Equal( [$"disconnect user {TestDatabase.Id( "david" )} reconnect=true"], testDatabase.Hooks.Events );
    }

    [Fact]
    public async Task Revising_memberships_grants_and_revokes_in_one_call( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        var watercooler = await RoomAsync( testDatabase, "watercooler" );

        await testDatabase.WriteAsync( scope => RoomMemberships.Revise( scope, watercooler, [TestDatabase.Id( "kevin" )], [TestDatabase.Id( "david" )] ) );

        var members = await MemberIdsAsync( testDatabase, watercooler.Id );
        Assert.Contains( TestDatabase.Id( "kevin" ), members );
        Assert.DoesNotContain( TestDatabase.Id( "david" ), members );
    }

    [Fact]
    public async Task Create_for_users_gives_them_immediate_membership_with_the_default_involvement( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );

        var room = await testDatabase.WriteAsync( scope => RoomOperations.CreateFor(
            scope,
            RoomType.Closed,
            "Hello!",
            TestDatabase.Id( "david" ),
            [TestDatabase.Id( "kevin" ), TestDatabase.Id( "david" )] ) );

        var memberships = await testDatabase.ReadAsync( db => MembershipQueries.ForRoom( db, room.Id ) );
        Assert.Equal( [TestDatabase.Id( "david" ), TestDatabase.Id( "kevin" )], memberships.Select( membership => membership.UserId ).Order( ) );
        Assert.All( memberships, membership => Assert.Equal( Involvement.Mentions, membership.Involvement ) );
        Assert.Equal( TestDatabase.Id( "kevin" ), await testDatabase.ScalarAsync<long>(
            "SELECT user_id FROM memberships WHERE room_id = @p0 ORDER BY id LIMIT 1", room.Id ) );
    }

    [Fact]
    public async Task Granted_memberships_are_stamped_by_sqlite_as_insert_all_does( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );

        var room = await testDatabase.WriteAsync( scope => RoomOperations.CreateFor(
            scope,
            RoomType.Closed,
            "Hello!",
            TestDatabase.Id( "david" ),
            [TestDatabase.Id( "kevin" )] ) );

        var createdAt = await testDatabase.ScalarAsync<string>( "SELECT created_at FROM memberships WHERE room_id = @p0", room.Id );
        Assert.Matches( @"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3}$", createdAt );
    }

    [Fact]
    public async Task Type_predicates_tell_open_closed_and_direct_rooms_apart( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );

        Assert.True( (await RoomAsync( testDatabase, "pets" )).IsOpen );
        Assert.False( (await RoomAsync( testDatabase, "pets" )).IsDirect );
        Assert.True( (await RoomAsync( testDatabase, "david_and_jason" )).IsDirect );
        Assert.True( (await RoomAsync( testDatabase, "designers" )).IsClosed );
    }

    [Fact]
    public async Task Direct_rooms_keep_their_type( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );

        var error = await Assert.ThrowsAsync<RecordInvalidException>( ( ) =>
            testDatabase.WriteAsync( scope => RoomOperations.Update(
                scope,
                TestDatabase.Id( "david_and_jason" ),
                new RoomChanges { Type = RoomType.Open } ) ) );

        Assert.Equal( ["can't be changed for a direct room"], error.Errors.ErrorsOn( "type" ) );
        Assert.True( (await RoomAsync( testDatabase, "david_and_jason" )).IsDirect );
    }

    [Fact]
    public async Task Destroying_a_room_removes_access_and_leaves_messages_for_recovery( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        var watercooler = TestDatabase.Id( "watercooler" );

        await testDatabase.WriteAsync( scope => RoomOperations.Destroy( scope, watercooler ) );

        Assert.True( await testDatabase.CountAsync( "messages", "room_id = @p0", watercooler ) > 0 );
        Assert.Equal( 0, await testDatabase.CountAsync( "memberships", "room_id = @p0", watercooler ) );
        Assert.True( (await testDatabase.ReadAsync( db => RoomQueries.FindById( db, watercooler ) ))!.IsClosed );
    }

    [Fact]
    public async Task Only_one_direct_room_exists_for_the_same_users( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        long[] jzAndKevin = [TestDatabase.Id( "jz" ), TestDatabase.Id( "kevin" )];

        var room1 = await testDatabase.WriteAsync( scope => RoomOperations.FindOrCreateDirectFor( scope, jzAndKevin, TestDatabase.Id( "jz" ) ) );
        var room2 = await testDatabase.WriteAsync( scope => RoomOperations.FindOrCreateDirectFor(
            scope,
            [.. jzAndKevin.Reverse( )],
            TestDatabase.Id( "kevin" ) ) );
        var existing = await testDatabase.WriteAsync( scope => RoomOperations.FindOrCreateDirectFor(
            scope,
            [TestDatabase.Id( "david" ), TestDatabase.Id( "kevin" )],
            TestDatabase.Id( "david" ) ) );

        Assert.Equal( room1.Id, room2.Id );
        Assert.Equal( TestDatabase.Id( "david_and_kevin" ), existing.Id );
        var members = await MemberIdsAsync( testDatabase, room1.Id );
        Assert.Equal( jzAndKevin.Order( ), members.Order( ) );
        Assert.All(
            await testDatabase.ReadAsync( db => MembershipQueries.ForRoom( db, room1.Id ) ),
            membership => Assert.Equal( Involvement.Everything, membership.Involvement ) );
    }

    [Fact]
    public async Task An_open_room_grants_access_to_all_users_after_creation( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );

        var room = await testDatabase.WriteAsync( scope => RoomOperations.Create(
            scope,
            RoomType.Open,
            "My open room with everyone!",
            TestDatabase.Id( "david" ) ) );

        Assert.Equal( await testDatabase.CountAsync( "users" ), (await MemberIdsAsync( testDatabase, room.Id )).Count );
    }

    [Fact]
    public async Task A_closed_room_that_becomes_open_grants_access_to_all_users_after_commit( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        var watercooler = TestDatabase.Id( "watercooler" );

        await testDatabase.WriteAsync( scope => RoomOperations.Update( scope, watercooler, new RoomChanges { Type = RoomType.Open } ) );

        Assert.Equal( await testDatabase.CountAsync( "users" ), (await MemberIdsAsync( testDatabase, watercooler )).Count );
        Assert.Equal( "Rooms::Open", await testDatabase.ScalarAsync<string>( "SELECT type FROM rooms WHERE id = @p0", watercooler ) );
    }

    [Fact]
    public async Task An_open_room_that_becomes_closed_revises_memberships_in_a_second_transaction( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        var hq = TestDatabase.Id( "hq" );
        var granted = new[] { TestDatabase.Id( "david" ), TestDatabase.Id( "jason" ), TestDatabase.Id( "bender" ) };

        // Rooms::ClosedsController#update: the update in one transaction, then the revision in a second.
        var room = await testDatabase.WriteAsync( scope => RoomOperations.Update( scope, hq, new RoomChanges { Name = "HQ2", Type = RoomType.Closed } ) );
        var (_, revoked) = await testDatabase.WriteAsync( scope => RoomMemberships.ReviseTo( scope, room, granted ) );

        Assert.Equal( "Rooms::Closed", await testDatabase.ScalarAsync<string>( "SELECT type FROM rooms WHERE id = @p0", hq ) );
        Assert.Equal( "HQ2", room.Name );
        Assert.Equal( granted.Order( ), (await MemberIdsAsync( testDatabase, hq )).Order( ) );
        Assert.Equal( new[] { TestDatabase.Id( "jz" ), TestDatabase.Id( "kevin" ) }.Order( ), revoked.Order( ) );
        Assert.Equal(
            revoked.Select( id => $"disconnect user {id} reconnect=true" ).Order( ),
            testDatabase.Hooks.Events.Order( ) );
    }

    [Fact]
    public async Task User_room_scopes_find_a_users_rooms_by_type_access_and_order( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        var david = TestDatabase.Id( "david" );

        Assert.Equal( 2, await testDatabase.ReadAsync( db => RoomQueries.ForUser( db, david ).Count( room => room.Type == RoomType.Direct ) ) );
        Assert.Equal( 4, await testDatabase.ReadAsync( db => RoomQueries.ForUser( db, david ).Count( room => room.Type != RoomType.Direct ) ) );
        Assert.Null( await testDatabase.ReadAsync( db => RoomQueries.FindByIdForUser( db, TestDatabase.Id( "kevin" ), TestDatabase.Id( "pets" ) ) ) );
        Assert.Null( await testDatabase.ReadAsync( db => RoomQueries.FindSharedByIdForUser( db, david, TestDatabase.Id( "david_and_jason" ) ) ) );
        Assert.NotNull( await testDatabase.ReadAsync( db => RoomQueries.FindDirectByIdForUser( db, david, TestDatabase.Id( "david_and_jason" ) ) ) );

        Assert.Equal(
            ["All Talk"],
            (await testDatabase.ReadAsync( db => RoomQueries.SharedForUserOrdered( db, TestDatabase.Id( "bender" ) ) )).Select( room => room.Name ) );
        Assert.Equal(
            Math.Max( TestDatabase.Id( "designers" ), TestDatabase.Id( "hq" ) ),
            (await testDatabase.ReadAsync( db => RoomQueries.LastForUser( db, TestDatabase.Id( "jz" ) ) ))?.Id );

        var names = (await testDatabase.ReadAsync( db => MembershipQueries.VisibleWithOrderedRoom( db, david ) )).Select( row => row.Room.Name ).ToList( );
        Assert.Equal( ["All Pets", "All Talk", "Designers", "HQ"], names[^4..] );
        Assert.Equal( [null, null], names[..2] );
    }

    [Fact]
    public async Task Active_bots_are_the_active_bot_members_of_a_room( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );

        var bots = await testDatabase.ReadAsync( db => RoomQueries.ActiveBots( db, TestDatabase.Id( "watercooler" ) ) );

        Assert.Equal( [TestDatabase.Id( "bender" )], bots.Select( bot => bot.Id ) );
        Assert.Equal( UserRole.Bot, bots[0].Role );
    }
}
