// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared;

namespace Aspire.Hosting.Tests.Publishing;

public class ContainerRuntimeDetectorTests
{
    [Fact]
    public void FindBestRuntime_PrefersRunningOverInstalled()
    {
        var runtimes = new[]
        {
            new ContainerRuntimeInfo { Executable = "docker", Name = "Docker", IsInstalled = true, IsRunning = false, IsDefault = true },
            new ContainerRuntimeInfo { Executable = "podman", Name = "Podman", IsInstalled = true, IsRunning = true, IsDefault = false }
        };

        var best = ContainerRuntimeDetector.FindBestRuntime(runtimes);

        Assert.Equal("podman", best?.Executable);
    }

    [Fact]
    public void FindBestRuntime_PrefersInstalledOverNotInstalled()
    {
        var runtimes = new[]
        {
            new ContainerRuntimeInfo { Executable = "docker", Name = "Docker", IsInstalled = false, IsRunning = false, IsDefault = true },
            new ContainerRuntimeInfo { Executable = "podman", Name = "Podman", IsInstalled = true, IsRunning = false, IsDefault = false }
        };

        var best = ContainerRuntimeDetector.FindBestRuntime(runtimes);

        Assert.Equal("podman", best?.Executable);
    }

    [Fact]
    public void FindBestRuntime_PrefersDefaultWhenEqual()
    {
        var runtimes = new[]
        {
            new ContainerRuntimeInfo { Executable = "docker", Name = "Docker", IsInstalled = true, IsRunning = true, IsDefault = true },
            new ContainerRuntimeInfo { Executable = "podman", Name = "Podman", IsInstalled = true, IsRunning = true, IsDefault = false }
        };

        var best = ContainerRuntimeDetector.FindBestRuntime(runtimes);

        Assert.Equal("docker", best?.Executable);
    }

    [Fact]
    public void FindBestRuntime_ReturnsNullForEmpty()
    {
        var best = ContainerRuntimeDetector.FindBestRuntime([]);

        Assert.Null(best);
    }

    [Fact]
    public void FindBestRuntime_ReturnsSingleRuntime()
    {
        var runtimes = new[]
        {
            new ContainerRuntimeInfo { Executable = "podman", Name = "Podman", IsInstalled = true, IsRunning = true, IsDefault = false }
        };

        var best = ContainerRuntimeDetector.FindBestRuntime(runtimes);

        Assert.Equal("podman", best?.Executable);
    }

    [Fact]
    public void FindBestRuntime_NeitherInstalled_ReturnsDefault()
    {
        var runtimes = new[]
        {
            new ContainerRuntimeInfo { Executable = "docker", Name = "Docker", IsInstalled = false, IsRunning = false, IsDefault = true },
            new ContainerRuntimeInfo { Executable = "podman", Name = "Podman", IsInstalled = false, IsRunning = false, IsDefault = false }
        };

        var best = ContainerRuntimeDetector.FindBestRuntime(runtimes);

        Assert.Equal("docker", best?.Executable);
    }

    [Fact]
    public void ParseVersionOutput_ValidDockerJson_ParsesVersions()
    {
        var json = """
        {
            "Client": { "Version": "28.0.1", "Context": "desktop-linux" },
            "Server": { "Version": "27.5.0", "Os": "linux" }
        }
        """;

        var info = ContainerRuntimeDetector.ParseVersionOutput(json);

        Assert.Equal(new Version(28, 0, 1), info.ClientVersion);
        Assert.Equal(new Version(27, 5, 0), info.ServerVersion);
        Assert.True(info.IsDockerDesktop);
        Assert.Equal("linux", info.ServerOs);
    }

    [Fact]
    public void ParseVersionOutput_DockerEngine_NotDesktop()
    {
        var json = """
        {
            "Client": { "Version": "29.1.3" },
            "Server": { "Version": "29.1.3", "Os": "linux" }
        }
        """;

        var info = ContainerRuntimeDetector.ParseVersionOutput(json);

        Assert.Equal(new Version(29, 1, 3), info.ClientVersion);
        Assert.False(info.IsDockerDesktop);
    }

    [Fact]
    public void ParseVersionOutput_PodmanJson_ParsesClient()
    {
        var json = """
        {
            "Client": { "Version": "4.9.3" }
        }
        """;

        var info = ContainerRuntimeDetector.ParseVersionOutput(json);

        Assert.Equal(new Version(4, 9, 3), info.ClientVersion);
        Assert.Null(info.ServerVersion);
        Assert.False(info.IsDockerDesktop);
    }

    [Fact]
    public void ParseVersionOutput_NonJsonFallback_UsesRegex()
    {
        var text = "podman version 5.2.1";

        var info = ContainerRuntimeDetector.ParseVersionOutput(text);

        Assert.Equal(new Version(5, 2, 1), info.ClientVersion);
    }

    [Fact]
    public void ParseVersionOutput_NullInput_ReturnsDefault()
    {
        var info = ContainerRuntimeDetector.ParseVersionOutput(null);

        Assert.Null(info.ClientVersion);
        Assert.Null(info.ServerVersion);
        Assert.False(info.IsDockerDesktop);
    }

    [Fact]
    public void ParseVersionOutput_EmptyInput_ReturnsDefault()
    {
        var info = ContainerRuntimeDetector.ParseVersionOutput("");

        Assert.Null(info.ClientVersion);
    }

    [Fact]
    public void ParseVersionOutput_WindowsContainers_DetectsOs()
    {
        var json = """
        {
            "Client": { "Version": "28.0.1", "Context": "desktop-linux" },
            "Server": { "Version": "28.0.1", "Os": "windows" }
        }
        """;

        var info = ContainerRuntimeDetector.ParseVersionOutput(json);

        Assert.Equal("windows", info.ServerOs);
    }
}
