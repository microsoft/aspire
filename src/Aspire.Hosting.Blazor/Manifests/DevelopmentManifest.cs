// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Hosting;

internal class DevelopmentManifest
{
    public string[] ContentRoots { get; set; } = [];
    public AssetNode Root { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

internal class AssetNode
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

internal class AssetMatch
{
    public int ContentRootIndex { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

internal class AssetPattern
{
    public int ContentRootIndex { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
