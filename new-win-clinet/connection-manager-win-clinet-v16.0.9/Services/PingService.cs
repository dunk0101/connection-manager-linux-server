using System.Net.NetworkInformation;
using ConnectionManager.Models;

namespace ConnectionManager.Services;

/// <summary>
/// Green/red online dots. Each PC pings the connections itself (only it can see its
/// own network). Same behaviour as the old program's PingService: 3 s timeout, the
/// host is taken from the address with any http(s)://, port and path removed.
/// </summary>
public static class PingService
{
    private const int PingTimeoutMs = 3000;

    public static string HostOf(string address)
    {
        string host = address.Trim();
        if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) host = host[7..];
        else if (host.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) host = host[8..];
        int portIndex = host.IndexOf(':');
        if (portIndex > 0) host = host[..portIndex];
        int pathIndex = host.IndexOf('/');
        if (pathIndex > 0) host = host[..pathIndex];
        return host;
    }

    public static async Task<bool> PingAsync(Connection connection)
    {
        try
        {
            string host = HostOf(connection.IpAddress);
            if (host.Length == 0) return false;
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, PingTimeoutMs);
            return reply.Status == IPStatus.Success;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Ping every connection at the same time and update its dot.</summary>
    public static async Task PingAllAsync(IEnumerable<Connection> connections)
    {
        var list = connections.ToList();
        var results = await Task.WhenAll(list.Select(PingAsync));
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            for (int i = 0; i < list.Count; i++)
                list[i].IsOnline = results[i];
        });
    }
}
