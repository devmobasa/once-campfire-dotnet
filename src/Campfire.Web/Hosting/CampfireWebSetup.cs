using Campfire.Application.Security;
using Campfire.Web.App;
using Campfire.Web.Cable;
using Campfire.Web.Http;
using Campfire.Web.Policy;
using Campfire.Web.Security;
using Campfire.Web.Views.Caching;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Json;

namespace Campfire.Web.Hosting;

/// <summary>The standard ASP.NET platform and the reviewed Kestrel front listener.</summary>
public static class CampfireWebSetup
{
    // Central browser forgery validation below owns unsafe methods, including login and form method overrides.
    public const string DisableCsrfProtectionKey = "DisableCsrfProtection";

    /// <summary>The platform setting that turns on its environment-driven forwarded-headers startup filter.</summary>
    private const string ForwardedHeadersEnabledKey = "ForwardedHeaders_Enabled";

    public static WebApplicationBuilder AddCampfireWeb( this WebApplicationBuilder builder )
    {
        // Only our explicit proxy boundary may consume forwarded metadata. Disable the platform's
        // environment-driven startup filter, which otherwise runs before that boundary.
        builder.WebHost.UseSetting( ForwardedHeadersEnabledKey, "false" );
        builder.Configuration.AddInMemoryCollection( [new( DisableCsrfProtectionKey, "true" ), new( ForwardedHeadersEnabledKey, "false" )] );
        var options = CampfireOptions.FromConfiguration( builder.Configuration );
        var front = FrontServerOptions.FromConfiguration( builder.Configuration );
        builder.Logging.SetMinimumLevel( LogLevelFor( options.LogLevel ) );
        builder.WebHost.ConfigureKestrel( kestrel => CampfireKestrel.Configure( kestrel, options, front ) );
        var services = builder.Services;
        services.AddSingleton( options );
        services.AddSingleton( front );
        services.AddSingleton<FrontResponseCache>( );
        services.AddSingleton<FrontRequestWatchdog>( );
        if ( front.Enabled )
        {
            services.AddHostedService( provider => provider.GetRequiredService<FrontRequestWatchdog>( ) );
        }

        services.AddSingleton( provider =>
            new AutomaticCertificates( front, provider.GetRequiredService<ILogger<AutomaticCertificates>>( ), TimeProvider.System ) );
        if ( front is { Enabled: true, HasTls: true } )
        {
            services.AddHostedService( provider => provider.GetRequiredService<AutomaticCertificates>( ) );
        }

        services.AddSingleton<TimeProvider>( CampfireClock.Create( options, TimeProvider.System ) );
        services.AddSingleton( new CampfireVersion( options.AppVersion, options.GitRevision ) );
        services.AddSingleton( CreateHttpOptions( options, builder.Environment.ContentRootPath ) );
        services.AddSingleton<RequestContextServices>( );
        services.AddSingleton( new RailsAvatarTokens( builder.Configuration[RailsAvatarTokens.SecretVariable] ) );
        services.AddBrowserAuthentication( StoragePaths.From( options, builder.Environment.ContentRootPath ).Root );
        services.AddSingleton<ITokenProtector, ProtectedTokens>( );
        services.AddSignInRateLimit( );
        services.AddProxyTrust( front );
        services.AddCampfireCompression( );
        services.Configure<FormOptions>( FormAndMethodOverride.Configure );
        services.Configure<JsonOptions>( json => json.SerializerOptions.TypeInfoResolverChain.Insert( 0, FrontendJsonContext.Default ) );
        services.AddRouting( );
        return builder;
    }

    /// <summary>
    /// The middleware pipeline, in Rails' order. <paramref name="staticFiles"/> installs <c>ActionDispatch::Static</c>
    /// where Rails has it, after SSL and before the request id: the app passes
    /// <c>pipeline =&gt; pipeline.UseCampfireAssets( )</c>; a host that serves no files passes a callback that adds nothing.
    /// </summary>
    public static WebApplication UseCampfireWeb( this WebApplication app, Action<IApplicationBuilder> staticFiles )
    {
        ArgumentNullException.ThrowIfNull( staticFiles );

        app.UseWebSockets( new WebSocketOptions { KeepAliveInterval = TimeSpan.Zero } );
        app.UseMiddleware<CableTransportMiddleware>( );
        app.UseMiddleware<ProxyTrustBoundary>( );
        app.UseForwardedHeaders( );
        app.UseMiddleware<HealthCheckMiddleware>( );
        app.UseMiddleware<FrontServerMiddleware>( );
        app.UseMiddleware<DefaultResponseHeadersMiddleware>( );
        app.UseExceptionHandler( new ExceptionHandlerOptions
        {
            ExceptionHandler = ErrorResults.HandleExceptionAsync,
            AllowStatusCode404Response = true,
            // ErrorResults logs unexpected failures once and renders expected client failures without stacks.
            SuppressDiagnosticsCallback = _ => true
        } );
        app.UseStatusCodePages( ErrorResults.WriteStatusPageAsync );
        EncodedCacheBody.UseCompression( app );
        app.UseMiddleware<WebSocketCompressionBoundary>( );
        app.UseMiddleware<SslMiddleware>( );
        if ( app.Services.GetService<CableServer>( ) is not null )
        {
            app.UseMiddleware<CableRequestMiddleware>( );
        }

        staticFiles( app );
        app.UseMiddleware<RequestIdMiddleware>( );
        if ( app.Services.GetService<PageResponseCache>( ) is not null )
        {
            app.UseMiddleware<PageCacheHitMiddleware>( );
        }

        app.UseMiddleware<FormAndMethodOverride>( );
        app.UseRouting( );
        app.UseMiddleware<RoutingRejection>( );
        app.UseMiddleware<RenderGeneration>( );
        app.UseAuthentication( );
        app.UseMiddleware<SessionActivity>( );
        app.UseMiddleware<BlockBannedRequestsMiddleware>( );
        app.UseAuthorization( );
        app.UseRateLimiter( );
        app.UseMiddleware<CsrfEnforcement>( );
        return app;
    }

    public static HttpOptions CreateHttpOptions( CampfireOptions options, string contentRoot ) => new( )
    {
        ForceSsl = !options.DisableSsl,
        PublicPath = options.PublicPath ?? Path.Combine( contentRoot, "public" )
    };

    private static LogLevel LogLevelFor( string level ) => level.ToLowerInvariant( ) switch
    {
        "debug" => LogLevel.Debug,
        "warn" => LogLevel.Warning,
        "error" or "fatal" or "unknown" => LogLevel.Error,
        _ => LogLevel.Information
    };
}
