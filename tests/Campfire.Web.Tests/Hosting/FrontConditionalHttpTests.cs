using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Tests.Hosting;

public sealed class FrontConditionalHttpTests
{
    private const string Modified = "Sat, 26 Sep 2026 12:00:00 GMT";
    private const string Later = "Sat, 26 Sep 2026 12:01:00 GMT";

    [Theory]
    [InlineData( "GET" )]
    [InlineData( "HEAD" )]
    public async Task Cold_and_warm_responses_share_conditional_validation_and_body_rules( string method )
    {
        ConditionalCase[] cases =
        [
            new( "\"one\"", "\"one\"", null, true ),
            new( "\"one\"", "W/\"one\"", null, true ),
            new( "W/\"one\"", "\"one\"", null, true ),
            new( "\"one\"", "\"other\", W/\"one\"", null, true ),
            new( "\"one,two\"", "\"other\", W/\"one,two\"", null, true ),
            new( "\"one\"", "*", null, true ),
            new( null, "*", null, true ),
            new( "\"one\"", null, Modified, true ),
            new( "\"one\"", null, Later, true ),
            new( "\"one\"", null, "Sat Sep 26 12:00:00 2026", true ),
            new( "\"one\"", null, "Sat, 26 Sep 2026 11:59:59 GMT", false ),
            new( "\"one\"", "\"other\"", Later, false ),
            new( "\"one\"", "\"ONE\"", Later, false ),
            new( "\"one\"", "invalid", Later, false ),
            new( "\"one\"", null, "invalid", false ),
            new( "\"one\"", "\"one\"", null, false, 302 ),
            new( null, "*", null, false, 404 ),
        ];
        var calls = 0;
        await using var server = await FrontTestServer.StartAsync( false, context =>
        {
            Interlocked.Increment( ref calls );
            var index = int.Parse( context.Request.Path.Value!.Split( '/' )[1], CultureInfo.InvariantCulture );
            var item = cases[index];
            context.Response.StatusCode = item.Status;
            context.Response.ContentType = "text/plain";
            context.Response.Headers.CacheControl = "public,max-age=300";
            context.Response.Headers.ETag = item.Etag;
            context.Response.Headers.LastModified = Modified;
            return context.Response.WriteAsync( "public body", context.RequestAborted );
        } );
        using var client = server.Client( );
        for ( var index = 0; index < cases.Length; index++ )
        {
            var item = cases[index];
            foreach ( var warm in new[] { false, true } )
            {
                var path = $"/{index}/{warm}";
                if ( warm )
                {
                    using var prime = await client.SendAsync( new HttpRequestMessage( new HttpMethod( method ), path ), TestContext.Current.CancellationToken );
                    Assert.Equal( item.Status, ( int ) prime.StatusCode );
                }

                var before = Volatile.Read( ref calls );
                using var request = new HttpRequestMessage( new HttpMethod( method ), path );
                if ( item.NoneMatch is { } noneMatch )
                {
                    request.Headers.TryAddWithoutValidation( "If-None-Match", noneMatch );
                }

                if ( item.Since is { } since )
                {
                    request.Headers.TryAddWithoutValidation( "If-Modified-Since", since );
                }

                using var response = await client.SendAsync( request, TestContext.Current.CancellationToken );
                Assert.Equal( item.NotModified ? HttpStatusCode.NotModified : ( HttpStatusCode ) item.Status, response.StatusCode );
                var cached = warm && item.Status < 400;
                Assert.Equal( cached ? "hit" : "miss", response.Headers.GetValues( "X-Cache" ).Single( ) );
                Assert.Equal( before + (cached ? 0 : 1), Volatile.Read( ref calls ) );
                var body = await response.Content.ReadAsByteArrayAsync( TestContext.Current.CancellationToken );
                if ( method == "HEAD" || item.NotModified )
                {
                    Assert.Empty( body );
                }
                else
                {
                    Assert.Equal( "public body", Encoding.UTF8.GetString( body ) );
                }

                if ( item.NotModified )
                {
                    Assert.Null( response.Content.Headers.ContentType );
                    Assert.False( response.Content.Headers.Contains( "Content-Length" ) );
                    Assert.Equal( item.Etag, response.Headers.ETag?.ToString( ) );
                    Assert.Equal( Modified, response.Content.Headers.LastModified?.ToString( "r", CultureInfo.InvariantCulture ) );
                    Assert.True( response.Headers.CacheControl!.Public );
                }
            }
        }
    }

    private sealed record ConditionalCase( string? Etag, string? NoneMatch, string? Since, bool NotModified, int Status = 200 );

    [Theory]
    [InlineData( "pipe", "GET" )]
    [InlineData( "file", "GET" )]
    [InlineData( "pipe", "HEAD" )]
    [InlineData( "file", "HEAD" )]
    public async Task Conditional_pipe_and_file_writes_keep_cold_and_warm_bodies_empty( string path, string method )
    {
        var file = Path.GetTempFileName( );
        await File.WriteAllTextAsync( file, "file body", TestContext.Current.CancellationToken );
        try
        {
            await using var server = await FrontTestServer.StartAsync( false, async context =>
            {
                context.Response.ContentType = "text/plain";
                context.Response.ContentLength = 9;
                context.Response.Headers.CacheControl = "public,max-age=300";
                context.Response.Headers.ETag = "\"body\"";
                if ( path == "file" )
                {
                    await context.Response.SendFileAsync( file, context.RequestAborted );
                }
                else
                {
                    await context.Response.BodyWriter.WriteAsync( "file body"u8.ToArray( ), context.RequestAborted );
                }
            } );
            using var client = server.Client( );
            foreach ( var warm in new[] { false, true } )
            {
                var url = $"/{warm}";
                if ( warm )
                {
                    using var prime = await client.SendAsync( new HttpRequestMessage( new HttpMethod( method ), url ), TestContext.Current.CancellationToken );
                    Assert.Equal( HttpStatusCode.OK, prime.StatusCode );
                }

                using var request = new HttpRequestMessage( new HttpMethod( method ), url );
                request.Headers.IfNoneMatch.ParseAdd( "\"body\"" );
                using var response = await client.SendAsync( request, TestContext.Current.CancellationToken );

                Assert.Equal( HttpStatusCode.NotModified, response.StatusCode );
                Assert.Equal( warm ? "hit" : "miss", response.Headers.GetValues( "X-Cache" ).Single( ) );
                Assert.Empty( await response.Content.ReadAsByteArrayAsync( TestContext.Current.CancellationToken ) );
                Assert.False( response.Content.Headers.Contains( "Content-Length" ) );
                Assert.Null( response.Content.Headers.ContentType );
            }
        }
        finally
        {
            File.Delete( file );
        }
    }
}
