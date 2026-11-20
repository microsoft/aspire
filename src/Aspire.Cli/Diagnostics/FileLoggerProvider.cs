// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Aspire.Cli.Interaction;
using Aspire.Shared;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Aspire.Cli.Diagnostics;

/// <summary>
/// A logger provider that writes all log messages to a file on disk.
/// This provider captures logs at all levels (Trace through Critical) for diagnostics,
/// independent of console verbosity settings.
/// On Unix, log files are restricted to the current user, including reused files.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private const int MaxQueuedMessages = 1024;

    private readonly string _logFilePath;
    private readonly StreamWriter? _writer;
    private readonly Channel<string>? _channel;
    private readonly Task? _writerTask;

    // True only when this instance created a new managed log file. Only that file may be discarded
    // on dispose; custom --log-file targets and files this instance failed to create are never deleted.
    private readonly bool _ownsManagedLogFile;
    private bool _disposed;

    // Set once anything worth keeping is recorded. It is never cleared.
    private volatile bool _retainLogFile;
    private CancellationToken? _discardOnDisposeUnlessCanceled;

    private FileLoggerProvider(string logFilePath)
    {
        _logFilePath = logFilePath;
    }

    // Completion builds the command model but must not create a log file for every Tab press.
    internal static FileLoggerProvider CreateDisabled(string logFilePath) => new(logFilePath);

    /// <summary>
    /// Gets the path to the log file.
    /// </summary>
    public string LogFilePath => _logFilePath;

    /// <summary>
    /// Creates a new FileLoggerProvider that writes to the specified directory.
    /// </summary>
    /// <param name="logsDirectory">The directory where log files will be written.</param>
    /// <param name="timeProvider">The time provider for timestamp generation.</param>
    /// <param name="errorWriter">Error writer for startup error messages.</param>
    public FileLoggerProvider(string logsDirectory, TimeProvider timeProvider, IStartupErrorWriter errorWriter)
        : this(ManagedLogFiles.GenerateLogFilePath(logsDirectory, timeProvider), logsDirectory, errorWriter, isManagedLog: true)
    {
    }

    /// <summary>
    /// Creates a new FileLoggerProvider with a specific log file path.
    /// </summary>
    /// <param name="logFilePath">The full path to the log file.</param>
    /// <param name="errorWriter">Error writer for startup error messages.</param>
    internal FileLoggerProvider(string logFilePath, IStartupErrorWriter errorWriter)
        : this(logFilePath, logsDirectory: null, errorWriter, isManagedLog: false)
    {
    }

    /// <summary>
    /// Creates a file logger. When its parent is the dedicated logs directory, repairs the directory's
    /// permissions and prunes old log files.
    /// </summary>
    internal FileLoggerProvider(string logFilePath, string? logsDirectory, IStartupErrorWriter errorWriter, bool isManagedLog)
    {
        _logFilePath = logFilePath;

        try
        {
            var directory = Path.GetDirectoryName(logFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                // Repair Aspire's dedicated log directory, but do not chmod arbitrary existing
                // parents selected by --log-file (which could be the user's working directory).
                if (logsDirectory is not null && Path.GetRelativePath(logsDirectory, directory) == ".")
                {
                    DirectoryHelper.CreateWithOwnerOnlyPermissions(directory);
                    ManagedLogFiles.Prune(directory, reserveSlot: isManagedLog);
                }
                else if (OperatingSystem.IsWindows())
                {
                    Directory.CreateDirectory(directory);
                }
                else
                {
                    Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }

            var writer = CreateWriter(logFilePath, isManagedLog);
            try
            {
                if (isManagedLog)
                {
                    ManagedLogFiles.WriteHeader(writer, logFilePath);
                }
            }
            catch
            {
                writer.Dispose();
                throw;
            }

            _writer = writer;
            _ownsManagedLogFile = isManagedLog;

            _channel = CreateChannel();
            _writerTask = Task.Run(ProcessLogQueueAsync);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WriteWarning(errorWriter, logFilePath, ex.Message);
            _writer = null;
            _channel = null;
        }
    }

    private static StreamWriter CreateWriter(string logFilePath, bool isManagedLog)
    {
        var options = new FileStreamOptions
        {
            Mode = isManagedLog ? FileMode.CreateNew : OperatingSystem.IsWindows() ? FileMode.Create : FileMode.OpenOrCreate,
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

    private static Channel<string> CreateChannel() =>
        Channel.CreateBounded<string>(new BoundedChannelOptions(MaxQueuedMessages)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

    private static void WriteWarning(IStartupErrorWriter errorWriter, string path, string message)
    {
        errorWriter.WriteMarkup($"Could not create log file at [blue]{path.EscapeMarkup()}[/]: {message.EscapeMarkup()}", KnownEmojis.PoliceCarLight);
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
        // Best-effort operations often log handled exceptions at Debug, so an exception at any level
        // is kept along with warnings and errors.
        if ((level >= LogLevel.Warning && level != LogLevel.None) || exception is not null)
        {
            _retainLogFile = true;
        }

        var ts = timestamp.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var lvl = GetLogLevelString(level);

        var logMessage = exception is not null
            ? $"[{ts}] [{lvl}] [{category}] {message}{Environment.NewLine}{exception}"
            : $"[{ts}] [{lvl}] [{category}] {message}";

        WriteLog(logMessage);
    }

    internal static string GetLogLevelString(LogLevel logLevel) => CliLogFormat.GetFileLevelToken(logLevel);

    internal static string GetShortCategoryName(string categoryName)
    {
        return CliLogFormat.GetShortCategoryName(categoryName);
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

        // Deciding after the writer drains means late warnings and cancellation, such as a Ctrl+C
        // during host or telemetry shutdown, still keep the log.
        if (_ownsManagedLogFile && _discardOnDisposeUnlessCanceled is { IsCancellationRequested: false } && !_retainLogFile)
        {
            try
            {
                File.Delete(_logFilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The startup prune removes the file later.
            }
        }
    }

    /// <summary>
    /// Deletes the managed log file on dispose unless it is retained or cancellation was requested.
    /// </summary>
    /// <param name="cancellationToken">The run's cancellation token. It is checked at dispose time, so
    /// cancellation that arrives during shutdown keeps the log.</param>
    /// <remarks>
    /// Tools such as the VS Code extension run one-shot query commands like <c>aspire ps</c> repeatedly.
    /// Their logs are still written as the command runs, so a hang, crash, or kill leaves a log behind.
    /// Only a clean run's log is discarded. The log is retained when a warning, error, or exception is
    /// logged, or when <see cref="RetainLogFile"/> is called. Has no effect on a custom <c>--log-file</c>
    /// or on a file this provider did not create.
    /// </remarks>
    internal void DiscardLogFileOnDisposeUnlessRetained(CancellationToken cancellationToken)
    {
        _discardOnDisposeUnlessCanceled = cancellationToken;
    }

    /// <summary>
    /// Keeps the log file even if it is later marked for discard, for runs that record a side effect
    /// worth diagnosing, such as stopping processes, without logging a warning.
    /// </summary>
    internal void RetainLogFile()
    {
        _retainLogFile = true;
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
