using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ConnectionManager.Platform;
using ConnectionManager.Services;

namespace ConnectionManager.ViewModels;

/// <summary>
/// The whole window: sign-in, the three tabs (Connections, Settings, About) and the
/// background work - asking the server every 5 s whether the list changed, and pinging
/// the connections (at start-up, then quick retries, then every 30 s, as before).
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(30);
    private static readonly int[] StartupPingRetriesMs = { 2000, 3000, 5000, 10000, 15000, 10000 };

    private int _revision = -1;
    private CancellationTokenSource? _loops;
    private bool _refreshing;

    public IPlatform Platform { get; }
    public SettingsStore Store { get; }
    public ApiClient Api { get; }

    public ConnectionsViewModel Connections { get; }
    public SettingsViewModel Settings { get; }
    public AboutViewModel About { get; }
    public SignInViewModel SignIn { get; }

    [ObservableProperty] private bool _isSignedIn;
    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private bool _isServerReachable = true;
    [ObservableProperty] private string _serverStatusText = "Connecting…";
    [ObservableProperty] private string _offlineMessage = string.Empty;

    // "Upload your old connections?" (Windows, first sign-in only)
    [ObservableProperty] private bool _showImportPrompt;
    [ObservableProperty] private string _importPromptText = string.Empty;
    [ObservableProperty] private bool _isImporting;
    private IReadOnlyList<ImportItem> _importItems = Array.Empty<ImportItem>();

    /// <summary>Raised when the program must close (Exit in the tray, or after an update started).</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Raised when the window must be shown (e.g. the user has to sign in).</summary>
    public event EventHandler? ShowWindowRequested;

    public MainViewModel(IPlatform platform, SettingsStore store)
    {
        Platform = platform;
        Store = store;
        Api = new ApiClient(AppInfo.Version);
        Connections = new ConnectionsViewModel(this);
        Settings = new SettingsViewModel(this);
        About = new AboutViewModel(this);
        SignIn = new SignInViewModel(this);
    }

    public string HeaderSubtitle => IsSignedIn ? $"{Store.Settings.Username} · {HostName}" : "Not signed in";
    private string HostName => Uri.TryCreate(Store.Settings.ServerUrl, UriKind.Absolute, out var u) ? u.Host : Store.Settings.ServerUrl;

    partial void OnIsSignedInChanged(bool value) => OnPropertyChanged(nameof(HeaderSubtitle));

    /// <summary>Called once when the program starts.</summary>
    public async Task StartAsync()
    {
        var s = Store.Settings;
        string token = string.Empty;
        try { token = string.IsNullOrEmpty(s.ProtectedToken) ? "" : Platform.UnprotectSecret(s.ProtectedToken); }
        catch { /* token from another Windows user / PC - sign in again */ }

        if (string.IsNullOrEmpty(token))
        {
            SignIn.Prepare();
            IsSignedIn = false;
            return;
        }
        Api.Configure(s.ServerUrl, token);
        IsSignedIn = true;
        await AfterSignInAsync();
    }

    public async Task OnSignedInAsync()
    {
        _revision = -1;
        IsSignedIn = true;
        Settings.Refresh();
        await AfterSignInAsync();
    }

    private async Task AfterSignInAsync()
    {
        Connections.Apply(Store.LoadCache());          // show the last known list straight away
        await RefreshAsync(force: true);
        StartLoops();
        _ = About.CheckAsync(silent: true);
        OfferOldImport();
    }

    /// <summary>Reload the list from the server. Without force, only when the server's
    /// revision number changed since the last load.</summary>
    public async Task RefreshAsync(bool force = false)
    {
        if (!IsSignedIn || (_refreshing && !force)) return;
        _refreshing = true;
        try
        {
            if (!force)
            {
                int rev = await Api.GetRevisionAsync();
                if (rev == _revision)
                {
                    MarkReachable();
                    return;
                }
            }
            var list = await Api.GetConnectionsAsync();
            _revision = list.Revision;
            var added = Connections.Apply(list.Connections);
            Store.SaveCache(list.Connections);
            MarkReachable();
            if (added.Count > 0)
                _ = PingService.PingAllAsync(added);
        }
        catch (ApiException ex)
        {
            HandleApiError(ex);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void MarkReachable()
    {
        IsServerReachable = true;
        OfflineMessage = string.Empty;
        ServerStatusText = "Connected";
    }

    /// <summary>Common handling of server problems. Returns the message for the caller.</summary>
    public string HandleApiError(ApiException ex)
    {
        if (ex.IsUnauthorized)
        {
            _ = SignOutAsync("This PC was signed out by the server. Please sign in again.", callServer: false);
        }
        else if (ex.IsOffline)
        {
            IsServerReachable = false;
            ServerStatusText = "Offline";
            OfflineMessage = $"{ex.Message} Showing the last list received - retrying every few seconds.";
        }
        return ex.Message;
    }

    private void StartLoops()
    {
        _loops?.Cancel();
        _loops = new CancellationTokenSource();
        var ct = _loops.Token;
        _ = PollLoopAsync(ct);
        _ = PingLoopAsync(ct);
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                await RefreshAsync();
        }
        catch (OperationCanceledException) { }
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        try
        {
            await PingService.PingAllAsync(Connections.AllConnections());
            // A few quick retries to catch machines that weren't ready at start-up.
            foreach (int ms in StartupPingRetriesMs)
            {
                await Task.Delay(ms, ct);
                await PingService.PingAllAsync(Connections.AllConnections());
            }
            using var timer = new PeriodicTimer(PingInterval);
            while (await timer.WaitForNextTickAsync(ct))
                await PingService.PingAllAsync(Connections.AllConnections());
        }
        catch (OperationCanceledException) { }
    }

    public async Task SignOutAsync(string message = "", bool callServer = true)
    {
        _loops?.Cancel();
        if (callServer)
        {
            try { await Api.SignOutAsync(); } catch (ApiException) { /* signed out locally anyway */ }
        }
        Api.Configure(Store.Settings.ServerUrl, string.Empty);
        Store.Settings.ProtectedToken = string.Empty;
        Store.Save();
        Store.ClearCache();
        _revision = -1;
        Connections.Apply(new());
        SignIn.Prepare(message);
        IsSignedIn = false;
        SelectedTabIndex = 0;
        ShowWindowRequested?.Invoke(this, EventArgs.Empty);
    }

    // --- old connections (Windows) ------------------------------------------------------

    private void OfferOldImport()
    {
        if (Store.Settings.OldImportOffered) return;
        try { _importItems = Platform.ReadOldConnections(); } catch { _importItems = Array.Empty<ImportItem>(); }
        if (_importItems.Count == 0)
        {
            Store.Settings.OldImportOffered = true;
            Store.Save();
            return;
        }
        ImportPromptText = $"This PC has {_importItems.Count} connection(s) saved by the old Connection Manager. " +
                           "Upload them to the server so every PC can use them? Connections whose IP address " +
                           "or URL is already on the server are skipped.";
        ShowImportPrompt = true;
    }

    [RelayCommand]
    private async Task UploadOldConnectionsAsync()
    {
        IsImporting = true;
        try
        {
            var result = await Api.ImportAsync(_importItems);
            Connections.Status = $"Uploaded {result.Added} connection(s); {result.Skipped} were already on the server.";
            Store.Settings.OldImportOffered = true;
            Store.Save();
            ShowImportPrompt = false;
            await RefreshAsync(force: true);
        }
        catch (ApiException ex)
        {
            ImportPromptText = "Upload failed: " + HandleApiError(ex);
        }
        finally
        {
            IsImporting = false;
        }
    }

    [RelayCommand]
    private void DismissImport()
    {
        Store.Settings.OldImportOffered = true;
        Store.Save();
        ShowImportPrompt = false;
    }

    // --- exit --------------------------------------------------------------------------

    public void RequestExit()
    {
        _loops?.Cancel();
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ShowAbout() => SelectedTabIndex = 2;
}
