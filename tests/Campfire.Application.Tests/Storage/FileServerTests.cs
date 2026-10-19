using System.Text;
using Campfire.Application.Storage.Http;
using Campfire.Application.Tests.Storage.Support;

namespace Campfire.Application.Tests.Storage;

/// <summary>
/// <c>Rack::Files#serving</c> under <c>ActiveStorage::FileServer</c> and <c>DiskController#show</c>.
/// The expected headers and bodies were captured from rack 3.2.6 in the reference image for a
/// 10-byte file <c>0123456789</c> modified at 2026-09-26 12:00:00 UTC.
/// </summary>
public sealed class FileServerTests : IDisposable
{
    private const string LastModified = "Sat, 26 Sep 2026 12:00:00 GMT";
    private readonly StorageHarness _harness = new( );
    private readonly string _path;

    public FileServerTests( )
    {
        _path = Path.Combine( _harness.Options.TempDirectory!, "file" );
        File.WriteAllText( _path, "0123456789" );
        File.SetLastWriteTimeUtc( _path, new DateTime( 2026, 9, 26, 12, 0, 0, DateTimeKind.Utc ) );
    }

    public void Dispose( ) => _harness.Dispose( );

    [Fact]
    public void A_whole_file_is_served_with_its_mtime_and_the_given_type_and_disposition( )
    {
        var served = FileServer.ServeFile( new FileRequest( "GET" ), _path, "image/png", "inline; filename=\"a.png\"" );

        Assert.Equal( 200, served.Status );
        AssertHeaders(
            served,
            ("last-modified", LastModified),
            ("content-type", "image/png"),
            ("content-length", "10"),
            ("content-disposition", "inline; filename=\"a.png\"") );
        Assert.Equal( "0123456789", Body( served ) );
    }

    [Fact]
    public void Head_keeps_the_headers_and_drops_the_body( )
    {
        var whole = FileServer.ServeFile( new FileRequest( "HEAD" ), _path, null, null );
        var ranges = FileServer.ServeFile( new FileRequest( "HEAD", "bytes=0-1,3-4" ), _path, null, null );

        Assert.Equal( (200, "10"), (whole.Status, whole.Header( "content-length" )) );
        Assert.Equal( (206, "156"), (ranges.Status, ranges.Header( "content-length" )) );
        Assert.Empty( whole.Body );
        Assert.Empty( ranges.Body );
        Assert.Equal( ("application/octet-stream", "attachment"), (whole.Header( "content-type" ), whole.Header( "content-disposition" )) );
    }

    [Fact]
    public void A_single_range_is_partial_content_with_a_content_range( )
    {
        var served = FileServer.ServeFile( new FileRequest( "GET", "bytes=2-4" ), _path, "image/png", "inline" );

        Assert.Equal( 206, served.Status );
        AssertHeaders(
            served,
            ("last-modified", LastModified),
            ("content-type", "image/png"),
            ("content-range", "bytes 2-4/10"),
            ("content-length", "3"),
            ("content-disposition", "inline") );
        Assert.Equal( "234", Body( served ) );
        Assert.IsType<FilePart>( Assert.Single( served.Body ) );
    }

    [Fact]
    public void Several_ranges_are_multipart_with_racks_boundary_and_the_disposition_type_stays_multipart( )
    {
        var served = FileServer.ServeFile( new FileRequest( "GET", "bytes=0-1,3-4" ), _path, "image/png", "inline" );

        Assert.Equal( 206, served.Status );

        // FileServer overwrites Content-Type afterwards, exactly as Rails does, so the multipart type is lost.
        AssertHeaders( served, ("last-modified", LastModified), ("content-type", "image/png"), ("content-length", "156"), ("content-disposition", "inline") );
        Assert.Equal(
            "\r\n--AaB03x\r\ncontent-type: text/plain\r\ncontent-range: bytes 0-1/10\r\n\r\n01\r\n--AaB03x\r\ncontent-type: text/plain\r\n" +
                "content-range: bytes 3-4/10\r\n\r\n34\r\n--AaB03x--\r\n",
            Body( served ) );
        Assert.Equal( 156, served.BodyLength );
    }

    [Fact]
    public void An_unsatisfiable_range_is_a_416_without_x_cascade_and_with_the_blobs_type( )
    {
        var served = FileServer.ServeFile( new FileRequest( "GET", "bytes=20-30" ), _path, "image/png", "inline" );

        Assert.Equal( 416, served.Status );
        AssertHeaders( served, ("content-type", "image/png"), ("content-length", "25"), ("content-range", "bytes */10"), ("content-disposition", "inline") );
        Assert.Equal( "Byte range unsatisfiable\n", Body( served ) );
    }

    [Theory]
    [InlineData( "GET", 304 )]
    [InlineData( "HEAD", 304 )]
    [InlineData( "POST", 200 )]
    public void The_http_freshness_policy_receives_file_metadata_before_range_selection( string method, int status )
    {
        var calls = 0;
        var request = new FileRequest( method, method == "POST" ? null : "bytes=0-0", modified =>
        {
            calls++;
            Assert.Equal( LastModified, modified );
            return true;
        } );

        var served = FileServer.ServeFile( request, _path, "image/png", "inline" );

        Assert.Equal( status, served.Status );
        Assert.Equal( method == "POST" ? 0 : 1, calls );
        if ( status == 304 )
        {
            Assert.Empty( served.Body );
            Assert.Null( served.Header( "content-type" ) );
            Assert.Null( served.Header( "content-length" ) );
            Assert.Null( served.Header( "content-range" ) );
        }
    }

    [Fact]
    public void An_empty_file_ignores_ranges( )
    {
        var empty = Path.Combine( _harness.Options.TempDirectory!, "empty" );
        File.WriteAllBytes( empty, [] );

        var served = FileServer.ServeFile( new FileRequest( "GET", "bytes=0-1" ), empty, null, null );

        Assert.Equal( (200, "0"), (served.Status, served.Header( "content-length" )) );
        Assert.Empty( served.Body );
    }

    [Fact]
    public async Task Disk_show_serves_a_signed_key_with_the_public_cache_header_and_no_etag( )
    {
        var blob = await _harness.UploadFixtureAsync( "moon.jpg", "image/jpeg" );
        var url = _harness.Storage.Disk.UrlPath(
            _harness.Storage.Verifier,
            blob.Key,
            StorageVectorsNow.AddMinutes( 5 ),
            blob.Filename,
            blob.ContentTypeForServing,
            "inline" );
        var encodedKey = Uri.UnescapeDataString( url.Split( '/' )[4] );

        var served = FileServer.DiskShow( _harness.Storage.Disk, _harness.Storage.Verifier, encodedKey, new FileRequest( "GET" ), StorageVectorsNow );

        Assert.Equal( 200, served.Status );
        Assert.Equal( "max-age=3600, public", served.Header( "cache-control" ) );
        Assert.Equal( "image/jpeg", served.Header( "content-type" ) );
        Assert.Equal( "inline; filename=\"moon.jpg\"; filename*=UTF-8''moon.jpg", served.Header( "content-disposition" ) );
        Assert.Null( served.Header( "etag" ) );
        Assert.Equal( blob.ByteSize, served.BodyLength );
    }

    [Fact]
    public async Task Disk_show_answers_404_for_bad_expired_or_missing_files_and_still_sets_the_cache_header( )
    {
        var blob = await _harness.UploadFixtureAsync( "moon.jpg", "image/jpeg" );
        var url = _harness.Storage.Disk.UrlPath(
            _harness.Storage.Verifier,
            blob.Key,
            StorageVectorsNow.AddMinutes( 5 ),
            blob.Filename,
            blob.ContentType,
            "inline" );
        var encodedKey = Uri.UnescapeDataString( url.Split( '/' )[4] );
        var disk = _harness.Storage.Disk;
        var verifier = _harness.Storage.Verifier;

        var tampered = FileServer.DiskShow( disk, verifier, encodedKey + "0", new FileRequest( "GET" ), StorageVectorsNow );
        var expired = FileServer.DiskShow( disk, verifier, encodedKey, new FileRequest( "GET" ), StorageVectorsNow.AddMinutes( 5 ) );
        _harness.Storage.DeleteFiles( blob );
        var missing = FileServer.DiskShow( disk, verifier, encodedKey, new FileRequest( "GET" ), StorageVectorsNow );

        foreach ( var served in new[] { tampered, expired, missing } )
        {
            Assert.Equal( 404, served.Status );
            Assert.Equal( "max-age=3600, public", served.Header( "cache-control" ) );
            Assert.Empty( served.Body );
        }
    }

    private static DateTimeOffset StorageVectorsNow => StorageVectors.Now;

    private static void AssertHeaders( ServedFile served, params (string Name, string Value)[] expected ) =>
        Assert.Equal( expected.Select( header => $"{header.Name}: {header.Value}" ), served.Headers.Select( header => $"{header.Key}: {header.Value}" ) );

    private static string Body( ServedFile served )
    {
        var builder = new StringBuilder( );
        foreach ( var part in served.Body )
        {
            builder.Append( part switch
            {
                BytesPart bytes => Encoding.ASCII.GetString( bytes.Bytes ),
                FilePart file => File.ReadAllText( file.Path ).Substring( ( int ) file.Offset, ( int ) file.Length ),
                _ => throw new InvalidOperationException( ),
            } );
        }

        return builder.ToString( );
    }
}
