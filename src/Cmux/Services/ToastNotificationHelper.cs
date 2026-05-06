using Cmux.Core.Models;
using Microsoft.Toolkit.Uwp.Notifications;

namespace Cmux.Services;

/// <summary>
/// Sends Windows toast notifications when AI coding agents need attention.
/// Uses the CommunityToolkit Notifications API. For unpackaged Win32 apps
/// the toolkit auto-registers an AumId, a Start Menu shortcut, and a COM
/// activator the first time a toast is shown — the only thing the host app
/// has to do is subscribe to <c>ToastNotificationManagerCompat.OnActivated</c>
/// (done in App.OnStartup) so click activations actually reach our code.
/// </summary>
public static class ToastNotificationHelper
{
    public static void ShowToast(TerminalNotification notification, string workspaceName)
    {
        try
        {
            var builder = new ToastContentBuilder()
                .AddText(notification.Title ?? string.Empty)
                .AddText(notification.Body ?? string.Empty)
                .AddAttributionText($"Workspace: {workspaceName}")
                // Activation arguments — read back in App's OnActivated handler
                // via ToastArguments.Parse and dispatched to MainViewModel.
                .AddArgument("action", "jumpToNotification")
                .AddArgument("notificationId", notification.Id)
                .AddArgument("workspaceId", notification.WorkspaceId)
                .AddArgument("surfaceId", notification.SurfaceId)
                // Explicit notification sound. Without an AddAudio call some
                // Windows configurations render the toast silently.
                .AddAudio(new System.Uri("ms-winsoundevent:Notification.Default"));
            if (!string.IsNullOrEmpty(notification.PaneId))
                builder.AddArgument("paneId", notification.PaneId);

            builder.Show();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Toast] ShowToast failed: {ex}");
        }
    }

    public static void ClearAll()
    {
        try
        {
            ToastNotificationManagerCompat.History.Clear();
        }
        catch
        {
            // Best effort
        }
    }
}
