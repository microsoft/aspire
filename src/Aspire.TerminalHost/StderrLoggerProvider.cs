// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.TerminalHost;

/// <summary>
/// Minimal logger provider that writes a single line per record to stderr.
/// Used by the terminal host so it doesn't pull in
/// Microsoft.Extensions.Logging.Console (which is not centrally managed in this repo).
/// </summary>
internal sealed class StderrLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new StderrLogger(categoryName);

    public void Dispose()
    {
    }

    private sealed class StderrLogger(string category) : ILogger
    {
        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var ts = DateTimeOffset.UtcNow.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
            var msg = formatter(state, exception);
            var line = $"{ts} [{logLevel,-11}] {category}: {msg}";

            try
            {
                Console.Error.WriteLine(line);
                if (exception is not null)
                {
                    Console.Error.WriteLine(exception);
                }
            }
            catch
            {
                // stderr unavailable — give up silently rather than tearing down the host.
            }
        }
    }
}
