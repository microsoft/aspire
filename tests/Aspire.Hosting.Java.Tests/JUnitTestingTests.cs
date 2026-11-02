// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using TestResult = Aspire.Hosting.ApplicationModel.TestResult;

#pragma warning disable ASPIRETESTING001

namespace Aspire.Hosting.Java.Tests;

public class JUnitTestingTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void ContractIsExperimentalAndExported()
    {
        var type = typeof(JUnitTestingExtensions);
        var method = type.GetMethod(nameof(JUnitTestingExtensions.WithJUnitTesting))!;
        foreach (var member in new MemberInfo[] { type, method })
        {
            Assert.Equal("ASPIRETESTING001", member.GetCustomAttribute<ExperimentalAttribute>()!.DiagnosticId);
        }
        Assert.NotNull(method.GetCustomAttribute<AspireExportAttribute>());
        Assert.Equal(typeof(IResourceBuilder<JavaAppResource>), method.ReturnType);
        Assert.Throws<ArgumentNullException>(() => JUnitTestingExtensions.WithJUnitTesting(null!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreservesJavaLaunchWaitsEnvironmentAndNormalRunBehavior(bool testing)
    {
        using var directory = new TempJavaAppDirectory(directoryName: "project with spaces");
        directory.Write("pom.xml", "<project/>");
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        if (testing)
        {
            builder.Configuration["ASPIRE_TEST_OUTPUT_PATH"] = Path.Combine(directory.Path, "test output");
        }
        var service = builder.AddExternalService("api", "https://example.com");
        var resource = builder.AddJavaApp("tests", directory.Path).WithMavenGoal("test", "-B")
            .WithJUnitTesting().WithArgs("-Dtest=ServiceTest").WithEnvironment("CUSTOM", "configured")
            .WithEnvironment(JUnitTestingExtensions.ReportEnvironmentVariable, "caller-owned")
            .WithReference(service).WaitFor(service);
        var annotation = Assert.Single(resource.Resource.Annotations.OfType<TestingAnnotation>());
        Assert.NotNull(annotation.Collector);
        Assert.Same(service.Resource, Assert.Single(resource.Resource.Annotations.OfType<WaitAnnotation>()).Resource);
        Assert.Same(ManifestPublishingCallbackAnnotation.Ignore,
            Assert.Single(resource.Resource.Annotations.OfType<ManifestPublishingCallbackAnnotation>()));
        await using var app = builder.Build();
        await BeforeStartAsync(builder, app);
        var invocation = JavaHostingExtensions.WrapperInvocationFor(
            Path.Combine(directory.Path, JavaHostingExtensions.s_defaultMavenWrapper), directory.Path, OperatingSystem.IsWindows());
        Assert.Equal(invocation.LeadingArgs.Concat(["test", "-B", "-Dtest=ServiceTest"]),
            await ArgumentEvaluator.GetArgumentListAsync(resource.Resource, app.Services));
        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(resource.Resource, serviceProvider: app.Services);
        Assert.Equal("configured", environment["CUSTOM"]);
        if (testing)
        {
            Assert.Equal(Path.Combine(builder.Configuration["ASPIRE_TEST_OUTPUT_PATH"]!, "tests"), annotation.OutputPath);
            Assert.NotEqual(annotation.OutputPath, environment[JUnitTestingExtensions.ReportEnvironmentVariable]);
            Assert.NotEqual("caller-owned", environment[JUnitTestingExtensions.ReportEnvironmentVariable]);
            Assert.Empty(Directory.GetFileSystemEntries(environment[JUnitTestingExtensions.ReportEnvironmentVariable]));
        }
        else
        {
            Assert.Null(annotation.OutputPath);
            Assert.Equal("caller-owned", environment[JUnitTestingExtensions.ReportEnvironmentVariable]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementKeepsContextAndFreshReportConfigurationWithDisposalCleanup(bool markerOnly)
    {
        using var directory = new TempJavaAppDirectory();
        directory.Write("pom.xml", "<project/>");
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        builder.Configuration["ASPIRE_TEST_OUTPUT_PATH"] = Path.Combine(directory.Path, "test output");
        var resource = builder.AddJavaApp("tests", directory.Path).WithMavenGoal("test").WithJUnitTesting();
        var original = Assert.Single(resource.Resource.Annotations.OfType<TestingAnnotation>());
        var invoked = false;
        resource.WithTesting(markerOnly ? null : _ =>
        {
            invoked = true;
            return Task.CompletedTask;
        });
        var replacement = Assert.Single(resource.Resource.Annotations.OfType<TestingAnnotation>());
        Assert.Same(original.TestingContext, replacement.TestingContext);
        Assert.Equal(original.OutputPath, replacement.OutputPath);
        Assert.Equal(markerOnly, replacement.Collector is null);
        Assert.False(invoked);
        await using var app = builder.Build();
        await BeforeStartAsync(builder, app);
        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(resource.Resource, serviceProvider: app.Services);
        var reportDirectory = environment[JUnitTestingExtensions.ReportEnvironmentVariable];
        Assert.Empty(Directory.GetFileSystemEntries(reportDirectory));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(reportDirectory));
        }
        await app.DisposeAsync();
        Assert.False(Directory.Exists(reportDirectory));
    }

    [Fact]
    public async Task CollectorStartsBeforePrestartAndStagesMultipleClassesOnlyAfterFailedCompletion()
    {
        using var directory = new TempJavaAppDirectory();
        directory.Write("pom.xml", "<project/>");
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        builder.Configuration["ASPIRE_TEST_OUTPUT_PATH"] = Path.Combine(directory.Path, "test output");
        var resource = builder.AddJavaApp("tests", directory.Path).WithMavenGoal("test").WithJUnitTesting();
        var annotation = Assert.Single(resource.Resource.Annotations.OfType<TestingAnnotation>());
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new List<TestResult>();
        var context = new ResourceTestingContext(annotation.TestingContext!, "tests", annotation.OutputPath!,
            published.Add, completion.Task, TestContext.Current.CancellationToken);
        var collection = annotation.Collector!(context);
        Assert.False(collection.IsCompleted);
        await using var app = builder.Build();
        await BeforeStartAsync(builder, app);
        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(resource.Resource, serviceProvider: app.Services);
        var reportDirectory = environment[JUnitTestingExtensions.ReportEnvironmentVariable];
        File.WriteAllText(Path.Combine(reportDirectory, "TEST-A.xml"), SurefireJUnitReportTests.PassingReport);
        File.WriteAllText(Path.Combine(reportDirectory, "TEST-B.xml"),
            SurefireJUnitReportTests.PassingReport.Replace("ServiceTest", "OtherTest", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(reportDirectory, "unrelated.xml"), "not a report");
        Directory.CreateDirectory(Path.Combine(reportDirectory, "nested"));
        File.WriteAllText(Path.Combine(reportDirectory, "nested", "TEST-stale.xml"), "not a report");
        Assert.Empty(Directory.GetFiles(context.OutputPath));
        completion.SetResult(false);
        await collection.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "example.ServiceTest.probe(String)[1]", "example.OtherTest.probe(String)[1]" },
            published.Select(result => result.Name));
        Assert.All(published, result => Assert.Equal(TestOutcome.Passed, result.Outcome));
        Assert.Equal(new[] { "TEST-A.xml", "TEST-B.xml" }, Directory.GetFiles(context.OutputPath)
            .Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(SurefireJUnitReportTests.PassingReport, File.ReadAllText(Path.Combine(context.OutputPath, "TEST-A.xml")));
        await app.DisposeAsync();
        Assert.False(Directory.Exists(reportDirectory));
        Assert.True(File.Exists(Path.Combine(context.OutputPath, "TEST-A.xml")));
    }

    [Theory]
    [InlineData("gradle")]
    [InlineData("jar")]
    [InlineData("goal")]
    [InlineData("replica")]
    [InlineData("reactor")]
    public async Task RejectsUnsupportedLaunchers(string scenario)
    {
        using var directory = new TempJavaAppDirectory();
        directory.Write("pom.xml", scenario == "reactor" ? "<project><modules><module>child</module></modules></project>" : "<project/>");
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        builder.Configuration["ASPIRE_TEST_OUTPUT_PATH"] = Path.Combine(directory.Path, "output");
        var resource = scenario == "jar"
            ? builder.AddJavaApp("tests", directory.Path, "tests.jar")
            : builder.AddJavaApp("tests", directory.Path);
        if (scenario == "gradle")
        {
            resource.WithGradleTask("test");
        }
        else if (scenario != "jar")
        {
            resource.WithMavenGoal(scenario == "goal" ? "verify" : "test");
        }
        resource.WithJUnitTesting();
        if (scenario == "replica")
        {
            resource.Resource.Annotations.Add(new ReplicaAnnotation(2));
        }
        await using var app = builder.Build();
        await Assert.ThrowsAsync<DistributedApplicationException>(() => BeforeStartAsync(builder, app));
    }

    [Fact]
    public async Task RejectsLateGoalsAndRestarts()
    {
        using var directory = new TempJavaAppDirectory();
        directory.Write("pom.xml", "<project/>");
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        builder.Configuration["ASPIRE_TEST_OUTPUT_PATH"] = Path.Combine(directory.Path, "output");
        var resource = builder.AddJavaApp("tests", directory.Path).WithMavenGoal("test").WithJUnitTesting().WithArgs("verify");
        await using var app = builder.Build();
        await BeforeStartAsync(builder, app);
        await Assert.ThrowsAsync<ArgumentException>(async () => await ArgumentEvaluator.GetArgumentListAsync(resource.Resource, app.Services));
        await builder.Eventing.PublishAsync(new BeforeResourceStartedEvent(resource.Resource, app.Services), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<DistributedApplicationException>(() => builder.Eventing.PublishAsync(
            new BeforeResourceStartedEvent(resource.Resource, app.Services), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReapplyingHelperDoesNotDuplicateRunnerConfiguration()
    {
        using var directory = new TempJavaAppDirectory();
        directory.Write("pom.xml", "<project/>");
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        builder.Configuration["ASPIRE_TEST_OUTPUT_PATH"] = Path.Combine(directory.Path, "output");
        var resource = builder.AddJavaApp("tests", directory.Path).WithMavenGoal("test").WithJUnitTesting();
        var original = Assert.Single(resource.Resource.Annotations.OfType<TestingAnnotation>());
        resource.WithJUnitTesting();
        var replacement = Assert.Single(resource.Resource.Annotations.OfType<TestingAnnotation>());
        Assert.Same(original.TestingContext, replacement.TestingContext);
        Assert.Equal(original.OutputPath, replacement.OutputPath);
        await using var app = builder.Build();
        await BeforeStartAsync(builder, app);
        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(resource.Resource, serviceProvider: app.Services);
        var reports = environment[JUnitTestingExtensions.ReportEnvironmentVariable];
        File.WriteAllText(Path.Combine(reports, "TEST-ServiceTest.xml"), SurefireJUnitReportTests.PassingReport);
        var results = new List<TestResult>();
        await replacement.Collector!(new ResourceTestingContext(replacement.TestingContext!, "tests",
            replacement.OutputPath!, results.Add, Task.FromResult(true), TestContext.Current.CancellationToken));
        Assert.Single(results);
        await builder.Eventing.PublishAsync(new BeforeResourceStartedEvent(resource.Resource, app.Services), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PublishingExcludesJUnitWithoutAllocatingTestingDirectories()
    {
        using var directory = new TempJavaAppDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: directory.Path);
        var output = Path.Combine(directory.Path, "test output");
        builder.Configuration["ASPIRE_TEST_OUTPUT_PATH"] = output;
        var resource = builder.AddJavaApp("tests", directory.Path).WithMavenGoal("test").WithJUnitTesting();
        Assert.True(resource.Resource.IsExcludedFromPublish());
        var annotation = Assert.Single(resource.Resource.Annotations.OfType<TestingAnnotation>());
        Assert.Null(annotation.OutputPath);
        Assert.Null(annotation.TestingContext);
        await using var app = builder.Build();
        Assert.False(Directory.Exists(output));
    }

    [Theory]
    [InlineData("-pl")]
    [InlineData("--projects=module")]
    [InlineData("-fother.xml")]
    [InlineData("@args")]
    [InlineData("verify")]
    [InlineData("surefire:test")]
    public void RejectsReactorAlternatePomAndAdditionalGoals(string argument)
    {
        Assert.Throws<ArgumentException>(() => JUnitTestingExtensions.ValidateArguments([argument]));
    }

    private static Task BeforeStartAsync(IDistributedApplicationBuilder builder, DistributedApplication app)
        => builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services,
            app.Services.GetRequiredService<DistributedApplicationModel>()), TestContext.Current.CancellationToken);
}
