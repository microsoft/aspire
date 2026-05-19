// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;

/// <summary>
/// Provides helper methods for looking up executables on the system PATH.
/// </summary>
internal static class PathLookupHelper
{
    /// <summary>
    /// Resolves an executable path or command name to a full path by searching the system PATH.
    /// </summary>
    /// <param name="executablePath">The executable path or command name to resolve.</param>
    /// <param name="environmentVariables">Optional environment variable overrides to use for lookup.</param>
    /// <returns>The resolved executable path if found; otherwise, <paramref name="executablePath"/> when it is an explicit path.</returns>
    /// <exception cref="FileNotFoundException">Thrown when <paramref name="executablePath"/> is a command name that is not found on PATH.</exception>
    public static string ResolveExecutablePath(string executablePath, IDictionary<string, string>? environmentVariables = null)
    {
        Debug.Assert(!string.IsNullOrWhiteSpace(executablePath));

        // Values with a directory/root component are explicit paths (relative, absolute, UNC, drive-relative, etc.).
        // Do not PATH-search or append PATHEXT here; let Process.Start handle missing files or platform-specific path rules.
        if (IsExplicitExecutablePath(executablePath))
        {
            return executablePath;
        }

        // The actual PATH walk is shared by FindFullPathFromPath below. This wrapper first computes the
        // effective PATH/PATHEXT from command-specific environment overrides so lookup matches the child process.
        var (pathOverride, pathExtensionsOverride) = GetPathLookupEnvironmentVariables(environmentVariables);

        var path = pathOverride ?? Environment.GetEnvironmentVariable("PATH");
        var pathExtensions = OperatingSystem.IsWindows()
            ? (pathExtensionsOverride ?? Environment.GetEnvironmentVariable("PATHEXT"))?.Split(';', StringSplitOptions.RemoveEmptyEntries) ?? []
            : null;

        // Non-explicit command names must resolve through the effective PATH before Process.Start sees them. On Windows,
        // CreateProcessW also searches the AppHost executable directory and current directory for bare names, which would
        // bypass the intended PATH-only lookup if we returned the original name on miss.
        // See https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessw
        return FindFullPathFromPath(executablePath, path, Path.PathSeparator, FileExistsAndIsExecutable, pathExtensions)
            ?? throw new FileNotFoundException($"Executable '{executablePath}' was not found on PATH.", executablePath);
    }

    /// <summary>
    /// Finds the full path of a command by searching the system PATH.
    /// On Windows, this also searches for executables with common extensions (.exe, .cmd, .bat, etc.).
    /// </summary>
    /// <param name="command">The command name to search for.</param>
    /// <returns>The full path to the executable if found; otherwise, <c>null</c>.</returns>
    public static string? FindFullPathFromPath(string command)
    {
        var pathExtensions = OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("PATHEXT")?.Split(';', StringSplitOptions.RemoveEmptyEntries) ?? []
            : null;

        return FindFullPathFromPath(command, Environment.GetEnvironmentVariable("PATH"), Path.PathSeparator, FileExistsAndIsExecutable, pathExtensions);
    }

    /// <summary>
    /// Finds the full path of a command by searching the specified PATH variable.
    /// </summary>
    /// <param name="command">The command name to search for.</param>
    /// <param name="pathVariable">The PATH environment variable value to search.</param>
    /// <param name="pathSeparator">The character used to separate paths in the PATH variable.</param>
    /// <param name="fileExists">A function to check if a file exists at a given path.</param>
    /// <param name="pathExtensions">Optional array of executable extensions to try (e.g., .exe, .cmd). When provided, these extensions will be appended to the command if not already present.</param>
    /// <returns>The full path to the executable if found; otherwise, <c>null</c>.</returns>
    internal static string? FindFullPathFromPath(string command, string? pathVariable, char pathSeparator, Func<string, bool> fileExists, string[]? pathExtensions = null)
    {
        Debug.Assert(!string.IsNullOrWhiteSpace(command));

        // If the command already has a known extension, just search for it directly.
        if (pathExtensions is not null && pathExtensions.Any(ext => command.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
        {
            return FindFullPath(command, pathVariable, pathSeparator, fileExists, pathExtensions: null);
        }

        return FindFullPath(command, pathVariable, pathSeparator, fileExists, pathExtensions);
    }

    private static string? FindFullPath(string command, string? pathVariable, char pathSeparator, Func<string, bool> fileExists, string[]? pathExtensions)
    {
        foreach (var directory in (pathVariable ?? string.Empty).Split(pathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            // On Windows, search each directory completely with all PATHEXT extensions before moving to the next.
            // This matches Windows command lookup behavior where directory order takes precedence.
            if (pathExtensions is not null && pathExtensions.Length > 0)
            {
                foreach (var extension in pathExtensions)
                {
                    var fullPathWithExt = Path.Combine(directory, command + extension);
                    if (fileExists(fullPathWithExt))
                    {
                        return fullPathWithExt;
                    }
                }
            }

            // Try exact match (for non-Windows, or as fallback on Windows if no extension match found in this directory).
            var fullPath = Path.Combine(directory, command);
            if (fileExists(fullPath))
            {
                return fullPath;
            }
        }

        return null;
    }

    private static bool IsExplicitExecutablePath(string executablePath)
    {
        return !string.Equals(Path.GetFileName(executablePath), executablePath, StringComparison.Ordinal)
            || Path.IsPathRooted(executablePath);
    }

    private static bool FileExistsAndIsExecutable(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            // Match Unix command lookup behavior by skipping PATH entries that exist but have no execute bit set.
            const UnixFileMode ExecuteBits = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

            return (File.GetUnixFileMode(path) & ExecuteBits) != 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static (string? Path, string? PathExtensions) GetPathLookupEnvironmentVariables(IDictionary<string, string>? environmentVariables)
    {
        if (environmentVariables is null)
        {
            return default;
        }

        var hasPath = environmentVariables.TryGetValue("PATH", out var path);
        string? pathExtensions = null;
        var hasPathExtensions = OperatingSystem.IsWindows() && environmentVariables.TryGetValue("PATHEXT", out pathExtensions);

        // Environment variables are case-insensitive on Windows and case-sensitive on Unix-like systems.
        // Scan once for both PATH and PATHEXT casing variants instead of scanning once per variable.
        if (OperatingSystem.IsWindows() && (!hasPath || !hasPathExtensions))
        {
            foreach (var (key, environmentValue) in environmentVariables)
            {
                if (!hasPath && string.Equals(key, "PATH", StringComparison.OrdinalIgnoreCase))
                {
                    path = environmentValue;
                    hasPath = true;
                }
                else if (!hasPathExtensions && string.Equals(key, "PATHEXT", StringComparison.OrdinalIgnoreCase))
                {
                    pathExtensions = environmentValue;
                    hasPathExtensions = true;
                }

                if (hasPath && hasPathExtensions)
                {
                    break;
                }
            }
        }

        return (path, pathExtensions);
    }
}
