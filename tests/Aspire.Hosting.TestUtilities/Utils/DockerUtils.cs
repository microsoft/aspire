// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Shared;

namespace Aspire.Hosting.Utils;

public sealed class DockerUtils
{
    // Bounds the one-off runtime probe below. ContainerRuntimeDetector already caps each individual
    // process at 10s, but a machine with several wedged CLIs could still stall test cleanup for a while.
    private static readonly TimeSpan s_runtimeDetectionTimeout = TimeSpan.FromSeconds(30);

    // `volume rm` / `volume inspect` are spelled identically by Docker and Podman, so the only thing that
    // varies is the executable. Resolved once because every functional test that uses a volume calls this.
    private static readonly Lazy<string?> s_runtimeExecutable = new(ResolveRuntimeExecutable);

    /// <summary>
    /// Resolves the container runtime the app host itself would have used, so volumes are removed with the
    /// same engine that created them.
    /// </summary>
    /// <remarks>
    /// Picking the first CLI on PATH is not good enough: with both CLIs installed and
    /// <c>ASPIRE_CONTAINER_RUNTIME=podman</c>, Aspire creates a Podman volume, and the same happens when
    /// Docker is installed but unhealthy and detection falls back to Podman. Either way a PATH-order guess
    /// would run <c>docker volume rm</c> against a volume Docker does not have, silently leaking it.
    /// <see cref="ContainerRuntimeDetector"/> is exactly what <c>ContainerRuntimeResolver</c> uses, and the
    /// configured-runtime precedence mirrors <c>DcpOptions</c>. The in-memory
    /// <c>DcpPublisher:ContainerRuntime</c> key is deliberately not honoured — this is a static helper with
    /// no <c>IConfiguration</c>, and no test that sets that key creates volumes.
    /// </remarks>
    private static string? ResolveRuntimeExecutable()
    {
        var configuredRuntime = GetConfiguredRuntime();

        // Detection is async and spawns processes. Task.Run keeps the blocking wait off any ambient
        // synchronization context, since callers are synchronous test teardown paths.
        using var cts = new CancellationTokenSource(s_runtimeDetectionTimeout);
        ContainerRuntimeInfo? detected;
        try
        {
            detected = Task.Run(
                () => ContainerRuntimeDetector.FindAvailableRuntimeAsync(configuredRuntime, cancellationToken: cts.Token),
                cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        if (detected is not { IsInstalled: true })
        {
            return null;
        }

        return PathLookupHelper.FindFullPathFromPath(detected.Executable);
    }

    /// <summary>
    /// Reads the explicitly configured container runtime, using the same precedence as <c>DcpOptions</c>.
    /// </summary>
    private static string? GetConfiguredRuntime()
    {
        var configuredRuntime = Environment.GetEnvironmentVariable(KnownConfigNames.ContainerRuntime)
            ?? Environment.GetEnvironmentVariable(KnownConfigNames.Legacy.ContainerRuntime);

        return string.IsNullOrEmpty(configuredRuntime) ? null : configuredRuntime;
    }

    /// <summary>
    /// Explains why no runtime could be used, so a failing cleanup does not look like a missing install
    /// when the runtime was actually pinned by configuration.
    /// </summary>
    private static string DescribeMissingRuntime()
        => GetConfiguredRuntime() is { } configuredRuntime
            ? $"the container runtime configured by {KnownConfigNames.ContainerRuntime} ('{configuredRuntime}') is not available."
            : "no container runtime was found on PATH.";

    public static void AttemptDeleteDockerVolume(string volumeName, bool throwOnFailure = false)
    {
        if (s_runtimeExecutable.Value is not string runtime)
        {
            if (throwOnFailure)
            {
                throw new InvalidOperationException($"Failed to delete the volume named '{volumeName}': {DescribeMissingRuntime()}");
            }

            return;
        }

        for (var i = 0; i < 3; i++)
        {
            if (i != 0)
            {
                Thread.Sleep(1000);
            }

            if (Process.Start(runtime, $"volume rm {volumeName}") is { } process)
            {
                var exited = process.WaitForExit(TimeSpan.FromSeconds(3));
                var done = exited && process.ExitCode == 0;
                process.Kill(entireProcessTree: true);
                process.Dispose();

                if (done)
                {
                    break;
                }
            }
        }

        if (throwOnFailure)
        {
            if (Process.Start(runtime, $"volume inspect {volumeName}") is { } process)
            {
                var exited = process.WaitForExit(TimeSpan.FromSeconds(3));
                var exitCode = process.ExitCode;
                process.Kill(entireProcessTree: true);
                process.Dispose();
                if (!exited)
                {
                    throw new InvalidOperationException($"Failed to inspect the deleted volume named '{volumeName}', the inspect process did not exit.");
                }
                if (exitCode == 0)
                {
                    throw new InvalidOperationException($"Failed to delete the volume named '{volumeName}'. Attempted to inspect the volume and it still exists.");
                }
            }
            else
            {
                throw new InvalidOperationException($"Failed to inspect the deleted volume named '{volumeName}', the inspect process did not start.");
            }
        }
    }
}
