using System.Diagnostics;
using System.Net;
using Campfire.Application.Integrations.WebPush;
using Campfire.IntegrationTests.Support;
using Campfire.Web.Paths;
using Campfire.Web.Workers;

namespace Campfire.IntegrationTests.Slice;

/// <summary>
/// The job kinds as the app registers them and how they behave around a real host: each kind's full-queue
/// policy and consumers, queued work draining when the host stops, what is left at the drain deadline abandoned
/// (nothing is persisted, so a crash loses the same work, the accepted best-effort decision), a failing job leaving the
/// host serving, and bot webhooks kept unrestricted: an internal address is delivered to.
/// </summary>
public sealed class JobKindsTests
{
    private static readonly long Bender = SliceAppFactory.Id( "bender" );

    /// <summary>A watercooler message; the room has the bot, so a reply lands there.</summary>
    private static readonly long Fourth = SliceAppFactory.Id( "fourth" );

    [Fact]
    public void Each_registered_kind_has_its_declared_queue_policy_and_consumer_count( )
    {
        using var factory = new SliceAppFactory( );
        var jobs = factory.App.Jobs;

        // JOB_CONCURRENCY is 1 in the slice; production defaults it like Rails' Resque pool.
        Assert.Equal( ("ActiveStorage::AnalyzeJob", FullQueuePolicy.Wait, 1), Describe( jobs.Analyze.Kind ) );
        Assert.Equal( ("Message::BroadcastUnreadRoomJob", FullQueuePolicy.Drop, 1), Describe( jobs.BroadcastUnreadRoom.Kind ) );
        Assert.Equal( ("ActiveStorage::PurgeJob", FullQueuePolicy.Wait, 1), Describe( jobs.Purge.Kind ) );
        Assert.Equal( ("Room::PushMessageJob", FullQueuePolicy.Drop, 1), Describe( jobs.PushMessage.Kind ) );
        Assert.Equal( ("RemoveBannedContentJob", FullQueuePolicy.Wait, 1), Describe( jobs.RemoveBannedContent.Kind ) );
        Assert.Equal( ("Bot::WebhookJob", FullQueuePolicy.Drop, 1), Describe( jobs.Webhook.Kind ) );
        JobKind[] kinds = [jobs.Analyze.Kind, jobs.BroadcastUnreadRoom.Kind, jobs.Purge.Kind, jobs.PushMessage.Kind, jobs.RemoveBannedContent.Kind, jobs.Webhook.Kind];
        Assert.All( kinds, kind => Assert.Equal( WorkerServiceCollectionExtensions.JobCapacity, kind.Capacity ) );
        Assert.Equal( (50, 10_000), (WebPushPool.MaxConcurrency, WebPushPool.MaxQueue) );
    }

    /// <summary>
    /// The webhook's host resolves to a private address (as an internal service's would), and the delivery goes there:
    /// unlike Opengraph and Web Push, <c>Webhook#http</c> has no private network guard.
    /// </summary>
    [Fact]
    public async Task Stopping_the_host_drains_queued_webhooks_to_an_internal_address_and_posts_the_replies( )
    {
        await using var factory = new HostedSliceAppFactory( );
        var replies = 0;
        await using var bots = await LoopbackServer.StartAsync( tls: false, async httpContext =>
        {
            await Task.Delay( 200, httpContext.RequestAborted );
            httpContext.Response.ContentType = "text/plain";
            await httpContext.Response.WriteAsync( $"ack {Interlocked.Increment( ref replies )}", httpContext.RequestAborted );
        } );
        factory.Network.Answer( "bots.internal", "10.20.30.40" ).Route( "10.20.30.40", bots.Endpoint );
        factory.Execute( $"UPDATE webhooks SET url = 'http://bots.internal/bender' WHERE user_id = {Bender}" );
        var message = Fourth;
        var jobs = factory.App.Jobs;
        var before = factory.Scalar<long>( $"SELECT count(*) FROM messages WHERE creator_id = {Bender}" );

        foreach ( var _ in Enumerable.Range( 0, 3 ) )
        {
            Assert.True( jobs.Webhook.Enqueue( new WebhookJob( Bender, message ) ) );
        }

        await factory.Host!.StopAsync( TestContext.Current.CancellationToken );

        Assert.Equal( 3, bots.Requests.Count );
        Assert.All( factory.Network.Dialed, dialed => Assert.Equal( "10.20.30.40:80", dialed ) );
        Assert.Equal( 3, jobs.Webhook.Counters.Performed );
        Assert.Equal( 0, jobs.Webhook.Counters.Abandoned );
        Assert.Equal( before + 3, factory.Scalar<long>( $"SELECT count(*) FROM messages WHERE creator_id = {Bender}" ) );
        Assert.False( jobs.Webhook.Enqueue( new WebhookJob( Bender, message ) ), "a stopped queue takes no more jobs" );
    }

    [Fact]
    public async Task Jobs_still_queued_at_the_drain_deadline_are_abandoned_not_kept( )
    {
        await using var factory = new HostedSliceAppFactory( drainTimeout: TimeSpan.FromSeconds( 1 ) );
        var hang = new TaskCompletionSource( TaskCreationOptions.RunContinuationsAsynchronously );
        await using var bots = await LoopbackServer.StartAsync( tls: false, httpContext => hang.Task.WaitAsync( httpContext.RequestAborted ) );
        factory.Network.Answer( "bots.internal", "10.20.30.40" ).Route( "10.20.30.40", bots.Endpoint );
        factory.Execute( $"UPDATE webhooks SET url = 'http://bots.internal/bender' WHERE user_id = {Bender}" );
        var message = Fourth;
        var jobs = factory.App.Jobs;

        foreach ( var _ in Enumerable.Range( 0, 3 ) )
        {
            jobs.Webhook.Enqueue( new WebhookJob( Bender, message ) );
        }

        await SliceWait.UntilAsync( ( ) => bots.Requests.Count == 1 );
        var stopping = Stopwatch.StartNew( );
        await factory.Host!.StopAsync( TestContext.Current.CancellationToken );
        hang.SetResult( );

        Assert.True( stopping.Elapsed < TimeSpan.FromSeconds( 10 ), $"stopping took {stopping.Elapsed}" );
        Assert.Equal( 1, jobs.Webhook.Counters.Failed );
        Assert.Equal( 2, jobs.Webhook.Counters.Abandoned );
        Assert.Single( bots.Requests );
    }

    [Fact]
    public async Task A_failing_job_leaves_the_worker_and_the_host_running( )
    {
        await using var factory = new SliceAppFactory( );
        await using var bots = await LoopbackServer.StartAsync( tls: false, async httpContext =>
        {
            httpContext.Response.ContentType = "text/plain";
            await httpContext.Response.WriteAsync( "still here", httpContext.RequestAborted );
        } );
        var message = Fourth;
        var jobs = factory.App.Jobs;

        // The fixture's webhook, http://example.com/bender, doesn't resolve: the job raises and is logged.
        jobs.Webhook.Enqueue( new WebhookJob( Bender, message ) );
        await SliceWait.UntilAsync( ( ) => jobs.Webhook.Counters.Failed == 1 );
        factory.Execute( $"UPDATE webhooks SET url = 'http://{bots.Endpoint}/bender' WHERE user_id = {Bender}" );
        jobs.Webhook.Enqueue( new WebhookJob( Bender, message ) );
        await SliceWait.UntilAsync( ( ) => jobs.Webhook.Counters.Performed == 1 );
        using var client = factory.Client( );

        Assert.Equal( HttpStatusCode.OK, (await client.GetAsync( Routes.RailsHealthCheck )).Status );
        Assert.Single( bots.Requests );
    }

    /// <summary>
    /// <c>RemoveBannedContentJob</c> → <c>User#remove_banned_content</c>: each of the user's messages destroyed (its
    /// search row, boosts and body with it) and its removal broadcast to the room; nobody else's message is touched.
    /// </summary>
    [Fact]
    public async Task Banned_content_removal_destroys_the_users_messages_and_broadcasts_each_removal( )
    {
        await using var factory = new SliceAppFactory( );
        using var david = await factory.SignedInClientAsync( "david@37signals.com" );
        var designers = SliceAppFactory.Id( "designers" );
        await david.PostMessageFromRoomPageAsync( designers, "<p>Something to ban</p>", "banned-1" );
        var davidId = SliceAppFactory.Id( "david" );
        var jz = SliceAppFactory.Id( "jz" );
        var davids = factory.Scalar<long>( $"SELECT count(*) FROM messages WHERE creator_id = {davidId}" );
        var jzs = factory.Scalar<long>( $"SELECT count(*) FROM messages WHERE creator_id = {jz}" );
        using var taps = await SliceTaps.OnRoomAsync( factory, designers );

        Assert.True( factory.App.Jobs.RemoveBannedContent.Enqueue( new RemoveBannedContentJob( davidId ) ) );
        await SliceWait.UntilAsync( ( ) => factory.App.Jobs.RemoveBannedContent.Counters.Performed == 1 );

        Assert.True( davids > 0 );
        Assert.Equal( 0, factory.Scalar<long>( $"SELECT count(*) FROM messages WHERE creator_id = {davidId}" ) );
        Assert.Equal( jzs, factory.Scalar<long>( $"SELECT count(*) FROM messages WHERE creator_id = {jz}" ) );
        Assert.Equal( 0, factory.Scalar<long>( "SELECT count(*) FROM message_search_index WHERE body LIKE '%Something to ban%'" ) );
        Assert.NotEmpty( factory.Events.Only( "broadcast remove" ) );
    }

    /// <summary>
    /// <c>ActiveStorage::PurgeJob</c>: destroying a message purges its file's blob later, and purging that blob destroys
    /// its thumbnail's variant record, whose image blob is purged later in turn (<c>has_one_attached :image</c>).
    /// </summary>
    [Fact]
    public async Task Destroying_a_message_purges_its_file_and_then_its_thumbnail_through_the_purge_job( )
    {
        await using var factory = new SliceAppFactory( );
        using var bot = factory.Client( );
        var watercooler = SliceAppFactory.Id( "watercooler" );
        var botKey = BotApiKeys.Bender( factory );
        await bot.PostAttachmentAsync( Routes.RoomBotMessages( watercooler, botKey ), SliceFiles.Moon( ) );
        var message = factory.Scalar<long>( $"SELECT max(id) FROM messages WHERE creator_id = {Bender}" );
        var blob = SliceRows.AttachedBlob( factory, message );
        var thumbnail = SliceRows.VariantImage( factory, blob.Id );
        var thumbnailBlob = factory.Scalar<long>( $"SELECT id FROM active_storage_blobs WHERE key = '{thumbnail}'" );
        var purge = factory.App.Jobs.Purge;

        var destroyed = await bot.SendRawAsync( HttpMethod.Delete, Routes.RoomBotMessage( watercooler, botKey, message ), "" );
        await SliceWait.UntilAsync( ( ) => factory.Scalar<long>( $"SELECT count(*) FROM active_storage_blobs WHERE id IN ({blob.Id}, {thumbnailBlob})" ) == 0
            && factory.Scalar<long>( "SELECT count(*) FROM campfire_cleanup" ) == 0 );

        Assert.Equal( HttpStatusCode.NoContent, destroyed.Status );
        Assert.Equal( 0, purge.Counters.Failed );
        Assert.Contains( $"purge_job blob {blob.Id}", factory.Events.Only( "purge_job" ) );
        Assert.Equal( 0, factory.Scalar<long>( $"SELECT count(*) FROM active_storage_blobs WHERE id IN ({blob.Id}, {thumbnailBlob})" ) );
        Assert.False( File.Exists( SliceRows.BlobPath( factory, blob.Key ) ) );
        Assert.False( File.Exists( SliceRows.BlobPath( factory, thumbnail ) ) );
    }

    private static (string Name, FullQueuePolicy WhenFull, int Consumers) Describe( JobKind kind ) => (kind.Name, kind.WhenFull, kind.Consumers);
}
