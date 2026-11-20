// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Aspire.Cli.Diagnostics;

/// <summary>
/// Owns the naming, ownership marker, and retention of automatic CLI log files in the dedicated logs directory.
/// </summary>
/// <remarks>
/// A file is managed only when its name matches <see cref="GenerateLogFilePath"/> and its first line is the
/// marker written by <see cref="WriteHeader"/> for that same name. Custom <c>--log-file</c> targets and legacy
/// logs have no marker, so they are never pruned even when their names look generated.
/// </remarks>
internal static partial class ManagedLogFiles
{
    // Includes the current process's file.
    private const int RetainedLogFileCount = 100;

    // Limit filesystem work on the startup path when many tracked logs have accumulated.
    private const int MaxPrunedLogFilesPerRun = 100;
    private const string HeaderPrefix = "# Aspire CLI managed diagnostic log: ";

    /// <summary>
    /// Generates a unique, chronologically-sortable managed log file path.
    /// </summary>
    /// <param name="logsDirectory">The directory where log files will be written.</param>
    /// <param name="timeProvider">The time provider for timestamp generation.</param>
    /// <param name="suffix">An optional lowercase suffix inserted before <c>_managed.log</c>, such as <c>detach-child</c>.</param>
    internal static string GenerateLogFilePath(string logsDirectory, TimeProvider timeProvider, string? suffix = null)
    {
        var timestamp = timeProvider.GetUtcNow().ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
        var id = Guid.NewGuid().ToString("N")[..8];
        var name = suffix is null
            ? $"cli_{timestamp}_{id}_managed.log"
            : $"cli_{timestamp}_{id}_{suffix}_managed.log";
        return Path.Combine(logsDirectory, name);
    }

    /// <summary>
    /// Writes the ownership marker that makes a newly created log file eligible for pruning.
    /// </summary>
    internal static void WriteHeader(TextWriter writer, string logFilePath)
    {
        writer.WriteLine(HeaderPrefix + Path.GetFileName(logFilePath));
    }

    // Every CLI invocation creates a log file, and tools such as the VS Code extension poll
    // commands like `aspire ps`, so the logs directory grows without bound unless old files are removed.
    // Pruning is best-effort: concurrent CLI processes may prune at the same time, and any file that
    // cannot be deleted is left for a later run.
    /// <summary>
    /// Deletes the oldest managed log files beyond the retention limit.
    /// </summary>
    /// <param name="logsDirectory">The dedicated logs directory.</param>
    /// <param name="reserveSlot">Whether the caller is about to create a managed log that counts toward the limit.</param>
    internal static void Prune(string logsDirectory, bool reserveSlot)
    {
        FileInfo[] logFiles;
        try
        {
            logFiles = new DirectoryInfo(logsDirectory).GetFiles("cli_*_managed.log")
                .Where(file => GeneratedLogFileNameRegex().IsMatch(file.Name))
                .Where(IsManagedLogFile)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var excessCount = Math.Min(logFiles.Length - (RetainedLogFileCount - (reserveSlot ? 1 : 0)), MaxPrunedLogFilesPerRun);
        if (excessCount <= 0)
        {
            return;
        }

        // Only deleted files count toward the per-run limit. Otherwise old logs that can't be deleted, such as
        // logs held open by long-running commands, could fill the limit on every run and keep newer inactive
        // logs from ever being pruned.
        var prunedCount = 0;
        foreach (var file in logFiles.OrderBy(file => file.LastWriteTimeUtc))
        {
            // Do not follow links that may point outside the logs directory.
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            if (TryDeleteInactiveLogFile(file.FullName) && ++prunedCount == excessCount)
            {
                break;
            }
        }
    }

    // Matches names produced by GenerateLogFilePath, for example:
    //   cli_20260212T180000_1a2b3c4d_managed.log
    //   cli_20260212T180000_1a2b3c4d_detach-child_managed.log
    [GeneratedRegex(@"^cli_[0-9]{8}T[0-9]{6}_[0-9a-f]{8}(?:_[a-z0-9-]+)?_managed\.log$")]
    private static partial Regex GeneratedLogFileNameRegex();

    private static bool IsManagedLogFile(FileInfo file)
    {
        try
        {
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }

            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return HasHeader(stream, file.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryDeleteInactiveLogFile(string path)
    {
        try
        {
            // Long-running commands such as `aspire run` keep their log open with FileShare.Read.
            // On Unix .NET enforces exclusive access with an advisory flock, which may be unavailable.
            // Windows must allow delete sharing so File.Delete can mark this open file for deletion.
            var share = OperatingSystem.IsWindows() ? FileShare.Delete : FileShare.None;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, share);
            if (!HasHeader(stream, Path.GetFileName(path)))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Managed files start with "# Aspire CLI managed diagnostic log: cli_<timestamp>_<id>_managed.log".
    // Binding the marker to the filename keeps a renamed copy from becoming another managed log.
    private static bool HasHeader(Stream stream, string fileName)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
        var expectedHeader = HeaderPrefix + fileName;
        Span<char> header = stackalloc char[expectedHeader.Length + 1];
        return reader.ReadBlock(header) == header.Length
            && header[..^1].SequenceEqual(expectedHeader)
            && header[^1] is '\r' or '\n';
    }
}
