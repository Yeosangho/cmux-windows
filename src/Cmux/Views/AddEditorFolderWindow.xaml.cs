using System.Windows;
using System.Windows.Controls;
using Cmux.Core.Models;
using Cmux.Core.Services;
using Microsoft.Win32;

namespace Cmux.Views;

public partial class AddEditorFolderWindow : Window
{
    public EditorFolder? Result { get; private set; }

    private const string ManualEntryLabel = "(Manual entry)";
    private bool _suppressSshConfigApply;

    public AddEditorFolderWindow()
    {
        InitializeComponent();
        LoadSshConfigEntries();
    }

    private void LoadSshConfigEntries()
    {
        var entries = SshConfigParser.ParseDefaultConfig();
        _suppressSshConfigApply = true;
        try
        {
            SshConfigCombo.Items.Clear();
            SshConfigCombo.Items.Add(new ComboBoxItem { Content = ManualEntryLabel, Tag = null });
            foreach (var entry in entries)
            {
                var label = string.IsNullOrEmpty(entry.HostName)
                    ? entry.Alias
                    : $"{entry.Alias}  ({entry.HostName})";
                SshConfigCombo.Items.Add(new ComboBoxItem { Content = label, Tag = entry });
            }
            SshConfigCombo.SelectedIndex = 0;
        }
        finally
        {
            _suppressSshConfigApply = false;
        }
    }

    private void SshConfigCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSshConfigApply) return;
        if (SshConfigCombo.SelectedItem is not ComboBoxItem item) return;

        if (item.Tag is SshConfigEntry entry)
        {
            // Alias mode: hand the bare alias to sftp.exe and let it resolve
            // user / port / identity from ~/.ssh/config. Disable the manual
            // routing fields, but KEEP PasswordBox enabled — the host may
            // require password auth or have a passphrase-protected key, both
            // of which we deliver via SSH_ASKPASS.
            HostBox.Text = entry.Alias;
            PortBox.Text = "";
            UsernameBox.Text = "";
            KeyPathBox.Text = "";
            SetManualFieldsEnabled(false);
            SshConfigInfoText.Visibility = Visibility.Visible;
        }
        else
        {
            HostBox.Text = "";
            PortBox.Text = "22";
            UsernameBox.Text = "";
            KeyPathBox.Text = "";
            SetManualFieldsEnabled(true);
            SshConfigInfoText.Visibility = Visibility.Collapsed;
        }
    }

    private void SetManualFieldsEnabled(bool enabled)
    {
        PortBox.IsEnabled = enabled;
        UsernameBox.IsEnabled = enabled;
        KeyPathBox.IsEnabled = enabled;
        PasswordAuthRadio.IsEnabled = enabled;
        KeyAuthRadio.IsEnabled = enabled;
        // PasswordBox stays usable in both modes so alias hosts that need a
        // password or key passphrase can still authenticate via SSH_ASKPASS.
        PasswordBox.IsEnabled = true;
    }

    private void KindChanged(object sender, RoutedEventArgs e)
    {
        if (LocalForm == null || RemoteForm == null) return;
        bool isLocal = LocalRadio.IsChecked == true;
        LocalForm.Visibility = isLocal ? Visibility.Visible : Visibility.Collapsed;
        RemoteForm.Visibility = isLocal ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BrowseLocal_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Select folder",
        };
        if (dlg.ShowDialog(this) == true)
        {
            LocalPathBox.Text = dlg.FolderName;
        }
    }

    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select private key file",
        };
        if (dlg.ShowDialog(this) == true)
        {
            KeyPathBox.Text = dlg.FileName;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (LocalRadio.IsChecked == true)
        {
            var path = LocalPathBox.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(path))
            {
                MessageBox.Show(this, "Please enter a folder path.", "Add Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Result = new EditorFolder
            {
                Kind = EditorFolderKind.Local,
                Path = path,
                DisplayName = LocalDisplayNameBox.Text?.Trim() ?? "",
            };
        }
        else
        {
            bool useSshConfig =
                SshConfigCombo.SelectedItem is ComboBoxItem item &&
                item.Tag is SshConfigEntry;

            var host = HostBox.Text?.Trim() ?? "";
            var user = UsernameBox.Text?.Trim() ?? "";
            var remotePath = RemotePathBox.Text?.Trim() ?? "/";

            if (string.IsNullOrWhiteSpace(host))
            {
                MessageBox.Show(this, "Please enter a host (or pick one from SSH config).", "Add Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!useSshConfig && string.IsNullOrWhiteSpace(user))
            {
                MessageBox.Show(this, "Please enter a username (or pick a host from SSH config).", "Add Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int port = 22;
            if (!useSshConfig)
            {
                int.TryParse(PortBox.Text?.Trim() ?? "22", out port);
                if (port <= 0) port = 22;
            }

            var folder = new EditorFolder
            {
                Kind = EditorFolderKind.RemoteSsh,
                Host = host,
                Port = port,
                Username = useSshConfig ? null : user,
                Path = remotePath,
                DisplayName = RemoteDisplayNameBox.Text?.Trim() ?? "",
                UsePasswordAuth = !useSshConfig && PasswordAuthRadio.IsChecked == true,
                PrivateKeyPath = useSshConfig || string.IsNullOrWhiteSpace(KeyPathBox.Text)
                    ? null
                    : KeyPathBox.Text.Trim(),
                UseSshConfig = useSshConfig,
            };

            // Persist password / key passphrase encrypted via SecretStoreService.
            // Saved in both alias and manual modes — SSH_ASKPASS feeds it to
            // sftp.exe whenever OpenSSH prompts (password auth, encrypted key
            // passphrase, etc).
            var secret = PasswordBox.Password ?? "";
            if (!string.IsNullOrEmpty(secret))
            {
                // Single secret slot — used as either password or passphrase
                // depending on what OpenSSH asks for.
                SecretStoreService.SetSecret($"editor-folder:{folder.Id}:password", secret);
            }

            Result = folder;
        }

        DialogResult = true;
        Close();
    }
}
