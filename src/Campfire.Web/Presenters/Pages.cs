using Campfire.Web.App;
using Campfire.Web.Http;
using Campfire.Web.Views.Models;
using Campfire.Web.Views.Rendering;
using RazorSlices;

namespace Campfire.Web.Presenters;

/// <summary>
/// How actions render templates: pages in the application layout (or turbo-rails' frame layout), templates without
/// a layout, and turbo streams. Each builds the request's <see cref="ViewContext"/> first (Rust
/// <c>presenters/page.rs</c> at <c>3fe9f61</c>).
/// </summary>
public static class Pages
{
    /// <summary>
    /// The implicit render of an <c>&lt;action&gt;.html.erb</c> page: the template's lookup (an action whose only
    /// template is HTML can't answer a request that doesn't accept HTML: <c>ActionController::UnknownFormat</c>, 406),
    /// then the application layout, or the frame layout for a Turbo-Frame request when <paramref name="frameLayout"/>
    /// (turbo-rails' <c>layout -&gt; { "turbo_rails/frame" if turbo_frame_request? }</c>, which a controller's own
    /// <c>layout</c> replaces).
    /// </summary>
    public static async Task<FrontendResult> PageAsync(
        RequestContext requestContext,
        Func<ViewContext, RazorSlice> page,
        int status = StatusCodes.Status200OK,
        bool frameLayout = true,
        bool cacheFragments = false )
    {
        ArgumentNullException.ThrowIfNull( requestContext );
        ArgumentNullException.ThrowIfNull( page );

        requestContext.RespondTo( Mime.Html );
        return await RenderPageAsync( requestContext, page, status, frameLayout, cacheFragments );
    }

    /// <summary>
    /// <see cref="PageAsync"/> without the template lookup: an explicit <c>render template:</c> answers HTML whatever
    /// the request's format, including the incompatible-browser page.
    /// </summary>
    public static async Task<FrontendResult> RenderPageAsync(
        RequestContext requestContext,
        Func<ViewContext, RazorSlice> page,
        int status = StatusCodes.Status200OK,
        bool frameLayout = true,
        bool cacheFragments = false )
    {
        ArgumentNullException.ThrowIfNull( page );

        var viewContext = await ContextAsync( requestContext, frameLayout, cacheFragments );
        return await RenderAsync( requestContext, viewContext, page( viewContext ), status );
    }

    /// <summary>
    /// The request's view context for a page, with the layout turbo-rails picks. Actions that look fragments up before
    /// building their views (a page of messages) build it first, then <see cref="RenderAsync"/>.
    /// </summary>
    public static Task<ViewContext> ContextAsync( RequestContext requestContext, bool frameLayout = true, bool cacheFragments = false )
    {
        ArgumentNullException.ThrowIfNull( requestContext );

        var layout = frameLayout && requestContext.IsTurboFrameRequest ? LayoutKind.TurboFrame : LayoutKind.Application;
        return requestContext.Views( ).ForRequestAsync( requestContext, layout, cacheFragments );
    }

    /// <summary>
    /// Renders a page slice built with <paramref name="viewContext"/>, adding the <c>Link</c> preload header when the
    /// application layout (with its stylesheets) rendered.
    /// </summary>
    public static async Task<FrontendResult> RenderAsync(
        RequestContext requestContext,
        ViewContext viewContext,
        RazorSlice page,
        int status = StatusCodes.Status200OK )
    {
        ArgumentNullException.ThrowIfNull( requestContext );
        ArgumentNullException.ThrowIfNull( viewContext );
        ArgumentNullException.ThrowIfNull( page );

        // Stable parts share content validators independently of the negotiated encoding.
        var computeDigest = RackETag.NeedsBodyDigest( status, requestContext.ResponseHeaders );
        var body = await SliceRenderer.RenderPageAsync( page, requestContext.RequestAborted, computeDigest );
        requestContext.HttpContext.Response.RegisterForDispose( body );
        var response = requestContext.Render( status, Mime.Html, "" );
        response.Body = body;
        if ( viewContext.Layout == LayoutKind.Application )
        {
            requestContext.HttpContext.RequestServices.GetRequiredService<LayoutAssets>( ).AddPreloadLinks( response.Headers );
        }

        return response;
    }

    /// <summary>A template rendered without a layout (<c>layout false</c>, a turbo stream, a fragment), labelled with its format.</summary>
    public static async Task<FrontendResult> BareAsync(
        RequestContext requestContext,
        MimeType template,
        Func<ViewContext, RazorSlice> view,
        int status = StatusCodes.Status200OK )
    {
        ArgumentNullException.ThrowIfNull( requestContext );
        ArgumentNullException.ThrowIfNull( view );

        requestContext.RespondTo( template );
        var viewContext = await requestContext.Views( ).ForRequestAsync( requestContext );
        return await BareRenderAsync( requestContext, template, view( viewContext ), status );
    }

    /// <summary>A bare template whose model and fragment lookup already used the request's view context.</summary>
    public static async Task<FrontendResult> BareRenderAsync(
        RequestContext requestContext,
        MimeType template,
        RazorSlice view,
        int status = StatusCodes.Status200OK )
    {
        var computeDigest = RackETag.NeedsBodyDigest( status, requestContext.ResponseHeaders );
        var body = await SliceRenderer.RenderPageAsync( view, requestContext.RequestAborted, computeDigest );
        requestContext.HttpContext.Response.RegisterForDispose( body );
        var response = requestContext.Render( status, template, "" );
        response.Body = body;
        return response;
    }
}
