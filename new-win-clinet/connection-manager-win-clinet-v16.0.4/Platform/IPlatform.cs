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

    /// <summary>Open a connection. Returns null on success or a message for the user.
    /// <paramref name="status"/> shows progress in the status line (e.g. "Installing FreeRDP…"
    /// on Ubuntu when a helper program has to be installed first).</summary>
    Task<string?> LaunchAsync(Connection connection, string password, bool shareClipboard, Action<string>? status = null);

    /// <summary>True only when the program really will start at login (entry present, points
    /// to this program, and not switched off in Task Manager &gt; Startup apps).</summary>
    bool GetStartWithOs();
    void SetStartWithOs(bool enabled);
    /// <summary>At every start while the setting is on: repair a missing or out-of-date entry.</summary>
    void EnsureStartWithOs();
    /// <summary>An older version of this client left its entry under the old shared name.</summary>
    bool HasLegacyStartEntry();
    /// <summary>The user switched it off in Task Manager &gt; Startup apps.</summary>
    bool IsStartupDisabledByWindows();
    /// <summary>Remove the "downloaded from the internet" mark from the program file.</summary>
    void ClearDownloadMark();
    /// <summary>Add a line to startup.log, and describe the start-up state for it.</summary>
    void LogStartup(string line);
    string DescribeStartup();

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
