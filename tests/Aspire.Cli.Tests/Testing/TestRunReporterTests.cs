// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Resources;
using Aspire.Cli.Testing;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

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
        Assert.Contains(interaction.DisplayedMessages, message => message.Message == RunCommandStrings.TestRunningHeader);
        Assert.Single(interaction.DisplayedRenderables);
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

    [Theory]
    [InlineData("failedResult")]
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
            "failedResult" => new TestRunEvent
            {
                Result = new PublishedTestResult { ResourceName = resource.ResourceName, Name = "example", Outcome = "failed" }
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
