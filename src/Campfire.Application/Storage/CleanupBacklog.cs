namespace Campfire.Application.Storage;

/// <summary>A durable queue snapshot. Retry scheduling never changes its oldest enqueue time.</summary>
public sealed class CleanupBacklog
{
    public string Queue { get; set; } = "";
    public long PendingCount { get; set; }
    public DateTime? OldestCreatedAt { get; set; }
    public DateTime? OldestDueAt { get; set; }
}
