using System.Text.Json.Serialization;

namespace Cmux.Core.Models;

public enum EditorFolderKind
{
    Local,
    RemoteSsh,
}

/// <summary>
/// A folder shown in the per-workspace Editor view. Either points at a local
/// directory or at a directory on a remote host reachable over SSH/SFTP.
/// </summary>
public class EditorFolder
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("kind")]
    public EditorFolderKind Kind { get; set; } = EditorFolderKind.Local;

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("host")]
    public string? Host { get; set; }

    [JsonPropertyName("port")]
    public int Port { get; set; } = 22;

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("privateKeyPath")]
    public string? PrivateKeyPath { get; set; }

    [JsonPropertyName("usePasswordAuth")]
    public bool UsePasswordAuth { get; set; }

    /// <summary>
    /// When true, the folder was created from an ~/.ssh/config alias and
    /// <see cref="Host"/> is the alias itself. The OpenSSH client (sftp.exe)
    /// resolves user / port / identity from the config; the manual-entry
    /// fields (<see cref="Username"/>, <see cref="Port"/>, <see cref="PrivateKeyPath"/>)
    /// are not consulted in that case. Old session.json files predate this
    /// flag and deserialize to false, preserving manual-entry semantics.
    /// </summary>
    [JsonPropertyName("useSshConfig")]
    public bool UseSshConfig { get; set; }
}
