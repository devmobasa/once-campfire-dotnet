using System.IO.Compression;
using System.Net;
using Campfire.IntegrationTests.Support;
using Campfire.Web.Paths;
using Campfire.Web.Tests.Cable.Support;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Campfire.IntegrationTests;

/// <summary>
/// The real server: Kestrel on <c>PORT</c> over plain HTTP, no <c>Server</c> header, a real-clock
/// <c>Date</c>, the peer address as <c>REMOTE_ADDR</c>, HEAD without a body, and chunked gzip.
/// </summary>
public sealed class KestrelTests( KestrelAppFactory factory ) : IClassFixture<KestrelAppFactory>
{
    private const string HealthBody = "<!DOCTYPE html><html><body style=\"background-color: green\"></body></html>";

    [Fact]
    public async Task The_app_listens_on_port_without_a_server_header_and_with_a_real_date( )
    {
        using var client = factory.Client( );

        using var response = await client.GetAsync( new Uri( Routes.RailsHealthCheck, UriKind.Relative ), TestContext.Current.CancellationToken );

        Assert.Equal( HttpStatusCode.OK, response.StatusCode );
        Assert.Equal( "text/html", response.Content.Headers.ContentType?.MediaType );
        Assert.Equal( HealthBody, await response.Content.ReadAsStringAsync( TestContext.Current.CancellationToken ) );
        Assert.Empty( response.Headers.Server );
        Assert.NotNull( response.Headers.Date );
        Assert.True( Math.Abs( (response.Headers.Date!.Value - DateTimeOffset.UtcNow).TotalMinutes ) < 5, "Date stays on the real clock" );
        Assert.Equal( factory.Port, factory.BaseAddress.Port );
        var addresses = factory.Services.GetRequiredService<IServer>( ).Features.Get<IServerAddressesFeature>( )!;
        Assert.Equal( $"http://[::]:{factory.Port}", Assert.Single( addresses.Addresses ) );
    }

    [Fact]
    public async Task Head_has_headers_and_no_body( )
    {
        using var client = factory.Client( );
        using var request = new HttpRequestMessage( HttpMethod.Head, new Uri( Routes.RailsHealthCheck, UriKind.Relative ) );

        using var response = await client.SendAsync( request, TestContext.Current.CancellationToken );

        Assert.Equal( HttpStatusCode.OK, response.StatusCode );
        Assert.Empty( await response.Content.ReadAsByteArrayAsync( TestContext.Current.CancellationToken ) );
        Assert.Equal( "text/html", response.Content.Headers.ContentType?.MediaType );
        Assert.Null( response.Headers.ETag );
    }

    [Fact]
    public async Task Gzip_goes_out_chunked( )
    {
        using var client = factory.Client( );
        using var request = new HttpRequestMessage( HttpMethod.Get, new Uri( EndpointPaths.Campfire.RecedeHistoricalLocation, UriKind.Relative ) );
        request.Headers.AcceptEncoding.ParseAdd( "gzip" );

        using var response = await client.SendAsync( request, TestContext.Current.CancellationToken );
        var body = await response.Content.ReadAsByteArrayAsync( TestContext.Current.CancellationToken );

        Assert.Equal( ["gzip"], response.Content.Headers.ContentEncoding );
        Assert.True( response.Headers.TransferEncodingChunked );
        Assert.Equal( "Going back…", Gunzip( body ) );
    }

    [Theory]
    [InlineData( "FOO", Routes.RailsHealthCheck, 405 )]
    [InlineData( "PROPFIND", Routes.RailsHealthCheck, 405 )]
    [InlineData( "FOO", "/nope", 404 )]
    [InlineData( "get", Routes.RailsHealthCheck, 200 )]
    public async Task Wire_method_statuses_follow_native_routing( string method, string path, int expectedStatus )
    {
        // HttpClient normalizes known lowercase verbs. This sends the actual wire spelling.
        var response = await RawHttp.SendAsync( factory.BaseAddress, method, path, ("Accept-Encoding", "identity") );

        Assert.Equal( expectedStatus, response.Status );
        if ( expectedStatus == 405 )
        {
            var allow = response.Headers["allow"].Split( ',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries );
            Assert.Contains( "GET", allow );
            Assert.Contains( "HEAD", allow );
        }
        else
        {
            Assert.False( response.Headers.ContainsKey( "allow" ) );
        }

        if ( method == "get" )
        {
            Assert.Equal( HealthBody, response.Body );
        }
    }

    private static string Gunzip( byte[] bytes )
    {
        using var input = new MemoryStream( bytes );
        using var gzip = new GZipStream( input, CompressionMode.Decompress );
        using var reader = new StreamReader( gzip );
        return reader.ReadToEnd( );
    }
}
