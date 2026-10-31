// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Backchannel;
using Aspire.Hosting.Cli;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Tests;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TestResult = Aspire.Hosting.ApplicationModel.TestResult;

#pragma warning disable ASPIRETESTING001 // Tests the experimental MSTest integration with the common coordinator.

namespace Aspire.Hosting.Dotnet.Tests;

[Trait("Partition", "4")]
public class MsTestCollectorTests(ITestOutputHelper outputHelper)
{
    private const string Report = """
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010" id="11111111-1111-1111-1111-111111111111">
          <Results>
            <UnitTestResult executionId="22222222-2222-2222-2222-222222222222" testId="33333333-3333-3333-3333-333333333333"
                            testName="fixture" outcome="Passed" duration="00:00:01" />
          </Results>
          <TestDefinitions>
            <UnitTest id="33333333-3333-3333-3333-333333333333"><TestMethod className="Example" name="fixture" /></UnitTest>
          </TestDefinitions>
          <ResultSummary outcome="Completed"><Counters total="1" passed="1" failed="0" timeout="0" notExecuted="0" /></ResultSummary>
        </TestRun>
        """;

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task CollectionWaitsForExitAndPublishesIntoTheCommonAggregateEvenOnProcessFailure(int exitCode)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = CreateBuilder(workspace);
        var resource = AddResource(builder, workspace);
        var path = GetReportPath(resource);
        using var notifications = ResourceNotificationServiceTestHelpers.Create();
        using var coordinator = CreateCoordinator(builder, notifications);
        var completion = coordinator.WaitForCompletionAsync(TestContext.Current.CancellationToken);
        Assert.False(completion.IsCompleted);
        File.WriteAllText(path, Report);
        // Overlapping consolidated artifacts must not cause double counting.
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(path)!, "merged"));
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "merged", "results.trx"), Report);
        await notifications.PublishUpdateAsync(resource.Resource, snapshot =>
            snapshot with { State = KnownResourceStates.Finished, ExitCode = exitCode });

        Assert.Equal(exitCode == 0, await completion.DefaultTimeout());
        var events = await ReadAsync(coordinator);
        var result = Assert.Single(events, e => e.Result is not null).Result!;
        Assert.Equal(("tests", "Example.fixture", "passed", 1000d),
            (result.ResourceName, result.Name, result.Outcome, result.DurationMilliseconds));
        Assert.Equal("completed", events.Last(e => e.Collection is not null).Collection!.Status);
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("run-failure")]
    public async Task ReportFailuresFailCollectionExplicitly(string failure)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = CreateBuilder(workspace);
        var resource = AddResource(builder, workspace);
        if (failure != "missing")
        {
            File.WriteAllText(GetReportPath(resource), failure == "malformed"
                ? "<TestRun>"
                : Report.Replace("outcome=\"Completed\"", "outcome=\"Failed\"", StringComparison.Ordinal));
        }
        using var notifications = ResourceNotificationServiceTestHelpers.Create();
        using var coordinator = CreateCoordinator(builder, notifications);
        var completion = coordinator.WaitForCompletionAsync(TestContext.Current.CancellationToken);
        await notifications.PublishUpdateAsync(resource.Resource, snapshot =>
            snapshot with { State = KnownResourceStates.Finished, ExitCode = 0 });

        Assert.False(await completion.DefaultTimeout());
        var events = await ReadAsync(coordinator);
        var collection = events.Last(e => e.Collection is not null).Collection!;
        Assert.Equal("failed", collection.Status);
        Assert.NotEmpty(collection.Error!);
        Assert.Equal(failure == "run-failure" ? 1 : 0, events.Count(e => e.Result is not null));
    }

    [Fact]
    public async Task CancelledCollectionDoesNotReadOrPublishAReport()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = CreateBuilder(workspace);
        var resource = AddResource(builder, workspace);
        var options = resource.Resource.Annotations.OfType<TestingAnnotation>().Single().Options!;
        var published = new List<TestResult>();
        using var cancellation = new CancellationTokenSource();
        var context = new ResourceTestingContext(options.RunContext!, "tests", options.OutputPath!,
            published.Add, new TaskCompletionSource<bool>().Task, cancellation.Token);
        var collection = Assert.Single(options.Collectors)(context);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collection.DefaultTimeout());
        Assert.Empty(published);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SymbolicLinkReportsOrDirectoriesAreNotRead(bool linkDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Creating symlinks can require privileges not available in CI.
        }
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = CreateBuilder(workspace);
        var resource = AddResource(builder, workspace);
        var target = Path.Combine(workspace.Path, "outside.trx");
        File.WriteAllText(target, Report);
        var options = resource.Resource.Annotations.OfType<TestingAnnotation>().Single().Options!;
        if (linkDirectory)
        {
            var displaced = options.OutputPath! + "-original";
            Directory.Move(options.OutputPath!, displaced);
            var targetDirectory = workspace.CreateDirectory("outside").FullName;
            File.Copy(target, Path.Combine(targetDirectory, "results.trx"));
            Directory.CreateSymbolicLink(options.OutputPath!, targetDirectory);
        }
        else
        {
            File.CreateSymbolicLink(GetReportPath(resource), target);
        }
        var context = new ResourceTestingContext(options.RunContext!, "tests", options.OutputPath!,
            _ => Assert.Fail("A linked report must not publish results."), Task.FromResult(true), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => Assert.Single(options.Collectors)(context));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [RequiresTools(["dotnet"])]
    [OuterloopTest("Restores and runs a real MSTest executable through DCP.")]
    public async Task RealMtpExecutableWritesTrxAndContributesPassesFailuresDataRowsAndSkips(bool fail)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectDirectory = workspace.CreateDirectory("MSTest project with spaces").FullName;
        File.Copy(Path.Combine(MSBuildUtils.GetRepoRoot(), "NuGet.config"), Path.Combine(workspace.Path, "NuGet.config"));
        var projectPath = Path.Combine(projectDirectory, "Fixture.csproj");
        File.WriteAllText(projectPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <OutputType>Exe</OutputType>
                <EnableMSTestRunner>true</EnableMSTestRunner>
                <IsTestProject>true</IsTestProject>
                <NuGetAudit>false</NuGetAudit>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="MSTest" Version="4.4.0" />
                <PackageReference Include="Microsoft.Testing.Extensions.TrxReport" Version="2.4.0" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(projectDirectory, "Fixture.cs"), """
            using Microsoft.VisualStudio.TestTools.UnitTesting;
            [TestClass]
            public class SmokeTests
            {
                [TestMethod]
                [DataRow(1)]
                [DataRow(2)]
                public void DataRows(int value) => Assert.IsTrue(value > 0);
                [TestMethod]
                public void Outcome()
                {
                    Assert.AreEqual("configured", System.Environment.GetEnvironmentVariable("MSTEST_REFERENCE_VALUE"));
                    Assert.IsFalse(System.Environment.GetEnvironmentVariable("MSTEST_FAIL") == "true", "intentional failure");
                }
                [TestMethod]
                [Ignore("intentional skip")]
                public void Skipped() { }
            }
            """);
        using var builder = CreateBuilder(workspace);
        var reference = builder.AddParameter("reference", "configured");
        var resource = builder.AddMsTest("tests", projectPath)
            .WithEnvironment("MSTEST_REFERENCE_VALUE", reference)
            .WithEnvironment("MSTEST_FAIL", fail ? "true" : "false");
        await using var app = builder.Build();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        await app.StartAsync(timeout.Token);
        try
        {
            var coordinator = app.Services.GetRequiredService<TestRunCoordinator>();
            var events = new List<TestRunEvent>();
            await foreach (var item in coordinator.WatchAsync(timeout.Token))
            {
                events.Add(item);
            }
            Assert.Equal(!fail, events.Last().Passed);
            var results = events.Where(e => e.Result is not null).Select(e => e.Result!).ToArray();
            Assert.Equal(4, results.Length);
            Assert.Equal(fail ? 2 : 3, results.Count(result => result.Outcome == "passed"));
            Assert.Equal(fail ? 1 : 0, results.Count(result => result.Outcome == "failed"));
            Assert.Equal(1, results.Count(result => result.Outcome == "skipped"));
            Assert.Equal("completed", events.Last(e => e.Collection is not null).Collection!.Status);
            Assert.True(File.Exists(GetReportPath(resource)));
            if (fail)
            {
                Assert.Contains("intentional failure", Assert.Single(results, result => result.Outcome == "failed").Message);
            }
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    private IDistributedApplicationTestingBuilder CreateBuilder(TemporaryWorkspace workspace)
    {
        var builder = TestDistributedApplicationBuilder.Create(options => options.DisableDashboard = true, outputHelper);
        builder.Configuration[KnownConfigNames.TestOutputPath] = Path.Combine(workspace.Path, "test output");

        return builder;
    }

    private static IResourceBuilder<MsTestResource> AddResource(IDistributedApplicationBuilder builder, TemporaryWorkspace workspace)
    {
        var path = Path.Combine(workspace.Path, "tests.csproj");
        File.WriteAllText(path, "<Project />");

        return builder.AddMsTest("tests", path);
    }

    private static string GetReportPath(IResourceBuilder<MsTestResource> resource)
    {
        return Path.Combine(resource.Resource.Annotations.OfType<TestingAnnotation>().Single().Options!.OutputPath!, "results.trx");
    }

    private static TestRunCoordinator CreateCoordinator(IDistributedApplicationTestingBuilder builder, ResourceNotificationService notifications)
    {
        return new(new DistributedApplicationModel(builder.Resources), notifications, TimeProvider.System, NullLogger<TestRunCoordinator>.Instance, new ResourceLoggerService());
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
