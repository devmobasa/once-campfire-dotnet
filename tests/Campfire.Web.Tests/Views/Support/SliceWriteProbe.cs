using System.Numerics;
using System.Text;
using Campfire.Web.Views;
using Campfire.Web.Views.Rendering;
using Microsoft.AspNetCore.Html;

namespace Campfire.Web.Tests.Views.Support;

/// <summary>Exercises the protected calls emitted by Razor without changing the renderer's encoder.</summary>
internal sealed class SliceWriteProbe( string path, string? text ) : RailsSlice
{
    internal static readonly string[] TextPaths =
    [
        "string", "chars", "bytes", "byte-memory", "byte-span", "generic-string", "object-string",
        "object-bytes", "object-text", "html-string", "html-content", "object-html", "safe-html", "object-safe-html", "content-builder"
    ];

    public override Task ExecuteAsync( )
    {
        switch ( path )
        {
            case "scalars":
                Scalars( );
                break;
            case "attributes":
                Attributes( );
                break;
            case "attribute-text":
                AttributeText( text, false );
                break;
            case "attribute-literal":
                AttributeText( text, true );
                break;
            case "attribute-content":
                AttributeContent( text );
                break;
            default:
                WriteText( );
                break;
        }

        return Task.CompletedTask;
    }

    private void WriteText( )
    {
        switch ( path )
        {
            case "string":
                Write( text );
                break;
            case "chars":
                Write( text.AsSpan( ) );
                break;
            case "bytes":
                Write( text is null ? null! : Encoding.UTF8.GetBytes( text ) );
                break;
            case "byte-memory":
                Write( new ReadOnlyMemory<byte>( Encoding.UTF8.GetBytes( text ?? string.Empty ) ) );
                break;
            case "byte-span":
                Write( Encoding.UTF8.GetBytes( text ?? string.Empty ).AsSpan( ) );
                break;
            case "generic-string":
                Write<string>( text );
                break;
            case "object-string":
                Write<object>( text );
                break;
            case "object-bytes":
                Write<object>( text is null ? null : Encoding.UTF8.GetBytes( text ) );
                break;
            case "object-text":
                Write( text is null ? null : new TextValue( text ) );
                break;
            default:
                WriteHtmlText( );
                break;
        }
    }

    private void WriteHtmlText( )
    {
        switch ( path )
        {
            case "html-string":
                Write( new HtmlString( text ) );
                break;
            case "html-content":
                Write( ( IHtmlContent ) new HtmlString( text ) );
                break;
            case "object-html":
                Write<object>( new HtmlString( text ) );
                break;
            case "safe-html":
                Write( SafeHtml.Raw( text ) );
                break;
            case "object-safe-html":
                Write<object>( SafeHtml.Raw( text ) );
                break;
            case "content-builder":
                Write( new HtmlContentBuilder( ).Append( text ) );
                break;
            default:
                throw new ArgumentOutOfRangeException( nameof( path ), path, "Unknown write path." );
        }
    }

    private void Scalars( )
    {
        Write( '&' );
        Write( ( char? ) '\'' );
        Write( ( char? ) null );
        Separator( );
        Write( ( byte ) 1 );
        Write( ( byte? ) 2 );
        Write( ( byte? ) null );
        Separator( );
        Write( ( short ) 3 );
        Write( ( short? ) 4 );
        Write( ( short? ) null );
        Separator( );
        Write( ( ushort ) 5 );
        Write( ( ushort? ) 6 );
        Write( ( ushort? ) null );
        Separator( );
        Write( 7 );
        Write( ( int? ) 8 );
        Write( ( int? ) null );
        Separator( );
        Write( 9U );
        Write( ( uint? ) 10 );
        Write( ( uint? ) null );
        Separator( );
        Write( 11L );
        Write( ( long? ) 12 );
        Write( ( long? ) null );
        Separator( );
        Write( 13UL );
        Write( ( ulong? ) 14 );
        Write( ( ulong? ) null );
        Separator( );
        Write( new BigInteger( 15 ) );
        Write( ( BigInteger? ) new BigInteger( 16 ) );
        Write( ( BigInteger? ) null );
        Separator( );
        Write( ( Half ) 17 );
        Write( ( Half? ) 18 );
        Write( ( Half? ) null );
        Separator( );
        Write( 19F );
        Write( ( float? ) 20 );
        Write( ( float? ) null );
        Separator( );
        Write( 21D );
        Write( ( double? ) 22 );
        Write( ( double? ) null );
        Separator( );
        Write( 23M );
        Write( ( decimal? ) 24 );
        Write( ( decimal? ) null );
        Separator( );
        var date = new DateTime( 2026, 10, 18, 12, 34, 56, DateTimeKind.Utc );
        Write( date );
        Write( ( DateTime? ) date );
        Write( ( DateTime? ) null );
        Separator( );
        var offset = new DateTimeOffset( date );
        Write( offset );
        Write( ( DateTimeOffset? ) offset );
        Write( ( DateTimeOffset? ) null );
        Separator( );
        var duration = new TimeSpan( 12, 34, 56 );
        Write( duration );
        Write( ( TimeSpan? ) duration );
        Write( ( TimeSpan? ) null );
        Separator( );
        var day = new DateOnly( 2026, 10, 18 );
        Write( day );
        Write( ( DateOnly? ) day );
        Write( ( DateOnly? ) null );
        Separator( );
        var time = new TimeOnly( 12, 34, 56 );
        Write( time );
        Write( ( TimeOnly? ) time );
        Write( ( TimeOnly? ) null );
        Separator( );
        var id = new Guid( "12345678-1234-1234-1234-123456789abc" );
        Write( id );
        Write( ( Guid? ) id );
        Write( ( Guid? ) null );
        Separator( );
        Write( new Version( 1, 2, 3 ) );
        Write( ( Version? ) null );
        Separator( );
        Write( ( bool? ) true );
        Write( ( bool? ) false );
        Write( ( bool? ) null );
    }

    private void AttributeText( string? value, bool literal )
    {
        BeginWriteAttribute( "title", " title=\"", 0, "\"", 0, 1 );
        WriteAttributeValue( "", 0, value, 0, 0, literal );
        EndWriteAttribute( );
    }

    private void AttributeContent( string? value )
    {
        BeginWriteAttribute( "title", " title=\"", 0, "\"", 0, 1 );
        WriteAttributeValue( "", 0, ( IHtmlContent ) new HtmlString( value ), 0, 0, false );
        EndWriteAttribute( );
    }

    private void Attributes( )
    {
        BeginWriteAttribute( "checked", " checked=\"", 0, "\"", 0, 1 );
        WriteAttributeValue( "", 0, true, 0, 0, false );
        EndWriteAttribute( );
        BeginWriteAttribute( "disabled", " disabled=\"", 0, "\"", 0, 1 );
        WriteAttributeValue( "", 0, false, 0, 0, false );
        EndWriteAttribute( );
        BeginWriteAttribute( "hidden", " hidden=\"", 0, "\"", 0, 1 );
        WriteAttributeValue( "", 0, ( bool? ) null, 0, 0, false );
        EndWriteAttribute( );
        BeginWriteAttribute( "selected", " selected=\"", 0, "\"", 0, 1 );
        WriteAttributeValue( "", 0, ( bool? ) true, 0, 0, false );
        EndWriteAttribute( );
        BeginWriteAttribute( "readonly", " readonly=\"", 0, "\"", 0, 1 );
        WriteAttributeValue<object>( "", 0, true, 0, 0, false );
        EndWriteAttribute( );
        BeginWriteAttribute( "data-bool", " data-bool=\"", 0, "\"", 0, 1 );
        WriteAttributeValue( "pre:", 0, ( bool? ) false, 0, 0, false );
        EndWriteAttribute( );
        BeginWriteAttribute( "data-null", " data-null=\"", 0, "\"", 0, 1 );
        WriteAttributeValue<string>( "pre:", 0, null, 0, 0, false );
        EndWriteAttribute( );
        BeginWriteAttribute( "class", " class=\"", 0, "\"", 0, 3 );
        WriteAttributeValue( "", 0, "literal&", 0, 0, true );
        WriteAttributeValue<string>( " omitted:", 0, null, 0, 0, false );
        WriteAttributeValue( " ", 0, "'&+🎉", 0, 0, false );
        EndWriteAttribute( );
    }

    private void Separator( ) => WriteLiteral( "|" );

    private sealed record TextValue( string Value )
    {
        public override string ToString( ) => Value;
    }
}
