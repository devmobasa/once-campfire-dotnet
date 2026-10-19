using Campfire.Web.Views.Models;
using RazorSlices;

namespace Campfire.Web.Views.Rendering;

/// <summary>A page layout rendered inside the application layout (<c>render layout: "rooms/layouts/edit" do</c>).</summary>
/// <typeparam name="TModel">The layout's model.</typeparam>
public abstract class PageLayoutSlice<TModel> : RailsLayoutSlice<TModel>, IUsesLayout<Views.layouts.application, LayoutModel>
    where TModel : IViewModel
{
    public LayoutModel LayoutModel => new( ViewContext, PageTitle, BodyClass );

    protected ViewContext ViewContext => Model.ViewContext;

    /// <summary><c>@page_title</c>.</summary>
    protected virtual string? PageTitle => null;

    /// <summary><c>@body_class</c>.</summary>
    protected virtual string? BodyClass => null;

    public RazorSlice<LayoutModel> CreateLayout( ) => Layouts.Create( ViewContext, LayoutModel );
}
