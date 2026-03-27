// Usage: dotnet run PrefixEndpoints.cs -- <manifest-path> <prefix> <output-path>
// Reads a static web assets endpoints manifest, prefixes every AssetFile with the
// given prefix, adds a SPA catch-all fallback endpoint, and writes the result.
// This script uses the same typed model and logic as EndpointsManifestTransformer
// in the Hosting library.

using System.Text.Json;
using System.Text.Json.Serialization;

if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: dotnet run PrefixEndpoints.cs -- <manifest-path> <prefix> <output-path>");
    return 1;
}

var manifestPath = args[0];
var prefix = args[1];
var outputPath = args[2];

var manifest = JsonSerializer.Deserialize(
    File.ReadAllText(manifestPath),
    ManifestJsonContext.Default.EndpointsManifest)!;

var fallbackEndpoints = new List<EndpointEntry>();

foreach (var ep in manifest.Endpoints)
{
    ep.AssetFile = $"{prefix}/{ep.AssetFile}";

    // Clone only the identity (uncompressed) index.html endpoint as a catch-all SPA fallback.
    // Skip compressed variants (those with Content-Encoding selectors) because the
    // ContentEncodingNegotiationMatcherPolicy would otherwise prefer the catch-all over
    // literal routes (like _blazor/_configuration) that lack encoding metadata.
    if (ep.Route == "index.html")
    {
        var hasContentEncoding = ep.Selectors?.Any(s => s.Name == "Content-Encoding") == true;

        if (!hasContentEncoding)
        {
            // Deep-clone via round-trip serialization, then patch route and cache header
            var fallbackJson = JsonSerializer.Serialize(ep, ManifestJsonContext.Default.EndpointEntry);
            var fallback = JsonSerializer.Deserialize(fallbackJson, ManifestJsonContext.Default.EndpointEntry)!;
            fallback.Route = "{**path:nonfile}";
            if (fallback.ResponseHeaders is not null)
            {
                foreach (var header in fallback.ResponseHeaders)
                {
                    if (header.Name == "Cache-Control")
                    {
                        header.Value = "no-store";
                    }
                }
            }
            fallbackEndpoints.Add(fallback);
        }
    }
}

manifest.Endpoints = [.. manifest.Endpoints, .. fallbackEndpoints];

var dir = Path.GetDirectoryName(outputPath);
if (dir is not null) Directory.CreateDirectory(dir);
File.WriteAllText(outputPath, JsonSerializer.Serialize(manifest, ManifestJsonContext.Default.EndpointsManifest));

return 0;

// Same typed model used by the Hosting library's EndpointsManifestTransformer
class EndpointsManifest
{
    public EndpointEntry[] Endpoints { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

class EndpointEntry
{
    public string Route { get; set; } = "";
    public string AssetFile { get; set; } = "";
    public EndpointSelector[]? Selectors { get; set; }
    public EndpointResponseHeader[]? ResponseHeaders { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

class EndpointSelector
{
    public string Name { get; set; } = "";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

class EndpointResponseHeader
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

[JsonSerializable(typeof(EndpointsManifest))]
[JsonSerializable(typeof(EndpointEntry))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
    WriteIndented = true)]
partial class ManifestJsonContext : JsonSerializerContext
{
}
