namespace ConnectionManager.Platform;

/// <summary>Which platform this build is for (the Windows client returns WindowsPlatform).</summary>
public static class PlatformFactory
{
    public static IPlatform Create() => OperatingSystem.IsLinux() ? new LinuxPlatform() : throw new PlatformNotSupportedException("This is the Ubuntu build of Connection Manager.");
}
