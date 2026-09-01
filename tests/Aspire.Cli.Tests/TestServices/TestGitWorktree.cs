// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Tests.TestServices;

internal static class TestGitWorktree
{
    public static string WriteLinkedWorktreeMetadata(
        string worktreeRoot,
        string commonGitDirectory,
        string worktreeName = "feature",
        bool useRelativePaths = false)
    {
        var adminDirectory = Path.Combine(Path.GetFullPath(commonGitDirectory), "worktrees", worktreeName);
        Directory.CreateDirectory(adminDirectory);

        var gitFilePath = WriteGitDirFile(worktreeRoot, adminDirectory, useRelativePaths);
        var backPointer = useRelativePaths
            ? Path.GetRelativePath(adminDirectory, gitFilePath)
            : gitFilePath;
        File.WriteAllText(Path.Combine(adminDirectory, "gitdir"), backPointer + Environment.NewLine);

        return adminDirectory;
    }

    public static string WriteGitDirFile(string checkoutRoot, string gitDirectory, bool useRelativePath = false)
    {
        var fullCheckoutRoot = Path.GetFullPath(checkoutRoot);
        var fullGitDirectory = Path.GetFullPath(gitDirectory);
        Directory.CreateDirectory(fullCheckoutRoot);
        Directory.CreateDirectory(fullGitDirectory);

        var gitFilePath = Path.Combine(fullCheckoutRoot, ".git");
        var pointer = useRelativePath
            ? Path.GetRelativePath(fullCheckoutRoot, fullGitDirectory)
            : fullGitDirectory;
        File.WriteAllText(gitFilePath, $"gitdir: {pointer}{Environment.NewLine}");

        return gitFilePath;
    }
}
