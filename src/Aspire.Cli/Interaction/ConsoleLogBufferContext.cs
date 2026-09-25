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
            if (_interactivePromptDepth > 0)
            {
                if (_bufferedMessages.Count >= MaxBufferedMessages)
                {
                    // Drop the oldest message to stay within the cap.
                    _bufferedMessages.Dequeue();
                }

                _bufferedMessages.Enqueue((output, message));
                return;
            }

            output.WriteLine(message);
        }
    }

    private void EndInteractivePromptScope()
    {
        lock (_logBufferLock)
        {
            if (_interactivePromptDepth > 0)
            {
                _interactivePromptDepth--;
            }

            if (_interactivePromptDepth > 0)
            {
                return;
            }

            // Flush all buffered messages under the lock. This is acceptable because
            // each message is a single short log line (sub-millisecond write) and
            // Console.Error is already internally serialized. Writing under lock
            // eliminates the need for a flush loop or _isFlushing flag entirely.
            while (_bufferedMessages.Count > 0)
            {
                var (writer, msg) = _bufferedMessages.Dequeue();
                writer.WriteLine(msg);
            }
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
