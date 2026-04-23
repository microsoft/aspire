// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Layout;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Aspire.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.LayoutTests;

/// <summary>
/// Verifies that <see cref="LayoutDiscovery"/> successfully discovers a bundle layout
/// whose <c>managed/</c> and <c>dcp/</c> entries are reparse points pointing into a
/// versioned subdirectory (the new on-disk shape introduced for transactional installs).
/// </summary>
public class LayoutDiscoveryReparsePointTests(ITestOutputHelper outputHelper)
{
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

        var topManaged = Path.Combine(layoutRoot, BundleDiscovery.ManagedDirectoryName);
        var topDcp = Path.Combine(layoutRoot, BundleDiscovery.DcpDirectoryName);
        ReparsePoint.CreateOrReplace(topManaged, versionedManaged);
        ReparsePoint.CreateOrReplace(topDcp, versionedDcp);

        Assert.True(ReparsePoint.IsReparsePoint(topManaged));
        Assert.True(ReparsePoint.IsReparsePoint(topDcp));

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
        }
    }
}
