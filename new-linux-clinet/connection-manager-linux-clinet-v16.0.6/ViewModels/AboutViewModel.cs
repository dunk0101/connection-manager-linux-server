using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ConnectionManager.Services;

namespace ConnectionManager.ViewModels;

/// <summary>
/// The About tab: this program's version, the server's version and the newest
/// Connection Manager build on the server, with an Update button when it is newer.
/// </summary>
public partial class AboutViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private ClientBuild? _latest;
    // A version whose automatic update failed: not tried again automatically until restart
    // (so a failed or cancelled install does not repeat every hour).
    private string? _autoUpdateFailedVersion;

    public string ApplicationName => "Connection Manager";
    public string Version => AppInfo.Version;
    public string PlatformName => _main.Platform.OsName;
    public string Copyright => $"© {DateTime.Now.Year} MLTech · Connection Manager";

    public string Description =>
        "Connection Manager keeps one shared list of your remote connections - Remote Desktop (RDP), " +
        "web pages, PuTTY (SSH) and RustDesk - on the Connection Manager Server, so every Windows and " +
        "Ubuntu PC that signs in sees the same list. A change made on one PC appears on the others " +
        "within a few seconds.\n\n" +
        "• Connections grouped by location, with up/down ordering\n" +
        "• One-click connect, with saved usernames and passwords\n" +
        "• Live online status (green / red) checked from this PC\n" +
        "• Passwords are stored encrypted on the server and only fetched when you connect\n" +
        "• Runs in the system tray, can start with the computer\n" +
        "• Updates itself from the server with one click";

    [ObservableProperty] private string _serverVersion = "-";
    [ObservableProperty] private string _latestVersion = "-";
    [ObservableProperty] private bool _isUpdateAvailable;
    [ObservableProperty] private bool _isUpdating;
    [ObservableProperty] private bool _isChecking;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _updateStatus = string.Empty;

    public string UpdateButtonText => _latest == null ? "Update" : $"Update to {_latest.Version}";

    public AboutViewModel(MainViewModel main) => _main = main;

    [RelayCommand]
    private Task CheckForUpdates() => CheckAsync(silent: false);

    public async Task CheckAsync(bool silent)
    {
        if (!_main.IsSignedIn) return;
        IsChecking = true;
        try
        {
            var info = await _main.Api.GetVersionAsync();
            ServerVersion = info.ServerVersion;
            info.Clients.TryGetValue(_main.Platform.DownloadKey, out _latest);
            LatestVersion = _latest?.Version ?? "not available";
            IsUpdateAvailable = _latest != null && AppInfo.Compare(_latest.Version, AppInfo.Version) > 0;
            OnPropertyChanged(nameof(UpdateButtonText));
            if (!silent)
                UpdateStatus = IsUpdateAvailable
                    ? $"Connection Manager {_latest!.Version} is available."
                    : "You have the newest version.";
        }
        catch (ApiException ex)
        {
            string msg = _main.HandleApiError(ex);
            if (!silent) UpdateStatus = msg;
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <summary>
    /// Check the server quietly and, when "Automatically update Connection Manager" is ticked
    /// in Settings and a newer version exists, install it (the program restarts by itself).
    /// Waits while a connection is being added or edited, so nothing typed is lost.
    /// </summary>
    public async Task CheckAndAutoUpdateAsync()
    {
        await CheckAsync(silent: true);
        if (!_main.Store.Settings.AutoUpdate || !IsUpdateAvailable || IsUpdating || _latest == null) return;
        if (_latest.Version == _autoUpdateFailedVersion) return;
        if (_main.Connections.Editor != null) return;   // tried again at the next check

        string version = _latest.Version;
        await UpdateAsync();
        if (!IsUpdating)                                 // still running = the update failed
            _autoUpdateFailedVersion = version;
    }

    [RelayCommand]
    private async Task UpdateAsync()
    {
        if (_latest == null || IsUpdating) return;
        IsUpdating = true;
        Progress = 0;
        UpdateStatus = $"Downloading {_latest.Version}…";
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "connection-manager-update-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, _latest.File);
            await _main.Api.DownloadAsync(_latest, file, new Progress<double>(p => Progress = p * 100));
            UpdateStatus = "Installing - Connection Manager will restart…";
            string? error = await _main.Platform.ApplyUpdateAsync(file);
            if (error == null)
            {
                _main.RequestExit();
                return;
            }
            UpdateStatus = error;
        }
        catch (ApiException ex)
        {
            UpdateStatus = _main.HandleApiError(ex);
        }
        catch (Exception ex)
        {
            UpdateStatus = "Update failed: " + ex.Message;
        }
        IsUpdating = false;
    }
}
