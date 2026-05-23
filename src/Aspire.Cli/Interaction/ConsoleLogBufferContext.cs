// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Interaction;

/// <summary>
/// Shared context that buffers console log output while interactive prompts are active.
/// Registered as a singleton so the logger provider and interaction service share the same state.
/// </summary>
internal sealed class ConsoleLogBufferContext
{
    // Cap the buffer to prevent unbounded memory growth when a prompt stays open
    // during heavy logging. Once the cap is reached, oldest messages are dropped.
    internal const int MaxBufferedMessages = 1000;

    // Guards prompt depth, buffer state, and direct writes. Holding the lock during
    // I/O is acceptable because Console.Error is already internally serialized and
    // each write is a single short log line (sub-millisecond).
    private readonly object _logBufferLock = new();
    private readonly Queue<(TextWriter Writer, string Message)> _bufferedMessages = new();
    private int _interactivePromptDepth;

    // When true, the buffer is being flushed. New writes arriving during flush are
    // still buffered so they don't interleave with the drain or appear before it.
    private bool _isFlushing;

    /// <summary>
    /// Begins an interactive prompt scope. Logs are buffered until the outermost scope ends.
    /// </summary>
    internal IDisposable BeginInteractivePromptScope()
    {
        lock (_logBufferLock)
        {
            _interactivePromptDepth++;
        }

        return new InteractivePromptScope(this);
    }

    /// <summary>
    /// Writes the message immediately if no prompt scope is active, otherwise buffers it.
    /// The decision and write are performed atomically under the same lock so no log line
    /// can slip into an active prompt window.
    /// </summary>
    internal void WriteOrBuffer(TextWriter output, string message)
    {
        lock (_logBufferLock)
        {
            // During an active prompt or an ongoing flush, queue log lines instead of
            // writing immediately so output ordering is preserved.
            if (_interactivePromptDepth > 0 || _isFlushing)
            {
                if (_bufferedMessages.Count >= MaxBufferedMessages)
                {
                    // Drop the oldest message to stay within the cap.
                    _bufferedMessages.Dequeue();
                }

                _bufferedMessages.Enqueue((output, message));
                return;
            }

            // No prompt active and not flushing — write directly under lock so the
            // decision and I/O are atomic with respect to prompt scope changes.
            output.WriteLine(message);
        }
    }

    private void EndInteractivePromptScope()
    {
        // Flush may need to loop because new messages can arrive during the drain.
        // The decrement must only happen once per scope close, so track it separately
        // from the loop iterations to avoid stealing depth from a concurrently opened scope.
        var decremented = false;
        while (true)
        {
            List<(TextWriter Writer, string Message)> messagesToFlush;

            lock (_logBufferLock)
            {
                if (!decremented && _interactivePromptDepth > 0)
                {
                    _interactivePromptDepth--;
                    decremented = true;
                }

                if (_interactivePromptDepth > 0)
                {
                    return;
                }

                if (_bufferedMessages.Count == 0)
                {
                    _isFlushing = false;
                    return;
                }

                // Enter flushing state so concurrent WriteOrBuffer calls still buffer.
                _isFlushing = true;

                // Drain current buffer under lock to preserve ordering.
                messagesToFlush = new List<(TextWriter, string)>(_bufferedMessages.Count);
                while (_bufferedMessages.Count > 0)
                {
                    messagesToFlush.Add(_bufferedMessages.Dequeue());
                }
            }

            // Write outside the lock to avoid holding it during I/O.
            foreach (var (writer, msg) in messagesToFlush)
            {
                writer.WriteLine(msg);
            }

            // Loop back to check whether new messages arrived while we were writing.
        }
    }

    private sealed class InteractivePromptScope(ConsoleLogBufferContext context) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            // Ensure scope close is applied only once for idempotent disposal.
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                context.EndInteractivePromptScope();
            }
        }
    }
}
