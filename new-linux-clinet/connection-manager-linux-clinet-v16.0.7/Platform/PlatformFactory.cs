using System.Diagnostics;
using Avalonia.Platform;

namespace ConnectionManager.Platform;

/// <summary>Which platform this build is for (the Windows client returns WindowsPlatform).</summary>
public static class PlatformFactory
{
    public static IPlatform Create()
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("This is the Ubuntu build of Connection Manager.");

        // First run from the download (e.g. double-clicked after extracting it): install
        // into ~/.local/share/connection-manager with a menu entry, then start that copy.
        string? installed = LinuxPlatform.PrepareInstall(
            () => AssetLoader.Open(new Uri("avares://ConnectionManager/Assets/icon.png")));
        if (installed != null)
        {
            Process.Start(new ProcessStartInfo(installed) { UseShellExecute = false, ArgumentList = { "--installed" } });
            Environment.Exit(0);
        }
        return new LinuxPlatform();
    }
}
