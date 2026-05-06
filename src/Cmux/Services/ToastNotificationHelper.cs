using System.IO;
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

            // Diagnostic — append the body we hand to the toolkit so we can
            // compare emitted vs displayed if a bug reappears.
            try
            {
                var logPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "cmuxw-toast.log");
                File.AppendAllText(logPath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] ShowToast id={notification.Id} title='{notification.Title}' body='{notification.Body}'\n",
                    System.Text.Encoding.UTF8);
            }
            catch { }

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
