using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ConnectionManager.Models;
using Microsoft.Win32;

namespace ConnectionManager.Platform;

/// <summary>
/// Windows version of the things that differ between operating systems.
/// The connection launchers are ported from the old program's ConnectionService.cs
/// (v15.0.4) and work the same way:
///
///   RDP       writes a per-host .rdp file with the password embedded (DPAPI
///             "password 51:b:") and the clipboard setting, also stores a cmdkey
///             /generic credential as a fallback, pre-authorises the resource prompt,
///             then runs mstsc.exe
///   Web       opens the default browser (https:// added if missing)
///   PuTTY     putty.exe -ssh user@host -pw password
///   RustDesk  rustdesk.exe --connect ID (Program Files or LocalAppData)
///   Start with Windows   HKCU\...\Run\ConnectionManager (same entry as the old program)
///   Update    a small .cmd waits for this program to close, replaces the .exe and
///             starts the new version
/// </summary>
[SupportedOSPlatform("windows")]
public class WindowsPlatform : IPlatform
{
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "ConnectionManager";
    private static readonly byte[] TokenEntropy = Encoding.UTF8.GetBytes("ConnectionManagerServer_token_v1");

    public string OsName => "Windows";
    public string DownloadKey => "windows";

    // A new folder, so the old program's %LOCALAPPDATA%\ConnectionManager\config.json is left alone.
    public string AppDataDir => LocalDir;

    private static string LocalDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConnectionManagerServer");
    private static string RdpDir => Path.Combine(LocalDir, "rdp");

    // --- device token (DPAPI, this Windows user only) ---------------------------------------

    public string ProtectSecret(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), TokenEntropy, DataProtectionScope.CurrentUser));

    public string UnprotectSecret(string protectedText) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedText), TokenEntropy, DataProtectionScope.CurrentUser));

    // --- connecting ----------------------------------------------------------------------------

    public Task<string?> LaunchAsync(Connection c, string password, bool shareClipboard)
    {
        try
        {
            string? error = c.ConnectionType switch
            {
                ConnectionType.RDP => ConnectRdp(c.IpAddress, c.Username, password, shareClipboard),
                ConnectionType.Ubuntu => ConnectRdp(c.IpAddress, StripDomain(c.Username), password, shareClipboard),
                ConnectionType.Web => ConnectWeb(c.IpAddress),
                ConnectionType.Putty => ConnectPutty(c.IpAddress, c.Username, password),
                ConnectionType.RustDesk => ConnectRustDesk(c.IpAddress),
                _ => "Unknown connection type."
            };
            return Task.FromResult(error);
        }
        catch (Exception ex)
        {
            return Task.FromResult<string?>($"Could not open {c.Name}: {ex.Message}");
        }
    }

    /// <summary>Ubuntu RDP: username without any DOMAIN\ or DOMAIN/ prefix.</summary>
    private static string StripDomain(string username)
    {
        int i = Math.Max(username.LastIndexOf('\\'), username.LastIndexOf('/'));
        return i >= 0 && i < username.Length - 1 ? username[(i + 1)..] : username;
    }

    private static string? ConnectRdp(string ip, string username, string password, bool shareClipboard)
    {
        // Also store a Generic credential as a harmless fallback.
        if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
            StoreRdpCredentials(ip, username, password);
        LaunchRdpFileWithPassword(ip, username, password, shareClipboard);
        return null;
    }

    private static string? ConnectWeb(string address)
    {
        string url = address.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        return null;
    }

    private static string? ConnectPutty(string address, string username, string password)
    {
        var psi = new ProcessStartInfo { FileName = "putty.exe", UseShellExecute = true };
        psi.ArgumentList.Add("-ssh");
        psi.ArgumentList.Add(string.IsNullOrEmpty(username) ? address : $"{username}@{address}");
        if (!string.IsNullOrEmpty(password))
        {
            psi.ArgumentList.Add("-pw");
            psi.ArgumentList.Add(password);
        }
        try
        {
            Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Not on the PATH - try the usual install folder.
            string pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PuTTY", "putty.exe");
            if (!File.Exists(pf))
                return "PuTTY is not installed (putty.exe was not found). Download it from https://www.putty.org/";
            psi.FileName = pf;
            Process.Start(psi);
        }
        return null;
    }

    private static string? ConnectRustDesk(string id)
    {
        string rustDeskPath = "rustdesk.exe";
        var programFilesPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RustDesk", "rustdesk.exe");
        var localAppDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "rustdesk", "rustdesk.exe");
        if (File.Exists(programFilesPath)) rustDeskPath = programFilesPath;
        else if (File.Exists(localAppDataPath)) rustDeskPath = localAppDataPath;

        var psi = new ProcessStartInfo { FileName = rustDeskPath, UseShellExecute = true };
        psi.ArgumentList.Add("--connect");
        psi.ArgumentList.Add(id);
        try
        {
            Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "RustDesk is not installed. Download it from https://rustdesk.com/";
        }
        return null;
    }

    /// <summary>Store the credential with cmdkey /generic:TERMSRV/host (the type mstsc reliably uses).</summary>
    private static void StoreRdpCredentials(string target, string username, string password)
    {
        try
        {
            RunHidden("cmdkey.exe", $"/delete:TERMSRV/{target}");
            RunHidden("cmdkey.exe", $"/generic:TERMSRV/{target}", $"/user:{username}", $"/pass:{password}");
        }
        catch { /* only a fallback */ }
    }

    private static void RunHidden(string file, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        p?.WaitForExit(5000);
    }

    /// <summary>
    /// Write a per-host .rdp file with the password embedded (password 51:b:) and launch it.
    /// Embedding the password makes Windows log in WITHOUT prompting for credentials.
    /// The "Caution" dialog that .rdp files trigger is disabled machine-wide via the
    /// RedirectionWarningDialogVersion policy; the older publisher dialog is pre-authorised
    /// per host via SuppressResourcePrompt.
    /// </summary>
    private static void LaunchRdpFileWithPassword(string ip, string username, string password, bool shareClipboard)
    {
        try
        {
            Directory.CreateDirectory(RdpDir);
            string safeName = ip;
            foreach (char ch in Path.GetInvalidFileNameChars())
                safeName = safeName.Replace(ch, '_');
            string rdpPath = Path.Combine(RdpDir, safeName + ".rdp");

            var lines = new List<string>
            {
                $"full address:s:{ip}",
                "prompt for credentials:i:0",
                "authentication level:i:0",
                shareClipboard ? "redirectclipboard:i:1" : "redirectclipboard:i:0"
            };
            if (!string.IsNullOrEmpty(username))
                lines.Insert(1, $"username:s:{username}");
            if (!string.IsNullOrEmpty(password))
                lines.Add($"password 51:b:{EncryptRdpPassword(password)}");
            File.WriteAllLines(rdpPath, lines);

            SuppressResourcePrompt(ip);

            var psi = new ProcessStartInfo { FileName = "mstsc.exe", UseShellExecute = true };
            psi.ArgumentList.Add(rdpPath);
            Process.Start(psi);
        }
        catch
        {
            Process.Start(new ProcessStartInfo { FileName = "mstsc.exe", Arguments = $"/v:{ip}", UseShellExecute = true });
        }
    }

    /// <summary>DPAPI (current user) over the UTF-16LE password, hex-encoded - the form mstsc reads.</summary>
    private static string EncryptRdpPassword(string plain) =>
        Convert.ToHexString(ProtectedData.Protect(Encoding.Unicode.GetBytes(plain), null, DataProtectionScope.CurrentUser));

    /// <summary>Pre-authorise local-resource redirection for an RDP host ("Don't ask again").</summary>
    private static void SuppressResourcePrompt(string target)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Terminal Server Client\LocalDevices");
            key?.SetValue(target, 0x4C, RegistryValueKind.DWord);
        }
        catch { }
    }

    // --- start with Windows ----------------------------------------------------------------------

    public bool GetStartWithOs()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
        return key?.GetValue(RunValueName) is string v && v.Length > 0;
    }

    public void SetStartWithOs(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
            key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"");
        else
            key.DeleteValue(RunValueName, false);
    }

    // --- update ------------------------------------------------------------------------------------

    public Task<string?> ApplyUpdateAsync(string downloadedFile)
    {
        string? exe = Environment.ProcessPath;
        if (exe == null || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(exe).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<string?>("Updates only work in the published ConnectionManager.exe.");

        // Can we write next to the program?
        string dir = Path.GetDirectoryName(exe)!;
        try
        {
            string probe = Path.Combine(dir, ".cm-write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch
        {
            return Task.FromResult<string?>($"No permission to replace the program in {dir}. Download the new version from the server's website instead.");
        }

        int pid = Environment.ProcessId;
        string cmd = Path.Combine(Path.GetDirectoryName(downloadedFile)!, "update.cmd");
        File.WriteAllText(cmd,
            "@echo off\r\n" +
            "setlocal\r\n" +
            $":wait\r\n" +
            $"tasklist /FI \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul\r\n" +
            "if not errorlevel 1 ( timeout /t 1 /nobreak >nul & goto wait )\r\n" +
            "set tries=0\r\n" +
            ":copy\r\n" +
            $"copy /Y \"{downloadedFile}\" \"{exe}\" >nul 2>&1\r\n" +
            "if errorlevel 1 (\r\n" +
            "  set /a tries+=1\r\n" +
            "  if %tries% LSS 15 ( timeout /t 1 /nobreak >nul & goto copy )\r\n" +
            ")\r\n" +
            $"start \"\" \"{exe}\" --updated\r\n" +
            $"del \"{downloadedFile}\" >nul 2>&1\r\n" +
            "(goto) 2>nul & del \"%~f0\"\r\n");

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{cmd}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
        return Task.FromResult<string?>(null);
    }

    // --- connections from the old Connection Manager (v15) on this PC -------------------------------

    private const string OldPrefix = "DPAPI:";
    private static readonly byte[] OldEntropy = Encoding.UTF8.GetBytes("ConnectionManager_v1.0_Entropy");
    private static readonly string[] OldTypes = { "RDP", "Web", "Putty", "Ubuntu", "RustDesk" };

    public IReadOnlyList<ImportItem> ReadOldConnections()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                   "ConnectionManager", "config.json");
        if (!File.Exists(path)) return Array.Empty<ImportItem>();

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!TryGet(doc.RootElement, "Connections", out var list) || list.ValueKind != JsonValueKind.Array)
            return Array.Empty<ImportItem>();

        var items = new List<ImportItem>();
        foreach (var c in list.EnumerateArray())
        {
            string Str(string name) => TryGet(c, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            string type = "RDP";
            if (TryGet(c, "ConnectionType", out var t))
            {
                if (t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out int n) && n >= 0 && n < OldTypes.Length) type = OldTypes[n];
                else if (t.ValueKind == JsonValueKind.String) type = t.GetString() ?? "RDP";
            }
            int sort = TryGet(c, "SortOrder", out var so) && so.ValueKind == JsonValueKind.Number ? so.GetInt32() : 0;
            string ip = Str("IpAddress");
            if (ip.Length == 0) continue;
            items.Add(new ImportItem(Str("Name"), ip, Str("Location"), Str("Username"),
                                     DecryptOld(Str("EncryptedPassword")), type, sort));
        }
        return items;
    }

    private static bool TryGet(JsonElement e, string name, out JsonElement value)
    {
        foreach (var p in e.EnumerateObject())
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
        value = default;
        return false;
    }

    /// <summary>The old program's DPAPI encryption ("DPAPI:" + base64, with its own entropy).</summary>
    private static string DecryptOld(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (!text.StartsWith(OldPrefix, StringComparison.Ordinal)) return text;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(text[OldPrefix.Length..]), OldEntropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return "";
        }
    }
}
