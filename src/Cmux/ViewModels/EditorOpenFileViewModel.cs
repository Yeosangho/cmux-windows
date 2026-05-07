using CommunityToolkit.Mvvm.ComponentModel;
using Cmux.Core.Models;
using Cmux.Core.Services;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using System.Windows.Media;

namespace Cmux.ViewModels;

/// <summary>
/// One open file in the Editor view. Owns its AvalonEdit
/// <see cref="TextDocument"/> so the editor control rebinds cleanly when the
/// user switches between open file tabs.
/// </summary>
public partial class EditorOpenFileViewModel : ObservableObject
{
    public EditorFolder Folder { get; }
    public string FullPath { get; }
    public string Name { get; }

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _loadError;

    [ObservableProperty]
    private TextDocument _document = new();

    [ObservableProperty]
    private IHighlightingDefinition? _highlighting;

    private readonly OpenSshSftpService? _remoteFs;
    private bool _suppressDirty;

    public EditorOpenFileViewModel(EditorFolder folder, string fullPath, string name, OpenSshSftpService? remoteFs)
    {
        Folder = folder;
        FullPath = fullPath;
        Name = name;
        _remoteFs = remoteFs;

        Highlighting = PatchForDarkBackground(
            ResolveHighlightingForExtension(System.IO.Path.GetExtension(fullPath)));
        Document.TextChanged += (_, _) =>
        {
            if (!_suppressDirty)
                IsDirty = true;
        };
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        LoadError = null;
        try
        {
            var text = await Task.Run(() => ReadText());
            _suppressDirty = true;
            Document.Text = text;
            _suppressDirty = false;
            IsDirty = false;
        }
        catch (Exception ex)
        {
            LoadError = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task SaveAsync()
    {
        var text = Document.Text;
        await Task.Run(() => WriteText(text));
        IsDirty = false;
    }

    private string ReadText()
    {
        if (Folder.Kind == EditorFolderKind.Local)
            return System.IO.File.ReadAllText(FullPath);

        if (Folder.Kind == EditorFolderKind.RemoteSsh && _remoteFs != null)
            return _remoteFs.ReadAllText(Folder, FullPath);

        return string.Empty;
    }

    private void WriteText(string text)
    {
        if (Folder.Kind == EditorFolderKind.Local)
        {
            System.IO.File.WriteAllText(FullPath, text);
            return;
        }

        if (Folder.Kind == EditorFolderKind.RemoteSsh && _remoteFs != null)
        {
            _remoteFs.WriteAllText(Folder, FullPath, text);
        }
    }

    /// <summary>
    /// Maps a file extension to one of AvalonEdit's built-in highlighting
    /// definitions. Falls back to null (plain text) for unknown extensions.
    /// </summary>
    private static IHighlightingDefinition? ResolveHighlightingForExtension(string? extension)
    {
        if (string.IsNullOrEmpty(extension)) return null;
        var ext = extension.ToLowerInvariant();

        // AvalonEdit ships with C#, C++, Java, JS, HTML, XML, XAML, Python, PHP, etc.
        // Map common extensions to its named definitions; fall back to extension lookup.
        var name = ext switch
        {
            ".cs" => "C#",
            ".c" or ".h" => "C++",
            ".cpp" or ".cxx" or ".cc" or ".hpp" or ".hh" => "C++",
            ".java" => "Java",
            ".js" or ".mjs" or ".cjs" => "JavaScript",
            ".ts" or ".tsx" => "JavaScript",
            ".html" or ".htm" => "HTML",
            ".xml" or ".xaml" or ".csproj" or ".props" or ".targets" => "XML",
            ".json" => "Json",
            ".py" => "Python",
            ".php" => "PHP",
            ".sql" => "TSQL",
            ".css" => "CSS",
            ".md" or ".markdown" => "MarkDown",
            ".sh" or ".bash" or ".zsh" => "Bash",
            ".ps1" or ".psm1" => "PowerShell",
            ".vb" => "VB",
            ".pl" or ".pm" => "Perl",
            ".tex" => "TeX",
            ".patch" or ".diff" => "Patch",
            ".lua" => "lua",
            ".f" or ".for" or ".f90" or ".f95" => "Fortran",
            ".coffee" => "CoffeeScript",
            ".ini" or ".cfg" or ".conf" or ".toml" => "INI",
            ".yml" or ".yaml" => "XML",
            ".asm" or ".s" => "ASM",
            _ => null,
        };

        if (name != null)
        {
            var def = HighlightingManager.Instance.GetDefinition(name);
            if (def != null) return def;
        }

        return HighlightingManager.Instance.GetDefinitionByExtension(ext);
    }

    /// <summary>
    /// AvalonEdit's bundled xshd files target a light background, so tokens
    /// painted in near-black (string literals, identifiers, default body
    /// text on some definitions) become invisible on our dark canvas. Walk
    /// the named colors once per definition and lift any near-black
    /// foreground onto a light grey that contrasts with BackgroundBrush.
    /// Other colors (keyword blue, comment green, etc.) stay untouched so
    /// the user still sees real syntax differentiation.
    /// </summary>
    private static readonly HashSet<string> _patchedDefinitions = [];
    private static readonly object _patchLock = new();

    private static IHighlightingDefinition? PatchForDarkBackground(IHighlightingDefinition? def)
    {
        if (def == null) return null;
        lock (_patchLock)
        {
            if (!_patchedDefinitions.Add(def.Name)) return def;
            foreach (var color in def.NamedHighlightingColors)
            {
                if (color.Foreground == null) continue;
                var wpfColor = color.Foreground.GetColor(null);
                if (wpfColor == null) continue;
                var c = wpfColor.Value;
                int avg = (c.R + c.G + c.B) / 3;
                if (avg >= 170) continue;   // bright enough already

                byte nr, ng, nb;
                if (avg < 40)
                {
                    // Near-black (default body text on most xshd files): map
                    // straight to ForegroundBrush so it reads cleanly.
                    nr = 0xE2; ng = 0xE2; nb = 0xE9;
                }
                else
                {
                    // Mid-tones (keyword blue, comment green, string red…):
                    // lift each channel by a fixed offset to keep the hue
                    // recognizable but push them well above the 0x14 dark
                    // background. Cap at 255.
                    int lift = 130;
                    nr = (byte)Math.Min(255, c.R + lift);
                    ng = (byte)Math.Min(255, c.G + lift);
                    nb = (byte)Math.Min(255, c.B + lift);
                }
                color.Foreground = new SimpleHighlightingBrush(
                    Color.FromArgb(c.A, nr, ng, nb));
            }
        }
        return def;
    }
}
