// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public sealed class ReproductionWorkerTests(ITestOutputHelper output) : IDisposable
{
    private readonly TemporaryWorkspace _workspace = TemporaryWorkspace.Create(output);
    private static readonly string s_script = Path.Combine(RepoRoot.Path, "eng", "scripts", "reproduction-worker.ps1");
    private const string WorkflowSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private string ResultsDirectory => Path.Combine(_workspace.Path, "results");
    private string ReceiptPath => Path.Combine(ResultsDirectory, "receipt.json");

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task OmittedInputsPreserveManualConfiguration()
    {
        var (result, values) = await ConfigureAsync([]);

        result.EnsureSuccessful();
        var config = JsonNode.Parse(values["config"])!;
        Assert.Equal("Hosting", config["test_project"]!.GetValue<string>());
        Assert.Equal("--filter-class \"*.ExistingTests\"", config["test_filter"]!.GetValue<string>());
        Assert.Equal("manual", config["mode"]!.GetValue<string>());
        Assert.Equal(3, config["runner_count"]!.GetValue<int>());
        Assert.Equal(3, config["repetitions"]!.GetValue<int>());
        Assert.Equal("", config["candidate_commit"]!.GetValue<string>());
        var matrix = JsonNode.Parse(values["matrix"])!["include"]!.AsArray();
        Assert.Equal(
            ["ubuntu-latest:1", "ubuntu-latest:2", "ubuntu-latest:3", "windows-latest:1", "windows-latest:2", "windows-latest:3"],
            matrix.Select(entry => $"{entry!["os"]}:{entry["index"]}"));
    }

    [Theory]
    [InlineData("single-test", "Example.Tests.Fails", "--filter-method \"Example.Tests.Fails\"")]
    [InlineData("", "Example.Tests.Fails", "--filter-method \"Example.Tests.Fails\"")]
    [InlineData("quarantine-project", "", "--filter-trait \"quarantined=true\"")]
    [RequiresTools(["pwsh"])]
    public async Task DispatchOverridesConfigurationWithBoundedSelection(string mode, string method, string expectedFilter)
    {
        var (result, values) = await ConfigureAsync(new()
        {
            ["test_project"] = "Cli.EndToEnd",
            ["mode"] = mode,
            ["test_method"] = method,
            ["os"] = "macos-latest",
            ["runner_count"] = "2",
            ["repetitions"] = "5",
            ["candidate_commit"] = WorkflowSha.ToUpperInvariant(),
            ["correlation_id"] = "repair-42.verify_1",
        });

        result.EnsureSuccessful();
        var config = JsonNode.Parse(values["config"])!;
        Assert.Equal(expectedFilter, values["test_filter"]);
        Assert.Equal("Cli.EndToEnd", values["test_project"]);
        Assert.Equal("true", values["is_cli_e2e"]);
        Assert.Equal(WorkflowSha, values["candidate_commit"]);
        Assert.Equal("5", values["repetitions"]);
        Assert.Equal("repair-42.verify_1", config["correlation_id"]!.GetValue<string>());
        Assert.Equal(2, JsonNode.Parse(values["matrix"])!["include"]!.AsArray().Count);
    }

    [Theory]
    [InlineData("test_project", "../Hosting", "project shortname")]
    [InlineData("test_project", "Hosting\n", "project shortname")]
    [InlineData("test_method", "*.Fails", "exact fully qualified")]
    [InlineData("test_method", "Example.Tests.Fails\n", "exact fully qualified")]
    [InlineData("test_method", "Example.Tests.Fails\"; echo bad", "exact fully qualified")]
    [InlineData("mode", "class", "mode must")]
    [InlineData("mode", "single-test", "requires an exact")]
    [InlineData("os", "self-hosted", "Unsupported os")]
    [InlineData("os", "ubuntu-latest,ubuntu-latest", "distinct runner")]
    [InlineData("os", "ubuntu-latest,", "Unsupported os")]
    [InlineData("runner_count", "0", "between 1 and 10")]
    [InlineData("runner_count", "11", "between 1 and 10")]
    [InlineData("repetitions", "51", "between 1 and 50")]
    [InlineData("repetitions", "1; echo bad", "between 1 and 50")]
    [InlineData("candidate_commit", "main", "full 40-character")]
    [InlineData("candidate_commit", "1234567", "full 40-character")]
    [InlineData("candidate_commit", WorkflowSha + "\n", "full 40-character")]
    [InlineData("correlation_id", "bad\nvalue", "correlation_id must")]
    [InlineData("correlation_id", "verify\n", "correlation_id must")]
    [InlineData("command", "echo bad", "Unknown or non-string")]
    [RequiresTools(["pwsh"])]
    public async Task InvalidDispatchFailsBeforeProducingOutputs(string name, string value, string diagnostic)
    {
        var (result, values) = await ConfigureAsync(new() { [name] = value });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(diagnostic, result.Output);
        Assert.Empty(values);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task QuarantineSelectionRejectsAnAdditionalMethod()
    {
        var (result, _) = await ConfigureAsync(new()
        {
            ["mode"] = "quarantine-project",
            ["test_method"] = "Example.Tests.Fails",
        });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("does not accept test_method", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task PlaceholderCannotProduceASuccessfulConfiguration()
    {
        using var command = CreateCommand()
            .WithEnvironmentVariable("TEST_FILTER", "--filter-method \"*.YourTestMethodName\"");

        var result = await command.ExecuteAsync("-Operation", "Configure");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Configure TEST_FILTER", result.Output);
    }

    [Theory]
    [InlineData("12", "60", 0)]
    [InlineData("129", "3", 1)]
    [RequiresTools(["pwsh"])]
    public async Task ManualCountsKeepExistingRangeWithinMatrixLimit(string runners, string repetitions, int exitCode)
    {
        using var command = CreateCommand()
            .WithEnvironmentVariable("RUNNERS_PER_OS", runners)
            .WithEnvironmentVariable("ITERATIONS_PER_RUNNER", repetitions);

        var result = await command.ExecuteAsync("-Operation", "Configure");

        result.EnsureExitCode(exitCode);
        if (exitCode != 0)
        {
            Assert.Contains("matrix must not exceed 256", result.Output);
        }
    }

    [Fact]
    [RequiresTools(["pwsh", "git"])]
    public async Task ReceiptSeparatesWorkflowAndActualSourceAndRetainsEveryIteration()
    {
        var sourceSha = GitCli.Run(RepoRoot.Path, "rev-parse", "HEAD");
        (await StartAsync(sourceSha)).EnsureSuccessful();
        var passedDirectory = CreateIteration(1);
        TestTrxBuilder.CreateTrxFile(Path.Combine(passedDirectory, "run.trx"), new TestTrxCase("Example.Tests.Passes", "passes", "Passed"));
        TestTrxBuilder.CreateTrxFile(Path.Combine(passedDirectory, "merged", "run.trx"), new TestTrxCase("Example.Tests.Passes", "passes", "Passed"));
        (await RecordAsync(1, 0)).EnsureSuccessful();

        var failedDirectory = CreateIteration(2);
        TestTrxBuilder.CreateTrxFile(Path.Combine(failedDirectory, "run.trx"), new TestTrxCase("Example.Tests.Fails", "fails", "Failed"));
        (await RecordAsync(2, 2)).EnsureExitCode(1);

        var receipt = JsonNode.Parse(await File.ReadAllTextAsync(ReceiptPath))!;
        Assert.Equal(WorkflowSha, receipt["workflow_sha"]!.GetValue<string>());
        Assert.Equal(sourceSha, receipt["source_sha"]!.GetValue<string>());
        Assert.Equal("7", receipt["run_attempt"]!.GetValue<string>());
        Assert.Equal("verify-42", receipt["config"]!["correlation_id"]!.GetValue<string>());
        var iterations = receipt["iterations"]!.AsArray();
        Assert.Equal(["passed", "failed"], iterations.Select(item => item!["status"]!.GetValue<string>()));
        Assert.Equal([1, 1], iterations.Select(item => item!["executed"]!.GetValue<int>()));
        Assert.Equal([0, 2], iterations.Select(item => item!["process_exit_code"]!.GetValue<int>()));
        for (var i = 0; i < iterations.Count; i++)
        {
            Assert.Equal($"iteration-{i + 1}/test-output.log", iterations[i]!["output"]!.GetValue<string>());
            Assert.Equal($"iteration-{i + 1}/run.trx", Assert.Single(iterations[i]!["trx"]!.AsArray())!.GetValue<string>());
            Assert.Equal($"output {i + 1}", await File.ReadAllTextAsync(Path.Combine(ResultsDirectory, $"iteration-{i + 1}", "test-output.log")));
        }
    }

    [Fact]
    [RequiresTools(["pwsh", "git"])]
    public async Task CandidateMismatchFailsBeforeCreatingAReceipt()
    {
        var result = await StartAsync(WorkflowSha);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Candidate checkout mismatch", result.Output);
        Assert.False(File.Exists(ReceiptPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [RequiresTools(["pwsh", "git"])]
    public async Task SuccessfulProcessWithoutExecutedTestsIsNotAPass(bool emptyTrx)
    {
        (await StartAsync()).EnsureSuccessful();
        var directory = CreateIteration(1);
        if (emptyTrx)
        {
            TestTrxBuilder.CreateTrxFile(Path.Combine(directory, "empty.trx"));
        }
        var result = await RecordAsync(1, 0);

        result.EnsureExitCode(1);
        var iteration = JsonNode.Parse(await File.ReadAllTextAsync(ReceiptPath))!["iterations"]![0]!;
        Assert.Equal("no-execution-evidence", iteration["status"]!.GetValue<string>());
        Assert.Equal(8, iteration["exit_code"]!.GetValue<int>());
        Assert.Equal(0, iteration["executed"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("Passed", 3)]
    [InlineData("Failed", 0)]
    [InlineData("Error", 0)]
    [RequiresTools(["pwsh", "git"])]
    public async Task ProcessAndReportMustBothSucceed(string outcome, int processExitCode)
    {
        (await StartAsync()).EnsureSuccessful();
        var directory = CreateIteration(1);
        TestTrxBuilder.CreateTrxFile(Path.Combine(directory, "run.trx"), new TestTrxCase("Example.Tests.Runs", "runs", outcome));

        var result = await RecordAsync(1, processExitCode);

        result.EnsureExitCode(1);
        var iteration = JsonNode.Parse(await File.ReadAllTextAsync(ReceiptPath))!["iterations"]![0]!;
        Assert.Equal("failed", iteration["status"]!.GetValue<string>());
        Assert.NotEqual(0, iteration["exit_code"]!.GetValue<int>());
        Assert.Equal(processExitCode, iteration["process_exit_code"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("<not-xml")]
    [InlineData("<TestRun><ResultSummary /></TestRun>")]
    [InlineData("<TestRun><ResultSummary><Counters executed=\"invalid\" passed=\"0\" failed=\"0\" /></ResultSummary></TestRun>")]
    [InlineData("<!DOCTYPE TestRun [<!ENTITY test SYSTEM 'file:///should-not-be-read'>]><TestRun>&test;</TestRun>")]
    [RequiresTools(["pwsh", "git"])]
    public async Task InvalidReportsFailExplicitlyWithoutCountingAPass(string report)
    {
        (await StartAsync()).EnsureSuccessful();
        var directory = CreateIteration(1);
        var reportPath = Path.Combine(directory, "run.trx");
        await File.WriteAllTextAsync(reportPath, report);

        var result = await RecordAsync(1, 0);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(JsonNode.Parse(await File.ReadAllTextAsync(ReceiptPath))!["iterations"]!.AsArray());
        Assert.Equal(report, await File.ReadAllTextAsync(reportPath));
    }

    [Theory]
    [InlineData("2")]
    [InlineData("0")]
    [InlineData("4")]
    [RequiresTools(["pwsh", "git"])]
    public async Task OutOfOrderIterationCannotInflateCounts(string iteration)
    {
        (await StartAsync()).EnsureSuccessful();

        var result = await RecordAsync(int.Parse(iteration), 0);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Iteration must be the next requested", result.Output);
        Assert.Empty(JsonNode.Parse(await File.ReadAllTextAsync(ReceiptPath))!["iterations"]!.AsArray());
    }

    [Fact]
    [RequiresTools(["pwsh", "git"])]
    public async Task DuplicateIterationCannotInflateCounts()
    {
        (await StartAsync()).EnsureSuccessful();
        var directory = CreateIteration(1);
        TestTrxBuilder.CreateTrxFile(Path.Combine(directory, "run.trx"), new TestTrxCase("Example.Tests.Passes", "passes", "Passed"));
        (await RecordAsync(1, 0)).EnsureSuccessful();

        var result = await RecordAsync(1, 0);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Iteration must be the next requested", result.Output);
        Assert.Single(JsonNode.Parse(await File.ReadAllTextAsync(ReceiptPath))!["iterations"]!.AsArray());
    }

    private PowerShellCommand CreateCommand() => new PowerShellCommand(s_script, output)
        .WithTimeout(TimeSpan.FromMinutes(1))
        .WithWorkingDirectory(RepoRoot.Path)
        .WithEnvironmentVariable("DISPATCH_INPUTS", "{}")
        .WithEnvironmentVariable("GITHUB_OUTPUT", Path.Combine(_workspace.Path, "outputs"))
        .WithEnvironmentVariable("TEST_PROJECT", "Hosting")
        .WithEnvironmentVariable("TEST_FILTER", "--filter-class \"*.ExistingTests\"")
        .WithEnvironmentVariable("TARGET_OSES", "ubuntu-latest,windows-latest")
        .WithEnvironmentVariable("RUNNERS_PER_OS", "3")
        .WithEnvironmentVariable("ITERATIONS_PER_RUNNER", "3");

    private async Task<(CommandResult Result, Dictionary<string, string> Values)> ConfigureAsync(Dictionary<string, string> inputs)
    {
        using var command = CreateCommand().WithEnvironmentVariable("DISPATCH_INPUTS", JsonSerializer.Serialize(inputs));
        var result = await command.ExecuteAsync("-Operation", "Configure");
        var outputPath = Path.Combine(_workspace.Path, "outputs");
        var values = File.Exists(outputPath)
            ? File.ReadAllLines(outputPath).Select(line => line.Split('=', 2)).ToDictionary(parts => parts[0], parts => parts[1])
            : [];

        return (result, values);
    }

    private async Task<CommandResult> StartAsync(string candidate = "")
    {
        using var command = CreateCommand()
            .WithEnvironmentVariable("WORKER_CONFIG", JsonSerializer.Serialize(new { repetitions = 3, candidate_commit = candidate, correlation_id = "verify-42" }))
            .WithEnvironmentVariable("GITHUB_REPOSITORY", "example/repo")
            .WithEnvironmentVariable("GITHUB_RUN_ID", "42")
            .WithEnvironmentVariable("GITHUB_RUN_ATTEMPT", "7")
            .WithEnvironmentVariable("WORKER_WORKFLOW_REF", "example/repo/.github/workflows/reproduce-flaky-tests.yml@refs/heads/main")
            .WithEnvironmentVariable("WORKER_WORKFLOW_SHA", WorkflowSha)
            .WithEnvironmentVariable("RUNNER_OS", "Linux")
            .WithEnvironmentVariable("WORKER_RUNNER_INDEX", "2");

        return await command.ExecuteAsync("-Operation", "Start", "-ResultsDirectory", $"\"{ResultsDirectory}\"");
    }

    private string CreateIteration(int iteration)
    {
        var directory = Directory.CreateDirectory(Path.Combine(ResultsDirectory, $"iteration-{iteration}")).FullName;
        File.WriteAllText(Path.Combine(directory, "test-output.log"), $"output {iteration}");

        return directory;
    }

    private async Task<CommandResult> RecordAsync(int iteration, int exitCode)
    {
        using var command = CreateCommand();

        return await command.ExecuteAsync("-Operation", "Iteration", "-ResultsDirectory", $"\"{ResultsDirectory}\"",
            "-Iteration", iteration.ToString(), "-ProcessExitCode", exitCode.ToString());
    }

    public void Dispose() => _workspace.Dispose();
}
