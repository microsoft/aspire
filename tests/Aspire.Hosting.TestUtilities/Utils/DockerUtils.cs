// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Shared;

namespace Aspire.Hosting.Utils;

public sealed class DockerUtils
{
    // `volume rm` / `volume inspect` are spelled identically by Docker and Podman, so the only thing that
    // varies is the executable. Resolved once because every functional test that uses a volume calls this.
    private static readonly Lazy<string?> s_runtimeExecutable = new(static () =>
        PathLookupHelper.FindFullPathFromPath(KnownContainerRuntimes.Docker)
        ?? PathLookupHelper.FindFullPathFromPath(KnownContainerRuntimes.Podman));

    public static void AttemptDeleteDockerVolume(string volumeName, bool throwOnFailure = false)
    {
        if (s_runtimeExecutable.Value is not string runtime)
        {
            if (throwOnFailure)
            {
                throw new InvalidOperationException($"Failed to delete the volume named '{volumeName}': no container runtime was found on PATH.");
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
