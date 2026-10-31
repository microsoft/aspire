// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;

#pragma warning disable ASPIRETESTING001

namespace Aspire.Hosting.Go.Tests;

public class GoTestResourceTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void PublicContractIsExperimentalAndExported()
    {
        foreach (var member in new MemberInfo[]
        {
            typeof(GoTestingExtensions), typeof(GoTestingExtensions).GetMethod(nameof(GoTestingExtensions.AddGoTest))!,
            typeof(GoTestResource)
        })
        {
            var attribute = member.GetCustomAttribute<ExperimentalAttribute>()!;
            Assert.Equal("ASPIRETESTING001", attribute.DiagnosticId);
            Assert.Equal("https://aka.ms/aspire/diagnostics/{0}", attribute.UrlFormat);
        }
        var method = typeof(GoTestingExtensions).GetMethod(nameof(GoTestingExtensions.AddGoTest))!;
        Assert.NotNull(method.GetCustomAttribute<AspireExportAttribute>());
        Assert.NotNull(typeof(GoTestResource).GetCustomAttribute<AspireExportAttribute>());
        Assert.NotNull(method.GetParameters()[1].GetCustomAttribute<ResourceNameAttribute>());
        Assert.Equal(typeof(IResourceBuilder<GoTestResource>), method.ReturnType);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PathsArgumentsEnvironmentAndReferencesCompose(bool testing)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var directory = workspace.CreateDirectory("go tests with spaces").FullName;
        using var builder = TestDistributedApplicationBuilder.Create(options => options.ProjectDirectory = workspace.Path, outputHelper);
        if (testing)
        {
            builder.Configuration["ASPIRE_TEST_OUTPUT_PATH"] = Path.Combine(workspace.Path, "output");
        }
        var api = builder.AddExternalService("api", "https://example.com");
        var resource = builder.AddGoTest("tests", Path.GetRelativePath(workspace.Path, directory), ["./api", "./other"])
            .WithArgs("-tags=integration", "-run=TestAPI", "-args", "custom value")
            .WithReference(api)
            .WaitFor(api)
            .WithEnvironment("CUSTOM", "value");
        Assert.Equal(directory, resource.Resource.WorkingDirectory);
        Assert.Equal("go", resource.Resource.Command);
        Assert.IsAssignableFrom<IResourceWithServiceDiscovery>(resource.Resource);
        Assert.Same(api.Resource, Assert.Single(resource.Resource.Annotations.OfType<WaitAnnotation>()).Resource);
        Assert.Same(ManifestPublishingCallbackAnnotation.Ignore,
            Assert.Single(resource.Resource.Annotations.OfType<ManifestPublishingCallbackAnnotation>()));
        await using var app = builder.Build();
        var services = app.Services;
        await builder.Eventing.PublishAsync(
            new BeforeStartEvent(services, new DistributedApplicationModel(builder.Resources)), TestContext.Current.CancellationToken);
        var expected = new List<string> { "test" };
        if (testing)
        {
            expected.AddRange(["-json", "-count=1"]);
        }
        expected.AddRange(["-tags=integration", "-run=TestAPI", "./api", "./other", "-args", "custom value"]);
        Assert.Equal(expected, await ArgumentEvaluator.GetArgumentListAsync(resource.Resource, services));
        var options = Assert.Single(resource.Resource.Annotations.OfType<TestingAnnotation>()).Options!;
        Assert.Equal(testing, options.IsTestRun);
        Assert.Equal(testing, options.OutputPath is not null);
    }

    [Theory]
    [InlineData("-json=false")]
    [InlineData("--json")]
    [InlineData("-count=2")]
    [InlineData("-test.count=2")]
    [InlineData("-c")]
    [InlineData("-list=.")]
    [InlineData("-exec=wrapper")]
    [InlineData("-bench=.")]
    [InlineData("-fuzz=Fuzz")]
    [InlineData("-v=false")]
    [InlineData("--")]
    public void ReservedFlagsFail(string value)
    {
        Assert.Throws<ArgumentException>(() => GoTestingExtensions.ValidateArguments([value]));
    }

    [Fact]
    public void ReportingOverridesInBinaryTailFail()
    {
        Assert.Throws<ArgumentException>(() => GoTestingExtensions.ValidateArguments(["-args", "-test.v=false"]));
        GoTestingExtensions.ValidateArguments(["-race", "-tags=integration", "-timeout=30s", "-cpu=1,2", "-args", "--custom"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-json=false")]
    public void InvalidPackagePatternFails(string pattern)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        Assert.Throws<ArgumentException>(() => builder.AddGoTest("tests", ".", [pattern]));
    }

    [Fact]
    public void EmptyPackageListFails()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        Assert.Throws<ArgumentException>(() => builder.AddGoTest("tests", ".", []));
    }

    [Theory]
    [InlineData("-json=false")]
    [InlineData("-count=2")]
    [InlineData("-fuzz=Fuzz")]
    [RequiresTools(["go"])]
    public async Task LaterDeferredGoFlagsCannotOverrideReporting(string flag)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        builder.Configuration["ASPIRE_TEST_OUTPUT_PATH"] = Path.Combine(workspace.Path, "output");
        var parameter = builder.AddParameter("flags", flag);
        var resource = builder.AddGoTest("tests", workspace.Path)
            .WithEnvironment("GOFLAGS", parameter)
            .WithEnvironment("GOENV", "off");
        await using var app = builder.Build();
        await builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services,
            new DistributedApplicationModel(builder.Resources)), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(resource.Resource, serviceProvider: app.Services));
    }

    [Fact]
    [RequiresTools(["go"])]
    public async Task SettingsProbePreservesModuleAndToolchainSelectionForExecution()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "go.mod"), "module example.com/tests\n\ngo 999.0\n");
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        builder.Configuration["ASPIRE_TEST_OUTPUT_PATH"] = Path.Combine(workspace.Path, "output");
        var resource = builder.AddGoTest("tests", workspace.Path)
            .WithEnvironment("GOFLAGS", "-tags=integration")
            .WithEnvironment("GOENV", "off")
            .WithEnvironment("GOTOOLCHAIN", "auto")
            .WithEnvironment("GOWORK", "off");
        await using var app = builder.Build();
        await builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services,
            new DistributedApplicationModel(builder.Resources)), TestContext.Current.CancellationToken);
        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(resource.Resource, serviceProvider: app.Services);
        Assert.Equal("-tags=integration", environment["GOFLAGS"]);
        Assert.Equal("auto", environment["GOTOOLCHAIN"]);
        Assert.Equal("off", environment["GOWORK"]);
        Assert.False(environment.ContainsKey("GO111MODULE"));
    }

    [Fact]
    public async Task DeferredArgumentValuesComposeWithoutLosingReferences()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        builder.Configuration["ASPIRE_TEST_OUTPUT_PATH"] = Path.Combine(workspace.Path, "output");
        var parameter = builder.AddParameter("argument", "custom value");
        var resource = builder.AddGoTest("tests", workspace.Path)
            .WithArgs(context =>
            {
                context.Args.Add("-args");
                context.Args.Add(parameter.Resource);
            });
        await using var app = builder.Build();
        await builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services,
            new DistributedApplicationModel(builder.Resources)), TestContext.Current.CancellationToken);
        Assert.Equal(["test", "-json", "-count=1", "./...", "-args", "custom value"],
            await ArgumentEvaluator.GetArgumentListAsync(resource.Resource, app.Services));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TestingRejectsReplicasAndTerminalsBeforeLaunch(bool terminal)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        builder.Configuration["ASPIRE_TEST_OUTPUT_PATH"] = Path.Combine(workspace.Path, "output");
        var resource = builder.AddGoTest("tests", workspace.Path);
        resource.Resource.Annotations.Add(terminal
            ? new TerminalAnnotation(new TerminalOptions())
            : new ReplicaAnnotation(2));
        await using var app = builder.Build();
        await Assert.ThrowsAsync<DistributedApplicationException>(() => builder.Eventing.PublishAsync(
            new BeforeStartEvent(app.Services, new DistributedApplicationModel(builder.Resources)),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TestingRejectsASecondStart()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        builder.Configuration["ASPIRE_TEST_OUTPUT_PATH"] = Path.Combine(workspace.Path, "output");
        var resource = builder.AddGoTest("tests", workspace.Path);
        await using var app = builder.Build();
        await builder.Eventing.PublishAsync(new BeforeResourceStartedEvent(resource.Resource, app.Services),
            TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<DistributedApplicationException>(() => builder.Eventing.PublishAsync(
            new BeforeResourceStartedEvent(resource.Resource, app.Services), TestContext.Current.CancellationToken));
    }
}
