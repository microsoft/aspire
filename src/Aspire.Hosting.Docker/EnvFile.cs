// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Docker;

internal sealed record EnvEntry(string Key, string? Value, string? Comment);

internal sealed class EnvFile
{
    private string? _path;
    private readonly ILogger? _logger;

    internal SortedDictionary<string, EnvEntry> Entries { get; } = [];

    private EnvFile(ILogger? logger = null)
    {
        _logger = logger;
    }

    public static EnvFile Create(string path, ILogger? logger = null)
    {
        return new EnvFile(logger) { _path = path };
    }

    public static EnvFile Load(string path, ILogger? logger = null)
    {
        var envFile = new EnvFile(logger) { _path = path };
        if (!File.Exists(path))
        {
            return envFile;
        }

        string? currentComment = null;

        var content = File.ReadAllText(path);
        var position = 0;
        while (ReadLine(content, ref position, out var lineEnding) is { } line)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#'))
            {
                // Extract comment text (remove # and trim)
                currentComment = trimmed.Length > 1 ? trimmed[1..].Trim() : string.Empty;
            }
            else if (TryParseKeyValue(line, out var key, out var value))
            {
                var trimmedValue = value.AsSpan().TrimStart();
                if (!trimmedValue.IsEmpty && trimmedValue[0] is '\'' or '"')
                {
                    var quote = trimmedValue[0];
                    if (!ContainsClosingQuote(trimmedValue[1..], quote))
                    {
                        // Compose accepts values such as BANNER='hello\nworld'. Keep the raw
                        // quoted text, including blank lines, '#' and '=', so rewriting does
                        // not interpret value content as comments or additional entries.
                        // https://docs.docker.com/compose/how-tos/environment-variables/variable-interpolation/#env-file-syntax
                        var multilineValue = new StringBuilder(value);
                        while (true)
                        {
                            multilineValue.Append(lineEnding);
                            var continuation = ReadLine(content, ref position, out lineEnding);
                            if (continuation is null)
                            {
                                throw new FormatException($"Unterminated quoted value for environment variable '{key}'.");
                            }

                            multilineValue.Append(continuation);
                            if (ContainsClosingQuote(continuation, quote))
                            {
                                break;
                            }
                        }

                        value = multilineValue.ToString();
                    }
                }

                envFile.Entries[key] = new EnvEntry(key, value, currentComment);
                currentComment = null; // Reset comment after associating it with a key
            }
            else
            {
                // Reset comment if we encounter a non-comment, non-key line
                currentComment = null;
            }
        }
        return envFile;
    }

    public void Add(string key, string? value, string? comment, bool onlyIfMissing = true)
    {
        if (Entries.ContainsKey(key) && onlyIfMissing)
        {
            return;
        }

        Entries[key] = new EnvEntry(key, value, comment);
    }

    private static bool TryParseKeyValue(string line, out string key, out string? value)
    {
        key = string.Empty;
        value = null;
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith('#') && trimmed.Contains('='))
        {
            var eqIndex = trimmed.IndexOf('=');
            if (eqIndex > 0)
            {
                key = trimmed[..eqIndex].Trim();
                value = eqIndex < trimmed.Length - 1 ? trimmed[(eqIndex + 1)..] : string.Empty;
                return true;
            }
        }
        return false;
    }

    private static bool ContainsClosingQuote(ReadOnlySpan<char> value, char quote)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\')
            {
                // A backslash escapes the next character, including a matching quote
                // or another backslash. An escaped quote cannot end the value.
                i++;
            }
            else if (value[i] == quote)
            {
                return true;
            }
        }

        return false;
    }

    private static string? ReadLine(string content, ref int position, out string lineEnding)
    {
        lineEnding = string.Empty;
        if (position >= content.Length)
        {
            return null;
        }

        var start = position;
        var relativeEnd = content.AsSpan(start).IndexOfAny('\r', '\n');
        if (relativeEnd < 0)
        {
            position = content.Length;
            return content[start..];
        }

        var end = start + relativeEnd;
        var newlineLength = content[end] == '\r' && end + 1 < content.Length && content[end + 1] == '\n' ? 2 : 1;
        position = end + newlineLength;
        // Preserve the original separator inside quoted values. Compose treats a
        // carriage return as value content, so using Environment.NewLine can change it.
        lineEnding = content[end..position];

        return content[start..end];
    }

    public void Save()
    {
        if (_path is null)
        {
            throw new InvalidOperationException("Cannot save EnvFile without a path. Use Load() to create an EnvFile with a path.");
        }

        // Log if we're about to overwrite an existing file
        if (File.Exists(_path))
        {
            _logger?.LogInformation("Environment file '{EnvFilePath}' already exists and will be overwritten", _path);
        }

        var lines = new List<string>();

        foreach (var entry in Entries.Values)
        {
            if (!string.IsNullOrWhiteSpace(entry.Comment))
            {
                lines.Add($"# {entry.Comment}");
            }
            lines.Add(entry.Value is not null ? $"{entry.Key}={entry.Value}" : $"{entry.Key}=");
            lines.Add(string.Empty);
        }

        File.WriteAllLines(_path, lines);
    }

    public void Save(bool includeValues)
    {
        if (includeValues)
        {
            Save();
        }
        else
        {
            SaveKeysOnly();
        }
    }

    private void SaveKeysOnly()
    {
        if (_path is null)
        {
            throw new InvalidOperationException("Cannot save EnvFile without a path. Use Load() to create an EnvFile with a path.");
        }

        var lines = new List<string>();

        foreach (var entry in Entries.Values)
        {
            if (!string.IsNullOrWhiteSpace(entry.Comment))
            {
                lines.Add($"# {entry.Comment}");
            }

            // If the entry already has a non-empty value (loaded from disk), preserve it
            // This ensures user-modified values are not overwritten when we save keys only
            if (!string.IsNullOrEmpty(entry.Value))
            {
                lines.Add($"{entry.Key}={entry.Value}");
            }
            else
            {
                lines.Add($"{entry.Key}=");
            }

            lines.Add(string.Empty);
        }

        File.WriteAllLines(_path, lines);
    }
}
