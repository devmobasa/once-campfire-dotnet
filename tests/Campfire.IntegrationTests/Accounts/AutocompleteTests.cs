using System.Net;
using System.Text.Json.Nodes;
using Campfire.IntegrationTests.Support;
using Campfire.Web.Paths;

namespace Campfire.IntegrationTests.Accounts;

/// <summary>
/// <c>Autocompletable::UsersController</c>: the mentions prompt's <c>&lt;lexxy-prompt-item&gt;</c>s (HTML without a
/// layout, filtered by <c>filter</c>, scoped to a room) and the pickers' JSON (filtered by <c>query</c>), 20 a page with
/// geared_pagination's headers on JSON.
/// </summary>
public sealed class AutocompleteTests( SliceAppFactory factory ) : IClassFixture<SliceAppFactory>
{
    private static readonly long Designers = SliceAppFactory.Id( "designers" );

    private static readonly long Watercooler = SliceAppFactory.Id( "watercooler" );

    [Theory]
    [InlineData( "text/html" )]
    [InlineData( "*/*" )]
    public async Task The_mentions_prompt_gets_the_rooms_active_people_matching_the_filter( string accept )
    {
        using var client = await factory.SignedInClientAsync( "david@37signals.com" );

        var items = await client.GetAsync( $"{Routes.AutocompletableUsersForRoom( Designers )}&filter=e", accept );

        Assert.Equal( HttpStatusCode.OK, items.Status );
        Assert.Equal( "text/html; charset=utf-8", items.ContentType );
        Assert.DoesNotContain( "<html", items.Body, StringComparison.Ordinal );
        Assert.Contains( "<lexxy-prompt-item", items.Body, StringComparison.Ordinal );
        Assert.Contains( "Kevin", items.Body, StringComparison.Ordinal );
        Assert.Null( items.Raw( "X-Total-Count" ) );
    }

    [Theory]
    [InlineData( false )]
    [InlineData( true )]
    public async Task The_pickers_get_json_with_the_total_count( bool explicitFormat )
    {
        using var client = await factory.SignedInClientAsync( "david@37signals.com" );

        var path = explicitFormat ? Routes.WithFormat( Routes.AutocompletableUsers, "json" ) : Routes.AutocompletableUsers;
        var json = await client.GetAsync( $"{path}?query=j", "application/json" );
        var users = JsonNode.Parse( json.Body )!.AsArray( );

        Assert.Equal( HttpStatusCode.OK, json.Status );
        Assert.Equal( "application/json; charset=utf-8", json.ContentType );
        Assert.Equal( ["Jason", "JZ"], users.Select( user => user!["name"]!.GetValue<string>( ) ) );
        Assert.All( users, user => Assert.StartsWith( $"{factory.BaseAddress}users/", user!["avatar_url"]!.GetValue<string>( ), StringComparison.Ordinal ) );
        Assert.All( users, user => Assert.NotEmpty( user!["sgid"]!.GetValue<string>( ) ) );
        Assert.Equal( "2", json.Raw( "X-Total-Count" ) );
        Assert.Null( json.Raw( "Link" ) );
    }

    [Fact]
    public async Task An_unformatted_picker_request_without_a_json_accept_header_still_gets_html( )
    {
        using var client = await factory.SignedInClientAsync( "david@37signals.com" );

        var response = await client.GetAsync( $"{Routes.AutocompletableUsers}?query=j", "*/*" );

        Assert.Equal( HttpStatusCode.OK, response.Status );
        Assert.Equal( "text/html; charset=utf-8", response.ContentType );
        Assert.Contains( "<lexxy-prompt-item", response.Body, StringComparison.Ordinal );
    }

    [Fact]
    public async Task A_room_the_user_is_not_in_is_404( )
    {
        using var client = await factory.SignedInClientAsync( "jz@37signals.com" );

        var outside = await client.GetAsync( $"{Routes.AutocompletableUsersForRoom( Watercooler )}&filter=a" );

        Assert.Equal( HttpStatusCode.NotFound, outside.Status );
    }
}
