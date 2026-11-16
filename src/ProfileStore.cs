using System.Text.Json;
using System.Text.Json.Serialization;

namespace OvpnClient;

public sealed class Profile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ImportedFrom { get; set; }
    public string? SavedUsername { get; set; }
    /// <summary>The user chose to keep this profile's password(s) in the login keyring.</summary>
    public bool SavePassword { get; set; }
    /// <summary>Gateway to use instead of a pushed one that is outside the VPN subnet (learned automatically).</summary>
    public string? RouteGatewayOverride { get; set; }
    public DateTime ImportedAt { get; set; }

    [JsonIgnore] public string Directory => Path.Combine(ProfileStore.ProfilesDir, Id);
    [JsonIgnore] public string ConfigPath => Path.Combine(Directory, "config.ovpn");
    [JsonIgnore] public string SocketPath => Path.Combine(ProfileStore.RuntimeDir, Id + ".sock");
}

[JsonSerializable(typeof(List<Profile>))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class ProfileJsonContext : JsonSerializerContext;

/// <summary>
/// Profiles live in ~/.local/share/ovpn-client/profiles/&lt;id&gt;/config.ovpn, with any
/// cert/key files the config references copied next to it. Passwords are never stored.
/// </summary>
public sealed class ProfileStore
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x
            ? x
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"),
        "ovpn-client");

    public static string ProfilesDir { get; } = Path.Combine(DataDir, "profiles");

    /// <summary>Where OpenVPN's management sockets go. Must be accessible only by us.</summary>
    public static string RuntimeDir { get; } = PickRuntimeDir();

    private static string PickRuntimeDir()
    {
        var dir = Path.Combine(
            Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } r ? r : "/tmp",
            "ovpn-client");
        // Unix socket paths are limited to 107 bytes; leave room for "/<id>.sock".
        if (dir.Length + 20 > 107)
            dir = $"/tmp/ovpn-client-{Environment.UserName}";
        return dir;
    }

    private static string IndexPath => Path.Combine(DataDir, "profiles.json");

    public List<Profile> Profiles { get; } = new();

    public ProfileStore()
    {
        System.IO.Directory.CreateDirectory(ProfilesDir);
        System.IO.Directory.CreateDirectory(RuntimeDir);
        File.SetUnixFileMode(RuntimeDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Load();
    }

    private void Load()
    {
        if (!File.Exists(IndexPath)) return;
        try
        {
            var list = JsonSerializer.Deserialize(File.ReadAllText(IndexPath), ProfileJsonContext.Default.ListProfile);
            if (list != null)
                Profiles.AddRange(list.Where(p => File.Exists(p.ConfigPath)));
        }
        catch (JsonException e)
        {
            Console.Error.WriteLine($"Could not read {IndexPath}: {e.Message}");
        }
    }

    public void Save()
    {
        var tmp = IndexPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Profiles, ProfileJsonContext.Default.ListProfile));
        File.Move(tmp, IndexPath, overwrite: true);
    }

    /// <summary>Copies a .ovpn file (and the files it references) into the store.</summary>
    public Profile Import(string sourcePath)
    {
        var text = File.ReadAllText(sourcePath);
        var cfg = OvpnConfig.ParseText(text); // throws on unreadable file, before we create anything

        var profile = new Profile
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Name = UniqueName(Path.GetFileNameWithoutExtension(sourcePath)),
            ImportedFrom = Path.GetFullPath(sourcePath),
            ImportedAt = DateTime.Now,
        };

        System.IO.Directory.CreateDirectory(profile.Directory);
        File.SetUnixFileMode(profile.Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(profile.ConfigPath, text);
        File.SetUnixFileMode(profile.ConfigPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        // Relative paths resolve against the config's folder, which is also how we run
        // openvpn (--cd), so copy them across keeping the same relative layout.
        var sourceDir = Path.GetDirectoryName(Path.GetFullPath(sourcePath))!;
        foreach (var rel in cfg.ReferencedFiles.Where(f => !Path.IsPathRooted(f)))
        {
            var src = Path.GetFullPath(Path.Combine(sourceDir, rel));
            var dst = Path.GetFullPath(Path.Combine(profile.Directory, rel));
            if (!File.Exists(src) || !dst.StartsWith(profile.Directory + Path.DirectorySeparatorChar))
                continue;
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, overwrite: true);
            File.SetUnixFileMode(dst, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        Profiles.Add(profile);
        Save();
        return profile;
    }

    public void Rename(Profile profile, string name)
    {
        profile.Name = name.Trim();
        Save();
    }

    public void Remove(Profile profile)
    {
        Profiles.Remove(profile);
        Save();
        try { System.IO.Directory.Delete(profile.Directory, recursive: true); }
        catch (IOException) { }
    }

    private string UniqueName(string baseName)
    {
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "VPN";
        var name = baseName;
        for (int i = 2; Profiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); i++)
            name = $"{baseName} ({i})";
        return name;
    }

    /// <summary>Files referenced by the config that are missing from the profile folder.</summary>
    public static List<string> MissingFiles(Profile profile)
    {
        try
        {
            var cfg = OvpnConfig.Parse(profile.ConfigPath);
            return cfg.ReferencedFiles
                .Where(f => !File.Exists(Path.IsPathRooted(f) ? f : Path.Combine(profile.Directory, f)))
                .ToList();
        }
        catch (IOException) { return new(); }
    }
}
