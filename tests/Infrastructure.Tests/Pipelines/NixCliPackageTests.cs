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
    public async Task FlakeExposesPackageAppOverlayAndChecks()
    {
        var flake = await ReadRepoFileAsync("flake.nix");

        Assert.Contains("description = \"Aspire CLI\";", flake);
        Assert.Contains("nixpkgs.url = \"github:NixOS/nixpkgs/nixos-unstable\";", flake);
        Assert.Contains("aspire-cli = aspireCli;", flake);
        Assert.Contains("default = aspireCli;", flake);
        Assert.Contains("overlays.default = final: _prev:", flake);
        Assert.Contains("program = \"${self.packages.${system}.aspire-cli}/bin/aspire\";", flake);
        Assert.Contains("checks = forAllSystems", flake);
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

    [Fact]
    public async Task DerivationUsesReleaseArchiveAndWritesNixSidecar()
    {
        var packageNix = await ReadRepoFileAsync("eng/nix/package.nix");

        Assert.Contains("src = fetchurl", packageNix);
        Assert.Contains("inherit (entry) url hash;", packageNix);
        Assert.Contains("cp -R . \"$out/lib/aspire-cli/\"", packageNix);
        Assert.Contains("'{\"source\":\"nix\"}'", packageNix);
        Assert.Contains("makeWrapper \"$out/lib/aspire-cli/aspire\" \"$out/bin/aspire\"", packageNix);
        Assert.Contains("autoPatchelfHook", packageNix);
        Assert.Contains("LD_LIBRARY_PATH", packageNix);
        Assert.Contains("binaryNativeCode", packageNix);
        Assert.Contains("mainProgram = \"aspire\";", packageNix);
    }

    [Fact]
    public async Task UpdateScriptUsesStableReleaseChecksums()
    {
        var script = await ReadRepoFileAsync("eng/nix/update-versions.sh");

        Assert.Contains("Nix packaging consumes stable GitHub release assets", script);
        Assert.Contains("releases/download/${release_tag}", script);
        Assert.Contains("archive_name=\"aspire-cli-${rid}-${normalized_version}.tar.gz\"", script);
        Assert.Contains("checksum_url=\"${archive_url}.sha512\"", script);
        Assert.Contains("hex_sha512_to_sri", script);
    }

    [Fact]
    public async Task ReleaseWorkflowUpdatesNixManifestFromLiveAssets()
    {
        var workflow = await ReadRepoFileAsync(".github/workflows/update-nix-cli-flake.yml");

        Assert.Contains("name: Update Nix CLI Flake", workflow);
        Assert.Contains("workflow_dispatch:", workflow);
        Assert.Contains("release_version:", workflow);
        Assert.Contains("eng/nix/update-versions.sh --version", workflow);
        Assert.Contains("uses: ./.github/actions/create-pull-request", workflow);
        Assert.Contains("BRANCH_NAME=\"update-baseline-$VERSION\"", workflow);
        Assert.Contains("branch: update-baseline-${{ inputs.release_version }}", workflow);
        Assert.Contains("creates or updates the PR", workflow);
        Assert.Contains("Dry-run does not fetch release assets", workflow);
        Assert.Contains("actions/create-github-app-token@1b10c78c7865c340bc4f6099eb2f838309f1e8c3", workflow);
    }

    [Fact]
    public async Task ReleasePipelineDispatchesNixUpdateAfterReleaseAssetUpload()
    {
        var pipeline = await ReadRepoFileAsync("eng/pipelines/release-publish-nuget.yml");

        Assert.Contains("- name: SkipNixPackageUpdate", pipeline);
        Assert.Contains("WorkflowFile       'update-nix-cli-flake.yml'", pipeline);
        Assert.Contains("defer_baseline_pr   = $deferBaselinePr", pipeline);
        Assert.Contains("dependsOn: PublishReleaseAssetsJob", pipeline);
        Assert.Contains("eq('${{ parameters.IsPrerelease }}', 'false')", pipeline);
        Assert.Contains("in(dependencies.PublishReleaseAssetsJob.result, 'Succeeded', 'SucceededWithIssues', 'Skipped')", pipeline);

        var assetUploadIndex = pipeline.IndexOf("Verify SHA512s and upload to GitHub Release", StringComparison.Ordinal);
        var nixUpdateIndex = pipeline.IndexOf("- job: UpdateNixPackageJob", StringComparison.Ordinal);
        Assert.True(assetUploadIndex >= 0, "Expected to find the CLI release asset upload step.");
        Assert.True(nixUpdateIndex >= 0, "Expected to find the Nix update job.");
        Assert.True(assetUploadIndex < nixUpdateIndex, "Nix update must run after GitHub release assets are uploaded.");
    }

    [Fact]
    public async Task ReleaseWorkflowCanDeferBaselinePrUntilNixManifestIsUpdated()
    {
        var workflow = await ReadRepoFileAsync(".github/workflows/release-github-tasks.yml");

        Assert.Contains("defer_baseline_pr:", workflow);
        Assert.Contains("Create Branch and Commit for Baseline PR", workflow);
        Assert.Contains("Baseline PR Deferred", workflow);
        Assert.Contains("PR creation is deferred until update-nix-cli-flake.yml commits eng/nix/versions.json to the same branch.", workflow);
        Assert.Contains("inputs.defer_baseline_pr != true", workflow);
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
