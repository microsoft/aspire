// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// End-to-end tests for the CreateFailingTestIssue tool.
/// </summary>
// These tests build and run repository tools in child dotnet processes. Keep them in the same
// non-parallel collection as the other tool tests so concurrent child builds don't race in artifacts/obj.
[Collection(ToolBuildCollection.Name)]
public sealed class CreateFailingTestIssueToolTests : IClassFixture<CreateFailingTestIssueFixture>, IDisposable
{
    private readonly TestTempDirectory _tempDirectory = new();
    private readonly CreateFailingTestIssueFixture _fixture;
    private readonly ITestOutputHelper _output;

    public CreateFailingTestIssueToolTests(CreateFailingTestIssueFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public void Dispose() => _tempDirectory.Dispose();

    [Fact]
    public async Task ResolvesFailureFromFixtureArtifacts()
    {
        var fixtureDirectory = CreateFixtureDirectory();

        var result = await RunToolAsync(
            fixtureDirectory,
            "--test", "Tests.Namespace.Type.Method",
            "--url", "https://github.com/microsoft/aspire/actions/runs/123",
            "--workflow", "ci",
            "--repo", "microsoft/aspire");

        Assert.Equal(0, result.ExitCode);

        var response = JsonSerializer.Deserialize<CreateFailingTestIssueResponse>(result.Output, JsonOptions);
        Assert.NotNull(response);
        Assert.True(response!.Success);
        Assert.NotNull(response.Match);
        Assert.Equal("exactCanonical", response.Match!.Strategy);
        Assert.Equal("Tests.Namespace.Type.Method", response.Match.CanonicalTestName);
        Assert.Equal("Tests.Namespace.Type.Method(input: 1)", response.Match.DisplayTestName);
        Assert.NotNull(response.Failure);
        Assert.Equal("Expected 1 but found 2.", response.Failure!.ErrorMessage);
        Assert.Equal("at Tests.Namespace.Type.Method() in TestFile.cs:line 42", response.Failure.StackTrace);
        Assert.Equal("stdout line 1", response.Failure.Stdout);
        Assert.NotNull(response.Issue);
        Assert.Contains("### Build information", response.Issue!.Body, StringComparison.Ordinal);
        Assert.Contains("Build: https://github.com/microsoft/aspire/actions/runs/123", response.Issue.Body, StringComparison.Ordinal);
        Assert.Contains("Build error leg or test failing: Tests.Namespace.Type.Method", response.Issue.Body, StringComparison.Ordinal);
        Assert.Contains("### Fill in the error message template", response.Issue.Body, StringComparison.Ordinal);
        Assert.Contains(@"""ErrorMessage"": ""Expected 1 but found 2.""", response.Issue.Body, StringComparison.Ordinal);
        Assert.Contains("### Error details", response.Issue.Body, StringComparison.Ordinal);
        Assert.Contains("Error Message: Expected 1 but found 2.", response.Issue.Body, StringComparison.Ordinal);
        Assert.Contains("Stack Trace:", response.Issue.Body, StringComparison.Ordinal);
        Assert.Contains("<summary>Standard Output</summary>", response.Issue.Body, StringComparison.Ordinal);
        Assert.Contains("stdout line 1", response.Issue.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("### Other info", response.Issue.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Pull Request:", response.Issue.Body, StringComparison.Ordinal);
        Assert.StartsWith("<!-- failing-test-signature: v1:", response.Issue!.MetadataMarker, StringComparison.Ordinal);
        Assert.Single(response.Resolution!.JobUrls);
        Assert.NotNull(response.Diagnostics);
        Assert.Equal("diagnostics.log", response.Diagnostics!.LogFile);
        Assert.DoesNotContain("\"log\":", result.Output, StringComparison.OrdinalIgnoreCase);
        var diagnosticsLog = File.ReadAllText(Path.Combine(fixtureDirectory, "diagnostics.log"));
        Assert.Contains("Resolved workflow selector 'ci' to '.github/workflows/ci.yml'.", diagnosticsLog, StringComparison.Ordinal);
        Assert.Contains("Matched query using 'exactCanonical'.", diagnosticsLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateFlagCreatesIssueOnGitHub()
    {
        var fixtureDirectory = CreateFixtureDirectory();

        // Provide search-issue fixture that returns no match so tool creates a new issue
        File.WriteAllText(
            Path.Combine(fixtureDirectory, "search-issue.json"),
            "{}");

        // Write a fixture response for the gh issue create call
        File.WriteAllText(
            Path.Combine(fixtureDirectory, "create-issue.json"),
            """
            {
              "number": 99999,
              "url": "https://github.com/microsoft/aspire/issues/99999"
            }
            """);

        var result = await RunToolAsync(
            fixtureDirectory,
            "--test", "Tests.Namespace.Type.Method",
            "--url", "https://github.com/microsoft/aspire/actions/runs/123",
            "--workflow", "ci",
            "--repo", "microsoft/aspire",
            "--create");

        Assert.Equal(0, result.ExitCode);

        var response = JsonSerializer.Deserialize<CreateFailingTestIssueResponse>(result.Output, JsonOptions);
        Assert.NotNull(response);
        Assert.True(response!.Success);
        Assert.NotNull(response.Issue);
        Assert.NotNull(response.Issue!.CreatedIssue);
        Assert.Equal(99999, response.Issue.CreatedIssue!.Number);
        Assert.Equal("https://github.com/microsoft/aspire/issues/99999", response.Issue.CreatedIssue.Url);
    }

    [Fact]
    public async Task CreateFlagReusesOpenExistingIssue()
    {
        var fixtureDirectory = CreateFixtureDirectory();

        // Provide search-issue fixture that returns an open issue
        File.WriteAllText(
            Path.Combine(fixtureDirectory, "search-issue.json"),
            """
            {
              "number": 55555,
              "url": "https://github.com/microsoft/aspire/issues/55555",
              "state": "open"
            }
            """);

        // Stub the add-issue-comment call
        File.WriteAllText(Path.Combine(fixtureDirectory, "add-issue-comment.json"), "{}");

        var result = await RunToolAsync(
            fixtureDirectory,
            "--test", "Tests.Namespace.Type.Method",
            "--url", "https://github.com/microsoft/aspire/actions/runs/123",
            "--workflow", "ci",
            "--repo", "microsoft/aspire",
            "--create");

        Assert.Equal(0, result.ExitCode);

        var response = JsonSerializer.Deserialize<CreateFailingTestIssueResponse>(result.Output, JsonOptions);
        Assert.NotNull(response);
        Assert.True(response!.Success);
        Assert.NotNull(response.Issue);
        Assert.NotNull(response.Issue!.ExistingIssue);
        Assert.Equal(55555, response.Issue.ExistingIssue!.Number);
        Assert.Null(response.Issue.CreatedIssue);

        var diagnosticsLog = File.ReadAllText(Path.Combine(fixtureDirectory, "diagnostics.log"));
        Assert.Contains("Found open issue #55555", diagnosticsLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateFlagReopensClosedExistingIssue()
    {
        var fixtureDirectory = CreateFixtureDirectory();

        // Provide search-issue fixture that returns a closed issue
        File.WriteAllText(
            Path.Combine(fixtureDirectory, "search-issue.json"),
            """
            {
              "number": 44444,
              "url": "https://github.com/microsoft/aspire/issues/44444",
              "state": "closed"
            }
            """);

        // Stub the reopen-issue and add-issue-comment calls
        File.WriteAllText(Path.Combine(fixtureDirectory, "reopen-issue.json"), "{}");
        File.WriteAllText(Path.Combine(fixtureDirectory, "add-issue-comment.json"), "{}");

        var result = await RunToolAsync(
            fixtureDirectory,
            "--test", "Tests.Namespace.Type.Method",
            "--url", "https://github.com/microsoft/aspire/actions/runs/123",
            "--workflow", "ci",
            "--repo", "microsoft/aspire",
            "--create");

        Assert.Equal(0, result.ExitCode);

        var response = JsonSerializer.Deserialize<CreateFailingTestIssueResponse>(result.Output, JsonOptions);
        Assert.NotNull(response);
        Assert.True(response!.Success);
        Assert.NotNull(response.Issue);
        Assert.NotNull(response.Issue!.ExistingIssue);
        Assert.Equal(44444, response.Issue.ExistingIssue!.Number);
        Assert.Null(response.Issue.CreatedIssue);

        var diagnosticsLog = File.ReadAllText(Path.Combine(fixtureDirectory, "diagnostics.log"));
        Assert.Contains("Found closed issue #44444. Reopening...", diagnosticsLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateFlagCreatesNewIssueWhenNoExistingIssueFound()
    {
        var fixtureDirectory = CreateFixtureDirectory();

        // Provide search-issue fixture that returns no match (empty object, no "number")
        File.WriteAllText(
            Path.Combine(fixtureDirectory, "search-issue.json"),
            "{}");

        // Provide create-issue fixture
        File.WriteAllText(
            Path.Combine(fixtureDirectory, "create-issue.json"),
            """
            {
              "number": 88888,
              "url": "https://github.com/microsoft/aspire/issues/88888"
            }
            """);

        var result = await RunToolAsync(
            fixtureDirectory,
            "--test", "Tests.Namespace.Type.Method",
            "--url", "https://github.com/microsoft/aspire/actions/runs/123",
            "--workflow", "ci",
            "--repo", "microsoft/aspire",
            "--create");

        Assert.Equal(0, result.ExitCode);

        var response = JsonSerializer.Deserialize<CreateFailingTestIssueResponse>(result.Output, JsonOptions);
        Assert.NotNull(response);
        Assert.True(response!.Success);
        Assert.NotNull(response.Issue);
        Assert.Null(response.Issue!.ExistingIssue);
        Assert.NotNull(response.Issue.CreatedIssue);
        Assert.Equal(88888, response.Issue.CreatedIssue!.Number);

        var diagnosticsLog = File.ReadAllText(Path.Combine(fixtureDirectory, "diagnostics.log"));
        Assert.Contains("No existing issue found. Creating new issue on GitHub...", diagnosticsLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForceNewFlagSkipsSearchAndCreatesNewIssue()
    {
        var fixtureDirectory = CreateFixtureDirectory();

        // Even though search-issue fixture returns an existing issue, --force-new should skip it
        File.WriteAllText(
            Path.Combine(fixtureDirectory, "search-issue.json"),
            """
            {
              "number": 55555,
              "url": "https://github.com/microsoft/aspire/issues/55555",
              "state": "open"
            }
            """);

        File.WriteAllText(
            Path.Combine(fixtureDirectory, "create-issue.json"),
            """
            {
              "number": 77777,
              "url": "https://github.com/microsoft/aspire/issues/77777"
            }
            """);

        var result = await RunToolAsync(
            fixtureDirectory,
            "--test", "Tests.Namespace.Type.Method",
            "--url", "https://github.com/microsoft/aspire/actions/runs/123",
            "--workflow", "ci",
            "--repo", "microsoft/aspire",
            "--create",
            "--force-new");

        Assert.Equal(0, result.ExitCode);

        var response = JsonSerializer.Deserialize<CreateFailingTestIssueResponse>(result.Output, JsonOptions);
        Assert.NotNull(response);
        Assert.True(response!.Success);
        Assert.NotNull(response.Issue);
        Assert.Null(response.Issue!.ExistingIssue);
        Assert.NotNull(response.Issue.CreatedIssue);
        Assert.Equal(77777, response.Issue.CreatedIssue!.Number);

        var diagnosticsLog = File.ReadAllText(Path.Combine(fixtureDirectory, "diagnostics.log"));
        Assert.Contains("--force-new flag set. Creating new issue on GitHub...", diagnosticsLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolvesRunFromPullRequestUrlWithCompletedRun()
    {
        var fixtureDirectory = CreateFixtureDirectoryWithPrResolution(
            pullRequestNumber: 500,
            headSha: "abc123def456",
            runStatus: "completed");

        var result = await RunToolAsync(
            fixtureDirectory,
            "--test", "Tests.Namespace.Type.Method",
            "--url", "https://github.com/microsoft/aspire/pull/500",
            "--workflow", "ci",
            "--repo", "microsoft/aspire");

        Assert.Equal(0, result.ExitCode);

        var response = JsonSerializer.Deserialize<CreateFailingTestIssueResponse>(result.Output, JsonOptions);
        Assert.NotNull(response);
        Assert.True(response!.Success);
        Assert.NotNull(response.Resolution);
        Assert.Equal("pull_request", response.Resolution!.SourceKind);
        Assert.Equal(500, response.Resolution.PullRequestNumber);
        Assert.Contains("Build: https://github.com/microsoft/aspire/actions/runs/123", response.Issue!.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolvesRunFromPullRequestUrlFallsBackToInProgressRun()
    {
        var fixtureDirectory = CreateFixtureDirectoryWithPrResolution(
            pullRequestNumber: 600,
            headSha: "def789abc012",
            runStatus: "in_progress");

        var result = await RunToolAsync(
            fixtureDirectory,
            "--test", "Tests.Namespace.Type.Method",
            "--url", "https://github.com/microsoft/aspire/pull/600",
            "--workflow", "ci",
            "--repo", "microsoft/aspire");

        Assert.Equal(0, result.ExitCode);

        var response = JsonSerializer.Deserialize<CreateFailingTestIssueResponse>(result.Output, JsonOptions);
        Assert.NotNull(response);
        Assert.True(response!.Success);
        Assert.NotNull(response.Resolution);
        Assert.Equal("pull_request", response.Resolution!.SourceKind);
        Assert.Equal(600, response.Resolution.PullRequestNumber);
    }

    [Fact]
    public async Task WithoutCreateFlagDoesNotCreateIssue()
    {
        var fixtureDirectory = CreateFixtureDirectory();

        var result = await RunToolAsync(
            fixtureDirectory,
            "--test", "Tests.Namespace.Type.Method",
            "--url", "https://github.com/microsoft/aspire/actions/runs/123",
            "--workflow", "ci",
            "--repo", "microsoft/aspire");

        Assert.Equal(0, result.ExitCode);

        var response = JsonSerializer.Deserialize<CreateFailingTestIssueResponse>(result.Output, JsonOptions);
        Assert.NotNull(response);
        Assert.True(response!.Success);
        Assert.NotNull(response.Issue);
        Assert.Null(response.Issue!.CreatedIssue);
    }

    [Fact]
    public async Task ReturnsAvailableFailedTestsWhenNoMatchExists()
    {
        var fixtureDirectory = CreateFixtureDirectory();

        var result = await RunToolAsync(
            fixtureDirectory,
            "--test", "Missing.Test",
            "--url", "https://github.com/microsoft/aspire/actions/runs/123",
            "--workflow", "ci",
            "--repo", "microsoft/aspire");

        Assert.NotEqual(0, result.ExitCode);

        var response = JsonSerializer.Deserialize<CreateFailingTestIssueResponse>(result.Output, JsonOptions);
        Assert.NotNull(response);
        Assert.False(response!.Success);
        Assert.Contains("did not match", response.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(response.Diagnostics);
        Assert.Contains("Tests.Namespace.Type.Method | Tests.Namespace.Type.Method(input: 1)", response.Diagnostics!.AvailableFailedTests);
    }

    [Fact]
    public async Task ReturnsAllFailingTestsWhenNoTestQueryIsProvided()
    {
        var fixtureDirectory = CreateFixtureDirectory(
            new TestTrxCase(
                CanonicalTestName: "Tests.Namespace.Type.Method",
                DisplayName: "Tests.Namespace.Type.Method(input: 1)",
                Outcome: "Failed",
                ErrorMessage: "Expected 1 but found 2.",
                StackTrace: "at Tests.Namespace.Type.Method() in TestFile.cs:line 42",
                StdOut: "stdout line 1"),
            new TestTrxCase(
                CanonicalTestName: "Tests.Namespace.OtherType.OtherMethod",
                DisplayName: "Tests.Namespace.OtherType.OtherMethod",
                Outcome: "Failed",
                ErrorMessage: "Expected true.",
                StackTrace: "at Tests.Namespace.OtherType.OtherMethod() in OtherTestFile.cs:line 10",
                StdOut: "stdout line 2"));

        var result = await RunToolAsync(
            fixtureDirectory,
            "--url", "https://github.com/microsoft/aspire/actions/runs/123",
            "--workflow", "ci",
            "--repo", "microsoft/aspire");

        Assert.Equal(0, result.ExitCode);

        var response = JsonSerializer.Deserialize<CreateFailingTestIssueResponse>(result.Output, JsonOptions);
        Assert.NotNull(response);
        Assert.True(response!.Success);
        Assert.Null(response.Match);
        Assert.Null(response.Failure);
        Assert.Null(response.Issue);
        Assert.NotNull(response.AllFailures);
        Assert.Equal(2, response.AllFailures!.FailedTests);

        var firstFailure = Assert.Single(response.AllFailures.Tests, static test => test.CanonicalTestName == "Tests.Namespace.OtherType.OtherMethod");
        Assert.Equal("Tests.Namespace.OtherType.OtherMethod", firstFailure.DisplayTestName);
        Assert.Equal(1, firstFailure.OccurrenceCount);
        Assert.Equal("Expected true.", firstFailure.PrimaryFailure.ErrorMessage);

        var secondFailure = Assert.Single(response.AllFailures.Tests, static test => test.CanonicalTestName == "Tests.Namespace.Type.Method");
        Assert.Equal("Tests.Namespace.Type.Method(input: 1)", secondFailure.DisplayTestName);
        Assert.Equal(1, secondFailure.OccurrenceCount);
        Assert.Equal("Expected 1 but found 2.", secondFailure.PrimaryFailure.ErrorMessage);
        Assert.Contains("Tests.Namespace.OtherType.OtherMethod", response.Diagnostics!.AvailableFailedTests);
        Assert.Contains("Tests.Namespace.Type.Method | Tests.Namespace.Type.Method(input: 1)", response.Diagnostics.AvailableFailedTests);

        var diagnosticsLog = File.ReadAllText(Path.Combine(fixtureDirectory, "diagnostics.log"));
        Assert.Contains("No test query was supplied; returning all 2 failing test(s).", diagnosticsLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NormalizesCanonicalNameWhenTrxStoresFullyQualifiedMethodName()
    {
        var fixtureDirectory = CreateFixtureDirectory(
            new TestTrxCase(
                CanonicalTestName: "Tests.Namespace.Type.Method",
                DisplayName: "Tests.Namespace.Type.Method",
                Outcome: "Failed",
                ErrorMessage: "Expected 1 but found 2.",
                StackTrace: "at Tests.Namespace.Type.Method() in TestFile.cs:line 42",
                StdOut: "stdout line 1",
                TestMethodName: "Tests.Namespace.Type.Method"));

        var result = await RunToolAsync(
            fixtureDirectory,
            "--test", "Tests.Namespace.Type.Method",
            "--url", "https://github.com/microsoft/aspire/actions/runs/123",
            "--workflow", "ci",
            "--repo", "microsoft/aspire");

        Assert.Equal(0, result.ExitCode);

        var response = JsonSerializer.Deserialize<CreateFailingTestIssueResponse>(result.Output, JsonOptions);
        Assert.NotNull(response);
        Assert.True(response!.Success);
        Assert.NotNull(response.Match);
        Assert.Equal("Tests.Namespace.Type.Method", response.Match!.CanonicalTestName);
        Assert.DoesNotContain("Tests.Namespace.Type.Tests.Namespace.Type.Method", response.Issue!.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SupportsAttemptJobUrlsWithQueryString()
    {
        var fixtureDirectory = CreateFixtureDirectory(runAttempt: 2);

        var result = await RunToolAsync(
            fixtureDirectory,
            "--test", "Tests.Namespace.Type.Method",
            "--url", "https://github.com/microsoft/aspire/actions/runs/123/attempts/2/job/456?pr=321",
            "--workflow", "ci",
            "--repo", "microsoft/aspire");

        Assert.Equal(0, result.ExitCode);

        var response = JsonSerializer.Deserialize<CreateFailingTestIssueResponse>(result.Output, JsonOptions);
        Assert.NotNull(response);
        Assert.True(response!.Success);
        Assert.NotNull(response.Resolution);
        Assert.Equal("job", response.Resolution!.SourceKind);
        Assert.Equal(2, response.Resolution.RunAttempt);
        Assert.Equal("https://github.com/microsoft/aspire/actions/runs/123/attempts/2", response.Resolution.RunUrl);
        Assert.Contains("Build: https://github.com/microsoft/aspire/actions/runs/123/attempts/2", response.Issue!.Body, StringComparison.Ordinal);
        Assert.Contains("Build error leg or test failing: Tests.Namespace.Type.Method", response.Issue.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllowsQueuedRunsWhenArtifactsAlreadyExist()
    {
        var fixtureDirectory = CreateFixtureDirectory(runStatus: "queued", artifactName: "All-TestResults");

        var result = await RunToolAsync(
            fixtureDirectory,
            "--test", "Tests.Namespace.Type.Method",
            "--url", "https://github.com/microsoft/aspire/actions/runs/123",
            "--workflow", "ci",
            "--repo", "microsoft/aspire");

        Assert.Equal(0, result.ExitCode);

        var response = JsonSerializer.Deserialize<CreateFailingTestIssueResponse>(result.Output, JsonOptions);
        Assert.NotNull(response);
        Assert.True(response!.Success);
        Assert.NotNull(response.Diagnostics);
        Assert.Contains("not completed yet (status: queued)", response.Diagnostics!.Warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task SkipsMissingFailedJobLogsWhenArtifactsStillResolveFailure()
    {
        var fixtureDirectory = CreateFixtureDirectory(artifactName: "All-TestResults", missingJobLog: true);

        var result = await RunToolAsync(
            fixtureDirectory,
            "--test", "Tests.Namespace.Type.Method",
            "--url", "https://github.com/microsoft/aspire/actions/runs/123",
            "--workflow", "ci",
            "--repo", "microsoft/aspire");

        Assert.Equal(0, result.ExitCode);

        var response = JsonSerializer.Deserialize<CreateFailingTestIssueResponse>(result.Output, JsonOptions);
        Assert.NotNull(response);
        Assert.True(response!.Success);
        Assert.NotNull(response.Diagnostics);
        Assert.Contains("Failed job log was unavailable for 'Tests / Linux / Sample' (456); continuing without it.", response.Diagnostics!.Warnings);
        var diagnosticsLog = File.ReadAllText(Path.Combine(fixtureDirectory, "diagnostics.log"));
        Assert.Contains("Skipping missing log for failed job 'Tests / Linux / Sample' (456).", diagnosticsLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolvesFailureFromQueuedRunLogsWhenNoTestResultsArtifactsExist()
    {
        var fixtureDirectory = CreateFixtureDirectory(runStatus: "queued");

        var result = await RunToolAsync(
            fixtureDirectory,
            "--test", "Tests.Namespace.Type.Method",
            "--url", "https://github.com/microsoft/aspire/actions/runs/123",
            "--workflow", "ci",
            "--repo", "microsoft/aspire");

        Assert.Equal(0, result.ExitCode);

        var response = JsonSerializer.Deserialize<CreateFailingTestIssueResponse>(result.Output, JsonOptions);
        Assert.NotNull(response);
        Assert.True(response!.Success);
        Assert.NotNull(response.Failure);
        Assert.Equal("Expected 1 but found 2.", response.Failure!.ErrorMessage);
        Assert.Contains("at Tests.Namespace.Type.Method() in TestFile.cs:line 42", response.Failure.StackTrace, StringComparison.Ordinal);
        Assert.Equal("job log", response.Failure.ArtifactName);
        Assert.Equal("n/a", response.Failure.TrxPath);
        Assert.NotNull(response.Diagnostics);
        Assert.Equal("diagnostics.log", response.Diagnostics!.LogFile);
        Assert.Contains("not completed yet (status: queued)", response.Diagnostics!.Warnings[0], StringComparison.Ordinal);
        Assert.Contains("Falling back to failed job logs.", response.Diagnostics!.Warnings[1], StringComparison.Ordinal);
        var queuedDiagnosticsLog = File.ReadAllText(Path.Combine(fixtureDirectory, "diagnostics.log"));
        Assert.Contains("Skipping fallback to all artifacts because the run is still active and no likely test-results artifacts were found.", queuedDiagnosticsLog, StringComparison.Ordinal);
        Assert.Contains("Using log-derived occurrences because no downloadable .trx artifacts were available.", queuedDiagnosticsLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReturnsFastFailureForQueuedRunsWithoutTestResultsArtifactsOrMatchingLogs()
    {
        var fixtureDirectory = CreateFixtureDirectory(runStatus: "queued", logContent: "No failed tests here.");

        var result = await RunToolAsync(
            fixtureDirectory,
            "--test", "Tests.Namespace.Type.Method",
            "--url", "https://github.com/microsoft/aspire/actions/runs/123",
            "--workflow", "ci",
            "--repo", "microsoft/aspire");

        Assert.NotEqual(0, result.ExitCode);

        var response = JsonSerializer.Deserialize<CreateFailingTestIssueResponse>(result.Output, JsonOptions);
        Assert.NotNull(response);
        Assert.False(response!.Success);
        Assert.Equal("The workflow run has not produced any failed test results in downloadable .trx artifacts yet.", response.ErrorMessage);
        Assert.NotNull(response.Diagnostics);
        Assert.Contains("not completed yet (status: queued)", response.Diagnostics!.Warnings[0], StringComparison.Ordinal);
        Assert.Equal("diagnostics.log", response.Diagnostics.LogFile);
        var failedDiagnosticsLog = File.ReadAllText(Path.Combine(fixtureDirectory, "diagnostics.log"));
        Assert.Contains("The workflow run has not produced any failed test results in downloadable .trx artifacts yet.", failedDiagnosticsLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KeepsIssueBodyUnderGitHubLimitBySnippingMiddleSections()
    {
        var fixtureDirectory = CreateFixtureDirectory(
            new TestTrxCase(
                CanonicalTestName: "Tests.Namespace.Type.Method",
                DisplayName: "Tests.Namespace.Type.Method",
                Outcome: "Failed",
                ErrorMessage: CreateRepeatedText("error", 10_000),
                StackTrace: CreateRepeatedText("stack", 20_000),
                StdOut: CreateRepeatedText("stdout", 80_000)));

        var result = await RunToolAsync(
            fixtureDirectory,
            "--test", "Tests.Namespace.Type.Method",
            "--url", "https://github.com/microsoft/aspire/actions/runs/123",
            "--workflow", "ci",
            "--repo", "microsoft/aspire");

        Assert.Equal(0, result.ExitCode);

        var response = JsonSerializer.Deserialize<CreateFailingTestIssueResponse>(result.Output, JsonOptions);
        Assert.NotNull(response);
        Assert.True(response!.Success);
        Assert.NotNull(response.Issue);
        Assert.True(response.Issue!.Body.Length < 64 * 1024, $"Issue body length was {response.Issue.Body.Length}.");
        Assert.Contains("Snipped in the middle to keep the issue body under GitHub's 64 KB limit.", response.Issue.Body, StringComparison.Ordinal);
        Assert.Contains("... [snipped middle content] ...", response.Issue.Body, StringComparison.Ordinal);
    }

    private async Task<ToolResult> RunToolAsync(string fixtureDirectory, params string[] args)
    {
        ProcessStartInfo processStartInfo = new()
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = fixtureDirectory
        };

        processStartInfo.ArgumentList.Add("run");
        processStartInfo.ArgumentList.Add("--no-build");
        processStartInfo.ArgumentList.Add("--project");
        processStartInfo.ArgumentList.Add(_fixture.ToolProjectPath);
        processStartInfo.ArgumentList.Add("--");

        foreach (var arg in args)
        {
            processStartInfo.ArgumentList.Add(arg);
        }

        processStartInfo.Environment["ASPIRE_FAILING_TEST_ISSUE_FIXTURE_DIR"] = fixtureDirectory;

        _output.WriteLine($"Running: {processStartInfo.FileName} {string.Join(" ", processStartInfo.ArgumentList)}");

        using var process = new Process { StartInfo = processStartInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(cancellationTokenSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(stdout))
        {
            _output.WriteLine(stdout);
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            _output.WriteLine(stderr);
        }

        return new ToolResult(process.ExitCode, stdout, stderr);
    }

    private string CreateFixtureDirectory(params TestTrxCase[] testCases)
        => CreateFixtureDirectory(1, "completed", "logs-Sample-linux", null, false, testCases);

    private string CreateFixtureDirectory(
        int runAttempt = 1,
        string runStatus = "completed",
        string artifactName = "logs-Sample-linux",
        string? logContent = null,
        bool missingJobLog = false,
        params TestTrxCase[] testCases)
    {
        var fixtureDirectory = Path.Combine(_tempDirectory.Path, Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(fixtureDirectory);

        WriteJsonFixture(
            fixtureDirectory,
            "repos/microsoft/aspire/actions/workflows/ci.yml",
            """
            {
              "id": 99,
              "name": "CI",
              "path": ".github/workflows/ci.yml"
            }
            """);

        WriteJsonFixture(
            fixtureDirectory,
            "repos/microsoft/aspire/actions/runs/123",
            $$"""
            {
              "id": 123,
              "workflow_id": 99,
              "html_url": "https://github.com/microsoft/aspire/actions/runs/123",
              "run_attempt": {{runAttempt}},
              "status": "{{runStatus}}",
              "pull_requests": [
                {
                  "number": 321
                }
              ]
            }
            """);

        WriteJsonFixture(
            fixtureDirectory,
            "repos/microsoft/aspire/actions/runs/123/jobs?per_page=100&page=1",
            """
            {
              "jobs": [
                {
                  "id": 456,
                  "name": "Tests / Linux / Sample",
                  "html_url": "https://github.com/microsoft/aspire/actions/runs/123/job/456",
                  "conclusion": "failure"
                }
              ]
            }
            """);

        WriteJsonFixture(
            fixtureDirectory,
            "repos/microsoft/aspire/actions/runs/123/attempts/2/jobs?per_page=100&page=1",
            """
            {
              "jobs": [
                {
                  "id": 456,
                  "name": "Tests / Linux / Sample",
                  "html_url": "https://github.com/microsoft/aspire/actions/runs/123/job/456",
                  "conclusion": "failure"
                }
              ]
            }
            """);

        if (missingJobLog)
        {
            WriteErrorFixture(
                fixtureDirectory,
                "repos/microsoft/aspire/actions/jobs/456/logs",
                "gh api -H \"Accept: application/vnd.github+json\" repos/microsoft/aspire/actions/jobs/456/logs failed: gh: HTTP 404");
        }
        else
        {
            WriteTextFixture(
                fixtureDirectory,
                "repos/microsoft/aspire/actions/jobs/456/logs",
                logContent ??
                """
                Failed Tests.Namespace.Type.Method [123 ms]
                Error Message:
                Expected 1 but found 2.

                <details><summary>🔴 <b>Tests.Namespace.Type.Method(input: 1)</b></summary>

                ```yml
                Expected 1 but found 2.
                at Tests.Namespace.Type.Method() in TestFile.cs:line 42
                --- End of stack trace from previous location ---
                ```
                </details>
                """);
        }

        WriteJsonFixture(
            fixtureDirectory,
            "repos/microsoft/aspire/actions/jobs/456",
            """
            {
              "id": 456,
              "run_id": 123
            }
            """);

        WriteJsonFixture(
            fixtureDirectory,
            "repos/microsoft/aspire/actions/runs/123/artifacts?per_page=100&page=1",
            $$"""
            {
              "artifacts": [
                {
                  "id": 789,
                  "name": "{{artifactName}}"
                }
              ]
            }
            """);

        var zipPath = GetFixturePath(fixtureDirectory, "repos/microsoft/aspire/actions/artifacts/789/zip", ".zip");
        TestTrxBuilder.CreateArtifactZip(
            zipPath,
            "results/sample.trx",
            testCases.Length > 0
                ? testCases
                : [
                    new TestTrxCase(
                        CanonicalTestName: "Tests.Namespace.Type.Method",
                        DisplayName: "Tests.Namespace.Type.Method(input: 1)",
                        Outcome: "Failed",
                        ErrorMessage: "Expected 1 but found 2.",
                        StackTrace: "at Tests.Namespace.Type.Method() in TestFile.cs:line 42",
                        StdOut: "stdout line 1")
                ]);

        return fixtureDirectory;
    }

    private string CreateFixtureDirectoryWithPrResolution(
        int pullRequestNumber,
        string headSha,
        string runStatus = "completed")
    {
        var fixtureDirectory = Path.Combine(_tempDirectory.Path, Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(fixtureDirectory);

        WriteJsonFixture(
            fixtureDirectory,
            "repos/microsoft/aspire/actions/workflows/ci.yml",
            """
            {
              "id": 99,
              "name": "CI",
              "path": ".github/workflows/ci.yml"
            }
            """);

        // PR details fixture
        WriteJsonFixture(
            fixtureDirectory,
            $"repos/microsoft/aspire/pulls/{pullRequestNumber}",
            $$"""
            {
              "number": {{pullRequestNumber}},
              "head": {
                "sha": "{{headSha}}"
              }
            }
            """);

        // Workflow runs list (no status filter — the fix removed status=completed)
        WriteJsonFixture(
            fixtureDirectory,
            "repos/microsoft/aspire/actions/workflows/ci.yml/runs?per_page=100&page=1",
            $$"""
            {
              "workflow_runs": [
                {
                  "id": 123,
                  "workflow_id": 99,
                  "html_url": "https://github.com/microsoft/aspire/actions/runs/123",
                  "run_attempt": 1,
                  "status": "{{runStatus}}",
                  "head_sha": "{{headSha}}",
                  "pull_requests": [
                    {
                      "number": {{pullRequestNumber}}
                    }
                  ]
                }
              ]
            }
            """);

        // Run details fixture (needed after resolution for job fetching)
        WriteJsonFixture(
            fixtureDirectory,
            "repos/microsoft/aspire/actions/runs/123",
            $$"""
            {
              "id": 123,
              "workflow_id": 99,
              "html_url": "https://github.com/microsoft/aspire/actions/runs/123",
              "run_attempt": 1,
              "status": "{{runStatus}}",
              "head_sha": "{{headSha}}",
              "pull_requests": [
                {
                  "number": {{pullRequestNumber}}
                }
              ]
            }
            """);

        WriteJsonFixture(
            fixtureDirectory,
            "repos/microsoft/aspire/actions/runs/123/jobs?per_page=100&page=1",
            """
            {
              "jobs": [
                {
                  "id": 456,
                  "name": "Tests / Linux / Sample",
                  "html_url": "https://github.com/microsoft/aspire/actions/runs/123/job/456",
                  "conclusion": "failure"
                }
              ]
            }
            """);

        WriteTextFixture(
            fixtureDirectory,
            "repos/microsoft/aspire/actions/jobs/456/logs",
            """
            Failed Tests.Namespace.Type.Method [123 ms]
            Error Message:
            Expected 1 but found 2.

            <details><summary>🔴 <b>Tests.Namespace.Type.Method(input: 1)</b></summary>

            ```yml
            Expected 1 but found 2.
            at Tests.Namespace.Type.Method() in TestFile.cs:line 42
            --- End of stack trace from previous location ---
            ```
            </details>
            """);

        WriteJsonFixture(
            fixtureDirectory,
            "repos/microsoft/aspire/actions/jobs/456",
            """
            {
              "id": 456,
              "run_id": 123
            }
            """);

        WriteJsonFixture(
            fixtureDirectory,
            "repos/microsoft/aspire/actions/runs/123/artifacts?per_page=100&page=1",
            """
            {
              "artifacts": [
                {
                  "id": 789,
                  "name": "logs-Sample-linux"
                }
              ]
            }
            """);

        var zipPath = GetFixturePath(fixtureDirectory, "repos/microsoft/aspire/actions/artifacts/789/zip", ".zip");
        TestTrxBuilder.CreateArtifactZip(
            zipPath,
            "results/sample.trx",
            new TestTrxCase(
                CanonicalTestName: "Tests.Namespace.Type.Method",
                DisplayName: "Tests.Namespace.Type.Method(input: 1)",
                Outcome: "Failed",
                ErrorMessage: "Expected 1 but found 2.",
                StackTrace: "at Tests.Namespace.Type.Method() in TestFile.cs:line 42",
                StdOut: "stdout line 1"));

        return fixtureDirectory;
    }

    private static void WriteJsonFixture(string fixtureDirectory, string endpoint, string json)
        => File.WriteAllText(GetFixturePath(fixtureDirectory, endpoint, ".json"), json);

    private static void WriteTextFixture(string fixtureDirectory, string endpoint, string text)
        => File.WriteAllText(GetFixturePath(fixtureDirectory, endpoint, ".txt"), text);

    private static void WriteErrorFixture(string fixtureDirectory, string endpoint, string text)
        => File.WriteAllText(GetFixturePath(fixtureDirectory, endpoint, ".err"), text);

    private static string GetFixturePath(string fixtureDirectory, string endpoint, string extension)
    {
        var fileName = System.Text.RegularExpressions.Regex.Replace(endpoint, @"[^A-Za-z0-9._-]+", "_").Trim('_');
        return Path.Combine(fixtureDirectory, $"{fileName}{extension}");
    }

    private static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);

    private static string CreateRepeatedText(string prefix, int targetLength)
    {
        var seed = $"{prefix}-line-0123456789abcdefghijklmnopqrstuvwxyz\n";
        var builder = new System.Text.StringBuilder(targetLength + seed.Length);
        while (builder.Length < targetLength)
        {
            builder.Append(seed);
        }

        return builder.ToString();
    }

    private sealed record ToolResult(int ExitCode, string Output, string Error);

    private sealed record CreateFailingTestIssueResponse(
        bool Success,
        string? ErrorMessage,
        ResolutionResponse? Resolution,
        MatchResponse? Match,
        AllFailuresResponse? AllFailures,
        FailureResponse? Failure,
        IssueResponse? Issue,
        DiagnosticsResponse? Diagnostics);

    private sealed record ResolutionResponse(string SourceKind, string RunUrl, int RunAttempt, int? PullRequestNumber, IReadOnlyList<string> JobUrls);

    private sealed record MatchResponse(string Strategy, string CanonicalTestName, string DisplayTestName);

    private sealed record AllFailuresResponse(int FailedTests, IReadOnlyList<FailingTestEntryResponse> Tests);

    private sealed record FailingTestEntryResponse(string CanonicalTestName, string DisplayTestName, int OccurrenceCount, FailureResponse PrimaryFailure);

    private sealed record FailureResponse(string ErrorMessage, string StackTrace, string Stdout, string ArtifactName, string TrxPath);

    private sealed record IssueResponse(string MetadataMarker, string Body, ExistingIssueResponse? ExistingIssue, CreatedIssueResponse? CreatedIssue);

    private sealed record ExistingIssueResponse(int Number, string Url);

    private sealed record CreatedIssueResponse(int Number, string Url);

    private sealed record DiagnosticsResponse(string LogFile, IReadOnlyList<string> Warnings, IReadOnlyList<string> AvailableFailedTests);
}
