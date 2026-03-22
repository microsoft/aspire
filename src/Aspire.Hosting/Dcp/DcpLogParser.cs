// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dcp;

/// <summary>
/// Helper class for parsing DCP-formatted log lines.
/// DCP log format: &lt;date&gt;\t&lt;level&gt;\t&lt;category&gt;\t&lt;log message&gt;
/// </summary>
internal static class DcpLogParser
{
    /// <summary>
    /// Tries to parse a DCP-formatted log line.
    /// </summary>
    /// <param name="line">The log line to parse (as bytes).</param>
    /// <param name="message">The extracted message.</param>
    /// <param name="logLevel">The extracted log level.</param>
    /// <param name="category">The extracted category.</param>
    /// <returns>True if the line was successfully parsed as a DCP log; false otherwise.</returns>
    public static bool TryParseDcpLog(ReadOnlySpan<byte> line, out string message, out LogLevel logLevel, out string category)
    {
        message = string.Empty;
        logLevel = LogLevel.Information;
        category = string.Empty;

        try
        {
            // The log format is
            // <date>\t<level>\t<category>\t<log message>
            // e.g. 2023-09-19T20:40:50.509-0700      info    dcp.ServiceReconciler       service /apigateway is now in state Ready       {"ServiceName": {"name":"apigateway"}}

            var tab = line.IndexOf((byte)'\t');
            if (tab < 0)
            {
                return false;
            }

            // Skip date
            line = line[(tab + 1)..];

            tab = line.IndexOf((byte)'\t');
            if (tab < 0)
            {
                return false;
            }

            var level = line[..tab];
            line = line[(tab + 1)..];

            tab = line.IndexOf((byte)'\t');
            if (tab < 0)
            {
                return false;
            }

            var categorySpan = line[..tab];
            line = line[(tab + 1)..];

            // Trim trailing carriage return.
            if (line.Length > 0 && line[^1] == '\r')
            {
                line = line[0..^1];
            }

            var messageSpan = line;

            // Parse log level
            if (level.SequenceEqual("info"u8))
            {
                logLevel = LogLevel.Information;
            }
            else if (level.SequenceEqual("error"u8))
            {
                logLevel = LogLevel.Error;
            }
            else if (level.SequenceEqual("warning"u8))
            {
                logLevel = LogLevel.Warning;
            }
            else if (level.SequenceEqual("debug"u8))
            {
                logLevel = LogLevel.Debug;
            }
            else if (level.SequenceEqual("trace"u8))
            {
                logLevel = LogLevel.Trace;
            }

            message = Encoding.UTF8.GetString(messageSpan);
            category = Encoding.UTF8.GetString(categorySpan);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Tries to parse a DCP-formatted log line from a string.
    /// </summary>
    /// <param name="line">The log line to parse (as string).</param>
    /// <param name="message">The extracted message.</param>
    /// <param name="logLevel">The extracted log level.</param>
    /// <param name="isErrorLevel">True if the log level indicates an error.</param>
    /// <returns>True if the line was successfully parsed as a DCP log; false otherwise.</returns>
    public static bool TryParseDcpLog(string line, out string message, out LogLevel logLevel, out bool isErrorLevel)
    {
        var bytes = Encoding.UTF8.GetBytes(line);
        var result = TryParseDcpLog(bytes.AsSpan(), out message, out logLevel, out _);
        isErrorLevel = logLevel == LogLevel.Error;
        return result;
    }

    /// <summary>
    /// Formats a system-level log message by parsing JSON metadata and applying the [sys] prefix format.
    /// </summary>
    /// <param name="message">The raw message which may contain a text portion and JSON metadata.</param>
    /// <returns>The formatted message with [sys] prefix and human-readable format.</returns>
    public static string FormatSystemLog(string message)
    {
        const string SystemLogPrefix = "[sys] ";

        // Try to find JSON portion in the message (starts with a tab followed by '{')
        var jsonStart = message.IndexOf('\t');
        if (jsonStart < 0)
        {
            // No JSON metadata, return message as-is with [sys] prefix
            return $"{SystemLogPrefix}{message}";
        }

        var textPart = message[..jsonStart];
        var jsonPart = message[(jsonStart + 1)..];

        // Try to parse the JSON metadata
        try
        {
            using var doc = JsonDocument.Parse(jsonPart);
            var root = doc.RootElement;

            // Build the formatted message
            var sb = new StringBuilder();
            sb.Append(SystemLogPrefix);

            // Add the text part if it exists
            if (!string.IsNullOrWhiteSpace(textPart))
            {
                sb.Append(textPart);
            }

            // Extract and add JSON fields in a loop
            var fields = new (string Name, string? Value)[]
            {
                ("Cmd", root.TryGetProperty("Cmd", out var cmdProp) ? cmdProp.GetString() : null),
                ("Args", root.TryGetProperty("Args", out var argsProp) ? argsProp.ToString() : null),
                ("ContainerName", root.TryGetProperty("ContainerName", out var containerNameProp) ? containerNameProp.GetString() : null),
                ("ContainerId", root.TryGetProperty("ContainerID", out var containerIdProp) ? containerIdProp.GetString() : null),
                ("Error", root.TryGetProperty("error", out var errorProp) ? errorProp.GetString() : null)
            };

            var hasAddedField = false;
            foreach (var (name, value) in fields)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    // Handle multi-line values
                    if (value.Contains('\n'))
                    {
                        if (sb.Length > SystemLogPrefix.Length || hasAddedField)
                        {
                            sb.Append(':');
                        }
                        sb.Append('\n');
                        // Prefix each line with [sys]
                        var lines = value.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                        for (int i = 0; i < lines.Length; i++)
                        {
                            sb.Append(SystemLogPrefix);
                            sb.Append(lines[i].Trim());
                            // Only add newline if not the last line
                            if (i < lines.Length - 1)
                            {
                                sb.Append('\n');
                            }
                        }
                    }
                    else
                    {
                        // Add delimiter
                        if (sb.Length > SystemLogPrefix.Length)
                        {
                            sb.Append(hasAddedField ? ", " : ": ");
                        }

                        // Add field in format "Name = Value"
                        sb.Append(name);
                        sb.Append(" = ");
                        sb.Append(value);
                        hasAddedField = true;
                    }
                }
            }

            return sb.ToString();
        }
        catch
        {
            // If JSON parsing fails, return the original message with [sys] prefix
            return $"{SystemLogPrefix}{message}";
        }
    }
}
