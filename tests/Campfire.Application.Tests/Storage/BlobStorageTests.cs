using System.Diagnostics;
using Campfire.Application.Storage;
using Campfire.Application.Tests.Storage.Support;
using Campfire.Application.Tests.Support;
using static Campfire.Application.Tests.Storage.Support.StorageVectors;

namespace Campfire.Application.Tests.Storage;

/// <summary>
/// The file-and-row protocol around uploads, variants and previews: races, rollbacks, integrity,
/// and the bounds Rails doesn't have. These need a working libvips and ffmpeg, but not the pinned
/// builds: nothing here compares encoded bytes.
/// </summary>
public sealed class BlobStorageTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_variant_is_published_then_recorded_with_its_image_attachment( )
    {
        using var harness = new StorageHarness( );
        var blob = await harness.UploadFixtureAsync( "moon.jpg", "image/jpeg" );

        var image = await harness.Storage.ProcessVariantAsync( blob, BlobStorage.VariationFor( blob, Variation.ResizeToLimit( 100, 100 ) ), Cancel );

        Assert.Equal( ("moon.jpg", "image/jpeg", "local"), (image.Filename.Raw, image.ContentType, image.ServiceName) );
        Assert.Equal( """{"identified":true,"width":100,"height":100,"analyzed":true}""", image.Metadata.ToJson( ) );
        Assert.True( harness.Storage.Disk.Exists( image.Key ) );
        Assert.Equal( image.Checksum, await BlobKeys.ChecksumFileAsync( harness.Storage.Disk.PathFor( image.Key ), Cancel ) );
        var record = Assert.Single( harness.Records.VariantRecords );
        Assert.Equal( (blob.Id, BlobStorage.VariationFor( blob, Variation.ResizeToLimit( 100, 100 ) ).Digest( )), (record.BlobId, record.VariationDigest) );
        Assert.Contains(
            harness.Records.Attachments,
            row => row.Name == "image" && row.Owner == AttachmentOwner.ForVariantRecord( record.Id ) && row.BlobId == image.Id );
        Assert.Empty( harness.TempFiles );
    }

    [Fact]
    public async Task Losing_the_variant_race_returns_the_winner_and_deletes_the_losers_file( )
    {
        using var harness = new StorageHarness( );
        using var database = await TestDatabase.CreateAsync( fixtures: false );
        var records = new PausedVariantRecords( new BlobRecords( database.Database ) );
        using var storage = new BlobStorage( harness.Options, Verifier, records, TimeProvider.System );
        using var competitor = new BlobStorage( harness.Options, Verifier, records, TimeProvider.System );
        using var content = File.OpenRead( Fixture( "moon.jpg" ) );
        var blob = await storage.CreateAndUploadAsync( content, new BlobFilename( "moon.jpg" ), "image/jpeg", Cancel );
        var variation = BlobStorage.VariationFor( blob, Variation.ResizeToLimit( 64, 64 ) );
        var pending = storage.ProcessVariantAsync( blob, variation, Cancel );
        await records.RecordingStarted.Task.WaitAsync( Cancel );
        Blob winner;
        try
        {
            winner = await competitor.ProcessVariantAsync( blob, variation, Cancel );
        }
        finally
        {
            records.ResumeRecording.TrySetResult( );
        }

        var image = await pending;

        Assert.Equal( winner.Id, image.Id );
        Assert.Equal( 2, records.RecordingAttempts );
        Assert.Equal( 1, await database.CountAsync( "active_storage_variant_records" ) );
        Assert.Equal( 2, await database.CountAsync( "active_storage_blobs" ) );
        Assert.Equal( new[] { Relative( blob ), Relative( winner ) }.Order( StringComparer.Ordinal ), harness.Files );
    }

    [Fact]
    public async Task Concurrent_requests_for_one_variant_record_it_once( )
    {
        using var harness = new StorageHarness( );
        var blob = await harness.UploadFixtureAsync( "moon.jpg", "image/jpeg" );
        var variation = BlobStorage.VariationFor( blob, Variation.ResizeToLimit( 32, 32 ) );

        var processing = Enumerable.Range( 0, 4 ).Select( _ => Task.Run( ( ) => harness.Storage.ProcessVariantAsync( blob, variation, Cancel ), Cancel ) );
        var images = await Task.WhenAll( processing );

        Assert.Single( images.Select( image => image.Id ).Distinct( ) );
        Assert.Single( harness.Records.VariantRecords );
        Assert.Equal( 2, harness.Files.Count );
    }

    [Fact]
    public async Task Concurrent_misses_for_one_variant_perform_one_conversion( )
    {
        using var harness = new StorageHarness( );
        var blob = await harness.UploadFixtureAsync( "moon.jpg", "image/jpeg" );
        var records = new PausedVariantRecords( harness.Records );
        using var storage = new BlobStorage( harness.Options, Verifier, records, TimeProvider.System );
        var variation = BlobStorage.VariationFor( blob, Variation.ResizeToLimit( 32, 32 ) );
        var first = storage.ProcessVariantAsync( blob, variation, Cancel );
        await records.RecordingStarted.Task.WaitAsync( Cancel );
        var second = storage.ProcessVariantAsync( blob, variation, Cancel );
        try
        {
            await records.ConcurrentMiss.Task.WaitAsync( Cancel );
        }
        finally
        {
            records.ResumeRecording.TrySetResult( );
        }

        var images = await Task.WhenAll( first, second );

        Assert.Equal( images[0].Id, images[1].Id );
        Assert.Equal( 1, records.RecordingAttempts );
        Assert.Single( harness.Records.VariantRecords );
        Assert.Equal( 2, harness.Files.Count );
        Assert.Empty( harness.TempFiles );
    }

    [Theory]
    [InlineData( false )]
    [InlineData( true )]
    public async Task A_failed_or_cancelled_variant_producer_releases_its_key_for_retry( bool cancel )
    {
        using var harness = new StorageHarness( );
        var blob = await harness.UploadFixtureAsync( "moon.jpg", "image/jpeg" );
        var records = new PausedVariantRecords( harness.Records );
        using var storage = new BlobStorage( harness.Options, Verifier, records, TimeProvider.System );
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource( Cancel );
        var variation = BlobStorage.VariationFor( blob, Variation.ResizeToLimit( 32, 32 ) );
        var first = storage.ProcessVariantAsync( blob, variation, stopping.Token );
        await records.RecordingStarted.Task.WaitAsync( Cancel );
        if ( cancel )
        {
            await stopping.CancelAsync( );
        }
        else
        {
            records.FirstFailure = new InvalidOperationException( "recording failed" );
        }

        records.ResumeRecording.TrySetResult( );
        if ( cancel )
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>( ( ) => first );
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>( ( ) => first );
        }

        var image = await storage.ProcessVariantAsync( blob, variation, Cancel );

        Assert.Equal( 2, records.RecordingAttempts );
        Assert.True( harness.Storage.Disk.Exists( image.Key ) );
        Assert.Equal( 2, harness.Files.Count );
        Assert.Empty( harness.TempFiles );
    }

    private sealed class PausedVariantRecords( IBlobRecords inner ) : IBlobRecords
    {
        private int _recordingAttempts;

        public TaskCompletionSource RecordingStarted { get; } = new( TaskCreationOptions.RunContinuationsAsynchronously );

        public TaskCompletionSource ConcurrentMiss { get; } = new( TaskCreationOptions.RunContinuationsAsynchronously );

        public TaskCompletionSource ResumeRecording { get; } = new( TaskCreationOptions.RunContinuationsAsynchronously );

        public int RecordingAttempts => Volatile.Read( ref _recordingAttempts );

        public Exception? FirstFailure { get; set; }

        public Task<Blob> InsertBlobAsync( NewBlob blob, DateTimeOffset createdAt, CancellationToken cancellationToken ) =>
            inner.InsertBlobAsync( blob, createdAt, cancellationToken );

        public Task<Blob?> AttachedBlobAsync( AttachmentOwner owner, string name, CancellationToken cancellationToken ) =>
            inner.AttachedBlobAsync( owner, name, cancellationToken );

        public async Task<Blob?> VariantImageAsync( long blobId, string variationDigest, CancellationToken cancellationToken )
        {
            var image = await inner.VariantImageAsync( blobId, variationDigest, cancellationToken );
            if ( image is null && RecordingStarted.Task.IsCompleted )
            {
                ConcurrentMiss.TrySetResult( );
            }

            return image;
        }

        public async Task<Blob?> RecordVariantAsync(
            long blobId, string variationDigest, NewBlob image, DateTimeOffset createdAt, CancellationToken cancellationToken )
        {
            if ( Interlocked.Increment( ref _recordingAttempts ) == 1 )
            {
                RecordingStarted.TrySetResult( );
                await ResumeRecording.Task.WaitAsync( cancellationToken );
                if ( FirstFailure is { } failure )
                {
                    throw failure;
                }
            }

            return await inner.RecordVariantAsync( blobId, variationDigest, image, createdAt, cancellationToken );
        }

        public Task<Blob?> RecordPreviewImageAsync( long blobId, NewBlob image, DateTimeOffset createdAt, CancellationToken cancellationToken ) =>
            inner.RecordPreviewImageAsync( blobId, image, createdAt, cancellationToken );
    }

    [Fact]
    public async Task Losing_the_preview_race_returns_the_attached_image_and_deletes_the_losers_file( )
    {
        using var harness = new StorageHarness( );
        var video = await harness.UploadFixtureAsync( "alpha-centuri.mov", "video/quicktime" );
        Blob? winner = null;
        harness.Records.BeforeRecording = async ( ) => winner = await harness.Storage.PreviewImageAsync( video, Cancel );

        var image = await harness.Storage.PreviewImageAsync( video, Cancel );

        Assert.Equal( winner!.Id, image.Id );
        Assert.Equal( ("alpha-centuri.jpg", "image/jpeg"), (image.Filename.Raw, image.ContentType) );
        Assert.Equal( """{"identified":true,"width":320,"height":180,"analyzed":true}""", image.Metadata.ToJson( ) );
        Assert.Single( harness.Records.Attachments, row => row.Name == "preview_image" && row.Owner == AttachmentOwner.ForBlob( video.Id ) );
        Assert.Equal( new[] { Relative( video ), Relative( winner ) }.Order( StringComparer.Ordinal ), harness.Files );
    }

    [Fact]
    public async Task An_upload_whose_row_is_never_kept_leaves_no_file( )
    {
        using var harness = new StorageHarness( );
        using var content = File.OpenRead( Fixture( "moon.jpg" ) );

        using ( var staged = await harness.Storage.StageUploadAsync( content, new BlobFilename( "moon.jpg" ), "image/jpeg", Cancel ) )
        {
            Assert.Equal( 12794, staged.Blob.ByteSize );
            Assert.Equal( "j65kKv2abaidraFRfGAQlg==", staged.Blob.Checksum );
            Assert.False( staged.IsPublished );
        }

        content.Position = 0;
        using ( var published = await harness.Storage.StageUploadAsync( content, new BlobFilename( "moon.jpg" ), "image/jpeg", Cancel ) )
        {
            published.Publish( );
            Assert.True( harness.Storage.Disk.Exists( published.Blob.Key ) );
        }

        using ( var unpublished = await harness.Storage.StageUploadAsync( new MemoryStream( [1] ), new BlobFilename( "a" ), null, Cancel ) )
        {
            Assert.Throws<InvalidOperationException>( unpublished.Keep );
        }

        Assert.Empty( harness.Files );
    }

    [Fact]
    public async Task Client_filenames_never_shape_paths( )
    {
        using var harness = new StorageHarness( );
        var hostile = await harness.UploadFixtureAsync( "moon.jpg", "image/jpeg", "../../../tmp/evil.j[page=5]" );
        var plain = await harness.UploadFixtureAsync( "moon.jpg", "image/jpeg", "../photo.JPG" );

        using var hostileCopy = await harness.Storage.OpenAsync( hostile, Cancel );
        using var plainCopy = await harness.Storage.OpenAsync( plain, Cancel );

        Assert.Equal( "../../../tmp/evil.j[page=5]", hostile.Filename.Raw );
        Assert.Equal( new[] { Relative( hostile ), Relative( plain ) }.Order( StringComparer.Ordinal ), harness.Files );
        Assert.Matches( @"^ActiveStorage-1-[0-9a-f]{32}$", Path.GetFileName( hostileCopy.Path ) );
        Assert.Matches( @"^ActiveStorage-2-[0-9a-f]{32}\.JPG$", Path.GetFileName( plainCopy.Path ) );
        Assert.Equal( harness.Options.TempDirectory, Path.GetDirectoryName( hostileCopy.Path ) );
    }

    [Fact]
    public async Task A_corrupted_file_fails_its_integrity_check_and_leaves_no_copy( )
    {
        using var harness = new StorageHarness( );
        var blob = await harness.UploadFixtureAsync( "moon.jpg", "image/jpeg" );
        await File.AppendAllTextAsync( harness.Storage.Disk.PathFor( blob.Key ), "tampered", Cancel );

        var error = await Assert.ThrowsAsync<StorageException>( ( ) => harness.Storage.AnalyzedMetadataAsync( blob, Cancel ) );

        Assert.Equal( StorageFailure.Integrity, error.Failure );
        Assert.Empty( harness.TempFiles );
    }

    [Fact]
    public async Task A_missing_file_is_file_not_found( )
    {
        using var harness = new StorageHarness( );
        var blob = await harness.UploadFixtureAsync( "moon.jpg", "image/jpeg" );
        harness.Storage.DeleteFiles( blob );

        var error = await Assert.ThrowsAsync<StorageException>( ( ) => harness.Storage.ProcessVariantAsync(
            blob,
            BlobStorage.VariationFor( blob, Variation.ResizeToLimit( 10, 10 ) ),
            Cancel ) );

        Assert.Equal( StorageFailure.FileNotFound, error.Failure );
    }

    [Fact]
    public async Task Images_over_the_pixel_limit_are_refused_without_rows_or_files( )
    {
        using var harness = new StorageHarness( options => options.MaxImagePixels = 640 * 639 );
        var blob = await harness.UploadFixtureAsync( "moon.jpg", "image/jpeg" );

        var error = await Assert.ThrowsAsync<StorageException>( ( ) => harness.Storage.ProcessVariantAsync(
            blob,
            BlobStorage.VariationFor( blob, Variation.ResizeToLimit( 10, 10 ) ),
            Cancel ) );

        Assert.Equal( StorageFailure.LimitExceeded, error.Failure );
        Assert.Empty( harness.Records.VariantRecords );
        Assert.Equal( [Relative( blob )], harness.Files );
    }

    [Fact]
    public async Task A_conversion_past_its_deadline_is_stopped_inside_libvips( )
    {
        using var harness = new StorageHarness( options => options.ImageTimeout = TimeSpan.FromMilliseconds( 200 ) );
        var blob = await harness.UploadFixtureAsync( "earth.png", "image/png" );
        var clock = Stopwatch.StartNew( );

        var error = await Assert.ThrowsAsync<StorageException>( ( ) => harness.Storage.ProcessVariantAsync(
            blob,
            BlobStorage.VariationFor( blob, Variation.ResizeToLimit( 1200, 800 ) ),
            Cancel ) );

        Assert.Equal( StorageFailure.TimedOut, error.Failure );
        Assert.True( clock.Elapsed < TimeSpan.FromSeconds( 5 ), $"stopped after {clock.Elapsed}" );
        Assert.Equal( [Relative( blob )], harness.Files );
    }

    [Fact]
    public async Task Uploads_past_the_size_limit_are_refused( )
    {
        using var harness = new StorageHarness( options => options.MaxUploadBytes = 1000 );

        var error = await Assert.ThrowsAsync<StorageException>( ( ) => harness.UploadFixtureAsync( "moon.jpg", "image/jpeg" ) );

        Assert.Equal( StorageFailure.LimitExceeded, error.Failure );
        Assert.Empty( harness.Records.Blobs );
        Assert.Empty( harness.Files );
    }

    [Fact]
    public async Task Bitmaps_are_neither_variable_nor_previewable( )
    {
        using var harness = new StorageHarness( );
        var bitmap = await harness.UploadFixtureAsync( "pixel.bmp", "image/bmp" );

        var unrepresentable = await Assert.ThrowsAsync<StorageException>( ( ) => harness.Storage.ProcessRepresentationAsync(
            bitmap,
            Variation.ResizeToLimit( 1, 1 ),
            Cancel ) );
        var invariable = Assert.Throws<StorageException>( ( ) => BlobStorage.VariationFor( bitmap, Variation.ResizeToLimit( 1, 1 ) ) );
        var unpreviewable = await Assert.ThrowsAsync<StorageException>( ( ) => harness.Storage.PreviewImageAsync( bitmap, Cancel ) );

        Assert.Equal( StorageFailure.Unrepresentable, unrepresentable.Failure );
        Assert.Equal( StorageFailure.Invariable, invariable.Failure );
        Assert.Equal( StorageFailure.Unpreviewable, unpreviewable.Failure );
    }

    [Fact]
    public async Task Without_ffprobe_or_ffmpeg_videos_analyze_empty_and_are_not_previewable( )
    {
        using var harness = new StorageHarness( options =>
        {
            options.FfprobePath = "campfire-no-ffprobe";
            options.FfmpegPath = "campfire-no-ffmpeg";
        } );
        var video = await harness.UploadFixtureAsync( "alpha-centuri.mov", "video/quicktime" );

        var metadata = await harness.Storage.AnalyzedMetadataAsync( video, Cancel );

        Assert.Equal( """{"identified":true,"audio":false,"video":false,"analyzed":true}""", metadata.ToJson( ) );
        Assert.False( await harness.Storage.IsPreviewableAsync( video ) );
        Assert.False( await harness.Storage.IsRepresentableAsync( video ) );
    }

    [Theory]
    [InlineData( "photo.tiff", "image/tiff", """{"format":"png","resize_to_limit":[1200,800]}""" )]
    [InlineData( "PHOTO.JPG", "image/jpeg", """{"format":"JPG","resize_to_limit":[1200,800]}""" )]
    [InlineData( "photo.png", "image/jpeg", """{"format":"jpg","resize_to_limit":[1200,800]}""" )]
    [InlineData( "photo", "image/webp", """{"format":"webp","resize_to_limit":[1200,800]}""" )]
    [InlineData( "photo.heic", "image/heic", """{"format":"png","resize_to_limit":[1200,800]}""" )]
    public void Variants_default_to_the_blobs_format_or_the_png_symbol( string filename, string contentType, string expected )
    {
        var blob = new Blob { Id = 1, Key = "k", Filename = new BlobFilename( filename ), ContentType = contentType, ServiceName = "local" };

        Assert.Equal( expected, BlobStorage.VariationFor( blob, Variation.ResizeToLimit( 1200, 800 ) ).ToString( ) );
    }

    [Fact]
    public void The_png_default_has_a_canonical_json_sha256_digest( )
    {
        var tiff = new Blob { Id = 1, Key = "k", Filename = new BlobFilename( "scan.tiff" ), ContentType = "image/tiff", ServiceName = "local" };
        Assert.Equal(
            "6dca2a709f080f08c2ea451ca8ccf17349646617cc4f1601bdeca40166fedeae",
            BlobStorage.VariationFor( tiff, Variation.ResizeToLimit( 1200, 800 ) ).Digest( ) );
    }

    private static string Relative( Blob blob ) => Path.Combine( blob.Key[..2], blob.Key[2..4], blob.Key );
}
