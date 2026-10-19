using System.Globalization;
using System.Text;
using Campfire.Web.Tests.Views.Support;
using Campfire.Web.Views;
using Campfire.Web.Views.Caching;
using Campfire.Web.Views.Models;
using static Campfire.Web.Tests.Views.Support.TokenStreamGoldenInputs;
using Slices = Campfire.Web.Views;

namespace Campfire.Web.Tests.Views;

/// <summary>
/// Fragment caching through the views: keys cover every input that changes a fragment, the store is consulted
/// before a view model is built, and stored fragments carry nothing viewer-specific.
/// </summary>
public sealed class FragmentRenderingTests
{
    private static readonly DateTimeOffset Then = DateTimeOffset.Parse( "2026-09-26T13:03:59.852991Z", CultureInfo.InvariantCulture );

    [Theory]
    [InlineData( false )]
    [InlineData( true )]
    public void Message_keys_change_with_every_input( bool memoize )
    {
        var inputs = new MessageCacheInputs( new( 1, Then ), new( 2, Then ), "HQ", [(new( 3, Then ), new( 4, Then ))], "http://campfire.test" )
        {
            DependencyMemo = memoize ? new FragmentKeys.MessageDependencyMemo( ) : null
        };
        var variants = new[]
        {
            inputs,
            inputs with { Message = new( 1, Then.AddTicks( 10 ) ) },
            inputs with { Creator = new( 2, Then.AddSeconds( 1 ) ) },
            inputs with { RoomName = "Designers" },
            inputs with { Boosters = [(new( 3, Then ), new( 4, Then.AddSeconds( 1 ) ))] },
            inputs with { Boosters = [] },
            inputs with { PublicBaseUrl = "https://chat.example" },
        };

        var keys = variants.Select( FragmentKeys.Message ).ToList( );

        Assert.Equal( keys.Count, keys.Distinct( StringComparer.Ordinal ).Count( ) );
        Assert.StartsWith( "views/messages/_message/messages/1-20260926130359852991/presentation-v3/", keys[0], StringComparison.Ordinal );
        Assert.Equal( keys[0], FragmentKeys.Message( inputs with
        {
            Boosters = [.. inputs.Boosters]
        } ) );

        var unboosted = inputs with { Boosters = [] };
        var unboostedVariants = new[]
        {
            unboosted,
            unboosted with { Creator = new( 2, Then.AddSeconds( 1 ) ) },
            unboosted with { Creator = new( 9, Then ) },
            unboosted with { RoomName = "Renamed" },
            unboosted with { PublicBaseUrl = "https://other.example" },
            unboosted with { Boosters = [(new( 3, Then ), new( 4, Then ))] },
        };
        var unboostedKeys = unboostedVariants.Select( FragmentKeys.Message ).ToList( );
        Assert.Equal( unboostedKeys.Count, unboostedKeys.Distinct( StringComparer.Ordinal ).Count( ) );
        Assert.Equal( unboostedKeys[0], FragmentKeys.Message( unboosted ) );
        // SHA-256 of the contract's UTF-8 dependency text, independently checked with OpenSSL.
        Assert.Equal( "views/messages/_message/messages/1-20260926130359852991/presentation-v3/dc4b6fd7908a0e376371907234e43a88", unboostedKeys[0] );
    }

    [Fact]
    public void A_hit_skips_building_the_view( )
    {
        var cache = new FragmentCache( );
        var viewContext = TestContexts.Plain( ) with
        {
            Cache = cache
        };
        var inputs = new MessageCacheInputs( new( 1, Then ), new( 2, Then ), "HQ", [], viewContext.FragmentBaseUrl );
        cache.Store( FragmentKeys.Message( inputs ), SafeHtml.Raw( "<div>cached</div>" ) );

        var item = MessageItem.Lookup( viewContext, inputs, "abc", 7, ( ) => throw new InvalidOperationException( "a hit must not build the view" ) );

        Assert.Equal( "<div>cached</div>", item.Fragment?.Html );
        Assert.Null( item.View );
        Assert.Equal( "message_abc", item.DomId( ) );
    }

    [Fact]
    public async Task A_message_renders_once_without_tokens_in_the_cached_fragment( )
    {
        var golden = TokenStreamGolden.Load( "messages_show_text" );
        var cache = new FragmentCache( );
        var viewContext = golden.Context( ) with
        {
            Cache = cache
        };
        var view = Message( golden.Input );
        var inputs = new MessageCacheInputs( new( view.Id, view.UpdatedAt ), new( view.Creator.Id, Then ), view.RoomName, [], viewContext.FragmentBaseUrl );
        var builds = 0;
        MessageItem Item( ) => MessageItem.Lookup( viewContext, inputs, view.ClientMessageId, view.RoomId, ( ) =>
        {
            builds++;
            return view;
        } );

        var first = await Parity.Render( Slices.messages.show.Create( new MessagesShowPage( viewContext, Item( ) ) ) );
        var second = await Parity.Render( Slices.messages.show.Create( new MessagesShowPage( viewContext, Item( ) ) ) );

        Assert.Equal( 1, builds );
        Assert.Equal( first, second );
        Assert.True( cache.TryGet( FragmentKeys.Message( inputs ), out var fragment ) );

        // The store holds placeholders, never a request's tokens; the page resolves them with its own.
        Assert.DoesNotContain( $"value=\"{CanonicalHtml.Csrf}\"", fragment.Html, StringComparison.Ordinal );
        Assert.DoesNotContain( "authenticity_token", fragment.Html, StringComparison.Ordinal );
        Assert.Contains( fragment.Html, first, StringComparison.Ordinal );
        Assert.Contains( "name=\"csrf-token\"", first, StringComparison.Ordinal );
    }

    [Fact]
    public async Task A_broadcast_fills_the_store_the_next_page_reads( )
    {
        var golden = TokenStreamGolden.Load( "messages_create" );
        var cache = new FragmentCache( );
        var viewContext = golden.Context( ) with
        {
            Cache = cache
        };
        var view = Message( golden.Input["message"]! );
        var inputs = new MessageCacheInputs( new( view.Id, view.UpdatedAt ), new( view.Creator.Id, Then ), view.RoomName, [], viewContext.FragmentBaseUrl );

        var item = MessageItem.Lookup( viewContext, inputs, view.ClientMessageId, view.RoomId, ( ) => view );
        var frame = Encoding.UTF8.GetString( await Broadcasts.MessageCreatedUtf8(
            viewContext,
            item,
            Kind( golden.Input["room_kind"] ),
            TestContext.Current.CancellationToken ) );
        var hit = MessageItem.Lookup(
            viewContext,
            inputs,
            view.ClientMessageId,
            view.RoomId,
            ( ) => throw new InvalidOperationException( "the broadcast stored it" ) );

        Assert.StartsWith( "<turbo-stream action=\"append\" target=\"messages_rooms_", frame, StringComparison.Ordinal );
        Assert.DoesNotContain( "authenticity_token", frame, StringComparison.Ordinal );
        Assert.Contains( hit.Fragment!.Html, frame, StringComparison.Ordinal );
    }

    [Fact]
    public async Task A_fragment_a_broadcast_rendered_first_serves_later_pages_without_token_fields( )
    {
        var golden = TokenStreamGolden.Load( "messages_create" );
        var cache = new FragmentCache( );
        var page = golden.Context( ) with
        {
            Cache = cache
        };
        var broadcast = page with
        {
            IncludeCsrfTags = false
        };
        var view = Message( golden.Input["message"]! );
        var inputs = new MessageCacheInputs( new( view.Id, view.UpdatedAt ), new( view.Creator.Id, Then ), view.RoomName, [], page.FragmentBaseUrl );

        await Broadcasts.MessageCreatedUtf8(
            broadcast,
            MessageItem.Lookup( broadcast, inputs, view.ClientMessageId, view.RoomId, ( ) => view ),
            RoomKind.Open,
            TestContext.Current.CancellationToken );
        var hit = MessageItem.Lookup(
            page,
            inputs,
            view.ClientMessageId,
            view.RoomId,
            ( ) => throw new InvalidOperationException( "the broadcast stored it" ) );

        // Rails caches the renderer's output, which has no session and so no token fields, and serves it to every page.
        Assert.DoesNotContain( "authenticity_token", hit.Fragment!.Html, StringComparison.Ordinal );
    }

    [Fact]
    public async Task Uncached_broadcasts_render_without_tokens( )
    {
        var golden = TokenStreamGolden.Load( "messages_create" );
        var viewContext = golden.Context( );
        var view = Message( golden.Input["message"]! );

        var frame = Encoding.UTF8.GetString( await Broadcasts.MessageCreatedUtf8(
            viewContext,
            MessageItem.Uncached( view ),
            RoomKind.Open,
            TestContext.Current.CancellationToken ) );

        Assert.DoesNotContain( "authenticity_token", frame, StringComparison.Ordinal );
        Assert.Equal( "<turbo-stream action=\"remove\" target=\"message_rich\"></turbo-stream>", Broadcasts.MessageRemoved( "rich" ).Html );
    }

    [Fact]
    public async Task Sidebar_direct_rooms_are_looked_up_before_building( )
    {
        var cache = new FragmentCache( );
        var viewContext = TestContexts.Plain( ) with
        {
            Cache = cache
        };
        var member = new UserSummary { Id = 9, Name = "Kevin McConnell", AvatarPath = "/users/9/avatar" };
        var build = ( ) => new SidebarDirect( 5, false, "1790427620300", [member] );
        var membership = new RecordVersion( 3, Then );
        var room = new RecordVersion( 5, Then );

        var miss = SidebarDirectItem.Lookup( viewContext, membership, room, [new( 9, Then )], build );
        var html = await Parity.Render( Slices.users.sidebars.show.Create( new SidebarPage( viewContext, member, "s", "u", [miss], [], [], false ) ) );
        var hit = SidebarDirectItem.Lookup( viewContext, membership, room, [new( 9, Then )], ( ) => throw new InvalidOperationException( "cached" ) );
        var renamed = SidebarDirectItem.Lookup( viewContext, membership, room, [new( 9, Then.AddSeconds( 1 ) )], build );

        Assert.NotNull( hit.Fragment );
        Assert.Contains( hit.Fragment.Html, html, StringComparison.Ordinal );
        Assert.Contains( "Kevin", hit.Fragment.Html, StringComparison.Ordinal );
        Assert.Null( renamed.Fragment );
    }
}
