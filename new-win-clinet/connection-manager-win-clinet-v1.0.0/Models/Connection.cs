using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ConnectionManager.Models;

/// <summary>
/// One shared connection (RDP machine, web page, PuTTY session or RustDesk computer),
/// as sent by the server. The password is NOT part of this object - it is fetched from
/// the server (GET /api/v1/connections/{id}/secret) only when Connect is pressed.
/// </summary>
public partial class Connection : ObservableObject
{
    [ObservableProperty] private string _id = string.Empty;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _ipAddress = string.Empty;
    [ObservableProperty] private string _location = string.Empty;
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private bool _hasPassword;
    [ObservableProperty] private ConnectionType _connectionType = ConnectionType.RDP;
    [ObservableProperty] private int _sortOrder;
    [ObservableProperty] private string _dateCreated = string.Empty;
    [ObservableProperty] private string _updatedAt = string.Empty;
    [ObservableProperty] private string _updatedBy = string.Empty;

    // Local only (never sent to the server)
    [ObservableProperty] [property: JsonIgnore] private bool _isOnline;
    [ObservableProperty] [property: JsonIgnore] private bool _isSelected;

    /// <summary>Short badge text shown on the left of each row.</summary>
    [JsonIgnore]
    public string Badge => ConnectionType switch
    {
        ConnectionType.RDP => "RDP",
        ConnectionType.Web => "WEB",
        ConnectionType.Putty => "SSH",
        ConnectionType.Ubuntu => "RDP",
        ConnectionType.RustDesk => "RD",
        _ => "?"
    };

    /// <summary>"IP - Type" line under the name (same text as the old program).</summary>
    [JsonIgnore]
    public string Detail => $"{IpAddress} - {ConnectionType}";

    partial void OnConnectionTypeChanged(ConnectionType value)
    {
        OnPropertyChanged(nameof(Badge));
        OnPropertyChanged(nameof(Detail));
    }

    partial void OnIpAddressChanged(string value) => OnPropertyChanged(nameof(Detail));
}

/// <summary>Type of connection. Ubuntu is kept only so older connections still open
/// (it is no longer offered when adding a connection, as in the old program).</summary>
public enum ConnectionType
{
    RDP,
    Web,
    Putty,
    Ubuntu,
    RustDesk
}
