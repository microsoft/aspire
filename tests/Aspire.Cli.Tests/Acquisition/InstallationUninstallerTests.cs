// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Acquisition;
using Aspire.Cli.Bundles;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Acquisition;

/// <summary>
/// Behavior tests for <see cref="InstallationUninstaller"/>. Sets up
/// realistic on-disk layouts inside a temp workspace and asserts that
/// <see cref="InstallationUninstaller.Plan"/> produces the right
/// <see cref="UninstallOutcome"/> and <see cref="UninstallPlan.Removals"/>
/// list, then exercises <see cref="InstallationUninstaller.ExecuteAsync"/>
/// to confirm only the planned paths are removed.
/// </summary>
public class InstallationUninstallerTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void Plan_ScriptRoute_OnlyDeletesAspireOwnedFiles_NotSiblingRoutes()
    {
        // Layout: a shared ~/.aspire-like prefix where script-route owns
        // bin/aspire + the bundle marker + versions/<id>, alongside
        // sibling routes (dogfood/, hives/) that script-route uninstall
        // MUST NOT touch.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var prefix = workspace.WorkspaceRoot.FullName;

        WriteScriptRouteInstall(prefix, versionString: "13.0.0-preview.1");
        // Sibling that must survive — a PR install living alongside the
        // release install in the shared prefix.
        var siblingPrPrefix = Path.Combine(prefix, "dogfood", "pr-9999");
        Directory.CreateDirectory(Path.Combine(siblingPrPrefix, "bin"));
        File.WriteAllText(Path.Combine(siblingPrPrefix, "bin", "aspire"), "sibling binary");
        // Sibling hive that must survive.
        Directory.CreateDirectory(Path.Combine(prefix, "hives", "pr-9999"));
        File.WriteAllText(Path.Combine(prefix, "hives", "pr-9999", "marker"), "sibling hive");

        var uninstaller = NewUninstaller();
        var plan = uninstaller.Plan(prefix);

        Assert.Equal(UninstallOutcome.Proceed, plan.Outcome);
        Assert.Equal(InstallSource.Script, plan.Route);
        Assert.DoesNotContain(plan.Removals, p => p == siblingPrPrefix);
        Assert.DoesNotContain(plan.Removals, p => p.Contains(Path.Combine(prefix, "hives"), StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Removals, p => string.Equals(p.TrimEnd(Path.DirectorySeparatorChar), prefix.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Execute_ScriptRoute_RemovesOnlyVersionIdMatchingMarker()
    {
        // U-2: only the <id> directory matching the bundle version marker
        // is removed. A second stale <id> directory in the same versions/
        // tree must survive.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var prefix = workspace.WorkspaceRoot.FullName;
        var currentVersion = "13.0.0-preview.1|123456|6386208000000000";
        var activeId = BundleService.ComputeVersionId(currentVersion);
        var staleId = BundleService.ComputeVersionId("99.99.99|0|0");

        WriteScriptRouteInstall(prefix, versionString: currentVersion);
        // Synthesize a stale versions/<id>/ dir from a different install
        // lifecycle.
        var staleDir = Path.Combine(prefix, BundleService.VersionsDirectoryName, staleId);
        Directory.CreateDirectory(staleDir);
        File.WriteAllText(Path.Combine(staleDir, "stale.txt"), "should survive");

        var uninstaller = NewUninstaller();
        var plan = uninstaller.Plan(prefix);
        Assert.Equal(UninstallOutcome.Proceed, plan.Outcome);

        await uninstaller.ExecuteAsync(plan, TestContext.Current.CancellationToken);

        Assert.False(File.Exists(Path.Combine(prefix, "bin", BinaryName())));
        Assert.False(File.Exists(Path.Combine(prefix, "bin", InstallSidecarReader.SidecarFileName)));
        Assert.False(Directory.Exists(Path.Combine(prefix, BundleService.VersionsDirectoryName, activeId)));
        // Stale dir survives because it was not the active version.
        Assert.True(Directory.Exists(staleDir));
        // The shared prefix and its sibling subtrees survive.
        Assert.True(Directory.Exists(prefix));
    }

    [Fact]
    public void Plan_ScriptRoute_MissingVersionMarker_SkipsVersionsCleanup()
    {
        // Marker missing → planner refuses to touch versions/ and surfaces
        // a manual-step hint instead, so an unrelated future install's
        // version dirs cannot get nuked.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var prefix = workspace.WorkspaceRoot.FullName;
        WriteScriptRouteInstall(prefix, versionString: null /* no marker */);
        var someVersionsContent = Path.Combine(prefix, BundleService.VersionsDirectoryName, "stranger");
        Directory.CreateDirectory(someVersionsContent);

        var uninstaller = NewUninstaller();
        var plan = uninstaller.Plan(prefix);

        Assert.Equal(UninstallOutcome.Proceed, plan.Outcome);
        Assert.DoesNotContain(plan.Removals, p => p.Contains("stranger", StringComparison.Ordinal));
        Assert.Contains(plan.ManualSteps, step => step.Contains("Bundle version marker missing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Execute_PrRoute_RemovesPrefixAndMatchingHive()
    {
        // Default dogfood layout: <root>/.aspire/dogfood/pr-1234 ↔
        // <root>/.aspire/hives/pr-1234. The whole pr-prefix is owned by
        // the PR install (no sharing); the hive must come along.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.WorkspaceRoot.FullName;
        var prPrefix = Path.Combine(root, "dogfood", "pr-1234");
        WriteSidecar(Path.Combine(prPrefix, "bin"), wireSource: "pr");
        var prHive = Path.Combine(root, "hives", "pr-1234");
        Directory.CreateDirectory(prHive);
        File.WriteAllText(Path.Combine(prHive, "packages.config"), "stub");

        var uninstaller = NewUninstaller();
        var plan = uninstaller.Plan(prPrefix);

        Assert.Equal(UninstallOutcome.Proceed, plan.Outcome);
        Assert.Equal(InstallSource.Pr, plan.Route);
        Assert.Contains(prPrefix, plan.Removals);
        Assert.Contains(prHive, plan.Removals);

        await uninstaller.ExecuteAsync(plan, TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(prPrefix));
        Assert.False(Directory.Exists(prHive));
    }

    [Fact]
    public void Plan_PrRoute_NonDefaultLayout_OmitsHiveAndAddsManualHint()
    {
        // Custom --install-path PR installs don't sit under
        // .../dogfood/pr-<N>, so we can't infer the hive label.
        // Verifies a manual-step hint is surfaced and the hive is NOT in
        // the removal set.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var prPrefix = Path.Combine(workspace.WorkspaceRoot.FullName, "custom-install");
        WriteSidecar(Path.Combine(prPrefix, "bin"), wireSource: "pr");

        var uninstaller = NewUninstaller();
        var plan = uninstaller.Plan(prPrefix);

        Assert.Equal(UninstallOutcome.Proceed, plan.Outcome);
        Assert.Contains(prPrefix, plan.Removals);
        Assert.DoesNotContain(plan.Removals, p => p.Contains("hives", StringComparison.Ordinal));
        Assert.Contains(plan.ManualSteps, step => step.Contains("does not follow the default", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("winget", "winget uninstall")]
    [InlineData("brew", "brew uninstall")]
    [InlineData("dotnet-tool", "dotnet tool uninstall")]
    public void Plan_PackagerRoute_RefusesWithRouteSpecificHint(string wireSource, string expectedHintFragment)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var prefix = workspace.WorkspaceRoot.FullName;
        WriteSidecar(Path.Combine(prefix, "bin"), wireSource);

        var uninstaller = NewUninstaller();
        var plan = uninstaller.Plan(prefix);

        Assert.Equal(UninstallOutcome.RefusedPackagerOwned, plan.Outcome);
        Assert.Empty(plan.Removals);
        Assert.Contains(plan.ManualSteps, step => step.Contains(expectedHintFragment, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Plan_MissingSidecar_RefusesWithManualRemoveHint()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var prefix = workspace.WorkspaceRoot.FullName;
        Directory.CreateDirectory(Path.Combine(prefix, "bin"));
        // No sidecar.

        var uninstaller = NewUninstaller();
        var plan = uninstaller.Plan(prefix);

        Assert.Equal(UninstallOutcome.RefusedUnknownRoute, plan.Outcome);
        Assert.Empty(plan.Removals);
        Assert.Contains(plan.ManualSteps, step => step.Contains("rm -rf", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_MissingPrefix_ReturnsNothingToDo()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var missing = Path.Combine(workspace.WorkspaceRoot.FullName, "not-there");

        var uninstaller = NewUninstaller();
        var plan = uninstaller.Plan(missing);

        Assert.Equal(UninstallOutcome.NothingToDo, plan.Outcome);
        Assert.NotNull(plan.RefusalReason);
    }

    [Fact]
    public void Plan_TargetContainsRunningCli_RefusesSelf()
    {
        // Stage a layout that contains the running process path, so the
        // ancestor check trips. We use the actual current-process binary
        // dir to verify symlink-aware comparison works end-to-end.
        var processPath = Environment.ProcessPath;
        Assert.NotNull(processPath);
        var processDir = Path.GetDirectoryName(processPath)!;

        // Climb to the directory under which the current process sits.
        // We pass that directory as the uninstall target and confirm the
        // planner refuses with the self-protect outcome.
        // The sidecar is written inside processDir/bin/ so the planner
        // actually classifies it as a script-route install, then the
        // running-from-prefix check should override.
        var binSibling = Path.Combine(processDir, "bin");
        var ownedByTest = false;
        try
        {
            if (!Directory.Exists(binSibling))
            {
                Directory.CreateDirectory(binSibling);
                ownedByTest = true;
            }
            var sidecarPath = Path.Combine(binSibling, InstallSidecarReader.SidecarFileName);
            var hadSidecar = File.Exists(sidecarPath);
            if (!hadSidecar)
            {
                File.WriteAllText(sidecarPath, "{\"source\":\"script\"}");
            }
            try
            {
                var uninstaller = NewUninstaller();
                var plan = uninstaller.Plan(processDir);
                Assert.Equal(UninstallOutcome.RefusedSelf, plan.Outcome);
                Assert.Empty(plan.Removals);
            }
            finally
            {
                if (!hadSidecar)
                {
                    File.Delete(sidecarPath);
                }
            }
        }
        finally
        {
            if (ownedByTest)
            {
                Directory.Delete(binSibling);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_OnRefusalPlan_Throws()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var prefix = workspace.WorkspaceRoot.FullName;
        WriteSidecar(Path.Combine(prefix, "bin"), wireSource: "brew");

        var uninstaller = NewUninstaller();
        var plan = uninstaller.Plan(prefix);
        Assert.Equal(UninstallOutcome.RefusedPackagerOwned, plan.Outcome);

        await Assert.ThrowsAsync<InvalidOperationException>(() => uninstaller.ExecuteAsync(plan, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Plan_LocalHiveRoute_UsesSurgicalScriptRouteRemoval()
    {
        // localhive shares the script-route shared-prefix layout. The
        // uninstaller must therefore use the same surgical removal logic
        // as script — only the Aspire-owned files inside the shared
        // prefix get removed, sibling subtrees survive.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var prefix = workspace.WorkspaceRoot.FullName;

        var binDir = Path.Combine(prefix, "bin");
        Directory.CreateDirectory(binDir);
        File.WriteAllText(Path.Combine(binDir, BinaryName()), "fake aspire binary");
        File.WriteAllText(
            Path.Combine(binDir, InstallSidecarReader.SidecarFileName),
            "{\"source\":\"localhive\"}");
        // Sibling subtree that must survive a localhive uninstall.
        var siblingHive = Path.Combine(prefix, "hives", "local");
        Directory.CreateDirectory(siblingHive);
        File.WriteAllText(Path.Combine(siblingHive, "marker"), "sibling hive");

        var uninstaller = NewUninstaller();
        var plan = uninstaller.Plan(prefix);

        Assert.Equal(UninstallOutcome.Proceed, plan.Outcome);
        Assert.Equal(InstallSource.LocalHive, plan.Route);
        // Removals must touch only files inside <prefix>/bin/, not the
        // sibling hive tree.
        Assert.DoesNotContain(plan.Removals, p => p.Contains(siblingHive, StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Removals, p => string.Equals(p.TrimEnd(Path.DirectorySeparatorChar), prefix.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal));
        // The Aspire binary IS removed.
        Assert.Contains(plan.Removals, p => p.EndsWith(BinaryName(), StringComparison.Ordinal));
    }

    private static InstallationUninstaller NewUninstaller()
    {
        var sidecarReader = new InstallSidecarReader();
        return new InstallationUninstaller(sidecarReader, NullLogger<InstallationUninstaller>.Instance);
    }

    private static void WriteSidecar(string binDir, string wireSource)
    {
        Directory.CreateDirectory(binDir);
        File.WriteAllText(
            Path.Combine(binDir, InstallSidecarReader.SidecarFileName),
            $"{{\"source\":\"{wireSource}\"}}");
    }

    private static void WriteScriptRouteInstall(string prefix, string? versionString)
    {
        var binDir = Path.Combine(prefix, "bin");
        Directory.CreateDirectory(binDir);
        File.WriteAllText(Path.Combine(binDir, BinaryName()), "fake aspire binary");
        WriteSidecar(binDir, wireSource: "script");
        if (!string.IsNullOrEmpty(versionString))
        {
            BundleService.WriteVersionMarker(prefix, versionString);
            var versionId = BundleService.ComputeVersionId(versionString);
            var versionDir = Path.Combine(prefix, BundleService.VersionsDirectoryName, versionId);
            Directory.CreateDirectory(versionDir);
            File.WriteAllText(Path.Combine(versionDir, "payload.txt"), "fake payload");
        }
    }

    private static string BinaryName() => OperatingSystem.IsWindows() ? "aspire.exe" : "aspire";
}
