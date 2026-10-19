using Campfire.Application.Data;
using Campfire.Application.Data.Records;
using Campfire.Application.Memberships;
using Campfire.Application.Messages;
using Campfire.Application.Rooms;
using Campfire.Application.Tests.Support;

namespace Campfire.Application.Tests.Messages;

/// <summary>The message rows of message operations, and its failure cases.</summary>
public sealed class MessageSequenceTests
{
    private static NewAttachment Moon => new( )
    {
        Key = "abc123",
        Filename = "moon.jpg",
        ContentType = "image/jpeg",
        Metadata = "{\"identified\":true}",
        ServiceName = "local",
        ByteSize = 1,
        UploadSource = "moon.jpg",
    };

    private static NewMessage InDesigners( string body = "Hello <b>there</b>", NewAttachment? attachment = null ) => new( )
    {
        RoomId = TestDatabase.Id( "designers" ),
        CreatorId = TestDatabase.Id( "david" ),
        ClientMessageId = "abc",
        Body = body,
        Attachment = attachment,
    };

    [Fact]
    public async Task Text_push_observes_committed_index_unread_and_touches_despite_cancellation( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        using var side = testDatabase.OpenSideConnection( );
        using var request = new CancellationTokenSource( );
        testDatabase.Clock.Travel( TimeSpan.FromSeconds( 1 ) );
        var observed = false;
        testDatabase.Hooks.Intercept = line =>
        {
            if ( !line.StartsWith( "push", StringComparison.Ordinal ) )
            {
                return;
            }

            Assert.Equal( "Hello there", Sql.Scalar( side, "SELECT body FROM message_search_index" ) );
            Assert.NotEqual( DBNull.Value, Sql.Scalar( side, $"SELECT unread_at FROM memberships WHERE id={TestDatabase.Id( "jason_designers" )}" ) );
            Assert.Equal(
                1L,
                Sql.Scalar(
                    side,
                    "SELECT count(*) FROM messages m JOIN rooms r ON r.id=m.room_id WHERE m.client_message_id='abc' AND r.updated_at>=m.updated_at" ) );
            observed = true;
            request.Cancel( );
        };
        var created = await testDatabase.Database.WriteAsync( scope => MessageOperations.Create( scope, InDesigners( ) ), request.Token );
        Assert.True( observed );
        Assert.True( request.IsCancellationRequested );
        Assert.Equal( created.Message.Id, await testDatabase.ScalarAsync<long>( "SELECT id FROM messages WHERE client_message_id='abc'" ) );
    }

    [Fact]
    public async Task Text_cancellation_while_waiting_for_the_writer_creates_nothing( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        using var held = await testDatabase.Database.AcquireWriterAsync( TestDatabase.Cancellation );
        using var request = new CancellationTokenSource( );
        var post = testDatabase.Database.WriteAsync( scope => MessageOperations.Create( scope, InDesigners( ) ), request.Token );
        Assert.False( post.IsCompleted );
        request.Cancel( );
        await Assert.ThrowsAnyAsync<OperationCanceledException>( async ( ) => await post );
        Assert.Equal( 0, await testDatabase.CountAsync( "messages", "client_message_id=@p0", "abc" ) );
        Assert.Empty( testDatabase.Hooks.Events );
    }

    [Fact]
    public async Task Attachment_upload_observes_committed_index_unread_and_touches( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        using var side = testDatabase.OpenSideConnection( );
        await testDatabase.WriteAsync( scope => MembershipPresence.Connected( scope, TestDatabase.Id( "jz_designers" ) ) );
        string? indexedAtUpload = null;
        object? unreadAtUpload = null;
        object? createdAtUpload = null;
        object? committedRowsAtUpload = null;
        testDatabase.Hooks.Intercept = line =>
        {
            if ( line.StartsWith( "upload", StringComparison.Ordinal ) )
            {
                indexedAtUpload = ( string? ) Sql.Scalar( side, "SELECT body FROM message_search_index" );
                unreadAtUpload = Sql.Scalar( side, $"SELECT unread_at FROM memberships WHERE id = {TestDatabase.Id( "jason_designers" )}" );
                createdAtUpload = Sql.Scalar( side, "SELECT created_at FROM messages WHERE client_message_id='abc'" );
                committedRowsAtUpload = Sql.Scalar( side, """
                    SELECT count(*) FROM messages m JOIN rooms r ON r.id=m.room_id
                    JOIN active_storage_attachments a ON a.record_type='Message' AND a.record_id=m.id
                    WHERE m.client_message_id='abc' AND r.updated_at>=m.updated_at
                    """ );
            }
        };

        var created = await testDatabase.WriteAsync( scope => MessageOperations.Create( scope, InDesigners( attachment: Moon ) ) );

        var blobId = created.Blob!.Id;
        Assert.Equal(
            [$"upload blob {blobId} from moon.jpg", $"push room {TestDatabase.Id( "designers" )} message {created.Message.Id}", $"analyze blob {blobId}"],
            testDatabase.Hooks.Events );
        Assert.Equal( "Hello there", indexedAtUpload );
        Assert.NotNull( createdAtUpload );
        Assert.Equal( createdAtUpload, unreadAtUpload );
        Assert.Equal( 1L, committedRowsAtUpload );
    }

    [Fact]
    public async Task Receiving_marks_visible_disconnected_members_other_than_the_creator_unread( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        await testDatabase.WriteAsync( scope => MembershipPresence.Connected( scope, TestDatabase.Id( "jz_designers" ) ) );
        var roomBefore = await testDatabase.ReadAsync( db => RoomQueries.FindById( db, TestDatabase.Id( "designers" ) )! );
        testDatabase.Clock.Travel( TimeSpan.FromSeconds( 1 ) );

        var message = (await testDatabase.WriteAsync( scope => MessageOperations.Create( scope, InDesigners( ) ) )).Message;

        async Task<DateTime?> Unread( string label ) => (await testDatabase.ReadAsync( db => MembershipQueries.Find( db, TestDatabase.Id( label ) ) )).UnreadAt;
        Assert.Equal( message.CreatedAt, await Unread( "jason_designers" ) );
        Assert.Equal( message.CreatedAt, await Unread( "kevin_designers" ) );
        Assert.Null( await Unread( "jz_designers" ) );
        Assert.Null( await Unread( "david_designers" ) );
        Assert.True( (await testDatabase.ReadAsync( db => RoomQueries.FindById( db, roomBefore.Id )! )).UpdatedAt > roomBefore.UpdatedAt );
        Assert.True( message.UpdatedAt >= message.CreatedAt );
        var body = await testDatabase.ReadAsync( db => MessageBodies.Body( db, message.Id ) );
        var recordType = await testDatabase.ScalarAsync<string>( "SELECT record_type FROM action_text_rich_texts WHERE record_id = @p0", message.Id );
        Assert.Equal( ("Hello <b>there</b>", "Message"), (body, recordType) );
    }

    [Theory]
    [InlineData( "designers", "david", "jason_designers", false )]
    [InlineData( "bender_and_kevin", "kevin", "bender_bender_and_kevin", true )]
    public async Task Only_direct_rooms_refresh_an_already_unread_membership( string room, string creator, string membership, bool direct )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        var attributes = InDesigners( ) with { RoomId = TestDatabase.Id( room ), CreatorId = TestDatabase.Id( creator ) };
        var first = await testDatabase.WriteAsync( scope => MessageOperations.Create( scope, attributes ) );
        var before = await testDatabase.ReadAsync( db => MembershipQueries.Find( db, TestDatabase.Id( membership ) ) );
        testDatabase.Clock.Travel( TimeSpan.FromSeconds( 1 ) );

        var second = await testDatabase.WriteAsync( scope => MessageOperations.Create( scope, attributes ) );
        var after = await testDatabase.ReadAsync( db => MembershipQueries.Find( db, before.Id ) );

        Assert.Equal( first.Message.CreatedAt, before.UnreadAt );
        Assert.Equal( direct ? second.Message.CreatedAt : before.UnreadAt, after.UnreadAt );
        if ( direct )
        {
            Assert.True( after.UpdatedAt > before.UpdatedAt );
        }
        else
        {
            Assert.Equal( before.UpdatedAt, after.UpdatedAt );
        }
    }

    [Fact]
    public async Task Invisible_members_are_not_marked_unread( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        await testDatabase.WriteAsync( scope => MembershipOperations.UpdateInvolvement( scope, TestDatabase.Id( "kevin_designers" ), Involvement.Invisible ) );

        await testDatabase.WriteAsync( scope => MessageOperations.Create( scope, InDesigners( "x" ) ) );

        Assert.Null( (await testDatabase.ReadAsync( db => MembershipQueries.Find( db, TestDatabase.Id( "kevin_designers" ) ) )).UnreadAt );
    }

    [Fact]
    public async Task A_null_involvement_is_left_out_of_unread_as_rails_sql_where_not_leaves_it_out( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        using ( var side = testDatabase.OpenSideConnection( ) )
        {
            Sql.Execute( side, $"UPDATE memberships SET involvement = NULL WHERE id = {TestDatabase.Id( "kevin_designers" )}" );
        }

        await testDatabase.WriteAsync( scope => MessageOperations.Create( scope, InDesigners( "x" ) ) );

        Assert.Null( (await testDatabase.ReadAsync( db => MembershipQueries.Find( db, TestDatabase.Id( "kevin_designers" ) ) )).UnreadAt );
        Assert.NotNull( (await testDatabase.ReadAsync( db => MembershipQueries.Find( db, TestDatabase.Id( "jason_designers" ) ) )).UnreadAt );
    }

    [Fact]
    public async Task Cancelling_the_request_after_commit_still_runs_every_after_commit_step( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        using var request = new CancellationTokenSource( );
        testDatabase.Hooks.Intercept = line =>
        {
            if ( line.StartsWith( "upload", StringComparison.Ordinal ) )
            {
                request.Cancel( );
            }
        };

        await testDatabase.Database.WriteAsync( scope => MessageOperations.Create( scope, InDesigners( attachment: Moon ) ), request.Token );

        Assert.True( request.IsCancellationRequested );
        Assert.Equal( ["upload", "push", "analyze"], testDatabase.Hooks.Events.Select( line => line.Split( ' ' )[0] ) );
        Assert.NotNull( (await testDatabase.ReadAsync( db => MembershipQueries.Find( db, TestDatabase.Id( "jason_designers" ) ) )).UnreadAt );
    }

    [Fact]
    public async Task A_rolled_back_create_emits_nothing( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );

        await Assert.ThrowsAsync<InvalidOperationException>( ( ) => testDatabase.WriteAsync( scope =>
        {
            MessageOperations.Create( scope, InDesigners( attachment: Moon ) );
            throw new InvalidOperationException( "boom" );
        } ) );

        Assert.Empty( testDatabase.Hooks.Events );
        Assert.Equal( 13, await testDatabase.CountAsync( "messages" ) );
        Assert.Equal( 0, await testDatabase.CountAsync( "active_storage_blobs" ) );
    }

    [Fact]
    public async Task Destroying_runs_the_index_delete_then_the_blob_purge( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        using var side = testDatabase.OpenSideConnection( );
        var created = await testDatabase.WriteAsync( scope => MessageOperations.Create( scope, InDesigners( attachment: Moon ) ) );
        await testDatabase.WriteAsync( scope => BoostOperations.Create( scope, created.Message.Id, TestDatabase.Id( "jason" ), "hi" ) );
        testDatabase.Hooks.Clear( );
        long? indexedAtPurge = null;
        testDatabase.Hooks.Intercept = line => indexedAtPurge = ( long? ) Sql.Scalar( side, "SELECT COUNT(*) FROM message_search_index" );

        await testDatabase.WriteAsync( scope => MessageOperations.Destroy( scope, created.Message.Id ) );

        Assert.Equal( [$"purge blob {created.Blob!.Id}"], testDatabase.Hooks.Events );
        Assert.Equal( 0, indexedAtPurge );
        Assert.Equal( 0, await testDatabase.CountAsync( "active_storage_attachments" ) );
        Assert.Equal( 0, await testDatabase.CountAsync( "boosts", "message_id = @p0", created.Message.Id ) );
        Assert.Equal( 0, await testDatabase.CountAsync( "action_text_rich_texts", "record_id = @p0", created.Message.Id ) );
        Assert.Equal( 1, await testDatabase.CountAsync( "active_storage_blobs" ) );
    }

    [Fact]
    public async Task Updating_the_body_touches_the_message_and_room_and_reindexes_after_commit( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        var message = (await testDatabase.WriteAsync( scope => MessageOperations.Create( scope, InDesigners( "My hovercraft is full of eels" ) ) )).Message;
        testDatabase.Clock.Travel( TimeSpan.FromSeconds( 1 ) );

        var updated = await testDatabase.WriteAsync( scope => MessageOperations.Update( scope, message.Id, "My hovercraft is full of sharks" ) );

        // The room's deferred touch takes its time after the message's, as touch_later's parent chain does.
        var stored = await testDatabase.ReadAsync( db => MessageQueries.Find( db, message.Id ) );
        Assert.True( stored.UpdatedAt > message.UpdatedAt );
        Assert.True( (await testDatabase.ReadAsync( db => RoomQueries.FindById( db, message.RoomId )! )).UpdatedAt >= stored.UpdatedAt );
        Assert.Equal( "My hovercraft is full of sharks", await testDatabase.ReadAsync( db => MessageSearchIndex.IndexedBody( db, message.Id ) ) );
        Assert.Equal( message.Id, updated.Id );
    }

    [Fact]
    public async Task Replacing_the_attachment_uploads_then_purges_the_old_blob_then_analyzes_the_new_one( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        var created = await testDatabase.WriteAsync( scope => MessageOperations.Create( scope, InDesigners( attachment: Moon ) ) );
        testDatabase.Hooks.Clear( );
        var earth = Moon with
        {
            Key = "def456",
            Filename = "earth.png",
            UploadSource = "earth.png"
        };

        await testDatabase.WriteAsync( scope => MessageOperations.Update( scope, created.Message.Id, default, attachment: earth ) );

        var blob = (await testDatabase.ReadAsync( db => MessageBodies.Attachment( db, created.Message.Id ) ))!.Value.Blob;
        Assert.Equal( "def456", blob.Key );
        Assert.Equal( [$"upload blob {blob.Id} from earth.png", $"purge blob {created.Blob!.Id}", $"analyze blob {blob.Id}"], testDatabase.Hooks.Events );
        Assert.Equal( "earth.png", blob.Filename );
    }

    [Theory]
    [InlineData( "body" )]
    [InlineData( "client" )]
    [InlineData( "empty" )]
    public async Task Saving_without_changed_indexed_text_leaves_the_index_alone( string assignment )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        var message = (await testDatabase.WriteAsync( scope => MessageOperations.Create( scope, InDesigners( "cats" ) ) )).Message;
        await testDatabase.ExecuteAsync( "UPDATE message_search_index SET body = 'stale' WHERE rowid = @p0", message.Id );

        await testDatabase.WriteAsync( scope => MessageOperations.Update(
            scope, message.Id,
            assignment == "body" ? new Assigned<string?>( "cats" ) : default,
            assignment == "client" ? new Assigned<string>( "renamed" ) : default ) );

        Assert.Equal( "stale", await testDatabase.ReadAsync( db => MessageSearchIndex.IndexedBody( db, message.Id ) ) );
    }

    [Fact]
    public async Task Boosting_touches_the_message_and_room_without_reindexing( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        var message = (await testDatabase.WriteAsync( scope => MessageOperations.Create( scope, InDesigners( "cats" ) ) )).Message;
        using ( var side = testDatabase.OpenSideConnection( ) )
        {
            Sql.Execute( side, $"UPDATE message_search_index SET body = 'stale' WHERE rowid = {message.Id}" );
        }

        testDatabase.Clock.Travel( TimeSpan.FromSeconds( 1 ) );
        var boost = await testDatabase.WriteAsync( scope => BoostOperations.Create( scope, message.Id, TestDatabase.Id( "jason" ), "hi" ) );

        var touched = await testDatabase.ReadAsync( db => MessageQueries.Find( db, message.Id ) );
        Assert.True( touched.UpdatedAt > message.UpdatedAt );
        Assert.True( (await testDatabase.ReadAsync( db => RoomQueries.FindById( db, message.RoomId )! )).UpdatedAt >= touched.UpdatedAt );
        Assert.Equal( "stale", await testDatabase.ReadAsync( db => MessageSearchIndex.IndexedBody( db, message.Id ) ) );

        testDatabase.Clock.Travel( TimeSpan.FromSeconds( 1 ) );
        await testDatabase.WriteAsync( scope => BoostOperations.Destroy( scope, boost.Id ) );
        Assert.True( (await testDatabase.ReadAsync( db => MessageQueries.Find( db, message.Id ) )).UpdatedAt > touched.UpdatedAt );
        Assert.Equal( "stale", await testDatabase.ReadAsync( db => MessageSearchIndex.IndexedBody( db, message.Id ) ) );
    }

    [Fact]
    public async Task Boosting_a_never_indexed_fixture_message_leaves_it_unindexed_as_rails_does( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        var first = await testDatabase.ReadAsync( db => MessageQueries.Find( db, TestDatabase.Id( "first" ) ) );
        testDatabase.Clock.Travel( TimeSpan.FromSeconds( 1 ) );

        await testDatabase.WriteAsync( scope => BoostOperations.Create( scope, first.Id, TestDatabase.Id( "jason" ), "hi" ) );

        Assert.Null( await testDatabase.ReadAsync( db => MessageSearchIndex.IndexedBody( db, first.Id ) ) );
        Assert.True( (await testDatabase.ReadAsync( db => MessageQueries.Find( db, first.Id ) )).UpdatedAt > first.UpdatedAt );
    }
}
