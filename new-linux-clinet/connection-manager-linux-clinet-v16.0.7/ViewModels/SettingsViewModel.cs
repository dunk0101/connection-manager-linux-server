using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ConnectionManager.ViewModels;

/// <summary>
/// The Settings tab - the same settings as the old program (Remote Desktop clipboard,
/// start with the computer, start minimised), kept on this PC, plus a small Server card.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private bool _loading;

    [ObservableProperty] private bool _shareClipboard;
    [ObservableProperty] private bool _startWithOs;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isStatusSuccess = true;

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        Refresh();
    }

    public string StartWithOsLabel => $"Start Connection Manager with {_main.Platform.OsName}";
    public string ServerUrl => _main.Store.Settings.ServerUrl;
    public string SignedInAs => _main.Store.Settings.Username;

    public void Refresh()
    {
        _loading = true;
        var s = _main.Store.Settings;
        ShareClipboard = s.ShareClipboard;
        // The real start-up entry wins over the saved value (as the old program did).
        StartWithOs = s.StartWithOs || SafeGetStartWithOs();
        StartMinimized = s.StartMinimized;
        _loading = false;
        OnPropertyChanged(nameof(ServerUrl));
        OnPropertyChanged(nameof(SignedInAs));
    }

    private bool SafeGetStartWithOs()
    {
        try { return _main.Platform.GetStartWithOs(); } catch { return false; }
    }

    partial void OnShareClipboardChanged(bool value)
    {
        if (_loading) return;
        _main.Store.Settings.ShareClipboard = value;
        Save();
    }

    partial void OnStartWithOsChanged(bool value)
    {
        if (_loading) return;
        _main.Store.Settings.StartWithOs = value;
        try
        {
            _main.Platform.SetStartWithOs(value);
            Save(value ? $"Connection Manager will start with {_main.Platform.OsName}."
                       : $"Connection Manager will no longer start with {_main.Platform.OsName}.");
        }
        catch (Exception ex)
        {
            Save();
            IsStatusSuccess = false;
            StatusMessage = "Could not change the start-up setting: " + ex.Message;
        }
    }

    partial void OnStartMinimizedChanged(bool value)
    {
        if (_loading) return;
        _main.Store.Settings.StartMinimized = value;
        Save();
    }

    private void Save(string? message = null)
    {
        try
        {
            _main.Store.Save();
            IsStatusSuccess = true;
            StatusMessage = message ?? "Settings saved.";
        }
        catch (Exception ex)
        {
            IsStatusSuccess = false;
            StatusMessage = "Could not save the settings: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task SignOutAsync() => await _main.SignOutAsync();
}
