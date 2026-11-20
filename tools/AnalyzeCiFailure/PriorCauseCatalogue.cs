// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AnalyzeCiFailure;

/// <summary>Builds a tolerant historical catalogue and current-evidence matches without I/O.</summary>
internal static partial class PriorCauseCatalogue
{

    internal static ImmutableArray<PriorCauseIndexEntry> BuildIndex(
        ImmutableDictionary<string, CollectedAttachment> attachments,
        ImmutableArray<CollectedTestFailure> tests)
    {
        var names = tests.Select(test => test.Name).ToHashSet(StringComparer.Ordinal);
        var diagnostics = tests.SelectMany(test => new[]
        {
            test.Diagnostics.Error, test.Diagnostics.StackTrace, test.Diagnostics.StandardOutput, test.Diagnostics.StandardError,
        }).Concat(attachments.Where(pair => pair.Key.StartsWith("diagnostics/job-", StringComparison.Ordinal) && pair.Key.EndsWith(".log", StringComparison.Ordinal))
            .Select(pair => pair.Value.Text)).ToArray();
        return attachments.Where(pair => pair.Key.StartsWith("history/causes/", StringComparison.Ordinal))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
            {
                try
                {
                    var cause = CiFailureJson.ToContract<PriorCauseRecord>(System.Text.Json.Nodes.JsonNode.Parse(pair.Value.Text))
                        ?? throw new ValidationException("Prior cause is not an object");
                    if (cause.Id is not { } id || !CauseIdSlugRegex().IsMatch(id) ||
                        id + ".json" != Path.GetFileName(pair.Key))
                    {
                        throw new ValidationException("Prior cause ID does not match its filename");
                    }
                    cause.RequireReadableCollections();
                    var occurrences = cause.Occurrences ?? [];
                    var testNames = (cause.Tests ?? []).Concat(occurrences.SelectMany(occurrence => (occurrence!.Tests ?? []).Concat(occurrence.FailedTests ?? [])))
                        .Select(test => test!.Name).Append(cause.TestName);
                    return new PriorCauseIndexEntry
                    {
                        File = pair.Key,
                        Id = id,
                        Type = cause.Type,
                        Title = DiagnosticTextSanitizer.Sanitize(cause.Title ?? id, limit: 238),
                        TestName = cause.TestName is { } label ? DiagnosticTextSanitizer.Sanitize(label, limit: 500) : null,
                        IssueUrl = cause.IssueUrl is { } url ? DiagnosticTextSanitizer.Sanitize(url, limit: 500) : null,
                        ErrorPattern = DiagnosticTextSanitizer.Sanitize(cause.ErrorPattern ?? "", limit: 500, multiline: true),
                        OccurrenceCount = occurrences.Length,
                        LastSeen = occurrences.Select(occurrence => occurrence!.ObservedAt).Where(value => value is not null)
                            .Order(StringComparer.Ordinal).LastOrDefault() is { } last ? DiagnosticTextSanitizer.Sanitize(last, limit: 500) : null,
                        CurrentTestMatch = testNames.Any(name => name is not null && names.Contains(name)),
                        CurrentDiagnosticMatch = !string.IsNullOrEmpty(cause.ErrorPattern) && diagnostics.Any(value => value.Contains(cause.ErrorPattern, StringComparison.Ordinal)),
                    };
                }
                catch (Exception ex) when (ex is JsonException or ValidationException)
                {
                    return new PriorCauseIndexEntry
                    {
                        File = pair.Key,
                        ReadError = "Cannot summarize prior cause record: " + CiFailureJson.Display(DiagnosticTextSanitizer.Sanitize(ex.Message, limit: 500)),
                    };
                }
            }).ToImmutableArray();
    }

    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*\z")]
    private static partial Regex CauseIdSlugRegex();
}
