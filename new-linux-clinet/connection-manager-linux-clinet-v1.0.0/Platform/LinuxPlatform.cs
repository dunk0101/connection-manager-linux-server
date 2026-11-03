using System.Diagnostics;
using ConnectionManager.Models;

namespace ConnectionManager.Platform;

/// <summary>
/// Ubuntu (desktop) version of the things that differ between operating systems.
///
///   RDP       xfreerdp3 (package freerdp3-x11) - the password is passed on standard
///             input (/args-from:stdin), so it never shows in the process list
///   Web       xdg-open (the default browser)
///   PuTTY     putty -ssh user@host -pw password (package putty)
///   RustDesk  rustdesk --connect ID (RustDesk .deb, or the Flatpak)
///   Start with Ubuntu   ~/.config/autostart/connection-manager.desktop
///   Update    the downloaded .tar.gz is unpacked and its files replace the installed
///             ones (rename, so the running program is not disturbed), then the new
///             version is started
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public class LinuxPlatform : IPlatform
{
    public string OsName => "Ubuntu";
    public string DownloadKey => "linux";

    public string AppDataDir => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x
            ? x : Path.Combine(Home, ".config"),
        "connection-manager");

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // The settings folder is private (700) and the file 600, so the token is stored as it is.
    public string ProtectSecret(string plain) => plain;
    public string UnprotectSecret(string protectedText) => protectedText;

    public IReadOnlyList<ImportItem> ReadOldConnections() => Array.Empty<ImportItem>();

    // --- connecting ------------------------------------------------------------------------

    public async Task<string?> LaunchAsync(Connection c, string password, bool shareClipboard)
    {
        try
        {
            return c.ConnectionType switch
            {
                ConnectionType.RDP => await LaunchRdpAsync(c.IpAddress, c.Username, password, shareClipboard),
                ConnectionType.Ubuntu => await LaunchRdpAsync(c.IpAddress, StripDomain(c.Username), password, shareClipboard),
                ConnectionType.Web => LaunchWeb(c.IpAddress),
                ConnectionType.Putty => LaunchPutty(c.IpAddress, c.Username, password),
                ConnectionType.RustDesk => LaunchRustDesk(c.IpAddress),
                _ => "Unknown connection type."
            };
        }
        catch (Exception ex)
        {
            return $"Could not open {c.Name}: {ex.Message}";
        }
    }

    private static string StripDomain(string username)
    {
        int i = Math.Max(username.LastIndexOf('\\'), username.LastIndexOf('/'));
        return i >= 0 && i < username.Length - 1 ? username[(i + 1)..] : username;
    }

    private static string? FindProgram(params string[] names)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin").Split(':')
                   .Concat(new[] { "/usr/bin", "/usr/local/bin", "/snap/bin", "/usr/share/rustdesk" });
        foreach (var name in names)
        {
            if (name.StartsWith('/') && File.Exists(name)) return name;
            foreach (var d in dirs)
            {
                var p = Path.Combine(d, name);
                if (File.Exists(p)) return p;
            }
        }
        return null;
    }

    private static Process Start(string file, IEnumerable<string> args, bool redirectStdin = false)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardInput = redirectStdin,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return Process.Start(psi) ?? throw new InvalidOperationException($"{file} did not start.");
    }

    private static async Task<string?> LaunchRdpAsync(string address, string username, string password, bool shareClipboard)
    {
        string? exe = FindProgram("xfreerdp3", "sdl-freerdp3", "xfreerdp");
        if (exe == null)
            return "Remote Desktop needs FreeRDP. Install it with:  sudo apt install freerdp3-x11";

        var args = new List<string>
        {
            "/v:" + address,
            "/cert:ignore",
            "/dynamic-resolution",
            "+auto-reconnect",
            "/title:" + address,
            shareClipboard ? "+clipboard" : "-clipboard",
        };
        string user = username, domain = string.Empty;
        int slash = username.IndexOf('\\');
        if (slash > 0) { domain = username[..slash]; user = username[(slash + 1)..]; }
        if (user.Length > 0) args.Add("/u:" + user);
        if (domain.Length > 0) args.Add("/d:" + domain);

        bool isV3 = !exe.EndsWith("/xfreerdp", StringComparison.Ordinal);
        if (password.Length == 0)
        {
            Start(exe, args);
            return null;
        }
        if (!isV3)
        {
            // FreeRDP 2 can't read arguments from stdin.
            args.Add("/p:" + password);
            Start(exe, args);
            return null;
        }
        // FreeRDP 3: give every argument, including the password, on standard input.
        args.Add("/p:" + password);
        var p = Start(exe, new[] { "/args-from:stdin" }, redirectStdin: true);
        await p.StandardInput.WriteAsync(string.Join('\n', args) + "\n");
        p.StandardInput.Close();
        return null;
    }

    private static string? LaunchWeb(string address)
    {
        string url = address.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;
        Start(FindProgram("xdg-open") ?? "xdg-open", new[] { url });
        return null;
    }

    private static string? LaunchPutty(string address, string username, string password)
    {
        string? exe = FindProgram("putty");
        if (exe == null)
            return "PuTTY is not installed. Install it with:  sudo apt install putty";
        var args = new List<string> { "-ssh", string.IsNullOrEmpty(username) ? address : $"{username}@{address}" };
        if (!string.IsNullOrEmpty(password)) { args.Add("-pw"); args.Add(password); }
        Start(exe, args);
        return null;
    }

    private static string? LaunchRustDesk(string id)
    {
        string? exe = FindProgram("rustdesk", "/usr/share/rustdesk/rustdesk");
        if (exe != null)
        {
            Start(exe, new[] { "--connect", id });
            return null;
        }
        string? flatpak = FindProgram("flatpak");
        bool hasFlatpakApp = Directory.Exists("/var/lib/flatpak/app/com.rustdesk.RustDesk")
                             || Directory.Exists(Path.Combine(Home, ".local/share/flatpak/app/com.rustdesk.RustDesk"));
        if (flatpak != null && hasFlatpakApp)
        {
            Start(flatpak, new[] { "run", "com.rustdesk.RustDesk", "--connect", id });
            return null;
        }
        return "RustDesk is not installed. Download the Ubuntu .deb from https://rustdesk.com/ and install it.";
    }

    // --- start with Ubuntu ------------------------------------------------------------------

    private static string AutostartFile => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(Home, ".config"),
        "autostart", "connection-manager.desktop");

    public bool GetStartWithOs() => File.Exists(AutostartFile);

    public void SetStartWithOs(bool enabled)
    {
        if (!enabled)
        {
            if (File.Exists(AutostartFile)) File.Delete(AutostartFile);
            return;
        }
        string exe = Environment.ProcessPath ?? "connection-manager";
        string icon = Path.Combine(Path.GetDirectoryName(exe) ?? "", "icon.png");
        Directory.CreateDirectory(Path.GetDirectoryName(AutostartFile)!);
        File.WriteAllText(AutostartFile,
            "[Desktop Entry]\n" +
            "Type=Application\n" +
            "Name=Connection Manager\n" +
            "Comment=Shared remote connections (RDP, Web, PuTTY, RustDesk)\n" +
            $"Exec=\"{exe}\" --autostart\n" +
            (File.Exists(icon) ? $"Icon={icon}\n" : "") +
            "X-GNOME-Autostart-enabled=true\n" +
            "X-GNOME-Autostart-Delay=5\n" +
            "Terminal=false\n");
    }

    // --- update ----------------------------------------------------------------------------

    public async Task<string?> ApplyUpdateAsync(string downloadedFile)
    {
        string? exe = Environment.ProcessPath;
        if (exe == null || Path.GetFileName(exe) is "dotnet")
            return "Updates only work in the installed program (not when run from the source code).";

        string? error = await InstallFromArchiveAsync(downloadedFile, Path.GetDirectoryName(exe)!);
        if (error != null)
            return error;

        // Start the new version. It closes this one (only one copy runs at a time).
        Start(exe, new[] { "--updated" });
        return null;
    }

    /// <summary>Unpack the downloaded .tar.gz and replace the files in <paramref name="installDir"/>.
    /// Each file is copied next to its target and then renamed over it, so the running
    /// program is never disturbed. Returns null on success, or a message.</summary>
    public static async Task<string?> InstallFromArchiveAsync(string archive, string installDir)
    {
        string unpack = Path.Combine(Path.GetDirectoryName(archive)!, "unpacked");
        Directory.CreateDirectory(unpack);
        var tar = Start("tar", new[] { "-xzf", archive, "-C", unpack });
        await tar.WaitForExitAsync();
        if (tar.ExitCode != 0)
            return "The update could not be unpacked.";

        // The archive holds one folder (connection-manager/) with the program and its files.
        string src = Directory.GetDirectories(unpack).FirstOrDefault() ?? unpack;
        if (!File.Exists(Path.Combine(src, "ConnectionManager")))
            return "The update does not contain the program.";

        try
        {
            foreach (var file in Directory.GetFiles(src))
            {
                string target = Path.Combine(installDir, Path.GetFileName(file));
                string temp = target + ".new";
                File.Copy(file, temp, overwrite: true);
                File.SetUnixFileMode(temp, File.GetUnixFileMode(file));
                File.Move(temp, target, overwrite: true);   // rename: the running copy is unaffected
            }
        }
        catch (UnauthorizedAccessException)
        {
            return $"No permission to replace the program in {installDir}. Run install.sh from the new download instead.";
        }
        return null;
    }
}
