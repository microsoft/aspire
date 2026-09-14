// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;

namespace Aspire.Cli.Agents;

/// <summary>
/// Applies the catalog's additive or managed-directory file installation policy.
/// </summary>
internal sealed class AgentAssetFileInstaller
{
    private readonly bool _manageDirectory;

    private AgentAssetFileInstaller(bool manageDirectory)
    {
        _manageDirectory = manageDirectory;
    }

    /// <summary>
    /// Adds or updates supplied files in place without removing user-authored files.
    /// </summary>
    public static AgentAssetFileInstaller Additive { get; } = new(manageDirectory: false);

    /// <summary>
    /// Updates package-owned files and removes stale files after successful writes.
    /// </summary>
    public static AgentAssetFileInstaller ManagedDirectory { get; } = new(manageDirectory: true);

    /// <summary>
    /// Installs an asset's files, returning whether any files were updated or removed.
    /// </summary>
    public Task<bool> InstallAsync(
        DirectoryInfo rootDirectory,
        string relativeAssetDirectory,
        string assetName,
        IReadOnlyList<AgentAssetFile> files,
        CancellationToken cancellationToken)
        => _manageDirectory
            ? SynchronizeFilesAsync(rootDirectory, relativeAssetDirectory, assetName, files, cancellationToken)
            : WriteFilesAsync(
                files.Select(file => (Path.Combine(rootDirectory.FullName, relativeAssetDirectory, assetName, file.RelativePath), file)),
                managedRoot: null,
                cancellationToken);

    private static async Task<bool> WriteFilesAsync(
        IEnumerable<(string DestinationPath, AgentAssetFile File)> files,
        string? managedRoot,
        CancellationToken cancellationToken)
    {
        var hasChanges = false;
        foreach (var (destinationPath, file) in files)
        {
            var directory = GetParentDirectory(destinationPath);
            bool exists;
            if (managedRoot is not null)
            {
                ValidateOrCreateDirectory(managedRoot, directory, createMissing: true);
                exists = ValidateDestinationFile(destinationPath);
            }
            else
            {
                // Skills retain their existing in-place behavior, including following links.
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                exists = File.Exists(destinationPath);
            }

            if (exists)
            {
                var existingContent = await File.ReadAllBytesAsync(destinationPath, cancellationToken);
                if (file.ContentEquals(existingContent))
                {
                    continue;
                }
            }

            await File.WriteAllBytesAsync(destinationPath, file.Bytes, cancellationToken);
            hasChanges = true;
        }

        return hasChanges;
    }

    private static async Task<bool> SynchronizeFilesAsync(
        DirectoryInfo rootDirectory,
        string relativeAssetDirectory,
        string assetName,
        IReadOnlyList<AgentAssetFile> files,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        assetName = NormalizeRelativePath(assetName);
        if (assetName.Contains(Path.DirectorySeparatorChar))
        {
            throw new InvalidOperationException($"Agent asset name '{assetName}' must be a single directory name.");
        }

        relativeAssetDirectory = NormalizeRelativePath(relativeAssetDirectory);
        var rootPath = rootDirectory.FullName;
        var assetPath = Path.Combine(rootPath, relativeAssetDirectory, assetName);
        ValidateOrCreateDirectory(rootPath, assetPath, createMissing: false);

        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var filesToInstall = new List<(string DestinationPath, AgentAssetFile File)>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destinationPath = Path.Combine(assetPath, NormalizeRelativePath(file.RelativePath));
            if (!destinations.Add(destinationPath))
            {
                throw new InvalidOperationException($"Agent asset file '{file.RelativePath}' has a duplicate destination.");
            }

            ValidateOrCreateDirectory(rootPath, GetParentDirectory(destinationPath), createMissing: false);
            ValidateDestinationFile(destinationPath);
            filesToInstall.Add((destinationPath, file));
        }

        // Inspect the owned tree before writing so an existing link cannot redirect writes
        // or cleanup outside this asset. Do not prune old files if a payload write fails.
        var existingFiles = Directory.Exists(assetPath) ? EnumerateManagedFiles(assetPath).ToArray() : [];
        var hasChanges = await WriteFilesAsync(filesToInstall, rootPath, cancellationToken);

        foreach (var path in existingFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Resolve only differently cased matches, after writes ensure the expected file
            // exists. An OS-wide case comparer cannot distinguish case-sensitive macOS volumes.
            if (destinations.TryGetValue(path, out var expectedPath) &&
                (string.Equals(path, expectedPath, StringComparison.Ordinal) ||
                 string.Equals(PathNormalizer.ResolvePathCasing(path), PathNormalizer.ResolvePathCasing(expectedPath), StringComparison.Ordinal)))
            {
                continue;
            }

            ValidateOrCreateDirectory(rootPath, GetParentDirectory(path), createMissing: false);
            if (ValidateDestinationFile(path))
            {
                File.Delete(path);
                hasChanges = true;
            }
        }

        return hasChanges;
    }

    private static string NormalizeRelativePath(string path)
    {
        // Bundle paths can use "ui/icon.bin" or "ui\icon.bin". Reject traversal and
        // Windows aliases such as "ui/../index.js", "C:\index.js", and "index.js:stream".
        var segments = path.Split(['/', '\\']);
        if (Path.IsPathRooted(path) ||
            segments.Any(segment => string.IsNullOrWhiteSpace(segment) ||
                segment is "." or ".." ||
                segment.Contains(':') ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                (OperatingSystem.IsWindows() && (segment.EndsWith('.') || segment.EndsWith(' ')))))
        {
            throw new InvalidOperationException($"Agent asset path '{path}' must be a safe relative path.");
        }

        return Path.Combine(segments);
    }

    private static string GetParentDirectory(string path)
        => Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Agent asset path '{path}' does not have a parent directory.");

    private static void ValidateOrCreateDirectory(string rootPath, string directoryPath, bool createMissing)
    {
        var relativePath = Path.GetRelativePath(rootPath, directoryPath);
        if (Path.IsPathRooted(relativePath) ||
            relativePath == ".." ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Agent asset destination '{directoryPath}' is outside the installation root.");
        }

        var currentPath = rootPath;
        ValidateDirectory(currentPath, createMissing);
        if (relativePath == ".")
        {
            return;
        }

        foreach (var segment in relativePath.Split(Path.DirectorySeparatorChar))
        {
            currentPath = Path.Combine(currentPath, segment);
            ValidateDirectory(currentPath, createMissing);
        }
    }

    private static void ValidateDirectory(string path, bool createMissing)
    {
        if (!TryGetAttributes(path, out var attributes))
        {
            if (!createMissing)
            {
                return;
            }

            Directory.CreateDirectory(path);
            attributes = File.GetAttributes(path);
        }

        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException($"Agent asset destination directory '{path}' is a symbolic link or reparse point.");
        }

        if (!attributes.HasFlag(FileAttributes.Directory))
        {
            throw new IOException($"Agent asset destination directory '{path}' is a file.");
        }
    }

    private static bool ValidateDestinationFile(string path)
    {
        if (!TryGetAttributes(path, out var attributes))
        {
            return false;
        }

        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException($"Agent asset destination '{path}' is a symbolic link or reparse point.");
        }

        if (attributes.HasFlag(FileAttributes.Directory))
        {
            throw new IOException($"Agent asset destination '{path}' is a directory.");
        }

        return true;
    }

    private static IEnumerable<string> EnumerateManagedFiles(string directoryPath)
    {
        foreach (var entryPath in Directory.EnumerateFileSystemEntries(directoryPath))
        {
            var attributes = File.GetAttributes(entryPath);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException($"Agent asset entry '{entryPath}' is a symbolic link or reparse point.");
            }

            if (attributes.HasFlag(FileAttributes.Directory))
            {
                foreach (var path in EnumerateManagedFiles(entryPath))
                {
                    yield return path;
                }
            }
            else
            {
                yield return entryPath;
            }
        }
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }
}
