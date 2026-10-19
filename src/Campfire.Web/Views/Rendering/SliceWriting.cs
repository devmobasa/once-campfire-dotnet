using System.Buffers;
using System.Globalization;
using System.Text.Unicode;

namespace Campfire.Web.Views.Rendering;

/// <summary>The raw output shared by the three RazorSlices inheritance roots.</summary>
internal interface ISliceOutput
{
    void WriteUtf8( ReadOnlySpan<byte> value );
}

/// <summary>Safe Rails escaping and formatting without an HtmlEncoder subclass.</summary>
internal static class SliceWriting
{
    private static readonly SearchValues<byte> SpecialBytes = SearchValues.Create( "&<>\"'"u8 );
    private const int ThreadBufferSize = 4096;
    private const int MaximumChunkLength = ThreadBufferSize / 6;

    // Rendering writes this buffer synchronously before reusing it. The app's literal output
    // copies the written span immediately and does not re-enter a slice during that copy.
    [ThreadStatic]
    private static byte[]? _outputBuffer;

    internal static void WriteText( ISliceOutput output, ReadOnlySpan<char> value ) =>
        WriteText( output, value, value.IndexOfAny( Escaping.SpecialCharacters ) );

    internal static void WriteText( ISliceOutput output, ReadOnlySpan<char> value, int first )
    {
        var buffer = _outputBuffer ??= GC.AllocateUninitializedArray<byte>( ThreadBufferSize );
        while ( value.Length > MaximumChunkLength )
        {
            var count = MaximumChunkLength;
            if ( char.IsHighSurrogate( value[count - 1] ) && char.IsLowSurrogate( value[count] ) )
            {
                count--;
            }

            WriteTextChunk( output, value[..count], first >= 0 && first < count ? first : -1, buffer );
            value = value[count..];
            first = value.IndexOfAny( Escaping.SpecialCharacters );
        }

        WriteTextChunk( output, value, first, buffer );
    }

    private static void WriteTextChunk( ISliceOutput output, ReadOnlySpan<char> value, int first, byte[] buffer )
    {
        var position = 0;
        while ( first >= 0 )
        {
            position += Transcode( value[..first], buffer.AsSpan( position ) );
            var entity = EntityUtf8( value[first] );
            entity.CopyTo( buffer.AsSpan( position ) );
            position += entity.Length;
            value = value[(first + 1)..];
            if ( value.Length < 16 )
            {
                position += WriteShortText( value, buffer.AsSpan( position ) );
                value = default;
                break;
            }

            first = value.IndexOfAny( Escaping.SpecialCharacters );
        }

        position += Transcode( value, buffer.AsSpan( position ) );
        output.WriteUtf8( buffer.AsSpan( 0, position ) );
    }

    private static int Transcode( ReadOnlySpan<char> value, Span<byte> destination )
    {
        if ( value.IsEmpty )
        {
            return 0;
        }

        var status = Utf8.FromUtf16( value, destination, out _, out var written, replaceInvalidSequences: true );
        if ( status != OperationStatus.Done )
        {
            throw new InvalidOperationException( "The Rails escape buffer is too small." );
        }

        return written;
    }

    private static ReadOnlySpan<byte> EntityUtf8( char special ) => special switch
    {
        '&' => "&amp;"u8,
        '<' => "&lt;"u8,
        '>' => "&gt;"u8,
        '"' => "&quot;"u8,
        '\'' => "&#39;"u8,
        _ => throw new ArgumentOutOfRangeException( nameof( special ) )
    };

    private static int WriteShortText( ReadOnlySpan<char> value, Span<byte> destination )
    {
        var position = 0;
        for ( var index = 0; index < value.Length; index++ )
        {
            if ( value[index] is '&' or '<' or '>' or '"' or '\'' )
            {
                var entity = EntityUtf8( value[index] );
                entity.CopyTo( destination[position..] );
                position += entity.Length;
            }
            else if ( value[index] < 0x80 )
            {
                destination[position++] = ( byte ) value[index];
            }
            else
            {
                var count = char.IsHighSurrogate( value[index] ) && index + 1 < value.Length && char.IsLowSurrogate( value[index + 1] ) ? 2 : 1;
                position += Transcode( value.Slice( index, count ), destination[position..] );
                index += count - 1;
            }
        }

        return position;
    }

    private static int WriteShortUtf8( ReadOnlySpan<byte> value, Span<byte> destination )
    {
        var position = 0;
        for ( var index = 0; index < value.Length; index++ )
        {
            if ( value[index] is ( byte ) '&' or ( byte ) '<' or ( byte ) '>' or ( byte ) '"' or ( byte ) '\'' )
            {
                var entity = EntityUtf8( ( char ) value[index] );
                entity.CopyTo( destination[position..] );
                position += entity.Length;
            }
            else
            {
                destination[position++] = value[index];
            }
        }

        return position;
    }

    internal static void WriteNullable<T>( ISliceOutput output, T? value )
        where T : struct, IUtf8SpanFormattable
    {
        if ( value.HasValue )
        {
            WriteValue( output, value.Value );
        }
    }

    internal static void WriteValue<T>( ISliceOutput output, T value )
        where T : IUtf8SpanFormattable
    {
        Span<byte> small = stackalloc byte[128];
        if ( value.TryFormat( small, out var written, default, CultureInfo.CurrentCulture ) )
        {
            WriteEscapedUtf8( output, small[..written] );
            return;
        }

        var size = 256;
        while ( true )
        {
            var rented = ArrayPool<byte>.Shared.Rent( size );
            try
            {
                if ( value.TryFormat( rented, out written, default, CultureInfo.CurrentCulture ) )
                {
                    WriteEscapedUtf8( output, rented.AsSpan( 0, written ) );
                    return;
                }

                size = checked(rented.Length * 2);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return( rented );
            }
        }
    }

    private static void WriteEscapedUtf8( ISliceOutput output, ReadOnlySpan<byte> value )
    {
        var first = value.IndexOfAny( SpecialBytes );
        if ( first < 0 )
        {
            output.WriteUtf8( value );
            return;
        }

        var buffer = _outputBuffer ??= GC.AllocateUninitializedArray<byte>( ThreadBufferSize );
        while ( value.Length > MaximumChunkLength )
        {
            var count = MaximumChunkLength;
            while ( (value[count] & 0xC0) == 0x80 )
            {
                count--;
            }

            WriteUtf8Chunk( output, value[..count], first >= 0 && first < count ? first : -1, buffer );
            value = value[count..];
            first = value.IndexOfAny( SpecialBytes );
        }

        WriteUtf8Chunk( output, value, first, buffer );
    }

    private static void WriteUtf8Chunk( ISliceOutput output, ReadOnlySpan<byte> value, int first, byte[] buffer )
    {
        var position = 0;
        while ( first >= 0 )
        {
            value[..first].CopyTo( buffer.AsSpan( position ) );
            position += first;
            var entity = EntityUtf8( ( char ) value[first] );
            entity.CopyTo( buffer.AsSpan( position ) );
            position += entity.Length;
            value = value[(first + 1)..];
            if ( value.Length < 16 )
            {
                position += WriteShortUtf8( value, buffer.AsSpan( position ) );
                value = default;
                break;
            }

            first = value.IndexOfAny( SpecialBytes );
        }

        value.CopyTo( buffer.AsSpan( position ) );
        output.WriteUtf8( buffer.AsSpan( 0, position + value.Length ) );
    }

    internal static void WriteSpanValue( ISliceOutput output, ISpanFormattable value )
    {
        Span<char> buffer = stackalloc char[128];
        if ( value.TryFormat( buffer, out var written, default, CultureInfo.CurrentCulture ) )
        {
            WriteText( output, buffer[..written] );
            return;
        }

        var size = 256;
        while ( true )
        {
            var rented = ArrayPool<char>.Shared.Rent( size );
            try
            {
                if ( value.TryFormat( rented, out written, default, CultureInfo.CurrentCulture ) )
                {
                    WriteText( output, rented.AsSpan( 0, written ) );
                    return;
                }

                size = checked(rented.Length * 2);
            }
            finally
            {
                ArrayPool<char>.Shared.Return( rented );
            }
        }
    }

}
