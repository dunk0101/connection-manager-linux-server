using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ConnectionManager.Models;

namespace ConnectionManager.ViewModels;

/// <summary>
/// The Add Connection / Edit Connection dialog (one dialog for both, same fields and
/// checks as the old AddConnectionWindow and EditConnectionWindow).
/// </summary>
public partial class ConnectionEditorViewModel : ObservableObject
{
    private readonly ConnectionsViewModel _owner;
    private readonly Connection? _original;

    public bool IsEdit => _original != null;
    public string Title => IsEdit ? "Edit Connection" : "Add New Connection";
    public string PasswordLabel => IsEdit ? "Password (leave blank to keep current)" : "Password (optional)";
    public List<string> KnownLocations { get; }

    [ObservableProperty] private ConnectionType _connectionType = ConnectionType.RDP;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _ipAddress = string.Empty;
    [ObservableProperty] private string _location = string.Empty;
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private bool _isSaving;

    public ConnectionEditorViewModel(ConnectionsViewModel owner, Connection? original, List<string> knownLocations)
    {
        _owner = owner;
        _original = original;
        KnownLocations = knownLocations;
        if (original != null)
        {
            ConnectionType = original.ConnectionType;
            Name = original.Name;
            IpAddress = original.IpAddress;
            Location = original.Location;
            Username = original.Username;
        }
    }

    // Radio buttons (Ubuntu is not offered any more, but an Ubuntu connection keeps its type).
    public bool IsRdp { get => ConnectionType == ConnectionType.RDP; set { if (value) ConnectionType = ConnectionType.RDP; } }
    public bool IsWeb { get => ConnectionType == ConnectionType.Web; set { if (value) ConnectionType = ConnectionType.Web; } }
    public bool IsPutty { get => ConnectionType == ConnectionType.Putty; set { if (value) ConnectionType = ConnectionType.Putty; } }
    public bool IsRustDesk { get => ConnectionType == ConnectionType.RustDesk; set { if (value) ConnectionType = ConnectionType.RustDesk; } }

    /// <summary>Username and password are only asked for RDP and PuTTY (and old Ubuntu ones).</summary>
    public bool ShowCredentials => ConnectionType is ConnectionType.RDP or ConnectionType.Putty or ConnectionType.Ubuntu;

    public string AddressLabel => ConnectionType switch
    {
        ConnectionType.Web => "URL *",
        ConnectionType.RustDesk => "RustDesk ID *",
        _ => "IP Address / URL *"
    };

    partial void OnConnectionTypeChanged(ConnectionType value)
    {
        OnPropertyChanged(nameof(IsRdp));
        OnPropertyChanged(nameof(IsWeb));
        OnPropertyChanged(nameof(IsPutty));
        OnPropertyChanged(nameof(IsRustDesk));
        OnPropertyChanged(nameof(ShowCredentials));
        OnPropertyChanged(nameof(AddressLabel));
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        ErrorMessage = string.Empty;
        if (string.IsNullOrWhiteSpace(Name)) { ErrorMessage = "Connection name is required."; return; }
        if (string.IsNullOrWhiteSpace(IpAddress)) { ErrorMessage = "IP address or URL is required."; return; }
        if (string.IsNullOrWhiteSpace(Location)) { ErrorMessage = "Location is required for grouping."; return; }

        // As before, a connection type without credentials keeps none.
        string user = ShowCredentials ? (Username ?? "").Trim() : string.Empty;
        string? password = ShowCredentials && !string.IsNullOrEmpty(Password) ? Password : null;
        var body = new Dictionary<string, object?>
        {
            ["name"] = Name.Trim(),
            ["ipAddress"] = IpAddress.Trim(),
            ["location"] = (Location ?? "").Trim(),
            ["username"] = user,
            ["password"] = password,
            ["connectionType"] = ConnectionType.ToString(),
        };
        if (IsEdit && !ShowCredentials)
            body["clearPassword"] = true;

        IsSaving = true;
        string? error = await _owner.SaveEditorAsync(_original, body);
        IsSaving = false;
        if (error != null)
            ErrorMessage = error;
        else
            _owner.CloseEditor();
    }

    [RelayCommand]
    private void Cancel() => _owner.CloseEditor();
}
