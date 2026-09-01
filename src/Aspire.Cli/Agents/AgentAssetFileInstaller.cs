// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;

namespace Aspire.Cli.Agents;

/// <summary>
/// Installs file-backed agent assets with staged writes and rollback on publication failure.
/// </summary>
internal static class AgentAssetFileInstaller
{
    // Match Windows and default macOS volume behavior. Case-sensitive macOS volumes
    // conservatively retain differently cased files rather than deleting a possible alias.
    private static readonly StringComparer s_pathComparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    /// <summary>
    /// Installs an asset's files, returning whether any files were updated or removed.
    /// </summary>
    public static async Task<bool> InstallAsync(
        DirectoryInfo rootDirectory,
        string relativeAssetDirectory,
        AgentAssetDefinition asset,
        IReadOnlyList<AgentAssetFile> files,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var assetName = NormalizeRelativePath(asset.Name);
        if (assetName.Contains(Path.DirectorySeparatorChar))
        {
            throw new InvalidOperationException($"Agent asset name '{asset.Name}' must be a single directory name.");
        }

        var rootPath = rootDirectory.FullName;
        var parentPath = Path.Combine(rootPath, NormalizeRelativePath(relativeAssetDirectory));
        var assetPath = Path.Combine(parentPath, assetName);
        ValidateOrCreateDirectory(rootPath, assetPath, createMissing: false);

        var expectedPaths = new HashSet<string>(s_pathComparer);
        var stagedFiles = new List<StagedFile>();
        string? stagingPath = null;

        try
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = NormalizeRelativePath(file.RelativePath);
                var destinationPath = Path.Combine(assetPath, relativePath);
                if (!expectedPaths.Add(destinationPath))
                {
                    throw new InvalidOperationException($"Agent asset file '{file.RelativePath}' has a duplicate destination.");
                }

                ValidateOrCreateDirectory(rootPath, GetParentDirectory(destinationPath), createMissing: false);
                if (ValidateDestinationFile(destinationPath))
                {
                    var existingContent = await File.ReadAllBytesAsync(destinationPath, cancellationToken);
                    if (file.ContentEquals(existingContent))
                    {
                        continue;
                    }
                }

                stagingPath ??= CreateTransactionDirectory(rootPath, parentPath, assetName, "staging");
                var stagedPath = Path.Combine(stagingPath, relativePath);
                ValidateOrCreateDirectory(stagingPath, GetParentDirectory(stagedPath), createMissing: true);
                await WriteStagedFileAsync(stagedPath, file.Bytes, cancellationToken);
                stagedFiles.Add(new(destinationPath, stagedPath));
            }

            if (asset.AssetKind is AgentAssetKind.Extension && Directory.Exists(assetPath))
            {
                // Extension directories are package-owned executable trees. Keeping old files
                // can leave an incompatible mix of JavaScript/UI versions. Skills stay additive.
                ValidateOrCreateDirectory(rootPath, assetPath, createMissing: false);
                CollectStaleFiles(assetPath, expectedPaths, stagedFiles);
            }

            if (stagedFiles.Count == 0)
            {
                return false;
            }

            // Cancellation is honored before publication, not between its synchronous renames.
            // Once publication starts, either finish or restore the previous files.
            cancellationToken.ThrowIfCancellationRequested();
            var backupPath = CreateTransactionDirectory(rootPath, parentPath, assetName, "rollback");
            PublishFiles(rootPath, stagedFiles, backupPath);
            return true;
        }
        finally
        {
            DeleteTransactionDirectory(stagingPath);
        }
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

    private static string CreateTransactionDirectory(string rootPath, string parentPath, string assetName, string purpose)
    {
        ValidateOrCreateDirectory(rootPath, parentPath, createMissing: true);

        // Sibling directories keep staged files and backups on the destination volume, allowing
        // atomic renames. A system temporary directory could require non-atomic cross-volume copies.
        var path = Path.Combine(parentPath, $".{assetName}.{purpose}.{Guid.NewGuid():N}");
        if (TryGetAttributes(path, out _))
        {
            throw new IOException($"Agent asset transaction directory '{path}' already exists.");
        }

        ValidateDirectory(path, createMissing: true);
        return path;
    }

    private static async Task WriteStagedFileAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        // CreateNew rejects existing files and pre-planted final-component symbolic links.
        await using var stream = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);
        await stream.WriteAsync(content, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static void PublishFiles(string rootPath, IReadOnlyList<StagedFile> stagedFiles, string backupPath)
    {
        var publishedFiles = new List<PublishedFile>();
        try
        {
            for (var i = 0; i < stagedFiles.Count; i++)
            {
                var file = stagedFiles[i];
                ValidateOrCreateDirectory(rootPath, GetParentDirectory(file.DestinationPath), createMissing: true);
                var destinationExists = ValidateDestinationFile(file.DestinationPath);
                if (destinationExists)
                {
                    var originalPath = Path.Combine(backupPath, i.ToString(CultureInfo.InvariantCulture));
                    File.Move(file.DestinationPath, originalPath);
                    publishedFiles.Add(new(file.DestinationPath, originalPath));
                }

                // A null staged path removes a stale file by moving it into the rollback directory.
                if (file.StagedPath is not null)
                {
                    File.Move(file.StagedPath, file.DestinationPath);
                    if (!destinationExists)
                    {
                        publishedFiles.Add(new(file.DestinationPath, BackupPath: null));
                    }
                }
            }
        }
        catch (Exception publishException) when (publishException is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var rollbackExceptions = RollbackFiles(rootPath, publishedFiles);
            if (rollbackExceptions.Count > 0)
            {
                // Never clean up the backup directory if rollback failed: it contains the
                // original files the user needs to recover from the incomplete installation.
                throw new AggregateException(
                    $"Agent asset publication failed and rollback was incomplete. Original files were preserved under '{backupPath}'.",
                    [publishException, .. rollbackExceptions]);
            }

            DeleteTransactionDirectory(backupPath);
            throw;
        }

        DeleteTransactionDirectory(backupPath);
    }

    private static List<Exception> RollbackFiles(string rootPath, List<PublishedFile> publishedFiles)
    {
        var exceptions = new List<Exception>();
        for (var i = publishedFiles.Count - 1; i >= 0; i--)
        {
            var file = publishedFiles[i];
            try
            {
                ValidateOrCreateDirectory(rootPath, GetParentDirectory(file.DestinationPath), createMissing: true);
                if (TryGetAttributes(file.DestinationPath, out var attributes))
                {
                    if (attributes.HasFlag(FileAttributes.Directory))
                    {
                        throw new IOException($"Agent asset rollback destination '{file.DestinationPath}' is a directory.");
                    }

                    // Deleting a final-component symlink removes the link, not its target.
                    File.Delete(file.DestinationPath);
                }

                if (file.BackupPath is not null)
                {
                    File.Move(file.BackupPath, file.DestinationPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                exceptions.Add(ex);
            }
        }

        return exceptions;
    }

    private static void CollectStaleFiles(string directoryPath, HashSet<string> expectedPaths, List<StagedFile> stagedFiles)
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
                CollectStaleFiles(entryPath, expectedPaths, stagedFiles);
            }
            else if (!expectedPaths.Contains(entryPath))
            {
                stagedFiles.Add(new(entryPath, StagedPath: null));
            }
        }
    }

    private static void DeleteTransactionDirectory(string? path)
    {
        if (path is null || !TryGetAttributes(path, out var attributes))
        {
            return;
        }

        if (attributes.HasFlag(FileAttributes.ReparsePoint) || !attributes.HasFlag(FileAttributes.Directory))
        {
            throw new InvalidOperationException($"Agent asset transaction directory '{path}' changed unexpectedly.");
        }

        Directory.Delete(path, recursive: true);
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

    private sealed record StagedFile(string DestinationPath, string? StagedPath);

    private sealed record PublishedFile(string DestinationPath, string? BackupPath);
}
