// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Provides a narrow logging surface for polyglot callback contexts.
/// </summary>
[AspireExport]
internal sealed class LogFacade
{
    private readonly Func<ILogger> _loggerAccessor;

    public LogFacade(ILogger logger)
        : this(() => logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
    }

    public LogFacade(Func<ILogger> loggerAccessor)
    {
        _loggerAccessor = loggerAccessor ?? throw new ArgumentNullException(nameof(loggerAccessor));
    }

    /// <summary>
    /// Writes an informational log message.
    /// </summary>
    /// <param name="message">The message to write.</param>
    [AspireExport(Description = "Writes an informational log message")]
    public void Info(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _loggerAccessor().LogInformation(message);
    }

    /// <summary>
    /// Writes a warning log message.
    /// </summary>
    /// <param name="message">The message to write.</param>
    [AspireExport(Description = "Writes a warning log message")]
    public void Warning(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _loggerAccessor().LogWarning(message);
    }

    /// <summary>
    /// Writes an error log message.
    /// </summary>
    /// <param name="message">The message to write.</param>
    [AspireExport(Description = "Writes an error log message")]
    public void Error(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _loggerAccessor().LogError(message);
    }

    /// <summary>
    /// Writes a debug log message.
    /// </summary>
    /// <param name="message">The message to write.</param>
    [AspireExport(Description = "Writes a debug log message")]
    public void Debug(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _loggerAccessor().LogDebug(message);
    }
}
