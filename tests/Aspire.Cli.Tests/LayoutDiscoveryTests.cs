// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Layout;
using Aspire.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests;

public class LayoutDiscoveryTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    [Fact]
    public void DiscoverLayout_PrefersEnvVarOverWellKnownPath()
    {
        // Create a temp directory with a valid layout and set it via the env var.
        // Even if a real layout exists at ~/.aspire/, the env var should take priority.
        var fakeLayoutDir = CreateTempDirectory();
        CreateValidBundleLayout(fakeLayoutDir);

        var envBefore = Environment.GetEnvironmentVariable(BundleDiscovery.LayoutPathEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(BundleDiscovery.LayoutPathEnvVar, fakeLayoutDir);

            var discovery = new LayoutDiscovery(NullLogger<LayoutDiscovery>.Instance);
            var layout = discovery.DiscoverLayout();

            Assert.NotNull(layout);
            Assert.Equal(fakeLayoutDir, layout.LayoutPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(BundleDiscovery.LayoutPathEnvVar, envBefore);
        }
    }

    [Fact]
    public void DiscoverLayout_IgnoresEnvVar_WhenPathHasNoValidLayout()
    {
        // Point the env var at an empty directory — it should be skipped.
        var emptyDir = CreateTempDirectory();

        var envBefore = Environment.GetEnvironmentVariable(BundleDiscovery.LayoutPathEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(BundleDiscovery.LayoutPathEnvVar, emptyDir);

            var discovery = new LayoutDiscovery(NullLogger<LayoutDiscovery>.Instance);
            var layout = discovery.DiscoverLayout();

            // Layout may still be discovered via relative or well-known paths,
            // but it must NOT come from the invalid env var path.
            if (layout is not null)
            {
                Assert.NotEqual(emptyDir, layout.LayoutPath);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(BundleDiscovery.LayoutPathEnvVar, envBefore);
        }
    }

    [Fact]
    public void DiscoverLayout_RejectsLayout_WhenManagedDirectoriesExistButExecutableIsMissing()
    {
        // Create directories but no aspire-managed executable.
        var incompleteDir = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(incompleteDir, BundleDiscovery.ManagedDirectoryName));
        Directory.CreateDirectory(Path.Combine(incompleteDir, BundleDiscovery.DcpDirectoryName));

        var envBefore = Environment.GetEnvironmentVariable(BundleDiscovery.LayoutPathEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(BundleDiscovery.LayoutPathEnvVar, incompleteDir);

            var discovery = new LayoutDiscovery(NullLogger<LayoutDiscovery>.Instance);
            var layout = discovery.DiscoverLayout();

            // The incomplete directory should not be selected as the layout path.
            // The discovery may still find a valid layout elsewhere (relative or well-known).
            if (layout is not null)
            {
                Assert.NotEqual(incompleteDir, layout.LayoutPath);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(BundleDiscovery.LayoutPathEnvVar, envBefore);
        }
    }

    private string CreateTempDirectory()
    {
        var dir = Directory.CreateTempSubdirectory("aspire-layout-test-").FullName;
        _tempDirs.Add(dir);
        return dir;
    }

    private static void CreateValidBundleLayout(string layoutPath)
    {
        var managedDir = Path.Combine(layoutPath, BundleDiscovery.ManagedDirectoryName);
        var dcpDir = Path.Combine(layoutPath, BundleDiscovery.DcpDirectoryName);
        Directory.CreateDirectory(managedDir);
        Directory.CreateDirectory(dcpDir);

        // Create a dummy aspire-managed executable
        var managedExeName = BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName);
        File.WriteAllText(Path.Combine(managedDir, managedExeName), "dummy");
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }
}
