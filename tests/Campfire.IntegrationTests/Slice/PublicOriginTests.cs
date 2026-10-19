using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using Campfire.Application.Rooms;
using Campfire.IntegrationTests.Support;
using Campfire.Web.Channels;
using Campfire.Web.Hosting;
using Campfire.Web.Paths;
using Microsoft.AspNetCore.Hosting;

namespace Campfire.IntegrationTests.Slice;

/// <summary>
/// Host-derived URLs in shared markup. Rails builds the copy-link URL in
/// the cached message partial from the request's <c>Host</c>, and its cache key has no host, so one forged header
/// reaches everyone who reads the fragment. With Thruster's <c>TLS_DOMAIN</c> set, the port builds those URLs, and the
/// broadcasts', from <c>https://&lt;first domain&gt;</c>. Without it (plain HTTP, as in the parity harness) the
/// request's origin is used as in Rails, and the origin in the fragment key keeps it to that origin's own entries.
/// </summary>
public sealed class PublicOriginTests
{
    private const string PublicDomain = "chat.example.com";

    private const string ForgedHost = "evil.example";

    private static readonly long Designers = SliceAppFactory.Id( "designers" );

    private static readonly long Third = SliceAppFactory.Id( "third" );

    [Theory]
    [InlineData( null, "https://chat.example.com", true )]
    [InlineData( "http://public.example.com:8080", "http://public.example.com:8080", false )]
    [InlineData( "https://public.example.com:8443", "https://public.example.com:8443", true )]
    public async Task A_configured_origin_keeps_forged_hosts_out_of_cached_fragments_and_broadcasts( string? configured, string origin, bool tls )
    {
        await using var factory = new TlsDomainAppFactory( configured, tls );
        using var david = await factory.SignedInClientAsync( "david@37signals.com" );
        using var broadcasts = await RoomBroadcastTap.StartAsync( factory, Designers );
        var cachedBefore = factory.App.Fragments.Count;

        var forged = await GetWithHostAsync( david, Routes.Room( Designers ), ForgedHost );
        var cachedAfterForged = factory.App.Fragments.Count;
        var honest = await david.GetAsync( Routes.Room( Designers ) );
        var cachedAfterHonest = factory.App.Fragments.Count;
        var posted = await PostWithHostAsync( david, SliceClient.MetaToken( honest.Body ), ForgedHost, "Posted through a forged host" );
        var created = factory.Scalar<long>( "SELECT max(id) FROM messages" );
        var broadcast = await broadcasts.NextHtmlAsync( );

        Assert.Equal( HttpStatusCode.OK, forged.Status );
        Assert.Contains( CopyLink( origin, Third ), forged.Body, StringComparison.Ordinal );
        Assert.True( cachedAfterForged > cachedBefore, "the forged request rendered and stored the fragments" );
        Assert.Equal( cachedAfterForged, cachedAfterHonest );
        Assert.Contains( CopyLink( origin, Third ), honest.Body, StringComparison.Ordinal );
        Assert.DoesNotContain( ForgedHost, honest.Body, StringComparison.Ordinal );
        Assert.Equal( HttpStatusCode.OK, posted.Status );
        Assert.Contains( CopyLink( origin, created ), broadcast, StringComparison.Ordinal );
        Assert.DoesNotContain( ForgedHost, broadcast, StringComparison.Ordinal );
    }

    [Theory]
    [InlineData( null, "https://chat.example.com", true )]
    [InlineData( "http://public.example.com:8080", "http://public.example.com:8080", false )]
    [InlineData( "https://public.example.com:8443", "https://public.example.com:8443", true )]
    public async Task Cached_bot_api_json_uses_the_configured_public_origin( string? configured, string origin, bool tls )
    {
        await using var factory = new TlsDomainAppFactory( configured, tls );
        using var bot = factory.Client( );
        var watercooler = SliceAppFactory.Id( "watercooler" );

        var forged = await GetWithHostAsync( bot, Routes.RoomBotMessages( watercooler, BotApiKeys.Bender( factory ) ), ForgedHost, "application/json" );

        var last = JsonNode.Parse( forged.Body )!.AsArray( )[^1]!;
        Assert.Equal( HttpStatusCode.OK, forged.Status );
        Assert.Equal( $"{origin}/rooms/{watercooler}/messages/{last["id"]!.GetValue<long>( )}", last["url"]!.GetValue<string>( ) );
        Assert.StartsWith( $"{origin}/users/", last["creator"]!["avatar_url"]!.GetValue<string>( ), StringComparison.Ordinal );
    }

    [Theory]
    [InlineData( null, "chat.example.com", true )]
    [InlineData( "http://public.example.com:8080", "public.example.com", false )]
    [InlineData( "https://public.example.com:8443", "public.example.com", true )]
    public async Task Link_previews_are_judged_against_the_configured_public_host( string? configured, string host, bool tls )
    {
        await using var factory = new TlsDomainAppFactory( configured, tls );

        var (forged, detached) = await factory.App.Database.ReadAsync(
            db => (factory.App.RichText( db, ForgedHost ).RequestHost, factory.App.RichText( db, requestHost: null ).RequestHost),
            TestContext.Current.CancellationToken );

        Assert.Equal( host, forged );
        Assert.Null( detached );
    }

    [Fact]
    public async Task Without_tls_domain_posted_and_broadcast_copy_links_keep_the_request_port( )
    {
        await using var factory = new SliceAppFactory( );
        using var david = await factory.SignedInClientAsync( "david@37signals.com" );
        using var broadcasts = await RoomBroadcastTap.StartAsync( factory, Designers );
        var page = await david.GetAsync( Routes.Room( Designers ) );
        var origin = $"http://127.0.0.1:{factory.Port}";

        var posted = await PostWithHostAsync( david, SliceClient.MetaToken( page.Body ), $"127.0.0.1:{factory.Port}", "A message with a port-aware copy link" );
        var created = factory.Scalar<long>( "SELECT max(id) FROM messages" );
        var broadcast = await broadcasts.NextHtmlAsync( );

        Assert.Equal( HttpStatusCode.OK, posted.Status );
        Assert.Contains( CopyLink( origin, created ), posted.Body, StringComparison.Ordinal );
        Assert.Contains( CopyLink( origin, created ), broadcast, StringComparison.Ordinal );
    }

    [Fact]
    public async Task Without_tls_domain_urls_follow_the_request_as_in_rails_and_the_key_keeps_them_apart( )
    {
        await using var factory = new SliceAppFactory( );
        using var david = await factory.SignedInClientAsync( "david@37signals.com" );
        using var broadcasts = await RoomBroadcastTap.StartAsync( factory, Designers );

        var forged = await GetWithHostAsync( david, Routes.Room( Designers ), ForgedHost );
        var cachedAfterForged = factory.App.Fragments.Count;
        var honest = await david.GetAsync( Routes.Room( Designers ) );
        var cachedAfterHonest = factory.App.Fragments.Count;
        var posted = await PostWithHostAsync( david, SliceClient.MetaToken( honest.Body ), ForgedHost, "Posted through a forged host" );
        var created = factory.Scalar<long>( "SELECT max(id) FROM messages" );
        var broadcast = await broadcasts.NextHtmlAsync( );

        Assert.Contains( CopyLink( $"http://{ForgedHost}", Third ), forged.Body, StringComparison.Ordinal );
        Assert.Contains( CopyLink( $"http://127.0.0.1:{factory.Port}", Third ), honest.Body, StringComparison.Ordinal );
        Assert.DoesNotContain( ForgedHost, honest.Body, StringComparison.Ordinal );
        Assert.True( cachedAfterHonest > cachedAfterForged, "each origin fills its own entries" );

        // The exposure that remains without TLS_DOMAIN, as in Rails: the poster's Host reaches every member.
        Assert.Contains( CopyLink( $"http://{ForgedHost}", created ), broadcast, StringComparison.Ordinal );
    }

    private static string CopyLink( string origin, long messageId ) =>
        $"data-copy-to-clipboard-content-value=\"{origin}/rooms/{Designers}/@{messageId}\"";

    private static Task<SliceReply> GetWithHostAsync( SliceClient client, string path, string host, string accept = "text/html,application/xhtml+xml" )
    {
        var request = new HttpRequestMessage( HttpMethod.Get, path );
        request.Headers.Host = host;
        return client.SendAsync( request, accept );
    }

    /// <summary>What the composer posts, with <paramref name="host"/> in place of the server's own.</summary>
    private static Task<SliceReply> PostWithHostAsync( SliceClient client, string csrfToken, string host, string body )
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent( body ), "message[body]" },
            { new StringContent( Guid.NewGuid( ).ToString( ) ), "message[client_message_id]" },
        };
        var request = new HttpRequestMessage( HttpMethod.Post, Routes.RoomMessages( Designers ) ) { Content = content };
        request.Headers.Host = host;
        request.Headers.Add( BrowserHeaders.CsrfToken, csrfToken );
        return client.SendAsync( request, SliceClient.TurboStreamAccept );
    }

    /// <summary>The app as Thruster runs it with TLS: <c>TLS_DOMAIN</c> names the public host.</summary>
    private sealed class TlsDomainAppFactory( string? origin = null, bool tls = true ) : SliceAppFactory
    {
        protected override void ConfigureWebHost( IWebHostBuilder builder )
        {
            base.ConfigureWebHost( builder );
            builder.UseSetting( CampfireOptions.TlsDomainVariable, tls ? $" {PublicDomain}, other.example.com" : "" );
            builder.UseSetting( "CAMPFIRE_PUBLIC_ORIGIN", origin );
        }
    }

    /// <summary>The HTML of each broadcast on a room's message stream, as the in-process pub/sub carries it.</summary>
    private sealed class RoomBroadcastTap : IDisposable
    {
        private readonly ConcurrentQueue<string> _payloads = new( );
        private IDisposable? _subscription;

        public static async Task<RoomBroadcastTap> StartAsync( SliceAppFactory factory, long roomId )
        {
            var room = await factory.App.Database.ReadAsync( db => RoomQueries.FindById( db, roomId )!, TestContext.Current.CancellationToken );
            var tap = new RoomBroadcastTap( );
            var stream = string.Join( ':', RoomBroadcasts.RoomMessages( room ) );
            tap._subscription = factory.App.Cable.Hub.Subscribe( stream, payload => tap._payloads.Enqueue( payload ) );
            return tap;
        }

        public async Task<string> NextHtmlAsync( )
        {
            await SliceWait.UntilAsync( ( ) => !_payloads.IsEmpty );
            Assert.True( _payloads.TryDequeue( out var payload ) );
            return JsonNode.Parse( payload )!.GetValue<string>( );
        }

        public void Dispose( ) => _subscription?.Dispose( );
    }
}
