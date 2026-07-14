// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared;

namespace Aspire.Cli.Tests.Utils;

public class ContainerRuntimeCheckTests
{
    [Fact]
    public void ParseVersionFromJsonOutput_WithDockerJsonOutput_ReturnsBothVersions()
    {
        // Real Docker version -f json output with both client and server
        var input = """{"Client":{"Platform":{"Name":"Docker Engine - Community"},"Version":"28.0.4","ApiVersion":"1.48","DefaultAPIVersion":"1.48","GitCommit":"b8034c0","GoVersion":"go1.23.7","Os":"linux","Arch":"amd64","BuildTime":"Tue Mar 25 15:07:16 2025","Context":"default"},"Server":{"Platform":{"Name":"Docker Engine - Community"},"Components":[{"Name":"Engine","Version":"28.0.4"}],"Version":"28.0.4","ApiVersion":"1.48"}}""";

        var info = ContainerRuntimeDetector.ParseVersionOutput(input);
        var (clientVersion, serverVersion, serverOs) = (info.ClientVersion, info.ServerVersion, info.ServerOs);

        Assert.NotNull(clientVersion);
        Assert.Equal(new Version(28, 0, 4), clientVersion);
        Assert.NotNull(serverVersion);
        Assert.Equal(new Version(28, 0, 4), serverVersion);
        Assert.False(info.IsDockerDesktop);
        Assert.Null(serverOs);
    }

    [Fact]
    public void ParseVersionFromJsonOutput_WithDockerDesktopMacJsonOutput_ReturnsBothVersionsAndContext()
    {
        // Docker Desktop on macOS JSON output
        var input = """{"Client":{"Version":"28.5.1","ApiVersion":"1.51","DefaultAPIVersion":"1.51","GitCommit":"e180ab8","GoVersion":"go1.24.8","Os":"darwin","Arch":"arm64","BuildTime":"Wed Oct  8 12:16:17 2025","Context":"desktop-linux"},"Server":{"Platform":{"Name":"Docker Desktop 4.49.0 (208700)"},"Components":[{"Name":"Engine","Version":"28.5.1"}],"Version":"28.5.1","ApiVersion":"1.51","MinAPIVersion":"1.24","GitCommit":"f8215cc","GoVersion":"go1.24.8","Os":"linux","Arch":"arm64","KernelVersion":"6.10.14-linuxkit","BuildTime":"2025-10-08T12:18:25.000000000+00:00"}}""";

        var info = ContainerRuntimeDetector.ParseVersionOutput(input);
        var (clientVersion, serverVersion, serverOs) = (info.ClientVersion, info.ServerVersion, info.ServerOs);

        Assert.NotNull(clientVersion);
        Assert.Equal(new Version(28, 5, 1), clientVersion);
        Assert.NotNull(serverVersion);
        Assert.Equal(new Version(28, 5, 1), serverVersion);
        Assert.True(info.IsDockerDesktop);
        Assert.Equal("linux", serverOs);
    }

    [Fact]
    public void ParseVersionFromJsonOutput_WithDockerEngineJsonOutput_ReturnsBothVersionsAndContext()
    {
        // Docker Engine (Linux) JSON output
        var input = """{"Client":{"Platform":{"Name":"Docker Engine - Community"},"Version":"29.1.3","ApiVersion":"1.52","DefaultAPIVersion":"1.52","GitCommit":"f52814d","GoVersion":"go1.25.5","Os":"linux","Arch":"amd64","BuildTime":"Fri Dec 12 14:49:37 2025","Context":"default"},"Server":{"Platform":{"Name":"Docker Engine - Community"},"Version":"29.1.3","ApiVersion":"1.52","MinAPIVersion":"1.44","Os":"linux","Arch":"amd64","Components":[{"Name":"Engine","Version":"29.1.3"}],"GitCommit":"fbf3ed2","GoVersion":"go1.25.5","KernelVersion":"5.15.0-113-generic","BuildTime":"2025-12-12T14:49:37.000000000+00:00"}}""";

        var info = ContainerRuntimeDetector.ParseVersionOutput(input);
        var (clientVersion, serverVersion, serverOs) = (info.ClientVersion, info.ServerVersion, info.ServerOs);

        Assert.NotNull(clientVersion);
        Assert.Equal(new Version(29, 1, 3), clientVersion);
        Assert.NotNull(serverVersion);
        Assert.Equal(new Version(29, 1, 3), serverVersion);
        Assert.False(info.IsDockerDesktop);
        Assert.Equal("linux", serverOs);
    }

    [Fact]
    public void ParseVersionFromJsonOutput_WithPodmanJsonOutput_ReturnsClientVersionOnly()
    {
        // Real Podman version -f json output (no Server section)
        var input = """{"Client":{"APIVersion":"4.9.3","Version":"4.9.3","GoVersion":"go1.22.2","GitCommit":"","BuiltTime":"Thu Jan  1 00:00:00 1970","Built":0,"OsArch":"linux/amd64","Os":"linux"}}""";

        var info = ContainerRuntimeDetector.ParseVersionOutput(input);
        var (clientVersion, serverVersion, serverOs) = (info.ClientVersion, info.ServerVersion, info.ServerOs);

        Assert.NotNull(clientVersion);
        Assert.Equal(new Version(4, 9, 3), clientVersion);
        Assert.Null(serverVersion);
        // Context is not exposed directly; tested via IsDockerDesktop
        Assert.Null(serverOs);
    }

    [Fact]
    public void ParseVersionFromJsonOutput_WithDockerDesktopWindowsJsonOutput_ServerNull_ReturnsClientAndContext()
    {
        // Docker Desktop on Windows may have Server:null if daemon is not running
        var input = """{"Client":{"Version":"29.1.3","ApiVersion":"1.52","DefaultAPIVersion":"1.52","GitCommit":"f52814d","GoVersion":"go1.25.5","Os":"windows","Arch":"amd64","BuildTime":"Fri Dec 12 14:51:52 2025","Context":"desktop-linux"},"Server":null}""";

        var info = ContainerRuntimeDetector.ParseVersionOutput(input);
        var (clientVersion, serverVersion, serverOs) = (info.ClientVersion, info.ServerVersion, info.ServerOs);

        Assert.NotNull(clientVersion);
        Assert.Equal(new Version(29, 1, 3), clientVersion);
        Assert.Null(serverVersion);
        Assert.True(info.IsDockerDesktop);
        Assert.Null(serverOs);
    }

    [Fact]
    public void ParseVersionFromJsonOutput_WithOldDockerVersion_ReturnsClientVersion()
    {
        var input = """{"Client":{"Version":"19.03.15","ApiVersion":"1.40"},"Server":null}""";

        var info = ContainerRuntimeDetector.ParseVersionOutput(input);
        var (clientVersion, serverVersion, serverOs) = (info.ClientVersion, info.ServerVersion, info.ServerOs);

        Assert.NotNull(clientVersion);
        Assert.Equal(new Version(19, 3, 15), clientVersion);
        Assert.Null(serverVersion);
        // Context is not exposed directly; tested via IsDockerDesktop
        Assert.Null(serverOs);
    }

    [Fact]
    public void ParseVersionFromJsonOutput_WithTwoPartVersion_ReturnsVersion()
    {
        var input = """{"Client":{"Version":"20.10","ApiVersion":"1.41"}}""";

        var info = ContainerRuntimeDetector.ParseVersionOutput(input);
        var (clientVersion, serverVersion, serverOs) = (info.ClientVersion, info.ServerVersion, info.ServerOs);

        Assert.NotNull(clientVersion);
        Assert.Equal(new Version(20, 10), clientVersion);
        Assert.Null(serverVersion);
        // Context is not exposed directly; tested via IsDockerDesktop
        Assert.Null(serverOs);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"Client":{}}""")]
    public void ParseVersionFromJsonOutput_WithInvalidInput_ReturnsNullVersions(string? input)
    {
        var info = ContainerRuntimeDetector.ParseVersionOutput(input!);
        var (clientVersion, serverVersion, serverOs) = (info.ClientVersion, info.ServerVersion, info.ServerOs);

        Assert.Null(clientVersion);
        Assert.Null(serverVersion);
        // Context is not exposed directly; tested via IsDockerDesktop
        Assert.Null(serverOs);
    }

    [Fact]
    public void ParseVersionFromJsonOutput_WithOnlyServerVersion_ReturnsServerVersionOnly()
    {
        // Edge case: only server version present (unusual but possible)
        var input = """{"Server":{"Version":"1.0.0"}}""";

        var info = ContainerRuntimeDetector.ParseVersionOutput(input);
        var (clientVersion, serverVersion, serverOs) = (info.ClientVersion, info.ServerVersion, info.ServerOs);

        Assert.Null(clientVersion);
        Assert.NotNull(serverVersion);
        Assert.Equal(new Version(1, 0, 0), serverVersion);
        // Context is not exposed directly; tested via IsDockerDesktop
        Assert.Null(serverOs);
    }

    [Fact]
    public void ParseVersionFromJsonOutput_WithInvalidVersionString_ReturnsNull()
    {
        var input = """{"Client":{"Version":"not-a-version"}}""";

        var info = ContainerRuntimeDetector.ParseVersionOutput(input);
        var (clientVersion, serverVersion, serverOs) = (info.ClientVersion, info.ServerVersion, info.ServerOs);

        Assert.Null(clientVersion);
        Assert.Null(serverVersion);
        // Context is not exposed directly; tested via IsDockerDesktop
        Assert.Null(serverOs);
    }

    [Fact]
    public void ParseVersionFromJsonOutput_WithMalformedJson_ReturnsNull()
    {
        var input = "{\"Client\":{\"Version\":\"28.0.4\""; // Missing closing braces

        var info = ContainerRuntimeDetector.ParseVersionOutput(input);
        var (clientVersion, serverVersion, serverOs) = (info.ClientVersion, info.ServerVersion, info.ServerOs);

        Assert.Null(clientVersion);
        Assert.Null(serverVersion);
        // Context is not exposed directly; tested via IsDockerDesktop
        Assert.Null(serverOs);
    }

    [Fact]
    public void ParseVersionFromJsonOutput_WithMismatchedClientServerVersions_ReturnsBothVersions()
    {
        // Hypothetical case where client and server versions differ
        var input = """{"Client":{"Version":"28.0.4"},"Server":{"Version":"27.5.1"}}""";

        var info = ContainerRuntimeDetector.ParseVersionOutput(input);
        var (clientVersion, serverVersion, serverOs) = (info.ClientVersion, info.ServerVersion, info.ServerOs);

        Assert.NotNull(clientVersion);
        Assert.Equal(new Version(28, 0, 4), clientVersion);
        Assert.NotNull(serverVersion);
        Assert.Equal(new Version(27, 5, 1), serverVersion);
        // Context is not exposed directly; tested via IsDockerDesktop
        Assert.Null(serverOs);
    }

    [Fact]
    public void ParseVersionFromJsonOutput_WithWindowsContainerMode_ReturnsWindowsServerOs()
    {
        // Docker running in Windows container mode
        var input = """{"Client":{"Version":"28.0.4","Context":"default"},"Server":{"Version":"28.0.4","Os":"windows"}}""";

        var info = ContainerRuntimeDetector.ParseVersionOutput(input);
        var (clientVersion, serverVersion, serverOs) = (info.ClientVersion, info.ServerVersion, info.ServerOs);

        Assert.NotNull(clientVersion);
        Assert.Equal(new Version(28, 0, 4), clientVersion);
        Assert.NotNull(serverVersion);
        Assert.Equal(new Version(28, 0, 4), serverVersion);
        Assert.False(info.IsDockerDesktop);
        Assert.Equal("windows", serverOs);
    }

    [Theory]
    [InlineData("Docker version 20.10.17, build 100c701", "20.10.17")]
    [InlineData("Docker version 27.5.1, build 9f9e405", "27.5.1")]
    [InlineData("Docker version 24.0.5, build ced0996", "24.0.5")]
    [InlineData("podman version 4.3.1", "4.3.1")]
    [InlineData("podman version 5.0.0", "5.0.0")]
    [InlineData("podman version 3.4.4", "3.4.4")]
    [InlineData("Docker version 19.03.15, build 99e3ed8919", "19.03.15")]
    [InlineData("Docker version 20.10, build abc123", "20.10")]
    public void ParseVersionFromOutput_WithValidVersionString_ReturnsCorrectVersion(string input, string expectedVersion)
    {
        var result = ContainerRuntimeDetector.ParseVersionOutput(input).ClientVersion;

        Assert.NotNull(result);
        Assert.Equal(Version.Parse(expectedVersion), result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Docker is not installed")]
    [InlineData("Command not found")]
    [InlineData("random text without version info")]
    public void ParseVersionFromOutput_WithInvalidInput_ReturnsNull(string input)
    {
        var result = ContainerRuntimeDetector.ParseVersionOutput(input).ClientVersion;

        Assert.Null(result);
    }

    [Fact]
    public void ParseVersionFromOutput_WithNullInput_ReturnsNull()
    {
        var result = ContainerRuntimeDetector.ParseVersionOutput(null!);

        Assert.Null(result.ClientVersion);
    }

    [Fact]
    public void ParseVersionFromOutput_WithDockerDesktopOutput_ReturnsVersion()
    {
        // Docker Desktop on Windows/Mac may have additional output
        var input = """
            Docker version 27.5.1, build 9f9e405
            
            """;

        var result = ContainerRuntimeDetector.ParseVersionOutput(input).ClientVersion;

        Assert.NotNull(result);
        Assert.Equal(new Version(27, 5, 1), result);
    }

    [Fact]
    public void ParseVersionFromOutput_WithPodmanVerboseOutput_ReturnsVersion()
    {
        // Podman may have additional output on some systems
        var input = """
            podman version 4.3.1
            API Version: 4.3.1
            """;

        var result = ContainerRuntimeDetector.ParseVersionOutput(input).ClientVersion;

        Assert.NotNull(result);
        Assert.Equal(new Version(4, 3, 1), result);
    }

    [Fact]
    public void ParseVersionFromOutput_WithCaseInsensitiveVersion_ReturnsVersion()
    {
        // Test case insensitivity
        var inputs = new[]
        {
            "Docker VERSION 27.5.1, build abc",
            "docker Version 27.5.1",
            "PODMAN VERSION 4.3.1",
        };

        foreach (var input in inputs)
        {
            var result = ContainerRuntimeDetector.ParseVersionOutput(input).ClientVersion;
            Assert.NotNull(result);
        }
    }
}
