// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Publishing;

/// <summary>
/// Retains a bounded tail of build output while tracking the total number of lines observed.
/// </summary>
internal sealed class BuildOutputCapture(int maxRetainedLineCount = 256)
{
    private readonly object _lock = new();
    private readonly CircularBuffer<string> _retainedLines = new(maxRetainedLineCount);

    /// <summary>
    /// Gets the total number of stdout and stderr lines observed.
    /// </summary>
    public int TotalLineCount { get; private set; }

    /// <summary>
    /// Adds a line of build output to the retained tail.
    /// </summary>
    /// <param name="line">The output line.</param>
    public void Add(string line)
    {
        lock (_lock)
        {
            TotalLineCount++;
            _retainedLines.Add(line);
        }
    }

    /// <summary>
    /// Returns the retained output lines in order.
    /// </summary>
    /// <returns>The retained output lines.</returns>
    public string[] ToArray()
    {
        lock (_lock)
        {
            return [.. _retainedLines];
        }
    }

    /// <summary>
    /// Returns the last <paramref name="maxLines"/> lines of output formatted for display.
    /// </summary>
    /// <param name="maxLines">The maximum number of lines to include.</param>
    /// <param name="outputDescription">The label used when the output is truncated.</param>
    /// <returns>The formatted output.</returns>
    public string GetFormattedOutput(int maxLines = 50, string outputDescription = "Build output")
    {
        return FormatOutput(ToArray(), TotalLineCount, maxLines, outputDescription);
    }

    /// <summary>
    /// Formats retained output lines for display.
    /// </summary>
    /// <param name="output">The retained output lines.</param>
    /// <param name="totalLineCount">The total number of lines observed.</param>
    /// <param name="maxLines">The maximum number of lines to include.</param>
    /// <param name="outputDescription">The label used when the output is truncated.</param>
    /// <returns>The formatted output.</returns>
    internal static string FormatOutput(IReadOnlyList<string> output, int totalLineCount, int maxLines = 50, string outputDescription = "Build output")
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLines);

        if (output.Count == 0)
        {
            return string.Empty;
        }

        var linesShown = Math.Min(maxLines, output.Count);
        IEnumerable<string> lines = output.Skip(output.Count - linesShown);
        var formattedOutput = string.Join(Environment.NewLine, lines);

        if (totalLineCount > linesShown)
        {
            return $"{outputDescription} truncated: showing last {linesShown} of {totalLineCount} lines.{Environment.NewLine}{formattedOutput}";
        }

        return formattedOutput;
    }
}
