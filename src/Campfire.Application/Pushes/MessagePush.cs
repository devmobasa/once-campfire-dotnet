namespace Campfire.Application.Pushes;

/// <summary>A message's push: its payload, and the subscriptions it goes to (everything first, then mentions).</summary>
public sealed record MessagePush(
    PushPayload? Payload, IReadOnlyList<PushSubscription> Everything, IReadOnlyList<PushSubscription> Mentions );
