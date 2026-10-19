using System.Text.RegularExpressions;
using Campfire.Web.Views.Rendering;
using RazorSlices;

namespace Campfire.Web.Tests.Views.Support;

/// <summary>Renders slices and compares them with the Rails goldens.</summary>
internal static partial class Parity
{
    public static string DiffDirectory => Path.Combine( AppContext.BaseDirectory, "views-diff" );

    public static Task<string> Render( RazorSlice slice ) => SliceRenderer.RenderToStringAsync( slice, TestContext.Current.CancellationToken ).AsTask( );

    /// <summary>
    /// Parity with golden <c>vectors/views/a/&lt;name&gt;.&lt;extension&gt;</c>: first the Rust views-A comparison (DOM
    /// token streams), then byte equality with CSRF tokens masked, which the Rust port didn't reach.
    /// </summary>
    public static void AssertDom( string name, string extension, string rendered )
    {
        var golden = PageGoldenFacts.Golden( name, extension );
        AssertDomAgainst( name, extension, golden, rendered );
    }

    /// <summary>Rails 27f5461 closes the transfer form. Preserve the older fixture and adapt only its missing closing tag.</summary>
    public static void AssertSessionTransferDom( string rendered )
    {
        const string name = "sessions_transfer";
        const string opening = "<form data-controller=\"auto-submit\"";
        var golden = PageGoldenFacts.Golden( name, "html" );
        Assert.Equal( 2, golden.Split( opening, StringSplitOptions.None ).Length );
        var start = golden.IndexOf( opening, StringComparison.Ordinal );
        var end = golden.IndexOf( '\n', start );
        Assert.EndsWith( " />", golden[start..end], StringComparison.Ordinal );
        Assert.DoesNotContain( "</form>", golden[start..end], StringComparison.Ordinal );
        AssertDomAgainst( name, "html", golden.Insert( end, "</form>" ), rendered );
    }

    /// <summary>
    /// Recorded native difference for admin account edit forms: Rails' singular-resource helper
    /// treats the account id as the <c>.873240054</c> suffix; native forms post to <c>/account</c>.
    /// The rewrite applies only to these explicitly selected account edit comparisons.
    /// </summary>
    public static void AssertAdminAccountEditDom( string name, string extension, string rendered )
    {
        const string railsAction = "action=\"/account.873240054\"";
        const string nativeAction = "action=\"/account\"";
        var golden = PageGoldenFacts.Golden( name, extension );
        Assert.Contains( railsAction, golden, StringComparison.Ordinal );
        Assert.Contains( nativeAction, rendered, StringComparison.Ordinal );
        Assert.DoesNotContain( railsAction, rendered, StringComparison.Ordinal );
        AssertDomAgainst( name, extension, golden.Replace( railsAction, nativeAction, StringComparison.Ordinal ), rendered );
    }

    private static void AssertDomAgainst( string name, string extension, string golden, string rendered )
    {
        var expected = DomTokens.Normalize( golden );
        var actual = DomTokens.Normalize( rendered );
        Save( $"{name}.{extension}", rendered );
        if ( DomTokens.Diff( expected, actual ) is { } report )
        {
            Assert.Fail( $"{name}: DOM differs from the reference\n{report}" );
        }

        AssertBytes( name, golden, rendered );
    }

    /// <summary>Byte equality with the golden, CSRF tokens masked on both sides; reports the first differing line.</summary>
    public static void AssertBytes( string name, string golden, string rendered )
    {
        var expected = MaskTokens( golden );
        var actual = MaskTokens( rendered );
        if ( expected == actual )
        {
            return;
        }

        var offset = 0;
        while ( offset < expected.Length && offset < actual.Length && expected[offset] == actual[offset] )
        {
            offset++;
        }

        var line = expected.AsSpan( 0, offset ).Count( '\n' ) + 1;
        string Around( string text ) => text.Substring( Math.Max( 0, offset - 60 ), Math.Min( 120, text.Length - Math.Max( 0, offset - 60 ) ) );
        Assert.Fail( $"{name}: bytes differ at offset {offset} (line {line})\n  rails:  {Around( expected )}\n  dotnet: {Around( actual )}" );
    }

    public static void Save( string file, string content )
    {
        Directory.CreateDirectory( DiffDirectory );
        File.WriteAllText( Path.Combine( DiffDirectory, file ), content );
    }

    public static string MaskTokens( string html )
    {
        var masked = AuthenticityToken( ).Replace( html, "name=\"authenticity_token\" value=\"[MASKED]\"" );
        return CsrfMeta( ).Replace( masked, "name=\"csrf-token\" content=\"[MASKED]\"" );
    }

    [GeneratedRegex( "name=\"authenticity_token\" value=\"[^\"]*\"" )]
    private static partial Regex AuthenticityToken( );

    [GeneratedRegex( "name=\"csrf-token\" content=\"[^\"]*\"" )]
    private static partial Regex CsrfMeta( );
}
