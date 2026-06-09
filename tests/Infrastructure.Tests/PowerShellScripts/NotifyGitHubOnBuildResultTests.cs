// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
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
    public async Task NewFailureTableRow_EscapesPipesInFailedStages()
    {
        var (_, row) = await RunHarnessAsync("""
            $CommitSha = 'abcdef1234567'
            $FailedStages = 'build | test'
            $BuildNumber = 'B9'
            $BuildUrl = 'https://example.test/9'
            $__result = New-FailureTableRow -Index 3
            """);

        // A raw pipe would split the markdown table cell; it must be backslash-escaped.
        Assert.Contains(@"build \| test", row);
        Assert.DoesNotContain("build | test", row);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task NewFailureTableRow_ShortensCommitShaToSevenChars()
    {
        var (_, row) = await RunHarnessAsync("""
            $CommitSha = 'abcdef1234567'
            $FailedStages = 'build'
            $BuildNumber = 'B9'
            $BuildUrl = 'https://example.test/9'
            $__result = New-FailureTableRow -Index 3
            """);

        // Displayed sha is shortened to 7 chars, but the commit link uses the full sha.
        Assert.Contains("[`abcdef1`]", row);
        Assert.Contains("/commit/abcdef1234567)", row);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task NewFailureTableRow_HandlesShaShorterThanSevenChars()
    {
        var (_, row) = await RunHarnessAsync("""
            $CommitSha = 'abc'
            $FailedStages = 'build'
            $BuildNumber = 'B9'
            $BuildUrl = 'https://example.test/9'
            $__result = New-FailureTableRow -Index 3
            """);

        // Substring(0, 7) would throw on a sub-7-char sha; the script must fall back to the full value.
        Assert.Contains("[`abc`]", row);
        Assert.Contains("/commit/abc)", row);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task NewFailureTableRow_RendersEmDashForEmptyFailedStages()
    {
        var (_, row) = await RunHarnessAsync("""
            $CommitSha = 'abcdef1234567'
            $FailedStages = ''
            $BuildNumber = 'B9'
            $BuildUrl = 'https://example.test/9'
            $__result = New-FailureTableRow -Index 3
            """);

        Assert.Contains("| \u2014 |", row);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task UpdateFailuresTableInBody_AppendsRowAndPreservesSurroundingProse()
    {
        var (_, body) = await RunHarnessAsync("""
            $begin = '<!-- ci-broken-failures:begin -->'
            $end = '<!-- ci-broken-failures:end -->'
            $hdr = "| # | When (UTC) | Build | Commit | Failed stages |`n|---|------------|-------|--------|---------------|"
            $existing = "INTRO PROSE`n$begin`n$hdr`n| 1 | 2026-01-01 00:00 | [B1](u) | [``abc``](c) | x |`n$end`nTRAILING PROSE"
            $__result = Update-FailuresTableInBody -Body $existing
            """);

        Assert.Equal(2, CountDataRows(body));
        Assert.Contains("| 2 |", body);
        // Human-authored prose outside the managed region must survive the edit.
        Assert.Contains("INTRO PROSE", body);
        Assert.Contains("TRAILING PROSE", body);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task UpdateFailuresTableInBody_RollsOverOldestRowsBeyondCap()
    {
        var (_, body) = await RunHarnessAsync("""
            $begin = '<!-- ci-broken-failures:begin -->'
            $end = '<!-- ci-broken-failures:end -->'
            $hdr = "| # | When (UTC) | Build | Commit | Failed stages |`n|---|------------|-------|--------|---------------|"
            $rows = (1..50 | ForEach-Object { "| $_ | 2026-01-01 00:00 | [B](u) | [``abc``](c) | x |" }) -join "`n"
            $existing = "$begin`n$hdr`n$rows`n$end"
            $__result = Update-FailuresTableInBody -Body $existing
            """);

        // FailuresTableMaxRows = 50: the 51st row drops the oldest, which is summarized as omitted.
        Assert.Equal(50, CountDataRows(body));
        Assert.Matches(@"_\d+ earlier failures omitted", body);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task UpdateFailuresTableInBody_AccountsForExistingOmittedCountInNextIndex()
    {
        var (_, body) = await RunHarnessAsync("""
            $begin = '<!-- ci-broken-failures:begin -->'
            $end = '<!-- ci-broken-failures:end -->'
            $hdr = "| # | When (UTC) | Build | Commit | Failed stages |`n|---|------------|-------|--------|---------------|"
            $omitted = '_3 earlier failures omitted; see issue comments._'
            $existing = "$begin`n$omitted`n`n$hdr`n| 4 | 2026-01-01 00:00 | [B](u) | [``abc``](c) | x |`n| 5 | 2026-01-01 00:00 | [B](u) | [``abc``](c) | x |`n$end"
            $__result = Update-FailuresTableInBody -Body $existing
            """);

        // nextIndex = visibleRows(2) + omitted(3) + 1 = 6
        Assert.Contains("| 6 |", body);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task UpdateFailuresTableInBody_LeavesBodyUnchangedAndWarnsWhenMarkersMissing()
    {
        var (result, body) = await RunHarnessAsync("""
            $existing = 'a hand-edited body with no managed markers'
            $__result = Update-FailuresTableInBody -Body $existing
            """);

        Assert.Equal("a hand-edited body with no managed markers", body);
        Assert.Contains("Could not locate failures-table markers", result.Output);
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

    private static int CountDataRows(string body)
        => Regex.Matches(body, @"(?m)^\|\s*\d+\s*\|").Count;

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
