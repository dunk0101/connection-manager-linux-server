namespace ConnectionManager.Platform;

/// <summary>Which platform this build is for (the Ubuntu client returns LinuxPlatform).</summary>
public static class PlatformFactory
{
    public static IPlatform Create() => OperatingSystem.IsWindows()
        ? new WindowsPlatform()
        : throw new PlatformNotSupportedException("This is the Windows build of Connection Manager.");
}
