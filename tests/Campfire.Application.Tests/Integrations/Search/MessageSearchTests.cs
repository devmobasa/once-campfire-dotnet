using System.Text;

using Campfire.Application.Integrations.Search;
using Campfire.Application.Memberships;
using Campfire.Application.Messages;
using Campfire.Application.Ruby;
using Campfire.Application.Tests.Support;

using Microsoft.Data.Sqlite;

namespace Campfire.Application.Tests.Integrations.Search;

/// <summary>
/// <c>SearchesController</c>'s query sanitizing (probed against the reference's Ruby by the Rust port), and
/// <c>test/controllers/searches_controller_test.rb</c> at the application boundary.
/// </summary>
public sealed class MessageSearchTests
{
    private static Task<Message> PostAsync( TestDatabase testDatabase, string room, string body, string creator = "david" ) =>
        testDatabase.PostMessageAsync( room, creator, body, clientMessageId: "search" );

    private static Task<SearchResults> FindAsync( TestDatabase testDatabase, string? query, string user = "david" ) =>
        testDatabase.ReadAsync( db => MessageSearch.Find( db, TestDatabase.Id( user ), query ) );

    [Fact]
    public void Keeps_only_word_characters_like_ruby( )
    {
        Assert.Equal( "héllo wörld_1 ２ 日本語 ‿ a b ️   é", SearchQuery.Sanitize( "héllo wörld_1 ２ 日本語 ‿ a-b ️ ❤ é" ) );
        Assert.Equal( " quoted  OR NEAR x  ", SearchQuery.Sanitize( "\"quoted\" OR NEAR(x*)" ) );
        Assert.Equal( "", SearchQuery.Sanitize( "" ) );
        Assert.Null( SearchQuery.Sanitize( null ) );
    }

    [Fact]
    public void Replaces_each_character_with_one_space_even_outside_the_bmp( )
    {
        Assert.Equal( "a b", SearchQuery.Sanitize( "a😀b" ) );
        Assert.Equal( "a b c", SearchQuery.Sanitize( "a\0b\ud800c" ) );
        Assert.Equal( "𝒳", SearchQuery.Sanitize( "𝒳" ) );
    }

    [Theory]
    [InlineData( "a" )]
    [InlineData( "Z" )]
    [InlineData( "0" )]
    [InlineData( "_" )]
    [InlineData( "é" )]
    [InlineData( "ß" )]
    [InlineData( "日" )]
    [InlineData( "‿" )]
    [InlineData( "́" )]
    [InlineData( "٣" )]
    [InlineData( "ǅ" )]
    [InlineData( "ʰ" )]
    [InlineData( "Ⅻ" )]
    [InlineData( "Ⓐ" )]
    [InlineData( "‍" )]
    public void Classifies_word_characters_like_onigmo( string character ) =>
        Assert.True( OnigmoWord.Contains( Rune.GetRuneAt( character, 0 ) ) );

    [Theory]
    [InlineData( " " )]
    [InlineData( "-" )]
    [InlineData( "*" )]
    [InlineData( "\"" )]
    [InlineData( " " )]
    [InlineData( "❤" )]
    [InlineData( "€" )]
    [InlineData( "½" )]
    [InlineData( "²" )]
    public void Classifies_other_characters_like_onigmo( string character ) =>
        Assert.False( OnigmoWord.Contains( Rune.GetRuneAt( character, 0 ) ) );

    [Fact]
    public async Task Finds_reachable_messages_and_nothing_for_a_blank_query( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        var message = await PostAsync( testDatabase, "designers", "Hello world!" );

        var found = await FindAsync( testDatabase, "hello" );
        Assert.Equal( "hello", found.Query );
        Assert.Equal( [message.Id], found.Messages.Select( match => match.Id ) );

        foreach ( var blank in new[] { null, "", "  ", "!?*" } )
        {
            var none = await FindAsync( testDatabase, blank );
            Assert.Null( none.Query );
            Assert.Empty( none.Messages );
        }
    }

    [Fact]
    public async Task Punctuation_never_reaches_fts5_as_syntax( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        var message = await PostAsync( testDatabase, "designers", "Do NOT feed the eel OR the shark" );

        foreach ( var query in new[] { "\"eel", "eel*", "(eel)", "eel:", "shark:eel", "eel\0", "-eel", "^eel", "eel +shark" } )
        {
            Assert.Equal( [message.Id], (await FindAsync( testDatabase, query )).Messages.Select( match => match.Id ) );
        }
    }

    [Theory]
    [InlineData( "NOT" )]
    [InlineData( "OR" )]
    [InlineData( "AND" )]
    [InlineData( "eel NOT" )]
    [InlineData( "NOT eel" )]
    [InlineData( "salt AND" )]
    public async Task Operator_words_are_matched_as_literal_terms( string query )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        var message = await PostAsync( testDatabase, "designers", "Do NOT feed the eel OR the shark salt AND pepper" );

        Assert.Equal( [message.Id], (await FindAsync( testDatabase, query )).Messages.Select( match => match.Id ) );
    }

    [Fact]
    public async Task Keeps_the_newest_hundred_ids_in_id_order_when_timestamps_disagree( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        testDatabase.Clock.TravelTo( new DateTime( 2024, 1, 1, 0, 0, 0, DateTimeKind.Utc ) );
        var ids = new List<long>( );
        for ( var index = 0; index < 102; index++ )
        {
            testDatabase.Clock.Travel( TimeSpan.FromSeconds( -1 ) );
            ids.Add( (await PostAsync( testDatabase, "designers", "divergentchronology" )).Id );
        }

        var found = await FindAsync( testDatabase, "divergentchronology" );

        Assert.Equal( ids.Skip( 2 ), found.Messages.Select( match => match.Id ) );
    }

    [Fact]
    public async Task Unreachable_messages_are_not_found( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        await PostAsync( testDatabase, "designers", "Hello world!" );
        var membership = TestDatabase.Id( "david_designers" );
        await testDatabase.ExecuteAsync( "DELETE FROM memberships WHERE id = @p0", membership );

        Assert.Empty( (await FindAsync( testDatabase, "hello" )).Messages );
    }

    [Fact]
    public async Task Records_the_sanitized_query_and_keeps_the_ten_most_recent( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        var david = TestDatabase.Id( "david" );

        await testDatabase.WriteAsync( scope => MessageSearch.Record( scope, david, "hello, world!" ) );
        Assert.Equal( "hello  world ", (await testDatabase.ReadAsync( db => MessageSearch.Recent( db, david ) ))[0].Query );

        for ( var index = 0; index < 12; index++ )
        {
            testDatabase.Clock.Travel( TimeSpan.FromSeconds( 1 ) );
            await testDatabase.WriteAsync( scope => MessageSearch.Record( scope, david, $"query{index}" ) );
        }

        var recent = await testDatabase.ReadAsync( db => MessageSearch.Recent( db, david ) );
        Assert.Equal( Enumerable.Range( 2, 10 ).Reverse( ).Select( index => $"query{index}" ), recent.Select( search => search.Query ) );
    }

    [Fact]
    public async Task Recording_without_a_query_violates_the_not_null_column_as_in_rails( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );

        await Assert.ThrowsAsync<SqliteException>( ( ) => testDatabase.WriteAsync( scope => MessageSearch.Record( scope, TestDatabase.Id( "david" ), null ) ) );
    }

    [Fact]
    public async Task Clears_the_search_history( )
    {
        using var testDatabase = await TestDatabase.CreateAsync( );
        var david = TestDatabase.Id( "david" );
        Assert.NotEmpty( await testDatabase.ReadAsync( db => MessageSearch.Recent( db, david ) ) );

        await testDatabase.WriteAsync( scope => MessageSearch.Clear( scope, david ) );

        Assert.Empty( await testDatabase.ReadAsync( db => MessageSearch.Recent( db, david ) ) );
        Assert.NotEmpty( await testDatabase.ReadAsync( db => MembershipQueries.ForUser( db, david ) ) );
    }
}
