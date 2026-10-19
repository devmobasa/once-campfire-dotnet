using System.Globalization;
using System.Text;
using Campfire.Application.Storage.Http;
using Campfire.Web.Http;
using Microsoft.Net.Http.Headers;

namespace Campfire.Web.Assets;

/// <summary>
/// <c>ActionDispatch::Static</c> (actionpack <c>middleware/static.rb</c>) over <c>Rack::Files</c>
/// (rack 3.2.6), serving <c>public/</c> and the precompiled <c>public/assets</c> with the headers
/// from <c>config/environments/production.rb</c>. Ported from Rust <c>crates/assets/src/serve.rs</c>.
/// </summary>
public sealed class StaticFiles( AssetCatalog catalog )
{
    /// <summary>
    /// <c>config.public_file_server.headers</c>: production.rb assigns it twice, and the later
    /// assignment wins.
    /// </summary>
    public const string CacheControl = "public, max-age=2592000";

    /// <summary>The precompressed siblings <c>FileHandler#try_precompressed_files</c> looks for, in order.</summary>
    private static readonly (string Encoding, string Extension)[] PrecompressedSiblings =
    [
        (ContentCodings.Brotli, ".br"),
        (ContentCodings.Gzip, ".gz"),
    ];

    /// <summary>
    /// <c>FileHandler#attempt</c>: a response when a public file matches a GET or HEAD request,
    /// <c>null</c> to hand the request on to the app.
    /// </summary>
    public StaticResponse? Serve( StaticRequest request )
    {
        ArgumentNullException.ThrowIfNull( request );

        if ( !HttpMethodChecks.IsGetOrHead( request.Method ) )
        {
            return null;
        }

        var found = FindFile( request.RawPath, request.AcceptEncoding ?? string.Empty );

        return found is null ? null : ServeFile( request, found.Value.File, found.Value.ContentHeaders );
    }

    private (StaticFile File, List<KeyValuePair<string, string>> ContentHeaders)? FindFile( string rawPath, string acceptEncoding )
    {
        var path = CleanPath( rawPath );
        if ( path is null )
        {
            return null;
        }

        var extname = RubyPaths.Extname( path );
        var contentType = RackMime.Lookup( extname );

        var candidates = new List<(string Path, string ContentType)> { (path, contentType ?? ContentTypes.PlainText) };
        // Only paths without a resolvable extension also try .html and /index.html.
        if ( contentType is null && extname != ".html" )
        {
            candidates.Add( (path + ".html", ContentTypes.Html) );
            candidates.Add( (path + "/index.html", ContentTypes.Html) );
        }

        foreach ( var (candidate, type) in candidates )
        {
            if ( TryFiles( candidate, type, acceptEncoding ) is { } found )
            {
                return found;
            }
        }

        return null;
    }

    private (StaticFile File, List<KeyValuePair<string, string>> ContentHeaders)? TryFiles( string path, string contentType, string acceptEncoding )
    {
        var headers = new List<KeyValuePair<string, string>> { new( RackHeaderNames.ContentType, contentType ) };

        if ( !Compressible( contentType ) )
        {
            return catalog.FindFile( path ) is { } plain ? (plain, headers) : null;
        }

        foreach ( var (encoding, extension) in PrecompressedSiblings )
        {
            if ( catalog.FindFile( path + extension ) is { } precompressed )
            {
                SetHeader( headers, RackHeaderNames.Vary, RackHeaderNames.AcceptEncoding );
                if ( Accepts( acceptEncoding, encoding ) )
                {
                    headers.Add( new( RackHeaderNames.ContentEncoding, encoding ) );
                    return (precompressed, headers);
                }
            }
        }

        return catalog.FindFile( path ) is { } identity ? (identity, headers) : null;
    }

    /// <summary><c>Rack::Files#serving</c>, then <c>FileHandler#serve</c>'s <c>headers.update(content_headers)</c>.</summary>
    private static StaticResponse ServeFile( StaticRequest request, StaticFile file, List<KeyValuePair<string, string>> contentHeaders )
    {
        if ( RackConditionalGet.IsFresh( request.IfNoneMatch, request.IfModifiedSince, etag: null, file.LastModified ) )
        {
            return new StaticResponse( StatusCodes.Status304NotModified, [], ReadOnlyMemory<byte>.Empty );
        }

        var size = file.Content.Length;
        var contentType = contentHeaders[0].Value;
        var headers = new List<KeyValuePair<string, string>>
        {
            new( RackHeaderNames.LastModified, file.LastModified ),
            new( RackHeaderNames.ContentType, contentType ),
            new( HeaderNames.CacheControl, CacheControl ),
        };
        var status = StatusCodes.Status200OK;
        var body = file.Content;

        var ranges = ByteRanges.Parse( request.Range, size );
        if ( ranges is { Count: 0 } )
        {
            const string Message = "Byte range unsatisfiable\n";
            headers =
            [
                new( RackHeaderNames.ContentType, ContentTypes.PlainText ),
                new( RackHeaderNames.ContentLength, Message.Length.ToString( CultureInfo.InvariantCulture ) ),
                new( RackHeaderNames.Cascade, "pass" ),
                new( RackHeaderNames.ContentRange, $"bytes */{size}" ),
            ];
            status = StatusCodes.Status416RangeNotSatisfiable;
            body = Encoding.ASCII.GetBytes( Message );
        }
        else if ( ranges is { Count: 1 } )
        {
            var (start, end) = ranges[0];
            headers.Add( new( RackHeaderNames.ContentRange, $"bytes {start}-{end}/{size}" ) );
            status = StatusCodes.Status206PartialContent;
            body = file.Content.Slice( ( int ) start, ( int ) ranges[0].Length );
        }
        else if ( ranges is not null )
        {
            SetHeader( headers, RackHeaderNames.ContentType, $"multipart/byteranges; boundary={FileServer.MultipartBoundary}" );
            status = StatusCodes.Status206PartialContent;
            body = Multipart( file.Content, ranges, contentType );
        }

        if ( status != StatusCodes.Status416RangeNotSatisfiable )
        {
            headers.Add( new( RackHeaderNames.ContentLength, body.Length.ToString( CultureInfo.InvariantCulture ) ) );
        }

        // Static always overrides Rack's multipart content-type with the file's own.
        foreach ( var (name, value) in contentHeaders )
        {
            SetHeader( headers, name, value );
        }

        return new StaticResponse( status, headers, HttpMethodChecks.IsHead( request.Method ) ? ReadOnlyMemory<byte>.Empty : body );
    }

    /// <summary><c>Rack::Files::BaseIterator</c> for more than one range.</summary>
    private static byte[] Multipart( ReadOnlyMemory<byte> content, IReadOnlyList<ByteRange> ranges, string contentType )
    {
        using var body = new MemoryStream( );
        foreach ( var range in ranges )
        {
            var heading = $"\r\n--{FileServer.MultipartBoundary}\r\ncontent-type: {contentType}\r\n"
                + $"content-range: bytes {range.Start}-{range.End}/{content.Length}\r\n\r\n";
            body.Write( Encoding.UTF8.GetBytes( heading ) );
            body.Write( content.Span.Slice( ( int ) range.Start, ( int ) range.Length ) );
        }

        body.Write( Encoding.UTF8.GetBytes( $"\r\n--{FileServer.MultipartBoundary}--\r\n" ) );

        return body.ToArray( );
    }

    private static void SetHeader( List<KeyValuePair<string, string>> headers, string name, string value )
    {
        var index = headers.FindIndex( header => header.Key == name );
        if ( index >= 0 )
        {
            headers[index] = new( name, value );
        }
        else
        {
            headers.Add( new( name, value ) );
        }
    }

    /// <summary>
    /// <c>FileHandler#clean_path</c>: <c>chomp("/")</c>, percent-decode (<c>URI::RFC2396_Parser#unescape</c>),
    /// reject NUL, then <c>Rack::Utils.clean_path_info</c>. Works on bytes, as Rack does.
    /// </summary>
    private static string? CleanPath( string rawPath )
    {
        var chomped = rawPath.EndsWith( '/' ) ? rawPath[..^1] : rawPath;
        var bytes = Unescape( chomped );
        if ( bytes.Contains( ( byte ) 0 ) )
        {
            return null;
        }

        var parts = SplitRuby( bytes );
        var clean = new List<byte[]>( );
        foreach ( var part in parts )
        {
            if ( part.Length == 0 || part is [( byte ) '.'] )
            {
                continue;
            }

            if ( part is [( byte ) '.', ( byte ) '.'] )
            {
                if ( clean.Count > 0 )
                {
                    clean.RemoveAt( clean.Count - 1 );
                }
            }
            else
            {
                clean.Add( part );
            }
        }

        var joined = string.Join( "/", clean.Select( part => Encoding.Latin1.GetString( part ) ) );
        if ( parts.Count == 0 || parts[0].Length == 0 )
        {
            joined = "/" + joined;
        }

        // File names are UTF-8; a path that isn't can't name one.
        try
        {
            return new UTF8Encoding( false, true ).GetString( Encoding.Latin1.GetBytes( joined ) );
        }
        catch ( DecoderFallbackException )
        {
            return null;
        }
    }

    /// <summary><c>URI::RFC2396_Parser#unescape</c>: <c>%XX</c> become bytes; anything else is kept.</summary>
    private static byte[] Unescape( string path )
    {
        var input = Encoding.UTF8.GetBytes( path );
        var output = new List<byte>( input.Length );
        for ( var index = 0; index < input.Length; index++ )
        {
            if ( IsPercentEscape( input, index ) )
            {
                output.Add( ( byte ) ((Uri.FromHex( ( char ) input[index + 1] ) << 4) | Uri.FromHex( ( char ) input[index + 2] )) );
                index += 2;
                continue;
            }

            output.Add( input[index] );
        }

        return [.. output];
    }

    /// <summary>A <c>%</c> at <paramref name="index"/> followed by two hex digits.</summary>
    private static bool IsPercentEscape( byte[] input, int index ) =>
        input[index] == '%'
        && index + 2 < input.Length
        && Uri.IsHexDigit( ( char ) input[index + 1] )
        && Uri.IsHexDigit( ( char ) input[index + 2] );

    /// <summary><c>String#split("/")</c>: trailing empty fields are dropped.</summary>
    private static List<byte[]> SplitRuby( byte[] bytes )
    {
        var parts = new List<byte[]>( );
        var start = 0;
        for ( var index = 0; index <= bytes.Length; index++ )
        {
            if ( index == bytes.Length || bytes[index] == '/' )
            {
                parts.Add( bytes[start..index] );
                start = index + 1;
            }
        }

        while ( parts.Count > 0 && parts[^1].Length == 0 )
        {
            parts.RemoveAt( parts.Count - 1 );
        }

        return parts;
    }

    /// <summary>
    /// <c>FileHandler</c>'s <c>compressible_content_types</c>: <c>/\A(?:text\/|application\/javascript|image\/svg\+xml)/</c>.
    /// </summary>
    private static bool Compressible( string contentType ) =>
        contentType.StartsWith( "text/", StringComparison.Ordinal )
        || contentType.StartsWith( ContentTypes.ApplicationJavaScript, StringComparison.Ordinal )
        || contentType.StartsWith( ContentTypes.Svg, StringComparison.Ordinal );

    /// <summary>
    /// <c>accept_encoding.any? { |enc, _| /\b#{encoding}\b/i.match?(enc) }</c> over Rack's parsed
    /// <c>Accept-Encoding</c> (quality values are not consulted).
    /// </summary>
    private static bool Accepts( string acceptEncoding, string encoding )
    {
        foreach ( var part in acceptEncoding.Split( ',' ) )
        {
            var coding = part.Split( ';', 2 )[0].Trim( );
            if ( ContainsWord( coding, encoding ) )
            {
                return true;
            }
        }

        return false;
    }

    /// <summary><c>/\b#{word}\b/i.match?(text)</c>: <paramref name="word"/> with no word character on either side.</summary>
    private static bool ContainsWord( string text, string word )
    {
        var matchIndex = text.IndexOf( word, StringComparison.OrdinalIgnoreCase );
        while ( matchIndex >= 0 )
        {
            var matchEnd = matchIndex + word.Length;
            var startsWord = matchIndex == 0 || !IsWordChar( text[matchIndex - 1] );
            var endsWord = matchEnd == text.Length || !IsWordChar( text[matchEnd] );
            if ( startsWord && endsWord )
            {
                return true;
            }

            matchIndex = text.IndexOf( word, matchIndex + 1, StringComparison.OrdinalIgnoreCase );
        }

        return false;
    }

    private static bool IsWordChar( char character ) => char.IsAsciiLetterOrDigit( character ) || character == '_';
}
