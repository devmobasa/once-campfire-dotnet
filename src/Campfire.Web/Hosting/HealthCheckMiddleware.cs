using Campfire.Web.Http;
using Campfire.Web.Paths;
using Campfire.Web.Policy;
using Campfire.Web.Security;

namespace Campfire.Web.Hosting;

/// <summary>The Rails health page bypasses TLS redirects, credentials and database-backed middleware.</summary>
internal sealed class HealthCheckMiddleware( RequestDelegate next, HttpOptions options, CampfireVersion version )
{
    internal const string Body = "<!DOCTYPE html><html><body style=\"background-color: green\"></body></html>";
    private static readonly byte[] Bytes = "<!DOCTYPE html><html><body style=\"background-color: green\"></body></html>"u8.ToArray( );

    public Task InvokeAsync( HttpContext context )
    {
        if ( !context.Request.Path.Equals( EndpointPaths.Campfire.Up, StringComparison.OrdinalIgnoreCase )
            || !HttpMethodChecks.IsGetOrHead( context.Request.Method ) )
        {
            return next( context );
        }

        DefaultResponseHeadersMiddleware.Apply( context.Response, options, version );
        context.Response.ContentType = ContentTypes.HtmlUtf8;
        context.Response.ContentLength = Bytes.Length;
        return HttpMethodChecks.IsHead( context.Request.Method )
            ? Task.CompletedTask
            : context.Response.BodyWriter.WriteAsync( Bytes, context.RequestAborted ).AsTask( );
    }
}
