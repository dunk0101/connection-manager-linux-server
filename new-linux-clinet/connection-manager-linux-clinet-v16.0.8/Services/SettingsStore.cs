using System.Reflection;
using System.Text.Json;
using ConnectionManager.Models;
using ConnectionManager.Platform;

namespace ConnectionManager.Services;

/// <summary>
/// Loads and saves this PC's settings (settings.json) and the last connection list
/// seen (connections-cache.json, no passwords - shown when the server can't be reached).
/// Files are private to the user (mode 600 on Ubuntu).
/// </summary>
public class SettingsStore
{
    private readonly string _dir;
    private readonly object _lock = new();

    public ClientSettings Settings { get; private set; } = new();

    public SettingsStore(IPlatform platform)
    {
        _dir = platform.AppDataDir;
        Directory.CreateDirectory(_dir);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private string SettingsPath => Path.Combine(_dir, "settings.json");
    private string CachePath => Path.Combine(_dir, "connections-cache.json");

    public ClientSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                Settings = JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(SettingsPath), ApiClient.Json) ?? new();
        }
        catch
        {
            Settings = new ClientSettings();
        }
        return Settings;
    }

    public void Save()
    {
        lock (_lock)
            WritePrivate(SettingsPath, JsonSerializer.Serialize(Settings, ApiClient.Json));
    }

    public void SaveCache(IEnumerable<Connection> connections)
    {
        try
        {
            lock (_lock)
                WritePrivate(CachePath, JsonSerializer.Serialize(connections.ToList(), ApiClient.Json));
        }
        catch { /* the cache is only a convenience */ }
    }

    public List<Connection> LoadCache()
    {
        try
        {
            if (File.Exists(CachePath))
                return JsonSerializer.Deserialize<List<Connection>>(File.ReadAllText(CachePath), ApiClient.Json) ?? new();
        }
        catch { }
        return new();
    }

    public void ClearCache()
    {
        try { File.Delete(CachePath); } catch { }
    }

    /// <summary>Write to a temporary file and swap it in, so a crash never leaves half a file.</summary>
    private static void WritePrivate(string path, string text)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>Version of this program, stamped in at build time from the folder name
/// (connection-manager-...-clinet-v16.0.1 -> v16.0.1).</summary>
public static class AppInfo
{
    public static string Version
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
            return $"v{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
        }
    }

    /// <summary>Compare "v1.2.10" style versions. Returns &gt;0 when a is newer than b.</summary>
    public static int Compare(string a, string b) => Parse(a).CompareTo(Parse(b));

    private static Version Parse(string s)
    {
        var parts = (s ?? "").Trim().TrimStart('v', 'V').Split('.');
        int P(int i) => i < parts.Length && int.TryParse(parts[i], out var n) ? n : 0;
        return new Version(P(0), P(1), P(2));
    }
}
