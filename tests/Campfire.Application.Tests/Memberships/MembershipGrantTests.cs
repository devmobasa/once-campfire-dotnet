using Campfire.Application.Memberships;
using Campfire.Application.Rooms;
using Campfire.Application.Tests.Support;
using Microsoft.Data.Sqlite;

namespace Campfire.Application.Tests.Memberships;

public sealed class MembershipGrantTests
{
    [Theory]
    [InlineData( false )]
    [InlineData( true )]
    public async Task A_grant_between_transactions_rolls_back_partial_statement_failure_then_retries( bool grantRooms )
    {
        using var test = await TestDatabase.CreateAsync( );
        var rooms = await test.WriteAsync( scope =>
        {
            var first = RoomOperations.Create( scope, RoomType.Closed, "first", TestDatabase.Id( "david" ) );
            var second = RoomOperations.Create( scope, RoomType.Closed, "second", TestDatabase.Id( "david" ) );
            return (first, second);
        } );
        var refusal = grantRooms ? $"NEW.room_id={rooms.second.Id}" : $"NEW.user_id={TestDatabase.Id( "jz" )}";
        await test.ExecuteAsync( $"CREATE TRIGGER refuse_second_grant BEFORE INSERT ON memberships WHEN {refusal} BEGIN SELECT RAISE(FAIL,'second grant unavailable'); END" );

        await test.WithWriterAsync( lease =>
        {
            Assert.Throws<SqliteException>( ( ) => Grant( ) );
            return 0;

            void Grant( )
            {
                if ( grantRooms )
                {
                    MembershipGrants.GrantRoomsTo( lease.Db, TestDatabase.Id( "david" ), [rooms.first.Id, rooms.second.Id] );
                }
                else
                {
                    MembershipGrants.GrantTo( lease.Db, rooms.first.Id, rooms.first.DefaultInvolvement, [TestDatabase.Id( "david" ), TestDatabase.Id( "jz" )] );
                }
            }
        } );

        Assert.Equal( 0, await test.CountAsync( "memberships", "room_id IN (@p0,@p1)", rooms.first.Id, rooms.second.Id ) );
        await test.ExecuteAsync( "DROP TRIGGER refuse_second_grant" );
        await test.WithWriterAsync( lease =>
        {
            if ( grantRooms )
            {
                MembershipGrants.GrantRoomsTo( lease.Db, TestDatabase.Id( "david" ), [rooms.first.Id, rooms.second.Id] );
            }
            else
            {
                MembershipGrants.GrantTo( lease.Db, rooms.first.Id, rooms.first.DefaultInvolvement, [TestDatabase.Id( "david" ), TestDatabase.Id( "jz" )] );
            }

            return 0;
        } );
        Assert.Equal( 2, await test.CountAsync( "memberships", "room_id IN (@p0,@p1)", rooms.first.Id, rooms.second.Id ) );
    }

    [Fact]
    public async Task A_grant_reuses_the_callers_transaction_and_rolls_back_with_it( )
    {
        using var test = await TestDatabase.CreateAsync( );
        var room = await test.WriteAsync( scope => RoomOperations.Create( scope, RoomType.Closed, "rollback", TestDatabase.Id( "david" ) ) );

        await Assert.ThrowsAsync<IOException>( ( ) => test.WriteAsync( scope =>
        {
            RoomMemberships.GrantTo( scope, room, [TestDatabase.Id( "jz" )] );
            throw new IOException( "caller rollback" );
        } ) );

        Assert.Equal( 0, await test.CountAsync( "memberships", "room_id=@p0", room.Id ) );
        await test.WriteAsync( scope => RoomMemberships.GrantTo( scope, room, [TestDatabase.Id( "jz" )] ) );
        Assert.Equal( 1, await test.CountAsync( "memberships", "room_id=@p0", room.Id ) );
    }
}
