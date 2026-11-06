// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Text.Json;
using System.Text;
using System.Text.Json.Serialization;

namespace AnalyzeCiFailure;

/// <summary>Selects the repository, workflow run and optional pinned attempt to acquire.</summary>
internal sealed record EvidenceCollectionRequest(string Repository, long RunId, int? Attempt);

/// <summary>Distinguishes optional-context issues, missing test evidence and fatal invocation failures.</summary>
internal enum AnalysisProblemCategory { OptionalContext, TestEvidence, Fatal }
/// <summary>Associates a recorded problem with the policy that determines its effect on analysis completeness.</summary>
internal sealed record CategorizedProblem(AnalysisProblemCategory Category, AnalysisProblem Problem);
/// <summary>Groups acquired evidence, raw attachments and the historical catalogue before diagnosis-context projection.</summary>
internal sealed record CollectedRun(
    EvidenceBundle Evidence,
    ImmutableDictionary<string, CollectedAttachment> Attachments,
    ImmutableArray<PriorCauseIndexEntry> PriorCauses)
{
    internal ImmutableDictionary<string, JsonElement> JsonContext { get; init; } = ImmutableDictionary<string, JsonElement>.Empty;
}
/// <summary>Distinguishes a collected run from a legitimate no-relevant-failures acquisition result.</summary>
internal sealed record EvidenceCollectionResult(CollectedRun? Run, bool HasRelevantFailures);
/// <summary>Groups trusted evidence, agent-facing context and the complete historical catalogue for package writing.</summary>
internal sealed record DiagnosisInput(
    EvidenceBundle Evidence,
    string Summary,
    ImmutableArray<PriorCauseIndexEntry> Catalogue,
    ImmutableDictionary<string, CollectedAttachment> Attachments,
    ImmutableDictionary<string, JsonElement> JsonContext);

// Keep full-record bytes even when an unselected historical record cannot be decoded.
/// <summary>Retains complete captured bytes and exposes BOM-aware text without rewriting the original attachment.</summary>
internal sealed record CollectedAttachment(ImmutableArray<byte> Bytes)
{
    internal string Text
    {
        get
        {
            // Match the existing durable file reader's BOM handling without re-encoding its bytes.
            using var stream = new MemoryStream(Bytes.ToArray(), writable: false);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
    }
    internal static CollectedAttachment FromText(string text) => new(Encoding.UTF8.GetBytes(text).ToImmutableArray());
}

/// <summary>Tracks tri-state failure discovery, established run identity, phase progress and retained problems.</summary>
internal sealed class AnalysisProgress
{
    internal bool? HasRelevantFailures { get; set; }
    internal long? RunId { get; set; }
    internal int? RunAttempt { get; set; }
    internal string? HeadSha { get; set; }
    internal string? RunUrl { get; set; }
    internal RunScope? RunScope { get; set; }
    internal long? SubjectPrNumber { get; set; }
    internal AnalysisDiagnostics Diagnostics { get; } = new();
    internal string Stage { get => Diagnostics.Stage; set => Diagnostics.EnterStage(value); }
    internal List<CategorizedProblem> Problems { get; } = [];

    internal void RecordProblem(AnalysisProblemCategory category, string stage, string message)
    {
        var problem = new AnalysisProblem
        {
            Stage = stage,
            Message = CiFailureJson.Display(DiagnosticTextSanitizer.Sanitize(message, limit: 500)),
        };
        Problems.Add(new(category, problem));
        Diagnostics.ReportProblem(category, problem);
    }

    internal AnalysisOutcome CreateOutcome(string status) => new()
    {
        SchemaVersion = 1,
        Status = status,
        HasRelevantFailures = HasRelevantFailures,
        RunId = RunId,
        RunAttempt = RunAttempt,
        HeadSha = HeadSha,
        RunUrl = RunUrl,
        RunScope = RunScope,
        SubjectPrNumber = SubjectPrNumber,
        Problems = Problems.Select(problem => problem.Problem).ToImmutableArray(),
    };
}

/// <summary>Serializes an invocation&apos;s established run facts, terminal status and problems without authorizing effects.</summary>
internal sealed record AnalysisOutcome
{
    public required int SchemaVersion { get; init; }
    public required string Status { get; init; }
    [JsonPropertyName("has_work")]
    public required bool? HasRelevantFailures { get; init; }
    public required long? RunId { get; init; }
    public required int? RunAttempt { get; init; }
    public required string? HeadSha { get; init; }
    public required string? RunUrl { get; init; }
    public required RunScope? RunScope { get; init; }
    public required long? SubjectPrNumber { get; init; }
    public required ImmutableArray<AnalysisProblem> Problems { get; init; }
}
