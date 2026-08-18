// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Acquisition;
using Aspire.Cli.Commands;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Aspire.Cli.Tests.Commands;

public class InstallationInfoOutputTests(ITestOutputHelper outputHelper)
{
    // ---------------------------------------------------------------------------
    // Aggregate-discovery timeout: updated for the new InstallationDiscoveryResult return type
    // while preserving the core falsifiability — that a discovery which ignores
    // cancellation is still bounded and the bound surfaces as AggregateFailureReason.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task DiscoverAllToResultSafelyAsync_TimesOutWhenDiscoveryDoesNotObserveCancellation()
    {
        using var releaseDiscovery = new ManualResetEventSlim();
        var discoveryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var discoveryExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var self = new InstallationInfo
        {
            Path = "/test/aspire",
            Status = InstallationInfoStatus.Ok,
        };
        var discovery = new FakeInstallationDiscovery(self)
        {
            DiscoverAllAsyncCallback = _ =>
            {
                discoveryStarted.SetResult();
                releaseDiscovery.Wait(CancellationToken.None);
                discoveryExited.SetResult();
                return Task.FromResult<IReadOnlyList<InstallationInfo>>([self]);
            },
        };
        var wingetProbe = new WingetFirstRunProbe(
            new TestWindowsRegistryReader(),
            NullLogger<WingetFirstRunProbe>.Instance);

        var discoveryTask = InstallationInfoOutput.DiscoverAllToResultSafelyAsync(
            discovery,
            wingetProbe,
            NullLogger.Instance,
            TimeSpan.FromMilliseconds(100),
            TestContext.Current.CancellationToken);
        InstallationDiscoveryResult result;
        try
        {
            await discoveryStarted.Task.DefaultTimeout();
            result = await discoveryTask.DefaultTimeout();
        }
        finally
        {
            releaseDiscovery.Set();
        }

        await discoveryExited.Task.DefaultTimeout();

        // Aggregate timeout surfaces as AggregateFailureReason, not as a peer row.
        Assert.Empty(result.Installations);
        Assert.NotNull(result.AggregateFailureReason);
        Assert.Contains("timed out after", result.AggregateFailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DiscoverAllToResultSafelyAsync_TimesOutWhenHiveEnumerationDoesNotObserveCancellation()
    {
        var self = new InstallationInfo
        {
            Path = "/test/aspire",
            Status = InstallationInfoStatus.Ok,
        };
        var discovery = new FakeInstallationDiscovery(self);
        var hiveEnumerator = new DelayedHiveEnumerator(TimeSpan.FromSeconds(2));
        var wingetProbe = new WingetFirstRunProbe(
            new TestWindowsRegistryReader(),
            NullLogger<WingetFirstRunProbe>.Instance);

        var result = await InstallationInfoOutput.DiscoverAllToResultSafelyAsync(
            discovery,
            hiveEnumerator,
            wingetProbe,
            NullLogger.Instance,
            TimeSpan.FromMilliseconds(100),
            TestContext.Current.CancellationToken).DefaultTimeout();

        var installation = Assert.Single(result.Installations);
        Assert.Same(self, installation);
        Assert.Empty(result.Hives);
        Assert.NotNull(result.AggregateFailureReason);
        Assert.Contains("timed out after", result.AggregateFailureReason, StringComparison.OrdinalIgnoreCase);

        Assert.Collection(
            InstallationInfoOutput.BuildInfoRows(result, result.Hives),
            row =>
            {
                Assert.Equal(InfoInstallationKind.Installation, row.Kind);
                Assert.Equal(self.Path, row.Path);
            },
            row => Assert.Equal(InfoInstallationKind.DiscoveryFailed, row.Kind));
    }

    // ---------------------------------------------------------------------------
    // MapToInfoInstallation: Route → source, hive correlation, field preservation
    // ---------------------------------------------------------------------------

    [Fact]
    public void MapToInfoInstallation_MapsRouteToSource_AndPreservesAllFields()
    {
        var install = new InstallationInfo
        {
            Path = "/usr/local/bin/aspire",
            CanonicalPath = "/opt/aspire/bin/aspire",
            Version = "13.0.0",
            Channel = "stable",
            Route = "path",
            PathStatus = InstallationPathStatus.Active,
            Status = InstallationInfoStatus.Ok,
        };
        var hivesByChannel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["stable"] = "/home/test/.aspire/hives/stable",
        };

        var row = InstallationInfoOutput.MapToInfoInstallation(install, hivesByChannel);

        Assert.Equal(InfoInstallationKind.Installation, row.Kind);
        Assert.Equal("/usr/local/bin/aspire", row.Path);
        Assert.Equal("/opt/aspire/bin/aspire", row.CanonicalPath);
        Assert.Equal("13.0.0", row.Version);
        Assert.Equal("stable", row.Channel);
        Assert.Equal("path", row.Source);   // Route → source
        Assert.Equal("/home/test/.aspire/hives/stable", row.Hive);  // hive correlated by channel
        Assert.Equal(InstallationPathStatus.Active, row.PathStatus);
        Assert.Equal(InstallationInfoStatus.Ok, row.Status);
        Assert.Null(row.StatusReason);
    }

    [Fact]
    public void MapToInfoInstallation_InvalidChannel_DoesNotCorrelateHive()
    {
        // A null or otherwise invalid channel must not trigger hive correlation
        // even if a hive directory matching the value exists in the dict.
        var install = new InstallationInfo
        {
            Path = "/some/bin/aspire",
            Channel = null,
            Route = "script",
            PathStatus = InstallationPathStatus.NotOnPath,
            Status = InstallationInfoStatus.Ok,
        };
        var hivesByChannel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["stable"] = "/home/test/.aspire/hives/stable",
        };

        var row = InstallationInfoOutput.MapToInfoInstallation(install, hivesByChannel);

        Assert.Equal(InfoInstallationKind.Installation, row.Kind);
        Assert.Null(row.Hive);
    }

    // ---------------------------------------------------------------------------
    // BuildInfoRows: orphan hives and aggregate failures
    // ---------------------------------------------------------------------------

    [Fact]
    public void BuildInfoRows_UnmatchedHivesBecome_OrphanHiveRows()
    {
        var self = new InstallationInfo
        {
            Path = "/usr/local/bin/aspire",
            Channel = "stable",
            Route = "path",
            PathStatus = InstallationPathStatus.Active,
            Status = InstallationInfoStatus.Ok,
        };
        // Three hives: one matched by the installation's channel, one orphan with a
        // valid-but-unmatched channel name (pr-999), and one with an invalid name
        // that is never a valid channel (some-legacy-dir). Both orphans must appear
        // as orphan-hive rows and must not be dropped.
        var hives = new[]
        {
            new HiveInfo("stable", "/home/test/.aspire/hives/stable"),
            new HiveInfo("pr-999", "/home/test/.aspire/hives/pr-999"),
            new HiveInfo("some-legacy-dir", "/home/test/.aspire/hives/some-legacy-dir"),
        };
        var discoveryResult = new InstallationDiscoveryResult([self], AggregateFailureReason: null);

        var rows = InstallationInfoOutput.BuildInfoRows(discoveryResult, hives);

        Assert.Equal(3, rows.Length);

        var installRow = Assert.Single(rows, r => r.Kind == InfoInstallationKind.Installation);
        Assert.Equal("/usr/local/bin/aspire", installRow.Path);
        Assert.Equal("/home/test/.aspire/hives/stable", installRow.Hive);

        var orphanRows = rows.Where(r => r.Kind == InfoInstallationKind.OrphanHive).ToList();
        Assert.Equal(2, orphanRows.Count);

        // Valid-channel name that no installation claimed: must be an orphan-hive row.
        var prOrphan = Assert.Single(orphanRows, r => r.Hive == "/home/test/.aspire/hives/pr-999");
        Assert.Equal(InstallationPathStatus.NotOnPath, prOrphan.PathStatus);
        Assert.Equal(InstallationInfoStatus.NoInstallFound, prOrphan.Status);
        Assert.Null(prOrphan.Channel);

        // Invalid-channel name (not a recognised channel): must still be an orphan-hive row,
        // not silently dropped. The hive directory is real; users need to see it.
        var legacyOrphan = Assert.Single(orphanRows, r => r.Hive == "/home/test/.aspire/hives/some-legacy-dir");
        Assert.Equal(InstallationPathStatus.NotOnPath, legacyOrphan.PathStatus);
        Assert.Equal(InstallationInfoStatus.NoInstallFound, legacyOrphan.Status);
        Assert.Null(legacyOrphan.Channel);
    }

    [Fact]
    public void BuildInfoRows_AggregateFailure_BecomesDiscoveryFailedRow()
    {
        var discoveryResult = new InstallationDiscoveryResult(
            Installations: [],
            AggregateFailureReason: "discovery exploded");

        static IEnumerable<HiveInfo> FailIfEnumerated()
        {
            Assert.Fail("Hive rows must not be evaluated when aggregate discovery fails.");
            yield break;
        }

        var rows = InstallationInfoOutput.BuildInfoRows(discoveryResult, FailIfEnumerated());

        var single = Assert.Single(rows);
        Assert.Equal(InfoInstallationKind.DiscoveryFailed, single.Kind);
        Assert.Equal(InstallationPathStatus.NotOnPath, single.PathStatus);
        Assert.Equal(InstallationInfoStatus.Failed, single.Status);
        Assert.Equal("discovery exploded", single.StatusReason);
    }

    [Fact]
    public void BuildHumanRenderable_MarksRunningInstallationAsCurrent()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var executionContext = workspace.CreateExecutionContext(identityChannel: "stable", identityVersion: "13.4.0");
        var self = new InstallationInfo
        {
            Path = "/usr/local/bin/aspire",
            Channel = "stable",
            PathStatus = InstallationPathStatus.Active,
            Status = InstallationInfoStatus.Ok,
        };
        var rows = InstallationInfoOutput.BuildInfoRows(
            new InstallationDiscoveryResult([self], AggregateFailureReason: null),
            hives: []);

        var plainText = RenderToPlainConsole(
            InstallationInfoOutput.BuildHumanRenderable(supportsLinks: false, executionContext, rows));

        Assert.Contains("Installation 1 (current)", plainText, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInfoRows_PeerFailedRow_RemainsInstallationKind()
    {
        // A peer-level failure (status: failed) is distinct from aggregate failure.
        // It must remain kind: "installation", not kind: "discovery-failed".
        var failedPeer = new InstallationInfo
        {
            Path = "/other/bin/aspire",
            PathStatus = InstallationPathStatus.Shadowed,
            Status = InstallationInfoStatus.Failed,
            StatusReason = "probe timed out",
        };
        var discoveryResult = new InstallationDiscoveryResult([failedPeer], AggregateFailureReason: null);
        var hives = Array.Empty<HiveInfo>();

        var rows = InstallationInfoOutput.BuildInfoRows(discoveryResult, hives);

        var single = Assert.Single(rows);
        Assert.Equal(InfoInstallationKind.Installation, single.Kind);
        Assert.Equal(InstallationInfoStatus.Failed, single.Status);
    }

    // ---------------------------------------------------------------------------
    // DiscoverAllToResultSafelyAsync: caller cancellation propagates
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task DiscoverAllToResultSafelyAsync_CallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        var self = new InstallationInfo
        {
            Path = "/test/aspire",
            Status = InstallationInfoStatus.Ok,
        };
        var discovery = new FakeInstallationDiscovery(self)
        {
            DiscoverAllAsyncCallback = async ct =>
            {
                await cts.CancelAsync();
                ct.ThrowIfCancellationRequested();
                return [self];
            },
        };
        var wingetProbe = new WingetFirstRunProbe(
            new TestWindowsRegistryReader(),
            NullLogger<WingetFirstRunProbe>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => InstallationInfoOutput.DiscoverAllToResultSafelyAsync(
                discovery, wingetProbe, NullLogger.Instance, cts.Token));
    }

    // ---------------------------------------------------------------------------
    // DescribeSelfAsInfoInstallation
    // ---------------------------------------------------------------------------

    [Fact]
    public void DescribeSelfAsInfoInstallation_Success_MapsToInstallationRow()
    {
        var self = new InstallationInfo
        {
            Path = "/self/aspire",
            CanonicalPath = "/self/aspire",
            Version = "13.0.0",
            Channel = "stable",
            Route = "script",
            PathStatus = InstallationPathStatus.Active,
            Status = InstallationInfoStatus.Ok,
        };
        var discovery = new FakeInstallationDiscovery(self);

        var row = InstallationInfoOutput.DescribeSelfAsInfoInstallation(
            discovery, NullLogger.Instance);

        Assert.Equal(InfoInstallationKind.Installation, row.Kind);
        Assert.Equal("/self/aspire", row.Path);
        Assert.Equal("script", row.Source);
        Assert.True(row.IsCurrent);
    }

    [Fact]
    public void DescribeSelfAsInfoInstallation_NonCancellationException_ReturnsDiscoveryFailedRow()
    {
        var self = new InstallationInfo { Path = "", Status = InstallationInfoStatus.Failed };
        var discovery = new FakeInstallationDiscovery(self)
        {
            DescribeSelfCallback = () => throw new InvalidOperationException("self probe exploded"),
        };
        var row = InstallationInfoOutput.DescribeSelfAsInfoInstallation(
            discovery, NullLogger.Instance);

        Assert.Equal(InfoInstallationKind.DiscoveryFailed, row.Kind);
        Assert.Equal(InstallationInfoStatus.Failed, row.Status);
    }

    [Fact]
    public void DescribeSelfAsInfoInstallation_CancellationException_Propagates()
    {
        var self = new InstallationInfo { Path = "", Status = InstallationInfoStatus.Failed };
        var discovery = new FakeInstallationDiscovery(self)
        {
            DescribeSelfCallback = () => throw new OperationCanceledException("cancelled"),
        };
        Assert.Throws<OperationCanceledException>(
            () => InstallationInfoOutput.DescribeSelfAsInfoInstallation(
                discovery, NullLogger.Instance));
    }

    // ---------------------------------------------------------------------------
    // JSON serialization: source field, not route; nulls omitted
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task SerializeInfoOutput_UsesSourceField_AndOmitsNulls()
    {
        var output = new InfoOutput
        {
            Version = "13.0.0",
            Channel = "stable",
            Installs =
            [
                new InfoInstallation
                {
                    Kind = InfoInstallationKind.Installation,
                    Path = "/usr/local/bin/aspire",
                    CanonicalPath = "/opt/aspire/bin/aspire",
                    Version = "13.0.0",
                    Channel = "stable",
                    Source = "path",
                    Hive = "/home/test/.aspire/hives/stable",
                    PathStatus = InstallationPathStatus.Active,
                    Status = InstallationInfoStatus.Ok,
                    IsCurrent = true,
                },
            ],
        };

        var json = JsonSerializer.Serialize(output, JsonSourceGenerationContext.Default.InfoOutput);

        // Wire name must be "source", not "route".
        Assert.Contains("\"source\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"route\"", json, StringComparison.Ordinal);
        // Required fields must be present.
        Assert.Contains("\"pathStatus\"", json, StringComparison.Ordinal);
        Assert.Contains("\"status\"", json, StringComparison.Ordinal);
        Assert.Contains("\"canonicalPath\"", json, StringComparison.Ordinal);
        // Null statusReason must be omitted (WhenWritingNull).
        Assert.DoesNotContain("\"statusReason\"", json, StringComparison.Ordinal);

        await Verify(json, "json");
    }

    // ---------------------------------------------------------------------------
    // InstallationInfoParser: source preferred; legacy route accepted as fallback
    // ---------------------------------------------------------------------------

    [Fact]
    public void InstallationInfoParser_AcceptsSourceField()
    {
        var element = JsonDocument.Parse("""
            {
              "path": "/usr/local/bin/aspire",
              "source": "script",
              "pathStatus": "active",
              "status": "ok"
            }
            """).RootElement;

        var info = InstallationInfoParser.Parse(element);

        Assert.Equal("script", info.Route);
    }

    [Fact]
    public void InstallationInfoParser_FallsBackToRouteWhenSourceAbsent()
    {
        var element = JsonDocument.Parse("""
            {
              "path": "/usr/local/bin/aspire",
              "route": "winget",
              "pathStatus": "active",
              "status": "ok"
            }
            """).RootElement;

        var info = InstallationInfoParser.Parse(element);

        Assert.Equal("winget", info.Route);
    }

    [Fact]
    public void InstallationInfoParser_PrefersSourceOverRoute_WhenBothPresent()
    {
        // Forward/backward compatibility: a row may carry both fields; source wins.
        var element = JsonDocument.Parse("""
            {
              "path": "/usr/local/bin/aspire",
              "source": "brew",
              "route": "script",
              "pathStatus": "active",
              "status": "ok"
            }
            """).RootElement;

        var info = InstallationInfoParser.Parse(element);

        Assert.Equal("brew", info.Route);
    }

    // ---------------------------------------------------------------------------
    // Human rendering: BuildHumanRenderable
    // ---------------------------------------------------------------------------

    [Fact]
    public void BuildHumanRenderable_MapsLocalizedStatusesAndPathStatuses()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var executionContext = workspace.CreateExecutionContext(identityChannel: "stable", identityVersion: "13.4.0");
        var rows = new[]
        {
            new InfoInstallation
            {
                Kind = InfoInstallationKind.Installation,
                Path = "/usr/local/bin/aspire",
                PathStatus = InstallationPathStatus.NotOnPath,
                Status = InstallationInfoStatus.NotProbed,
            },
        };

        var plainText = RenderToPlainConsole(InstallationInfoOutput.BuildHumanRenderable(supportsLinks: false, executionContext, rows));
        outputHelper.WriteLine(plainText);

        // Localized human text, not the raw wire tokens.
        Assert.Contains(InfoOptionStrings.PathStatusNotOnPath, plainText, StringComparison.Ordinal);
        Assert.Contains(InfoOptionStrings.StatusNotProbed, plainText, StringComparison.Ordinal);
        Assert.DoesNotContain(InstallationPathStatus.NotOnPath, plainText, StringComparison.Ordinal);
        Assert.DoesNotContain(InstallationInfoStatus.NotProbed, plainText, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildHumanRenderable_PreservesHostileMarkupLikeValues_WithoutInterpretation()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var executionContext = workspace.CreateExecutionContext(identityChannel: "[blue]daily", identityVersion: "13.4.0");
        var rows = new[]
        {
            new InfoInstallation
            {
                Kind = InfoInstallationKind.Installation,
                Path = @"C:\tools\[red]aspire.exe",
                PathStatus = InstallationPathStatus.Active,
                Status = InstallationInfoStatus.Failed,
                StatusReason = "Missing [yellow]install metadata[/]",
            },
        };

        var plainText = RenderToPlainConsole(InstallationInfoOutput.BuildHumanRenderable(supportsLinks: false, executionContext, rows));
        outputHelper.WriteLine(plainText);

        // Hostile/peer-supplied values must survive verbatim as plain text — never
        // interpreted as Spectre markup tags (which would strip/colorize them or throw).
        Assert.Contains("[blue]daily", plainText, StringComparison.Ordinal);
        Assert.Contains(@"C:\tools\[red]aspire.exe", plainText, StringComparison.Ordinal);
        Assert.Contains("Missing [yellow]install metadata[/]", plainText, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildHumanRenderable_UsesFixedWidthLabelColumn_ValuesAlignAcrossDifferentLabelLengths()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var executionContext = workspace.CreateExecutionContext(identityChannel: "stable", identityVersion: "13.4.0");
        var rows = new[]
        {
            new InfoInstallation
            {
                Kind = InfoInstallationKind.Installation,
                Source = "peer-source-marker",
                PathStatus = InstallationPathStatus.Active,
                Status = InstallationInfoStatus.Ok,
            },
            new InfoInstallation
            {
                Kind = InfoInstallationKind.Installation,
                Version = "peer-version-marker",
                PathStatus = InstallationPathStatus.Active,
                Status = InstallationInfoStatus.Ok,
            },
        };

        var plainText = RenderToPlainConsole(InstallationInfoOutput.BuildHumanRenderable(supportsLinks: false, executionContext, rows));
        outputHelper.WriteLine(plainText);

        var lines = plainText.Split('\n');
        // "Source" (6 chars) and "Version" (7 chars) have different label lengths, but the
        // fixed-width label column (sized from every possible label, e.g. "Canonical path")
        // must still align both values' starting columns identically.
        var sourceLine = Assert.Single(lines, l => l.Contains("peer-source-marker", StringComparison.Ordinal));
        var versionLine = Assert.Single(lines, l => l.Contains("peer-version-marker", StringComparison.Ordinal));
        Assert.Equal(
            sourceLine.IndexOf("peer-source-marker", StringComparison.Ordinal),
            versionLine.IndexOf("peer-version-marker", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildHumanRenderable_WrappedLongValue_ContinuationIndentedToValueColumn()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var executionContext = workspace.CreateExecutionContext(identityChannel: "stable", identityVersion: "13.4.0");
        var longReason = string.Join(' ', Enumerable.Repeat("word", 40));
        var rows = new[]
        {
            new InfoInstallation
            {
                Kind = InfoInstallationKind.Installation,
                PathStatus = InstallationPathStatus.Active,
                Status = InstallationInfoStatus.Failed,
                StatusReason = longReason,
            },
        };

        // A narrow console forces the long reason value to wrap across multiple lines.
        var plainText = RenderToPlainConsole(
            InstallationInfoOutput.BuildHumanRenderable(supportsLinks: false, executionContext, rows),
            width: 40);
        outputHelper.WriteLine(plainText);

        var lines = plainText.Split('\n');
        var reasonLineIndex = Array.FindIndex(lines, l => l.Contains("word", StringComparison.Ordinal));
        Assert.True(reasonLineIndex >= 0, "Expected to find a line containing the wrapped reason text.");

        var reasonLine = lines[reasonLineIndex];
        var valueColumnStart = reasonLine.IndexOf("word", StringComparison.Ordinal);

        // The wrap must have actually occurred (otherwise this test isn't exercising anything).
        var continuationLine = lines[reasonLineIndex + 1];
        Assert.Contains("word", continuationLine, StringComparison.Ordinal);

        // The continuation line's text must start at the same column as the first line's
        // value (i.e., indented past the indent + label columns), not at column 0 and not
        // bleeding into the label column — this is what a bordered table or naive wrap would
        // get wrong.
        var continuationValueStart = continuationLine.IndexOf("word", StringComparison.Ordinal);
        Assert.Equal(valueColumnStart, continuationValueStart);
        Assert.True(valueColumnStart > 0, "Value column must not start at column 0 — the indent/label columns precede it.");
    }

    [Fact]
    public void BuildHumanRenderable_OmitsUnavailableNullableFields()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var executionContext = workspace.CreateExecutionContext(identityChannel: "stable", identityVersion: "13.4.0");
        var rows = new[]
        {
            new InfoInstallation
            {
                Kind = InfoInstallationKind.Installation,
                Path = "/usr/local/bin/aspire",
                PathStatus = InstallationPathStatus.Active,
                Status = InstallationInfoStatus.Ok,
                // Version, Channel, Hive, CanonicalPath, Source, StatusReason all null/absent.
            },
        };

        var plainText = RenderToPlainConsole(InstallationInfoOutput.BuildHumanRenderable(supportsLinks: false, executionContext, rows));
        outputHelper.WriteLine(plainText);

        // The top identity section always shows "Version"/"Channel" for the running CLI, so
        // scope the "omitted when absent" assertions to the per-row installation subsection.
        var installationsHeadingIndex = plainText.IndexOf(InfoOptionStrings.InstallationsHeading, StringComparison.Ordinal);
        Assert.True(installationsHeadingIndex >= 0, "Expected to find the Installations heading.");
        var installationSection = plainText[installationsHeadingIndex..];

        Assert.DoesNotContain(InfoOptionStrings.VersionLabel, installationSection, StringComparison.Ordinal);
        Assert.DoesNotContain(InfoOptionStrings.ChannelLabel, installationSection, StringComparison.Ordinal);
        Assert.DoesNotContain(InfoOptionStrings.HiveLabel, installationSection, StringComparison.Ordinal);
        Assert.DoesNotContain(InfoOptionStrings.CanonicalPathLabel, installationSection, StringComparison.Ordinal);
        Assert.DoesNotContain(InfoOptionStrings.SourceLabel, installationSection, StringComparison.Ordinal);
        Assert.DoesNotContain(InfoOptionStrings.ReasonLabel, installationSection, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildHumanRenderable_RendersUnbordered_NoTableBoxDrawingCharacters()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var executionContext = workspace.CreateExecutionContext(identityChannel: "stable", identityVersion: "13.4.0");
        var rows = new[]
        {
            new InfoInstallation
            {
                Kind = InfoInstallationKind.Installation,
                Path = "/usr/local/bin/aspire",
                PathStatus = InstallationPathStatus.Active,
                Status = InstallationInfoStatus.Ok,
            },
        };

        var plainText = RenderToPlainConsole(InstallationInfoOutput.BuildHumanRenderable(supportsLinks: false, executionContext, rows));
        outputHelper.WriteLine(plainText);

        // A bordered Table (the doctor-command rendering style) would emit box-drawing
        // characters such as ┌─┬─┐│├┼┤└┴┘. The --info renderer must use an unbordered
        // Grid/Rows instead.
        foreach (var boxChar in new[] { '┌', '┬', '┐', '│', '├', '┼', '┤', '└', '┴', '┘', '─' })
        {
            Assert.DoesNotContain(boxChar, plainText);
        }
    }

    [Fact]
    public void BuildHumanRenderable_IdentitySection_ShowsRunningCliVersionAndChannel()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var executionContext = workspace.CreateExecutionContext(identityChannel: "staging", identityVersion: "9.9.9");

        var plainText = RenderToPlainConsole(
            InstallationInfoOutput.BuildHumanRenderable(supportsLinks: false, executionContext, rows: []));
        outputHelper.WriteLine(plainText);

        Assert.Contains(InfoOptionStrings.InfoHeading, plainText, StringComparison.Ordinal);
        Assert.Contains("9.9.9", plainText, StringComparison.Ordinal);
        Assert.Contains("staging", plainText, StringComparison.Ordinal);
    }

    private static string RenderToPlainConsole(IRenderable renderable, int width = int.MaxValue)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            Interactive = InteractionSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
        });

        console.Profile.Width = width;
        console.Profile.Capabilities.Links = false;
        console.Write(renderable);

        return writer.ToString().Replace("\r\n", "\n");
    }
}
