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

    private readonly TestTempDirectory _tempDirectory = new();
    private readonly string _repoRoot;
    private readonly string _harnessPath;
    private readonly ITestOutputHelper _output;

    public CreateFailingTestIssueWorkflowTests(ITestOutputHelper output)
    {
        _output = output;
        _repoRoot = FindRepoRoot();
        _harnessPath = Path.Combine(_repoRoot, "tests", "Infrastructure.Tests", "WorkflowScripts", "create-failing-test-issue.harness.js");
    }

    public void Dispose() => _tempDirectory.Dispose();

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
        Assert.NotNull(result.TestQueries);
        Assert.Single(result.TestQueries!);
        Assert.Equal("Tests.Namespace.Type.Method(input: 1)", result.TestQueries![0]);
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
        Assert.NotNull(result.TestQueries);
        Assert.Single(result.TestQueries!);
        Assert.Equal("Tests.Namespace.Type.Method", result.TestQueries![0]);
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
        Assert.NotNull(result.TestQueries);
        Assert.Single(result.TestQueries!);
        Assert.Equal("Tests.Namespace.Type.Method(input: 1)", result.TestQueries![0]);
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
        Assert.NotNull(result.TestQueries);
        Assert.Single(result.TestQueries!);
        Assert.Equal("Tests.Namespace.Type.Method", result.TestQueries![0]);
        Assert.Equal("https://github.com/microsoft/aspire/pull/999", result.SourceUrl);
        Assert.True(result.ForceNew);
        Assert.False(result.ListOnly);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ParseCommandSupportsMultiplePositionalTestNames()
    {
        var result = await InvokeHarnessAsync<ParseCommandResult>(
            "parseCommand",
            new
            {
                body = "/create-issue Tests.Class.MethodA Tests.Class.MethodB Tests.Class.MethodC"
            });

        Assert.True(result.Success);
        Assert.Equal("Tests.Class.MethodA", result.TestQuery);
        Assert.NotNull(result.TestQueries);
        Assert.Equal(3, result.TestQueries!.Length);
        Assert.Equal("Tests.Class.MethodA", result.TestQueries[0]);
        Assert.Equal("Tests.Class.MethodB", result.TestQueries[1]);
        Assert.Equal("Tests.Class.MethodC", result.TestQueries[2]);
        Assert.False(result.ListOnly);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ParseCommandSupportsMultipleTestFlags()
    {
        var result = await InvokeHarnessAsync<ParseCommandResult>(
            "parseCommand",
            new
            {
                body = "/create-issue --test Tests.Class.MethodA --test Tests.Class.MethodB --test Tests.Class.MethodC --force-new",
                defaultSourceUrl = "https://github.com/microsoft/aspire/pull/999"
            });

        Assert.True(result.Success);
        Assert.Equal("Tests.Class.MethodA", result.TestQuery);
        Assert.NotNull(result.TestQueries);
        Assert.Equal(3, result.TestQueries!.Length);
        Assert.Equal("Tests.Class.MethodA", result.TestQueries[0]);
        Assert.Equal("Tests.Class.MethodB", result.TestQueries[1]);
        Assert.Equal("Tests.Class.MethodC", result.TestQueries[2]);
        Assert.True(result.ForceNew);
        Assert.False(result.ListOnly);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ParseCommandSupportsMultiplePositionalTestNamesWithTrailingUrl()
    {
        var result = await InvokeHarnessAsync<ParseCommandResult>(
            "parseCommand",
            new
            {
                body = "/create-issue Tests.Class.MethodA Tests.Class.MethodB https://github.com/microsoft/aspire/pull/123"
            });

        Assert.True(result.Success);
        Assert.Equal("Tests.Class.MethodA", result.TestQuery);
        Assert.NotNull(result.TestQueries);
        Assert.Equal(2, result.TestQueries!.Length);
        Assert.Equal("Tests.Class.MethodA", result.TestQueries[0]);
        Assert.Equal("Tests.Class.MethodB", result.TestQueries[1]);
        Assert.Equal("https://github.com/microsoft/aspire/pull/123", result.SourceUrl);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ParseCommandSupportsMultiplePositionalTestNamesWithFlags()
    {
        var result = await InvokeHarnessAsync<ParseCommandResult>(
            "parseCommand",
            new
            {
                body = "/create-issue Tests.Class.MethodA Tests.Class.MethodB --force-new",
                defaultSourceUrl = "https://github.com/microsoft/aspire/pull/999"
            });

        Assert.True(result.Success);
        Assert.NotNull(result.TestQueries);
        Assert.Equal(2, result.TestQueries!.Length);
        Assert.Equal("Tests.Class.MethodA", result.TestQueries[0]);
        Assert.Equal("Tests.Class.MethodB", result.TestQueries[1]);
        Assert.True(result.ForceNew);
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
        Assert.NotNull(result.TestQueries);
        Assert.Empty(result.TestQueries!);
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
        Assert.NotNull(result.TestQueries);
        Assert.Empty(result.TestQueries!);
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
    public async Task BuildIssueSearchQueryTargetsFailingTestIssuesByMetadataMarker()
    {
        var query = await InvokeHarnessAsync<string>(
            "buildIssueSearchQuery",
            new
            {
                owner = "microsoft",
                repo = "aspire",
                metadataMarker = "<!-- failing-test-signature: v1:abc123 -->"
            });

        Assert.Equal("repo:microsoft/aspire is:issue label:failing-test in:body \"<!-- failing-test-signature: v1:abc123 -->\"", query);
    }

    private async Task<T> InvokeHarnessAsync<T>(string operation, object payload)
    {
        var requestPath = Path.Combine(_tempDirectory.Path, $"{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new { operation, payload }, s_jsonOptions));

        using var command = new NodeCommand(_output, "create-failing-test-issue");
        command.WithWorkingDirectory(_repoRoot);

        var result = await command.ExecuteScriptAsync(_harnessPath, requestPath);
        Assert.Equal(0, result.ExitCode);

        var response = JsonSerializer.Deserialize<HarnessResponse<T>>(result.Output, s_jsonOptions);
        Assert.NotNull(response);
        return response!.Result;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var gitPath = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }

    private sealed record HarnessResponse<T>(T Result);

    private sealed record ParseCommandResult(bool Success, string TestQuery, string[]? TestQueries, string? SourceUrl, string Workflow, bool ForceNew, bool ListOnly, string? ErrorMessage);

    private sealed record FormatListResponseResult(bool Error, string Message, string[]? Tests);
}
