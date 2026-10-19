using System.Text.RegularExpressions;

using Campfire.Application.Data;
using Campfire.Application.Storage.Disk;
using Campfire.Application.Storage.Media;

namespace Campfire.Application.Storage;

/// <summary>
/// The Active Storage flows Campfire drives, over one disk service, the app's verifier and the
/// <see cref="IBlobRecords"/> rows: uploads (<c>create_and_upload!</c>), the file half of analysis, tracked variants
/// (<c>VariantWithRecord</c>) and video previews (<c>ActiveStorage::Preview</c>).
/// <para>
/// Each flow is split so the slow half never holds the database writer: the file half (copying,
/// MD5, libvips, ffmpeg) touches no rows and yields a <see cref="StagedBlob"/>; the record half is
/// one short <see cref="IBlobRecords"/> call. The combined methods do both, in Rails' order.
/// </para>
/// </summary>
public sealed partial class BlobStorage : IDisposable
{
    private readonly StorageOptions _options;
    private readonly IBlobRecords _records;
    private readonly TimeProvider _time;
    private readonly BlobAnalyzer _analyzer;
    private readonly SemaphoreSlim _conversions;
    private readonly VariantFlights _variantFlights = new( );

    public BlobStorage( StorageOptions options, ActiveStorageVerifier verifier, IBlobRecords records, TimeProvider time )
    {
        ArgumentNullException.ThrowIfNull( options );
        ArgumentNullException.ThrowIfNull( verifier );
        ArgumentNullException.ThrowIfNull( records );
        ArgumentNullException.ThrowIfNull( time );

        options.Validate( );
        _options = options;
        _records = records;
        _time = time;
        Verifier = verifier;
        Disk = new DiskService( options.Root, options.ServiceName );
        Tools = new MediaTools( options );
        _analyzer = new BlobAnalyzer( Tools );
        _conversions = new SemaphoreSlim( options.MaxConcurrentConversions, options.MaxConcurrentConversions );
    }

    public DiskService Disk { get; }

    public ActiveStorageVerifier Verifier { get; }

    public MediaTools Tools { get; }

    public void Dispose( )
    {
        _variantFlights.Dispose( );
        _conversions.Dispose( );
    }

    // --- Uploads -----------------------------------------------------------------------------------

    /// <summary>
    /// The file half of attaching an upload (<c>Blob.build_after_unfurling(io:, filename:, content_type:)</c>,
    /// <c>identify: true</c>): streams <paramref name="content"/> into a staged file of at most
    /// <see cref="StorageOptions.MaxUploadBytes"/>, and builds the row with a new key, the checksum
    /// and the content type Marcel identifies. The client's filename is only ever data.
    /// </summary>
    public async Task<StagedBlob> StageUploadAsync( Stream content, BlobFilename filename, string? declaredType, CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( filename );

        var file = await Disk.StageAsync( content, _options.MaxUploadBytes, cancellationToken );
        var blob = NewBlob.Unfurl( BlobKeys.Generate( ), file.Head.Span, file.Length, file.Checksum, filename, declaredType, Disk.Name );

        return new StagedBlob( Disk, blob, file );
    }

    /// <summary><see cref="StageUploadAsync"/> from a file already on disk (a spooled multipart part); the source remains with its caller.</summary>
    public async Task<StagedBlob> StageFileAsync( string path, BlobFilename filename, string? declaredType, CancellationToken cancellationToken )
    {
        var source = StorageFiles.OpenSequentialRead( path );
        await using ( source )
        {
            return await StageUploadAsync( source, filename, declaredType, cancellationToken );
        }
    }

    /// <summary>
    /// <c>Blob.create_and_upload!(io:, filename:, content_type:)</c>: stages the bytes, saves the row,
    /// then publishes the file, as Rails saves before it uploads.
    /// </summary>
    public async Task<Blob> CreateAndUploadAsync( Stream content, BlobFilename filename, string? declaredType, CancellationToken cancellationToken )
    {
        using var staged = await StageUploadAsync( content, filename, declaredType, cancellationToken );
        var blob = await _records.InsertBlobAsync( staged.Blob, Now( ), cancellationToken );
        staged.Publish( );
        staged.Keep( );

        return blob;
    }

    // --- Reading -----------------------------------------------------------------------------------

    /// <summary>
    /// <c>blob.open</c>: a checksum-verified copy in the temp directory named
    /// <c>ActiveStorage-&lt;id&gt;-…&lt;ext&gt;</c>, deleted on dispose. The extension is kept only when it
    /// is plain alphanumeric, so no client-chosen text reaches the tools' option syntax.
    /// </summary>
    public async Task<BlobCopy> OpenAsync( Blob blob, CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( blob );

        var extension = PlainExtension( ).IsMatch( blob.Filename.ExtensionWithDelimiter ) ? blob.Filename.ExtensionWithDelimiter : "";
        var copy = new BlobCopy( Path.Combine( _options.ResolvedTempDirectory, $"ActiveStorage-{blob.Id}-{Guid.NewGuid( ):N}{extension}" ) );
        try
        {
            var checksum = await CopyAsync( blob.Key, copy.Path, cancellationToken );

            // `verify_integrity_of(tempfile, checksum:)`; only composed blobs, which Campfire never makes, lack a checksum.
            if ( blob.Checksum is not null && checksum != blob.Checksum )
            {
                throw new StorageException( StorageFailure.Integrity, $"The file of blob {blob.Id} doesn't match its checksum." );
            }

            return copy;
        }
        catch
        {
            copy.Dispose( );
            throw;
        }
    }

    /// <summary>Deletes the blob's files (<c>Blob#delete</c>): its own, and legacy untracked variants of an image. Rows are the caller's.</summary>
    public void DeleteFiles( Blob blob )
    {
        ArgumentNullException.ThrowIfNull( blob );

        DeleteFiles( blob.Key, blob.IsImage );
    }

    /// <summary>Deletes files from a durable key after its blob row has disappeared.</summary>
    public void DeleteFiles( string key, bool deleteLegacyVariants )
    {
        Disk.Delete( key );
        if ( deleteLegacyVariants )
        {
            Disk.DeleteLegacyVariants( key );
        }
    }

    // --- Analysis ----------------------------------------------------------------------------------

    /// <summary>
    /// The file half of <c>blob.analyze</c>: what it saves, <c>metadata.merge(analyzer.metadata.merge(analyzed: true))</c>.
    /// <see cref="BlobOperations.AnalyzeAsync"/> saves it and touches the blob's attachment records.
    /// </summary>
    public async Task<BlobMetadata> AnalyzedMetadataAsync( Blob blob, CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( blob );

        var kind = BlobAnalyzer.For( blob.ContentType );
        var extracted = BlobMetadata.Empty;
        if ( kind != AnalyzerKind.Null )
        {
            using var copy = await OpenAsync( blob, cancellationToken );
            extracted = await AnalyzeFileAsync( kind, copy.Path, cancellationToken );
        }

        return blob.Metadata.Merge( extracted.With( BlobMetadata.AnalyzedKey, true ) );
    }

    /// <summary>The analyzer's metadata for a file, run under the conversion limit.</summary>
    private async Task<BlobMetadata> AnalyzeFileAsync( AnalyzerKind kind, string path, CancellationToken cancellationToken )
    {
        await _conversions.WaitAsync( cancellationToken );
        try
        {
            return await _analyzer.MetadataAsync( kind, path, cancellationToken );
        }
        finally
        {
            _conversions.Release( );
        }
    }

    /// <summary>Copies the file of <paramref name="key"/> to a new file at <paramref name="path"/>, returning the checksum of what was copied.</summary>
    private async Task<string> CopyAsync( string key, string path, CancellationToken cancellationToken )
    {
        var source = Disk.OpenRead( key );
        await using ( source )
        {
            var output = StorageFiles.CreateNew( path );
            await using ( output )
            {
                return await BlobKeys.CopyWithChecksumAsync( source, output, cancellationToken );
            }
        }
    }

    /// <summary>The current time as Active Record stores timestamps: UTC, truncated to microseconds.</summary>
    private DateTimeOffset Now( ) => new( RailsTimestamp.Truncate( _time.GetUtcNow( ).UtcDateTime ) );

    [GeneratedRegex( @"^\.[A-Za-z0-9]{1,16}$" )]
    private static partial Regex PlainExtension( );
}
