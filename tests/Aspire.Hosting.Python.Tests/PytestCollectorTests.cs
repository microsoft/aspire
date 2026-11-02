// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Backchannel;
using Aspire.Hosting.Cli;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TestResult = Aspire.Hosting.ApplicationModel.TestResult;

#pragma warning disable ASPIRETESTING001 // Exercises pytest collection through the common testing coordinator.

namespace Aspire.Hosting.Python.Tests;

public class PytestCollectorTests(ITestOutputHelper outputHelper)
{
    private const string Report = """
        <testsuites><testsuite tests="1" failures="0" errors="0" skipped="0">
          <testcase classname="test_api" name="test_pass[1]" time="0.125" />
        </testsuite></testsuites>
        """;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(99)]
    public async Task CollectionWaitsForProcessExitIncludingFailureAndDoesNotInferCancellation(int exitCode)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = CreateBuilder(workspace);
        var resource = AddResource(builder, workspace);
        using var notifications = ResourceNotificationServiceTestHelpers.Create();
        using var coordinator = new TestRunCoordinator(new DistributedApplicationModel(builder.Resources),
            notifications, TimeProvider.System, NullLogger<TestRunCoordinator>.Instance);
        var completion = coordinator.WaitForCompletionAsync(TestContext.Current.CancellationToken);
        Assert.False(completion.IsCompleted);
        File.WriteAllText(Path.Combine(workspace.Path, "runner reports", "pytest.xml"), Report);
        File.WriteAllText(Path.Combine(workspace.Path, "runner reports", "other.xml"), Report);
        await notifications.PublishUpdateAsync(resource.Resource, snapshot =>
            snapshot with { State = KnownResourceStates.Finished, ExitCode = exitCode });
        Assert.Equal(exitCode == 0, await completion.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        var events = await ReadAsync(coordinator);
        var result = Assert.Single(events, e => e.Result is not null).Result!;
        Assert.Equal(("tests", "test_api.test_pass[1]", "passed", 125d),
            (result.ResourceName, result.Name, result.Outcome, result.DurationMilliseconds));
        Assert.Equal("completed", events.Last(e => e.Collection is not null).Collection!.Status);
        Assert.Equal(Report, await File.ReadAllTextAsync(GetReportPath(resource), TestContext.Current.CancellationToken));
        Assert.Equal(new[] { "pytest.xml" }, Directory.GetFiles(Path.GetDirectoryName(GetReportPath(resource))!)
            .Select(Path.GetFileName).ToArray());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("inconsistent")]
    [InlineData("stale")]
    [InlineData("existing")]
    public async Task CollectorErrorsFailTheAggregateWithoutInventingResults(string failure)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = CreateBuilder(workspace);
        var resource = AddResource(builder, workspace);
        if (failure is "stale" or "existing")
        {
            File.WriteAllText(GetReportPath(resource), Report);
        }
        if (failure is not ("missing" or "stale"))
        {
            File.WriteAllText(Path.Combine(workspace.Path, "runner reports", "pytest.xml"),
                failure == "malformed" ? "<testsuite>" : failure == "inconsistent"
                    ? Report.Replace("failures=\"0\"", "failures=\"1\"", StringComparison.Ordinal) : Report);
        }
        using var notifications = ResourceNotificationServiceTestHelpers.Create();
        using var coordinator = new TestRunCoordinator(new DistributedApplicationModel(builder.Resources),
            notifications, TimeProvider.System, NullLogger<TestRunCoordinator>.Instance);
        var completion = coordinator.WaitForCompletionAsync(TestContext.Current.CancellationToken);
        await notifications.PublishUpdateAsync(resource.Resource, snapshot =>
            snapshot with { State = KnownResourceStates.Finished, ExitCode = 0 });
        Assert.False(await completion.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        var events = await ReadAsync(coordinator);
        Assert.All(events, e => Assert.Null(e.Result));
        var collection = events.Last(e => e.Collection is not null).Collection!;
        Assert.Equal("failed", collection.Status);
        Assert.NotEmpty(collection.Error!);
        if (failure is "stale" or "existing")
        {
            Assert.Equal(Report, await File.ReadAllTextAsync(GetReportPath(resource), TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task ExplicitJunitErrorFailsAZeroExitWithoutCancellingOtherResults()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = CreateBuilder(workspace);
        var resource = AddResource(builder, workspace);
        File.WriteAllText(Path.Combine(workspace.Path, "runner reports", "pytest.xml"), """
            <testsuite tests="2" failures="0" errors="1" skipped="0">
              <testcase name="setup"><error message="setup error">traceback</error></testcase>
              <testcase name="next" />
            </testsuite>
            """);
        using var notifications = ResourceNotificationServiceTestHelpers.Create();
        using var coordinator = new TestRunCoordinator(new DistributedApplicationModel(builder.Resources),
            notifications, TimeProvider.System, NullLogger<TestRunCoordinator>.Instance);
        var completion = coordinator.WaitForCompletionAsync(TestContext.Current.CancellationToken);
        await notifications.PublishUpdateAsync(resource.Resource, snapshot =>
            snapshot with { State = KnownResourceStates.Finished, ExitCode = 0 });
        Assert.False(await completion.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        var events = await ReadAsync(coordinator);
        Assert.Equal(new[] { "error", "passed" }, events.Where(e => e.Result is not null).Select(e => e.Result!.Outcome));
        Assert.Equal("completed", events.Last(e => e.Collection is not null).Collection!.Status);
    }

    [Fact]
    public async Task CancellationInterruptsCompletionWaitWithoutHarvesting()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = CreateBuilder(workspace);
        var resource = AddResource(builder, workspace);
        var options = resource.Resource.Annotations.OfType<TestingAnnotation>().Single();
        var published = new List<TestResult>();
        using var cancellation = new CancellationTokenSource();
        var context = new ResourceTestingContext(options.TestingContext!, "tests", options.OutputPath!,
            published.Add, new TaskCompletionSource<bool>().Task, cancellation.Token);
        var collection = options.Collector!(context);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            collection.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Empty(published);
    }

    [Theory]
    [InlineData("source-report")]
    [InlineData("source-directory")]
    [InlineData("destination-report")]
    [InlineData("destination-directory")]
    public async Task SymlinkReportsAndDirectoriesAreRejected(string link)
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Creating symlinks can require privileges unavailable in CI.
        }
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = CreateBuilder(workspace);
        var resource = AddResource(builder, workspace);
        var options = resource.Resource.Annotations.OfType<TestingAnnotation>().Single();
        var outside = workspace.CreateDirectory("outside").FullName;
        var target = Path.Combine(outside, "pytest.xml");
        File.WriteAllText(target, Report);
        var source = Path.Combine(workspace.Path, "runner reports");
        if (link == "destination-directory")
        {
            Directory.Move(options.OutputPath!, options.OutputPath! + "-original");
            Directory.CreateSymbolicLink(options.OutputPath!, outside);
        }
        else if (link == "source-directory")
        {
            Directory.Move(source, source + "-original");
            Directory.CreateSymbolicLink(source, outside);
        }
        else if (link == "source-report")
        {
            File.CreateSymbolicLink(Path.Combine(source, "pytest.xml"), target);
        }
        else
        {
            File.WriteAllText(Path.Combine(source, "pytest.xml"), Report);
            File.CreateSymbolicLink(GetReportPath(resource), target);
        }
        var context = new ResourceTestingContext(options.TestingContext!, "tests", options.OutputPath!,
            _ => Assert.Fail("Linked reports must not publish results."), Task.FromResult(true), TestContext.Current.CancellationToken);
        if (link == "destination-report")
        {
            await Assert.ThrowsAsync<IOException>(() => options.Collector!(context));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => options.Collector!(context));
        }
        Assert.Equal(Report, await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("pass")]
    [InlineData("fail")]
    [InlineData("teardown")]
    [InlineData("pass-teardown")]
    [InlineData("skip-teardown")]
    [InlineData("setup")]
    [InlineData("all-skipped")]
    [InlineData("collection")]
    [InlineData("empty")]
    [InlineData("disabled-junit")]
    [InlineData("xpass")]
    [InlineData("strict-xpass")]
    [RequiresTools(["uv"])]
    [OuterloopTest("Installs declared pytest dependencies and runs a real suite through DCP.")]
    public async Task RealPytestWritesJunitAndContributesToTheCommonAggregate(string scenario)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var directory = workspace.CreateDirectory("pytest project with spaces").FullName;
        File.WriteAllText(Path.Combine(directory, "pyproject.toml"), """
            [project]
            name = "aspire-pytest-fixture"
            version = "1.0.0"
            requires-python = ">=3.11"
            dependencies = ["pytest==9.0.2"]
            [tool.pytest.ini_options]
            addopts = "--junitxml=config-report.xml"
            """);
        if (scenario != "empty")
        {
            File.WriteAllText(Path.Combine(directory, "test_fixture.py"), """
                import os
                import pytest

                @pytest.fixture
                def lifecycle():
                    if os.environ["SCENARIO"] == "setup":
                        pytest.fail("intentional setup error")
                    yield
                    if os.environ["SCENARIO"] in ("teardown", "pass-teardown", "skip-teardown"):
                        pytest.fail("intentional teardown error")

                @pytest.mark.parametrize("value", [1, 2])
                def test_values(value):
                    assert value > 0

                def test_outcome(lifecycle):
                    if os.environ["SCENARIO"] == "skip-teardown":
                        pytest.skip("intentional call skip")
                    assert os.environ["REFERENCE"] == "configured"
                    assert os.environ["SCENARIO"] not in ("fail", "teardown"), "intentional call failure"

                @pytest.mark.skip(reason="intentional skip")
                def test_skip():
                    pass

                @pytest.mark.xfail(reason="intentional xfail", strict=os.environ["SCENARIO"] == "strict-xpass")
                def test_xfail():
                    assert os.environ["SCENARIO"] in ("xpass", "strict-xpass")
                """);
        }
        if (scenario == "collection")
        {
            File.WriteAllText(Path.Combine(directory, "test_collection.py"), "raise ImportError('intentional collection error')\n");
        }
        using var builder = CreateBuilder(workspace);
        var reference = builder.AddParameter("reference", "configured");
        var resource = builder.AddPytest("tests", directory)
            .WithUv()
            .WithVirtualEnvironment("custom venv", createIfNotExists: false)
            .WithEnvironment("REFERENCE", reference)
            .WithEnvironment("SCENARIO", scenario)
            .WithEnvironment("PYTEST_ADDOPTS", "--junitxml=environment-report.xml");
        if (scenario == "disabled-junit")
        {
            resource.WithArgs("-p", "no:junitxml");
        }
        if (scenario == "all-skipped")
        {
            resource.WithArgs("-k", "test_skip");
        }
        await using var app = builder.Build();
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        startup.CancelAfter(TimeSpan.FromMinutes(3));
        await app.StartAsync(startup.Token);
        var arguments = await ArgumentEvaluator.GetArgumentListAsync(resource.Resource, app.Services);
        var runnerReport = Assert.Single(arguments, argument =>
            argument.StartsWith("--junitxml=", StringComparison.Ordinal))["--junitxml=".Length..];
        try
        {
            using var completion = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            completion.CancelAfter(TimeSpan.FromMinutes(2));
            var events = new List<TestRunEvent>();
            await foreach (var item in app.Services.GetRequiredService<TestRunCoordinator>().WatchAsync(completion.Token))
            {
                events.Add(item);
            }
            Assert.Equal(scenario is "pass" or "all-skipped" or "xpass", events.Last().Passed);
            var results = events.Where(e => e.Result is not null).Select(e => e.Result!).ToArray();
            var expected = scenario switch
            {
                "pass" => (Passed: 3, Failed: 0, Error: 0, Skipped: 2),
                "fail" => (Passed: 2, Failed: 1, Error: 0, Skipped: 2),
                "teardown" => (Passed: 2, Failed: 1, Error: 1, Skipped: 2),
                "pass-teardown" or "setup" => (Passed: 2, Failed: 0, Error: 1, Skipped: 2),
                "skip-teardown" => (Passed: 2, Failed: 0, Error: 1, Skipped: 3),
                "all-skipped" => (Passed: 0, Failed: 0, Error: 0, Skipped: 1),
                "collection" => (Passed: 0, Failed: 0, Error: 1, Skipped: 0),
                "xpass" => (Passed: 4, Failed: 0, Error: 0, Skipped: 1),
                "strict-xpass" => (Passed: 3, Failed: 1, Error: 0, Skipped: 1),
                _ => (Passed: 0, Failed: 0, Error: 0, Skipped: 0)
            };
            Assert.Equal(expected, (results.Count(r => r.Outcome == "passed"),
                results.Count(r => r.Outcome == "failed"), results.Count(r => r.Outcome == "error"),
                results.Count(r => r.Outcome == "skipped")));
            Assert.Equal(expected.Passed + expected.Failed + expected.Error + expected.Skipped, results.Length);
            Assert.Equal(scenario == "disabled-junit" ? "failed" : "completed",
                events.Last(e => e.Collection is not null).Collection!.Status);
            Assert.Equal(scenario != "disabled-junit", File.Exists(GetReportPath(resource)));
            Assert.Equal(Path.Combine(builder.Configuration[KnownConfigNames.TestOutputPath]!, "tests", "pytest.xml"),
                GetReportPath(resource));
            if (scenario != "disabled-junit")
            {
                Assert.Equal(await File.ReadAllTextAsync(runnerReport, completion.Token),
                    await File.ReadAllTextAsync(GetReportPath(resource), completion.Token));
            }
            if (scenario is "fail" or "teardown" or "pass-teardown" or "skip-teardown" or "setup" or "collection")
            {
                Assert.All(results.Where(r => r.Outcome is "failed" or "error"), result => Assert.Contains("intentional", result.Message));
            }
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();
            Assert.False(Directory.Exists(Path.GetDirectoryName(runnerReport)));
        }
    }

    private IDistributedApplicationTestingBuilder CreateBuilder(TemporaryWorkspace workspace)
    {
        var builder = TestDistributedApplicationBuilder.Create(options => options.DisableDashboard = true, outputHelper);
        builder.Configuration[KnownConfigNames.TestOutputPath] = Path.Combine(workspace.Path, "test output");
        return builder;
    }

    private static IResourceBuilder<PythonAppResource> AddResource(
        IDistributedApplicationBuilder builder, TemporaryWorkspace workspace)
    {
        var reports = workspace.CreateDirectory("runner reports").FullName;
        return builder.AddPytest("tests", workspace.Path)
            .WithTesting(context => PytestJunitReport.CollectAsync(context, reports));
    }

    private static string GetReportPath(IResourceBuilder<PythonAppResource> resource)
    {
        return Path.Combine(resource.Resource.Annotations.OfType<TestingAnnotation>().Single().OutputPath!, "pytest.xml");
    }

    private static async Task<List<TestRunEvent>> ReadAsync(TestRunCoordinator coordinator)
    {
        var events = new List<TestRunEvent>();
        await foreach (var item in coordinator.WatchAsync(TestContext.Current.CancellationToken))
        {
            events.Add(item);
        }
        return events;
    }
}
