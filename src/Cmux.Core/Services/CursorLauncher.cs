using System.Diagnostics;
using Cmux.Core.Models;

namespace Cmux.Core.Services;

/// <summary>
/// Opens an EditorFolder in Cursor (or VSCode — both share the same CLI).
/// Cursor reuses an existing window when invoked with a folder it already has
/// open, so callers don't need to track window state — just launch.
/// </summary>
public static class CursorLauncher
{
    public const string CursorCommand = "cursor";
    public const string VsCodeCommand = "code";

    /// <summary>
    /// Opens the folder root in the specified editor. Convenience wrapper that
    /// passes the folder's own Path. For sub-directory / file targets use
    /// <see cref="OpenPath"/> instead.
    /// </summary>
    public static void OpenFolder(EditorFolder folder, string editorCommand)
        => OpenPath(folder, folder.Path, editorCommand);

    /// <summary>
    /// Opens an arbitrary path inside the folder's transport (local or
    /// Remote-SSH) in the specified editor. Cursor/VSCode will reuse an
    /// existing window if it already has the same folder open and bring it
    /// to the foreground, so the caller does not need to track window state.
    /// </summary>
    public static void OpenPath(EditorFolder folder, string path, string editorCommand)
    {
        if (string.IsNullOrWhiteSpace(editorCommand))
            editorCommand = CursorCommand;

        var args = BuildArgs(folder, path);
        var psi = new ProcessStartInfo
        {
            FileName = editorCommand,
            UseShellExecute = true,    // resolves .cmd shims on PATH
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var _ = Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to launch '{editorCommand}'. Make sure Cursor/VSCode's CLI is " +
                "installed and on PATH (Cmd Palette → 'Shell Command: Install \"cursor\"/\"code\" command').",
                ex);
        }
    }

    private static List<string> BuildArgs(EditorFolder folder, string? requested = null)
    {
        var args = new List<string>();
        var path = string.IsNullOrWhiteSpace(requested) ? folder.Path : requested;
        if (string.IsNullOrWhiteSpace(path)) path = ".";

        // Force a new window. Without --new-window the editor's
        // `window.openFoldersInNewWindow` setting decides; many users have it
        // set to "off" so different folders end up replacing the same window
        // instead of opening alongside. With --new-window the user gets one
        // Cursor window per cmuxw folder click, which matches the
        // "Cursor-as-tabs-of-cmuxw" mental model.
        args.Add("--new-window");

        if (folder.Kind == EditorFolderKind.RemoteSsh)
        {
            // VSCode/Cursor Remote-SSH expects --remote ssh-remote+<target>.
            // Same target syntax sftp.exe takes: bare alias for ssh_config
            // entries, otherwise user@host.
            string target;
            if (folder.UseSshConfig || string.IsNullOrWhiteSpace(folder.Username))
                target = folder.Host ?? "";
            else
                target = $"{folder.Username}@{folder.Host}";

            args.Add("--remote");
            args.Add($"ssh-remote+{target}");
            args.Add(path);
        }
        else
        {
            args.Add(path);
        }
        return args;
    }
}
