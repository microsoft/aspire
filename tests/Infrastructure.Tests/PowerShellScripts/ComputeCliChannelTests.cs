// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Tests for <c>eng/scripts/compute-cli-channel.ps1</c>, which resolves the
/// <c>AspireCliChannel</c> MSBuild property baked into the native CLI binary
/// from AzDO build context (Build.Reason, Build.SourceBranch,
/// System.PullRequest.PullRequestNumber, the queue-time
/// <c>aspireCliChannelOverride</c> parameter, and DotNetFinalVersionKind).
///
/// The script is the canonical computation invoked by
/// <c>eng/pipelines/templates/build_sign_native.yml</c>. These tests pin the
/// cascade ordering — specifically that <c>release/*</c> and
/// <c>internal/release/*</c> source branches resolve to <c>staging</c> even
/// when <c>DotNetFinalVersionKind=release</c> (which is the steady state
/// during stabilization). A regression of that ordering reintroduces
/// https://github.com/microsoft/aspire/issues/17527.
/// </summary>
public sealed class ComputeCliChannelTests
{
    private readonly string _scriptPath;
    private readonly ITestOutputHelper _output;

    public ComputeCliChannelTests(ITestOutputHelper output)
    {
        _output = output;
        _scriptPath = Path.Combine(FindRepoRoot(), "eng", "scripts", "compute-cli-channel.ps1");
    }

    public static TheoryData<string, string, string, string, string, string, string> CascadeCases() => new()
    {
        // The bug fix from https://github.com/microsoft/aspire/issues/17527: a
        // stabilizing release-branch build sets DotNetFinalVersionKind=release
        // (StabilizePackageVersion=true), but the resulting binary must still
        // identify as staging because the packages have not yet been promoted
        // to nuget.org. Without the release-branch arm running before the
        // versionKind=release arm, the staging dogfood build would bake
        // AspireCliChannel=stable and aspire init would drop a nuget.config
        // with no staging feed mapping.
        { "release branch + versionKind=release", "IndividualCI", "refs/heads/release/13.4", "", "auto", "release", "staging" },
        { "release branch + versionKind=prerelease (early stabilization)", "IndividualCI", "refs/heads/release/13.4", "", "auto", "prerelease", "staging" },
        { "main + versionKind=release", "IndividualCI", "refs/heads/main", "", "auto", "release", "stable" },
        { "main + versionKind=prerelease", "IndividualCI", "refs/heads/main", "", "auto", "prerelease", "daily" },
        { "internal/release branch", "IndividualCI", "refs/heads/internal/release/13.4", "", "auto", "release", "staging" },
        { "override=stable beats release-branch arm", "Manual", "refs/heads/release/13.4", "", "stable", "release", "stable" },
        { "override=staging on main", "Manual", "refs/heads/main", "", "staging", "release", "staging" },
        { "override=daily on release branch", "Manual", "refs/heads/release/13.4", "", "daily", "release", "daily" },
        { "override=auto falls through cascade", "IndividualCI", "refs/heads/main", "", "auto", "release", "stable" },
        { "PullRequest with numeric prNumber", "PullRequest", "refs/pull/17528/merge", "17528", "auto", "prerelease", "pr-17528" },
    };

    [Theory]
    [MemberData(nameof(CascadeCases))]
    [RequiresTools(["pwsh"])]
    public async Task ResolvesExpectedChannel(string description, string reason, string sourceBranch, string prNumber, string @override, string versionKind, string expectedChannel)
    {
        _output.WriteLine($"Case: {description}");

        var result = await RunScript(reason, sourceBranch, prNumber, @override, versionKind);

        result.EnsureSuccessful("compute-cli-channel.ps1 failed");
        Assert.Contains($"Aspire CLI channel: {expectedChannel}", result.Output);
        // The AzDO logging command is the consumer contract for
        // build_sign_native.yml; a refactor that drops it would silently break
        // every later step that reads $(aspireCliChannel). Pin it explicitly.
        Assert.Contains($"##vso[task.setvariable variable=aspireCliChannel]{expectedChannel}", result.Output);
        // The build tag is the consumer contract for release-publish-nuget.yml's
        // GA ship-build guard: it asserts the selected source build's tags
        // include `aspire-cli-channel - stable` before publishing to nuget.org.
        // A refactor that drops this tag emission would silently disable the
        // guard and re-introduce the risk that PR #17528 / issue #17527 closed.
        Assert.Contains($"##vso[build.addbuildtag]aspire-cli-channel - {expectedChannel}", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task ThrowsWhenPullRequestPrNumberIsNotNumeric()
    {
        // Catches the case where the AzDO agent leaks the unresolved
        // System.PullRequest.PullRequestNumber macro string into the build —
        // failing here gives clearer attribution than letting
        // IdentityChannelReader.IsValidChannel reject the baked value at CLI
        // startup time.
        var result = await RunScript(
            reason: "PullRequest",
            sourceBranch: "refs/pull/17528/merge",
            prNumber: "$(System.PullRequest.PullRequestNumber)",
            @override: "auto",
            versionKind: "prerelease");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("was not a numeric PR number", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task ThrowsWhenOverrideIsInvalid()
    {
        // The AzDO `values:` enum in azure-pipelines.yml constrains the
        // top-level queue-time UI to the accepted set, but a direct
        // template-caller could still pass an arbitrary string — this is the
        // defense-in-depth that catches that path.
        var result = await RunScript(
            reason: "Manual",
            sourceBranch: "refs/heads/main",
            prNumber: "",
            @override: "garbage",
            versionKind: "prerelease");

        Assert.NotEqual(0, result.ExitCode);
        // The PowerShell exception formatter wraps long messages at terminal width; match
        // on a substring guaranteed to live on a single output line.
        Assert.Contains("aspireCliChannelOverride='garbage'", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task CaseInsensitiveOverrideIsNormalizedToLowercase()
    {
        // PowerShell's `-notin` operator used for validation is
        // case-insensitive by default, but the runtime
        // IdentityChannelReader.IsValidChannel is case-sensitive. Without
        // explicit normalization, a capitalized override would pass validation
        // and produce a binary that throws at startup. Pin the normalization
        // so that defensive behavior doesn't regress.
        var result = await RunScript(
            reason: "Manual",
            sourceBranch: "refs/heads/release/13.4",
            prNumber: "",
            @override: "Stable",
            versionKind: "release");

        result.EnsureSuccessful("compute-cli-channel.ps1 failed");
        Assert.Contains("Aspire CLI channel: stable", result.Output);
    }

    private async Task<CommandResult> RunScript(string reason, string sourceBranch, string prNumber, string @override, string versionKind)
    {
        using var cmd = new PowerShellCommand(_scriptPath, _output)
            .WithTimeout(TimeSpan.FromMinutes(1));

        var args = new List<string>
        {
            "-Reason", $"\"{reason}\"",
            "-SourceBranch", $"\"{sourceBranch}\"",
            "-PrNumber", $"\"{prNumber}\"",
            "-Override", $"\"{@override}\"",
            "-VersionKind", $"\"{versionKind}\""
        };

        return await cmd.ExecuteAsync([.. args]);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Aspire.slnx")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not find repository root");
    }
}
