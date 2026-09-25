// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public sealed class NixCliPackageTests(ITestOutputHelper outputHelper) : IDisposable
{
    private static readonly Dictionary<string, string> s_expectedSystems = new(StringComparer.Ordinal)
    {
        ["aarch64-darwin"] = "osx-arm64",
        ["aarch64-linux"] = "linux-arm64",
        ["x86_64-darwin"] = "osx-x64",
        ["x86_64-linux"] = "linux-x64",
    };

    private readonly TemporaryWorkspace _workspace = TemporaryWorkspace.Create(outputHelper);

    public void Dispose() => _workspace.Dispose();

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
    [RequiresTools(["bash", "base64", "xxd"])]
    public async Task UpdateVersionsParsesFirstHashTokenFromSha512CompanionFile()
    {
        var outputPath = Path.Combine(_workspace.Path, "versions.json");
        var fakeBinPath = Path.Combine(_workspace.Path, "bin");
        Directory.CreateDirectory(fakeBinPath);

        var curlPath = Path.Combine(fakeBinPath, "curl");
        await File.WriteAllTextAsync(curlPath, """
            #!/usr/bin/env bash
            set -euo pipefail
            url="${@: -1}"
            printf '%s  %s\n' 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' "${url##*/}"
            """);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(curlPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        var result = await RunBashAsync(
            Path.Combine(RepoRoot.Path, "eng", "nix", "update-versions.sh"),
            ["--version", "13.4.0", "--output-path", outputPath],
            new Dictionary<string, string?>
            {
                ["PATH"] = fakeBinPath + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH")
            });

        Assert.True(result.ExitCode == 0, $"Expected update-versions.sh to succeed.{Environment.NewLine}{result.Output}");

        var manifest = await ReadJsonObjectAsync(outputPath);
        var systems = GetRequiredObject(manifest, "systems");
        var expectedHash = "sha512-" + Convert.ToBase64String(Convert.FromHexString(new string('a', 128)));

        foreach (var system in s_expectedSystems.Keys)
        {
            var entry = GetRequiredObject(systems, system);
            Assert.Equal(expectedHash, GetRequiredString(entry, "hash"));
        }
    }

    [Fact]
    [RequiresTools(["bash", "base64", "xxd"])]
    public async Task UpdateVersionsUsesSuppliedSha512OverridesWithoutReadingRelease()
    {
        var outputPath = Path.Combine(_workspace.Path, "versions.json");
        var fakeBinPath = Path.Combine(_workspace.Path, "bin");
        Directory.CreateDirectory(fakeBinPath);

        // A curl stub that always fails. Offline mode (--sha512 supplied) must
        // never invoke curl, so its presence here proves no network read happens.
        var curlPath = Path.Combine(fakeBinPath, "curl");
        await File.WriteAllTextAsync(curlPath, """
            #!/usr/bin/env bash
            echo 'curl should not be called in offline mode' >&2
            exit 1
            """);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(curlPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        // Distinct digest per RID so a mismapped override would fail the assert.
        var hexByRid = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["osx-arm64"] = new string('a', 128),
            ["osx-x64"] = new string('b', 128),
            ["linux-arm64"] = new string('c', 128),
            ["linux-x64"] = new string('d', 128),
        };

        var arguments = new List<string> { "--version", "13.4.0", "--output-path", outputPath };
        foreach (var (rid, hex) in hexByRid)
        {
            arguments.Add("--sha512");
            arguments.Add($"{rid}={hex}");
        }

        var result = await RunBashAsync(
            Path.Combine(RepoRoot.Path, "eng", "nix", "update-versions.sh"),
            arguments.ToArray(),
            new Dictionary<string, string?>
            {
                ["PATH"] = fakeBinPath + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH")
            });

        Assert.True(result.ExitCode == 0, $"Expected update-versions.sh to succeed in offline mode.{Environment.NewLine}{result.Output}");

        var manifest = await ReadJsonObjectAsync(outputPath);
        var systems = GetRequiredObject(manifest, "systems");

        foreach (var (system, rid) in s_expectedSystems)
        {
            var entry = GetRequiredObject(systems, system);
            var expectedHash = "sha512-" + Convert.ToBase64String(Convert.FromHexString(hexByRid[rid]));
            Assert.Equal(expectedHash, GetRequiredString(entry, "hash"));
        }
    }

    [Fact]
    [RequiresTools(["bash", "base64", "xxd"])]
    public async Task UpdateVersionsRejectsPartialSha512Overrides()
    {
        var outputPath = Path.Combine(_workspace.Path, "versions.json");

        // Supply only one platform's digest. Offline mode requires all four, so
        // the script must fail rather than silently curl the missing three.
        var result = await RunBashAsync(
            Path.Combine(RepoRoot.Path, "eng", "nix", "update-versions.sh"),
            ["--version", "13.4.0", "--output-path", outputPath, "--sha512", $"osx-arm64={new string('a', 128)}"],
            new Dictionary<string, string?>
            {
                ["PATH"] = Environment.GetEnvironmentVariable("PATH")
            });

        Assert.False(result.ExitCode == 0, $"Expected update-versions.sh to fail with a partial override set.{Environment.NewLine}{result.Output}");
        Assert.Contains("no --sha512 override supplied for rid", result.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(outputPath), "versions.json must not be written when the override set is incomplete.");
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

    private static async Task<CommandResult> RunBashAsync(string scriptPath, string[] arguments, Dictionary<string, string?> environment)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo.FileName = "bash";
        process.StartInfo.ArgumentList.Add(scriptPath);

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.UseShellExecute = false;

        foreach (var (name, value) in environment)
        {
            process.StartInfo.Environment[name] = value;
        }

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(cancellationTokenSource.Token);

        var output = await outputTask + await errorTask;

        return new CommandResult(process.ExitCode, output);
    }

    private static Task<string> ReadRepoFileAsync(string relativePath)
        => Path.IsPathRooted(relativePath)
            ? File.ReadAllTextAsync(relativePath)
            : File.ReadAllTextAsync(Path.Combine(RepoRoot.Path, relativePath));

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

    private sealed record CommandResult(int ExitCode, string Output);
}