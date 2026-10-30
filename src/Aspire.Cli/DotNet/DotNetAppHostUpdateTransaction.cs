// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
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
            if (!state.Edited)
            {
                continue;
            }

            try
            {
                var current = await ReadAsync(path, CancellationToken.None);
                if (!Equal(current, state.Expected))
                {
                    throw new InvalidOperationException(
                        string.Format(CultureInfo.CurrentCulture, UpdateCommandStrings.UpdateCandidateRollbackFileChangedFormat, path));
                }

                if (state.Original is null)
                {
                    File.Delete(path);
                }
                else
                {
                    await File.WriteAllBytesAsync(path, state.Original, CancellationToken.None);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                failures.Add(exception);
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
    }
}
