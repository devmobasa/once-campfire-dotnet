using System.Globalization;
using System.Text;
using Microsoft.Net.Http.Headers;

namespace Campfire.Web.Http;

/// <summary><c>ActionController::DataStreaming</c> (rails <c>1a02651</c>) and <c>ActionDispatch::Http::ContentDisposition</c>.</summary>
public sealed partial class RequestContext
{
    /// <summary>
    /// <c>send_file path, type:, disposition:, filename:</c>. Throws <see cref="FileNotFoundException"/>
    /// (Rails' <c>MissingFile</c>, a 500) when the file can't be read.
    /// </summary>
    public FrontendResult SendFile( string path, SendOptions? options = null )
    {
        ArgumentNullException.ThrowIfNull( path );

        var file = new FileInfo( path );
        if ( !file.Exists )
        {
            throw new FileNotFoundException( $"Cannot read file {path}", path );
        }

        options ??= new SendOptions( );
        options = options.Filename is null ? options with
        {
            Filename = file.Name
        } : options;

        return Send( options, file.Length, ( offset, length ) => ResponseBody.FromFile( file.FullName, offset, length ) );
    }

    /// <summary>
    /// <c>send_data data, type:, disposition:, filename:</c>. It ends in <c>render body:</c>, so the
    /// <c>Vary: Accept</c> rule applies (unlike <c>send_file</c>).
    /// </summary>
    public FrontendResult SendData( ReadOnlyMemory<byte> data, SendOptions? options = null )
    {
        var response = Send(
            options ?? new SendOptions( ),
            data.Length,
            ( offset, length ) => ResponseBody.FromBytes( data.Slice( ( int ) offset, ( int ) length ) ) );

        return SetVaryHeader( response );
    }

    /// <summary>
    /// <c>ActionDispatch::Http::ContentDisposition.format</c>: an ASCII <c>filename</c> (transliterated,
    /// then escaped) and a UTF-8 <c>filename*</c>.
    /// </summary>
    public static string ContentDisposition( string disposition, string? filename )
    {
        ArgumentNullException.ThrowIfNull( disposition );

        if ( filename is null )
        {
            return disposition;
        }

        var ascii = PercentEscape( Transliterate( filename ), static byteValue => byteValue == ' ' || IsUnescaped( byteValue, "!#$+.^_`|~-" ) );
        var utf8 = PercentEscape( filename, static byteValue => IsUnescaped( byteValue, "!#$&+.^_`|~-" ) );

        return $"{disposition}; filename=\"{ascii}\"; filename*=UTF-8''{utf8}";
    }

    private FrontendResult Send( SendOptions options, long total, Func<long, long, ResponseBody> slice )
    {
        var contentType = options.ContentType ?? ContentTypeFor( options.Filename );
        var response = FrontendResult.WithBody( options.Status, contentType, slice( 0, total ) );
        if ( options.Disposition is { } disposition )
        {
            response.Headers.ContentDisposition = ContentDisposition( disposition, options.Filename );
        }

        response.Headers["Content-Transfer-Encoding"] = "binary";
        if ( !options.Ranges )
        {
            return response;
        }

        response.Headers.ETag = ResponseHeaders.ETag;
        response.Headers.LastModified = ResponseHeaders.LastModified;
        if ( response.Body.IsDigestible )
        {
            RackETag.Apply( response );
        }

        RackConditionalGet.Apply( Request.Method, Request.Headers, response );
        if ( response.Status == StatusCodes.Status304NotModified )
        {
            return response;
        }

        response.Headers.AcceptRanges = "bytes";
        switch ( SingleByteRange.Parse( Request.Header( HeaderNames.Range ), total ) )
        {
            case SingleByteRange.Unsatisfiable:
                var unsatisfiable = new FrontendResult( StatusCodes.Status416RangeNotSatisfiable );
                unsatisfiable.Headers.ContentRange = $"bytes */{total}";
                return unsatisfiable;
            case SingleByteRange.Range range:
                response.Status = StatusCodes.Status206PartialContent;
                response.Headers.ContentRange = $"bytes {range.Start}-{range.End}/{total}";
                response.Body = slice( range.Start, range.End - range.Start + 1 );
                return response;
            default:
                return response;
        }
    }

    /// <summary>An ASCII letter or digit, or one of <paramref name="punctuation"/>.</summary>
    private static bool IsUnescaped( byte byteValue, string punctuation ) =>
        char.IsAsciiLetterOrDigit( ( char ) byteValue ) || punctuation.Contains( ( char ) byteValue, StringComparison.Ordinal );

    private static string ContentTypeFor( string? filename )
    {
        var extension = filename is null ? "" : Path.GetExtension( filename ).TrimStart( '.' ).ToLowerInvariant( );

        return extension.Length > 0 && Mime.LookupByExtension( extension ) is { } mime ? mime.Value : ContentTypes.Binary;
    }

    private static string PercentEscape( string text, Func<byte, bool> keep )
    {
        var output = new StringBuilder( text.Length );
        foreach ( var byteValue in Encoding.UTF8.GetBytes( text ) )
        {
            if ( keep( byteValue ) )
            {
                output.Append( ( char ) byteValue );
            }
            else
            {
                output.Append( '%' ).Append( byteValue.ToString( "X2", CultureInfo.InvariantCulture ) );
            }
        }

        return output.ToString( );
    }

    /// <summary><c>I18n.transliterate</c> with the default rules: Latin letters lose their accents, anything else non-ASCII becomes <c>?</c>.</summary>
    private static string Transliterate( string text )
    {
        var output = new StringBuilder( text.Length );
        foreach ( var rune in text.EnumerateRunes( ) )
        {
            output.Append( rune.IsAscii ? rune.ToString( ) : TransliterateRune( rune.Value ) );
        }

        return output.ToString( );
    }

    private static string TransliterateRune( int codePoint ) => codePoint switch
    {
        >= 'À' and <= 'Å' => "A",
        'Æ' => "AE",
        'Ç' => "C",
        >= 'È' and <= 'Ë' => "E",
        >= 'Ì' and <= 'Ï' => "I",
        'Ð' => "D",
        'Ñ' => "N",
        (>= 'Ò' and <= 'Ö') or 'Ø' => "O",
        >= 'Ù' and <= 'Ü' => "U",
        'Ý' => "Y",
        'Þ' => "Th",
        'ß' => "ss",
        >= 'à' and <= 'å' => "a",
        'æ' => "ae",
        'ç' => "c",
        >= 'è' and <= 'ë' => "e",
        >= 'ì' and <= 'ï' => "i",
        'ð' => "d",
        'ñ' => "n",
        (>= 'ò' and <= 'ö') or 'ø' => "o",
        >= 'ù' and <= 'ü' => "u",
        'ý' or 'ÿ' => "y",
        'þ' => "th",
        _ => "?",
    };
}
