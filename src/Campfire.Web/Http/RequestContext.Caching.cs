using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Net.Http.Headers;

namespace Campfire.Web.Http;

/// <summary><c>ActionController::ConditionalGet</c> (rails <c>1a02651</c>, <c>strict_freshness</c> on since 8.0).</summary>
public sealed partial class RequestContext
{
    /// <summary>
    /// <c>fresh_when</c>: sets <c>ETag</c> / <c>Last-Modified</c> and returns the 304 <c>head</c> when the
    /// request is fresh, else <c>null</c>.
    /// </summary>
    public FrontendResult? FreshWhen( Freshness freshness )
    {
        ArgumentNullException.ThrowIfNull( freshness );

        CacheControl.Remove( CacheControl.Keys.NoStore );
        if ( freshness.StrongEtag is { } strong )
        {
            ResponseHeaders.ETag = $"\"{CombineEtags( strong, freshness.TemplateDigest )}\"";
        }
        else if ( freshness.Etag is not null || freshness.TemplateDigest is not null )
        {
            ResponseHeaders.ETag = $"W/\"{CombineEtags( freshness.Etag ?? [], freshness.TemplateDigest )}\"";
        }

        if ( freshness.LastModified is { } lastModified )
        {
            ResponseHeaders.LastModified = HttpDate.Format( lastModified );
        }

        if ( freshness.Public )
        {
            CacheControl.Public = true;
        }

        return IsFresh( ) ? Head( StatusCodes.Status304NotModified ) : null;
    }

    /// <summary><c>stale?</c>: sets the validators, then the inverse of freshness.</summary>
    public bool Stale( Freshness freshness ) => FreshWhen( freshness ) is null;

    /// <summary><c>expires_in seconds, public:, must_revalidate:, stale_while_revalidate:, ...</c>.</summary>
    public void ExpiresIn( long seconds, ExpiresInOptions? options = null )
    {
        options ??= new ExpiresInOptions( );
        CacheControl.Remove( CacheControl.Keys.NoStore );
        CacheControl.MaxAge = seconds;
        CacheControl.Public = options.Public;
        CacheControl.MustRevalidate = options.MustRevalidate;
        CacheControl.StaleWhileRevalidate = options.StaleWhileRevalidate;
        CacheControl.StaleIfError = options.StaleIfError;
        CacheControl.Immutable = options.Immutable;
        CacheControl.Extras.Clear( );
        if ( !ResponseHeaders.ContainsKey( HeaderNames.Date ) )
        {
            ResponseHeaders.Date = HttpDate.Format( Now );
        }
    }

    /// <summary><c>expires_now</c>: <c>no-cache</c>.</summary>
    public void ExpiresNow( )
    {
        CacheControl.Clear( );
        CacheControl.NoCache = true;
    }

    /// <summary><c>no_store</c>.</summary>
    public void NoStore( )
    {
        CacheControl.Clear( );
        CacheControl.NoStore = true;
    }

    /// <summary>
    /// <c>combine_etags</c>: <c>[validator, *etaggers]</c> digested like
    /// <c>ActiveSupport::Digest.hexdigest(expand_cache_key(...))</c> (SHA-256, 32 hex digits). The
    /// etaggers are, in registration order, <c>EtagWithTemplateDigest</c>, <c>EtagWithFlash</c> (the
    /// flash as <c>key/value</c> pairs, when it isn't empty), turbo-rails' frame etagger and
    /// geared_pagination's page etagger.
    /// </summary>
    private string CombineEtags( IReadOnlyList<string> validators, string? templateDigest )
    {
        var parts = new List<string>( validators );
        if ( templateDigest is not null )
        {
            parts.Add( templateDigest );
        }

        // etag { flash if request.respond_to?(:flash) && !flash.empty? }, for any format.
        if ( !Flash.IsEmpty )
        {
            parts.Add( string.Join( "/", Flash.Entries.Select( entry => $"{entry.Key}/{FlashCacheKey( entry.Value )}" ) ) );
        }

        if ( IsTurboFrameRequest )
        {
            parts.Add( "frame" );
        }

        if ( Current<GearedPage>( ) is { } page )
        {
            parts.Add( page.CacheKey );
        }

        var digest = SHA256.HashData( Encoding.UTF8.GetBytes( string.Join( "/", parts ) ) );

        return Convert.ToHexStringLower( digest )[..32];
    }

    /// <summary><c>to_param</c> of a flash value.</summary>
    private static string FlashCacheKey( JsonNode? value ) => value switch
    {
        null => "",
        JsonValue text when text.TryGetValue<string>( out var stringValue ) => stringValue,
        _ => value.ToJsonString( ),
    };

    /// <summary><c>request.fresh?(response)</c> with <c>strict_freshness</c> (the 8.0 default).</summary>
    private bool IsFresh( ) => HttpMethodChecks.IsGetOrHead( Request.Method )
        && RackConditionalGet.IsFresh( Request.Headers, ResponseHeaders.ETag.ToString( ), ResponseHeaders.LastModified.ToString( ) );
}
