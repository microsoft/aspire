// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared.TerminalHost;

namespace Aspire.Hosting.Tests;

public class TerminalHostPathsTests
{
    private const string ExampleHome = "/Users/example";
    private const string ExampleAppHost = "/Users/example/code/MyApp.AppHost.csproj";

    [Fact]
    public void ComputeReplicaIdIsDeterministicForSameInputs()
    {
        var a = TerminalHostPaths.ComputeReplicaId(ExampleAppHost, "frontend", 0);
        var b = TerminalHostPaths.ComputeReplicaId(ExampleAppHost, "frontend", 0);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ComputeReplicaIdProducesElevenCharBase64UrlOutput()
    {
        var id = TerminalHostPaths.ComputeReplicaId(ExampleAppHost, "frontend", 0);

        Assert.Equal(TerminalHostPaths.ReplicaIdLength, id.Length);
        // base64url alphabet: A–Z, a–z, 0–9, '-', '_'. No '+', '/' or '=' padding.
        Assert.Matches("^[A-Za-z0-9_-]+$", id);
    }

    [Fact]
    public void ComputeReplicaIdDiffersAcrossReplicaIndex()
    {
        var r0 = TerminalHostPaths.ComputeReplicaId(ExampleAppHost, "frontend", 0);
        var r1 = TerminalHostPaths.ComputeReplicaId(ExampleAppHost, "frontend", 1);
        var r2 = TerminalHostPaths.ComputeReplicaId(ExampleAppHost, "frontend", 2);

        Assert.NotEqual(r0, r1);
        Assert.NotEqual(r0, r2);
        Assert.NotEqual(r1, r2);
    }

    [Fact]
    public void ComputeReplicaIdDiffersAcrossResourceName()
    {
        var front = TerminalHostPaths.ComputeReplicaId(ExampleAppHost, "frontend", 0);
        var back = TerminalHostPaths.ComputeReplicaId(ExampleAppHost, "backend", 0);

        Assert.NotEqual(front, back);
    }

    [Fact]
    public void ComputeReplicaIdDiffersAcrossAppHostPath()
    {
        var a = TerminalHostPaths.ComputeReplicaId("/Users/example/a/MyApp.AppHost.csproj", "frontend", 0);
        var b = TerminalHostPaths.ComputeReplicaId("/Users/example/b/MyApp.AppHost.csproj", "frontend", 0);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ComputeReplicaIdGuardsAgainstTupleBoundaryCollisions()
    {
        // Without a NUL separator, ("/foo", "bar") could collide with ("/foob", "ar").
        // The NUL between fields makes those two inputs produce different ids.
        var ab = TerminalHostPaths.ComputeReplicaId("/foo", "bar", 0);
        var cd = TerminalHostPaths.ComputeReplicaId("/foob", "ar", 0);

        Assert.NotEqual(ab, cd);
    }

    [Fact]
    public void ComputeReplicaIdIsCaseSensitiveOnUnix()
    {
        // On macOS/Linux the path is hashed as-is (APFS can be case-sensitive, Linux
        // filesystems are case-sensitive by default). On Windows NormalizePath uppercases
        // the path so casing differences collapse — we don't assert that here because the
        // test runs on multiple OSes.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var lower = TerminalHostPaths.ComputeReplicaId("/users/example/myapp.apphost.csproj", "frontend", 0);
        var upper = TerminalHostPaths.ComputeReplicaId("/Users/Example/MyApp.AppHost.csproj", "frontend", 0);

        Assert.NotEqual(lower, upper);
    }

    [Fact]
    public void GetSocketPathBuildsExpectedLayout()
    {
        var id = TerminalHostPaths.ComputeReplicaId(ExampleAppHost, "frontend", 0);
        var control = TerminalHostPaths.GetSocketPath(ExampleHome, id, TerminalHostPaths.ControlSockPurpose);

        var expected = Path.Combine(
            ExampleHome,
            TerminalHostPaths.DotAspireDirectoryName,
            TerminalHostPaths.TrmnlDirectoryName,
            $"{id}.{TerminalHostPaths.ControlSockPurpose}.sock");

        Assert.Equal(expected, control);
    }

    [Fact]
    public void GetMetadataPathBuildsExpectedLayout()
    {
        var id = TerminalHostPaths.ComputeReplicaId(ExampleAppHost, "frontend", 0);
        var meta = TerminalHostPaths.GetMetadataPath(ExampleHome, id);

        var expected = Path.Combine(
            ExampleHome,
            TerminalHostPaths.DotAspireDirectoryName,
            TerminalHostPaths.TrmnlDirectoryName,
            $"{id}.{TerminalHostPaths.MetadataSuffix}");

        Assert.Equal(expected, meta);
    }

    [Fact]
    public void GetSocketPathFitsInsideMacOsSunPathLimit()
    {
        // sockaddr_un.sun_path is 104 bytes on macOS (including the trailing NUL).
        // Even with a relatively long home directory the path must comfortably fit;
        // if a future change makes the layout more verbose this guards against a
        // silent regression that only manifests on macOS.
        var home = "/Users/abcdefghijklmnop";
        var id = TerminalHostPaths.ComputeReplicaId(ExampleAppHost, "frontend", 0);
        var control = TerminalHostPaths.GetSocketPath(home, id, TerminalHostPaths.ControlSockPurpose);

        // System.Text.Encoding.UTF8.GetByteCount is the right unit for sun_path: it is
        // a C string, not a fixed-encoding identifier.
        var bytes = System.Text.Encoding.UTF8.GetByteCount(control);

        // 104 - 1 (NUL) = 103 usable bytes. Generous slack here intentionally; we want
        // to flag long before we hit the real limit.
        Assert.True(bytes < 90, $"Socket path is {bytes} bytes long: {control}");
    }
}
