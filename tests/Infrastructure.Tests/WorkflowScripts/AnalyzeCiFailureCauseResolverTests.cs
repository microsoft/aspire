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
                    issue_url = "https://github.com/microsoft/aspire/issues/42",
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
        Assert.Equal(
            "https://github.com/microsoft/aspire/issues/42",
            windowsCause.GetProperty("issue_url").GetString());
        Assert.Equal(["windows-process-init-0xc0000142"], ReadStrings(windowsCause, "aliases"));
        JsonElement windowsAlias = Assert.Single(
            result.GetProperty("priorCauseAliases").EnumerateArray(),
            alias => alias.GetProperty("legacy_id").GetString() == "windows-process-init-0xc0000142");
        Assert.Equal(
            "windows-process-init-failure-0xc0000142",
            windowsAlias.GetProperty("canonical_id").GetString());

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
    public async Task CoalescesFreshFlakyCausesByNormalizedTestIdentityDeterministically()
    {
        JsonElement forward = await ResolveAsync(CreateFreshSameTestPayload(reverse: false));
        JsonElement reversed = await ResolveAsync(CreateFreshSameTestPayload(reverse: true));

        Assert.Equal(forward.GetRawText(), reversed.GetRawText());

        JsonElement cause = FindOnlyCause(forward);
        Assert.Equal("sample-theory-alpha", cause.GetProperty("id").GetString());
        Assert.Equal("Alpha theory failure", cause.GetProperty("title").GetString());
        Assert.Equal(
            "Aspire.Tests.SampleTheory(value: 1)",
            cause.GetProperty("test_name").GetString());
        Assert.Equal("Alpha failure", cause.GetProperty("error_pattern").GetString());
        Assert.Equal(
            [
                "Aspire.Tests.SampleTheory(value: 1)",
                "Aspire.Tests.SampleTheory(value: 2)"
            ],
            ReadStrings(cause, "test_names"));
        Assert.Equal(["sample-theory-zeta"], ReadStrings(cause, "aliases"));
        Assert.Equal([101, 202], ReadInt32s(cause, "job_ids"));
        Assert.Equal(["Tests / Linux", "Tests / Windows"], ReadStrings(cause, "job_names"));

        Assert.Equal(["sample-theory-alpha"], ReadStrings(forward.GetProperty("analysis"), "causes"));
        foreach (JsonElement job in forward.GetProperty("analysis").GetProperty("failed_jobs").EnumerateArray())
        {
            Assert.Equal(["sample-theory-alpha"], ReadStrings(job, "cause_ids"));
        }
        foreach (JsonElement test in forward.GetProperty("analysis").GetProperty("failed_tests").EnumerateArray())
        {
            Assert.Equal("sample-theory-alpha", test.GetProperty("cause_id").GetString());
        }

        JsonElement canonicalization = Assert.Single(
            forward.GetProperty("canonicalizations").EnumerateArray(),
            item => item.GetProperty("proposed_id").GetString() == "sample-theory-zeta");
        Assert.Equal("sample-theory-alpha", canonicalization.GetProperty("canonical_id").GetString());
        Assert.Empty(forward.GetProperty("priorCauseMigrations").EnumerateArray());
        Assert.Empty(forward.GetProperty("priorCauseAliases").EnumerateArray());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task AuthoritativeHistoricalCauseAbsorbsFreshSameTestCauseDeterministically()
    {
        JsonElement forward = await ResolveAsync(CreateAuthoritativeSameTestPayload(reverse: false));
        JsonElement reversed = await ResolveAsync(CreateAuthoritativeSameTestPayload(reverse: true));

        Assert.Equal(forward.GetRawText(), reversed.GetRawText());

        JsonElement cause = FindOnlyCause(forward);
        Assert.Equal("canonical-sample-test", cause.GetProperty("id").GetString());
        Assert.Equal("Stored sample test", cause.GetProperty("title").GetString());
        Assert.Equal(
            [
                "Aspire.Tests.SampleTheory(value: 1)",
                "Aspire.Tests.SampleTheory(value: 2)",
                "Aspire.Tests.SampleTheory(value: 3)"
            ],
            ReadStrings(cause, "test_names"));
        Assert.Equal(
            ["fresh-sample-test", "fresh-zeta-sample-test"],
            ReadStrings(cause, "aliases"));
        Assert.Equal([101, 202, 303], ReadInt32s(cause, "job_ids"));
        Assert.Equal(
            ["Tests / Linux", "Tests / Windows", "Tests / macOS"],
            ReadStrings(cause, "job_names"));

        Assert.Equal(["canonical-sample-test"], ReadStrings(forward.GetProperty("analysis"), "causes"));
        foreach (JsonElement job in forward.GetProperty("analysis").GetProperty("failed_jobs").EnumerateArray())
        {
            Assert.Equal(["canonical-sample-test"], ReadStrings(job, "cause_ids"));
        }
        foreach (JsonElement test in forward.GetProperty("analysis").GetProperty("failed_tests").EnumerateArray())
        {
            Assert.Equal("canonical-sample-test", test.GetProperty("cause_id").GetString());
        }

        Assert.Equal(
            [
                "fresh-sample-test:canonical-sample-test",
                "fresh-zeta-sample-test:canonical-sample-test"
            ],
            forward.GetProperty("canonicalizations")
                .EnumerateArray()
                .Select(item =>
                    $"{item.GetProperty("proposed_id").GetString()}:{item.GetProperty("canonical_id").GetString()}"));
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ConvergesCompatibleAuthoritativeCanonicalIdsForOneTestIdentity()
    {
        JsonElement result = await ResolveAsync(new
        {
            analysis = new
            {
                causes = new[] { "proposal-alpha", "proposal-beta" },
                failed_jobs = new object[]
                {
                    new
                    {
                        id = 101,
                        name = "Tests / Linux",
                        classification = "flaky-test",
                        reason = "Alpha failure"
                    },
                    new
                    {
                        id = 202,
                        name = "Tests / Windows",
                        classification = "flaky-test",
                        reason = "Beta failure"
                    }
                },
                failed_tests = new object[]
                {
                    new
                    {
                        name = "Aspire.Tests.SampleTheory(value: 1)",
                        job = "Tests / Linux",
                        error = "Alpha failure",
                        stack_trace = string.Empty
                    },
                    new
                    {
                        name = "Aspire.Tests.SampleTheory(value: 2)",
                        job = "Tests / Windows",
                        error = "Beta failure",
                        stack_trace = string.Empty
                    }
                }
            },
            causes = new object[]
            {
                new
                {
                    id = "proposal-alpha",
                    type = "flaky-test",
                    title = "Alpha failure",
                    test_name = "Aspire.Tests.SampleTheory(value: 1)",
                    error_pattern = "Alpha failure",
                    job_ids = new[] { 101 }
                },
                new
                {
                    id = "proposal-beta",
                    type = "flaky-test",
                    title = "Beta failure",
                    test_name = "Aspire.Tests.SampleTheory(value: 2)",
                    error_pattern = "Beta failure",
                    job_ids = new[] { 202 }
                }
            },
            priorCauses = new object[]
            {
                new
                {
                    id = "proposal-alpha",
                    canonical_id = "canonical-alpha",
                    type = "flaky-test"
                },
                new
                {
                    id = "canonical-alpha",
                    type = "flaky-test",
                    title = "Canonical alpha",
                    test_name = "Aspire.Tests.SampleTheory(value: 1)",
                    error_pattern = "Alpha failure"
                },
                new
                {
                    id = "proposal-beta",
                    canonical_id = "canonical-beta",
                    type = "flaky-test"
                },
                new
                {
                    id = "canonical-beta",
                    type = "flaky-test",
                    title = "Canonical beta",
                    test_name = "Aspire.Tests.SampleTheory(value: 2)",
                    error_pattern = "Beta failure"
                }
            },
            retryPatterns = new { jobFailurePatterns = Array.Empty<object>() }
        });

        JsonElement cause = FindOnlyCause(result);
        Assert.Equal("canonical-alpha", cause.GetProperty("id").GetString());
        Assert.Equal(
            ["canonical-beta", "proposal-alpha", "proposal-beta"],
            ReadStrings(cause, "aliases"));
        foreach (JsonElement job in result.GetProperty("analysis").GetProperty("failed_jobs").EnumerateArray())
        {
            Assert.Equal(["canonical-alpha"], ReadStrings(job, "cause_ids"));
        }
        Assert.Equal(
            [
                "canonical-beta:canonical-alpha",
                "proposal-beta:canonical-alpha"
            ],
            result.GetProperty("priorCauseAliases")
                .EnumerateArray()
                .Select(alias =>
                    $"{alias.GetProperty("legacy_id").GetString()}:{alias.GetProperty("canonical_id").GetString()}"));
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task SupportsMainRepositoryBreakageCauses()
    {
        JsonElement result = await ResolveAsync(new
        {
            analysis = new
            {
                causes = new[] { "main-build-breakage" },
                failed_jobs = new[]
                {
                    new
                    {
                        id = 303,
                        name = "Build / Build Aspire",
                        classification = "main-repository-breakage",
                        reason = "The main branch no longer compiles."
                    }
                },
                failed_tests = Array.Empty<object>()
            },
            causes = new[]
            {
                new
                {
                    id = "main-build-breakage",
                    type = "main-repository-breakage",
                    title = "Main branch build failure",
                    error_pattern = "error CS1002: ; expected",
                    job_ids = new[] { 303 }
                }
            },
            priorCauses = Array.Empty<object>(),
            retryPatterns = new { jobFailurePatterns = Array.Empty<object>() }
        });

        JsonElement cause = FindOnlyCause(result);
        Assert.Equal("main-repository-breakage", cause.GetProperty("type").GetString());
        Assert.Equal(["Build / Build Aspire"], ReadStrings(cause, "job_names"));

        JsonElement failedJob = result.GetProperty("analysis").GetProperty("failed_jobs").EnumerateArray().Single();
        Assert.Equal(["main-build-breakage"], ReadStrings(failedJob, "cause_ids"));
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
    public async Task RejectsNoncanonicalProposedCauseIds()
    {
        CommandResult result = await ExecuteHarnessAsync(new
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

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "Cause ID 'NuGet_Feed Timeout' must already be a canonical lowercase slug.",
            result.Output,
            StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RejectsNoncanonicalRunSummaryCauseIds()
    {
        CommandResult result = await ExecuteHarnessAsync(new
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
                    id = "nuget-feed-timeout",
                    type = "infra-failure",
                    title = "NuGet feed timeout",
                    error_pattern = "The NuGet feed timed out.",
                    job_ids = new[] { 1 }
                }
            },
            priorCauses = Array.Empty<object>(),
            retryPatterns = new { jobFailurePatterns = Array.Empty<object>() }
        });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "Cause ID 'NuGet_Feed Timeout' must already be a canonical lowercase slug.",
            result.Output,
            StringComparison.Ordinal);
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
    public async Task RejectsMalformedStoredMatcherWithCauseContext()
    {
        object payload = CreateSingleTestPayload(
            "Aspire.Sample.Tests.SampleTests.CurrentTest",
            "current-sample-cause",
            new
            {
                id = "stored-malformed-matcher",
                type = "flaky-test",
                title = "Stored cause with malformed matcher",
                test_name = "Aspire.Sample.Tests.SampleTests.PriorTest",
                error_pattern = "Shared failure token",
                matchers = new object[]
                {
                    new
                    {
                        kind = "error-literal",
                        value = "Shared failure token"
                    },
                    new
                    {
                        kind = "error-regex",
                        pattern = "[invalid",
                        flags = "i"
                    }
                }
            },
            error: "Shared failure token");

        CommandResult result = await ExecuteHarnessAsync(payload);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Prior cause 'stored-malformed-matcher' matcher 1", result.Output, StringComparison.Ordinal);
        Assert.Contains("invalid regular expression '[invalid' with flags 'i'", result.Output, StringComparison.Ordinal);
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

        JsonElement cause = FindOnlyCause(result);
        Assert.Equal(canonicalCauseId, cause.GetProperty("id").GetString());
        Assert.Equal([aliasCauseId], ReadStrings(cause, "aliases"));
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
    public async Task MatchesCanonicalProposalToLegacyPriorCauseId()
    {
        const string legacyCauseId = "processguest-signalerThrows-treekill-timeout";
        const string canonicalCauseId = "processguest-signalerthrows-treekill-timeout";
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
                        reason = "Legacy infrastructure failure"
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

        JsonElement cause = FindOnlyCause(result);
        Assert.Equal(canonicalCauseId, cause.GetProperty("id").GetString());
        Assert.Equal([legacyCauseId], ReadStrings(cause, "aliases"));
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
    public async Task DoesNotMatchSupportedTypeIncompatiblePriorCauseForTestName()
    {
        const string testName = "Aspire.Sample.Tests.SampleTests.FlakyTest";
        const string currentCauseId = "current-flaky-test";
        object payload = CreateSingleTestPayload(
            testName,
            currentCauseId,
            new
            {
                id = "prior-infra-failure",
                type = "infra-failure",
                title = "Prior infrastructure failure",
                test_name = testName,
                error_pattern = "The sample test failed."
            },
            error: "The sample test failed.");

        JsonElement result = await ResolveAsync(payload);

        Assert.Equal(currentCauseId, FindOnlyCause(result).GetProperty("id").GetString());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task PrefersTypeCompatiblePriorCauseForMatchingTestName()
    {
        const string testName = "Aspire.Sample.Tests.SampleTests.FlakyTest";
        const string canonicalCauseId = "canonical-flaky-test";
        const string issueUrl = "https://github.com/microsoft/aspire/issues/12345";
        object payload = CreateSingleTestPayload(
            testName,
            "current-flaky-test",
            new
            {
                id = canonicalCauseId,
                type = "flaky-test",
                title = "Canonical flaky test",
                test_name = testName,
                error_pattern = "The sample test failed.",
                issue_url = issueUrl,
                occurrences = new[] { new { observed_at = "2026-07-02T00:00:00Z" } }
            },
            error: "The sample test failed.");

        using JsonDocument payloadDocument = JsonDocument.Parse(JsonSerializer.Serialize(payload, s_jsonOptions));
        JsonElement root = payloadDocument.RootElement;
        object expandedPayload = new
        {
            analysis = root.GetProperty("analysis"),
            causes = root.GetProperty("causes"),
            priorCauses = new object[]
            {
                new
                {
                    id = "older-infra-failure",
                    type = "infra-failure",
                    title = "Older infrastructure failure",
                    test_name = testName,
                    error_pattern = "The sample test failed.",
                    occurrences = new[] { new { observed_at = "2026-07-01T00:00:00Z" } }
                },
                root.GetProperty("priorCauses")[0]
            },
            retryPatterns = root.GetProperty("retryPatterns")
        };

        JsonElement result = await ResolveAsync(expandedPayload);

        JsonElement cause = FindOnlyCause(result);
        Assert.Equal(canonicalCauseId, cause.GetProperty("id").GetString());
        Assert.Equal(issueUrl, cause.GetProperty("issue_url").GetString());
        Assert.False(cause.TryGetProperty("aliases", out _));
        Assert.Empty(result.GetProperty("priorCauseAliases").EnumerateArray());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ConvergesCompatibleHistoricalTestRootsToTheOldestCanonicalCause()
    {
        JsonElement forward = await ResolveAsync(CreateHistoricalSameTestPayload(reverse: false));
        JsonElement reversed = await ResolveAsync(CreateHistoricalSameTestPayload(reverse: true));

        Assert.Equal(forward.GetRawText(), reversed.GetRawText());

        JsonElement cause = FindOnlyCause(forward);
        Assert.Equal("oldest-sample-test", cause.GetProperty("id").GetString());
        Assert.Equal("Oldest sample test", cause.GetProperty("title").GetString());
        Assert.Equal(
            "https://github.com/microsoft/aspire/issues/200",
            cause.GetProperty("issue_url").GetString());
        Assert.Equal(
            ["newer-sample-alias", "newer-sample-marker", "newer-sample-test", "oldest-sample-marker"],
            ReadStrings(cause, "aliases"));

        Assert.Empty(forward.GetProperty("priorCauseMigrations").EnumerateArray());
        Assert.Equal(
            [
                "newer-sample-alias:oldest-sample-test",
                "newer-sample-test:oldest-sample-test"
            ],
            forward.GetProperty("priorCauseAliases")
                .EnumerateArray()
                .Select(alias =>
                    $"{alias.GetProperty("legacy_id").GetString()}:{alias.GetProperty("canonical_id").GetString()}"));
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ConvergesLegacyNormalizedTestRootToExistingCompatibleCanonical()
    {
        JsonElement forward = await ResolveAsync(CreateLegacyNormalizedSameTestPayload(reverse: false));
        JsonElement reversed = await ResolveAsync(CreateLegacyNormalizedSameTestPayload(reverse: true));

        Assert.Equal(forward.GetRawText(), reversed.GetRawText());

        JsonElement cause = FindOnlyCause(forward);
        Assert.Equal("legacy-sample", cause.GetProperty("id").GetString());
        Assert.Equal(
            "https://github.com/microsoft/aspire/issues/10",
            cause.GetProperty("issue_url").GetString());
        Assert.Equal(
            ["Legacy.Sample", "legacy-root-alias", "legacy-root-marker", "safe-root-marker"],
            ReadStrings(cause, "aliases"));
        Assert.Empty(forward.GetProperty("priorCauseMigrations").EnumerateArray());
        Assert.Equal(
            [
                "legacy-root-alias:legacy-sample",
                "Legacy.Sample:legacy-sample"
            ],
            forward.GetProperty("priorCauseAliases")
                .EnumerateArray()
                .Select(alias =>
                    $"{alias.GetProperty("legacy_id").GetString()}:{alias.GetProperty("canonical_id").GetString()}"));
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ConvergesProposedHistoricalAliasToTheOldestSameTestRoot()
    {
        JsonElement result = await ResolveAsync(
            CreateHistoricalSameTestPayload(reverse: false, proposedCauseId: "newer-sample-alias"));

        JsonElement cause = FindOnlyCause(result);
        Assert.Equal("oldest-sample-test", cause.GetProperty("id").GetString());
        Assert.Equal(
            ["newer-sample-alias", "newer-sample-marker", "newer-sample-test", "oldest-sample-marker"],
            ReadStrings(cause, "aliases"));
        Assert.Equal(
            [
                "newer-sample-alias:oldest-sample-test",
                "newer-sample-test:oldest-sample-test"
            ],
            result.GetProperty("priorCauseAliases")
                .EnumerateArray()
                .Select(alias =>
                    $"{alias.GetProperty("legacy_id").GetString()}:{alias.GetProperty("canonical_id").GetString()}"));
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RejectsHistoricalSameTestAliasWhoseRootHasAnotherType()
    {
        const string testName = "Aspire.Sample.Tests.SampleTests.FlakyTest";
        using JsonDocument payloadDocument = JsonDocument.Parse(
            JsonSerializer.Serialize(CreateHistoricalSameTestPayload(reverse: false), s_jsonOptions));
        JsonElement payload = payloadDocument.RootElement;
        JsonElement[] priorCauses =
        [
            .. payload.GetProperty("priorCauses").EnumerateArray(),
            JsonSerializer.SerializeToElement(new
            {
                id = "mismatched-flaky-alias",
                canonical_id = "mismatched-infra-root",
                type = "flaky-test",
                test_name = testName
            }, s_jsonOptions),
            JsonSerializer.SerializeToElement(new
            {
                id = "mismatched-infra-root",
                type = "infra-failure",
                title = "Mismatched infrastructure root",
                error_pattern = "Infrastructure failed",
                occurrences = new[] { new { observed_at = "2026-08-03T00:00:00Z" } }
            }, s_jsonOptions)
        ];
        CommandResult result = await ExecuteHarnessAsync(new
        {
            analysis = payload.GetProperty("analysis"),
            causes = payload.GetProperty("causes"),
            priorCauses,
            retryPatterns = payload.GetProperty("retryPatterns")
        });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "Cause 'current-sample-test' of type 'flaky-test' cannot alias prior cause type 'infra-failure'.",
            result.Output,
            StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RejectsHistoricalTestRootThatWouldAliasToConflictingCanonicalCauses()
    {
        const string alphaTestName = "Aspire.Sample.Tests.AlphaTests.FlakyTest";
        const string betaTestName = "Aspire.Sample.Tests.BetaTests.FlakyTest";
        CommandResult result = await ExecuteHarnessAsync(new
        {
            analysis = new
            {
                causes = new[] { "current-alpha", "current-beta" },
                failed_jobs = new object[]
                {
                    new
                    {
                        id = 1,
                        name = "Tests / Alpha",
                        classification = "flaky-test",
                        reason = "Alpha failed"
                    },
                    new
                    {
                        id = 2,
                        name = "Tests / Beta",
                        classification = "flaky-test",
                        reason = "Beta failed"
                    }
                },
                failed_tests = new object[]
                {
                    new
                    {
                        name = alphaTestName,
                        job = "Tests / Alpha",
                        error = "Alpha failed",
                        stack_trace = string.Empty
                    },
                    new
                    {
                        name = betaTestName,
                        job = "Tests / Beta",
                        error = "Beta failed",
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
                    title = "Current alpha",
                    test_name = alphaTestName,
                    error_pattern = "Alpha failed",
                    job_ids = new[] { 1 }
                },
                new
                {
                    id = "current-beta",
                    type = "flaky-test",
                    title = "Current beta",
                    test_name = betaTestName,
                    error_pattern = "Beta failed",
                    job_ids = new[] { 2 }
                }
            },
            priorCauses = new object[]
            {
                new
                {
                    id = "oldest-alpha",
                    type = "flaky-test",
                    title = "Oldest alpha",
                    test_name = alphaTestName,
                    error_pattern = "Alpha failed",
                    occurrences = new[] { new { observed_at = "2026-08-01T00:00:00Z" } }
                },
                new
                {
                    id = "oldest-beta",
                    type = "flaky-test",
                    title = "Oldest beta",
                    test_name = betaTestName,
                    error_pattern = "Beta failed",
                    occurrences = new[] { new { observed_at = "2026-08-01T00:00:00Z" } }
                },
                new
                {
                    id = "shared-newer-root",
                    type = "flaky-test",
                    title = "Shared newer root",
                    test_names = new[] { alphaTestName, betaTestName },
                    error_pattern = "Shared failure",
                    occurrences = new[] { new { observed_at = "2026-08-02T00:00:00Z" } }
                }
            },
            retryPatterns = new { jobFailurePatterns = Array.Empty<object>() }
        });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "Prior cause 'shared-newer-root' matched conflicting canonical causes 'oldest-alpha' and 'oldest-beta'.",
            result.Output,
            StringComparison.Ordinal);
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
    public async Task RejectsAliasWhoseSourceIsAlsoCanonicalInCurrentBatch()
    {
        CommandResult result = await ExecuteHarnessAsync(CreateAliasCanonicalCollisionPayload());

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "cannot alias prior cause 'canonical-beta' because the current batch also resolves to it",
            result.Output,
            StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RejectsAliasWhoseNormalizedSourceIsCanonicalInCurrentBatch()
    {
        CommandResult result = await ExecuteHarnessAsync(
            CreateAliasCanonicalCollisionPayload("Canonical Beta"));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "cannot alias prior cause 'Canonical Beta' because the current batch also resolves to 'canonical-beta'",
            result.Output,
            StringComparison.Ordinal);
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
    public async Task AppliesJobNameOnlyRetryPattern()
    {
        const string canonicalCauseId = "stable-job-cause";
        JsonElement result = await ResolveAsync(CreateRetryPatternPayload(
            "agent-proposed-cause",
            new
            {
                jobName = new { regex = ".*Sample.*" },
                causeId = canonicalCauseId
            }));

        Assert.Equal(canonicalCauseId, FindOnlyCause(result).GetProperty("id").GetString());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RetryPatternMigratesNormalizedLegacyCause()
    {
        const string legacyCauseId = "Windows Process Init Failure 0xC0000142";
        const string canonicalCauseId = "windows-process-init-failure-0xc0000142";
        const string issueUrl = "https://github.com/microsoft/aspire/issues/42";
        JsonElement result = await ResolveAsync(CreateRetryPatternPayload(
            "agent-proposed-cause",
            new
            {
                output = "Shared retry token",
                causeId = canonicalCauseId
            },
            [
                new
                {
                    id = legacyCauseId,
                    type = "infra-failure",
                    title = "Stored legacy cause",
                    error_pattern = "Legacy infrastructure failure",
                    issue_url = issueUrl
                }
            ]));

        JsonElement cause = FindOnlyCause(result);
        Assert.Equal(canonicalCauseId, cause.GetProperty("id").GetString());
        Assert.Equal(issueUrl, cause.GetProperty("issue_url").GetString());
        Assert.Equal([legacyCauseId], ReadStrings(cause, "aliases"));
        JsonElement migration = Assert.Single(result.GetProperty("priorCauseMigrations").EnumerateArray());
        Assert.Equal(legacyCauseId, migration.GetProperty("legacy_id").GetString());
        Assert.Equal(canonicalCauseId, migration.GetProperty("canonical_id").GetString());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task JobRetryPatternCannotBeClaimedByFlakyCauseBeforeInfraCause()
    {
        const string canonicalCauseId = "windows-process-init-failure-0xc0000142";
        var retryPatterns = new
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
        };
        JsonElement firstRun = await ResolveAsync(new
        {
            analysis = new
            {
                causes = new[] { "flaky-worker-proposal" },
                failed_jobs = new[]
                {
                    new
                    {
                        id = 1,
                        name = "Tests / Worker (windows-latest)",
                        classification = "flaky-test",
                        reason = "Process completed with exit code -1073741502 (0xC0000142)"
                    }
                },
                failed_tests = Array.Empty<object>()
            },
            causes = new[]
            {
                new
                {
                    id = "flaky-worker-proposal",
                    type = "flaky-test",
                    title = "Worker test failed",
                    error_pattern = "Process completed with exit code -1073741502 (0xC0000142)",
                    job_ids = new[] { 1 }
                }
            },
            priorCauses = Array.Empty<object>(),
            retryPatterns
        });

        Assert.Equal("flaky-worker-proposal", FindOnlyCause(firstRun).GetProperty("id").GetString());

        JsonElement secondRun = await ResolveAsync(new
        {
            analysis = new
            {
                causes = new[] { "infra-worker-proposal" },
                failed_jobs = new[]
                {
                    new
                    {
                        id = 2,
                        name = "Build / Worker (windows-latest)",
                        classification = "transient-infra",
                        reason = "Process completed with exit code -1073741502 (0xC0000142)"
                    }
                },
                failed_tests = Array.Empty<object>()
            },
            causes = new[]
            {
                new
                {
                    id = "infra-worker-proposal",
                    type = "infra-failure",
                    title = "Windows process initialization failed",
                    error_pattern = "Process completed with exit code -1073741502 (0xC0000142)",
                    job_ids = new[] { 2 }
                }
            },
            priorCauses = firstRun.GetProperty("causes"),
            retryPatterns
        });

        JsonElement infraCause = FindOnlyCause(secondRun);
        Assert.Equal(canonicalCauseId, infraCause.GetProperty("id").GetString());
        Assert.Equal("infra-failure", infraCause.GetProperty("type").GetString());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RerunAttributionRejectsMissingTrustedJobCoverage()
    {
        CommandResult result = await ExecuteHarnessAsync(
            new
            {
                analysis = new
                {
                    failed_jobs = new[]
                    {
                        new { id = 1, classification = "transient-infra" },
                        new { id = 2, classification = "transient-infra" }
                    }
                },
                causes = new[]
                {
                    new
                    {
                        id = "first-infra-cause",
                        type = "infra-failure",
                        job_ids = new[] { 1 }
                    }
                },
                trustedFailedJobs = new[]
                {
                    new { id = 1, name = "Build / Linux" },
                    new { id = 2, name = "Build / Windows" }
                }
            },
            "validateCauseJobAttribution");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Tracked failed jobs are missing cause references: 2", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RejectsCauseTypesAssignedToIncompatibleJobClassifications()
    {
        CommandResult result = await ExecuteHarnessAsync(
            new
            {
                analysis = new
                {
                    failed_jobs = new[]
                    {
                        new { id = 1, classification = "transient-infra" },
                        new { id = 2, classification = "main-repository-breakage" }
                    }
                },
                causes = new object[]
                {
                    new
                    {
                        id = "infra-cause",
                        type = "infra-failure",
                        job_ids = new[] { 2 }
                    },
                    new
                    {
                        id = "main-breakage",
                        type = "main-repository-breakage",
                        job_ids = new[] { 1 }
                    }
                },
                trustedFailedJobs = new[]
                {
                    new { id = 1, name = "Build / Linux" },
                    new { id = 2, name = "Build / Windows" }
                }
            },
            "validateCauseJobAttribution");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "Cause 'infra-cause' of type 'infra-failure' cannot reference job ID '2' classified as 'main-repository-breakage'.",
            result.Output,
            StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ResolutionAllowsFlakyCauseForDeterministicJobWithMatchingTrustedTestEvidence()
    {
        JsonElement result = await ResolveAsync(new
        {
            analysis = new
            {
                causes = new[] { "flaky-test" },
                failed_jobs = new[]
                {
                    new { id = 1, name = "Untrusted job name", classification = "code-issue" }
                },
                failed_tests = new[]
                {
                    new
                    {
                        name = "Aspire.Tests.Flaky",
                        job = "Tests / Linux",
                        classification = "flaky",
                        error = "Test failed",
                        stack_trace = "at Aspire.Tests.Flaky()",
                        reason = "Known flaky behavior"
                    }
                }
            },
            causes = new[]
            {
                new
                {
                    id = "flaky-test",
                    type = "flaky-test",
                    title = "Flaky test",
                    test_name = "Aspire.Tests.Flaky",
                    error_pattern = "Test failed",
                    job_ids = new[] { 1 }
                }
            },
            trustedFailedJobs = new[]
            {
                new { id = 1, name = "Tests / Linux" }
            },
            priorCauses = Array.Empty<object>(),
            retryPatterns = new { jobFailurePatterns = Array.Empty<object>() }
        });

        JsonElement cause = FindOnlyCause(result);
        Assert.Equal(["Tests / Linux"], ReadStrings(cause, "job_names"));
        Assert.Equal(
            ["flaky-test"],
            ReadStrings(result.GetProperty("analysis").GetProperty("failed_jobs")[0], "cause_ids"));
        Assert.Equal(
            "flaky-test",
            result.GetProperty("analysis").GetProperty("failed_tests")[0].GetProperty("cause_id").GetString());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ResolutionRejectsFlakyCauseForDeterministicJobWithoutMatchingTrustedTestEvidence()
    {
        CommandResult result = await ExecuteHarnessAsync(new
        {
            analysis = new
            {
                causes = new[] { "flaky-test" },
                failed_jobs = new[]
                {
                    new { id = 1, name = "Untrusted job name", classification = "code-issue" }
                },
                failed_tests = new[]
                {
                    new
                    {
                        name = "Aspire.Tests.Flaky",
                        job = "Tests / Windows",
                        classification = "flaky",
                        error = "Test failed",
                        stack_trace = "at Aspire.Tests.Flaky()",
                        reason = "Known flaky behavior"
                    }
                }
            },
            causes = new[]
            {
                new
                {
                    id = "flaky-test",
                    type = "flaky-test",
                    title = "Flaky test",
                    test_name = "Aspire.Tests.Flaky",
                    error_pattern = "Test failed",
                    job_ids = new[] { 1 }
                }
            },
            trustedFailedJobs = new[]
            {
                new { id = 1, name = "Tests / Linux" }
            },
            priorCauses = Array.Empty<object>(),
            retryPatterns = new { jobFailurePatterns = Array.Empty<object>() }
        });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "Cause 'flaky-test' references job 'Untrusted job name', which is classified as 'code-issue'.",
            result.Output,
            StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ResolutionRejectsFlakyCauseWhenTestNameAndClassificationMatchDifferentEvidence()
    {
        CommandResult result = await ExecuteHarnessAsync(new
        {
            analysis = new
            {
                causes = new[] { "flaky-test" },
                failed_jobs = new[]
                {
                    new { id = 1, name = "Untrusted job name", classification = "code-issue" }
                },
                failed_tests = new[]
                {
                    new
                    {
                        name = "Aspire.Tests.OtherFlaky",
                        job = "Tests / Linux",
                        classification = "flaky",
                        error = "Other test failed",
                        stack_trace = "at Aspire.Tests.OtherFlaky()",
                        reason = "Known flaky behavior"
                    },
                    new
                    {
                        name = "Aspire.Tests.Flaky",
                        job = "Tests / Linux",
                        classification = "code-issue",
                        error = "Test failed",
                        stack_trace = "at Aspire.Tests.Flaky()",
                        reason = "Deterministic failure"
                    }
                }
            },
            causes = new[]
            {
                new
                {
                    id = "flaky-test",
                    type = "flaky-test",
                    title = "Flaky test",
                    test_name = "Aspire.Tests.Flaky",
                    error_pattern = "Test failed",
                    job_ids = new[] { 1 }
                }
            },
            trustedFailedJobs = new[]
            {
                new { id = 1, name = "Tests / Linux" }
            },
            priorCauses = Array.Empty<object>(),
            retryPatterns = new { jobFailurePatterns = Array.Empty<object>() }
        });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "Cause 'flaky-test' references job 'Untrusted job name', which is classified as 'code-issue'.",
            result.Output,
            StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task AllowsFlakyCauseForDeterministicJobWithMatchingFailedTestEvidence()
    {
        CommandResult result = await ExecuteHarnessAsync(
            new
            {
                analysis = new
                {
                    failed_jobs = new[]
                    {
                        new { id = 1, name = "Tests / Linux", classification = "code-issue" }
                    },
                    failed_tests = new[]
                    {
                        new
                        {
                            name = "Aspire.Tests.Flaky",
                            job = "Tests / Linux",
                            classification = "flaky"
                        }
                    }
                },
                causes = new[]
                {
                    new
                    {
                        id = "flaky-test",
                        type = "flaky-test",
                        test_name = "Aspire.Tests.Flaky",
                        job_ids = new[] { 1 }
                    }
                },
                trustedFailedJobs = new[]
                {
                    new { id = 1, name = "Tests / Linux" }
                }
            },
            "validateCauseJobAttribution");

        result.EnsureSuccessful();
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RejectsFlakyCauseForDeterministicJobWithoutMatchingFailedTestEvidence()
    {
        CommandResult result = await ExecuteHarnessAsync(
            new
            {
                analysis = new
                {
                    failed_jobs = new[]
                    {
                        new { id = 1, name = "Tests / Linux", classification = "code-issue" }
                    },
                    failed_tests = new[]
                    {
                        new
                        {
                            name = "Aspire.Tests.Flaky",
                            job = "Tests / Windows",
                            classification = "flaky"
                        }
                    }
                },
                causes = new[]
                {
                    new
                    {
                        id = "flaky-test",
                        type = "flaky-test",
                        test_name = "Aspire.Tests.Flaky",
                        job_ids = new[] { 1 }
                    }
                },
                trustedFailedJobs = new[]
                {
                    new { id = 1, name = "Tests / Linux" }
                }
            },
            "validateCauseJobAttribution");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "Cause 'flaky-test' of type 'flaky-test' cannot reference job ID '1' classified as 'code-issue'.",
            result.Output,
            StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RejectsSwappedCauseTypesDuringResolution()
    {
        CommandResult result = await ExecuteHarnessAsync(new
        {
            analysis = new
            {
                causes = new[] { "infra-cause", "main-breakage" },
                failed_jobs = new object[]
                {
                    new
                    {
                        id = 1,
                        name = "Build / Linux",
                        classification = "transient-infra",
                        reason = "Infrastructure failure"
                    },
                    new
                    {
                        id = 2,
                        name = "Build / Windows",
                        classification = "main-repository-breakage",
                        reason = "Repository breakage"
                    }
                },
                failed_tests = Array.Empty<object>()
            },
            causes = new object[]
            {
                new
                {
                    id = "infra-cause",
                    type = "infra-failure",
                    title = "Infrastructure failure",
                    error_pattern = "Infrastructure failure",
                    job_ids = new[] { 2 }
                },
                new
                {
                    id = "main-breakage",
                    type = "main-repository-breakage",
                    title = "Repository breakage",
                    error_pattern = "Repository breakage",
                    job_ids = new[] { 1 }
                }
            },
            priorCauses = Array.Empty<object>(),
            retryPatterns = new { jobFailurePatterns = Array.Empty<object>() }
        });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "Cause 'infra-cause' references job 'Build / Windows', which is classified as 'main-repository-breakage'.",
            result.Output,
            StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RejectsCurrentCauseMergeAcrossTypes()
    {
        CommandResult result = await ExecuteHarnessAsync(new
        {
            analysis = new
            {
                causes = new[] { "infra-proposal", "shared-canonical" },
                failed_jobs = new object[]
                {
                    new
                    {
                        id = 1,
                        name = "Build / Windows",
                        classification = "transient-infra",
                        reason = "Shared failure"
                    },
                    new
                    {
                        id = 2,
                        name = "Tests / Windows",
                        classification = "flaky-test",
                        reason = "Shared failure"
                    }
                },
                failed_tests = new[]
                {
                    new
                    {
                        name = "Aspire.Tests.Sample",
                        job = "Tests / Windows",
                        error = "Shared failure",
                        stack_trace = string.Empty
                    }
                }
            },
            causes = new object[]
            {
                new
                {
                    id = "infra-proposal",
                    type = "infra-failure",
                    title = "Infrastructure failure",
                    error_pattern = "Shared failure",
                    job_ids = new[] { 1 }
                },
                new
                {
                    id = "shared-canonical",
                    type = "flaky-test",
                    title = "Flaky test",
                    test_name = "Aspire.Tests.Sample",
                    error_pattern = "Shared failure",
                    job_ids = new[] { 2 }
                }
            },
            priorCauses = Array.Empty<object>(),
            retryPatterns = new
            {
                jobFailurePatterns = new[]
                {
                    new
                    {
                        output = new { regex = "Shared failure" },
                        causeId = "shared-canonical"
                    }
                }
            }
        });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("cannot merge current causes with types", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RejectsCanonicalRedirectAcrossPriorCauseTypes()
    {
        CommandResult result = await ExecuteHarnessAsync(new
        {
            analysis = new
            {
                causes = new[] { "legacy-infra" },
                failed_jobs = new[]
                {
                    new
                    {
                        id = 1,
                        name = "Tests / Windows",
                        classification = "flaky-test",
                        reason = "Shared failure"
                    }
                },
                failed_tests = new[]
                {
                    new
                    {
                        name = "Aspire.Tests.Sample",
                        job = "Tests / Windows",
                        error = "Shared failure",
                        stack_trace = string.Empty
                    }
                }
            },
            causes = new[]
            {
                new
                {
                    id = "legacy-infra",
                    type = "flaky-test",
                    title = "Current flaky test",
                    test_name = "Aspire.Tests.Sample",
                    error_pattern = "Shared failure",
                    job_ids = new[] { 1 }
                }
            },
            priorCauses = new object[]
            {
                new
                {
                    id = "legacy-infra",
                    type = "infra-failure",
                    title = "Prior infrastructure failure",
                    error_pattern = "Shared failure"
                },
                new
                {
                    id = "canonical-flaky",
                    type = "flaky-test",
                    title = "Canonical flaky test",
                    test_name = "Aspire.Tests.Sample",
                    error_pattern = "Shared failure"
                }
            },
            retryPatterns = new { jobFailurePatterns = Array.Empty<object>() }
        });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("cannot alias prior cause type", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RejectsFlakyTestAssociationUsingAgentAuthoredJobName()
    {
        const string testName = "Aspire.Sample.Tests.SampleTests.FlakyTest";
        CommandResult result = await ExecuteHarnessAsync(new
        {
            analysis = new
            {
                causes = new[] { "sample-flaky-test" },
                failed_jobs = new[]
                {
                    new
                    {
                        id = 101,
                        name = "Agent supplied job name",
                        classification = "flaky-test",
                        reason = "The sample test failed."
                    }
                },
                failed_tests = new[]
                {
                    new
                    {
                        name = testName,
                        job = "Agent supplied job name",
                        error = "The sample test failed.",
                        stack_trace = string.Empty
                    }
                }
            },
            trustedFailedJobs = new[]
            {
                new { id = 101, name = "Tests / Sample / Sample (windows-latest)" }
            },
            causes = new[]
            {
                new
                {
                    id = "sample-flaky-test",
                    type = "flaky-test",
                    title = "Sample flaky test",
                    test_name = testName,
                    error_pattern = "The sample test failed.",
                    job_ids = new[] { 101 }
                }
            },
            priorCauses = Array.Empty<object>(),
            retryPatterns = new { jobFailurePatterns = Array.Empty<object>() }
        });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("is not in its referenced failed jobs", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task UsesTrustedJobNamesForPersistedAttribution()
    {
        JsonElement result = await ResolveAsync(new
        {
            analysis = new
            {
                causes = new[] { "worker-crash" },
                failed_jobs = new[]
                {
                    new
                    {
                        id = 101,
                        name = "Agent supplied job name",
                        classification = "transient-infra",
                        reason = "Worker crashed"
                    }
                },
                failed_tests = Array.Empty<object>()
            },
            trustedFailedJobs = new[]
            {
                new { id = 101, name = "Build / Windows" }
            },
            causes = new[]
            {
                new
                {
                    id = "worker-crash",
                    type = "infra-failure",
                    title = "Worker crash",
                    error_pattern = "Worker crashed",
                    job_ids = new[] { 101 }
                }
            },
            priorCauses = Array.Empty<object>(),
            retryPatterns = new { jobFailurePatterns = Array.Empty<object>() }
        });

        Assert.Equal(["Build / Windows"], ReadStrings(FindOnlyCause(result), "job_names"));
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
    public async Task RejectsUnsafeCauseIdInDisabledRetryPattern()
    {
        CommandResult result = await ExecuteHarnessAsync(CreateRetryPatternPayload(
            "current-infra-cause",
            new
            {
                output = "Shared retry token",
                causeId = "../outside/victim",
                enabled = false
            }));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "jobFailurePatterns[0].causeId '../outside/victim' must be a safe cause ID",
            result.Output,
            StringComparison.Ordinal);
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
        string trustedFailedJobsPath = Path.Combine(_workspace.Path, "trusted-failed-jobs.json");

        await WriteJsonAsync(analysisPath, new
        {
            causes = new[] { proposedCauseId },
            failed_jobs = new[]
            {
                new
                {
                    id = 1,
                    name = "Agent supplied job name",
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
        await WriteJsonAsync(Path.Combine(priorCausesDirectory, $"{proposedCauseId}.json"), new
        {
            id = proposedCauseId,
            type = "infra-failure",
            title = "Superseded Windows process initialization failure",
            error_pattern = "0xC0000142",
            issue_url = "https://github.com/microsoft/aspire/issues/42",
            occurrences = new[] { new { observed_at = "2026-08-01T01:22:31Z" } }
        });
        await WriteJsonAsync(trustedFailedJobsPath, new[]
        {
            new { id = 1, name = "Tests / Sample / Sample (windows-latest)" }
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
            retryPatternsPath,
            trustedFailedJobsPath);

        result.EnsureSuccessful();
        Assert.False(File.Exists(Path.Combine(causesDirectory, $"{proposedCauseId}.json")));
        Assert.True(File.Exists(Path.Combine(causesDirectory, $"{canonicalCauseId}.json")));

        using JsonDocument analysis = JsonDocument.Parse(await File.ReadAllTextAsync(analysisPath));
        Assert.Equal(
            [canonicalCauseId],
            ReadStrings(analysis.RootElement, "causes"));

        using JsonDocument canonical = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(causesDirectory, $"{canonicalCauseId}.json")));
        Assert.Equal(
            ["Tests / Sample / Sample (windows-latest)"],
            ReadStrings(canonical.RootElement, "job_names"));
        Assert.Equal(
            "https://github.com/microsoft/aspire/issues/42",
            canonical.RootElement.GetProperty("issue_url").GetString());
        Assert.Equal([proposedCauseId], ReadStrings(canonical.RootElement, "aliases"));

        using JsonDocument alias = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(priorCausesDirectory, $"{proposedCauseId}.json")));
        Assert.Equal(canonicalCauseId, alias.RootElement.GetProperty("canonical_id").GetString());
    }

    [Theory]
    [InlineData("cause--id")]
    [InlineData("cause-")]
    [RequiresTools(["node"])]
    public async Task RejectsNonCanonicalRetryPatternCauseId(string causeId)
    {
        CommandResult result = await ExecuteHarnessAsync(CreateRetryPatternPayload(
            "current-infra-cause",
            new
            {
                output = "Shared retry token",
                causeId,
                enabled = true
            }));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("must be a safe cause ID", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task CommandLineRejectsUnsafeRetryPatternCauseIdWithoutMigratingFiles()
    {
        const string proposedCauseId = "current-infra-cause";
        const string unsafeCauseId = "../outside/victim";

        string analysisPath = Path.Combine(_workspace.Path, "unsafe-analysis-result.json");
        string causesDirectory = Directory.CreateDirectory(Path.Combine(_workspace.Path, "unsafe-causes")).FullName;
        string priorCausesDirectory = Directory.CreateDirectory(Path.Combine(_workspace.Path, "unsafe-prior-causes")).FullName;
        string retryPatternsPath = Path.Combine(_workspace.Path, "unsafe-retry-patterns.json");
        string outsideDirectory = Directory.CreateDirectory(Path.Combine(_workspace.Path, "outside")).FullName;
        string outsideCausePath = Path.Combine(outsideDirectory, "victim.json");
        const string outsideCauseContents = """{"sentinel":"must remain unchanged"}""";

        await WriteJsonAsync(analysisPath, new
        {
            causes = new[] { proposedCauseId },
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
        });
        await WriteJsonAsync(Path.Combine(causesDirectory, $"{proposedCauseId}.json"), new
        {
            id = proposedCauseId,
            type = "infra-failure",
            title = "Current infrastructure cause",
            error_pattern = "Shared retry token",
            job_ids = new[] { 1 }
        });
        await WriteJsonAsync(retryPatternsPath, new
        {
            jobFailurePatterns = new[]
            {
                new
                {
                    output = "Shared retry token",
                    causeId = unsafeCauseId
                }
            }
        });
        await File.WriteAllTextAsync(outsideCausePath, outsideCauseContents);

        using NodeCommand command = new(_output, label: "rejectUnsafeCauseId");
        command.WithWorkingDirectory(_repoRoot).WithTimeout(TimeSpan.FromMinutes(1));

        CommandResult result = await command.ExecuteScriptAsync(
            _resolverPath,
            analysisPath,
            causesDirectory,
            priorCausesDirectory,
            retryPatternsPath);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("must be a safe cause ID", result.Output, StringComparison.Ordinal);
        Assert.Equal(outsideCauseContents, await File.ReadAllTextAsync(outsideCausePath));
        Assert.True(File.Exists(Path.Combine(causesDirectory, $"{proposedCauseId}.json")));
    }

    [Theory]
    [InlineData("canonical-beta")]
    [InlineData("Canonical Beta")]
    [RequiresTools(["node"])]
    public async Task CommandLineLeavesCanonicalCauseUnchangedWhenAliasSourceCollides(string canonicalBetaId)
    {
        string analysisPath = Path.Combine(_workspace.Path, "collision-analysis-result.json");
        string causesDirectory = Directory.CreateDirectory(Path.Combine(_workspace.Path, "collision-causes")).FullName;
        string priorCausesDirectory = Directory.CreateDirectory(Path.Combine(_workspace.Path, "collision-prior-causes")).FullName;
        string retryPatternsPath = Path.Combine(_workspace.Path, "collision-retry-patterns.json");
        object payload = CreateAliasCanonicalCollisionPayload(canonicalBetaId);
        using JsonDocument payloadDocument = JsonDocument.Parse(JsonSerializer.Serialize(payload, s_jsonOptions));
        JsonElement root = payloadDocument.RootElement;

        await WriteJsonAsync(analysisPath, root.GetProperty("analysis"));
        foreach (JsonElement cause in root.GetProperty("causes").EnumerateArray())
        {
            string causeId = cause.GetProperty("id").GetString()!;
            await WriteJsonAsync(Path.Combine(causesDirectory, $"{causeId}.json"), cause);
        }
        foreach (JsonElement cause in root.GetProperty("priorCauses").EnumerateArray())
        {
            string causeId = cause.GetProperty("id").GetString()!;
            await WriteJsonAsync(Path.Combine(priorCausesDirectory, $"{causeId}.json"), cause);
        }
        await WriteJsonAsync(retryPatternsPath, root.GetProperty("retryPatterns"));
        string canonicalBetaPath = Path.Combine(priorCausesDirectory, $"{canonicalBetaId}.json");
        string canonicalBetaBefore = await File.ReadAllTextAsync(canonicalBetaPath);

        using NodeCommand command = new(_output, label: "rejectAliasCanonicalCollision");
        command.WithWorkingDirectory(_repoRoot).WithTimeout(TimeSpan.FromMinutes(1));

        CommandResult result = await command.ExecuteScriptAsync(
            _resolverPath,
            analysisPath,
            causesDirectory,
            priorCausesDirectory,
            retryPatternsPath);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("current batch also resolves to", result.Output, StringComparison.Ordinal);
        Assert.Equal(canonicalBetaBefore, await File.ReadAllTextAsync(canonicalBetaPath));
    }

    [Theory]
    [InlineData("legacy-root-alias")]
    [InlineData("Legacy_Sample")]
    [RequiresTools(["node"])]
    public async Task CommandLineConvergesLegacyNormalizedTestRootWithoutOverwritingCanonical(
        string aliasCauseId)
    {
        const string legacyCauseId = "Legacy.Sample";
        const string canonicalCauseId = "legacy-sample";
        const string testMethod = "Aspire.Sample.Tests.SampleTests.FlakyTest";
        string analysisPath = Path.Combine(_workspace.Path, "legacy-test-analysis.json");
        string causesDirectory = Directory.CreateDirectory(
            Path.Combine(_workspace.Path, "legacy-test-causes")).FullName;
        string priorCausesDirectory = Directory.CreateDirectory(
            Path.Combine(_workspace.Path, "legacy-test-prior-causes")).FullName;
        string retryPatternsPath = Path.Combine(_workspace.Path, "legacy-test-retry-patterns.json");
        object payload = CreateLegacyNormalizedSameTestPayload(
            reverse: false,
            parameterized: true,
            aliasCauseId: aliasCauseId);
        using JsonDocument payloadDocument = JsonDocument.Parse(
            JsonSerializer.Serialize(payload, s_jsonOptions));
        JsonElement root = payloadDocument.RootElement;

        await WriteJsonAsync(analysisPath, root.GetProperty("analysis"));
        foreach (JsonElement cause in root.GetProperty("causes").EnumerateArray())
        {
            string causeId = cause.GetProperty("id").GetString()!;
            await WriteJsonAsync(Path.Combine(causesDirectory, $"{causeId}.json"), cause);
        }
        foreach (JsonElement priorCause in root.GetProperty("priorCauses").EnumerateArray())
        {
            string priorCauseId = priorCause.GetProperty("id").GetString()!;
            await WriteJsonAsync(
                Path.Combine(priorCausesDirectory, $"{priorCauseId}.json"),
                priorCause);
        }
        await WriteJsonAsync(retryPatternsPath, root.GetProperty("retryPatterns"));

        using NodeCommand command = new(_output, label: "convergeLegacyNormalizedTestRoot");
        command.WithWorkingDirectory(_repoRoot).WithTimeout(TimeSpan.FromMinutes(1));

        CommandResult firstResult = await command.ExecuteScriptAsync(
            _resolverPath,
            analysisPath,
            causesDirectory,
            priorCausesDirectory,
            retryPatternsPath);
        firstResult.EnsureSuccessful();

        string canonicalPriorPath = Path.Combine(priorCausesDirectory, $"{canonicalCauseId}.json");
        string legacyPriorPath = Path.Combine(priorCausesDirectory, $"{legacyCauseId}.json");
        string aliasPriorPath = Path.Combine(priorCausesDirectory, $"{aliasCauseId}.json");
        using JsonDocument canonicalPrior = JsonDocument.Parse(await File.ReadAllTextAsync(canonicalPriorPath));
        Assert.False(canonicalPrior.RootElement.TryGetProperty("canonical_id", out _));
        Assert.Equal(
            "https://github.com/microsoft/aspire/issues/20",
            canonicalPrior.RootElement.GetProperty("issue_url").GetString());
        Assert.Equal(200, canonicalPrior.RootElement.GetProperty("occurrences")[0].GetProperty("run_id").GetInt32());

        using JsonDocument legacyPrior = JsonDocument.Parse(await File.ReadAllTextAsync(legacyPriorPath));
        Assert.Equal(canonicalCauseId, legacyPrior.RootElement.GetProperty("canonical_id").GetString());
        Assert.Equal(
            "https://github.com/microsoft/aspire/issues/10",
            legacyPrior.RootElement.GetProperty("issue_url").GetString());
        Assert.Equal(100, legacyPrior.RootElement.GetProperty("occurrences")[0].GetProperty("run_id").GetInt32());

        using JsonDocument aliasPrior = JsonDocument.Parse(await File.ReadAllTextAsync(aliasPriorPath));
        Assert.Equal(canonicalCauseId, aliasPrior.RootElement.GetProperty("canonical_id").GetString());
        Assert.Equal(150, aliasPrior.RootElement.GetProperty("occurrences")[0].GetProperty("run_id").GetInt32());

        string normalizedCausePath = Path.Combine(causesDirectory, $"{canonicalCauseId}.json");
        string firstNormalizedCause = await File.ReadAllTextAsync(normalizedCausePath);
        using (JsonDocument normalizedCause = JsonDocument.Parse(firstNormalizedCause))
        {
            Assert.Equal($"{testMethod}(value: 2)", normalizedCause.RootElement.GetProperty("test_name").GetString());
            Assert.Equal(
                [
                    $"{testMethod}(value: 1)",
                    $"{testMethod}(value: 2)",
                    $"{testMethod}(value: 3)"
                ],
                ReadStrings(normalizedCause.RootElement, "test_names"));
            Assert.Equal(
                new[] { legacyCauseId, aliasCauseId, "legacy-root-marker", "safe-root-marker" }
                    .Distinct()
                    .Order(),
                ReadStrings(normalizedCause.RootElement, "aliases").Order());
            Assert.Equal(
                "https://github.com/microsoft/aspire/issues/10",
                normalizedCause.RootElement.GetProperty("issue_url").GetString());
        }

        // The workflow persists the normalized cause by merging it into the existing physical
        // canonical record before a later run invokes the resolver again.
        await WriteJsonAsync(canonicalPriorPath, new
        {
            id = canonicalCauseId,
            type = "flaky-test",
            title = "Safe sample root",
            test_name = $"{testMethod}(value: 2)",
            test_names = new[]
            {
                $"{testMethod}(value: 1)",
                $"{testMethod}(value: 2)",
                $"{testMethod}(value: 3)"
            },
            error_pattern = "The safe sample root failed.",
            issue_url = "https://github.com/microsoft/aspire/issues/20",
            aliases = new[] { legacyCauseId, aliasCauseId, "legacy-root-marker", "safe-root-marker" }
                .Distinct()
                .Order()
                .ToArray(),
            occurrences = new[] { new { run_id = 200, observed_at = "2026-08-02T00:00:00Z" } }
        });
        string canonicalPriorAfterFirstRun = await File.ReadAllTextAsync(canonicalPriorPath);
        string legacyPriorAfterFirstRun = await File.ReadAllTextAsync(legacyPriorPath);
        string aliasPriorAfterFirstRun = await File.ReadAllTextAsync(aliasPriorPath);
        CommandResult secondResult = await command.ExecuteScriptAsync(
            _resolverPath,
            analysisPath,
            causesDirectory,
            priorCausesDirectory,
            retryPatternsPath);
        secondResult.EnsureSuccessful();

        Assert.Equal(firstNormalizedCause, await File.ReadAllTextAsync(normalizedCausePath));
        Assert.Equal(canonicalPriorAfterFirstRun, await File.ReadAllTextAsync(canonicalPriorPath));
        Assert.Equal(legacyPriorAfterFirstRun, await File.ReadAllTextAsync(legacyPriorPath));
        Assert.Equal(aliasPriorAfterFirstRun, await File.ReadAllTextAsync(aliasPriorPath));
        Assert.DoesNotContain("Migrated legacy cause", secondResult.Output, StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task CommandLineMigratesLegacyCauseFiles()
    {
        const string legacyCauseId = "processguest-signalerThrows_treekill-timeout";
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
        Assert.False(migratedCause.TryGetProperty("canonical_id", out _));
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
        string persistenceScript = File.ReadAllText(
            Path.Combine(_repoRoot, ".github", "workflows", "analyze-ci-failure-persistence.sh"));

        int publishJobIndex = workflow.IndexOf("publish-data:", StringComparison.Ordinal);
        int checkoutIndex = workflow.IndexOf("- name: Checkout publication helpers", publishJobIndex, StringComparison.Ordinal);
        int artifactDownloadIndex = workflow.IndexOf("- uses: actions/download-artifact@v4", publishJobIndex, StringComparison.Ordinal);
        int resolverIndex = workflow.IndexOf(
            "node .github/workflows/analyze-ci-failure-cause-resolver.js",
            publishJobIndex,
            StringComparison.Ordinal);
        int persistenceIndex = workflow.IndexOf(
            "analyze-ci-failure-persistence.sh write-run-summary",
            publishJobIndex,
            StringComparison.Ordinal);
        int memoryPushIndex = workflow.IndexOf("git -C memory-repo push origin \"HEAD:$MEMORY_BRANCH\"", StringComparison.Ordinal);
        int issuePublicationIndex = workflow.IndexOf("- name: Publish cause issues", StringComparison.Ordinal);
        int commentStepIndex = workflow.IndexOf("- name: Comment on pull request", StringComparison.Ordinal);
        int resolverFailureStepIndex = workflow.IndexOf("- name: Report cause resolver failure", StringComparison.Ordinal);

        Assert.True(resolverIndex >= 0, "The publish job must invoke the deterministic cause resolver.");
        Assert.True(checkoutIndex < artifactDownloadIndex, "Checkout must not delete the downloaded trusted run artifacts.");
        Assert.True(persistenceIndex > resolverIndex, "Cause identities must be normalized before the run summary is persisted.");
        Assert.Contains("group: analyze-ci-failure", workflow, StringComparison.Ordinal);
        Assert.Contains("queue: max", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "($existing | del(.job_ids, .job_names, .aliases, .test_names)) *",
            persistenceScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "[($existing.occurrences // [])[], ($new.occurrences // [])[]]",
            persistenceScript,
            StringComparison.Ordinal);
        Assert.True(memoryPushIndex < issuePublicationIndex, "Canonical cause identities must be pushed before issue side effects.");
        Assert.Contains("node .github/workflows/analyze-ci-failure-cause-resolver.js \\", workflow, StringComparison.Ordinal);
        Assert.Contains("|| RESOLVER_STATUS=$?", workflow, StringComparison.Ordinal);
        Assert.Contains("if [ \"$RESOLVER_STATUS\" -eq 0 ]; then", workflow, StringComparison.Ordinal);
        Assert.Contains("echo \"resolver_status=${RESOLVER_STATUS}\" >> \"$GITHUB_OUTPUT\"", workflow, StringComparison.Ordinal);
        Assert.True(commentStepIndex > issuePublicationIndex, "PR comments must run after cause issue side effects.");
        Assert.True(
            resolverFailureStepIndex > commentStepIndex,
            "Resolver failures must be reported only after the independent PR analysis comment step.");
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

    private async Task<CommandResult> ExecuteHarnessAsync(
        object payload,
        string operation = "resolveCauses")
    {
        string inputPath = Path.Combine(_workspace.Path, $"{Guid.NewGuid():N}.json");
        string requestJson = JsonSerializer.Serialize(new HarnessRequest
        {
            Operation = operation,
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

    private static object CreateRetryPatternPayload(
        string causeId,
        object retryPattern,
        object[]? priorCauses = null)
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
            priorCauses = priorCauses ?? [],
            retryPatterns = new { jobFailurePatterns = new[] { retryPattern } }
        };

    private static object CreateAliasCanonicalCollisionPayload(string canonicalBetaId = "canonical-beta")
        => new
        {
            analysis = new
            {
                causes = new[] { "canonical-beta", "current-beta" },
                failed_jobs = new[]
                {
                    new
                    {
                        id = 1,
                        name = "Build / Alpha",
                        classification = "transient-infra",
                        reason = "Alpha failure token"
                    },
                    new
                    {
                        id = 2,
                        name = "Build / Beta",
                        classification = "transient-infra",
                        reason = "Beta failure token"
                    }
                },
                failed_tests = Array.Empty<object>()
            },
            causes = new[]
            {
                new
                {
                    id = "canonical-beta",
                    type = "infra-failure",
                    title = "Alpha failure token",
                    error_pattern = "Alpha failure token",
                    job_ids = new[] { 1 }
                },
                new
                {
                    id = "current-beta",
                    type = "infra-failure",
                    title = "Beta failure token",
                    error_pattern = "Beta failure token",
                    job_ids = new[] { 2 }
                }
            },
            priorCauses = new[]
            {
                new
                {
                    id = "canonical-alpha",
                    type = "infra-failure",
                    title = "Canonical alpha",
                    error_pattern = "Alpha failure token",
                    matchers = new[] { new { kind = "error-literal", value = "Alpha failure token" } }
                },
                new
                {
                    id = canonicalBetaId,
                    type = "infra-failure",
                    title = "Canonical beta",
                    error_pattern = "Beta failure token",
                    matchers = new[] { new { kind = "error-literal", value = "Beta failure token" } }
                }
            },
            retryPatterns = new { jobFailurePatterns = Array.Empty<object>() }
        };

    private static object CreateFreshSameTestPayload(bool reverse)
    {
        const string alphaTestName = "Aspire.Tests.SampleTheory(value: 1)";
        const string zetaTestName = "Aspire.Tests.SampleTheory(value: 2)";
        object[] causes =
        [
            new
            {
                id = "sample-theory-alpha",
                type = "flaky-test",
                title = "Alpha theory failure",
                test_name = alphaTestName,
                error_pattern = "Alpha failure",
                job_ids = new[] { 101 }
            },
            new
            {
                id = "sample-theory-zeta",
                type = "flaky-test",
                title = "Zeta theory failure",
                test_name = zetaTestName,
                error_pattern = "Zeta failure",
                job_ids = new[] { 202 }
            }
        ];
        object[] failedJobs =
        [
            new
            {
                id = 101,
                name = "Tests / Linux",
                classification = "flaky-test",
                reason = "Alpha failure"
            },
            new
            {
                id = 202,
                name = "Tests / Windows",
                classification = "flaky-test",
                reason = "Zeta failure"
            }
        ];
        object[] failedTests =
        [
            new
            {
                name = alphaTestName,
                job = "Tests / Linux",
                error = "Alpha failure",
                stack_trace = string.Empty
            },
            new
            {
                name = zetaTestName,
                job = "Tests / Windows",
                error = "Zeta failure",
                stack_trace = string.Empty
            }
        ];

        if (reverse)
        {
            Array.Reverse(causes);
        }

        return new
        {
            analysis = new
            {
                causes = reverse
                    ? new[] { "sample-theory-zeta", "sample-theory-alpha" }
                    : new[] { "sample-theory-alpha", "sample-theory-zeta" },
                failed_jobs = failedJobs,
                failed_tests = failedTests
            },
            causes,
            priorCauses = Array.Empty<object>(),
            retryPatterns = new { jobFailurePatterns = Array.Empty<object>() }
        };
    }

    private static object CreateAuthoritativeSameTestPayload(bool reverse)
    {
        const string canonicalTestName = "Aspire.Tests.SampleTheory(value: 1)";
        const string freshTestName = "Aspire.Tests.SampleTheory(value: 2)";
        const string freshZetaTestName = "Aspire.Tests.SampleTheory(value: 3)";
        object[] causes =
        [
            new
            {
                id = "canonical-sample-test",
                type = "flaky-test",
                title = "Current canonical sample test",
                test_name = canonicalTestName,
                error_pattern = "Canonical failure",
                job_ids = new[] { 101 }
            },
            new
            {
                id = "fresh-sample-test",
                type = "flaky-test",
                title = "Fresh sample test",
                test_name = freshTestName,
                error_pattern = "Fresh failure",
                job_ids = new[] { 202 }
            },
            new
            {
                id = "fresh-zeta-sample-test",
                type = "flaky-test",
                title = "Fresh zeta sample test",
                test_name = freshZetaTestName,
                error_pattern = "Fresh zeta failure",
                job_ids = new[] { 303 }
            }
        ];
        if (reverse)
        {
            Array.Reverse(causes);
        }

        return new
        {
            analysis = new
            {
                causes = reverse
                    ? new[] { "fresh-zeta-sample-test", "fresh-sample-test", "canonical-sample-test" }
                    : new[] { "canonical-sample-test", "fresh-sample-test", "fresh-zeta-sample-test" },
                failed_jobs = new object[]
                {
                    new
                    {
                        id = 101,
                        name = "Tests / Linux",
                        classification = "flaky-test",
                        reason = "Canonical failure"
                    },
                    new
                    {
                        id = 202,
                        name = "Tests / Windows",
                        classification = "flaky-test",
                        reason = "Fresh failure"
                    },
                    new
                    {
                        id = 303,
                        name = "Tests / macOS",
                        classification = "flaky-test",
                        reason = "Fresh zeta failure"
                    }
                },
                failed_tests = new object[]
                {
                    new
                    {
                        name = canonicalTestName,
                        job = "Tests / Linux",
                        error = "Canonical failure",
                        stack_trace = string.Empty
                    },
                    new
                    {
                        name = freshTestName,
                        job = "Tests / Windows",
                        error = "Fresh failure",
                        stack_trace = string.Empty
                    },
                    new
                    {
                        name = freshZetaTestName,
                        job = "Tests / macOS",
                        error = "Fresh zeta failure",
                        stack_trace = string.Empty
                    }
                }
            },
            causes,
            priorCauses = new[]
            {
                new
                {
                    id = "canonical-sample-test",
                    type = "flaky-test",
                    title = "Stored sample test",
                    error_pattern = "Stored failure",
                    issue_url = "https://github.com/microsoft/aspire/issues/100"
                }
            },
            retryPatterns = new { jobFailurePatterns = Array.Empty<object>() }
        };
    }

    private static object CreateHistoricalSameTestPayload(
        bool reverse,
        string proposedCauseId = "current-sample-test")
    {
        const string testName = "Aspire.Sample.Tests.SampleTests.FlakyTest";
        object[] priorCauses =
        [
            new
            {
                id = "newer-sample-alias",
                canonical_id = "newer-sample-test",
                type = "flaky-test"
            },
            new
            {
                id = "newer-sample-test",
                type = "flaky-test",
                title = "Newer sample test",
                test_name = testName,
                error_pattern = "The newer sample test failed.",
                issue_url = "https://github.com/microsoft/aspire/issues/200",
                aliases = new[] { "newer-sample-marker" },
                occurrences = new[] { new { observed_at = "2026-08-02T00:00:00Z" } }
            },
            new
            {
                id = "oldest-sample-test",
                type = "flaky-test",
                title = "Oldest sample test",
                test_name = testName,
                error_pattern = "The oldest sample test failed.",
                aliases = new[] { "oldest-sample-marker" },
                occurrences = new[] { new { observed_at = "2026-08-01T00:00:00Z" } }
            }
        ];
        if (reverse)
        {
            Array.Reverse(priorCauses);
        }

        return new
        {
            analysis = new
            {
                causes = new[] { proposedCauseId },
                failed_jobs = new[]
                {
                    new
                    {
                        id = 1,
                        name = "Tests / Sample",
                        classification = "flaky-test",
                        reason = "The current sample test failed."
                    }
                },
                failed_tests = new[]
                {
                    new
                    {
                        name = testName,
                        job = "Tests / Sample",
                        error = "The current sample test failed.",
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
                    title = "Current sample test",
                    test_name = testName,
                    error_pattern = "The current sample test failed.",
                    job_ids = new[] { 1 }
                }
            },
            priorCauses,
            retryPatterns = new { jobFailurePatterns = Array.Empty<object>() }
        };
    }

    private static object CreateLegacyNormalizedSameTestPayload(
        bool reverse,
        bool parameterized = false,
        string aliasCauseId = "legacy-root-alias")
    {
        const string testMethod = "Aspire.Sample.Tests.SampleTests.FlakyTest";
        string legacyTestName = parameterized ? $"{testMethod}(value: 1)" : testMethod;
        string canonicalTestName = parameterized ? $"{testMethod}(value: 2)" : testMethod;
        string currentTestName = parameterized ? $"{testMethod}(value: 3)" : testMethod;
        object[] priorCauses =
        [
            new
            {
                id = aliasCauseId,
                canonical_id = "Legacy.Sample",
                type = "flaky-test",
                occurrences = new[] { new { run_id = 150, observed_at = "2026-08-01T12:00:00Z" } }
            },
            new
            {
                id = "legacy-sample",
                type = "flaky-test",
                title = "Safe sample root",
                test_name = canonicalTestName,
                error_pattern = "The safe sample root failed.",
                issue_url = "https://github.com/microsoft/aspire/issues/20",
                aliases = new[] { "safe-root-marker" },
                occurrences = new[] { new { run_id = 200, observed_at = "2026-08-02T00:00:00Z" } }
            },
            new
            {
                id = "Legacy.Sample",
                type = "flaky-test",
                title = "Legacy sample root",
                test_name = legacyTestName,
                error_pattern = "The legacy sample root failed.",
                issue_url = "https://github.com/microsoft/aspire/issues/10",
                aliases = new[] { "legacy-root-marker" },
                occurrences = new[] { new { run_id = 100, observed_at = "2026-08-01T00:00:00Z" } }
            }
        ];
        if (reverse)
        {
            Array.Reverse(priorCauses);
        }

        return new
        {
            analysis = new
            {
                causes = new[] { "current-sample-test" },
                failed_jobs = new[]
                {
                    new
                    {
                        id = 1,
                        name = "Tests / Sample",
                        classification = "flaky-test",
                        reason = "The current sample test failed."
                    }
                },
                failed_tests = new[]
                {
                    new
                    {
                        name = currentTestName,
                        job = "Tests / Sample",
                        error = "The current sample test failed.",
                        stack_trace = string.Empty
                    }
                }
            },
            causes = new[]
            {
                new
                {
                    id = "current-sample-test",
                    type = "flaky-test",
                    title = "Current sample test",
                    test_name = currentTestName,
                    error_pattern = "The current sample test failed.",
                    job_ids = new[] { 1 }
                }
            },
            priorCauses,
            retryPatterns = new { jobFailurePatterns = Array.Empty<object>() }
        };
    }

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
