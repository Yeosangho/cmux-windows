using System.Windows.Controls;
using System.Windows.Input;
using Cmux.ViewModels;

namespace Cmux.Controls;

public partial class BroadcastInputBar : UserControl
{
    public BroadcastInputBar()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        // Re-pull the available pane list so the popup reflects the workspace
        // it was opened against, in case the user toggled the bar from a
        // different workspace than last time.
        if (e.NewValue is BroadcastInputViewModel vm)
            vm.RefreshAvailablePanes();
    }

    private void OnIsVisibleChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        // Move keyboard focus into the input box on show so the user can
        // start typing immediately after toggling the bar (Ctrl+Shift+B).
        if (IsVisible)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                InputBox.Focus();
                Keyboard.Focus(InputBox);
            }), System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Enter (no modifiers) → submit. Shift+Enter / Ctrl+Enter → let
        // the TextBox insert a literal newline (AcceptsReturn=true). The
        // textbox is multi-line so users can compose a multi-line prompt
        // and fire it once, sidestepping per-keystroke PTY echo lag for
        // the active pane.
        if (e.Key == Key.Enter)
        {
            var mods = Keyboard.Modifiers;
            bool insertNewline = (mods & (ModifierKeys.Shift | ModifierKeys.Control)) != 0;
            if (!insertNewline)
            {
                if (DataContext is BroadcastInputViewModel vm && vm.SubmitCommand.CanExecute(null))
                    vm.SubmitCommand.Execute(null);
                e.Handled = true;
            }
            // else: TextBox handles Shift+Enter / Ctrl+Enter as a newline
        }
        else if (e.Key == Key.Escape)
        {
            if (DataContext is BroadcastInputViewModel vm)
                vm.HideCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void PresetItem_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        // The DataContext for an ItemsControl-bound Button is the row's
        // BroadcastPreset; pull it via Tag (we set Tag={Binding}) so the
        // VM doesn't need to infer the row.
        if (sender is System.Windows.FrameworkElement { Tag: BroadcastPreset preset }
            && DataContext is BroadcastInputViewModel vm)
        {
            vm.ApplyPresetCommand.Execute(preset);

            // Close the popup so focus returns to the input. Walk up to
            // find the Popup ancestor since the button lives inside the
            // popup's content tree.
            var ancestor = sender as System.Windows.DependencyObject;
            while (ancestor != null)
            {
                if (ancestor is System.Windows.Controls.Primitives.Popup popup)
                {
                    popup.IsOpen = false;
                    break;
                }
                ancestor = System.Windows.Media.VisualTreeHelper.GetParent(ancestor);
            }
            // Popup uses a separate visual tree — fall through to the
            // ToggleButton next to it: setting PresetToggle.IsChecked =
            // false also closes the popup if VisualTreeHelper.GetParent
            // returned null (popup content is logical-tree only).
            PresetToggle.IsChecked = false;

            InputBox.Focus();
            Keyboard.Focus(InputBox);
        }
    }
}
