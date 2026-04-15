// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aspire.TestTools;

/// <summary>
/// Reads asciicast v2 (.cast) recording files and extracts plain text content.
/// </summary>
static partial class CastFileReader
{
    private const int MaxLines = 100;

    /// <summary>
    /// Reads the terminal recording text for a given test method name.
    /// Looks for a .cast file in the specified recordings directory,
    /// extracts the output event text, and strips ANSI escape codes.
    /// </summary>
    /// <returns>The extracted plain text, or <c>null</c> if no recording file was found.</returns>
    public static string? ReadRecordingText(string recordingsDir, string testMethodName)
    {
        if (!Directory.Exists(recordingsDir))
        {
            return null;
        }

        var castPath = Path.Combine(recordingsDir, $"{testMethodName}.cast");
        if (!File.Exists(castPath))
        {
            return null;
        }

        try
        {
            return ExtractTextFromCastFile(castPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Failed to read recording file {castPath}: {ex.Message}");
            return null;
        }
    }

    private static string? ExtractTextFromCastFile(string castPath)
    {
        var sb = new StringBuilder();
        var headerSkipped = false;

        foreach (var line in File.ReadLines(castPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (!headerSkipped)
            {
                headerSkipped = true;
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var arr = doc.RootElement;
                if (arr.ValueKind is not JsonValueKind.Array || arr.GetArrayLength() < 3)
                {
                    continue;
                }

                var eventType = arr[1].GetString();
                var data = arr[2].GetString();

                if (eventType is not "o" || data is null)
                {
                    continue;
                }

                sb.Append(data);
            }
            catch (JsonException)
            {
                // Skip malformed event lines.
            }
        }

        var rawText = sb.ToString();
        var plainText = StripAnsiEscapes(rawText);

        if (plainText.Length == 0)
        {
            return null;
        }

        // Keep only the last N lines so the summary shows the most recent output.
        var lines = plainText.Split('\n');
        if (lines.Length > MaxLines)
        {
            var tail = lines.AsSpan(lines.Length - MaxLines);
            plainText = $"… ({lines.Length - MaxLines} lines omitted)\n{string.Join('\n', tail.ToArray())}";
        }

        return plainText;
    }

    private static string StripAnsiEscapes(string text)
    {
        return AnsiEscapeRegex().Replace(text, string.Empty);
    }

    /// <summary>
    /// Matches ANSI escape sequences:
    /// - CSI sequences: ESC [ ... letter (e.g., colors, cursor movement)
    /// - OSC sequences: ESC ] ... BEL (e.g., terminal title)
    /// - Two-character escapes: ESC followed by a single character
    /// </summary>
    [GeneratedRegex(@"\x1b\[[0-9;?]*[A-Za-z]|\x1b\][^\x07]*\x07|\x1b[^[\]][^\x1b]?")]
    private static partial Regex AnsiEscapeRegex();
}
