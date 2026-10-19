using System.Globalization;
using System.Net;
using Campfire.Application.Security;
using Campfire.Application.Storage;
using Campfire.IntegrationTests.Support;
using Campfire.Web.Paths;

namespace Campfire.IntegrationTests.Slice;

public sealed class StorageFileConditionalHttpTests
{
    [Theory]
    [InlineData( "disk", "GET" )]
    [InlineData( "disk", "HEAD" )]
    [InlineData( "proxy", "GET" )]
    [InlineData( "proxy", "HEAD" )]
    public async Task File_validators_are_evaluated_before_valid_invalid_and_unsatisfiable_ranges( string kind, string method )
    {
        await using var factory = new SliceAppFactory( );
        var blob = await CreateAsync( factory );
        var path = PathFor( factory, blob, kind, factory.App.Now.AddMinutes( 5 ) );
        using var client = factory.Client( );
        var original = await client.GetAsync( path );
        Assert.Equal( HttpStatusCode.OK, original.Status );
        Assert.NotNull( original.ContentHeaders.LastModified );
        var modified = original.ContentHeaders.LastModified!.Value.ToString( "r", CultureInfo.InvariantCulture );
        var later = original.ContentHeaders.LastModified.Value.AddMinutes( 1 ).ToString( "r", CultureInfo.InvariantCulture );
        var etag = original.Headers.ETag?.ToString( );
        var strong = etag?.StartsWith( "W/", StringComparison.Ordinal ) == true ? etag[2..] : etag;
        (string Range, string? NoneMatch, string? Since, bool Fresh)[] cases =
        [
            ("bytes=0-0", "*", null, true),
            ("bytes=999-", "*", null, true),
            ("bytes=invalid", "*", null, true),
            ("bytes=0-0", null, modified, true),
            ("bytes=0-0", null, later, true),
            ("bytes=0-0", "\"other\"", later, false),
            ("bytes=0-0", "invalid", later, false),
            ("bytes=0-0", strong is null ? "\"other\", W/\"missing\"" : $"\"other\", {strong}", later, strong is not null),
        ];
        foreach ( var item in cases )
        {
            using var request = new HttpRequestMessage( new HttpMethod( method ), path );
            request.Headers.TryAddWithoutValidation( "Range", item.Range );
            if ( item.NoneMatch is not null )
            {
                request.Headers.TryAddWithoutValidation( "If-None-Match", item.NoneMatch );
            }

            if ( item.Since is not null )
            {
                request.Headers.TryAddWithoutValidation( "If-Modified-Since", item.Since );
            }

            var response = await client.SendAsync( request );
            Assert.Equal( item.Fresh ? HttpStatusCode.NotModified : HttpStatusCode.PartialContent, response.Status );
            Assert.Equal( item.Fresh || method == "HEAD" ? "" : "0", response.Body );
            if ( item.Fresh )
            {
                Assert.Null( response.ContentType );
                Assert.Null( response.Raw( "Content-Length" ) );
                Assert.Null( response.Raw( "Content-Range" ) );
            }
        }
    }

    [Theory]
    [InlineData( "disk", "GET" )]
    [InlineData( "disk", "HEAD" )]
    [InlineData( "proxy", "GET" )]
    [InlineData( "proxy", "HEAD" )]
    public async Task Wildcard_ranges_do_not_bypass_signed_key_or_file_existence_checks( string kind, string method )
    {
        await using var factory = new SliceAppFactory( );
        var blob = await CreateAsync( factory );
        var path = PathFor( factory, blob, kind, factory.App.Now.AddMinutes( 5 ) );
        var expired = PathFor( factory, blob, kind, factory.App.Now.AddSeconds( -1 ) );
        var tampered = path.Replace( kind == "disk" ? "/disk/" : "/proxy/", kind == "disk" ? "/disk/tampered" : "/proxy/tampered", StringComparison.Ordinal );
        using var client = factory.Client( );
        foreach ( var rejected in new[] { tampered, expired } )
        {
            using var request = WildcardRange( method, rejected );
            Assert.Equal( HttpStatusCode.NotFound, (await client.SendAsync( request )).Status );
        }

        File.Delete( factory.App.Storage.Disk.PathFor( blob.Key ) );
        using var missing = WildcardRange( method, path );
        Assert.Equal( HttpStatusCode.NotFound, (await client.SendAsync( missing )).Status );

        if ( kind == "proxy" )
        {
            var deleted = await CreateAsync( factory );
            var deletedPath = PathFor( factory, deleted, kind, factory.App.Now.AddMinutes( 5 ) );
            factory.Execute( $"DELETE FROM active_storage_blobs WHERE id={deleted.Id}" );
            using var missingRecord = WildcardRange( method, deletedPath );
            Assert.Equal( HttpStatusCode.NotFound, (await client.SendAsync( missingRecord )).Status );
        }
    }

    [Theory]
    [InlineData( "GET" )]
    [InlineData( "HEAD" )]
    public async Task Representation_proxy_checks_the_processed_file_and_signatures_before_revalidation( string method )
    {
        await using var factory = new SliceAppFactory( );
        var moon = SliceFiles.Moon( );
        using var bytes = new MemoryStream( moon.Bytes );
        var app = factory.App;
        var blob = await app.Storage.CreateAndUploadAsync( bytes, new BlobFilename( moon.Name ), moon.Type, TestContext.Current.CancellationToken );
        var signedId = app.StorageVerifier.SignedBlobId( blob.Id );
        var variation = Variation.ResizeToLimit( 10, 10 );
        var key = variation.Key( app.StorageVerifier );
        var path = $"/rails/active_storage/representations/proxy/{signedId}/{Uri.EscapeDataString( key )}/{moon.Name}";
        using var client = factory.Client( );
        var original = await client.GetAsync( path );
        Assert.Equal( HttpStatusCode.OK, original.Status );
        Assert.NotEmpty( original.WireBytes );
        using var fresh = WildcardRange( method, path );
        var unchanged = await client.SendAsync( fresh );
        Assert.Equal( HttpStatusCode.NotModified, unchanged.Status );
        Assert.Empty( unchanged.Body );
        using var invalid = WildcardRange( method, path.Replace( Uri.EscapeDataString( key ), "invalid--0", StringComparison.Ordinal ) );
        Assert.Equal( HttpStatusCode.NotFound, (await client.SendAsync( invalid )).Status );
        var image = await app.Storage.ExistingVariantAsync( blob, BlobStorage.VariationFor( blob, variation ), TestContext.Current.CancellationToken );
        Assert.NotNull( image );
        File.Delete( app.Storage.Disk.PathFor( image.Key ) );
        using var missing = WildcardRange( method, path );
        Assert.Equal( HttpStatusCode.NotFound, (await client.SendAsync( missing )).Status );
    }

    [Theory]
    [InlineData( "avatar", "GET" )]
    [InlineData( "avatar", "HEAD" )]
    [InlineData( "bot-avatar", "GET" )]
    [InlineData( "bot-avatar", "HEAD" )]
    [InlineData( "logo", "GET" )]
    [InlineData( "logo", "HEAD" )]
    public async Task Public_image_validators_precede_ignored_ranges_and_keep_avatar_authorization( string kind, string method )
    {
        await using var factory = new SliceAppFactory( );
        using var client = factory.Client( );
        client.SignInAs( factory, SliceAppFactory.Id( "david" ) );
        var token = factory.App.SignedIds.Generate( "User", SliceAppFactory.Id( kind == "bot-avatar" ? "bender" : "kevin" ), SignedIds.AvatarPurpose, factory.App.Now.AddMinutes( 5 ) );
        var path = kind == "logo" ? Routes.AccountLogo : $"/users/{token}/avatar";
        var original = await client.GetAsync( path, "image/*" );
        Assert.Equal( HttpStatusCode.OK, original.Status );
        Assert.NotNull( original.Headers.ETag );
        foreach ( var range in new[] { "bytes=0-0", "bytes=invalid" } )
        {
            using var request = WildcardRange( method, path );
            request.Headers.Remove( "Range" );
            request.Headers.TryAddWithoutValidation( "Range", range );
            request.Headers.Remove( "If-None-Match" );
            request.Headers.TryAddWithoutValidation( "If-None-Match", "\"other\", " + original.Headers.ETag );
            var response = await client.SendAsync( request, "image/*" );
            Assert.Equal( HttpStatusCode.NotModified, response.Status );
            Assert.Empty( response.Body );
        }

        if ( kind != "logo" )
        {
            using var invalid = WildcardRange( method, "/users/invalid--0/avatar" );
            Assert.Equal( HttpStatusCode.NotFound, (await client.SendAsync( invalid, "image/*" )).Status );
            using var anonymous = factory.Client( );
            using var noSession = WildcardRange( method, path );
            Assert.Equal( HttpStatusCode.Found, (await anonymous.SendAsync( noSession, "image/*" )).Status );
        }
    }

    private static HttpRequestMessage WildcardRange( string method, string path )
    {
        var request = new HttpRequestMessage( new HttpMethod( method ), path );
        request.Headers.TryAddWithoutValidation( "Range", "bytes=0-0" );
        request.Headers.TryAddWithoutValidation( "If-None-Match", "*" );
        return request;
    }

    private static async Task<Blob> CreateAsync( SliceAppFactory factory )
    {
        using var bytes = new MemoryStream( "0123456789"u8.ToArray( ) );
        return await factory.App.Storage.CreateAndUploadAsync( bytes, new BlobFilename( "conditional.txt" ), "text/plain", TestContext.Current.CancellationToken );
    }

    private static string PathFor( SliceAppFactory factory, Blob blob, string kind, DateTimeOffset expires ) => kind == "disk"
        ? factory.App.Storage.Disk.UrlPath( factory.App.StorageVerifier, blob.Key, expires, blob.Filename, blob.ContentType, "inline" )
        : $"/rails/active_storage/blobs/proxy/{factory.App.StorageVerifier.SignedBlobId( blob.Id, expires )}/{blob.Filename}";
}
