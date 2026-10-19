using System.Globalization;
using Campfire.Web.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Campfire.Web.Hosting;

/// <summary>A byte-bounded, expiring public-response cache; request host and response Vary partition its entries.</summary>
internal sealed class FrontResponseCache( FrontServerOptions options )
{
    private const int MaximumUriLength = 2048;
    private const int MaximumVariantsPerUrl = 64;
    private const int EntryBookkeepingBytes = 384;
    private const int HeaderBookkeepingBytes = 64;
    private static readonly string[] StoredEncodings = [ContentCodings.Brotli, ContentCodings.Gzip, ContentCodings.Identity];
    private static readonly string[] IdentityEncoding = [ContentCodings.Identity];

    /// <summary>Hop-by-hop headers, which belong to one connection and are never replayed.</summary>
    private static readonly string[] TransportHeaders =
    [
        HeaderNames.Connection,
        HeaderNames.TransferEncoding,
        HeaderNames.KeepAlive,
        HeaderNames.TE,
        HeaderNames.Trailer,
        HeaderNames.Upgrade,
        HeaderNames.ProxyAuthenticate,
        HeaderNames.ProxyAuthorization,
    ];

    private readonly Lock _gate = new( );
    private readonly Dictionary<string, VariantSet> _variants = new( StringComparer.Ordinal );
    private readonly LinkedList<Entry> _lru = new( );
    private long _bytes;

    internal sealed record Entry(
        string Key,
        string VariantKey,
        int Status,
        Dictionary<string, StringValues> Headers,
        byte[] Body,
        DateTimeOffset Created,
        long InitialAgeSeconds,
        DateTimeOffset Expires,
        long Size )
    {
        internal LinkedListNode<string>? Recency { get; set; }
    }

    private sealed class VariantSet( string[] names )
    {
        internal string[] Names { get; } = names;
        internal Dictionary<string, LinkedListNode<Entry>> Entries { get; } = new( StringComparer.Ordinal );
        internal LinkedList<string> Recency { get; } = new( );
    }

    public bool Eligible( HttpRequest request ) =>
        options is { CacheBytes: > 0, MaximumCacheItemBytes: > 0 }
        && UsesCacheHeaders( request )
        && (request.Path.Value?.Length ?? 0) + (request.QueryString.Value?.Length ?? 0) <= MaximumUriLength;

    // The former cache handler's header-copy behavior does not depend on retention capacity or URI length.
    internal static bool UsesCacheHeaders( HttpRequest request ) =>
        HttpMethodChecks.IsGetOrHead( request.Method )
        && !request.Headers.ContainsKey( HeaderNames.Range )
        && !request.Headers.ContainsKey( HeaderNames.Upgrade );

    /// <summary>
    /// How long a response may be reused: a 2xx or 3xx (not 304) that doesn't vary on everything, marked
    /// <c>public</c> and not <c>private</c>, <c>no-cache</c> or <c>no-store</c>, for its <c>s-maxage</c> or <c>max-age</c>.
    /// </summary>
    public static TimeSpan? Lifetime( HttpResponse response )
    {
        if ( !IsCacheableStatus( response.StatusCode )
            || VariesOnEverything( response )
            || !CacheControlHeaderValue.TryParse( response.Headers.CacheControl.ToString( ), out var cache )
            || !cache.Public
            || cache.Private
            || cache.NoCache
            || cache.NoStore )
        {
            return null;
        }

        var age = cache.SharedMaxAge ?? cache.MaxAge;
        return age > TimeSpan.Zero ? age : null;
    }

    public Entry? Find( HttpRequest request )
    {
        var key = Key( request );
        VariantSet? variants;
        lock ( _gate )
        {
            if ( !_variants.TryGetValue( key, out variants ) )
            {
                return null;
            }
        }

        // Header work happens outside the shared LRU lock. The schema is immutable and is rechecked
        // below in case a response changed Vary while this request computed its variant key.
        var accepted = AcceptEncoding.Parse( request.Headers.AcceptEncoding.ToString( ) );
        var encoding = AcceptEncoding.SelectBest( StoredEncodings, accepted );
        if ( encoding is null )
        {
            return null;
        }

        var variantKey = VariantKey( request, variants.Names, encoding );
        // Browsers advertise Brotli even when only a gzip representation exists. Keep the
        // application's negotiated representation as a bounded fallback, without accepting
        // an encoding the client disabled or scanning every stored Vary variant.
        var fallbackEncoding = encoding == ContentCodings.Brotli ? Compression.NegotiatedEncoding( request ) : null;
        var fallbackKey = fallbackEncoding is not null && fallbackEncoding != encoding
            ? VariantKey( request, variants.Names, fallbackEncoding ) : null;
        lock ( _gate )
        {
            if ( !_variants.TryGetValue( key, out var current ) || !ReferenceEquals( current, variants ) )
            {
                return null;
            }

            var node = FindFresh( variants, variantKey );
            if ( node is null && fallbackKey is not null )
            {
                node = FindFresh( variants, fallbackKey );
            }

            if ( node is not null )
            {
                return Touch( variants, node );
            }
        }

        // Images can be stored as identity even when the request prefers gzip. Negotiate
        // this fallback only after a miss, outside the LRU lock, so compressed hits pay no
        // extra header sorting or key allocation. The non-encoding Vary values are unchanged.
        if ( encoding == ContentCodings.Identity || fallbackEncoding == ContentCodings.Identity
            || AcceptEncoding.SelectBest( IdentityEncoding, accepted ) is null )
        {
            return null;
        }

        var identityKey = string.Concat( ContentCodings.Identity, variantKey.AsSpan( encoding.Length ) );
        lock ( _gate )
        {
            if ( !_variants.TryGetValue( key, out var current ) || !ReferenceEquals( current, variants ) )
            {
                return null;
            }

            var node = FindFresh( variants, identityKey );
            return node is null ? null : Touch( variants, node );
        }
    }

    // The caller holds _gate for both the lookup and its recency updates.
    private Entry Touch( VariantSet variants, LinkedListNode<Entry> node )
    {
        _lru.Remove( node );
        _lru.AddFirst( node );
        variants.Recency.Remove( node.Value.Recency! );
        variants.Recency.AddFirst( node.Value.Recency! );
        return node.Value;
    }

    private LinkedListNode<Entry>? FindFresh( VariantSet variants, string variantKey )
    {
        if ( !variants.Entries.TryGetValue( variantKey, out var node ) )
        {
            return null;
        }

        if ( node.Value.Expires <= DateTimeOffset.UtcNow )
        {
            Remove( node );
            return null;
        }

        return node;
    }

    public void Store( HttpRequest request, HttpResponse response, byte[] body, TimeSpan lifetime, DateTimeOffset? createdAt = null )
    {
        var key = Key( request );
        var headers = response.Headers.ToDictionary( pair => pair.Key, pair => new StringValues( pair.Value.ToArray( ) ), StringComparer.OrdinalIgnoreCase );
        headers.Remove( HeaderNames.SetCookie );
        headers.Remove( HeaderNames.Date ); // Each connection gets Kestrel's current Date, including cache hits.
        headers.Remove( HeaderNames.Age ); // Hits calculate age from the original response, never from its last reuse.
        // Kestrel's chunk framing belongs to this connection. Replaying Transfer-Encoding would make
        // it treat a cached payload as already framed, leaving the client waiting for a chunk terminator.
        foreach ( var name in response.Headers.Connection.ToString( ).Split( ',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries ) )
        {
            headers.Remove( name );
        }

        foreach ( var name in TransportHeaders )
        {
            headers.Remove( name );
        }

        var names = response.Headers.Vary.ToString( ).Split( ',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries )
            .Select( name => name.ToUpperInvariant( ) ).Distinct( StringComparer.Ordinal ).Order( StringComparer.Ordinal ).ToArray( );
        var encoding = response.Headers.ContentEncoding.ToString( ).Trim( ).ToLowerInvariant( );
        encoding = encoding.Length == 0 ? ContentCodings.Identity : encoding;
        if ( !StoredEncodings.Contains( encoding, StringComparer.Ordinal ) )
        {
            return;
        }

        var variantKey = VariantKey( request, names, encoding );
        // Charge retained UTF-16 strings as well as payloads, plus conservative per-entry/header bookkeeping.
        var size = body.LongLength + 2L * key.Length + EntryBookkeepingBytes;
        size += headers.Sum( pair => 2L * pair.Key.Length + 2L * pair.Value.ToString( ).Length + HeaderBookkeepingBytes );
        size += 2L * variantKey.Length + names.Sum( name => 2L * name.Length + HeaderBookkeepingBytes );
        if ( size > options.CacheBytes || body.Length > options.MaximumCacheItemBytes )
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var created = createdAt ?? now;
        var initialAge = InitialAge( response.Headers.Age.ToString( ) );
        var remaining = initialAge >= lifetime.TotalSeconds ? TimeSpan.Zero : lifetime - TimeSpan.FromSeconds( initialAge );
        var expires = remaining > DateTimeOffset.MaxValue - created ? DateTimeOffset.MaxValue : created + remaining;
        var entry = new Entry( key, variantKey, response.StatusCode, headers, body, created, initialAge, expires, size );
        lock ( _gate )
        {
            if ( _variants.TryGetValue( key, out var previous ) )
            {
                if ( !previous.Names.SequenceEqual( names ) )
                {
                    foreach ( var node in previous.Entries.Values.ToArray( ) )
                    {
                        Remove( node );
                    }
                }
                else if ( previous.Entries.TryGetValue( variantKey, out var matching ) )
                {
                    Remove( matching );
                }
                else if ( previous.Entries.Count >= MaximumVariantsPerUrl && previous.Recency.Last is { } oldestVariant )
                {
                    Remove( previous.Entries[oldestVariant.Value] );
                }
            }

            if ( expires <= now )
            {
                return;
            }

            while ( _bytes > options.CacheBytes - size && _lru.Last is { } oldest )
            {
                Remove( oldest );
            }

            if ( !_variants.TryGetValue( key, out var variants ) )
            {
                variants = new VariantSet( names );
                _variants.Add( key, variants );
            }

            entry.Recency = variants.Recency.AddFirst( variantKey );
            variants.Entries.Add( variantKey, _lru.AddFirst( entry ) );
            _bytes += size;
        }
    }

    private static long InitialAge( string value )
    {
        var age = value.AsSpan( );
        var comma = age.IndexOf( ',' );
        if ( comma >= 0 )
        {
            age = age[..comma];
        }

        return long.TryParse( age.Trim( ), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds ) && seconds >= 0 ? seconds : 0;
    }

    private void Remove( LinkedListNode<Entry> node )
    {
        _lru.Remove( node );
        _bytes -= node.Value.Size;
        var variants = _variants[node.Value.Key];
        variants.Entries.Remove( node.Value.VariantKey );
        variants.Recency.Remove( node.Value.Recency! );
        if ( variants.Entries.Count == 0 )
        {
            _variants.Remove( node.Value.Key );
        }
    }

    private static bool IsCacheableStatus( int status ) =>
        status is >= StatusCodes.Status200OK and < StatusCodes.Status400BadRequest and not StatusCodes.Status304NotModified;

    private static bool VariesOnEverything( HttpResponse response ) =>
        response.Headers.Vary.ToString( ).Split( ',' ).Any( value => value.Trim( ) == "*" );

    private static string Key( HttpRequest request ) =>
        $"{request.Method}\n{request.Scheme}\n{request.Host.Value}\n{request.PathBase}{request.Path}{request.QueryString}";

    private static string VariantKey( HttpRequest request, string[] names, string encoding ) => encoding + ":" + string.Concat( names.Where(
        name => !name.Equals( HeaderNames.AcceptEncoding, StringComparison.OrdinalIgnoreCase ) ).Select( name =>
    {
        // Key the bytes' actual representation, including precompressed static files. A word
        // match in StaticFiles can select br even when the deflater would negotiate gzip.
        var value = request.Headers[name].ToString( );
        // Length framing prevents ambiguous concatenations when Vary names or values contain separators.
        return value.Length.ToString( CultureInfo.InvariantCulture ) + ":" + value;
    } ) );
}
