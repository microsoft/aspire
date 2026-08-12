// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Shared.ConsoleLogs;

internal sealed class PromptContext
{
    // Only store large and reference larger values.
    private const int MinReferenceLength = 256;

    private readonly Dictionary<string, string> _promptValueMap = new Dictionary<string, string>();

    /// <summary>
    /// Gets whether values should be processed (duplicate lines removed, duplicate values replaced with references).
    /// When <c>false</c>, <see cref="AddValue{T}"/> returns the input unchanged.
    /// </summary>
    public bool ProcessValues { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="PromptContext"/>.
    /// </summary>
    /// <param name="processValues">
    /// When <c>true</c> (the default), duplicate lines are removed and repeated values are replaced with
    /// back-references to save tokens. Set to <c>false</c> to emit raw values without processing.
    /// </param>
    public PromptContext(bool processValues = true)
    {
        ProcessValues = processValues;
    }

    public string? AddValue<T>(string? input, Func<T, string> getKey, T instance)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        if (!ProcessValues)
        {
            return input;
        }

        if (input.Length < MinReferenceLength)
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
