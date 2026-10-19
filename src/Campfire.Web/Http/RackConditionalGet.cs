using Microsoft.Net.Http.Headers;

namespace Campfire.Web.Http;

/// <summary>
/// Converts successful GET and HEAD responses to empty 304 responses using shared HTTP validators.
/// </summary>
public static class RackConditionalGet
{
    public static void Apply( string method, IHeaderDictionary requestHeaders, FrontendResult response )
    {
        ArgumentNullException.ThrowIfNull( requestHeaders );
        ArgumentNullException.ThrowIfNull( response );

        if ( !HttpMethodChecks.IsGetOrHead( method ) || response.Status != StatusCodes.Status200OK
            || !IsFresh( requestHeaders, response.Header( HeaderNames.ETag ), response.Header( HeaderNames.LastModified ) ) )
        {
            return;
        }

        response.Status = StatusCodes.Status304NotModified;
        response.Headers.Remove( HeaderNames.ContentType );
        response.Headers.Remove( HeaderNames.ContentLength );
        response.Body = ResponseBody.Empty;
    }

    internal static bool IsFresh( IHeaderDictionary requestHeaders, string? etag, string? lastModified ) =>
        IsFresh( requestHeaders.TryGetValue( HeaderNames.IfNoneMatch, out var noneMatch ) ? noneMatch.ToString( ) : null,
            requestHeaders.IfModifiedSince.ToString( ), etag, lastModified );

    internal static bool IsFresh( string? ifNoneMatch, string? ifModifiedSince, string? etag, string? lastModified )
    {
        if ( ifNoneMatch is not null )
        {
            if ( ifNoneMatch.Trim( ) == "*" )
            {
                return true;
            }

            return EntityTagHeaderValue.TryParseStrictList( [ifNoneMatch], out var validators )
                && !validators.Any( validator => validator.Equals( EntityTagHeaderValue.Any ) )
                && EntityTagHeaderValue.TryParse( etag, out var current )
                && validators.Any( validator => validator.Compare( current, useStrongComparison: false ) );
        }

        return HeaderUtilities.TryParseDate( ifModifiedSince, out var since )
            && HeaderUtilities.TryParseDate( lastModified, out var modified ) && since >= modified;
    }
}
