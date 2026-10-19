using System.Net;
using Campfire.Application.Rooms;
using Campfire.Application.Security;
using Campfire.IntegrationTests.Support;
using Campfire.Web.Hosting;
using Campfire.Web.Paths;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;

namespace Campfire.IntegrationTests.Slice;

public sealed class NativeRepairRegressionTests
{
    [Fact]
    public async Task Posting_after_room_deletion_acknowledges_the_composer_without_writing_activity( )
    {
        await using var factory = new SliceAppFactory( );
        using var client = factory.Client( );
        client.SignInAs( factory, SliceAppFactory.Id( "david" ) );
        var room = SliceAppFactory.Id( "designers" );
        var csrf = await client.CsrfTokenAsync( Routes.Room( room ) );
        await factory.App.Database.WriteAsync( scope => RoomOperations.Destroy( scope, room ), TestContext.Current.CancellationToken );
        await SliceWait.UntilAsync( ( ) => factory.Scalar<long>( $"SELECT COUNT(*) FROM rooms WHERE id={room}" ) == 0 );
        StaleSessions.MarkAll( factory );
        var before = DatabaseSnapshot.Of( factory );

        var response = await client.PostMessageAsync( room, csrf, body: "Too late", clientMessageId: "deleted-room-post" );

        Assert.Equal( HttpStatusCode.OK, response.Status );
        Assert.Contains( "This room was deleted.", response.Body, StringComparison.Ordinal );
        Assert.Contains( "id=\"composer-frame\"", response.Body, StringComparison.Ordinal );
        Assert.Equal( before, DatabaseSnapshot.Of( factory ) );
    }

    [Fact]
    public async Task Paged_messages_revalidate_privately_on_both_200_and_304( )
    {
        await using var factory = new SliceAppFactory( );
        using var client = factory.Client( );
        client.SignInAs( factory, SliceAppFactory.Id( "david" ) );
        var path = Routes.RoomMessages( SliceAppFactory.Id( "designers" ) );
        var page = await client.GetAsync( path );
        Assert.Equal( HttpStatusCode.OK, page.Status );
        Assert.NotNull( page.Headers.ETag );
        Assert.Equal( "max-age=0, private, must-revalidate", page.Raw( "Cache-Control" ) );

        var request = new HttpRequestMessage( HttpMethod.Get, path );
        request.Headers.IfNoneMatch.Add( page.Headers.ETag! );
        var unchanged = await client.SendAsync( request );
        Assert.Equal( HttpStatusCode.NotModified, unchanged.Status );
        Assert.Empty( unchanged.Body );
        Assert.Equal( "max-age=0, private, must-revalidate", unchanged.Raw( "Cache-Control" ) );
    }

    [Theory]
    [InlineData( false )]
    [InlineData( true )]
    public async Task Html_errors_keep_styling_and_a_way_back_while_json_keeps_problem_details( bool serverError )
    {
        await using var factory = new SliceAppFactory( );
        using var client = factory.Client( );
        client.SignInAs( factory, SliceAppFactory.Id( "david" ) );
        if ( serverError )
        {
            factory.Execute( "ALTER TABLE rooms RENAME TO unavailable_rooms" );
        }

        var path = serverError ? Routes.Room( SliceAppFactory.Id( "designers" ) ) : "/unmapped-native-error";
        var expected = serverError ? HttpStatusCode.InternalServerError : HttpStatusCode.NotFound;

        var html = await client.GetAsync( path );
        Assert.Equal( expected, html.Status );
        Assert.Contains( "<style>", html.Body, StringComparison.Ordinal );
        Assert.Contains( "class=\"error\"", html.Body, StringComparison.Ordinal );
        Assert.Contains( "href=\"/\"", html.Body, StringComparison.Ordinal );
        Assert.Contains( "Go back", html.Body, StringComparison.Ordinal );
        Assert.DoesNotContain( "SQLite", html.Body, StringComparison.Ordinal );

        var json = await client.GetAsync( path, "application/json" );
        Assert.Equal( expected, json.Status );
        Assert.StartsWith( "application/problem+json", json.ContentType, StringComparison.Ordinal );
        Assert.Contains( $"\"status\":{( int ) expected}", json.Body, StringComparison.Ordinal );
        Assert.DoesNotContain( "SQLite", json.Body, StringComparison.Ordinal );
    }

    [Fact]
    public async Task Expiring_avatar_urls_never_enter_the_shared_front_cache( )
    {
        await using var factory = new AvatarFrontFactory( );
        using var client = factory.Client( );
        var user = SliceAppFactory.Id( "kevin" );
        client.SignInAs( factory, user );
        var token = factory.App.SignedIds.Generate( "User", user, SignedIds.AvatarPurpose, factory.Clock.GetUtcNow( ).AddSeconds( 5 ) );
        var path = $"/users/{token}/avatar";

        var first = await client.GetAsync( path, "image/*" );
        Assert.Equal( HttpStatusCode.OK, first.Status );
        Assert.Contains( "<svg", first.Body, StringComparison.Ordinal );
        Assert.Equal( "max-age=5, private", first.Raw( "Cache-Control" ) );
        var second = await client.GetAsync( path, "image/*" );
        Assert.Equal( HttpStatusCode.OK, second.Status );
        Assert.NotEqual( "hit", second.Raw( "X-Cache" ) );
        var conditional = new HttpRequestMessage( HttpMethod.Get, path );
        conditional.Headers.IfNoneMatch.Add( first.Headers.ETag! );
        var fresh = await client.SendAsync( conditional, "image/*" );
        Assert.Equal( HttpStatusCode.NotModified, fresh.Status );
        Assert.Equal( "max-age=5, private", fresh.Raw( "Cache-Control" ) );

        factory.Clock.Advance( TimeSpan.FromSeconds( 6 ) );
        var expired = await client.GetAsync( path, "image/*" );
        Assert.Equal( HttpStatusCode.NotFound, expired.Status );
        Assert.NotEqual( "hit", expired.Raw( "X-Cache" ) );
    }

    private sealed class AvatarFrontFactory : SliceAppFactory
    {
        protected override void ConfigureWebHost( IWebHostBuilder builder )
        {
            base.ConfigureWebHost( builder );
            // Use the real cache middleware on the existing ephemeral listener.
            var frontServer = new FrontServerOptions { Enabled = true, TargetPort = 0, LogRequests = false };
            builder.ConfigureTestServices( services => services.AddSingleton( frontServer ) );
        }
    }
}
