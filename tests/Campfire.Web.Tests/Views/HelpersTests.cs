using System.Globalization;
using Campfire.Web.Tests.Views.Support;
using Campfire.Web.Views;
using Campfire.Web.Views.Helpers;
using Campfire.Web.Views.Models;

namespace Campfire.Web.Tests.Views;

/// <summary>Rails helper semantics (Rust <c>crates/views/src/helpers/*</c> and <c>messages/support.rs</c> unit tests).</summary>
public sealed class HelpersTests
{
    [Fact]
    public void Escapes_like_erb_util( ) => Assert.Equal( "&lt;&amp;&gt;&quot;&#39;x é 🎉", Escaping.Escape( "<&>\"'x é 🎉" ) );

    [Fact]
    public void Renders_attributes_in_order_with_rails_value_rules( )
    {
        var attributes = new TagAttributes( ).Class( "a&b" ).Hidden( ).Required( false ).Data( "turbo_permanent", true ).Aria( "hidden", "true" ).Tabindex( -1 )
            .Attribute( "data-action", SafeHtml.Raw( "a->b" ) );

        Assert.Equal(
            " class=\"a&amp;b\" hidden=\"hidden\" data-turbo-permanent=\"true\" aria-hidden=\"true\" tabindex=\"-1\" data-action=\"a->b\"",
            attributes.Render( ) );
    }

    [Fact]
    public void Assignment_keeps_position_and_nil_keeps_its_slot( )
    {
        var attributes = new TagAttributes( ).Value( "x" ).Class( "c" );
        attributes.Attribute( "value", "y" );
        attributes.Attribute( "id", null );
        attributes.FetchOrSet( "id", "ignored" );

        Assert.Equal( " value=\"y\" class=\"c\"", attributes.Render( ) );
    }

    [Fact]
    public void Default_data_attributes_go_where_the_callers_data_starts( )
    {
        var attributes = new TagAttributes( ).Class( "direct" ).Id( "list_1" ).Data( "sorted_list_number", "5" ).Style( "s" );

        var merged = attributes.WithDefaultData( new TagAttributes( ).Data( "room_id", 1 ).Data( "sorted_list_number", "0" ) );

        Assert.Equal( " class=\"direct\" id=\"list_1\" data-room-id=\"1\" data-sorted-list-number=\"5\" style=\"s\"", merged.Render( ) );
    }

    [Fact]
    public void Builds_tags_like_the_tag_helper( )
    {
        Assert.Equal( "<meta name=\"x\">", TagHelper.BuilderTag( "meta", new TagAttributes( ).Name( "x" ) ).Html );
        Assert.Equal( "<turbo-frame id=\"f\"></turbo-frame>", TagHelper.BuilderTag( "turbo_frame", new TagAttributes( ).Id( "f" ) ).Html );
        Assert.Equal( "<img src=\"/a\" />", TagHelper.LegacyTag( "img", new TagAttributes( ).Attribute( "src", "/a" ) ).Html );
        Assert.Equal( "<textarea>\n&lt;x&gt;</textarea>", TagHelper.ContentTagText( "textarea", null, "<x>" ).Html );
        Assert.Equal( "<textarea></textarea>", TagHelper.BuilderTag( "textarea", null ).Html );
    }

    [Fact]
    public void Sanitizes_ids_and_nested_object_names( )
    {
        Assert.Equal( "account_settings", FormHelper.SanitizeObjectName( "account[settings]" ) );
        Assert.Equal( "user", FormHelper.SanitizeObjectName( "user" ) );
        Assert.Equal( "boost_content", FormHelper.SanitizeToId( "boost[content]" ) );
        Assert.Equal( "user_ids_", FormHelper.SanitizeToId( "user_ids[]" ) );
    }

    [Fact]
    public void Forms_keep_empty_frontend_fields_and_method_overrides( )
    {
        var viewContext = TestContexts.Plain( ) with
        {
            IncludeCsrfTags = true
        };

        var form = FormHelper.FormWith( viewContext, "http://campfire.test/rooms/1?x=1" ).Method( "PATCH" ).Open( );
        var button = FormHelper.ButtonTo( viewContext, "/messages/1/boosts/2", new TagAttributes( ).Method( "delete" ) );

        Assert.Contains(
            "<input type=\"hidden\" name=\"_method\" value=\"patch\" /><input type=\"hidden\" name=\"authenticity_token\" value=\"\" />",
            form.Html,
            StringComparison.Ordinal );
        Assert.Equal( "</button><input type=\"hidden\" name=\"authenticity_token\" value=\"\" /></form>", button.Close.Html );
    }

    [Fact]
    public void Outside_a_request_forms_carry_no_token( )
    {
        var viewContext = TestContexts.Plain( );

        Assert.Equal( "<form action=\"/x\" accept-charset=\"UTF-8\" method=\"post\">", FormHelper.FormWith( viewContext, "/x" ).Open( ).Html );
        Assert.Equal( SafeHtml.Empty, ApplicationHelper.CsrfMetaTags( viewContext ) );
    }

    [Fact]
    public void A_file_field_on_an_opened_form_needs_multipart_declared( )
    {
        var form = FormHelper.FormWith( TestContexts.Plain( ), "/x" );
        form.Open( );

        Assert.Throws<InvalidOperationException>( ( ) => form.FileField( "avatar" ) );
    }

    [Fact]
    public void Truncates_like_rails( )
    {
        Assert.Equal( "abc…", RubyText.Truncate( "abcdef", 4, "…" ) );
        Assert.Equal( "abcd", RubyText.Truncate( "abcd", 4, "…" ) );
        Assert.Equal( "🎉🎉…", RubyText.Truncate( "🎉🎉🎉🎉", 3, "…" ) );
    }

    [Fact]
    public void Builds_sentences( )
    {
        Assert.Equal( string.Empty, RubyText.ToSentence( [] ) );
        Assert.Equal( "A", RubyText.ToSentence( ["A"] ) );
        Assert.Equal( "A and B", RubyText.ToSentence( ["A", "B"] ) );
        Assert.Equal( "A+B", RubyText.ToSentence( ["A", "B"], "+" ) );
        Assert.Equal( "A, B, and C", RubyText.ToSentence( ["A", "B", "C"], "+" ) );
    }

    [Fact]
    public void Capitalizes_like_ruby( )
    {
        Assert.Equal( "Chrome", RubyText.Capitalize( "chROME" ) );
        Assert.Equal( "Élan", RubyText.Capitalize( "éLAN" ) );
    }

    [Fact]
    public void Encodes_urlsafe_base64( ) => Assert.Equal( "aHR0cDovL3gvP2E=", RubyText.UrlSafeBase64( "http://x/?a" ) );

    [Fact]
    public void Epoch_truncates_through_a_float_like_ruby( )
    {
        Assert.Equal( 1790425426483, RubyText.EpochMilliseconds( DateTimeOffset.Parse( "2026-09-26T12:23:46.483521Z", CultureInfo.InvariantCulture ) ) );
        Assert.Equal( 1790421826000, RubyText.EpochMilliseconds( DateTimeOffset.Parse( "2026-09-26T11:23:46Z", CultureInfo.InvariantCulture ) ) );
    }

    [Theory]
    [InlineData( 600.0, "600.0" )]
    [InlineData( 1.0, "1.0" )]
    [InlineData( 16.0 / 9.0, "1.7777777777777777" )]
    [InlineData( 1e16, "1.0e+16" )]
    [InlineData( 1e15, "1000000000000000.0" )]
    [InlineData( 0.0001, "0.0001" )]
    [InlineData( 0.00001, "1.0e-05" )]
    [InlineData( 1.5e20, "1.5e+20" )]
    [InlineData( -2.5, "-2.5" )]
    public void Formats_floats_like_ruby( double value, string expected ) => Assert.Equal( expected, RubyText.RubyFloat( value ) );

    [Fact]
    public void Halves_numbers_like_ruby( )
    {
        Assert.Equal( "320", RubyNumber.Whole( 641 ).Half( ).ToString( ) );
        Assert.Equal( "320.5", RubyNumber.Real( 641 ).Half( ).ToString( ) );
    }

    [Fact]
    public void Formats_times_like_active_support( )
    {
        var time = DateTimeOffset.Parse( "2026-09-26T12:26:46.8489991Z", CultureInfo.InvariantCulture );

        Assert.Equal( "2026-09-26T12:26:46Z", RubyText.Iso8601( time ) );
        Assert.Equal( "2026-09-26T12:26:46.848Z", RubyText.JsonTime( time ) );
        Assert.Equal( "20260926122646", RubyText.NumberTimestamp( time ) );
        Assert.Equal( "20260926122646848999", RubyText.UsecTimestamp( time ) );
    }

    [Fact]
    public void Computes_initials_like_ruby( )
    {
        Assert.Equal( "SoB3_", UsersHelper.Initials( "Élodie Ünal-Smith o'Brien 3po _x ñ" ) );
        Assert.Equal( "DHH", UsersHelper.Initials( "David Heinemeier Hansson" ) );
    }

    [Fact]
    public void Crc32_matches_zlib( ) => Assert.Equal( 0xCBF4_3926u, UsersHelper.Crc32( "123456789"u8 ) );

    [Fact]
    public void Titles_join_name_and_bio( )
    {
        Assert.Equal( "JZ – Designer", UsersHelper.UserTitle( "JZ", "Designer" ) );
        Assert.Equal( "JZ", UsersHelper.UserTitle( "JZ", "  " ) );
    }

    [Fact]
    public void Direct_room_labels_join_initials( )
    {
        UserSummary Named( string name ) => new( )
        {
            Id = 1,
            Name = name,
            AvatarPath = "/a"
        };

        Assert.Equal( "JF+KM", UsersHelper.DirectMemberInitials( [Named( "jason fried" ), Named( "Kevin mcConnell" )] ) );
        Assert.Equal( "A, B, and CDE", UsersHelper.DirectMemberInitials( [Named( "a" ), Named( "b" ), Named( "c d e f" )] ) );
    }

    [Fact]
    public void Builds_turbo_streams_like_turbo_rails( )
    {
        Assert.Equal(
            "<turbo-stream maintain_scroll=\"true\" action=\"append\" target=\"x\"><template><b>y</b></template></turbo-stream>",
            TurboHelper.TurboStreamActionTag( "append", "x", SafeHtml.Raw( "<b>y</b>" ), new TagAttributes( ).Attribute( "maintain_scroll", "true" ) ).Html );
        Assert.Equal( "<turbo-stream action=\"remove\" target=\"x\"></turbo-stream>", TurboHelper.TurboStreamActionTag( "remove", "x" ).Html );
    }

    [Fact]
    public void Involvements_cycle_like_rails( )
    {
        Assert.Equal( "everything", RoomsHelper.NextInvolvement( false, "mentions" ) );
        Assert.Equal( "mentions", RoomsHelper.NextInvolvement( false, "invisible" ) );
        Assert.Equal( "nothing", RoomsHelper.NextInvolvement( true, "everything" ) );
        Assert.Equal( "everything", RoomsHelper.NextInvolvement( true, "nothing" ) );
    }

    [Fact]
    public void Unknown_translation_keys_fail_loudly( ) => Assert.Throws<ArgumentException>( ( ) => TranslationsHelper.TranslationsFor( "nope" ) );
}
