using System.Numerics;
using System.Text;
using Microsoft.AspNetCore.Html;
using RazorSlices;

namespace Campfire.Web.Views.Rendering;

/// <summary>Rails escaping for generated Razor calls; the dependency encoder remains a safe fallback.</summary>
public abstract class RailsSlice<TModel> : RazorSlice<TModel>, ISliceOutput
{
    protected new void Write( char value ) => SliceWriting.WriteValue( this, value );

    protected new void Write( char? value ) => SliceWriting.WriteNullable( this, value );

    protected new void Write( byte value ) => SliceWriting.WriteValue( this, value );

    protected new void Write( byte? value ) => SliceWriting.WriteNullable( this, value );

    protected new void Write( short value ) => SliceWriting.WriteValue( this, value );

    protected new void Write( short? value ) => SliceWriting.WriteNullable( this, value );

    protected new void Write( ushort value ) => SliceWriting.WriteValue( this, value );

    protected new void Write( ushort? value ) => SliceWriting.WriteNullable( this, value );

    protected new void Write( int value ) => SliceWriting.WriteValue( this, value );

    protected new void Write( int? value ) => SliceWriting.WriteNullable( this, value );

    protected new void Write( uint value ) => SliceWriting.WriteValue( this, value );

    protected new void Write( uint? value ) => SliceWriting.WriteNullable( this, value );

    protected new void Write( long value ) => SliceWriting.WriteValue( this, value );

    protected new void Write( long? value ) => SliceWriting.WriteNullable( this, value );

    protected new void Write( ulong value ) => SliceWriting.WriteValue( this, value );

    protected new void Write( ulong? value ) => SliceWriting.WriteNullable( this, value );

    protected new void Write( BigInteger value ) => SliceWriting.WriteValue( this, value );

    protected new void Write( BigInteger? value ) => SliceWriting.WriteNullable( this, value );

    protected new void Write( Half value ) => SliceWriting.WriteValue( this, value );

    protected new void Write( Half? value ) => SliceWriting.WriteNullable( this, value );

    protected new void Write( float value ) => SliceWriting.WriteValue( this, value );

    protected new void Write( float? value ) => SliceWriting.WriteNullable( this, value );

    protected new void Write( double value ) => SliceWriting.WriteValue( this, value );

    protected new void Write( double? value ) => SliceWriting.WriteNullable( this, value );

    protected new void Write( decimal value ) => SliceWriting.WriteValue( this, value );

    protected new void Write( decimal? value ) => SliceWriting.WriteNullable( this, value );

    protected new void Write( DateTime value ) => SliceWriting.WriteValue( this, value );

    protected new void Write( DateTime? value ) => SliceWriting.WriteNullable( this, value );

    protected new void Write( DateTimeOffset value ) => SliceWriting.WriteValue( this, value );

    protected new void Write( DateTimeOffset? value ) => SliceWriting.WriteNullable( this, value );

    protected new void Write( TimeSpan value ) => SliceWriting.WriteValue( this, value );

    protected new void Write( TimeSpan? value ) => SliceWriting.WriteNullable( this, value );

    protected new void Write( DateOnly value ) => SliceWriting.WriteValue( this, value );

    protected new void Write( DateOnly? value ) => SliceWriting.WriteNullable( this, value );

    protected new void Write( TimeOnly value ) => SliceWriting.WriteValue( this, value );

    protected new void Write( TimeOnly? value ) => SliceWriting.WriteNullable( this, value );

    protected new void Write( Guid value ) => SliceWriting.WriteValue( this, value );

    protected new void Write( Guid? value ) => SliceWriting.WriteNullable( this, value );

    void ISliceOutput.WriteUtf8( ReadOnlySpan<byte> value ) => WriteLiteral( value );

    protected new void Write( string? value )
    {
        if ( string.IsNullOrEmpty( value ) )
        {
            return;
        }

        var first = value.AsSpan( ).IndexOfAny( Escaping.SpecialCharacters );
        if ( first < 0 )
        {
            WriteLiteral( value );
            return;
        }

        SliceWriting.WriteText( this, value.AsSpan( ), first );
    }

    protected new void Write( ReadOnlySpan<char> value ) => SliceWriting.WriteText( this, value );

    protected new void Write( byte[] value ) => Write( value.AsSpan( ) );

    protected new void Write( ReadOnlyMemory<byte> value ) => Write( value.Span );

    protected new void Write( ReadOnlySpan<byte> value ) => Write( Encoding.UTF8.GetString( value ) );

    protected new void Write( HtmlString value ) => base.Write( value );

    protected new void Write( IHtmlContent? value ) => base.Write( value );

    protected new void Write( bool? value ) => base.Write( value );

    protected new void Write( Version? value )
    {
        if ( value is not null )
        {
            SliceWriting.WriteValue( this, value );
        }
    }

    protected new void Write<T>( T? value )
    {
        switch ( value )
        {
            case null:
                return;
            case string text:
                Write( text );
                break;
            case byte[] bytes:
                Write( bytes );
                break;
            case HtmlString html:
                base.Write( html );
                break;
            case IHtmlContent content:
                base.Write( content );
                break;
            case IUtf8SpanFormattable formatted:
                SliceWriting.WriteValue( this, formatted );
                break;
            case ISpanFormattable formatted:
                SliceWriting.WriteSpanValue( this, formatted );
                break;
            default:
                Write( value.ToString( ) );
                break;
        }
    }

    protected new void WriteAttributeValue( string prefix, int prefixOffset, bool value, int valueOffset, int valueLength, bool isLiteral ) =>
        base.WriteAttributeValue( prefix, prefixOffset, value, valueOffset, valueLength, isLiteral );

    protected new void WriteAttributeValue( string prefix, int prefixOffset, bool? value, int valueOffset, int valueLength, bool isLiteral ) =>
        base.WriteAttributeValue( prefix, prefixOffset, value, valueOffset, valueLength, isLiteral );

    protected new void WriteAttributeValue<TValue>( string prefix, int prefixOffset, TValue? value, int valueOffset, int valueLength, bool isLiteral )
    {
        if ( isLiteral || value is null or bool or IHtmlContent )
        {
            base.WriteAttributeValue( prefix, prefixOffset, value, valueOffset, valueLength, isLiteral );
        }
        else
        {
            // The non-null empty value emits the dependency's attribute/value prefixes. The
            // safe write then emits the escaped value without an intermediate string.
            base.WriteAttributeValue( prefix, prefixOffset, HtmlString.Empty, valueOffset, valueLength, isLiteral: false );
            Write( value );
        }
    }
}

