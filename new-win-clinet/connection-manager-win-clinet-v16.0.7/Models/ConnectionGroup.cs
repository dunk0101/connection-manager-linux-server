using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ConnectionManager.Models;

/// <summary>A group of connections that share the same Location.</summary>
public partial class ConnectionGroup : ObservableObject
{
    [ObservableProperty] private string _location = string.Empty;
    [ObservableProperty] private bool _isExpanded = true;

    public ObservableCollection<Connection> Connections { get; } = new();

    public string CountText => $"({Connections.Count})";

    public ConnectionGroup(string location)
    {
        Location = location;
        Connections.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CountText));
    }
}
