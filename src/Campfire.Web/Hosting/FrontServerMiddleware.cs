using System.Globalization;
using System.Net;
using Campfire.Web.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace Campfire.Web.Hosting;

/// <summary>The public listener's redirects, HTTP-01 responses, body bounds and cache, without an upstream hop.</summary>
internal sealed partial class FrontServerMiddleware(
    RequestDelegate next,
    FrontServerOptions options,
    AutomaticCertificates certificates,
    FrontResponseCache cache,
    FrontRequestWatchdog watchdog,
    ILogger<FrontServerMiddleware> logger )
{
    // The X-Cache values Thruster sends.
    private const string CacheHit = "hit";
    private const string CacheMiss = "miss";
    private const string CacheBypass = "bypass";

    /// <summary>The forwarded headers only a trusted peer may send (the address chain is handled separately).</summary>
    private static readonly string[] ForwardedHeaders =
    [
        ForwardedHeaderNames.Host,
        ForwardedHeaderNames.Proto,
        ForwardedHeaderNames.Port,
        ForwardedHeaderNames.Forwarded,
    ];

    public async Task InvokeAsync( HttpContext httpContext )
    {
        if ( !options.Enabled || IsTargetListener( httpContext ) )
        {
            await next( httpContext );
            return;
        }

        using var deadline = new FrontRequestDeadline( httpContext, options, watchdog );
        try
        {
            await ServeAsync( httpContext );
        }
        finally
        {
            if ( options.LogRequests && logger.IsEnabled( LogLevel.Information ) )
            {
                // Request paths can include bot secrets and user input; status and method suffice here.
                RequestCompleted( logger, httpContext.Request.Method, httpContext.Response.StatusCode );
            }
        }
    }

    private async Task ServeAsync( HttpContext httpContext )
    {
        if ( options.HasTls && !httpContext.Request.IsHttps )
        {
            await HttpTlsAsync( httpContext );
            return;
        }

        PrepareForwarding( httpContext );
        await ServeApplicationAsync( httpContext );
    }

    /// <summary>The application's own listener, which the front forwards to and does not handle again.</summary>
    private bool IsTargetListener( HttpContext httpContext ) =>
        options.TargetPort != 0 && httpContext.Connection.LocalPort == options.TargetPort;

    private void PrepareForwarding( HttpContext httpContext )
    {
        var address = IPv4Mapping.Unmap( httpContext.Connection.RemoteIpAddress )?.ToString( );
        var trusted = address is not null && RemoteIp.IsTrusted( address, options.TrustedProxies );
        if ( !trusted )
        {
            foreach ( var header in ForwardedHeaders )
            {
                httpContext.Request.Headers.Remove( header );
            }
        }

        // Only explicitly configured transport peers may supply an address chain. Strip alternate
        // address sources; native forwarding uses the TCP peer as the trust anchor.
        httpContext.Request.Headers.Remove( ForwardedHeaderNames.ClientIp );
        httpContext.Request.Headers.Remove( ForwardedHeaderNames.Forwarded );
        // The native forwarded-header middleware uses the transport peer as its trust anchor.
        if ( !trusted )
        {
            httpContext.Request.Headers[ForwardedHeaderNames.For] = address;
        }

        var requestStart = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds( ).ToString( CultureInfo.InvariantCulture );
        httpContext.Request.Headers[CampfireHeaderNames.RequestStart] = "t=" + requestStart;
    }

    private async Task ServeApplicationAsync( HttpContext httpContext )
    {
        var eligible = cache.Eligible( httpContext.Request );
        httpContext.Response.OnStarting( static state =>
        {
            var (httpContext, usesCacheHeaders, cacheEligible) = ( (HttpContext, bool, bool) ) state;
            // Exception handling can clear prepared headers when replacing a failed response.
            // The public front still owns X-Cache on that replacement response.
            if ( !httpContext.Response.Headers.ContainsKey( CampfireHeaderNames.Cache ) )
            {
                httpContext.Response.Headers[CampfireHeaderNames.Cache] = cacheEligible ? CacheMiss : CacheBypass;
            }

            // Thruster's gzip wrapper initially adds Vary. Its cache response overwrites that value
            // when the app supplies Vary; bypassed requests append the app's value instead. Preserve
            // both the app's 304 Vary and the intentional duplicate on bypassed POST responses.
            // H1's 101 retains Thruster's outer Vary header. Only the accepted H2 CONNECT
            // response is a 200 tunnel whose HTTP encoding headers must be omitted.
            var extendedTunnel = HttpMethodChecks.IsConnect( httpContext.Request.Method ) && Compression.IsWebSocketAccepted( httpContext );
            if ( !extendedTunnel && (!usesCacheHeaders || httpContext.Response.Headers.Vary.Count == 0) )
            {
                httpContext.Response.Headers.Append( HeaderNames.Vary, HeaderNames.AcceptEncoding );
            }

            if ( httpContext.Response is { StatusCode: StatusCodes.Status501NotImplemented, ContentType: null } )
            {
                // Go's HTTP writer sniffed Puma's unsupported-method body as plain text.
                httpContext.Response.ContentType = ContentTypes.PlainTextUtf8;
            }

            return Task.CompletedTask;
        }, (httpContext, FrontResponseCache.UsesCacheHeaders( httpContext.Request ), eligible) );
        if ( httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>( ) is { IsReadOnly: false } bodyLimit )
        {
            bodyLimit.MaxRequestBodySize = options.MaximumRequestBytes == 0 ? null : options.MaximumRequestBytes;
        }

        if ( eligible && cache.Find( httpContext.Request ) is { } entry )
        {
            await HitAsync( httpContext, entry );
            return;
        }

        httpContext.Response.Headers[CampfireHeaderNames.Cache] = eligible ? CacheMiss : CacheBypass;
        if ( !eligible )
        {
            await next( httpContext );
            return;
        }

        var original = httpContext.Features.GetRequiredFeature<IHttpResponseBodyFeature>( );
        using var captured = new FrontCacheBody( original, httpContext, options.MaximumCacheItemBytes );
        httpContext.Features.Set<IHttpResponseBodyFeature>( captured );
        httpContext.Response.OnStarting( captured.DecideAsync );
        try
        {
            await next( httpContext );
            var bytes = await captured.CapturedAsync( httpContext.RequestAborted );
            if ( bytes is not null && captured.Lifetime is { } lifetime )
            {
                cache.Store( httpContext.Request, httpContext.Response, bytes, lifetime, captured.CreatedAt );
            }
        }
        finally
        {
            httpContext.Features.Set( original );
        }
    }

    private static async Task HitAsync( HttpContext httpContext, FrontResponseCache.Entry entry )
    {
        foreach ( var (name, value) in entry.Headers )
        {
            httpContext.Response.Headers[name] = value;
        }

        httpContext.Response.Headers[CampfireHeaderNames.Cache] = CacheHit;
        var residentSeconds = ( long ) Math.Max( 0, (DateTimeOffset.UtcNow - entry.Created).TotalSeconds );
        httpContext.Response.Headers.Age = (entry.InitialAgeSeconds + residentSeconds).ToString( CultureInfo.InvariantCulture );
        var notModified = entry.Status == StatusCodes.Status200OK
            && RackConditionalGet.IsFresh( httpContext.Request.Headers, httpContext.Response.Headers.ETag.ToString( ), httpContext.Response.Headers.LastModified.ToString( ) );
        httpContext.Response.StatusCode = notModified ? StatusCodes.Status304NotModified : entry.Status;
        if ( notModified )
        {
            httpContext.Response.Headers.ContentLength = null;
            httpContext.Response.ContentType = null;
            return;
        }

        if ( !HttpMethodChecks.IsHead( httpContext.Request.Method ) )
        {
            await httpContext.Response.Body.WriteAsync( entry.Body, httpContext.RequestAborted );
        }
    }

    [LoggerMessage( EventId = 1, Level = LogLevel.Information, Message = "HTTP {Method} completed with {Status}" )]
    private static partial void RequestCompleted( ILogger logger, string method, int status );

    private async Task HttpTlsAsync( HttpContext httpContext )
    {
        var host = HostNames.Normalize( httpContext.Request.Host.Host );
        var path = httpContext.Request.Path.Value ?? "/";
        if ( path.StartsWith( FrontServerOptions.ChallengePath, StringComparison.Ordinal ) )
        {
            // autocert's HTTP-01 handler: the token for a configured host, else 404; an unknown host is forbidden.
            var allowed = certificates.Allows( host );
            var token = allowed ? certificates.HttpToken( path ) : null;
            if ( token is not null )
            {
                httpContext.Response.StatusCode = StatusCodes.Status200OK;
            }
            else if ( allowed )
            {
                httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            }
            else
            {
                httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
            }

            httpContext.Response.ContentType = ContentTypes.PlainTextUtf8;
            if ( !HttpMethodChecks.IsHead( httpContext.Request.Method ) )
            {
                await httpContext.Response.WriteAsync( token ?? "acme/autocert: certificate cache miss\n", httpContext.RequestAborted );
            }

            return;
        }

        httpContext.Response.Headers.Connection = "close";
        if ( !certificates.Allows( host ) )
        {
            httpContext.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
            httpContext.Response.ContentType = ContentTypes.PlainTextUtf8;
            await httpContext.Response.WriteAsync( "Misdirected Request\n", httpContext.RequestAborted );
            return;
        }

        var url = "https://" + host + httpContext.Request.PathBase + httpContext.Request.Path + httpContext.Request.QueryString;
        httpContext.Response.StatusCode = StatusCodes.Status301MovedPermanently;
        httpContext.Response.Headers.Location = url;
        if ( HttpMethodChecks.IsGetOrHead( httpContext.Request.Method ) )
        {
            httpContext.Response.ContentType = ContentTypes.HtmlUtf8;
            if ( HttpMethodChecks.IsGet( httpContext.Request.Method ) )
            {
                // Go's http.Redirect body.
                var body = "<a href=\"" + WebUtility.HtmlEncode( url ) + "\">Moved Permanently</a>.\n\n";
                await httpContext.Response.WriteAsync( body, httpContext.RequestAborted );
            }
        }
    }
}
