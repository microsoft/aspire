// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.RegularExpressions;
using Aspire.Shared;

namespace Aspire.Cli.Diagnostics;

internal sealed partial class RollingLogFile : TextWriter
{
    internal const int FileSizeLimit = 1024 * 1024;
    internal const int RetainedFileCount = 100;
    internal const string TruncationMarker = "[Log entry truncated to the diagnostic file size limit]";

    private const int ArchiveCount = 2;
    private readonly string _path;
    private readonly string _directory;
    private readonly string _lockPath;
    private readonly int _fileSizeLimit;
    private readonly int _retainedFileCount;
    private StreamWriter _writer;

    public override Encoding Encoding => Encoding.UTF8;

    internal RollingLogFile(string path)
        : this(path, FileSizeLimit, RetainedFileCount)
    {
    }

    internal RollingLogFile(string path, int fileSizeLimit, int retainedFileCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            fileSizeLimit,
            Encoding.UTF8.Preamble.Length + Encoding.UTF8.GetByteCount(TruncationMarker + Environment.NewLine + Environment.NewLine) + 4);
        ArgumentOutOfRangeException.ThrowIfLessThan(retainedFileCount, 1);

        _path = Path.GetFullPath(path);
        _directory = Path.GetDirectoryName(_path)!;
        if (!IsManagedLogFile(_path, _directory))
        {
            throw new ArgumentException("Automatic CLI logs must use a generated CLI log file name.", nameof(path));
        }

        _lockPath = Path.Combine(_directory, ".cli-retention.lock");
        _fileSizeLimit = fileSizeLimit;
        _retainedFileCount = retainedFileCount;

        // Creation and rotation share a lock so concurrent invocations cannot each claim
        // the last available slot. Ordinary writes keep their own file handle and need no lock.
        using var retentionLock = FileLock.Acquire(_lockPath, TimeSpan.FromSeconds(5));
        PruneLogs();
        _writer = CreateWriter(_path);
    }

    internal static bool IsManagedLogFile(string path, string logsDirectory)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        return Path.GetRelativePath(Path.GetFullPath(logsDirectory), directory) == "."
            && LogFileNameRegex().IsMatch(Path.GetFileName(path));
    }

    internal static StreamWriter CreateWriter(string logFilePath)
    {
        var options = new FileStreamOptions
        {
            Mode = OperatingSystem.IsWindows() ? FileMode.Create : FileMode.OpenOrCreate,
            Access = FileAccess.Write,
            Share = FileShare.Read
        };
        if (!OperatingSystem.IsWindows())
        {
            // Restrict access at creation so no log contents are ever written with broader permissions.
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        var stream = new FileStream(logFilePath, options);
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                // UnixCreateMode does not change existing files. Harden the opened file before
                // truncating or writing, using its handle to avoid a path-replacement race.
                File.SetUnixFileMode(stream.SafeFileHandle, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                stream.SetLength(0);
            }

            return new StreamWriter(stream, Encoding.UTF8)
            {
                AutoFlush = true
            };
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public override void WriteLine(string? value)
    {
        var message = BoundEntry(value ?? string.Empty);
        var length = Math.Max(_writer.BaseStream.Position, Encoding.UTF8.Preamble.Length);
        if (length + Encoding.UTF8.GetByteCount(message) + Encoding.UTF8.GetByteCount(NewLine) > _fileSizeLimit)
        {
            Rotate();
        }

        _writer.NewLine = NewLine;
        _writer.WriteLine(message);
    }

    public override Task WriteLineAsync(string? value)
    {
        WriteLine(value);

        return Task.CompletedTask;
    }

    private string BoundEntry(string message)
    {
        var availableBytes = _fileSizeLimit - Encoding.UTF8.Preamble.Length - Encoding.UTF8.GetByteCount(NewLine);
        if (Encoding.UTF8.GetByteCount(message) <= availableBytes)
        {
            return message;
        }

        var suffix = Environment.NewLine + TruncationMarker;
        var buffer = new byte[availableBytes - Encoding.UTF8.GetByteCount(suffix)];
        Encoding.UTF8.GetEncoder().Convert(message.AsSpan(), buffer, flush: true, out var charactersUsed, out _, out _);

        return message[..charactersUsed] + suffix;
    }

    private void Rotate()
    {
        using var retentionLock = FileLock.Acquire(_lockPath, TimeSpan.FromSeconds(5));
        _writer.Dispose();

        File.Delete(Path.ChangeExtension(_path, $"{ArchiveCount}.log"));
        for (var index = ArchiveCount - 1; index >= 1; index--)
        {
            var archive = Path.ChangeExtension(_path, $"{index}.log");
            if (File.Exists(archive))
            {
                File.Move(archive, Path.ChangeExtension(_path, $"{index + 1}.log"));
            }
        }
        File.Move(_path, Path.ChangeExtension(_path, "1.log"));

        PruneLogs();
        _writer = CreateWriter(_path);
    }

    private void PruneLogs()
    {
        var files = new DirectoryInfo(_directory).EnumerateFiles("cli_*.log")
            .Where(file => LogFileNameRegex().IsMatch(file.Name) && (file.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderBy(file => file.Length <= _fileSizeLimit)
            .ThenBy(file => file.LastWriteTimeUtc)
            .ThenBy(file => file.Name, StringComparer.Ordinal)
            .ToArray();
        var remaining = files.Length;
        var oversizedFileRemaining = false;
        Exception? deletionError = null;
        foreach (var file in files)
        {
            var oversized = file.Length > _fileSizeLimit;
            if (!oversized && remaining < _retainedFileCount)
            {
                continue;
            }

            try
            {
                // An exclusive open protects live writers on Unix as well as Windows.
                // DeleteOnClose removes the file without a close-then-delete race.
                using (new FileStream(file.FullName, FileMode.Open, FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose))
                {
                }
                remaining--;
            }
            catch (FileNotFoundException)
            {
                remaining--;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                deletionError = exception;
                oversizedFileRemaining |= oversized;
            }
        }

        // Reserve one slot for the new current file. Combined with the per-file limit,
        // this also bounds total automatic log storage to 100 MiB.
        if (remaining >= _retainedFileCount || oversizedFileRemaining)
        {
            throw new IOException("Could not enforce automatic CLI log retention without deleting an active or inaccessible log file.", deletionError);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _writer.Dispose();
        }

        base.Dispose(disposing);
    }

    // Managed names include cli_20261105T120000_a1b2c3d4.log and detached logs such as
    // cli_20261105T120000123_detach-child_<32 hex digits>.log, plus .1.log/.2.log archives.
    // Custom names and files outside the dedicated directory are not retention candidates.
    [GeneratedRegex(@"^cli_(?:[0-9]{8}T[0-9]{6}_[0-9a-f]{8}(?:_[A-Za-z0-9-]+)?|[0-9]{8}T[0-9]{9}_detach-child_[0-9a-f]{32})(?:\.[12])?\.log$", RegexOptions.CultureInvariant)]
    private static partial Regex LogFileNameRegex();
}
