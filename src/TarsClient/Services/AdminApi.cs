using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;

namespace TarsClient.Services;

/// <summary><c>/api/admin/*</c> over the pinned TLS channel. Every call carries <c>x-tars-key</c>.</summary>
public sealed partial class AdminApi
{
    #region Fields

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http = Tls.CreateHttpClient(TimeSpan.FromSeconds(15));
    private readonly Func<(string url, string key)> _config;

    // The spec line reads: **Access key** (`x-tars-key` header): `KEY`. We take the last code span, not the header name.
    [GeneratedRegex(@"\*\*Access key\*\*[^\r\n]*`([^`\s]+)`[^`\r\n]*$", RegexOptions.Multiline)]
    private static partial Regex AccessKeyLine();

    #endregion

    #region Constructor

    /// <summary>Creates the client; <paramref name="config"/> is read on every call so URL and key changes apply at once.</summary>
    public AdminApi(Func<(string url, string key)> config) => _config = config;

    #endregion

    #region Public Methods

    /// <summary>Maps the WebSocket URL to the HTTP base: <c>wss://host:port/ws</c> → <c>https://host:port</c>.</summary>
    public static string HttpBase(string wsUrl)
    {
        var scheme = wsUrl.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) ? "http" : "https";
        return new UriBuilder(wsUrl) { Scheme = scheme, Path = "", Query = "" }.Uri.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>Pulls the access key out of the spec the server fills in for allowlisted PCs.</summary>
    public static string? ExtractAccessKey(string markdown)
    {
        var m = AccessKeyLine().Match(markdown);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary><c>GET /api/health</c>.</summary>
    public Task<JsonElement> HealthAsync() => GetAsync<JsonElement>("/api/health");

    /// <summary><c>GET /api/admin/config</c>.</summary>
    public Task<ConfigResponse> GetConfigAsync() => GetAsync<ConfigResponse>("/api/admin/config");

    /// <summary><c>PATCH /api/admin/config</c>, optionally restarting the server.</summary>
    public async Task PatchConfigAsync(Dictionary<string, string> changes, bool restart)
    {
        using var _ = await SendAsync(Request(HttpMethod.Patch, $"/api/admin/config?restart={(restart ? "true" : "false")}", JsonContent.Create(changes)));
    }

    /// <summary><c>GET /api/admin/files</c>.</summary>
    public async Task<List<ServerFile>> GetFilesAsync()
    {
        var root = await GetAsync<JsonElement>("/api/admin/files");
        var array = root.ValueKind == JsonValueKind.Array ? root : root.GetProperty("files");
        return array.Deserialize<List<ServerFile>>(Json) ?? [];
    }

    /// <summary>Reads one knowledge or persona file as text.</summary>
    public async Task<string> GetFileAsync(string area, string name)
    {
        using var response = await SendAsync(Request(HttpMethod.Get, FilePath(area, name)));
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>Saves one knowledge or persona file.</summary>
    public async Task PutFileAsync(string area, string name, string text)
    {
        using var _ = await SendAsync(Request(HttpMethod.Put, FilePath(area, name), new StringContent(text, Encoding.UTF8, "text/plain")));
    }

    /// <summary>Deletes a knowledge file (the server protects <c>core.md</c> and the persona).</summary>
    public async Task DeleteFileAsync(string name)
    {
        using var _ = await SendAsync(Request(HttpMethod.Delete, FilePath("knowledge", name)));
    }

    /// <summary><c>GET /api/admin/status</c> with the last <paramref name="lines"/> log lines.</summary>
    public Task<JsonElement> GetStatusAsync(int lines, CancellationToken ct = default) =>
        GetAsync<JsonElement>($"/api/admin/status?lines={lines}", ct);

    /// <summary><c>POST /api/admin/restart</c>.</summary>
    public async Task RestartAsync()
    {
        using var _ = await SendAsync(Request(HttpMethod.Post, "/api/admin/restart"));
    }

    /// <summary>Whether the stored key opens the admin API. An unreachable server counts as yes: that isn't the key's fault.</summary>
    public async Task<bool> KeyWorksAsync()
    {
        try
        {
            await GetConfigAsync();
            return true;
        }
        catch (AdminException ex) when (ex.Message.StartsWith("401", StringComparison.Ordinal)) { return false; }
        catch (Exception ex) when (ex is AdminException or JsonException) { return true; }
    }

    /// <summary>
    /// The server fills the current access key into <c>/docs/desktop-client-spec.md</c> for allowlisted PCs, so a
    /// fresh install provisions itself without the key ever living in the repo or the installer.
    /// </summary>
    public async Task<string?> FetchKeyFromSpecAsync()
    {
        try
        {
            using var response = await SendAsync(Request(HttpMethod.Get, "/docs/desktop-client-spec.md"));
            return ExtractAccessKey(await response.Content.ReadAsStringAsync());
        }
        catch (AdminException ex)
        {
            Log.Write($"key provisioning: {ex.Message}");
            return null;
        }
    }

    #endregion

    #region Private Methods

    private static string FilePath(string area, string name) =>
        $"/api/admin/files/{Uri.EscapeDataString(area)}/{Uri.EscapeDataString(name)}";

    private HttpRequestMessage Request(HttpMethod method, string path, HttpContent? content = null)
    {
        var (url, key) = _config();
        var request = new HttpRequestMessage(method, HttpBase(url) + path) { Content = content };
        if (!string.IsNullOrWhiteSpace(key)) request.Headers.Add("x-tars-key", key.Trim());
        return request;
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken ct = default)
    {
        using var response = await SendAsync(Request(HttpMethod.Get, path), ct);
        return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
    {
        HttpResponseMessage response;
        try { response = await _http.SendAsync(request, ct); }
        catch (HttpRequestException ex) { throw new AdminException($"NO CARRIER: {ex.InnerException?.Message ?? ex.Message}"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new AdminException("TIMEOUT"); }

        if (response.IsSuccessStatusCode) return response;

        var reason = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(reason);
            if (doc.RootElement.TryGetProperty("detail", out var detail)) reason = detail.ToString();
        }
        catch (JsonException) { /* not JSON: keep the raw body */ }

        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new AdminException("401 ACCESS KEY REJECTED: set it under SYSTEM"),
            HttpStatusCode.Forbidden => new AdminException("403 NOT ON ALLOWLIST"),
            _ => new AdminException($"{(int)response.StatusCode} {reason}".Trim()),
        };
    }

    #endregion
}
