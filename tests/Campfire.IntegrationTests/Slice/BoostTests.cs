using System.Net;
using System.Text.Json.Nodes;
using Campfire.Application.Rooms;
using Campfire.IntegrationTests.Support;
using Campfire.Web.Channels;
using Campfire.Web.Paths;

namespace Campfire.IntegrationTests.Slice;

/// <summary>
/// <c>Messages::BoostsController</c> (<c>test/controllers/messages/boosts_controller_test.rb</c>) and the operation's boost
/// sequence: the boost row and deferred touch in the transaction, then the room's broadcast. Unchanged text is not reindexed.
/// </summary>
public sealed class BoostTests( SharedSliceApp shared ) : IClassFixture<SharedSliceApp>
{
    private static readonly long Designers = SliceAppFactory.Id( "designers" );
    private static readonly long First = SliceAppFactory.Id( "first" );

    [Fact]
    public async Task The_boosts_and_the_boost_form_render_for_a_reachable_message( )
    {
        var client = await shared.SignedInAsync( "david@37signals.com" );

        var index = await client.GetAsync( Routes.MessageBoosts( First ) );
        var form = await client.GetAsync( Routes.NewMessageBoost( First ) );

        Assert.Equal( (HttpStatusCode.OK, HttpStatusCode.OK), (index.Status, form.Status) );
        Assert.Contains( "id=\"boosts_message_0001\"", index.Body, StringComparison.Ordinal );
        Assert.Contains( "Hello", index.Body, StringComparison.Ordinal );
        Assert.Contains( "id=\"new_boost_message_0001\"", form.Body, StringComparison.Ordinal );
        Assert.Contains( $"action=\"/messages/{First}/boosts\"", form.Body, StringComparison.Ordinal );
    }

    [Fact]
    public async Task Cached_boost_fragments_have_no_token_and_removal_keeps_origin_and_owner_checks( )
    {
        var david = await shared.SignedInAsync( "david@37signals.com" );
        var kevin = await shared.SignedInAsync( "kevin@37signals.com" );
        var boost = SliceAppFactory.Id( "first" );
        var formAction = $"action=\"/messages/{First}/boosts/{boost}\"";
        var davidPage = await david.GetAsync( Routes.MessageBoosts( First ) );
        var kevinPage = await kevin.GetAsync( Routes.MessageBoosts( First ) );
        Assert.DoesNotContain( "authenticity_token", FormMarkup( davidPage.Body, formAction ), StringComparison.Ordinal );
        Assert.DoesNotContain( "authenticity_token", FormMarkup( kevinPage.Body, formAction ), StringComparison.Ordinal );
        var davidsToken = SliceClient.MetaToken( davidPage.Body );
        var kevinsToken = SliceClient.MetaToken( kevinPage.Body );
        var withKevinsToken = await kevin.SubmitFormAsync(
            HttpMethod.Post,
            Routes.MessageBoost( First, boost ),
            kevinsToken,
            [("_method", "delete")],
            accept: "text/html" );
        var withDavidsToken = await kevin.SubmitFormAsync(
            HttpMethod.Post,
            Routes.MessageBoost( First, boost ),
            davidsToken,
            [("_method", "delete")],
            accept: "text/html",
            headers: [(BrowserHeaders.FetchSite, "cross-site")] );

        // A same-origin Kevin request cannot remove David's boost; hostile metadata fails before the operation.
        Assert.Equal( HttpStatusCode.NotFound, withKevinsToken.Status );
        Assert.Equal( HttpStatusCode.UnprocessableEntity, withDavidsToken.Status );
        Assert.Equal( 1, shared.Scalar<long>( $"SELECT count(*) FROM boosts WHERE id = {boost}" ) );
    }

    [Fact]
    public async Task A_message_in_a_room_the_user_is_not_in_is_not_found( )
    {
        var client = await shared.SignedInAsync( "jz@37signals.com" );

        var reply = await client.GetAsync( Routes.MessageBoosts( SliceAppFactory.Id( "fourth" ) ) );

        Assert.Equal( HttpStatusCode.NotFound, reply.Status );
    }

    [Fact]
    public async Task Boosting_touches_without_reindexing_then_appends_the_boost_then_redirects( )
    {
        using var factory = new SliceAppFactory( );
        var (client, token) = await factory.SignedInWithPageTokenAsync( "david@37signals.com", Routes.Room( Designers ) );
        factory.Execute( $"INSERT INTO message_search_index(rowid, body) VALUES ({First}, 'stale')" );
        var app = factory.App;
        var room = await app.Database.ReadAsync( db => RoomQueries.FindById( db, Designers )!, TestContext.Current.CancellationToken );
        var payloads = new List<string>( );
        using var subscription = app.Cable.Hub.Subscribe( string.Join( ':', RoomBroadcasts.RoomMessages( room ) ), payload =>
        {
            payloads.Add( payload );
            factory.Events.Record( "broadcast" );
        } );
        var before = factory.Scalar<string>( $"SELECT updated_at FROM messages WHERE id = {First}" );
        factory.Clock.Advance( TimeSpan.FromSeconds( 5 ) );
        factory.Events.Clear( );

        var reply = await client.SubmitFormAsync( HttpMethod.Post, Routes.MessageBoosts( First ), token, [("boost[content]", "Morning!")] );

        var david = SliceAppFactory.Id( "david" );
        var boost = factory.Scalar<long>( $"SELECT id FROM boosts WHERE message_id = {First} AND content = 'Morning!' AND booster_id = {david}" );
        Assert.Equal( HttpStatusCode.Found, reply.Status );
        Assert.Equal( $"{factory.BaseAddress}messages/{First}/boosts", reply.Location );
        Assert.NotEqual( before, factory.Scalar<string>( $"SELECT updated_at FROM messages WHERE id = {First}" ) );
        Assert.Equal( "stale", factory.Scalar<string>( $"SELECT body FROM message_search_index WHERE rowid = {First}" ) );
        Assert.Equal( ["broadcast"], factory.Events.Only( "plain_text", "broadcast" ) );
        var payload = JsonNode.Parse( Assert.Single( payloads ) )!.GetValue<string>( );
        Assert.StartsWith(
            "<turbo-stream maintain_scroll=\"true\" action=\"append\" target=\"boosts_message_0001\"><template>",
            payload,
            StringComparison.Ordinal );
        Assert.Contains( $"id=\"boost_{boost}\"", payload, StringComparison.Ordinal );
        client.Dispose( );
    }

    [Fact]
    public async Task The_boosts_list_serves_cached_fragments_without_token_fields( )
    {
        using var factory = new SliceAppFactory( );
        var (client, token) = await factory.SignedInWithPageTokenAsync( "david@37signals.com", Routes.Room( Designers ) );

        await client.SubmitFormAsync( HttpMethod.Post, Routes.MessageBoosts( First ), token, [("boost[content]", "Morning!")] );
        var boost = factory.Scalar<long>( $"SELECT id FROM boosts WHERE message_id = {First} AND content = 'Morning!'" );
        var list = await client.GetAsync( Routes.MessageBoosts( First ) );
        var existing = SliceAppFactory.Id( "first" );

        // Cached fragments never include a hidden token; submissions use the page meta token.
        Assert.DoesNotContain( "authenticity_token", Between( list.Body, $"id=\"boost_{boost}\"", "</form>" ), StringComparison.Ordinal );
        Assert.DoesNotContain( "authenticity_token", Between( list.Body, $"id=\"boost_{existing}\"", "</form>" ), StringComparison.Ordinal );
        client.Dispose( );
    }

    [Fact]
    public async Task Only_the_booster_removes_a_boost_and_the_removal_goes_to_the_room( )
    {
        using var factory = new SliceAppFactory( );
        var boost = SliceAppFactory.Id( "first" );
        var (kevin, kevinToken) = await factory.SignedInWithPageTokenAsync( "kevin@37signals.com", Routes.Room( Designers ) );
        var (david, davidToken) = await factory.SignedInWithPageTokenAsync( "david@37signals.com", Routes.Room( Designers ) );
        using var taps = await SliceTaps.OnRoomAsync( factory, Designers );
        factory.Events.Clear( );

        var notTheirs = await kevin.SubmitFormAsync( HttpMethod.Delete, Routes.MessageBoost( First, boost ), kevinToken );
        var removed = await david.SubmitFormAsync( HttpMethod.Delete, Routes.MessageBoost( First, boost ), davidToken );

        Assert.Equal( HttpStatusCode.NotFound, notTheirs.Status );
        Assert.Equal( HttpStatusCode.NoContent, removed.Status );
        Assert.Equal( 0, factory.Scalar<long>( $"SELECT count(*) FROM boosts WHERE id = {boost}" ) );
        Assert.Equal( ["broadcast remove"], factory.Events.Only( "broadcast" ) );
        kevin.Dispose( );
        david.Dispose( );
    }

    [Fact]
    public async Task A_boost_without_content_cannot_be_saved( )
    {
        using var factory = new SliceAppFactory( );
        var (client, token) = await factory.SignedInWithPageTokenAsync( "david@37signals.com", Routes.Room( Designers ) );
        var boosts = factory.Scalar<long>( "SELECT count(*) FROM boosts" );

        var reply = await client.SubmitFormAsync( HttpMethod.Post, Routes.MessageBoosts( First ), token, [("boost[other]", "x")] );

        Assert.Equal( HttpStatusCode.InternalServerError, reply.Status );
        Assert.Equal( boosts, factory.Scalar<long>( "SELECT count(*) FROM boosts" ) );
        client.Dispose( );
    }

    private static string FormMarkup( string html, string marker )
    {
        var start = html.IndexOf( marker, StringComparison.Ordinal );
        Assert.True( start >= 0, $"no {marker}" );
        var end = html.IndexOf( "</form>", start, StringComparison.Ordinal );
        Assert.True( end >= 0, "the matching form is not closed" );
        return html[start..(end + "</form>".Length)];
    }

    /// <summary>The text from <paramref name="start"/> to the next <paramref name="end"/>.</summary>
    private static string Between( string text, string start, string end )
    {
        var from = text.IndexOf( start, StringComparison.Ordinal );
        Assert.True( from >= 0, $"no {start}" );
        var to = text.IndexOf( end, from, StringComparison.Ordinal );
        return text[from..(to < 0 ? text.Length : to)];
    }
}
