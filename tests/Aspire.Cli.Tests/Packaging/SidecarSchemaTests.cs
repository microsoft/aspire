// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;

namespace Aspire.Cli.Tests.Packaging;

/// <summary>
/// Verifies that the source-of-truth <c>.aspire-install.json</c> sidecar shipped in the
/// dotnet-tool RID-specific nupkgs is valid JSON and matches the route sidecar schema:
/// a <c>route</c> string and an optional <c>updateCommand</c> string.
/// </summary>
public class SidecarSchemaTests
{
    [Fact]
    public void DotnetToolSidecar_IsValidJsonAndMatchesSchema()
    {
        var sidecarPath = Path.Combine(GetRepoRoot(), "src", "Aspire.Cli", ".aspire-install.json");

        Assert.True(File.Exists(sidecarPath), $"Expected sidecar at {sidecarPath}");

        var content = File.ReadAllText(sidecarPath);
        using var doc = JsonDocument.Parse(content);

        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);

        Assert.True(doc.RootElement.TryGetProperty("route", out var routeElement),
            "Sidecar must contain a 'route' property.");
        Assert.Equal(JsonValueKind.String, routeElement.ValueKind);
        Assert.Equal("dotnet-tool", routeElement.GetString());

        Assert.True(doc.RootElement.TryGetProperty("updateCommand", out var updateCommandElement),
            "Sidecar must contain an 'updateCommand' property.");
        Assert.Equal(JsonValueKind.String, updateCommandElement.ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(updateCommandElement.GetString()),
            "'updateCommand' must be non-empty.");
        Assert.Equal("dotnet tool update -g Aspire.Cli", updateCommandElement.GetString());
    }

    private static string GetRepoRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
