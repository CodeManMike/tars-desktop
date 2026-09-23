using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TarsClient.Services;

public sealed record ConfigField(
    string Key, string Type, bool Secret, string Description, string Value,
    [property: JsonPropertyName("pending_restart")] bool PendingRestart);

public sealed record ConfigResponse(List<ConfigField> Fields, [property: JsonPropertyName("restart_required")] bool RestartRequired);

public sealed record ServerFile(string Area, string Name, long Bytes, double Modified, bool Deletable);

public sealed class AdminException(string message) : Exception(message);

/// <summary>/api/admin/* over the pinned TLS channel. Every call carries x-tars-key.</summary>
public sealed class AdminApi
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly HttpClient _http = Tls.CreateHttpClient(TimeSpan.FromSeconds(15));
    readonly Func<(string url, string key)> _config;

    public AdminApi(Func<(string url, string key)> config) => _config = config;

    /// <summary>wss://host:port/ws → https://host:port</summary>
    public static string HttpBase(string wsUrl)
    {
        var u = new UriBuilder(wsUrl) { Scheme = wsUrl.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) ? "http" : "https", Path = "", Query = "" };
        return u.Uri.GetLeftPart(UriPartial.Authority);
    }

    HttpRequestMessage Req(HttpMethod method, string path, HttpContent? content = null)
    {
        var (url, key) = _config();
        var req = new HttpRequestMessage(method, HttpBase(url) + path) { Content = content };
        if (!string.IsNullOrWhiteSpace(key)) req.Headers.Add("x-tars-key", key.Trim());
        return req;
    }

    async Task<HttpResponseMessage> Send(HttpRequestMessage req, CancellationToken ct = default)
    {
        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, ct); }
        catch (HttpRequestException ex) { throw new AdminException($"NO CARRIER: {ex.InnerException?.Message ?? ex.Message}"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new AdminException("TIMEOUT"); }
        if (resp.IsSuccessStatusCode) return resp;
        var body = await resp.Content.ReadAsStringAsync(ct);
        var reason = body;
        try { using var d = JsonDocument.Parse(body); if (d.RootElement.TryGetProperty("detail", out var det)) reason = det.ToString(); } catch { }
        throw resp.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new AdminException("401 ACCESS KEY REJECTED: set it under SYSTEM"),
            HttpStatusCode.Forbidden => new AdminException("403 NOT ON ALLOWLIST"),
            _ => new AdminException($"{(int)resp.StatusCode} {reason}".Trim()),
        };
    }

    async Task<T> Get<T>(string path, CancellationToken ct = default)
    {
        using var resp = await Send(Req(HttpMethod.Get, path), ct);
        return (await resp.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    public Task<JsonElement> HealthAsync() => Get<JsonElement>("/api/health");
    public Task<ConfigResponse> GetConfigAsync() => Get<ConfigResponse>("/api/admin/config");

    public async Task PatchConfigAsync(Dictionary<string, string> changes, bool restart)
    {
        using var resp = await Send(Req(HttpMethod.Patch, $"/api/admin/config?restart={(restart ? "true" : "false")}", JsonContent.Create(changes)));
    }

    public async Task<List<ServerFile>> GetFilesAsync()
    {
        var root = await Get<JsonElement>("/api/admin/files");
        var arr = root.ValueKind == JsonValueKind.Array ? root : root.GetProperty("files");
        return arr.Deserialize<List<ServerFile>>(Json) ?? [];
    }

    static string FilePath(string area, string name) => $"/api/admin/files/{Uri.EscapeDataString(area)}/{Uri.EscapeDataString(name)}";

    public async Task<string> GetFileAsync(string area, string name)
    {
        using var resp = await Send(Req(HttpMethod.Get, FilePath(area, name)));
        return await resp.Content.ReadAsStringAsync();
    }

    public async Task PutFileAsync(string area, string name, string text)
    {
        using var resp = await Send(Req(HttpMethod.Put, FilePath(area, name), new StringContent(text, Encoding.UTF8, "text/plain")));
    }

    public async Task DeleteFileAsync(string name)
    {
        using var resp = await Send(Req(HttpMethod.Delete, FilePath("knowledge", name)));
    }

    public Task<JsonElement> GetStatusAsync(int lines, CancellationToken ct = default) => Get<JsonElement>($"/api/admin/status?lines={lines}", ct);

    /// <summary>true = the stored key opens the admin API (or the server is unreachable, which isn't the key's fault).</summary>
    public async Task<bool> KeyWorksAsync()
    {
        try { await GetConfigAsync(); return true; }
        catch (AdminException ex) when (ex.Message.StartsWith("401")) { return false; }
        catch { return true; }
    }

    public async Task RestartAsync()
    {
        using var resp = await Send(Req(HttpMethod.Post, "/api/admin/restart"));
    }

    /// <summary>
    /// The server fills the current access key into /docs/desktop-client-spec.md for allowlisted PCs,
    /// so a fresh install can provision itself without the key ever living in the repo or installer.
    /// </summary>
    public async Task<string?> FetchKeyFromSpecAsync()
    {
        try
        {
            using var resp = await Send(Req(HttpMethod.Get, "/docs/desktop-client-spec.md"));
            var md = await resp.Content.ReadAsStringAsync();
            // The line reads: **Access key** (`x-tars-key` header): `KEY`. Take the last code span, not the header name.
            var m = Regex.Match(md, @"\*\*Access key\*\*[^\r\n]*`([^`\s]+)`[^`\r\n]*$", RegexOptions.Multiline);
            return m.Success ? m.Groups[1].Value : null;
        }
        catch (AdminException ex)
        {
            Log.Write($"key provisioning: {ex.Message}");
            return null;
        }
    }
}
