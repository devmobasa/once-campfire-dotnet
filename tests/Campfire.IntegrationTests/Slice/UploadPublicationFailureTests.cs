using System.Net;
using Campfire.IntegrationTests.Support;
using Campfire.Web.Paths;

namespace Campfire.IntegrationTests.Slice;

/// <summary>
/// File publication fails after the database transaction commits. Committed rows remain, unkept staging is cleaned,
/// and the later push, analysis, broadcasts, and webhooks do not run.
/// </summary>
public sealed class UploadPublicationFailureTests( SliceAppFactory factory ) : IClassFixture<SliceAppFactory>
{
    [Fact]
    public async Task A_failed_file_publication_keeps_committed_rows_and_stops_later_deliveries( )
    {
        var room = SliceAppFactory.Id( "bender_and_kevin" );
        using var client = await factory.SignedInClientAsync( "kevin@37signals.com" );
        var token = await client.CsrfTokenAsync( Routes.Room( room ) );
        using var taps = await SliceTaps.OnRoomAsync( factory, room );
        factory.Events.Clear( );
        factory.Faults.DuringUpload = ( ) =>
        {
            var message = SliceRows.Message( factory, "upload-fails" );
            var blob = SliceRows.AttachedBlob( factory, message.Id );
            Directory.CreateDirectory( SliceRows.BlobPath( factory, blob.Key ) );
        };

        var reply = await client.PostMessageAsync( room, token,
            file: ("note.txt", "text/plain", "uploaded file content"u8.ToArray( )), clientMessageId: "upload-fails" );

        var message = SliceRows.Message( factory, "upload-fails" );
        var blob = SliceRows.AttachedBlob( factory, message.Id );
        Assert.Equal( HttpStatusCode.InternalServerError, reply.Status );
        Assert.Equal( "note.txt", factory.Scalar<string>( $"SELECT body FROM message_search_index WHERE rowid={message.Id}" ) );
        Assert.True( Directory.Exists( SliceRows.BlobPath( factory, blob.Key ) ), "the real publication destination is obstructed" );
        Assert.False( File.Exists( SliceRows.BlobPath( factory, blob.Key ) ) );
        Assert.Equal( 0, SliceRows.StagedFiles( factory ) );
        Assert.Equal( message.CreatedAt, SliceRows.UnreadAt( factory, "bender_bender_and_kevin" ) );
        Assert.Equal( [$"upload blob {blob.Id}"], factory.Events.Only( "upload", "push_job", "analyze_job", "broadcast", "unread", "webhook_job" ) );
    }
}
