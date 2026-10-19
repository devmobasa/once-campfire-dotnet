using System.Text.RegularExpressions;

using Campfire.Application.Data.Records;
using Campfire.Application.Messages;
using Campfire.Application.Tests.Support;

namespace Campfire.Application.Tests.Messages;

/// <summary>
/// <c>test/models/message_test.rb</c>, <c>message/searchable_test.rb</c>, the data parts of
/// <c>message/attachment_test.rb</c>, and Rust's <c>message_test.rs</c>.
/// </summary>
public sealed partial class MessageTests
{
    private static Task<List<long>> SearchAsync( TestDatabase testDatabase, string room, string query ) =>
        testDatabase.ReadAsync( db => MessageSearchIndex.SearchInRoom( db, TestDatabase.Id( room ), query ).Select( message => message.Id ).ToList( ) );

    [Fact]
    public async Task Creating_a_message_enqueues_to_push_later( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );

        var message = await testDatabase.PostMessageAsync( "designers", "jason", "Hello", "123" );

        Assert.Equal( [$"push room {TestDatabase.Id( "designers" )} message {message.Id}"], testDatabase.Hooks.Events );
    }

    [Fact]
    public async Task Mentionees_are_mentioned_members_of_the_room_without_duplicates( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        async Task<List<long>> Mentioned( string body )
        {
            var message = await testDatabase.PostMessageAsync( "pets", "jason", body, Guid.NewGuid( ).ToString( ) );
            var mentionees = await testDatabase.ReadAsync( db => MessageBodies.Mentionees( db, testDatabase.BodyText, message ) );
            return [.. mentionees.Select( mentionee => mentionee.Id )];
        }

        var david = TestDatabase.Id( "david" );
        Assert.Equal( [david], await Mentioned( $"<div>Hey {BasicBodyText.MentionAttachmentFor( david )}</div>" ) );
        Assert.Equal(
            [david],
            await Mentioned( $"<div>Hey {BasicBodyText.MentionAttachmentFor( david )} {BasicBodyText.MentionAttachmentFor( david )}</div>" ) );
        Assert.Empty( await Mentioned( $"<div>Hey {BasicBodyText.MentionAttachmentFor( TestDatabase.Id( "kevin" ) )}</div>" ) );
    }

    [Fact]
    public async Task Message_body_is_indexed_and_searchable( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        var message = await testDatabase.PostMessageAsync( "designers", "david", "My hovercraft is full of eels", "earth" );
        Assert.Equal( [message.Id], await SearchAsync( testDatabase, "designers", "eel" ) );

        await testDatabase.WriteAsync( scope => MessageOperations.Update( scope, message.Id, "My hovercraft is full of sharks" ) );
        Assert.Equal( [message.Id], await SearchAsync( testDatabase, "designers", "sharks" ) );

        await testDatabase.WriteAsync( scope => MessageOperations.Destroy( scope, message.Id ) );
        Assert.Empty( await SearchAsync( testDatabase, "designers", "sharks" ) );
    }

    [Fact]
    public async Task Search_results_are_returned_in_message_order( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        var ids = new List<long>( );
        foreach ( var body in new[] { "first cat", "second cat", "third cat", "cat cat cat" } )
        {
            testDatabase.Clock.Travel( TimeSpan.FromSeconds( 1 ) );
            ids.Add( (await testDatabase.PostMessageAsync( "designers", "david", body, body )).Id );
        }

        Assert.Equal( ids, await SearchAsync( testDatabase, "designers", "cat" ) );
    }

    [Fact]
    public async Task Rich_text_body_is_converted_to_plain_text_for_indexing( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );

        var message = await testDatabase.PostMessageAsync( "designers", "david", "<span>My hovercraft is full of eels</span>", "earth" );

        Assert.Empty( await SearchAsync( testDatabase, "designers", "span" ) );
        Assert.Equal( [message.Id], await SearchAsync( testDatabase, "designers", "eel" ) );
    }

    [Fact]
    public async Task Reachable_search_finds_only_messages_in_the_users_rooms( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        var message = await testDatabase.PostMessageAsync( "designers", "david", "hovercraft", "earth" );

        var reachable = await testDatabase.ReadAsync( db => MessageSearchIndex.SearchReachable( db, TestDatabase.Id( "kevin" ), "hovercraft" ) );
        Assert.Equal( [message.Id], reachable.Select( match => match.Id ) );
        Assert.Empty( await testDatabase.ReadAsync( db => MessageSearchIndex.SearchReachable( db, TestDatabase.Id( "bender" ), "hovercraft" ) ) );
    }

    [Fact]
    public async Task A_blank_message_with_an_attachment_uses_the_filename_as_plain_text_body( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        var attachment = new NewAttachment
        {
            Key = "abc123",
            Filename = "moon.jpg",
            ContentType = "image/jpeg",
            ServiceName = "local",
            ByteSize = 1,
            UploadSource = "file",
        };

        var created = await testDatabase.WriteAsync( scope => MessageOperations.Create( scope, new NewMessage
        {
            RoomId = TestDatabase.Id( "hq" ),
            CreatorId = TestDatabase.Id( "david" ),
            ClientMessageId = "message",
            Attachment = attachment,
        } ) );

        Assert.Equal( "moon.jpg", await testDatabase.ReadAsync( db => MessageBodies.PlainText( db, testDatabase.BodyText, created.Message.Id ) ) );
        Assert.Equal(
            MessageContentType.Attachment,
            await testDatabase.ReadAsync( db => MessageBodies.ContentType( db, testDatabase.BodyText, created.Message.Id ) ) );
        Assert.Equal( [created.Message.Id], await SearchAsync( testDatabase, "hq", "moon" ) );
        Assert.Equal( ("Message", "attachment"), (created.Attachment!.RecordType, created.Attachment.Name) );
    }

    [Fact]
    public async Task A_play_command_for_a_known_sound_makes_a_sound_message( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );

        var trombone = await testDatabase.PostMessageAsync( "designers", "david", "/play trombone", "x" );
        var unknown = await testDatabase.PostMessageAsync( "designers", "david", "/play nosuchsound", "y" );

        Assert.Equal( MessageContentType.Sound, await testDatabase.ReadAsync( db => MessageBodies.ContentType( db, testDatabase.BodyText, trombone.Id ) ) );
        Assert.Equal( "trombone", (await testDatabase.ReadAsync( db => MessageBodies.Sound( db, testDatabase.BodyText, trombone.Id ) ))!.Name );
        Assert.Equal( MessageContentType.Text, await testDatabase.ReadAsync( db => MessageBodies.ContentType( db, testDatabase.BodyText, unknown.Id ) ) );
        Assert.Null( Sounds.FromPlainText( "/play trombone\n" ) );
        Assert.Null( Sounds.FromPlainText( "/play tromböne" ) );
    }

    [Fact]
    public async Task Client_message_id_defaults_to_a_uuid( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );

        var message = await testDatabase.PostMessageAsync( "hq", "bender", "beep" );

        Assert.Matches( UuidPattern( ), message.ClientMessageId );
    }

    [Fact]
    public void The_builtin_sounds_are_sound_rb_exactly( )
    {
        var source = RepositoryPaths.ReadReferenceText( "app", "models", "sound.rb" );
        var expected = SoundDefinition( ).Matches( source )
            .Select( match => match.Groups["text"].Success
                ? new Sound( match.Groups["name"].Value, match.Groups["text"].Value, null )
                : new Sound(
                    match.Groups["name"].Value,
                    null,
                    new SoundImage( match.Groups["file"].Value, int.Parse( match.Groups["width"].Value ), int.Parse( match.Groups["height"].Value ) ) ) )
            .ToList( );

        Assert.Equal( 56, expected.Count );
        Assert.Equal( expected, Sounds.Builtin );
    }

    [GeneratedRegex( "^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$" )]
    private static partial Regex UuidPattern( );

    [GeneratedRegex( "new\\(name: \"(?<name>[^\"]+)\", " +
        "(?:text: \"(?<text>[^\"]*)\"|image: \\{ name: \"(?<file>[^\"]+)\", width: (?<width>\\d+), height: (?<height>\\d+) \\})\\)" )]
    private static partial Regex SoundDefinition( );
}
