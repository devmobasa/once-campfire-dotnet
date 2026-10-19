using System.Security.Authentication;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace Campfire.Web.Hosting;

/// <summary>
/// Kestrel as the application and optional public front server, where Puma 7.2.1 sits in the reference:
/// plain HTTP on <c>PORT</c> (every interface, as Puma binds <c>0.0.0.0</c>), no <c>Server</c> header,
/// and limits aligned with Puma's where Kestrel's defaults are stricter:
/// <list type="table">
/// <item>
/// <term>Request body</term>
/// <description>Puma: none (Thruster applies <c>MAX_REQUEST_BODY</c>). Kestrel: 30 MB by default → unlimited.</description>
/// </item>
/// <item>
/// <term>Request line</term>
/// <description>Puma: 12 KiB request URI (<c>puma_http11</c>). Kestrel: 8 KiB → 12 KiB plus method and version.</description>
/// </item>
/// <item>
/// <term>Headers</term>
/// <description>
/// Puma: 112 KiB in total (<c>MAX_HEADER</c>), no count limit. Kestrel: 32 KiB and 100 headers → 112 KiB and 10,000.
/// </description>
/// </item>
/// <item>
/// <term>Body data rate</term>
/// <description>Puma: no minimum. Kestrel: 240 B/s after 5 s → none (Thruster times out slow clients).</description>
/// </item>
/// <item>
/// <term>Response data rate</term>
/// <description>Puma: a 10 s write timeout per write. Kestrel: 240 B/s → none; recorded difference.</description>
/// </item>
/// <item>
/// <term>Keep-alive</term>
/// <description>
/// Direct mode: Puma's 65 s <c>persistent_timeout</c>. Front mode: all listeners use <c>HTTP_IDLE_TIMEOUT</c> (60 s by default).
/// </description>
/// </item>
/// <item>
/// <term>First bytes</term>
/// <description>Puma: 30 s <c>first_data_timeout</c>. Kestrel: 30 s <c>RequestHeadersTimeout</c>, unchanged.</description>
/// </item>
/// </list>
/// Kestrel writes <c>Date</c> from the real clock. The registered
/// <see cref="TimeProvider"/> doesn't affect it.
/// </summary>
public static class CampfireKestrel
{
    public const long MaxRequestLineBytes = (12 * 1024) + 64;
    public const int MaxRequestHeadersBytes = 112 * 1024;
    public const int MaxRequestHeaderCount = 10_000;
    public static readonly TimeSpan KeepAliveTimeout = TimeSpan.FromSeconds( 65 );

    public static void Configure( KestrelServerOptions kestrel, CampfireOptions options, FrontServerOptions? front = null )
    {
        ArgumentNullException.ThrowIfNull( kestrel );
        ArgumentNullException.ThrowIfNull( options );

        kestrel.AddServerHeader = false;
        kestrel.Limits.MaxRequestBodySize = null;
        kestrel.Limits.MaxRequestLineSize = ( int ) MaxRequestLineBytes;
        kestrel.Limits.MaxRequestHeadersTotalSize = MaxRequestHeadersBytes;
        kestrel.Limits.MaxRequestHeaderCount = MaxRequestHeaderCount;
        kestrel.Limits.MinRequestBodyDataRate = null;
        kestrel.Limits.MinResponseDataRate = null;
        kestrel.Limits.KeepAliveTimeout = KeepAliveTimeout;
        if ( front is not { Enabled: true } )
        {
            kestrel.ListenAnyIP( options.Port );
            return;
        }

        kestrel.Limits.KeepAliveTimeout = front.IdleTimeout == TimeSpan.Zero ? TimeSpan.MaxValue : front.IdleTimeout;
        kestrel.Limits.RequestHeadersTimeout = front.ReadTimeout == TimeSpan.Zero ? TimeSpan.MaxValue : front.ReadTimeout;
        kestrel.ListenAnyIP( front.TargetPort );
        // Kestrel requires a dedicated HTTP/2 listener for unencrypted prior-knowledge clients.
        // HTTP/1 clients can still use the target listener; TLS negotiates both on the public HTTPS port.
        var httpProtocols = front is { H2CEnabled: true, HasTls: false } ? HttpProtocols.Http2 : HttpProtocols.Http1;
        kestrel.ListenAnyIP( front.HttpPort, listener => listener.Protocols = httpProtocols );
        if ( front.HasTls )
        {
            kestrel.ListenAnyIP( front.HttpsPort, listener =>
            {
                listener.Protocols = HttpProtocols.Http1AndHttp2;
                listener.Use( next => async connection =>
                {
                    try
                    {
                        await FrontTlsHello.InspectAsync( connection, front.ReadTimeout );
                        await next( connection );
                    }
                    catch ( Exception error ) when ( error is AuthenticationException or OperationCanceledException or IOException )
                    {
                        // Malformed, disconnected and stalled peers are routine on a public TLS port.
                        // Unexpected programming/configuration failures still reach Kestrel's logger.
                        var logger = kestrel.ApplicationServices.GetRequiredService<ILogger<FrontTlsHello>>( );
                        if ( logger.IsEnabled( LogLevel.Debug ) )
                        {
                            logger.LogDebug( "TLS peer closed or rejected: {Reason}", error.Message );
                        }

                        connection.Abort( );
                    }
                } );
                listener.UseHttps( new TlsHandshakeCallbackOptions
                {
                    HandshakeTimeout = TimeSpan.FromMinutes( 5 ),
                    OnConnection = context => kestrel.ApplicationServices.GetRequiredService<AutomaticCertificates>( ).SelectAsync( context ),
                } );
            } );
        }
    }
}
