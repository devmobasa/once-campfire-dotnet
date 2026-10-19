namespace Campfire.Web.Workers;

/// <summary>The committed message whose unread room notice is published after its room append.</summary>
public sealed record BroadcastUnreadRoomJob( long MessageId );
