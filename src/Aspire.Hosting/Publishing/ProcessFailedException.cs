// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
    /// <param name="buildOutput">The retained stdout/stderr lines from the build process.</param>
    /// <param name="totalBuildOutputLineCount">The total number of stdout/stderr lines observed.</param>
    public ProcessFailedException(string message, int exitCode, IReadOnlyList<string> buildOutput, int? totalBuildOutputLineCount = null)
        : base(message)
    {
        ExitCode = exitCode;
        BuildOutput = buildOutput;
        TotalBuildOutputLineCount = totalBuildOutputLineCount ?? buildOutput.Count;
    }

    /// <summary>
    /// The process exit code.
    /// </summary>
    public int ExitCode { get; }

    /// <summary>
    /// The retained stdout/stderr lines from the build process.
    /// </summary>
    public IReadOnlyList<string> BuildOutput { get; }

    /// <summary>
    /// The total number of stdout/stderr lines observed for the failed process.
    /// </summary>
    public int TotalBuildOutputLineCount { get; }

    /// <inheritdoc/>
    public override string Message => BuildOutput.Count > 0
        ? $"{base.Message}{Environment.NewLine}{GetFormattedOutput()}"
        : base.Message;

    /// <summary>
    /// Returns the last <paramref name="maxLines"/> lines of build output formatted for display.
    /// </summary>
    public string GetFormattedOutput(int maxLines = 50)
    {
        return BuildOutputCapture.FormatOutput(BuildOutput, TotalBuildOutputLineCount, maxLines);
    }
}
