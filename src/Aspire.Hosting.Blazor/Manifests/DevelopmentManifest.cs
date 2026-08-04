// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// These types mirror the static web assets development runtime manifest format
// produced by the .NET SDK's GenerateStaticWebAssetsDevelopmentManifest MSBuild task
// (see: https://github.com/dotnet/sdk/blob/main/src/StaticWebAssetsSdk/Tasks/GenerateStaticWebAssetsDevelopmentManifest.cs)
// and consumed by ASP.NET Core's UseStaticWebAssets() at development time.
//
// The model is intentionally a subset — we only declare properties we need to inspect
// or modify (ContentRoots, Children tree, ContentRootIndex). Properties like SubPath,
// Pattern, and Depth are preserved via [JsonExtensionData] during round-trip serialization,
// ensuring forward-compatibility as the SDK adds new fields across versions.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Hosting;

internal sealed class DevelopmentManifest
{
    public string[] ContentRoots { get; set; } = [];
    public AssetNode Root { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

internal sealed class AssetNode
{
    public Dictionary<string, AssetNode>? Children { get; set; }
    public AssetMatch? Asset { get; set; }
    public AssetPattern[]? Patterns { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public void OffsetContentRootIndices(int offset)
    {
#pragma warning disable IDE0031 // Use null propagation - can't use ?. with +=
        if (Asset is not null)
        {
            Asset.ContentRootIndex += offset;
        }
#pragma warning restore IDE0031

        if (Patterns is not null)
        {
            foreach (var pattern in Patterns)
            {
                pattern.ContentRootIndex += offset;
            }
        }

        if (Children is not null)
        {
            foreach (var child in Children.Values)
            {
                child.OffsetContentRootIndices(offset);
            }
        }
    }
}

internal sealed class AssetMatch
{
    public int ContentRootIndex { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

internal sealed class AssetPattern
{
    public int ContentRootIndex { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
