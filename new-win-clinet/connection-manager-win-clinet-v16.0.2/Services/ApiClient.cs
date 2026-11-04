using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ConnectionManager.Models;
using ConnectionManager.Platform;

namespace ConnectionManager.Services;

/// <summary>A problem reported by (or reaching) the server, with a message for the user.</summary>
public class ApiException : Exception
{
    public HttpStatusCode? Status { get; }
    public bool IsUnauthorized => Status == HttpStatusCode.Unauthorized;
    public bool IsOffline => Status == null;
    public ApiException(string message, HttpStatusCode? status = null) : base(message) => Status = status;
}

public record ConnectionList(int Revision, List<Connection> Connections);
public record SignInResult(string Token, string Username, bool IsAdmin, string ServerVersion);
public record ClientBuild(string Version, string File, string Sha256, long Size, string Url);
public record VersionInfo(string ServerVersion, Dictionary<string, ClientBuild> Clients);
public record ImportResult(int Added, int Skipped);

/// <summary>
/// Talks to the Connection Manager Server JSON API (/api/v1, described in API.md).
/// Every request carries "Authorization: Bearer &lt;device token&gt;".
/// </summary>
public class ApiClient
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
        // Window position is NaN until the window has been placed once.
        NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private readonly HttpClient _http;
    private string _token = string.Empty;

    public string BaseUrl { get; private set; } = string.Empty;
    public string ClientVersion { get; }
    public bool HasToken => !string.IsNullOrEmpty(_token);

    public ApiClient(string clientVersion)
    {
        ClientVersion = clientVersion;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"ConnectionManager/{clientVersion.TrimStart('v')}");
        _http.DefaultRequestHeaders.Add("X-Client-Version", clientVersion);
    }

    public void Configure(string baseUrl, string token)
    {
        BaseUrl = NormaliseUrl(baseUrl);
        _token = token;
    }

    public static string NormaliseUrl(string url)
    {
        url = (url ?? string.Empty).Trim().TrimEnd('/');
        if (url.Length > 0 && !url.Contains("://"))
            url = "https://" + url;
        return url;
    }

    private HttpRequestMessage Request(HttpMethod method, string path, object? body = null)
    {
        if (string.IsNullOrEmpty(BaseUrl))
            throw new ApiException("No server address is set.");
        var req = new HttpRequestMessage(method, BaseUrl + path);
        if (HasToken)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        if (body != null)
            req.Content = JsonContent.Create(body, options: Json);
        return req;
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, object? body = null)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(Request(method, path, body));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new ApiException($"Can't reach the server ({Short(ex)}).");
        }
        using (resp)
        {
            string text = await resp.Content.ReadAsStringAsync();
            JsonNode? node = null;
            try { node = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text); } catch (JsonException) { }
            if (!resp.IsSuccessStatusCode)
            {
                string msg = node?["error"]?.GetValue<string>() ?? $"The server answered {(int)resp.StatusCode} {resp.ReasonPhrase}.";
                throw new ApiException(msg, resp.StatusCode);
            }
            return node;
        }
    }

    private static string Short(Exception ex) =>
        ex is TaskCanceledException ? "timed out" : (ex.InnerException?.Message ?? ex.Message);

    private static T Read<T>(JsonNode? node, string? property = null)
    {
        var n = property == null ? node : node?[property];
        return n.Deserialize<T>(Json) ?? throw new ApiException("The server sent an unexpected answer.");
    }

    // --- sign in -------------------------------------------------------------------

    public async Task<SignInResult> SignInAsync(string baseUrl, string username, string password,
                                                string deviceName, string platform)
    {
        Configure(baseUrl, string.Empty);
        var node = await SendAsync(HttpMethod.Post, "/api/v1/auth/token", new
        {
            username, password, device_name = deviceName, platform, client_version = ClientVersion
        });
        var result = new SignInResult(
            node?["token"]?.GetValue<string>() ?? "",
            node?["username"]?.GetValue<string>() ?? username,
            node?["is_admin"]?.GetValue<bool>() ?? false,
            node?["server_version"]?.GetValue<string>() ?? "");
        _token = result.Token;
        return result;
    }

    public async Task SignOutAsync()
    {
        try { await SendAsync(HttpMethod.Delete, "/api/v1/auth/token"); }
        finally { _token = string.Empty; }
    }

    // --- connections -----------------------------------------------------------------

    public async Task<int> GetRevisionAsync() =>
        Read<int>(await SendAsync(HttpMethod.Get, "/api/v1/revision"), "revision");

    public async Task<ConnectionList> GetConnectionsAsync()
    {
        var node = await SendAsync(HttpMethod.Get, "/api/v1/connections");
        return new ConnectionList(Read<int>(node, "revision"), Read<List<Connection>>(node, "connections"));
    }

    public object ConnectionBody(string name, string ip, string location, string username,
                                 string? password, ConnectionType type) =>
        new { name, ipAddress = ip, location, username, password, connectionType = type.ToString() };

    public async Task<Connection> CreateAsync(object body) =>
        Read<Connection>(await SendAsync(HttpMethod.Post, "/api/v1/connections", body), "connection");

    public async Task<Connection> UpdateAsync(string id, object body) =>
        Read<Connection>(await SendAsync(HttpMethod.Put, $"/api/v1/connections/{Uri.EscapeDataString(id)}", body), "connection");

    public async Task DeleteAsync(string id) =>
        await SendAsync(HttpMethod.Delete, $"/api/v1/connections/{Uri.EscapeDataString(id)}");

    public async Task<Connection> CopyAsync(string id) =>
        Read<Connection>(await SendAsync(HttpMethod.Post, $"/api/v1/connections/{Uri.EscapeDataString(id)}/copy"), "connection");

    public async Task<string> GetPasswordAsync(string id) =>
        Read<string>(await SendAsync(HttpMethod.Get, $"/api/v1/connections/{Uri.EscapeDataString(id)}/secret"), "password");

    public async Task SetOrderAsync(IEnumerable<string> ids) =>
        await SendAsync(HttpMethod.Post, "/api/v1/connections/order", new { ids = ids.ToList() });

    public async Task<ImportResult> ImportAsync(IEnumerable<ImportItem> items)
    {
        var node = await SendAsync(HttpMethod.Post, "/api/v1/connections/import", new { connections = items.ToList() });
        return new ImportResult(Read<int>(node, "added"), Read<int>(node, "skipped"));
    }

    // --- versions and updates ---------------------------------------------------------

    public async Task<VersionInfo> GetVersionAsync()
    {
        var node = await SendAsync(HttpMethod.Get, "/api/v1/version");
        return new VersionInfo(Read<string>(node, "server_version"),
                               node?["clients"].Deserialize<Dictionary<string, ClientBuild>>(Json) ?? new());
    }

    /// <summary>Download a client build to <paramref name="destination"/> and check its SHA-256.</summary>
    public async Task DownloadAsync(ClientBuild build, string destination, IProgress<double>? progress = null)
    {
        using var req = Request(HttpMethod.Get, build.Url);
        using var dl = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        HttpResponseMessage resp;
        try { resp = await dl.SendAsync(req, HttpCompletionOption.ResponseHeadersRead); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        { throw new ApiException($"Download failed ({Short(ex)})."); }
        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
                throw new ApiException($"Download failed ({(int)resp.StatusCode}).", resp.StatusCode);
            long total = resp.Content.Headers.ContentLength ?? build.Size;
            await using (var src = await resp.Content.ReadAsStreamAsync())
            await using (var dst = File.Create(destination))
            {
                var buffer = new byte[81920];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buffer)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n));
                    done += n;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }
        }
        await using var check = File.OpenRead(destination);
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(check)).ToLowerInvariant();
        if (!string.Equals(hash, build.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(destination);
            throw new ApiException("The download was damaged (its checksum did not match). Please try again.");
        }
    }
}
