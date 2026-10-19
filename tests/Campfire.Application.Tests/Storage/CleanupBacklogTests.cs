using Campfire.Application.Rooms;
using Campfire.Application.Storage;
using Campfire.Application.Tests.Storage.Support;
using Campfire.Application.Tests.Support;

namespace Campfire.Application.Tests.Storage;

public sealed class CleanupBacklogTests
{
    [Fact]
    public async Task Enqueue_times_survive_retries_and_restart_in_both_durable_queues( )
    {
        using var test = await TestDatabase.CreateAsync( );
        using var files = new StorageHarness( );
        test.Clock.TravelTo( new DateTime( 2026, 10, 18, 12, 0, 0, DateTimeKind.Utc ) );
        var createdAt = test.Clock.Now;
        var room = await test.WriteAsync( scope =>
        {
            CleanupOutbox.QueueFile( scope, new Blob { Id = 123, Key = "timestampfailure", Filename = new BlobFilename( "x" ), ServiceName = "local" } );
            var pending = RoomOperations.Create( scope, RoomType.Closed, "pending", TestDatabase.Id( "david" ) );
            RoomOperations.Destroy( scope, pending.Id );
            return pending;
        } );
        Assert.Equal( createdAt, await test.ScalarAsync<DateTime>( "SELECT created_at FROM campfire_cleanup" ) );
        Assert.Equal( createdAt, await test.ScalarAsync<DateTime>( "SELECT created_at FROM campfire_room_deletions WHERE room_id=@p0", room.Id ) );
        Directory.CreateDirectory( files.Storage.Disk.PathFor( "timestampfailure" ) );
        test.Clock.Travel( TimeSpan.FromMinutes( 2 ) );

        await CleanupOutbox.DrainAsync( test.Database, files.Storage, TestContext.Current.CancellationToken );
        await RoomDeletions.DrainAsync( test.Database, _ => throw new IOException( "unavailable" ), TestContext.Current.CancellationToken );
        test.Database.Dispose( );
        using var reopened = Campfire.Application.Data.CampfireDatabase.Open( new Campfire.Application.Data.DatabaseOptions
        { StorageRoot = test.Directory, Environment = "test" }, test.Clock, test.Hooks.ToHooks( test.BodyText ) );

        Assert.Equal( createdAt, await reopened.ReadAsync( db => Sql.Scalar<DateTime>( db, "SELECT created_at FROM campfire_cleanup" ), TestContext.Current.CancellationToken ) );
        Assert.Equal( createdAt, await reopened.ReadAsync( db => Sql.Scalar<DateTime>( db, "SELECT created_at FROM campfire_room_deletions WHERE room_id=@p0", room.Id ), TestContext.Current.CancellationToken ) );
        var backlog = await CleanupOutbox.BacklogAsync( reopened, TestContext.Current.CancellationToken );
        Assert.Equal( ["storage", "rooms"], backlog.Select( queue => queue.Queue ) );
        Assert.All( backlog, queue =>
        {
            Assert.Equal( 1, queue.PendingCount );
            Assert.Equal( createdAt, queue.OldestCreatedAt );
            Assert.Null( queue.OldestDueAt );
        } );
        test.Clock.Travel( TimeSpan.FromSeconds( 31 ) );
        backlog = await CleanupOutbox.BacklogAsync( reopened, TestContext.Current.CancellationToken );
        Assert.All( backlog, queue =>
        {
            Assert.Equal( createdAt, queue.OldestCreatedAt );
            Assert.Equal( createdAt.AddMinutes( 2 ).AddSeconds( 30 ), queue.OldestDueAt );
        } );
    }
}
