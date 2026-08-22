// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public sealed class AnalyzeCiFailureCauseResolverTests : IDisposable
{
    private const string RemoteHostTestName = "Aspire.Hosting.RemoteHost.Tests.JsonRpcAuthenticationTests.FailedAuthentication_ClosesConnection_AndPreventsFurtherCalls";

    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly TemporaryWorkspace _workspace;
    private readonly string _repoRoot;
    private readonly string _harnessPath;
    private readonly string _resolverPath;
    private readonly ITestOutputHelper _output;

    public AnalyzeCiFailureCauseResolverTests(ITestOutputHelper output)
    {
        _output = output;
        _workspace = TemporaryWorkspace.Create(output);
        _repoRoot = RepoRoot.Path;
        _harnessPath = Path.Combine(
            _repoRoot,
            "tests",
            "Infrastructure.Tests",
            "WorkflowScripts",
            "analyze-ci-failure-cause-resolver.harness.js");
        _resolverPath = Path.Combine(
            _repoRoot,
            ".github",
            "workflows",
            "analyze-ci-failure-cause-resolver.js");
    }

    public void Dispose() => _workspace.Dispose();

    [Fact]
    [RequiresTools(["node"])]
    public async Task ReusesCanonicalCausesAndAttributesEachFailureToItsJob()
    {
        JsonElement result = await ResolveAsync(new
        {
            analysis = new
            {
                causes = new[] { "windows-process-init-0xc0000142", "remotehost-jsonrpcauth-rpc-timeout" },
                failed_jobs = new object[]
                {
                    new
                    {
                        id = 101,
                        name = "Tests / Microsoft.Azure.StackExchangeRedis / Microsoft.Azure.StackExchangeRedis (windows-latest)",
                        classification = "transient-infra",
                        reason = "Process completed with exit code -1073741502 (0xC0000142)."
                    },
                    new
                    {
                        id = 202,
                        name = "Tests / Hosting.RemoteHost / Hosting.RemoteHost (windows-latest)",
                        classification = "flaky-test",
                        reason = "The RemoteHost authentication test timed out."
                    }
                },
                failed_tests = new[]
                {
                    new
                    {
                        name = RemoteHostTestName,
                        job = "Tests / Hosting.RemoteHost / Hosting.RemoteHost (windows-latest)",
                        error = "System.TimeoutException: Timed out connecting to test RPC server.",
                        stack_trace = "at JsonRpcAuthenticationTests.RemoteHostTestServer.ConnectToServerAsync()"
                    }
                }
            },
            causes = new object[]
            {
                new
                {
                    id = "windows-process-init-0xc0000142",
                    type = "infra-failure",
                    title = "Windows test host process crashes with exit code 0xC0000142",
                    error_pattern = "Process completed with exit code -1073741502 (0xC0000142).",
                    job_ids = new[] { 101 }
                },
                new
                {
                    id = "remotehost-jsonrpcauth-rpc-timeout",
                    type = "flaky-test",
                    title = "RemoteHost RPC timeout",
                    test_name = RemoteHostTestName,
                    error_pattern = "System.TimeoutException: Timed out connecting to test RPC server.",
                    job_ids = new[] { 202 }
                }
            },
            priorCauses = new object[]
            {
                new
                {
                    id = "windows-process-init-failure-0xc0000142",
                    type = "infra-failure",
                    title = "Windows process initialization failure",
                    error_pattern = "0xC0000142",
                    occurrences = new[] { new { observed_at = "2026-07-10T01:22:31Z" } }
                },
                new
                {
                    id = "windows-process-init-0xc0000142",
                    type = "infra-failure",
                    title = "Windows test host process crashes",
                    error_pattern = "0xC0000142",
                    occurrences = new[] { new { observed_at = "2026-08-07T18:03:31Z" } }
                },
                new
                {
                    id = "remotehost-jsonrpc-auth-timeout",
                    type = "flaky-test",
                    title = "RemoteHost authentication timeout",
                    test_name = RemoteHostTestName,
                    error_pattern = "Timed out connecting to test RPC server",
                    occurrences = new[] { new { observed_at = "2026-07-21T00:33:28Z" } }
                },
                new
                {
                    id = "remotehost-jsonrpcauth-rpc-timeout",
                    type = "flaky-test",
                    title = "RemoteHost RPC timeout",
                    test_name = RemoteHostTestName,
                    error_pattern = "Timed out connecting to test RPC server",
                    occurrences = new[] { new { observed_at = "2026-08-07T18:03:31Z" } }
                }
            },
            retryPatterns = new
            {
                jobFailurePatterns = new[]
                {
                    new
                    {
                        output = "0xC0000142",
                        reason = "Windows process initialization failure",
                        causeId = "windows-process-init-failure-0xc0000142"
                    }
                }
            }
        });

        string[] causeIds = result.GetProperty("analysis").GetProperty("causes")
            .EnumerateArray()
            .Select(static value => value.GetString()!)
            .Order()
            .ToArray();
        Assert.Equal(
            ["remotehost-jsonrpc-auth-timeout", "windows-process-init-failure-0xc0000142"],
            causeIds);

        JsonElement windowsCause = FindCause(result, "windows-process-init-failure-0xc0000142");
        Assert.Equal(
            ["Tests / Microsoft.Azure.StackExchangeRedis / Microsoft.Azure.StackExchangeRedis (windows-latest)"],
            ReadStrings(windowsCause, "job_names"));

        JsonElement remoteHostCause = FindCause(result, "remotehost-jsonrpc-auth-timeout");
        Assert.Equal(
            ["Tests / Hosting.RemoteHost / Hosting.RemoteHost (windows-latest)"],
            ReadStrings(remoteHostCause, "job_names"));

        JsonElement[] failedJobs = result.GetProperty("analysis").GetProperty("failed_jobs").EnumerateArray().ToArray();
        Assert.Equal(
            ["windows-process-init-failure-0xc0000142"],
            ReadStrings(failedJobs.Single(job => job.GetProperty("id").GetInt32() == 101), "cause_ids"));
        Assert.Equal(
            ["remotehost-jsonrpc-auth-timeout"],
            ReadStrings(failedJobs.Single(job => job.GetProperty("id").GetInt32() == 202), "cause_ids"));
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task CanonicalizesParameterizedTheoryTestNames()
    {
        const string canonicalCauseId = "servicebus-namespace-connection-string";
        const string canonicalTestName = "Aspire.Azure.Messaging.ServiceBus.Tests.AspireServiceBusExtensionsTests.NamespaceWorksInConnectionStrings";
        string currentTestName = $"{canonicalTestName}(connectionString: \"Endpoint=(primary)\")";

        JsonElement result = await ResolveAsync(CreateSingleTestPayload(
            currentTestName,
            "new-servicebus-cause",
            new
            {
                id = canonicalCauseId,
                type = "flaky-test",
                title = "Service Bus connection string test",
                test_name = $"{canonicalTestName}(connectionString: \"Endpoint=(secondary)\")",
                error_pattern = "Collection was modified",
                occurrences = new[] { new { observed_at = "2026-07-17T18:57:00Z" } }
            }));

        Assert.Equal(canonicalCauseId, FindOnlyCause(result).GetProperty("id").GetString());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task SanitizesProposedCauseIds()
    {
        JsonElement result = await ResolveAsync(new
        {
            analysis = new
            {
                causes = new[] { "NuGet_Feed Timeout" },
                failed_jobs = new[]
                {
                    new
                    {
                        id = 1,
                        name = "Tests / Sample / Sample (ubuntu-latest)",
                        classification = "transient-infra",
                        reason = "The NuGet feed timed out."
                    }
                },
                failed_tests = Array.Empty<object>()
            },
            causes = new[]
            {
                new
                {
                    id = "NuGet_Feed Timeout",
                    type = "infra-failure",
                    title = "NuGet feed timeout",
                    error_pattern = "The NuGet feed timed out.",
                    job_ids = new[] { 1 }
                }
            },
            priorCauses = Array.Empty<object>(),
            retryPatterns = new { jobFailurePatterns = Array.Empty<object>() }
        });

        Assert.Equal(["nuget-feed-timeout"], ReadStrings(result.GetProperty("analysis"), "causes"));
        Assert.Equal("nuget-feed-timeout", FindOnlyCause(result).GetProperty("id").GetString());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RejectsCollidingSanitizedCauseIds()
    {
        object payload = new
        {
            analysis = new
            {
                causes = new[] { "NuGet_Feed Timeout", "nuget-feed-timeout" },
                failed_jobs = new[]
                {
                    new
                    {
                        id = 1,
                        name = "Tests / Sample / Sample (ubuntu-latest)",
                        classification = "transient-infra",
                        reason = "The NuGet feed timed out."
                    }
                },
                failed_tests = Array.Empty<object>()
            },
            causes = new[]
            {
                new
                {
                    id = "NuGet_Feed Timeout",
                    type = "infra-failure",
                    title = "First NuGet feed timeout",
                    error_pattern = "The NuGet feed timed out.",
                    job_ids = new[] { 1 }
                },
                new
                {
                    id = "nuget-feed-timeout",
                    type = "infra-failure",
                    title = "Second NuGet feed timeout",
                    error_pattern = "The NuGet feed timed out.",
                    job_ids = new[] { 1 }
                }
            },
            priorCauses = Array.Empty<object>(),
            retryPatterns = new { jobFailurePatterns = Array.Empty<object>() }
        };

        CommandResult result = await ExecuteHarnessAsync(payload);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("normalize to the same cause ID", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task IncludesAllJobBackedCausesInRunSummary()
    {
        JsonElement result = await ResolveAsync(new
        {
            analysis = new
            {
                causes = new[] { "listed-cause" },
                failed_jobs = new[]
                {
                    new
                    {
                        id = 1,
                        name = "Tests / Listed / Listed (ubuntu-latest)",
                        classification = "transient-infra",
                        reason = "Listed failure"
                    },
                    new
                    {
                        id = 2,
                        name = "Tests / Unlisted / Unlisted (ubuntu-latest)",
                        classification = "transient-infra",
                        reason = "Unlisted failure"
                    }
                },
                failed_tests = Array.Empty<object>()
            },
            causes = new[]
            {
                new
                {
                    id = "listed-cause",
                    type = "infra-failure",
                    title = "Listed cause",
                    error_pattern = "Listed failure",
                    job_ids = new[] { 1 }
                },
                new
                {
                    id = "unlisted-cause",
                    type = "infra-failure",
                    title = "Unlisted cause",
                    error_pattern = "Unlisted failure",
                    job_ids = new[] { 2 }
                }
            },
            priorCauses = Array.Empty<object>(),
            retryPatterns = new { jobFailurePatterns = Array.Empty<object>() }
        });

        Assert.Equal(
            ["listed-cause", "unlisted-cause"],
            ReadStrings(result.GetProperty("analysis"), "causes").Order().ToArray());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ValidatesTestAttributionWithinTheCauseDeclaredJobs()
    {
        const string testName = "Aspire.Sample.Tests.SampleTests.FlakyTest";
        const string trackedJobName = "Tests / Sample / Sample (ubuntu-latest)";
        const string codeIssueJobName = "Tests / Sample / Sample (windows-latest)";

        JsonElement result = await ResolveAsync(new
        {
            analysis = new
            {
                causes = new[] { "sample-flaky-test" },
                failed_jobs = new object[]
                {
                    new
                    {
                        id = 1,
                        name = trackedJobName,
                        classification = "flaky-test",
                        reason = "The sample test failed intermittently."
                    },
                    new
                    {
                        id = 2,
                        name = codeIssueJobName,
                        classification = "code-issue",
                        reason = "The sample test failed because of the PR."
                    }
                },
                failed_tests = new[]
                {
                    new
                    {
                        name = $"{testName}(value: 1)",
                        job = trackedJobName,
                        error = "Transient sample failure",
                        stack_trace = string.Empty
                    },
                    new
                    {
                        name = $"{testName}(value: 2)",
                        job = codeIssueJobName,
                        error = "Deterministic sample failure",
                        stack_trace = string.Empty
                    }
                }
            },
            causes = new[]
            {
                new
                {
                    id = "sample-flaky-test",
                    type = "flaky-test",
                    title = "Sample flaky test",
                    test_name = $"{testName}(value: 1)",
                    error_pattern = "Transient sample failure",
                    job_ids = new[] { 1 }
                }
            },
            priorCauses = Array.Empty<object>(),
            retryPatterns = new { jobFailurePatterns = Array.Empty<object>() }
        });

        JsonElement cause = FindOnlyCause(result);
        Assert.Equal([1], ReadInt32s(cause, "job_ids"));
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task UsesExplicitMatcherForCrossTestRootCause()
    {
        const string canonicalCauseId = "hosting-parentprocess-dcp-timestamp-badrequest";
        const string currentTestName = "Aspire.Hosting.Tests.DistributedApplicationTests.ParentProcessLifetimeReusesResourcesAcrossAppRestartsAndStopsWhenParentExits";

        JsonElement result = await ResolveAsync(CreateSingleTestPayload(
            currentTestName,
            "hosting-parentprocess-reuse-dcp-timestamp-badrequest",
            new
            {
                id = canonicalCauseId,
                type = "flaky-test",
                title = "DCP timestamp parsing failure",
                test_name = "Aspire.Hosting.Tests.DistributedApplicationTests.ParentProcessLifetimeScopesExecutableAndContainerToParentProcess",
                test_names = new[]
                {
                    "Aspire.Hosting.Tests.DistributedApplicationTests.ParentProcessLifetimeScopesExecutableAndContainerToParentProcess"
                },
                error_pattern = "cannot parse Z as .000000",
                matchers = new[]
                {
                    new
                    {
                        kind = "error-regex",
                        pattern = "parsing time[\\s\\S]+cannot parse[\\s\\S]+\\.000000",
                        flags = "i"
                    }
                },
                occurrences = new[] { new { observed_at = "2026-07-13T02:20:00Z" } }
            },
            error: "Container cannot be handled: parsing time \"0001-01-01T00:06:30Z\" cannot parse \"Z\" as \".000000\"."));

        JsonElement cause = FindOnlyCause(result);
        Assert.Equal(canonicalCauseId, cause.GetProperty("id").GetString());
        Assert.Equal(2, ReadStrings(cause, "test_names").Length);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task DoesNotMergeSimilarFailuresWithoutDeterministicMatcher()
    {
        JsonElement result = await ResolveAsync(new
        {
            analysis = new
            {
                causes = new[] { "vscode-e2e-java-apphost-flaky" },
                failed_jobs = new[]
                {
                    new
                    {
                        id = 1,
                        name = "Tests / VS Code extension E2E (Linux, java-apphost)",
                        classification = "flaky-test",
                        reason = "Process completed with exit code 1."
                    }
                },
                failed_tests = Array.Empty<object>()
            },
            causes = new[]
            {
                new
                {
                    id = "vscode-e2e-java-apphost-flaky",
                    type = "flaky-test",
                    title = "VS Code extension Java AppHost shard fails",
                    error_pattern = "Process completed with exit code 1.",
                    job_ids = new[] { 1 }
                }
            },
            priorCauses = new[]
            {
                new
                {
                    id = "vscode-e2e-linux-settings-files-flaky",
                    type = "flaky-test",
                    title = "VS Code extension settings-files shard fails",
                    error_pattern = "Process completed with exit code 1.",
                    occurrences = new[] { new { observed_at = "2026-08-14T00:00:00Z" } }
                }
            },
            retryPatterns = new { jobFailurePatterns = Array.Empty<object>() }
        });

        Assert.Equal("vscode-e2e-java-apphost-flaky", FindOnlyCause(result).GetProperty("id").GetString());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ResolvesCanonicalIdAliases()
    {
        const string canonicalCauseId = "hosting-parentprocess-dcp-timestamp-badrequest";
        const string aliasCauseId = "hosting-parentprocess-dcp-timestamp-alias";

        JsonElement result = await ResolveAsync(new
        {
            analysis = new
            {
                causes = new[] { aliasCauseId },
                failed_jobs = new[]
                {
                    new
                    {
                        id = 1,
                        name = "Tests / Aspire.Hosting / Aspire.Hosting (ubuntu-latest)",
                        classification = "transient-infra",
                        reason = "DCP rejected a fractional timestamp."
                    }
                },
                failed_tests = Array.Empty<object>()
            },
            causes = new[]
            {
                new
                {
                    id = aliasCauseId,
                    type = "infra-failure",
                    title = "DCP timestamp failure",
                    error_pattern = "DCP rejected a fractional timestamp.",
                    job_ids = new[] { 1 }
                }
            },
            priorCauses = new object[]
            {
                new
                {
                    id = canonicalCauseId,
                    type = "infra-failure",
                    title = "Canonical DCP timestamp failure",
                    error_pattern = "DCP rejected a fractional timestamp.",
                    occurrences = new[] { new { observed_at = "2026-07-01T00:00:00Z" } }
                },
                new
                {
                    id = aliasCauseId,
                    canonical_id = canonicalCauseId,
                    type = "infra-failure",
                    title = "Alias",
                    error_pattern = "DCP rejected a fractional timestamp."
                }
            },
            retryPatterns = new { jobFailurePatterns = Array.Empty<object>() }
        });

        Assert.Equal(canonicalCauseId, FindOnlyCause(result).GetProperty("id").GetString());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ExplicitAliasRemainsAuthoritativeWhenMatchersAreAmbiguous()
    {
        const string canonicalCauseId = "canonical-infra-cause";
        const string aliasCauseId = "canonical-infra-alias";
        JsonElement result = await ResolveAsync(new
        {
            analysis = new
            {
                causes = new[] { aliasCauseId },
                failed_jobs = new[]
                {
                    new
                    {
                        id = 1,
                        name = "Tests / Sample / Sample (ubuntu-latest)",
                        classification = "transient-infra",
                        reason = "Shared deterministic failure token"
                    }
                },
                failed_tests = Array.Empty<object>()
            },
            causes = new[]
            {
                new
                {
                    id = aliasCauseId,
                    type = "infra-failure",
                    title = "Current infrastructure cause",
                    error_pattern = "Shared deterministic failure token",
                    job_ids = new[] { 1 }
                }
            },
            priorCauses = new object[]
            {
                new
                {
                    id = canonicalCauseId,
                    type = "infra-failure",
                    title = "Canonical infrastructure cause",
                    error_pattern = "Shared deterministic failure token"
                },
                new
                {
                    id = aliasCauseId,
                    canonical_id = canonicalCauseId,
                    type = "infra-failure",
                    title = "Canonical alias",
                    error_pattern = "Shared deterministic failure token"
                },
                CreatePriorMatcherCause("first-matcher-cause"),
                CreatePriorMatcherCause("second-matcher-cause")
            },
            retryPatterns = new { jobFailurePatterns = Array.Empty<object>() }
        });

        Assert.Equal(canonicalCauseId, FindOnlyCause(result).GetProperty("id").GetString());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ToleratesLegacyPriorCauseIds()
    {
        JsonElement result = await ResolveAsync(new
        {
            analysis = new
            {
                causes = new[] { "current-infra-cause" },
                failed_jobs = new[]
                {
                    new
                    {
                        id = 1,
                        name = "Tests / Sample / Sample (ubuntu-latest)",
                        classification = "transient-infra",
                        reason = "Current infrastructure failure"
                    }
                },
                failed_tests = Array.Empty<object>()
            },
            causes = new[]
            {
                new
                {
                    id = "current-infra-cause",
                    type = "infra-failure",
                    title = "Current infrastructure cause",
                    error_pattern = "Current infrastructure failure",
                    job_ids = new[] { 1 }
                }
            },
            priorCauses = new[]
            {
                new
                {
                    id = "processguest-signalerThrows-treekill-timeout",
                    type = "flaky-test",
                    title = "Legacy cause",
                    error_pattern = "Legacy failure"
                }
            },
            retryPatterns = new { jobFailurePatterns = Array.Empty<object>() }
        });

        Assert.Equal("current-infra-cause", FindOnlyCause(result).GetProperty("id").GetString());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task MatchesSanitizedProposalToLegacyPriorCauseId()
    {
        const string legacyCauseId = "processguest-signalerThrows-treekill-timeout";
        const string canonicalCauseId = "processguest-signalerthrows-treekill-timeout";
        JsonElement result = await ResolveAsync(new
        {
            analysis = new
            {
                causes = new[] { legacyCauseId },
                failed_jobs = new[]
                {
                    new
                    {
                        id = 1,
                        name = "Tests / Sample / Sample (ubuntu-latest)",
                        classification = "transient-infra",
                        reason = "Legacy infrastructure failure"
                    }
                },
                failed_tests = Array.Empty<object>()
            },
            causes = new[]
            {
                new
                {
                    id = legacyCauseId,
                    type = "infra-failure",
                    title = "Legacy infrastructure cause",
                    error_pattern = "Legacy infrastructure failure",
                    job_ids = new[] { 1 }
                }
            },
            priorCauses = new[]
            {
                new
                {
                    id = legacyCauseId,
                    type = "infra-failure",
                    title = "Stored legacy cause",
                    error_pattern = "Legacy infrastructure failure"
                }
            },
            retryPatterns = new { jobFailurePatterns = Array.Empty<object>() }
        });

        Assert.Equal(canonicalCauseId, FindOnlyCause(result).GetProperty("id").GetString());
        JsonElement migration = Assert.Single(result.GetProperty("priorCauseMigrations").EnumerateArray());
        Assert.Equal(legacyCauseId, migration.GetProperty("legacy_id").GetString());
        Assert.Equal(canonicalCauseId, migration.GetProperty("canonical_id").GetString());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task DerivesMigratedIdFromLegacyCauseInsteadOfCurrentProposal()
    {
        const string testName = "Aspire.Sample.Tests.SampleTests.FlakyTest";
        const string legacyCauseId = "Legacy.Cause";
        const string canonicalCauseId = "legacy-cause";

        JsonElement result = await ResolveAsync(CreateSingleTestPayload(
            testName,
            "unrelated-current-proposal",
            new
            {
                id = legacyCauseId,
                type = "flaky-test",
                title = "Stored legacy cause",
                test_name = testName,
                error_pattern = "The sample test failed."
            },
            error: "The sample test failed."));

        Assert.Equal(canonicalCauseId, FindOnlyCause(result).GetProperty("id").GetString());
        JsonElement migration = Assert.Single(result.GetProperty("priorCauseMigrations").EnumerateArray());
        Assert.Equal(legacyCauseId, migration.GetProperty("legacy_id").GetString());
        Assert.Equal(canonicalCauseId, migration.GetProperty("canonical_id").GetString());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task DoesNotMatchAmbiguousNormalizedPriorCauseIds()
    {
        const string currentCauseId = "legacy-cause";
        JsonElement result = await ResolveAsync(new
        {
            analysis = new
            {
                causes = new[] { currentCauseId },
                failed_jobs = new[]
                {
                    new
                    {
                        id = 1,
                        name = "Tests / Sample / Sample (ubuntu-latest)",
                        classification = "transient-infra",
                        reason = "Current infrastructure failure"
                    }
                },
                failed_tests = Array.Empty<object>()
            },
            causes = new[]
            {
                new
                {
                    id = currentCauseId,
                    type = "infra-failure",
                    title = "Current infrastructure cause",
                    error_pattern = "Current infrastructure failure",
                    job_ids = new[] { 1 }
                }
            },
            priorCauses = new[]
            {
                CreateLegacyCause("Legacy.Cause"),
                CreateLegacyCause("legacy_Cause"),
                CreateLegacyCause("Legacy Cause")
            },
            retryPatterns = new { jobFailurePatterns = Array.Empty<object>() }
        });

        Assert.Equal(currentCauseId, FindOnlyCause(result).GetProperty("id").GetString());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task DoesNotTrustAgentAuthoredCanonicalId()
    {
        const string proposedCauseId = "proposed-infra-cause";
        JsonElement result = await ResolveAsync(new
        {
            analysis = new
            {
                causes = new[] { proposedCauseId },
                failed_jobs = new[]
                {
                    new
                    {
                        id = 1,
                        name = "Tests / Sample / Sample (ubuntu-latest)",
                        classification = "transient-infra",
                        reason = "Current infrastructure failure"
                    }
                },
                failed_tests = Array.Empty<object>()
            },
            causes = new[]
            {
                new
                {
                    id = proposedCauseId,
                    canonical_id = "agent-selected-canonical-cause",
                    type = "infra-failure",
                    title = "Current infrastructure cause",
                    error_pattern = "Current infrastructure failure",
                    job_ids = new[] { 1 }
                }
            },
            priorCauses = Array.Empty<object>(),
            retryPatterns = new { jobFailurePatterns = Array.Empty<object>() }
        });

        JsonElement cause = FindOnlyCause(result);
        Assert.Equal(proposedCauseId, cause.GetProperty("id").GetString());
        Assert.False(cause.TryGetProperty("canonical_id", out _));
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task IgnoresAgentAuthoredTestNames()
    {
        const string proposedCauseId = "proposed-flaky-cause";
        const string failedTestName = "Current.Tests.ActualFailure";
        const string unrelatedTestName = "Current.Tests.UnrelatedFailure";
        const string jobName = "Tests / Sample / Sample (ubuntu-latest)";
        JsonElement result = await ResolveAsync(new
        {
            analysis = new
            {
                causes = new[] { proposedCauseId },
                failed_jobs = new[]
                {
                    new
                    {
                        id = 1,
                        name = jobName,
                        classification = "flaky-test",
                        reason = "Two tests failed independently."
                    }
                },
                failed_tests = new[]
                {
                    new
                    {
                        name = failedTestName,
                        job = jobName,
                        error = "Actual deterministic failure token",
                        stack_trace = string.Empty
                    },
                    new
                    {
                        name = unrelatedTestName,
                        job = jobName,
                        error = "Unrelated deterministic failure token",
                        stack_trace = string.Empty
                    }
                }
            },
            causes = new[]
            {
                new
                {
                    id = proposedCauseId,
                    type = "flaky-test",
                    title = "Actual flaky test",
                    test_name = failedTestName,
                    test_names = new[] { unrelatedTestName },
                    error_pattern = "Actual deterministic failure token",
                    job_ids = new[] { 1 }
                }
            },
            priorCauses = new[]
            {
                new
                {
                    id = "unrelated-canonical-cause",
                    type = "infra-failure",
                    title = "Unrelated canonical cause",
                    error_pattern = "Unrelated deterministic failure token",
                    matchers = new[]
                    {
                        new
                        {
                            kind = "error-literal",
                            value = "Unrelated deterministic failure token"
                        }
                    }
                }
            },
            retryPatterns = new { jobFailurePatterns = Array.Empty<object>() }
        });

        JsonElement cause = FindOnlyCause(result);
        Assert.Equal(proposedCauseId, cause.GetProperty("id").GetString());
        Assert.Equal([failedTestName], ReadStrings(cause, "test_names"));
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task PreservesWorkflowOwnedIssueUrl()
    {
        const string canonicalCauseId = "canonical-infra-cause";
        JsonElement result = await ResolveAsync(new
        {
            analysis = new
            {
                causes = new[] { canonicalCauseId },
                failed_jobs = new[]
                {
                    new
                    {
                        id = 1,
                        name = "Tests / Sample / Sample (ubuntu-latest)",
                        classification = "transient-infra",
                        reason = "Current infrastructure failure"
                    }
                },
                failed_tests = Array.Empty<object>()
            },
            causes = new[]
            {
                new
                {
                    id = canonicalCauseId,
                    type = "infra-failure",
                    title = "Current infrastructure cause",
                    error_pattern = "Current infrastructure failure",
                    issue_url = "https://github.com/microsoft/aspire/issues/4242",
                    job_ids = new[] { 1 }
                }
            },
            priorCauses = new[]
            {
                new
                {
                    id = canonicalCauseId,
                    type = "infra-failure",
                    title = "Canonical infrastructure cause",
                    error_pattern = "Stored infrastructure failure",
                    issue_url = "https://github.com/microsoft/aspire/issues/1111"
                }
            },
            retryPatterns = new { jobFailurePatterns = Array.Empty<object>() }
        });

        Assert.Equal(
            "https://github.com/microsoft/aspire/issues/1111",
            FindOnlyCause(result).GetProperty("issue_url").GetString());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ExistingCanonicalIdPreservesStoredMetadata()
    {
        const string canonicalCauseId = "existing-canonical-infra-cause";
        JsonElement result = await ResolveAsync(new
        {
            analysis = new
            {
                causes = new[] { canonicalCauseId },
                failed_jobs = new[]
                {
                    new
                    {
                        id = 1,
                        name = "Tests / Sample / Sample (ubuntu-latest)",
                        classification = "transient-infra",
                        reason = "Current failure details"
                    }
                },
                failed_tests = Array.Empty<object>()
            },
            causes = new[]
            {
                new
                {
                    id = canonicalCauseId,
                    type = "infra-failure",
                    title = "Agent-generated replacement title",
                    error_pattern = "Agent-generated replacement pattern",
                    job_ids = new[] { 1 }
                }
            },
            priorCauses = new[]
            {
                new
                {
                    id = canonicalCauseId,
                    type = "infra-failure",
                    title = "Stored canonical title",
                    error_pattern = "Stored canonical pattern",
                    occurrences = new[] { new { observed_at = "2026-07-01T00:00:00Z" } }
                }
            },
            retryPatterns = new { jobFailurePatterns = Array.Empty<object>() }
        });

        JsonElement cause = FindOnlyCause(result);
        Assert.Equal("Stored canonical title", cause.GetProperty("title").GetString());
        Assert.Equal("Stored canonical pattern", cause.GetProperty("error_pattern").GetString());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task MatchersOnlyInspectEvidenceForTheirCauseWithinAJob()
    {
        const string jobName = "Tests / Sample / Sample (ubuntu-latest)";
        JsonElement result = await ResolveAsync(new
        {
            analysis = new
            {
                causes = new[] { "current-alpha", "current-beta" },
                failed_jobs = new[]
                {
                    new
                    {
                        id = 1,
                        name = jobName,
                        classification = "flaky-test",
                        reason = "Two independent tests failed."
                    }
                },
                failed_tests = new[]
                {
                    new
                    {
                        name = "Current.Tests.Alpha",
                        job = jobName,
                        error = "Deterministic alpha failure token",
                        stack_trace = string.Empty
                    },
                    new
                    {
                        name = "Current.Tests.Beta",
                        job = jobName,
                        error = "Deterministic beta failure token",
                        stack_trace = string.Empty
                    }
                }
            },
            causes = new object[]
            {
                new
                {
                    id = "current-alpha",
                    type = "flaky-test",
                    title = "Alpha failure",
                    test_name = "Current.Tests.Alpha",
                    error_pattern = "Deterministic alpha failure token",
                    job_ids = new[] { 1 }
                },
                new
                {
                    id = "current-beta",
                    type = "flaky-test",
                    title = "Beta failure",
                    test_name = "Current.Tests.Beta",
                    error_pattern = "Deterministic beta failure token",
                    job_ids = new[] { 1 }
                }
            },
            priorCauses = new object[]
            {
                new
                {
                    id = "canonical-alpha",
                    type = "flaky-test",
                    title = "Canonical alpha failure",
                    test_name = "Prior.Tests.Alpha",
                    error_pattern = "Deterministic alpha failure token",
                    matchers = new[]
                    {
                        new { kind = "error-literal", value = "Deterministic alpha failure token" }
                    }
                },
                new
                {
                    id = "canonical-beta",
                    type = "flaky-test",
                    title = "Canonical beta failure",
                    test_name = "Prior.Tests.Beta",
                    error_pattern = "Deterministic beta failure token",
                    matchers = new[]
                    {
                        new { kind = "error-literal", value = "Deterministic beta failure token" }
                    }
                }
            },
            retryPatterns = new { jobFailurePatterns = Array.Empty<object>() }
        });

        Assert.Equal(
            ["canonical-alpha", "canonical-beta"],
            result.GetProperty("causes")
                .EnumerateArray()
                .Select(cause => cause.GetProperty("id").GetString()!)
                .Order()
                .ToArray());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RejectsCauseWithoutJobReference()
    {
        const string testName = "Aspire.Hosting.Tests.SampleTests.FlakyTest";
        object payload = new
        {
            analysis = new
            {
                causes = new[] { "sample-flaky-test" },
                failed_jobs = new[]
                {
                    new
                    {
                        id = 1,
                        name = "Tests / Sample / Sample (ubuntu-latest)",
                        classification = "flaky-test",
                        reason = "The sample test failed."
                    }
                },
                failed_tests = new[]
                {
                    new
                    {
                        name = testName,
                        job = "Tests / Sample / Sample (ubuntu-latest)",
                        error = "The sample test failed.",
                        stack_trace = string.Empty
                    }
                }
            },
            causes = new[]
            {
                new
                {
                    id = "sample-flaky-test",
                    type = "flaky-test",
                    title = "Sample flaky test",
                    test_name = testName,
                    error_pattern = "The sample test failed."
                }
            },
            priorCauses = Array.Empty<object>(),
            retryPatterns = new { jobFailurePatterns = Array.Empty<object>() }
        };

        CommandResult result = await ExecuteHarnessAsync(payload);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("must reference at least one failed job", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RejectsUnsupportedCauseTypes()
    {
        object payload = new
        {
            analysis = new
            {
                causes = new[] { "sample-code-issue" },
                failed_jobs = new[]
                {
                    new
                    {
                        id = 1,
                        name = "Tests / Sample / Sample (ubuntu-latest)",
                        classification = "flaky-test",
                        reason = "The sample test failed."
                    }
                },
                failed_tests = Array.Empty<object>()
            },
            causes = new[]
            {
                new
                {
                    id = "sample-code-issue",
                    type = "code-issue",
                    title = "Sample code issue",
                    error_pattern = "The sample test failed.",
                    job_ids = new[] { 1 }
                }
            },
            priorCauses = Array.Empty<object>(),
            retryPatterns = new { jobFailurePatterns = Array.Empty<object>() }
        };

        CommandResult result = await ExecuteHarnessAsync(payload);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("unsupported type 'code-issue'", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RejectsUnsupportedCanonicalPriorCauseTypes()
    {
        object payload = CreateSingleTestPayload(
            "Aspire.Sample.Tests.SampleTests.FlakyTest",
            "current-flaky-test",
            new
            {
                id = "stored-code-issue",
                type = "code-issue",
                title = "Stored code issue",
                test_name = "Aspire.Sample.Tests.SampleTests.FlakyTest",
                error_pattern = "The sample test failed."
            },
            error: "The sample test failed.");

        CommandResult result = await ExecuteHarnessAsync(payload);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("unsupported type 'code-issue'", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RejectsAmbiguousExplicitMatchers()
    {
        object payload = new
        {
            analysis = new
            {
                causes = new[] { "new-infra-cause" },
                failed_jobs = new[]
                {
                    new
                    {
                        id = 1,
                        name = "Tests / Sample / Sample (ubuntu-latest)",
                        classification = "transient-infra",
                        reason = "Shared deterministic failure token"
                    }
                },
                failed_tests = Array.Empty<object>()
            },
            causes = new[]
            {
                new
                {
                    id = "new-infra-cause",
                    type = "infra-failure",
                    title = "Shared deterministic failure token",
                    error_pattern = "Shared deterministic failure token",
                    job_ids = new[] { 1 }
                }
            },
            priorCauses = new object[]
            {
                CreatePriorMatcherCause("first-cause"),
                CreatePriorMatcherCause("second-cause")
            },
            retryPatterns = new { jobFailurePatterns = Array.Empty<object>() }
        };

        CommandResult result = await ExecuteHarnessAsync(payload);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("matched multiple canonical prior causes", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RejectsConflictingCanonicalizationMechanisms()
    {
        const string testName = "Aspire.Sample.Tests.SampleTests.FlakyTest";
        object payload = CreateSingleTestPayload(
            testName,
            "new-sample-cause",
            new
            {
                id = "canonical-test-cause",
                type = "flaky-test",
                title = "Canonical test cause",
                test_name = testName,
                error_pattern = "Prior sample failure",
                occurrences = new[] { new { observed_at = "2026-07-01T00:00:00Z" } }
            },
            error: "Distinctive shared infrastructure token");

        using JsonDocument payloadDocument = JsonDocument.Parse(JsonSerializer.Serialize(payload, s_jsonOptions));
        JsonElement root = payloadDocument.RootElement;
        object expandedPayload = new
        {
            analysis = root.GetProperty("analysis"),
            causes = root.GetProperty("causes"),
            priorCauses = new object[]
            {
                root.GetProperty("priorCauses")[0],
                new
                {
                    id = "canonical-infra-cause",
                    type = "infra-failure",
                    title = "Canonical infrastructure cause",
                    error_pattern = "Distinctive shared infrastructure token",
                    matchers = new[]
                    {
                        new { kind = "error-literal", value = "Distinctive shared infrastructure token" }
                    }
                }
            },
            retryPatterns = root.GetProperty("retryPatterns")
        };

        CommandResult result = await ExecuteHarnessAsync(expandedPayload);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("matched conflicting canonical prior causes", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task IgnoresDisabledRetryPatterns()
    {
        const string currentCauseId = "current-infra-cause";
        JsonElement result = await ResolveAsync(CreateRetryPatternPayload(
            currentCauseId,
            new
            {
                output = "Shared retry token",
                causeId = "disabled-canonical-cause",
                enabled = false
            }));

        Assert.Equal(currentCauseId, FindOnlyCause(result).GetProperty("id").GetString());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task IgnoresInvalidRetryPatternRegex()
    {
        const string currentCauseId = "current-infra-cause";
        JsonElement result = await ResolveAsync(CreateRetryPatternPayload(
            currentCauseId,
            new
            {
                output = new { regex = "[invalid" },
                causeId = "invalid-regex-canonical-cause"
            }));

        Assert.Equal(currentCauseId, FindOnlyCause(result).GetProperty("id").GetString());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task CommandLineRewritesCauseFilesAndRunReferences()
    {
        const string proposedCauseId = "windows-process-init-0xc0000142";
        const string canonicalCauseId = "windows-process-init-failure-0xc0000142";

        string analysisPath = Path.Combine(_workspace.Path, "analysis-result.json");
        string causesDirectory = Directory.CreateDirectory(Path.Combine(_workspace.Path, "causes")).FullName;
        string priorCausesDirectory = Directory.CreateDirectory(Path.Combine(_workspace.Path, "prior-causes")).FullName;
        string retryPatternsPath = Path.Combine(_workspace.Path, "retry-patterns.json");

        await WriteJsonAsync(analysisPath, new
        {
            causes = new[] { proposedCauseId },
            failed_jobs = new[]
            {
                new
                {
                    id = 1,
                    name = "Tests / Sample / Sample (windows-latest)",
                    classification = "transient-infra",
                    reason = "Process completed with exit code -1073741502 (0xC0000142)."
                }
            },
            failed_tests = Array.Empty<object>()
        });
        await WriteJsonAsync(Path.Combine(causesDirectory, $"{proposedCauseId}.json"), new
        {
            id = proposedCauseId,
            type = "infra-failure",
            title = "Windows process initialization failure",
            error_pattern = "0xC0000142",
            job_ids = new[] { 1 }
        });
        await WriteJsonAsync(Path.Combine(priorCausesDirectory, $"{canonicalCauseId}.json"), new
        {
            id = canonicalCauseId,
            type = "infra-failure",
            title = "Canonical Windows process initialization failure",
            error_pattern = "0xC0000142",
            occurrences = new[] { new { observed_at = "2026-07-10T01:22:31Z" } }
        });
        await WriteJsonAsync(retryPatternsPath, new
        {
            jobFailurePatterns = new[]
            {
                new
                {
                    jobName = new { regex = ".*windows.*" },
                    output = "0xC0000142",
                    causeId = canonicalCauseId
                }
            }
        });

        using NodeCommand command = new(_output, label: "resolveCausesCli");
        command.WithWorkingDirectory(_repoRoot).WithTimeout(TimeSpan.FromMinutes(1));

        CommandResult result = await command.ExecuteScriptAsync(
            _resolverPath,
            analysisPath,
            causesDirectory,
            priorCausesDirectory,
            retryPatternsPath);

        result.EnsureSuccessful();
        Assert.False(File.Exists(Path.Combine(causesDirectory, $"{proposedCauseId}.json")));
        Assert.True(File.Exists(Path.Combine(causesDirectory, $"{canonicalCauseId}.json")));

        using JsonDocument analysis = JsonDocument.Parse(await File.ReadAllTextAsync(analysisPath));
        Assert.Equal(
            [canonicalCauseId],
            ReadStrings(analysis.RootElement, "causes"));
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task CommandLineMigratesLegacyCauseFiles()
    {
        const string legacyCauseId = "processguest-signalerThrows-treekill-timeout";
        const string canonicalCauseId = "processguest-signalerthrows-treekill-timeout";

        string analysisPath = Path.Combine(_workspace.Path, "legacy-analysis-result.json");
        string causesDirectory = Directory.CreateDirectory(Path.Combine(_workspace.Path, "legacy-causes")).FullName;
        string priorCausesDirectory = Directory.CreateDirectory(Path.Combine(_workspace.Path, "legacy-prior-causes")).FullName;
        string retryPatternsPath = Path.Combine(_workspace.Path, "legacy-retry-patterns.json");

        await WriteJsonAsync(analysisPath, new
        {
            causes = new[] { canonicalCauseId },
            failed_jobs = new[]
            {
                new
                {
                    id = 1,
                    name = "Tests / Sample / Sample (ubuntu-latest)",
                    classification = "transient-infra",
                    reason = "Legacy infrastructure failure"
                }
            },
            failed_tests = Array.Empty<object>()
        });
        await WriteJsonAsync(Path.Combine(causesDirectory, $"{canonicalCauseId}.json"), new
        {
            id = canonicalCauseId,
            type = "infra-failure",
            title = "Current infrastructure cause",
            error_pattern = "Legacy infrastructure failure",
            job_ids = new[] { 1 }
        });
        await WriteJsonAsync(Path.Combine(priorCausesDirectory, $"{legacyCauseId}.json"), new
        {
            id = legacyCauseId,
            type = "infra-failure",
            title = "Stored legacy cause",
            error_pattern = "Legacy infrastructure failure",
            issue_url = "https://github.com/microsoft/aspire/issues/1111",
            occurrences = new[] { new { observed_at = "2026-07-01T00:00:00Z" } }
        });
        await WriteJsonAsync(Path.Combine(priorCausesDirectory, "legacy-cause-alias.json"), new
        {
            id = "legacy-cause-alias",
            canonical_id = legacyCauseId,
            type = "infra-failure",
            title = "Legacy cause alias",
            error_pattern = "Legacy infrastructure failure"
        });
        await WriteJsonAsync(
            retryPatternsPath,
            new { jobFailurePatterns = Array.Empty<object>() });

        using NodeCommand command = new(_output, label: "migrateLegacyCause");
        command.WithWorkingDirectory(_repoRoot).WithTimeout(TimeSpan.FromMinutes(1));

        CommandResult result = await command.ExecuteScriptAsync(
            _resolverPath,
            analysisPath,
            causesDirectory,
            priorCausesDirectory,
            retryPatternsPath);
        result.EnsureSuccessful();

        string canonicalPath = Path.Combine(priorCausesDirectory, $"{canonicalCauseId}.json");
        Assert.True(File.Exists(canonicalPath));
        Assert.Equal(
            ["legacy-cause-alias.json", $"{canonicalCauseId}.json"],
            Directory.GetFiles(priorCausesDirectory, "*.json").Select(path => Path.GetFileName(path)!).Order().ToArray());

        using JsonDocument migratedDocument = JsonDocument.Parse(await File.ReadAllTextAsync(canonicalPath));
        JsonElement migratedCause = migratedDocument.RootElement;
        Assert.Equal(canonicalCauseId, migratedCause.GetProperty("id").GetString());
        Assert.Equal(
            "https://github.com/microsoft/aspire/issues/1111",
            migratedCause.GetProperty("issue_url").GetString());
        Assert.Single(migratedCause.GetProperty("occurrences").EnumerateArray());

        using JsonDocument aliasDocument = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(priorCausesDirectory, "legacy-cause-alias.json")));
        Assert.Equal(canonicalCauseId, aliasDocument.RootElement.GetProperty("canonical_id").GetString());

        using JsonDocument analysisDocument = JsonDocument.Parse(await File.ReadAllTextAsync(analysisPath));
        Assert.Equal([canonicalCauseId], ReadStrings(analysisDocument.RootElement, "causes"));

        string[] causeFiles = Directory.GetFiles(causesDirectory, "*.json")
            .Select(Path.GetFileName)
            .Order()
            .ToArray()!;
        Assert.Equal([$"{canonicalCauseId}.json"], causeFiles);
    }

    [Fact]
    public void WorkflowNormalizesCausesBeforePersistingRunSummary()
    {
        string workflow = File.ReadAllText(
            Path.Combine(_repoRoot, ".github", "workflows", "analyze-ci-failure.md"));

        int resolverIndex = workflow.IndexOf("analyze-ci-failure-cause-resolver.js", StringComparison.Ordinal);
        int persistenceIndex = workflow.IndexOf("cp \"$ANALYSIS_FILE\" \"memory-repo/runs/${RUN_ID}.json\"", StringComparison.Ordinal);
        int memoryPushIndex = workflow.IndexOf("git -C memory-repo push origin \"HEAD:$MEMORY_BRANCH\"", StringComparison.Ordinal);
        int issueCreationIndex = workflow.IndexOf("CREATED_ISSUE_URL=$(gh issue create", StringComparison.Ordinal);

        Assert.True(resolverIndex >= 0, "The publish job must invoke the deterministic cause resolver.");
        Assert.True(persistenceIndex > resolverIndex, "Cause identities must be normalized before the run summary is persisted.");
        Assert.Contains("group: analyze-ci-failure-publish", workflow, StringComparison.Ordinal);
        Assert.Contains("queue: max", workflow, StringComparison.Ordinal);
        Assert.Contains("$ex * ($new | del(.occurrences))", workflow, StringComparison.Ordinal);
        Assert.True(memoryPushIndex < issueCreationIndex, "Canonical cause identities must be pushed before issue side effects.");
        Assert.Contains("node .github/workflows/analyze-ci-failure-cause-resolver.js \\", workflow, StringComparison.Ordinal);
        Assert.Contains("|| RESOLVER_STATUS=$?", workflow, StringComparison.Ordinal);
        Assert.Contains("if [ \"$RESOLVER_STATUS\" -eq 0 ]; then", workflow, StringComparison.Ordinal);
        Assert.True(
            workflow.LastIndexOf("exit \"$RESOLVER_STATUS\"", StringComparison.Ordinal) > workflow.IndexOf("# ── 5. Post PR comment", StringComparison.Ordinal),
            "Resolver failures must be reported only after the independent PR analysis comment is posted.");
        Assert.False(
            workflow.Contains("FIRST_JOB=$(jq -r '.failed_jobs[0].name", StringComparison.Ordinal),
            "Occurrence attribution must come from each cause's job references.");
    }

    private async Task<JsonElement> ResolveAsync(object payload)
    {
        CommandResult result = await ExecuteHarnessAsync(payload);
        result.EnsureSuccessful();

        HarnessResponse<JsonElement>? response = JsonSerializer.Deserialize<HarnessResponse<JsonElement>>(
            result.Output,
            s_jsonOptions);
        Assert.NotNull(response);

        return response.Result;
    }

    private async Task<CommandResult> ExecuteHarnessAsync(object payload)
    {
        string inputPath = Path.Combine(_workspace.Path, $"{Guid.NewGuid():N}.json");
        string requestJson = JsonSerializer.Serialize(new HarnessRequest
        {
            Operation = "resolveCauses",
            Payload = payload
        }, s_jsonOptions);

        await File.WriteAllTextAsync(inputPath, requestJson);

        using NodeCommand command = new(_output, label: "resolveCauses");
        command.WithWorkingDirectory(_repoRoot).WithTimeout(TimeSpan.FromMinutes(1));

        return await command.ExecuteScriptAsync(_harnessPath, inputPath);
    }

    private static Task WriteJsonAsync(string path, object value)
        => File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, s_jsonOptions));

    private static object CreateSingleTestPayload(
        string testName,
        string causeId,
        object priorCause,
        string error = "System.InvalidOperationException: Collection was modified.")
        => new
        {
            analysis = new
            {
                causes = new[] { causeId },
                failed_jobs = new[]
                {
                    new
                    {
                        id = 1,
                        name = "Tests / Sample / Sample (ubuntu-latest)",
                        classification = "flaky-test",
                        reason = error
                    }
                },
                failed_tests = new[]
                {
                    new
                    {
                        name = testName,
                        job = "Tests / Sample / Sample (ubuntu-latest)",
                        error,
                        stack_trace = string.Empty
                    }
                }
            },
            causes = new[]
            {
                new
                {
                    id = causeId,
                    type = "flaky-test",
                    title = "Current flaky test",
                    test_name = testName,
                    error_pattern = error,
                    job_ids = new[] { 1 }
                }
            },
            priorCauses = new[] { priorCause },
            retryPatterns = new { jobFailurePatterns = Array.Empty<object>() }
        };

    private static object CreatePriorMatcherCause(string causeId)
        => new
        {
            id = causeId,
            type = "infra-failure",
            title = causeId,
            error_pattern = "Shared deterministic failure token",
            matchers = new[]
            {
                new
                {
                    kind = "error-literal",
                    value = "Shared deterministic failure token"
                }
            },
            occurrences = new[] { new { observed_at = "2026-07-01T00:00:00Z" } }
        };

    private static object CreateLegacyCause(string causeId)
        => new
        {
            id = causeId,
            type = "infra-failure",
            title = causeId,
            error_pattern = "Legacy infrastructure failure"
        };

    private static object CreateRetryPatternPayload(string causeId, object retryPattern)
        => new
        {
            analysis = new
            {
                causes = new[] { causeId },
                failed_jobs = new[]
                {
                    new
                    {
                        id = 1,
                        name = "Tests / Sample / Sample (ubuntu-latest)",
                        classification = "transient-infra",
                        reason = "Shared retry token"
                    }
                },
                failed_tests = Array.Empty<object>()
            },
            causes = new[]
            {
                new
                {
                    id = causeId,
                    type = "infra-failure",
                    title = "Current infrastructure cause",
                    error_pattern = "Shared retry token",
                    job_ids = new[] { 1 }
                }
            },
            priorCauses = Array.Empty<object>(),
            retryPatterns = new { jobFailurePatterns = new[] { retryPattern } }
        };

    private static JsonElement FindOnlyCause(JsonElement result)
        => Assert.Single(result.GetProperty("causes").EnumerateArray());

    private static JsonElement FindCause(JsonElement result, string causeId)
        => result.GetProperty("causes")
            .EnumerateArray()
            .Single(cause => cause.GetProperty("id").GetString() == causeId);

    private static string[] ReadStrings(JsonElement element, string propertyName)
        => element.GetProperty(propertyName)
            .EnumerateArray()
            .Select(static value => value.GetString()!)
            .ToArray();

    private static int[] ReadInt32s(JsonElement element, string propertyName)
        => element.GetProperty(propertyName)
            .EnumerateArray()
            .Select(static value => value.GetInt32())
            .ToArray();

    private sealed class HarnessRequest
    {
        public string Operation { get; init; } = string.Empty;
        public object? Payload { get; init; }
    }

    private sealed class HarnessResponse<T>
    {
        public required T Result { get; init; }
    }
}
