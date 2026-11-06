// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Text.Json;
using AnalyzeCiFailure;
using VerifyXunit;
using Xunit;
using static AnalyzeCiFailure.ValidationRules;

namespace Infrastructure.Tests;

/// <summary>Checks the verdict and publication policy across combinations of job and test classifications.</summary>
public sealed class AnalyzeCiFailureVerdictMatrixTests
{
    [Fact]
    public async Task CompleteRulesHaveOneAcceptedVerdictForEachSupportedDecisionShape()
    {
        var results = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var scope in new[] { RunScope.Main, RunScope.PullRequest })
        {
            foreach (var first in Enum.GetValues<JobClassification>())
            {
                foreach (var second in Enum.GetValues<JobClassification>().Select(value => (JobClassification?)value).Prepend(element: null))
                {
                    foreach (var testClass in new[] { (TestClassification?)null, TestClassification.Flaky, TestClassification.CodeIssue })
                    {
                        foreach (var overlap in new[] { false, true })
                        {
                            var classifications = second is { } value ? new[] { first, value } : [first];
                            var jobs = classifications.Select((_, index) =>
                                new FailedJob(NormalizedJsonNumber.Parse((index + 1).ToString()), (index + 1).ToString(), $"Job {index + 1}")).ToImmutableArray();
                            var tests = testClass is { } classification
                                ? ImmutableArray.Create(new AnalysisTest
                                {
                                    Name = "Tests.Example",
                                    Job = "Job 1",
                                    JobId = JsonSerializer.SerializeToElement(value: 1),
                                    Classification = classification,
                                    Reason = "Decision",
                                })
                                : [];
                            var causes = classifications.Select((jobClass, index) => (jobClass, index))
                                .Where(item => item.jobClass != JobClassification.CodeIssue)
                                .Select(item => new FailureCause(
                                    $"cause-{item.index + 1}",
                                    $"cause-{item.index + 1}.json",
                                    item.jobClass switch
                                    {
                                        JobClassification.TransientInfrastructure => CauseType.InfrastructureFailure,
                                        JobClassification.FlakyTest => CauseType.FlakyTest,
                                        _ => CauseType.MainRepositoryBreakage,
                                    },
                                    [jobs[item.index].Id],
                                    item.jobClass == JobClassification.FlakyTest
                                        ? testClass == TestClassification.Flaky && item.index == 0
                                            ? [new TestObservation("Tests.Example", jobs[0].Id)] : []
                                        : default))
                                .ToList();
                            if (testClass == TestClassification.Flaky && first != JobClassification.FlakyTest)
                            {
                                causes.Add(new FailureCause(
                                    "test-cause",
                                    "test-cause.json",
                                    CauseType.FlakyTest,
                                    [jobs[0].Id],
                                    [new TestObservation("Tests.Example", jobs[0].Id)]));
                            }
                            if (overlap && causes.FirstOrDefault(cause => cause.Type == CauseType.FlakyTest) is { } shared)
                            {
                                causes.Add(shared with { Id = "overlapping-cause", FileName = "overlapping-cause.json" });
                            }
                            var accepted = new List<string>();
                            foreach (var verdict in Enum.GetValues<AnalysisVerdict>())
                            {
                                var report = new AnalysisReport
                                {
                                    Verdict = verdict,
                                    Jobs = classifications.Select((jobClass, index) => new AnalysisJob
                                    {
                                        Id = JsonSerializer.SerializeToElement(index + 1),
                                        Classification = jobClass,
                                        Reason = "Decision",
                                    }).ToImmutableArray(),
                                    Tests = tests,
                                    Causes = causes.Select(cause => cause.Id).ToImmutableArray(),
                                };
                                var run = new ValidatedRun(
                                    scope,
                                    "https://github.com/microsoft/aspire/actions/runs/123",
                                    scope == RunScope.Main ? "" : "42");
                                try
                                {
                                    AnalysisValidator.ValidateBundle(AnalyzeCiFailureFixtures.CreateEvidence(scope), "microsoft/aspire");
                                    RequirePermittedVerdict(report, run);
                                    RequireExactJobCoverage(report, jobs, run);
                                    RequireExactTestCoverage(report, tests.Select(test => new TestIdentity(test.Name!, test.Job!, test.NormalizedJobId)).ToImmutableArray(), jobs);
                                    foreach (var cause in causes)
                                    {
                                        RequireCauseFields(cause);
                                        ValidateCauseCompatibility(cause, report, jobs, run);
                                    }
                                    var immutableCauses = causes.ToImmutableArray();
                                    ValidateVerdict(report, immutableCauses, run);
                                    RequireCauseCoverage(report, immutableCauses);
                                    accepted.Add(CiFailureJson.EnumText(verdict));
                                }
                                catch (ValidationException)
                                {
                                    // An empty set records rejection by the complete production rule sequence.
                                }
                            }
                            Assert.True(accepted.Count <= 1, $"{scope}/{first}/{second}/{testClass}/{overlap}: {string.Join(", ", accepted)}");
                            results[$"{scope}/{first}/{second?.ToString() ?? "none"}/{testClass?.ToString() ?? "none"}/overlap={overlap}"] = accepted.ToArray();
                        }
                    }
                }
            }
        }

        Assert.Contains(results.Values, values => values.SequenceEqual(["mixed"]));
        Assert.Contains(results.Values, values => values.SequenceEqual(["transient-infra"]));
        Assert.Contains(results.Values, values => values.SequenceEqual(["flaky-test"]));
        Assert.Contains(results.Values, values => values.SequenceEqual(["code-issue"]));
        Assert.Contains(results.Values, values => values.SequenceEqual(["main-repository-breakage"]));
        await Verifier.Verify(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }), "json")
            .UseDirectory("Snapshots");
    }
}
