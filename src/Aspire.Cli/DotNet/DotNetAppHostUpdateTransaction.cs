// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Hosting.Utils;

namespace Aspire.Cli.DotNet;

/// <summary>
/// Tracks in-place candidate edits and restores owned files when an update fails.
/// </summary>
internal sealed class DotNetAppHostUpdateTransaction : IAsyncDisposable
{
    private readonly Dictionary<string, FileState> _files;
    private bool _committed;
    private bool _disposed;

    internal DotNetAppHostUpdateTransaction(IReadOnlyDictionary<string, byte[]?> originalFiles)
    {
        _files = originalFiles.ToDictionary(
            static pair => pair.Key,
            static pair => new FileState(pair.Value),
            StringComparers.FileSystemPath);
    }

    internal async Task PrepareBackupsAsync(
        IEnumerable<FileInfo> files, IInteractionService interactionService, bool supportsInteractiveInput, CancellationToken cancellationToken)
    {
        var paths = files.Select(static file => PathNormalizer.ResolveToFilesystemPath(file.FullName))
            .Distinct(StringComparers.FileSystemPath).ToArray();
        var existingBackups = paths.SelectMany(static path => new[] { path + ".aspire-backup", path + ".aspire-backup-absent" })
            .Where(File.Exists).ToArray();
        if (existingBackups.Length > 0)
        {
            interactionService.DisplayPlainText(string.Format(
                CultureInfo.CurrentCulture, UpdateCommandStrings.DryRunExistingBackupsFormat,
                string.Join(Environment.NewLine, existingBackups)));
            // Deliberately independent of --yes: replacing these files discards recovery data.
            if (!supportsInteractiveInput || !await interactionService.PromptConfirmAsync(
                UpdateCommandStrings.DryRunReplaceBackupsPrompt, PromptBinding.CreateDefault(false), cancellationToken))
            {
                throw new ProjectUpdaterException(UpdateCommandStrings.DryRunBackupsNotReplaced);
            }
        }

        await VerifyAsync(cancellationToken);
        foreach (var path in paths)
        {
            var state = _files[path];
            var backup = path + (state.Original is null ? ".aspire-backup-absent" : ".aspire-backup");
            var temporary = backup + "." + Guid.NewGuid().ToString("N");
            try
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.Asynchronous
                };
                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                }
                await using (var stream = new FileStream(temporary, options))
                {
                    if (state.Original is not null)
                    {
                        await stream.WriteAsync(state.Original, cancellationToken);
                    }
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(flushToDisk: true);
                }
                // A backup is visible only after its complete contents have been written.
                File.Move(temporary, backup, overwrite: existingBackups.Contains(backup, StringComparers.FileSystemPath));
                state.BackupPath = backup;
                var otherBackup = path + (state.Original is null ? ".aspire-backup" : ".aspire-backup-absent");
                if (existingBackups.Contains(otherBackup, StringComparers.FileSystemPath))
                {
                    File.Delete(otherBackup);
                }
            }
            finally
            {
                File.Delete(temporary);
            }
        }
    }

    internal async Task ApplyAsync(
        IEnumerable<FileInfo> files, Func<Task> action, CancellationToken cancellationToken)
    {
        var paths = files.Select(static file => PathNormalizer.ResolveToFilesystemPath(file.FullName))
            .Distinct(StringComparers.FileSystemPath).ToArray();
        await VerifyAsync(cancellationToken);
        foreach (var path in paths)
        {
            if (!_files.ContainsKey(path))
            {
                throw new InvalidOperationException($"Update file '{path}' was not included in the candidate.");
            }
        }

        try
        {
            await action();
        }
        finally
        {
            // Native package-add and SDK migration can edit a file before reporting failure.
            // Record those partial edits too, without letting cancellation prevent rollback.
            foreach (var path in paths)
            {
                _files[path].Expected = await ReadAsync(path, CancellationToken.None);
                _files[path].Edited = true;
            }
        }
    }

    internal async Task ApplyPreparedAsync(
        FileInfo file, ReadOnlyMemory<byte> proposedContent, Func<Action, Task> action, CancellationToken cancellationToken)
    {
        await VerifyAsync(cancellationToken);
        var path = PathNormalizer.ResolveToFilesystemPath(file.FullName);
        var state = _files[path];
        var writeStarted = false;
        var succeeded = false;
        try
        {
            await action(() => writeStarted = true);
            succeeded = true;
        }
        finally
        {
            // A stale-baseline or CreateNew failure did not write our candidate. Never claim
            // ownership of the other writer's file and subsequently delete or roll it back.
            if (writeStarted)
            {
                state.Expected = succeeded ? proposedContent.ToArray() : await ReadAsync(path, CancellationToken.None);
                state.Edited = true;
            }
        }
    }

    internal async Task VerifyAsync(CancellationToken cancellationToken)
    {
        foreach (var (path, state) in _files)
        {
            var current = await ReadAsync(path, cancellationToken);
            if (!Equal(current, state.Expected))
            {
                throw new InvalidOperationException(string.Format(
                    CultureInfo.CurrentCulture, UpdateCommandStrings.UpdateCandidateFileChangedFormat, path));
            }
        }
    }

    internal void Commit() => _committed = true;

    public async ValueTask DisposeAsync()
    {
        if (_committed || _disposed)
        {
            return;
        }
        _disposed = true;

        List<Exception> failures = [];
        foreach (var (path, state) in _files.Reverse())
        {
            if (!state.Edited && state.BackupPath is null)
            {
                continue;
            }

            try
            {
                var current = await ReadAsync(path, CancellationToken.None);
                if (state.Edited && !Equal(current, state.Expected))
                {
                    throw new InvalidOperationException(
                        string.Format(CultureInfo.CurrentCulture, UpdateCommandStrings.UpdateCandidateRollbackFileChangedFormat, path));
                }

                if (state.Edited)
                {
                    if (state.Original is null)
                    {
                        File.Delete(path);
                    }
                    else if (state.BackupPath is { } backupPath)
                    {
                        File.Copy(backupPath, path, overwrite: true);
                    }
                    else
                    {
                        await File.WriteAllBytesAsync(path, state.Original, CancellationToken.None);
                    }
                }
                if (state.BackupPath is not null)
                {
                    File.Delete(state.BackupPath);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                failures.Add(state.BackupPath is null ? exception : new IOException(string.Format(
                    CultureInfo.CurrentCulture, UpdateCommandStrings.DryRunBackupRecoveryFormat, path, state.BackupPath), exception));
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(UpdateCommandStrings.UpdateCandidateRollbackFailed, failures);
        }
    }

    internal static Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken)
        => File.Exists(path) ? ReadExistingAsync(path, cancellationToken) : Task.FromResult<byte[]?>(null);

    private static async Task<byte[]?> ReadExistingAsync(string path, CancellationToken cancellationToken)
        => await File.ReadAllBytesAsync(path, cancellationToken);

    private static bool Equal(byte[]? left, byte[]? right)
        => left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);

    private sealed class FileState(byte[]? original)
    {
        internal byte[]? Original { get; } = original;
        internal byte[]? Expected { get; set; } = original;
        internal bool Edited { get; set; }
        internal string? BackupPath { get; set; }
    }
}
