// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;

namespace AnalyzeCiFailure;

/// <summary>Owns invocation output directories, temporary scratch, captured facts and diagnostic finalization.</summary>
internal sealed class AnalysisInvocation : IDisposable
{
    internal string RootDirectory { get; } = Directory.CreateTempSubdirectory("aspire-ci-invocation-").FullName;
    internal AnalysisScratchWorkspace Scratch { get; }
    internal EvidencePaths Evidence { get; }
    internal EvidencePaths Replay { get; }
    internal InvocationCapture Capture { get; } = new();
    internal AnalysisProgress Progress { get; } = new();
    internal string DiagnosticsDirectory => Path.Combine(RootDirectory, "diagnostics");

    internal AnalysisInvocation()
    {
        Scratch = new AnalysisScratchWorkspace(Path.Combine(RootDirectory, "scratch"));
        Evidence = new EvidencePaths(Path.Combine(RootDirectory, "evidence"));
        Replay = new EvidencePaths(Path.Combine(RootDirectory, "replay"));
        Progress.Diagnostics.EnableProgress(DiagnosticsDirectory);
    }

    internal string RecoveryDirectory(string stage) => Path.Combine(RootDirectory, "recovery", stage);

    internal async Task WritePathsAsync(string stage, string? githubOutput)
    {
        var lines = $"workspace_dir={RootDirectory}\n" +
            $"evidence_dir={Evidence.RootDirectory}\nreplay_dir={Replay.RootDirectory}\nrecovery_dir={RecoveryDirectory(stage)}\n" +
            $"diagnostics_dir={DiagnosticsDirectory}\n";
        // Write paths before work begins, including failed/cancelled acquisition.
        // Upload steps consume runner-local paths; only artifacts cross job boundaries.
        if (lines.Contains('\r') || RootDirectory.Contains('\n'))
        {
            throw new ValidationException("Invocation paths must not contain newlines.");
        }
        Console.Write(lines);
        if (githubOutput is not null)
        {
            try
            {
                await File.AppendAllTextAsync(githubOutput, lines, CancellationToken.None);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Capture.Failure = error;
                Progress.RecordProblem(AnalysisProblemCategory.Fatal, "path-output", error.Message);
                await WriteDiagnosticsBestEffortAsync("failed");
                throw;
            }
        }
    }

    internal Task WriteDiagnosticsAsync(string status)
        => Progress.Diagnostics.WriteAsync(DiagnosticsDirectory, Progress, status, Capture.Failure);

    internal async Task WriteDiagnosticsBestEffortAsync(string status)
    {
        try
        {
            await WriteDiagnosticsAsync(status);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Progress.RecordProblem(AnalysisProblemCategory.Fatal, "diagnostics", error.Message);
            Console.Error.WriteLine($"::error::Unable to retain analysis diagnostics: {CiFailureJson.Display(DiagnosticTextSanitizer.Sanitize(error.Message, limit: 500))}");
        }
    }

    public void Dispose() => Scratch.Dispose();
    internal async Task WriteRecoveryBestEffortAsync(string stage, string status)
    {
        try
        {
            await RecoveryCaptureWriter.WriteAsync(RecoveryDirectory(stage), Progress, status, Capture, Scratch);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ValidationException or JsonException)
        {
            Progress.RecordProblem(AnalysisProblemCategory.Fatal, "recovery", error.Message);
            Console.Error.WriteLine($"::error::Unable to retain analysis recovery: {CiFailureJson.Display(error.Message)}");
        }
    }

}
