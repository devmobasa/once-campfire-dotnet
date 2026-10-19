using Campfire.Application.Data;
using Campfire.Application.Data.Records;
using Campfire.Application.Messages;
using Campfire.Application.Rooms;
using Campfire.Application.Storage;
using Campfire.Application.Tests.Support;

namespace Campfire.Application.Tests.Messages;

/// <summary>
/// <c>message.attachment = value</c> for a signed blob id (a direct upload, <c>CreateOne</c> with a <c>String</c>) and for
/// <c>nil</c>/<c>""</c> (<c>DeleteOne</c>): the rows, the identification a new blob gets, and the commit callbacks.
/// </summary>
public sealed class MessageAttachmentTests
{
    private static readonly BlobIdentity Identified = new( "image/jpeg", """{"identified":true}""" );

    private static NewAttachment Moon => new( )
    {
        Key = "abc123",
        Filename = "moon.jpg",
        ContentType = "image/jpeg",
        Metadata = """{"identified":true}""",
        ServiceName = "local",
        ByteSize = 1,
        UploadSource = "moon.jpg",
    };

    private static NewMessage InDesigners( string? body = "Hello <b>there</b>" ) => new( )
    {
        RoomId = TestDatabase.Id( "designers" ),
        CreatorId = TestDatabase.Id( "david" ),
        ClientMessageId = "direct",
        Body = body,
    };

    [Fact]
    public async Task A_signed_blob_is_attached_identified_and_analyzed_later_without_an_upload( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        var blob = await DirectUploadAsync( testDatabase );
        testDatabase.Hooks.Clear( );

        var created = await testDatabase.WriteAsync( scope => MessageOperations.Create(
            scope,
            InDesigners( ) with { AttachedBlob = new SignedBlobAttachment( blob.Id, Identified ) } ) );

        Assert.Equal( [$"push room {TestDatabase.Id( "designers" )} message {created.Message.Id}", $"analyze blob {blob.Id}"], testDatabase.Hooks.Events );
        var attached = await testDatabase.ReadAsync( db => MessageBodies.Attachment( db, created.Message.Id )!.Value );
        Assert.Equal( (blob.Id, "image/jpeg", """{"identified":true}"""), (attached.Blob.Id, attached.Blob.ContentType, attached.Blob.Metadata) );
        Assert.Equal( (blob.Id, "image/jpeg"), (created.Blob!.Id, created.Blob.ContentType) );
        Assert.Equal( "Hello there", await testDatabase.ReadAsync( db => MessageSearchIndex.IndexedBody( db, created.Message.Id ) ) );
        Assert.True( created.Message.UpdatedAt >= created.Message.CreatedAt );
    }

    [Fact]
    public async Task An_identified_and_analyzed_blob_is_attached_as_it_is( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        var blob = await DirectUploadAsync( testDatabase, """{"identified":true,"analyzed":true}""" );
        testDatabase.Hooks.Clear( );

        var created = await testDatabase.WriteAsync( scope => MessageOperations.Create(
            scope,
            InDesigners( ) with { AttachedBlob = new SignedBlobAttachment( blob.Id, null ) } ) );

        Assert.Equal( [$"push room {TestDatabase.Id( "designers" )} message {created.Message.Id}"], testDatabase.Hooks.Events );
        var stored = await testDatabase.ReadAsync( db => BlobRecords.FindById( db, blob.Id )! );
        Assert.Equal( ("application/octet-stream", """{"identified":true,"analyzed":true}"""), (stored.ContentType, stored.Metadata.ToJson( )) );
    }

    [Fact]
    public async Task Attaching_a_signed_blob_on_update_purges_the_old_blob_then_analyzes_the_new_one( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        var created = await testDatabase.WriteAsync( scope => MessageOperations.Create( scope, InDesigners( "" ) with { Attachment = Moon } ) );
        var blob = await DirectUploadAsync( testDatabase );
        await testDatabase.ExecuteAsync( "UPDATE active_storage_blobs SET filename = 'earth.jpg' WHERE id = @p0", blob.Id );
        testDatabase.Hooks.Clear( );
        testDatabase.Clock.Travel( TimeSpan.FromSeconds( 1 ) );

        await testDatabase.WriteAsync( scope =>
        {
            MessageOperations.Update( scope, created.Message.Id, default );
            MessageAttachments.AttachBlob( scope, created.Message.Id, new SignedBlobAttachment( blob.Id, Identified ) );
        } );

        Assert.Equal( [$"purge blob {created.Blob!.Id}", $"analyze blob {blob.Id}"], testDatabase.Hooks.Events );
        Assert.Equal( "earth.jpg", await testDatabase.ReadAsync( db => MessageSearchIndex.IndexedBody( db, created.Message.Id ) ) );
        Assert.Equal( blob.Id, await testDatabase.ReadAsync( db => MessageBodies.Attachment( db, created.Message.Id )!.Value.Blob.Id ) );
        Assert.True( (await testDatabase.ReadAsync( db => MessageQueries.Find( db, created.Message.Id ) )).UpdatedAt > created.Message.UpdatedAt );
    }

    [Fact]
    public async Task Attaching_the_blob_a_message_already_has_keeps_its_attachment_and_touches_nothing( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        var blob = await DirectUploadAsync( testDatabase );
        var created = await testDatabase.WriteAsync( scope => MessageOperations.Create(
            scope,
            InDesigners( ) with { AttachedBlob = new SignedBlobAttachment( blob.Id, Identified ) } ) );
        var before = await testDatabase.ReadAsync( db => MessageQueries.Find( db, created.Message.Id ) );
        await testDatabase.ExecuteAsync( "UPDATE message_search_index SET body = 'stale' WHERE rowid = @p0", created.Message.Id );
        testDatabase.Hooks.Clear( );
        testDatabase.Clock.Travel( TimeSpan.FromSeconds( 1 ) );

        var attached = await testDatabase.WriteAsync( scope => MessageAttachments.AttachBlob(
            scope,
            created.Message.Id,
            new SignedBlobAttachment( blob.Id, null ) ) );

        Assert.False( attached.Created );
        Assert.Equal( "Hello there", await testDatabase.ReadAsync( db => MessageSearchIndex.IndexedBody( db, created.Message.Id ) ) );
        Assert.Empty( testDatabase.Hooks.Events );
        Assert.Equal( created.Attachment!.Id, await testDatabase.ReadAsync( db => MessageBodies.Attachment( db, created.Message.Id )!.Value.Attachment.Id ) );
        Assert.Equal( before.UpdatedAt, (await testDatabase.ReadAsync( db => MessageQueries.Find( db, created.Message.Id ) )).UpdatedAt );
    }

    [Fact]
    public async Task Removing_an_absent_attachment_reindexes_without_touching_the_message( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        var created = await testDatabase.WriteAsync( scope => MessageOperations.Create( scope, InDesigners( "cats" ) ) );
        await testDatabase.ExecuteAsync( "UPDATE message_search_index SET body = 'stale' WHERE rowid = @p0", created.Message.Id );
        testDatabase.Clock.Travel( TimeSpan.FromSeconds( 1 ) );

        var removed = await testDatabase.WriteAsync( scope => MessageAttachments.Detach( scope, created.Message.Id ) );

        Assert.False( removed );
        Assert.Equal( "cats", await testDatabase.ReadAsync( db => MessageSearchIndex.IndexedBody( db, created.Message.Id ) ) );
        Assert.Equal( created.Message.UpdatedAt, (await testDatabase.ReadAsync( db => MessageQueries.Find( db, created.Message.Id ) )).UpdatedAt );
    }

    [Fact]
    public async Task Detaching_destroys_the_attachment_purges_its_blob_later_and_touches_the_message_and_room( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        var created = await testDatabase.WriteAsync( scope => MessageOperations.Create( scope, InDesigners( "" ) with { Attachment = Moon } ) );
        var roomBefore = await testDatabase.ReadAsync( db => RoomQueries.FindById( db, created.Message.RoomId )! );
        testDatabase.Hooks.Clear( );
        testDatabase.Clock.Travel( TimeSpan.FromSeconds( 1 ) );

        var detached = await testDatabase.WriteAsync( scope => MessageAttachments.Detach( scope, created.Message.Id ) );
        var again = await testDatabase.WriteAsync( scope => MessageAttachments.Detach( scope, created.Message.Id ) );

        Assert.Equal( "", await testDatabase.ReadAsync( db => MessageSearchIndex.IndexedBody( db, created.Message.Id ) ) );
        Assert.True( detached );
        Assert.False( again );
        Assert.Equal( [$"purge blob {created.Blob!.Id}"], testDatabase.Hooks.Events );
        Assert.Null( await testDatabase.ReadAsync( db => MessageBodies.Attachment( db, created.Message.Id ) ) );
        Assert.True( (await testDatabase.ReadAsync( db => MessageQueries.Find( db, created.Message.Id ) )).UpdatedAt > created.Message.UpdatedAt );
        Assert.True( (await testDatabase.ReadAsync( db => RoomQueries.FindById( db, created.Message.RoomId )! )).UpdatedAt > roomBefore.UpdatedAt );
    }

    [Fact]
    public async Task A_blob_that_is_gone_rolls_the_create_back( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        var messages = await testDatabase.CountAsync( "messages" );

        await Assert.ThrowsAsync<RecordNotFoundException>( ( ) => testDatabase.WriteAsync( scope => MessageOperations.Create(
            scope,
            InDesigners( ) with { AttachedBlob = new SignedBlobAttachment( 987654, Identified ) } ) ) );

        Assert.Equal( messages, await testDatabase.CountAsync( "messages" ) );
        Assert.Empty( testDatabase.Hooks.Events );
    }

    /// <summary>A blob as <c>create_before_direct_upload!</c> leaves it: not identified, no file behind it here.</summary>
    private static Task<ActiveStorageBlob> DirectUploadAsync( TestDatabase testDatabase, string metadata = "{}" ) =>
        testDatabase.WriteAsync( scope => DirectUploads.CreateBlob(
            scope,
            new DirectUploadBlob( "moon.jpg", 1234, "q9xRlxbyA0+6tbj/YKSfgw==", "application/octet-stream", metadata ),
            "local" ) );
}
