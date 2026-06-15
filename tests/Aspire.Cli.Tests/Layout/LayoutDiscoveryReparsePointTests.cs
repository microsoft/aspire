// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Layout;
using Aspire.Cli.Tests.Acquisition;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Aspire.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.LayoutTests;

/// <summary>
/// Verifies that <see cref="LayoutDiscovery"/> successfully discovers a bundle layout
/// whose <c>bundle/</c> entry is a reparse point pointing at a versioned subdirectory
/// (the new on-disk shape introduced for transactional installs).
/// </summary>
/// <remarks>
/// The Aspire-home fallback test mutates the process-wide <c>ASPIRE_HOME</c> environment
/// variable, which is also read by <see cref="CliPathHelper.GetDefaultAspireHomeDirectory()"/>
/// and indirectly by <c>BundleService.ComputeDefaultExtractDir</c>. xUnit runs test classes
/// in parallel by default, so without joining <see cref="EnvVarMutatingTestCollection"/> a
/// concurrent reader in another suite could observe the temporary override and assert against
/// the wrong path. Disabling parallelization across the mutating tests prevents that race.
/// </remarks>
[Collection(EnvVarMutatingTestCollection.Name)]
public class LayoutDiscoveryReparsePointTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void DiscoverLayout_ResolvesProcessPathSymlinkBeforeRelativeDiscovery()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(),
            "Symlink resolution test only runs on Linux/macOS where unprivileged symlink creation is reliable.");

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var realLayoutRoot = Path.Combine(
            workspace.WorkspaceRoot.FullName,
            "WinGet",
            "Packages",
            "Microsoft.Aspire_Microsoft.Winget.Source_8wekyb3d8bbwe");
        CreateValidBundleLayout(realLayoutRoot);

        var realBinary = Path.Combine(realLayoutRoot, "aspire");
        File.WriteAllText(realBinary, "stub");

        var linksDir = Path.Combine(workspace.WorkspaceRoot.FullName, "WinGet", "Links");
        Directory.CreateDirectory(linksDir);
        var linkPath = Path.Combine(linksDir, "aspire");
        File.CreateSymbolicLink(linkPath, realBinary);

        var discovery = new LayoutDiscovery(NullLogger<LayoutDiscovery>.Instance)
        {
            ProcessPathOverride = linkPath
        };

        var layout = discovery.DiscoverLayout();

        Assert.NotNull(layout);
        Assert.Equal(realLayoutRoot, layout!.LayoutPath);
    }

    [Fact]
    public void DiscoverLayout_FallsBackToRawProcessPathWhenResolvedPathHasNoLayout()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(),
            "Symlink resolution test only runs on Linux/macOS where unprivileged symlink creation is reliable.");

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var rawLayoutRoot = Path.Combine(workspace.WorkspaceRoot.FullName, "custom-layout");
        CreateValidBundleLayout(rawLayoutRoot);

        var realBinaryDir = Path.Combine(workspace.WorkspaceRoot.FullName, "real-binary");
        Directory.CreateDirectory(realBinaryDir);
        var realBinary = Path.Combine(realBinaryDir, "aspire");
        File.WriteAllText(realBinary, "stub");

        var linkPath = Path.Combine(rawLayoutRoot, "aspire");
        File.CreateSymbolicLink(linkPath, realBinary);

        var discovery = new LayoutDiscovery(NullLogger<LayoutDiscovery>.Instance)
        {
            ProcessPathOverride = linkPath
        };

        var layout = discovery.DiscoverLayout();

        Assert.NotNull(layout);
        Assert.Equal(rawLayoutRoot, layout!.LayoutPath);
    }

    [Fact]
    public void DiscoverLayout_ResolvesThroughReparsePoints()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var layoutRoot = workspace.WorkspaceRoot.FullName;

        var versionsDir = Path.Combine(layoutRoot, "versions", "v1");
        var versionedManaged = Path.Combine(versionsDir, BundleDiscovery.ManagedDirectoryName);
        var versionedDcp = Path.Combine(versionsDir, BundleDiscovery.DcpDirectoryName);
        Directory.CreateDirectory(versionedManaged);
        Directory.CreateDirectory(versionedDcp);
        File.WriteAllText(
            Path.Combine(versionedManaged, BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName)),
            "stub");

        // Create a single bundle/ link pointing at the versioned directory.
        var bundleLink = Path.Combine(layoutRoot, BundleDiscovery.BundleDirectoryName);
        ReparsePoint.CreateOrReplace(bundleLink, versionsDir);

        Assert.True(ReparsePoint.IsReparsePoint(bundleLink));

        var originalEnv = Environment.GetEnvironmentVariable(BundleDiscovery.LayoutPathEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(BundleDiscovery.LayoutPathEnvVar, layoutRoot);
            var discovery = new LayoutDiscovery(NullLogger<LayoutDiscovery>.Instance);
            var layout = discovery.DiscoverLayout();

            Assert.NotNull(layout);
            Assert.Equal(layoutRoot, layout!.LayoutPath);
            Assert.True(discovery.IsBundleModeAvailable());
        }
        finally
        {
            Environment.SetEnvironmentVariable(BundleDiscovery.LayoutPathEnvVar, originalEnv);
            ReparsePoint.RemoveIfExists(bundleLink);
        }
    }

    [Fact]
    public void DiscoverLayout_FallsBackToAspireHomeWhenLayoutIsNotRelativeToCli()
    {
        // Simulates a sidecar-less install where the CLI binary lives somewhere
        // unrelated to the extracted bundle (e.g. a Nix store / read-only path)
        // and the bundle is extracted to $ASPIRE_HOME.
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var binaryDir = Path.Combine(workspace.WorkspaceRoot.FullName, "opt", "aspire", "bin");
        Directory.CreateDirectory(binaryDir);
        var binaryPath = Path.Combine(binaryDir, OperatingSystem.IsWindows() ? "aspire.exe" : "aspire");
        File.WriteAllText(binaryPath, "stub");

        var aspireHome = Path.Combine(workspace.WorkspaceRoot.FullName, "home", ".aspire");
        CreateValidBundleLayout(aspireHome);

        var originalAspireHome = Environment.GetEnvironmentVariable(CliPathHelper.AspireHomeEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(CliPathHelper.AspireHomeEnvironmentVariable, aspireHome);

            var discovery = new LayoutDiscovery(NullLogger<LayoutDiscovery>.Instance)
            {
                ProcessPathOverride = binaryPath
            };

            var layout = discovery.DiscoverLayout();

            Assert.NotNull(layout);
            Assert.Equal(aspireHome, layout!.LayoutPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(CliPathHelper.AspireHomeEnvironmentVariable, originalAspireHome);
        }
    }

    [Fact]
    public void DiscoverLayout_PrefersRelativeLayoutOverAspireHomeLayout()
    {
        // The Aspire-home probe is the last fallback in DiscoverLayout so that
        // package-managed or sidecar-owned installs (winget/brew/dotnet-tool/script/
        // pr/localhive) — which already colocate the bundle next to or above the
        // CLI binary — are never shadowed by a stale or differently-versioned
        // layout left over in $HOME/.aspire from an earlier sidecar-less run.
        // This regression test pins that ordering: when both layouts exist, the
        // relative-to-CLI layout wins.
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Colocated (relative) layout: CLI in {layoutRoot}/bin/aspire with the
        // bundle as a sibling of bin/.
        var relativeLayoutRoot = Path.Combine(workspace.WorkspaceRoot.FullName, "colocated");
        var binDir = Path.Combine(relativeLayoutRoot, "bin");
        Directory.CreateDirectory(binDir);
        var binaryPath = Path.Combine(binDir, OperatingSystem.IsWindows() ? "aspire.exe" : "aspire");
        File.WriteAllText(binaryPath, "stub");
        CreateValidBundleLayout(relativeLayoutRoot);

        // Competing $ASPIRE_HOME layout: also valid but should not be selected.
        var aspireHome = Path.Combine(workspace.WorkspaceRoot.FullName, "home", ".aspire");
        CreateValidBundleLayout(aspireHome);

        var originalAspireHome = Environment.GetEnvironmentVariable(CliPathHelper.AspireHomeEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(CliPathHelper.AspireHomeEnvironmentVariable, aspireHome);

            var discovery = new LayoutDiscovery(NullLogger<LayoutDiscovery>.Instance)
            {
                ProcessPathOverride = binaryPath
            };

            var layout = discovery.DiscoverLayout();

            Assert.NotNull(layout);
            Assert.Equal(relativeLayoutRoot, layout!.LayoutPath);
            Assert.NotEqual(aspireHome, layout.LayoutPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(CliPathHelper.AspireHomeEnvironmentVariable, originalAspireHome);
        }
    }

    private static void CreateValidBundleLayout(string layoutRoot)
    {
        var bundleDir = Path.Combine(layoutRoot, BundleDiscovery.BundleDirectoryName);
        var managedDir = Path.Combine(bundleDir, BundleDiscovery.ManagedDirectoryName);
        var dcpDir = Path.Combine(bundleDir, BundleDiscovery.DcpDirectoryName);
        Directory.CreateDirectory(managedDir);
        Directory.CreateDirectory(dcpDir);
        File.WriteAllText(
            Path.Combine(managedDir, BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName)),
            "stub");
    }
}
