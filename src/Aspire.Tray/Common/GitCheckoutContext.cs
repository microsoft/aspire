// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray;

internal static class GitCheckoutContext
{
    internal static string? GetLabel(string appHostPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(appHostPath));
            if (directory is null || !Directory.Exists(directory))
            {
                return null;
            }
            for (var depth = 0; directory is not null && depth < 64; depth++)
            {
                var gitPath = Path.Combine(directory, ".git");
                if (Directory.Exists(gitPath))
                {
                    return ReadBranch(gitPath);
                }
                if (File.Exists(gitPath))
                {
                    var pointer = ReadLine(gitPath);
                    if (pointer?.StartsWith("gitdir:", StringComparison.Ordinal) != true)
                    {
                        return null;
                    }
                    var gitDirectory = Path.GetFullPath(pointer[7..].Trim(), directory);
                    var branch = ReadBranch(gitDirectory);
                    if (branch is not null)
                    {
                        return branch;
                    }
                    // A detached linked worktree still has a useful checkout folder name.
                    // Validate Git's back-pointer so submodules/separate gitdirs are excluded.
                    var parent = Path.GetDirectoryName(gitDirectory);
                    var backPointer = ReadLine(Path.Combine(gitDirectory, "gitdir"));
                    var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
                    return Path.GetFileName(parent) == "worktrees" && backPointer is not null
                        && comparer.Equals(Path.GetFullPath(backPointer, gitDirectory), gitPath)
                        ? Path.GetFileName(directory) : null;
                }
                directory = Path.GetDirectoryName(directory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException)
        {
            // Git context is optional; unavailable metadata must never hide an AppHost.
        }
        return null;
    }

    private static string? ReadBranch(string gitDirectory)
    {
        const string Prefix = "ref: refs/heads/";
        var head = ReadLine(Path.Combine(gitDirectory, "HEAD"));
        return head?.StartsWith(Prefix, StringComparison.Ordinal) == true && head.Length > Prefix.Length
            ? head[Prefix.Length..] : null;
    }

    private static string? ReadLine(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 4096)
        {
            return null;
        }
        using var reader = new StreamReader(stream);
        return reader.ReadLine()?.Trim();
    }
}
