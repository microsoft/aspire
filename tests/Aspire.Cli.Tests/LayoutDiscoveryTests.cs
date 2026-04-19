// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Bundles;
using Aspire.Cli.Layout;
using Aspire.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests;

public class LayoutDiscoveryTests
{
    [Fact]
    public void DiscoverLayout_ReturnsLayout_ForLegacyLayoutWithoutMarkers()
    {
        var layoutDirectory = CreateLayout(writeVersionMarker: false, writeExtractionInProgressMarker: false);

        try
        {
            using var scope = new LayoutPathEnvironmentVariableScope(layoutDirectory.FullName);
            var discovery = new LayoutDiscovery(NullLogger<LayoutDiscovery>.Instance);

            var layout = discovery.DiscoverLayout();

            Assert.NotNull(layout);
            Assert.Equal(layoutDirectory.FullName, layout!.LayoutPath);
        }
        finally
        {
            layoutDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void DiscoverLayout_ReturnsNull_WhenExtractionInProgressMarkerExists()
    {
        var layoutDirectory = CreateLayout(writeVersionMarker: true, writeExtractionInProgressMarker: true);

        try
        {
            using var scope = new LayoutPathEnvironmentVariableScope(layoutDirectory.FullName);
            var discovery = new LayoutDiscovery(NullLogger<LayoutDiscovery>.Instance);

            Assert.Null(discovery.DiscoverLayout());
        }
        finally
        {
            layoutDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void DiscoverLayout_ReturnsLayout_WhenMarkersIndicateComplete()
    {
        var layoutDirectory = CreateLayout(writeVersionMarker: true, writeExtractionInProgressMarker: false);

        try
        {
            using var scope = new LayoutPathEnvironmentVariableScope(layoutDirectory.FullName);
            var discovery = new LayoutDiscovery(NullLogger<LayoutDiscovery>.Instance);

            var layout = discovery.DiscoverLayout();

            Assert.NotNull(layout);
            Assert.Equal(layoutDirectory.FullName, layout!.LayoutPath);
        }
        finally
        {
            layoutDirectory.Delete(recursive: true);
        }
    }

    private static DirectoryInfo CreateLayout(bool writeVersionMarker, bool writeExtractionInProgressMarker)
    {
        var root = Directory.CreateTempSubdirectory("aspire-layout-");
        var managedDirectory = root.CreateSubdirectory(BundleDiscovery.ManagedDirectoryName);
        root.CreateSubdirectory(BundleDiscovery.DcpDirectoryName);

        var managedPath = Path.Combine(managedDirectory.FullName, BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName));
        File.WriteAllText(managedPath, "test");

        if (writeVersionMarker)
        {
            BundleService.WriteVersionMarker(root.FullName, "test-version");
        }

        if (writeExtractionInProgressMarker)
        {
            BundleService.WriteExtractionInProgressMarker(root.FullName, "test-version");
        }

        return root;
    }

    private sealed class LayoutPathEnvironmentVariableScope : IDisposable
    {
        private readonly string? _originalValue = Environment.GetEnvironmentVariable(BundleDiscovery.LayoutPathEnvVar);

        public LayoutPathEnvironmentVariableScope(string layoutPath)
        {
            Environment.SetEnvironmentVariable(BundleDiscovery.LayoutPathEnvVar, layoutPath);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(BundleDiscovery.LayoutPathEnvVar, _originalValue);
        }
    }
}
