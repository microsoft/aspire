// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.IO.Hashing;
using System.Text;
using Aspire.Hosting.Utils;

namespace Aspire.Cli.Agents;

/// <summary>
/// Serializes file-backed agent asset writers with staged writes and rollback on publication failure.
/// </summary>
internal sealed class AgentAssetFileInstaller
{
    private static readonly StringComparer s_pathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static readonly TimeSpan s_leaseRetryDelay = TimeSpan.FromMilliseconds(100);

    private readonly Action<string, HashSet<string>, List<StagedFile>> _collectStaleFiles;

    private AgentAssetFileInstaller(Action<string, HashSet<string>, List<StagedFile>> collectStaleFiles)
    {
        _collectStaleFiles = collectStaleFiles;
    }

    /// <summary>
    /// Adds or updates supplied files without removing user-authored files.
    /// </summary>
    public static AgentAssetFileInstaller Additive { get; } = new(static (_, _, _) => { });

    /// <summary>
    /// Synchronizes package-owned files, including stale-file removals in the same transaction.
    /// </summary>
    public static AgentAssetFileInstaller ManagedDirectory { get; } = new(CollectStaleFiles);

    /// <summary>
    /// Installs an asset's files, returning whether any files were updated or removed.
    /// </summary>
    public async Task<bool> InstallAsync(
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
        var parentPath = Path.Combine(rootPath, relativeAssetDirectory);
        var assetPath = Path.Combine(parentPath, assetName);
        ValidateOrCreateDirectory(rootPath, assetPath, createMissing: false);

        // Resolve ancestors above the installation root as well. Use the resolved path for both
        // the lease and writes so a caller's directory alias cannot redirect a waiting writer.
        rootPath = ResolveDirectoryPath(rootPath);
        parentPath = Path.Combine(rootPath, relativeAssetDirectory);
        assetPath = Path.Combine(parentPath, assetName);

        // Comparison and stale-file planning must happen after acquisition: another revision may
        // have been installed while we waited. Keep the lease through rollback and staging cleanup.
        using var destinationLease = await AcquireDestinationLeaseAsync(assetPath, cancellationToken);
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

            if (Directory.Exists(assetPath))
            {
                ValidateOrCreateDirectory(rootPath, assetPath, createMissing: false);
                _collectStaleFiles(assetPath, expectedPaths, stagedFiles);
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

    /// <summary>
    /// Gets the stable, per-user lease path for an installed asset directory.
    /// </summary>
    internal static string GetDestinationLeasePath(string assetPath)
    {
        var homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!Path.IsPathFullyQualified(homePath))
        {
            throw new IOException("The user profile directory is required to coordinate agent asset installation.");
        }

        // Like CLI backchannels, leases are shared by installations under the user's profile,
        // not a particular ASPIRE_HOME, worktree, or bundle version. Do not put them in the
        // cache (which can be cleared) or the managed payload (which can remove stale files).
        var leaseDirectory = ResolveDirectoryPath(Path.Combine(homePath, ".aspire", "cli", "agent-asset-locks"));
        var assetIdentity = GetDirectoryIdentity(assetPath);
        var leaseDirectoryIdentity = GetDirectoryIdentity(leaseDirectory);
        if (leaseDirectoryIdentity == assetIdentity ||
            leaseDirectoryIdentity.StartsWith(assetIdentity + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Agent asset destination '{assetPath}' contains the installer lease directory.");
        }

        var leaseName = Convert.ToHexString(XxHash3.Hash(Encoding.UTF8.GetBytes(assetIdentity))).ToLowerInvariant();
        return Path.Combine(leaseDirectory, $"{leaseName}.lock");
    }

    private static string ResolveDirectoryPath(string path)
        => PathNormalizer.TryResolveSymlinks(path, out var resolvedPath)
            ? resolvedPath
            : throw new IOException($"Could not resolve agent asset directory '{path}'.");

    private static string GetDirectoryIdentity(string path)
    {
        // Normalize before hashing even when the destination does not exist yet. Folding case
        // and Unicode also covers case-insensitive volumes mounted on Unix; on case-sensitive
        // volumes this can only serialize otherwise independent writers, never split a lease.
        return Path.TrimEndingDirectorySeparator(ResolveDirectoryPath(path))
            .Normalize(NormalizationForm.FormC)
            .ToUpperInvariant();
    }

    private static async Task<FileStream> AcquireDestinationLeaseAsync(string assetPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var leasePath = GetDestinationLeasePath(assetPath);
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
                // Retain the empty file when closing. FileLock uses DeleteOnClose, which can
                // unlink a Unix lock while a waiter opens it and allow a third writer to lock
                // a different inode. An OS handle has no thread affinity across awaits.
                lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
            }
            catch (IOException ex) when (IsLeaseContention(ex))
            {
                await Task.Delay(s_leaseRetryDelay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                ValidateOrCreateDirectory(leaseRoot, leaseDirectory, createMissing: false);
                if (!ValidateDestinationFile(leasePath))
                {
                    throw new IOException($"Agent asset lease '{leasePath}' disappeared during acquisition.");
                }

                if (!OperatingSystem.IsWindows())
                {
                    VerifyExclusiveLease(leasePath);
                }

                cancellationToken.ThrowIfCancellationRequested();
                return lease;
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }
    }

    private static void VerifyExclusiveLease(string leasePath)
    {
        try
        {
            // As in HeldFileLease, fail closed if Unix flock is unsupported or disabled:
            // FileStream can otherwise succeed without actually acquiring an exclusive lock.
            // https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/Microsoft/Win32/SafeHandles/SafeFileHandle.Unix.cs
            using var verification = new FileStream(leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
        }
        catch (IOException ex) when (IsLeaseContention(ex))
        {
            return;
        }

        throw new IOException($"Exclusive file locking is required for agent asset lease '{leasePath}'.");
    }

    private static bool IsLeaseContention(IOException exception)
    {
        // Windows sharing/lock violations are HRESULTs. Unix FileStream exposes EWOULDBLOCK
        // as raw errno (11 on Linux, 35 on macOS). Other I/O and access failures must surface.
        return OperatingSystem.IsWindows()
            ? exception.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021)
            : exception.HResult is 11 or 35;
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
