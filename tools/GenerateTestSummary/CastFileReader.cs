// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aspire.TestTools;

/// <summary>
/// Reads asciicast v2 (.cast) recording files and extracts terminal output text.
/// Only SGR color/style codes (<c>\e[...m</c>) are preserved for rendering inside
/// a <c>```ansi</c> fenced code block in GitHub markdown. All other escape sequences
/// (cursor movement, private modes, OSC, etc.) are stripped.
/// </summary>
static partial class CastFileReader
{
    private const int MaxLines = 100;

    /// <summary>
    /// Reads the terminal recording text for a given test method name.
    /// Looks for a .cast file in the specified recordings directory and
    /// extracts the output event text, preserving ANSI escape codes.
    /// </summary>
    /// <returns>The extracted text (with ANSI codes), or <c>null</c> if no recording file was found.</returns>
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

        var text = StripNonSgrEscapes(sb.ToString());

        if (text.Length == 0)
        {
            return null;
        }

        // Keep only the last N lines so the summary shows the most recent output.
        var lines = text.Split('\n');
        if (lines.Length > MaxLines)
        {
            var tail = lines.AsSpan(lines.Length - MaxLines);
            text = $"… ({lines.Length - MaxLines} lines omitted)\n{string.Join('\n', tail.ToArray())}";
        }

        return text;
    }

    /// <summary>
    /// Strips all ANSI escape sequences except SGR (Select Graphic Rendition)
    /// codes which control color and text style (<c>\e[...m</c>).
    /// GitHub's <c>```ansi</c> block only renders SGR; other sequences
    /// (cursor movement, private modes, OSC, etc.) render as garbage.
    /// </summary>
    private static string StripNonSgrEscapes(string text)
    {
        return NonSgrEscapeRegex().Replace(text, string.Empty);
    }

    /// <summary>
    /// Matches ANSI escape sequences that are NOT SGR (<c>\e[...m</c>):
    /// <list type="bullet">
    /// <item>CSI sequences ending in any letter except 'm': <c>\e[...X</c> (cursor, erase, scroll, modes)</item>
    /// <item>CSI private mode sequences: <c>\e[?...h</c>, <c>\e[?...l</c>, etc.</item>
    /// <item>OSC sequences: <c>\e]...BEL</c> or <c>\e]...\e\\</c></item>
    /// <item>Two-character escapes: <c>\eX</c> where X is not '[' or ']'</item>
    /// </list>
    /// </summary>
    [GeneratedRegex(@"\x1b\[\?[0-9;]*[A-Za-z]|\x1b\[[0-9;]*[A-La-lN-Zn-z]|\x1b\][^\x07]*(?:\x07|\x1b\\)|\x1b[^[\]m][^\x1b]?")]
    private static partial Regex NonSgrEscapeRegex();
}
