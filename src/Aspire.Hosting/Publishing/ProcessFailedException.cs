// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Publishing;

/// <summary>
/// Exception thrown when a container image build or dotnet publish operation fails.
/// </summary>
internal sealed class ProcessFailedException : DistributedApplicationException
{
    /// <summary>
    /// Initializes a new instance of <see cref="ProcessFailedException"/>.
    /// </summary>
    /// <param name="message">A summary of the failure (e.g., "Docker build failed with exit code 1.").</param>
    /// <param name="exitCode">The process exit code.</param>
    /// <param name="processOutput">The retained stdout/stderr lines from the failed process.</param>
    /// <param name="totalProcessOutputLineCount">The total number of stdout/stderr lines observed.</param>
    public ProcessFailedException(string message, int exitCode, IReadOnlyList<string> processOutput, int? totalProcessOutputLineCount = null)
        : base(message)
    {
        ExitCode = exitCode;
        ProcessOutput = processOutput;
        TotalProcessOutputLineCount = totalProcessOutputLineCount ?? processOutput.Count;
    }

    /// <summary>
    /// The process exit code.
    /// </summary>
    public int ExitCode { get; }

    /// <summary>
    /// The retained stdout/stderr lines from the failed process.
    /// </summary>
    public IReadOnlyList<string> ProcessOutput { get; }

    /// <summary>
    /// The total number of stdout/stderr lines observed for the failed process.
    /// </summary>
    public int TotalProcessOutputLineCount { get; }

    /// <inheritdoc/>
    public override string Message => ProcessOutput.Count > 0
        ? $"{base.Message}{Environment.NewLine}{GetFormattedOutput()}"
        : base.Message;

    /// <summary>
    /// Returns the last <paramref name="maxLines"/> lines of process output formatted for display.
    /// </summary>
    public string GetFormattedOutput(int maxLines = 50)
    {
        return ProcessOutputCapture.FormatOutput(ProcessOutput, TotalProcessOutputLineCount, maxLines, outputDescription: "Process output");
    }
}
