using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ConnectionManager.Models;
using ConnectionManager.Services;

namespace ConnectionManager.ViewModels;

/// <summary>Sign in to the Connection Manager Server (first run, or after signing out).</summary>
public partial class SignInViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    [ObservableProperty] private string _serverUrl = ClientSettings.DefaultServerUrl;
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private string _infoMessage = string.Empty;
    [ObservableProperty] private bool _isBusy;

    public string VersionText => $"Connection Manager {AppInfo.Version} for {_main.Platform.OsName}";

    public SignInViewModel(MainViewModel main) => _main = main;

    public void Prepare(string message = "")
    {
        var s = _main.Store.Settings;
        ServerUrl = string.IsNullOrWhiteSpace(s.ServerUrl) ? ClientSettings.DefaultServerUrl : s.ServerUrl;
        Username = s.Username;
        Password = string.Empty;
        ErrorMessage = string.Empty;
        InfoMessage = message;
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        ErrorMessage = string.Empty;
        if (string.IsNullOrWhiteSpace(ServerUrl)) { ErrorMessage = "Enter the server address."; return; }
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrEmpty(Password))
        {
            ErrorMessage = "Enter your username and password.";
            return;
        }
        IsBusy = true;
        try
        {
            string url = ApiClient.NormaliseUrl(ServerUrl);
            var result = await _main.Api.SignInAsync(url, Username.Trim(), Password,
                                                     Environment.MachineName, _main.Platform.DownloadKey);
            var s = _main.Store.Settings;
            s.ServerUrl = url;
            s.Username = result.Username;
            s.ProtectedToken = _main.Platform.ProtectSecret(result.Token);
            _main.Store.Save();
            Password = string.Empty;
            InfoMessage = string.Empty;
            await _main.OnSignedInAsync();
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            ErrorMessage = "Sign-in failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
