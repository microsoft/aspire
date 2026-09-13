// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Hashing;
using System.Text;
using Aspire.Cli.Agents.AspireSkills;
using Aspire.Hosting.Utils;

namespace Aspire.Cli.Agents;

/// <summary>
/// Serializes skill text file installation and restores originals if publication fails.
/// </summary>
internal sealed class SkillFileInstaller(Action<string, string> moveFile)
{
    private static readonly StringComparer s_pathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static readonly TimeSpan s_leaseRetryDelay = TimeSpan.FromMilliseconds(100);

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

        // Only ancestors above the installation root may be aliases. Keep the root itself
        // unresolved so a newly introduced root link still fails validation after the wait.
        if (Path.GetDirectoryName(rootPath) is { } rootParent)
        {
            rootPath = Path.Combine(ResolveDirectoryPath(rootParent), Path.GetFileName(rootPath));
        }

        parentPath = Path.Combine(rootPath, relativeSkillDirectory);
        skillPath = Path.Combine(parentPath, skillName);

        // Use resolved paths for both writes and the lease, so retargeting an ancestor alias
        // cannot redirect a waiting installer. Enumerate and compare only after acquisition:
        // another process may have changed initially equal files while we waited.
        using var destinationLease = await AcquireDestinationLeaseAsync(skillPath, cancellationToken);
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

    /// <summary>
    /// Gets the stable, per-user lease path for an installed skill directory.
    /// </summary>
    internal static string GetDestinationLeasePath(string skillPath)
        => GetDestinationLeasePath(skillPath, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>
    /// Gets a destination lease path under the supplied user profile.
    /// </summary>
    internal static string GetDestinationLeasePath(string skillPath, string userProfilePath)
    {
        if (!Path.IsPathFullyQualified(userProfilePath))
        {
            throw new IOException("The user profile directory is required to coordinate skill installation.");
        }

        var homePath = ResolveDirectoryPath(userProfilePath);
        if (!TryGetAttributes(homePath, out _))
        {
            throw new IOException($"The user profile directory '{userProfilePath}' does not exist.");
        }

        // These locks belong to the actual user profile, not ASPIRE_HOME, a workspace cache,
        // a bundle version, or the installed payload. Resolve only the profile's aliases:
        // resolving the entire lease directory would hide links in .aspire/cli/agent-asset-locks.
        var leaseDirectory = Path.Combine(homePath, ".aspire", "cli", "agent-asset-locks");
        ValidateOrCreateDirectory(homePath, leaseDirectory, createMissing: false);
        var skillIdentity = GetDirectoryIdentity(skillPath);
        var leaseDirectoryIdentity = GetDirectoryIdentity(leaseDirectory);
        var skillPrefix = Path.EndsInDirectorySeparator(skillIdentity)
            ? skillIdentity
            : skillIdentity + Path.DirectorySeparatorChar;
        if (leaseDirectoryIdentity == skillIdentity ||
            leaseDirectoryIdentity.StartsWith(skillPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Skill destination '{skillPath}' contains the installer lease directory.");
        }

        var leaseName = Convert.ToHexString(XxHash3.Hash(Encoding.UTF8.GetBytes(skillIdentity))).ToLowerInvariant();
        return Path.Combine(leaseDirectory, $"{leaseName}.lock");
    }

    private static string ResolveDirectoryPath(string path)
        => PathNormalizer.TryResolveSymlinks(path, out var resolvedPath)
            ? resolvedPath
            : throw new IOException($"Could not resolve skill installation directory '{path}'.");

    private static string GetDirectoryIdentity(string path)
    {
        // Normalize even missing destinations. Case folding and Unicode Form C also cover
        // case-insensitive Unix volumes; on case-sensitive volumes this may serialize two
        // independent destinations, but never gives aliases of one destination different locks.
        return Path.TrimEndingDirectorySeparator(ResolveDirectoryPath(path))
            .Normalize(NormalizationForm.FormC)
            .ToUpperInvariant();
    }

    private static async Task<FileStream> AcquireDestinationLeaseAsync(string skillPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var leasePath = GetDestinationLeasePath(skillPath);
        var leaseDirectory = GetParentDirectory(leasePath);
        var leaseRoot = Path.GetPathRoot(leasePath)!;
        ValidateOrCreateDirectory(leaseRoot, leaseDirectory, createMissing: true);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateOrCreateDirectory(leaseRoot, leaseDirectory, createMissing: false);
            ValidateDestinationFile(leasePath);

            FileStream lease;
            try
            {
                // Retain the empty file after closing the handle. FileLock's DeleteOnClose
                // can unlink a Unix lock while a waiter opens it, allowing a third writer to
                // lock a different inode. OS handles also have no thread affinity across awaits.
                lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
            }
            catch (IOException ex) when (IsLeaseContention(ex))
            {
                await Task.Delay(s_leaseRetryDelay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var acquired = false;
            try
            {
                ValidateOrCreateDirectory(leaseRoot, leaseDirectory, createMissing: false);
                if (!ValidateDestinationFile(leasePath))
                {
                    throw new IOException($"Skill installation lease '{leasePath}' disappeared during acquisition.");
                }

                if (!OperatingSystem.IsWindows())
                {
                    VerifyExclusiveLease(leasePath);
                }

                cancellationToken.ThrowIfCancellationRequested();
                acquired = true;
                return lease;
            }
            finally
            {
                if (!acquired)
                {
                    lease.Dispose();
                }
            }
        }
    }

    private static void VerifyExclusiveLease(string leasePath)
    {
        try
        {
            // As in HeldFileLease, a second open verifies Unix flock was actually enforced.
            // FileStream can succeed without a lock when flock is unsupported or disabled.
            // https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/Microsoft/Win32/SafeHandles/SafeFileHandle.Unix.cs
            using var verification = new FileStream(leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
        }
        catch (IOException ex) when (IsLeaseContention(ex))
        {
            return;
        }

        throw new IOException($"Exclusive file locking is required for skill installation lease '{leasePath}'.");
    }

    private static bool IsLeaseContention(IOException exception)
    {
        // Windows reports sharing/lock violation HRESULTs. Unix reports raw EWOULDBLOCK:
        // 11 on Linux, 35 on macOS. Do not retry unrelated I/O or permission errors.
        return OperatingSystem.IsWindows()
            ? exception.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021)
            : exception.HResult == (OperatingSystem.IsMacOS() ? 35 : 11);
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
