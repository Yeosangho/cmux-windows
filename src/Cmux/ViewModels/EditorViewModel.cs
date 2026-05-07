using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cmux.Core.Models;
using Cmux.Core.Services;

namespace Cmux.ViewModels;

/// <summary>
/// Workspace-level view model for the Editor view. Holds the list of root
/// folder nodes (one per <see cref="EditorFolder"/> on the workspace) and the
/// open file tabs.
/// </summary>
public partial class EditorViewModel : ObservableObject, IDisposable
{
    private readonly Workspace _workspace;
    private readonly OpenSshSftpService _remoteFs;

    [ObservableProperty]
    private ObservableCollection<EditorFolderNodeViewModel> _rootFolders = [];

    [ObservableProperty]
    private ObservableCollection<EditorOpenFileViewModel> _openFiles = [];

    [ObservableProperty]
    private EditorOpenFileViewModel? _activeFile;

    public EditorViewModel(Workspace workspace)
    {
        _workspace = workspace;
        _remoteFs = new OpenSshSftpService(secret => SecretStoreService.GetSecret(secret));

        foreach (var folder in workspace.EditorFolders)
            RootFolders.Add(BuildRootNode(folder));
    }

    public void AddFolder(EditorFolder folder)
    {
        _workspace.EditorFolders.Add(folder);
        RootFolders.Add(BuildRootNode(folder));
    }

    [RelayCommand]
    public void RemoveFolder(EditorFolderNodeViewModel? root)
    {
        if (root == null || !root.IsRoot) return;

        // Close any open files belonging to this folder.
        var toClose = OpenFiles.Where(f => f.Folder.Id == root.Folder.Id).ToList();
        foreach (var f in toClose) CloseFile(f);

        _remoteFs.Disconnect(root.Folder.Id);
        _workspace.EditorFolders.Remove(root.Folder);
        RootFolders.Remove(root);
    }

    [RelayCommand]
    public void RefreshFolder(EditorFolderNodeViewModel? root)
    {
        if (root == null) return;
        root.RefreshChildren();
    }

    public async Task OpenFileAsync(EditorFolderNodeViewModel node)
    {
        if (node.IsDirectory) return;

        var existing = OpenFiles.FirstOrDefault(f =>
            f.Folder.Id == node.Folder.Id &&
            string.Equals(f.FullPath, node.FullPath, StringComparison.Ordinal));
        if (existing != null)
        {
            ActiveFile = existing;
            return;
        }

        var file = new EditorOpenFileViewModel(node.Folder, node.FullPath, node.Name, _remoteFs);
        OpenFiles.Add(file);
        ActiveFile = file;
        await file.LoadAsync();
    }

    [RelayCommand]
    public void CloseFile(EditorOpenFileViewModel? file)
    {
        if (file == null) return;
        int idx = OpenFiles.IndexOf(file);
        OpenFiles.Remove(file);

        if (ActiveFile == file)
        {
            if (OpenFiles.Count == 0)
                ActiveFile = null;
            else
                ActiveFile = OpenFiles[Math.Min(idx, OpenFiles.Count - 1)];
        }
    }

    [RelayCommand]
    public async Task SaveActiveAsync()
    {
        if (ActiveFile == null) return;
        ActiveFile.LoadError = null;
        try
        {
            await ActiveFile.SaveAsync();
        }
        catch (Exception ex)
        {
            ActiveFile.LoadError = $"Save failed: {ex.Message}";
        }
    }

    private EditorFolderNodeViewModel BuildRootNode(EditorFolder folder)
    {
        var displayName = string.IsNullOrWhiteSpace(folder.DisplayName)
            ? (folder.Kind == EditorFolderKind.RemoteSsh
                ? (string.IsNullOrWhiteSpace(folder.Username)
                    ? $"{folder.Host}:{folder.Path}"
                    : $"{folder.Username}@{folder.Host}:{folder.Path}")
                : folder.Path)
            : folder.DisplayName;

        var fs = folder.Kind == EditorFolderKind.RemoteSsh ? _remoteFs : null;
        return new EditorFolderNodeViewModel(folder, fs, displayName, folder.Path, true, true);
    }

    public void Dispose()
    {
        _remoteFs.Dispose();
    }
}
