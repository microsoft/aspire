// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Shared.ConsoleLogs;

internal sealed class PromptContext
{
    // Only store large and reference larger values.
    private const int MinReferenceLength = 256;

    private readonly Dictionary<string, string> _promptValueMap = new Dictionary<string, string>();

    public string? AddValue<T>(string? input, Func<T, string> getKey, T instance)
    {
        if (string.IsNullOrEmpty(input) || input.Length < MinReferenceLength)
        {
            return input;
        }

        input = RemoveDuplicateLines(input);
        input = SharedAIHelpers.LimitLength(input);

        if (!_promptValueMap.TryGetValue(input, out var reference))
        {
            _promptValueMap[input] = getKey(instance);
            return input;
        }

        return reference;
    }

    private static string RemoveDuplicateLines(string input)
    {
        var lines = input.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        if (lines.Length == 1)
        {
            return lines[0];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var uniqueLines = new List<string>();

        foreach (var line in lines)
        {
            if (seen.Add(line)) // Add returns false if the line already exists
            {
                uniqueLines.Add(line);
            }
        }

        var value = string.Join(Environment.NewLine, uniqueLines);
        return value;
    }
}
