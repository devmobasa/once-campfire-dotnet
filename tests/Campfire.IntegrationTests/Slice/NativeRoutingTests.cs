using System.Net;
using Campfire.IntegrationTests.Support;
using Campfire.Web.Paths;

namespace Campfire.IntegrationTests.Slice;

/// <summary>Real production routing and middleware; no fake dispatcher or policy vectors.</summary>
public sealed class NativeRoutingTests
{
    [Theory]
    [InlineData( false )]
    [InlineData( true )]
    public async Task Wrong_method_returns_405_with_Allow_for_anonymous_and_signed_in_clients( bool signedIn )
    {
        await using var factory = new SliceAppFactory( );
        using var client = factory.Client( );
        if ( signedIn )
        {
            client.SignInAs( factory, SliceAppFactory.Id( "david" ) );
        }

        var response = await client.SendAsync( new HttpRequestMessage( HttpMethod.Put, Routes.NewSession ) );
        Assert.Equal( HttpStatusCode.MethodNotAllowed, response.Status );
        Assert.Contains( "GET", response.Raw( "Allow" ), StringComparison.Ordinal );
        Assert.Contains( "HEAD", response.Raw( "Allow" ), StringComparison.Ordinal );
        Assert.Null( response.Location );
    }

    [Theory]
    [InlineData( false )]
    [InlineData( true )]
    public async Task Framework_content_type_rejection_remains_415_without_challenge_or_csrf( bool signedIn )
    {
        await using var factory = new SliceAppFactory( );
        using var client = factory.Client( );
        if ( signedIn )
        {
            client.SignInAs( factory, SliceAppFactory.Id( "david" ) );
        }

        var response = await client.SendAsync( new HttpRequestMessage( HttpMethod.Post, Routes.UnfurlLink )
        {
            Content = new StringContent( "url=https://example.test" )
        } );
        Assert.Equal( HttpStatusCode.UnsupportedMediaType, response.Status );
        Assert.Null( response.Location );
        Assert.Null( response.Raw( "Allow" ) );
    }

    [Fact]
    public async Task Pagination_get_and_head_are_both_mapped_and_do_not_create_messages( )
    {
        await using var factory = new SliceAppFactory( );
        using var client = factory.Client( );
        client.SignInAs( factory, SliceAppFactory.Id( "david" ) );
        var room = SliceAppFactory.Id( "designers" );
        var count = factory.Scalar<long>( "SELECT COUNT(*) FROM messages" );
        var get = await client.GetAsync( Routes.RoomMessages( room ) );
        var head = await client.SendAsync( new HttpRequestMessage( HttpMethod.Head, Routes.RoomMessages( room ) ) );
        Assert.Equal( HttpStatusCode.OK, get.Status );
        Assert.Equal( get.Status, head.Status );
        Assert.NotEmpty( get.Body );
        Assert.Empty( head.Body );
        Assert.Equal( count, factory.Scalar<long>( "SELECT COUNT(*) FROM messages" ) );
    }

    [Fact]
    public async Task Retired_raw_upload_and_direct_upload_routes_do_not_issue_or_accept_tokens( )
    {
        await using var factory = new SliceAppFactory( );
        using var client = factory.Client( );
        client.SignInAs( factory, SliceAppFactory.Id( "david" ) );
        var upload = await client.SendAsync(
            new HttpRequestMessage( HttpMethod.Put, "/rails/active_storage/disk/unused" ) { Content = new StringContent( "bytes" ) } );
        var create = await client.SendAsync(
            new HttpRequestMessage( HttpMethod.Post, "/rails/active_storage/direct_uploads" ) { Content = new StringContent( "{}" ) } );
        Assert.Equal( HttpStatusCode.MethodNotAllowed, upload.Status );
        Assert.Contains( "GET", upload.Raw( "Allow" ), StringComparison.Ordinal );
        Assert.Equal( HttpStatusCode.NotFound, create.Status );
    }

    [Fact]
    public async Task Authenticated_JSON_is_strictly_parsed_and_errors_do_not_reroute( )
    {
        await using var factory = new SliceAppFactory( );
        using var client = factory.Client( );
        client.SignInAs( factory, SliceAppFactory.Id( "david" ) );
        var token = await client.CsrfTokenAsync( Routes.UserProfile );
        var response = await client.SendJsonAsync( HttpMethod.Post, Routes.UnfurlLink, token, "{", "application/json" );
        Assert.Equal( HttpStatusCode.BadRequest, response.Status );
        Assert.Null( response.Location );
    }

    [Theory]
    [InlineData( "/rooms/not-a-number/messages", HttpStatusCode.NotFound )]
    [InlineData( "/rooms/999999999999/messages", HttpStatusCode.NotFound )]
    public async Task Room_ids_are_parsed_before_lookup( string path, HttpStatusCode expected )
    {
        await using var factory = new SliceAppFactory( );
        using var client = factory.Client( );
        client.SignInAs( factory, SliceAppFactory.Id( "david" ) );

        var response = await client.GetAsync( path );

        Assert.Equal( expected, response.Status );
    }

    [Theory]
    [InlineData( "_method", "bogus" )]
    [InlineData( "duplicate", "delete" )]
    public async Task Invalid_or_duplicate_method_overrides_are_rejected( string kind, string method )
    {
        await using var factory = new SliceAppFactory( );
        using var client = factory.Client( );
        client.SignInAs( factory, SliceAppFactory.Id( "david" ) );
        var room = SliceAppFactory.Id( "designers" );
        (string Name, string Value)[] fields = kind == "duplicate" ? [("_method", method), ("_method", method)] : [("_method", method)];

        var response = await client.SubmitFormAsync( HttpMethod.Post, Routes.Room( room ), null, fields );

        Assert.Equal( HttpStatusCode.BadRequest, response.Status );
    }

    [Fact]
    public async Task Method_override_header_does_not_change_the_actual_post_method( )
    {
        await using var factory = new SliceAppFactory( );
        using var client = factory.Client( );
        client.SignInAs( factory, SliceAppFactory.Id( "david" ) );
        var room = SliceAppFactory.Id( "designers" );
        using var request = new HttpRequestMessage( HttpMethod.Post, Routes.Room( room ) ) { Content = new StringContent( "" ) };
        request.Headers.TryAddWithoutValidation( "X-HTTP-Method-Override", "DELETE" );

        var response = await client.SendAsync( request );

        Assert.Equal( HttpStatusCode.MethodNotAllowed, response.Status );
        Assert.Contains( "GET", response.Raw( "Allow" ), StringComparison.Ordinal );
        Assert.Contains( "DELETE", response.Raw( "Allow" ), StringComparison.Ordinal );
    }

    [Fact]
    public async Task Room_delete_requires_membership_administration_and_same_origin_metadata( )
    {
        await using var factory = new SliceAppFactory( );
        var room = SliceAppFactory.Id( "designers" );
        using var member = factory.Client( );
        member.SignInAs( factory, SliceAppFactory.Id( "kevin" ) );
        var memberToken = await member.CsrfTokenAsync( Routes.Room( room ) );
        var denied = await member.SubmitFormAsync( HttpMethod.Delete, Routes.Room( room ), memberToken );
        Assert.Equal( HttpStatusCode.Forbidden, denied.Status );
        Assert.Equal( 1, factory.Scalar<long>( $"SELECT COUNT(*) FROM rooms WHERE id={room}" ) );

        using var administrator = factory.Client( );
        administrator.SignInAs( factory, SliceAppFactory.Id( "david" ) );
        var administratorToken = await administrator.CsrfTokenAsync( Routes.Room( room ) );
        var crossSite = await administrator.SubmitFormAsync(
            HttpMethod.Delete,
            Routes.Room( room ),
            null,
            headers: [(BrowserHeaders.FetchSite, "cross-site")] );
        Assert.Equal( HttpStatusCode.UnprocessableEntity, crossSite.Status );
        Assert.Equal( 1, factory.Scalar<long>( $"SELECT COUNT(*) FROM rooms WHERE id={room}" ) );
        var deleted = await administrator.SubmitFormAsync( HttpMethod.Delete, Routes.Room( room ), administratorToken );
        Assert.Equal( HttpStatusCode.Found, deleted.Status );
        await SliceWait.UntilAsync( ( ) => factory.Scalar<long>( $"SELECT COUNT(*) FROM rooms WHERE id={room}" ) == 0 );
        Assert.Equal( 0, factory.Scalar<long>( $"SELECT COUNT(*) FROM rooms WHERE id={room}" ) );
    }
}
