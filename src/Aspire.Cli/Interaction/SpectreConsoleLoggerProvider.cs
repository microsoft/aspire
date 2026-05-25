// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Interaction;

internal class SpectreConsoleLoggerProvider : ILoggerProvider
{
    private readonly TextWriter _output;
    private readonly ConsoleLogBufferContext _bufferContext;

    /// <summary>
    /// Creates a logger provider that writes to the specified output.
    /// </summary>
    /// <param name="output">The text writer to write log messages to.</param>
    /// <param name="bufferContext">Shared buffer context for pausing logs during interactive prompts.</param>
    public SpectreConsoleLoggerProvider(TextWriter output, ConsoleLogBufferContext bufferContext)
    {
        _output = output;
        _bufferContext = bufferContext;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new SpectreConsoleLogger(_output, categoryName, _bufferContext);
    }

    public void Dispose()
    {
    }
}

internal class SpectreConsoleLogger(TextWriter output, string categoryName, ConsoleLogBufferContext bufferContext) : ILogger
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
        var shortCategoryName = CliLogFormat.GetShortCategoryName(categoryName);

        // Format timestamp to show only time (HH:mm:ss) for debugging purposes
        var timestamp = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        var logMessage = $"[{timestamp}] [{GetLogLevelString(logLevel)}] {shortCategoryName}: {formattedMessage}";

        // Write to the configured output (stderr by default)
        bufferContext.WriteOrBuffer(output, logMessage);
    }

    private static string GetLogLevelString(LogLevel logLevel) => CliLogFormat.GetConsoleLevelToken(logLevel);
}
