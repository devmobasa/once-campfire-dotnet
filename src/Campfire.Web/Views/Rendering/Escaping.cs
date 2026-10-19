using System.Buffers;
using System.Text;

namespace Campfire.Web.Views;

/// <summary>
/// <c>ERB::Util.html_escape</c>: only <c>&amp; &lt; &gt; &quot; '</c> are escaped, as <c>&amp;amp; &amp;lt; &amp;gt;
/// &amp;quot; &amp;#39;</c>. The one entity table here serves the helpers that assemble markup outside a slice and
/// the safe write members used by every slice.
/// </summary>
public static class Escaping
{
    /// <summary>The characters <c>html_escape</c> replaces, for vectorized scans.</summary>
    internal static readonly SearchValues<char> SpecialCharacters = SearchValues.Create( "&<>\"'" );

    private const string QuoteEntity = "&quot;";

    /// <summary>Each special character's entity, indexed by the character; <see langword="null"/> for any other.</summary>
    private static readonly string?[] Entities = CreateEntities( );

    /// <summary>The entity <paramref name="character"/> becomes, or <see langword="null"/> when it stays as it is.</summary>
    internal static string? Entity( int character ) => ( uint ) character < ( uint ) Entities.Length ? Entities[character] : null;

    /// <summary>The length <paramref name="text"/> has once escaped (<see cref="WriteEscaped"/>).</summary>
    internal static int EscapedLength( ReadOnlySpan<char> text, bool quotesOnly = false )
    {
        var length = text.Length;
        for ( var special = IndexOfSpecial( text, quotesOnly ); special >= 0; special = IndexOfSpecial( text, quotesOnly ) )
        {
            length += Replacement( text[special], quotesOnly ).Length - 1;
            text = text[(special + 1)..];
        }

        return length;
    }

    /// <summary>Writes <paramref name="text"/> escaped into <paramref name="output"/> and returns the characters written.</summary>
    internal static int WriteEscaped( Span<char> output, ReadOnlySpan<char> text, bool quotesOnly = false )
    {
        var position = 0;
        for ( var special = IndexOfSpecial( text, quotesOnly ); special >= 0; special = IndexOfSpecial( text, quotesOnly ) )
        {
            text[..special].CopyTo( output[position..] );
            position += special;
            var replacement = Replacement( text[special], quotesOnly );
            replacement.CopyTo( output[position..] );
            position += replacement.Length;
            text = text[(special + 1)..];
        }

        text.CopyTo( output[position..] );
        return position + text.Length;
    }

    /// <summary>Escapes <c>&amp; &lt; &gt; &quot; '</c> as ERB does; <see langword="null"/> is the empty string.</summary>
    public static string Escape( string? text )
    {
        if ( string.IsNullOrEmpty( text ) )
        {
            return string.Empty;
        }

        var first = text.AsSpan( ).IndexOfAny( SpecialCharacters );
        if ( first < 0 )
        {
            return text;
        }

        var builder = new StringBuilder( text.Length + 16 );
        builder.Append( text, 0, first );
        AppendEscaped( builder, text.AsSpan( first ) );
        return builder.ToString( );
    }

    /// <summary>Appends <paramref name="text"/> escaped.</summary>
    public static void AppendEscaped( StringBuilder builder, ReadOnlySpan<char> text )
    {
        ArgumentNullException.ThrowIfNull( builder );

        while ( !text.IsEmpty )
        {
            var special = text.IndexOfAny( SpecialCharacters );
            if ( special < 0 )
            {
                builder.Append( text );
                return;
            }

            builder.Append( text[..special] ).Append( Entity( text[special] ) );
            text = text[(special + 1)..];
        }
    }

    /// <summary>
    /// The first character <see cref="Replacement"/> replaces: any special character, or for an html_safe attribute
    /// value (<paramref name="quotesOnly"/>) only <c>"</c>, so the value cannot end the attribute.
    /// </summary>
    private static int IndexOfSpecial( ReadOnlySpan<char> text, bool quotesOnly ) =>
        quotesOnly ? text.IndexOf( '"' ) : text.IndexOfAny( SpecialCharacters );

    /// <summary>The entity a character <see cref="IndexOfSpecial"/> found is written as.</summary>
    private static string Replacement( char special, bool quotesOnly ) => quotesOnly ? QuoteEntity : Entities[special]!;

    private static string?[] CreateEntities( )
    {
        var entities = new string?['>' + 1];
        entities['&'] = "&amp;";
        entities['<'] = "&lt;";
        entities['>'] = "&gt;";
        entities['"'] = QuoteEntity;
        entities['\''] = "&#39;";
        return entities;
    }
}
