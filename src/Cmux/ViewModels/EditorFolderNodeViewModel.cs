using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Cmux.Core.Models;
using Cmux.Core.Services;

namespace Cmux.ViewModels;

/// <summary>
/// One entry in the Editor file tree. Represents either the folder root,
/// a sub-directory, or a file. Children of directories are loaded lazily.
/// </summary>
public partial class EditorFolderNodeViewModel : ObservableObject
{
    private static readonly EditorFolderNodeViewModel _placeholder = new()
    {
        Name = "Loading...",
        IsPlaceholder = true,
    };

    public EditorFolder Folder { get; init; } = null!;

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _fullPath = "";

    [ObservableProperty]
    private bool _isDirectory;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isPlaceholder;

    public bool IsRoot { get; init; }

    public ObservableCollection<EditorFolderNodeViewModel> Children { get; } = [];

    private bool _childrenLoaded;
    private readonly OpenSshSftpService? _remoteFs;

    public EditorFolderNodeViewModel() { }

    public EditorFolderNodeViewModel(EditorFolder folder, OpenSshSftpService? remoteFs, string name, string fullPath, bool isDirectory, bool isRoot)
    {
        Folder = folder;
        _remoteFs = remoteFs;
        _name = name;
        _fullPath = fullPath;
        _isDirectory = isDirectory;
        IsRoot = isRoot;

        if (isDirectory)
            Children.Add(_placeholder);
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (!value || !IsDirectory || _childrenLoaded) return;
        _ = LoadChildrenAsync();
    }

    public async Task LoadChildrenAsync()
    {
        if (_childrenLoaded) return;
        if (!IsDirectory) return;

        IsLoading = true;
        try
        {
            var loaded = await Task.Run(() => LoadChildrenSync());
            Children.Clear();
            foreach (var child in loaded)
                Children.Add(child);
            _childrenLoaded = true;
        }
        catch (Exception ex)
        {
            Children.Clear();
            Children.Add(new EditorFolderNodeViewModel
            {
                Name = $"<error: {ex.Message}>",
                IsPlaceholder = true,
            });
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void RefreshChildren()
    {
        _childrenLoaded = false;
        Children.Clear();
        Children.Add(_placeholder);
        if (IsExpanded)
            _ = LoadChildrenAsync();
    }

    private List<EditorFolderNodeViewModel> LoadChildrenSync()
    {
        var result = new List<EditorFolderNodeViewModel>();

        if (Folder.Kind == EditorFolderKind.Local)
        {
            if (!Directory.Exists(FullPath)) return result;

            foreach (var dir in Directory.EnumerateDirectories(FullPath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(new EditorFolderNodeViewModel(Folder, _remoteFs, Path.GetFileName(dir), dir, true, false));
            }
            foreach (var file in Directory.EnumerateFiles(FullPath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(new EditorFolderNodeViewModel(Folder, _remoteFs, Path.GetFileName(file), file, false, false));
            }
        }
        else if (Folder.Kind == EditorFolderKind.RemoteSsh && _remoteFs != null)
        {
            foreach (var entry in _remoteFs.ListDirectory(Folder, FullPath))
            {
                result.Add(new EditorFolderNodeViewModel(Folder, _remoteFs, entry.Name, entry.FullPath, entry.IsDirectory, false));
            }
        }

        return result;
    }
}
