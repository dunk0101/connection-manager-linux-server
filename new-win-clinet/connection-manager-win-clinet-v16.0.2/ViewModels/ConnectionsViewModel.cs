using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ConnectionManager.Models;
using ConnectionManager.Services;

namespace ConnectionManager.ViewModels;

/// <summary>
/// The Connections tab - works the same way as the old program: groups by Location,
/// Add / Edit / Copy / Delete, double-click to edit, Move Up / Move Down, Connect,
/// green/red online dots. Every change is saved on the server, so all PCs see it.
/// </summary>
public partial class ConnectionsViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public ObservableCollection<ConnectionGroup> ConnectionGroups { get; } = new();

    [ObservableProperty] private Connection? _selectedConnection;
    [ObservableProperty] private ConnectionEditorViewModel? _editor;
    [ObservableProperty] private bool _isEditorOpen;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private bool _isConfirmDeleteOpen;
    [ObservableProperty] private string _confirmDeleteText = string.Empty;
    [ObservableProperty] private bool _isBusy;

    public MainViewModel Main => _main;
    public bool HasSelection => SelectedConnection != null;
    public bool IsEmpty => ConnectionGroups.Count == 0;

    public ConnectionsViewModel(MainViewModel main)
    {
        _main = main;
        ConnectionGroups.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    partial void OnSelectedConnectionChanged(Connection? value)
    {
        foreach (var c in AllConnections())
            c.IsSelected = false;
        if (value != null)
            value.IsSelected = true;
        OnPropertyChanged(nameof(HasSelection));
    }

    public List<Connection> AllConnections() => ConnectionGroups.SelectMany(g => g.Connections).ToList();

    public List<string> Locations() =>
        AllConnections().Select(c => c.Location).Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(l => l, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Show the list from the server. Keeps each connection's online dot, the
    /// selection and which groups are collapsed. Returns connections not seen before
    /// (so they can be pinged straight away).</summary>
    public List<Connection> Apply(List<Connection> connections)
    {
        var previous = AllConnections().ToDictionary(c => c.Id);
        string? selectedId = SelectedConnection?.Id;
        var collapsed = new HashSet<string>(_main.Store.Settings.CollapsedGroups, StringComparer.OrdinalIgnoreCase);
        var added = new List<Connection>();

        ConnectionGroups.Clear();
        var groups = connections
            .GroupBy(c => c.Location, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var g in groups)
        {
            var group = new ConnectionGroup(g.First().Location) { IsExpanded = !collapsed.Contains(g.Key) };
            foreach (var c in g.OrderBy(c => c.SortOrder))
            {
                if (previous.TryGetValue(c.Id, out var old))
                    c.IsOnline = old.IsOnline;
                else
                    added.Add(c);
                group.Connections.Add(c);
            }
            group.PropertyChanged += OnGroupPropertyChanged;
            ConnectionGroups.Add(group);
        }
        SelectedConnection = selectedId == null ? null : AllConnections().FirstOrDefault(c => c.Id == selectedId);
        return added;
    }

    private void OnGroupPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ConnectionGroup.IsExpanded) || sender is not ConnectionGroup g) return;
        var list = _main.Store.Settings.CollapsedGroups;
        list.RemoveAll(l => string.Equals(l, g.Location, StringComparison.OrdinalIgnoreCase));
        if (!g.IsExpanded) list.Add(g.Location);
        _main.Store.Save();
    }

    /// <summary>Run a server change, then reload the list. Shows any problem in the status line.</summary>
    private async Task<bool> RunAsync(Func<Task> action, string? doneMessage = null)
    {
        IsBusy = true;
        try
        {
            await action();
            Status = doneMessage ?? string.Empty;
            await _main.RefreshAsync(force: true);
            return true;
        }
        catch (ApiException ex)
        {
            Status = _main.HandleApiError(ex);
            await _main.RefreshAsync(force: true);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // --- toolbar -----------------------------------------------------------------------

    [RelayCommand]
    private void OpenAddDialog()
    {
        Editor = new ConnectionEditorViewModel(this, null, Locations());
        IsEditorOpen = true;
    }

    [RelayCommand]
    private void OpenEditDialog(Connection? connection = null)
    {
        var conn = connection ?? SelectedConnection;
        if (conn == null) return;
        SelectedConnection = conn;
        Editor = new ConnectionEditorViewModel(this, conn, Locations());
        IsEditorOpen = true;
    }

    public void CloseEditor()
    {
        IsEditorOpen = false;
        Editor = null;
    }

    /// <summary>Save from the Add / Edit dialog. Returns an error message, or null when saved.</summary>
    public async Task<string?> SaveEditorAsync(Connection? original, object body)
    {
        try
        {
            if (original == null)
            {
                var created = await _main.Api.CreateAsync(body);
                Status = $"Added {created.Name}.";
                _pendingSelectId = created.Id;
            }
            else
            {
                await _main.Api.UpdateAsync(original.Id, body);
                Status = $"Saved {original.Name}.";
            }
        }
        catch (ApiException ex)
        {
            return _main.HandleApiError(ex);
        }
        await _main.RefreshAsync(force: true);
        if (_pendingSelectId != null)
        {
            SelectedConnection = AllConnections().FirstOrDefault(c => c.Id == _pendingSelectId) ?? SelectedConnection;
            _pendingSelectId = null;
        }
        // Ping the added / edited connection straight away (its address may have changed).
        if (SelectedConnection != null)
            SelectedConnection.IsOnline = await PingService.PingAsync(SelectedConnection);
        return null;
    }

    private string? _pendingSelectId;

    [RelayCommand]
    private async Task CopyConnectionAsync(Connection? connection = null)
    {
        var conn = connection ?? SelectedConnection;
        if (conn == null) return;
        await RunAsync(async () =>
        {
            var copy = await _main.Api.CopyAsync(conn.Id);
            _pendingSelectId = copy.Id;
        }, $"Copied {conn.Name}.");
        if (_pendingSelectId != null)
        {
            SelectedConnection = AllConnections().FirstOrDefault(c => c.Id == _pendingSelectId) ?? SelectedConnection;
            _pendingSelectId = null;
        }
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (SelectedConnection == null) return;
        ConfirmDeleteText = $"Delete \"{SelectedConnection.Name}\" ({SelectedConnection.IpAddress})? " +
                            "It will be removed from every PC that uses this server.";
        IsConfirmDeleteOpen = true;
    }

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        IsConfirmDeleteOpen = false;
        var conn = SelectedConnection;
        if (conn == null) return;
        if (await RunAsync(() => _main.Api.DeleteAsync(conn.Id), $"Deleted {conn.Name}."))
            SelectedConnection = null;
    }

    [RelayCommand]
    private void CancelDelete() => IsConfirmDeleteOpen = false;

    // --- each row ----------------------------------------------------------------------

    [RelayCommand]
    private async Task ConnectAsync(Connection? connection)
    {
        var conn = connection ?? SelectedConnection;
        if (conn == null) return;
        SelectedConnection = conn;

        string password = string.Empty;
        bool needsPassword = conn.HasPassword && conn.ConnectionType is ConnectionType.RDP or ConnectionType.Putty or ConnectionType.Ubuntu;
        if (needsPassword)
        {
            try
            {
                password = await _main.Api.GetPasswordAsync(conn.Id);
            }
            catch (ApiException ex)
            {
                // Server unreachable: still open the connection; the user can type the password.
                Status = _main.HandleApiError(ex) + " Opening without the saved password.";
            }
        }

        string? error = await _main.Platform.LaunchAsync(conn, password, _main.Store.Settings.ShareClipboard,
            s => Avalonia.Threading.Dispatcher.UIThread.Post(() => Status = s));
        if (error != null)
            Status = error;
        else if (!needsPassword || password.Length > 0)
            Status = $"Opening {conn.Name}…";
    }

    [RelayCommand]
    private Task MoveUpAsync(Connection connection) => MoveAsync(connection, -1);

    [RelayCommand]
    private Task MoveDownAsync(Connection connection) => MoveAsync(connection, +1);

    private async Task MoveAsync(Connection connection, int delta)
    {
        var group = ConnectionGroups.FirstOrDefault(g => g.Connections.Contains(connection));
        if (group == null) return;
        int index = group.Connections.IndexOf(connection);
        int target = index + delta;
        if (target < 0 || target >= group.Connections.Count) return;

        group.Connections.Move(index, target);
        for (int i = 0; i < group.Connections.Count; i++)
            group.Connections[i].SortOrder = i;
        SelectedConnection = connection;
        await RunAsync(() => _main.Api.SetOrderAsync(group.Connections.Select(c => c.Id)));
    }
}
