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
///
/// HOW IT IS INSTALLED (v16.0.1 - no terminal commands needed):
///   System  the .deb package: /opt/connection-manager (FreeRDP + PuTTY come with it)
///   User    ~/.local/share/connection-manager - when ConnectionManager is started from
///           anywhere else (e.g. double-clicked in the unpacked download) it copies
///           itself there, adds the menu entry and restarts from there (PrepareInstall)
/// If FreeRDP or PuTTY is missing when Connect is pressed, it installs them through
/// Ubuntu's normal password window (pkexec), or - if that is not available - a terminal
/// window that only asks for the password (InstallPackagesAsync).
/// Updates: a System install installs the new .deb (password window); a User install
/// replaces its own files from the .tar.gz (no password).
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public class LinuxPlatform : IPlatform
{
    public const string SystemDir = "/opt/connection-manager";
    public static readonly string[] HelperPackages = { "freerdp3-x11", "putty" };
    private const string ExeName = "ConnectionManager";

    public enum InstallMode { System, User, Other }

    public string OsName => "Ubuntu";

    /// <summary>A .deb install updates from the .deb, a user install from the tarball.</summary>
    public string DownloadKey => ModeOf(Environment.ProcessPath) == InstallMode.System ? "linux-deb" : "linux";

    public string AppDataDir => Path.Combine(ConfigHome, "connection-manager");

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string ConfigHome =>
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(Home, ".config");
    private static string DataHome =>
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x ? x : Path.Combine(Home, ".local", "share");

    public static string UserDir => Path.Combine(DataHome, "connection-manager");
    private static string UserDesktopFile => Path.Combine(DataHome, "applications", "connection-manager.desktop");
    private static string UserBinLink => Path.Combine(Home, ".local", "bin", "connection-manager");

    // The settings folder is private (700) and the file 600, so the token is stored as it is.
    public string ProtectSecret(string plain) => plain;
    public string UnprotectSecret(string protectedText) => protectedText;

    public IReadOnlyList<ImportItem> ReadOldConnections() => Array.Empty<ImportItem>();

    // --- installing itself ------------------------------------------------------------------

    public static InstallMode ModeOf(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return InstallMode.Other;
        string dir = Path.GetDirectoryName(Path.GetFullPath(exePath)) ?? "";
        if (dir == SystemDir) return InstallMode.System;
        if (dir == Path.GetFullPath(UserDir)) return InstallMode.User;
        return InstallMode.Other;
    }

    /// <summary>
    /// Called once at start-up. Returns the path of a newly installed copy that should be
    /// started instead of this one, or null to carry on.
    ///   Other  -> copy itself to ~/.local/share/connection-manager, add the menu entry
    ///   System -> remove an old ~/.local copy (so there is one menu entry) and point
    ///             "Start with Ubuntu" at the installed program
    /// </summary>
    public static string? PrepareInstall(Func<Stream>? iconSource = null)
    {
        string? exe = Environment.ProcessPath;
        if (exe == null || Path.GetFileName(exe) is "dotnet") return null;   // run from source
        try
        {
            switch (ModeOf(exe))
            {
                case InstallMode.Other:
                    return InstallForUser(exe, iconSource);
                case InstallMode.System:
                    RemoveUserInstall();
                    RepointAutostart("/usr/bin/connection-manager");
                    return null;
                default:
                    WriteUserMenuEntry(iconSource);   // repair a missing menu entry
                    return null;
            }
        }
        catch
        {
            return null;   // never stop the program starting because of this
        }
    }

    /// <summary>Copy the program into ~/.local/share/connection-manager and add the
    /// menu entry and the connection-manager command. Returns the installed program.</summary>
    public static string InstallForUser(string exe, Func<Stream>? iconSource)
    {
        Directory.CreateDirectory(UserDir);
        string target = Path.Combine(UserDir, ExeName);
        CopyOver(exe, target);
        File.SetUnixFileMode(target, (UnixFileMode)0b111_101_101);   // 755
        // Extra files from the download (install.sh), if they are next to it.
        string srcDir = Path.GetDirectoryName(exe)!;
        foreach (var extra in new[] { "install.sh" })
        {
            string f = Path.Combine(srcDir, extra);
            if (File.Exists(f)) CopyOver(f, Path.Combine(UserDir, extra));
        }
        WriteUserMenuEntry(iconSource);
        Directory.CreateDirectory(Path.GetDirectoryName(UserBinLink)!);
        if (File.Exists(UserBinLink) || Directory.Exists(UserBinLink) || new FileInfo(UserBinLink).LinkTarget != null)
            File.Delete(UserBinLink);
        File.CreateSymbolicLink(UserBinLink, target);
        RepointAutostart(target);
        return target;
    }

    private static void CopyOver(string source, string target)
    {
        string temp = target + ".new";
        File.Copy(source, temp, overwrite: true);
        File.Move(temp, target, overwrite: true);   // rename: a running copy is unaffected
    }

    private static void WriteUserMenuEntry(Func<Stream>? iconSource)
    {
        string icon = Path.Combine(UserDir, "icon.png");
        if (!File.Exists(icon) && iconSource != null)
        {
            using var s = iconSource();
            using var f = File.Create(icon);
            s.CopyTo(f);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(UserDesktopFile)!);
        File.WriteAllText(UserDesktopFile, DesktopEntry($"\"{Path.Combine(UserDir, ExeName)}\"", icon));
    }

    public static string DesktopEntry(string exec, string icon) =>
        "[Desktop Entry]\n" +
        "Type=Application\n" +
        "Name=Connection Manager\n" +
        "GenericName=Remote Connections\n" +
        "Comment=Shared remote connections (RDP, Web, PuTTY, RustDesk)\n" +
        $"Exec={exec}\n" +
        $"Icon={icon}\n" +
        "Terminal=false\n" +
        "Categories=Network;RemoteAccess;\n" +
        "StartupWMClass=ConnectionManager\n";

    private static void RemoveUserInstall()
    {
        if (Directory.Exists(UserDir)) Directory.Delete(UserDir, recursive: true);
        if (File.Exists(UserDesktopFile)) File.Delete(UserDesktopFile);
        var link = new FileInfo(UserBinLink);
        if (link.LinkTarget != null && link.LinkTarget.StartsWith(UserDir, StringComparison.Ordinal))
            link.Delete();
    }

    private static void RepointAutostart(string exe)
    {
        if (!File.Exists(AutostartFile)) return;
        WriteAutostart(exe);
    }

    // --- connecting ------------------------------------------------------------------------

    public async Task<string?> LaunchAsync(Connection c, string password, bool shareClipboard, Action<string>? status = null)
    {
        try
        {
            return c.ConnectionType switch
            {
                ConnectionType.RDP => await LaunchRdpAsync(c.IpAddress, c.Username, password, shareClipboard, status),
                ConnectionType.Ubuntu => await LaunchRdpAsync(c.IpAddress, StripDomain(c.Username), password, shareClipboard, status),
                ConnectionType.Web => LaunchWeb(c.IpAddress),
                ConnectionType.Putty => await LaunchPuttyAsync(c.IpAddress, c.Username, password, status),
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

    public static string? FindProgram(params string[] names)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin").Split(':')
                   .Concat(new[] { "/usr/bin", "/bin", "/usr/local/bin", "/snap/bin", "/usr/share/rustdesk" });
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

    private static string? FindFreeRdp() => FindProgram("xfreerdp3", "sdl-freerdp3", "xfreerdp");

    /// <summary>Make sure FreeRDP and PuTTY are installed (both at once, one password).
    /// Returns null when the wanted program is now there, otherwise a message.</summary>
    private static async Task<string?> EnsureHelpersAsync(Func<string?> find, string what, Action<string>? status)
    {
        if (find() != null) return null;
        status?.Invoke($"Installing {what} - enter your password in the window that opens…");
        string? error = await InstallPackagesAsync(HelperPackages);
        if (find() != null)
        {
            status?.Invoke($"{what} installed.");
            return null;
        }
        return error ?? $"{what} could not be installed. Ask your administrator to install: {string.Join(' ', HelperPackages)}";
    }

    private static async Task<string?> LaunchRdpAsync(string address, string username, string password, bool shareClipboard,
                                                      Action<string>? status)
    {
        if (await EnsureHelpersAsync(FindFreeRdp, "FreeRDP (Remote Desktop)", status) is { } err)
            return err;
        string exe = FindFreeRdp()!;

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

    private static async Task<string?> LaunchPuttyAsync(string address, string username, string password, Action<string>? status)
    {
        if (await EnsureHelpersAsync(() => FindProgram("putty"), "PuTTY", status) is { } err)
            return err;
        var args = new List<string> { "-ssh", string.IsNullOrEmpty(username) ? address : $"{username}@{address}" };
        if (!string.IsNullOrEmpty(password)) { args.Add("-pw"); args.Add(password); }
        Start(FindProgram("putty")!, args);
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

    // --- installing packages without the user typing commands ------------------------------

    /// <summary>
    /// Run "apt-get install -y ARGS" as root:
    ///   1. pkexec - Ubuntu's normal password window
    ///   2. if there is no pkexec or no password agent: a terminal window that runs the
    ///      command with sudo, so the user only types their password
    /// apt waits (up to 10 minutes) if Software Updater is using it.
    /// Returns null on success, or a message.
    /// </summary>
    public static async Task<string?> InstallPackagesAsync(IEnumerable<string> aptArgs)
    {
        var args = aptArgs.ToList();
        var aptCmd = new List<string> { "apt-get", "-o", "DPkg::Lock::Timeout=600", "install", "-y" };
        aptCmd.AddRange(args);

        string? pkexec = FindProgram("pkexec");
        if (pkexec != null)
        {
            var p = Start(pkexec, aptCmd);
            await p.WaitForExitAsync();
            if (p.ExitCode == 0) return null;
            if (p.ExitCode == 126) return "Cancelled - the password window was closed.";
            // 127 = no password agent in this session (e.g. some RDP desktops): use a terminal.
        }

        var term = TerminalCommand(SudoScript(aptCmd));
        if (term == null)
            return "No password window or terminal was found to install: " + string.Join(' ', args);
        var t = Start(term.Value.exe, term.Value.args);
        await t.WaitForExitAsync();
        return null;   // the caller checks whether the program is now there
    }

    public static string SudoScript(IEnumerable<string> aptCmd)
    {
        string cmd = string.Join(' ', aptCmd.Select(a => "'" + a.Replace("'", "'\\''") + "'"));
        return "echo 'Connection Manager needs to install some programs.'; " +
               "echo 'Please type YOUR password below (the one you log in with) and press Enter.'; echo; " +
               $"sudo {cmd}; s=$?; echo; " +
               "if [ $s -eq 0 ]; then echo 'Done - this window closes by itself.'; sleep 3; " +
               "else read -r -p 'Something went wrong. Press Enter to close this window.' _; fi";
    }

    /// <summary>The first terminal found, set up to run SCRIPT and to wait until it closes.</summary>
    public static (string exe, string[] args)? TerminalCommand(string script)
    {
        var candidates = new (string name, Func<string, string[]> args)[]
        {
            ("xfce4-terminal", s => new[] { "--disable-server", "--title=Connection Manager", "-x", "bash", "-c", s }),
            ("gnome-terminal", s => new[] { "--wait", "--title=Connection Manager", "--", "bash", "-c", s }),
            ("konsole", s => new[] { "--nofork", "-e", "bash", "-c", s }),
            ("mate-terminal", s => new[] { "--disable-factory", "-x", "bash", "-c", s }),
            ("xterm", s => new[] { "-T", "Connection Manager", "-e", "bash", "-c", s }),
            ("x-terminal-emulator", s => new[] { "-e", "bash", "-c", s }),
        };
        foreach (var (name, args) in candidates)
            if (FindProgram(name) is { } exe)
                return (exe, args(script));
        return null;
    }

    // --- start with Ubuntu ------------------------------------------------------------------

    private static string AutostartFile => Path.Combine(ConfigHome, "autostart", "connection-manager.desktop");

    public bool GetStartWithOs() => File.Exists(AutostartFile);

    public void SetStartWithOs(bool enabled)
    {
        if (!enabled)
        {
            if (File.Exists(AutostartFile)) File.Delete(AutostartFile);
            return;
        }
        WriteAutostart(Environment.ProcessPath ?? "connection-manager");
    }

    private static void WriteAutostart(string exe)
    {
        string icon = ModeOf(exe) == InstallMode.System || exe == "/usr/bin/connection-manager"
            ? "connection-manager"
            : Path.Combine(Path.GetDirectoryName(exe) ?? "", "icon.png");
        Directory.CreateDirectory(Path.GetDirectoryName(AutostartFile)!);
        File.WriteAllText(AutostartFile,
            "[Desktop Entry]\n" +
            "Type=Application\n" +
            "Name=Connection Manager\n" +
            "Comment=Shared remote connections (RDP, Web, PuTTY, RustDesk)\n" +
            $"Exec=\"{exe}\" --autostart\n" +
            $"Icon={icon}\n" +
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

        if (downloadedFile.EndsWith(".deb", StringComparison.OrdinalIgnoreCase))
        {
            // System install: apt installs the new package (one password window).
            string? err = await InstallPackagesAsync(new[] { downloadedFile });
            if (err != null) return err;
            string installed = Path.Combine(SystemDir, ExeName);
            if (!File.Exists(installed)) return "The update was not installed.";
            Start(installed, new[] { "--updated" });
            return null;
        }

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
        if (!File.Exists(Path.Combine(src, ExeName)))
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
            return $"No permission to replace the program in {installDir}.";
        }
        return null;
    }
}
