using Campfire.Web.Http;
using Campfire.Web.Policy;

namespace Campfire.Web.Security;

/// <summary>Application-wide response headers: the configured defaults and the app's version and revision.</summary>
public sealed class DefaultResponseHeadersMiddleware( RequestDelegate next, HttpOptions options, CampfireVersion version )
{
    public async Task InvokeAsync( HttpContext httpContext )
    {
        httpContext.Response.OnStarting( ( ) =>
        {
            Apply( httpContext.Response, options, version );
            return Task.CompletedTask;
        } );
        await next( httpContext );
    }

    internal static void Apply( HttpResponse response, HttpOptions options, CampfireVersion version )
    {
        foreach ( var (name, value) in options.DefaultHeaders )
        {
            response.Headers.TryAdd( name, value );
        }

        response.Headers[CampfireHeaderNames.Version] = version.AppVersion;
        if ( version.GitRevision is { } revision )
        {
            response.Headers[CampfireHeaderNames.Revision] = revision;
        }
    }
}
