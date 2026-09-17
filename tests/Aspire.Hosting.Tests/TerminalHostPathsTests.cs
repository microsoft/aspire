// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared.TerminalHost;

namespace Aspire.Hosting.Tests;

public class TerminalHostPathsTests
{
    private const string ExampleHome = "/Users/example";

    [Fact]
    public void CreateReplicaIdProducesElevenCharBase64UrlOutput()
    {
        var id = TerminalHostPaths.CreateReplicaId();

        Assert.Equal(TerminalHostPaths.ReplicaIdLength, id.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", id);
    }

    [Fact]
    public void CreateReplicaIdProducesPerRunValues()
    {
        var ids = Enumerable.Range(0, 16)
            .Select(_ => TerminalHostPaths.CreateReplicaId())
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(16, ids.Count);
    }

    [Fact]
    public void GetSocketPathBuildsExpectedLayout()
    {
        var trmnlDirectory = TerminalHostPaths.GetTrmnlDirectory(ExampleHome);
        var id = TerminalHostPaths.CreateReplicaId();
        var control = TerminalHostPaths.GetSocketPath(trmnlDirectory, id, TerminalHostPaths.ControlSockPurpose);

        Assert.Equal(Path.Combine(trmnlDirectory, $"{id}.{TerminalHostPaths.ControlSockPurpose}.sock"), control);
    }

    [Fact]
    public void GetMetadataPathBuildsExpectedLayout()
    {
        var trmnlDirectory = TerminalHostPaths.GetTrmnlDirectory(ExampleHome);
        var id = TerminalHostPaths.CreateReplicaId();
        var metadata = TerminalHostPaths.GetMetadataPath(trmnlDirectory, id);

        Assert.Equal(Path.Combine(trmnlDirectory, $"{id}.{TerminalHostPaths.MetadataSuffix}"), metadata);
    }

    [Fact]
    public void GetMetadataTemporaryPathBuildsExpectedLayout()
    {
        var trmnlDirectory = TerminalHostPaths.GetTrmnlDirectory(ExampleHome);
        var id = TerminalHostPaths.CreateReplicaId();
        var metadata = TerminalHostPaths.GetMetadataPath(trmnlDirectory, id);

        Assert.Equal(
            Path.Combine(trmnlDirectory, $"{id}.{TerminalHostPaths.MetadataTemporarySuffix}"),
            TerminalHostPaths.GetMetadataTemporaryPath(metadata));
    }

    [Fact]
    public void GetSocketPathFitsInsideMacOsSunPathLimit()
    {
        var home = "/Users/abcdefghijklmnop";
        var trmnlDirectory = TerminalHostPaths.GetTrmnlDirectory(home);
        var id = TerminalHostPaths.CreateReplicaId();
        var control = TerminalHostPaths.GetSocketPath(trmnlDirectory, id, TerminalHostPaths.ControlSockPurpose);
        var bytes = System.Text.Encoding.UTF8.GetByteCount(control);

        Assert.True(bytes < 90, $"Socket path is {bytes} bytes long: {control}");
    }
}
