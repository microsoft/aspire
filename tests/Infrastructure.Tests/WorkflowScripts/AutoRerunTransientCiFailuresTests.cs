// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Tests for .github/workflows/auto-rerun-transient-ci-failures.js.
/// </summary>
public sealed class AutoRerunTransientCiFailuresTests : IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly TestTempDirectory _tempDir = new();
    private readonly string _repoRoot;
    private readonly string _harnessPath;
    private readonly ITestOutputHelper _output;

    public AutoRerunTransientCiFailuresTests(ITestOutputHelper output)
    {
        _output = output;
        _repoRoot = FindRepoRoot();
        _harnessPath = Path.Combine(_repoRoot, "tests", "Infrastructure.Tests", "WorkflowScripts", "auto-rerun-transient-ci-failures.harness.js");
    }

    public void Dispose() => _tempDir.Dispose();

    [Fact]
    [RequiresTools(["node"])]
    public async Task RetriesJobLevelInfrastructureFailureWithNoFailedSteps()
    {
        WorkflowJob job = CreateJob(failedSteps: []);

        AnalyzeFailedJobsResult result = await AnalyzeSingleJobAsync(job, "The hosted runner lost communication with the server.");

        Assert.Single(result.RetryableJobs);
        Assert.Equal("Job-level runner or infrastructure failure matched the transient allowlist.", result.RetryableJobs[0].Reason);
        Assert.Empty(result.SkippedJobs);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RetriesRetrySafeFailedStepWhenAnnotationsMatchTransientSignature()
    {
        WorkflowJob job = CreateJob(failedSteps: ["Checkout code"]);

        AnalyzeFailedJobsResult result = await AnalyzeSingleJobAsync(job, "fatal: expected 'packfile'");

        Assert.Single(result.RetryableJobs);
        Assert.Equal("Failed step 'Checkout code' matched the transient annotation allowlist.", result.RetryableJobs[0].Reason);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task SkipsJobsWhoseFailedStepsAreOutsideRetrySafeAllowlist()
    {
        WorkflowJob job = CreateJob(failedSteps: ["Compile project"]);

        AnalyzeFailedJobsResult result = await AnalyzeSingleJobAsync(job, "The hosted runner lost communication with the server.");

        Assert.Empty(result.RetryableJobs);
        Assert.Single(result.SkippedJobs);
        Assert.Equal("Failed step 'Compile project' is not covered by the retry-safe rerun rules.", result.SkippedJobs[0].Reason);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task SkipsRetrySafeStepsWhenAnnotationsAreGeneric()
    {
        WorkflowJob job = CreateJob(failedSteps: ["Set up .NET Core"]);

        AnalyzeFailedJobsResult result = await AnalyzeSingleJobAsync(job, "Process completed with exit code 1.");

        Assert.Empty(result.RetryableJobs);
        Assert.Single(result.SkippedJobs);
        Assert.Equal("Failed step 'Set up .NET Core' did not include a retry-safe transient infrastructure signal in the job annotations.", result.SkippedJobs[0].Reason);
    }

    [Theory]
    [InlineData("Final Results")]
    [InlineData("Tests / Final Test Results")]
    [RequiresTools(["node"])]
    public async Task IgnoresConfiguredAggregatorJobsEntirely(string jobName)
    {
        WorkflowJob job = CreateJob(name: jobName, failedSteps: ["Set up job"]);

        AnalyzeFailedJobsResult result = await AnalyzeSingleJobAsync(job, "The hosted runner lost communication with the server.");

        Assert.Empty(result.FailedJobs);
        Assert.Empty(result.RetryableJobs);
        Assert.Empty(result.SkippedJobs);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task KeepsMixedFailureVetoWhenIgnoredTestStepsFailAlongsideRetrySafeSteps()
    {
        WorkflowJob job = CreateJob(failedSteps: ["Run tests (Windows)", "Upload logs, and test results"]);

        AnalyzeFailedJobsResult result = await AnalyzeSingleJobAsync(job, "Failed to CreateArtifact: Unable to make request: ENOTFOUND");

        Assert.Empty(result.RetryableJobs);
        Assert.Single(result.SkippedJobs);
        Assert.Equal(
            "Failed steps 'Run tests (Windows) | Upload logs, and test results' include a test execution failure, so the job was not retried without a high-confidence infrastructure override.",
            result.SkippedJobs[0].Reason);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task AllowsNarrowOverrideForExplicitJobLevelInfrastructureAnnotationsOnIgnoredSteps()
    {
        WorkflowJob job = CreateJob(failedSteps: ["Run tests (Windows)"]);

        AnalyzeFailedJobsResult result = await AnalyzeSingleJobAsync(job, "The hosted runner lost communication with the server.");

        Assert.Single(result.RetryableJobs);
        Assert.Equal("Ignored failed step 'Run tests (Windows)' matched the job-level infrastructure override allowlist.", result.RetryableJobs[0].Reason);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task AllowsNarrowOverrideForWindowsPostTestCleanupProcessInitializationFailures()
    {
        WorkflowJob job = CreateJob(failedSteps:
        [
            "Upload logs, and test results",
            "Copy CLI E2E recordings for upload",
            "Upload CLI E2E recordings",
            "Generate test results summary",
            "Post Checkout code"
        ]);

        AnalyzeFailedJobsResult result = await AnalyzeSingleJobAsync(job, "Process completed with exit code -1073741502.");

        Assert.Single(result.RetryableJobs);
        Assert.Equal(
            "Post-test cleanup steps 'Upload logs, and test results | Copy CLI E2E recordings for upload | Upload CLI E2E recordings | Generate test results summary | Post Checkout code' matched the Windows process initialization failure override allowlist.",
            result.RetryableJobs[0].Reason);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task DoesNotOverrideWindowsProcessInitializationFailuresWhenTestExecutionAlsoFailed()
    {
        WorkflowJob job = CreateJob(failedSteps:
        [
            "Run tests (Windows)",
            "Upload logs, and test results",
            "Generate test results summary"
        ]);

        AnalyzeFailedJobsResult result = await AnalyzeSingleJobAsync(job, "Process completed with exit code -1073741502.");

        Assert.Empty(result.RetryableJobs);
        Assert.Single(result.SkippedJobs);
        Assert.Equal(
            "Failed steps 'Run tests (Windows) | Upload logs, and test results | Generate test results summary' include a test execution failure, so the job was not retried without a high-confidence infrastructure override.",
            result.SkippedJobs[0].Reason);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task AllowsNarrowLogBasedOverrideForDncengFeedServiceIndexFailuresInIgnoredBuildSteps()
    {
        WorkflowJob job = CreateJob(failedSteps: ["Build test project"]);

        AnalyzeFailedJobsResult result = await AnalyzeSingleJobAsync(
            job,
            "Process completed with exit code 1.",
            "error : Unable to load the service index for source https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json.");

        Assert.Single(result.RetryableJobs);
        Assert.Equal(
            "Failed step 'Build test project' will be retried because the job log shows a likely transient infrastructure network failure. Matched pattern: `/Unable to load the service index for source https:\\/\\/(?:pkgs\\.dev\\.azure\\.com\\/dnceng|dnceng\\.pkgs\\.visualstudio\\.com)\\/public\\/_packaging\\//i`.",
            result.RetryableJobs[0].Reason);
    }

    [Theory]
    [InlineData("Install sdk for nuget based testing")]
    [InlineData("Build with packages")]
    [InlineData("Run TypeScript SDK validation")]
    [InlineData("Build Python validation image")]
    [RequiresTools(["node"])]
    public async Task AllowsSameFeedOverrideForOtherCiBootstrapBuildAndValidationSteps(string failedStep)
    {
        WorkflowJob job = CreateJob(failedSteps: [failedStep]);

        AnalyzeFailedJobsResult result = await AnalyzeSingleJobAsync(
            job,
            "Process completed with exit code 1.",
            "error : Unable to load the service index for source https://dnceng.pkgs.visualstudio.com/public/_packaging/dotnet9-transport/nuget/v3/index.json.");

        Assert.Single(result.RetryableJobs);
        Assert.Equal(
            $"Failed step '{failedStep}' will be retried because the job log shows a likely transient infrastructure network failure. Matched pattern: `/Unable to load the service index for source https:\\/\\/(?:pkgs\\.dev\\.azure\\.com\\/dnceng|dnceng\\.pkgs\\.visualstudio\\.com)\\/public\\/_packaging\\//i`.",
            result.RetryableJobs[0].Reason);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task AllowsSameNetworkOverrideForBroaderBootstrapStepsOutsideTheOldAllowlist()
    {
        WorkflowJob job = CreateJob(failedSteps: ["Run ./.github/actions/enumerate-tests"]);

        AnalyzeFailedJobsResult result = await AnalyzeSingleJobAsync(
            job,
            "Process completed with exit code 1.",
            "error : Unable to load the service index for source https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json.");

        Assert.Single(result.RetryableJobs);
        Assert.Equal(
            "Failed step 'Run ./.github/actions/enumerate-tests' will be retried because the job log shows a likely transient infrastructure network failure. Matched pattern: `/Unable to load the service index for source https:\\/\\/(?:pkgs\\.dev\\.azure\\.com\\/dnceng|dnceng\\.pkgs\\.visualstudio\\.com)\\/public\\/_packaging\\//i`.",
            result.RetryableJobs[0].Reason);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task UsesPluralFailedStepLabelWhenMultipleFailedStepsShareALogBasedOverride()
    {
        WorkflowJob job = CreateJob(failedSteps: ["Build test project", "Check validation results"]);

        AnalyzeFailedJobsResult result = await AnalyzeSingleJobAsync(
            job,
            "Process completed with exit code 1.",
            "error : Unable to load the service index for source https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json.");

        Assert.Single(result.RetryableJobs);
        Assert.Equal(
            "Failed steps 'Build test project | Check validation results' will be retried because the job log shows a likely transient infrastructure network failure. Matched pattern: `/Unable to load the service index for source https:\\/\\/(?:pkgs\\.dev\\.azure\\.com\\/dnceng|dnceng\\.pkgs\\.visualstudio\\.com)\\/public\\/_packaging\\//i`.",
            result.RetryableJobs[0].Reason);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task UsesLongerMarkdownFenceWhenMatchedPatternContainsBackticks()
    {
        string result = await InvokeHarnessAsync<string>(
            "formatMatchedPatternForMarkdown",
            new
            {
                matchedPattern = "/foo`bar/i"
            });

        Assert.Equal(" Matched pattern: ``/foo`bar/i``.", result);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task DoesNotApplyBroadNetworkOverrideWhenTestExecutionFailed()
    {
        WorkflowJob job = CreateJob(failedSteps: ["Run tests (Windows)"]);

        AnalyzeFailedJobsResult result = await AnalyzeSingleJobAsync(
            job,
            "Process completed with exit code 1.",
            "error : Unable to load the service index for source https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json.");

        Assert.Empty(result.RetryableJobs);
        Assert.Single(result.SkippedJobs);
        Assert.Equal(
            "Failed step 'Run tests (Windows)' includes a test execution failure, so the job was not retried without a high-confidence infrastructure override.",
            result.SkippedJobs[0].Reason);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task DoesNotRetryIgnoredBuildStepsWhenTheLogLacksFeedNetworkSignature()
    {
        WorkflowJob job = CreateJob(failedSteps: ["Build test project"]);

        AnalyzeFailedJobsResult result = await AnalyzeSingleJobAsync(
            job,
            "Process completed with exit code 1.",
            "error MSB4236: The SDK specified could not be found.");

        Assert.Empty(result.RetryableJobs);
        Assert.Single(result.SkippedJobs);
        Assert.Equal(
            "Failed step 'Build test project' is only retried when the job shows a high-confidence infrastructure override, and none was found.",
            result.SkippedJobs[0].Reason);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task UsesAvailableDiagnosticsMessageWhenNoSignalsWereInspectedFromFailedStepsOrLogs()
    {
        WorkflowJob job = CreateJob(failedSteps: []);

        AnalyzeFailedJobsResult result = await AnalyzeSingleJobAsync(job, string.Empty);

        Assert.Empty(result.RetryableJobs);
        Assert.Single(result.SkippedJobs);
        Assert.Equal(
            "No retry-safe transient infrastructure signal was found in the available job diagnostics.",
            result.SkippedJobs[0].Reason);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ContinuesInspectingLaterLogsWhenEarlierCandidatesAreNotRetryable()
    {
        WorkflowJob firstJob = CreateJob(id: 1, failedSteps: ["Build test project"]);
        WorkflowJob secondJob = CreateJob(id: 2, failedSteps: ["Build test project"]);

        AnalyzeFailedJobsResult result = await AnalyzeJobsAsync(
            [firstJob, secondJob],
            new Dictionary<string, string>
            {
                ["1"] = "Process completed with exit code 1.",
                ["2"] = "Process completed with exit code 1."
            },
            new Dictionary<string, string>
            {
                ["1"] = "error MSB4236: The SDK specified could not be found.",
                ["2"] = "error : Unable to load the service index for source https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json."
            },
            maxRetryableJobs: 1);

        Assert.Equal([1, 2], result.LogRequestJobIds);
        Assert.Single(result.RetryableJobs);
        Assert.Equal(2, result.RetryableJobs[0].Id);
        Assert.Equal(1, result.LogRequestJobIds[0]);
        Assert.Single(result.SkippedJobs);
        Assert.Equal(1, result.SkippedJobs[0].Id);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task GetCheckRunIdForJobParsesCheckRunIdFromWorkflowJobPayload()
    {
        int? checkRunId = await InvokeHarnessAsync<int?>(
            "getCheckRunIdForJob",
            new
            {
                job = new WorkflowJob
                {
                    Id = 10,
                    CheckRunUrl = "https://api.github.com/repos/microsoft/aspire/check-runs/123456789"
                }
            });

        Assert.Equal(123456789, checkRunId);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task GetCheckRunIdForJobFallsBackToLoadingWorkflowJobWhenNeeded()
    {
        int? checkRunId = await InvokeHarnessAsync<int?>(
            "getCheckRunIdForJob",
            new
            {
                job = new WorkflowJob { Id = 42 },
                workflowJob = new WorkflowJob
                {
                    CheckRunUrl = "https://api.github.com/repos/microsoft/aspire/check-runs/987654321"
                }
            });

        Assert.Equal(987654321, checkRunId);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task GetCheckRunIdForJobReturnsNullWhenNoCheckRunIdCanBeResolved()
    {
        int? checkRunId = await InvokeHarnessAsync<int?>(
            "getCheckRunIdForJob",
            new
            {
                job = new WorkflowJob
                {
                    Id = 42,
                    CheckRunUrl = "https://api.github.com/repos/microsoft/aspire/actions/jobs/42"
                },
                workflowJob = new WorkflowJob()
            });

        Assert.Null(checkRunId);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task GetAssociatedPullRequestNumbersPrefersWorkflowRunPayloadWhenPresent()
    {
        AssociatedPullRequestNumbersResult result = await InvokeHarnessAsync<AssociatedPullRequestNumbersResult>(
            "getAssociatedPullRequestNumbers",
            new
            {
                workflowRun = new
                {
                    pull_requests = new[]
                    {
                        new { number = 15110 },
                        new { number = 15110 },
                        new { number = 15111 }
                    }
                }
            });

        Assert.Equal([15110, 15111], result.PullRequestNumbers);
        Assert.Empty(result.Requests);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task GetAssociatedPullRequestNumbersFallsBackToHeadLookupWhenPayloadOmitsPullRequests()
    {
        AssociatedPullRequestNumbersResult result = await InvokeHarnessAsync<AssociatedPullRequestNumbersResult>(
            "getAssociatedPullRequestNumbers",
            new
            {
                workflowRun = new
                {
                    head_branch = "conditional-test-runs",
                    head_sha = "5ccc5540dcc68f57dbe10ff941d1f39bab9f9336",
                    head_repository = new
                    {
                        owner = new
                        {
                            login = "radical"
                        }
                    }
                },
                pullRequestsByHead = new Dictionary<string, object[]>
                {
                    ["radical:conditional-test-runs"] =
                    [
                        new
                        {
                            number = 13832,
                            head = new
                            {
                                @ref = "conditional-test-runs",
                                sha = "5ccc5540dcc68f57dbe10ff941d1f39bab9f9336",
                                repo = new
                                {
                                    owner = new
                                    {
                                        login = "radical"
                                    }
                                }
                            }
                        }
                    ]
                }
            });

        Assert.Equal([13832], result.PullRequestNumbers);

        RequestRecord request = Assert.Single(result.Requests);
        Assert.Equal("GET /repos/{owner}/{repo}/pulls", request.Route);
        Assert.Equal("radical:conditional-test-runs", request.Payload.GetProperty("head").GetString());
        Assert.Equal("all", request.Payload.GetProperty("state").GetString());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task GetAssociatedPullRequestNumbersPaginatesFallbackLookupUntilItFindsTheMatchingSha()
    {
        AssociatedPullRequestNumbersResult result = await InvokeHarnessAsync<AssociatedPullRequestNumbersResult>(
            "getAssociatedPullRequestNumbers",
            new
            {
                workflowRun = new
                {
                    head_branch = "conditional-test-runs",
                    head_sha = "wanted-sha",
                    head_repository = new
                    {
                        owner = new
                        {
                            login = "radical"
                        }
                    }
                },
                pullRequestsByHeadPages = new Dictionary<string, object[][]>
                {
                    ["radical:conditional-test-runs"] =
                    [
                        [
                            new
                            {
                                number = 11111,
                                head = new
                                {
                                    @ref = "conditional-test-runs",
                                    sha = "old-sha",
                                    repo = new
                                    {
                                        owner = new
                                        {
                                            login = "radical"
                                        }
                                    }
                                }
                            }
                        ],
                        [
                            new
                            {
                                number = 13832,
                                head = new
                                {
                                    @ref = "conditional-test-runs",
                                    sha = "wanted-sha",
                                    repo = new
                                    {
                                        owner = new
                                        {
                                            login = "radical"
                                        }
                                    }
                                }
                            }
                        ]
                    ]
                }
            });

        Assert.Equal([13832], result.PullRequestNumbers);
        Assert.Equal(2, result.Requests.Length);
        Assert.Equal(1, result.Requests[0].Payload.GetProperty("page").GetInt32());
        Assert.Equal(2, result.Requests[1].Payload.GetProperty("page").GetInt32());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task GetAssociatedPullRequestNumbersRejectsAmbiguousFallbackMatches()
    {
        AssociatedPullRequestNumbersResult result = await InvokeHarnessAsync<AssociatedPullRequestNumbersResult>(
            "getAssociatedPullRequestNumbers",
            new
            {
                workflowRun = new
                {
                    head_branch = "conditional-test-runs",
                    head_repository = new
                    {
                        owner = new
                        {
                            login = "radical"
                        }
                    }
                },
                pullRequestsByHead = new Dictionary<string, object[]>
                {
                    ["radical:conditional-test-runs"] =
                    [
                        new
                        {
                            number = 13832,
                            head = new
                            {
                                @ref = "conditional-test-runs",
                                sha = "first-sha",
                                repo = new
                                {
                                    owner = new
                                    {
                                        login = "radical"
                                    }
                                }
                            }
                        },
                        new
                        {
                            number = 15177,
                            head = new
                            {
                                @ref = "conditional-test-runs",
                                sha = "second-sha",
                                repo = new
                                {
                                    owner = new
                                    {
                                        login = "radical"
                                    }
                                }
                            }
                        }
                    ]
                }
            });

        Assert.Empty(result.PullRequestNumbers);
        Assert.Single(result.Requests);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task GetAssociatedPullRequestNumbersReturnsEmptyWhenFallbackLookupFails()
    {
        AssociatedPullRequestNumbersResult result = await InvokeHarnessAsync<AssociatedPullRequestNumbersResult>(
            "getAssociatedPullRequestNumbers",
            new
            {
                workflowRun = new
                {
                    head_branch = "conditional-test-runs",
                    head_repository = new
                    {
                        owner = new
                        {
                            login = "radical"
                        }
                    }
                },
                failPullRequestLookup = "HTTP 502"
            });

        Assert.Empty(result.PullRequestNumbers);
        Assert.Single(result.Requests);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ComputeRerunEligibilityStillReportsSafeCandidatesDuringDryRun()
    {
        bool rerunEligible = await InvokeHarnessAsync<bool>(
            "computeRerunEligibility",
            new
            {
                dryRun = true,
                retryableCount = 1
            });

        Assert.True(rerunEligible);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ComputeRerunExecutionEligibilityDisablesRerunsWhenDryRunIsEnabled()
    {
        bool rerunExecutionEligible = await InvokeHarnessAsync<bool>(
            "computeRerunExecutionEligibility",
            new
            {
                dryRun = true,
                retryableCount = 1
            });

        Assert.False(rerunExecutionEligible);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task AutomaticRerunRequiresAtLeastOneRetryableJob()
    {
        bool rerunEligible = await InvokeHarnessAsync<bool>(
            "computeRerunEligibility",
            new
            {
                retryableCount = 0
            });

        Assert.False(rerunEligible);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task AutomaticRerunIsEligibleWhenRetryableJobsStayWithinTheCap()
    {
        bool rerunEligible = await InvokeHarnessAsync<bool>(
            "computeRerunEligibility",
            new
            {
                retryableCount = 2
            });

        Assert.True(rerunEligible);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task AutomaticRerunIsSuppressedWhenMatchedJobsExceedTheCap()
    {
        bool rerunEligible = await InvokeHarnessAsync<bool>(
            "computeRerunEligibility",
            new
            {
                retryableCount = 6
            });

        Assert.False(rerunEligible);
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData(5, 1, true)]
    [InlineData(5, 2, false)]
    [InlineData(4, 2, true)]
    [InlineData(5, 3, false)]
    [InlineData(1, 4, false)]
    public async Task AttemptSpecificEligibilityRulesAreApplied(int retryableCount, int runAttempt, bool expectedEligible)
    {
        bool rerunEligible = await InvokeHarnessAsync<bool>(
            "computeRerunEligibility",
            new
            {
                retryableCount,
                runAttempt
            });

        Assert.Equal(expectedEligible, rerunEligible);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ExecutionEligibilityAppliesTheStricterCapAfterTheFirstAttempt()
    {
        bool rerunExecutionEligible = await InvokeHarnessAsync<bool>(
            "computeRerunExecutionEligibility",
            new
            {
                dryRun = false,
                retryableCount = 5,
                runAttempt = 2
            });

        Assert.False(rerunExecutionEligible);
    }

    [Fact]
    public async Task RepresentativeWorkflowFixturesStayAlignedWithCurrentWorkflowDefinitions()
    {
        Dictionary<string, string[]> expectations = new()
        {
            [".github/workflows/run-tests.yml"] =
            [
                "- name: Checkout code",
                "- name: Set up .NET Core",
                "- name: Install sdk for nuget based testing",
                "- name: Build test project",
                "- name: Run tests (Windows)",
                "- name: Upload logs, and test results",
                "- name: Copy CLI E2E recordings for upload",
                "- name: Upload CLI E2E recordings",
                "- name: Generate test results summary",
            ],
            [".github/workflows/build-packages.yml"] =
            [
                "- name: Build with packages",
            ],
            [".github/workflows/polyglot-validation.yml"] =
            [
                "- name: Build Python validation image",
                "- name: Run TypeScript SDK validation",
            ],
            [".github/workflows/ci.yml"] =
            [
                "name: Final Results",
            ],
            [".github/workflows/tests.yml"] =
            [
                "- uses: ./.github/actions/enumerate-tests",
                "name: Final Test Results",
            ],
        };

        foreach ((string relativePath, string[] expectedLines) in expectations)
        {
            string workflowText = await ReadRepoFileAsync(relativePath);

            foreach (string expectedLine in expectedLines)
            {
                Assert.Contains(expectedLine, workflowText);
            }
        }
    }

    [Fact]
    public async Task WorkflowYamlKeepsDocumentedSafetyRails()
    {
        string workflowText = await ReadRepoFileAsync(".github/workflows/auto-rerun-transient-ci-failures.yml");

        Assert.Contains("workflow_dispatch:", workflowText);
        Assert.Contains("dry_run:", workflowText);
        Assert.Contains("default: false", workflowText);
        Assert.Contains("rerun_execution_eligible", workflowText);
        Assert.Contains("needs.analyze-transient-failures.outputs.rerun_execution_eligible == 'true'", workflowText);
        Assert.Contains("MANUAL_DRY_RUN", workflowText);
        Assert.Contains("function parseManualDryRun()", workflowText);
        Assert.Contains("const dryRun = parseManualDryRun();", workflowText);
        Assert.Contains("computeRerunExecutionEligibility", workflowText);
        Assert.Contains("getAssociatedPullRequestNumbers", workflowText);
        Assert.Contains("github.event.workflow_run.run_attempt <= 3", workflowText);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task WriteAnalysisSummaryUsesExplicitOutcomeHeadingAndClickableAnalyzedRunLink()
    {
        SummaryResult result = await InvokeHarnessAsync<SummaryResult>(
            "writeAnalysisSummary",
            new
            {
                failedJobs = new[]
                {
                    new SummaryJob { Id = 11, Name = "Tests / One", Reason = "Reason one" }
                },
                retryableJobs = new[]
                {
                    new SummaryJob { Id = 11, Name = "Tests / One", Reason = "Reason one" }
                },
                skippedJobs = Array.Empty<SummaryJob>(),
                dryRun = false,
                rerunEligible = true,
                sourceRunAttempt = 1,
                sourceRunUrl = "https://github.com/microsoft/aspire/actions/runs/123"
            });

        SummaryEvent headingEvent = Assert.Single(result.Events, e => e.Type == "heading" && e.Level == 1);
        Assert.Equal("Rerun eligible", headingEvent.Text);

        SummaryEvent linkEvent = Assert.Single(result.Events, e => e.Type == "link" && e.Text == "workflow run attempt 1");
        Assert.Equal("https://github.com/microsoft/aspire/actions/runs/123/attempts/1", linkEvent.Href);

        SummaryEvent rawEvent = Assert.Single(result.Events, e => e.Type == "raw" && e.Text == "Matched 1 retry-safe job for rerun.");
        Assert.Equal("Matched 1 retry-safe job for rerun.", rawEvent.Text);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task WriteAnalysisSummaryShowsWouldRerunDuringDryRun()
    {
        SummaryResult result = await InvokeHarnessAsync<SummaryResult>(
            "writeAnalysisSummary",
            new
            {
                failedJobs = new[]
                {
                    new SummaryJob { Id = 11, Name = "Tests / One", Reason = "Reason one" }
                },
                retryableJobs = new[]
                {
                    new SummaryJob { Id = 11, Name = "Tests / One", Reason = "Reason one" }
                },
                skippedJobs = Array.Empty<SummaryJob>(),
                dryRun = true,
                rerunEligible = true,
                sourceRunAttempt = 1,
                sourceRunUrl = "https://github.com/microsoft/aspire/actions/runs/123"
            });

        SummaryEvent headingEvent = Assert.Single(result.Events, e => e.Type == "heading" && e.Level == 1);
        Assert.Equal("Rerun eligible", headingEvent.Text);

        SummaryEvent rawEvent = Assert.Single(
            result.Events,
            e => e.Type == "raw" && e.Text == "Matched 1 retry-safe job that would be rerun if dry run were disabled.");
        Assert.Equal("Matched 1 retry-safe job that would be rerun if dry run were disabled.", rawEvent.Text);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RerunMatchedJobsMakesNoRequestsWhenNoJobsAreSupplied()
    {
        RerunMatchedJobsResult result = await InvokeHarnessAsync<RerunMatchedJobsResult>(
            "rerunMatchedJobs",
            new
            {
                owner = "dotnet",
                repo = "aspire",
                retryableJobs = Array.Empty<RetryableJobInput>(),
                sourceRunUrl = "https://github.com/microsoft/aspire/actions/runs/123"
            });

        Assert.Empty(result.Requests);
        Assert.Empty(result.Events);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RerunMatchedJobsRequestsOneRunLevelRerunAndWritesTheSummary()
    {
        RerunMatchedJobsResult result = await InvokeHarnessAsync<RerunMatchedJobsResult>(
            "rerunMatchedJobs",
            new
            {
                owner = "dotnet",
                repo = "aspire",
                retryableJobs = new[]
                {
                    new RetryableJobInput
                    {
                        Id = 11,
                        Name = "Tests / One",
                        HtmlUrl = "https://github.com/microsoft/aspire/actions/runs/123/job/11",
                        Reason = "Reason one"
                    },
                    new RetryableJobInput
                    {
                        Id = 22,
                        Name = "Tests / Two",
                        HtmlUrl = "https://github.com/microsoft/aspire/actions/runs/123/job/22",
                        Reason = "Reason two"
                    }
                },
                pullRequestNumbers = new[] { 15110 },
                issueStatesByNumber = new Dictionary<string, string>
                {
                    ["15110"] = "open"
                },
                commentHtmlUrlByNumber = new Dictionary<string, string>
                {
                    ["15110"] = "https://github.com/microsoft/aspire/pull/15110#issuecomment-123"
                },
                latestRunAttempt = 2,
                sourceRunId = 123,
                sourceRunAttempt = 1,
                sourceRunUrl = "https://github.com/microsoft/aspire/actions/runs/123"
            });

        Assert.Collection(
            result.Requests,
            request =>
            {
                Assert.Equal("GET /repos/{owner}/{repo}/issues/{issue_number}", request.Route);
                Assert.Equal("dotnet", request.Payload.GetProperty("owner").GetString());
                Assert.Equal("aspire", request.Payload.GetProperty("repo").GetString());
                Assert.Equal(15110, request.Payload.GetProperty("issue_number").GetInt32());
            },
            request =>
            {
                Assert.Equal("POST /repos/{owner}/{repo}/actions/runs/{run_id}/rerun-failed-jobs", request.Route);
                Assert.Equal(123, request.Payload.GetProperty("run_id").GetInt32());
            },
            request =>
            {
                Assert.Equal("GET /repos/{owner}/{repo}/actions/runs/{run_id}", request.Route);
                Assert.Equal(123, request.Payload.GetProperty("run_id").GetInt32());
            },
            request =>
            {
                Assert.Equal("POST /repos/{owner}/{repo}/issues/{issue_number}/comments", request.Route);
                Assert.Equal(15110, request.Payload.GetProperty("issue_number").GetInt32());
                Assert.Equal(
                    "Re-running the failed jobs in the CI workflow for this pull request because 2 jobs were identified as retry-safe transient failures in [the CI run attempt](https://github.com/microsoft/aspire/actions/runs/123/attempts/1).\nGitHub was asked to rerun all failed jobs for that attempt, and the rerun is being tracked in [the rerun attempt](https://github.com/microsoft/aspire/actions/runs/123/attempts/2).\nThe job links below point to the failed attempt jobs that matched the retry-safe transient failure rules.\n\n- [Tests / One](https://github.com/microsoft/aspire/actions/runs/123/job/11) - Reason one\n- [Tests / Two](https://github.com/microsoft/aspire/actions/runs/123/job/22) - Reason two",
                    request.Payload.GetProperty("body").GetString());
            });

        SummaryEvent headingEvent = Assert.Single(result.Events, e => e.Type == "heading" && e.Level == 1);
        Assert.Equal("Rerun requested", headingEvent.Text);

        SummaryEvent failedAttemptLink = Assert.Single(result.Events, e => e.Type == "link" && e.Text == "workflow run attempt 1");
        Assert.Equal("https://github.com/microsoft/aspire/actions/runs/123/attempts/1", failedAttemptLink.Href);

        SummaryEvent rerunAttemptLink = Assert.Single(result.Events, e => e.Type == "link" && e.Text == "workflow run attempt 2");
        Assert.Equal("https://github.com/microsoft/aspire/actions/runs/123/attempts/2", rerunAttemptLink.Href);

        SummaryEvent commentLink = Assert.Single(result.Events, e => e.Type == "link" && e.Text == "PR #15110 comment");
        Assert.Equal("https://github.com/microsoft/aspire/pull/15110#issuecomment-123", commentLink.Href);

        SummaryEvent rerunExplanation = Assert.Single(result.Events, e => e.Type == "raw" && e.Text == "The matched jobs below made the run eligible for rerun. GitHub was asked to rerun all failed jobs for the failed attempt.");
        Assert.Equal("The matched jobs below made the run eligible for rerun. GitHub was asked to rerun all failed jobs for the failed attempt.", rerunExplanation.Text);

        SummaryEvent retryableJobsHeading = Assert.Single(result.Events, e => e.Type == "heading" && e.Level == 2 && e.Text == "Retryable jobs");
        Assert.Equal("Retryable jobs", retryableJobsHeading.Text);

        SummaryEvent tableEvent = Assert.Single(result.Events, e => e.Type == "table");
        Assert.Equal("Job", tableEvent.Rows[0][0].GetProperty("data").GetString());
        Assert.Equal("Reason", tableEvent.Rows[0][1].GetProperty("data").GetString());
        Assert.Equal("Tests / One", tableEvent.Rows[1][0].GetString());
        Assert.Equal("Reason one", tableEvent.Rows[1][1].GetString());
        Assert.Equal("Tests / Two", tableEvent.Rows[2][0].GetString());
        Assert.Equal("Reason two", tableEvent.Rows[2][1].GetString());

        Assert.Contains(result.Events, e => e.Type == "raw" && e.Text == "Pull request comments:");
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RerunMatchedJobsSkipsRerunsWhenAllAssociatedPullRequestsAreClosed()
    {
        RerunMatchedJobsResult result = await InvokeHarnessAsync<RerunMatchedJobsResult>(
            "rerunMatchedJobs",
            new
            {
                owner = "dotnet",
                repo = "aspire",
                retryableJobs = new[]
                {
                    new RetryableJobInput
                    {
                        Id = 11,
                        Name = "Tests / One",
                        HtmlUrl = "https://github.com/microsoft/aspire/actions/runs/123/job/11",
                        Reason = "Reason one"
                    }
                },
                pullRequestNumbers = new[] { 15110 },
                issueStatesByNumber = new Dictionary<string, string>
                {
                    ["15110"] = "closed"
                },
                sourceRunUrl = "https://github.com/microsoft/aspire/actions/runs/123"
            });

        RequestRecord request = Assert.Single(result.Requests);
        Assert.Equal("GET /repos/{owner}/{repo}/issues/{issue_number}", request.Route);
        Assert.Equal(15110, request.Payload.GetProperty("issue_number").GetInt32());

        SummaryEvent skippedHeading = Assert.Single(result.Events, e => e.Type == "heading" && e.Text == "Rerun skipped");
        Assert.Equal(1, skippedHeading.Level);

        SummaryEvent analyzedRunLink = Assert.Single(result.Events, e => e.Type == "link" && e.Text == "workflow run");
        Assert.Equal("https://github.com/microsoft/aspire/actions/runs/123", analyzedRunLink.Href);

        SummaryEvent skippedRaw = Assert.Single(result.Events, e => e.Type == "raw" && e.Text is not null && e.Text.Contains("All associated pull requests are closed."));
        Assert.Contains("All associated pull requests are closed. No jobs were rerun.", skippedRaw.Text);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RerunMatchedJobsDoesNotFabricateCommentLinksWhenGitHubDoesNotReturnACommentUrl()
    {
        RerunMatchedJobsResult result = await InvokeHarnessAsync<RerunMatchedJobsResult>(
            "rerunMatchedJobs",
            new
            {
                owner = "dotnet",
                repo = "aspire",
                retryableJobs = new[]
                {
                    new RetryableJobInput
                    {
                        Id = 11,
                        Name = "Tests / One",
                        HtmlUrl = "https://github.com/microsoft/aspire/actions/runs/123/job/11",
                        Reason = "Reason one"
                    }
                },
                pullRequestNumbers = new[] { 15110 },
                issueStatesByNumber = new Dictionary<string, string>
                {
                    ["15110"] = "open"
                },
                latestRunAttempt = 2,
                sourceRunId = 123,
                sourceRunAttempt = 1,
                sourceRunUrl = "https://github.com/microsoft/aspire/actions/runs/123"
            });

        Assert.Contains(result.Events, e => e.Type == "raw" && e.Text == "Pull request comments:");
        Assert.DoesNotContain(result.Events, e => e.Type == "link" && e.Text == "PR #15110 comment");
        Assert.Contains(result.Events, e => e.Type == "raw" && e.Text == "PR #15110 comment");
    }

    private async Task<AnalyzeFailedJobsResult> AnalyzeSingleJobAsync(WorkflowJob job, string annotationsOrText, string jobLogText = "")
    {
        Dictionary<string, string>? jobLogTextByJobId = string.IsNullOrEmpty(jobLogText)
            ? null
            : new Dictionary<string, string> { [job.Id.ToString()] = jobLogText };

        return await AnalyzeJobsAsync(
            [job],
            new Dictionary<string, string> { [job.Id.ToString()] = annotationsOrText },
            jobLogTextByJobId);
    }

    private Task<AnalyzeFailedJobsResult> AnalyzeJobsAsync(
        WorkflowJob[] jobs,
        Dictionary<string, string> annotationTextByJobId,
        Dictionary<string, string>? jobLogTextByJobId = null,
        int? maxRetryableJobs = null)
        => InvokeHarnessAsync<AnalyzeFailedJobsResult>(
            "analyzeFailedJobs",
            new AnalyzeFailedJobsRequest
            {
                Jobs = jobs,
                AnnotationTextByJobId = annotationTextByJobId,
                JobLogTextByJobId = jobLogTextByJobId,
                MaxRetryableJobs = maxRetryableJobs
            });

    private async Task<T> InvokeHarnessAsync<T>(string operation, object payload)
    {
        string inputPath = Path.Combine(_tempDir.Path, $"{Guid.NewGuid():N}.json");
        string requestJson = JsonSerializer.Serialize(new HarnessRequest
        {
            Operation = operation,
            Payload = payload
        }, s_jsonOptions);

        await File.WriteAllTextAsync(inputPath, requestJson);

        using NodeCommand command = new(_output, label: operation);
        command.WithWorkingDirectory(_repoRoot).WithTimeout(TimeSpan.FromMinutes(1));

        CommandResult result = await command.ExecuteScriptAsync(_harnessPath, inputPath);
        result.EnsureSuccessful();

        HarnessResponse<T>? response = JsonSerializer.Deserialize<HarnessResponse<T>>(result.Output, s_jsonOptions);
        Assert.NotNull(response);

        return response.Result!;
    }

    private static WorkflowJob CreateJob(int id = 1, string name = "Tests / Sample / Sample (ubuntu-latest)", string conclusion = "failure", string[]? failedSteps = null)
        => new()
        {
            Id = id,
            Name = name,
            Conclusion = conclusion,
            Steps = (failedSteps ?? []).Select(stepName => new WorkflowStep
            {
                Name = stepName,
                Conclusion = "failure"
            }).ToArray()
        };

    private static string FindRepoRoot()
    {
        string? current = AppContext.BaseDirectory;

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current, "Aspire.slnx")))
            {
                return current;
            }

            current = Directory.GetParent(current)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not find repository root containing Aspire.slnx");
    }

    private Task<string> ReadRepoFileAsync(string relativePath)
        => File.ReadAllTextAsync(Path.Combine(_repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private sealed class HarnessRequest
    {
        public string Operation { get; init; } = string.Empty;
        public object? Payload { get; init; }
    }

    private sealed class HarnessResponse<T>
    {
        public T? Result { get; init; }
    }

    private sealed class AnalyzeFailedJobsRequest
    {
        public WorkflowJob[] Jobs { get; init; } = [];
        public Dictionary<string, string> AnnotationTextByJobId { get; init; } = [];
        public Dictionary<string, string>? JobLogTextByJobId { get; init; }
        public int? MaxRetryableJobs { get; init; }
    }

    private sealed class AnalyzeFailedJobsResult
    {
        public AnalyzedJob[] FailedJobs { get; init; } = [];
        public AnalyzedJob[] RetryableJobs { get; init; } = [];
        public AnalyzedJob[] SkippedJobs { get; init; } = [];
        public int[] LogRequestJobIds { get; init; } = [];
    }

    private sealed class AssociatedPullRequestNumbersResult
    {
        public int[] PullRequestNumbers { get; init; } = [];
        public RequestRecord[] Requests { get; init; } = [];
    }

    private sealed class AnalyzedJob
    {
        public int Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string? HtmlUrl { get; init; }
        public string[] FailedSteps { get; init; } = [];
        public string Reason { get; init; } = string.Empty;
    }

    private sealed class WorkflowJob
    {
        public int Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Conclusion { get; init; } = string.Empty;
        public WorkflowStep[] Steps { get; init; } = [];

        [JsonPropertyName("check_run_url")]
        public string? CheckRunUrl { get; init; }
    }

    private sealed class WorkflowStep
    {
        public string Name { get; init; } = string.Empty;
        public string Conclusion { get; init; } = string.Empty;
    }

    private sealed class SummaryResult
    {
        public SummaryEvent[] Events { get; init; } = [];
    }

    private sealed class SummaryEvent
    {
        public string Type { get; init; } = string.Empty;
        public string? Text { get; init; }
        public string? Href { get; init; }
        public int? Level { get; init; }
        public bool? AddEol { get; init; }
        public JsonElement[][] Rows { get; init; } = [];
    }

    private sealed class SummaryJob
    {
        public int Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Reason { get; init; } = string.Empty;
    }

    private sealed class RetryableJobInput
    {
        public int Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string? HtmlUrl { get; init; }
        public string Reason { get; init; } = string.Empty;
    }

    private sealed class RerunMatchedJobsResult
    {
        public RequestRecord[] Requests { get; init; } = [];
        public SummaryEvent[] Events { get; init; } = [];
    }

    private sealed class RequestRecord
    {
        public string Route { get; init; } = string.Empty;
        public JsonElement Payload { get; init; }
    }
}
