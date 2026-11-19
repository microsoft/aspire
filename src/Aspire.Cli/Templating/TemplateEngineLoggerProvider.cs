// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Templating;

internal sealed class TemplateEngineLoggerProvider(ILogger logger) : ILoggerProvider, ILogger
{
    private readonly ConcurrentQueue<string> _errors = new();

    public string[] Errors => _errors.ToArray();

    public ILogger CreateLogger(string categoryName) => this;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => logger.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) =>
        logLevel != LogLevel.None && (logLevel >= LogLevel.Error || logger.IsEnabled(logLevel));

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        logger.Log(logLevel, eventId, state, exception, formatter);
        if (logLevel >= LogLevel.Error)
        {
            var message = formatter(state, exception);
            _errors.Enqueue(exception is null ? message : $"{message}{Environment.NewLine}{exception.Message}");
        }
    }

    public void Dispose()
    {
    }
}
