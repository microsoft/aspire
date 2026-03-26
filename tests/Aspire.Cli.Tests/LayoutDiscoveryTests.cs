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
    public void DiscoverLayout_FindsWellKnownPath_WhenValidLayoutExists()
    {
        // Create a temp dir simulating ~/.aspire/ with a valid layout
        var fakeHome = CreateTempDirectory();
        var aspireDir = Path.Combine(fakeHome, ".aspire");
        Directory.CreateDirectory(aspireDir);
        CreateValidBundleLayout(aspireDir);

        var context = CreateContext(fakeHome);
        var discovery = new LayoutDiscovery(context, NullLogger<LayoutDiscovery>.Instance);
        var layout = discovery.DiscoverLayout();

        Assert.NotNull(layout);
        Assert.Equal(aspireDir, layout.LayoutPath);
    }

    [Fact]
    public void DiscoverLayout_ReturnsNull_WhenNoValidLayout()
    {
        // Create a temp dir with no valid layout anywhere
        var fakeHome = CreateTempDirectory();
        var aspireDir = Path.Combine(fakeHome, ".aspire");
        Directory.CreateDirectory(aspireDir);
        // No managed/dcp directories

        var context = CreateContext(fakeHome);
        var discovery = new LayoutDiscovery(context, NullLogger<LayoutDiscovery>.Instance);
        var layout = discovery.DiscoverLayout();

        // Layout could still be found via relative path (unlikely in test), but
        // it should NOT find one at the well-known path because it's incomplete
        if (layout is not null)
        {
            Assert.NotEqual(aspireDir, layout.LayoutPath);
        }
    }

    [Fact]
    public void DiscoverLayout_RejectsLayout_WhenManagedDirectoriesExistButExecutableIsMissing()
    {
        var fakeHome = CreateTempDirectory();
        var aspireDir = Path.Combine(fakeHome, ".aspire");
        Directory.CreateDirectory(Path.Combine(aspireDir, BundleDiscovery.ManagedDirectoryName));
        Directory.CreateDirectory(Path.Combine(aspireDir, BundleDiscovery.DcpDirectoryName));
        // No aspire-managed executable

        var context = CreateContext(fakeHome);
        var discovery = new LayoutDiscovery(context, NullLogger<LayoutDiscovery>.Instance);
        var layout = discovery.DiscoverLayout();

        // The incomplete directory should not be selected
        if (layout is not null)
        {
            Assert.NotEqual(aspireDir, layout.LayoutPath);
        }
    }

    private string CreateTempDirectory()
    {
        var dir = Directory.CreateTempSubdirectory("aspire-layout-test-").FullName;
        _tempDirs.Add(dir);
        return dir;
    }

    private static CliExecutionContext CreateContext(string homeDir, IReadOnlyDictionary<string, string?>? environmentVariables = null)
    {
        var aspireDir = Path.Combine(homeDir, ".aspire");
        return new CliExecutionContext(
            new DirectoryInfo("."),
            new DirectoryInfo(Path.Combine(aspireDir, "hives")),
            new DirectoryInfo(Path.Combine(aspireDir, "cache")),
            new DirectoryInfo(Path.Combine(aspireDir, "sdks")),
            new DirectoryInfo(Path.Combine(aspireDir, "logs")),
            "test.log",
            environmentVariables: environmentVariables ?? new Dictionary<string, string?>(),
            homeDirectory: new DirectoryInfo(homeDir));
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
