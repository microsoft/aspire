// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace AnalyzeCiFailure;

/// <summary>Retains bounded diagnostic records and streams sanitized operational progress without captured payloads.</summary>
internal sealed class AnalysisDiagnostics
{
    private readonly JsonArray _events = [];
    private readonly JsonArray _receipts = [];
    private long _stageStarted = Stopwatch.GetTimestamp();
    private bool _stageFinished;
    private int _omitted;
    private bool _progressEnabled;
    private bool _stageProgressStarted;
    private bool _stderrEnabled = true;
    private string? _progressFile;
    private long _progressBytes;
    internal string Stage { get; private set; } = "identity";

    internal static string DescribeStage(string stage) => stage switch
    {
        "identity" => "Resolving workflow run and pull request",
        "jobs" => "Listing workflow jobs",
        "context" => "Collecting job logs, annotations and workflow context",
        "history" => "Reading prior CI failure history",
        "test-evidence" => "Collecting and parsing test results",
        "projection" => "Building evidence bundle",
        "views" => "Writing evidence and analysis inputs",
        "replay" => "Replaying saved evidence",
        "validation" => "Validating analysis proposal",
        "publication" => "Publishing validated analysis",
        "workflow-output" => "Writing GitHub Actions outputs",
        _ => stage,
    };

    internal void EnableProgress(string directory)
    {
        _progressEnabled = true;
        _progressFile = Path.Combine(directory, "progress.log");
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            DisableProgressFile("disabled", error.Message);
        }
        WriteProgress("Invocation initialized.");
    }

    internal void StartCurrentStage()
    {
        _stageProgressStarted = true;
        WriteProgress($"started: {DescribeStage(Stage)}");
    }

    internal void EnterStage(string stage)
    {
        FinishStage("completed");
        Stage = stage;
        _stageStarted = Stopwatch.GetTimestamp();
        _stageFinished = false;
        StartCurrentStage();
    }

    internal void FinishStage(string result)
    {
        if (!_stageFinished)
        {
            var duration = (long)Stopwatch.GetElapsedTime(_stageStarted).TotalMilliseconds;
            Record("stage", result, new { name = Stage, duration_ms = duration });
            _stageFinished = true;
            if (_stageProgressStarted)
            {
                WriteProgress($"{result}: {DescribeStage(Stage)} duration_ms={duration}");
            }
        }
    }

    internal void ReportProblem(AnalysisProblemCategory category, AnalysisProblem problem)
        => WriteProgress($"{DescribeStage(problem.Stage)} {(category == AnalysisProblemCategory.Fatal ? "failed" : "warning")}: {problem.Message}");

    internal void WriteProgress(string message)
    {
        if (!_progressEnabled)
        {
            return;
        }
        // GitHub recognizes embedded "::warning::" commands as well as commands at
        // the start of a line. Keep upstream error text inert in the running trace.
        var text = CiFailureJson.Display(DiagnosticTextSanitizer.Sanitize(message, limit: 1000))
            .Replace("::", ": :", StringComparison.Ordinal);
        var line = $"[{DateTimeOffset.UtcNow:O}] [{Stage}] {text}{Environment.NewLine}";
        if (_stderrEnabled)
        {
            try
            {
                Console.Error.Write(line);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                _stderrEnabled = false;
                Record("progress-stderr", "disabled", new { error = error.Message });
                WriteProgress($"Warning: stderr progress is unavailable: {error.Message}");
            }
        }
        if (_progressFile is not { } path)
        {
            return;
        }
        try
        {
            var bytes = Encoding.UTF8.GetByteCount(line);
            var limitNotice = $"Progress log file reached its size limit; progress continues on stderr.{Environment.NewLine}";
            if (_progressBytes + bytes + Encoding.UTF8.GetByteCount(limitNotice) > AnalysisDiagnostics.MaximumFileBytes)
            {
                File.AppendAllText(path, limitNotice);
                DisableProgressFile("truncated", "Progress log file reached its size limit; progress continues on stderr.");
                return;
            }
            // Close each append so readers and artifact uploads can consume the trace
            // while acquisition is still running, including before summary.json exists.
            File.AppendAllText(path, line);
            _progressBytes += bytes;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            DisableProgressFile("disabled", error.Message);
        }
    }

    private void DisableProgressFile(string result, string message)
    {
        _progressFile = null;
        Record("progress-log", result, new { message });
        WriteProgress($"Warning: retained progress log is {result}: {message}");
    }

    internal void Record<T>(string action, string result, T detail) => Add(_events, action, result, detail);
    internal void Receipt<T>(string action, string result, T detail) => Add(_receipts, action, result, detail);
    internal JsonNode ReceiptSnapshot() => _receipts.DeepClone();

    private void Add<T>(JsonArray destination, string action, string result, T detail)
    {
        var entry = new JsonObject
        {
            ["stage"] = Stage,
            ["action"] = action,
            ["result"] = result,
            ["detail"] = DiagnosticTextSanitizer.SanitizeJson(CiFailureJson.ToNode(detail), limit: 500, multiline: true),
        };
        if (destination.Count == AnalysisDiagnostics.MaximumManifestEntries ||
            Encoding.UTF8.GetByteCount(CiFailureJson.Serialize(entry)) > AnalysisDiagnostics.MaximumFileBytes)
        {
            _omitted++;
            return;
        }
        destination.Add(entry);
        if (action != "stage")
        {
            WriteProgress($"{action}: {result}");
        }
    }

    internal async Task WriteAsync(string directory, AnalysisProgress progress, string status, Exception? failure)
    {
        FinishStage(status == "failed" ? "failed" : "completed");
        var outcome = DiagnosticTextSanitizer.SanitizeJson(JsonNode.Parse(TransientJson.Write(progress.CreateOutcome(status))), limit: 500, multiline: true)!;
        var problems = outcome["problems"]!.AsArray();
        var omittedProblems = 0;
        while (problems.Count > AnalysisDiagnostics.MaximumManifestEntries)
        {
            problems.RemoveAt(problems.Count - 1);
            omittedProblems++;
        }
        var summary = new JsonObject
        {
            ["schema_version"] = 1,
            ["status"] = status,
            ["outcome"] = outcome,
            ["exception"] = failure is null ? null : AnalysisDiagnostics.DescribeException(failure),
            ["events"] = _events.DeepClone(),
            ["receipts"] = _receipts.DeepClone(),
            ["omitted_entries"] = _omitted,
            ["omitted_problems"] = omittedProblems,
        };
        // Bound valid JSON, not its serialized prefix. Prefer confirmed side-effect receipts
        // over decision events when the existing recovery-file byte limit is reached.
        var events = summary["events"]!.AsArray();
        var receipts = summary["receipts"]!.AsArray();
        var text = CiFailureJson.Serialize(summary);
        while (Encoding.UTF8.GetByteCount(text) > AnalysisDiagnostics.MaximumFileBytes &&
            (events.Count > 0 || problems.Count > 0 || receipts.Count > 0))
        {
            var entries = events.Count > 0 ? events : problems.Count > 0 ? problems : receipts;
            var count = Math.Max(1, entries.Count / 2);
            for (var i = 0; i < count; i++)
            {
                entries.RemoveAt(entries.Count - 1);
            }
            var omittedKey = ReferenceEquals(entries, problems) ? "omitted_problems" : "omitted_entries";
            summary[omittedKey] = summary[omittedKey]!.GetValue<int>() + count;
            text = CiFailureJson.Serialize(summary);
        }
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "summary.json"), text);
    }    internal const int MaximumFileBytes = 2 * 1024 * 1024;
    internal const int MaximumManifestEntries = 1024;

    internal static JsonObject DescribeException(Exception error) => new()
    {
        ["type"] = error.GetType().FullName,
        ["message"] = DiagnosticTextSanitizer.Sanitize(error.Message, limit: 500, multiline: true),
        ["stack_trace"] = DiagnosticTextSanitizer.Sanitize(error.StackTrace ?? "", limit: 8000, multiline: true),
    };

}
