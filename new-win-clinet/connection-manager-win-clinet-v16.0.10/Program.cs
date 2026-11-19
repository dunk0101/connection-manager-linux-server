using Avalonia;
using Avalonia.Media;

namespace ConnectionManager;

/// <summary>
/// Connection Manager - start of the program.
///
/// Connection Manager keeps one shared list of remote connections (RDP, Web, PuTTY,
/// RustDesk) on the Connection Manager Server. Every PC that signs in sees the same
/// list. Tabs: Connections, Settings, About (with a one-click Update from the server).
///
/// Arguments:  --minimized   start hidden in the tray
/// </summary>
internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter" })
            .LogToTrace();
}
