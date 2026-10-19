using Campfire.Web.Views.Caching;
using Campfire.Web.Views.Models;
using Microsoft.AspNetCore.Html;
using RazorSlices;

namespace Campfire.Web.Views.Rendering;

/// <summary>The base of every slice with a <see cref="ViewContext"/>: partial and fragment rendering.</summary>
/// <typeparam name="TModel">The slice's model.</typeparam>
public abstract class ViewSlice<TModel> : RailsSlice<TModel>, IRecordedSlice
    where TModel : IViewModel
{
    protected ViewContext ViewContext => Model.ViewContext;
    ViewContext IRecordedSlice.RecordingContext => ViewContext;

    /// <summary><c>render partial</c>: <paramref name="slice"/> writes straight into this slice's output.</summary>
    protected ValueTask<HtmlString> Partial( RazorSlice slice ) => RenderPartialAsync( slice );

    /// <summary><c>render message</c> (<c>messages/_message</c>, whose body is <c>cache [ message, ... ]</c>).</summary>
    protected ValueTask<HtmlString> RenderMessage( MessageItem item )
    {
        ArgumentNullException.ThrowIfNull( item );

        return RenderCachedAsync(
            item,
            static ( viewContext, view ) => Views.messages._message.Create( new MessagePartial( viewContext, view ) ),
            item.View?.ValidUntil );
    }

    /// <summary><c>render membership</c> (<c>users/sidebars/rooms/_direct</c>, whose body is <c>cache membership</c>).</summary>
    protected ValueTask<HtmlString> RenderSidebarDirect( SidebarDirectItem item ) =>
        RenderCachedAsync( item, static ( viewContext, view ) => Views.users.sidebars.rooms._direct.Create( new SidebarDirectPartial( viewContext, view ) ) );

    /// <summary>
    /// <c>render boost</c> (<c>messages/boosts/_boost</c>, whose body is <c>cache boost</c>): no fragment is looked up
    /// before the view exists, so the store is asked while rendering.
    /// </summary>
    protected ValueTask<HtmlString> RenderBoost( BoostView boost )
    {
        ArgumentNullException.ThrowIfNull( boost );

        var cacheKey = ViewContext.Cache is null ? null : FragmentKeys.Boost( boost );
        return RenderCachedAsync(
            boost,
            cacheKey,
            static ( viewContext, view ) => Views.messages.boosts._boost.Create( new BoostPartial( viewContext, view ) ) );
    }

    /// <summary>
    /// A partial whose whole body is <c>cache record do ... end</c>: a fragment found up front goes out as it is;
    /// otherwise the item's view renders, through the store when there is one.
    /// </summary>
    private ValueTask<HtmlString> RenderCachedAsync<TView>(
        CachedItem<TView> item,
        Func<ViewContext, TView, RazorSlice> createSlice,
        DateTimeOffset? validUntil = null )
        where TView : class
    {
        ArgumentNullException.ThrowIfNull( item );

        if ( item.Fragment is { } fragment )
        {
            return ValueTask.FromResult( WriteFragment( fragment ) );
        }

        if ( item.View is not { } view )
        {
            throw new InvalidOperationException( "A cached item holds a fragment or a view." );
        }

        return RenderCachedAsync( view, item.CacheKey, createSlice, validUntil );
    }

    /// <summary>
    /// Renders <paramref name="view"/> into the store in the fragment context when there is a store and a key (a
    /// fragment stored meanwhile goes out instead), else inline with this request's context.
    /// </summary>
    private async ValueTask<HtmlString> RenderCachedAsync<TView>(
        TView view,
        string? cacheKey,
        Func<ViewContext, TView, RazorSlice> createSlice,
        DateTimeOffset? validUntil = null )
    {
        if ( ViewContext.Cache is { } cache && cacheKey is not null )
        {
            var fragment = await cache.FetchRenderedAsync( cacheKey, ViewContext.ForFragment( ), view, createSlice, validUntil, CancellationToken );
            return WriteFragment( fragment );
        }

        return await RenderPartialAsync( createSlice( ViewContext, view ) );
    }

    private HtmlString WriteFragment( CachedFragment fragment )
    {
        ViewContext.Recorder?.Mark( fragment );
        WriteLiteral( fragment.Bytes );
        return HtmlString.Empty;
    }

    /// <summary>
    /// <c>render partial: ..., collection: items</c>: each item's output, back to back, in the place of one
    /// expression (so the line keeps its indentation and newline, as ERB's does).
    /// </summary>
    protected static async ValueTask<HtmlString> Each<TItem>( IEnumerable<TItem> items, Func<TItem, ValueTask<HtmlString>> render )
    {
        ArgumentNullException.ThrowIfNull( items );
        ArgumentNullException.ThrowIfNull( render );

        foreach ( var item in items )
        {
            await render( item );
        }

        return HtmlString.Empty;
    }

    /// <summary>Writes the output of a <c>yield</c> callback (a partial layout's block) and returns nothing to write.</summary>
    protected static async ValueTask<HtmlString> Yield( Func<ValueTask> block )
    {
        ArgumentNullException.ThrowIfNull( block );

        await block( );
        return HtmlString.Empty;
    }
}
