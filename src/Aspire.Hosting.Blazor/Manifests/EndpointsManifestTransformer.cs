// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>
/// Transforms static web asset manifests for multi-app gateway hosting:
/// prefixes endpoint asset paths and merges multiple runtime manifests into one.
/// </summary>
internal static class EndpointsManifestTransformer
{
    /// <summary>
    /// Reads an endpoints manifest and prefixes every <c>AssetFile</c> with <c>{prefix}/</c>.
    /// Also adds a SPA catch-all fallback endpoint cloned from the <c>index.html</c> entry.
    /// Routes are left unchanged — <c>MapGroup</c> handles URL prefixing at the routing level.
    /// </summary>
    public static async Task<string> PrefixEndpointsAssetFileAsync(string manifestPath, string prefix, CancellationToken ct)
    {
        var manifest = JsonSerializer.Deserialize(
            await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false),
            ManifestJsonContext.Default.EndpointsManifest)!;

        var fallbackEndpoints = new List<EndpointEntry>();

        foreach (var ep in manifest.Endpoints)
        {
            ep.AssetFile = $"{prefix}/{ep.AssetFile}";

            // Clone only the identity (uncompressed) index.html endpoint as a catch-all SPA fallback.
            // We skip compressed variants (those with Content-Encoding selectors) because the
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

        return JsonSerializer.Serialize(manifest, ManifestJsonContext.Default.EndpointsManifest);
    }

    /// <summary>
    /// Merges multiple per-app runtime manifests into a single manifest.
    /// Each app's tree is wrapped under its path prefix node. <c>ContentRootIndex</c> values
    /// are offset for each subsequent app so they point to the correct entry in the
    /// combined <c>ContentRoots</c> array.
    /// </summary>
    public static async Task MergeRuntimeManifestsAsync(
        List<AppManifestPaths> appManifests,
        string outputPath,
        ILogger logger,
        CancellationToken ct)
    {
        var mergedContentRoots = new List<string>();
        var mergedChildren = new Dictionary<string, AssetNode>();

        foreach (var manifest in appManifests)
        {
            var reg = manifest.Registration;
            var runtimePath = manifest.RuntimeManifest;
            var prefix = reg.PathPrefix;

            if (!File.Exists(runtimePath))
            {
                BlazorGatewayLog.RuntimeManifestNotFound(logger, runtimePath);
                continue;
            }

            var appManifest = JsonSerializer.Deserialize(
                await File.ReadAllTextAsync(runtimePath, ct).ConfigureAwait(false),
                ManifestJsonContext.Default.DevelopmentManifest)!;

            var offset = mergedContentRoots.Count;

            mergedContentRoots.AddRange(appManifest.ContentRoots);

            var appChildren = appManifest.Root.Children;
            if (offset > 0 && appChildren is not null)
            {
                foreach (var child in appChildren.Values)
                {
                    child.OffsetContentRootIndices(offset);
                }
            }

            mergedChildren[prefix] = new AssetNode { Children = appChildren };

            BlazorGatewayLog.MergedRuntimeManifest(logger,
                reg.Resource.Name, prefix, offset, appManifest.ContentRoots.Length);
        }

        var merged = new DevelopmentManifest
        {
            ContentRoots = [.. mergedContentRoots],
            Root = new AssetNode { Children = mergedChildren }
        };

        await File.WriteAllTextAsync(
            outputPath,
            JsonSerializer.Serialize(merged, ManifestJsonContext.Default.DevelopmentManifest),
            ct).ConfigureAwait(false);
        BlazorGatewayLog.WroteMergedManifest(logger, outputPath);
    }
}
