// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace AnalyzeCiFailure;

// Read-only views for discovery. The original cause files retain all occurrence
// diagnostics and unknown fields; these partial views are never written over them.
/// <summary>Provides a tolerant read-only view of historical causes for catalogue construction.</summary>
internal sealed record PriorCauseRecord
{
    [JsonPropertyName("id"), JsonConverter(typeof(CiFailureJson.OptionalTextConverter))]
    public string? Id { get; init; }
    [JsonPropertyName("type")]
    public CauseType? Type { get; init; }
    [JsonPropertyName("title"), JsonConverter(typeof(CiFailureJson.OptionalTextConverter))]
    public string? Title { get; init; }
    [JsonPropertyName("test_name"), JsonConverter(typeof(CiFailureJson.OptionalTextConverter))]
    public string? TestName { get; init; }
    [JsonPropertyName("issue_url"), JsonConverter(typeof(CiFailureJson.OptionalTextConverter))]
    public string? IssueUrl { get; init; }
    [JsonPropertyName("error_pattern"), JsonConverter(typeof(CiFailureJson.OptionalTextConverter))]
    public string? ErrorPattern { get; init; }
    [JsonPropertyName("tests")]
    public ImmutableArray<PriorTestReference?>? Tests { get; init; }
    [JsonPropertyName("occurrences")]
    public ImmutableArray<PriorOccurrence?>? Occurrences { get; init; }

    internal void RequireReadableCollections()
    {
        if (!(Tests ?? []).All(test => test is not null) || !(Occurrences ?? []).All(occurrence =>
            occurrence is not null && (occurrence.Tests ?? []).All(test => test is not null) && (occurrence.FailedTests ?? []).All(test => test is not null)))
        {
            throw new ValidationException("Prior cause 'tests' and 'occurrences' must contain objects, not null entries.");
        }
    }
}

/// <summary>Reads a historical test name without requiring the complete durable test-observation shape.</summary>
internal sealed record PriorTestReference
{
    [JsonPropertyName("name"), JsonConverter(typeof(CiFailureJson.OptionalTextConverter))]
    public string? Name { get; init; }
}

/// <summary>Reads the historical occurrence fields needed for catalogue ordering and test matching.</summary>
internal sealed record PriorOccurrence
{
    [JsonPropertyName("observed_at"), JsonConverter(typeof(CiFailureJson.OptionalTextConverter))]
    public string? ObservedAt { get; init; }
    [JsonPropertyName("tests")]
    public ImmutableArray<PriorTestReference?>? Tests { get; init; }
    [JsonPropertyName("failed_tests")]
    public ImmutableArray<PriorTestReference?>? FailedTests { get; init; }
}

/// <summary>Summarizes one historical cause, current-evidence matches or a readable record-corruption diagnostic.</summary>
internal sealed record PriorCauseIndexEntry
{
    [JsonPropertyName("file")]
    public required string File { get; init; }
    [JsonPropertyName("id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; init; }
    [JsonPropertyName("type"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CauseType? Type { get; init; }
    [JsonPropertyName("title"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }
    [JsonPropertyName("test_name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TestName { get; init; }
    [JsonPropertyName("issue_url"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IssueUrl { get; init; }
    [JsonPropertyName("error_pattern"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorPattern { get; init; }
    [JsonPropertyName("occurrence_count"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? OccurrenceCount { get; init; }
    [JsonPropertyName("last_seen"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastSeen { get; init; }
    [JsonPropertyName("current_test_match"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CurrentTestMatch { get; init; }
    [JsonPropertyName("current_diagnostic_match"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CurrentDiagnosticMatch { get; init; }
    [JsonPropertyName("read_error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReadError { get; init; }
}
