// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace AnalyzeCiFailure;

/// <summary>Carries the accepted run scope, canonical URL and subject-PR context used by publication.</summary>
internal sealed record ValidatedRun(RunScope Scope, string Url, string PrNumbers);
/// <summary>Retains an accepted failed job&apos;s exact numeric identity, original token and display name.</summary>
internal sealed record FailedJob(NormalizedJsonNumber Id, string SerializedId, string Name);
/// <summary>Matches a test&apos;s raw name and job to an optional precise observation identity.</summary>
internal sealed record TestIdentity(string Name, string Job, NormalizedJsonNumber? JobId = null, string? ObservationId = null);
/// <summary>Identifies one accepted test observation within a producing job and any ambiguous-name group.</summary>
internal sealed record TestObservation(string Name, NormalizedJsonNumber JobId, string? ObservationId = null);
/// <summary>Binds an accepted cause identity and type to the exact jobs and observations it covers.</summary>
internal sealed record FailureCause(
    string Id,
    string FileName,
    CauseType Type,
    ImmutableArray<NormalizedJsonNumber> JobIds,
    ImmutableArray<TestObservation> Tests)
{
    internal CauseDefinition? Definition { get; init; }
    internal bool IsExistingProposal { get; init; }
}
/// <summary>Carries the accepted definition used when introducing a new durable cause record.</summary>
internal sealed record CauseDefinition
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }
    [JsonPropertyName("type")]
    public required CauseType Type { get; init; }
    [JsonPropertyName("title")]
    public required string Title { get; init; }
    [JsonPropertyName("error_pattern")]
    public required string ErrorPattern { get; init; }
    [JsonPropertyName("test_name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TestName { get; init; }
}
/// <summary>Reads the historical cause identity and type used to reject incompatible history updates.</summary>
internal sealed record StoredCauseIdentity(
    [property: JsonPropertyName("type")] CauseType Type,
    [property: JsonPropertyName("test_name"), JsonConverter(typeof(CiFailureJson.OptionalTextConverter))] string? TestName)
{
    [JsonPropertyName("id"), JsonConverter(typeof(CiFailureJson.OptionalTextConverter))]
    public string? Id { get; init; }
}
/// <summary>Binds accepted classifications and causes to the complete trusted evidence required by privileged effects.</summary>
internal sealed record ValidatedAnalysis(
    AnalysisReport Report,
    ValidatedRun Run,
    ImmutableArray<FailedJob> Jobs,
    ImmutableArray<FailureCause> Causes,
    ImmutableArray<TestFailureEvidence> Tests,
    RunEvidence Evidence)
{
    internal required EvidenceBundle Bundle { get; init; }
    internal required PublicationEvidence CapturedPublication { get; init; }
}
