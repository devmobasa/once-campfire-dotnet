using Campfire.Application.Data;
using Campfire.Application.Storage;
using Campfire.Application.Tests.Storage.Support;
using Campfire.Application.Tests.Support;
using static Campfire.Application.Tests.Storage.Support.StorageVectors;

namespace Campfire.Application.Tests.Storage;

public sealed class BlobPurgeTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Forced_process_termination_replays_unacknowledged_file_cleanup( )
    {
        if ( CleanupCrashProcess.IsChild )
        {
            await CleanupCrashProcess.RunChildAsync( Cancel );
            return;
        }

        await CleanupCrashProcess.AssertRecoveryAsync(
            $"{typeof( BlobPurgeTests ).FullName}.{nameof( Forced_process_termination_replays_unacknowledged_file_cleanup )}", Cancel );
    }

    [Fact]
    public async Task Failed_file_deletion_recovers_after_restart_including_legacy_variants( )
    {
        using var test = await TestDatabase.CreateAsync( fixtures: false );
        using var files = new StorageHarness( );
        using var storage = new BlobStorage( files.Options, Verifier, new BlobRecords( test.Database ), test.Clock );
        using var content = File.OpenRead( Fixture( "moon.jpg" ) );
        var blob = await storage.CreateAndUploadAsync( content, new BlobFilename( "moon.jpg" ), "image/jpeg", Cancel );
        var path = storage.Disk.PathFor( blob.Key );
        File.Delete( path );
        Directory.CreateDirectory( path );

        var failure = await Record.ExceptionAsync( ( ) => BlobOperations.PurgeAsync( test.Database, storage, blob.Id, Cancel ) );
        Assert.True( failure is IOException or UnauthorizedAccessException );
        Assert.Equal( 0, await test.CountAsync( "active_storage_blobs" ) );
        Assert.Equal( 1, await test.CountAsync( "campfire_cleanup", "kind=2" ) );
        Directory.Delete( path );
        await File.WriteAllTextAsync( path, "remaining file", Cancel );
        var legacy = Path.Combine( storage.Disk.Root, "va", "ri", "variants", blob.Key );
        Directory.CreateDirectory( legacy );
        await File.WriteAllTextAsync( Path.Combine( legacy, "old" ), "variant", Cancel );
        test.Database.Dispose( );

        using var reopened = CampfireDatabase.Open( new DatabaseOptions { StorageRoot = test.Directory, Environment = "test" }, test.Clock, test.Hooks.ToHooks( test.BodyText ) );
        using var resumed = new BlobStorage( files.Options, Verifier, new BlobRecords( reopened ), test.Clock );
        Assert.Equal( 1, await CleanupOutbox.DrainAsync( reopened, resumed, Cancel ) );
        Assert.False( File.Exists( path ) );
        Assert.Empty( Directory.EnumerateFileSystemEntries( legacy ) );
        Assert.Equal( 0L, await reopened.ReadAsync( db => Sql.Scalar<long>( db, "SELECT COUNT(*) FROM campfire_cleanup" ), Cancel ) );
        Assert.Equal( 0, await CleanupOutbox.DrainAsync( reopened, resumed, Cancel ) );
    }

    [Fact]
    public async Task Cancellation_before_acknowledgement_replays_an_already_deleted_file( )
    {
        using var test = await TestDatabase.CreateAsync( fixtures: false );
        using var files = new StorageHarness( );
        using var storage = new BlobStorage( files.Options, Verifier, new BlobRecords( test.Database ), test.Clock );
        var blob = await UploadAsync( storage );
        await test.WriteAsync( scope =>
        {
            CleanupOutbox.QueueFile( scope, blob );
            Sql.Execute( scope.Db, "DELETE FROM active_storage_blobs WHERE id=@p0", blob.Id );
        } );
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource( Cancel );
        using ( var writer = await test.Database.AcquireWriterAsync( Cancel ) )
        {
            var pending = CleanupOutbox.DrainAsync( test.Database, storage, stopping.Token );
            Assert.False( pending.IsCompleted );
            Assert.False( storage.Disk.Exists( blob.Key ) );
            await stopping.CancelAsync( );
            await Assert.ThrowsAnyAsync<OperationCanceledException>( ( ) => pending );
        }

        Assert.Equal( 1, await test.CountAsync( "campfire_cleanup" ) );
        Assert.Equal( 1, await CleanupOutbox.DrainAsync( test.Database, storage, Cancel ) );
        Assert.Equal( 0, await test.CountAsync( "campfire_cleanup" ) );
    }

    [Fact]
    public async Task Blob_deletion_rollback_keeps_its_file_and_removes_cleanup_work( )
    {
        using var test = await TestDatabase.CreateAsync( fixtures: false );
        using var files = new StorageHarness( );
        using var storage = new BlobStorage( files.Options, Verifier, new BlobRecords( test.Database ), test.Clock );
        var blob = await UploadAsync( storage );
        await test.ExecuteAsync( "CREATE TRIGGER refuse_blob_delete BEFORE DELETE ON active_storage_blobs BEGIN SELECT RAISE(ABORT,'blocked'); END" );

        await Assert.ThrowsAnyAsync<Exception>( ( ) => BlobOperations.PurgeAsync( test.Database, storage, blob.Id, Cancel ) );

        Assert.Equal( 1, await test.CountAsync( "active_storage_blobs" ) );
        Assert.Equal( 0, await test.CountAsync( "campfire_cleanup" ) );
        Assert.True( storage.Disk.Exists( blob.Key ) );
    }

    [Fact]
    public async Task Pending_file_keys_remain_reserved_until_cleanup_acknowledges_them( )
    {
        using var test = await TestDatabase.CreateAsync( fixtures: false );
        using var files = new StorageHarness( );
        using var storage = new BlobStorage( files.Options, Verifier, new BlobRecords( test.Database ), test.Clock );
        var blob = await UploadAsync( storage );
        await test.WriteAsync( scope =>
        {
            CleanupOutbox.QueueFile( scope, blob );
            Sql.Execute( scope.Db, "DELETE FROM active_storage_blobs WHERE id=@p0", blob.Id );
        } );
        var replacement = NewBlob.Unfurl( blob.Key, "text"u8, 4, BlobKeys.Checksum( "text"u8 ), new BlobFilename( "x.txt" ), "text/plain", "local" );
        var records = new BlobRecords( test.Database );
        await Assert.ThrowsAsync<InvalidOperationException>( ( ) => records.InsertBlobAsync( replacement, Now, Cancel ) );
        await CleanupOutbox.DrainAsync( test.Database, storage, Cancel );
        var inserted = await records.InsertBlobAsync( replacement, Now, Cancel );
        Assert.Equal( blob.Key, inserted.Key );
    }

    private static async Task<Blob> UploadAsync( BlobStorage storage )
    {
        using var stream = new MemoryStream( "text"u8.ToArray( ) );
        return await storage.CreateAndUploadAsync( stream, new BlobFilename( "x.txt" ), "text/plain", Cancel );
    }

    [Fact]
    public async Task Cleanup_pages_stay_bounded_and_failed_keys_do_not_block_other_keys( )
    {
        using var test = await TestDatabase.CreateAsync( fixtures: false );
        using var files = new StorageHarness( );
        using var storage = new BlobStorage( files.Options, Verifier, new BlobRecords( test.Database ), test.Clock );
        var template = await UploadAsync( storage );
        var obstruction = storage.Disk.PathFor( "blockedkey" );
        Directory.CreateDirectory( obstruction );
        await test.WriteAsync( scope =>
        {
            CleanupOutbox.QueueFile( scope, template with { Key = "blockedkey" } );
            for ( var index = 0; index < 130; index++ )
            {
                CleanupOutbox.QueueFile( scope, template with { Key = $"queued{index}" } );
            }
        } );
        Assert.Equal( 63, await CleanupOutbox.DrainAsync( test.Database, storage, Cancel ) );
        Assert.Equal( 68, await test.CountAsync( "campfire_cleanup" ) );
        Assert.Equal( 64, await CleanupOutbox.DrainAsync( test.Database, storage, Cancel ) );
        Assert.Equal( 3, await CleanupOutbox.DrainAsync( test.Database, storage, Cancel ) );
        Assert.Equal( 1, await test.CountAsync( "campfire_cleanup" ) );
        Directory.Delete( obstruction );
        test.Clock.Travel( TimeSpan.FromMinutes( 1 ) );
        Assert.Equal( 1, await CleanupOutbox.DrainAsync( test.Database, storage, Cancel ) );
        Assert.True( storage.Disk.Exists( template.Key ) );
    }
}
