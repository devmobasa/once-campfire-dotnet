using System.Net;
using AngleSharp.Html.Parser;
using Campfire.Application.Security;
using Campfire.IntegrationTests.Support;
using Campfire.Web.Paths;
using Campfire.Web.Security;

namespace Campfire.IntegrationTests.Accounts;

/// <summary>
/// Transfer links (<c>Sessions::TransfersController</c>): <c>user.transfer_id</c> signs the user in on another device
/// for four hours; an expired, forged, wrong-purpose or deactivated user's link answers 400.
/// </summary>
public sealed class TransferTests
{
    private static readonly long Kevin = SliceAppFactory.Id( "kevin" );

    [Fact]
    public async Task A_signed_out_transfer_get_renders_one_complete_auto_submit_form( )
    {
        using var factory = new SliceAppFactory( );
        using var client = factory.Client( );

        var page = await client.GetAsync( Routes.SessionTransfer( "some-token" ) );

        Assert.Equal( HttpStatusCode.OK, page.Status );
        var document = new HtmlParser( ).ParseDocument( page.Body );
        var form = Assert.Single( document.QuerySelectorAll( "form[data-controller='auto-submit']" ) );
        Assert.Single( form.QuerySelectorAll( "input[name='_method'][value='put']" ) );
        Assert.Equal( page.Body.Split( "<form ", StringSplitOptions.None ).Length - 1, page.Body.Split( "</form>", StringSplitOptions.None ).Length - 1 );
        Assert.Matches( """<form\b[^>]*data-controller="auto-submit"[^>]*>(?:\s*<input\b[^>]*>)*\s*</form>""", page.Body );
    }

    [Fact]
    public async Task The_link_page_puts_back_to_itself_and_the_put_signs_in( )
    {
        using var factory = new SliceAppFactory( );
        var transferId = factory.App.SignedIds.GenerateTransferId( Kevin, factory.Clock.GetUtcNow( ) );
        using var client = factory.Client( );

        var page = await client.GetAsync( Routes.SessionTransfer( transferId ) );
        var signedIn = await client.SubmitFormAsync( HttpMethod.Put, Routes.SessionTransfer( transferId ), SliceClient.MetaToken( page.Body ) );
        var home = await client.GetAsync( Routes.Root );

        Assert.Equal( HttpStatusCode.OK, page.Status );
        Assert.Contains( "action=\"/session/transfers/", page.Body, StringComparison.Ordinal );
        Assert.Contains( "name=\"_method\" value=\"put\"", page.Body, StringComparison.Ordinal );
        Assert.Equal( HttpStatusCode.Found, signedIn.Status );
        Assert.Equal( factory.BaseAddress.ToString( ), signedIn.Location );
        Assert.Equal( 1L, factory.Scalar<long>( $"SELECT COUNT(*) FROM sessions WHERE user_id = {Kevin}" ) );
        Assert.Equal( HttpStatusCode.Found, home.Status );
        Assert.Contains( "/rooms/", home.Location, StringComparison.Ordinal );
    }

    [Fact]
    public async Task A_transfer_link_does_not_reuse_an_unrelated_login_return_url( )
    {
        using var factory = new SliceAppFactory( );
        var transferId = factory.App.SignedIds.GenerateTransferId( Kevin, factory.Clock.GetUtcNow( ) );
        using var client = factory.Client( );

        await client.GetAsync( Routes.EditAccount );
        var token = await client.CsrfTokenAsync( Routes.SessionTransfer( transferId ) );
        var signedIn = await client.SubmitFormAsync( HttpMethod.Put, Routes.SessionTransfer( transferId ), token );

        Assert.Equal( factory.BaseAddress.ToString( ), signedIn.Location );
    }

    [Fact]
    public async Task Links_expire_after_four_hours( )
    {
        using var factory = new SliceAppFactory( );
        var transferId = factory.App.SignedIds.GenerateTransferId( Kevin, factory.Clock.GetUtcNow( ) );
        using var client = factory.Client( );
        var token = await client.CsrfTokenAsync( );

        factory.Clock.Advance( SignedIds.TransferLifetime + TimeSpan.FromSeconds( 1 ) );
        var expired = await client.SubmitFormAsync( HttpMethod.Put, Routes.SessionTransfer( transferId ), token );

        Assert.Equal( HttpStatusCode.BadRequest, expired.Status );
        Assert.Equal( "", expired.Body );
        Assert.Equal( 0L, factory.Scalar<long>( $"SELECT COUNT(*) FROM sessions WHERE user_id = {Kevin}" ) );
        Assert.DoesNotContain( BrowserAuthentication.CookieName + "=", client.CookieHeader, StringComparison.Ordinal );
    }

    [Fact]
    public async Task Forged_wrong_purpose_and_deactivated_links_are_refused( )
    {
        using var factory = new SliceAppFactory( );
        var now = factory.Clock.GetUtcNow( );
        var valid = factory.App.SignedIds.GenerateTransferId( Kevin, now );
        var avatarToken = factory.App.SignedIds.Generate( "User", Kevin, SignedIds.AvatarPurpose );
        var forged = valid[..^4] + "0000";
        using var client = factory.Client( );
        var token = await client.CsrfTokenAsync( );

        var wrongPurpose = await client.SubmitFormAsync( HttpMethod.Put, Routes.SessionTransfer( avatarToken ), token );
        var tampered = await client.SubmitFormAsync( HttpMethod.Put, Routes.SessionTransfer( forged ), token );
        factory.Execute( $"UPDATE users SET status = 1 WHERE id = {Kevin}" );
        var deactivated = await client.SubmitFormAsync( HttpMethod.Put, Routes.SessionTransfer( valid ), token );

        Assert.Equal( HttpStatusCode.BadRequest, wrongPurpose.Status );
        Assert.Equal( HttpStatusCode.BadRequest, tampered.Status );
        Assert.Equal( HttpStatusCode.BadRequest, deactivated.Status );
        Assert.Equal( 0L, factory.Scalar<long>( $"SELECT COUNT(*) FROM sessions WHERE user_id = {Kevin}" ) );
    }
}
