using System.Text;
using Microsoft.AspNetCore.Html;

namespace Campfire.Web.Views;

/// <summary>
/// Trusted markup: <c>ActiveSupport::SafeBuffer</c>. Slices write it without escaping, while plain strings are always
/// escaped by the slice write layer. Build it from escaped text (<see cref="Text"/>), from helpers, or from markup that
/// is already sanitized (<see cref="Raw"/>, e.g. rendered rich text).
/// </summary>
public sealed class SafeHtml : HtmlString, IEquatable<SafeHtml>
{
    public static new readonly SafeHtml Empty = new( string.Empty );

    private SafeHtml( string value )
        : base( value )
    {
    }

    public string Html => Value ?? string.Empty;

    public int Length => Html.Length;

    public bool IsEmpty => Html.Length == 0;

    /// <summary><c>String#html_safe</c> / <c>raw</c>: the caller vouches for <paramref name="html"/>.</summary>
    public static SafeHtml Raw( string? html ) => string.IsNullOrEmpty( html ) ? Empty : new SafeHtml( html );

    /// <summary><c>h(text)</c>: escaped plain text.</summary>
    public static SafeHtml Text( string? text ) => Raw( Escaping.Escape( text ) );

    /// <summary><c>safe_join(parts, separator)</c> for already-safe parts.</summary>
    public static SafeHtml Join( IEnumerable<SafeHtml> parts, string separator = "" )
    {
        ArgumentNullException.ThrowIfNull( parts );

        return Raw( string.Join( separator, parts.Select( part => part.Html ) ) );
    }

    /// <summary>Concatenates many parts without intermediate strings.</summary>
    public static SafeHtml Concat( params ReadOnlySpan<SafeHtml> parts )
    {
        var builder = new StringBuilder( );
        foreach ( var part in parts )
        {
            builder.Append( part.Html );
        }

        return Raw( builder.ToString( ) );
    }

    /// <summary><c>SafeBuffer#+</c> with another safe buffer.</summary>
    public static SafeHtml operator +( SafeHtml left, SafeHtml right )
    {
        ArgumentNullException.ThrowIfNull( left );
        ArgumentNullException.ThrowIfNull( right );

        return Raw( left.Html + right.Html );
    }

    public static SafeHtml Add( SafeHtml left, SafeHtml right ) => left + right;

    public bool Equals( SafeHtml? other ) => other is not null && string.Equals( Html, other.Html, StringComparison.Ordinal );

    public override bool Equals( object? obj ) => obj is SafeHtml other && Equals( other );

    public override int GetHashCode( ) => StringComparer.Ordinal.GetHashCode( Html );

    public override string ToString( ) => Html;
}
