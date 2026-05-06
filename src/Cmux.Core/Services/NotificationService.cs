using System.Collections.ObjectModel;
using Cmux.Core.Models;

namespace Cmux.Core.Services;

/// <summary>
/// Manages terminal notifications. Tracks unread state, provides
/// jump-to-unread functionality, and fires Windows toast notifications.
/// </summary>
public class NotificationService
{
    private readonly ObservableCollection<TerminalNotification> _notifications = [];
    private readonly object _lock = new();

    public ObservableCollection<TerminalNotification> Notifications => _notifications;
    public int UnreadCount => _notifications.Count(n => !n.IsRead);

    public event Action<TerminalNotification>? NotificationAdded;
    public event Action? UnreadCountChanged;

    /// <summary>
    /// Marshal callback used to dispatch ObservableCollection mutations and
    /// event firing onto the UI thread. PTY output (which generates OSC
    /// notifications) is processed on a background thread, and a ListBox
    /// bound to <see cref="Notifications"/> throws ItemContainerGenerator
    /// "Verify" errors when items are inserted from the wrong thread.
    /// App.xaml.cs sets this to a Dispatcher.BeginInvoke wrapper at startup.
    /// </summary>
    public Action<Action>? UIMarshal { get; set; }

    /// <summary>
    /// Adds a new notification.
    /// <para>
    /// <paramref name="senderId"/> and <paramref name="senderTimestamp"/> come
    /// from the OSC 99 <c>i=</c> / <c>ts=</c> fields when the source uses
    /// them. <paramref name="senderId"/> is composed with <paramref name="paneId"/>
    /// to scope deduplication: the same id retransmitted from the same pane
    /// is dropped (so hooks can resend without spamming), while the same id
    /// from a different pane is treated as a separate notification.
    /// </para>
    /// </summary>
    public void AddNotification(
        string workspaceId,
        string surfaceId,
        string? paneId,
        string title,
        string? subtitle,
        string body,
        NotificationSource source,
        string? senderId = null,
        DateTime? senderTimestamp = null)
    {
        // Composite dedup key: pane + sender id. Without paneId scoping, two
        // panes legitimately emitting the same id (e.g. an `Stop` hook that
        // uses `$$` PID — process ids reset per-shell) would clobber each other.
        string? dedupKey = senderId == null
            ? null
            : $"{paneId ?? string.Empty}\0{senderId}";

        TerminalNotification notification;
        lock (_lock)
        {
            if (dedupKey != null && _notifications.Any(n => n.DedupKey == dedupKey))
                return;

            notification = new TerminalNotification
            {
                WorkspaceId = workspaceId,
                SurfaceId = surfaceId,
                PaneId = paneId,
                Title = title,
                Subtitle = subtitle,
                Body = body,
                Source = source,
                IsRead = false,
                Timestamp = senderTimestamp ?? DateTime.UtcNow,
                DedupKey = dedupKey,
            };
        }

        // Apply mutation + events on the UI thread when a marshal callback is
        // wired up. Otherwise WPF's ListBox.ItemContainerGenerator throws
        // cross-thread Verify errors the second time PTY-driven notifications
        // arrive while the panel is showing earlier items.
        void Apply()
        {
            lock (_lock)
            {
                _notifications.Insert(0, notification);
                while (_notifications.Count > 500)
                    _notifications.RemoveAt(_notifications.Count - 1);
            }
            NotificationAdded?.Invoke(notification);
            UnreadCountChanged?.Invoke();
        }

        if (UIMarshal != null) UIMarshal(Apply);
        else Apply();
    }

    /// <summary>
    /// Marks a notification as read.
    /// </summary>
    public void MarkAsRead(string notificationId)
    {
        lock (_lock)
        {
            var notification = _notifications.FirstOrDefault(n => n.Id == notificationId);
            if (notification != null)
            {
                notification.IsRead = true;
                UnreadCountChanged?.Invoke();
            }
        }
    }

    /// <summary>
    /// Marks all notifications for a workspace as read.
    /// </summary>
    public void MarkWorkspaceAsRead(string workspaceId)
    {
        lock (_lock)
        {
            foreach (var n in _notifications.Where(n => n.WorkspaceId == workspaceId && !n.IsRead))
                n.IsRead = true;
        }
        UnreadCountChanged?.Invoke();
    }

    /// <summary>
    /// Marks all notifications as read.
    /// </summary>
    public void MarkAllAsRead()
    {
        lock (_lock)
        {
            foreach (var n in _notifications.Where(n => !n.IsRead))
                n.IsRead = true;
        }
        UnreadCountChanged?.Invoke();
    }

    /// <summary>
    /// Gets the most recent unread notification.
    /// </summary>
    public TerminalNotification? GetLatestUnread()
    {
        lock (_lock)
        {
            return _notifications.FirstOrDefault(n => !n.IsRead);
        }
    }

    /// <summary>
    /// Gets unread count for a specific workspace.
    /// </summary>
    public int GetUnreadCount(string workspaceId)
    {
        lock (_lock)
        {
            return _notifications.Count(n => n.WorkspaceId == workspaceId && !n.IsRead);
        }
    }

    /// <summary>
    /// Gets the latest notification text for a workspace (for sidebar display).
    /// </summary>
    public string? GetLatestText(string workspaceId)
    {
        lock (_lock)
        {
            var latest = _notifications.FirstOrDefault(n => n.WorkspaceId == workspaceId);
            return latest?.Body;
        }
    }

    /// <summary>
    /// Clears all notifications.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _notifications.Clear();
        }
        UnreadCountChanged?.Invoke();
    }
}
