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
///             switches off the "Caution: Unknown remote connection" warning (asks for
///             admin permission once per PC), then runs mstsc.exe
///   Web       opens the default browser (https:// added if missing)
///   PuTTY     putty.exe -ssh user@host -pw password
///   RustDesk  rustdesk.exe --connect ID (Program Files or LocalAppData)
///   Start with Windows   HKCU\...\Run\ConnectionManagerServer (its own entry - v16.0.0 to
///             v16.0.2 shared "ConnectionManager" with the old v15 program, which stopped it
///             starting at login). Checked and repaired every time the program starts.
///   Update    a small .cmd waits for this program to close, replaces the .exe and
///             starts the new version
/// </summary>
[SupportedOSPlatform("windows")]
public class WindowsPlatform : IPlatform
{
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "ConnectionManagerServer";
    // The old v15 program's entry name, also used by this client up to v16.0.2.
    private const string LegacyRunValueName = "ConnectionManager";
    // Task Manager / Settings > Apps > Startup store "enabled/disabled" here.
    private const string StartupApprovedPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
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

    // Runs on a background thread: the RDP path may wait for a Windows admin (UAC) prompt.
    public Task<string?> LaunchAsync(Connection c, string password, bool shareClipboard, Action<string>? status = null) =>
        Task.Run(() => Launch(c, password, shareClipboard));

    private static string? Launch(Connection c, string password, bool shareClipboard)
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
            return error;
        }
        catch (Exception ex)
        {
            return $"Could not open {c.Name}: {ex.Message}";
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
        EnsureRdpWarningOff();
        LaunchRdpFileWithPassword(ip, username, password, shareClipboard);
        return null;
    }

    // --- the "Caution: Unknown remote connection" warning --------------------------------------
    //
    // Since the April 2026 Windows update, every unsigned .rdp file opens with a "Caution:
    // Unknown remote connection" warning that has no "Don't ask again" box. Windows turns it
    // off (back to the old dialog, which SuppressResourcePrompt pre-answers) with this
    // MACHINE-wide policy value. Only an administrator can write it, so the first RDP Connect
    // on a PC that does not have it asks once for admin permission (UAC) and sets it. Up to
    // v16.0.4 the client only assumed the value was there; on PCs where nobody had set it by
    // hand, the warning appeared on every connection.

    private const string RdpClientPolicyPath = @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services\Client";
    private const string RdpWarningValueName = "RedirectionWarningDialogVersion";
    // Asked (or refused) while this program is running: do not ask again until it restarts.
    private static bool _askedForRdpPolicy;

    private static bool RdpWarningIsOff()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RdpClientPolicyPath);
            return key?.GetValue(RdpWarningValueName) is int v && v == 1;
        }
        catch { return false; }
    }

    /// <summary>
    /// Make sure the warning is switched off on this PC. Shows the Windows admin prompt
    /// once if it is not; if the user says No, the RDP connection still opens (with the
    /// warning) and the prompt is not shown again until the program is restarted.
    /// </summary>
    private static void EnsureRdpWarningOff()
    {
        if (RdpWarningIsOff() || _askedForRdpPolicy) return;
        _askedForRdpPolicy = true;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "reg.exe",
                Arguments = $"add \"HKLM\\{RdpClientPolicyPath}\" /v {RdpWarningValueName} /t REG_DWORD /d 1 /f",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(60000);
        }
        catch { /* No pressed at the admin prompt (error 1223), or not allowed: connect anyway. */ }
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
    /// RedirectionWarningDialogVersion policy (set by EnsureRdpWarningOff); the older
    /// publisher dialog is pre-authorised per host via SuppressResourcePrompt.
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
    //
    // Windows starts every program listed under HKCU\...\Run when the user logs in, unless the
    // program was switched off in Task Manager > Startup apps (StartupApproved\Run, first byte
    // odd = disabled). "Start with Windows" only counts as on when the entry exists, points to
    // THIS exe, and is not switched off.

    private static string? ExePath => Environment.ProcessPath;
    private static string RunCommand => $"\"{ExePath}\"";

    /// <summary>The exe path inside a Run value ("C:\x\y.exe" --args  ->  C:\x\y.exe).</summary>
    private static string? ExeFromCommand(object? value)
    {
        if (value is not string cmd) return null;
        cmd = cmd.Trim();
        if (cmd.Length == 0) return null;
        if (cmd.StartsWith('"'))
        {
            int end = cmd.IndexOf('"', 1);
            return end > 1 ? cmd[1..end] : cmd.Trim('"');
        }
        int exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? cmd[..(exe + 4)] : cmd;
    }

    private static bool IsThisExe(object? value)
    {
        string? path = ExeFromCommand(value);
        if (path == null || ExePath == null) return false;
        try
        {
            return string.Equals(Path.GetFullPath(path), Path.GetFullPath(ExePath), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool PointsToThisExe()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
        return IsThisExe(key?.GetValue(RunValueName));
    }

    public bool IsStartupDisabledByWindows()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupApprovedPath, false);
            return key?.GetValue(RunValueName) is byte[] b && b.Length > 0 && (b[0] & 1) == 1;
        }
        catch { return false; }
    }

    public bool GetStartWithOs() => PointsToThisExe() && !IsStartupDisabledByWindows();

    public void SetStartWithOs(bool enabled)
    {
        using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true)
                         ?? Registry.CurrentUser.CreateSubKey(RunKeyPath))
        {
            if (enabled)
            {
                if (ExePath == null) throw new InvalidOperationException("the program's location is unknown.");
                key.SetValue(RunValueName, RunCommand);
            }
            else
            {
                key.DeleteValue(RunValueName, false);
            }
            RemoveLegacyEntry(key);
        }

        // Ticking the box also undoes "Disabled" from Task Manager > Startup apps.
        try
        {
            using var approved = Registry.CurrentUser.CreateSubKey(StartupApprovedPath);
            if (enabled)
                approved?.SetValue(RunValueName, new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
            else
                approved?.DeleteValue(RunValueName, false);
        }
        catch { }
    }

    /// <summary>
    /// Called every time the program starts while "Start with Windows" is on: puts the Run
    /// entry back if it is missing or points somewhere else (e.g. the exe was moved).
    /// A "Disabled" set by the user in Task Manager is left alone.
    /// </summary>
    public void EnsureStartWithOs()
    {
        if (ExePath == null) return;
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (!IsThisExe(key.GetValue(RunValueName)))
            key.SetValue(RunValueName, RunCommand);
        RemoveLegacyEntry(key);
    }

    /// <summary>True when v16.0.0-v16.0.2 of this client left its start-up entry under the old
    /// shared name "ConnectionManager" (pointing to this exe), i.e. the box was ticked then.</summary>
    public bool HasLegacyStartEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            return IsThisExe(key?.GetValue(LegacyRunValueName));
        }
        catch { return false; }
    }

    /// <summary>Remove the old shared "ConnectionManager" entry - but only when it starts THIS
    /// exe. An entry for the old v15 program is left alone.</summary>
    private static void RemoveLegacyEntry(RegistryKey runKey)
    {
        try
        {
            if (IsThisExe(runKey.GetValue(LegacyRunValueName)))
                runKey.DeleteValue(LegacyRunValueName, false);
        }
        catch { }
    }

    /// <summary>Remove the "downloaded from the internet" mark from the exe, so Windows does
    /// not hold it at a SmartScreen / security prompt when it is started at login.</summary>
    public void ClearDownloadMark()
    {
        if (ExePath == null) return;
        try { File.Delete(ExePath + ":Zone.Identifier"); } catch { }
    }

    /// <summary>One line per start in startup.log (last 50 lines kept), to find out why
    /// the program did or did not start at login.</summary>
    public void LogStartup(string line)
    {
        try
        {
            Directory.CreateDirectory(LocalDir);
            string log = Path.Combine(LocalDir, "startup.log");
            var lines = File.Exists(log) ? File.ReadAllLines(log).ToList() : new List<string>();
            lines.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}");
            if (lines.Count > 50) lines = lines.Skip(lines.Count - 50).ToList();
            File.WriteAllLines(log, lines);
        }
        catch { }
    }

    /// <summary>Short description of the start-up state for startup.log.</summary>
    public string DescribeStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            string ours = key?.GetValue(RunValueName) as string ?? "(none)";
            string legacy = key?.GetValue(LegacyRunValueName) as string ?? "(none)";
            return $"Run\\{RunValueName}={ours} thisExe={PointsToThisExe()} disabledInTaskManager={IsStartupDisabledByWindows()} " +
                   $"Run\\{LegacyRunValueName}={legacy}";
        }
        catch (Exception ex) { return "could not read the registry: " + ex.Message; }
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
