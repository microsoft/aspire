// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Hosting.Utils;

public sealed class DockerUtils
{
    public static void AttemptDeleteDockerVolume(string volumeName, bool throwOnFailure = false)
    {
        for (var i = 0; i < 3; i++)
        {
            if (i != 0)
            {
                Thread.Sleep(1000);
            }

            if (Process.Start("docker", $"volume rm {volumeName}") is { } process)
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
            if (Process.Start("docker", $"volume inspect {volumeName}") is { } process)
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
                    throw new InvalidOperationException($"Failed to delete docker volume named '{volumeName}'. Attempted to inspect the volume and it still exists.");
                }
            }
            else
            {
                throw new InvalidOperationException($"Failed to inspect the deleted volume named '{volumeName}', the inspect process did not start.");
            }
        }
    }
}
