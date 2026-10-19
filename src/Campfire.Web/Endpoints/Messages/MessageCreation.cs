using Campfire.Application.Bots;
using Campfire.Application.Messages;
using Campfire.Application.RichText;
using Campfire.Application.Rooms;
using Campfire.Application.Storage;
using Campfire.Web.App;
using Campfire.Web.Http;
using Campfire.Web.Presenters;
using Campfire.Web.Views.Helpers;
using Campfire.Web.Workers;

namespace Campfire.Web.Endpoints.Messages;

/// <summary>
/// <c>MessagesController#create</c>'s sequence, shared with the bot API:
/// <list type="number">
/// <item>Before any lease: the upload is staged (copied, checksummed, identified) and the body canonicalized.</item>
/// <item>One write transaction: the message, body, blob and attachment rows, touches, search index and unread memberships.
/// After commit: publish the staged file, queue push delivery, then queue analysis.</item>
/// <item><c>process_attachment</c>: analysis (outside the lease; its rows in a short transaction), then the
/// <c>:thumb</c> variant or the video's WebP preview.</item>
/// <item><c>broadcast_create</c>: append the message, then queue timestamped notices for unread or connected members.</item>
/// <item>Webhook jobs for the eligible bots.</item>
/// </list>
/// Once the transaction commits, nothing below it takes the request's token: a client that goes away doesn't cancel
/// committed work.
/// </summary>
public static class MessageCreation
{
    /// <summary>Creates the message, then answers with <c>messages/create.turbo_stream</c>.</summary>
    public static async Task<FrontendResult> CreateAsync( RequestContext requestContext, Room room, long creatorId, MessageAttributes attributes )
    {
        var (_, html) = await CreateAndDeliverAsync( requestContext, room, creatorId, attributes );

        // create.turbo_stream renders @message, whose fragment broadcast_create just stored: the request-less
        // rendering, as in Rails.
        requestContext.RespondTo( Mime.TurboStream );
        return requestContext.Render( StatusCodes.Status200OK, Mime.TurboStream, new BytesBody( html ) );
    }

    /// <summary>
    /// <c>MessagesController#create</c> up to its response, which the bot API (<c>Messages::ByBotsController#create</c>,
    /// <c>super</c> then <c>head :created</c>) shares: the create and its after-commit work, <c>process_attachment</c>,
    /// <c>broadcast_create</c> and <c>deliver_webhooks_to_bots</c>. Returns the message and the broadcast
    /// <c>&lt;turbo-stream&gt;</c>.
    /// </summary>
    public static async Task<(Message Message, byte[] BroadcastHtml)> CreateAndDeliverAsync(
        RequestContext requestContext,
        Room room,
        long creatorId,
        MessageAttributes attributes )
    {
        ArgumentNullException.ThrowIfNull( requestContext );
        ArgumentNullException.ThrowIfNull( room );
        ArgumentNullException.ThrowIfNull( attributes );

        var app = requestContext.App( );
        var requestHost = requestContext.Request.Host;
        var created = await CreateMessageAsync( app, room, creatorId, attributes, requestHost, requestContext.RequestAborted );

        // Committed: every step below runs to the end.
        var committed = CancellationToken.None;
        await ProcessAttachmentAsync( app, created, committed );
        var broadcast = new MessageBroadcast( room, created.Message, ViewContexts.RendererBaseUrl( requestContext ), requestHost, created );
        var html = await BroadcastCreateAsync( app, requestContext.Views( ), broadcast, committed );
        await app.Database.ReadAsync( db =>
        {
            BotOperations.DeliverWebhooksToBots( db, app.Database.Hooks, created.Message, room );
            return true;
        }, committed );

        return (created.Message, html);
    }

    /// <summary>
    /// <c>@room.messages.create_with_attachment!(attributes)</c> without its processing: stage, then the transaction and
    /// its after-commit work. The staged file is removed unless the upload step kept it (a rollback, or a failure
    /// before the upload step).
    /// </summary>
    public static async Task<CreatedMessage> CreateMessageAsync(
        CampfireApp app,
        Room room,
        long creatorId,
        MessageAttributes attributes,
        string? requestHost,
        CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( app );
        ArgumentNullException.ThrowIfNull( room );
        ArgumentNullException.ThrowIfNull( attributes );

        // nil or "" (a removal) removes nothing from a new message.
        using var attachment = await StagedAssignment.StageAsync( app, attributes.Attachment, cancellationToken );
        var body = attributes.HasBody ? attributes.Body ?? "" : null;
        var source = new MessageSource( room.Id, creatorId, body, attachment.Upload, attributes.ClientMessageId, attachment.SignedBlob );
        return await CreateMessageAsync( app, source, requestHost, cancellationToken );
    }

    /// <summary>
    /// The transaction and its after-commit work for a message whose file, if any, is already staged: the body
    /// (assigned text, as the rich text attribute stores it) is canonicalized first, outside the lease.
    /// </summary>
    public static async Task<CreatedMessage> CreateMessageAsync(
        CampfireApp app,
        MessageSource source,
        string? requestHost,
        CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( app );
        ArgumentNullException.ThrowIfNull( source );

        string? body = null;
        if ( source.Body is { } assigned )
        {
            body = await app.Database.ReadAsync(
                db => RichTextPipeline.CanonicalHtml( assigned, app.RichText( db, requestHost ) ),
                cancellationToken );
        }

        var message = new NewMessage
        {
            RoomId = source.RoomId,
            CreatorId = source.CreatorId,
            ClientMessageId = source.ClientMessageId,
            Body = body,
            Attachment = source.Staged is null ? null : AttachmentAssignment.ToNewAttachment( source.Staged ),
            AttachedBlob = source.AttachedBlob,
        };

        return await app.Database.WriteAsync( scope => MessageOperations.Create( scope, message ), cancellationToken );
    }

    /// <summary>
    /// <c>Message#process_attachment</c>: <c>attachment&amp;.analyze</c>, then
    /// <c>attachment.preview(format: :webp).processed</c> for a video or <c>attachment.representation(:thumb).processed</c>
    /// for anything representable.
    /// </summary>
    public static async Task ProcessAttachmentAsync( CampfireApp app, CreatedMessage created, CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( app );
        ArgumentNullException.ThrowIfNull( created );

        if ( created.Blob is not { } row )
        {
            return;
        }

        var blob = await BlobOperations.AnalyzeAsync( app.Database, app.Storage, BlobRecords.ToBlob( row ), cancellationToken );
        if ( blob.IsVideo )
        {
            await app.Storage.ProcessPreviewAsync( blob, Variation.FormatOnly( "webp" ), cancellationToken );
        }
        else if ( await app.Storage.IsRepresentableAsync( blob ) )
        {
            var thumbnail = Variation.ResizeToLimit( MessagesHelper.ThumbnailMaxWidth, MessagesHelper.ThumbnailMaxHeight );
            await app.Storage.ProcessRepresentationAsync( blob, thumbnail, cancellationToken );
        }
    }

    /// <summary>
    /// <c>Message#broadcast_create</c>: the message as <c>ApplicationController.render</c> renders it (no request, the
    /// request's protocol and host including its port) appended to <c>[ room, :messages ]</c>, then unread notification work.
    /// Returns the <c>&lt;turbo-stream&gt;</c> sent.
    /// </summary>
    private static async Task<byte[]> BroadcastCreateAsync(
        CampfireApp app,
        ViewContexts views,
        MessageBroadcast broadcast,
        CancellationToken cancellationToken )
    {
        var (room, message, baseUrl, requestHost, created) = broadcast;
        var cache = views.CaptureBroadcastCache( baseUrl );
        var ffmpeg = await app.Storage.Tools.FfmpegExistsAsync( );
        var (broadcastContext, item) = await app.Database.ReadAsync( db =>
        {
            var broadcastContext = views.ForBroadcast( db, baseUrl, cache );
            var presenter = new MessagePresenter( app, db, requestHost, ffmpeg );
            if ( created is not null && ReferenceEquals( created.Message, message ) )
            {
                presenter.UseCreatedText( created );
            }

            var item = presenter.Item( broadcastContext, message );
            return (broadcastContext, item);
        }, cancellationToken );

        var html = await app.Broadcasts.MessageCreatedAsync( room, broadcastContext, item, cancellationToken );
        app.Jobs.BroadcastUnreadRoom.Enqueue( new BroadcastUnreadRoomJob( message.Id ) );
        return html;
    }

    /// <summary>
    /// <c>room.messages.create_with_attachment!(attachment:, creator:)</c> run outside a request, as a bot's webhook reply
    /// is: the create, <c>process_attachment</c>, then <c>broadcast_create</c> rendered by
    /// <c>ApplicationController.renderer</c> on its default host. Nothing takes a token after the commit.
    /// </summary>
    public static async Task<Message> CreateDetachedAsync(
        CampfireApp app,
        ViewContexts views,
        Room room,
        MessageSource source,
        CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( app );
        ArgumentNullException.ThrowIfNull( views );
        ArgumentNullException.ThrowIfNull( room );

        var created = await CreateMessageAsync( app, source, requestHost: null, cancellationToken );
        var committed = CancellationToken.None;
        await ProcessAttachmentAsync( app, created, committed );
        var broadcast = new MessageBroadcast( room, created.Message, app.Options.PublicOrigin ?? ViewContexts.DetachedBaseUrl, RequestHost: null, Created: null );
        await BroadcastCreateAsync( app, views, broadcast, committed );
        return created.Message;
    }

    /// <summary>What <c>broadcast_create</c> renders and where.</summary>
    /// <param name="Room">The room, whose stream and members get the message.</param>
    /// <param name="Message">The message.</param>
    /// <param name="BaseUrl">The protocol and host the rendered URLs use.</param>
    /// <param name="RequestHost"><c>Current.request_host</c>, which link previews in the body may not point back at.</param>
    /// <param name="Created">The create that made the message in this request, whose body text the presenter reuses.</param>
    private readonly record struct MessageBroadcast( Room Room, Message Message, string BaseUrl, string? RequestHost, CreatedMessage? Created );
}
