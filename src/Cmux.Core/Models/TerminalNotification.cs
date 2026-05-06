namespace Cmux.Core.Models;

public enum NotificationSource
{
    Osc9,
    Osc99,
    Osc777,
    Cli,
}

public record TerminalNotification
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public required string WorkspaceId { get; init; }
    public required string SurfaceId { get; init; }
    public string? PaneId { get; init; }
    public bool IsRead { get; set; }
    public required string Title { get; init; }
    public string? Subtitle { get; init; }
    public required string Body { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    /// <summary>
    /// Convenience accessor that converts the UTC <see cref="Timestamp"/> to
    /// the user's system timezone for UI display. Bound from XAML so panel
    /// rows show local-clock time (e.g. KST for Korean users) instead of
    /// the raw UTC stored value.
    /// </summary>
    public DateTime LocalTimestamp => Timestamp.Kind switch
    {
        DateTimeKind.Utc => Timestamp.ToLocalTime(),
        DateTimeKind.Local => Timestamp,
        _ => DateTime.SpecifyKind(Timestamp, DateTimeKind.Utc).ToLocalTime(),
    };
    public NotificationSource Source { get; init; }
    /// <summary>
    /// Composite key (paneId + sender-supplied OSC 99 <c>i=</c> value) used by
    /// NotificationService to drop duplicate retransmits. Null when the sender
    /// didn't supply an id.
    /// </summary>
    public string? DedupKey { get; init; }
}

public record AppNotification
{
    public required string WorkspaceName { get; init; }
    public required string SurfaceName { get; init; }
    public required TerminalNotification Notification { get; init; }
}
