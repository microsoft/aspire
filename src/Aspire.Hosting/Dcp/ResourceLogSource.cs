// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Aspire.Hosting.Dcp.Model;
using Aspire.Shared.ConsoleLogs;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dcp;

/// <param name="Content">The normalized (display-ready) content of the log entry.</param>
/// <param name="IsErrorMessage">Whether the entry represents an error.</param>
/// <param name="Timestamp">The timestamp extracted from the log entry, if available.</param>
/// <param name="RawContent">
/// The content to use as the raw log line for export scenarios (e.g. log serialization). Populated only for DCP
/// system log entries, where it includes the timestamp prefix so that exported lines retain their timestamps.
/// For non-system entries, <see langword="null"/> indicates the caller should fall back to <see cref="Content"/>.
/// </param>
internal readonly record struct ResourceLogEntry(string Content, bool IsErrorMessage, DateTime? Timestamp = null, string? RawContent = null);

internal sealed class ResourceLogSource<TResource>(
    ILogger logger,
    IKubernetesService kubernetesService,
    TResource resource,
    bool follow) :
    IAsyncEnumerable<IReadOnlyList<ResourceLogEntry>>
    where TResource : CustomResource, IKubernetesStaticMetadata
{
    public async IAsyncEnumerator<IReadOnlyList<ResourceLogEntry>> GetAsyncEnumerator(CancellationToken cancellationToken)
    {
        // For follow mode, we require a cancellable token to stop streaming.
        // For non-follow mode (snapshot), streams complete naturally so we create our own cancellable token if needed.
        CancellationTokenSource? ownedCts = null;
        if (!cancellationToken.CanBeCanceled)
        {
            if (follow)
            {
                throw new ArgumentException("Cancellation token must be cancellable in order to prevent leaking resources when following logs.", nameof(cancellationToken));
            }
            // Create our own cancellable token for the APIs that require it.
            // For non-follow mode, streams complete naturally when all logs are read.
            ownedCts = new CancellationTokenSource();
            cancellationToken = ownedCts.Token;
        }

        var channel = Channel.CreateUnbounded<ResourceLogEntry>(new UnboundedChannelOptions
        {
            AllowSynchronousContinuations = false,
            SingleReader = true,
            SingleWriter = false
        });

        async Task StreamLogsAsync(Stream stream, bool isError, bool parseDcpLogs)
        {
            try
            {
                await foreach (var rawLine in ReadLogLinesAsync(stream, cancellationToken).ConfigureAwait(false))
                {
                    var line = NormalizeCarriageReturns(rawLine);
                    var lineIsError = isError;
                    DateTime? timestamp = null;
                    string? rawContent = null;

                    // Parse DCP logs if requested
                    if (parseDcpLogs && DcpLogParser.TryParseDcpLog(line, out var parsedMessage, out _, out var isErrorLevel, out var dcpTimestamp))
                    {
                        // Format system logs with [sys] prefix and improved readability
                        line = DcpLogParser.FormatSystemLog(parsedMessage);
                        lineIsError = isErrorLevel;
                        timestamp = dcpTimestamp?.UtcDateTime;
                        // Build raw content with the timestamp prefix so LogEntry.RawContent retains the timestamp for export.
                        rawContent = timestamp is not null ? $"{timestamp.Value:o} {line}" : line;
                    }
                    var succeeded = channel.Writer.TryWrite(new ResourceLogEntry(line, lineIsError, timestamp, rawContent));
                    if (!succeeded)
                    {
                        logger.LogWarning("Failed to write log entry to channel. Logs for {Kind} {Name} may be incomplete", resource.Kind, resource.Metadata.Name);
                        channel.Writer.TryComplete();
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Expected
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error happened when capturing logs for {Kind} {Name}", resource.Kind, resource.Metadata.Name);
                channel.Writer.TryComplete(ex);
            }
        }

        try
        {
            var streamTasks = new List<Task>();

            var startupStderrStream = await kubernetesService.GetLogStreamAsync(resource, Logs.StreamTypeStartupStdErr, cancellationToken, follow: follow, timestamps: true).ConfigureAwait(false);
            var startupStdoutStream = await kubernetesService.GetLogStreamAsync(resource, Logs.StreamTypeStartupStdOut, cancellationToken, follow: follow, timestamps: true).ConfigureAwait(false);

            var startupStdoutStreamTask = Task.Run(() => StreamLogsAsync(startupStdoutStream, isError: false, parseDcpLogs: false), cancellationToken);
            streamTasks.Add(startupStdoutStreamTask);

            var startupStderrStreamTask = Task.Run(() => StreamLogsAsync(startupStderrStream, isError: false, parseDcpLogs: false), cancellationToken);
            streamTasks.Add(startupStderrStreamTask);

            var stdoutStream = await kubernetesService.GetLogStreamAsync(resource, Logs.StreamTypeStdOut, cancellationToken, follow: follow, timestamps: true).ConfigureAwait(false);
            var stderrStream = await kubernetesService.GetLogStreamAsync(resource, Logs.StreamTypeStdErr, cancellationToken, follow: follow, timestamps: true).ConfigureAwait(false);

            var stdoutStreamTask = Task.Run(() => StreamLogsAsync(stdoutStream, isError: false, parseDcpLogs: false), cancellationToken);
            streamTasks.Add(stdoutStreamTask);

            var stderrStreamTask = Task.Run(() => StreamLogsAsync(stderrStream, isError: true, parseDcpLogs: false), cancellationToken);
            streamTasks.Add(stderrStreamTask);

            var systemStream = await kubernetesService.GetLogStreamAsync(resource, Logs.StreamTypeSystem, cancellationToken, follow: follow, timestamps: true).ConfigureAwait(false);

            var systemStreamTask = Task.Run(() => StreamLogsAsync(systemStream, isError: false, parseDcpLogs: true), cancellationToken);
            streamTasks.Add(systemStreamTask);

            // End the enumeration when all streams have been read to completion.
            async Task WaitForStreamsToCompleteAsync()
            {
                await Task.WhenAll(streamTasks).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                channel.Writer.TryComplete();
            }

            _ = WaitForStreamsToCompleteAsync();

            await foreach (var batch in channel.GetBatchesAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                yield return batch;
            }
        }
        finally
        {
            ownedCts?.Dispose();
        }
    }

    private static async IAsyncEnumerable<string> ReadLogLinesAsync(Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var sr = new StreamReader(stream, leaveOpen: false);
        var sb = new StringBuilder();
        var buffer = new char[4096];

        while (true)
        {
            var read = await sr.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (sb.Length > 0)
                {
                    yield return sb.ToString();
                }

                yield break;
            }

            for (var i = 0; i < read; i++)
            {
                var ch = buffer[i];
                if (ch == '\n')
                {
                    if (sb.Length > 0 && sb[^1] == '\r')
                    {
                        sb.Length--;
                    }

                    yield return sb.ToString();
                    sb.Clear();
                }
                else
                {
                    sb.Append(ch);
                }
            }
        }
    }

    private static string NormalizeCarriageReturns(string line)
    {
        if (!line.Contains('\r'))
        {
            return line;
        }

        if (TimestampParser.TryParseConsoleTimestamp(line, out var timestampParseResult))
        {
            var prefixLength = line.Length - timestampParseResult.Value.ModifiedText.Length;
            var prefix = line[..prefixLength];
            return prefix + GetTextAfterLastCarriageReturn(timestampParseResult.Value.ModifiedText);
        }

        return GetTextAfterLastCarriageReturn(line);
    }

    private static string GetTextAfterLastCarriageReturn(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        var endIndex = text[^1] == '\r' ? text.Length - 1 : text.Length;
        if (endIndex == 0)
        {
            return string.Empty;
        }

        var carriageReturnIndex = text.LastIndexOf('\r', endIndex - 1);
        return carriageReturnIndex >= 0 ? text[(carriageReturnIndex + 1)..endIndex] : text[..endIndex];
    }
}
