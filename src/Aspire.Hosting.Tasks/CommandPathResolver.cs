// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Tasks;

// Visual Studio loads the net472 build of these tasks, so this mirrors src/Shared/PathLookupHelper.cs
// (which relies on .NET Core-only APIs) instead of linking it.
internal static class CommandPathResolver
{
    // cmd.exe searches these when PATHEXT is unset. MSBuild normally inherits PATHEXT, but a stripped
    // environment would otherwise only find extensionless files on Windows.
    // See https://learn.microsoft.com/windows-server/administration/windows-commands/path
    private static readonly string[] s_defaultWindowsPathExtensions = [".COM", ".EXE", ".BAT", ".CMD"];

    public static string? ResolveFromPath(string command, string? path = null)
    {
        return EnumerateFromPath(command, path).FirstOrDefault();
    }

    public static IEnumerable<string> EnumerateFromPath(string command, string? path = null)
    {
        return EnumerateFromPath(
            command,
            path ?? Environment.GetEnvironmentVariable("PATH"),
            IsWindows() ? GetWindowsPathExtensions(Environment.GetEnvironmentVariable("PATHEXT")) : null,
            FileExistsAndIsExecutable);
    }

    internal static IEnumerable<string> EnumerateFromPath(string command, string? path, string[]? pathExtensions, Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            yield break;
        }

        // A command that already ends in a PATHEXT extension (for example "aspire.exe") is searched as-is.
        if (pathExtensions is not null && pathExtensions.Any(extension => command.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
        {
            pathExtensions = null;
        }

        var seenPaths = new HashSet<string>(IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (var pathEntry in path!.Split(Path.PathSeparator))
        {
            // Windows PATH entries can be quoted, for example:
            //   C:\Tools;"C:\Program Files\dotnet";C:\Users\user\.dotnet\tools
            // Unix PATH has no quoting, so entries are literal and a quote or space is part of the name.
            var directory = TryGetFullPath(IsWindows() ? pathEntry.Trim().Trim('"') : pathEntry);
            if (directory is null)
            {
                continue;
            }

            if (TryFindInDirectory(directory, command, pathExtensions, fileExists) is { } candidate && seenPaths.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    internal static string[] GetWindowsPathExtensions(string? pathExtensions)
    {
        var extensions = (pathExtensions ?? string.Empty).Split([';'], StringSplitOptions.RemoveEmptyEntries);
        return extensions.Length > 0 ? extensions : s_defaultWindowsPathExtensions;
    }

    private static string? TryFindInDirectory(string directory, string command, string[]? pathExtensions, Func<string, bool> fileExists)
    {
        // Windows command lookup tries every PATHEXT extension, in order, within a directory before
        // moving on, and only then falls back to the bare name.
        if (pathExtensions is not null)
        {
            foreach (var extension in pathExtensions)
            {
                if (TryCombine(directory, command + extension, out var candidateWithExtension) &&
                    FileExistsSafe(fileExists, candidateWithExtension))
                {
                    return GetOnDiskPath(candidateWithExtension);
                }
            }
        }

        return TryCombine(directory, command, out var candidate) && FileExistsSafe(fileExists, candidate)
            ? candidate
            : null;
    }

    // PATHEXT entries are conventionally upper case (".CMD"), but Windows file names are case-insensitive, so
    // "aspire" + ".CMD" matches "aspire.cmd". Report the file name as it exists on disk so resolved paths read the
    // way the file was named. Falls back to the constructed path when the directory cannot be enumerated, for
    // example when a test supplies its own existence probe.
    private static string GetOnDiskPath(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            var fileName = Path.GetFileName(path);
            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName))
            {
                return path;
            }

#if NET
            var matches = Directory.EnumerateFiles(directory, fileName, new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, MatchType = MatchType.Simple });
#else
            // .NET Framework only runs on Windows, where file name patterns already match case-insensitively.
            var matches = Directory.EnumerateFiles(directory, fileName);
#endif
            foreach (var match in matches)
            {
                if (string.Equals(Path.GetFileName(match), fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return Path.Combine(directory, Path.GetFileName(match));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
        }

        return path;
    }

    private static string? TryGetFullPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static bool TryCombine(string directory, string fileName, out string path)
    {
        try
        {
            path = Path.Combine(directory, fileName);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            path = string.Empty;
            return false;
        }
    }

    // An unreadable PATH directory or malformed candidate can make the existence probe throw instead of returning false.
    private static bool FileExistsSafe(Func<string, bool> fileExists, string path)
    {
        try
        {
            return fileExists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PathTooLongException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool FileExistsAndIsExecutable(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

#if NET
        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        // Match Unix command lookup by skipping PATH entries that exist but have no execute bit set.
        const UnixFileMode ExecuteBits = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

        return (File.GetUnixFileMode(path) & ExecuteBits) != 0;
#else
        // .NET Framework only runs on Windows, where file existence is sufficient.
        return true;
#endif
    }

    private static bool IsWindows() => Path.DirectorySeparatorChar == '\\';
}
