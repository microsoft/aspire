// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Aspire.Templates.Tests;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Tests for eng/pipelines/scripts/Notify-GitHubOnBuildResult.ps1.
///
/// The pure helper functions (branch classification, failures-table editing,
/// row formatting) are exercised by dot-sourcing the script unmodified and
/// invoking the functions directly. Dot-sourcing with a non-notifiable branch
/// makes the script's main routine bail out before any token mint or gh call,
/// so the helpers are left defined in scope with no side effects. ('exit' inside
/// a dot-sourced script only unwinds that script, not the test harness.)
///
/// The -DryRun contract is exercised end-to-end against the real script with a
/// recording fake 'gh' on PATH, asserting that no gh process is ever launched.
/// </summary>
public sealed class NotifyGitHubOnBuildResultTests : IDisposable
{
    private readonly TestTempDirectory _tempDir = new();
    private readonly string _scriptPath;
    private readonly ITestOutputHelper _output;

    public NotifyGitHubOnBuildResultTests(ITestOutputHelper output)
    {
        _output = output;
        _scriptPath = Path.Combine(
            TestUtils.FindRepoRoot()?.FullName ?? throw new InvalidOperationException("Could not find repository root"),
            "eng", "pipelines", "scripts", "Notify-GitHubOnBuildResult.ps1");
    }

    public void Dispose() => _tempDir.Dispose();

    [Theory]
    [InlineData("main", true)]
    [InlineData("release/9.0", true)]
    [InlineData("release/10.1.2", true)]
    [InlineData("main-something", false)]   // pipeline trigger uses a main* wildcard; must match 'main' exactly
    [InlineData("mainline", false)]
    [InlineData("internal/release/9.0", false)]
    [InlineData("feature/anything", false)]
    [RequiresTools(["pwsh"])]
    public async Task TestNotifiableBranch_ClassifiesBranches(string branch, bool expected)
    {
        var (_, outContent) = await RunHarnessAsync($"$__result = Test-NotifiableBranch -Name '{branch}'");

        Assert.Equal(expected ? "True" : "False", outContent.Trim());
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task GhAvailable_ReturnsTrue_WhenGhExitsZero()
    {
        var bin = MakeFakeGhBin(exitCode: 0);
        var (_, r) = await RunHarnessAsync($"""
            $env:PATH = '{bin}'
            $__result = Test-GhAvailable
            """);

        Assert.Equal("True", r.Trim());
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task GhAvailable_ReturnsFalse_WhenGhExitsNonZero()
    {
        var bin = MakeFakeGhBin(exitCode: 3);
        var (_, r) = await RunHarnessAsync($"""
            $env:PATH = '{bin}'
            $__result = Test-GhAvailable
            """);

        Assert.Equal("False", r.Trim());
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task GhAvailable_ReturnsFalse_WhenGhMissingFromPath()
    {
        // PATH points at an empty dir, so `gh` cannot be resolved; the probe must
        // report unavailable rather than let the resolution error escape.
        var empty = Path.Combine(_tempDir.Path, $"empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(empty);
        var (_, r) = await RunHarnessAsync($"""
            $env:PATH = '{empty}'
            $__result = Test-GhAvailable
            """);

        Assert.Equal("False", r.Trim());
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task IssueBodyMatchesMarker_True_WhenMarkerAtStartOfLine()
    {
        var (_, r) = await RunHarnessAsync("""
            $marker = '<!-- aspire-internal-build-broken:main -->'
            $body = "$marker`n`nThe build is failing."
            $__result = Test-IssueBodyMatchesMarker -Body $body -Marker $marker
            """);

        Assert.Equal("True", r.Trim());
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task IssueBodyMatchesMarker_False_WhenMarkerOnlyMidProse()
    {
        // The marker pasted inside a sentence (not at line start) must NOT match,
        // so success-mode never comments on / closes an unrelated issue.
        var (_, r) = await RunHarnessAsync("""
            $marker = '<!-- aspire-internal-build-broken:main -->'
            $body = "see the tracking issue mentioning $marker inline somewhere"
            $__result = Test-IssueBodyMatchesMarker -Body $body -Marker $marker
            """);

        Assert.Equal("False", r.Trim());
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task IssueBodyMatchesMarker_False_WhenMarkerAbsentOrBodyEmpty()
    {
        var (_, r) = await RunHarnessAsync("""
            $marker = '<!-- aspire-internal-build-broken:main -->'
            $a = Test-IssueBodyMatchesMarker -Body 'no marker here' -Marker $marker
            $b = Test-IssueBodyMatchesMarker -Body '' -Marker $marker
            $__result = "$a,$b"
            """);

        Assert.Equal("False,False", r.Trim());
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task NewIssueBody_ContainsMarkerBuildCommitAndFailedStages()
    {
        var (_, body) = await RunHarnessAsync("""
            $Branch = 'main'
            $BuildNumber = 'B9'
            $BuildUrl = 'https://example.test/9'
            $CommitSha = 'abcdef1234567'
            $FailedStages = 'build, assemble'
            $__result = New-IssueBody -Marker '<!-- aspire-internal-build-broken:main -->'
            """);

        Assert.StartsWith("<!-- aspire-internal-build-broken:main -->", body);
        Assert.Contains("[B9](https://example.test/9)", body);
        Assert.Contains("abcdef1234567", body);
        Assert.Contains("**Failed stages:** build, assemble", body);
        Assert.Contains("cc @joperezr @radical", body);
        // The in-body failures table was removed; no managed-region markers remain.
        Assert.DoesNotContain("ci-broken-failures", body);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task NewFailureFollowupComment_ContainsBuildCommitAndFailedStages()
    {
        var (_, comment) = await RunHarnessAsync("""
            $Branch = 'main'
            $BuildNumber = 'B42'
            $BuildUrl = 'https://example.test/42'
            $CommitSha = 'deadbeefcafef00d'
            $FailedStages = 'template_tests'
            $__result = New-FailureFollowupCommentBody
            """);

        Assert.Contains("[B42](https://example.test/42)", comment);
        Assert.Contains("deadbeefcafef00d", comment);
        Assert.Contains("**Failed stages:** template_tests", comment);
        Assert.Contains("cc @joperezr @radical", comment);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task FormatFailedStages_RendersEmDashWhenEmpty()
    {
        var (_, r) = await RunHarnessAsync("""
            $FailedStages = ''
            $__result = Format-FailedStages
            """);

        Assert.Equal("\u2014", r.Trim());
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task DryRunFailureMode_LaunchesNoGhProcess()
    {
        var result = await RunRealScriptDryRunAsync("Failure");

        result.EnsureExitCode(0);
        Assert.Contains("DRY-RUN", result.Output);
        Assert.Contains("gh issue create", result.Output);
        AssertNoGhInvoked();
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task DryRunSuccessMode_LaunchesNoGhProcess()
    {
        var result = await RunRealScriptDryRunAsync("Success");

        result.EnsureExitCode(0);
        Assert.Contains("DRY-RUN", result.Output);
        Assert.Contains("gh issue close --reason completed", result.Output);
        AssertNoGhInvoked();
    }

    private async Task<(CommandResult Result, string OutContent)> RunHarnessAsync(string body)
    {
        var harnessPath = Path.Combine(_tempDir.Path, $"harness-{Guid.NewGuid():N}.ps1");
        var outPath = Path.Combine(_tempDir.Path, $"out-{Guid.NewGuid():N}.txt");

        File.WriteAllText(harnessPath, HarnessTemplate.Replace("# __BODY__", body));

        using var cmd = new PowerShellCommand(harnessPath, _output)
            .WithTimeout(TimeSpan.FromMinutes(2));

        var result = await cmd.ExecuteAsync(
            "-ScriptPath", $"\"{_scriptPath}\"",
            "-Out", $"\"{outPath}\"");

        result.EnsureExitCode(0, "notify harness failed");

        var outContent = File.Exists(outPath) ? File.ReadAllText(outPath) : string.Empty;
        return (result, outContent);
    }

    private async Task<CommandResult> RunRealScriptDryRunAsync(string mode)
    {
        var fakeBin = Path.Combine(_tempDir.Path, $"fakebin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fakeBin);
        var ghCallLog = Path.Combine(_tempDir.Path, $"gh-calls-{Guid.NewGuid():N}.log");
        WriteFakeGh(fakeBin);

        var pathWithFakeGh = fakeBin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");

        using var cmd = new PowerShellCommand(_scriptPath, _output)
            .WithTimeout(TimeSpan.FromMinutes(2))
            .WithEnvironmentVariable("PATH", pathWithFakeGh)
            .WithEnvironmentVariable("GH_CALL_LOG", ghCallLog);

        _lastGhCallLog = ghCallLog;

        return await cmd.ExecuteAsync(
            "-Mode", mode,
            "-Branch", "main",
            "-BuildId", "1",
            "-BuildNumber", "B1",
            "-BuildUrl", "https://example.test/1",
            "-CommitSha", "deadbeefcafef00d",
            "-DryRun");
    }

    private string? _lastGhCallLog;

    private void AssertNoGhInvoked()
    {
        // The recording fake gh appends to GH_CALL_LOG on every invocation, so the
        // file existing at all means dry-run launched gh — which it must never do.
        if (_lastGhCallLog is not null && File.Exists(_lastGhCallLog))
        {
            Assert.Fail($"Dry-run must not launch gh, but the fake gh recorded calls:\n{File.ReadAllText(_lastGhCallLog)}");
        }
    }

    private static void WriteFakeGh(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(
                Path.Combine(dir, "gh.cmd"),
                "@echo off\r\n>>\"%GH_CALL_LOG%\" echo %*\r\nexit /b 0\r\n");
        }
        else
        {
            var ghPath = Path.Combine(dir, "gh");
            File.WriteAllText(ghPath, "#!/bin/sh\nprintf '%s\\n' \"$*\" >> \"$GH_CALL_LOG\"\nexit 0\n");
            File.SetUnixFileMode(
                ghPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    private string MakeFakeGhBin(int exitCode)
    {
        var dir = Path.Combine(_tempDir.Path, $"ghbin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(
                Path.Combine(dir, "gh.cmd"),
                $"@echo off\r\necho gh version 0.0\r\nexit /b {exitCode}\r\n");
        }
        else
        {
            var ghPath = Path.Combine(dir, "gh");
            File.WriteAllText(ghPath, $"#!/bin/sh\necho 'gh version 0.0'\nexit {exitCode}\n");
            File.SetUnixFileMode(
                ghPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        return dir;
    }

    // Dot-sources the shipped script with a non-notifiable branch (so main bails before any
    // gh/token work) and runs the per-test body, which sets $__result. The "# __BODY__" token
    // is replaced with the test body; the script's $ and {} are left verbatim.
    private const string HarnessTemplate = """
        param([string]$ScriptPath, [string]$Out)
        $ErrorActionPreference = 'Stop'
        . "$ScriptPath" -Mode Failure -Branch 'ci-test-harness-not-notifiable' -BuildId '1' -BuildNumber 'B1' -BuildUrl 'https://example.test/1' -CommitSha 'deadbeefcafef00d' -DryRun *> $null
        $Owner = 'microsoft'; $Repo = 'aspire'
        # __BODY__
        [System.IO.File]::WriteAllText($Out, [string]$__result)
        """;
}
