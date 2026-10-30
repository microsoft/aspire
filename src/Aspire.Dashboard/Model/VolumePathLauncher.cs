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
            var fullPath = Path.GetFullPath(path);
            var startInfo = CreateStartInfo(fullPath, Directory.Exists(fullPath), GetCurrentPlatform());
            if (startInfo is null)
            {
                return false;
            }

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

    /// <summary>
    /// Creates the command that shows the path in the platform's file explorer without opening it. Opening a mounted
    /// file with its default handler could run it, for example a <c>.command</c> file on macOS or a <c>.desktop</c>
    /// file on Linux, so a file is selected in its folder instead:
    /// <list type="bullet">
    /// <item>Windows: <c>explorer.exe /select,"C:\src\init.sql"</c>, or <c>explorer.exe "C:\src\data"</c> for a folder.</item>
    /// <item>macOS: <c>open -R /src/init.sql</c> selects the file or folder in Finder.</item>
    /// <item>Linux: <c>xdg-open</c> can't select a file, so it opens the file's folder, or the folder itself.</item>
    /// </list>
    /// </summary>
    internal static ProcessStartInfo? CreateStartInfo(string fullPath, bool isDirectory, VolumePathPlatform platform)
    {
        ProcessStartInfo startInfo;
        switch (platform)
        {
            case VolumePathPlatform.Windows:
                // explorer.exe parses its own command line, and only accepts the path quoted after "/select,", so
                // ArgumentList's quoting can't be used. Windows paths can't contain '"', so reject one rather than
                // let it end the quoted path early.
                if (fullPath.Contains('"'))
                {
                    return null;
                }

                startInfo = new ProcessStartInfo("explorer.exe", isDirectory ? $"\"{fullPath}\"" : $"/select,\"{fullPath}\"");
                break;
            case VolumePathPlatform.MacOS:
                startInfo = new ProcessStartInfo("open") { ArgumentList = { "-R", fullPath } };
                break;
            default:
                var folder = isDirectory ? fullPath : Path.GetDirectoryName(fullPath);
                if (string.IsNullOrEmpty(folder))
                {
                    return null;
                }

                startInfo = new ProcessStartInfo("xdg-open") { ArgumentList = { folder } };
                break;
        }

        startInfo.UseShellExecute = false;
        return startInfo;
    }

    private static VolumePathPlatform GetCurrentPlatform() =>
        OperatingSystem.IsWindows() ? VolumePathPlatform.Windows
        : OperatingSystem.IsMacOS() ? VolumePathPlatform.MacOS
        : VolumePathPlatform.Linux;
}

internal enum VolumePathPlatform
{
    Windows,
    MacOS,
    Linux
}
