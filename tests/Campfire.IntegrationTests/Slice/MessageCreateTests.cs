using System.Net;
using System.Text.Json.Nodes;
using Campfire.Application.Rooms;
using Campfire.IntegrationTests.Support;
using Campfire.Web.Channels;
using Campfire.Web.Paths;

namespace Campfire.IntegrationTests.Slice;

/// <summary>
/// <c>MessagesController#create</c> end to end: the operation sequence for an image message (the transaction, then the
/// search index, the file, <c>room.receive</c> and the analysis job, then <c>process_attachment</c>, the broadcasts and
/// the webhooks), observed through the hooks, the pub/sub and the database.
/// </summary>
public sealed class MessageCreateTests( SliceAppFactory factory ) : IClassFixture<SliceAppFactory>
{
    private static readonly long Designers = SliceAppFactory.Id( "designers" );

    private static readonly string[] DesignersMembers = ["david", "jason", "jz", "kevin"];

    [Fact]
    public async Task An_image_message_runs_the_create_sequence_in_order( )
    {
        using var client = await factory.SignedInClientAsync( "david@37signals.com" );
        var token = await client.CsrfTokenAsync( Routes.Room( Designers ) );
        using var taps = await SliceTaps.OnRoomAsync( factory, Designers );
        factory.Events.Clear( );
        var room = await factory.App.Database.ReadAsync( db => RoomQueries.Find( db, Designers ), TestContext.Current.CancellationToken );
        string? broadcast = null;
        using var append = factory.App.Cable.Hub.Subscribe( string.Join( ':', RoomBroadcasts.RoomMessages( room ) ),
            payload => broadcast = JsonNode.Parse( payload )!.GetValue<string>( ) );

        // All creation rows, indexing, and unread marking commit before file publication.
        string? unreadAtUpload = null;
        factory.Faults.DuringUpload = ( ) =>
        {
            var indexed = factory.Scalar<long>( """
                SELECT count(*) FROM message_search_index i JOIN messages m ON m.id = i.rowid WHERE m.client_message_id = 'sequence-image'
                """ ) == 1;
            factory.Events.Record( indexed ? "indexed" : "not indexed" );
            unreadAtUpload = SliceRows.UnreadAt( factory, "jason_designers" );
        };

        var reply = await client.PostMessageAsync( Designers, token, file: SliceFiles.Moon( ), clientMessageId: "sequence-image" );
        var unreadJobs = factory.App.Jobs.BroadcastUnreadRoom;
        var enqueued = unreadJobs.Counters.Enqueued;
        await SliceWait.UntilAsync( ( ) => unreadJobs.Counters.Performed + unreadJobs.Counters.Failed >= enqueued );

        Assert.Equal( reply.Body.TrimEnd( '\n' ), broadcast );
        Assert.Equal( HttpStatusCode.OK, reply.Status );
        Assert.Equal( "text/vnd.turbo-stream.html; charset=utf-8", reply.ContentType );
        Assert.StartsWith( $"<turbo-stream action=\"append\" target=\"messages_rooms_closed_{Designers}\">", reply.Body, StringComparison.Ordinal );
        Assert.Contains( "id=\"message_sequence-image\"", reply.Body, StringComparison.Ordinal );
        Assert.Contains( "class=\"message__attachment\"", reply.Body, StringComparison.Ordinal );
        Assert.EndsWith( "</turbo-stream>\n", reply.Body, StringComparison.Ordinal );
        Assert.DoesNotContain( "authenticity_token", reply.Body, StringComparison.Ordinal );

        var message = SliceRows.Message( factory, "sequence-image" );
        Assert.Equal( message.CreatedAt, unreadAtUpload );
        var blob = SliceRows.AttachedBlob( factory, message.Id );
        var steps = SliceSequence.Steps( factory.Events, message.Id, blob.Id );
        Assert.Equal(
            [
                $"upload blob {blob.Id}",
                "indexed",
                $"push_job room {Designers} message {message.Id}",
                $"analyze_job blob {blob.Id}",
                "broadcast append",
            ],
            steps.Take( 5 ) );

        // The background notice reaches members whose room remains unread or connected.
        Assert.Equal(
            DesignersMembers.Where( user => user != "david" ).Select( user => $"unread user {SliceAppFactory.Id( user )}" ).Order( StringComparer.Ordinal ),
            steps.Skip( 5 ).Order( StringComparer.Ordinal ) );

        Assert.Equal( 1, factory.Scalar<long>( $"SELECT count(*) FROM message_search_index WHERE rowid = {message.Id}" ) );
        Assert.True( File.Exists( SliceRows.BlobPath( factory, blob.Key ) ), "the uploaded file is in place" );
        Assert.Contains( "\"analyzed\":true", blob.Metadata, StringComparison.Ordinal );
        Assert.Contains( "\"width\":", blob.Metadata, StringComparison.Ordinal );
        var thumb = SliceRows.VariantImage( factory, blob.Id );
        Assert.True( File.Exists( SliceRows.BlobPath( factory, thumb ) ), "the :thumb variant is recorded with its file" );

        // room.receive: members who are visible, disconnected and not the creator are unread at the message's time.
        Assert.Equal( message.CreatedAt, SliceRows.UnreadAt( factory, "jason_designers" ) );
        Assert.Equal( message.CreatedAt, SliceRows.UnreadAt( factory, "kevin_designers" ) );
        Assert.Null( SliceRows.UnreadAt( factory, "david_designers" ) );
    }

    [Fact]
    public async Task A_message_to_a_bot_in_a_direct_room_queues_its_webhook_after_the_broadcast( )
    {
        var room = SliceAppFactory.Id( "bender_and_kevin" );
        using var client = await factory.SignedInClientAsync( "kevin@37signals.com" );
        var token = await client.CsrfTokenAsync( Routes.Room( room ) );
        using var taps = await SliceTaps.OnRoomAsync( factory, room );
        factory.Events.Clear( );

        var reply = await client.PostMessageAsync( room, token, body: "<p>Hello Bender</p>", clientMessageId: "to-the-bot" );

        var message = SliceRows.Message( factory, "to-the-bot" );
        var events = factory.Events.Only( "push_job", "broadcast", "webhook_job" );
        var body = SliceRows.MessageBody( factory, message.Id );
        Assert.Equal( HttpStatusCode.OK, reply.Status );
        Assert.Contains( "Hello Bender", reply.Body, StringComparison.Ordinal );
        Assert.Equal(
            [
                $"push_job room {room} message {message.Id}",
                "broadcast append",
                $"webhook_job bot {SliceAppFactory.Id( "bender" )} message {message.Id}",
            ],
            events );
        Assert.Equal( "<p>Hello Bender</p>", body );
        Assert.Equal( "Hello Bender", factory.Scalar<string>( $"SELECT body FROM message_search_index WHERE rowid = {message.Id}" ) );
    }

    [Fact]
    public async Task Paging_back_answers_a_fragment_204_before_the_first_message_and_404_for_an_unknown_anchor_or_room( )
    {
        using var client = await factory.SignedInClientAsync( "david@37signals.com" );
        var first = factory.Scalar<long>( $"SELECT id FROM messages WHERE room_id = {Designers} ORDER BY created_at LIMIT 1" );

        var page = await client.GetAsync( Routes.RoomMessages( Designers ) );
        var before = await client.GetAsync( $"{Routes.RoomMessages( Designers )}?before={first}" );
        var missing = await client.GetAsync( $"{Routes.RoomMessages( Designers )}?before=999999" );
        var notMember = await client.GetAsync( Routes.RoomMessages( SliceAppFactory.Id( "bender_and_kevin" ) ) );

        Assert.Equal( HttpStatusCode.OK, page.Status );
        Assert.StartsWith( "<div id=\"message_", page.Body.TrimStart( ), StringComparison.Ordinal );
        Assert.DoesNotContain( "<html", page.Body, StringComparison.Ordinal );
        Assert.NotNull( page.Headers.ETag );
        Assert.Equal( HttpStatusCode.NoContent, before.Status );
        Assert.Equal( HttpStatusCode.NotFound, missing.Status );
        Assert.Equal( HttpStatusCode.NotFound, notMember.Status );
    }
}
