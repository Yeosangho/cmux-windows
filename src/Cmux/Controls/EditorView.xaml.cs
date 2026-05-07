using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Cmux.ViewModels;

namespace Cmux.Controls;

public partial class EditorView : UserControl
{
    private EditorViewModel? _vm;

    public EditorView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null)
            _vm.PropertyChanged -= OnVmPropertyChanged;

        _vm = DataContext as EditorViewModel;

        if (_vm != null)
            _vm.PropertyChanged += OnVmPropertyChanged;

        UpdateEditorBinding();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorViewModel.ActiveFile))
            UpdateEditorBinding();
    }

    private void UpdateEditorBinding()
    {
        if (_vm?.ActiveFile is { } file)
        {
            Editor.Document = file.Document;
            // file.Highlighting was already passed through PatchForDarkBackground
            // (EditorOpenFileViewModel ctor), so near-black tokens read as light
            // grey and mid-tones get lifted while preserving their hue.
            Editor.SyntaxHighlighting = file.Highlighting;
        }
        else
        {
            Editor.Document = new ICSharpCode.AvalonEdit.Document.TextDocument();
            Editor.SyntaxHighlighting = null;
        }
    }

    private void FileTab_Click(object sender, MouseButtonEventArgs e)
    {
        if (_vm == null) return;
        if (sender is FrameworkElement fe && fe.DataContext is EditorOpenFileViewModel file)
        {
            _vm.ActiveFile = file;
        }
    }

    private async void CloseFile_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        if (sender is Button btn && btn.Tag is EditorOpenFileViewModel file)
        {
            if (file.IsDirty)
            {
                var owner = Window.GetWindow(this);
                var result = MessageBox.Show(
                    owner,
                    $"Do you want to save changes to {file.Name}?\n\nYour changes will be lost if you don't save them.",
                    "Unsaved changes",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Warning);
                if (result == MessageBoxResult.Cancel) return;
                if (result == MessageBoxResult.Yes)
                {
                    _vm.ActiveFile = file;
                    await _vm.SaveActiveAsync();
                    if (file.LoadError != null)
                    {
                        MessageBox.Show(owner,
                            $"Save failed: {file.LoadError}",
                            "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                }
            }
            _vm.CloseFile(file);
        }
    }

    private async void SaveActive_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        var file = _vm.ActiveFile;
        await _vm.SaveActiveAsync();
        if (file != null && file.LoadError != null)
        {
            MessageBox.Show(Window.GetWindow(this),
                $"Save failed: {file.LoadError}",
                "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm == null) return;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        if (ctrl && e.Key == Key.S)
        {
            e.Handled = true;
            var file = _vm.ActiveFile;
            await _vm.SaveActiveAsync();
            if (file != null && file.LoadError != null)
            {
                MessageBox.Show(Window.GetWindow(this),
                    $"Save failed: {file.LoadError}",
                    "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
