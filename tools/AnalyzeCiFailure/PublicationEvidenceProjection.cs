// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Text.Json;

namespace AnalyzeCiFailure;

/// <summary>Projects collected test observations into publication-compatible evidence without granting publication authority.</summary>
internal static class PublicationEvidenceProjection
{
    internal static ImmutableArray<TestFailureEvidence> ProjectTestFailures(EvidenceBundle evidence)
    {
        var jobs = evidence.Jobs.ToDictionary(job => job.Id, job => DiagnosticTextSanitizer.Sanitize(job.Name, limit: 500));
        var ambiguous = evidence.TestEvidence.Failures.GroupBy(test => (test.Name, test.JobId))
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        return evidence.TestEvidence.Failures.Select((test, index) => new TestFailureEvidence
        {
            Name = test.Name,
            DisplayName = DiagnosticTextSanitizer.Sanitize(test.DisplayName, limit: 500),
            Job = jobs[test.JobId],
            JobId = JsonSerializer.SerializeToElement(test.JobId),
            ObservationId = ambiguous.Contains((test.Name, test.JobId)) ? $"observation-{index + 1}" : null,
            Error = DiagnosticTextSanitizer.Sanitize(test.Diagnostics.Error, limit: 1000, multiline: true),
            StackTrace = DiagnosticTextSanitizer.Sanitize(test.Diagnostics.StackTrace, limit: 2000, multiline: true),
            StandardOutput = DiagnosticTextSanitizer.Sanitize(test.Diagnostics.StandardOutput, limit: 4000, multiline: true),
            StandardError = DiagnosticTextSanitizer.Sanitize(test.Diagnostics.StandardError, limit: 4000, multiline: true),
            ResultFile = test.Sources[0].ResultFile,
            ResultFormat = test.Sources[0].Format,
            // Raw execution IDs establish copied-result identity only within evidence.
            // Publication metadata must not persist secret-shaped values or controls.
            ExecutionId = test.Sources[0].ExecutionId is { } execution
                ? DiagnosticTextSanitizer.Sanitize(execution, limit: 500) : null,
        }).ToImmutableArray();
    }
}
