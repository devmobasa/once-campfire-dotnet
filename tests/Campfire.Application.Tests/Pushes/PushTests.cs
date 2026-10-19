using Campfire.Application.Data.Hooks;
using Campfire.Application.Integrations.WebPush;
using Campfire.Application.Memberships;
using Campfire.Application.Pushes;
using Campfire.Application.Tests.Support;

namespace Campfire.Application.Tests.Pushes;

/// <summary>
/// <c>test/models/room/push_test.rb</c> (who gets pushed; delivery is the web layer's) and
/// <c>test/models/push/subscription_test.rb</c> (validation and endpoint pinning).
/// </summary>
public sealed class PushTests
{
    private const string WebPushPublicTestIp = "142.250.185.206";

    private static string? Public( string host ) => WebPushPublicTestIp;

    private static string? Private( string host ) => null;

    private static async Task<int> DeliveriesAsync( TestDatabase testDatabase, string room, string body )
    {
        var message = await testDatabase.PostMessageAsync( room, "david", body, clientMessageId: "earth" );
        var push = await testDatabase.ReadAsync( db => PushSubscriptionOperations.PushFor( db, testDatabase.BodyText, message.Id, testDatabase.Clock.Now ) );
        return push.Everything.Count + push.Mentions.Count;
    }

    [Fact]
    public async Task Delivers_a_new_message_to_other_room_users_with_push_subscriptions( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        var all = await testDatabase.CountAsync( "push_subscriptions" );

        Assert.Equal( all - 1, await DeliveriesAsync( testDatabase, "hq", "This is from earth" ) );
    }

    [Fact]
    public async Task Banned_users_keep_subscriptions_without_receiving_room_content( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        var userId = TestDatabase.Id( "jason" );
        var message = await testDatabase.PostMessageAsync( "designers", "david", "Private room content" );
        var before = await testDatabase.ReadAsync( db => PushSubscriptionOperations.PushFor( db, testDatabase.BodyText, message.Id, testDatabase.Clock.Now ) );
        Assert.Contains( before.Everything, subscription => subscription.UserId == userId );
        var subscriptions = await testDatabase.ReadAsync( db => PushSubscriptionOperations.ForUser( db, userId ) );
        Assert.NotEmpty( subscriptions );
        await testDatabase.ExecuteAsync( "UPDATE users SET status='banned' WHERE id=@p0", userId );

        var after = await testDatabase.ReadAsync( db => PushSubscriptionOperations.PushFor( db, testDatabase.BodyText, message.Id, testDatabase.Clock.Now ) );

        Assert.DoesNotContain( after.Everything.Concat( after.Mentions ), subscription => subscription.UserId == userId );
        Assert.Equal( subscriptions.Select( subscription => subscription.Id ),
            (await testDatabase.ReadAsync( db => PushSubscriptionOperations.ForUser( db, userId ) )).Select( subscription => subscription.Id ) );
    }

    [Fact]
    public async Task Notifies_subscribed_users_and_mentioned_ones( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );

        Assert.Equal( 2, await DeliveriesAsync( testDatabase, "designers", "This is from earth" ) );
        Assert.Equal( 3, await DeliveriesAsync( testDatabase, "designers", $"Hey {BasicBodyText.MentionAttachmentFor( TestDatabase.Id( "kevin" ) )}" ) );
    }

    [Fact]
    public async Task Does_not_notify_for_connected_rooms( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        await testDatabase.WriteAsync( scope => MembershipPresence.Connected( scope, TestDatabase.Id( "kevin_designers" ) ) );

        Assert.Equal( 2, await DeliveriesAsync( testDatabase, "designers", $"Hey {BasicBodyText.MentionAttachmentFor( TestDatabase.Id( "kevin" ) )}" ) );
    }

    [Fact]
    public async Task Does_not_notify_for_invisible_rooms( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        await testDatabase.WriteAsync( scope => MembershipOperations.UpdateInvolvement( scope, TestDatabase.Id( "kevin_designers" ), Involvement.Invisible ) );

        Assert.Equal( 2, await DeliveriesAsync( testDatabase, "designers", $"Hey {BasicBodyText.MentionAttachmentFor( TestDatabase.Id( "kevin" ) )}" ) );
    }

    [Fact]
    public async Task Payloads_show_the_room_or_the_sender( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        async Task<PushPayload> PayloadAsync( string room )
        {
            var message = await testDatabase.PostMessageAsync( room, "david", "Hi" );
            return Assert.IsType<PushPayload>( (await testDatabase.ReadAsync( db => PushSubscriptionOperations.PushFor(
                db,
                testDatabase.BodyText,
                message.Id,
                testDatabase.Clock.Now ) )).Payload );
        }

        Assert.Equal( new PushPayload( "Designers", "David: Hi", $"/rooms/{TestDatabase.Id( "designers" )}" ), await PayloadAsync( "designers" ) );
        Assert.Equal( new PushPayload( "David", "Hi", $"/rooms/{TestDatabase.Id( "david_and_jason" )}" ), await PayloadAsync( "david_and_jason" ) );
    }

    [Fact]
    public async Task The_badge_is_the_unread_room_count( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );

        await DeliveriesAsync( testDatabase, "designers", "unread" );

        Assert.Equal( 1, await testDatabase.ReadAsync( db => MembershipQueries.UnreadCount( db, TestDatabase.Id( "kevin" ) ) ) );
    }

    [Theory]
    [InlineData( "unsubscribed" )]
    [InlineData( "invisible" )]
    [InlineData( "mentions-only" )]
    public async Task Push_payload_is_not_rendered_without_eligible_recipients( string state )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        var message = await testDatabase.PostMessageAsync( "designers", "david", "No notification needed" );
        if ( state == "unsubscribed" )
        {
            await testDatabase.ExecuteAsync( "DELETE FROM push_subscriptions" );
        }
        else
        {
            await testDatabase.ExecuteAsync( "UPDATE memberships SET involvement=@p0", state == "invisible" ? Involvement.Invisible : Involvement.Mentions );
        }

        var bodyText = new CountingBodyText( testDatabase.BodyText );
        var (_, notifications) = await testDatabase.ReadAsync( db => MessagePusher.Notifications( db, bodyText, message.Id, testDatabase.Clock.Now ) );

        Assert.Empty( notifications );
        Assert.Equal( 0, bodyText.PlainTextCalls );
    }

    private sealed class CountingBodyText( IMessageBodyText inner ) : IMessageBodyText
    {
        public int PlainTextCalls { get; private set; }

        public string ToPlainText( string storedBody, Func<long, string?> userName )
        {
            PlainTextCalls++;
            return inner.ToPlainText( storedBody, userName );
        }

        public IReadOnlyList<long> MentionedUserIds( string storedBody ) => inner.MentionedUserIds( storedBody );
    }

    [Fact]
    public async Task Each_notification_carries_its_subscribers_unread_room_count( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        await testDatabase.ExecuteAsync( "UPDATE memberships SET unread_at = '2026-01-01 00:00:00' WHERE user_id = @p0", TestDatabase.Id( "jason" ) );
        var message = await testDatabase.PostMessageAsync( "hq", "david", "Hi" );

        var (_, notifications) = await testDatabase.ReadAsync( db => MessagePusher.Notifications(
            db,
            testDatabase.BodyText,
            message.Id,
            testDatabase.Clock.Now ) );

        Assert.NotEmpty( notifications );
        foreach ( var notification in notifications )
        {
            var userId = await testDatabase.ScalarAsync<long>( "SELECT user_id FROM push_subscriptions WHERE id = @p0", notification.SubscriptionId );
            Assert.Equal( await testDatabase.ReadAsync( db => MembershipQueries.UnreadCount( db, userId ) ), notification.Badge );
        }

        Assert.Contains( notifications, notification => notification.Badge > 1 );
    }

    [Theory]
    [InlineData( "https://fcm.googleapis.com/fcm/send/token123" )]
    [InlineData( "https://jmt17.google.com/fcm/send/token123" )]
    [InlineData( "https://updates.push.services.mozilla.com/wpush/v2/token123" )]
    [InlineData( "https://web.push.apple.com/QaBC123" )]
    [InlineData( "https://wns2-db5p.notify.windows.com/w/?token=abc123" )]
    public void Accepts_every_permitted_push_service( string endpoint ) => Assert.True( PushEndpoints.Validate( endpoint, Public ).IsEmpty );

    [Theory]
    [InlineData( "http://fcm.googleapis.com/fcm/send/abc123", "must use HTTPS" )]
    [InlineData( "https://attacker.example.com/webhook", "is not a permitted push service" )]
    [InlineData( "https://evilfcm.googleapis.com.attacker.example/webhook", "is not a permitted push service" )]
    [InlineData( "https://fcm.googleapis.com:8443/fcm/send/abc123", "must use the default HTTPS port" )]
    [InlineData( "", "can't be blank" )]
    [InlineData( "not a url", "is not a valid URL" )]
    public void Rejects_bad_endpoints( string endpoint, string error ) =>
        Assert.Contains( error, PushEndpoints.Validate( endpoint, Public ).ErrorsOn( "endpoint" ) );

    [Fact]
    public void Rejects_an_endpoint_that_resolves_privately_and_pins_public_ones( )
    {
        const string Endpoint = "https://fcm.googleapis.com/fcm/send/abc123";

        Assert.Contains( "resolves to a private or invalid IP address", PushEndpoints.Validate( Endpoint, Private ).ErrorsOn( "endpoint" ) );
        Assert.Null( PushEndpoints.ResolvedIp( Endpoint, Private ) );
        Assert.Equal( WebPushPublicTestIp, PushEndpoints.ResolvedIp( Endpoint, Public ) );
        Assert.Null( PushEndpoints.ResolvedIp( "https://attacker.example.com/collect", Public ) );
        Assert.Null( PushEndpoints.ResolvedIp( "https://fcm.googleapis.com:22/fcm/send/abc123", Public ) );
    }

    [Fact]
    public async Task Subscribing_creates_touches_or_rejects( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        var david = TestDatabase.Id( "david" );
        const string Endpoint = "https://fcm.googleapis.com/x";

        static bool IsValid( string? endpoint ) => PushEndpoints.Validate( endpoint, Public ).IsEmpty;
        Task<SubscribeOutcome> RegisterAsync( string endpoint ) =>
            testDatabase.WriteAsync( scope => PushSubscriptionRegistration.Register(
                scope,
                david,
                new PushSubscriptionParams( endpoint, "k", "a" ),
                "ua",
                IsValid ) );

        Assert.Equal( SubscribeOutcome.Invalid, await RegisterAsync( "http://fcm.googleapis.com/x" ) );
        Assert.Equal( SubscribeOutcome.Subscribed, await RegisterAsync( Endpoint ) );
        Assert.Equal( SubscribeOutcome.Subscribed, await RegisterAsync( Endpoint ) );

        var subscriptions = await testDatabase.ReadAsync( db => PushSubscriptionOperations.ForUser( db, david ) );
        Assert.Equal( 2, subscriptions.Count );
        Assert.Contains( subscriptions, subscription => subscription is { Endpoint: Endpoint, UserAgent: "ua" } );

        await testDatabase.WriteAsync( scope => PushSubscriptionOperations.DestroyByEndpoint( scope, david, Endpoint ) );
        Assert.Single( await testDatabase.ReadAsync( db => PushSubscriptionOperations.ForUser( db, david ) ) );
    }
}
