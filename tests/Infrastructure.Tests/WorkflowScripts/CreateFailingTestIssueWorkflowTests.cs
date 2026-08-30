// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Tests for .github/workflows/create-failing-test-issue.js.
/// </summary>
public sealed class CreateFailingTestIssueWorkflowTests : IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly TemporaryWorkspace _workspace;
    private readonly string _repoRoot;
    private readonly string _harnessPath;
    private readonly ITestOutputHelper _output;

    public CreateFailingTestIssueWorkflowTests(ITestOutputHelper output)
    {
        _output = output;
        _workspace = TemporaryWorkspace.Create(output);
        _repoRoot = RepoRoot.Path;
        _harnessPath = Path.Combine(_repoRoot, "tests", "Infrastructure.Tests", "WorkflowScripts", "create-failing-test-issue.harness.js");
    }

    public void Dispose() => _workspace.Dispose();

    [Fact]
    [RequiresTools(["node"])]
    public async Task ParseCommandSupportsFlagSyntax()
    {
        var result = await InvokeHarnessAsync<ParseCommandResult>(
            "parseCommand",
            new
            {
                body = "/create-issue --test \"Tests.Namespace.Type.Method(input: 1)\" --url https://github.com/microsoft/aspire/actions/runs/123 --workflow .github/workflows/custom.yml --force-new"
            });

        Assert.True(result.Success);
        Assert.Equal("Tests.Namespace.Type.Method(input: 1)", result.TestQuery);
        Assert.Equal("https://github.com/microsoft/aspire/actions/runs/123", result.SourceUrl);
        Assert.Equal(".github/workflows/custom.yml", result.Workflow);
        Assert.True(result.ForceNew);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ParseCommandFallsBackToDefaultSourceUrlForSinglePositionalArgument()
    {
        var result = await InvokeHarnessAsync<ParseCommandResult>(
            "parseCommand",
            new
            {
                body = "/create-issue Tests.Namespace.Type.Method",
                defaultSourceUrl = "https://github.com/microsoft/aspire/pull/999"
            });

        Assert.True(result.Success);
        Assert.Equal("Tests.Namespace.Type.Method", result.TestQuery);
        Assert.Equal("https://github.com/microsoft/aspire/pull/999", result.SourceUrl);
        Assert.Equal("ci", result.Workflow);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ParseCommandUsesTrailingUrlForCompatibilitySyntax()
    {
        var result = await InvokeHarnessAsync<ParseCommandResult>(
            "parseCommand",
            new
            {
                body = "/create-issue \"Tests.Namespace.Type.Method(input: 1)\" https://github.com/microsoft/aspire/actions/runs/123/job/456"
            });

        Assert.True(result.Success);
        Assert.Equal("Tests.Namespace.Type.Method(input: 1)", result.TestQuery);
        Assert.Equal("https://github.com/microsoft/aspire/actions/runs/123/job/456", result.SourceUrl);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ParseCommandSupportsPositionalTestNameWithFlags()
    {
        var result = await InvokeHarnessAsync<ParseCommandResult>(
            "parseCommand",
            new
            {
                body = "/create-issue Tests.Namespace.Type.Method --force-new",
                defaultSourceUrl = "https://github.com/microsoft/aspire/pull/999"
            });

        Assert.True(result.Success);
        Assert.Equal("Tests.Namespace.Type.Method", result.TestQuery);
        Assert.Equal("https://github.com/microsoft/aspire/pull/999", result.SourceUrl);
        Assert.True(result.ForceNew);
        Assert.False(result.ListOnly);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ParseCommandRejectsAmbiguousPositionalSyntax()
    {
        var result = await InvokeHarnessAsync<ParseCommandResult>(
            "parseCommand",
            new
            {
                body = "/create-issue Tests Namespace Type Method"
            });

        Assert.False(result.Success);
        Assert.Contains("ambiguous", result.ErrorMessage);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ParseCommandRejectsPositionalBeforeTestFlag()
    {
        var result = await InvokeHarnessAsync<ParseCommandResult>(
            "parseCommand",
            new
            {
                body = "/create-issue MyTest --test OtherTest"
            });

        Assert.False(result.Success);
        Assert.Contains("ambiguous", result.ErrorMessage);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ParseCommandReturnsListOnlyWhenNoArgumentsProvided()
    {
        var result = await InvokeHarnessAsync<ParseCommandResult>(
            "parseCommand",
            new
            {
                body = "/create-issue",
                defaultSourceUrl = "https://github.com/microsoft/aspire/pull/999"
            });

        Assert.True(result.Success);
        Assert.True(result.ListOnly);
        Assert.Equal(string.Empty, result.TestQuery);
        Assert.Equal("https://github.com/microsoft/aspire/pull/999", result.SourceUrl);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ParseCommandReturnsListOnlyWhenFlagBasedWithoutTest()
    {
        var result = await InvokeHarnessAsync<ParseCommandResult>(
            "parseCommand",
            new
            {
                body = "/create-issue --workflow custom.yml --url https://github.com/microsoft/aspire/actions/runs/123"
            });

        Assert.True(result.Success);
        Assert.True(result.ListOnly);
        Assert.Equal(string.Empty, result.TestQuery);
        Assert.Equal("https://github.com/microsoft/aspire/actions/runs/123", result.SourceUrl);
        Assert.Equal("custom.yml", result.Workflow);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task FormatListResponseReturnsErrorWhenResolverFailed()
    {
        var result = await InvokeHarnessAsync<FormatListResponseResult>(
            "formatListResponse",
            new
            {
                resolverOutcome = "failure",
                resultJson = (object?)null
            });

        Assert.True(result.Error);
        Assert.Contains("resolver failed", result.Message);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task FormatListResponseReturnsTestNamesFromResult()
    {
        var result = await InvokeHarnessAsync<FormatListResponseResult>(
            "formatListResponse",
            new
            {
                resolverOutcome = "success",
                resultJson = new
                {
                    allFailures = new
                    {
                        tests = new[]
                        {
                            new { canonicalTestName = "Namespace.Class.MethodA", displayTestName = "MethodA" },
                            new { canonicalTestName = "Namespace.Class.MethodB", displayTestName = "MethodB" }
                        }
                    }
                }
            });

        Assert.False(result.Error);
        Assert.NotNull(result.Tests);
        Assert.Equal(2, result.Tests!.Length);
        Assert.Contains("Namespace.Class.MethodA", result.Tests);
        Assert.Contains("Namespace.Class.MethodB", result.Tests);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task FormatListResponseReturnsNoFailuresWhenResultIsEmpty()
    {
        var result = await InvokeHarnessAsync<FormatListResponseResult>(
            "formatListResponse",
            new
            {
                resolverOutcome = "success",
                resultJson = new { allFailures = new { tests = Array.Empty<object>() } }
            });

        Assert.False(result.Error);
        Assert.Contains("No test failures", result.Message);
        Assert.Null(result.Tests);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task FormatListResponseReturnsErrorWhenResolverFailedWithResult()
    {
        var result = await InvokeHarnessAsync<FormatListResponseResult>(
            "formatListResponse",
            new
            {
                resolverOutcome = "failure",
                resultJson = new
                {
                    success = false,
                    errorMessage = "Could not find any TRX files.",
                    allFailures = new { tests = Array.Empty<object>() }
                }
            });

        Assert.True(result.Error);
        Assert.Contains("Could not find any TRX files", result.Message);
    }

    [Fact]
    [RequiresTools(["node"])]
    public void WorkflowDelegatesLifecycleToCheckedInAdapter()
    {
        var workflowPath = Path.Combine(_repoRoot, ".github", "workflows", "create-failing-test-issue.yml");
        var workflow = File.ReadAllText(workflowPath);

        Assert.Contains("create-failing-test-issue-tracking.js", workflow, StringComparison.Ordinal);
        Assert.Contains(".reconcile(", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("tracking.recordRun", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("tracking.duplicateExemptStamp()", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("github.rest.issues.create({", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("search.issuesAndPullRequests", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckedInLifecycleAdapterExists()
    {
        var adapterPath = Path.Combine(_repoRoot, ".github", "workflows", "create-failing-test-issue-tracking.js");

        Assert.True(File.Exists(adapterPath), $"Missing lifecycle adapter: {adapterPath}");
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task LifecycleAdapterUsesSharedReconciliationForExistingIssue()
    {
        const string marker = "<!-- failing-test-signature: v1:abc -->";
        var result = await InvokeHarnessAsync<ReconciliationResponse>(
            "reconcile",
            new
            {
                marker,
                body = $"{marker}\n\nBody",
                issues = new[]
                {
                    new { number = 42, body = marker, state = "closed", comments = Array.Empty<string>() },
                }
            });

        Assert.True(result.Available);
        Assert.False(result.Result!.Created);
        Assert.Equal(42, result.Result.Number);
        var issue = Assert.Single(result.Issues!);
        Assert.Equal("open", issue.State);
        Assert.Contains(issue.Comments, comment => comment.Contains("<!-- run:123 -->", StringComparison.Ordinal));
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task LifecycleAdapterForceNewUsesPlannerAndStampsExemption()
    {
        const string marker = "<!-- failing-test-signature: v1:abc -->";
        var result = await InvokeHarnessAsync<ReconciliationResponse>(
            "reconcile",
            new
            {
                marker,
                body = $"{marker}\n\nBody",
                forceNew = true,
                issues = new[]
                {
                    new { number = 42, body = marker, state = "open", comments = Array.Empty<string>() },
                }
            });

        Assert.True(result.Available);
        Assert.True(result.Result!.Created);
        Assert.Equal(1000, result.Result.Number);
        var created = Assert.Single(result.Issues!, issue => issue.Number == 1000);
        Assert.Contains("<!-- tracking-issue-duplicate-exempt -->", created.Body, StringComparison.Ordinal);
        Assert.Empty(created.Comments);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task LifecycleAdapterEnsuresFailingTestLabelBeforeReconciliation()
    {
        const string marker = "<!-- failing-test-signature: v1:abc -->";
        var result = await InvokeHarnessAsync<ReconciliationResponse>(
            "reconcile",
            new
            {
                marker,
                body = $"{marker}\n\nBody",
                issues = Array.Empty<object>(),
            });

        Assert.NotNull(result.Calls);
        Assert.Equal(["ensureLabel"], result.Calls);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task GhApiTransportListsAllIssueStatesWithPagination()
    {
        var result = await InvokeHarnessAsync<GhTransportResponse>(
            "ghTransport",
            new { operation = "list" });

        Assert.True(result.Available);
        var call = Assert.Single(result.Calls!);
        Assert.Contains("--paginate", call.Args);
        Assert.Contains("--slurp", call.Args);
        Assert.Contains("repos/microsoft/aspire/issues?labels=failing-test&state=all&per_page=100", call.Args);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task GhApiTransportOwnsEveryIssueMutation()
    {
        var result = await InvokeHarnessAsync<GhTransportResponse>(
            "ghTransport",
            new { operation = "mutate" });

        Assert.True(result.Available);
        Assert.Equal(7, result.Calls!.Length);
        Assert.All(result.Calls, call => Assert.Equal("api", call.Args[0]));
        Assert.Contains(result.Calls, call => call.Args.Contains("POST") && call.Args.Contains("repos/microsoft/aspire/labels"));
        Assert.Contains(result.Calls, call => call.Args.Contains("POST") && call.Args.Contains("repos/microsoft/aspire/issues"));
        Assert.Contains(result.Calls, call => call.Args.Contains("PATCH") && call.Input!.Contains("\"state\":\"closed\"", StringComparison.Ordinal));
        Assert.Contains(result.Calls, call => call.Args.Contains("PATCH") && call.Input!.Contains("\"state\":\"open\"", StringComparison.Ordinal));
    }

    [Fact]
    public void CSharpToolDelegatesLifecycleToCheckedInAdapter()
    {
        var commandPath = Path.Combine(_repoRoot, "tools", "CreateFailingTestIssue", "FailingTestIssueCommand.cs");
        var command = File.ReadAllText(commandPath);

        Assert.Contains("create-failing-test-issue-tracking.js", command, StringComparison.Ordinal);
        Assert.DoesNotContain("FindIssuesByMarkerAsync", command, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateIssueAsync", command, StringComparison.Ordinal);
        Assert.DoesNotContain("IssueHasCommentMarkerAsync", command, StringComparison.Ordinal);
        Assert.DoesNotContain("CloseIssueAsNotPlannedAsync", command, StringComparison.Ordinal);
        Assert.DoesNotContain("ReopenIssueAsync", command, StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpToolTerminatesAdapterProcessTreeWhenCancelled()
    {
        var commandPath = Path.Combine(_repoRoot, "tools", "CreateFailingTestIssue", "FailingTestIssueCommand.cs");
        var command = File.ReadAllText(commandPath);

        Assert.Contains("process.Kill(entireProcessTree: true)", command, StringComparison.Ordinal);
    }

    [Fact]
    public void ProducersUseSharedPlannerForTrustedCloseAndDryRun()
    {
        var reportCi = File.ReadAllText(Path.Combine(_repoRoot, ".github", "workflows", "report-ci-failure.js"));
        var monitor = File.ReadAllText(Path.Combine(_repoRoot, ".github", "workflows", "monitor-scheduled-workflows.js"));

        Assert.Contains("tracking.executeIssueReconciliation(", reportCi, StringComparison.Ordinal);
        Assert.DoesNotContain("tracking.listOpenIssuesByLabel(", reportCi, StringComparison.Ordinal);
        Assert.DoesNotContain("tracking.closeIssue(", reportCi, StringComparison.Ordinal);

        Assert.Contains("tracking.planIssueReconciliation(", monitor, StringComparison.Ordinal);
        Assert.Contains("tracking.executeIssueReconciliation(", monitor, StringComparison.Ordinal);
        Assert.DoesNotContain("tracking.closeIssue(", monitor, StringComparison.Ordinal);
    }

    private async Task<T> InvokeHarnessAsync<T>(string operation, object payload)
    {
        var requestPath = Path.Combine(_workspace.Path, $"{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new { operation, payload }, s_jsonOptions));

        using var command = new NodeCommand(_output, "create-failing-test-issue");
        command.WithWorkingDirectory(_repoRoot);

        var result = await command.ExecuteScriptAsync(_harnessPath, requestPath);
        Assert.Equal(0, result.ExitCode);

        var response = JsonSerializer.Deserialize<HarnessResponse<T>>(result.Output, s_jsonOptions);
        Assert.NotNull(response);
        return response!.Result;
    }

    private sealed record HarnessResponse<T>(T Result);

    private sealed record ParseCommandResult(bool Success, string TestQuery, string? SourceUrl, string Workflow, bool ForceNew, bool ListOnly, string? ErrorMessage);

    private sealed record FormatListResponseResult(bool Error, string Message, string[]? Tests);

    private sealed record ReconciliationResponse(bool Available, ReconciliationResult? Result, ReconciliationIssue[]? Issues, string[]? Calls);

    private sealed record ReconciliationResult(int Number, bool Created);

    private sealed record ReconciliationIssue(int Number, string State, string Body, string[] Comments);

    private sealed record GhTransportResponse(bool Available, GhInvocation[]? Calls);

    private sealed record GhInvocation(string[] Args, string? Input);
}
