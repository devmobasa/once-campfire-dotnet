using System.IO.Compression;
using System.Net;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Tests.Hosting;

public sealed class FrontCacheHttpTests
{
    [Theory]
    [InlineData( "/users/1/avatar", "image/png" )]
    [InlineData( "/assets/application.css", "text/css" )]
    public async Task Public_hits_accept_browser_session_cookies_without_reentering_the_app( string path, string contentType )
    {
        var calls = 0;
        await using var server = await FrontTestServer.StartAsync( false, context =>
        {
            Interlocked.Increment( ref calls );
            context.Response.ContentType = contentType;
            context.Response.Headers.CacheControl = "public,max-age=300";
            return context.Response.WriteAsync( "public bytes", context.RequestAborted );
        } );
        using var client = server.Client( );
        for ( var attempt = 0; attempt < 2; attempt++ )
        {
            using var request = new HttpRequestMessage( HttpMethod.Get, path );
            request.Headers.Add( "Cookie", "session_token=opaque-browser-ticket" );
            using var response = await client.SendAsync( request, TestContext.Current.CancellationToken );
            Assert.Equal( HttpStatusCode.OK, response.StatusCode );
            Assert.Equal( attempt == 0 ? "miss" : "hit", response.Headers.GetValues( "X-Cache" ).Single( ) );
            Assert.Equal( "public bytes", await response.Content.ReadAsStringAsync( TestContext.Current.CancellationToken ) );
        }

        Assert.Equal( 1, Volatile.Read( ref calls ) );
    }

    [Theory]
    [InlineData( "gzip", true )]
    [InlineData( "gzip,deflate,br", true )]
    [InlineData( "gzip,identity;q=0", false )]
    [InlineData( "gzip,*;q=0", false )]
    public async Task Gzip_accepting_avatar_requests_reuse_identity_only_when_it_is_allowed( string accepted, bool reuse )
    {
        var calls = 0;
        byte[] avatar = Convert.FromBase64String( "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aD1sAAAAASUVORK5CYII=" );
        await using var server = await FrontTestServer.StartAsync( false, httpContext =>
        {
            Interlocked.Increment( ref calls );
            httpContext.Response.ContentType = "image/png";
            httpContext.Response.Headers.CacheControl = "public,max-age=300";
            return httpContext.Response.Body.WriteAsync( avatar, httpContext.RequestAborted ).AsTask( );
        } );
        using var client = server.Client( );
        for ( var attempt = 0; attempt < 2; attempt++ )
        {
            using var request = new HttpRequestMessage( HttpMethod.Get, "/users/1/avatar" );
            request.Headers.AcceptEncoding.ParseAdd( accepted );
            using var response = await client.SendAsync( request, TestContext.Current.CancellationToken );
            Assert.Equal( HttpStatusCode.OK, response.StatusCode );
            Assert.Equal( attempt == 1 && reuse ? "hit" : "miss", response.Headers.GetValues( "X-Cache" ).Single( ) );
            Assert.Empty( response.Content.Headers.ContentEncoding );
            Assert.Equal( avatar, await response.Content.ReadAsByteArrayAsync( TestContext.Current.CancellationToken ) );
        }

        Assert.Equal( reuse ? 1 : 2, Volatile.Read( ref calls ) );
    }

    [Theory]
    [InlineData( "GET" )]
    [InlineData( "HEAD" )]
    public async Task Gzip_cache_hits_on_get_and_head_preserve_not_modified_headers_and_empty_bodies( string method )
    {
        var calls = 0;
        await using var server = await FrontTestServer.StartAsync( false, httpContext =>
        {
            Interlocked.Increment( ref calls );
            httpContext.Response.ContentType = "text/plain";
            httpContext.Response.Headers.CacheControl = "public,max-age=300";
            httpContext.Response.Headers.ETag = "\"version-one\"";
            return httpContext.Response.WriteAsync( "public body", httpContext.RequestAborted );
        } );
        using var client = server.Client( );
        for ( var attempt = 0; attempt < 3; attempt++ )
        {
            using var request = new HttpRequestMessage( new HttpMethod( method ), "/cached" );
            request.Headers.AcceptEncoding.ParseAdd( "gzip" );
            if ( attempt == 2 )
            {
                request.Headers.IfNoneMatch.ParseAdd( "\"version-one\"" );
            }

            using var response = await client.SendAsync( request, TestContext.Current.CancellationToken );
            Assert.Equal( attempt == 0 ? "miss" : "hit", response.Headers.GetValues( "X-Cache" ).Single( ) );
            Assert.Equal( attempt == 2 ? HttpStatusCode.NotModified : HttpStatusCode.OK, response.StatusCode );
            Assert.Equal( "gzip", Assert.Single( response.Content.Headers.ContentEncoding ) );
            Assert.Contains( "Accept-Encoding", response.Headers.Vary );
            var bytes = await response.Content.ReadAsByteArrayAsync( TestContext.Current.CancellationToken );
            if ( method == "HEAD" || attempt == 2 )
            {
                Assert.Empty( bytes );
            }
            else
            {
                using var body = new MemoryStream( bytes );
                using var gzip = new GZipStream( body, CompressionMode.Decompress );
                using var reader = new StreamReader( gzip );
                Assert.Equal( "public body", await reader.ReadToEndAsync( TestContext.Current.CancellationToken ) );
            }

            if ( attempt == 2 )
            {
                Assert.Null( response.Content.Headers.ContentType );
                Assert.False( response.Content.Headers.Contains( "Content-Length" ) );
            }
        }

        Assert.Equal( 1, Volatile.Read( ref calls ) );
    }
}
