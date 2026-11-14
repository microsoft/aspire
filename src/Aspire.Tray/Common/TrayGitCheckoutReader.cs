// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray;

/// <summary>Reads Git repository and worktree metadata from the filesystem.</summary>
internal static class TrayGitCheckoutReader
{
    public static TrayGitCheckout? Read(string appHostPath)
    {
        try
        {
            appHostPath = Path.GetFullPath(appHostPath);
            if (!System.IO.Directory.Exists(Path.GetDirectoryName(appHostPath)))
            {
                // A removed checkout must not inherit Git metadata from an unrelated ancestor.
                return null;
            }
            for (var root = Path.GetDirectoryName(appHostPath); root is not null; root = Path.GetDirectoryName(root))
            {
                var git = Path.Combine(root, ".git");
                if (File.Exists(git))
                {
                    // Linked worktrees use "gitdir: /repo/.git/worktrees/cart"; relative paths
                    // are relative to the pointer file, not the tray's working directory.
                    var pointer = ReadMetadata(git);
                    if (!pointer.StartsWith("gitdir:", StringComparison.Ordinal))
                    {
                        return null;
                    }
                    git = Path.GetFullPath(pointer[7..].Trim(), root);
                    var backPointer = Path.Combine(git, "gitdir");
                    // A copied checkout retains its old .git pointer. Git's administrative
                    // gitdir file must point back to this checkout's .git, not the original.
                    if (!File.Exists(Path.Combine(git, "commondir")) || !File.Exists(backPointer)
                        || !TrayAppHostPath.Comparer.Equals(Path.GetFullPath(ReadMetadata(backPointer), git), Path.Combine(root, ".git")))
                    {
                        return null;
                    }
                }
                else if (!System.IO.Directory.Exists(git))
                {
                    continue;
                }

                // commondir usually contains "../..". HEAD belongs to the individual checkout.
                // https://git-scm.com/docs/gitrepository-layout
                var commonFile = Path.Combine(git, "commondir");
                var common = File.Exists(commonFile) ? Path.GetFullPath(ReadMetadata(commonFile), git) : git;
                if (!System.IO.Directory.Exists(common) || !File.Exists(Path.Combine(common, "HEAD")))
                {
                    return null;
                }
                var headFile = Path.Combine(git, "HEAD");
                if (!File.Exists(headFile))
                {
                    return null;
                }
                var head = ReadMetadata(headFile);
                const string BranchPrefix = "ref: refs/heads/";
                var label = head.StartsWith(BranchPrefix, StringComparison.Ordinal) ? head[BranchPrefix.Length..] : Path.GetFileName(root);
                if (string.IsNullOrWhiteSpace(label))
                {
                    label = Path.GetFileName(root);
                }
                var relative = Path.GetRelativePath(root, appHostPath);
                common = Path.TrimEndingDirectorySeparator(Path.GetFullPath(common));
                // Match the platform's path comparison without making branch labels case-insensitive.
                var key = OperatingSystem.IsWindows()
                    ? new TrayApplicationKey(common.ToUpperInvariant(), relative.ToUpperInvariant())
                    : new TrayApplicationKey(common, relative);
                // Ordinary repositories store metadata in <checkout>/.git; bare repositories
                // commonly use <name>.git directly, with no main checkout. Use a synthetic
                // <name> root for presentation so the parent directory does not name the app.
                // https://git-scm.com/docs/git-worktree
                var commonName = Path.GetFileName(common);
                var canonicalRoot = commonName.Equals(".git", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetDirectoryName(common) ?? root
                    : commonName.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
                        ? common[..^4] : common;
                var canonicalPath = Path.Combine(canonicalRoot, relative);
                return new(key, root, label.ReplaceLineEndings(" ").Replace('\t', ' '), canonicalPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            // Missing or unreadable Git metadata must never hide an AppHost or guess a group.
        }
        return null;
    }

    private static string ReadMetadata(string path)
    {
        // Git replaces refs by renaming a lock file over them. Presentation reads must
        // permit that replacement, including on Windows, rather than blocking git switch.
        using var stream = OpenMetadata(path);
        using var reader = new StreamReader(stream);
        var buffer = new char[4097];
        var length = reader.ReadBlock(buffer, 0, buffer.Length);
        return length <= 4096 ? new string(buffer, 0, length).Trim() : throw new IOException("Git metadata exceeds 4096 characters.");
    }

    internal static FileStream OpenMetadata(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}
