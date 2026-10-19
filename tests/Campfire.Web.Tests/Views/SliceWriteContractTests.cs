using System.Buffers;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using Campfire.Web.Tests.Views.Support;
using Campfire.Web.Views;
using Campfire.Web.Views.Rendering;
using Microsoft.AspNetCore.Html;
using RazorSlices;

namespace Campfire.Web.Tests.Views;

/// <summary>Rails-compatible escaping at the actual protected Razor write boundary.</summary>
public sealed class SliceWriteContractTests
{
    private const string Text = "&<>\"'+ café Ελληνικά 🎉";
    private const string Escaped = "&amp;&lt;&gt;&quot;&#39;+ café Ελληνικά 🎉";

    public static TheoryData<string> TextPaths => new( SliceWriteProbe.TextPaths );

    [Theory]
    [MemberData( nameof( TextPaths ) )]
    public async Task Text_writes_preserve_the_declared_escaping_contract( string path )
    {
        var expected = path switch
        {
            "html-string" or "html-content" or "object-html" or "safe-html" or "object-safe-html" => Text,
            "content-builder" => HtmlEncoder.Default.Encode( Text ),
            _ => Escaped
        };
        await AssertOutput( path, Text, expected );
        await AssertOutput( path, "", "" );
        await AssertOutput( path, null, "" );
    }

    [Theory]
    [MemberData( nameof( TextPaths ) )]
    public async Task Malformed_utf16_preserves_the_characterized_wire_bytes( string path )
    {
        var trusted = path is "html-string" or "html-content" or "object-html" or "safe-html" or "object-safe-html";
        foreach ( var (text, escaped, raw) in new[]
        {
            ("\ud800", "\ufffd", "\ufffd"), ("\udfff", "\ufffd", "\ufffd"),
            ("\ud800&", "\ufffd&amp;", "\ufffd&"), ("&\ud800", "&amp;\ufffd", "&\ufffd"),
            ("<&\udfff'", "&lt;&amp;\ufffd&#39;", "<&\ufffd'")
        } )
        {
            var expected = path == "content-builder" ? HtmlEncoder.Default.Encode( text ) : trusted ? raw : escaped;
            Assert.Equal( Encoding.UTF8.GetBytes( expected ), await SliceRenderer.RenderToUtf8Async( new SliceWriteProbe( path, text ), TestContext.Current.CancellationToken ) );
        }
    }

    [Fact]
    public async Task Scalar_overloads_run_with_exact_text_and_wire_output( )
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            const string expected = "&amp;&#39;|12|34|56|78|910|1112|1314|1516|1718|1920|2122|2324|"
                + "10/18/2026 12:34:5610/18/2026 12:34:56|"
                + "10/18/2026 12:34:56 +00:0010/18/2026 12:34:56 +00:00|"
                + "12:34:5612:34:56|10/18/202610/18/2026|12:3412:34|"
                + "12345678-1234-1234-1234-123456789abc12345678-1234-1234-1234-123456789abc|1.2.3|";
            await AssertOutput( "scalars", "", expected + "TrueFalse", expected );
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData( "plain" )]
    [InlineData( "generic" )]
    [InlineData( "layout" )]
    public async Task All_slice_roots_render_the_same_escaped_values_and_attributes( string root )
    {
        const string expected = "&amp;&#39;|42|&lt;&amp;🎉| raw&| title=\"&#39;&amp;\"";
        RazorSlice Create( ) => root switch
        {
            "plain" => new PlainRootProbe( ),
            "generic" => new GenericRootProbe { Model = "<&🎉" },
            "layout" => new LayoutRootProbe { Model = "<&🎉" },
            _ => throw new ArgumentOutOfRangeException( nameof( root ) )
        };

        Assert.Equal( expected, await SliceRenderer.RenderToStringAsync( Create( ), TestContext.Current.CancellationToken ) );
        Assert.Equal( Encoding.UTF8.GetBytes( expected ), await SliceRenderer.RenderToUtf8Async( Create( ), TestContext.Current.CancellationToken ) );
    }

    [Fact]
    public void Long_escaped_text_writes_bounded_chunks_without_splitting_surrogate_pairs( )
    {
        var text = new string( '&', 681 ) + "🎉" + new string( '\'', 683 ) + "é\ud800<";
        var expected = string.Concat( Enumerable.Repeat( "&amp;", 681 ) ) + "🎉"
            + string.Concat( Enumerable.Repeat( "&#39;", 683 ) ) + "é\ufffd&lt;";
        var output = new BoundedOutput( );

        SliceWriting.WriteText( output, text.AsSpan( ) );

        Assert.Equal( expected, Encoding.UTF8.GetString( output.Bytes.WrittenSpan ) );
        Assert.InRange( output.LargestWrite, 1, 4096 );
    }

    [Fact]
    public async Task Large_escaped_values_preserve_bytes_above_the_reusable_buffer_limit( )
    {
        var text = string.Concat( Enumerable.Repeat( "<&'\"+ café 🎉>", 1000 ) );
        var expected = string.Concat( Enumerable.Repeat( "&lt;&amp;&#39;&quot;+ café 🎉&gt;", 1000 ) );
        await AssertOutput( "string", text, expected );
        await AssertOutput( "chars", text, expected );
        await AssertOutput( "attribute-text", text, $" title=\"{expected}\"" );
    }

    [Theory]
    [InlineData( typeof( RailsSlice ) )]
    [InlineData( typeof( RailsSlice<> ) )]
    [InlineData( typeof( RailsLayoutSlice<> ) )]
    public void Every_dependency_write_overload_is_intercepted_by_each_slice_base( Type sliceBase )
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var dependency = typeof( RazorSlice ).GetMethods( flags ).Where( IsWrite ).Select( Signature ).Order( StringComparer.Ordinal );
        var intercepted = sliceBase.GetMethods( flags ).Where( IsWrite ).Select( Signature ).Order( StringComparer.Ordinal );

        Assert.Equal( dependency, intercepted );

        static bool IsWrite( MethodInfo method ) => method.Name is "Write" or "WriteAttributeValue";
        static string Signature( MethodInfo method ) => $"{method.Name}`{method.GetGenericArguments( ).Length}({string.Join( ",", method.GetParameters( ).Select( parameter => TypeName( parameter.ParameterType ) ) )})";
        static string TypeName( Type type ) => type.IsGenericParameter
            ? $"`{type.GenericParameterPosition}"
            : type.IsGenericType
                ? $"{type.GetGenericTypeDefinition( ).FullName}<{string.Join( ",", type.GetGenericArguments( ).Select( TypeName ) )}>"
                : type.FullName!;
    }

    [Fact]
    public async Task Attribute_rules_preserve_literals_prefixes_nulls_and_boolean_names( )
    {
        await AssertOutput( "attribute-text", Text, $" title=\"{Escaped}\"" );
        await AssertOutput( "attribute-text", "", " title=\"\"" );
        await AssertOutput( "attribute-text", null, "" );
        await AssertOutput( "attribute-literal", Text, $" title=\"{Text}\"" );
        await AssertOutput( "attribute-content", Text, $" title=\"{Text}\"" );
        const string attributes = " checked=\"checked\" selected=\"selected\" readonly=\"readonly\" data-bool=\"pre:False\" data-null=\"\" class=\"literal& &#39;&amp;+🎉\"";
        // RazorSlices 0.12.1's pipe bool writer does not advance its output buffer. Preserve that
        // existing transport difference here; changing dependency bool output is a separate contract.
        await AssertOutput( "attributes", "", attributes, attributes.Replace( "pre:False", "pre:", StringComparison.Ordinal ) );
    }

    [Fact]
    public void New_app_html_content_implementations_require_an_escaping_contract_review( )
    {
        // Custom WriteTo implementations receive the Default encoder. A new implementation must be
        // reviewed before expanding the escaping contract beyond the existing raw SafeHtml.
        var assemblies = new Stack<Assembly>( [typeof( SafeHtml ).Assembly] );
        var seen = new HashSet<Assembly>( );
        var implementations = new List<Type>( );
        while ( assemblies.TryPop( out var assembly ) )
        {
            if ( !seen.Add( assembly ) )
            {
                continue;
            }

            implementations.AddRange( assembly.GetTypes( ).Where( type => type.IsClass && typeof( IHtmlContent ).IsAssignableFrom( type ) ) );
            foreach ( var reference in assembly.GetReferencedAssemblies( ).Where( name => name.Name?.StartsWith( "Campfire.", StringComparison.Ordinal ) == true ) )
            {
                assemblies.Push( Assembly.Load( reference ) );
            }
        }

        Assert.Equal( typeof( SafeHtml ), Assert.Single( implementations ) );
    }

    private static async Task AssertOutput( string path, string? text, string expected, string? expectedUtf8 = null )
    {
        Assert.Equal( expected, await SliceRenderer.RenderToStringAsync( new SliceWriteProbe( path, text ), TestContext.Current.CancellationToken ) );
        Assert.Equal( Encoding.UTF8.GetBytes( expectedUtf8 ?? expected ), await SliceRenderer.RenderToUtf8Async( new SliceWriteProbe( path, text ), TestContext.Current.CancellationToken ) );
    }

    private sealed class BoundedOutput : ISliceOutput
    {
        internal ArrayBufferWriter<byte> Bytes { get; } = new( );

        internal int LargestWrite { get; private set; }

        public void WriteUtf8( ReadOnlySpan<byte> value )
        {
            LargestWrite = Math.Max( LargestWrite, value.Length );
            value.CopyTo( Bytes.GetSpan( value.Length ) );
            Bytes.Advance( value.Length );
        }
    }

    private sealed class PlainRootProbe : RailsSlice
    {
        public override Task ExecuteAsync( )
        {
            Write( '&' );
            Write( ( char? ) '\'' );
            WriteLiteral( "|" );
            Write( ( int? ) 42 );
            Write( ( int? ) null );
            WriteLiteral( "|" );
            Write( "<&🎉" );
            WriteLiteral( "|" );
            Write( new HtmlString( " raw&" ) );
            WriteLiteral( "|" );
            BeginWriteAttribute( "title", " title=\"", 0, "\"", 0, 1 );
            WriteAttributeValue( "", 0, "'&", 0, 0, false );
            EndWriteAttribute( );
            return Task.CompletedTask;
        }
    }

    private sealed class GenericRootProbe : RailsSlice<string>
    {
        public override Task ExecuteAsync( )
        {
            Write( '&' );
            Write( ( char? ) '\'' );
            WriteLiteral( "|" );
            Write( ( int? ) 42 );
            Write( ( int? ) null );
            WriteLiteral( "|" );
            Write( Model );
            WriteLiteral( "|" );
            Write( new HtmlString( " raw&" ) );
            WriteLiteral( "|" );
            BeginWriteAttribute( "title", " title=\"", 0, "\"", 0, 1 );
            WriteAttributeValue( "", 0, "'&", 0, 0, false );
            EndWriteAttribute( );
            return Task.CompletedTask;
        }
    }

    private sealed class LayoutRootProbe : RailsLayoutSlice<string>
    {
        public override Task ExecuteAsync( )
        {
            Write( '&' );
            Write( ( char? ) '\'' );
            WriteLiteral( "|" );
            Write( ( int? ) 42 );
            Write( ( int? ) null );
            WriteLiteral( "|" );
            Write( Model );
            WriteLiteral( "|" );
            Write( new HtmlString( " raw&" ) );
            WriteLiteral( "|" );
            BeginWriteAttribute( "title", " title=\"", 0, "\"", 0, 1 );
            WriteAttributeValue( "", 0, "'&", 0, 0, false );
            EndWriteAttribute( );
            return Task.CompletedTask;
        }
    }
}
