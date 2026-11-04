using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using ConnectionManager.Services;
using ConnectionManager.ViewModels;

namespace ConnectionManager.Views;

/// <summary>
/// The main window. Closing it hides it to the tray (Exit is in the tray menu), as in the
/// old program. The size and position are remembered (saved on hide and every 60 s), and
/// it always opens in the normal (not maximised) state.
/// Log out, reboot and shutdown are never blocked: when the system closes the window
/// (CloseReason OSShutdown / ApplicationShutdown) it saves and really closes, instead of
/// hiding - otherwise KDE says "Logout cancelled by 'Connection Manager'".
/// </summary>
public partial class MainWindow : Window
{
    private readonly SettingsStore? _store;
    private bool _exiting;
    private bool _boundsRestored;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(60) };

    public MainWindow() => InitializeComponent();   // for the designer

    public MainWindow(MainViewModel vm, SettingsStore store)
    {
        InitializeComponent();
        DataContext = vm;
        _store = store;
        RestoreSize();
        Opened += (_, _) => RestorePosition();
        _saveTimer.Tick += (_, _) => SaveBounds();
        _saveTimer.Start();
    }

    private void RestoreSize()
    {
        var s = _store!.Settings;
        if (s.WindowWidth >= MinWidth) Width = s.WindowWidth;
        if (s.WindowHeight >= MinHeight) Height = s.WindowHeight;
    }

    private void RestorePosition()
    {
        if (_boundsRestored || _store == null) return;
        _boundsRestored = true;
        var s = _store.Settings;
        if (double.IsNaN(s.WindowLeft) || double.IsNaN(s.WindowTop)) return;
        var pos = new PixelPoint((int)s.WindowLeft, (int)s.WindowTop);
        // Only if it would be visible on one of the screens now connected.
        var rect = new PixelRect(pos, new PixelSize((int)Width, (int)Height));
        if (Screens.All.Any(sc => sc.WorkingArea.Intersects(rect)))
            Position = pos;
    }

    private void SaveBounds()
    {
        if (_store == null || !IsVisible || WindowState != WindowState.Normal) return;
        var s = _store.Settings;
        s.WindowLeft = Position.X;
        s.WindowTop = Position.Y;
        s.WindowWidth = Width;
        s.WindowHeight = Height;
        try { _store.Save(); } catch { }
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        RestorePosition();
    }

    public void PrepareToExit()
    {
        SaveBounds();
        _exiting = true;
        _saveTimer.Stop();
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        bool systemClosing = e.CloseReason is WindowCloseReason.OSShutdown
                                           or WindowCloseReason.ApplicationShutdown;
        if (!_exiting && !systemClosing)
        {
            // The user closed the window: hide to the tray instead of closing.
            e.Cancel = true;
            SaveBounds();
            Hide();
        }
        else if (!_exiting)
        {
            // Log out / reboot / shutdown: save and let it close (never block it).
            SaveBounds();
            _exiting = true;
            _saveTimer.Stop();
        }
        base.OnClosing(e);
    }
}
