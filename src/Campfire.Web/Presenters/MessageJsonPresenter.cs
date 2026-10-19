using Campfire.Application.Data;
using Campfire.Application.Messages;
using Campfire.Application.RichText;
using Campfire.Application.Users;
using Campfire.Web.App;
using Campfire.Web.Paths;
using Campfire.Web.Views.Caching;
using Campfire.Web.Views.Helpers;
using Campfire.Web.Views.Json;

namespace Campfire.Web.Presenters;

/// <summary>
/// Messages and boosts as the bot API's Jbuilder views show them (<c>messages/_message.json.jbuilder</c>,
/// <c>messages/boosts/_boost.json.jbuilder</c>, <c>users/_user.json.jbuilder</c>), on one leased connection. Each entry
/// carries its <c>json.cache!</c> key, built from the record's version, its author's version and the base URL of its
/// URLs, so a cache hit skips building the fragment. A page's authors load in one query.
/// </summary>
public sealed class MessageJsonPresenter
{
    private readonly CampfireApp _app;
    private readonly CampfireDb _db;
    private readonly string _baseUrl;
    private readonly RichTextContext _richText;
    private readonly Dictionary<long, User> _users = [];

    /// <param name="app">The app.</param>
    /// <param name="db">A leased connection.</param>
    /// <param name="baseUrl">
    /// The request's protocol, host and port, which the <c>*_url</c> helpers use. The entries are cached, so a configured
    /// public origin takes its place (configured public origin), as in the HTML fragments.
    /// </param>
    /// <param name="requestHost"><c>Current.request_host</c>, which link previews in a body may not point back at.</param>
    public MessageJsonPresenter( CampfireApp app, CampfireDb db, string baseUrl, string? requestHost )
    {
        ArgumentNullException.ThrowIfNull( app );
        ArgumentNullException.ThrowIfNull( baseUrl );

        _app = app;
        _db = db;
        _baseUrl = app.Options.PublicOrigin ?? baseUrl;
        _richText = app.RichText( db, requestHost );
    }

    /// <summary><c>json.array! @messages, partial: "messages/message"</c>'s entries, with the authors preloaded.</summary>
    public List<JsonEntry<MessageJson>> MessageEntries( IReadOnlyList<Message> messages )
    {
        ArgumentNullException.ThrowIfNull( messages );

        Preload( [.. messages.Select( message => message.CreatorId )] );
        return [.. messages.Select( MessageEntry )];
    }

    /// <summary>
    /// <c>messages/_message.json.jbuilder</c> (<c>json.cache! message</c>): the id, <c>created_at.utc</c>, the body as
    /// <c>plain_text_body</c> and <c>body.to_s</c>, the creator (<c>users/_user</c>), the room's id and
    /// <c>room_message_url</c>.
    /// </summary>
    public JsonEntry<MessageJson> MessageEntry( Message message )
    {
        ArgumentNullException.ThrowIfNull( message );

        var creator = Author( message.CreatorId );
        var key = FragmentKeys.MessageJson( Version( message.Id, message.UpdatedAt ), Version( creator.Id, creator.UpdatedAt ), _baseUrl );

        return new JsonEntry<MessageJson>( key, ( ) => new MessageJson(
            message.Id,
            UserPresentation.AsUtc( message.CreatedAt ),
            MessageBodies.PlainText( _db, _app.Database.Hooks.BodyText, message.Id ),
            BodyHtml( message.Id ),
            ToUserJson( creator ),
            message.RoomId,
            _baseUrl + Routes.RoomMessage( message.RoomId, message.Id ) ) );
    }

    /// <summary>
    /// <c>messages/boosts/_boost.json.jbuilder</c> (<c>json.cache! boost</c>): the id, the content,
    /// <c>created_at.utc</c>, the booster (<c>users/_user</c>), and the message's id and <c>room_message_url</c>.
    /// </summary>
    public JsonEntry<BoostJson> BoostEntry( Boost boost, Message message )
    {
        ArgumentNullException.ThrowIfNull( boost );
        ArgumentNullException.ThrowIfNull( message );

        var booster = Author( boost.BoosterId );
        var key = FragmentKeys.BoostJson( Version( boost.Id, boost.UpdatedAt ), Version( booster.Id, booster.UpdatedAt ), _baseUrl );

        return new JsonEntry<BoostJson>( key, ( ) => new BoostJson(
            boost.Id,
            boost.Content,
            UserPresentation.AsUtc( boost.CreatedAt ),
            ToUserJson( booster ),
            boost.MessageId,
            _baseUrl + Routes.RoomMessage( message.RoomId, message.Id ) ) );
    }

    /// <summary><c>users/_user.json.jbuilder</c>: the id, name and role, and <c>fresh_user_avatar_url</c>.</summary>
    private UserJson ToUserJson( User user ) => new(
        user.Id,
        user.Name,
        UsersHelper.RoleName( user.Role ),
        _baseUrl + UserPresentation.AvatarPath( _app, user ) );

    /// <summary>
    /// <c>message.body.to_s</c>: the rendered rich text in its layout, or <c>""</c> without a body (an attachment
    /// message's unsaved <c>ActionText::RichText</c> has a nil body, whose <c>to_s</c> is empty).
    /// </summary>
    private string BodyHtml( long messageId ) =>
        MessageBodies.Body( _db, messageId ) is { } body ? RichTextPipeline.RenderHtml( body, _richText ) : "";

    /// <summary>
    /// The author: <c>message.creator</c> or <c>boost.booster</c>. Users are never deleted; a missing one fails the
    /// render as <c>nil</c> does in the partial (500).
    /// </summary>
    private User Author( long userId )
    {
        if ( !_users.ContainsKey( userId ) )
        {
            Preload( [userId] );
        }

        return _users.TryGetValue( userId, out var user )
            ? user
            : throw new InvalidOperationException( $"undefined method 'id' for nil (user {userId} is gone)" );
    }

    private void Preload( IReadOnlyCollection<long> userIds )
    {
        var missing = userIds.Where( id => !_users.ContainsKey( id ) ).Distinct( ).ToList( );
        if ( missing.Count == 0 )
        {
            return;
        }

        foreach ( var (id, user) in UserQueries.FindByIds( _db, missing ) )
        {
            _users[id] = user;
        }
    }

    private static RecordVersion Version( long id, DateTime updatedAt ) => new( id, UserPresentation.AsUtc( updatedAt ) );
}
