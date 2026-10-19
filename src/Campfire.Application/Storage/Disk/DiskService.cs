namespace Campfire.Application.Storage.Disk;

/// <summary>
/// <c>ActiveStorage::Service::DiskService</c> for Campfire's <c>local</c> service
/// (<c>root: Rails.root.join("storage", "files")</c>): a blob's file is <c>&lt;root&gt;/ab/cd/&lt;key&gt;</c>.
/// Files are written to <see cref="StagingDirectory"/> first and renamed into place whole
/// (<see cref="Publish"/>); Rails writes in place. Paths are built from keys only, never from filenames.
/// </summary>
public sealed partial class DiskService
{
    /// <summary>The staging directory's name under the root. Keys are alphanumeric, so no key folder can collide with it.</summary>
    public const string StagingDirectoryName = ".staging";

    public DiskService( string root, string name = "local" )
    {
        ArgumentException.ThrowIfNullOrEmpty( root );
        ArgumentException.ThrowIfNullOrEmpty( name );

        Root = Path.GetFullPath( root );
        Name = name;
        StagingDirectory = Path.Combine( Root, StagingDirectoryName );
    }

    /// <summary>The service's root directory.</summary>
    public string Root { get; }

    /// <summary>The service name blobs record (<c>service_name</c>).</summary>
    public string Name { get; }

    /// <summary>Where files are written before they are published. It lives under the root, so publishing is a same-directory-tree rename.</summary>
    public string StagingDirectory { get; }

    /// <summary><c>path_for(key)</c>: <c>File.join(root, key[0..1], key[2..3], key)</c>. Only well-formed keys are accepted.</summary>
    public string PathFor( string key )
    {
        if ( !BlobKeys.IsWellFormed( key ) )
        {
            throw new ArgumentException( "A blob key is alphanumeric.", nameof( key ) );
        }

        var first = key[..Math.Min( 2, key.Length )];
        var second = key.Length > 2 ? key[2..Math.Min( 4, key.Length )] : "";
        return Path.Combine( Root, first, second, key );
    }

    /// <summary>
    /// A fresh path in the staging directory, for a tool that writes its own output (libvips picks its saver from
    /// <paramref name="suffix"/>).
    /// </summary>
    public string NewStagingPath( string suffix = "" )
    {
        Directory.CreateDirectory( StagingDirectory );

        return Path.Combine( StagingDirectory, Guid.NewGuid( ).ToString( "N" ) + suffix );
    }

    /// <summary>Streams <paramref name="content"/> into a new staging file, refusing more than <paramref name="maxBytes"/>.</summary>
    public Task<StagedFile> StageAsync( Stream content, long maxBytes, CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( content );

        return StagedFile.WriteAsync( NewStagingPath( ), content, maxBytes, cancellationToken );
    }

    /// <summary>Checksums and takes ownership of a file written at a <see cref="NewStagingPath"/>.</summary>
    public Task<StagedFile> AdoptStagedAsync( string stagingPath, long maxBytes, CancellationToken cancellationToken )
    {
        var fullPath = Path.GetFullPath( stagingPath );
        if ( Path.GetDirectoryName( fullPath ) != StagingDirectory )
        {
            throw new ArgumentException( "Only files in the staging directory can be adopted.", nameof( stagingPath ) );
        }

        return StagedFile.AdoptAsync( fullPath, maxBytes, cancellationToken );
    }

    /// <summary>
    /// Renames a staged file to <paramref name="key"/>'s path in one step. The key must be new: an
    /// existing file is never replaced.
    /// </summary>
    public void Publish( StagedFile staged, string key )
    {
        ArgumentNullException.ThrowIfNull( staged );

        if ( staged.IsPublished )
        {
            throw new InvalidOperationException( "The file is already published." );
        }

        var destination = PathFor( key );
        Directory.CreateDirectory( Path.GetDirectoryName( destination )! );
        File.Move( staged.Path, destination, overwrite: false );
        staged.MarkPublished( destination );
    }

    /// <summary>
    /// <c>upload(key, io, checksum:)</c>, as the direct-upload <c>PUT</c> uses it: the bytes are staged,
    /// compared with <paramref name="checksum"/> (<see cref="StorageFailure.Integrity"/> when they
    /// differ, and nothing is kept) and only then published.
    /// </summary>
    public async Task UploadAsync( string key, Stream content, string? checksum, long maxBytes, CancellationToken cancellationToken )
    {
        using var staged = await StageAsync( content, maxBytes, cancellationToken );
        if ( checksum is not null && staged.Checksum != checksum )
        {
            throw new StorageException( StorageFailure.Integrity, "The uploaded bytes don't match the checksum." );
        }

        Publish( staged, key );
    }

    /// <summary>Opens the blob's file for reading; <see cref="StorageFailure.FileNotFound"/> when it is missing.</summary>
    public FileStream OpenRead( string key )
    {
        try
        {
            return StorageFiles.OpenSequentialRead( PathFor( key ), FileShare.Read | FileShare.Delete );
        }
        catch ( Exception error ) when ( error is FileNotFoundException or DirectoryNotFoundException )
        {
            throw new StorageException( StorageFailure.FileNotFound, $"No file for key {key}.", error );
        }
    }

    /// <summary><c>download(key)</c>.</summary>
    public async Task<byte[]> DownloadAsync( string key, CancellationToken cancellationToken )
    {
        var file = OpenRead( key );
        await using ( file )
        {
            var data = new MemoryStream( );
            await file.CopyToAsync( data, cancellationToken );

            return data.ToArray( );
        }
    }

    /// <summary><c>download_chunk(key, range)</c>: up to <paramref name="length"/> bytes from <paramref name="offset"/>.</summary>
    public async Task<byte[]> DownloadChunkAsync( string key, long offset, int length, CancellationToken cancellationToken )
    {
        ArgumentOutOfRangeException.ThrowIfNegative( offset );
        ArgumentOutOfRangeException.ThrowIfNegative( length );

        var file = OpenRead( key );
        await using ( file )
        {
            file.Seek( offset, SeekOrigin.Begin );
            var data = new byte[( int ) Math.Min( length, Math.Max( 0, file.Length - offset ) )];
            await file.ReadExactlyAsync( data, cancellationToken );

            return data;
        }
    }

    /// <summary><c>delete(key)</c>: a missing file is fine.</summary>
    public void Delete( string key )
    {
        try
        {
            File.Delete( PathFor( key ) );
        }
        catch ( DirectoryNotFoundException )
        {
            // A missing key directory also means the file is absent.
        }
    }

    /// <summary>
    /// <c>delete_prefixed("variants/#{key}/")</c> from <c>Blob#delete</c>: removes every entry of the
    /// legacy (untracked) variants directory <c>&lt;root&gt;/va/ri/variants/&lt;key&gt;/</c>, as
    /// <c>Dir.glob(...*)</c> plus <c>rm_rf</c> do (dot entries don't match the glob).
    /// </summary>
    public void DeleteLegacyVariants( string key )
    {
        if ( !BlobKeys.IsWellFormed( key ) )
        {
            throw new ArgumentException( "A blob key is alphanumeric.", nameof( key ) );
        }

        var directory = Path.Combine( Root, "va", "ri", "variants", key );
        if ( !Directory.Exists( directory ) )
        {
            return;
        }

        foreach ( var entry in new DirectoryInfo( directory ).EnumerateFileSystemInfos( ) )
        {
            if ( entry.Name.StartsWith( '.' ) )
            {
                continue;
            }

            try
            {
                if ( entry is DirectoryInfo subdirectory && !entry.Attributes.HasFlag( FileAttributes.ReparsePoint ) )
                {
                    subdirectory.Delete( recursive: true );
                }
                else
                {
                    entry.Delete( );
                }
            }
            catch ( FileNotFoundException )
            {
                // Another cleanup already removed this entry.
            }
            catch ( DirectoryNotFoundException )
            {
                // Another cleanup already removed this directory.
            }
        }
    }

    /// <summary><c>exist?(key)</c>.</summary>
    public bool Exists( string key ) => File.Exists( PathFor( key ) );

    /// <summary>
    /// Deletes staging files last written before <paramref name="olderThan"/>: what a crash between
    /// staging and publishing leaves behind. Returns how many were removed.
    /// </summary>
    public int SweepStaging( DateTimeOffset olderThan )
    {
        if ( !Directory.Exists( StagingDirectory ) )
        {
            return 0;
        }

        var removed = 0;
        foreach ( var file in new DirectoryInfo( StagingDirectory ).EnumerateFiles( ) )
        {
            if ( file.LastWriteTimeUtc < olderThan.UtcDateTime )
            {
                file.Delete( );
                removed++;
            }
        }

        return removed;
    }
}
