// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Ats;

/// <summary>
/// ATS exports for logging operations.
/// </summary>
internal static class LoggingExports
{
    /// <summary>
    /// Gets the logger factory from the service provider.
    /// </summary>
    /// <param name="serviceProvider">The service provider handle.</param>
    /// <returns>A logger factory handle.</returns>
    [AspireExport("getLoggerFactory", Description = "Gets the logger factory from the service provider")]
    public static ILoggerFactory GetLoggerFactory(this IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        return serviceProvider.GetRequiredService<ILoggerFactory>();
    }

    /// <summary>
    /// Logs an information message.
    /// </summary>
    [AspireExport("logInformation", Description = "Logs an information message")]
    public static void LogInformation(this ILogger logger, string message)
    {
        logger.LogInformation("{Message}", message);
    }

    /// <summary>
    /// Logs a warning message.
    /// </summary>
    [AspireExport("logWarning", Description = "Logs a warning message")]
    public static void LogWarning(this ILogger logger, string message)
    {
        logger.LogWarning("{Message}", message);
    }

    /// <summary>
    /// Logs an error message.
    /// </summary>
    [AspireExport("logError", Description = "Logs an error message")]
    public static void LogError(this ILogger logger, string message)
    {
        logger.LogError("{Message}", message);
    }

    /// <summary>
    /// Logs a debug message.
    /// </summary>
    [AspireExport("logDebug", Description = "Logs a debug message")]
    public static void LogDebug(this ILogger logger, string message)
    {
        logger.LogDebug("{Message}", message);
    }

    /// <summary>
    /// Logs a message with a specified log level.
    /// </summary>
    [AspireExport("log", Description = "Logs a message with specified level")]
    public static void Log(this ILogger logger, string level, string message)
    {
        logger.Log(ParseLogLevel(level), "{Message}", message);
    }

    /// <summary>
    /// Creates a logger for the specified category name.
    /// </summary>
    /// <param name="loggerFactory">The logger factory handle.</param>
    /// <param name="categoryName">The category name.</param>
    /// <returns>A logger handle.</returns>
    [AspireExport("createLogger", Description = "Creates a logger for a category")]
    public static ILogger CreateLogger(this ILoggerFactory loggerFactory, string categoryName)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryName);

        return loggerFactory.CreateLogger(categoryName);
    }

    /// <summary>
    /// Gets the resource logger service from the service provider.
    /// </summary>
    /// <param name="serviceProvider">The service provider handle.</param>
    /// <returns>A resource logger service handle.</returns>
    [AspireExport("getResourceLoggerService", Description = "Gets the resource logger service from the service provider")]
    public static ResourceLoggerService GetResourceLoggerService(this IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        return serviceProvider.GetRequiredService<ResourceLoggerService>();
    }

    /// <summary>
    /// Completes the log stream for a resource.
    /// </summary>
    [AspireExport("completeLog", Description = "Completes the log stream for a resource")]
    public static void CompleteLog(this ResourceLoggerService loggerService, IResourceBuilder<IResource> resource)
    {
        loggerService.Complete(resource.Resource);
    }

    /// <summary>
    /// Completes the log stream by resource name.
    /// </summary>
    [AspireExport("completeLogByName", Description = "Completes the log stream by resource name")]
    public static void CompleteLogByName(this ResourceLoggerService loggerService, string resourceName)
    {
        loggerService.Complete(resourceName);
    }

    internal static LogLevel ParseLogLevel(string level, bool throwOnUnknown = false)
    {
        ArgumentNullException.ThrowIfNull(level);

        return level.ToLowerInvariant() switch
        {
            "trace" => LogLevel.Trace,
            "debug" => LogLevel.Debug,
            "information" or "info" => LogLevel.Information,
            "warning" or "warn" => LogLevel.Warning,
            "error" => LogLevel.Error,
            "critical" => LogLevel.Critical,
            _ when throwOnUnknown => throw new ArgumentOutOfRangeException(nameof(level), level, "Unsupported log level."),
            _ => LogLevel.Information
        };
    }
}
