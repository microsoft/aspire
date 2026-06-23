// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Xunit;

namespace Infrastructure.Tests;

public sealed class NixCliPackageTests
{
    private static readonly Dictionary<string, string> s_expectedSystems = new(StringComparer.Ordinal)
    {
        ["aarch64-darwin"] = "osx-arm64",
        ["aarch64-linux"] = "linux-arm64",
        ["x86_64-darwin"] = "osx-x64",
        ["x86_64-linux"] = "linux-x64",
    };

    [Fact]
    public async Task ManifestDescribesExpectedStableReleaseAssets()
    {
        var manifest = await ReadJsonObjectAsync("eng/nix/versions.json");

        var version = GetRequiredString(manifest, "version");
        Assert.Matches(@"^\d+\.\d+\.\d+$", version);
        Assert.Equal($"v{version}", GetRequiredString(manifest, "releaseTag"));

        var systems = GetRequiredObject(manifest, "systems");
        Assert.Equal(
            s_expectedSystems.Keys.Order(StringComparer.Ordinal),
            systems.Select(system => system.Key).Order(StringComparer.Ordinal));

        foreach (var (system, rid) in s_expectedSystems)
        {
            var entry = GetRequiredObject(systems, system);
            var archiveName = $"aspire-cli-{rid}-{version}.tar.gz";
            Assert.Equal(rid, GetRequiredString(entry, "rid"));
            Assert.Equal(archiveName, GetRequiredString(entry, "archiveName"));

            var url = new Uri(GetRequiredString(entry, "url"));
            Assert.Equal("https", url.Scheme);
            Assert.Equal("github.com", url.Host);
            Assert.Equal($"/microsoft/aspire/releases/download/v{version}/{archiveName}", url.AbsolutePath);

            var hash = GetRequiredString(entry, "hash");
            Assert.StartsWith("sha512-", hash, StringComparison.Ordinal);
            Assert.Equal(64, Convert.FromBase64String(hash["sha512-".Length..]).Length);
        }
    }

    [Fact]
    public async Task ManifestVersionMatchesPackageValidationBaselineVersion()
    {
        var manifest = await ReadJsonObjectAsync("eng/nix/versions.json");
        var directoryBuildProps = await ReadRepoFileAsync("src/Directory.Build.props");
        var match = System.Text.RegularExpressions.Regex.Match(
            directoryBuildProps,
            @"<PackageValidationBaselineVersion[^>]*>([^<]+)</PackageValidationBaselineVersion>");

        Assert.True(match.Success, "Expected PackageValidationBaselineVersion in src/Directory.Build.props.");
        Assert.Equal(match.Groups[1].Value, GetRequiredString(manifest, "version"));
    }

    [Fact]
    public async Task FlakeLockPinsNixpkgsInput()
    {
        var flakeLock = await ReadJsonObjectAsync("flake.lock");
        var nodes = GetRequiredObject(flakeLock, "nodes");
        var nixpkgs = GetRequiredObject(nodes, "nixpkgs");
        var locked = GetRequiredObject(nixpkgs, "locked");
        var original = GetRequiredObject(nixpkgs, "original");

        Assert.Equal("github", GetRequiredString(locked, "type"));
        Assert.Equal("NixOS", GetRequiredString(locked, "owner"));
        Assert.Equal("nixpkgs", GetRequiredString(locked, "repo"));
        Assert.StartsWith("sha256-", GetRequiredString(locked, "narHash"), StringComparison.Ordinal);
        Assert.Equal("nixos-unstable", GetRequiredString(original, "ref"));
    }

    private static async Task<JsonObject> ReadJsonObjectAsync(string relativePath)
    {
        var contents = await ReadRepoFileAsync(relativePath);
        return JsonNode.Parse(contents)?.AsObject()
            ?? throw new InvalidOperationException($"Could not parse {relativePath} as a JSON object.");
    }

    private static Task<string> ReadRepoFileAsync(string relativePath)
        => File.ReadAllTextAsync(Path.Combine(RepoRoot.Path, relativePath));

    private static JsonObject GetRequiredObject(JsonObject obj, string propertyName)
    {
        Assert.True(obj.TryGetPropertyValue(propertyName, out var value), $"Expected property '{propertyName}'.");
        return Assert.IsType<JsonObject>(value);
    }

    private static string GetRequiredString(JsonObject obj, string propertyName)
    {
        Assert.True(obj.TryGetPropertyValue(propertyName, out var value), $"Expected property '{propertyName}'.");
        return Assert.IsAssignableFrom<JsonValue>(value).GetValue<string>();
    }
}
