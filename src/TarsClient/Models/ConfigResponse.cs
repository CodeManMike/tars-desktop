using System.Text.Json.Serialization;

namespace TarsClient.Models;

/// <summary>The body of <c>GET /api/admin/config</c>.</summary>
/// <param name="Fields">Every exposed server setting.</param>
/// <param name="RestartRequired">Whether saved changes are waiting for a restart.</param>
public sealed record ConfigResponse(List<ConfigField> Fields, [property: JsonPropertyName("restart_required")] bool RestartRequired);
