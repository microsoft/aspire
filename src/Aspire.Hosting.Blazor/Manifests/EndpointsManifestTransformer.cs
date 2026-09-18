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
    /// Also adds a SPA catch-all fallback endpoint cloned from the <c>index.html</c> entry,
    /// but only when the manifest doesn't already define its own <c>{**path:nonfile}</c> fallback.
    /// Routes are left unchanged — <c>MapGroup</c> handles URL prefixing at the routing level.
    /// <para>
    /// TEMPORARY: this is interim scaffolding for the experimental Blazor gateway and will be
    /// removed once the .NET gateway CLI package and the SDK static-web-assets fallback feature
    /// (StaticWebAssetSpaFallbackEnabled, https://github.com/dotnet/aspnetcore/issues/64828) own
    /// SPA fallback routing directly. See the body of <see cref="PrefixEndpointsAssetFileAsync"/>.
    /// </para>
    /// </summary>
    public static async Task<string> PrefixEndpointsAssetFileAsync(string manifestPath, string prefix, CancellationToken ct)
    {
        var manifest = JsonSerializer.Deserialize(
            await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false),
            ManifestJsonContext.Default.EndpointsManifest)
            ?? throw new InvalidOperationException($"Failed to deserialize endpoints manifest from '{manifestPath}'.");

        var fallbackEndpoints = new List<EndpointEntry>();

        // TEMPORARY: this whole transformer is interim scaffolding for the experimental Blazor
        // gateway (ASPIREBLAZOR001) and is expected to be deleted, not evolved. It will be
        // replaced by two proper mechanisms:
        //   1. The .NET gateway CLI package (the .NET 11 gateway), which owns SPA fallback routing
        //      correctly instead of us rewriting each app's endpoints manifest.
        //      See: https://github.com/dotnet/aspnetcore/pull/67599.
        //   2. The SDK static-web-assets fallback feature (StaticWebAssetSpaFallbackEnabled), which
        //      emits the fallback endpoint at build time so hosts don't have to synthesize it.
        //      See: https://github.com/dotnet/aspnetcore/issues/64828.
        // Because it is temporary, we deliberately keep the detection dumb: only skip injection when
        // the manifest already contains the exact "{**path:nonfile}" route (the same literal the SDK
        // emits and that we would otherwise clone). We do NOT try to parse routes with
        // RoutePattern.Parse — that would pull the ASP.NET Core routing framework reference into this
        // hosting library (and the plain-SDK PrefixEndpoints.cs Docker script) just to inspect one
        // string, which isn't worth it for code on its way out.
        //
        // In the hosted Blazor Web App shape the server already owns request routing (Razor components
        // plus its own fallback), so a second {**path:nonfile} catch-all collides with the existing
        // routing and causes the app to return HTTP 500. Skipping injection when a fallback is already
        // present keeps us correct for both the standalone-WASM-plus-gateway shape (none present, so we
        // add one) and the hosted-Web-App shape (already present, so we leave it alone).
        var hasExistingFallback = manifest.Endpoints.Any(
            e => string.Equals(e.Route, "{**path:nonfile}", StringComparison.OrdinalIgnoreCase));

        foreach (var ep in manifest.Endpoints)
        {
            ep.AssetFile = $"{prefix}/{ep.AssetFile}";

            // Clone only the identity (uncompressed) index.html endpoint as a catch-all SPA fallback.
            // We skip compressed variants (those with Content-Encoding selectors) because the
            // ContentEncodingNegotiationMatcherPolicy would otherwise prefer the catch-all over
            // literal routes (like _blazor/_configuration) that lack encoding metadata.
            if (!hasExistingFallback && string.Equals(ep.Route, "index.html", StringComparison.OrdinalIgnoreCase))
            {
                var hasContentEncoding = ep.Selectors?.Any(s => s.Name == "Content-Encoding") == true;

                if (!hasContentEncoding)
                {
                    // Deep-clone via round-trip serialization, then patch route and cache header
                    var fallbackJson = JsonSerializer.Serialize(ep, ManifestJsonContext.Relaxed.EndpointEntry);
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

        return JsonSerializer.Serialize(manifest, ManifestJsonContext.Relaxed.EndpointsManifest);
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
            JsonSerializer.Serialize(merged, ManifestJsonContext.Relaxed.DevelopmentManifest),
            ct).ConfigureAwait(false);
        BlazorGatewayLog.WroteMergedManifest(logger, outputPath);
    }
}
