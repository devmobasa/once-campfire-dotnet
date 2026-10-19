namespace Campfire.Application.Rooms;

/// <summary>A single consumer's bounded keyset position. Restarting begins another complete rotation.</summary>
public sealed class RoomDeletionCursor
{
    internal long AfterRoomId { get; set; }
}
