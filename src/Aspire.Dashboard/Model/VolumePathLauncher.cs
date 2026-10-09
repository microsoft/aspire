// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Aspire.Dashboard.Model;

/// <summary>
/// Reveals a bind-mount volume's host path in the local OS file explorer. This only works when the dashboard
/// process is running on the same machine as the browser and the mounted path (the common local-development
/// case), so callers must treat a false return as an expected, user-facing outcome rather than an error to log.
/// </summary>
public interface IVolumePathLauncher
{
    /// <summary>Attempts to open <paramref name="path"/> in the OS file explorer.</summary>
    /// <param name="path">The host path to reveal.</param>
    /// <returns><see langword="true"/> if an explorer process was launched; otherwise <see langword="false"/>.</returns>
    bool TryReveal(string? path);
}

internal sealed class VolumePathLauncher : IVolumePathLauncher
{
    public bool TryReveal([NotNullWhen(true)] string? path)
    {
        // A named Docker volume's "source" is an opaque volume name, not a filesystem path, and a bind mount
        // created elsewhere (e.g. inside WSL, or on a remote Docker host) won't resolve on this machine. Checking
        // existence first turns both cases into an expected "can't open" outcome instead of a confusing OS error
        // or an explorer window that silently opens to nothing.
        if (string.IsNullOrWhiteSpace(path) || (!Directory.Exists(path) && !File.Exists(path)))
        {
            return false;
        }

        try
        {
            var startInfo = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("explorer.exe", $"\"{path}\"")
                : OperatingSystem.IsMacOS()
                    ? new ProcessStartInfo("open", $"\"{path}\"")
                    : new ProcessStartInfo("xdg-open", $"\"{path}\"");

            startInfo.UseShellExecute = false;

            using var process = Process.Start(startInfo);
            return process is not null;
        }
        catch (Exception)
        {
            // The target platform may not have a file explorer available (e.g. a minimal container image), or the
            // process couldn't be launched for some other OS-specific reason. Either way, this is a best-effort
            // convenience feature, so report it back to the caller as a normal failure instead of throwing.
            return false;
        }
    }
}
