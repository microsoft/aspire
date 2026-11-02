// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Testing;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Microsoft.DotNet.RemoteExecutor;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Spectre.Console;

namespace Aspire.Cli.Tests.Testing;

public class TestRunReporterTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void CreationProtectsArtifactsBeforeAppHostStartup()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var context = CreateContext(workspace);
        var reporter = CreateReporter(context);

        Assert.Equal(Path.Combine(context.AspireHomeDirectory.FullName, "testruns"), Path.GetDirectoryName(reporter.OutputPath));
        AssertPrivateDirectory(Path.GetDirectoryName(reporter.OutputPath)!);
        AssertPrivateDirectory(reporter.OutputPath);
        AssertPrivateFile(reporter.SummaryPath);
        var summary = JsonNode.Parse(File.ReadAllText(reporter.SummaryPath))!;
        Assert.Equal("running", summary["status"]!.GetValue<string>());
        Assert.Null(summary["completedAt"]);
        Assert.Null(summary["cliExitCode"]);
        Assert.Empty(summary["resources"]!.AsArray());
        Assert.Equal(["summary.json"], Directory.GetFiles(reporter.OutputPath).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("0100")]
    [InlineData("0700")]
    public void RestrictiveUmaskStillCreatesAccessibleOwnerOnlyArtifacts(string umask)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix umask does not apply on Windows.");

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var context = CreateContext(workspace);
        Directory.CreateDirectory(context.AspireHomeDirectory.FullName);
        var launcher = ProcessTestHelpers.CreateScript(workspace.WorkspaceRoot, "with-umask",
            $"umask {umask}\nexec \"$ASPIRE_TEST_CHILD_EXECUTABLE\" \"$@\"", "");

        // Change umask only in a child so parallel tests keep their process-wide permissions.
        using var invocation = RemoteExecutor.Invoke(static directory =>
        {
            var context = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(directory));
            var reporter = CreateReporter(context);

            AssertPrivateDirectory(Path.GetDirectoryName(reporter.OutputPath)!);
            AssertPrivateDirectory(reporter.OutputPath);
            AssertPrivateFile(reporter.SummaryPath);
            Assert.Equal("running", JsonNode.Parse(File.ReadAllText(reporter.SummaryPath))!["status"]!.GetValue<string>());

            reporter.Complete(CommandResult.Success());

            AssertPrivateFile(reporter.SummaryPath);
            Assert.Equal("passed", JsonNode.Parse(File.ReadAllText(reporter.SummaryPath))!["status"]!.GetValue<string>());
        }, workspace.Path, new RemoteInvokeOptions { Start = false });
        var startInfo = invocation.Process.StartInfo;
        startInfo.Environment["ASPIRE_TEST_CHILD_EXECUTABLE"] = startInfo.FileName;
        startInfo.FileName = launcher;
        invocation.Process.Start();
    }

    [Fact]
    public void ExistingRunDirectoryPermissionsAreNotRepairedDuringPersistence()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var reporter = CreateReporter(CreateContext(workspace));
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead;
        File.SetUnixFileMode(reporter.OutputPath, mode);

        Assert.Throws<IOException>(() => reporter.Complete(CommandResult.Success()));

        Assert.Equal(mode, File.GetUnixFileMode(reporter.OutputPath));
        Assert.Equal("running", JsonNode.Parse(File.ReadAllText(reporter.SummaryPath))!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task ConcurrentInvocationsHaveDifferentPrivateOutputDirectories()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var context = CreateContext(workspace);
        var reports = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => CreateReporter(context))));

        Assert.Equal(16, reports.Select(r => r.OutputPath).Distinct(StringComparers.FileSystemPath).Count());
        foreach (var report in reports)
        {
            AssertPrivateDirectory(report.OutputPath);
            AssertPrivateFile(report.SummaryPath);
        }
        Assert.Empty(Directory.GetFiles(Path.Combine(context.AspireHomeDirectory.FullName, "testruns")));
    }

    [Fact]
    public void ExistingTestRunsParentIsHardenedWithoutChangingAspireHome()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var context = CreateContext(workspace);
        var parent = Path.Combine(context.AspireHomeDirectory.FullName, "testruns");
        Directory.CreateDirectory(parent);
        if (!OperatingSystem.IsWindows())
        {
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
            File.SetUnixFileMode(context.AspireHomeDirectory.FullName, mode);
            File.SetUnixFileMode(parent, mode);
            CreateReporter(context);
            Assert.Equal(mode, File.GetUnixFileMode(context.AspireHomeDirectory.FullName));
        }
        else
        {
            CreateReporter(context);
        }
        AssertPrivateDirectory(parent);
    }

    [Fact]
    public void LinkedAspireHomeIsRejectedWithoutChangingItsTarget()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Creating links requires privileges not present on ordinary Windows test agents.
        }
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var context = CreateContext(workspace);
        var target = workspace.CreateDirectory("unrelated");
        Directory.CreateSymbolicLink(context.AspireHomeDirectory.FullName, target.FullName);
        var mode = File.GetUnixFileMode(target.FullName);

        Assert.Throws<IOException>(() => CreateReporter(context));
        Assert.Equal(mode, File.GetUnixFileMode(target.FullName));
        Assert.Empty(target.GetFileSystemInfos());
    }

    [Fact]
    public void OtherWritableAspireHomeIsRejectedWithoutChangingPermissions()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var context = CreateContext(workspace);
        Directory.CreateDirectory(context.AspireHomeDirectory.FullName);
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupWrite | UnixFileMode.GroupExecute;
        File.SetUnixFileMode(context.AspireHomeDirectory.FullName, mode);

        Assert.Throws<IOException>(() => CreateReporter(context));
        Assert.Equal(mode, File.GetUnixFileMode(context.AspireHomeDirectory.FullName));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SummaryPreservesReplicasAndFinalOutcomesBeforeReturningCompletion(bool supportsLinks)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var context = CreateContext(workspace);
        var paths = new List<string>();
        var interaction = new TestInteractionService { SupportsLinks = supportsLinks, DisplaySubtleMessageCallback = paths.Add };
        var reporter = CreateReporter(context, interaction, interactive: true);
        var failed = Resource("tests-1", "Exited", 1, "failed");
        var passed = Resource("tests-2", "Finished", 0, "passed");
        var backchannel = new TestAppHostBackchannel
        {
            WatchTestRunAsyncCallback = _ => Events(
                new TestRunEvent { Resource = failed },
                new TestRunEvent { Resource = passed },
                new TestRunEvent { Resource = passed with { ResourceId = null, State = "Hidden", ExitCode = null, Outcome = "pending" } },
                new TestRunEvent { Resource = passed with { ExitCode = null, Outcome = "pending" } },
                new TestRunEvent { Passed = false })
        };

        Assert.False(await reporter.WaitForCompletionAsync(backchannel, TestContext.Current.CancellationToken));
        var beforeStop = JsonNode.Parse(File.ReadAllText(reporter.SummaryPath))!;
        Assert.Equal(2, beforeStop["resources"]!.AsArray().Count);
        Assert.Equal("passed", beforeStop["resources"]![1]!["outcome"]!.GetValue<string>());
        reporter.Complete(CommandResult.Failure(CliExitCodes.TestRunFailed));
        AssertPrivateFile(reporter.SummaryPath);
        Assert.Contains(interaction.DisplayedMessages, message => message.Message == TestCommandStrings.RunningHeader);
        Assert.Empty(interaction.DisplayedRenderables);
        var expectedLink = Aspire.Cli.Utils.MarkupHelpers.SafeFileLink(interaction, reporter.SummaryPath);
        Assert.Contains(paths, message => message.Contains(expectedLink, StringComparison.Ordinal));

        var summary = JsonNode.Parse(File.ReadAllText(reporter.SummaryPath))!;
        summary["runId"] = "run-id";
        summary["outputPath"] = "output-directory";
        summary["summaryPath"] = "summary-file";
        await Verify(summary.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), "json")
            .UseDirectory("Snapshots").UseParameters(supportsLinks);
    }

    [Fact]
    public async Task CancellationPersistsHonestIncompleteResourceOutcomes()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var reporter = CreateReporter(CreateContext(workspace));
        var backchannel = new TestAppHostBackchannel
        {
            WatchTestRunAsyncCallback = _ => Events(new TestRunEvent { Resource = Resource("tests-1", "Running", null, "pending") })
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reporter.WaitForCompletionAsync(backchannel, TestContext.Current.CancellationToken));
        reporter.Complete(CommandResult.Cancelled());
        var summary = JsonNode.Parse(File.ReadAllText(reporter.SummaryPath))!;
        Assert.Equal("cancelled", summary["status"]!.GetValue<string>());
        Assert.Equal(130, summary["cliExitCode"]!.GetValue<int>());
        Assert.Equal("incomplete", summary["resources"]![0]!["outcome"]!.GetValue<string>());
        Assert.Null(summary["resources"]![0]!["exitCode"]);
    }

    [Fact]
    public async Task ResourceTransitionsAndDelayedTestResultsHaveDistinctRenderingAndFinalCounts()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        var reporter = CreateReporter(CreateContext(workspace), interaction, interactive: true);
        var resource = Resource("tests-1", "Starting", null, "pending") with { ExpectedReplicas = 1 };
        var cargo = resource with { ResourceName = "cargo", ResourceId = "cargo-1", State = "Running" };
        var backchannel = new TestAppHostBackchannel { WatchTestRunAsyncCallback = _ => Stream() };

        Assert.False(await reporter.WaitForCompletionAsync(backchannel, TestContext.Current.CancellationToken));
        reporter.ReportCompletedResources(passed: false);
        var beforeShutdown = interaction.DisplayedMessages.ToArray();
        reporter.Complete(CommandResult.Failure(CliExitCodes.WaitTimeout));
        Assert.Equal(beforeShutdown, interaction.DisplayedMessages);
        Assert.Empty(interaction.DisplayedRenderables);
        await Verify(interaction.DisplayedMessages.Select(m => new { Emoji = m.Emoji.Name, m.Message }))
            .UseDirectory("Snapshots");

        async IAsyncEnumerable<TestRunEvent> Stream()
        {
            await Task.CompletedTask;
            yield return new TestRunEvent { Resource = resource with { ResourceId = null, State = null } };
            yield return new TestRunEvent { Resource = resource with { ResourceId = null } };
            yield return new TestRunEvent { Resource = resource with { ExitCode = 99 } };
            yield return new TestRunEvent { Resource = resource with { State = "Running", ExitCode = 99 } };
            yield return new TestRunEvent { Resource = cargo };
            yield return new TestRunEvent { Collection = new TestCollectionState { ResourceName = "cargo", Status = "notConfigured" } };
            yield return new TestRunEvent { Resource = cargo with { State = "Finished", ExitCode = 0, Outcome = "passed" } };
            yield return new TestRunEvent { Collection = new TestCollectionState { ResourceName = "tests", CollectorCount = 1, Status = "running" } };
            yield return new TestRunEvent { Resource = resource with { State = "Finished", ExitCode = 0 } };
            var processTransitionCount = interaction.DisplayedMessages.Count;
            yield return new TestRunEvent { Resource = resource with { State = "Finished", ExitCode = 0, Outcome = "passed" } };
            Assert.Equal(processTransitionCount, interaction.DisplayedMessages.Count);
            foreach (var outcome in new[] { "failed", "passed", "skipped", "inconclusive", "error", "timedOut", "cancelled" })
            {
                yield return new TestRunEvent
                {
                    Result = new PublishedTestResult { ResourceName = "tests", Name = $"fixture-{outcome}", Outcome = outcome }
                };
            }
            Assert.Equal(0, interaction.DisplayedMessages.Count(m => m.Emoji.Name == KnownEmojis.BarChart.Name));
            yield return new TestRunEvent { Collection = new TestCollectionState { ResourceName = "tests", CollectorCount = 1, Status = "completed" } };
            Assert.Equal(1, interaction.DisplayedMessages.Count(m => m.Emoji.Name == KnownEmojis.BarChart.Name));
            yield return new TestRunEvent { Passed = false };
        }
    }

    [Theory]
    [InlineData("failed", false, "red")]
    [InlineData("passed", true, "green")]
    [InlineData("skipped", true, "yellow")]
    [InlineData("inconclusive", false, "red")]
    [InlineData("error", false, "red")]
    [InlineData("timedOut", false, "red")]
    [InlineData("cancelled", false, "red")]
    public async Task PublishedOutcomePreservesIndividualIdentityAndTruthfulResourceCounts(string outcome, bool passed, string color)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        var reporter = CreateReporter(CreateContext(workspace), interaction, interactive: true);
        var backchannel = new TestAppHostBackchannel
        {
            WatchTestRunAsyncCallback = _ => Events(
                new TestRunEvent { Resource = Resource("tests-1", "Finished", 0, "passed") with { ExpectedReplicas = 1 } },
                new TestRunEvent { Result = new PublishedTestResult { ResourceName = "tests", Name = "fixture", Outcome = outcome } },
                new TestRunEvent { Collection = new TestCollectionState { ResourceName = "tests", CollectorCount = 1, Status = "completed" } },
                new TestRunEvent { Passed = passed })
        };
        Assert.Equal(passed, await reporter.WaitForCompletionAsync(backchannel, TestContext.Current.CancellationToken));
        reporter.Complete(passed ? CommandResult.Success() : CommandResult.Failure(CliExitCodes.TestRunFailed));

        var displayOutcome = outcome == "timedOut" ? TestCommandStrings.TimedOut : outcome;
        Assert.Equal($"Test [teal]tests[/] / [blue]fixture[/]: [{color}]{displayOutcome}[/]",
            Assert.Single(interaction.DisplayedMessages, m => m.Message.StartsWith("Test [", StringComparison.Ordinal)).Message);
        var summary = JsonNode.Parse(File.ReadAllText(reporter.SummaryPath))!;
        Assert.Equal(outcome, Assert.Single(summary["testResults"]!.AsArray())!["outcome"]!.GetValue<string>());
        Assert.Equal(passed ? "passed" : "failed", summary["status"]!.GetValue<string>());
        Assert.Equal(passed ? 0 : CliExitCodes.TestRunFailed, summary["cliExitCode"]!.GetValue<int>());
        var counts = summary["publishedResults"]!;
        var expected = (Passed: outcome == "passed" ? 1 : 0, Failed: passed ? 0 : 1, Other: outcome == "skipped" ? 1 : 0);
        Assert.Equal(expected, (counts["passed"]!.GetValue<int>(), counts["failed"]!.GetValue<int>(), counts["skipped"]!.GetValue<int>()));
        Assert.Equal($"Resource [teal]tests[/]: [{(passed ? "green" : "red")}]{(passed ? "passed" : "failed")}[/]; " +
            $"[green]{expected.Passed}[/] passed, [red]{expected.Failed}[/] failed, [yellow]{expected.Other}[/] other",
            Assert.Single(interaction.DisplayedMessages, m => m.Emoji.Name == KnownEmojis.BarChart.Name).Message);
    }

    [Fact]
    public async Task AllPublishedOutcomesRetainSpecificNamesAndTotalsInJson()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        var reporter = TestRunReporter.Create(CreateContext(workspace), interaction, interactive: true,
            OutputFormat.Json, new FakeTimeProvider(), NullLogger.Instance);
        var items = new List<TestRunEvent>
        {
            new() { Resource = Resource("tests-1", "Finished", 0, "passed") with { ExpectedReplicas = 1 } }
        };
        items.AddRange(new[] { "failed", "passed", "skipped", "inconclusive", "error", "timedOut", "cancelled" }
            .Select(outcome => new TestRunEvent
            {
                Result = new PublishedTestResult { ResourceName = "tests", Name = $"fixture-{outcome}", Outcome = outcome }
            }));
        items.Add(new TestRunEvent { Collection = new TestCollectionState { ResourceName = "tests", CollectorCount = 1, Status = "completed" } });
        items.Add(new TestRunEvent { Passed = false });
        var backchannel = new TestAppHostBackchannel { WatchTestRunAsyncCallback = _ => Events(items.ToArray()) };
        Assert.False(await reporter.WaitForCompletionAsync(backchannel, TestContext.Current.CancellationToken));
        reporter.Complete(CommandResult.Failure(CliExitCodes.TestRunFailed));
        Assert.Empty(interaction.DisplayedMessages);
        var stdout = Assert.Single(interaction.DisplayedRawText);
        Assert.Equal(ConsoleOutput.Standard, stdout.ConsoleOverride);
        Assert.Equal(File.ReadAllText(reporter.SummaryPath), stdout.Text);
        var summary = JsonNode.Parse(stdout.Text)!;
        summary["runId"] = "run-id";
        summary["outputPath"] = "output-directory";
        summary["summaryPath"] = "summary-file";
        await Verify(summary.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), "json")
            .UseDirectory("Snapshots");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TestLogMarkupEscapesNamesAndRespectsConsoleColorCapabilities(bool color)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var context = CreateContext(workspace);
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = color ? AnsiSupport.Yes : AnsiSupport.No,
            ColorSystem = color ? ColorSystemSupport.TrueColor : ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output)),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
        });
        console.Profile.Width = 200;
        var realInteraction = new ConsoleInteractionService(new ConsoleEnvironment(console, console, TextReader.Null),
            context, TestHelpers.CreateInteractiveHostEnvironment(), new EnvironmentProcessPathProvider(),
            NullLoggerFactory.Instance, new ConsoleLogBufferContext());
        var interaction = new TestInteractionService
        {
            DisplayMessageCallback = (emoji, message, target) => realInteraction.DisplayMessage(emoji, message, allowMarkup: true, consoleOverride: target)
        };
        var reporter = CreateReporter(context, interaction, interactive: true);
        var resource = Resource("tests-1", "Running", null, "pending") with { ResourceName = "resource[red]", ExpectedReplicas = 1 };
        var backchannel = new TestAppHostBackchannel
        {
            WatchTestRunAsyncCallback = _ => Events(
                new TestRunEvent { Resource = resource },
                new TestRunEvent { Result = new PublishedTestResult { ResourceName = resource.ResourceName, Name = "test[blue]", Outcome = "skipped" } },
                new TestRunEvent { Collection = new TestCollectionState { ResourceName = resource.ResourceName, CollectorCount = 1, Status = "completed" } },
                new TestRunEvent { Resource = resource with { State = "Finished", ExitCode = 0, Outcome = "passed" } },
                new TestRunEvent { Passed = true })
        };
        Assert.True(await reporter.WaitForCompletionAsync(backchannel, TestContext.Current.CancellationToken));
        reporter.Complete(CommandResult.Success());
        Assert.Equal("Test [teal]resource[[red]][/] / [blue]test[[blue]][/]: [yellow]skipped[/]",
            Assert.Single(interaction.DisplayedMessages, m => m.Message.StartsWith("Test [", StringComparison.Ordinal)).Message);
        Assert.Equal(color, output.ToString().Contains('\u001b'));
        if (!color)
        {
            await Verify(output.ToString()).UseDirectory("Snapshots");
        }
    }

    [Theory]
    [InlineData(CliExitCodes.WaitTimeout)]
    [InlineData(CliExitCodes.Cancelled)]
    public async Task InterruptedCollectionShowsPublishedSoFarInsteadOfFinalTotals(int exitCode)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        var reporter = CreateReporter(CreateContext(workspace), interaction, interactive: true);
        var resource = Resource("tests-1", "Finished", 0, "passed") with { ExpectedReplicas = 1 };
        var backchannel = new TestAppHostBackchannel
        {
            WatchTestRunAsyncCallback = _ => Events(
                new TestRunEvent { Resource = resource },
                new TestRunEvent { Collection = new TestCollectionState { ResourceName = "tests", CollectorCount = 1, Status = "running" } },
                new TestRunEvent { Result = new PublishedTestResult { ResourceName = "tests", Name = "partial", Outcome = "passed" } })
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reporter.WaitForCompletionAsync(backchannel, TestContext.Current.CancellationToken));
        Assert.Equal(0, interaction.DisplayedMessages.Count(m => m.Emoji.Name == KnownEmojis.BarChart.Name));
        reporter.Complete(CommandResult.Failure(exitCode, errorMessage: "interrupted collection"));
        Assert.Equal("Resource [teal]tests[/]: [yellow]incomplete[/]; published so far: [green]1[/] passed, [red]0[/] failed, [yellow]0[/] other",
            Assert.Single(interaction.DisplayedMessages, m => m.Emoji.Name == KnownEmojis.BarChart.Name).Message);
        Assert.Empty(interaction.DisplayedRenderables);
        await Verify(interaction.DisplayedMessages.Select(m => m.Message)).UseDirectory("Snapshots").UseParameters(exitCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResourceCountsWaitForEveryReplicaAndKeepProcessAndCollectorFailuresIndependent(bool processFails)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        var reporter = CreateReporter(CreateContext(workspace), interaction, interactive: true);
        var backchannel = new TestAppHostBackchannel { WatchTestRunAsyncCallback = _ => Stream() };
        Assert.False(await reporter.WaitForCompletionAsync(backchannel, TestContext.Current.CancellationToken));
        reporter.Complete(CommandResult.Failure(CliExitCodes.TestRunFailed));
        Assert.Equal(processFails ? [] : new[] { "collector failed" }, interaction.DisplayedErrors);
        Assert.Single(interaction.DisplayedMessages, m => m.Emoji.Name == KnownEmojis.BarChart.Name);
        await Verify(interaction.DisplayedMessages.Select(m => m.Message)).UseDirectory("Snapshots").UseParameters(processFails);

        async IAsyncEnumerable<TestRunEvent> Stream()
        {
            await Task.CompletedTask;
            yield return new TestRunEvent { Resource = Resource("tests-1", "Finished", 0, "passed") };
            yield return new TestRunEvent { Resource = Resource("tests-2", "Running", null, "pending") };
            yield return new TestRunEvent { Result = new PublishedTestResult { ResourceName = "tests", Name = "fixture", Outcome = "passed" } };
            yield return new TestRunEvent
            {
                Collection = new TestCollectionState
                {
                    ResourceName = "tests", CollectorCount = 1, Status = processFails ? "completed" : "failed",
                    Error = processFails ? null : "collector failed"
                }
            };
            Assert.Equal(0, interaction.DisplayedMessages.Count(m => m.Emoji.Name == KnownEmojis.BarChart.Name));
            yield return new TestRunEvent { Resource = Resource("tests-2", "Exited", processFails ? 1 : 0, processFails ? "failed" : "passed") };
            yield return new TestRunEvent { Passed = false };
        }
    }

    [Fact]
    public async Task UnknownTerminalExitCodeIsExplicitWithoutInventingTestCounts()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        var reporter = CreateReporter(CreateContext(workspace), interaction, interactive: true);
        var backchannel = new TestAppHostBackchannel
        {
            WatchTestRunAsyncCallback = _ => Events(
                new TestRunEvent { Resource = Resource("tests-1", "Exited", null, "failed") with { ExpectedReplicas = 1 } },
                new TestRunEvent { Collection = new TestCollectionState { ResourceName = "tests", Status = "notConfigured" } },
                new TestRunEvent { Passed = false })
        };
        Assert.False(await reporter.WaitForCompletionAsync(backchannel, TestContext.Current.CancellationToken));
        reporter.Complete(CommandResult.Failure(CliExitCodes.TestRunFailed));
        Assert.Empty(interaction.DisplayedRenderables);
        await Verify(interaction.DisplayedMessages.Select(m => m.Message)).UseDirectory("Snapshots");
    }

    [Theory]
    [InlineData("failedResult")]
    [InlineData("inconclusive")]
    [InlineData("error")]
    [InlineData("timedOut")]
    [InlineData("cancelled")]
    [InlineData("unknownOutcome")]
    [InlineData("unknownResource")]
    [InlineData("negativeDuration")]
    [InlineData("runningCollection")]
    [InlineData("failedCollection")]
    [InlineData("negativeCollectorCount")]
    [InlineData("unknownCollectionState")]
    public async Task InvalidResultsAndIncompleteCollectionCannotProduceAggregateSuccess(string scenario)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var reporter = CreateReporter(CreateContext(workspace));
        var resource = Resource("tests-1", "Finished", 0, "passed") with { ExpectedReplicas = 1 };
        var item = scenario switch
        {
            "failedResult" or "inconclusive" or "error" or "timedOut" or "cancelled" => new TestRunEvent
            {
                Result = new PublishedTestResult
                {
                    ResourceName = resource.ResourceName, Name = "example",
                    Outcome = scenario == "failedResult" ? "failed" : scenario
                }
            },
            "unknownOutcome" => new TestRunEvent
            {
                Result = new PublishedTestResult { ResourceName = resource.ResourceName, Name = "example", Outcome = "unknown" }
            },
            "unknownResource" => new TestRunEvent
            {
                Result = new PublishedTestResult { ResourceName = "unregistered", Name = "example", Outcome = "passed" }
            },
            "negativeDuration" => new TestRunEvent
            {
                Result = new PublishedTestResult { ResourceName = resource.ResourceName, Name = "example", Outcome = "passed", DurationMilliseconds = -1 }
            },
            _ => new TestRunEvent
            {
                Collection = new TestCollectionState
                {
                    ResourceName = resource.ResourceName,
                    CollectorCount = scenario == "negativeCollectorCount" ? -1 : 1,
                    Status = scenario switch
                    {
                        "runningCollection" => "running",
                        "failedCollection" => "failed",
                        "negativeCollectorCount" => "completed",
                        _ => "unknown"
                    }
                }
            }
        };
        var backchannel = new TestAppHostBackchannel
        {
            WatchTestRunAsyncCallback = _ => Events(
                new TestRunEvent { Resource = resource }, item, new TestRunEvent { Passed = true })
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reporter.WaitForCompletionAsync(backchannel, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AggregateSuccessCannotHideMissingReplicaOutcomes()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var reporter = CreateReporter(CreateContext(workspace));
        var backchannel = new TestAppHostBackchannel
        {
            WatchTestRunAsyncCallback = _ => Events(
                new TestRunEvent { Resource = Resource("tests-1", "Finished", 0, "passed") },
                new TestRunEvent { Passed = true })
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reporter.WaitForCompletionAsync(backchannel, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("Finished", 0, "passed", null)]
    [InlineData("Finished", 1, "passed", "tests-1")]
    [InlineData("Finished", null, "passed", "tests-1")]
    [InlineData("Running", 0, "passed", "tests-1")]
    [InlineData(null, 0, "passed", "tests-1")]
    [InlineData("Running", null, "invalid", "tests-1")]
    public async Task InvalidResourceOutcomesCannotPass(string? state, int? exitCode, string outcome, string? resourceId)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var reporter = CreateReporter(CreateContext(workspace));
        var backchannel = new TestAppHostBackchannel
        {
            WatchTestRunAsyncCallback = _ => Events(
                new TestRunEvent { Resource = Resource("tests-1", "Finished", exitCode, outcome) with { ResourceId = resourceId, State = state } },
                new TestRunEvent { Passed = true })
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reporter.WaitForCompletionAsync(backchannel, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ModelPlaceholdersDoNotCountAsAdditionalPassingReplicas()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var reporter = CreateReporter(CreateContext(workspace));
        var backchannel = new TestAppHostBackchannel
        {
            WatchTestRunAsyncCallback = _ => Events(
                new TestRunEvent { Resource = Resource("tests-1", "Finished", 0, "passed") },
                new TestRunEvent { Resource = Resource("tests-2", "finished", 0, "passed") },
                new TestRunEvent { Resource = new TestResourceState { ResourceName = "tests", State = "Waiting", ExpectedReplicas = 2 } },
                new TestRunEvent { Passed = true })
        };

        Assert.True(await reporter.WaitForCompletionAsync(backchannel, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConflictingFinalOutcomesCannotOverwriteFailure()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var reporter = CreateReporter(CreateContext(workspace));
        var backchannel = new TestAppHostBackchannel
        {
            WatchTestRunAsyncCallback = _ => Events(
                new TestRunEvent { Resource = Resource("tests-1", "Finished", 1, "failed") },
                new TestRunEvent { Resource = Resource("tests-1", "Finished", 0, "passed") },
                new TestRunEvent { Passed = true })
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reporter.WaitForCompletionAsync(backchannel, TestContext.Current.CancellationToken));
        reporter.Complete(CommandResult.Failure(CliExitCodes.TestRunFailed));
        var summary = JsonNode.Parse(File.ReadAllText(reporter.SummaryPath))!;
        Assert.Equal("failed", summary["resources"]![0]!["outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task LateRpcFramesCannotOverwriteFinalCancelledSummary()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var reporter = CreateReporter(CreateContext(workspace));
        using var cancellation = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backchannel = new TestAppHostBackchannel { WatchTestRunAsyncCallback = _ => DelayedEvents() };
        var waiting = reporter.WaitForCompletionAsync(backchannel, cancellation.Token);
        await ready.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        reporter.Complete(CommandResult.Cancelled());
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        var summary = JsonNode.Parse(File.ReadAllText(reporter.SummaryPath))!;
        Assert.Equal("cancelled", summary["status"]!.GetValue<string>());
        Assert.Equal("incomplete", summary["resources"]![0]!["outcome"]!.GetValue<string>());
        Assert.Equal("Running", summary["resources"]![0]!["state"]!.GetValue<string>());

        async IAsyncEnumerable<TestRunEvent> DelayedEvents()
        {
            yield return new TestRunEvent { Resource = Resource("tests-1", "Running", null, "pending") };
            ready.SetResult();
            await release.Task;
            yield return new TestRunEvent { Resource = Resource("tests-1", "Finished", 0, "passed") };
            yield return new TestRunEvent { Passed = true };
        }
    }

    [Fact]
    public void LinkedSummaryIsRejectedWithoutOverwritingTheTarget()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var reporter = CreateReporter(CreateContext(workspace));
        var target = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "untouched.txt"));
        File.WriteAllText(target.FullName, "unchanged");
        File.Delete(reporter.SummaryPath);
        File.CreateSymbolicLink(reporter.SummaryPath, target.FullName);

        Assert.Throws<IOException>(() => reporter.Complete(CommandResult.Success()));
        Assert.Equal("unchanged", File.ReadAllText(target.FullName));
    }

    private static TestResourceState Resource(string id, string state, int? exitCode, string outcome) => new()
    {
        ResourceName = "tests",
        ResourceId = id,
        ExpectedReplicas = 2,
        State = state,
        ExitCode = exitCode,
        Outcome = outcome
    };

    private static async IAsyncEnumerable<TestRunEvent> Events(params TestRunEvent[] events)
    {
        await Task.CompletedTask;
        foreach (var item in events)
        {
            yield return item;
        }
    }

    private static CliExecutionContext CreateContext(TemporaryWorkspace workspace)
    {
        var context = workspace.CreateExecutionContext();
        Directory.CreateDirectory(context.HomeDirectory.FullName);

        return context;
    }

    private static TestRunReporter CreateReporter(CliExecutionContext context, TestInteractionService? interaction = null, bool interactive = false) =>
        TestRunReporter.Create(context, interaction ?? new TestInteractionService(), interactive,
            OutputFormat.Table, new FakeTimeProvider(new DateTimeOffset(2026, 10, 31, 6, 0, 0, TimeSpan.Zero)), NullLogger.Instance);

    private static void AssertPrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var security = new DirectoryInfo(path).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected);
            Assert.Equal(identity.User, security.GetOwner(typeof(SecurityIdentifier)));
            Assert.NotEmpty(security.GetAccessRules(true, true, typeof(SecurityIdentifier)));
            foreach (var rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>())
            {
                Assert.Equal(identity.User, rule.IdentityReference);
            }
        }
        else
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(path));
        }
    }

    private static void AssertPrivateFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var security = new FileInfo(path).GetAccessControl();
            Assert.NotEmpty(security.GetAccessRules(true, true, typeof(SecurityIdentifier)));
            foreach (var rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>())
            {
                Assert.Equal(identity.User, rule.IdentityReference);
            }
        }
        else
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }
}
