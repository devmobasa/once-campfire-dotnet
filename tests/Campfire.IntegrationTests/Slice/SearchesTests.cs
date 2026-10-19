using System.Net;
using System.Text.RegularExpressions;
using Campfire.IntegrationTests.Support;
using Campfire.Web.Paths;

namespace Campfire.IntegrationTests.Slice;

/// <summary>
/// <c>SearchesController</c> end to end (<c>test/controllers/searches_controller_test.rb</c>): full-text search over the
/// rooms the user is in, the recent searches recorded and cleared, and operator words treated as literal terms.
/// </summary>
public sealed partial class SearchesTests
{
    private static readonly long Designers = SliceAppFactory.Id( "designers" );

    private static readonly long David = SliceAppFactory.Id( "david" );

    [Fact]
    public async Task The_initial_view_has_no_results_and_the_fixture_search_is_recent( )
    {
        await using var factory = new SliceAppFactory( );
        using var david = await factory.SignedInClientAsync( "david@37signals.com" );

        var page = await david.GetAsync( Routes.Searches );

        Assert.Equal( HttpStatusCode.OK, page.Status );
        Assert.Contains( "<title>Search</title>", page.Body, StringComparison.Ordinal );
        Assert.DoesNotContain( "class=\"message ", page.Body, StringComparison.Ordinal );
        Assert.Contains( "href=\"/searches?q=pizza\"", page.Body, StringComparison.Ordinal );
        Assert.Contains( "text/html; charset=utf-8", page.ContentType, StringComparison.Ordinal );
    }

    [Fact]
    public async Task Finds_reachable_messages_and_not_the_others( )
    {
        await using var factory = new SliceAppFactory( );
        using var david = await factory.SignedInClientAsync( "david@37signals.com" );
        await david.PostMessageFromRoomPageAsync( Designers, "<p>Hello world!</p>", "search-hello" );

        var found = await david.GetAsync( Routes.SearchesFor( "hello" ) );
        factory.Execute( $"DELETE FROM memberships WHERE id = {SliceAppFactory.Id( "david_designers" )}" );
        var unreachable = await david.GetAsync( Routes.SearchesFor( "hello" ) );

        Assert.Equal( HttpStatusCode.OK, found.Status );
        Assert.Contains( "id=\"message_search-hello\"", found.Body, StringComparison.Ordinal );
        Assert.Contains( "Hello world!", found.Body, StringComparison.Ordinal );
        Assert.Matches( "“hello”</span>\\s*<span class=\"flex-item-no-shrink\">1</span>", found.Body );
        Assert.Equal( HttpStatusCode.OK, unreachable.Status );
        Assert.DoesNotContain( "id=\"message_search-hello\"", unreachable.Body, StringComparison.Ordinal );
    }

    [Fact]
    public async Task Create_records_the_sanitized_query_and_redirects_to_its_results( )
    {
        await using var factory = new SliceAppFactory( );
        using var david = await factory.SignedInClientAsync( "david@37signals.com" );
        var token = await david.CsrfTokenAsync( Routes.Searches );

        var created = await david.SubmitFormAsync( HttpMethod.Post, Routes.Searches, token, [("q", "hello, world")] );

        Assert.Equal( HttpStatusCode.Found, created.Status );
        Assert.Equal( $"{factory.BaseAddress}searches?q=hello++world", created.Location );
        Assert.Equal( 1, factory.Scalar<long>( $"SELECT count(*) FROM searches WHERE user_id = {David} AND query = 'hello  world'" ) );
    }

    [Fact]
    public async Task Recent_searches_keep_the_ten_most_recent( )
    {
        await using var factory = new SliceAppFactory( );
        using var david = await factory.SignedInClientAsync( "david@37signals.com" );
        var token = await david.CsrfTokenAsync( Routes.Searches );

        foreach ( var term in Enumerable.Range( 1, 11 ).Select( number => $"term{number}" ) )
        {
            factory.Clock.Advance( TimeSpan.FromSeconds( 1 ) );
            var created = await david.SubmitFormAsync( HttpMethod.Post, Routes.Searches, token, [("q", term)] );
            Assert.Equal( HttpStatusCode.Found, created.Status );
        }

        var page = await david.GetAsync( Routes.Searches );

        Assert.Equal( 10, factory.Scalar<long>( $"SELECT count(*) FROM searches WHERE user_id = {David}" ) );
        Assert.Equal( 0, factory.Scalar<long>( $"SELECT count(*) FROM searches WHERE user_id = {David} AND query IN ('pizza', 'term1')" ) );
        var recent = RecentLinkPattern( ).Matches( page.Body ).Select( match => match.Groups[1].Value ).Take( 10 ).ToList( );
        Assert.Equal( Enumerable.Range( 2, 10 ).Reverse( ).Select( number => $"term{number}" ), recent );
    }

    [Fact]
    public async Task Clear_destroys_the_users_searches( )
    {
        await using var factory = new SliceAppFactory( );
        using var david = await factory.SignedInClientAsync( "david@37signals.com" );
        var token = await david.CsrfTokenAsync( Routes.Searches );
        Assert.True( factory.Scalar<long>( $"SELECT count(*) FROM searches WHERE user_id = {David}" ) > 0 );

        var cleared = await david.SubmitFormAsync( HttpMethod.Post, Routes.ClearSearches, token, [("_method", "delete")] );

        Assert.Equal( HttpStatusCode.Found, cleared.Status );
        Assert.Equal( $"{factory.BaseAddress}searches", cleared.Location );
        Assert.Equal( 0, factory.Scalar<long>( $"SELECT count(*) FROM searches WHERE user_id = {David}" ) );
    }

    [Theory]
    [InlineData( "NOT" )]
    [InlineData( "AND" )]
    [InlineData( "salt AND" )]
    public async Task Operator_words_do_not_block_index_create_or_clear( string query )
    {
        await using var factory = new SliceAppFactory( );
        using var david = await factory.SignedInClientAsync( "david@37signals.com" );
        var token = await david.CsrfTokenAsync( Routes.Searches );
        var before = factory.Scalar<long>( $"SELECT count(*) FROM searches WHERE user_id = {David}" );

        var index = await david.GetAsync( Routes.SearchesFor( query ) );
        var create = await david.SubmitFormAsync( HttpMethod.Post, Routes.Searches, token, [("q", query)] );

        Assert.Equal( HttpStatusCode.OK, index.Status );
        Assert.Equal( HttpStatusCode.Found, create.Status );
        Assert.Equal( before + 1, factory.Scalar<long>( $"SELECT count(*) FROM searches WHERE user_id = {David}" ) );

        var clear = await david.SubmitFormAsync( HttpMethod.Post, $"{Routes.ClearSearches}?q={Uri.EscapeDataString( query )}", token, [("_method", "delete")] );

        Assert.Equal( HttpStatusCode.Found, clear.Status );
        Assert.Equal( 0, factory.Scalar<long>( $"SELECT count(*) FROM searches WHERE user_id = {David}" ) );
    }

    [Fact]
    public async Task Without_a_query_create_fails_on_the_not_null_column_and_a_non_string_query_fails_too( )
    {
        await using var factory = new SliceAppFactory( );
        using var david = await factory.SignedInClientAsync( "david@37signals.com" );
        var token = await david.CsrfTokenAsync( Routes.Searches );

        var missing = await david.SubmitFormAsync( HttpMethod.Post, Routes.Searches, token );
        var array = await david.GetAsync( $"{Routes.Searches}?q[]=hello" );

        Assert.Equal( HttpStatusCode.InternalServerError, missing.Status );
        Assert.Equal( HttpStatusCode.InternalServerError, array.Status );
    }

    [Fact]
    public async Task A_bot_key_gets_the_sign_in_challenge_and_no_results( )
    {
        await using var factory = new SliceAppFactory( );
        using var client = factory.Client( );
        var path = BotApiKeys.InQuery( Routes.Searches, BotApiKeys.Bender( factory ) );

        var denied = await client.GetAsync( path );

        SliceAssert.SignInChallenge( factory, denied, path );
        Assert.DoesNotContain( "lexxy-content", denied.Body, StringComparison.Ordinal );
    }

    [GeneratedRegex( "<a class=\"align-center gap room btn txt-nowrap\" href=\"/searches\\?q=([^\"]*)\">" )]
    private static partial Regex RecentLinkPattern( );
}
