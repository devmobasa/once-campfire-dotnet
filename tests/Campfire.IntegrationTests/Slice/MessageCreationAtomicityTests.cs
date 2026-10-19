using System.Net;
using Campfire.IntegrationTests.Support;
using Campfire.Web.Paths;

namespace Campfire.IntegrationTests.Slice;

/// <summary>
/// A message's database work (the message, its body, attachment rows, search index row and unread marks) is one
/// unit: when any of it fails nothing is kept and nothing reaches the jobs or the broadcasts, and once the create is
/// answered every read sees all of it, with or without whole-response reuse.
/// </summary>
public sealed class MessageCreationAtomicityTests
{
    [Theory]
    [InlineData( "text", "fts" )]
    [InlineData( "text", "unread" )]
    [InlineData( "multipart", "fts" )]
    [InlineData( "multipart", "unread" )]
    [InlineData( "signed", "fts" )]
    [InlineData( "signed", "unread" )]
    public async Task Failed_creation_database_work_rolls_back_without_external_effects( string input, string failure )
    {
        using var factory = new SliceAppFactory( );
        var room = SliceAppFactory.Id( "bender_and_kevin" );
        using var client = await factory.SignedInClientAsync( "kevin@37signals.com" );
        var token = await client.CsrfTokenAsync( Routes.Room( room ) );
        using var taps = await SliceTaps.OnRoomAsync( factory, room );
        string? signedId = null;
        string? originalFile = null;
        string? originalBlob = null;
        long blobId = 0;
        if ( input == "signed" )
        {
            (blobId, signedId) = await StoredBlobs.CreateAsync( factory, "note.txt", "uploaded file content"u8.ToArray( ), "application/octet-stream" );
            var key = factory.Scalar<string>( $"SELECT key FROM active_storage_blobs WHERE id={blobId}" );
            originalFile = SliceRows.BlobPath( factory, key! );
            originalBlob = factory.Scalar<string>( $"SELECT content_type || '|' || metadata FROM active_storage_blobs WHERE id={blobId}" );
            Assert.True( File.Exists( originalFile ) );
        }

        string[] tables = ["messages", "action_text_rich_texts", "active_storage_blobs", "active_storage_attachments"];
        var rows = tables.ToDictionary( table => table, table => factory.Scalar<long>( $"SELECT count(*) FROM {table}" ) );
        var files = Path.Combine( factory.StoragePath, "files" );
        var fileCount = Directory.Exists( files ) ? Directory.GetFiles( files, "*", SearchOption.AllDirectories ).Length : 0;
        var roomVersion = factory.Scalar<string>( $"SELECT updated_at FROM rooms WHERE id={room}" );
        var membership = SliceAppFactory.Id( "bender_bender_and_kevin" );
        var membershipVersion = factory.Scalar<string>( $"SELECT updated_at FROM memberships WHERE id={membership}" );
        var indexRows = factory.Scalar<long>( "SELECT count(*) FROM message_search_index" );
        var indexTable = "message_search_index";
        if ( failure == "fts" )
        {
            factory.Execute( "ALTER TABLE message_search_index RENAME TO message_search_index_elsewhere" );
            indexTable = "message_search_index_elsewhere";
        }
        else
        {
            factory.Execute( $"""
                CREATE TRIGGER fail_creation_unread BEFORE UPDATE OF unread_at ON memberships WHEN NEW.room_id = {room}
                BEGIN SELECT RAISE(ABORT, 'unread failure'); END
                """ );
        }

        factory.Clock.Advance( TimeSpan.FromSeconds( 1 ) );
        factory.Events.Clear( );
        var reply = input == "signed"
            ? await client.SubmitFormAsync( HttpMethod.Post, Routes.RoomMessages( room ), token,
                [("message[attachment]", signedId!), ("message[client_message_id]", "atomic-create-failure")] )
            : await client.PostMessageAsync( room, token,
                body: input == "text" ? "<p>Atomic note</p>" : null,
                file: input == "multipart" ? ("note.txt", "text/plain", "uploaded file content"u8.ToArray( )) : null,
                clientMessageId: "atomic-create-failure" );
        Assert.Equal( HttpStatusCode.InternalServerError, reply.Status );
        Assert.Equal( 0, SliceRows.MessageCount( factory, "atomic-create-failure" ) );
        foreach ( var table in tables )
        {
            Assert.Equal( rows[table], factory.Scalar<long>( $"SELECT count(*) FROM {table}" ) );
        }

        Assert.Equal( indexRows, factory.Scalar<long>( $"SELECT count(*) FROM {indexTable}" ) );
        Assert.Null( SliceRows.UnreadAt( factory, "bender_bender_and_kevin" ) );
        Assert.Equal( roomVersion, factory.Scalar<string>( $"SELECT updated_at FROM rooms WHERE id={room}" ) );
        Assert.Equal( membershipVersion, factory.Scalar<string>( $"SELECT updated_at FROM memberships WHERE id={membership}" ) );
        Assert.Equal( 0, SliceRows.StagedFiles( factory ) );
        Assert.Equal( fileCount, Directory.Exists( files ) ? Directory.GetFiles( files, "*", SearchOption.AllDirectories ).Length : 0 );
        Assert.Empty( factory.Events.Only( "upload", "push_job", "analyze_job", "broadcast", "unread", "webhook_job" ) );
        if ( originalFile is not null )
        {
            Assert.Equal( "uploaded file content"u8.ToArray( ), await File.ReadAllBytesAsync( originalFile, TestContext.Current.CancellationToken ) );
            Assert.Equal( originalBlob, factory.Scalar<string>( $"SELECT content_type || '|' || metadata FROM active_storage_blobs WHERE id={blobId}" ) );
        }
    }

    [Theory]
    [InlineData( 0 )]
    [InlineData( 64 )]
    public async Task Reads_started_after_acknowledgement_see_the_text_and_index( int budget )
    {
        using var factory = new ResponseCacheAppFactory( budget );
        var room = SliceAppFactory.Id( "bender_and_kevin" );
        using var client = await factory.SignedInClientAsync( "kevin@37signals.com" );
        var path = Routes.Room( room );
        var warm = await client.GetAsync( path );
        await client.GetAsync( path );
        await client.GetAsync( Routes.RoomMessages( room ) );
        const string body = "Fresh acknowledged message";
        var reply = await client.PostMessageAsync( room, SliceClient.MetaToken( warm.Body ), body: body, clientMessageId: "atomic-text-success" );
        Assert.Equal( HttpStatusCode.OK, reply.Status );
        Assert.Equal( 1, SliceRows.MessageCount( factory, "atomic-text-success" ) );
        var message = SliceRows.Message( factory, "atomic-text-success" );
        Assert.Equal( body, factory.Scalar<string>( $"SELECT body FROM message_search_index WHERE rowid={message.Id}" ) );
        Assert.Equal( message.CreatedAt, SliceRows.UnreadAt( factory, "bender_bender_and_kevin" ) );
        Assert.Contains( body, (await client.GetAsync( path )).Body, StringComparison.Ordinal );
        Assert.Contains( body, (await client.GetAsync( Routes.RoomMessages( room ) )).Body, StringComparison.Ordinal );
    }
}
