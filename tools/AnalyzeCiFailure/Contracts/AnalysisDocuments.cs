// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;
using AnalyzeCiFailure.Common;

namespace AnalyzeCiFailure.Contracts;

// The JSON files exchanged between the workflow, the agent, and the persistence helpers.
// Property names map to snake_case through JsonFiles.Options.
//
// Agent-written documents use nullable members throughout: a missing or null field must reach the
// validator so it can report the specific rule that was broken, rather than failing to bind.

/// <summary><c>analysis-result.json</c>, written by the agent. Fields the validator does not check are ignored.</summary>
internal sealed record AnalysisDocument(
    long? RunId = null,
    string? RunScope = null,
    string? Verdict = null,
    List<FailedJobEntry?>? FailedJobs = null,
    List<FailedTestEntry?>? FailedTests = null,
    List<string?>? Causes = null)
{
    /// <summary>The subject PR. An explicit <c>null</c> means the agent could not identify one.</summary>
    public PullRequestReference? Pr
    {
        get;
        init
        {
            field = value;
            HasPr = true;
        }
    }

    /// <summary>
    /// Whether <c>pr</c> was written at all. A pull-request analysis must state its subject PR,
    /// even if only as <c>null</c>, so an omitted field is distinguishable from an explicit null.
    /// </summary>
    [JsonIgnore]
    public bool HasPr { get; private init; }
}

/// <summary>The run analysis-result.json claims to describe, read before the rest of the document.</summary>
internal sealed record AnalysisRunIdentity(long? RunId = null, string? RunScope = null);

internal sealed record PullRequestReference(long? Number = null);

internal sealed record FailedJobEntry(long? Id = null, string? Classification = null, string? Reason = null);

internal sealed record FailedTestEntry(
    string? Name = null,
    string? Job = null,
    string? Error = null,
    string? StackTrace = null,
    string? StandardOutput = null,
    string? StandardError = null,
    string? Classification = null,
    string? Reason = null);

/// <summary>
/// <c>causes/&lt;id&gt;.json</c>, written by the agent. Bound with <see cref="JsonFiles.StrictOptions"/>,
/// so any other field, including the publisher-owned ones, is rejected.
/// </summary>
internal sealed record CauseDocument(
    string? Id = null,
    string? Type = null,
    string? Title = null,
    string? TestName = null,
    string? ErrorPattern = null,
    List<long>? JobIds = null);

/// <summary>A cause stored by an earlier run. Only the fields that must not change are read.</summary>
internal sealed record PriorCauseDocument(string? Type = null, string? TestName = null);

/// <summary><c>ci-failure-data/run-context.json</c>, collected by the workflow.</summary>
internal sealed record RunContextDocument(long RunId, string RunScope, string? PrNumbers = null);

/// <summary><c>ci-failure-data/run.json</c>, the GitHub API run object.</summary>
internal sealed record RunMetadataDocument(long? Id = null, string? HtmlUrl = null);

/// <summary><c>ci-failure-data/test-evidence.json</c>, collected by the workflow.</summary>
internal sealed record TestEvidenceDocument(string? State = null);

/// <summary>An entry of the sanitized <c>failed-jobs.json</c>.</summary>
internal sealed record TrustedJob(long Id, string Name);

/// <summary>An entry of the sanitized <c>test-failures.json</c>. The sanitizer fills absent diagnostics with "".</summary>
internal sealed record TrustedTestFailure(
    string Test,
    string Job,
    string Error,
    string StackTrace,
    string StandardOutput,
    string StandardError);
