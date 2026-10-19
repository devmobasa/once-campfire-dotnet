using System.Net;
using Campfire.Application.Storage;
using Campfire.IntegrationTests.Support;

namespace Campfire.IntegrationTests.Slice;

public sealed class DiskConditionalHttpTests
{
    [Theory]
    [InlineData( "GET" )]
    [InlineData( "HEAD" )]
    public async Task A_disk_download_ignores_matching_dates_when_if_none_match_disagrees( string method )
    {
        await using var factory = new SliceAppFactory( );
        using var bytes = new MemoryStream( "disk bytes"u8.ToArray( ) );
        var app = factory.App;
        var blob = await app.Storage.CreateAndUploadAsync( bytes, new BlobFilename( "conditional.txt" ), "text/plain", TestContext.Current.CancellationToken );
        var path = app.Storage.Disk.UrlPath( app.StorageVerifier, blob.Key, app.Now.AddMinutes( 5 ), blob.Filename, blob.ContentType, "inline" );
        using var client = factory.Client( );
        var original = await client.GetAsync( path );
        Assert.Equal( HttpStatusCode.OK, original.Status );
        Assert.NotNull( original.ContentHeaders.LastModified );
        using var request = new HttpRequestMessage( new HttpMethod( method ), path );
        request.Headers.IfModifiedSince = original.ContentHeaders.LastModified;
        request.Headers.TryAddWithoutValidation( "If-None-Match", "\"other\"" );

        var response = await client.SendAsync( request );

        Assert.Equal( HttpStatusCode.OK, response.Status );
        Assert.Equal( method == "HEAD" ? "" : "disk bytes", response.Body );
    }
}
