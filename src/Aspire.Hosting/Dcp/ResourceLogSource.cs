// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;
using System.Threading.Channels;
using Aspire.Hosting.Dcp.Model;

namespace Aspire.Hosting.Dcp;

using LogEntry = (string Content, bool IsErrorMessage);
using LogEntryList = IReadOnlyList<(string Content, bool IsErrorMessage)>;

internal sealed class ResourceLogSource<TResource>(
    ILogger logger,
    IKubernetesService kubernetesService,
    TResource resource,
    bool follow) :
    IAsyncEnumerable<LogEntryList>
    where TResource : CustomResource, IKubernetesStaticMetadata
{
    public async IAsyncEnumerator<LogEntryList> GetAsyncEnumerator(CancellationToken cancellationToken)
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

        var channel = Channel.CreateUnbounded<LogEntry>(new UnboundedChannelOptions
        {
            AllowSynchronousContinuations = false,
            SingleReader = true,
            SingleWriter = false
        });

        async Task StreamLogsAsync(Stream stream, bool isError, bool parseDcpLogs)
        {
            try
            {
                using var sr = new StreamReader(stream, leaveOpen: false);
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await sr.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line is null)
                    {
                        return; // No more data
                    }

                    // Parse DCP logs if requested
                    if (parseDcpLogs && DcpLogParser.TryParseDcpLog(line, out var parsedMessage, out _, out var isErrorLevel))
                    {
                        // Format system logs with [sys] prefix and improved readability
                        line = DcpLogParser.FormatSystemLog(parsedMessage);
                        isError = isErrorLevel;
                    }

                    var succeeded = channel.Writer.TryWrite((line, isError));
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
}
