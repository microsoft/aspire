// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Runs the production hook-maintenance scripts offline, replacing only their GitHub API boundary.
/// Fixtures include first-parent history, canonical plugin versions, and immutable contents responses.
/// </summary>
public sealed class TelemetryHookMaintenanceTests : IDisposable
{
    private const string Repository = "microsoft/aspire-skills";
    private const string BaseVersion = "0.0.2";
    private const string NewVersion = "0.0.3";
    private const string LatestVersion = "0.0.4";
    // Synthetic commit identities for offline fixtures, not actual upstream release pins.
    private const string OldCommit = "1111111111111111111111111111111111111111";
    private const string NewCommit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string LatestCommit = "cccccccccccccccccccccccccccccccccccccccc";
    private const string DevCommit = "dddddddddddddddddddddddddddddddddddddddd";
    private const string RootCommit = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private const string MetadataName = "telemetry-hooks.metadata.json";
    private const string PendingMetadataName = "pending metadata.json";
    private const string PluginPath = ".claude-plugin/plugin.json";
    private const string ShellText = "#!/usr/bin/env bash\necho 'aspire'\n";
    private const string PowerShellText = "#!/usr/bin/env pwsh\nWrite-Output 'aspire'\n";
    private const string NewShellText = "#!/usr/bin/env bash\necho 'new aspire'\n";
    private const string NewPowerShellText = "#!/usr/bin/env pwsh\nWrite-Output 'hook protocol 1.2.3'\n";
    private const string ExpectedSha512 = "00957ac0d67fb6cb43c6dc38038de8dc96f75375d278c907484686d38faf812eeaec1153a0e523507b4afb49cf8f39f9075afb20ab13e71e753a455d3abb78b0";

    private static readonly string[] s_hookNames = ["track-telemetry.sh", "track-telemetry.ps1"];
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true, NewLine = "\n" };
    private readonly TemporaryWorkspace _workspace;
    private readonly string _scriptDirectory;
    private readonly string _hooksDirectory;
    private readonly Dictionary<string, string> _responses = new(StringComparer.Ordinal);
    private readonly ITestOutputHelper _output;

    public TelemetryHookMaintenanceTests(ITestOutputHelper output)
    {
        _output = output;
        _workspace = TemporaryWorkspace.Create(output);
        _scriptDirectory = _workspace.CreateDirectory(Path.Combine("eng", "scripts")).FullName;
        _hooksDirectory = _workspace.CreateDirectory(Path.Combine("src", "Aspire.Cli", "Agents", "Hooks")).FullName;
        foreach (var name in new[] { "telemetry-hooks.common.ps1", "update-telemetry-hooks.ps1", "verify-telemetry-hooks.ps1" })
        {
            File.Copy(Path.Combine(RepoRoot.Path, "eng", "scripts", name), Path.Combine(_scriptDirectory, name));
        }

        // Wrappers load their real common script. Append just the API replacement to the isolated
        // copy, so parameter binding, source validation, staging, and verification all stay real.
        File.AppendAllText(Path.Combine(_scriptDirectory, "telemetry-hooks.common.ps1"), """

            function Invoke-TelemetryHooksGitHubApi {
                param([Parameter(Mandatory = $true)][string]$Endpoint)

                $root = Join-Path $PSScriptRoot '..\..'
                [System.IO.File]::AppendAllText((Join-Path $root 'api-requests.txt'), "$Endpoint`n")
                $fixtures = Get-Content -LiteralPath (Join-Path $root 'api-fixtures.json') -Raw | ConvertFrom-Json -AsHashtable
                if (-not $fixtures.ContainsKey($Endpoint)) {
                    throw "Unexpected GitHub API request: $Endpoint"
                }
                $response = $fixtures[$Endpoint]
                # Fixture values are raw JSON, or ERROR: followed by a simulated gh diagnostic.
                if ($response.StartsWith('ERROR:', [StringComparison]::Ordinal)) {
                    throw $response.Substring(6)
                }
                return $response
            }

            """);

        SetCommit(OldCommit, []);
        SetMain(OldCommit, []);
        SetComparison(OldCommit, OldCommit, "identical");
        SetSource(OldCommit, BaseVersion, ShellText, PowerShellText);
        WriteLocalState(OldCommit, BaseVersion, ShellText, PowerShellText);
    }

    public void Dispose() => _workspace.Dispose();

    [Theory]
    [RequiresTools(["pwsh"])]
    [InlineData("\n", false)]
    [InlineData("\r\n", false)]
    [InlineData("\r", false)]
    [InlineData("\n", true)]
    [InlineData("\r\n", true)]
    public async Task NormalizesLineEndingsAndBomBeforeHashing(string newline, bool bom)
    {
        var path = Path.Combine(_workspace.Path, "input.bin");
        File.WriteAllBytes(path, Encode(ShellText, newline, bom));

        var result = await RunHelperAsync("""
            $bytes = ConvertTo-TelemetryHookBytes ([System.IO.File]::ReadAllBytes((Join-Path $PSScriptRoot 'input.bin')))
            Write-Output (Get-TelemetryHookSha512Hex $bytes)
            """);

        result.EnsureSuccessful();
        Assert.Equal(ExpectedSha512, result.Output.Trim());
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task NormalizationRejectsInvalidUtf8()
    {
        var result = await RunHelperAsync("ConvertTo-TelemetryHookBytes ([byte[]]@(0xc3, 0x28))");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Unable to translate", result.Output);
    }

    [Theory]
    [RequiresTools(["pwsh"])]
    [InlineData("0.0.2", "0.0.3", -1)]
    [InlineData("0.0.3-rc.1", "0.0.3", -1)]
    [InlineData("0.0.3+build.1", "0.0.3", 0)]
    [InlineData("0.0.4-rc.1", "0.0.3", 1)]
    [InlineData("0.1.0-alpha", "0.0.3", 1)]
    [InlineData("1.10.0", "1.9.0", 1)]
    [InlineData("2.0.0", "10.0.0", -1)]
    [InlineData("1.0.0", "1.0.0-rc.1", 1)]
    [InlineData("1.0.0-alpha.10", "1.0.0-alpha.2", 1)]
    [InlineData("1.0.0-1", "1.0.0-alpha", -1)]
    [InlineData("1.0.0-alpha", "1.0.0-beta", -1)]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha", 1)]
    [InlineData("1.0.0+build.2", "1.0.0+build.1", 0)]
    [InlineData("999999999999999999999.0.0", "1.0.0", 1)]
    public async Task ComparesSemVerPrecedence(string version, string previous, int expected)
    {
        var result = await RunHelperAsync($"[Math]::Sign((Compare-TelemetryHooksVersion '{version}' '{previous}'))");

        result.EnsureSuccessful();
        Assert.Equal(expected.ToString(System.Globalization.CultureInfo.InvariantCulture), result.Output.Trim());
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task HookFileNamesMatchEmbeddedScripts()
    {
        var result = await RunHelperAsync("Get-TelemetryHookFileNames");
        result.EnsureSuccessful();
        var onDisk = Directory.EnumerateFiles(Path.Combine(RepoRoot.Path, "src", "Aspire.Cli", "Agents", "Hooks"), "track-telemetry.*")
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal);

        Assert.Equal(onDisk, result.Output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task BundleEntryPointsAndHelperAliasesAreRemoved()
    {
        foreach (var name in new[] { "aspire-skills-bundle.common.ps1", "update-aspire-skills-bundle.ps1", "verify-aspire-skills-bundle.ps1" })
        {
            Assert.False(File.Exists(Path.Combine(RepoRoot.Path, "eng", "scripts", name)));
        }

        var result = await RunHelperAsync("""
            $names = @(
                'Get-AspireSkillsHookFileNames',
                'Invoke-AspireSkillsGitHubApi',
                'Get-AspireSkillsSha512Hex',
                'Get-AspireSkillsReleaseCommitSha',
                'Get-AspireSkillsHookContent',
                'ConvertTo-LfUtf8Bytes'
            )
            Write-Output (@(Get-Command -Name $names -CommandType Function,Alias -ErrorAction SilentlyContinue).Count)
            """);

        result.EnsureSuccessful();
        Assert.Equal("0", result.Output.Trim());
    }

    [Theory]
    [RequiresTools(["pwsh"])]
    [InlineData("\n", false)]
    [InlineData("\r\n", false)]
    [InlineData("\r\n", true)]
    public async Task VerifiesNormalizedCheckoutWithoutMutation(string newline, bool bom)
    {
        File.WriteAllBytes(HookPath(s_hookNames[0]), Encode(ShellText, newline, bom));
        File.WriteAllBytes(HookPath(s_hookNames[1]), Encode(PowerShellText, newline, bom));
        var before = CaptureState();

        (await RunVerifyAsync()).EnsureSuccessful();

        AssertUnchanged(before);
        Assert.Equal(
            [Api("commits/main"), ContentEndpoint(OldCommit, PluginPath), ContentEndpoint(OldCommit, $"hooks/scripts/{s_hookNames[0]}"), ContentEndpoint(OldCommit, $"hooks/scripts/{s_hookNames[1]}")],
            ReadRequests());
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task RecordedSourceCanBeVerifiedAndReplayed()
    {
        var before = CaptureState();

        (await RunVerifyAsync()).EnsureSuccessful();
        (await RunUpdateAsync(OldCommit, BaseVersion)).EnsureSuccessful();

        AssertUnchanged(before);
        Assert.Equal(
            [
                Api("commits/main"), ContentEndpoint(OldCommit, PluginPath),
                ContentEndpoint(OldCommit, $"hooks/scripts/{s_hookNames[0]}"), ContentEndpoint(OldCommit, $"hooks/scripts/{s_hookNames[1]}"),
                Api("commits/main"), ContentEndpoint(OldCommit, PluginPath),
                ContentEndpoint(OldCommit, $"hooks/scripts/{s_hookNames[0]}"), ContentEndpoint(OldCommit, $"hooks/scripts/{s_hookNames[1]}"),
                CompareEndpoint(OldCommit, OldCommit)
            ],
            ReadRequests());
    }

    [Theory]
    [RequiresTools(["pwsh"])]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewReleaseCanReplaceTrackedAndPendingPins(bool includePendingBaseline)
    {
        SetNewRelease(NewVersion);
        var baselinePath = includePendingBaseline ? WritePendingMetadata(OldCommit, BaseVersion, ShellText, PowerShellText) : null;
        var baselineBytes = baselinePath is not null ? File.ReadAllBytes(baselinePath) : null;

        (await RunUpdateAsync(NewCommit, NewVersion, baselinePath)).EnsureSuccessful();
        (await RunVerifyAsync()).EnsureSuccessful();

        Assert.Equal(MetadataText(NewCommit, NewVersion, NewShellText, NewPowerShellText), File.ReadAllText(HookPath(MetadataName)));
        Assert.Equal(Encoding.UTF8.GetBytes(NewShellText), File.ReadAllBytes(HookPath(s_hookNames[0])));
        Assert.Equal(Encoding.UTF8.GetBytes(NewPowerShellText), File.ReadAllBytes(HookPath(s_hookNames[1])));
        AssertOnlyHookFiles();
        if (baselinePath is not null)
        {
            Assert.Equal(baselineBytes, File.ReadAllBytes(baselinePath));
        }
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task UpdateCannotRelabelPinnedSource()
    {
        var before = CaptureState();

        var update = await RunUpdateAsync(OldCommit, NewVersion);

        Assert.NotEqual(0, update.ExitCode);
        Assert.Contains("does not match canonical plugin version", update.Output);
        AssertUnchanged(before);
    }

    [Theory]
    [RequiresTools(["pwsh"])]
    [InlineData("version")]
    [InlineData("hash")]
    [InlineData("source-missing")]
    public async Task ExistingPinsRequireCanonicalVersionAndHashes(string corruption)
    {
        WriteLocalState(OldCommit, BaseVersion, corruption == "hash" ? "tampered\n" : ShellText, PowerShellText);
        SetNewRelease(NewVersion);
        if (corruption == "version")
        {
            SetContent(OldCommit, PluginPath, JsonSerializer.SerializeToUtf8Bytes(new { version = NewVersion }));
        }
        else if (corruption == "source-missing")
        {
            _responses[ContentEndpoint(OldCommit, $"hooks/scripts/{s_hookNames[1]}")] = "ERROR:gh: Not Found (HTTP 404)";
        }
        var before = CaptureState();

        Assert.NotEqual(0, (await RunUpdateAsync(NewCommit, NewVersion)).ExitCode);
        Assert.NotEqual(0, (await RunVerifyAsync()).ExitCode);

        AssertUnchanged(before);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task ReplayingSameCommitPreservesBytesAndTimestamps()
    {
        var before = CaptureState();

        (await RunUpdateAsync(OldCommit, BaseVersion)).EnsureSuccessful();
        (await RunUpdateAsync(OldCommit, BaseVersion)).EnsureSuccessful();

        AssertUnchanged(before);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task NewMainReleaseWritesOnlyNormalizedHooksAndMetadata()
    {
        SetNewRelease(NewVersion);
        SetContent(NewCommit, $"hooks/scripts/{s_hookNames[0]}", Encode(NewShellText, "\r\n", true));
        SetContent(NewCommit, $"hooks/scripts/{s_hookNames[1]}", Encode(NewPowerShellText, "\r", true));
        var unrelatedPath = Path.Combine(_workspace.Path, "unrelated.txt");
        File.WriteAllText(unrelatedPath, "leave unchanged");

        (await RunUpdateAsync(NewCommit.ToUpperInvariant(), NewVersion)).EnsureSuccessful();
        (await RunVerifyAsync()).EnsureSuccessful();

        Assert.Equal(Encoding.UTF8.GetBytes(NewShellText), File.ReadAllBytes(HookPath(s_hookNames[0])));
        Assert.Equal(Encoding.UTF8.GetBytes(NewPowerShellText), File.ReadAllBytes(HookPath(s_hookNames[1])));
        Assert.Equal(MetadataText(NewCommit, NewVersion, NewShellText, NewPowerShellText), File.ReadAllText(HookPath(MetadataName)));
        Assert.Equal("leave unchanged", File.ReadAllText(unrelatedPath));
        AssertOnlyHookFiles();
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task UnchangedHooksKeepTimestampsWhenOnlyPinChanges()
    {
        SetNewRelease(NewVersion, ShellText, PowerShellText);
        var before = CaptureState();

        (await RunUpdateAsync(NewCommit, NewVersion)).EnsureSuccessful();

        foreach (var name in s_hookNames)
        {
            Assert.Equal(before[name].Bytes, File.ReadAllBytes(HookPath(name)));
            Assert.Equal(before[name].LastWrite, File.GetLastWriteTimeUtc(HookPath(name)));
        }
        Assert.Equal(MetadataText(NewCommit, NewVersion, ShellText, PowerShellText), File.ReadAllText(HookPath(MetadataName)));
        AssertOnlyHookFiles();
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task ReleasedMainPinsDoNotHaveAnArbitraryHistoryCutoff()
    {
        var parent = OldCommit;
        for (var index = 1; index <= 150; index++)
        {
            var commit = index.ToString("x40", System.Globalization.CultureInfo.InvariantCulture);
            SetCommit(commit, [parent]);
            if (index == 150)
            {
                SetMain(commit, [parent]);
                SetComparison(OldCommit, commit, "ahead");
            }
            parent = commit;
        }
        var before = CaptureState();

        (await RunVerifyAsync()).EnsureSuccessful();

        AssertUnchanged(before);
        Assert.Equal(151, ReadRequests().Count(endpoint => endpoint.StartsWith(Api("commits/"), StringComparison.Ordinal)));
    }

    [Theory]
    [RequiresTools(["pwsh"])]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsDevAndSecondParentSources(bool merged)
    {
        if (merged)
        {
            SetMain(NewCommit, [OldCommit, DevCommit]);
            SetComparison(DevCommit, NewCommit, "ahead");
            SetComparison(DevCommit, OldCommit, "diverged");
        }
        else
        {
            SetComparison(DevCommit, OldCommit, "diverged");
        }
        var before = CaptureState();

        var result = await RunUpdateAsync(DevCommit, NewVersion);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("main lineage", result.Output);
        AssertUnchanged(before);
        var expected = merged
            ? new[] { Api("commits/main"), CompareEndpoint(DevCommit, NewCommit), CompareEndpoint(DevCommit, OldCommit) }
            : [Api("commits/main"), CompareEndpoint(DevCommit, OldCommit)];
        Assert.Equal(expected, ReadRequests());
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task VerifierRejectsSecondParentPinEvenWhenLocalHashesMatch()
    {
        WriteLocalState(DevCommit, NewVersion, ShellText, PowerShellText);
        SetMain(NewCommit, [OldCommit, DevCommit]);
        SetComparison(DevCommit, NewCommit, "ahead");
        SetComparison(DevCommit, OldCommit, "diverged");
        var before = CaptureState();

        var result = await RunVerifyAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("first-parent", result.Output);
        AssertUnchanged(before);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task DelayedOlderCommitCannotOverwriteNewerPinEvenAtSameVersion()
    {
        SetNewRelease(NewVersion);
        SetSource(OldCommit, NewVersion, ShellText, PowerShellText);
        SetComparison(NewCommit, OldCommit, "behind");
        WriteLocalState(NewCommit, NewVersion, NewShellText, NewPowerShellText);
        var before = CaptureState();

        var result = await RunUpdateAsync(OldCommit, NewVersion);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("behind or diverged", result.Output);
        AssertUnchanged(before);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task DivergedPinCannotBeReplaced()
    {
        SetNewRelease(NewVersion);
        SetComparison(OldCommit, NewCommit, "diverged");
        var before = CaptureState();

        var result = await RunUpdateAsync(NewCommit, NewVersion);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("behind or diverged", result.Output);
        AssertUnchanged(before);
    }

    [Theory]
    [RequiresTools(["pwsh"])]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingNewerPinRejectsDelayedSourceEvenAtSameVersion(bool invokeHelper)
    {
        SetNewRelease(NewVersion);
        SetLatestRelease(NewVersion);
        SetComparison(LatestCommit, NewCommit, "behind");
        var baselinePath = WritePendingMetadata(LatestCommit, NewVersion, NewShellText);
        var baselineBytes = File.ReadAllBytes(baselinePath);
        var baselineTime = File.GetLastWriteTimeUtc(baselinePath);
        var before = CaptureState();

        var result = invokeHelper
            ? await RunHelperAsync($"""
                Update-TelemetryHooks -Directory (Join-Path $PSScriptRoot 'src\Aspire.Cli\Agents\Hooks') `
                    -SourceCommit '{NewCommit}' -Version '{NewVersion}' `
                    -BaselineMetadataPath (Join-Path $PSScriptRoot '{PendingMetadataName}')
                """)
            : await RunUpdateAsync(NewCommit, NewVersion, baselinePath);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("behind or diverged", result.Output);
        Assert.Contains(LatestCommit, result.Output);
        Assert.Contains(CompareEndpoint(OldCommit, NewCommit), ReadRequests());
        Assert.Contains(CompareEndpoint(LatestCommit, NewCommit), ReadRequests());
        AssertUnchanged(before);
        Assert.Equal(baselineBytes, File.ReadAllBytes(baselinePath));
        Assert.Equal(baselineTime, File.GetLastWriteTimeUtc(baselinePath));
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task PendingVersionRejectsRegressionBeforeFetching()
    {
        var baselinePath = WritePendingMetadata(LatestCommit, LatestVersion, NewShellText);
        var before = CaptureState();

        var result = await RunUpdateAsync(NewCommit, NewVersion, baselinePath);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("version regression", result.Output);
        Assert.Empty(ReadRequests());
        AssertUnchanged(before);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task PendingMetadataDoesNotReplaceTheTrackedBaseline()
    {
        SetNewRelease(NewVersion);
        SetSource(OldCommit, NewVersion, ShellText, PowerShellText);
        SetComparison(NewCommit, OldCommit, "behind");
        WriteLocalState(NewCommit, NewVersion, NewShellText, NewPowerShellText);
        var baselinePath = WritePendingMetadata(OldCommit, NewVersion, ShellText, PowerShellText);
        var before = CaptureState();

        var result = await RunUpdateAsync(OldCommit, NewVersion, baselinePath);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("behind or diverged", result.Output);
        Assert.Contains(NewCommit, result.Output);
        AssertUnchanged(before);
    }

    [Theory]
    [RequiresTools(["pwsh"])]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceIdenticalToOrAheadOfPendingPinCanUpdateAndReplay(bool ahead)
    {
        SetNewRelease(NewVersion);
        SetComparison(NewCommit, NewCommit, "identical");
        if (ahead)
        {
            SetLatestRelease(LatestVersion);
        }
        var commit = ahead ? LatestCommit : NewCommit;
        var version = ahead ? LatestVersion : NewVersion;
        var baselinePath = WritePendingMetadata(NewCommit, NewVersion, NewShellText);
        var baselineBytes = File.ReadAllBytes(baselinePath);
        var baselineTime = File.GetLastWriteTimeUtc(baselinePath);

        (await RunUpdateAsync(commit, version, baselinePath)).EnsureSuccessful();
        var afterUpdate = CaptureState();
        (await RunUpdateAsync(commit, version, baselinePath)).EnsureSuccessful();

        Assert.Equal(MetadataText(commit, version, NewShellText, NewPowerShellText), File.ReadAllText(HookPath(MetadataName)));
        AssertUnchanged(afterUpdate);
        Assert.Equal(baselineBytes, File.ReadAllBytes(baselinePath));
        Assert.Equal(baselineTime, File.GetLastWriteTimeUtc(baselinePath));
    }

    [Theory]
    [RequiresTools(["pwsh"])]
    [InlineData("missing")]
    [InlineData("invalid-json")]
    [InlineData("wrong-repository")]
    [InlineData("invalid-hash")]
    [InlineData("wrapped-bundle")]
    public async Task InvalidPendingMetadataFailsBeforeFetchingOrMutation(string corruption)
    {
        var baselinePath = WritePendingMetadata(NewCommit, NewVersion, NewShellText);
        switch (corruption)
        {
            case "missing": File.Delete(baselinePath); break;
            case "invalid-json": File.WriteAllText(baselinePath, "{"); break;
            case "wrapped-bundle": File.WriteAllText(baselinePath, WrappedBundleMetadataText()); break;
            default:
                var metadata = JsonNode.Parse(File.ReadAllText(baselinePath))!;
                if (corruption == "wrong-repository")
                {
                    metadata["repository"] = "someone/aspire-skills";
                }
                else
                {
                    metadata["files"]![s_hookNames[0]] = "bad";
                }
                File.WriteAllText(baselinePath, metadata.ToJsonString());
                break;
        }
        var before = CaptureState();

        Assert.NotEqual(0, (await RunUpdateAsync(NewCommit, NewVersion, baselinePath)).ExitCode);

        Assert.Empty(ReadRequests());
        AssertUnchanged(before);
    }

    [Theory]
    [RequiresTools(["pwsh"])]
    [InlineData("hash")]
    [InlineData("version")]
    [InlineData("diverged")]
    [InlineData("remote-error")]
    public async Task PendingBaselineMustMatchCanonicalReleasedSource(string corruption)
    {
        SetNewRelease(NewVersion);
        SetLatestRelease(LatestVersion);
        var baselinePath = WritePendingMetadata(
            corruption == "diverged" ? DevCommit : NewCommit,
            corruption == "version" ? BaseVersion : NewVersion,
            corruption == "hash" ? "tampered\n" : NewShellText);
        if (corruption == "diverged")
        {
            SetComparison(DevCommit, LatestCommit, "diverged");
        }
        if (corruption == "remote-error")
        {
            _responses[ContentEndpoint(NewCommit, $"hooks/scripts/{s_hookNames[1]}")] = "ERROR:HTTP 403";
        }
        var before = CaptureState();

        Assert.NotEqual(0, (await RunUpdateAsync(LatestCommit, LatestVersion, baselinePath)).ExitCode);

        AssertUnchanged(before);
    }

    [Theory]
    [RequiresTools(["pwsh"])]
    [InlineData(BaseVersion)]
    [InlineData("0.0.3-rc.1")]
    public async Task RejectsVersionRegressionBeforeFetching(string version)
    {
        SetSource(OldCommit, NewVersion, ShellText, PowerShellText);
        WriteLocalState(OldCommit, NewVersion, ShellText, PowerShellText);
        var before = CaptureState();

        var result = await RunUpdateAsync(NewCommit, version);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("version regression", result.Output);
        Assert.Empty(ReadRequests());
        AssertUnchanged(before);
    }

    [Theory]
    [RequiresTools(["pwsh"])]
    [InlineData("0.0.0")]
    [InlineData("0.0.1")]
    [InlineData("0.0.2")]
    [InlineData("0.1.0-preview.2")]
    [InlineData("1.0.0+build.9")]
    [InlineData("2.3.4")]
    public async Task CanonicalReleasedVersionsCanUpdateAndReplayWithoutVersionPolicy(string version)
    {
        SetSource(OldCommit, "0.0.0", ShellText, PowerShellText);
        WriteLocalState(OldCommit, "0.0.0", ShellText, PowerShellText);
        SetNewRelease(version);
        SetComparison(NewCommit, NewCommit, "identical");

        (await RunUpdateAsync(NewCommit, version)).EnsureSuccessful();
        var afterUpdate = CaptureState();
        var helper = await RunHelperAsync($"""
            Update-TelemetryHooks -Directory (Join-Path $PSScriptRoot 'src\Aspire.Cli\Agents\Hooks') `
                -SourceCommit '{NewCommit}' -Version '{version}'
            """);

        helper.EnsureSuccessful();
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(MetadataText(NewCommit, version, NewShellText, NewPowerShellText)),
            JsonNode.Parse(File.ReadAllText(HookPath(MetadataName)))));
        AssertUnchanged(afterUpdate);
    }

    [Theory]
    [RequiresTools(["pwsh"])]
    [InlineData("main", BaseVersion)]
    [InlineData("v0.0.3", BaseVersion)]
    [InlineData("aspire-skills-v0.0.2.zip", BaseVersion)]
    [InlineData("1111111", BaseVersion)]
    [InlineData("111111111111111111111111111111111111111x", BaseVersion)]
    [InlineData(OldCommit, "v0.0.3")]
    [InlineData(OldCommit, "0.00.3")]
    [InlineData(OldCommit, "0.0")]
    [InlineData(OldCommit, "0.0.3-01")]
    [InlineData(OldCommit, "0.0.3+")]
    [InlineData(OldCommit, "0.0.3 ")]
    public async Task RejectsMutableShaAndNoncanonicalVersionInputs(string commit, string version)
    {
        var before = CaptureState();

        Assert.NotEqual(0, (await RunUpdateAsync(commit, version)).ExitCode);

        Assert.Empty(ReadRequests());
        AssertUnchanged(before);
    }

    [Theory]
    [RequiresTools(["pwsh"])]
    [InlineData("-SourceCommit", OldCommit)]
    [InlineData("-Version", BaseVersion)]
    public async Task UpdaterRequiresBothExplicitInputs(string parameter, string value)
    {
        var before = CaptureState();

        var result = await RunScriptAsync("update-telemetry-hooks.ps1", parameter, value);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(ReadRequests());
        AssertUnchanged(before);
    }

    [Theory]
    [RequiresTools(["pwsh"])]
    [InlineData("-Repository", Repository)]
    [InlineData("-Tag", "v0.0.2")]
    [InlineData("-Archive", "aspire-skills-v0.0.2.zip")]
    [InlineData("-ArchivePath", "aspire-skills-v0.0.2.zip")]
    [InlineData("-AssetName", "aspire-skills-v0.0.2.zip")]
    public async Task BothWrappersRejectBundleArguments(string parameter, string value)
    {
        var before = CaptureState();

        var update = await RunScriptAsync(
            "update-telemetry-hooks.ps1", "-SourceCommit", OldCommit, "-Version", BaseVersion, parameter, value);
        var verify = await RunScriptAsync("verify-telemetry-hooks.ps1", parameter, value);

        Assert.NotEqual(0, update.ExitCode);
        Assert.NotEqual(0, verify.ExitCode);
        Assert.Empty(ReadRequests());
        AssertUnchanged(before);
    }

    [Theory]
    [RequiresTools(["pwsh"])]
    [InlineData("missing")]
    [InlineData("invalid-json")]
    [InlineData("invalid-utf8")]
    [InlineData("array")]
    [InlineData("missing-version")]
    [InlineData("extra-hooks-block")]
    [InlineData("wrapped-bundle")]
    [InlineData("archive-field")]
    [InlineData("wrong-repository")]
    [InlineData("array-repository")]
    [InlineData("short-sha")]
    [InlineData("null-sha")]
    [InlineData("invalid-version")]
    [InlineData("numeric-version")]
    [InlineData("missing-file")]
    [InlineData("extra-file")]
    [InlineData("wrong-case-file")]
    [InlineData("invalid-hash")]
    [InlineData("uppercase-hash")]
    [InlineData("numeric-hash")]
    [InlineData("duplicate-version")]
    [InlineData("duplicate-file")]
    public async Task BothWrappersRejectCorruptMetadataWithoutFetching(string corruption)
    {
        var metadata = JsonNode.Parse(File.ReadAllText(HookPath(MetadataName)))!.AsObject();
        switch (corruption)
        {
            case "missing": File.Delete(HookPath(MetadataName)); break;
            case "invalid-json": File.WriteAllText(HookPath(MetadataName), "{"); break;
            case "invalid-utf8": File.WriteAllBytes(HookPath(MetadataName), [0xc3, 0x28]); break;
            case "array": File.WriteAllText(HookPath(MetadataName), "[]"); break;
            case "wrapped-bundle": File.WriteAllText(HookPath(MetadataName), WrappedBundleMetadataText()); break;
            case "duplicate-version":
                File.WriteAllText(HookPath(MetadataName), metadata.ToJsonString().Replace("\"version\":", "\"version\":\"0.0.3\",\"version\":", StringComparison.Ordinal));
                break;
            case "duplicate-file":
                File.WriteAllText(HookPath(MetadataName), metadata.ToJsonString().Replace("\"track-telemetry.sh\":", "\"track-telemetry.sh\":\"bad\",\"track-telemetry.sh\":", StringComparison.Ordinal));
                break;
            default:
                switch (corruption)
                {
                    case "missing-version": metadata.Remove("version"); break;
                    case "extra-hooks-block": metadata["hooks"] = new JsonObject(); break;
                    case "archive-field": metadata["assetName"] = "archive.zip"; break;
                    case "wrong-repository": metadata["repository"] = "someone/aspire-skills"; break;
                    case "array-repository": metadata["repository"] = new JsonArray(Repository); break;
                    case "short-sha": metadata["commitSha"] = "1111111"; break;
                    case "null-sha": metadata["commitSha"] = null; break;
                    case "invalid-version": metadata["version"] = "v0.0.3"; break;
                    case "numeric-version": metadata["version"] = 2; break;
                    case "missing-file": metadata["files"]!.AsObject().Remove(s_hookNames[1]); break;
                    case "extra-file": metadata["files"]!["extra.sh"] = ExpectedSha512; break;
                    case "wrong-case-file":
                        metadata["files"]!.AsObject().Remove(s_hookNames[0]);
                        metadata["files"]!["Track-Telemetry.sh"] = ExpectedSha512;
                        break;
                    case "invalid-hash": metadata["files"]![s_hookNames[0]] = new string('z', 128); break;
                    case "uppercase-hash": metadata["files"]![s_hookNames[0]] = ExpectedSha512.ToUpperInvariant(); break;
                    case "numeric-hash": metadata["files"]![s_hookNames[0]] = 123; break;
                    default: throw new ArgumentOutOfRangeException(nameof(corruption));
                }
                File.WriteAllText(HookPath(MetadataName), metadata.ToJsonString());
                break;
        }
        var before = CaptureState();

        Assert.NotEqual(0, (await RunUpdateAsync(OldCommit, BaseVersion)).ExitCode);
        Assert.NotEqual(0, (await RunVerifyAsync()).ExitCode);

        Assert.Empty(ReadRequests());
        AssertUnchanged(before);
    }

    [Theory]
    [RequiresTools(["pwsh"])]
    [InlineData("track-telemetry.sh", true)]
    [InlineData("track-telemetry.ps1", true)]
    [InlineData("track-telemetry.sh", false)]
    [InlineData("track-telemetry.ps1", false)]
    public async Task BothWrappersRejectMissingOrChangedLocalHooks(string name, bool missing)
    {
        if (missing)
        {
            File.Delete(HookPath(name));
        }
        else
        {
            File.WriteAllText(HookPath(name), "tampered\n");
        }
        var before = CaptureState();

        Assert.NotEqual(0, (await RunUpdateAsync(OldCommit, BaseVersion)).ExitCode);
        Assert.NotEqual(0, (await RunVerifyAsync()).ExitCode);

        Assert.Empty(ReadRequests());
        AssertUnchanged(before);
    }

    [Theory]
    [RequiresTools(["pwsh"])]
    [InlineData("type", "\"symlink\"")]
    [InlineData("type", "[\"file\"]")]
    [InlineData("name", "\"other.ps1\"")]
    [InlineData("path", "\"elsewhere/track-telemetry.ps1\"")]
    [InlineData("encoding", "\"utf-8\"")]
    [InlineData("content", "\"not base64\"")]
    [InlineData("content", "null")]
    [InlineData("content", "\"\"")]
    [InlineData("content", "\"wyg=\"")]
    [InlineData("sha", "\"bbbbbbb\"")]
    public async Task BothWrappersRejectMalformedCanonicalContentsAfterFirstHookFetch(string property, string json)
    {
        var endpoint = ContentEndpoint(OldCommit, $"hooks/scripts/{s_hookNames[1]}");
        var response = JsonNode.Parse(_responses[endpoint])!;
        response[property] = JsonNode.Parse(json);
        _responses[endpoint] = response.ToJsonString();
        var before = CaptureState();

        Assert.NotEqual(0, (await RunUpdateAsync(OldCommit, BaseVersion)).ExitCode);
        Assert.NotEqual(0, (await RunVerifyAsync()).ExitCode);

        Assert.Equal(2, ReadRequests().Count(request => request == ContentEndpoint(OldCommit, $"hooks/scripts/{s_hookNames[0]}")));
        AssertUnchanged(before);
    }

    [Theory]
    [RequiresTools(["pwsh"])]
    [InlineData("ERROR:gh: Not Found (HTTP 404)")]
    [InlineData("ERROR:gh: authentication failed (HTTP 401)")]
    [InlineData("ERROR:connection reset")]
    [InlineData("not json")]
    [InlineData("[]")]
    public async Task BothWrappersFailExplicitlyOnCanonicalSourceFailures(string response)
    {
        _responses[ContentEndpoint(OldCommit, $"hooks/scripts/{s_hookNames[1]}")] = response;
        var before = CaptureState();

        Assert.NotEqual(0, (await RunUpdateAsync(OldCommit, BaseVersion)).ExitCode);
        Assert.NotEqual(0, (await RunVerifyAsync()).ExitCode);

        AssertUnchanged(before);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task MissingCanonicalPluginVersionFailsWithoutFallback()
    {
        _responses[ContentEndpoint(OldCommit, PluginPath)] = "ERROR:gh: Not Found (HTTP 404)";
        var before = CaptureState();

        var update = await RunUpdateAsync(OldCommit, BaseVersion);
        var verify = await RunVerifyAsync();

        Assert.NotEqual(0, update.ExitCode);
        Assert.NotEqual(0, verify.ExitCode);
        Assert.Contains("HTTP 404", update.Output);
        Assert.Contains("HTTP 404", verify.Output);
        Assert.Equal(
            [Api("commits/main"), ContentEndpoint(OldCommit, PluginPath), Api("commits/main"), ContentEndpoint(OldCommit, PluginPath)],
            ReadRequests());
        AssertUnchanged(before);
    }

    [Theory]
    [RequiresTools(["pwsh"])]
    [InlineData("{}")]
    [InlineData("{\"version\":\"0.0.4\"}")]
    [InlineData("{\"version\":\"v0.0.3\"}")]
    [InlineData("{\"version\":2}")]
    [InlineData("[]")]
    [InlineData("{")]
    public async Task CanonicalPluginMustDeclareTheExactRequestedVersion(string pluginJson)
    {
        SetContent(OldCommit, PluginPath, Encoding.UTF8.GetBytes(pluginJson));
        var before = CaptureState();

        Assert.NotEqual(0, (await RunUpdateAsync(OldCommit, BaseVersion)).ExitCode);
        Assert.NotEqual(0, (await RunVerifyAsync()).ExitCode);

        AssertUnchanged(before);
    }

    [Theory]
    [RequiresTools(["pwsh"])]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchingLocalAndMetadataHashesCannotConcealCanonicalMismatch(bool newerRelease)
    {
        WriteLocalState(OldCommit, BaseVersion, "tampered\n", PowerShellText);
        if (newerRelease)
        {
            SetNewRelease(NewVersion);
        }
        var before = CaptureState();

        var update = await RunUpdateAsync(newerRelease ? NewCommit : OldCommit, newerRelease ? NewVersion : BaseVersion);
        var verify = await RunVerifyAsync();

        Assert.NotEqual(0, update.ExitCode);
        Assert.NotEqual(0, verify.ExitCode);
        Assert.Contains("does not match canonical", update.Output);
        Assert.Contains("does not match canonical", verify.Output);
        AssertUnchanged(before);
    }

    [Theory]
    [RequiresTools(["pwsh"])]
    [InlineData("missing-hook")]
    [InlineData("invalid-utf8")]
    [InlineData("wrong-version")]
    public async Task PartialNewReleaseFetchOrValidationNeverPublishesAnyFile(string failure)
    {
        SetNewRelease(NewVersion);
        switch (failure)
        {
            case "missing-hook":
                _responses[ContentEndpoint(NewCommit, $"hooks/scripts/{s_hookNames[1]}")] = "ERROR:gh: Not Found (HTTP 404)";
                break;
            case "invalid-utf8":
                SetContent(NewCommit, $"hooks/scripts/{s_hookNames[1]}", [0xc3, 0x28]);
                break;
            case "wrong-version":
                SetContent(NewCommit, PluginPath, JsonSerializer.SerializeToUtf8Bytes(new { version = LatestVersion }));
                break;
        }
        var before = CaptureState();

        Assert.NotEqual(0, (await RunUpdateAsync(NewCommit, NewVersion)).ExitCode);

        AssertUnchanged(before);
    }

    [Theory]
    [RequiresTools(["pwsh"])]
    [InlineData("main-sha")]
    [InlineData("main-error")]
    [InlineData("parents")]
    [InlineData("parent-sha")]
    [InlineData("resolved-sha")]
    [InlineData("status")]
    [InlineData("base-sha")]
    [InlineData("merge-base-sha")]
    [InlineData("compare-error")]
    public async Task VerifierRejectsInvalidCommitAndComparisonResponses(string corruption)
    {
        SetMain(NewCommit, [OldCommit]);
        SetComparison(OldCommit, NewCommit, "ahead");
        var endpoint = corruption switch
        {
            "resolved-sha" => Api($"commits/{OldCommit}"),
            "status" or "base-sha" or "merge-base-sha" or "compare-error" => CompareEndpoint(OldCommit, NewCommit),
            _ => Api("commits/main")
        };
        var response = JsonNode.Parse(_responses[endpoint])!;
        switch (corruption)
        {
            case "main-sha": response["sha"] = "bbbbbbb"; break;
            case "parents": response["parents"] = new JsonObject(); break;
            case "parent-sha": response["parents"]![0]!["sha"] = "1111111"; break;
            case "resolved-sha": response["sha"] = DevCommit; break;
            case "status": response["status"] = "unknown"; break;
            case "base-sha": response["base_commit"]!["sha"] = DevCommit; break;
            case "merge-base-sha": response["merge_base_commit"]!["sha"] = DevCommit; break;
        }
        _responses[endpoint] = corruption.EndsWith("-error", StringComparison.Ordinal) ? "ERROR:HTTP 403" : response.ToJsonString();
        var before = CaptureState();

        Assert.NotEqual(0, (await RunVerifyAsync()).ExitCode);

        AssertUnchanged(before);
    }

    private void SetNewRelease(string version, string shell = NewShellText, string powershell = NewPowerShellText)
    {
        SetCommit(NewCommit, [OldCommit, DevCommit]);
        SetMain(NewCommit, [OldCommit, DevCommit]);
        SetComparison(OldCommit, NewCommit, "ahead");
        SetSource(NewCommit, version, shell, powershell);
    }

    private void SetLatestRelease(string version)
    {
        SetCommit(LatestCommit, [NewCommit]);
        SetMain(LatestCommit, [NewCommit]);
        SetComparison(OldCommit, LatestCommit, "ahead");
        SetComparison(NewCommit, LatestCommit, "ahead");
        SetComparison(LatestCommit, LatestCommit, "identical");
        SetSource(LatestCommit, version, NewShellText, NewPowerShellText);
    }

    private string WritePendingMetadata(string commit, string version, string shell, string powershell = NewPowerShellText)
    {
        var path = Path.Combine(_workspace.Path, PendingMetadataName);
        File.WriteAllText(path, MetadataText(commit, version, shell, powershell));
        return path;
    }

    private void SetSource(string commit, string version, string shell, string powershell)
    {
        SetContent(commit, PluginPath, JsonSerializer.SerializeToUtf8Bytes(new { name = "aspire", version }));
        SetContent(commit, $"hooks/scripts/{s_hookNames[0]}", Encoding.UTF8.GetBytes(shell));
        SetContent(commit, $"hooks/scripts/{s_hookNames[1]}", Encoding.UTF8.GetBytes(powershell));
    }

    private void SetContent(string commit, string path, byte[] bytes)
    {
        _responses[ContentEndpoint(commit, path)] = JsonSerializer.Serialize(new
        {
            type = "file",
            name = path[(path.LastIndexOf('/') + 1)..],
            path,
            encoding = "base64",
            content = Convert.ToBase64String(bytes, Base64FormattingOptions.InsertLineBreaks),
            sha = RootCommit
        });
    }

    private void SetCommit(string commit, string[] parents)
        => _responses[Api($"commits/{commit}")] = CommitResponse(commit, parents);

    private void SetMain(string commit, string[] parents)
        => _responses[Api("commits/main")] = CommitResponse(commit, parents);

    private static string CommitResponse(string commit, string[] parents)
        => JsonSerializer.Serialize(new { sha = commit, parents = parents.Select(sha => new { sha }).ToArray() });

    private void SetComparison(string from, string to, string status)
        => _responses[CompareEndpoint(from, to)] = JsonSerializer.Serialize(new
        {
            status,
            base_commit = new { sha = from },
            merge_base_commit = new { sha = status switch { "behind" => to, "diverged" => RootCommit, _ => from } }
        });

    private void WriteLocalState(string commit, string version, string shell, string powershell)
    {
        File.WriteAllText(HookPath(s_hookNames[0]), shell);
        File.WriteAllText(HookPath(s_hookNames[1]), powershell);
        File.WriteAllText(HookPath(MetadataName), MetadataText(commit, version, shell, powershell));
        foreach (var name in s_hookNames.Append(MetadataName))
        {
            File.SetLastWriteTimeUtc(HookPath(name), new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        }
    }

    private static string MetadataText(string commit, string version, string shell, string powershell)
        => JsonSerializer.Serialize(new
        {
            version,
            repository = Repository,
            commitSha = commit,
            files = new Dictionary<string, string>
            {
                [s_hookNames[0]] = Convert.ToHexStringLower(SHA512.HashData(Encoding.UTF8.GetBytes(shell))),
                [s_hookNames[1]] = Convert.ToHexStringLower(SHA512.HashData(Encoding.UTF8.GetBytes(powershell)))
            }
        }, s_jsonOptions) + "\n";

    private static string WrappedBundleMetadataText()
    {
        var metadata = JsonNode.Parse(MetadataText(OldCommit, BaseVersion, ShellText, PowerShellText))!;
        return JsonSerializer.Serialize(new
        {
            version = BaseVersion,
            repository = Repository,
            tag = $"v{BaseVersion}",
            assetName = $"aspire-skills-v{BaseVersion}.zip",
            sha512 = ExpectedSha512,
            hooks = new
            {
                commitSha = OldCommit,
                files = metadata["files"]
            }
        });
    }

    private Dictionary<string, (byte[]? Bytes, DateTime? LastWrite)> CaptureState()
        => s_hookNames.Append(MetadataName).ToDictionary(
            name => name,
            name => File.Exists(HookPath(name))
                ? ((byte[]?)File.ReadAllBytes(HookPath(name)), (DateTime?)File.GetLastWriteTimeUtc(HookPath(name)))
                : (null, null),
            StringComparer.Ordinal);

    private void AssertUnchanged(Dictionary<string, (byte[]? Bytes, DateTime? LastWrite)> before)
    {
        foreach (var (name, state) in before)
        {
            Assert.Equal(state.Bytes is not null, File.Exists(HookPath(name)));
            if (state.Bytes is not null)
            {
                Assert.Equal(state.Bytes, File.ReadAllBytes(HookPath(name)));
                Assert.Equal(state.LastWrite, File.GetLastWriteTimeUtc(HookPath(name)));
            }
        }
        Assert.Equal(
            before.Where(entry => entry.Value.Bytes is not null).Select(entry => entry.Key).Order(StringComparer.Ordinal),
            Directory.EnumerateFileSystemEntries(_hooksDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    private void AssertOnlyHookFiles()
        => Assert.Equal(
            s_hookNames.Append(MetadataName).Order(StringComparer.Ordinal),
            Directory.EnumerateFileSystemEntries(_hooksDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal));

    private Task<CommandResult> RunUpdateAsync(string commit, string version, string? baselineMetadataPath = null)
    {
        List<string> arguments = ["-SourceCommit", $"\"{commit}\"", "-Version", $"\"{version}\""];
        if (baselineMetadataPath is not null)
        {
            arguments.AddRange(["-BaselineMetadataPath", $"\"{baselineMetadataPath}\""]);
        }
        return RunScriptAsync("update-telemetry-hooks.ps1", [.. arguments]);
    }

    private Task<CommandResult> RunVerifyAsync() => RunScriptAsync("verify-telemetry-hooks.ps1");

    private async Task<CommandResult> RunScriptAsync(string name, params string[] arguments)
    {
        File.WriteAllText(Path.Combine(_workspace.Path, "api-fixtures.json"), JsonSerializer.Serialize(_responses));
        using var command = new PowerShellCommand(Path.Combine(_scriptDirectory, name), _output)
            .WithWorkingDirectory(_workspace.Path)
            .WithTimeout(TimeSpan.FromMinutes(1));
        return await command.ExecuteAsync(arguments);
    }

    private async Task<CommandResult> RunHelperAsync(string body)
    {
        File.WriteAllText(Path.Combine(_workspace.Path, "api-fixtures.json"), JsonSerializer.Serialize(_responses));
        var driver = Path.Combine(_workspace.Path, "driver.ps1");
        File.WriteAllText(driver, """
            $ErrorActionPreference = 'Stop'
            . (Join-Path $PSScriptRoot 'eng\scripts\telemetry-hooks.common.ps1')

            """ + body);
        using var command = new PowerShellCommand(driver, _output).WithTimeout(TimeSpan.FromMinutes(1));
        return await command.ExecuteAsync();
    }

    private string[] ReadRequests()
    {
        var path = Path.Combine(_workspace.Path, "api-requests.txt");
        return File.Exists(path) ? File.ReadAllLines(path) : [];
    }

    private string HookPath(string name) => Path.Combine(_hooksDirectory, name);
    private static string Api(string endpoint) => $"repos/{Repository}/{endpoint}";
    private static string ContentEndpoint(string commit, string path) => Api($"contents/{path}?ref={commit}");
    private static string CompareEndpoint(string from, string to) => Api($"compare/{from}...{to}?per_page=1");

    private static byte[] Encode(string text, string newline, bool bom)
    {
        var bytes = Encoding.UTF8.GetBytes(text.Replace("\n", newline, StringComparison.Ordinal));
        return bom ? [0xef, 0xbb, 0xbf, .. bytes] : bytes;
    }
}
