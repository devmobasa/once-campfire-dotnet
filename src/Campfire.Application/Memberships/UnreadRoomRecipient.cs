namespace Campfire.Application.Memberships;

/// <summary>A recipient and its stable membership cursor for bounded unread delivery.</summary>
public sealed record UnreadRoomRecipient( long MembershipId, long UserId );
