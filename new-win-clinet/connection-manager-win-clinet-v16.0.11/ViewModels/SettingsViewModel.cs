using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ConnectionManager.Services;

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

    // This PC card (v16.0.8)
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveNameCommand))]
    private string _pcName = string.Empty;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveNameCommand))]
    private bool _isSavingName;
    // The message under the name box (shown in the card, not at the bottom of the tab).
    [ObservableProperty] private string _nameStatus = string.Empty;
    [ObservableProperty] private bool _isNameStatusSuccess = true;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isStatusSuccess = true;

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        Refresh();
    }

    public string StartWithOsLabel => $"Start Connection Manager with {_main.Platform.OsName}";
    public string ServerUrl => _main.Store.Settings.ServerUrl;
    public string DeviceSymbol => _main.DeviceSymbol;

    public string SignedInAs => _main.Store.Settings.Username;

    public void Refresh()
    {
        _loading = true;
        var s = _main.Store.Settings;
        ShareClipboard = s.ShareClipboard;
        // Show whether it REALLY starts with the computer (not just the saved setting).
        StartWithOs = SafeGetStartWithOs();
        StartMinimized = s.StartMinimized;
        PcName = _main.DeviceName;
        _loading = false;
        if (s.StartWithOs && !StartWithOs && SafeIsStartupDisabled())
        {
            IsStatusSuccess = false;
            StatusMessage = $"Start-up is turned off in {_main.Platform.OsName} Startup apps. Tick the box again to turn it back on.";
        }
        OnPropertyChanged(nameof(ServerUrl));
        OnPropertyChanged(nameof(SignedInAs));
    }

    private bool SafeGetStartWithOs()
    {
        try { return _main.Platform.GetStartWithOs(); } catch { return false; }
    }

    private bool SafeIsStartupDisabled()
    {
        try { return _main.Platform.IsStartupDisabledByWindows(); } catch { return false; }
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

    // --- This PC: name and badge (v16.0.8) -------------------------------------------------

    // Same rule as the server (code/db.py clean_device_name).
    private const int MaxPcName = 60;
    private static readonly Regex PcNameRule = new(@"^[\w .()'\-]+$");

    /// <summary>Called when the server's name or badge for this PC changed (sign-in, the
    /// 5-second check, or a rename on the website). The box only follows the new name if
    /// the user is not half-way through typing a different one.</summary>
    public void OnDeviceChanged(string oldName)
    {
        OnPropertyChanged(nameof(DeviceSymbol));
        if (!IsSavingName && (PcName == oldName || string.IsNullOrWhiteSpace(PcName)))
        {
            if (PcName != _main.DeviceName && !string.IsNullOrEmpty(oldName))
            {
                IsNameStatusSuccess = true;
                NameStatus = $"Renamed to {_main.DeviceName} on the server website.";
            }
            PcName = _main.DeviceName;
        }
    }

    private bool CanSaveName() => !IsSavingName && !string.IsNullOrWhiteSpace(PcName);

    [RelayCommand(CanExecute = nameof(CanSaveName))]
    private async Task SaveNameAsync()
    {
        string name = string.Join(' ', (PcName ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (name.Length == 0 || name.Length > MaxPcName || !PcNameRule.IsMatch(name))
        {
            IsNameStatusSuccess = false;
            NameStatus = $"A PC name is 1-{MaxPcName} characters: letters, numbers, spaces and . _ - ( ) '";
            return;
        }
        if (name == _main.DeviceName)
        {
            PcName = name;
            IsNameStatusSuccess = true;
            NameStatus = $"This PC is already called {name}.";
            return;
        }
        IsSavingName = true;
        try
        {
            var device = await _main.Api.RenameDeviceAsync(name);
            IsSavingName = false;
            _main.ApplyDevice(device);
            PcName = device.Name;
            IsNameStatusSuccess = true;
            NameStatus = $"This PC is now called {device.Name}. The website shows the new name too.";
        }
        catch (ApiException ex)
        {
            IsNameStatusSuccess = false;
            NameStatus = "Could not rename this PC: " + _main.HandleApiError(ex);
        }
        finally
        {
            IsSavingName = false;
        }
    }

    [RelayCommand]
    private async Task SignOutAsync() => await _main.SignOutAsync();
}
