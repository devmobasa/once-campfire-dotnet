using System.Text.Encodings.Web;
using Campfire.Web.Http;
using Campfire.Web.Views.Models;
using RazorSlices;

namespace Campfire.Web.Views.Rendering;

/// <summary>
/// How the rest of the app renders a slice: Rails write members with <see cref="HtmlEncoder.Default"/> as a fallback. Pages render once into owned
/// UTF-8 memory for the response (<see cref="RenderPageAsync"/>); fragments and broadcasts go to a string or UTF-8
/// bytes.
/// </summary>
public static class SliceRenderer
{
    /// <summary>Renders <paramref name="slice"/> to a string.</summary>
    public static ValueTask<string> RenderToStringAsync( RazorSlice slice, CancellationToken cancellationToken = default )
    {
        ArgumentNullException.ThrowIfNull( slice );

        return slice.RenderAsync( HtmlEncoder.Default, cancellationToken );
    }

    /// <summary>Renders <paramref name="slice"/> as trusted markup, for caching or embedding in another render.</summary>
    public static async ValueTask<SafeHtml> RenderToHtmlAsync( RazorSlice slice, CancellationToken cancellationToken = default ) =>
        SafeHtml.Raw( await RenderToStringAsync( slice, cancellationToken ) );

    /// <summary>Renders <paramref name="slice"/> to UTF-8 bytes (a response body whose ETag is computed first, or a cable frame).</summary>
    public static async ValueTask<byte[]> RenderToUtf8Async( RazorSlice slice, CancellationToken cancellationToken = default )
    {
        using var body = await RenderPageAsync( slice, cancellationToken, computeDigest: false, recordFragments: false );
        return body.Bytes.ToArray( );
    }

    /// <summary>
    /// Renders once into owned UTF-8 memory. Recorded pages reuse fragment digests in their content validator.
    /// </summary>
    internal static async ValueTask<PooledBytesBody> RenderPageAsync(
        RazorSlice slice,
        CancellationToken cancellationToken,
        bool computeDigest = true,
        bool recordFragments = true )
    {
        ArgumentNullException.ThrowIfNull( slice );

        using var buffer = new Utf8PageBuffer( PageCapacity( slice ), computeDigest );
        var context = recordFragments ? (slice as IRecordedSlice)?.RecordingContext : null;
        var previous = context?.Recorder;
        var recorder = context is null ? null : new PageRecorder( buffer );
        if ( context is not null )
        {
            context.Recorder = recorder;
        }

        try
        {
            await slice.RenderAsync( buffer, HtmlEncoder.Default, cancellationToken );
            var body = buffer.TakeBody( recorder?.Take( buffer.Length ) );
            body.ValidUntil = recorder?.ValidUntil ?? DateTimeOffset.MaxValue;
            return body;
        }
        finally
        {
            if ( context is not null )
            {
                context.Recorder = previous;
            }
        }
    }

    private static int PageCapacity( RazorSlice slice )
    {
        // Cached byte lengths are already known before rendering. Large room/message pages can
        // start near their final size; small pages retain the small rental, and hints stay bounded.
        const int maximumHint = 1024 * 1024;
        var expected = slice switch
        {
            RazorSlice<RoomsShowPage> room => 64 * 1024 + MessageBytes( room.Model.Messages ),
            RazorSlice<MessagesIndexPage> messages => 8 * 1024 + MessageBytes( messages.Model.Messages ),
            RazorSlice<RoomsRefreshTurboStreamPage> refresh =>
                8 * 1024 + MessageBytes( refresh.Model.NewMessages ) + MessageBytes( refresh.Model.UpdatedMessages ),
            _ => 16 * 1024,
        };
        return ( int ) Math.Min( expected, maximumHint );
    }

    /// <summary>The messages' cached fragment lengths, counting each message still to render as 16 KB.</summary>
    private static long MessageBytes( IReadOnlyList<MessageItem> messages )
    {
        var bytes = 0L;
        for ( var index = 0; index < messages.Count; index++ )
        {
            bytes += messages[index].Fragment?.Bytes.Length ?? 16L * 1024;
        }

        return bytes;
    }
}
