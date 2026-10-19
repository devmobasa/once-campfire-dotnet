using System.Net;
using System.Text.Json.Nodes;
using Campfire.Application.Integrations.WebPush;
using Campfire.Application.Memberships;
using Campfire.Application.Pushes;
using Campfire.Application.Tests.Integrations.Support;
using Campfire.Application.Tests.Support;

namespace Campfire.Application.Tests.Integrations.WebPush;

/// <summary>
/// <c>WebPush::Notification#deliver</c> against a local push service over TLS: pinning, the gem's headers, which
/// endpoints are skipped, and which errors destroy the subscription (<c>WebPush::Pool#deliver</c>).
/// </summary>
public sealed class WebPushDeliveryTests
{
    private const string Endpoint = "https://fcm.googleapis.com/fcm/send/abc";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Delivers_to_the_pinned_address_with_the_gems_headers( )
    {
        await using var service = FakePushService.Start( );
        using var sender = service.Sender( new FrozenClock( DateTimeOffset.FromUnixTimeSeconds( 1_700_000_000 ) ) );
        using var receiver = new PushReceiver( );
        var notification = receiver.Notification( 1, Endpoint );

        Assert.Equal( 201, await sender.DeliverAsync( notification, Cancellation ) );

        Assert.Equal( ["fcm.googleapis.com"], service.Resolver.Lookups );
        Assert.Equal( [$"{FakePushService.PublicIp}:443"], service.Dialer.Dialed );
        var request = Assert.Single( service.Server.Received );
        Assert.Equal( ("POST", "/fcm/send/abc"), (request.Method, request.Target) );
        Assert.Equal(
            [
                "Ttl",
                "Urgency",
                "Authorization",
                "Accept-Encoding",
                "Accept",
                "User-Agent",
                "Connection",
                "Host",
                "Content-Type",
                "Content-Encoding",
                "Content-Length",
            ],
            request.Headers.Select( header => header.Key ) );
        var expected = JsonNode.Parse( IntegrationVectors.Text( "web_push_expected.json" ) )!;
        foreach ( var header in expected["headers"]!.AsArray( ) )
        {
            var (name, value) = (header![0]!.GetValue<string>( ), header[1]!.GetValue<string>( ));
            Assert.Equal( name == "Content-Length" ? request.Body.Length.ToString( ) : value, request.Header( name ) );
        }

        Assert.Equal(
            ("fcm.googleapis.com", "Ruby", "*/*", "close"),
            (request.Header( "Host" ), request.Header( "User-Agent" ), request.Header( "Accept" ), request.Header( "Connection" )) );
        var authorization = request.Header( "Authorization" )!;
        Assert.StartsWith( $"vapid t={expected["jwt_header_segment"]}.{expected["jwt_payload_segment"]}.", authorization, StringComparison.Ordinal );
        Assert.EndsWith( $",k={expected["authorization_k"]}", authorization, StringComparison.Ordinal );
        Assert.Equal( notification.EncodedMessage( ), receiver.Open( request.Body ) );
    }

    /// <summary>Only permitted <c>https://…:443</c> push services with a public address; nothing is sent to the others.</summary>
    [Theory]
    [InlineData( "https://updates.push.services.mozilla.com/wpush/v2/x" )]
    [InlineData( "https://web.push.apple.com/QaBC123" )]
    [InlineData( "https://attacker.example.com/collect" )]
    [InlineData( "https://fcm.googleapis.com:22/fcm/send/abc" )]
    [InlineData( "http://fcm.googleapis.com/fcm/send/abc" )]
    [InlineData( "https://evilfcm.googleapis.com.attacker.example/webhook" )]
    [InlineData( null )]
    public async Task Skips_endpoints_it_may_not_deliver_to( string? endpoint )
    {
        await using var service = FakePushService.Start( );
        service.Resolver.Set( "updates.push.services.mozilla.com", "10.0.0.5" );
        using var sender = service.Sender( );
        using var receiver = new PushReceiver( );

        Assert.Null( await sender.DeliverAsync( receiver.Notification( 1, endpoint! ), Cancellation ) );

        Assert.Empty( service.Server.Received );
        Assert.Empty( service.Dialer.Dialed );
    }

    /// <summary><c>test/models/push/subscription_test.rb</c>: validation resolves at the point of use, through the guard.</summary>
    [Fact]
    public async Task Resolves_endpoints_for_subscription_validation( )
    {
        await using var service = FakePushService.Start( );
        service.Resolver.Set( "updates.push.services.mozilla.com", "10.0.0.5" );
        using var sender = service.Sender( );

        async Task<string> ValidateAsync( string endpoint )
        {
            var address = await sender.ResolvedEndpointAddressAsync( endpoint, Cancellation );
            return PushEndpoints.Validate( endpoint, _ => address?.ToString( ) ).ToString( );
        }

        Assert.Equal( "", await ValidateAsync( Endpoint ) );
        Assert.Contains(
            "resolves to a private or invalid IP address",
            await ValidateAsync( "https://updates.push.services.mozilla.com/wpush/v2/x" ),
            StringComparison.Ordinal );
        Assert.Contains( "is not a permitted push service", await ValidateAsync( "https://attacker.example.com/collect" ), StringComparison.Ordinal );
        Assert.Equal( ["fcm.googleapis.com", "updates.push.services.mozilla.com"], service.Resolver.Lookups );
    }

    [Theory]
    [InlineData( 410, "Gone", "WebPush::ExpiredSubscription", true )]
    [InlineData( 404, "Not Found", "WebPush::InvalidSubscription", true )]
    [InlineData( 403, "Forbidden", "WebPush::Unauthorized", false )]
    [InlineData( 401, "Unauthorized", "WebPush::Unauthorized", false )]
    [InlineData( 400, "UnauthorizedRegistration", "WebPush::Unauthorized", false )]
    [InlineData( 400, "Bad Request", "WebPush::ResponseError", false )]
    [InlineData( 413, "Payload Too Large", "WebPush::PayloadTooLarge", false )]
    [InlineData( 429, "Too Many Requests", "WebPush::TooManyRequests", false )]
    [InlineData( 503, "Service Unavailable", "WebPush::PushServiceError", false )]
    [InlineData( 302, "Found", "WebPush::ResponseError", false )]
    public async Task Service_errors_keep_their_class_and_invalidate_only_gone_endpoints( int status, string reason, string rubyClass, bool invalidates )
    {
        await using var service = FakePushService.Start( status, reason );
        using var sender = service.Sender( );
        using var receiver = new PushReceiver( );

        var error = await Assert.ThrowsAsync<WebPushDeliveryException>( ( ) => sender.DeliverAsync( receiver.Notification( 1, Endpoint ), Cancellation ) );

        Assert.Equal( (rubyClass, invalidates, status), (error.RubyClass, error.InvalidatesSubscription, error.Status) );
    }

    /// <summary>Local key and TLS failures retain subscriptions.</summary>
    [Fact]
    public async Task Openssl_errors_keep_the_subscription( )
    {
        await using var service = FakePushService.Start( );
        using var sender = service.Sender( );
        var badKey = new WebPushNotification( "t", "b", "/", 0, 1, Endpoint, "dGVzdF9rZXk", "dGVzdF9hdXRo" );

        Assert.False( (await Assert.ThrowsAsync<WebPushDeliveryException>( ( ) => sender.DeliverAsync( badKey, Cancellation ) )).InvalidatesSubscription );

        using var untrusting = new WebPushSender( service.Network with
        {
            TrustedRoots = []
        }, FakePushService.Vapid );
        using var receiver = new PushReceiver( );
        var tls = await Assert.ThrowsAsync<WebPushDeliveryException>( ( ) => untrusting.DeliverAsync( receiver.Notification( 1, Endpoint ), Cancellation ) );
        Assert.Equal( ("OpenSSL::SSL::SSLError", false), (tls.RubyClass, tls.InvalidatesSubscription) );

        var blank = new WebPushNotification( "t", "b", "/", 0, 1, Endpoint, "", "dGVzdF9hdXRo" );
        var argument = await Assert.ThrowsAsync<WebPushDeliveryException>( ( ) => sender.DeliverAsync( blank, Cancellation ) );
        Assert.Equal( ("ArgumentError", false), (argument.RubyClass, argument.InvalidatesSubscription) );
    }

    /// <summary>A push service that keeps the connection waiting is given up on (Net::ReadTimeout), and the subscription kept.</summary>
    [Fact]
    public async Task Gives_up_on_a_silent_push_service( )
    {
        await using var silent = SlowServer.Silent( );
        var resolver = new FakeHostResolver( ).Set( "fcm.googleapis.com", FakePushService.PublicIp );
        var network = IntegrationVectors.Network( resolver, new MappingDialer( [IPAddress.Parse( FakePushService.PublicIp )], silent.Endpoint ) );
        using var sender = new WebPushSender( network, FakePushService.Vapid, null, TimeSpan.FromMilliseconds( 300 ), TimeSpan.FromSeconds( 30 ) );
        using var receiver = new PushReceiver( );

        var error = await Assert.ThrowsAsync<WebPushDeliveryException>( ( ) => sender.DeliverAsync( receiver.Notification( 1, Endpoint ), Cancellation ) );

        Assert.False( error.InvalidatesSubscription );
        Assert.StartsWith( "Net::", error.RubyClass, StringComparison.Ordinal );
    }

    [Fact]
    public async Task Sends_the_test_notification_with_a_random_body_and_the_badge( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        await using var service = FakePushService.Start( );
        using var sender = service.Sender( );
        using var receiver = new PushReceiver( );
        var subscription = new PushSubscription
        {
            Id = 7,
            UserId = TestDatabase.Id( "david" ),
            Endpoint = Endpoint,
            P256dhKey = receiver.P256dh,
            AuthKey = receiver.AuthKey,
        };
        var unread = await testDatabase.ReadAsync( db => MembershipQueries.UnreadCount( db, subscription.UserId ) );

        var notification = await testDatabase.ReadAsync( db => WebPushNotification.ForTest(
            db,
            subscription,
            "http://example.com/users/me/push_subscriptions" ) );
        await sender.DeliverAsync( notification, Cancellation );

        var message = JsonNode.Parse( receiver.Open( Assert.Single( service.Server.Received ).Body ) )!;
        Assert.Equal( "Campfire Test", message["title"]!.GetValue<string>( ) );
        Assert.True( Guid.TryParse( message["options"]!["body"]!.GetValue<string>( ), out _ ) );
        Assert.Equal( "/account/logo", message["options"]!["icon"]!.GetValue<string>( ) );
        Assert.Equal( "http://example.com/users/me/push_subscriptions", message["options"]!["data"]!["path"]!.GetValue<string>( ) );
        Assert.Equal( unread, message["options"]!["data"]!["badge"]!.GetValue<int>( ) );
    }

    /// <summary>A <see cref="TimeProvider"/> stopped at one instant.</summary>
    private sealed class FrozenClock( DateTimeOffset now ) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow( ) => now;
    }
}
