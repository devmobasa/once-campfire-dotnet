using Campfire.Application.Storage;
using Campfire.Application.Tests.Storage.Support;
using Campfire.Application.Tests.Support;
using static Campfire.Application.Tests.Storage.Support.StorageVectors;

namespace Campfire.Application.Tests.Storage;

public sealed class BlobPurgeRetryRegressionTests
{
    [Fact]
    public async Task Retrying_a_failed_purge_recovers_the_deleted_blobs_file_key( )
    {
        var cancel = TestContext.Current.CancellationToken;
        using var test = await TestDatabase.CreateAsync( fixtures: false );
        using var files = new StorageHarness( );
        using var storage = new BlobStorage( files.Options, Verifier, new BlobRecords( test.Database ), test.Clock );
        using var content = new MemoryStream( "text"u8.ToArray( ) );
        var blob = await storage.CreateAndUploadAsync( content, new BlobFilename( "x.txt" ), "text/plain", cancel );
        var path = storage.Disk.PathFor( blob.Key );
        File.Delete( path );
        Directory.CreateDirectory( path );
        var failure = await Record.ExceptionAsync( ( ) => BlobOperations.PurgeAsync( test.Database, storage, blob.Id, cancel ) );
        Assert.True( failure is IOException or UnauthorizedAccessException );
        Directory.Delete( path );
        await File.WriteAllTextAsync( path, "remaining file", cancel );

        Assert.True( await BlobOperations.PurgeAsync( test.Database, storage, blob.Id, cancel ) );
        Assert.False( File.Exists( path ) );
    }
}
