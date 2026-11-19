namespace ConnectionManager.Models;

/// <summary>
/// Settings kept on THIS PC only (the connection list itself lives on the server).
/// Saved as settings.json in the platform's app-data folder.
/// </summary>
public class ClientSettings
{
    public const string DefaultServerUrl = "https://connection-manager.lloydstechnical.com";

    // Server sign-in
    public string ServerUrl { get; set; } = DefaultServerUrl;
    public string Username { get; set; } = string.Empty;
    /// <summary>Device token, protected by the platform (DPAPI on Windows).</summary>
    public string ProtectedToken { get; set; } = string.Empty;

    // Settings tab (same settings as the old program)
    public bool ShareClipboard { get; set; } = true;
    public bool StartWithOs { get; set; }
    public bool StartMinimized { get; set; }

    // Updates (v16.0.6): install a newer Connection Manager from the server by itself.
    // On by default, also for PCs whose settings.json was saved before this setting existed.
    public bool AutoUpdate { get; set; } = true;

    // Window position and size
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 600;
    public double WindowHeight { get; set; } = 900;

    /// <summary>Locations the user collapsed (kept per PC).</summary>
    public List<string> CollapsedGroups { get; set; } = new();

    /// <summary>Windows only: the old Connection Manager's connections were offered for upload.</summary>
    public bool OldImportOffered { get; set; }
}
