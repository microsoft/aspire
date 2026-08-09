// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Utils;

/// <summary>
/// Retains a bounded tail of process output while tracking the total number of lines observed.
/// </summary>
internal sealed class ProcessOutputCapture(int maxRetainedLineCount)
{
    private readonly object _lock = new();
    private readonly Queue<string> _retainedLines = new();
    private readonly int _maxRetainedLineCount = maxRetainedLineCount;

    /// <summary>
    /// Gets the total number of stdout and stderr lines observed.
    /// </summary>
    public int TotalLineCount { get; private set; }

    /// <summary>
    /// Adds a line of process output to the retained tail.
    /// </summary>
    /// <param name="line">The output line.</param>
    public void Add(string line)
    {
        lock (_lock)
        {
            TotalLineCount++;
            _retainedLines.Enqueue(line);

            while (_retainedLines.Count > _maxRetainedLineCount)
            {
                _retainedLines.Dequeue();
            }
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
    public string GetFormattedOutput(int maxLines = 50, string outputDescription = "Command output")
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
    internal static string FormatOutput(IReadOnlyList<string> output, int totalLineCount, int maxLines = 50, string outputDescription = "Command output")
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
