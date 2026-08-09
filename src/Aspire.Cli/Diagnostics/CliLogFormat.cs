// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Diagnostics;

internal static class CliLogFormat
{
    internal readonly record struct FileLogEntry(string Level, string Category, string Message);

    internal static class Categories
    {
        internal const string Stdout = "Stdout";
        internal const string Stderr = "Stderr";
        internal const string AppHost = "AppHost";
        internal const string AppHostPrefix = AppHost + "/";
        internal const string DetachedAppHostPrefix = "DetachedAppHost/";
        internal const string Build = "Build";
        internal const string Dashboard = "Dashboard";
        internal const string Package = "Package";
        internal const string GuestAppHostProject = nameof(Projects.GuestAppHostProject);
        internal const string AspireCliTelemetry = nameof(Telemetry.AspireCliTelemetry);
    }

    internal static class FileLevelTokens
    {
        internal const string Trace = "TRCE";
        internal const string Debug = "DBUG";
        internal const string Information = "INFO";
        internal const string Warning = "WARN";
        internal const string Error = "FAIL";
        internal const string Critical = "CRIT";
    }

    internal static class ConsoleLevelTokens
    {
        internal const string Debug = "dbug";
        internal const string Information = "info";
        internal const string Warning = "warn";
        internal const string Error = "fail";
        internal const string Critical = "crit";
    }

    internal static class MessagePrefixes
    {
        internal const string Executing = "Executing: ";
    }

    internal static string GetFileLevelToken(LogLevel logLevel) => logLevel switch
    {
        LogLevel.Trace => FileLevelTokens.Trace,
        LogLevel.Debug => FileLevelTokens.Debug,
        LogLevel.Information => FileLevelTokens.Information,
        LogLevel.Warning => FileLevelTokens.Warning,
        LogLevel.Error => FileLevelTokens.Error,
        LogLevel.Critical => FileLevelTokens.Critical,
        _ => logLevel.ToString().ToUpperInvariant()
    };

    internal static string GetConsoleLevelToken(LogLevel logLevel) => logLevel switch
    {
        LogLevel.Debug => ConsoleLevelTokens.Debug,
        LogLevel.Information => ConsoleLevelTokens.Information,
        LogLevel.Warning => ConsoleLevelTokens.Warning,
        LogLevel.Error => ConsoleLevelTokens.Error,
        LogLevel.Critical => ConsoleLevelTokens.Critical,
        _ => logLevel.ToString().ToLowerInvariant()
    };

    internal static bool TryGetLogLevelFromFileToken(string token, out LogLevel logLevel)
    {
        logLevel = token switch
        {
            FileLevelTokens.Trace => LogLevel.Trace,
            FileLevelTokens.Debug => LogLevel.Debug,
            FileLevelTokens.Information => LogLevel.Information,
            FileLevelTokens.Warning => LogLevel.Warning,
            FileLevelTokens.Error => LogLevel.Error,
            FileLevelTokens.Critical => LogLevel.Critical,
            _ => LogLevel.None
        };

        return logLevel is not LogLevel.None;
    }

    internal static string GetShortCategoryName(string categoryName)
    {
        var lastDotIndex = categoryName.LastIndexOf('.');
        return lastDotIndex >= 0 ? categoryName.Substring(lastDotIndex + 1) : categoryName;
    }

    internal static string GetDetachedAppHostCategory(string categoryName) => Categories.DetachedAppHostPrefix + categoryName;

    internal static bool TryParseFileLogLine(string line, out FileLogEntry entry)
    {
        entry = default;

        if (!line.StartsWith('['))
        {
            return false;
        }

        // Parse file log lines written by FileLoggerProvider as:
        //   [2026-05-15 17:07:30.501] [INFO] [AppHost] apphost.ts(5,22): error TS1109: Expression expected.
        // The message can contain additional brackets or delimiters, so only consume
        // the fixed timestamp, level, and category segments from the front of the line.
        var timestampEnd = line.IndexOf("] [", StringComparison.Ordinal);
        if (timestampEnd < 0)
        {
            return false;
        }

        var levelStart = timestampEnd + 3;
        var levelEnd = line.IndexOf(']', levelStart);
        if (levelEnd < 0 || levelEnd + 3 > line.Length)
        {
            return false;
        }

        var categoryStart = levelEnd + 3;
        var categoryEnd = line.IndexOf(']', categoryStart);
        if (categoryEnd < 0 || categoryEnd + 2 > line.Length)
        {
            return false;
        }

        entry = new FileLogEntry(
            line[levelStart..levelEnd],
            line[categoryStart..categoryEnd],
            line[(categoryEnd + 2)..]);
        return true;
    }
}
