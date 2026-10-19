using Campfire.Application.Data;
using Campfire.Application.Data.Records;
using Campfire.Application.Messages;
using Campfire.Application.RichText;
using Campfire.Application.Rooms;
using Campfire.Application.Storage;
using Campfire.Application.Users;
using Campfire.Web.App;
using Campfire.Web.Views.Caching;
using Campfire.Web.Views.Models;

namespace Campfire.Web.Presenters;

/// <summary>
/// Maps message rows to what <c>messages/_message</c> renders, on one leased connection, with the rows a page needs
/// preloaded (Rails' <c>with_creator</c>, <c>with_attachment_details</c>, <c>with_boosts</c>). <see cref="Items"/> looks
/// each message's fragment up before building its view: <c>cache [ message, "presentation-v3" ]</c>
/// wraps the whole partial, so on a hit nothing of it is evaluated. Ported from Rust <c>controllers/presenters</c> at
/// <c>3fe9f61</c>. The loading of the rows lives in <c>MessagePresenter.Loading.cs</c>, the mapping to views in
/// <c>MessagePresenter.Views.cs</c>.
/// </summary>
public sealed partial class MessagePresenter
{
    private readonly CampfireApp _app;
    private readonly CampfireDb _db;
    private readonly RichTextContext _richText;
    private readonly bool _ffmpegExists;
    private readonly Dictionary<long, User?> _users = [];
    private readonly Dictionary<long, RecordVersion> _versions = [];
    private readonly Dictionary<long, string> _roomNames = [];
    private readonly Dictionary<long, List<Boost>> _boosts = [];
    private readonly List<long> _pendingUserIds = [];
    private FragmentKeys.MessageDependencyMemo? _keyMemo;
    private Dictionary<long, string?>? _bodies;
    private Dictionary<long, (ActiveStorageAttachment Attachment, ActiveStorageBlob Blob)?>? _attachments;

    /// <param name="app">The app.</param>
    /// <param name="db">A leased connection.</param>
    /// <param name="requestHost"><c>Current.request_host</c>, which link previews may not point back at; null outside a request.</param>
    /// <param name="ffmpegExists">Whether videos are previewable (<see cref="BlobStorage.IsPreviewableAsync"/>), known before the lease.</param>
    /// <param name="room">The room every message is in, when there is one: its display name is then known up front.</param>
    public MessagePresenter( CampfireApp app, CampfireDb db, string? requestHost, bool ffmpegExists, Room? room = null )
    {
        ArgumentNullException.ThrowIfNull( app );

        _app = app;
        _db = db;
        _richText = app.RichText( db, requestHost );
        _ffmpegExists = ffmpegExists;
        if ( room is not null )
        {
            _roomNames[room.Id] = RoomPresentation.DisplayName( db, room, forUser: null );
        }
    }

    /// <summary>Only the creating foreground render reuses its immutable canonical text; jobs load their own data.</summary>
    internal void UseCreatedText( CreatedMessage created )
    {
        if ( created.CanonicalText.IsSet )
        {
            _bodies ??= [];
            _bodies[created.Message.Id] = created.CanonicalText.Value;
        }
    }

    /// <summary>
    /// <c>render @messages</c>: each message's stored fragment, or its view. <paramref name="viewContext"/> must be the one
    /// the fragments render in, since the key includes its fragment origin. Every fragment is looked up before any
    /// view is built, so the bodies and attachments of all the misses load together, once.
    /// </summary>
    public List<MessageItem> Items( ViewContext viewContext, IReadOnlyList<Message> messages )
    {
        ArgumentNullException.ThrowIfNull( viewContext );
        ArgumentNullException.ThrowIfNull( messages );

        Preload( messages );
        var items = new List<MessageItem>( messages.Count );
        List<long>? missedIds = null;
        for ( var index = 0; index < messages.Count; index++ )
        {
            var message = messages[index];
            var item = MessageItem.Find( viewContext, CacheInputs( viewContext, message ), message.ClientMessageId, message.RoomId );
            if ( item.Fragment is null )
            {
                missedIds ??= new List<long>( messages.Count );
                missedIds.Add( message.Id );
            }

            items.Add( item );
        }

        if ( missedIds is not null )
        {
            LoadBodies( missedIds );
            for ( var index = 0; index < items.Count; index++ )
            {
                if ( items[index].Fragment is null )
                {
                    items[index].Build( View( messages[index] ) );
                }
            }
        }

        return items;
    }

    /// <summary><c>render message</c>.</summary>
    public MessageItem Item( ViewContext viewContext, Message message )
    {
        ArgumentNullException.ThrowIfNull( viewContext );
        ArgumentNullException.ThrowIfNull( message );

        return MessageItem.Lookup(
            viewContext,
            CacheInputs( viewContext, message ),
            message.ClientMessageId,
            message.RoomId,
            (Presenter: this, Message: message),
            static state => state.Presenter.View( state.Message ) );
    }

    /// <summary>What a message's fragment depends on, known before its view is built.</summary>
    public MessageCacheInputs CacheInputs( ViewContext viewContext, Message message )
    {
        ArgumentNullException.ThrowIfNull( viewContext );
        ArgumentNullException.ThrowIfNull( message );

        var boosts = Boosts( message.Id );
        var boosters = boosts.Count == 0 ? [] : new (RecordVersion Boost, RecordVersion Booster)[boosts.Count];
        for ( var index = 0; index < boosts.Count; index++ )
        {
            var boost = boosts[index];
            boosters[index] = (new RecordVersion( boost.Id, UserPresentation.AsUtc( boost.UpdatedAt ) ), Version( boost.BoosterId ));
        }

        return new MessageCacheInputs(
            new RecordVersion( message.Id, UserPresentation.AsUtc( message.UpdatedAt ) ),
            Version( message.CreatorId ),
            RoomName( message.RoomId ),
            boosters,
            viewContext.FragmentBaseUrl )
        {
            DependencyMemo = viewContext.Cache is null ? null : (_keyMemo ??= new FragmentKeys.MessageDependencyMemo( ))
        };
    }
}
