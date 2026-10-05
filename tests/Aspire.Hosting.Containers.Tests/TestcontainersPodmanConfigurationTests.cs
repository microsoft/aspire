// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Microsoft.DotNet.RemoteExecutor;

namespace Aspire.Hosting.Containers.Tests;

/// <summary>
/// Covers <see cref="TestcontainersPodmanConfiguration.Decide"/>, which decides how to point
/// Testcontainers at a container runtime. CI always has a Docker socket, so it only ever exercises the
/// no-op branch; without these tests every Podman branch would ship unverified.
/// </summary>
/// <remarks>
/// Socket paths that the production code assembles with <see cref="Path.Combine(string[])"/> are built the
/// same way here, because the separator it inserts is platform dependent and these tests run everywhere.
/// Paths the production code hard-codes are hard-coded here too.
/// </remarks>
public class TestcontainersPodmanConfigurationTests
{
    private const string DockerHostVariable = "DOCKER_HOST";
    private const string RyukDisabledVariable = "TESTCONTAINERS_RYUK_DISABLED";

    private const string HomeDirectory = "/home/tester";
    private const string XdgRuntimeDirectory = "/run/user/1000";

    // The macOS `podman machine` layout, which is what the CLI fallback reports.
    private const string PodmanMachineSocket = "/var/folders/_h/xxxx/T/podman/podman-machine-default-api.sock";

    [Fact]
    public void IsFeatureSupported_ContainerRuntime_DoesNotInitializeTestcontainersConfiguration()
    {
        RemoteExecutor.Invoke(static () =>
        {
            Assert.False(TestcontainersPodmanConfiguration.IsConfigurationInitialized);

            _ = RequiresFeatureAttribute.IsFeatureSupported(TestFeature.ContainerRuntime);

            Assert.False(TestcontainersPodmanConfiguration.IsConfigurationInitialized);
        }).Dispose();
    }

    [Fact]
    public void Decide_OnWindowsWithAPodmanSocket_ReportsNoEndpoint()
    {
        // The socket is one the non-Windows path would happily adopt, so this fails if the Windows guard
        // is ever dropped. Testcontainers 4.8.1 cannot drive Podman over a named pipe, and there is no
        // Unix socket to point it at, so the fixtures have to be skipped.
        var decision = Decide(isWindows: true, environment: [], existingSockets: ["/run/podman/podman.sock"]);

        Assert.Equal(new PodmanConfigurationDecision(HasUsableEndpoint: false), decision);
    }

    [Fact]
    public void Decide_OnWindowsWithDockerHostPointingAtDocker_ReportsEndpointWithoutChangingEnvironment()
    {
        var decision = Decide(
            isWindows: true,
            environment: new() { [DockerHostVariable] = "npipe://./pipe/docker_engine" },
            existingSockets: []);

        Assert.Equal(new PodmanConfigurationDecision(HasUsableEndpoint: true), decision);
    }

    [Fact]
    public void Decide_OnWindowsWithDockerHostPointingAtPodman_ReportsNoEndpoint()
    {
        // Off Windows this same value would be adopted and Ryuk turned off; on Windows Testcontainers
        // cannot use it at all, so claiming an endpoint would just trade a skip for a hard failure.
        var decision = Decide(
            isWindows: true,
            environment: new() { [DockerHostVariable] = "npipe:////./pipe/podman-machine-default" },
            existingSockets: []);

        Assert.Equal(new PodmanConfigurationDecision(HasUsableEndpoint: false), decision);
    }

    [Fact]
    public void Decide_WithDockerHostPointingAtDocker_LeavesRyukEnabled()
    {
        var decision = Decide(
            isWindows: false,
            environment: new() { [DockerHostVariable] = "unix:///var/run/docker.sock" },
            existingSockets: []);

        Assert.Equal(new PodmanConfigurationDecision(HasUsableEndpoint: true), decision);
    }

    [Theory]
    [InlineData("unix:///run/user/1000/podman/podman.sock")]
    [InlineData("unix:///run/podman/podman.sock")]
    [InlineData("unix:///var/folders/_h/xxxx/T/podman/podman-machine-default-api.sock")]
    [InlineData("unix:///home/tester/.local/share/containers/Podman/machine/podman.sock")]
    public void Decide_WithDockerHostPointingAtPodman_DisablesRyuk(string dockerHost)
    {
        // `podman machine start` prints an `export DOCKER_HOST=...` hint, so this is the normal way a
        // developer ends up on Podman - and Ryuk cannot bind-mount a rootless Podman socket.
        var decision = Decide(
            isWindows: false,
            environment: new() { [DockerHostVariable] = dockerHost },
            existingSockets: []);

        Assert.Equal(new PodmanConfigurationDecision(HasUsableEndpoint: true, DockerHost: null, DisableRyuk: true), decision);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public void Decide_WithDockerHostPointingAtPodman_DoesNotOverrideAnExplicitRyukSetting(string ryukDisabled)
    {
        var decision = Decide(
            isWindows: false,
            environment: new()
            {
                [DockerHostVariable] = "unix:///run/podman/podman.sock",
                [RyukDisabledVariable] = ryukDisabled,
            },
            existingSockets: []);

        Assert.Equal(new PodmanConfigurationDecision(HasUsableEndpoint: true), decision);
    }

    // Attribute arguments cannot contain a nested array, so the multi-segment candidates come through
    // MemberData rather than InlineData.
    public static TheoryData<string[]> DockerSocketCandidates => new()
    {
        new[] { "/var/run/docker.sock" },
        new[] { XdgRuntimeDirectory, "docker.sock" },
        new[] { HomeDirectory, ".docker", "run", "docker.sock" },
        new[] { HomeDirectory, ".docker", "desktop", "docker.sock" },
    };

    [Theory]
    [MemberData(nameof(DockerSocketCandidates))]
    public void Decide_WithADockerSocket_LeavesTestcontainersAlone(string[] socketPathParts)
    {
        var decision = Decide(isWindows: false, environment: [], existingSockets: [Path.Combine(socketPathParts)]);

        Assert.Equal(new PodmanConfigurationDecision(HasUsableEndpoint: true), decision);
    }

    public static TheoryData<string[]> PodmanSocketCandidates => new()
    {
        new[] { XdgRuntimeDirectory, "podman", "podman.sock" },
        new[] { "/run/podman/podman.sock" },
        new[] { HomeDirectory, ".local", "share", "containers", "podman", "machine", "podman.sock" },
    };

    [Theory]
    [MemberData(nameof(PodmanSocketCandidates))]
    public void Decide_WithAPodmanSocket_PointsTestcontainersAtIt(string[] socketPathParts)
    {
        var podmanSocket = Path.Combine(socketPathParts);

        var decision = Decide(isWindows: false, environment: [], existingSockets: [podmanSocket]);

        Assert.Equal(
            new PodmanConfigurationDecision(HasUsableEndpoint: true, DockerHost: $"unix://{podmanSocket}", DisableRyuk: true),
            decision);
    }

    [Fact]
    public void Decide_WithAPodmanSocket_DoesNotOverrideAnExplicitRyukSetting()
    {
        var decision = Decide(
            isWindows: false,
            environment: new() { [RyukDisabledVariable] = "false" },
            existingSockets: ["/run/podman/podman.sock"]);

        Assert.Equal(
            new PodmanConfigurationDecision(HasUsableEndpoint: true, DockerHost: "unix:///run/podman/podman.sock"),
            decision);
    }

    [Fact]
    public void Decide_WithBothSockets_PrefersDocker()
    {
        var decision = Decide(
            isWindows: false,
            environment: [],
            existingSockets: ["/var/run/docker.sock", "/run/podman/podman.sock"]);

        Assert.Equal(new PodmanConfigurationDecision(HasUsableEndpoint: true), decision);
    }

    [Fact]
    public void Decide_WithNoWellKnownSocket_FallsBackToThePodmanCli()
    {
        var decision = Decide(
            isWindows: false,
            environment: [],
            existingSockets: [],
            podmanSocketFromCli: PodmanMachineSocket);

        Assert.Equal(
            new PodmanConfigurationDecision(HasUsableEndpoint: true, DockerHost: $"unix://{PodmanMachineSocket}", DisableRyuk: true),
            decision);
    }

    [Fact]
    public void Decide_WithAWellKnownSocket_DoesNotConsultThePodmanCli()
    {
        // Shelling out costs a process launch on every test run, so the well-known paths have to win.
        var cliInvoked = false;

        var decision = TestcontainersPodmanConfiguration.Decide(
            isWindows: false,
            HomeDirectory,
            _ => null,
            path => path == "/run/podman/podman.sock",
            () =>
            {
                cliInvoked = true;
                return null;
            });

        Assert.Equal(
            new PodmanConfigurationDecision(HasUsableEndpoint: true, DockerHost: "unix:///run/podman/podman.sock", DisableRyuk: true),
            decision);
        Assert.False(cliInvoked);
    }

    [Fact]
    public void Decide_WithNoRuntimeAtAll_ReportsNoEndpoint()
    {
        var decision = Decide(isWindows: false, environment: [], existingSockets: []);

        Assert.Equal(new PodmanConfigurationDecision(HasUsableEndpoint: false), decision);
    }

    [Fact]
    public void Decide_WithoutAHomeDirectory_SkipsTheHomeRelativeCandidates()
    {
        // Path.Combine throws on a null root, so a home directory that cannot be resolved has to be
        // dropped from the candidate list rather than fed into it.
        var dockerDesktopSocket = Path.Combine(HomeDirectory, ".docker", "desktop", "docker.sock");

        var decision = TestcontainersPodmanConfiguration.Decide(
            isWindows: false,
            homeDirectory: null,
            _ => null,
            path => path == dockerDesktopSocket,
            () => null);

        Assert.Equal(new PodmanConfigurationDecision(HasUsableEndpoint: false), decision);
    }

    [Fact]
    public void Decide_WithoutXdgRuntimeDir_SkipsTheXdgRelativeCandidates()
    {
        var rootlessPodmanSocket = Path.Combine(XdgRuntimeDirectory, "podman", "podman.sock");

        var decision = TestcontainersPodmanConfiguration.Decide(
            isWindows: false,
            HomeDirectory,
            _ => null,
            path => path == rootlessPodmanSocket,
            () => null);

        Assert.Equal(new PodmanConfigurationDecision(HasUsableEndpoint: false), decision);
    }

    private static PodmanConfigurationDecision Decide(
        bool isWindows,
        Dictionary<string, string> environment,
        string[] existingSockets,
        string? podmanSocketFromCli = null)
    {
        environment.TryAdd("XDG_RUNTIME_DIR", XdgRuntimeDirectory);

        return TestcontainersPodmanConfiguration.Decide(
            isWindows,
            HomeDirectory,
            variable => environment.GetValueOrDefault(variable),
            path => path is not null && existingSockets.Contains(path),
            () => podmanSocketFromCli);
    }
}
