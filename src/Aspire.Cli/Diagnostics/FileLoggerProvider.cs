// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Aspire.Cli.Interaction;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Aspire.Cli.Diagnostics;

/// <summary>
/// A logger provider that writes all log messages to a file on disk.
/// This provider captures logs at all levels (Trace through Critical) for diagnostics,
/// independent of console verbosity settings.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private const int MaxQueuedMessages = 1024;

    private readonly string _logFilePath;
    private readonly StreamWriter? _writer;
    private readonly Channel<string>? _channel;
    private readonly Task? _writerTask;
    private bool _disposed;

    /// <summary>
    /// Gets the path to the log file.
    /// </summary>
    public string LogFilePath => _logFilePath;

    /// <summary>
    /// Generates a unique, chronologically-sortable log file name.
    /// </summary>
    /// <param name="logsDirectory">The directory where log files will be written.</param>
    /// <param name="timeProvider">The time provider for timestamp generation.</param>
    /// <param name="suffix">An optional suffix appended before the extension (e.g. "detach-child").</param>
    internal static string GenerateLogFilePath(string logsDirectory, TimeProvider timeProvider, string? suffix = null)
    {
        var timestamp = timeProvider.GetUtcNow().ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
        var id = Guid.NewGuid().ToString("N")[..8];
        var name = suffix is null
            ? $"cli_{timestamp}_{id}.log"
            : $"cli_{timestamp}_{id}_{suffix}.log";
        return Path.Combine(logsDirectory, name);
    }

    /// <summary>
    /// Creates a new FileLoggerProvider that writes to the specified directory.
    /// </summary>
    /// <param name="logsDirectory">The directory where log files will be written.</param>
    /// <param name="timeProvider">The time provider for timestamp generation.</param>
    /// <param name="errorWriter">Error writer for startup error messages.</param>
    public FileLoggerProvider(string logsDirectory, TimeProvider timeProvider, IStartupErrorWriter errorWriter)
        : this(GenerateLogFilePath(logsDirectory, timeProvider), errorWriter)
    {
    }

    /// <summary>
    /// Creates a new FileLoggerProvider with a specific log file path.
    /// </summary>
    /// <param name="logFilePath">The full path to the log file.</param>
    /// <param name="errorWriter">Error writer for startup error messages.</param>
    internal FileLoggerProvider(string logFilePath, IStartupErrorWriter errorWriter)
    {
        _logFilePath = logFilePath;

        try
        {
            var directory = Path.GetDirectoryName(logFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _writer = new StreamWriter(logFilePath, append: false, Encoding.UTF8)
            {
                AutoFlush = true
            };

            _channel = CreateChannel();
            _writerTask = Task.Run(ProcessLogQueueAsync);
        }
        catch (IOException ex)
        {
            WriteWarning(errorWriter, logFilePath, ex.Message);
            _writer = null;
            _channel = null;
        }
    }

    private static Channel<string> CreateChannel() =>
        Channel.CreateBounded<string>(new BoundedChannelOptions(MaxQueuedMessages)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

    private static void WriteWarning(IStartupErrorWriter errorWriter, string path, string message)
    {
        errorWriter.WriteMarkup($"Could not create log file at [blue]{path.EscapeMarkup()}[/]: {message.EscapeMarkup()}", KnownEmojis.Warning);
    }

    private async Task ProcessLogQueueAsync()
    {
        if (_channel is null || _writer is null)
        {
            return;
        }

        try
        {
            await foreach (var message in _channel.Reader.ReadAllAsync())
            {
                await _writer.WriteLineAsync(message).ConfigureAwait(false);
            }
        }
        catch (ChannelClosedException)
        {
            // Expected when channel is completed during disposal
        }
        catch (ObjectDisposedException)
        {
            // Writer was disposed while writing - expected during shutdown
        }
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new FileLogger(this, categoryName);
    }

    internal void WriteLog(string message)
    {
        if (_channel is null || _disposed)
        {
            return;
        }

        // Try to write to the channel - this will succeed as long as there's space
        // and the channel hasn't been completed yet
        if (_channel.Writer.TryWrite(message))
        {
            return;
        }

        // TryWrite failed - either channel is full (need backpressure) or completed (disposal)
        if (_disposed)
        {
            return;
        }

        // Try async write which will wait for space or throw if completed
        try
        {
            // WaitToWriteAsync returns false if the channel is completed
            // This is cheaper than catching ChannelClosedException from WriteAsync
            if (!_channel.Writer.WaitToWriteAsync().AsTask().GetAwaiter().GetResult())
            {
                return;
            }

            // Space is available, write the message
            _channel.Writer.TryWrite(message);
        }
        catch (ChannelClosedException)
        {
            // Channel was completed between WaitToWriteAsync and TryWrite - rare race
        }
    }

    internal void WriteLog(DateTimeOffset timestamp, LogLevel level, string category, string message, Exception? exception = null)
    {
        var ts = timestamp.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var lvl = GetLogLevelString(level);

        var logMessage = exception is not null
            ? $"[{ts}] [{lvl}] [{category}] {message}{Environment.NewLine}{exception}"
            : $"[{ts}] [{lvl}] [{category}] {message}";

        WriteLog(logMessage);
    }

    internal static string GetLogLevelString(LogLevel logLevel) => logLevel switch
    {
        LogLevel.Trace => "TRCE",
        LogLevel.Debug => "DBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "FAIL",
        LogLevel.Critical => "CRIT",
        _ => logLevel.ToString().ToUpperInvariant()
    };

    internal static string GetShortCategoryName(string categoryName)
    {
        var lastDotIndex = categoryName.LastIndexOf('.');
        return lastDotIndex >= 0 ? categoryName.Substring(lastDotIndex + 1) : categoryName;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Complete the channel to signal the writer task to finish
        // Any messages already in the channel will be drained by the writer task
        _channel?.Writer.TryComplete();

        // Wait for the writer task to finish processing ALL remaining messages
        _writerTask?.GetAwaiter().GetResult();

        _writer?.Dispose();
    }
}

/// <summary>
/// A logger that writes to a file via the FileLoggerProvider.
/// </summary>
internal sealed class FileLogger(FileLoggerProvider provider, string categoryName) : ILogger
{
    // Suppress Microsoft.Hosting.Lifetime logs - these are CLI host lifecycle noise
    private static readonly string[] s_suppressedCategories = ["Microsoft.Hosting.Lifetime"];

    // Always enabled for file logging, except suppressed categories
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && !s_suppressedCategories.Contains(categoryName);

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

        var shortCategory = FileLoggerProvider.GetShortCategoryName(categoryName);
        provider.WriteLog(DateTimeOffset.UtcNow, logLevel, shortCategory, message, exception);
    }
}
