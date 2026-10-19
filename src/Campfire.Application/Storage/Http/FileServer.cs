using System.Globalization;
using System.Text;
using Campfire.Application.Storage.Disk;

namespace Campfire.Application.Storage.Http;

/// <summary>
/// <c>ActiveStorage::FileServer#serve_file</c> over <c>Rack::Files#serving</c> (rack 3.2.6), and
/// <c>ActiveStorage::DiskController#show</c> around it. Conditional GET on the file's mtime, single
/// and multipart byte ranges, 416s and HEAD, as pure decisions; the web layer writes the result.
/// </summary>
public static class FileServer
{
    /// <summary><c>Rack::Files::MULTIPART_BOUNDARY</c>.</summary>
    public const string MultipartBoundary = "AaB03x";

    /// <summary>The <c>after_action</c> header of <c>DiskController#show</c> (<c>config/initializers/active_storage.rb</c>).</summary>
    public const string DiskCacheControl = "max-age=3600, public";

    /// <summary>Disk keys have no extension, so Rack's MIME lookup falls back to its default.</summary>
    private const string RackDefaultMime = "text/plain";

    /// <summary>
    /// <c>DiskController#show</c> with the initializer's <c>Cache-Control</c>, which the <c>after_action</c>
    /// adds to every outcome: 404 (empty body) for a bad key or a missing file, else the served file.
    /// File bodies have no <c>ETag</c>. The HTTP layer validates conditional headers against file existence and <c>Last-Modified</c>.
    /// </summary>
    public static ServedFile DiskShow( DiskService disk, ActiveStorageVerifier verifier, string encodedKey, FileRequest request, DateTimeOffset now )
    {
        ArgumentNullException.ThrowIfNull( disk );

        var key = DiskService.DecodeVerifiedKey( verifier, encodedKey, now );
        if ( key is null || !BlobKeys.IsWellFormed( key.Key ) )
        {
            return NotFound( );
        }

        try
        {
            var served = ServeFile( request, disk.PathFor( key.Key ), key.ContentType, key.Disposition );

            return served with
            {
                Headers = [.. served.Headers, new( RackHeaderNames.CacheControl, DiskCacheControl )]
            };
        }
        catch ( Exception error ) when ( error is FileNotFoundException or DirectoryNotFoundException )
        {
            // `rescue Errno::ENOENT; head :not_found`
            return NotFound( );
        }
    }

    /// <summary>
    /// <c>serve_file(path, content_type:, disposition:)</c>. Throws <see cref="FileNotFoundException"/>
    /// when the file is missing, as <c>File.mtime</c> raises <c>Errno::ENOENT</c>.
    /// </summary>
    public static ServedFile ServeFile( FileRequest request, string path, string? contentType, string? disposition )
    {
        ArgumentNullException.ThrowIfNull( request );

        var served = Serving( request, path );
        var headers = served.Headers.ToList( );
        if ( served.Status == 416 )
        {
            headers.RemoveAll( header => string.Equals( header.Key, RackHeaderNames.Cascade, StringComparison.OrdinalIgnoreCase ) );
        }

        if ( served.Status != 304 )
        {
            SetHeader( headers, RackHeaderNames.ContentType, contentType ?? StorageContentTypes.Binary );
        }

        SetHeader( headers, RackHeaderNames.ContentDisposition, disposition ?? ContentDisposition.Attachment );

        return served with
        {
            Headers = headers
        };
    }

    /// <summary><c>Rack::Files#serving(request, path)</c>.</summary>
    private static ServedFile Serving( FileRequest request, string path )
    {
        if ( request.Method == "OPTIONS" )
        {
            return new ServedFile( 200, [new( RackHeaderNames.Allow, "GET, HEAD, OPTIONS" ), new( RackHeaderNames.ContentLength, "0" )], [] );
        }

        var file = new FileInfo( path );
        if ( !file.Exists )
        {
            throw new FileNotFoundException( "No such file.", path );
        }

        var lastModified = file.LastWriteTimeUtc.ToString( "r", CultureInfo.InvariantCulture );
        if ( request.Method is "GET" or "HEAD" && request.IsFresh?.Invoke( lastModified ) == true )
        {
            return new ServedFile( 304, [new( RackHeaderNames.LastModified, lastModified )], [] );
        }

        var size = file.Length;
        var ranges = ByteRanges.Parse( request.Range, size );
        if ( ranges is { Count: 0 } )
        {
            const string message = "Byte range unsatisfiable\n";
            return new ServedFile(
                416,
                [
                    new( RackHeaderNames.ContentType, "text/plain" ),
                    new( RackHeaderNames.ContentLength, message.Length.ToString( CultureInfo.InvariantCulture ) ),
                    new( RackHeaderNames.Cascade, "pass" ),
                    new( RackHeaderNames.ContentRange, $"bytes */{size}" ),
                ],
                [new BytesPart( Encoding.ASCII.GetBytes( message ) )] );
        }

        List<KeyValuePair<string, string>> headers = [new( RackHeaderNames.LastModified, lastModified ), new( RackHeaderNames.ContentType, RackDefaultMime )];
        var (status, body) = ranges is null ? (200, [new FilePart( path, 0, size )]) : (206, PartialBody( headers, path, ranges, size ));
        headers.Add( new( RackHeaderNames.ContentLength, body.Sum( part => part.Length ).ToString( CultureInfo.InvariantCulture ) ) );

        // HEAD drops the body; so does an empty file (its single range reads nothing).
        return new ServedFile( status, headers, request.Method == "HEAD" || size == 0 ? [] : body );
    }

    /// <summary><c>BaseIterator</c>: one range as is, several as <c>multipart/byteranges</c>.</summary>
    private static List<BodyPart> PartialBody( List<KeyValuePair<string, string>> headers, string path, IReadOnlyList<ByteRange> ranges, long size )
    {
        if ( ranges.Count == 1 )
        {
            headers.Add( new( RackHeaderNames.ContentRange, $"bytes {ranges[0].Start}-{ranges[0].End}/{size}" ) );

            return [new FilePart( path, ranges[0].Start, ranges[0].Length )];
        }

        SetHeader( headers, RackHeaderNames.ContentType, $"multipart/byteranges; boundary={MultipartBoundary}" );
        var parts = new List<BodyPart>( (ranges.Count * 2) + 1 );
        foreach ( var range in ranges )
        {
            var heading = $"\r\n--{MultipartBoundary}\r\ncontent-type: {RackDefaultMime}\r\ncontent-range: bytes {range.Start}-{range.End}/{size}\r\n\r\n";
            parts.Add( new BytesPart( Encoding.ASCII.GetBytes( heading ) ) );
            parts.Add( new FilePart( path, range.Start, range.Length ) );
        }

        parts.Add( new BytesPart( Encoding.ASCII.GetBytes( $"\r\n--{MultipartBoundary}--\r\n" ) ) );

        return parts;
    }

    private static ServedFile NotFound( ) => new( 404, [new( RackHeaderNames.CacheControl, DiskCacheControl )], [] );

    private static void SetHeader( List<KeyValuePair<string, string>> headers, string name, string value )
    {
        var index = headers.FindIndex( header => string.Equals( header.Key, name, StringComparison.OrdinalIgnoreCase ) );
        if ( index >= 0 )
        {
            headers[index] = new( headers[index].Key, value );
        }
        else
        {
            headers.Add( new( name, value ) );
        }
    }
}
