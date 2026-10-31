// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

#pragma warning disable ASPIRETESTING001 // Tests the experimental testing integration.

namespace Aspire.Hosting.Dotnet.Tests;

public class MsTestResourceTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void PublicContractIsExperimentalAndExported()
    {
        foreach (var type in new[] { typeof(MsTestResource), typeof(MsTestOptions), typeof(MsTestHostingExtensions) })
        {
            var experimental = type.GetCustomAttribute<ExperimentalAttribute>()!;
            Assert.Equal("ASPIRETESTING001", experimental.DiagnosticId);
            Assert.Equal("https://aka.ms/aspire/diagnostics/{0}", experimental.UrlFormat);
        }
        Assert.NotNull(typeof(MsTestResource).GetCustomAttribute<AspireExportAttribute>());
        Assert.NotNull(typeof(MsTestOptions).GetCustomAttribute<AspireDtoAttribute>());
        var method = typeof(MsTestHostingExtensions).GetMethod(nameof(MsTestHostingExtensions.AddMsTest))!;
        Assert.NotNull(method.GetCustomAttribute<AspireExportAttribute>());
        Assert.Equal("ASPIRETESTING001", method.GetCustomAttribute<ExperimentalAttribute>()!.DiagnosticId);
        Assert.NotNull(method.GetParameters()[1].GetCustomAttribute<ResourceNameAttribute>());
        Assert.All(typeof(MsTestOptions).GetProperties(), property =>
            Assert.Equal("ASPIRETESTING001", property.GetCustomAttribute<ExperimentalAttribute>()!.DiagnosticId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PathsArgumentsAndDefaultRunBehaviorArePreserved(bool testMode)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = workspace.CreateDirectory("tests with spaces").FullName;
        var path = Path.Combine(project, "tests.csproj");
        File.WriteAllText(path, "<Project />");
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path, outputHelper);
        if (testMode)
        {
            builder.Configuration["ASPIRE_TEST_OUTPUT_PATH"] = Path.Combine(workspace.Path, "reports with spaces");
        }
        var resource = builder.AddMsTest("tests", Path.GetRelativePath(workspace.Path, path),
            new MsTestOptions { Configuration = "Release", TargetFramework = "net10.0", Args = ["--filter", "Name~a b"] })
            .WithEnvironment("CUSTOM_VALUE", "value")
            .WithArgs("--settings", "settings with spaces.runsettings");
        var testing = Assert.Single(resource.Resource.Annotations.OfType<TestingAnnotation>()).Options!;
        Assert.Equal(testMode, testing.IsTestRun);
        Assert.Equal("dotnet", resource.Resource.Command);
        Assert.Equal(project, resource.Resource.WorkingDirectory);
        Assert.IsAssignableFrom<IResourceWithServiceDiscovery>(resource.Resource);
        Assert.Same(ManifestPublishingCallbackAnnotation.Ignore,
            Assert.Single(resource.Resource.Annotations.OfType<ManifestPublishingCallbackAnnotation>()));
        await using var app = builder.Build();
        await builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services,
            app.Services.GetRequiredService<DistributedApplicationModel>()), TestContext.Current.CancellationToken);
        var arguments = await ArgumentEvaluator.GetArgumentListAsync(resource.Resource, app.Services);
        var expected = new List<string> { "run", "--project", path, "--no-launch-profile",
            "--configuration", "Release", "--framework", "net10.0", "--", "--filter", "Name~a b",
            "--settings", "settings with spaces.runsettings" };
        if (testMode)
        {
            Assert.True(Directory.Exists(testing.OutputPath));
            expected.AddRange(["--report-trx", "--results-directory", testing.OutputPath!, "--report-trx-filename", "results.trx"]);
        }
        else
        {
            Assert.Null(testing.OutputPath);
            Assert.Null(testing.RunContext);
        }
        Assert.Equal(expected, arguments);
        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(resource.Resource, serviceProvider: app.Services);
        Assert.Equal("value", environment["CUSTOM_VALUE"]);
    }

    [Theory]
    [InlineData("--results-directory")]
    [InlineData("--report-trx-filename=/outside/results.trx")]
    [InlineData("--report-trx")]
    [InlineData("@response.rsp")]
    [InlineData("--server")]
    [InlineData("--retry-failed-tests")]
    public void ReservedRunnerArgumentsAreRejected(string argument)
    {
        Assert.Throws<ArgumentException>(() => MsTestHostingExtensions.ValidateRunnerArguments([argument]));
    }

    [Fact]
    public async Task LaterArgumentRedirectionAndMultipleReplicasAreRejected()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.Path, "tests.csproj");
        File.WriteAllText(path, "<Project />");
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        builder.Configuration["ASPIRE_TEST_OUTPUT_PATH"] = Path.Combine(workspace.Path, "reports");
        var resource = builder.AddMsTest("tests", path).WithArgs("--results-directory", "elsewhere");
        await using var app = builder.Build();
        await builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services,
            app.Services.GetRequiredService<DistributedApplicationModel>()), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ArgumentException>(async () => await ArgumentEvaluator.GetArgumentListAsync(resource.Resource, app.Services));

        resource.Resource.Annotations.Add(new ReplicaAnnotation(2));
        await Assert.ThrowsAsync<DistributedApplicationException>(() => builder.Eventing.PublishAsync(new BeforeStartEvent(
            app.Services, app.Services.GetRequiredService<DistributedApplicationModel>()), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TestingResourceCannotRestartAndReferencesAndWaitsCompose()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.Path, "tests.csproj");
        File.WriteAllText(path, "<Project />");
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        builder.Configuration["ASPIRE_TEST_OUTPUT_PATH"] = Path.Combine(workspace.Path, "reports");
        var api = builder.AddResource(new DotnetProjectResource("api", workspace.Path)).WithHttpEndpoint();
        var resource = builder.AddMsTest("tests", path).WithReference(api).WaitFor(api);
        Assert.Same(api.Resource, Assert.Single(resource.Resource.Annotations.OfType<WaitAnnotation>()).Resource);
        Assert.NotEmpty(resource.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>());
        await using var app = builder.Build();
        await builder.Eventing.PublishAsync(new BeforeResourceStartedEvent(resource.Resource, app.Services),
            TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<DistributedApplicationException>(() => builder.Eventing.PublishAsync(
            new BeforeResourceStartedEvent(resource.Resource, app.Services), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void InvalidPathsAndOptionsAreRejectedBeforeRegistration()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        Assert.Throws<ArgumentNullException>(() => MsTestHostingExtensions.AddMsTest(null!, "tests", "test.csproj"));
        Assert.Throws<ArgumentNullException>(() => builder.AddMsTest("tests", null!));
        Assert.Throws<ArgumentException>(() => builder.AddMsTest("tests", "test.fsproj"));
        Assert.Throws<ArgumentException>(() => builder.AddMsTest("tests", workspace.Path));
        Assert.Throws<FileNotFoundException>(() => builder.AddMsTest("tests", Path.Combine(workspace.Path, "missing.csproj")));
        var path = Path.Combine(workspace.Path, "tests.csproj");
        File.WriteAllText(path, "<Project />");
        Assert.Throws<ArgumentException>(() => builder.AddMsTest("tests", path, new MsTestOptions { TargetFramework = " " }));
        Assert.Throws<ArgumentException>(() => builder.AddMsTest("tests", path, new MsTestOptions { Configuration = "" }));
        Assert.Throws<ArgumentException>(() => builder.AddMsTest("tests", path, new MsTestOptions { Args = [null!] }));
        Assert.Empty(builder.Resources.OfType<MsTestResource>());
    }
}
