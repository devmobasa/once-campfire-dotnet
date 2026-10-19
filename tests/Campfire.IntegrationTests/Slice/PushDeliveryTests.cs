using System.Net;
using System.Text.Json.Nodes;
using Campfire.IntegrationTests.Support;
using Campfire.Web.Paths;

namespace Campfire.IntegrationTests.Slice;

/// <summary>
/// <c>Users::PushSubscriptions::TestNotificationsController</c> and a message's push fan-out, delivered for real to a
/// synthetic push service over TLS: the endpoint's host resolves to a fake public address that
/// <see cref="SliceNetwork"/> routes to a loopback server, and the test decrypts what arrives with the subscriber's keys.
/// </summary>
public sealed class PushDeliveryTests
{
    private static readonly long David = SliceAppFactory.Id( "david" );

    private static readonly long DavidChrome = SliceAppFactory.Id( "david_chrome" );

    [Fact]
    public async Task A_test_notification_is_encrypted_signed_and_sent_to_the_pinned_address( )
    {
        await using var factory = new VapidSliceAppFactory( );
        using var subscriber = new PushSubscriber( );
        await using var service = await LoopbackServer.StartAsync( tls: true, httpContext =>
        {
            httpContext.Response.StatusCode = StatusCodes.Status201Created;
            return Task.CompletedTask;
        } );
        factory.Network.Answer( "fcm.googleapis.com", PushSubscriber.PublicIp ).Route( PushSubscriber.PublicIp, service.Endpoint );
        factory.Execute(
            "UPDATE push_subscriptions SET endpoint = 'https://fcm.googleapis.com/fcm/send/e2e', "
            + $"p256dh_key = '{subscriber.P256dh}', auth_key = '{subscriber.AuthKey}' WHERE id = {DavidChrome}" );
        using var david = await factory.SignedInClientAsync( "david@37signals.com" );
        var token = await david.CsrfTokenAsync( Routes.UserPushSubscriptions );

        var sent = await david.SubmitFormAsync( HttpMethod.Post, Routes.UserPushSubscriptionTestNotifications( DavidChrome ), token );

        var request = Assert.Single( service.Requests );
        var payload = JsonNode.Parse( subscriber.Open( request.Body ) )!;
        var unreadRooms = factory.Scalar<long>( $"SELECT count(*) FROM memberships WHERE user_id = {David} AND unread_at IS NOT NULL" );
        Assert.Equal( HttpStatusCode.Found, sent.Status );
        Assert.Equal( $"{factory.BaseAddress}users/me/push_subscriptions", sent.Location );
        Assert.Equal( [$"{PushSubscriber.PublicIp}:443"], factory.Network.Dialed );
        Assert.Equal( ("POST", "fcm.googleapis.com", "/fcm/send/e2e"), (request.Method, request.Host, request.PathAndQuery) );
        Assert.Equal( "aes128gcm", request.Headers["Content-Encoding"] );
        Assert.Equal( "2419200", request.Headers["TTL"] );
        Assert.Equal( "high", request.Headers["Urgency"] );
        Assert.StartsWith( "vapid t=", request.Headers["Authorization"], StringComparison.Ordinal );
        Assert.Contains( ",k=", request.Headers["Authorization"], StringComparison.Ordinal );
        Assert.Equal( "Campfire Test", payload["title"]!.GetValue<string>( ) );
        Assert.True( Guid.TryParse( payload["options"]!["body"]!.GetValue<string>( ), out _ ) );
        Assert.Equal( "/account/logo", payload["options"]!["icon"]!.GetValue<string>( ) );
        Assert.Equal( $"{factory.BaseAddress}users/me/push_subscriptions", payload["options"]!["data"]!["path"]!.GetValue<string>( ) );
        Assert.Equal( unreadRooms, payload["options"]!["data"]!["badge"]!.GetValue<long>( ) );
    }

    [Fact]
    public async Task A_test_notification_to_a_privately_resolving_endpoint_is_not_sent( )
    {
        await using var factory = new VapidSliceAppFactory( );
        factory.Network.Answer( "fcm.googleapis.com", "10.1.2.3" );
        using var david = await factory.SignedInClientAsync( "david@37signals.com" );
        var token = await david.CsrfTokenAsync( Routes.UserPushSubscriptions );
        var jasonChrome = SliceAppFactory.Id( "jason_chrome" );

        var sent = await david.SubmitFormAsync( HttpMethod.Post, Routes.UserPushSubscriptionTestNotifications( DavidChrome ), token );
        var others = await david.SubmitFormAsync( HttpMethod.Post, Routes.UserPushSubscriptionTestNotifications( jasonChrome ), token );

        Assert.Equal( HttpStatusCode.Found, sent.Status );
        Assert.Equal( ["fcm.googleapis.com"], factory.Network.Lookups );
        Assert.Empty( factory.Network.Dialed );
        Assert.Equal( HttpStatusCode.NotFound, others.Status );
    }

    /// <summary>The controller doesn't rescue what the gem raises: an expired subscription is a 500, and the row stays.</summary>
    [Fact]
    public async Task A_push_service_that_answers_gone_fails_the_request( )
    {
        await using var factory = new VapidSliceAppFactory( );
        using var subscriber = new PushSubscriber( );
        await using var service = await LoopbackServer.StartAsync( tls: true, httpContext =>
        {
            httpContext.Response.StatusCode = StatusCodes.Status410Gone;
            return Task.CompletedTask;
        } );
        factory.Network.Answer( "fcm.googleapis.com", PushSubscriber.PublicIp ).Route( PushSubscriber.PublicIp, service.Endpoint );
        factory.Execute( $"UPDATE push_subscriptions SET p256dh_key = '{subscriber.P256dh}', auth_key = '{subscriber.AuthKey}' WHERE id = {DavidChrome}" );
        using var david = await factory.SignedInClientAsync( "david@37signals.com" );
        var token = await david.CsrfTokenAsync( Routes.UserPushSubscriptions );

        var sent = await david.SubmitFormAsync( HttpMethod.Post, Routes.UserPushSubscriptionTestNotifications( DavidChrome ), token );

        Assert.Equal( HttpStatusCode.InternalServerError, sent.Status );
        Assert.Single( service.Requests );
        Assert.Equal( 1, factory.Scalar<long>( $"SELECT count(*) FROM push_subscriptions WHERE id = {DavidChrome}" ) );
    }

    /// <summary>
    /// <c>Room::PushMessageJob</c> end to end: the members who follow everything get the message, decryptable with their
    /// keys. Local encryption errors keep the affected subscriptions.
    /// </summary>
    [Fact]
    public async Task A_new_message_is_delivered_and_local_key_errors_keep_subscriptions( )
    {
        await using var factory = new VapidSliceAppFactory( );
        using var jason = new PushSubscriber( );
        await using var service = await LoopbackServer.StartAsync( tls: true, httpContext =>
        {
            httpContext.Response.StatusCode = StatusCodes.Status201Created;
            return Task.CompletedTask;
        } );
        factory.Network.Answer( "fcm.googleapis.com", PushSubscriber.PublicIp ).Route( PushSubscriber.PublicIp, service.Endpoint );
        var designers = SliceAppFactory.Id( "designers" );
        var jasonChrome = SliceAppFactory.Id( "jason_chrome" );
        var jzChrome = SliceAppFactory.Id( "jz_chrome" );
        factory.Execute( $"UPDATE push_subscriptions SET p256dh_key = '{jason.P256dh}', auth_key = '{jason.AuthKey}' WHERE id = {jasonChrome}" );
        using var david = await factory.SignedInClientAsync( "david@37signals.com" );

        await david.PostMessageFromRoomPageAsync( designers, "<p>Push me</p>", "push-me" );
        await SliceWait.UntilAsync( ( ) => factory.App.Jobs.PushMessage.Counters.Performed == 1 );
        var pool = factory.Services.GetRequiredService<Campfire.Application.Integrations.WebPush.WebPushPool>( );
        await SliceWait.UntilAsync( ( ) => pool.Pending == 0 );

        var request = Assert.Single( service.Requests );
        var payload = JsonNode.Parse( jason.Open( request.Body ) )!;
        Assert.Equal( "/fcm/send/567", request.PathAndQuery );
        Assert.Equal( "Designers", payload["title"]!.GetValue<string>( ) );
        Assert.Equal( "David: Push me", payload["options"]!["body"]!.GetValue<string>( ) );
        Assert.Equal( $"/rooms/{designers}", payload["options"]!["data"]!["path"]!.GetValue<string>( ) );
        Assert.Equal( 1, factory.Scalar<long>( $"SELECT count(*) FROM push_subscriptions WHERE id = {jasonChrome}" ) );
        Assert.Equal( 1, factory.Scalar<long>( $"SELECT count(*) FROM push_subscriptions WHERE id = {jzChrome}" ) );
    }
}
