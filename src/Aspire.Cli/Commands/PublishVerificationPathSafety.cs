// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Git;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Commands;

/// <summary>
/// Validates repository and filesystem boundaries used by publish verification.
/// </summary>
internal static class PublishVerificationPathSafety
{
    internal static StringComparer PathComparer { get; } = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    internal static StringComparison PathComparison { get; } = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public static async Task ValidateDestinationAsync(
        string destinationPath,
        DirectoryInfo repositoryRoot,
        IGitRepository gitRepository,
        CancellationToken cancellationToken)
    {
        var fullRepositoryRoot = Path.GetFullPath(repositoryRoot.FullName);
        var fullDestinationPath = Path.GetFullPath(destinationPath);
        if (!IsWithinRoot(fullRepositoryRoot, fullDestinationPath))
        {
            throw new PublishVerificationException(PublishCommandStrings.VerifyDestinationOutsideRepository);
        }

        var resolvedRepositoryRoot = ResolveExistingPath(fullRepositoryRoot, fullRepositoryRoot);
        var resolvedDestinationPath = ResolveExistingPath(fullDestinationPath, fullRepositoryRoot);
        if (!IsWithinRoot(resolvedRepositoryRoot, resolvedDestinationPath))
        {
            throw new PublishVerificationException(PublishCommandStrings.VerifyDestinationLinkEscape);
        }

        var nearestExistingDirectory = FindNearestExistingDirectory(fullDestinationPath);
        var containingRepository = await gitRepository.GetRootAsync(
            nearestExistingDirectory,
            cancellationToken).ConfigureAwait(false);
        if (containingRepository is null)
        {
            throw new PublishVerificationException(PublishCommandStrings.VerifyGitQueryFailed);
        }

        if (!PathEquals(containingRepository.FullName, fullRepositoryRoot))
        {
            throw new PublishVerificationException(PublishCommandStrings.VerifyNestedRepository);
        }

        if (Directory.Exists(fullDestinationPath))
        {
            ValidateExistingTree(fullDestinationPath, fullRepositoryRoot, rejectGitMetadata: true);
        }
        else if (File.Exists(fullDestinationPath))
        {
            ValidateExistingEntry(new FileInfo(fullDestinationPath), fullRepositoryRoot, rejectGitMetadata: true);
        }
    }

    public static void ValidateGeneratedTree(string outputPath)
    {
        if (Directory.Exists(outputPath))
        {
            ValidateExistingTree(outputPath, outputPath, rejectGitMetadata: true);
        }
        else if (File.Exists(outputPath))
        {
            ValidateExistingEntry(new FileInfo(outputPath), outputPath, rejectGitMetadata: true);
        }
    }

    public static bool IsWithinRoot(string rootPath, string path)
    {
        var fullRootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var fullPath = Path.GetFullPath(path);
        return PathEquals(fullRootPath, fullPath) ||
            fullPath.StartsWith(fullRootPath + Path.DirectorySeparatorChar, PathComparison);
    }

    public static bool PathEquals(string first, string second)
    {
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            PathComparison);
    }

    public static bool PathsOverlap(string first, string second)
    {
        return IsWithinRoot(first, second) || IsWithinRoot(second, first);
    }

    private static string ResolveExistingPath(string path, string repositoryRoot)
    {
        var root = Path.GetPathRoot(path)
            ?? throw new PublishVerificationException(PublishCommandStrings.VerifyInvalidDestinationPath);
        var relativePath = Path.GetRelativePath(root, path);
        var currentPath = root;

        foreach (var segment in relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            var candidatePath = Path.Combine(currentPath, segment);
            FileSystemInfo? entry = Directory.Exists(candidatePath)
                ? new DirectoryInfo(candidatePath)
                : File.Exists(candidatePath)
                    ? new FileInfo(candidatePath)
                    : null;
            if (entry is null)
            {
                currentPath = candidatePath;
                continue;
            }

            entry.Refresh();
            if ((entry.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                currentPath = candidatePath;
                continue;
            }

            var resolvedTarget = entry.ResolveLinkTarget(returnFinalTarget: true)
                ?? throw new PublishVerificationException(PublishCommandStrings.VerifyDestinationLinkEscape);
            currentPath = Path.GetFullPath(resolvedTarget.FullName);
            if (!IsWithinRoot(repositoryRoot, currentPath))
            {
                throw new PublishVerificationException(PublishCommandStrings.VerifyDestinationLinkEscape);
            }
        }

        return Path.GetFullPath(currentPath);
    }

    private static DirectoryInfo FindNearestExistingDirectory(string path)
    {
        var candidate = Directory.Exists(path)
            ? new DirectoryInfo(path)
            : new FileInfo(path).Directory;

        while (candidate is not null && !candidate.Exists)
        {
            candidate = candidate.Parent;
        }

        return candidate
            ?? throw new PublishVerificationException(PublishCommandStrings.VerifyInvalidDestinationPath);
    }

    private static void ValidateExistingTree(
        string treeRoot,
        string repositoryRoot,
        bool rejectGitMetadata)
    {
        var directories = new Stack<DirectoryInfo>();
        var rootDirectory = new DirectoryInfo(treeRoot);
        ValidateExistingEntry(rootDirectory, repositoryRoot, rejectGitMetadata);
        directories.Push(rootDirectory);

        while (directories.TryPop(out var directory))
        {
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                ValidateExistingEntry(entry, repositoryRoot, rejectGitMetadata);
                if (entry is DirectoryInfo childDirectory &&
                    (entry.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    directories.Push(childDirectory);
                }
            }
        }
    }

    private static void ValidateExistingEntry(
        FileSystemInfo entry,
        string repositoryRoot,
        bool rejectGitMetadata)
    {
        entry.Refresh();
        if (rejectGitMetadata &&
            entry.Name.Equals(".git", StringComparison.OrdinalIgnoreCase))
        {
            throw new PublishVerificationException(PublishCommandStrings.VerifyNestedRepository);
        }

        if ((entry.Attributes & FileAttributes.ReparsePoint) == 0)
        {
            return;
        }

        var resolvedTarget = entry.ResolveLinkTarget(returnFinalTarget: true)
            ?? throw new PublishVerificationException(PublishCommandStrings.VerifyDestinationLinkEscape);
        if (!IsWithinRoot(repositoryRoot, resolvedTarget.FullName))
        {
            throw new PublishVerificationException(PublishCommandStrings.VerifyDestinationLinkEscape);
        }

        // Git stores a symbolic link as its link text while File.ReadAllBytes follows the link.
        // Reject links even when they remain inside the repository rather than silently comparing
        // different representations or traversing a mutable junction during verification.
        throw new PublishVerificationException(PublishCommandStrings.VerifyLinkedOutputPath);
    }
}
