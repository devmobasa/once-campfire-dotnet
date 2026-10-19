using System.Diagnostics;
using System.Net;
using AngleSharp.Html.Parser;
using Campfire.Application.Accounts;
using Campfire.Application.Users;
using Campfire.Web.App;
using Campfire.Web.Endpoints;
using Campfire.Web.Hosting;
using Campfire.Web.Paths;
using Campfire.Web.Presenters;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Campfire.Web.Tests.Hosting;

public sealed class FrontCacheFreshnessTests( ITestOutputHelper output )
{
    [Fact]
    public async Task Hits_accumulate_age_without_renewing_the_remaining_lifetime( )
    {
        var calls = 0;
        await using var server = await FrontTestServer.StartAsync( false, context =>
        {
            Interlocked.Increment( ref calls );
            context.Response.Headers.CacheControl = "public,max-age=4";
            context.Response.Headers.Age = "1";
            return context.Response.WriteAsync( "public bytes", context.RequestAborted );
        } );
        using var client = server.Client( );
        using var prime = await client.GetAsync( "/age", TestContext.Current.CancellationToken );
        using var first = await client.GetAsync( "/age", TestContext.Current.CancellationToken );
        Assert.Equal( "hit", first.Headers.GetValues( "X-Cache" ).Single( ) );
        Assert.NotNull( first.Headers.Age );
        Assert.True( first.Headers.Age >= TimeSpan.FromSeconds( 1 ) );

        await Task.Delay( TimeSpan.FromMilliseconds( 1100 ), TestContext.Current.CancellationToken );
        using var second = await client.GetAsync( "/age", TestContext.Current.CancellationToken );
        Assert.Equal( "hit", second.Headers.GetValues( "X-Cache" ).Single( ) );
        Assert.True( second.Headers.Age > first.Headers.Age );
        Assert.Equal( TimeSpan.FromSeconds( 4 ), second.Headers.CacheControl!.MaxAge );
        output.WriteLine( $"first-hit Age={first.Headers.GetValues( "Age" ).Single( )}, second-hit Age={second.Headers.GetValues( "Age" ).Single( )}, Cache-Control={second.Headers.CacheControl}" );

        await Task.Delay( TimeSpan.FromMilliseconds( 2100 ), TestContext.Current.CancellationToken );
        using var expired = await client.GetAsync( "/age", TestContext.Current.CancellationToken );
        Assert.Equal( "miss", expired.Headers.GetValues( "X-Cache" ).Single( ) );
        Assert.Equal( 2, Volatile.Read( ref calls ) );
        output.WriteLine( $"after original freshness deadline: X-Cache={expired.Headers.GetValues( "X-Cache" ).Single( )}, origin calls={Volatile.Read( ref calls )}" );
    }

    [Fact]
    public async Task Time_spent_streaming_the_initial_body_consumes_its_freshness( )
    {
        var calls = 0;
        await using var server = await FrontTestServer.StartAsync( false, async context =>
        {
            Interlocked.Increment( ref calls );
            context.Response.Headers.CacheControl = "public,max-age=1";
            await context.Response.WriteAsync( "first", context.RequestAborted );
            await context.Response.Body.FlushAsync( context.RequestAborted );
            await Task.Delay( TimeSpan.FromMilliseconds( 1200 ), context.RequestAborted );
            await context.Response.WriteAsync( "second", context.RequestAborted );
        } );
        using var client = server.Client( );
        using var first = await client.GetAsync( "/slow", TestContext.Current.CancellationToken );
        Assert.Equal( "firstsecond", await first.Content.ReadAsStringAsync( TestContext.Current.CancellationToken ) );
        using var second = await client.GetAsync( "/slow", TestContext.Current.CancellationToken );

        Assert.Equal( "miss", second.Headers.GetValues( "X-Cache" ).Single( ) );
        Assert.Equal( 2, Volatile.Read( ref calls ) );
        output.WriteLine( $"slow fill: next X-Cache={second.Headers.GetValues( "X-Cache" ).Single( )}, origin calls={Volatile.Read( ref calls )}" );
    }

    [Theory]
    [InlineData( "1" )]
    [InlineData( "999999999999999" )]
    public async Task Already_aged_responses_are_not_reused( string age )
    {
        var calls = 0;
        await using var server = await FrontTestServer.StartAsync( false, context =>
        {
            Interlocked.Increment( ref calls );
            context.Response.Headers.CacheControl = "public,max-age=1";
            context.Response.Headers.Age = age;
            return context.Response.WriteAsync( "public bytes", context.RequestAborted );
        } );
        using var client = server.Client( );
        using var first = await client.GetAsync( "/aged", TestContext.Current.CancellationToken );
        using var second = await client.GetAsync( "/aged", TestContext.Current.CancellationToken );

        Assert.Equal( "miss", second.Headers.GetValues( "X-Cache" ).Single( ) );
        Assert.Equal( 2, Volatile.Read( ref calls ) );
    }

    [Fact]
    public async Task A_frozen_business_clock_does_not_age_a_new_public_avatar( )
    {
        var production = ProductionAppBuilder.Create( frontServer: true, settings: [new( CampfireClock.FrozenTimeVariable, "2001-01-01 00:00:00" )] );
        await using var server = production.Build( );
        await server.App.BootCampfireAsync( );
        server.App.UseCampfireWeb( staticFiles: _ => { } );
        server.App.MapCampfireEndpoints( );
        await server.App.StartAsync( TestContext.Current.CancellationToken );
        var app = server.App.Services.GetRequiredService<CampfireApp>( );
        await app.Database.WriteAsync( scope => AccountOperations.Create( scope, "Avatar test" ), TestContext.Current.CancellationToken );
        var user = await app.Database.WriteAsync( scope => UserOperations.Create( scope, new NewUser
        {
            Name = "Avatar Person",
            EmailAddress = "avatar@example.test",
            PasswordDigest = Passwords.Digest( "avatar-password", Passwords.MinimumIterations ),
        } ), TestContext.Current.CancellationToken );
        var path = UserPresentation.AvatarPath( app, user );
        using var handler = server.Handler( );
        handler.UseCookies = true;
        using var client = new HttpClient( handler ) { BaseAddress = server.Http };
        var form = new HtmlParser( ).ParseDocument( await client.GetStringAsync( Routes.NewSession, TestContext.Current.CancellationToken ) );
        var token = form.QuerySelector( "input[name=authenticity_token]" )!.GetAttribute( "value" )!;
        using var signIn = new HttpRequestMessage( HttpMethod.Post, Routes.Session )
        {
            Content = new FormUrlEncodedContent( [new( "authenticity_token", token ), new( "email_address", "avatar@example.test" ), new( "password", "avatar-password" )] ),
        };
        signIn.Headers.Add( "Sec-Fetch-Site", "same-origin" );
        signIn.Headers.Add( "Origin", server.Http.GetLeftPart( UriPartial.Authority ) );
        using var signedIn = await client.SendAsync( signIn, TestContext.Current.CancellationToken );
        Assert.Equal( HttpStatusCode.Found, signedIn.StatusCode );
        using var first = await client.GetAsync( path, TestContext.Current.CancellationToken );
        using var second = await client.GetAsync( path, TestContext.Current.CancellationToken );

        Assert.Equal( HttpStatusCode.OK, first.StatusCode );
        Assert.Equal( HttpStatusCode.OK, second.StatusCode );
        Assert.Equal( "hit", second.Headers.GetValues( "X-Cache" ).Single( ) );
        Assert.NotNull( second.Headers.Age );
        Assert.InRange( second.Headers.Age.Value.TotalSeconds, 0, 30 );
        Assert.True( second.Headers.Date > new DateTimeOffset( 2020, 1, 1, 0, 0, 0, TimeSpan.Zero ) );
        Assert.Equal( await first.Content.ReadAsByteArrayAsync( TestContext.Current.CancellationToken ), await second.Content.ReadAsByteArrayAsync( TestContext.Current.CancellationToken ) );
        output.WriteLine( $"avatar business year=2001, first Date={first.Headers.Date:r}, hit Date={second.Headers.Date:r}, hit Age={second.Headers.GetValues( "Age" ).Single( )}, X-Cache={second.Headers.GetValues( "X-Cache" ).Single( )}" );
    }

    [Theory]
    [InlineData( "-1", 0 )]
    [InlineData( "invalid", 0 )]
    [InlineData( "3, 999", 3 )]
    public async Task Initial_age_uses_only_a_valid_first_nonnegative_value( string age, int expected )
    {
        await using var server = await FrontTestServer.StartAsync( false, context =>
        {
            context.Response.Headers.CacheControl = "public,max-age=300";
            context.Response.Headers.Age = age;
            return context.Response.WriteAsync( "public bytes", context.RequestAborted );
        } );
        using var client = server.Client( );
        var started = Stopwatch.GetTimestamp( );
        using var first = await client.GetAsync( "/initial-age", TestContext.Current.CancellationToken );
        using var second = await client.GetAsync( "/initial-age", TestContext.Current.CancellationToken );

        Assert.Equal( "hit", second.Headers.GetValues( "X-Cache" ).Single( ) );
        Assert.NotNull( second.Headers.Age );
        var maximumAge = expected + Math.Ceiling( Stopwatch.GetElapsedTime( started ).TotalSeconds );
        Assert.InRange( second.Headers.Age.Value.TotalSeconds, expected, maximumAge );
    }
}
