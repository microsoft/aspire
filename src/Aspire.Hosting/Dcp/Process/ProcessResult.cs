// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Dcp.Process;

internal sealed class ProcessResult
{
    public ProcessResult(int exitCode, IReadOnlyList<string>? processOutput = null, int? totalProcessOutputLineCount = null)
    {
        ExitCode = exitCode;
        ProcessOutput = processOutput ?? [];
        TotalProcessOutputLineCount = totalProcessOutputLineCount ?? ProcessOutput.Count;
    }

    public int ExitCode { get; }

    public IReadOnlyList<string> ProcessOutput { get; }

    public int TotalProcessOutputLineCount { get; }

    public string GetFormattedOutput(int maxLines = 50, string outputDescription = "Command output")
    {
        return ProcessOutputCapture.FormatOutput(ProcessOutput, TotalProcessOutputLineCount, maxLines, outputDescription);
    }
}
