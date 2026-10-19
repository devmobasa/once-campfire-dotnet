using Campfire.Application.Data;
using Campfire.Application.Memberships;
using Campfire.Application.Rooms;
using Campfire.Application.Storage;
using Campfire.Application.Tests.Storage.Support;
using Campfire.Application.Tests.Support;

namespace Campfire.Application.Tests.Rooms;

public sealed class RoomDeletionTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Logical_removal_rollback_preserves_access_and_leaves_no_deletion_intent( )
    {
        using var test = await TestDatabase.CreateAsync( );
        var roomId = TestDatabase.Id( "watercooler" );
        var members = await test.CountAsync( "memberships", "room_id=@p0", roomId );
        var original = await test.ReadAsync( db => RoomQueries.Find( db, roomId ) );
        await Assert.ThrowsAsync<InvalidOperationException>( ( ) => test.WriteAsync( scope =>
        {
            RoomOperations.Destroy( scope, roomId );
            throw new InvalidOperationException( "rollback" );
        } ) );
        Assert.Equal( members, await test.CountAsync( "memberships", "room_id=@p0", roomId ) );
        Assert.Equal( original.Type, (await test.ReadAsync( db => RoomQueries.Find( db, roomId ) )).Type );
        Assert.Equal( 0, await test.CountAsync( "campfire_room_deletions" ) );
        Assert.Equal( 0, await test.CountAsync( "campfire_room_deletion_members" ) );
    }

    [Theory]
    [InlineData( "update" )]
    [InlineData( "grant" )]
    [InlineData( "revise" )]
    [InlineData( "open_grant" )]
    public async Task Stale_room_mutations_are_rejected_after_logical_removal( string mutation )
    {
        using var test = await TestDatabase.CreateAsync( );
        var roomId = TestDatabase.Id( "watercooler" );
        var stale = await test.ReadAsync( db => RoomQueries.Find( db, roomId ) );
        await test.WriteAsync( scope => RoomOperations.Destroy( scope, roomId ) );
        test.Hooks.Clear( );

        await Assert.ThrowsAsync<RecordNotFoundException>( ( ) => test.WriteAsync( scope =>
        {
            switch ( mutation )
            {
                case "update":
                    RoomOperations.Update( scope, roomId, new RoomChanges { Type = RoomType.Open, Name = "reopened" } );
                    break;
                case "grant":
                    RoomMemberships.GrantTo( scope, stale, [TestDatabase.Id( "jz" )] );
                    break;
                case "revise":
                    RoomMemberships.ReviseTo( scope, stale, [TestDatabase.Id( "jz" )] );
                    break;
                case "open_grant":
                    MembershipGrants.GrantRoomsTo( scope.Db, TestDatabase.Id( "jz" ), [roomId] );
                    break;
            }
        } ) );

        var deleting = await test.ReadAsync( db => RoomQueries.Find( db, roomId ) );
        Assert.Equal( RoomType.Closed, deleting.Type );
        Assert.Equal( stale.Name, deleting.Name );
        Assert.Equal( 0, await test.CountAsync( "memberships", "room_id=@p0", roomId ) );
        Assert.Equal( 1, await test.CountAsync( "campfire_room_deletions", "room_id=@p0", roomId ) );
        Assert.Empty( test.Hooks.Events );
    }

    [Theory]
    [InlineData( "grant" )]
    [InlineData( "revise" )]
    [InlineData( "open_grant" )]
    public async Task Stale_grants_are_rejected_after_physical_deletion_also( string mutation )
    {
        using var test = await TestDatabase.CreateAsync( );
        using var files = new StorageHarness( );
        var room = await test.WriteAsync( scope => RoomOperations.Create( scope, RoomType.Closed, "gone", TestDatabase.Id( "david" ) ) );
        await test.WriteAsync( scope => RoomOperations.Destroy( scope, room.Id ) );
        await FinishAsync( test.Database, files.Storage );

        await Assert.ThrowsAsync<RecordNotFoundException>( ( ) => test.WriteAsync( scope =>
        {
            switch ( mutation )
            {
                case "grant":
                    RoomMemberships.GrantTo( scope, room, [TestDatabase.Id( "jz" )] );
                    break;
                case "revise":
                    RoomMemberships.ReviseTo( scope, room, [TestDatabase.Id( "jz" )] );
                    break;
                case "open_grant":
                    MembershipGrants.GrantRoomsTo( scope.Db, TestDatabase.Id( "jz" ), [room.Id] );
                    break;
            }
        } ) );

        Assert.Equal( 0, await test.CountAsync( "memberships", "room_id=@p0", room.Id ) );
        Assert.Equal( 0, await test.CountAsync( "campfire_room_deletions", "room_id=@p0", room.Id ) );
    }

    [Fact]
    public async Task Final_destruction_deletes_late_memberships_as_Rails_dependent_delete_does( )
    {
        using var test = await TestDatabase.CreateAsync( );
        using var files = new StorageHarness( );
        var room = await test.WriteAsync( scope => RoomOperations.Create( scope, RoomType.Closed, "late membership", TestDatabase.Id( "david" ) ) );
        await test.WriteAsync( scope => RoomOperations.Destroy( scope, room.Id ) );
        await test.ExecuteAsync( "INSERT INTO memberships (room_id,user_id,created_at,updated_at) VALUES (@p0,@p1,@p2,@p2)", room.Id, TestDatabase.Id( "jz" ), test.Clock.Now );

        await FinishAsync( test.Database, files.Storage );

        Assert.Equal( 0, await test.CountAsync( "rooms", "id=@p0", room.Id ) );
        Assert.Equal( 0, await test.CountAsync( "memberships", "room_id=@p0", room.Id ) );
        Assert.Equal( 0, await test.CountAsync( "campfire_room_deletions", "room_id=@p0", room.Id ) );
    }

    [Theory]
    [InlineData( 0 )]
    [InlineData( 1 )]
    [InlineData( 2 )]
    [InlineData( 3 )]
    [InlineData( 4 )]
    public async Task Restart_resumes_each_phase_and_preserves_legitimate_empty_closed_rooms( int steps )
    {
        using var test = await TestDatabase.CreateAsync( );
        using var files = new StorageHarness( );
        var roomId = TestDatabase.Id( "watercooler" );
        var empty = await test.WriteAsync( scope => RoomOperations.Create( scope, RoomType.Closed, "empty", TestDatabase.Id( "david" ) ) );
        await test.WriteAsync( scope => RoomOperations.Destroy( scope, roomId ) );
        Assert.Equal( 0, await test.CountAsync( "memberships", "room_id=@p0", roomId ) );
        Assert.True( await test.CountAsync( "messages", "room_id=@p0", roomId ) > 0 );
        for ( var index = 0; index < steps; index++ )
        {
            await RoomDeletions.DrainAsync( test.Database, _ => { }, Cancel );
        }

        test.Database.Dispose( );
        using var reopened = CampfireDatabase.Open( new DatabaseOptions { StorageRoot = test.Directory, Environment = "test" },
            test.Clock, test.Hooks.ToHooks( test.BodyText ) );
        await FinishAsync( reopened, files.Storage );

        Assert.Null( await reopened.ReadAsync( db => RoomQueries.FindById( db, roomId ), Cancel ) );
        Assert.NotNull( await reopened.ReadAsync( db => RoomQueries.FindById( db, empty.Id ), Cancel ) );
        Assert.Equal( 0L, await reopened.ReadAsync( db => Sql.Scalar<long>( db, "SELECT COUNT(*) FROM campfire_room_deletions" ), Cancel ) );
        Assert.Equal( 0L, await reopened.ReadAsync( db => Sql.Scalar<long>( db, "SELECT COUNT(*) FROM campfire_room_deletion_members" ), Cancel ) );
    }

    [Fact]
    public async Task Broadcast_failure_remains_pending_and_replays_before_resets( )
    {
        using var test = await TestDatabase.CreateAsync( );
        var roomId = TestDatabase.Id( "watercooler" );
        await test.WriteAsync( scope => RoomOperations.Destroy( scope, roomId ) );
        await Assert.ThrowsAsync<InvalidOperationException>( ( ) => RoomDeletions.BroadcastAsync( test.Database, roomId,
            _ => throw new InvalidOperationException( "broadcast failed" ), Cancel ) );
        Assert.Equal( 0L, await test.ScalarAsync<long>( "SELECT phase FROM campfire_room_deletions WHERE room_id=@p0", roomId ) );
        test.Hooks.Clear( );
        var broadcasts = 0;
        await RoomDeletions.DrainAsync( test.Database, _ => broadcasts++, Cancel );
        Assert.Equal( 1, broadcasts );
        Assert.Empty( test.Hooks.Events );
        Assert.Equal( 1L, await test.ScalarAsync<long>( "SELECT phase FROM campfire_room_deletions WHERE room_id=@p0", roomId ) );
    }

    [Fact]
    public async Task A_failed_room_does_not_abort_the_rest_of_its_page( )
    {
        using var test = await TestDatabase.CreateAsync( );
        var rooms = await test.WriteAsync( scope =>
        {
            var first = RoomOperations.Create( scope, RoomType.Closed, "failed", TestDatabase.Id( "david" ) );
            var second = RoomOperations.Create( scope, RoomType.Closed, "healthy", TestDatabase.Id( "david" ) );
            RoomOperations.Destroy( scope, first.Id );
            RoomOperations.Destroy( scope, second.Id );
            return (first.Id, second.Id);
        } );

        Assert.Equal( 1, await RoomDeletions.DrainAsync( test.Database, room =>
        {
            if ( room.Id == rooms.Item1 )
            {
                throw new IOException( "broadcast unavailable" );
            }
        }, Cancel ) );

        Assert.Equal( 1L, await test.ScalarAsync<long>( "SELECT attempts FROM campfire_room_deletions WHERE room_id=@p0", rooms.Item1 ) );
        Assert.Equal( 0L, await test.ScalarAsync<long>( "SELECT phase FROM campfire_room_deletions WHERE room_id=@p0", rooms.Item1 ) );
        Assert.Equal( 1L, await test.ScalarAsync<long>( "SELECT phase FROM campfire_room_deletions WHERE room_id=@p0", rooms.Item2 ) );
    }

    [Fact]
    public async Task Cancellation_after_disconnect_replays_the_unacknowledged_recipient( )
    {
        using var test = await TestDatabase.CreateAsync( );
        var roomId = TestDatabase.Id( "watercooler" );
        await test.WriteAsync( scope => RoomOperations.Destroy( scope, roomId ) );
        await RoomDeletions.DrainAsync( test.Database, _ => { }, Cancel );
        var before = await test.CountAsync( "campfire_room_deletion_members" );
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource( Cancel );
        test.Hooks.Clear( );
        test.Hooks.Intercept = _ => stopping.Cancel( );
        await Assert.ThrowsAnyAsync<OperationCanceledException>( ( ) => RoomDeletions.DrainAsync( test.Database, _ => { }, stopping.Token ) );
        Assert.Equal( before, await test.CountAsync( "campfire_room_deletion_members" ) );
        var interrupted = Assert.Single( test.Hooks.Events );
        test.Hooks.Intercept = null;
        await RoomDeletions.DrainAsync( test.Database, _ => { }, Cancel );
        Assert.Equal( 2, test.Hooks.Events.Count( value => value == interrupted ) );
    }

    [Fact]
    public async Task Fts_failure_keeps_committed_deletion_and_blocks_purge_and_the_next_message( )
    {
        using var test = await TestDatabase.CreateAsync( );
        using var files = new StorageHarness( );
        var roomId = TestDatabase.Id( "watercooler" );
        var first = await test.ScalarAsync<long>( "SELECT MIN(id) FROM messages WHERE room_id=@p0", roomId );
        await test.ExecuteAsync( "INSERT INTO active_storage_blobs (byte_size,created_at,filename,key,service_name) VALUES (1,@p0,'x','pendingfile','local')", test.Clock.Now );
        var blobId = await test.ScalarAsync<long>( "SELECT MAX(id) FROM active_storage_blobs" );
        await test.ExecuteAsync( "INSERT INTO active_storage_attachments (blob_id,created_at,name,record_id,record_type) VALUES (@p0,@p1,'attachment',@p2,'Message')", blobId, test.Clock.Now, first );
        await test.WriteAsync( scope => RoomOperations.Destroy( scope, roomId ) );
        for ( var phase = 0; phase < 3; phase++ )
        {
            await RoomDeletions.DrainAsync( test.Database, _ => { }, Cancel );
        }

        var before = await test.CountAsync( "messages", "room_id=@p0", roomId );
        await test.ExecuteAsync( "DROP TABLE message_search_index" );
        test.Hooks.Clear( );
        var roomFailures = new List<(long Id, Exception Error)>( );
        Assert.Equal( 0, await RoomDeletions.DrainAsync( test.Database, _ => { }, Cancel,
            failed: ( id, error ) => roomFailures.Add( (id, error) ) ) );
        var roomFailure = Assert.Single( roomFailures );
        Assert.Equal( roomId, roomFailure.Id );
        Assert.IsType<AfterCommitFailedException>( roomFailure.Error );
        Assert.Equal( before - 1, await test.CountAsync( "messages", "room_id=@p0", roomId ) );
        Assert.Equal( 1, await test.CountAsync( "campfire_cleanup", "kind=0 AND record_id=@p0", first ) );
        Assert.DoesNotContain( test.Hooks.Events, value => value.StartsWith( "purge blob ", StringComparison.Ordinal ) );
        var storageFailures = new List<long>( );
        var receiptId = await test.ScalarAsync<long>( "SELECT id FROM campfire_cleanup WHERE kind=0 AND record_id=@p0", first );
        Assert.Equal( 0, await CleanupOutbox.DrainAsync( test.Database, files.Storage, Cancel,
            ( id, _ ) => storageFailures.Add( id ) ) );
        Assert.Equal( [receiptId], storageFailures );
        test.Clock.Travel( TimeSpan.FromMinutes( 1 ) );
        Assert.Equal( 0, await RoomDeletions.DrainAsync( test.Database, _ => { }, Cancel ) );
        Assert.Equal( 1, await test.CountAsync( "active_storage_blobs", "id=@p0", blobId ) );

        await test.ExecuteAsync( "CREATE VIRTUAL TABLE message_search_index USING fts5(body,tokenize=porter)" );
        await FinishAsync( test.Database, files.Storage );
        Assert.Equal( 0, await test.CountAsync( "rooms", "id=@p0", roomId ) );
        Assert.Equal( 0, await test.CountAsync( "active_storage_blobs", "id=@p0", blobId ) );
        Assert.Equal( 0, await test.CountAsync( "campfire_cleanup" ) );
    }

    [Fact]
    public async Task Large_rooms_reset_one_page_and_destroy_one_message_per_transaction( )
    {
        using var test = await TestDatabase.CreateAsync( );
        var roomId = TestDatabase.Id( "watercooler" );
        await test.ExecuteAsync( "WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<1000) INSERT INTO messages (client_message_id,created_at,creator_id,room_id,updated_at) SELECT 'bulk-'||x,@p0,@p1,@p2,@p0 FROM n", test.Clock.Now, TestDatabase.Id( "david" ), roomId );
        await test.ExecuteAsync( "WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<129) INSERT INTO users (id,name,created_at,updated_at) SELECT 100000+x,'member-'||x,@p0,@p0 FROM n", test.Clock.Now );
        await test.ExecuteAsync( "WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<129) INSERT INTO memberships (room_id,user_id,created_at,updated_at) SELECT @p0,100000+x,@p1,@p1 FROM n", roomId, test.Clock.Now );
        await test.WriteAsync( scope => RoomOperations.Destroy( scope, roomId ) );
        await RoomDeletions.DrainAsync( test.Database, _ => { }, Cancel );
        var members = await test.CountAsync( "campfire_room_deletion_members" );
        await RoomDeletions.DrainAsync( test.Database, _ => { }, Cancel );
        Assert.Equal( members - 64, await test.CountAsync( "campfire_room_deletion_members" ) );
        while ( await test.ScalarAsync<long>( "SELECT phase FROM campfire_room_deletions WHERE room_id=@p0", roomId ) == 1 )
        {
            await RoomDeletions.DrainAsync( test.Database, _ => { }, Cancel );
        }

        var before = await test.CountAsync( "messages", "room_id=@p0", roomId );
        await RoomDeletions.DrainAsync( test.Database, _ => { }, Cancel );
        Assert.Equal( before - 1, await test.CountAsync( "messages", "room_id=@p0", roomId ) );
        await test.ExecuteAsync( "UPDATE users SET bio='writer remains available' WHERE id=@p0", TestDatabase.Id( "david" ) );
    }

    [Fact]
    public async Task A_deletion_cursor_wraps_past_blocked_pages_even_after_restart( )
    {
        using var test = await TestDatabase.CreateAsync( );
        test.Clock.TravelTo( new DateTime( 2026, 10, 18, 12, 0, 0, DateTimeKind.Utc ) );
        var later = await test.WriteAsync( scope =>
        {
            for ( var index = 0; index < 17; index++ )
            {
                var room = RoomOperations.Create( scope, RoomType.Closed, $"blocked-{index}", TestDatabase.Id( "david" ) );
                RoomOperations.Destroy( scope, room.Id );
                CleanupOutbox.QueueIndex( scope, 100000 + index, room.Id );
            }

            Sql.Execute( scope.Db, "UPDATE campfire_room_deletions SET phase=2" );
            var ready = RoomOperations.Create( scope, RoomType.Closed, "ready", TestDatabase.Id( "david" ) );
            RoomOperations.Destroy( scope, ready.Id );
            return ready;
        } );
        var cursor = new RoomDeletionCursor( );
        Assert.Equal( 0, await RoomDeletions.DrainAsync( test.Database, _ => { }, Cancel, cursor ) );
        cursor = new RoomDeletionCursor( );

        for ( var pass = 0; pass < 8; pass++ )
        {
            await RoomDeletions.DrainAsync( test.Database, _ => { }, Cancel, cursor );
        }

        Assert.Null( await test.ReadAsync( db => RoomQueries.FindById( db, later.Id ) ) );
        Assert.Equal( 17, await test.CountAsync( "campfire_room_deletions" ) );
        Assert.Equal( 17, await test.CountAsync( "campfire_cleanup" ) );
    }

    private static async Task FinishAsync( CampfireDatabase database, BlobStorage storage )
    {
        for ( var attempt = 0; attempt < 100; attempt++ )
        {
            var progress = await CleanupOutbox.DrainAsync( database, storage, Cancel );
            progress += await RoomDeletions.DrainAsync( database, _ => { }, Cancel );
            if ( progress == 0 )
            {
                return;
            }
        }

        Assert.Fail( "Cleanup did not finish within the fixture's bounded work count." );
    }
}
