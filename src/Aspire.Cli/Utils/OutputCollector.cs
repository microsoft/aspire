// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Utils;

internal enum OutputLineStream
{
    StdOut,
    StdErr
}

internal sealed class OutputCollector
{
    private readonly CircularBuffer<(OutputLineStream Stream, string Line)> _lines = new(10000); // 10k lines.
    private readonly object _lock = new object();
    private readonly FileLoggerProvider? _fileLogger;
    private readonly string _category;

    /// <summary>
    /// Creates an OutputCollector that only buffers output in memory.
    /// </summary>
    public OutputCollector() : this(null, "AppHost")
    {
    }

    /// <summary>
    /// Creates an OutputCollector that buffers output and optionally logs to disk.
    /// </summary>
    /// <param name="fileLogger">Optional file logger for writing output to disk.</param>
    /// <param name="category">Category for log entries (e.g., "Build", "AppHost").</param>
    public OutputCollector(FileLoggerProvider? fileLogger, string category = "AppHost")
    {
        _fileLogger = fileLogger;
        _category = category;
    }

    public void AppendOutput(string line)
    {
        AppendLine(OutputLineStream.StdOut, line);
    }

    public void AppendError(string line)
    {
        AppendLine(OutputLineStream.StdErr, line);
    }

    public IEnumerable<(OutputLineStream Stream, string Line)> GetLines()
    {
        lock (_lock)
        {
            return _lines.ToArray();
        }
    }

    private void AppendLine(OutputLineStream stream, string line)
    {
        lock (_lock)
        {
            _lines.Add((stream, line));
            _fileLogger?.WriteLog(DateTimeOffset.UtcNow, stream == OutputLineStream.StdErr ? LogLevel.Error : LogLevel.Information, _category, line);
        }
    }
}
