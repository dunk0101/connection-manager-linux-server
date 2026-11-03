using ConnectionManager.Models;

namespace ConnectionManager.Platform;

/// <summary>
/// Everything that differs between Windows and Ubuntu. The rest of the program
/// (screens, server, ping, update checks) is the same on both.
/// The Windows client implements this in WindowsPlatform.cs, the Ubuntu client in
/// LinuxPlatform.cs.
/// </summary>
public interface IPlatform
{
    /// <summary>"Windows" or "Ubuntu" - used in labels such as "Start ... with Windows".</summary>
    string OsName { get; }

    /// <summary>Key of this platform's build on the server: "windows" or "linux".</summary>
    string DownloadKey { get; }

    /// <summary>Folder for settings.json and the offline cache.</summary>
    string AppDataDir { get; }

    /// <summary>Protect / unprotect the device token before it is written to disk.</summary>
    string ProtectSecret(string plain);
    string UnprotectSecret(string protectedText);

    /// <summary>Open a connection. Returns null on success or a message for the user.</summary>
    Task<string?> LaunchAsync(Connection connection, string password, bool shareClipboard);

    bool GetStartWithOs();
    void SetStartWithOs(bool enabled);

    /// <summary>Replace the running program with the downloaded build and start it.
    /// Returns null when the new version has been started (the caller then exits),
    /// or a message explaining why it could not be updated.</summary>
    Task<string?> ApplyUpdateAsync(string downloadedFile);

    /// <summary>Connections from an older Connection Manager on this PC (Windows only),
    /// with passwords decrypted, ready to upload. Empty when there are none.</summary>
    IReadOnlyList<ImportItem> ReadOldConnections();
}

/// <summary>One connection to upload with POST /api/v1/connections/import.</summary>
public record ImportItem(string Name, string IpAddress, string Location, string Username,
                         string Password, string ConnectionType, int SortOrder);
