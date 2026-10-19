using System.Net;
using Campfire.IntegrationTests.Support;
using Campfire.Web.Paths;
using Campfire.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Campfire.IntegrationTests.Slice;

/// <summary>Production cookie tickets, fresh database authority, native forms and session revocation.</summary>
public sealed class SignInTests
{
    private static readonly long Designers = SliceAppFactory.Id( "designers" );

    [Fact]
    public async Task Native_cookie_validation_refreshes_old_activity_once_without_reissuing_the_ticket( )
    {
        await using var factory = new SliceAppFactory( );
        using var client = factory.Client( );
        var userId = SliceAppFactory.Id( "david" );
        client.SignInAs( factory, userId );
        factory.Execute( $"UPDATE sessions SET last_active_at='2000-01-01 00:00:00', user_agent='old-agent', ip_address='192.0.2.1' WHERE user_id={userId}" );

        var first = await client.GetAsync( Routes.Room( Designers ) );

        Assert.Equal( HttpStatusCode.OK, first.Status );
        var refreshed = await SliceWait.UntilAsync(
            ( ) => Task.FromResult( LatestSessionColumn( factory, userId, "last_active_at" ) ),
            lastActiveAt => lastActiveAt?.Contains( "2000-01-01", StringComparison.Ordinal ) == false );
        Assert.DoesNotContain( "2000-01-01", refreshed, StringComparison.Ordinal );
        Assert.DoesNotContain( BrowserAuthentication.CookieName + "=", first.Raw( "Set-Cookie" ) ?? "", StringComparison.Ordinal );
        factory.Execute( $"UPDATE sessions SET user_agent='unchanged' WHERE user_id={userId}" );

        var second = await client.GetAsync( Routes.Room( Designers ) );

        Assert.Equal( HttpStatusCode.OK, second.Status );
        Assert.Equal( refreshed, LatestSessionColumn( factory, userId, "last_active_at" ) );
        Assert.Equal( "unchanged", LatestSessionColumn( factory, userId, "user_agent" ) );
    }

    [Fact]
    public async Task A_native_session_ticket_authenticates_and_database_revocation_takes_effect_next_request( )
    {
        await using var factory = new SliceAppFactory( );
        using var client = factory.Client( );
        client.SignInAs( factory, SliceAppFactory.Id( "david" ) );
        var page = await client.GetAsync( Routes.Room( Designers ) );
        Assert.Equal( HttpStatusCode.OK, page.Status );
        Assert.Contains( "<title>Designers</title>", page.Body, StringComparison.Ordinal );
        Assert.Contains( "content=\"127326141\"", page.Body, StringComparison.Ordinal );
        Assert.DoesNotContain( BrowserAuthentication.CookieName + "=", page.Raw( "Set-Cookie" ) ?? "", StringComparison.Ordinal );

        factory.Execute( $"DELETE FROM sessions WHERE user_id={SliceAppFactory.Id( "david" )}" );
        var revoked = await client.GetAsync( Routes.Room( Designers ) );
        SliceAssert.SignInChallenge( factory, revoked, Routes.Room( Designers ) );
    }

    [Fact]
    public async Task A_forged_ticket_cannot_authenticate( )
    {
        await using var factory = new SliceAppFactory( );
        using var client = factory.Client( );
        client.AddCookie( BrowserAuthentication.CookieName, "forged-session-id-127326141" );
        var page = await client.GetAsync( Routes.Room( Designers ) );
        SliceAssert.SignInChallenge( factory, page, Routes.Room( Designers ) );
    }

    [Fact]
    public async Task A_Rails_format_session_token_is_signed_out_and_sign_in_replaces_it( )
    {
        await using var factory = new SliceAppFactory( );
        using var client = factory.Client( );
        var railsCookie = RepositoryPaths.ReadVectorJson( "rails_compat.json" )["signed_cookies"]!["generate"]![0]!["raw"].RequiredString( );
        client.AddCookie( "session_token", railsCookie );
        var sessions = factory.Scalar<long>( "SELECT COUNT(*) FROM sessions" );

        SliceAssert.SignInChallenge( factory, await client.GetAsync( Routes.Room( Designers ) ), Routes.Room( Designers ) );
        Assert.Equal( sessions, factory.Scalar<long>( "SELECT COUNT(*) FROM sessions" ) );

        var signedIn = await client.SignInAsync( "david@37signals.com" );

        Assert.Equal( HttpStatusCode.Found, signedIn.Status );
        Assert.Contains( "session_token=", signedIn.Raw( "Set-Cookie" ) ?? "", StringComparison.Ordinal );
        Assert.DoesNotContain( railsCookie, client.CookieHeader, StringComparison.Ordinal );
        Assert.Equal( HttpStatusCode.OK, (await client.GetAsync( Routes.Room( Designers ) )).Status );
    }

    [Theory]
    [InlineData( false )]
    [InlineData( true )]
    public async Task A_genuine_ticket_with_a_modified_mac_or_wrong_protection_purpose_cannot_authenticate( bool wrongPurpose )
    {
        await using var factory = new SliceAppFactory( );
        using var client = factory.Client( );
        client.SignInAs( factory, SliceAppFactory.Id( "david" ) );
        Assert.Equal( HttpStatusCode.OK, (await client.GetAsync( Routes.Room( Designers ) )).Status );
        var ticketValue = TicketCookieValue( client );
        string rejectedTicket;
        if ( wrongPurpose )
        {
            var options = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>( ).Get( BrowserAuthentication.Scheme );
            var ticket = options.TicketDataFormat.Unprotect( ticketValue );
            Assert.NotNull( ticket );
            var differentPurpose = factory.Services.GetRequiredService<IDataProtectionProvider>( )
                .CreateProtector( "Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationMiddleware", "Different.Browser", "v2" );
            rejectedTicket = new TicketDataFormat( differentPurpose ).Protect( ticket );
        }
        else
        {
            var bytes = WebEncoders.Base64UrlDecode( ticketValue );
            bytes[^1] ^= 1;
            rejectedTicket = WebEncoders.Base64UrlEncode( bytes );
        }

        var count = factory.Scalar<long>( "SELECT count(*) FROM sessions" );
        client.AddCookie( BrowserAuthentication.CookieName, rejectedTicket );
        SliceAssert.SignInChallenge( factory, await client.GetAsync( Routes.Room( Designers ) ), Routes.Room( Designers ) );
        Assert.Equal( count, factory.Scalar<long>( "SELECT count(*) FROM sessions" ) );
    }

    [Fact]
    public async Task An_expired_ticket_for_an_existing_session_redirects_without_changing_sessions( )
    {
        await using var factory = new SliceAppFactory( );
        using var client = factory.Client( );
        client.SignInAs( factory, SliceAppFactory.Id( "david" ), DateTimeOffset.UtcNow.AddMinutes( -1 ) );
        var sessions = factory.Scalar<long>( "SELECT COUNT(*) FROM sessions" );

        var page = await client.GetAsync( Routes.Room( Designers ) );

        SliceAssert.SignInChallenge( factory, page, Routes.Room( Designers ) );
        Assert.Equal( sessions, factory.Scalar<long>( "SELECT COUNT(*) FROM sessions" ) );
    }

    [Fact]
    public async Task Login_form_carries_the_local_return_URL_and_rejects_external_redirects( )
    {
        await using var factory = new SliceAppFactory( );
        using var client = factory.Client( );
        var target = Routes.Room( Designers );
        var form = await client.GetAsync( $"{Routes.NewSession}?returnUrl={Uri.EscapeDataString( target )}" );
        Assert.Contains( "name=\"returnUrl\"", form.Body, StringComparison.Ordinal );
        var signedIn = await client.SubmitFormAsync(
            HttpMethod.Post,
            Routes.Session,
            SliceClient.MetaToken( form.Body ),
            [("email_address", "david@37signals.com"), ("password", "secret123456"), ("returnUrl", target)] );
        Assert.Equal( HttpStatusCode.Found, signedIn.Status );
        Assert.Equal( new Uri( factory.BaseAddress, target ).AbsoluteUri, signedIn.Location );
        var issued = signedIn.Raw( "Set-Cookie" ) ?? "";
        Assert.Contains( BrowserAuthentication.CookieName + "=", issued, StringComparison.Ordinal );
        Assert.Contains( "httponly", issued, StringComparison.OrdinalIgnoreCase );
        Assert.Contains( "samesite=lax", issued, StringComparison.OrdinalIgnoreCase );
        Assert.Contains( "path=/", issued, StringComparison.OrdinalIgnoreCase );
        Assert.Contains( BrowserAuthentication.CookieName + "=", client.CookieHeader, StringComparison.Ordinal );
        var token = await client.CsrfTokenAsync( target );
        var external = await client.SubmitFormAsync(
            HttpMethod.Post,
            Routes.Session,
            token,
            [("email_address", "david@37signals.com"), ("password", "secret123456"), ("returnUrl", "//evil.example")] );
        Assert.Equal( factory.BaseAddress.ToString( ), external.Location );
    }

    [Fact]
    public async Task A_wrong_password_renders_the_form_again_with_401_and_alert( )
    {
        await using var factory = new SliceAppFactory( );
        using var client = factory.Client( );
        var rejected = await client.SignInAsync( "david@37signals.com", "not-the-password" );
        Assert.Equal( HttpStatusCode.Unauthorized, rejected.Status );
        Assert.Contains( "Too many requests or unauthorized.", rejected.Body, StringComparison.Ordinal );
        Assert.Contains( "value=\"david@37signals.com\"", rejected.Body, StringComparison.Ordinal );
    }

    [Fact]
    public async Task A_form_method_override_signs_out_only_with_same_origin_metadata( )
    {
        await using var factory = new SliceAppFactory( );
        using var client = factory.Client( );
        client.SignInAs( factory, SliceAppFactory.Id( "david" ) );
        var token = await client.CsrfTokenAsync( Routes.Room( Designers ) );
        var rejected = await client.SubmitFormAsync(
            HttpMethod.Post,
            Routes.Session,
            null,
            [("_method", "delete")],
            headers: [(BrowserHeaders.FetchSite, "cross-site")] );
        Assert.Equal( HttpStatusCode.UnprocessableEntity, rejected.Status );
        var signedOut = await client.SubmitFormAsync( HttpMethod.Post, Routes.Session, token, [("_method", "delete")] );
        Assert.Equal( HttpStatusCode.Found, signedOut.Status );
        var next = await client.GetAsync( Routes.Room( Designers ) );
        SliceAssert.SignInChallenge( factory, next, Routes.Room( Designers ) );
    }

    /// <summary>A column of the user's newest session row.</summary>
    private static string? LatestSessionColumn( SliceAppFactory factory, long userId, string column ) =>
        factory.Scalar<string>( $"SELECT {column} FROM sessions WHERE user_id={userId} ORDER BY id DESC LIMIT 1" );

    /// <summary>The value of the client's authentication ticket cookie.</summary>
    private static string TicketCookieValue( SliceClient client )
    {
        const string prefix = BrowserAuthentication.CookieName + "=";
        var cookie = client.CookieHeader.Split( ';' ).Select( part => part.Trim( ) ).Single( part => part.StartsWith( prefix, StringComparison.Ordinal ) );
        return cookie[prefix.Length..];
    }
}
