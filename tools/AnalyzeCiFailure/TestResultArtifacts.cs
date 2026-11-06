// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text.Json.Serialization;
using static AnalyzeCiFailure.CiFailureJson;

namespace AnalyzeCiFailure;

/// <summary>Matches artifacts to producing expectations and allocates deterministic acquisition budgets.</summary>
internal static class TestResultArtifacts
{
    internal static IReadOnlyList<SelectedTestArtifact> SelectAvailable(
        IReadOnlyList<WorkflowArtifact> artifacts,
        IReadOnlyList<TestArtifactExpectation> jobs,
        string startedAt,
        string updatedAt,
        AnalysisDiagnostics diagnostics,
        Action<TestArtifactExpectation, string> reportProblem)
    {
        var candidates = new List<SelectedTestArtifact>();
        foreach (var job in jobs)
        {
            try
            {
                candidates.AddRange(Select(artifacts, [job], startedAt, updatedAt, diagnostics: diagnostics));
            }
            catch (ValidationException error)
            {
                reportProblem(job, error.Message);
            }
        }
        var duplicateIds = candidates.GroupBy(candidate => ReadNormalizedNumber(candidate.Artifact.Id))
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        var selected = new List<SelectedTestArtifact>();
        long bytes = 0;
        foreach (var candidate in candidates.OrderByDescending(candidate => candidate.Expectation.ReservesBudget)
            .ThenByDescending(candidate => candidate.RequiresResults)
            .ThenBy(candidate => candidate.Expectation.ReservesBudget ? "" : candidate.JobName, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Expectation.ReservesBudget ? 0 : GitHubJson.ReadRequiredId(candidate.Artifact.Id)))
        {
            var size = (long)double.Parse(GetValueText(candidate.Artifact.SizeInBytes), CultureInfo.InvariantCulture);
            if (duplicateIds.Contains(ReadNormalizedNumber(candidate.Artifact.Id)))
            {
                reportProblem(candidate.Expectation, "Repeated artifact ID cannot identify exactly one producing expectation.");
            }
            else if (selected.Count == 20 || bytes + size > 1073741824)
            {
                reportProblem(candidate.Expectation, "Test result artifact exceeds the shared download budget.");
            }
            else
            {
                selected.Add(candidate);
                bytes += size;
            }
        }
        return selected;
    }

    // Keep numeric tokens intact and defer validation until an artifact matches a producing job.
    internal static IReadOnlyList<WorkflowArtifact> ReadArtifacts(JsonArray artifacts)
        => artifacts.OfType<JsonObject>().Select(artifact => new WorkflowArtifact(
            artifact["id"],
            Text(artifact["name"]),
            artifact["size_in_bytes"],
            artifact["expired"] is JsonValue expired && expired.TryGetValue<bool>(out var isExpired) ? isExpired : null,
            Text(artifact["created_at"]))).ToArray();

    internal static IReadOnlyList<SelectedTestArtifact> Select(
        IReadOnlyList<WorkflowArtifact> artifacts,
        IReadOnlyList<TestArtifactExpectation> jobs,
        string startedAt,
        string updatedAt,
        int maxArtifacts = 20,
        long maxTotalBytes = 1073741824,
        long maxArtifactBytes = 104857600,
        AnalysisDiagnostics? diagnostics = null)
    {
        var selected = new List<SelectedTestArtifact>();
        var expectedArtifacts = new HashSet<(string Job, string Artifact)>();
        foreach (var job in jobs)
        {
            if (string.IsNullOrEmpty(job.JobName) || string.IsNullOrEmpty(job.ArtifactName) ||
                !Enum.IsDefined(job.Format) || !Enum.IsDefined(job.EvidencePolicy))
            {
                throw new ValidationException($"Workflow conventions returned an invalid test artifact plan: {Describe(job)}");
            }
            if (!expectedArtifacts.Add((job.JobName, job.ArtifactName)))
            {
                throw new ValidationException($"test result artifact does not identify exactly one failed job: repeated expectation for {Describe(job)}");
            }
            var matches = artifacts.Where(artifact => artifact.Expired is false && artifact.Name == job.ArtifactName &&
                artifact.CreatedAt is { } created && string.CompareOrdinal(created, startedAt) > 0 &&
                string.CompareOrdinal(created, updatedAt) <= 0).ToArray();
            diagnostics?.Record(
                "artifact-candidates",
                matches.Length == 1 ? "matched" : matches.Length == 0 ? "no-current-artifact" : "ambiguous",
                new
                {
                    job = job.JobName,
                    artifact = job.ArtifactName,
                    eligible = matches.Length,
                    named = artifacts.Count(artifact => artifact.Name == job.ArtifactName),
                    expired_or_unknown = artifacts.Count(artifact => artifact.Name == job.ArtifactName && artifact.Expired is not false)
                });
            if (matches.Length == 0 && !job.ReservesBudget)
            {
                continue;
            }
            if (!(matches.Length > 0))
            {
                throw new ValidationException($"test result artifact is missing for a failed test job: {Describe(job)}");
            }
            if (!(matches.Length == 1))
            {
                throw new ValidationException($"test result artifact does not identify exactly one failed job: {Describe(job)} matches={matches.Length}");
            }
            selected.Add(new SelectedTestArtifact(matches[0], job));
        }
        // Sizes such as 1e-400 or 10.00000000000000001 must not round into
        // integers before they are charged to the shared acquisition budget.
        static bool HasValidMetadata(WorkflowArtifact artifact, long limit) => GitHubJson.TryReadId(artifact.Id, out _) &&
            ReadNormalizedNumber(artifact.SizeInBytes) is not null &&
            double.TryParse(GetValueText(artifact.SizeInBytes), CultureInfo.InvariantCulture, out var size) &&
            size >= 0 && size <= limit && size == Math.Floor(size) &&
            ReadNormalizedNumber(artifact.SizeInBytes) == NormalizedJsonNumber.Parse(((long)size).ToString(CultureInfo.InvariantCulture));
        static long GetSize(SelectedTestArtifact selected) => (long)double.Parse(GetValueText(selected.Artifact.SizeInBytes), CultureInfo.InvariantCulture);
        var requiredArtifacts = selected.Where(selection => selection.Expectation.ReservesBudget).ToList();
        foreach (var invalidOptional in selected.Where(selection => !selection.Expectation.ReservesBudget && !HasValidMetadata(selection.Artifact, maxArtifactBytes)))
        {
            diagnostics?.Record("artifact-selection", "invalid-size-or-id", new { job = invalidOptional.JobName, artifact = invalidOptional.Artifact.Name });
        }
        if (!(requiredArtifacts.Count <= maxArtifacts))
        {
            throw new ValidationException($"test result artifact count exceeds the download budget: required={requiredArtifacts.Count} limit={maxArtifacts}");
        }
        if (requiredArtifacts.FirstOrDefault(selection => !HasValidMetadata(selection.Artifact, maxArtifactBytes)) is { } invalid)
        {
            throw new ValidationException($"test result artifact has invalid or excessive size metadata: {Describe(invalid.Expectation)} " +
                $"id={Display(DiagnosticTextSanitizer.Sanitize(GetValueText(invalid.Artifact.Id), limit: 40))} " +
                $"size={Display(DiagnosticTextSanitizer.Sanitize(GetValueText(invalid.Artifact.SizeInBytes), limit: 40))} limit={maxArtifactBytes}");
        }
        if (!(requiredArtifacts.Select(selection => ReadNormalizedNumber(selection.Artifact.Id)).Distinct().Count() == requiredArtifacts.Count))
        {
            throw new ValidationException("test result artifact does not identify exactly one failed job: repeated artifact IDs among required results");
        }
        var bytes = requiredArtifacts.Sum(GetSize);
        if (!(bytes <= maxTotalBytes))
        {
            throw new ValidationException($"test result artifacts exceed the cumulative download budget: required={bytes} limit={maxTotalBytes}");
        }
        foreach (var optional in selected.Where(selection => !selection.Expectation.ReservesBudget && HasValidMetadata(selection.Artifact, maxArtifactBytes))
            .OrderByDescending(selection => selection.RequiresResults)
            .ThenBy(selection => selection.JobName, StringComparer.Ordinal)
            .ThenBy(selection => GitHubJson.ReadRequiredId(selection.Artifact.Id)))
        {
            if (requiredArtifacts.Count < maxArtifacts && bytes + GetSize(optional) <= maxTotalBytes)
            {
                bytes += GetSize(optional);
                requiredArtifacts.Add(optional);
            }
            else
            {
                diagnostics?.Record("artifact-selection", "budget", new { job = optional.JobName, artifact = optional.Artifact.Name });
            }
        }
        if (!(requiredArtifacts.Select(selection => ReadNormalizedNumber(selection.Artifact.Id)).Distinct().Count() == requiredArtifacts.Count))
        {
            throw new ValidationException("test result artifact does not identify exactly one failed job: repeated artifact IDs among selected results");
        }

        return requiredArtifacts;
    }

    internal static string Describe(TestArtifactExpectation job)
        => $"job='{Display(DiagnosticTextSanitizer.Sanitize(job.JobName, limit: 80))}' " +
            $"artifact='{Display(DiagnosticTextSanitizer.Sanitize(job.ArtifactName, limit: 100))}'";

    internal static JsonArray SerializeSelection(IEnumerable<SelectedTestArtifact> artifacts)
        => (JsonArray)ToNode(artifacts.Select(selected => new TestResultArtifactRecord
        {
            Id = ToContract<JsonElement?>(selected.Artifact.Id),
            Name = selected.Artifact.Name,
            SizeInBytes = ToContract<JsonElement?>(selected.Artifact.SizeInBytes),
            Job = selected.JobName,
            Format = selected.Format,
        }).ToArray())!;

}

/// <summary>Retains artifact metadata tokens until matching and resource-limit validation establish eligibility.</summary>
internal sealed record WorkflowArtifact(JsonNode? Id, string? Name, JsonNode? SizeInBytes, bool? Expired, string? CreatedAt);

/// <summary>Binds a selected artifact to its producing expectation, format and result requirements.</summary>
internal sealed record SelectedTestArtifact(WorkflowArtifact Artifact, TestArtifactExpectation Expectation)
{
    internal string JobName => Expectation.JobName;
    internal TestArtifactFormat Format => Expectation.Format;
    internal bool RequiresResults => Expectation.RequiresResults;
}

/// <summary>Identifies the supported structured test-result format contained in an artifact.</summary>
internal enum TestArtifactFormat
{
    [JsonStringEnumMemberName("trx")]
    Trx,
    [JsonStringEnumMemberName("mocha")]
    Mocha,
}
