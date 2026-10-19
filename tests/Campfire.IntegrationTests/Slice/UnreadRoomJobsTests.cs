using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using Campfire.Application.Memberships;
using Campfire.Application.Rooms;
using Campfire.IntegrationTests.Support;
using Campfire.Web.App;
using Campfire.Web.Channels;
using Campfire.Web.Paths;
using Campfire.Web.Workers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Campfire.IntegrationTests.Slice;

public sealed class UnreadRoomJobsTests
{
    private static readonly long Designers = SliceAppFactory.Id( "designers" );

    [Fact]
    public async Task Creating_a_message_leaves_unread_delivery_to_a_worker( )
    {
        await using var factory = new PausedJobsFactory( );
        using var client = await factory.SignedInClientAsync( "david@37signals.com" );
        var deliveries = new ConcurrentQueue<string>( );
        using var subscription = factory.App.Cable.Hub.Subscribe(
            UnreadRoomsChannel.StreamNameFor( SliceAppFactory.Id( "jason" ) ), deliveries.Enqueue );
        var room = await factory.App.Database.ReadAsync( db => RoomQueries.Find( db, Designers ), TestContext.Current.CancellationToken );
        var queuedAtAppend = -1L;
        using var append = factory.App.Cable.Hub.Subscribe( string.Join( ':', RoomBroadcasts.RoomMessages( room ) ),
            _ => queuedAtAppend = factory.App.Jobs.BroadcastUnreadRoom.Counters.Enqueued );

        var token = await client.CsrfTokenAsync( Routes.Room( Designers ) );
        var reply = await client.PostMessageAsync( Designers, token, body: "<p>Queued unread</p>", clientMessageId: "queued-unread" );

        Assert.Equal( System.Net.HttpStatusCode.OK, reply.Status );
        Assert.Equal( 0, queuedAtAppend );
        Assert.Empty( deliveries );
        Assert.Equal( 1, factory.App.Jobs.BroadcastUnreadRoom.Count );
    }

    [Theory]
    [InlineData( "unread", true )]
    [InlineData( "connected", true )]
    [InlineData( "cutoff", true )]
    [InlineData( "expired", false )]
    [InlineData( "read-and-left", false )]
    [InlineData( "revoked", false )]
    [InlineData( "invisible-connected", true )]
    [InlineData( "creator-connected", true )]
    public async Task Delivery_uses_current_memberships_and_the_message_creation_timestamp( string state, bool receives )
    {
        await using var factory = new PausedJobsFactory( );
        using var client = await factory.SignedInClientAsync( "david@37signals.com" );
        factory.Clock.Advance( TimeSpan.FromMilliseconds( 125 ) );
        await client.PostMessageFromRoomPageAsync( Designers, "<p>Queued state</p>", "queued-state" );
        var message = SliceRows.Message( factory, "queued-state" );
        var recipient = state == "creator-connected" ? "david" : "jason";
        var userId = SliceAppFactory.Id( recipient );
        var membership = SliceAppFactory.Id( recipient + "_designers" );
        factory.Clock.Advance( TimeSpan.FromSeconds( 90 ) );
        if ( state != "unread" )
        {
            factory.Execute( $"UPDATE memberships SET unread_at=NULL, connected_at=NULL WHERE id={membership}" );
        }

        if ( state.Contains( "connected", StringComparison.Ordinal ) || state is "cutoff" or "expired" )
        {
            var offset = state == "cutoff" ? 60 : state == "expired" ? 61 : 0;
            var connected = factory.Clock.GetUtcNow( ).AddSeconds( -offset ).ToString( "yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture );
            factory.Execute( $"UPDATE memberships SET connected_at='{connected}' WHERE id={membership}" );
        }

        if ( state == "invisible-connected" )
        {
            factory.Execute( $"UPDATE memberships SET involvement='invisible' WHERE id={membership}" );
        }

        if ( state == "revoked" )
        {
            factory.Execute( $"DELETE FROM memberships WHERE id={membership}" );
        }

        var deliveries = new ConcurrentQueue<string>( );
        using var subscription = factory.App.Cable.Hub.Subscribe( UnreadRoomsChannel.StreamNameFor( userId ), deliveries.Enqueue );
        await new BroadcastUnreadRoomHandler( factory.App ).PerformAsync( new BroadcastUnreadRoomJob( message.Id ), TestContext.Current.CancellationToken );

        if ( receives )
        {
            var notice = JsonNode.Parse( Assert.Single( deliveries ) )!.AsObject( );
            Assert.Equal( Designers, notice["roomId"]!.GetValue<long>( ) );
            Assert.NotNull( notice["at"] );
            Assert.Equal( 1790428492.125d, notice["at"]!.GetValue<double>( ) );
        }
        else
        {
            Assert.Empty( deliveries );
        }
    }

    [Fact]
    public async Task Accepted_notices_preserve_recipient_order_while_the_worker_drains( )
    {
        await using var factory = new OrderedJobsFactory( );
        using var client = await factory.SignedInClientAsync( "david@37signals.com" );
        var deliveries = new ConcurrentQueue<double>( );
        using var subscription = factory.App.Cable.Hub.Subscribe(
            UnreadRoomsChannel.StreamNameFor( SliceAppFactory.Id( "jason" ) ),
            payload => deliveries.Enqueue( JsonNode.Parse( payload )!["at"]!.GetValue<double>( ) ) );
        await client.PostMessageFromRoomPageAsync( Designers, "<p>First</p>", "ordered-first" );
        await factory.Gate.Started.Task.WaitAsync( TestContext.Current.CancellationToken );
        factory.Clock.Advance( TimeSpan.FromMilliseconds( 125 ) );
        await client.PostMessageFromRoomPageAsync( Designers, "<p>Second</p>", "ordered-second" );
        var queue = factory.App.Jobs.BroadcastUnreadRoom;
        try
        {
            Assert.Equal( 1, queue.Kind.Consumers );
            Assert.Equal( 1, queue.Count );
            Assert.Empty( deliveries );

            var worker = factory.Services.GetServices<IHostedService>( ).OfType<JobWorker<BroadcastUnreadRoomJob>>( ).Single( );
            var stopped = worker.StopAsync( TestContext.Current.CancellationToken );
            factory.Gate.Release.TrySetResult( );
            await stopped;
        }
        finally
        {
            factory.Gate.Release.TrySetResult( );
        }

        Assert.Equal( [1790428492d, 1790428492.125d], deliveries );
        Assert.Equal( 2, queue.Counters.Performed );
        Assert.False( queue.Enqueue( new BroadcastUnreadRoomJob( SliceAppFactory.Id( "first" ) ) ) );
    }

    [Fact]
    public async Task Unread_delivery_observes_membership_changes_before_later_pages( )
    {
        await using var factory = new PausedJobsFactory( );
        using var client = await factory.SignedInClientAsync( "david@37signals.com" );
        await client.PostMessageFromRoomPageAsync( Designers, "<p>Paged unread</p>", "paged-unread" );
        var message = SliceRows.Message( factory, "paged-unread" );
        factory.Execute( $"DELETE FROM memberships WHERE room_id={Designers}" );
        const long FirstId = 2_000_000_000;
        const int Recipients = 257;
        factory.Execute( $"WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<{Recipients}) " +
            $"INSERT INTO memberships(id,user_id,room_id,unread_at,created_at,updated_at) " +
            $"SELECT {FirstId}+i,{FirstId}+i,{Designers},'2026-09-26 13:14:52','2026-09-26 13:14:52','2026-09-26 13:14:52' FROM n" );
        var delivered = new List<int>( );
        var subscriptions = Enumerable.Range( 1, Recipients ).Select( recipient => factory.App.Cable.Hub.Subscribe(
            UnreadRoomsChannel.StreamNameFor( FirstId + recipient ), _ =>
            {
                delivered.Add( recipient );
                if ( recipient == 128 )
                {
                    factory.Execute( $"UPDATE memberships SET unread_at=NULL,connected_at=NULL WHERE id={FirstId + Recipients}" );
                }
            } ) ).ToList( );
        try
        {
            await new BroadcastUnreadRoomHandler( factory.App ).PerformAsync( new BroadcastUnreadRoomJob( message.Id ), TestContext.Current.CancellationToken );
            Assert.Equal( Enumerable.Range( 1, Recipients - 1 ), delivered );
        }
        finally
        {
            subscriptions.ForEach( subscription => subscription.Dispose( ) );
        }
    }

    [Fact]
    public async Task Cancelling_unread_delivery_stops_publication_inside_a_page( )
    {
        await using var factory = new PausedJobsFactory( );
        using var client = await factory.SignedInClientAsync( "david@37signals.com" );
        await client.PostMessageFromRoomPageAsync( Designers, "<p>Cancelled unread</p>", "cancelled-unread" );
        var message = SliceRows.Message( factory, "cancelled-unread" );
        factory.Execute( $"UPDATE memberships SET unread_at='2026-09-26 13:14:52' WHERE room_id={Designers}" );
        var recipients = await factory.App.Database.ReadAsync( db => MembershipQueries.ForRoom( db, Designers ), TestContext.Current.CancellationToken );
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource( TestContext.Current.CancellationToken );
        var deliveries = new List<long>( );
        var subscriptions = recipients.Select( row => row.UserId )
            .Select( userId => factory.App.Cable.Hub.Subscribe( UnreadRoomsChannel.StreamNameFor( userId ), _ =>
            {
                deliveries.Add( userId );
                cancelled.Cancel( );
            } ) ).ToList( );
        try
        {
            Assert.True( subscriptions.Count > 1 );
            await Assert.ThrowsAnyAsync<OperationCanceledException>( ( ) => new BroadcastUnreadRoomHandler( factory.App )
                .PerformAsync( new BroadcastUnreadRoomJob( message.Id ), cancelled.Token ) );
            Assert.Single( deliveries );
        }
        finally
        {
            subscriptions.ForEach( subscription => subscription.Dispose( ) );
        }
    }

    private sealed class DeliveryGate
    {
        private int _started;

        public TaskCompletionSource Started { get; } = new( TaskCreationOptions.RunContinuationsAsynchronously );

        public TaskCompletionSource Release { get; } = new( TaskCreationOptions.RunContinuationsAsynchronously );

        public async Task EnterAsync( CancellationToken cancellationToken )
        {
            if ( Interlocked.Increment( ref _started ) == 1 )
            {
                Started.TrySetResult( );
                await Release.Task.WaitAsync( cancellationToken );
            }
        }
    }

    private sealed class OrderedJobsFactory : SliceAppFactory
    {
        public DeliveryGate Gate { get; } = new( );

        protected override void ConfigureWebHost( IWebHostBuilder builder )
        {
            base.ConfigureWebHost( builder );
            builder.UseSetting( "JOB_CONCURRENCY", "2" );
            builder.ConfigureTestServices( services =>
            {
                services.RemoveAll<IJobHandler<BroadcastUnreadRoomJob>>( );
                services.AddScoped<IJobHandler<BroadcastUnreadRoomJob>>( provider =>
                    new GatedUnreadHandler( provider.GetRequiredService<CampfireApp>( ), Gate ) );
            } );
        }
    }

    private sealed class GatedUnreadHandler( CampfireApp app, DeliveryGate gate ) : IJobHandler<BroadcastUnreadRoomJob>
    {
        public async Task PerformAsync( BroadcastUnreadRoomJob job, CancellationToken cancellationToken )
        {
            await gate.EnterAsync( cancellationToken );
            await new BroadcastUnreadRoomHandler( app ).PerformAsync( job, cancellationToken );
        }
    }

    private sealed class PausedJobsFactory : SliceAppFactory
    {
        protected override void ConfigureWebHost( IWebHostBuilder builder )
        {
            base.ConfigureWebHost( builder );
            builder.ConfigureTestServices( services =>
            {
                var workers = services.Where( service => service.ServiceType == typeof( IHostedService ) &&
                    service.ImplementationType is { IsGenericType: true } type &&
                    type.GetGenericTypeDefinition( ) == typeof( JobWorker<> ) ).ToList( );
                foreach ( var worker in workers )
                {
                    services.Remove( worker );
                }
            } );
        }
    }
}
