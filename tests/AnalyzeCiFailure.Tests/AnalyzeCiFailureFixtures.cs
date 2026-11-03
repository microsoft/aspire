// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using AnalyzeCiFailure;

namespace Infrastructure.Tests;

/// <summary>Ports historical scenario data to typed inputs; all decisions remain production validation's responsibility.</summary>
internal static class AnalyzeCiFailureFixtures
{
    internal static EvidenceBundle CreateEvidence(RunScope scope) => new()
    {
        SchemaVersion = 1,
        Run = new CollectedWorkflowRun
        {
            Repository = "microsoft/aspire", Id = 123, Attempt = 1, Scope = scope,
            Event = scope == RunScope.Main ? "push" : "pull_request",
            HeadBranch = scope == RunScope.Main ? "main" : "feature", HeadSha = new string('a', count: 40),
            Url = "https://github.com/microsoft/aspire/actions/runs/123",
        },
        SubjectPr = scope == RunScope.Main ? null : new CollectedSubjectPr { Number = 42, Metadata = null },
        MainContext = scope != RunScope.Main ? null : new CollectedMainContext
        {
            FailedSha = new string('a', count: 40), LastSuccessfulRun = null, TriggeringMerge = null,
            CandidateHistory = new CollectedCandidateHistory { State = CandidateHistoryState.Unavailable, Candidates = null },
        },
        Jobs = [new JobEvidence { Id = 123, Name = "Tests", Url = null, Conclusion = "failure", Steps = [] }],
        TestEvidence = new CollectedTestEvidence { State = EvidenceState.Complete, Failures = [], Problems = [] },
        PriorCauseIdentities = [],
    };

    internal static AnalysisProposal CreateInfrastructureProposal(EvidenceBundle evidence) => new()
    {
        SchemaVersion = 1, Run = new ProposedWorkflowRun { Repository = evidence.Run.Repository, Id = evidence.Run.Id, Attempt = evidence.Run.Attempt },
        Jobs = evidence.Jobs.Select(job => new ProposedJobDecision { JobId = job.Id, Classification = JobClassification.TransientInfrastructure, Reason = "Runner lost" }).ToImmutableArray(),
        Tests = [],
        Causes =
        [
            new NewCauseProposal
            {
                Definition = new ProposedCauseDefinition { Id = "runner-lost", Type = CauseType.InfrastructureFailure, Title = "Runner lost", ErrorPattern = "Runner lost" },
                JobIds = evidence.Jobs.Select(job => job.Id).ToImmutableArray(), TestRefs = [], Reason = "Runner lost",
            },
        ],
    };

    /// <summary>Reads the structured-evidence availability field used by historical fixture conversion.</summary>
    internal sealed record TestEvidenceState([property: System.Text.Json.Serialization.JsonPropertyName("state")] EvidenceState State);

    internal static PublicationEvidence ReadPublication(EvidencePaths paths, RunContext context)
    {
        static T? Optional<T>(string path) where T : class
        {
            var node = File.Exists(path) ? CiFailureJson.Read(path) : null;
            return node is JsonObject ? CiFailureJson.ToContract<T>(node) : null;
        }
        var state = Optional<CandidateHistoryStatus>(paths.CandidateHistoryStatus)?.State ?? CandidateHistoryState.Unavailable;
        ImmutableArray<CandidateMerge>? candidates = null;
        if (context.RunScope == RunScope.Main && state == CandidateHistoryState.Available)
        {
            var node = File.Exists(paths.CandidateMerges) ? CiFailureJson.Read(paths.CandidateMerges) : null;
            candidates = node is JsonArray ? CiFailureJson.ToContract<ImmutableArray<CandidateMerge>>(node) : [];
        }
        return new(
            Optional<PullRequestMetadata>(paths.PullRequestMetadata),
            Optional<WorkflowRun>(paths.LastSuccessfulMainRun),
            Optional<MergedPullRequest>(paths.TriggeringMerge),
            state,
            candidates);
    }

    internal static (EvidenceBundle Evidence, AnalysisProposal Proposal) Read(string workingDirectory)
    {
        var root = Path.Combine(workingDirectory, "ci-failure-analysis/evidence");
        var output = Path.Combine(workingDirectory, "ci-analysis-output");
        JsonNode? Read(string name)
        {
            var path = Path.Combine(
                root,
                name is "run-context.json" or "test-evidence.json" or "test-failures.json"
                ? name : "diagnostics/" + name);
            return File.Exists(path) ? CiFailureJson.Read(path) : null;
        }
        var context = Read("run-context.json")!;
        var metadata = Read("run.json")!;
        var report = CiFailureJson.Read(Path.Combine(output, "analysis-result.json"))!;
        var jobData = (JsonArray)Read("failed-jobs.json")!;
        AddFixtureStepNumbers(jobData);
        var jobs = CollectionProjection.ProjectJobs(jobData);
        var scope = CiFailureJson.ReadEnum<RunScope>(CiFailureJson.Text(context["run_scope"]), "Run scope is invalid");
        var state = CiFailureJson.ReadEnum<EvidenceState>(CiFailureJson.Text(Read("test-evidence.json")?["state"]), "Test evidence state is invalid");
        var raw = Read("test-failures.json") as JsonArray ?? [];
        var tests = TestFailureReader.ConsolidateFailures(raw.Select(test =>
        {
            var input = CiFailureJson.ToContract<TestFailureInput>(test)!;
            var matching = jobs.Where(job => input.JobId is { } id ? job.Id == GitHubJson.ReadRequiredId(id) : job.Name == input.Job).ToArray();
            return new RawTestFailure(
                matching.Length == 1 ? matching[0].Id : 1,
                input.Job ?? "",
                input.Name ?? "",
                new CollectedDiagnostics
                {
                    Error = input.Error ?? "",
                    StackTrace = input.StackTrace ?? "",
                    StandardOutput = input.StandardOutput ?? "",
                    StandardError = input.StandardError ?? "",
                },
                new TestResultSource
                {
                    ArtifactId = 8,
                    ResultFile = input.ResultFile ?? "results.trx",
                    Format = input.ResultFormat ?? TestArtifactFormat.Trx,
                    ExecutionId = input.ExecutionId,
                });
        }).ToImmutableArray());
        var prior = Directory.Exists(Path.Combine(root, "history/causes"))
            ? Directory.GetFiles(Path.Combine(root, "history/causes"), "*.json").ToImmutableDictionary(
                path => "history/causes/" + Path.GetFileName(path),
                path => new CollectedAttachment(File.ReadAllBytes(path).ToImmutableArray()))
            : ImmutableDictionary<string, CollectedAttachment>.Empty;
        var index = PriorCauseCatalogue.BuildIndex(prior, tests);
        var progress = new AnalysisProgress
        {
            RunId = GitHubJson.ReadRequiredId(context["run_id"]), RunAttempt = context["run_attempt"]?.GetValue<int>() ?? 1,
            RunScope = scope, HasRelevantFailures = true,
            SubjectPrNumber = long.TryParse(CiFailureJson.Text(context["pr_numbers"]), NumberStyles.None, CultureInfo.InvariantCulture, out var pr) && pr > 0 ? pr : null,
        };
        var docs = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var name in new[] { "pr-metadata.json", "last-successful-main-run.json", "triggering-merge-pr.json", "candidate-merge-history-status.json", "candidate-merges.json" })
        {
            try
            {
                docs[name] = Read(name);
            }
            catch (JsonException)
            {
                docs[name] = null;
            }
        }
        // Historical fixtures predate captured upstream event/branch fields.
        metadata["head_sha"] ??= context["head_sha"]?.DeepClone() ?? JsonValue.Create("");
        metadata["event"] ??= scope == RunScope.Main ? "push" : "pull_request";
        metadata["head_branch"] ??= scope == RunScope.Main ? "main" : "feature";
        docs["run.json"] = metadata;
        var prMetadata = docs.GetValueOrDefault("pr-metadata.json") as JsonObject;
        var capturedPr = prMetadata is null || prMetadata.Count == 0 ? null : new CollectedPrMetadata
        {
            Title = CiFailureJson.Text(prMetadata["title"]) ?? "", Author = CiFailureJson.Text(prMetadata["user"]) ?? "",
            State = CiFailureJson.Text(prMetadata["state"]) ?? "", HeadBranch = CiFailureJson.Text(prMetadata["head_branch"]) ?? "",
            BaseBranch = CiFailureJson.Text(prMetadata["base_branch"]) ?? "", Url = CiFailureJson.Text(prMetadata["html_url"]) ?? "",
        };
        var history = docs.GetValueOrDefault("candidate-merge-history-status.json") is JsonObject historyNode
            ? CiFailureJson.ToContract<CandidateHistoryStatus>(historyNode)?.State ?? CandidateHistoryState.Unavailable
            : CandidateHistoryState.Unavailable;
        var candidates = docs.GetValueOrDefault("candidate-merges.json") is JsonArray candidateNodes
            ? CiFailureJson.ToContract<ImmutableArray<CandidateMerge>>(candidateNodes) : [];
        var main = scope != RunScope.Main ? null : CollectionProjection.ProjectMainContext(
            CiFailureJson.Text(metadata["head_sha"]) ?? "",
            docs.GetValueOrDefault("last-successful-main-run.json") as JsonObject ?? [],
            docs.GetValueOrDefault("triggering-merge-pr.json") as JsonObject ?? [],
            candidates,
            history);
        var bundle = CollectionProjection.CreateBundle("microsoft/aspire", progress, metadata, jobs, tests, state, index, capturedPr, main);
        var decisions = ((report["failed_tests"] as JsonArray) ?? []).Select((test, ordinal) =>
        {
            var matching = bundle.TestEvidence.Failures.Where(collected => collected.Name == CiFailureJson.Text(test?["name"]) &&
                (test?["job_id"] is { } id ? collected.JobId == GitHubJson.ReadRequiredId(id) : jobs.Any(job => job.Id == collected.JobId && job.Name == CiFailureJson.Text(test?["job"])))).ToArray();
            var observation = CiFailureJson.Text(test?["observation_id"]);
            var selected = matching.Length == 1 ? matching[0] : matching.FirstOrDefault(collected =>
                observation == $"observation-{bundle.TestEvidence.Failures.IndexOf(collected) + 1}");
            return new ProposedTestDecision
            {
                Ref = selected?.Ref ?? $"unknown-test-{ordinal}",
                Classification = CiFailureJson.ReadEnum<TestClassification>(CiFailureJson.Text(test?["classification"]), "Test classification is invalid"),
                Reason = CiFailureJson.Text(test?["reason"]) ?? "",
            };
        }).ToImmutableArray();
        var causesDirectory = Path.Combine(output, "causes");
        var causes = Directory.Exists(causesDirectory) ? Directory.GetFiles(causesDirectory, "*.json").Order(StringComparer.Ordinal).Select(path =>
        {
            var cause = CiFailureJson.Read(path)!;
            var id = CiFailureJson.Text(cause["id"])!;
            var jobIds = (cause["job_ids"] as JsonArray ?? []).Select(GitHubJson.ReadRequiredId).ToImmutableArray();
            var type = CiFailureJson.ReadEnum<CauseType>(CiFailureJson.Text(cause["type"]), "Cause type is invalid");
            var references = type != CauseType.FlakyTest ? [] : bundle.TestEvidence.Failures.Where(test =>
                cause["tests"] is JsonArray explicitTests
                    ? explicitTests.Any(reference => CiFailureJson.Text(reference?["name"]) == test.Name &&
                        GitHubJson.ReadRequiredId(reference?["job_id"]) == test.JobId &&
                        (reference?["observation_id"] is null || CiFailureJson.Text(reference["observation_id"]) == $"observation-{bundle.TestEvidence.Failures.IndexOf(test) + 1}"))
                    : test.Name == CiFailureJson.Text(cause["test_name"]) && jobIds.Contains(test.JobId))
                .Select(test => test.Ref).ToImmutableArray();
            if (!bundle.PriorCauseIdentities.Any(priorCause => priorCause.Id == id) &&
                File.Exists(Path.Combine(workingDirectory, "memory-repo", "causes", Path.GetFileName(path))))
            {
                bundle = bundle with
                {
                    PriorCauseIdentities = bundle.PriorCauseIdentities.Add(new PriorCauseIdentity { Id = id, Type = type }),
                };
            }
            if (bundle.PriorCauseIdentities.Any(priorCause => priorCause.Id == id))
            {
                return (ProposedCause)new ExistingCauseProposal { Id = id, JobIds = jobIds, TestRefs = references, Reason = "Recorded mechanism" };
            }
            return new NewCauseProposal
            {
                Definition = new ProposedCauseDefinition
                {
                    Id = id,
                    Type = type,
                    Title = CiFailureJson.Text(cause["title"])!,
                    ErrorPattern = CiFailureJson.Text(cause["error_pattern"])!,
                },
                JobIds = jobIds,
                TestRefs = references,
                Reason = "Current mechanism",
            };
        }).ToImmutableArray() : [];
        return (bundle, new AnalysisProposal
        {
            SchemaVersion = 1, Run = new ProposedWorkflowRun
            {
                Repository = "microsoft/aspire", Id = GitHubJson.ReadRequiredId(report["run_id"]),
                Attempt = report["run_attempt"]?.GetValue<int>() ?? 1,
            },
            Jobs = (report["failed_jobs"] as JsonArray ?? []).Select(job => new ProposedJobDecision
            {
                JobId = GitHubJson.ReadRequiredId(job?["id"]),
                Classification = CiFailureJson.ReadEnum<JobClassification>(CiFailureJson.Text(job?["classification"]), "Job classification is invalid"),
                Reason = CiFailureJson.Text(job?["reason"]) ?? "",
            }).ToImmutableArray(),
            Tests = decisions, Causes = causes,
        });
    }

    internal static void Write(string workingDirectory)
    {
        var (evidence, proposal) = Read(workingDirectory);
        File.WriteAllText(Path.Combine(workingDirectory, "ci-failure-analysis/evidence", "evidence.json"), TransientJson.Write(evidence));
        File.WriteAllText(Path.Combine(workingDirectory, "ci-analysis-output", "proposal.json"), TransientJson.Write(proposal));
    }

    internal static ValidatedAnalysis Validate(string workingDirectory)
    {
        var (evidence, proposal) = Read(workingDirectory);
        return AnalysisValidator.Validate(evidence, proposal, "microsoft/aspire");
    }

    internal static void AddFixtureStepNumbers(JsonArray jobs)
    {
        foreach (var job in jobs.OfType<JsonObject>())
        {
            foreach (var (step, index) in (job["steps"] as JsonArray ?? []).Select((step, index) => (step, index)))
            {
                if (step is JsonObject entry)
                {
                    entry["number"] ??= index + 1;
                }
            }
        }
    }

    /// <summary>Supplies raw structured-test fields when constructing scenario evidence fixtures.</summary>
    private sealed record TestFailureInput
    {
        [JsonPropertyName("test")] public string? Name { get; init; }
        [JsonPropertyName("job")] public string? Job { get; init; }
        [JsonPropertyName("job_id")] public JsonElement? JobId { get; init; }
        [JsonPropertyName("error")] public string? Error { get; init; }
        [JsonPropertyName("stack_trace")] public string? StackTrace { get; init; }
        [JsonPropertyName("standard_output")] public string? StandardOutput { get; init; }
        [JsonPropertyName("standard_error")] public string? StandardError { get; init; }
        [JsonPropertyName("result_file")] public string? ResultFile { get; init; }
        [JsonPropertyName("result_format")] public TestArtifactFormat? ResultFormat { get; init; }
        [JsonPropertyName("execution_id")] public string? ExecutionId { get; init; }
    }
}
