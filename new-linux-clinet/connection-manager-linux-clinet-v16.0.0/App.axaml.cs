using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using ConnectionManager.Platform;
using ConnectionManager.Services;
using ConnectionManager.ViewModels;
using ConnectionManager.Views;

namespace ConnectionManager;

public partial class App : Application
{
    private MainWindow? _window;
    private TrayIcon? _tray;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            CloseOtherInstances();

            var platform = PlatformFactory.Create();
            var store = new SettingsStore(platform);
            store.Load();
            var vm = new MainViewModel(platform, store);
            _window = new MainWindow(vm, store);

            CreateTrayIcon();

            vm.ExitRequested += (_, _) => ExitApplication(desktop);
            vm.ShowWindowRequested += (_, _) => _window.ShowFromTray();

            // Start hidden in the tray when asked - but only if already signed in.
            bool minimized = desktop.Args?.Contains("--minimized") == true || store.Settings.StartMinimized;
            bool signedIn = !string.IsNullOrEmpty(store.Settings.ProtectedToken);
            if (!minimized || !signedIn)
                desktop.MainWindow = _window;

            Dispatcher.UIThread.Post(async () => await vm.StartAsync());
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Only one Connection Manager runs at a time: starting it closes any other
    /// copy (as the old program did). This is also how an update hands over.</summary>
    private static void CloseOtherInstances()
    {
        try
        {
            var me = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcessesByName(me.ProcessName))
            {
                using (p)
                {
                    if (p.Id == me.Id) continue;
                    try
                    {
                        p.Kill();
                        p.WaitForExit(3000);
                    }
                    catch { /* not ours / already gone */ }
                }
            }
        }
        catch { }
    }

    private void CreateTrayIcon()
    {
        var open = new NativeMenuItem("Open Connection Manager");
        open.Click += (_, _) => _window?.ShowFromTray();
        var exit = new NativeMenuItem("Exit");
        exit.Click += (_, _) => (_window?.DataContext as MainViewModel)?.RequestExit();

        _tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://ConnectionManager/Assets/icon.png"))),
            ToolTipText = "Connection Manager",
            Menu = new NativeMenu { open, new NativeMenuItemSeparator(), exit },
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => _window?.ShowFromTray();
        TrayIcon.SetIcons(this, new TrayIcons { _tray });
    }

    private void ExitApplication(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _window?.PrepareToExit();
        if (_tray != null)
        {
            _tray.IsVisible = false;
            _tray.Dispose();
        }
        desktop.Shutdown();
    }
}
