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
}
