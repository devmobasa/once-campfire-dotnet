using System.Net;
using Campfire.IntegrationTests.Support;
using Campfire.Web.Paths;

namespace Campfire.IntegrationTests.Slice;

/// <summary>Messages attach already stored blobs by signed id, identify them on assignment, and purge replaced attachments.</summary>
public sealed class StoredBlobAttachmentTests
{
    /// <summary>The content type the blobs are stored with, so that attaching them is what identifies them.</summary>
    private const string Unidentified = "application/octet-stream";

    private static readonly long Designers = SliceAppFactory.Id( "designers" );

    [Fact]
    public async Task A_message_attaches_an_uploaded_blob_by_its_signed_id_identifies_and_processes_it( )
    {
        using var factory = new SliceAppFactory( );
        var (client, token) = await factory.SignedInWithPageTokenAsync( "david@37signals.com", Routes.Room( Designers ) );
        var (blobId, signedId) = await StoredBlobs.CreateAsync( factory, "photo.bin", SliceFiles.Moon( ).Bytes, Unidentified );
        using var taps = await SliceTaps.OnRoomAsync( factory, Designers );
        factory.Events.Clear( );

        var reply = await client.SubmitFormAsync(
            HttpMethod.Post,
            Routes.RoomMessages( Designers ),
            token,
            [("message[attachment]", signedId), ("message[client_message_id]", "by-signed-id")] );

        var message = SliceRows.Message( factory, "by-signed-id" );
        var attached = SliceRows.AttachedBlob( factory, message.Id );
        Assert.Equal( HttpStatusCode.OK, reply.Status );
        Assert.Contains( "class=\"message__attachment\"", reply.Body, StringComparison.Ordinal );
        Assert.Equal( blobId, attached.Id );
        Assert.Equal( "photo.bin", factory.Scalar<string>( $"SELECT body FROM message_search_index WHERE rowid={message.Id}" ) );
        Assert.Equal( message.CreatedAt, SliceRows.UnreadAt( factory, "jason_designers" ) );
        Assert.Equal( "image/jpeg", factory.Scalar<string>( $"SELECT content_type FROM active_storage_blobs WHERE id = {blobId}" ) );
        Assert.Contains( "\"identified\":true", attached.Metadata, StringComparison.Ordinal );
        Assert.Contains( "\"analyzed\":true", attached.Metadata, StringComparison.Ordinal );
        Assert.True( File.Exists( SliceRows.BlobPath( factory, SliceRows.VariantImage( factory, blobId ) ) ), "the :thumb variant is made" );
        Assert.Empty( factory.Events.Only( "upload" ) );
        Assert.Equal(
            [$"push_job room {Designers} message {message.Id}", $"analyze_job blob {blobId}", "broadcast append"],
            factory.Events.Only( "push_job", "analyze_job", "broadcast" ) );
        client.Dispose( );
    }

    [Fact]
    public async Task An_invalid_signed_id_is_not_found_and_a_missing_blob_renders_room_not_found( )
    {
        using var factory = new SliceAppFactory( );
        var (client, token) = await factory.SignedInWithPageTokenAsync( "david@37signals.com", Routes.Room( Designers ) );
        var (blobId, signedId) = await StoredBlobs.CreateAsync( factory, "a.txt", "hello!"u8.ToArray( ), Unidentified );
        var messages = factory.Scalar<long>( "SELECT count(*) FROM messages" );

        var forged = await client.SubmitFormAsync(
            HttpMethod.Post,
            Routes.RoomMessages( Designers ),
            token,
            [("message[attachment]", "forged--123"), ("message[client_message_id]", "forged")] );
        factory.Execute( $"DELETE FROM active_storage_blobs WHERE id = {blobId}" );
        var gone = await client.SubmitFormAsync(
            HttpMethod.Post,
            Routes.RoomMessages( Designers ),
            token,
            [("message[attachment]", signedId), ("message[client_message_id]", "gone")] );

        Assert.Equal( HttpStatusCode.NotFound, forged.Status );
        Assert.Equal( HttpStatusCode.OK, gone.Status );
        Assert.Contains( "rel=\"stylesheet\"", gone.Body, StringComparison.Ordinal );
        Assert.Equal( messages, factory.Scalar<long>( "SELECT count(*) FROM messages" ) );
        client.Dispose( );
    }

    [Fact]
    public async Task Updating_replaces_the_attachment_with_a_signed_blob_and_a_blank_one_removes_it( )
    {
        using var factory = new SliceAppFactory( );
        var (client, token) = await factory.SignedInWithPageTokenAsync( "david@37signals.com", Routes.Room( Designers ) );
        await client.PostMessageAsync( Designers, token, file: SliceFiles.Moon( ), clientMessageId: "to-replace" );
        var message = SliceRows.Message( factory, "to-replace" );
        var original = SliceRows.AttachedBlob( factory, message.Id );
        var (blobId, signedId) = await StoredBlobs.CreateAsync( factory, "note.txt", "hello!"u8.ToArray( ), Unidentified );
        factory.Events.Clear( );

        var replaced = await client.SubmitFormAsync( HttpMethod.Put, Routes.RoomMessage( Designers, message.Id ), token, [("message[attachment]", signedId)] );
        // The purge job runs in the background and queues its variant's purge in turn: keep to the two blobs at hand.
        var afterReplace = factory.Events.All.Where( line => line == $"purge_job blob {original.Id}" || line == $"analyze_job blob {blobId}" ).ToList( );
        var identified = factory.Scalar<string>( $"SELECT content_type FROM active_storage_blobs WHERE id = {blobId}" );
        var removed = await client.SubmitFormAsync( HttpMethod.Put, Routes.RoomMessage( Designers, message.Id ), token, [("message[attachment]", "")] );

        Assert.Equal( (HttpStatusCode.Found, HttpStatusCode.Found), (replaced.Status, removed.Status) );
        Assert.Equal( [$"purge_job blob {original.Id}", $"analyze_job blob {blobId}"], afterReplace );
        Assert.Equal(
            0,
            factory.Scalar<long>( $"SELECT count(*) FROM active_storage_attachments WHERE record_type = 'Message' AND record_id = {message.Id}" ) );
        Assert.Equal( "text/plain", identified );
        client.Dispose( );
    }
}
