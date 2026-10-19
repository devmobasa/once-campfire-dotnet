namespace Campfire.Web.Workers;

/// <summary>The queue of every job kind, for producers.</summary>
public sealed class JobChannels(
    JobChannel<AnalyzeBlobJob> analyze,
    JobChannel<BroadcastUnreadRoomJob> broadcastUnreadRoom,
    JobChannel<PurgeBlobJob> purge,
    JobChannel<PushMessageJob> pushMessage,
    JobChannel<WebhookJob> webhook,
    JobChannel<RemoveBannedContentJob> removeBannedContent )
{
    public JobChannel<AnalyzeBlobJob> Analyze { get; } = analyze;

    public JobChannel<BroadcastUnreadRoomJob> BroadcastUnreadRoom { get; } = broadcastUnreadRoom;

    public JobChannel<PurgeBlobJob> Purge { get; } = purge;

    public JobChannel<PushMessageJob> PushMessage { get; } = pushMessage;

    public JobChannel<WebhookJob> Webhook { get; } = webhook;

    public JobChannel<RemoveBannedContentJob> RemoveBannedContent { get; } = removeBannedContent;
}
