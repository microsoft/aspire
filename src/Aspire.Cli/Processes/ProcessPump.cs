// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Processes;

/// <summary>
/// Shared line-pump used by both Unix and Windows variants of
/// <see cref="IsolatedProcess"/>. A pump reads complete lines from a
/// <see cref="TextReader"/> and dispatches them to a callback, completing when the
/// reader hits EOF.
/// </summary>
/// <remarks>
/// Callback exceptions do NOT terminate the drain — the pump continues reading until
/// EOF so that a verbose child cannot back-pressure into a full pipe and block on
/// every subsequent write. The first exception is recorded and surfaced via the
/// returned <see cref="Completion"/> task after the pump finishes draining.
/// </remarks>
internal sealed class ProcessPump
{
    private ProcessPump(Task completion)
    {
        Completion = completion;
    }

    /// <summary>Completes (or faults) when the underlying reader hits EOF.</summary>
    public Task Completion { get; }

    /// <summary>
    /// Starts a pump that reads lines from <paramref name="reader"/> and invokes
    /// <paramref name="onLine"/> for each non-null line. The pump runs on a background
    /// task and stops when the reader returns null (EOF) or throws.
    /// </summary>
    public static ProcessPump Start(TextReader reader, Action<string> onLine)
    {
        var completion = Task.Run(() => RunAsync(reader, onLine));
        return new ProcessPump(completion);
    }

    private static async Task RunAsync(TextReader reader, Action<string> onLine)
    {
        Exception? firstCallbackException = null;

        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // The reader's underlying stream was torn down (typically because the
                // child process was disposed while a read was in flight). Treat as EOF
                // and let the pump exit cleanly — surfacing a previously-recorded
                // callback exception at this point would just confuse error attribution.
                return;
            }
            catch (IOException)
            {
                // The pipe was broken — Windows surfaces "pipe is broken" / Unix surfaces
                // EBADF when the underlying handle is reaped during a read. Treat as EOF.
                return;
            }

            if (line is null)
            {
                break;
            }

            try
            {
                onLine(line);
            }
            catch (Exception ex) when (firstCallbackException is null)
            {
                // Record but keep draining — the pipe MUST be drained so the child can
                // continue to write without blocking. The recorded exception is
                // re-thrown after EOF so callers can observe it via Completion.
                firstCallbackException = ex;
            }
            catch
            {
                // Subsequent callback failures are dropped; the first one is enough
                // signal for callers.
            }
        }

        if (firstCallbackException is not null)
        {
            // Preserve the original stack trace when faulting the pump task.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstCallbackException).Throw();
        }
    }
}
