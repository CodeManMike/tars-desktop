using System.Text.Json.Serialization;

namespace TarsClient.Models;

/// <summary>One server setting from <c>GET /api/admin/config</c>.</summary>
/// <param name="Key">Setting name, e.g. <c>CLOUD_MODEL</c>.</param>
/// <param name="Type"><c>str</c>, <c>int</c>, <c>bool</c>, <c>list</c> or <c>choice:a,b,c</c>.</param>
/// <param name="Secret">Whether the value comes back masked.</param>
/// <param name="Description">What the setting does.</param>
/// <param name="Value">Current value (masked for secrets).</param>
/// <param name="PendingRestart">Changed but not applied until the server restarts.</param>
public sealed record ConfigField(
    string Key, string Type, bool Secret, string Description, string Value,
    [property: JsonPropertyName("pending_restart")] bool PendingRestart);
