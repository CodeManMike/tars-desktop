using System.Text.Json.Serialization;

namespace TarsClient.Models;

/// <summary>Source-generated JSON for <see cref="ClientSettings"/> (camelCase, indented).</summary>
[JsonSerializable(typeof(ClientSettings))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
