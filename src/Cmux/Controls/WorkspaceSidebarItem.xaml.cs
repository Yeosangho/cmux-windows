using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Cmux.ViewModels;
using Cmux.Views;

namespace Cmux.Controls;

public partial class WorkspaceSidebarItem : UserControl
{
    private ListBoxItem? _hostItem;

    public WorkspaceSidebarItem()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private WorkspaceViewModel? Vm => DataContext as WorkspaceViewModel;
    private MainViewModel? MainVm => FindMainViewModel();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        DetachFromHost();
        _hostItem = FindAncestor<ListBoxItem>(this);
        if (_hostItem != null)
        {
            _hostItem.Selected += OnHostSelected;
            _hostItem.Unselected += OnHostUnselected;
            UpdateFolderTreeVisibility(_hostItem.IsSelected);
        }
        else
        {
            UpdateFolderTreeVisibility(false);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => DetachFromHost();

    private void DetachFromHost()
    {
        if (_hostItem != null)
        {
            _hostItem.Selected -= OnHostSelected;
            _hostItem.Unselected -= OnHostUnselected;
            _hostItem = null;
        }
    }

    private void OnHostSelected(object sender, RoutedEventArgs e) => UpdateFolderTreeVisibility(true);
    private void OnHostUnselected(object sender, RoutedEventArgs e) => UpdateFolderTreeVisibility(false);

    private void UpdateFolderTreeVisibility(bool selected)
    {
        FolderTreeHost.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
    }

    private static T? FindAncestor<T>(DependencyObject? start) where T : DependencyObject
    {
        var current = start;
        while (current != null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void Rename_Click(object sender, RoutedEventArgs e) => StartRename();

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (MainVm is { } main && Vm is { } ws)
        {
            main.DuplicateWorkspace(ws);
        }
    }

    private void NewSurface_Click(object sender, RoutedEventArgs e) => Vm?.CreateNewSurface();

    private void SetIcon_Click(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;

        var prompt = new TextPromptWindow(
            title: "Workspace Icon",
            message: "Enter a single icon (emoji/symbol) or a glyph code like E8A5, U+E8A5, 0xE8A5.",
            defaultValue: Vm.IconGlyph)
        {
            Owner = Window.GetWindow(this),
        };

        if (prompt.ShowDialog() != true)
            return;

        var input = prompt.ResponseText;
        if (string.IsNullOrWhiteSpace(input))
            return;

        var value = input.Trim();

        if (value.StartsWith("<svg", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("SVG is not supported in workspace icon yet. Use emoji/symbol or MDL2 hex code.",
                "Workspace Icon", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (TryParseHexGlyph(value, out var glyph))
            Vm.IconGlyph = glyph;
        else
            Vm.IconGlyph = value;
    }

    private void SetColor_Click(object sender, RoutedEventArgs e)
    {
        if (Vm == null || sender is not MenuItem item || item.Tag is not string color)
            return;

        Vm.AccentColor = color;
    }

    private void SetCustomColor_Click(object sender, RoutedEventArgs e)
    {
        if (Vm == null)
            return;

        var picker = new ColorPickerWindow(Vm.AccentColor)
        {
            Owner = Window.GetWindow(this),
        };

        if (picker.ShowDialog() == true && !string.IsNullOrWhiteSpace(picker.SelectedHex))
            Vm.AccentColor = picker.SelectedHex;
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (MainVm is { } main && Vm is { } ws)
        {
            int idx = main.Workspaces.IndexOf(ws);
            if (idx > 0) main.Workspaces.Move(idx, idx - 1);
        }
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (MainVm is { } main && Vm is { } ws)
        {
            int idx = main.Workspaces.IndexOf(ws);
            if (idx >= 0 && idx < main.Workspaces.Count - 1)
                main.Workspaces.Move(idx, idx + 1);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (MainVm is { } main && Vm is { } ws)
            main.CloseWorkspace(ws);
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var dlg = new AddEditorFolderWindow
        {
            Owner = Window.GetWindow(this),
        };
        if (dlg.ShowDialog() == true && dlg.Result != null)
        {
            Vm.Editor.AddFolder(dlg.Result);
        }
    }

    private void FolderTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm == null) return;
        if (sender is not TreeView tree) return;
        if (tree.SelectedItem is not EditorFolderNodeViewModel node) return;
        if (node.IsPlaceholder) return;

        // Default editor: Cursor. Cursor reuses an already-open window for the
        // same folder + brings it to the foreground, so no window-tracking
        // needed on our side.
        OpenInEditor(node, Cmux.Core.Services.CursorLauncher.CursorCommand);
    }

    private void OpenInEditor(EditorFolderNodeViewModel node, string editorCommand)
    {
        try
        {
            Cmux.Core.Services.CursorLauncher.OpenPath(node.Folder, node.FullPath, editorCommand);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                ex.Message,
                "Open in editor failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OpenInCursor_Click(object sender, RoutedEventArgs e)
    {
        if (FolderTree.SelectedItem is EditorFolderNodeViewModel node && !node.IsPlaceholder)
            OpenInEditor(node, Cmux.Core.Services.CursorLauncher.CursorCommand);
    }

    private void OpenInVsCode_Click(object sender, RoutedEventArgs e)
    {
        if (FolderTree.SelectedItem is EditorFolderNodeViewModel node && !node.IsPlaceholder)
            OpenInEditor(node, Cmux.Core.Services.CursorLauncher.VsCodeCommand);
    }

    private void RefreshFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        if (FolderTree.SelectedItem is EditorFolderNodeViewModel node)
            Vm.Editor.RefreshFolder(node);
    }

    private void RemoveRootFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        if (FolderTree.SelectedItem is not EditorFolderNodeViewModel node) return;

        // Right-clicking inside a child node (or an "<error: ...>" placeholder
        // produced by a failed remote load) leaves SelectedItem as that
        // descendant, not the root. Walk up via folder Id so the menu item
        // still works in those cases.
        var root = node.IsRoot
            ? node
            : Vm.Editor.RootFolders.FirstOrDefault(r => r.Folder.Id == node.Folder.Id);
        if (root == null) return;

        var result = MessageBox.Show(
            Window.GetWindow(this),
            $"Remove '{root.Name}' from this workspace?",
            "Remove Folder",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (result == MessageBoxResult.OK)
            Vm.Editor.RemoveFolder(root);
    }

    private void StartRename()
    {
        NameDisplay.Visibility = Visibility.Collapsed;
        NameEditor.Visibility = Visibility.Visible;
        NameEditor.SelectAll();
        NameEditor.Focus();
    }

    private void FinishRename()
    {
        NameEditor.Visibility = Visibility.Collapsed;
        NameDisplay.Visibility = Visibility.Visible;
    }

    private void NameDisplay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            StartRename();
            e.Handled = true;
        }
    }

    private void NameEditor_LostFocus(object sender, RoutedEventArgs e) => FinishRename();

    private void NameEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Escape)
        {
            if (e.Key == Key.Escape && Vm != null)
                NameEditor.Text = Vm.Name; // revert
            FinishRename();
            e.Handled = true;
        }
    }

    private static bool TryParseHexGlyph(string input, out string glyph)
    {
        glyph = string.Empty;

        var normalized = input.Trim();
        if (normalized.StartsWith("U+", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[2..];
        else if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[2..];

        if (!uint.TryParse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codePoint))
            return false;

        if (codePoint > 0x10FFFF)
            return false;

        glyph = char.ConvertFromUtf32((int)codePoint);
        return true;
    }

    private MainViewModel? FindMainViewModel()
    {
        var window = Window.GetWindow(this);
        return window?.DataContext as MainViewModel;
    }
}
