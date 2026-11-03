// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Tests for .github/workflows/update-actionlint.js, which proposes bumps to the
/// .github/actionlint-version.json pin.
/// </summary>
public sealed class UpdateActionlintTests : IDisposable
{
    private const string FixtureSha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string NewArchiveContent = "actionlint 1.7.13 archive bytes";

    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string s_newArchiveSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(NewArchiveContent)));

    private readonly TemporaryWorkspace _workspace;
    private readonly string _repoRoot;
    private readonly string _harnessPath;
    private readonly ITestOutputHelper _output;

    public UpdateActionlintTests(ITestOutputHelper output)
    {
        _output = output;
        _workspace = TemporaryWorkspace.Create(output);
        _repoRoot = RepoRoot.Path;
        _harnessPath = Path.Combine(_repoRoot, "tests", "Infrastructure.Tests", "WorkflowScripts", "update-actionlint.harness.js");
    }

    public void Dispose() => _workspace.Dispose();

    [Fact]
    [RequiresTools(["node"])]
    public async Task ReadPinParsesCommittedPinFile()
    {
        var content = await File.ReadAllTextAsync(Path.Combine(_repoRoot, ".github", "actionlint-version.json"));
        using var document = JsonDocument.Parse(content);
        var expectedPin = new Pin(
            document.RootElement.GetProperty("version").GetString()!,
            document.RootElement.GetProperty("linuxAmd64Sha256").GetString()!);

        var pin = await InvokeAsync<Pin>("readPin", new { content });

        Assert.Equal(expectedPin, pin);
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("""{"version":"1.7","linuxAmd64Sha256":"8aca8db96f1b94770f1b0d72b6dddcb1ebb8123cb3712530b08cc387b349a3d8"}""", "Pinned version '1.7' is not a MAJOR.MINOR.PATCH version.")]
    [InlineData("""{"version":"1.7.12","linuxAmd64Sha256":"ABC"}""", "Pinned linuxAmd64Sha256 'ABC' is not a lowercase SHA-256 hex digest.")]
    [InlineData("""{"version":"1.7.12"}""", "Pin file must contain exactly 'version' and 'linuxAmd64Sha256' but has 'version'.")]
    [InlineData("""{"version":"1.7.12","linuxAmd64Sha256":"8aca8db96f1b94770f1b0d72b6dddcb1ebb8123cb3712530b08cc387b349a3d8","checksums":"https://example.com"}""", "Pin file must contain exactly 'version' and 'linuxAmd64Sha256' but has 'checksums,linuxAmd64Sha256,version'.")]
    public async Task ReadPinRejectsMalformedPins(string content, string expectedError)
    {
        var error = await InvokeExpectingErrorAsync("readPin", new { content });

        Assert.Equal(expectedError, error);
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("1.7.13", "1.7.12", 1)]
    [InlineData("1.10.0", "1.9.99", 1)]
    [InlineData("2.0.0", "1.99.99", 1)]
    [InlineData("1.7.12", "1.7.12", 0)]
    [InlineData("1.7.11", "1.7.12", -1)]
    public async Task CompareVersionsUsesNumericSemverOrdering(string left, string right, int expected)
    {
        Assert.Equal(expected, await InvokeAsync<int>("compareVersions", new { left, right }));
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("1.7.13")]
    [InlineData("v1.7.13-rc.1")]
    [InlineData("v1.7")]
    [InlineData("v01.7.13")]
    public async Task ParseReleaseTagRejectsNonReleaseTags(string tag)
    {
        var error = await InvokeExpectingErrorAsync("parseReleaseTag", new { tag });

        Assert.Equal($"Release tag '{tag}' is not of the form vMAJOR.MINOR.PATCH.", error);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ApplyPinRewritesAllFieldsTogether()
    {
        var content = PinContent("1.7.12");

        var updated = await InvokeAsync<string>("applyPin", new { content, version = "1.7.13", sha256 = s_newArchiveSha256 });

        Assert.Equal(
            $$"""
            {
              "version": "1.7.13",
              "linuxAmd64Sha256": "{{s_newArchiveSha256}}"
            }

            """,
            updated);
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("1.7.12")]
    [InlineData("1.7.11")]
    public async Task ApplyPinRefusesNonUpgrades(string version)
    {
        var error = await InvokeExpectingErrorAsync("applyPin", new { content = PinContent("1.7.12"), version, sha256 = s_newArchiveSha256 });

        Assert.Equal($"Refusing to change actionlint from 1.7.12 to {version}; only upgrades are allowed.", error);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task SelectLinuxAmd64AssetReturnsDigest()
    {
        var asset = await InvokeAsync<Asset>("selectLinuxAmd64Asset", new { release = Release("1.7.13", $"sha256:{s_newArchiveSha256}"), version = "1.7.13" });

        Assert.Equal(
            new Asset(
                "actionlint_1.7.13_linux_amd64.tar.gz",
                "https://github.com/rhysd/actionlint/releases/download/v1.7.13/actionlint_1.7.13_linux_amd64.tar.gz",
                s_newArchiveSha256),
            asset);
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData(null, "Asset 'actionlint_1.7.13_linux_amd64.tar.gz' has no sha256 digest (got 'null').")]
    [InlineData("sha512:abc", "Asset 'actionlint_1.7.13_linux_amd64.tar.gz' has no sha256 digest (got 'sha512:abc').")]
    public async Task SelectLinuxAmd64AssetRequiresSha256Digest(string? digest, string expectedError)
    {
        var error = await InvokeExpectingErrorAsync("selectLinuxAmd64Asset", new { release = Release("1.7.13", digest), version = "1.7.13" });

        Assert.Equal(expectedError, error);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task SelectLinuxAmd64AssetRejectsPrerelease()
    {
        var release = new { tag_name = "v1.7.13", draft = false, prerelease = true, assets = Array.Empty<object>() };

        var error = await InvokeExpectingErrorAsync("selectLinuxAmd64Asset", new { release, version = "1.7.13" });

        Assert.Equal("Release v1.7.13 is a draft or prerelease.", error);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task SelectLinuxAmd64AssetRejectsMissingAsset()
    {
        var release = new { tag_name = "v1.7.13", draft = false, prerelease = false, assets = Array.Empty<object>() };

        var error = await InvokeExpectingErrorAsync("selectLinuxAmd64Asset", new { release, version = "1.7.13" });

        Assert.Equal("Expected exactly one 'actionlint_1.7.13_linux_amd64.tar.gz' asset in release v1.7.13 but found 0.", error);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task SelectLinuxAmd64AssetRejectsUnexpectedDownloadUrl()
    {
        var release = new
        {
            tag_name = "v1.7.13",
            draft = false,
            prerelease = false,
            assets = new[] { new { name = "actionlint_1.7.13_linux_amd64.tar.gz", browser_download_url = "https://example.com/actionlint.tar.gz", digest = $"sha256:{s_newArchiveSha256}" } },
        };

        var error = await InvokeExpectingErrorAsync("selectLinuxAmd64Asset", new { release, version = "1.7.13" });

        Assert.Equal("Asset 'actionlint_1.7.13_linux_amd64.tar.gz' has unexpected download URL 'https://example.com/actionlint.tar.gz'.", error);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task CheckIsNoOpWhenPinIsLatest()
    {
        var result = await InvokeAsync<CheckResult>("check", new { pinContent = PinContent("1.7.12"), release = Release("1.7.12", $"sha256:{FixtureSha256}"), archive = "" });

        Assert.False(result.Updated);
        Assert.Equal("1.7.12", result.PreviousVersion);
        Assert.Equal(["json https://api.github.com/repos/rhysd/actionlint/releases/latest"], result.Calls);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task CheckDownloadsAndVerifiesNewerRelease()
    {
        var result = await InvokeAsync<CheckResult>("check", new { pinContent = PinContent("1.7.12"), release = Release("1.7.13", $"sha256:{s_newArchiveSha256}"), archive = NewArchiveContent });

        Assert.True(result.Updated);
        Assert.Equal("1.7.12", result.PreviousVersion);
        Assert.Equal("1.7.13", result.Version);
        Assert.Equal(s_newArchiveSha256, result.Sha256);
        Assert.Equal("/tmp/out/actionlint_1.7.13_linux_amd64.tar.gz", result.ArchivePath);
        Assert.Equal(
            [
                "json https://api.github.com/repos/rhysd/actionlint/releases/latest",
                "bytes https://github.com/rhysd/actionlint/releases/download/v1.7.13/actionlint_1.7.13_linux_amd64.tar.gz",
                "write /tmp/out/actionlint_1.7.13_linux_amd64.tar.gz",
            ],
            result.Calls);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task CheckFailsWhenDownloadDoesNotMatchDigest()
    {
        var error = await InvokeExpectingErrorAsync("check", new { pinContent = PinContent("1.7.12"), release = Release("1.7.13", $"sha256:{new string('0', 64)}"), archive = NewArchiveContent });

        Assert.Equal(
            $"Downloaded 'actionlint_1.7.13_linux_amd64.tar.gz' has SHA-256 {s_newArchiveSha256} but the release API digest is {new string('0', 64)}.",
            error);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task CheckRefusesToDowngrade()
    {
        var error = await InvokeExpectingErrorAsync("check", new { pinContent = PinContent("1.7.12"), release = Release("1.7.11", $"sha256:{FixtureSha256}"), archive = "" });

        Assert.Equal("Latest actionlint release 1.7.11 is older than the pinned 1.7.12; refusing to downgrade.", error);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task FormatStepOutputsEmitsOnlyStatusForNoOp()
    {
        var outputs = await InvokeAsync<string>("formatStepOutputs", new { result = new { updated = false, previousVersion = "1.7.12" } });

        Assert.Equal("updated=false\nprevious-version=1.7.12\n", outputs);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task FormatStepOutputsEmitsVerifiedCandidateForUpdate()
    {
        var outputs = await InvokeAsync<string>("formatStepOutputs", new
        {
            result = new { updated = true, previousVersion = "1.7.12", version = "1.7.13", sha256 = s_newArchiveSha256, archivePath = "/tmp/out/actionlint_1.7.13_linux_amd64.tar.gz" },
        });

        Assert.Equal(
            $"updated=true\nprevious-version=1.7.12\nversion=1.7.13\nsha256={s_newArchiveSha256}\narchive-path=/tmp/out/actionlint_1.7.13_linux_amd64.tar.gz\n",
            outputs);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ApplyCommandRewritesPinFileInWorkingDirectory()
    {
        var pinDirectory = Directory.CreateDirectory(Path.Combine(_workspace.Path, ".github"));
        var pinPath = Path.Combine(pinDirectory.FullName, "actionlint-version.json");
        await File.WriteAllTextAsync(pinPath, PinContent("1.7.12"));

        using var command = new NodeCommand(_output, "update-actionlint-apply");
        command.WithWorkingDirectory(_workspace.Path);
        var result = await command.ExecuteScriptAsync(
            Path.Combine(_repoRoot, ".github", "workflows", "update-actionlint.js"),
            "apply",
            "1.7.13",
            s_newArchiveSha256);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            $$"""
            {
              "version": "1.7.13",
              "linuxAmd64Sha256": "{{s_newArchiveSha256}}"
            }

            """,
            await File.ReadAllTextAsync(pinPath));
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ApplyCommandLeavesPinUntouchedOnDowngrade()
    {
        var pinDirectory = Directory.CreateDirectory(Path.Combine(_workspace.Path, ".github"));
        var pinPath = Path.Combine(pinDirectory.FullName, "actionlint-version.json");
        var original = PinContent("1.7.12");
        await File.WriteAllTextAsync(pinPath, original);

        using var command = new NodeCommand(_output, "update-actionlint-apply");
        command.WithWorkingDirectory(_workspace.Path);
        var result = await command.ExecuteScriptAsync(
            Path.Combine(_repoRoot, ".github", "workflows", "update-actionlint.js"),
            "apply",
            "1.7.11",
            s_newArchiveSha256);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(original, await File.ReadAllTextAsync(pinPath));
    }

    private static string PinContent(string version) =>
        JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["version"] = version,
            ["linuxAmd64Sha256"] = FixtureSha256,
        });

    private static object Release(string version, string? digest) => new
    {
        tag_name = $"v{version}",
        draft = false,
        prerelease = false,
        assets = new object[]
        {
            new { name = $"actionlint_{version}_checksums.txt", browser_download_url = $"https://github.com/rhysd/actionlint/releases/download/v{version}/actionlint_{version}_checksums.txt", digest = $"sha256:{new string('1', 64)}" },
            new { name = $"actionlint_{version}_linux_amd64.tar.gz", browser_download_url = $"https://github.com/rhysd/actionlint/releases/download/v{version}/actionlint_{version}_linux_amd64.tar.gz", digest },
        },
    };

    private async Task<T> InvokeAsync<T>(string operation, object payload)
    {
        var response = await InvokeHarnessAsync<T>(operation, payload);
        Assert.Null(response.Error);
        return response.Result!;
    }

    private async Task<string> InvokeExpectingErrorAsync(string operation, object payload)
    {
        var response = await InvokeHarnessAsync<JsonElement>(operation, payload);
        Assert.NotNull(response.Error);
        return response.Error!;
    }

    private async Task<HarnessResponse<T>> InvokeHarnessAsync<T>(string operation, object payload)
    {
        var requestPath = Path.Combine(_workspace.Path, $"{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new { operation, payload }, s_jsonOptions));

        using var command = new NodeCommand(_output, "update-actionlint");
        command.WithWorkingDirectory(_repoRoot);

        var result = await command.ExecuteScriptAsync(_harnessPath, requestPath);
        Assert.Equal(0, result.ExitCode);

        var response = JsonSerializer.Deserialize<HarnessResponse<T>>(result.Output, s_jsonOptions);
        Assert.NotNull(response);
        return response!;
    }

    private sealed record HarnessResponse<T>(T? Result, string? Error);

    private sealed record Pin(string Version, string Sha256);

    private sealed record Asset(string Name, string Url, string Sha256);

    private sealed record CheckResult(bool Updated, string PreviousVersion, string? Version, string? Sha256, string? ArchivePath, string[] Calls);
}
