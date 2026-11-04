// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils.EnvironmentChecker;
using Aspire.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;

namespace Aspire.Cli.Tests.Utils;

public class ContainerRuntimeCheckTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WslcHostSupport_IsWindowsOnly(bool isWindows)
    {
        var probe = new WslcContainerRuntimeProbe();

        Assert.Equal(isWindows, probe.IsSupportedHost(isWindows));
    }

    [Theory]
    [InlineData(KnownContainerRuntimes.Docker)]
    [InlineData(KnownContainerRuntimes.Podman)]
    [InlineData(KnownContainerRuntimes.Wslc)]
    public void RuntimeVersionPolicy_UsesRegisteredKeyRatherThanDisplayName(string key)
    {
        var result = ContainerRuntimeCheck.CheckRuntimePolicy(new ContainerRuntimeInfo
        {
            Key = key,
            Executable = "fixture",
            Name = "Different display name",
            ClientVersion = new Version(1, 0, 0)
        });

        Assert.NotNull(result);
        Assert.Equal(key == KnownContainerRuntimes.Wslc ? EnvironmentCheckStatus.Fail : EnvironmentCheckStatus.Warning, result.Status);
        Assert.Contains("Different display name", result.Message);
    }

    [Theory]
    [InlineData("3.0.2.0", "3.0.2", false)]
    [InlineData("3.0.2", "3.0.2.0", false)]
    [InlineData("3.0.2-rc.1", "3.0.3", true)]
    [InlineData("3.0.3.0", "3.0.2-rc.1", true)]
    [InlineData("3.0.3-rc.1", "3.0.3", false)]
    [InlineData("3.0.1.9", "3.0.3", true)]
    [InlineData("3.0.3.0", "3.0.1.9", true)]
    [InlineData("3.0.3+build.1", "3.0.2+build.2", false)]
    public void WslcVersionPolicy_RecognizesNativeAndSemanticVersions(string client, string server, bool expectFailure)
    {
        var metadata = WslcContainerRuntimeProbe.ParseInfoOutput(
            $$$"""{"Client":{"Version":"{{{client}}}"},"Server":{"SessionManagerVersion":"{{{server}}}"}}""");
        var result = ContainerRuntimeCheck.CheckRuntimePolicy(CreateWslcInfo(metadata));

        if (expectFailure)
        {
            Assert.NotNull(result);
            Assert.Equal(EnvironmentCheckStatus.Fail, result.Status);
            var rejectedVersion = client.StartsWith("3.0.3", StringComparison.Ordinal) ? server : client;
            Assert.Contains(rejectedVersion, result.Message);
        }
        else
        {
            Assert.Null(result);
        }
        Assert.Null(metadata.Error);
        Assert.Equal(client, metadata.ClientVersionText);
        Assert.Equal(server, metadata.ServerVersionText);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("""{"Client":{"Version":"3.0.3.0"}}""")]
    [InlineData("""{"Client":{"Version":"invalid"},"Server":{"SessionManagerVersion":"3.0.3"}}""")]
    [InlineData("""{"Client":{"Version":"3.0.3.0"},"Server":{"SessionManagerVersion":"invalid"}}""")]
    public void WslcVersionPolicy_UnusableRequiredMetadata_ReportsFailure(string? json)
    {
        var metadata = WslcContainerRuntimeProbe.ParseInfoOutput(json);
        var result = ContainerRuntimeCheck.CheckRuntimePolicy(CreateWslcInfo(metadata));

        Assert.NotNull(metadata.Error);
        Assert.NotNull(result);
        Assert.Equal(EnvironmentCheckStatus.Fail, result.Status);
        Assert.Equal("wslc", result.Name);
        Assert.Equal(metadata.Error, result.Details);
    }

    [Theory]
    [InlineData("linux")]
    [InlineData("windows")]
    public void DockerDistributionVersion_PreservesVersionAndPrioritizesWindowsModeFailure(string serverOs)
    {
        var metadata = ContainerRuntimeProbe.ParseVersionOutput(
            $$$"""{"Client":{"Version":"26.1.5+dfsg1"},"Server":{"Version":"26.1.5+dfsg1","Os":"{{{serverOs}}}"}}""");
        Assert.Equal(new Version(26, 1, 5), metadata.ClientVersion);
        Assert.Equal(new Version(26, 1, 5), metadata.ServerVersion);
        var result = ContainerRuntimeCheck.CheckRuntimePolicy(new ContainerRuntimeInfo
        {
            Key = "docker",
            Executable = "docker",
            Name = "Docker",
            ClientVersion = metadata.ClientVersion,
            ServerVersion = metadata.ServerVersion,
            ClientVersionText = metadata.ClientVersionText,
            ServerVersionText = metadata.ServerVersionText,
            ServerOs = metadata.ServerOs
        });

        Assert.NotNull(result);
        Assert.Equal(serverOs == "windows" ? EnvironmentCheckStatus.Fail : EnvironmentCheckStatus.Warning, result.Status);
        if (serverOs == "linux")
        {
            Assert.Contains("26.1.5+dfsg1", result.Message);
        }
    }

    private static ContainerRuntimeInfo CreateWslcInfo(ContainerRuntimeProbe.RuntimeVersionInfo metadata) => new()
    {
        Key = "wslc",
        Executable = "wslc",
        Name = "WSLc",
        ClientVersion = metadata.ClientVersion,
        ServerVersion = metadata.ServerVersion,
        IsClientPrerelease = metadata.IsClientPrerelease,
        IsServerPrerelease = metadata.IsServerPrerelease,
        VersionError = metadata.Error,
        ClientVersionText = metadata.ClientVersionText,
        ServerVersionText = metadata.ServerVersionText
    };

    [Theory]
    [InlineData("DOCKER", "docker")]
    [InlineData("Podman", "podman")]
    [InlineData("WSLC", "wslc")]
    public void GetProbe_ResolvesConfiguredKeyToRegisteredRuntime(string configuredKey, string expectedKey)
    {
        var probe = ContainerRuntimeDetector.GetProbe(configuredKey);

        Assert.Equal(expectedKey, probe.Key);
        Assert.Same(probe, ContainerRuntimeDetector.GetProbe(expectedKey));
    }

    [Fact]
    public void RuntimeProbe_KeyIsIndependentOfExecutable()
    {
        var probe = new ContainerRuntimeProbe("runtime-key", "runtime-cli", "Runtime", 0);

        Assert.Equal("runtime-key", probe.Key);
        Assert.Equal("runtime-cli", probe.Executable);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData(null, "")]
    [InlineData("", "unsupported-legacy-runtime")]
    public async Task CheckAsync_EmptyEffectiveRuntime_AutoDetectsAfterApplyingPrecedence(string? modern, string? legacy)
    {
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["ASPIRE_CONTAINER_RUNTIME"] = modern,
            ["DOTNET_ASPIRE_CONTAINER_RUNTIME"] = legacy
        });
        var logger = new FakeLogger<ContainerRuntimeCheck>();
        var check = new ContainerRuntimeCheck(logger, environment);

        var results = await check.CheckAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(results);
        var probedKeys = logger.Collector.GetSnapshot()
            .Where(log => log.Message.StartsWith("Probing container runtime '", StringComparison.Ordinal))
            .Select(log => log.Message)
            .Order()
            .ToArray();
        var expectedProbes = ContainerRuntimeDetector.GetKnownRuntimes(environment.IsWindows())
            .Select(probe => $"Probing container runtime '{probe.Name}' ({probe.Executable})...")
            .Order()
            .ToArray();
        Assert.Equal(expectedProbes, probedKeys);
    }

    [Fact]
    public async Task CheckAsync_AutoDetection_ReportsContainerChecks()
    {
        var check = new ContainerRuntimeCheck(NullLogger<ContainerRuntimeCheck>.Instance, new TestEnvironment());

        var results = await check.CheckAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(results);
        Assert.All(results, result => Assert.Equal(EnvironmentCheckCategories.Container, result.Category));
    }

    [Theory]
    [InlineData("ASPIRE_CONTAINER_RUNTIME")]
    [InlineData("DOTNET_ASPIRE_CONTAINER_RUNTIME")]
    public async Task CheckAsync_UnknownConfiguredRuntime_ReportsUnsupportedWithoutProbing(string variableName)
    {
        var configuredRuntime = $"aspire-missing-runtime-{Guid.NewGuid():N}";
        var environment = new TestEnvironment(new Dictionary<string, string?> { [variableName] = configuredRuntime });
        var logger = new FakeLogger<ContainerRuntimeCheck>();
        var check = new ContainerRuntimeCheck(logger, environment);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(ContainerRuntimeCheck.CheckName, result.Name);
        Assert.Equal(EnvironmentCheckStatus.Fail, result.Status);
        Assert.Contains($"Unsupported container runtime '{configuredRuntime}'", result.Details);
        Assert.Equal(0, logger.Collector.GetSnapshot().Count(log => log.Message.StartsWith("Probing container runtime '", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("linux", "ASPIRE_CONTAINER_RUNTIME")]
    [InlineData("linux", "DOTNET_ASPIRE_CONTAINER_RUNTIME")]
    [InlineData("macos", "ASPIRE_CONTAINER_RUNTIME")]
    [InlineData("macos", "DOTNET_ASPIRE_CONTAINER_RUNTIME")]
    public async Task CheckAsync_ConfiguredWslcOnNonWindows_ReportsFailure(string os, string variableName)
    {
        var variables = new Dictionary<string, string?> { [variableName] = "wslc" };
        var environment = os == "linux" ? TestEnvironment.CreateLinux(variables) : TestEnvironment.CreateMacOS(variables);
        var check = new ContainerRuntimeCheck(NullLogger<ContainerRuntimeCheck>.Instance, environment);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckCategories.Container, result.Category);
        Assert.Equal(KnownContainerRuntimes.Wslc, result.Name);
        Assert.Equal(EnvironmentCheckStatus.Fail, result.Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GetKnownRuntimes_OnlyIncludesWslcOnWindows(bool isWindows)
    {
        var includesWslc = ContainerRuntimeDetector.GetKnownRuntimes(isWindows)
            .Any(runtime => runtime.Key == KnownContainerRuntimes.Wslc);

        Assert.Equal(isWindows, includesWslc);
    }

    [Fact]
    public async Task FindAvailableRuntimeAsync_RestrictsDiscoveryToSupportedRuntimes()
    {
        var runtime = await ContainerRuntimeDetector.FindAvailableRuntimeAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            supportedRuntimes: []);

        Assert.Null(runtime);
    }

    [Fact]
    public void ParseVersionFromJsonOutput_WithWslcInfo_ReturnsClientAndSessionManagerVersions()
    {
        var input = """{"Client":{"Version":"3.0.3.0"},"Server":{"SessionManagerVersion":"3.0.3","Sessions":[{"ID":1,"Name":"default"}]}}""";

        var info = WslcContainerRuntimeProbe.ParseInfoOutput(input);

        Assert.Equal(new Version(3, 0, 3, 0), info.ClientVersion);
        Assert.Equal(new Version(3, 0, 3), info.ServerVersion);
        Assert.False(info.IsDockerDesktop);
        Assert.Null(info.ServerOs);
    }

    [Fact]
    public void ParseVersionFromJsonOutput_WithDockerJsonOutput_ReturnsBothVersions()
    {
        // Real Docker version -f json output with both client and server
        var input = """{"Client":{"Platform":{"Name":"Docker Engine - Community"},"Version":"28.0.4","ApiVersion":"1.48","DefaultAPIVersion":"1.48","GitCommit":"b8034c0","GoVersion":"go1.23.7","Os":"linux","Arch":"amd64","BuildTime":"Tue Mar 25 15:07:16 2025","Context":"default"},"Server":{"Platform":{"Name":"Docker Engine - Community"},"Components":[{"Name":"Engine","Version":"28.0.4"}],"Version":"28.0.4","ApiVersion":"1.48"}}""";

        var info = ContainerRuntimeProbe.ParseVersionOutput(input);
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

        var info = ContainerRuntimeProbe.ParseVersionOutput(input);
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

        var info = ContainerRuntimeProbe.ParseVersionOutput(input);
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

        var info = ContainerRuntimeProbe.ParseVersionOutput(input);
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

        var info = ContainerRuntimeProbe.ParseVersionOutput(input);
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

        var info = ContainerRuntimeProbe.ParseVersionOutput(input);
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

        var info = ContainerRuntimeProbe.ParseVersionOutput(input);
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
        var info = ContainerRuntimeProbe.ParseVersionOutput(input!);
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

        var info = ContainerRuntimeProbe.ParseVersionOutput(input);
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

        var info = ContainerRuntimeProbe.ParseVersionOutput(input);
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

        var info = ContainerRuntimeProbe.ParseVersionOutput(input);
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

        var info = ContainerRuntimeProbe.ParseVersionOutput(input);
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

        var info = ContainerRuntimeProbe.ParseVersionOutput(input);
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
        var result = ContainerRuntimeProbe.ParseVersionOutput(input).ClientVersion;

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
        var result = ContainerRuntimeProbe.ParseVersionOutput(input).ClientVersion;

        Assert.Null(result);
    }

    [Fact]
    public void ParseVersionFromOutput_WithNullInput_ReturnsNull()
    {
        var result = ContainerRuntimeProbe.ParseVersionOutput(null!);

        Assert.Null(result.ClientVersion);
    }

    [Fact]
    public void ParseVersionFromOutput_WithDockerDesktopOutput_ReturnsVersion()
    {
        // Docker Desktop on Windows/Mac may have additional output
        var input = """
            Docker version 27.5.1, build 9f9e405
            
            """;

        var result = ContainerRuntimeProbe.ParseVersionOutput(input).ClientVersion;

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

        var result = ContainerRuntimeProbe.ParseVersionOutput(input).ClientVersion;

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
            var result = ContainerRuntimeProbe.ParseVersionOutput(input).ClientVersion;
            Assert.NotNull(result);
        }
    }
}
