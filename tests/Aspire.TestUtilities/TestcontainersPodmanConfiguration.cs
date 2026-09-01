// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Shared;

namespace Aspire.TestUtilities;

/// <summary>
/// Points Testcontainers at Podman on machines where Podman is the only container runtime, and reports
/// whether Testcontainers has an endpoint to talk to at all.
/// </summary>
/// <remarks>
/// <para>
/// Testcontainers 4.x cannot find Podman on its own. Its endpoint discovery only ever probes Docker
/// sockets: <c>UnixEndpointAuthenticationProvider</c> checks <c>/var/run/docker.sock</c>, and
/// <c>RootlessUnixEndpointAuthenticationProvider</c> is hard-coded to <c>docker.sock</c> file names
/// (<c>private const string DockerSocket = "docker.sock"</c>) and is additionally gated on
/// <c>IsOSPlatform(Linux)</c>, so on macOS it is never even tried. Nothing in the provider chain looks at
/// a path containing "podman". Without this, every Testcontainers-backed fixture fails on a Podman-only
/// host with <c>DockerUnavailableException</c> ("Docker is either not running or misconfigured...").
/// See https://github.com/testcontainers/testcontainers-dotnet/blob/4.8.1/src/Testcontainers/Configurations/TestcontainersSettings.cs
/// and https://github.com/testcontainers/testcontainers-dotnet/blob/4.8.1/src/Testcontainers/Builders/RootlessUnixEndpointAuthenticationProvider.cs
/// </para>
/// <para>
/// Configuration has to happen before the first <c>ContainerBuilder</c> is constructed, because
/// <c>TestcontainersSettings</c> resolves its endpoint and Ryuk switches once, in its static constructor.
/// The <see cref="TestFeature.Testcontainers"/> capability check is the hook for that, so every
/// Testcontainers fixture in the repo passes through it - either by calling
/// <see cref="RequiresFeatureAttribute.IsFeatureSupported"/> directly before building its container, or via
/// the trait attribute, which xUnit evaluates during discovery. That makes this strictly more reliable than
/// a module initializer, which would only cover the assemblies it happened to be compiled into.
/// </para>
/// <para>
/// Podman cannot always be reached, though: Testcontainers 4.8.1 cannot drive it over a Windows named pipe,
/// and on Linux <c>podman</c> runs daemonlessly with no API socket unless <c>podman system service</c> is
/// running. Those hosts still run containers perfectly well through DCP, so they keep
/// <see cref="TestFeature.ContainerRuntime"/>; it is <see cref="TestFeature.Testcontainers"/> that consults
/// <see cref="HasUsableEndpoint"/> and skips the fixtures which would otherwise throw.
/// </para>
/// </remarks>
internal static class TestcontainersPodmanConfiguration
{
    private const string DockerHostVariable = "DOCKER_HOST";
    private const string RyukDisabledVariable = "TESTCONTAINERS_RYUK_DISABLED";

    // Give up rather than hang the test run if the Podman CLI is wedged.
    private static readonly TimeSpan s_podmanInspectTimeout = TimeSpan.FromSeconds(10);

    // Called once per test during trait evaluation, so the probing must happen at most once per process.
    private static readonly Lazy<bool> s_hasUsableEndpoint = new(Configure);

    /// <summary>
    /// Reports whether configuration has already run, without triggering it.
    /// </summary>
    internal static bool IsConfigurationInitialized => s_hasUsableEndpoint.IsValueCreated;

    /// <summary>
    /// Configures Testcontainers for Podman if required. Safe to call repeatedly; the work happens once.
    /// </summary>
    internal static void EnsureConfigured() => _ = s_hasUsableEndpoint.Value;

    /// <summary>
    /// Reports whether Testcontainers has a Docker-compatible API endpoint to talk to, configuring it for
    /// Podman first if that is what it takes. Safe to call repeatedly; the work happens once.
    /// </summary>
    internal static bool HasUsableEndpoint => s_hasUsableEndpoint.Value;

    /// <returns><inheritdoc cref="HasUsableEndpoint" path="/summary"/></returns>
    private static bool Configure()
    {
        var decision = Decide(
            OperatingSystem.IsWindows(),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetEnvironmentVariable,
            SocketExists,
            FindPodmanSocketFromCli);

        // Both settings are published as environment variables rather than assigned on
        // TestcontainersSettings, because they have to survive a process boundary. Microsoft.Testing.Platform
        // runs the tests in a child process (for example when --hangdump is used), and that child inherits
        // the environment but not managed static state, so an in-process assignment ends up applying to the
        // wrong process. Going through the environment also keeps this file free of a compile-time
        // dependency on the Testcontainers API, which Aspire.TestUtilities deliberately does not reference.
        if (decision.DockerHost is { } dockerHost)
        {
            Environment.SetEnvironmentVariable(DockerHostVariable, dockerHost);
        }

        if (decision.DisableRyuk)
        {
            Environment.SetEnvironmentVariable(RyukDisabledVariable, "true");
        }

        return decision.HasUsableEndpoint;
    }

    /// <summary>
    /// Works out what Testcontainers needs, without touching any ambient state. Every input is injected so
    /// that the branches below - almost all of which are unreachable on CI, where Docker is always present -
    /// can be exercised by unit tests.
    /// </summary>
    /// <param name="isWindows">Whether the host is Windows.</param>
    /// <param name="homeDirectory">The current user's home directory, used to build socket candidates.</param>
    /// <param name="getEnvironmentVariable">Reads an environment variable.</param>
    /// <param name="socketExists">Reports whether a socket file exists at the given path.</param>
    /// <param name="findPodmanSocketFromCli">Asks the Podman CLI for the machine's API socket path.</param>
    internal static PodmanConfigurationDecision Decide(
        bool isWindows,
        string? homeDirectory,
        Func<string, string?> getEnvironmentVariable,
        Func<string?, bool> socketExists,
        Func<string?> findPodmanSocketFromCli)
    {
        var dockerHost = getEnvironmentVariable(DockerHostVariable);

        // Windows talks to the engine over a named pipe, and Podman-on-Windows needs fixes that are not in
        // Testcontainers 4.8.1 (https://github.com/testcontainers/testcontainers-dotnet/issues/1438). There
        // is nothing to configure, so the only question is whether the developer already pointed
        // Testcontainers at something it can actually use. A Podman pipe is rejected rather than trusted,
        // because running the fixtures against it only produces DockerUnavailableException instead of the
        // skip the caller wants. A Docker Desktop install is handled by the caller, which additionally
        // treats the `docker` CLI being on PATH as proof of a reachable endpoint.
        if (isWindows)
        {
            return new PodmanConfigurationDecision(
                HasUsableEndpoint: !string.IsNullOrEmpty(dockerHost) && !LooksLikePodmanEndpoint(dockerHost));
        }

        // An explicit DOCKER_HOST always wins: it is how a developer points the tests at a specific engine,
        // and Testcontainers' EnvironmentEndpointAuthenticationProvider already honours it. This is also the
        // branch a test host child process takes, because it inherits the values configured below.
        if (!string.IsNullOrEmpty(dockerHost))
        {
            // Ryuk still has to be turned off when that endpoint is a Podman one. `podman machine start`
            // prints an `export DOCKER_HOST=...` hint, so pointing DOCKER_HOST at Podman by hand is the
            // normal setup rather than an exotic one.
            return new PodmanConfigurationDecision(
                HasUsableEndpoint: true,
                DisableRyuk: LooksLikePodmanEndpoint(dockerHost) && !IsRyukSettingExplicit(getEnvironmentVariable));
        }

        // CI (GitHub Actions and Helix) always has Docker, so this leaves those runs on exactly the code
        // path they use today.
        if (DockerSocketExists(homeDirectory, getEnvironmentVariable, socketExists))
        {
            return new PodmanConfigurationDecision(HasUsableEndpoint: true);
        }

        if (FindPodmanSocket(homeDirectory, getEnvironmentVariable, socketExists, findPodmanSocketFromCli) is not { } podmanSocketPath)
        {
            return default;
        }

        return new PodmanConfigurationDecision(
            HasUsableEndpoint: true,
            DockerHost: $"unix://{podmanSocketPath}",
            DisableRyuk: !IsRyukSettingExplicit(getEnvironmentVariable));
    }

    /// <summary>
    /// Reports whether an endpoint is served by Podman rather than Docker.
    /// </summary>
    /// <remarks>
    /// A substring match is the only option available without connecting: the value is an opaque URI and
    /// Docker and Podman speak the same API over it. It holds for every layout Podman produces, all of
    /// which name the product somewhere in the path:
    /// <code>
    /// unix:///run/user/1000/podman/podman.sock                                        (Linux, rootless)
    /// unix:///run/podman/podman.sock                                                  (Linux, rootful)
    /// unix:///var/folders/_h/.../T/podman/podman-machine-default-api.sock             (macOS machine)
    /// npipe:////./pipe/podman-machine-default                                         (Windows machine)
    /// </code>
    /// A false negative only costs the Ryuk workaround below, and a false positive only skips a container
    /// reaper the fixtures do not rely on, so neither direction breaks a test run.
    /// </remarks>
    private static bool LooksLikePodmanEndpoint(string dockerHost)
        => dockerHost.Contains(KnownContainerRuntimes.Podman, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reports whether the developer has taken ownership of the Ryuk setting, in which case their value
    /// stands regardless of what the runtime is.
    /// </summary>
    private static bool IsRyukSettingExplicit(Func<string, string?> getEnvironmentVariable)
        => !string.IsNullOrEmpty(getEnvironmentVariable(RyukDisabledVariable));

    /// <summary>
    /// Reports whether a Docker socket that Testcontainers would discover on its own already exists.
    /// </summary>
    private static bool DockerSocketExists(string? homeDirectory, Func<string, string?> getEnvironmentVariable, Func<string?, bool> socketExists)
    {
        // Mirrors the socket paths Testcontainers 4.8.1 probes on Unix, plus the Docker Desktop socket.
        string?[] candidates =
        [
            "/var/run/docker.sock",
            CombineWithEnvironmentVariable(getEnvironmentVariable, "XDG_RUNTIME_DIR", "docker.sock"),
            CombineWithHome(homeDirectory, ".docker", "run", "docker.sock"),
            CombineWithHome(homeDirectory, ".docker", "desktop", "docker.sock"),
        ];

        return candidates.Any(socketExists);
    }

    /// <summary>
    /// Locates the Podman API socket, preferring well-known locations before shelling out to the CLI.
    /// </summary>
    private static string? FindPodmanSocket(
        string? homeDirectory,
        Func<string, string?> getEnvironmentVariable,
        Func<string?, bool> socketExists,
        Func<string?> findPodmanSocketFromCli)
    {
        string?[] candidates =
        [
            // Linux, rootless.
            CombineWithEnvironmentVariable(getEnvironmentVariable, "XDG_RUNTIME_DIR", "podman", "podman.sock"),
            // Linux, rootful.
            "/run/podman/podman.sock",
            // macOS: `podman machine` maintains this symlink to the current machine's API socket. Preferred
            // over the link target because AF_UNIX paths are limited to ~104 bytes on macOS and the target
            // lives under a long $TMPDIR path.
            CombineWithHome(homeDirectory, ".local", "share", "containers", "podman", "machine", "podman.sock"),
        ];

        return candidates.FirstOrDefault(socketExists) ?? findPodmanSocketFromCli();
    }

    /// <summary>
    /// Asks the Podman CLI where the machine's API socket lives, as a fallback for layouts this file does
    /// not know about.
    /// </summary>
    private static string? FindPodmanSocketFromCli()
    {
        // `podman machine inspect --format {{.ConnectionInfo.PodmanSocket.Path}}` prints one absolute path
        // per inspected machine, e.g.
        //   /var/folders/_h/jnjmbyss12g40fn478_1k5rr0000gn/T/podman/podman-machine-default-api.sock
        // It exits non-zero when no machine exists, and prints nothing on Linux where there is no VM.
        var startInfo = new ProcessStartInfo(KnownContainerRuntimes.Podman)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add("machine");
        startInfo.ArgumentList.Add("inspect");
        startInfo.ArgumentList.Add("--format");
        startInfo.ArgumentList.Add("{{.ConnectionInfo.PodmanSocket.Path}}");

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            // Drain both streams concurrently so a full pipe buffer cannot block podman before it exits.
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit((int)s_podmanInspectTimeout.TotalMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            // The overload that takes a timeout does not wait for the redirected streams to be flushed.
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                return null;
            }

            return standardOutput.GetAwaiter().GetResult()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(SocketExists);
        }
        catch (Exception)
        {
            // Podman is not installed, not on PATH, or otherwise unusable. Leave Testcontainers alone so it
            // reports its own diagnostics.
            return null;
        }
    }

    private static bool SocketExists(string? path) => !string.IsNullOrEmpty(path) && File.Exists(path);

    private static string? CombineWithEnvironmentVariable(Func<string, string?> getEnvironmentVariable, string variable, params string[] parts)
    {
        var root = getEnvironmentVariable(variable);
        return string.IsNullOrEmpty(root) ? null : Path.Combine([root, .. parts]);
    }

    private static string? CombineWithHome(string? homeDirectory, params string[] parts)
        => string.IsNullOrEmpty(homeDirectory) ? null : Path.Combine([homeDirectory, .. parts]);
}

/// <summary>
/// What <see cref="TestcontainersPodmanConfiguration.Decide"/> concluded about the current machine.
/// </summary>
/// <param name="HasUsableEndpoint">
/// Whether Testcontainers ends up with a Docker-compatible API endpoint to talk to. When this is
/// <see langword="false"/> every Testcontainers fixture will throw <c>DockerUnavailableException</c>, so
/// those tests should be skipped rather than run.
/// </param>
/// <param name="DockerHost">
/// The value to publish as <c>DOCKER_HOST</c>, or <see langword="null"/> to leave it alone.
/// </param>
/// <param name="DisableRyuk">
/// Whether to publish <c>TESTCONTAINERS_RYUK_DISABLED=true</c>. Ryuk, the resource reaper, bind-mounts the
/// engine socket into a privileged container and always mounts it at <c>/var/run/docker.sock</c>
/// (<c>ResourceReaper.UnixSocketMount</c>). Under rootless Podman that fails outright - "statfs ...:
/// operation not supported" on a macOS podman machine, "permission denied" on Linux - so it has to be off.
/// See https://github.com/testcontainers/testcontainers-dotnet/issues/876. The fixtures dispose their own
/// containers, and Helix already disables Ryuk for Testcontainers work items
/// (tests/helix/send-to-helix-inner.proj).
/// </param>
internal readonly record struct PodmanConfigurationDecision(
    bool HasUsableEndpoint,
    string? DockerHost = null,
    bool DisableRyuk = false);
