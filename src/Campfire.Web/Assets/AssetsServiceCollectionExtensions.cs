using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Campfire.Web.Assets;

/// <summary>Registers and serves the Propshaft-compatible asset pipeline.</summary>
public static class AssetsServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="AssetCatalog"/> (built once, on first use), <see cref="AssetPaths"/>,
    /// <see cref="AssetTags"/> and <see cref="StaticFiles"/>. Directories come from the
    /// <c>Campfire:Assets</c> section, then the environment, then the checkout.
    /// </summary>
    public static IServiceCollection AddCampfireAssets( this IServiceCollection services, Action<AssetOptions>? configure = null )
    {
        ArgumentNullException.ThrowIfNull( services );

        var options = services.AddOptions<AssetOptions>( ).BindConfiguration( AssetOptions.SectionName );
        if ( configure is not null )
        {
            options.Configure( configure );
        }

        services.AddSingleton( provider =>
        {
            var configured = provider.GetRequiredService<IOptions<AssetOptions>>( ).Value;

            return AssetCatalog.Build(
                AssetDirectories.ResolveReference( configured.ReferenceDirectory ),
                AssetDirectories.ResolveVendor( configured.VendorDirectory ) );
        } );
        services.AddSingleton<AssetPaths>( );
        services.AddSingleton<AssetTags>( );
        services.AddSingleton<StaticFiles>( );

        return services;
    }

    /// <summary>
    /// <c>ActionDispatch::Static</c>: answers GET and HEAD requests for files in <c>public/</c> and
    /// <c>/assets/*</c>, and passes everything else on. Rails runs it inside <c>Rack::Deflater</c>
    /// and ahead of <c>Rack::ETag</c> (static responses carry no ETag), so place it after the
    /// deflater and before the ETag middleware. The catalog is built here, at startup, so a broken
    /// asset tree stops the boot instead of the first request.
    /// </summary>
    public static IApplicationBuilder UseCampfireAssets( this IApplicationBuilder app )
    {
        ArgumentNullException.ThrowIfNull( app );

        var staticFiles = app.ApplicationServices.GetRequiredService<StaticFiles>( );
        _ = app.ApplicationServices.GetRequiredService<AssetCatalog>( );

        return app.Use( ( httpContext, next ) =>
        {
            var response = staticFiles.Serve( ToStaticRequest( httpContext.Request ) );

            return response is null ? next( httpContext ) : WriteAsync( httpContext, response );
        } );
    }

    private static StaticRequest ToStaticRequest( HttpRequest request )
    {
        // Rack sees PATH_INFO as sent; Kestrel's Path is already decoded and dot-segment normalized.
        var rawTarget = request.HttpContext.Features.Get<IHttpRequestFeature>( )?.RawTarget;
        string rawPath;
        if ( rawTarget is not null && rawTarget.StartsWith( '/' ) )
        {
            var query = rawTarget.IndexOf( '?', StringComparison.Ordinal );
            rawPath = query >= 0 ? rawTarget[..query] : rawTarget;
        }
        else
        {
            rawPath = request.PathBase.Add( request.Path ).ToUriComponent( );
        }

        return new StaticRequest(
            request.Method,
            rawPath,
            request.Headers.AcceptEncoding.Count > 0 ? request.Headers.AcceptEncoding.ToString( ) : null,
            request.Headers.Range.Count > 0 ? request.Headers.Range.ToString( ) : null,
            request.Headers.IfModifiedSince.Count > 0 ? request.Headers.IfModifiedSince.ToString( ) : null,
            request.Headers.TryGetValue( HeaderNames.IfNoneMatch, out var noneMatch ) ? noneMatch.ToString( ) : null );
    }

    private static async Task WriteAsync( HttpContext httpContext, StaticResponse response )
    {
        var httpResponse = httpContext.Response;
        httpResponse.StatusCode = response.Status;
        foreach ( var (name, value) in response.Headers )
        {
            httpResponse.Headers.Append( name, value );
        }

        if ( !response.Body.IsEmpty )
        {
            await httpResponse.Body.WriteAsync( response.Body, httpContext.RequestAborted );
        }
    }
}
