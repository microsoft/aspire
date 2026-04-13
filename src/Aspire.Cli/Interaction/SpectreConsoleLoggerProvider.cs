// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;
using System.Globalization;

namespace Aspire.Cli.Interaction;

internal class SpectreConsoleLoggerProvider : ILoggerProvider
{
    // Shared buffering state for console logs emitted while interactive prompts are active.
    private static readonly object s_logBufferLock = new();
    private static readonly Queue<(TextWriter Writer, string Message)> s_bufferedMessages = new();
    private static int s_interactivePromptDepth;

    private readonly TextWriter _output;

    /// <summary>
    /// Creates a logger provider that writes to the specified output.
    /// </summary>
    /// <param name="output">The text writer to write log messages to.</param>
    public SpectreConsoleLoggerProvider(TextWriter output)
    {
        _output = output;
    }

    // Depth-based scope to support nested prompts. Logs flush when the outermost scope ends.
    internal static IDisposable BeginInteractivePromptScope()
    {
        lock (s_logBufferLock)
        {
            s_interactivePromptDepth++;
        }

        return new InteractivePromptScope();
    }

    internal static void WriteOrBuffer(TextWriter output, string message)
    {
        lock (s_logBufferLock)
        {
            // During an active prompt, queue log lines instead of writing immediately.
            if (s_interactivePromptDepth > 0)
            {
                s_bufferedMessages.Enqueue((output, message));
                return;
            }
        }

        output.WriteLine(message);
    }

    private static void EndInteractivePromptScope()
    {
        List<(TextWriter Writer, string Message)> messagesToFlush = [];

        lock (s_logBufferLock)
        {
            if (s_interactivePromptDepth > 0)
            {
                s_interactivePromptDepth--;
            }

            if (s_interactivePromptDepth > 0)
            {
                return;
            }

            // Drain under lock to preserve ordering across concurrent writers.
            while (s_bufferedMessages.Count > 0)
            {
                messagesToFlush.Add(s_bufferedMessages.Dequeue());
            }
        }

        // Write outside the lock to avoid holding the global lock during I/O.
        foreach (var (writer, message) in messagesToFlush)
        {
            writer.WriteLine(message);
        }
    }

    private sealed class InteractivePromptScope : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            // Ensure scope close is applied only once for idempotent disposal.
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                EndInteractivePromptScope();
            }
        }
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new SpectreConsoleLogger(_output, categoryName);
    }

    public void Dispose()
    {
    }
}

internal class SpectreConsoleLogger(TextWriter output, string categoryName) : ILogger
{
    public bool IsEnabled(LogLevel logLevel) =>
        logLevel >= LogLevel.Debug &&
        (categoryName.StartsWith("Aspire.Cli", StringComparison.Ordinal) || logLevel >= LogLevel.Warning);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        if (string.IsNullOrEmpty(message) && exception is null)
        {
            return;
        }

        var formattedMessage = exception is not null ? $"{message} {exception}" : message;

        // Extract the last token from the category name to reduce noise
        var shortCategoryName = GetShortCategoryName(categoryName);

        // Format timestamp to show only time (HH:mm:ss) for debugging purposes
        var timestamp = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        var logMessage = $"[{timestamp}] [{GetLogLevelString(logLevel)}] {shortCategoryName}: {formattedMessage}";

        // Write to the configured output (stderr by default)
        SpectreConsoleLoggerProvider.WriteOrBuffer(output, logMessage);
    }

    private static string GetLogLevelString(LogLevel logLevel) => logLevel switch
    {
        LogLevel.Debug => "dbug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "fail",
        LogLevel.Critical => "crit",
        _ => logLevel.ToString().ToLower()
    };

    private static string GetShortCategoryName(string categoryName)
    {
        var lastDotIndex = categoryName.LastIndexOf('.');
        return lastDotIndex >= 0 ? categoryName.Substring(lastDotIndex + 1) : categoryName;
    }
}
