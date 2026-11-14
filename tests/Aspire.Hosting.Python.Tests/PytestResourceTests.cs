// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using TestResult = Aspire.Hosting.ApplicationModel.TestResult;

#pragma warning disable ASPIRETESTING001 // Tests the experimental pytest integration.

namespace Aspire.Hosting.Python.Tests;

public class PytestResourceTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void PublicContractIsExperimentalAndExported()
    {
        var type = typeof(PytestHostingExtensions);
        var method = type.GetMethod(nameof(PytestHostingExtensions.AddPytest))!;
        foreach (var member in new MemberInfo[] { type, method })
        {
            var experimental = member.GetCustomAttribute<ExperimentalAttribute>()!;
            Assert.Equal("ASPIRETESTING001", experimental.DiagnosticId);
            Assert.Equal("https://aka.ms/aspire/diagnostics/{0}", experimental.UrlFormat);
        }
        Assert.NotNull(method.GetCustomAttribute<AspireExportAttribute>());
        Assert.NotNull(method.GetParameters()[1].GetCustomAttribute<ResourceNameAttribute>());
        Assert.Equal(typeof(IResourceBuilder<PythonAppResource>), method.ReturnType);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArgumentsVirtualEnvironmentAndNormalRunBehaviorCompose(bool testing)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var directory = workspace.CreateDirectory("tests with spaces").FullName;
        using var builder = TestDistributedApplicationBuilder.Create(options => options.ProjectDirectory = workspace.Path, outputHelper);
        if (testing)
        {
            builder.Configuration["ASPIRE_TEST_OUTPUT_PATH"] = Path.Combine(workspace.Path, "reports with spaces");
        }
        var api = builder.AddExternalService("api", "https://example.com");
        var resource = builder.AddPytest("tests", Path.GetRelativePath(workspace.Path, directory))
            .WithVirtualEnvironment("custom environment", createIfNotExists: false)
            .WithArgs("-k", "a or b", "--", "tests with spaces")
            .WithReference(api)
            .WaitFor(api)
            .WithEnvironment("CUSTOM", "value");
        Assert.Equal(directory, resource.Resource.WorkingDirectory);
        var venv = Assert.Single(resource.Resource.Annotations.OfType<PythonEnvironmentAnnotation>()).VirtualEnvironment!;
        Assert.Equal(venv.GetExecutable("pytest"), resource.Resource.Command);
        Assert.IsAssignableFrom<IResourceWithServiceDiscovery>(resource.Resource);
        Assert.Same(api.Resource, Assert.Single(resource.Resource.Annotations.OfType<WaitAnnotation>()).Resource);
        Assert.Same(ManifestPublishingCallbackAnnotation.Ignore,
            Assert.Single(resource.Resource.Annotations.OfType<ManifestPublishingCallbackAnnotation>()));
        var annotation = Assert.Single(resource.Resource.Annotations.OfType<TestingAnnotation>());
        Assert.NotNull(annotation.Collector);
        await using var app = builder.Build();
        await BeforeStartAsync(builder, app);
        var arguments = await ArgumentEvaluator.GetArgumentListAsync(resource.Resource, app.Services);
        var expected = new List<string> { "-k", "a or b" };
        if (testing)
        {
            Assert.True(Directory.Exists(annotation.OutputPath));
            Assert.Equal(Path.Combine(builder.Configuration["ASPIRE_TEST_OUTPUT_PATH"]!, "tests"), annotation.OutputPath);
            var reportPath = Assert.Single(arguments, arg => arg.StartsWith("--junitxml=", StringComparison.Ordinal))["--junitxml=".Length..];
            var reportDirectory = Path.GetDirectoryName(reportPath)!;
            Assert.True(Directory.Exists(reportDirectory));
            Assert.NotEqual(annotation.OutputPath, reportDirectory);
            Assert.Empty(Directory.GetFileSystemEntries(reportDirectory));
            expected.Add("--junitxml=" + reportPath);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(annotation.OutputPath!));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(reportDirectory));
            }
        }
        else
        {
            Assert.Null(annotation.OutputPath);
            Assert.Null(annotation.TestingContext);
        }
        expected.AddRange(["--", "tests with spaces"]);
        Assert.Equal(expected, arguments);
        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(resource.Resource, serviceProvider: app.Services);
        Assert.Equal("value", environment["CUSTOM"]);
    }

    [Fact]
    public async Task UvInstallerUsesSelectedEnvironmentAndGroupWithoutChangingPytestCommand()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, "pyproject.toml"), "[project]\nname = 'fixture'\nversion = '1.0'\n");
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        var resource = builder.AddPytest("tests", workspace.Path)
            .WithUv(args: ["sync", "--group", "test"])
            .WithVirtualEnvironment("custom venv", createIfNotExists: false);
        await using var app = builder.Build();
        await BeforeStartAsync(builder, app);
        var installer = Assert.Single(builder.Resources.OfType<PythonInstallerResource>());
        Assert.Equal("uv", installer.Command);
        Assert.Equal(new[] { "sync", "--group", "test" },
            await ArgumentEvaluator.GetArgumentListAsync(installer, app.Services));
        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(installer, serviceProvider: app.Services);
        Assert.Equal(Path.Combine(workspace.Path, "custom venv"), environment["UV_PROJECT_ENVIRONMENT"]);
        Assert.Equal(Assert.Single(resource.Resource.Annotations.OfType<PythonEnvironmentAnnotation>())
            .VirtualEnvironment!.GetExecutable("pytest"), resource.Resource.Command);
    }

    [Fact]
    public async Task PipInstallerRetainsRequirementsAndCompletionDependency()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, "requirements.txt"), "pytest==9.0.2");
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        var resource = builder.AddPytest("tests", workspace.Path);
        await using var app = builder.Build();
        await BeforeStartAsync(builder, app);
        var installer = Assert.Single(builder.Resources.OfType<PythonInstallerResource>());
        var arguments = await ArgumentEvaluator.GetArgumentListAsync(installer, app.Services);
        Assert.Equal(new[] { "install", "-r", "requirements.txt" }, arguments);
        Assert.Contains(resource.Resource.Annotations.OfType<WaitAnnotation>(), wait => wait.Resource == installer);
    }

    [Theory]
    [InlineData("--junitxml")]
    [InlineData("--junit-xml=outside.xml")]
    [InlineData("@args.txt")]
    [InlineData("--reruns=2")]
    [InlineData("--count")]
    public void ReservedArgumentsFailExplicitly(string argument)
    {
        Assert.Throws<ArgumentException>(() => PytestHostingExtensions.ValidateArguments([argument]));
    }

    [Fact]
    public async Task LaterRedirectionReplicasEntrypointChangesAndRestartsAreRejected()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        builder.Configuration["ASPIRE_TEST_OUTPUT_PATH"] = Path.Combine(workspace.Path, "reports");
        var resource = builder.AddPytest("tests", workspace.Path).WithArgs("--junitxml=outside.xml");
        await using var app = builder.Build();
        await BeforeStartAsync(builder, app);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await ArgumentEvaluator.GetArgumentListAsync(resource.Resource, app.Services));
        resource.Resource.Annotations.Add(new ReplicaAnnotation(2));
        await Assert.ThrowsAsync<DistributedApplicationException>(() => BeforeStartAsync(builder, app));
        resource.Resource.Annotations.Remove(resource.Resource.Annotations.OfType<ReplicaAnnotation>().Single());
        resource.WithEntrypoint(EntrypointType.Module, "pytest");
        await Assert.ThrowsAsync<DistributedApplicationException>(() => BeforeStartAsync(builder, app));
        await builder.Eventing.PublishAsync(new BeforeResourceStartedEvent(resource.Resource, app.Services),
            TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<DistributedApplicationException>(() => builder.Eventing.PublishAsync(
            new BeforeResourceStartedEvent(resource.Resource, app.Services), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RuntimeCollectorCanStartBeforeRunnerConfigurationAndStagesOnlyAfterCompletion()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        builder.Configuration["ASPIRE_TEST_OUTPUT_PATH"] = Path.Combine(workspace.Path, "reports");
        var resource = builder.AddPytest("tests", workspace.Path);
        var annotation = Assert.Single(resource.Resource.Annotations.OfType<TestingAnnotation>());
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new List<TestResult>();
        var context = new ResourceTestingContext(annotation.TestingContext!, "tests", annotation.OutputPath!,
            published.Add, completion.Task, TestContext.Current.CancellationToken);
        Assert.Equal(builder.Configuration["ASPIRE_TEST_OUTPUT_PATH"], context.TestingContext.OutputPath);
        Assert.Equal(Path.Combine(context.TestingContext.OutputPath, "tests"), context.OutputPath);
        var collection = annotation.Collector!(context);
        Assert.False(collection.IsCompleted);
        await using var app = builder.Build();
        await BeforeStartAsync(builder, app);
        var arguments = await ArgumentEvaluator.GetArgumentListAsync(resource.Resource, app.Services);
        var reportPath = Assert.Single(arguments)["--junitxml=".Length..];
        const string report = """
            <testsuite tests="1" failures="0" errors="0" skipped="0"><testcase name="example" /></testsuite>
            """;
        await File.WriteAllTextAsync(reportPath, report, TestContext.Current.CancellationToken);
        Assert.Empty(Directory.GetFiles(context.OutputPath));
        completion.SetResult(false);
        await collection.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(TestOutcome.Passed, Assert.Single(published).Outcome);
        Assert.Equal(report, await File.ReadAllTextAsync(Path.Combine(context.OutputPath, "pytest.xml"),
            TestContext.Current.CancellationToken));
        await app.DisposeAsync();
        Assert.False(Directory.Exists(Path.GetDirectoryName(reportPath)));
        Assert.True(File.Exists(Path.Combine(context.OutputPath, "pytest.xml")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacingCollectorPreservesFreshRunnerConfigurationAndDisposalCleansIt(bool markerOnly)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        builder.Configuration["ASPIRE_TEST_OUTPUT_PATH"] = Path.Combine(workspace.Path, "reports");
        var first = builder.AddPytest("first", workspace.Path);
        var original = Assert.Single(first.Resource.Annotations.OfType<TestingAnnotation>());
        var invoked = false;
        first.WithTesting(markerOnly ? null : _ =>
        {
            invoked = true;
            return Task.CompletedTask;
        });
        var replacement = Assert.Single(first.Resource.Annotations.OfType<TestingAnnotation>());
        Assert.Same(original.TestingContext, replacement.TestingContext);
        Assert.Equal(original.OutputPath, replacement.OutputPath);
        Assert.Equal(markerOnly, replacement.Collector is null);
        var second = builder.AddPytest("second", workspace.Path);
        Assert.False(invoked);
        await using var app = builder.Build();
        await BeforeStartAsync(builder, app);
        var directories = new List<string>();
        foreach (var resource in new[] { first, second })
        {
            var arguments = await ArgumentEvaluator.GetArgumentListAsync(resource.Resource, app.Services);
            var reportPath = Assert.Single(arguments)["--junitxml=".Length..];
            var directory = Path.GetDirectoryName(reportPath)!;
            Assert.Empty(Directory.GetFileSystemEntries(directory));
            directories.Add(directory);
        }
        Assert.NotEqual(directories[0], directories[1]);
        await app.DisposeAsync();
        Assert.All(directories, directory => Assert.False(Directory.Exists(directory)));
    }

    [Fact]
    public void InvalidInputsAreRejectedBeforeRegistration()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        Assert.Throws<ArgumentNullException>(() => PytestHostingExtensions.AddPytest(null!, "tests", "."));
        Assert.Throws<ArgumentNullException>(() => builder.AddPytest("tests", null!));
        Assert.Throws<ArgumentException>(() => builder.AddPytest("tests", " "));
        Assert.Throws<ArgumentException>(() => builder.AddPytest("", "."));
        Assert.Empty(builder.Resources.OfType<PythonAppResource>());
    }

    [Fact]
    public async Task PublishOmitsPytestAndDoesNotAllocateTestingOutputOrInstallers()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, "requirements.txt"), "pytest==9.0.2");
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path);
        builder.Configuration["ASPIRE_TEST_OUTPUT_PATH"] = Path.Combine(workspace.Path, "reports");
        var resource = builder.AddPytest("tests", workspace.Path).WithUv();
        Assert.True(resource.Resource.IsExcludedFromPublish());
        var publishedResource = Assert.Single(builder.Resources);
        Assert.Equal("tests", publishedResource.Name);
        Assert.True(publishedResource.IsExcludedFromPublish());
        var annotation = Assert.Single(resource.Resource.Annotations.OfType<TestingAnnotation>());
        Assert.Null(annotation.OutputPath);
        Assert.Null(annotation.TestingContext);
        await using var app = builder.Build();
        var manifest = await ManifestUtils.GetManifestForModel(app.Services.GetRequiredService<DistributedApplicationModel>(), workspace.Path);

        await Verify(manifest.ToJsonString(), "json");
    }

    private static Task BeforeStartAsync(IDistributedApplicationBuilder builder, DistributedApplication app)
    {
        return builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services,
            app.Services.GetRequiredService<DistributedApplicationModel>()), TestContext.Current.CancellationToken);
    }
}
