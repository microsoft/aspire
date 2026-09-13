// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents.AspireSkills;

namespace Aspire.Cli.Agents;

/// <summary>
/// Stages skill text files and restores their originals if publication fails.
/// </summary>
internal sealed class SkillFileInstaller(Action<string, string> moveFile)
{
    private static readonly StringComparer s_pathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    /// <summary>
    /// Gets the installer that publishes files using same-volume renames.
    /// </summary>
    public static SkillFileInstaller Instance { get; } = new(File.Move);

    /// <summary>
    /// Adds or updates a skill's files, returning whether any files changed.
    /// </summary>
    public async Task<bool> InstallAsync(
        DirectoryInfo rootDirectory,
        string relativeSkillDirectory,
        string skillName,
        IEnumerable<SkillAssetFile> files,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        skillName = AspireSkillsBundleProvider.NormalizeRelativePath(skillName);
        if (skillName.Contains(Path.DirectorySeparatorChar))
        {
            throw new InvalidOperationException($"Skill name '{skillName}' must be a single directory name.");
        }

        relativeSkillDirectory = AspireSkillsBundleProvider.NormalizeRelativePath(relativeSkillDirectory);
        var rootPath = Path.TrimEndingDirectorySeparator(rootDirectory.FullName);
        var parentPath = Path.Combine(rootPath, relativeSkillDirectory);
        var skillPath = Path.Combine(parentPath, skillName);
        ValidateOrCreateDirectory(rootPath, skillPath, createMissing: false);

        var destinations = new HashSet<string>(s_pathComparer);
        var stagedFiles = new List<StagedFile>();
        var publishedFiles = new List<PublishedFile>();
        string? stagingPath = null;
        string? backupPath = null;
        Exception? failure = null;
        var rollbackIncomplete = false;

        try
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = AspireSkillsBundleProvider.NormalizeRelativePath(file.RelativePath);
                var destinationPath = Path.Combine(skillPath, relativePath);
                if (!destinations.Add(destinationPath))
                {
                    throw new InvalidOperationException($"Skill file '{file.RelativePath}' has a duplicate destination.");
                }

                ValidateOrCreateDirectory(rootPath, GetParentDirectory(destinationPath), createMissing: false);
                if (ValidateDestinationFile(destinationPath))
                {
                    // Every skill file is text, including scripts and unfamiliar suffixes. Keep
                    // File.ReadAllText's BOM detection and replacement fallback, not strict UTF-8.
                    var existingContent = await File.ReadAllTextAsync(destinationPath, cancellationToken);
                    if (string.Equals(existingContent.ReplaceLineEndings("\n"), file.Content.ReplaceLineEndings("\n"), StringComparison.Ordinal))
                    {
                        continue;
                    }
                }

                stagingPath ??= CreateTransactionDirectory(rootPath, parentPath, skillName, "staging");
                var stagedPath = Path.Combine(stagingPath, relativePath);
                ValidateOrCreateDirectory(stagingPath, GetParentDirectory(stagedPath), createMissing: true);
                await WriteStagedFileAsync(stagedPath, file.Content, cancellationToken);
                stagedFiles.Add(new(destinationPath, stagedPath, relativePath));
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (stagedFiles.Count == 0)
            {
                return false;
            }

            backupPath = CreateTransactionDirectory(rootPath, parentPath, skillName, "rollback");

            // Creating the rollback directory is still preparation. After this last cancellation
            // check, do not interrupt the synchronous renames or any rollback they require.
            cancellationToken.ThrowIfCancellationRequested();
            PublishFiles(rootPath, stagedFiles, backupPath, publishedFiles);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        {
            var rollbackExceptions = RollbackFiles(rootPath, publishedFiles);
            if (rollbackExceptions.Count > 0)
            {
                rollbackIncomplete = true;
                failure = new IOException(
                    $"Skill publication failed and rollback was incomplete. Original files were preserved under '{backupPath}'.",
                    new AggregateException([ex, .. rollbackExceptions]));
                throw failure;
            }

            failure = ex;
            throw;
        }
        finally
        {
            // An incomplete rollback must retain the only recoverable copies of the originals.
            // Also preserve its diagnostic if removing the staged files fails.
            CleanupTransactionDirectories(rootPath, stagingPath, rollbackIncomplete ? null : backupPath, failure);
        }
    }

    private static string GetParentDirectory(string path)
        => Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Skill path '{path}' does not have a parent directory.");

    private static void ValidateOrCreateDirectory(string rootPath, string directoryPath, bool createMissing)
    {
        var relativePath = Path.GetRelativePath(rootPath, directoryPath);
        if (Path.IsPathRooted(relativePath) ||
            relativePath == ".." ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Skill destination '{directoryPath}' is outside the installation root.");
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
            throw new InvalidOperationException($"Skill destination directory '{path}' is a symbolic link or reparse point.");
        }

        if (!attributes.HasFlag(FileAttributes.Directory))
        {
            throw new IOException($"Skill destination directory '{path}' is a file.");
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
            throw new InvalidOperationException($"Skill destination '{path}' is a symbolic link or reparse point.");
        }

        if (attributes.HasFlag(FileAttributes.Directory))
        {
            throw new IOException($"Skill destination '{path}' is a directory.");
        }

        return true;
    }

    private static string CreateTransactionDirectory(string rootPath, string parentPath, string skillName, string purpose)
    {
        ValidateOrCreateDirectory(rootPath, parentPath, createMissing: true);

        // Siblings keep staging and rollback on the destination volume; the system temporary
        // directory could require non-atomic copies. Updates therefore need permission to create
        // sibling directories and rename originals, not just write access to existing files.
        var path = Path.Combine(parentPath, $".{skillName}.{purpose}.{Guid.NewGuid():N}");
        if (TryGetAttributes(path, out _))
        {
            throw new IOException($"Skill transaction directory '{path}' already exists.");
        }

        ValidateDirectory(path, createMissing: true);
        return path;
    }

    private static async Task WriteStagedFileAsync(string path, string content, CancellationToken cancellationToken)
    {
        // CreateNew rejects pre-existing files and final-component symbolic links. Match
        // File.WriteAllText's UTF-8 without a BOM while keeping all source files as text.
        await using var stream = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(content.AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }

    private void PublishFiles(string rootPath, IReadOnlyList<StagedFile> stagedFiles, string backupPath, List<PublishedFile> publishedFiles)
    {
        for (var i = 0; i < stagedFiles.Count; i++)
        {
            var file = stagedFiles[i];
            ValidateOrCreateDirectory(rootPath, GetParentDirectory(file.DestinationPath), createMissing: true);
            ValidateOrCreateDirectory(rootPath, GetParentDirectory(file.StagedPath), createMissing: false);
            if (!ValidateDestinationFile(file.StagedPath))
            {
                throw new IOException($"Staged skill file '{file.StagedPath}' disappeared before publication.");
            }

            var destinationExists = ValidateDestinationFile(file.DestinationPath);
            if (destinationExists)
            {
                // Preserve relative names so an incomplete rollback leaves originals that can
                // be recovered without reconstructing the transaction's file ordering.
                var originalPath = Path.Combine(backupPath, file.RelativePath);
                ValidateOrCreateDirectory(rootPath, GetParentDirectory(originalPath), createMissing: true);
                moveFile(file.DestinationPath, originalPath);
                publishedFiles.Add(new(file.DestinationPath, originalPath));
            }

            moveFile(file.StagedPath, file.DestinationPath);
            if (!destinationExists)
            {
                publishedFiles.Add(new(file.DestinationPath, BackupPath: null));
            }
        }
    }

    private List<Exception> RollbackFiles(string rootPath, List<PublishedFile> publishedFiles)
    {
        var exceptions = new List<Exception>();
        for (var i = publishedFiles.Count - 1; i >= 0; i--)
        {
            var file = publishedFiles[i];
            try
            {
                ValidateOrCreateDirectory(rootPath, GetParentDirectory(file.DestinationPath), createMissing: true);
                if (file.BackupPath is not null)
                {
                    ValidateOrCreateDirectory(rootPath, GetParentDirectory(file.BackupPath), createMissing: false);
                    if (!ValidateDestinationFile(file.BackupPath))
                    {
                        throw new IOException($"Skill rollback file '{file.BackupPath}' disappeared.");
                    }
                }

                if (TryGetAttributes(file.DestinationPath, out var attributes))
                {
                    if (attributes.HasFlag(FileAttributes.Directory))
                    {
                        throw new IOException($"Skill rollback destination '{file.DestinationPath}' is a directory.");
                    }

                    // Deleting a final-component symlink removes the link, not its target.
                    File.Delete(file.DestinationPath);
                }

                if (file.BackupPath is not null)
                {
                    moveFile(file.BackupPath, file.DestinationPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                exceptions.Add(ex);
            }
        }

        return exceptions;
    }

    private static void CleanupTransactionDirectories(string rootPath, string? stagingPath, string? backupPath, Exception? failure)
    {
        var cleanupExceptions = new List<Exception>();
        foreach (var path in new[] { stagingPath, backupPath })
        {
            try
            {
                if (path is not null && TryGetAttributes(path, out _))
                {
                    ValidateOrCreateDirectory(rootPath, path, createMissing: false);
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                cleanupExceptions.Add(new IOException($"Could not remove skill transaction directory '{path}'.", ex));
            }
        }

        if (cleanupExceptions.Count > 0)
        {
            var cleanupDetails = string.Join(" ", cleanupExceptions.Select(static ex => ex.Message));
            if (failure is not null)
            {
                cleanupExceptions.Insert(0, failure);
            }

            throw new IOException(
                $"{failure?.Message} Skill transaction cleanup failed. {cleanupDetails}".TrimStart(),
                new AggregateException(cleanupExceptions));
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

    private sealed record StagedFile(string DestinationPath, string StagedPath, string RelativePath);

    private sealed record PublishedFile(string DestinationPath, string? BackupPath);
}
