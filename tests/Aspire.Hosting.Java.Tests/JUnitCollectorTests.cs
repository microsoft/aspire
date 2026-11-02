// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Backchannel;
using Aspire.Hosting.Cli;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using TestResult = Aspire.Hosting.ApplicationModel.TestResult;

#pragma warning disable ASPIRETESTING001

namespace Aspire.Hosting.Java.Tests;

public class JUnitCollectorTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task ResultsAssociateWithResourceWithoutOverridingProcessFailure(int exitCode, bool passed)
    {
        using var directory = new TempJavaAppDirectory();
        using var builder = CreateBuilder(directory);
        var source = directory.Write("reports/TEST-ServiceTest.xml", SurefireJUnitReportTests.PassingReport);
        var resource = builder.AddJavaApp("java-tests", directory.Path).WithMavenGoal("test")
            .WithTesting(context => SurefireJUnitReport.CollectAsync(context, Path.GetDirectoryName(source)!));
        using var notifications = ResourceNotificationServiceTestHelpers.Create();
        using var coordinator = new TestRunCoordinator(new DistributedApplicationModel(builder.Resources),
            notifications, TimeProvider.System, NullLogger<TestRunCoordinator>.Instance, new ResourceLoggerService());
        var completion = coordinator.WaitForCompletionAsync(TestContext.Current.CancellationToken);
        Assert.False(completion.IsCompleted);
        await notifications.PublishUpdateAsync(resource.Resource, snapshot => snapshot with
        {
            State = KnownResourceStates.Finished,
            ExitCode = exitCode
        });
        Assert.Equal(passed, await completion.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        var events = new List<TestRunEvent>();
        await foreach (var item in coordinator.WatchAsync(TestContext.Current.CancellationToken))
        {
            events.Add(item);
        }
        var result = Assert.Single(events, item => item.Result is not null).Result!;
        Assert.Equal(("java-tests", "example.ServiceTest.probe(String)[1]", "passed", 125d),
            (result.ResourceName, result.Name, result.Outcome, result.DurationMilliseconds));
        Assert.Equal("completed", events.Last(item => item.Collection is not null).Collection!.Status);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("duplicate")]
    [InlineData("existing")]
    public async Task ReportSetErrorsDoNotPublishPartialOrFabricatedResults(string scenario)
    {
        using var directory = new TempJavaAppDirectory();
        using var builder = CreateBuilder(directory);
        var source = Path.Combine(directory.Path, "reports");
        Directory.CreateDirectory(source);
        var resource = builder.AddJavaApp("java-tests", directory.Path).WithMavenGoal("test").WithTesting();
        var annotation = Assert.Single(resource.Resource.Annotations.OfType<TestingAnnotation>());
        if (scenario != "missing")
        {
            File.WriteAllText(Path.Combine(source, "TEST-A.xml"), SurefireJUnitReportTests.PassingReport);
        }
        if (scenario is "malformed" or "duplicate")
        {
            File.WriteAllText(Path.Combine(source, "TEST-B.xml"),
                scenario == "malformed" ? "<testsuite>" : SurefireJUnitReportTests.PassingReport);
        }
        if (scenario == "existing")
        {
            File.WriteAllText(Path.Combine(annotation.OutputPath!, "TEST-A.xml"), "existing artifact");
        }
        var results = new List<TestResult>();
        var context = new ResourceTestingContext(annotation.TestingContext!, "java-tests", annotation.OutputPath!,
            results.Add, Task.FromResult(true), TestContext.Current.CancellationToken);
        var error = await Record.ExceptionAsync(() => SurefireJUnitReport.CollectAsync(context, source));
        Assert.NotNull(error);
        Assert.Empty(results);
        if (scenario == "existing")
        {
            Assert.Equal("existing artifact", File.ReadAllText(Path.Combine(annotation.OutputPath!, "TEST-A.xml")));
        }
    }

    [Theory]
    [InlineData("source-file")]
    [InlineData("source-directory")]
    [InlineData("destination-file")]
    [InlineData("destination-directory")]
    public async Task RejectsSourceAndDestinationLinksWithoutChangingTargets(string link)
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Windows CI may not grant symbolic link privileges.
        }
        using var directory = new TempJavaAppDirectory();
        using var builder = CreateBuilder(directory);
        var source = Path.Combine(directory.Path, "reports");
        Directory.CreateDirectory(source);
        var outside = Path.Combine(directory.Path, "outside");
        Directory.CreateDirectory(outside);
        var target = directory.Write("outside/TEST-A.xml", SurefireJUnitReportTests.PassingReport);
        var resource = builder.AddJavaApp("java-tests", directory.Path).WithMavenGoal("test").WithTesting();
        var annotation = Assert.Single(resource.Resource.Annotations.OfType<TestingAnnotation>());
        if (link == "source-file")
        {
            File.CreateSymbolicLink(Path.Combine(source, "TEST-A.xml"), target);
        }
        else if (link == "source-directory")
        {
            Directory.Delete(source);
            Directory.CreateSymbolicLink(source, outside);
        }
        else
        {
            File.WriteAllText(Path.Combine(source, "TEST-A.xml"), SurefireJUnitReportTests.PassingReport);
            if (link == "destination-file")
            {
                File.CreateSymbolicLink(Path.Combine(annotation.OutputPath!, "TEST-A.xml"), target);
            }
            else
            {
                Directory.Delete(annotation.OutputPath!);
                Directory.CreateSymbolicLink(annotation.OutputPath!, outside);
            }
        }
        var context = new ResourceTestingContext(annotation.TestingContext!, "java-tests", annotation.OutputPath!,
            _ => Assert.Fail("Linked artifacts must not publish results."), Task.FromResult(true), TestContext.Current.CancellationToken);
        var error = await Record.ExceptionAsync(() => SurefireJUnitReport.CollectAsync(context, source));
        Assert.True(error is IOException or InvalidDataException, error?.ToString() ?? "Linked report unexpectedly succeeded.");
        Assert.Equal(SurefireJUnitReportTests.PassingReport, File.ReadAllText(target));
    }

    [Fact]
    public async Task CancellationInterruptsCompletionBeforeHarvesting()
    {
        using var directory = new TempJavaAppDirectory();
        using var builder = CreateBuilder(directory);
        var resource = builder.AddJavaApp("java-tests", directory.Path).WithMavenGoal("test").WithJUnitTesting();
        var annotation = Assert.Single(resource.Resource.Annotations.OfType<TestingAnnotation>());
        using var cancellation = new CancellationTokenSource();
        var context = new ResourceTestingContext(annotation.TestingContext!, "java-tests", annotation.OutputPath!,
            _ => Assert.Fail("Cancelled collection must not publish."), new TaskCompletionSource<bool>().Task, cancellation.Token);
        var collection = annotation.Collector!(context);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collection.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
    }

    private IDistributedApplicationTestingBuilder CreateBuilder(TempJavaAppDirectory directory)
    {
        var builder = TestDistributedApplicationBuilder.Create(options => options.DisableDashboard = true, outputHelper);
        builder.Configuration["ASPIRE_TEST_OUTPUT_PATH"] = Path.Combine(directory.Path, "test output");
        return builder;
    }
}
