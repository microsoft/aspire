// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// Regression test for https://github.com/microsoft/aspire/issues/15891.
/// </summary>
/// <remarks>
/// <para>
/// Reproduces the user-visible failure where <c>aspire update --channel daily</c> on a
/// file-based AppHost referencing several stable Aspire integration packages crashed
/// with <c>NU1103</c>. The CLI rewrote <c>nuget.config</c> to add the daily feed plus a
/// <c>&lt;packageSourceMapping&gt;</c> pinning <c>Aspire*</c> to that feed, then ran each
/// per-package <c>dotnet package add</c> with restore enabled. Restore on the first add
/// saw the still-stable references for the not-yet-bumped packages, but the new mapping
/// blocked the nuget.org fallback for them, so NuGet emitted <c>NU1103</c> and the
/// update aborted halfway through.
/// </para>
/// <para>
/// The fix in <c>ProjectUpdater</c> passes <c>--no-restore</c> on every per-package add
/// and runs a single <c>dotnet restore</c> after every package edit has been applied,
/// so the packageSourceMapping never sees a half-updated reference graph. This test
/// exercises that invariant end-to-end against the LocalHive channel — the bug fires
/// for any <c>Explicit</c> channel (daily, staging, PR hives, local), and LocalHive is
/// the only channel a source-build E2E run can deterministically control.
/// </para>
/// <para>
/// This lives in its own test class (separate from <see cref="ChannelUpdateWorkflowTests"/>)
/// so CI's per-class job split puts the regression on its own runner — failures point
/// directly at the issue without sharing a budget with unrelated channel tests.
/// </para>
/// </remarks>
public sealed class UpdateChannelNuGetConfigOrderingTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task AspireUpdateAppliesAllPackageEditsBeforeRestoringWhenNuGetConfigGainsSourceMapping()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);

        // The bug only fires when the channel switch causes the CLI to rewrite nuget.config
        // and add a packageSourceMapping. That requires an Explicit channel (daily, staging,
        // PR hive, or local) with a controlled set of packages. Only LocalHive gives a
        // source-build E2E run that level of control; for other strategies we skip rather
        // than try to reproduce against a moving public daily feed.
        if (strategy.Mode != CliInstallMode.LocalHive)
        {
            Assert.Skip(
                "Issue #15891 requires an Explicit channel with controlled package versions. " +
                "Run with ASPIRE_E2E_ARCHIVE pointing at a locally-built archive (LocalHive).");
        }

        // Pre-stage the locally-built nupkgs for the integration packages we'll reference.
        // Three is enough to make the bug surface deterministically: the per-package adds run
        // sequentially, so the first one's restore (without the fix) sees the other two still
        // pinned to the stable version that doesn't exist on the local feed.
        var localChannel = CliE2ETestHelpers.PrepareLocalChannel(
            repoRoot,
            strategy,
            ["Aspire.Hosting.Redis.", "Aspire.Hosting.PostgreSQL.", "Aspire.Hosting.Kafka."]);

        Assert.NotNull(localChannel);

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot, strategy, output, mountDockerSocket: false, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        // Suppress the post-update CLI self-update prompt so it doesn't block the test waiting
        // for "Update successful!". Same pattern as CentralPackageManagementTests.
        await auto.TypeAsync("aspire config set features.updateNotificationsEnabled false -g");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Run aspire init non-interactively to scaffold a single-file C# AppHost. We'll
        // overwrite the generated apphost.cs and nuget.config below to set up the bug
        // pre-condition (stable Aspire packages + only nuget.org configured).
        await auto.TypeAsync("aspire init --language csharp");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Created aspire.config.json", timeout: TimeSpan.FromMinutes(2));
        await auto.DeclineAgentInitPromptAsync(counter);

        var appHostCsPath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs");
        var nugetConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, "nuget.config");
        var aspireConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.config.json");

        Assert.True(File.Exists(appHostCsPath), $"Expected apphost.cs at: {appHostCsPath}");
        Assert.True(File.Exists(aspireConfigPath), $"Expected aspire.config.json at: {aspireConfigPath}");

        // Set up the bug pre-condition from the host side — the workspace is bind-mounted
        // into the container so these writes are visible to the in-container CLI.

        // 1. apphost.cs: reference three stable Aspire integration packages from nuget.org.
        //    13.2.1 is the same release David Fowler used in the issue's repro and is real
        //    on nuget.org for all three packages, so the initial state matches the bug
        //    report exactly: stable references that nuget.org can resolve before the
        //    channel switch.
        const string StablePackageVersion = "13.2.1";
        await File.WriteAllTextAsync(appHostCsPath,
            $$"""
            #:sdk Aspire.AppHost.Sdk@{{StablePackageVersion}}
            #:package Aspire.Hosting.AppHost@{{StablePackageVersion}}
            #:package Aspire.Hosting.Redis@{{StablePackageVersion}}
            #:package Aspire.Hosting.PostgreSQL@{{StablePackageVersion}}
            #:package Aspire.Hosting.Kafka@{{StablePackageVersion}}

            var builder = DistributedApplication.CreateBuilder(args);
            builder.Build().Run();
            """);

        // 2. nuget.config: only nuget.org, no Aspire feed, no packageSourceMapping. This is
        //    the file shape from the issue. The CLI's NuGetConfigMerger will rewrite it to
        //    add the local feed plus a <packageSourceMapping> pinning Aspire* to that feed —
        //    that rewrite is exactly what triggers the regression on the per-package restores
        //    when the fix is missing.
        await File.WriteAllTextAsync(nugetConfigPath,
            """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
              </packageSources>
            </configuration>
            """);

        // 3. aspire.config.json: point at the locally-built channel/SDK so `aspire update`
        //    has a concrete Explicit target to compute updates against. Preserves the
        //    appHost/profiles sections that `aspire init` wrote.
        CliE2ETestHelpers.WriteLocalChannelSettings(workspace.WorkspaceRoot.FullName, localChannel.SdkVersion);

        // Run the update. Pass --channel local explicitly to make the CLI's channel
        // resolution deterministic regardless of any global channel state.
        await auto.TypeAsync("aspire update --channel local");
        await auto.EnterAsync();

        await auto.WaitUntilTextAsync("Perform updates?", timeout: TimeSpan.FromMinutes(2));
        await auto.EnterAsync();

        // The pre-existing nuget.config doesn't already contain the local feed, so the CLI
        // will prompt to apply the merged changes. Accept the default (Yes).
        await auto.WaitUntilTextAsync("Apply these changes to NuGet.config?", timeout: TimeSpan.FromMinutes(1));
        await auto.EnterAsync();

        // Without the fix this is where the test would fail: NuGet would emit NU1103 on
        // the first per-package restore and "Update successful!" would never appear before
        // the timeout. With the fix all package edits land first, then the single deferred
        // restore sees a coherent reference graph and succeeds.
        await auto.WaitUntilTextAsync("Update successful!", timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        // Host-side assertions on the post-update files.
        var updatedAppHostCs = await File.ReadAllTextAsync(appHostCsPath);
        var updatedNuGetConfig = await File.ReadAllTextAsync(nugetConfigPath);

        // Pre-condition was actually exercised: nuget.config gained a packageSourceMapping
        // for Aspire*. If this assertion fails it means the rewrite never happened and the
        // test isn't actually guarding the bug.
        Assert.Contains("packageSourceMapping", updatedNuGetConfig, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Aspire", updatedNuGetConfig, StringComparison.Ordinal);

        // Every Aspire integration directive was bumped — proves the loop ran to completion
        // rather than aborting after the first add (the user-visible symptom of the bug).
        foreach (var packageId in new[]
                 {
                     "Aspire.Hosting.AppHost",
                     "Aspire.Hosting.Redis",
                     "Aspire.Hosting.PostgreSQL",
                     "Aspire.Hosting.Kafka",
                 })
        {
            Assert.Contains($"#:package {packageId}@{localChannel.SdkVersion}", updatedAppHostCs, StringComparison.Ordinal);
            Assert.DoesNotContain($"#:package {packageId}@{StablePackageVersion}", updatedAppHostCs, StringComparison.Ordinal);
        }

        // The SDK directive is bumped on the same code path; assert it too so a future
        // regression that only updates packages but not the SDK is also caught.
        Assert.Contains($"#:sdk Aspire.AppHost.Sdk@{localChannel.SdkVersion}", updatedAppHostCs, StringComparison.Ordinal);

        // Best-effort cleanup of the global feature flag we toggled above so other tests in
        // the same shared home aren't affected. Bound the wait so cleanup never burns the
        // automator's default timeout, and accept either OK or ERR — we only care that the
        // shell returned to a prompt.
        try
        {
            await auto.TypeAsync("aspire config delete features.updateNotificationsEnabled -g");
            await auto.EnterAsync();
            await auto.WaitForAnyPromptAsync(counter, TimeSpan.FromSeconds(30));
        }
        catch
        {
        }

        await auto.TypeAsync("exit");
        await auto.EnterAsync();
        await pendingRun;
    }
}
