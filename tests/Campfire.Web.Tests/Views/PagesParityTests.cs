using System.Text.Json.Nodes;
using Campfire.Web.Tests.Views.Support;
using Campfire.Web.Views;
using Campfire.Web.Views.Models;
using static Campfire.Web.Tests.Views.Support.PageGoldenFacts;
using Slices = Campfire.Web.Views;

namespace Campfire.Web.Tests.Views;

/// <summary>DOM parity of the sessions, first-run, welcome, autocompletable, Action Text and mailer views (Rust <c>parity_a.rs</c>).</summary>
public sealed class PagesParityTests
{
    [Theory]
    [InlineData( "sessions_new", null, null )]
    [InlineData( "sessions_new_email", "x@y.com", null )]
    [InlineData( "sessions_new_rejected", "david@37signals.com", "Too many requests or unauthorized." )]
    [InlineData( "sessions_new_with_logo", null, null )]
    public async Task Sign_in_matches_rails( string name, string? email, string? alert )
    {
        var model = new SessionsNewPage( Context( name, new PageGoldenRequest( FlashAlert: alert ) ), email, HelpContact( name ) );

        var html = await Parity.Render( Slices.sessions.new_html.Create( model ) );

        Parity.AssertDom( name, "html", html );
    }

    [Fact]
    public async Task Session_transfer_matches_the_complete_rails_form( )
    {
        const string name = "sessions_transfer";

        var html = await Parity.Render( Slices.sessions.transfers.show.Create( new SessionTransferPage(
            Context( name ),
            Case( name )["path"].RequiredString( ) ) ) );

        Parity.AssertSessionTransferDom( html );
    }

    [Fact]
    public async Task First_run_matches_rails( )
    {
        const string name = "first_run";

        var html = await Parity.Render( Slices.first_runs.show.Create( new FirstRunPage( Context( name ) ) ) );

        Parity.AssertDom( name, "html", html );
    }

    [Fact]
    public async Task Welcome_matches_rails( )
    {
        const string name = "welcome";

        var html = await Parity.Render( Slices.welcome.show.Create( new WelcomePage( Context( name ) ) ) );

        Parity.AssertDom( name, "html", html );
    }

    [Fact]
    public async Task Autocompletable_users_match_rails( )
    {
        const string name = "autocompletable_users";
        var users = Data( name )["autocompletable"]!.AsArray( ).Select( user => Mention( name, user.RequiredString( ) ) ).ToList( );

        var html = await Parity.Render( Slices.autocompletable.users.index.Create( new AutocompletableUsersPage( Context( name ), users ) ) );

        Parity.AssertDom( name, "html", html );
    }

    [Theory]
    [InlineData( "og_embed" )]
    [InlineData( "og_embed_twitter" )]
    [InlineData( "og_embed_long_no_href" )]
    public async Task Opengraph_embeds_match_rails( string name )
    {
        var attributes = JsonNode.Parse( File.ReadAllText( GoldenPath( "og_embeds.json" ) ) )![name]!;
        var embed = new OpengraphEmbed(
            attributes["href"].OptionalString( ),
            attributes["url"].OptionalString( ),
            attributes["filename"].RequiredString( ),
            attributes["description"].OptionalString( ) );

        var html = await Parity.Render( Slices.action_text.attachables._opengraph_embed.Create( new OpengraphEmbedPartial( embed ) ) );

        Parity.AssertDom( name, "html", html );
    }

    [Fact]
    public async Task Action_text_content_layout_matches_rails_byte_for_byte( )
    {
        const string name = "action_text_content";

        var content = new ActionTextContent( SafeHtml.Raw( "<p>Hi <strong>there</strong></p>\n" ) );
        var html = await Parity.Render( Slices.layouts.action_text.contents._content.Create( content ) );

        Parity.AssertDom( name, "html", html );
        Assert.Equal( Golden( name, "html" ), html );
    }

    [Fact]
    public async Task Mailer_layout_matches_rails( )
    {
        const string name = "mailer_layout";

        var html = await Parity.Render( Slices.layouts.mailer.Create( new MailerLayout( SafeHtml.Raw( "<p>Mail</p>" ) ) ) );

        Parity.AssertDom( name, "html", html );
    }
}
