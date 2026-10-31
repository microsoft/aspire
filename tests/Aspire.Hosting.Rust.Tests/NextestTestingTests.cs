// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;

#pragma warning disable ASPIRETESTING001

namespace Aspire.Hosting.Rust.Tests;

public class NextestTestingTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void PublicContractIsExperimentalAndExported()
    {
        var method = typeof(NextestTestingExtensions).GetMethod(nameof(NextestTestingExtensions.WithNextestTesting))!;
        foreach (var member in new MemberInfo[] { typeof(NextestTestingExtensions), method })
        {
            var experimental = member.GetCustomAttribute<ExperimentalAttribute>()!;
            Assert.Equal("ASPIRETESTING001", experimental.DiagnosticId);
            Assert.Equal("https://aka.ms/aspire/diagnostics/{0}", experimental.UrlFormat);
        }
        Assert.NotNull(method.GetCustomAttribute<AspireExportAttribute>());
        Assert.Equal(typeof(IResourceBuilder<ExecutableResource>), method.ReturnType);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingExecutableArgumentsAndEnvironmentCompose(bool testing)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        if (testing)
        {
            builder.Configuration["ASPIRE_TEST_OUTPUT_PATH"] = Path.Combine(workspace.Path, "output");
        }
        var api = builder.AddExternalService("api", "https://example.com");
        var parameter = builder.AddParameter("features", "integration");
        var directory = workspace.CreateDirectory("crate with spaces").FullName;
        var original = builder.AddExecutable("tests", "cargo", directory, "+stable", "nextest", "run", "--workspace");
        var resource = original.WithNextestTesting()
            .WithArgs("--package=fixture", "--cargo-profile=test", "--target=host", "--manifest-path=Cargo.toml")
            .WithArgs(context =>
            {
                context.Args.Add("--features");
                context.Args.Add(parameter.Resource);
            })
            .WithEnvironment("CUSTOM", "value")
            .WithEnvironment("API_URL", api)
            .WaitFor(api);
        Assert.Same(original, resource);
        Assert.Equal(directory, resource.Resource.WorkingDirectory);
        Assert.Same(api.Resource, Assert.Single(resource.Resource.Annotations.OfType<WaitAnnotation>()).Resource);
        Assert.Same(ManifestPublishingCallbackAnnotation.Ignore,
            Assert.Single(resource.Resource.Annotations.OfType<ManifestPublishingCallbackAnnotation>()));
        await using var app = builder.Build();
        await builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services,
            new DistributedApplicationModel(builder.Resources)), TestContext.Current.CancellationToken);
        var expected = new List<string>
        {
            "+stable", "nextest", "run", "--workspace", "--package=fixture", "--cargo-profile=test",
            "--target=host", "--manifest-path=Cargo.toml", "--features", "integration"
        };
        var options = Assert.Single(resource.Resource.Annotations.OfType<TestingAnnotation>()).Options!;
        Assert.Equal(testing, options.IsTestRun);
        if (testing)
        {
            expected.AddRange(["--config-file=" + Path.Combine(options.OutputPath!, "nextest.toml"),
                "--profile=aspire", "--no-fail-fast", "--no-tests=fail", "--retries=0", "--message-format=human"]);
            Assert.True(File.Exists(Path.Combine(options.OutputPath!, "nextest.toml")));
        }
        Assert.Equal(expected, await ArgumentEvaluator.GetArgumentListAsync(resource.Resource, app.Services));
        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(resource.Resource, serviceProvider: app.Services);
        Assert.Equal("value", environment["CUSTOM"]);
        Assert.Equal("https://example.com/", environment["API_URL"]);
    }

    [Fact]
    public async Task ConfigurationEscapesPathsAndEnforcesReporting()
    {
        await Verify(NextestTestingExtensions.CreateConfiguration("C:\\crate \"with spaces\"\\日本語🦀\n\\junit.xml"), "toml");
    }

    [Theory]
    [InlineData("--config-file=other.toml")]
    [InlineData("--tool-config-file=tool:other.toml")]
    [InlineData("--profile=ci")]
    [InlineData("-Pci")]
    [InlineData("--retries=1")]
    [InlineData("--stress-count=2")]
    [InlineData("--stress-duration=1h")]
    [InlineData("--no-tests=pass")]
    [InlineData("--no-run")]
    [InlineData("--no-capture")]
    [InlineData("--message-format=libtest-json")]
    [InlineData("--override-version-check")]
    [InlineData("--fail-fast")]
    [InlineData("--ff")]
    [InlineData("--max-fail=1")]
    [InlineData("--debugger=gdb")]
    [InlineData("--tracer=strace")]
    [InlineData("--")]
    [InlineData("-Rlatest")]
    public void ManagedOrUnsupportedFlagsFail(string flag)
    {
        Assert.Throws<ArgumentException>(() => NextestTestingExtensions.ValidateArguments(["nextest", "run", flag]));
    }

    [Theory]
    [InlineData("test")]
    [InlineData("run")]
    [InlineData("nextest")]
    public void NonNextestEntrypointsFail(string command)
    {
        Assert.Throws<ArgumentException>(() => NextestTestingExtensions.ValidateArguments([command]));
    }

    [Theory]
    [InlineData("terminal")]
    [InlineData("replicas")]
    [InlineData("command")]
    public async Task UnsupportedLaunchFailsBeforeExecution(string scenario)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        builder.Configuration["ASPIRE_TEST_OUTPUT_PATH"] = Path.Combine(workspace.Path, "output");
        var resource = builder.AddExecutable("tests", scenario == "command" ? "other" : "cargo", workspace.Path, "nextest", "run").WithNextestTesting();
        if (scenario != "command")
        {
            resource.Resource.Annotations.Add(scenario == "terminal"
                ? new TerminalAnnotation(new TerminalOptions()) : new ReplicaAnnotation(2));
        }
        await using var app = builder.Build();
        await Assert.ThrowsAsync<DistributedApplicationException>(() => builder.Eventing.PublishAsync(
            new BeforeStartEvent(app.Services, new DistributedApplicationModel(builder.Resources)),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SecondStartFails()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        builder.Configuration["ASPIRE_TEST_OUTPUT_PATH"] = Path.Combine(workspace.Path, "output");
        var resource = builder.AddExecutable("tests", "cargo", workspace.Path, "nextest", "run").WithNextestTesting();
        await using var app = builder.Build();
        await builder.Eventing.PublishAsync(new BeforeResourceStartedEvent(resource.Resource, app.Services),
            TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<DistributedApplicationException>(() => builder.Eventing.PublishAsync(
            new BeforeResourceStartedEvent(resource.Resource, app.Services), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void OutputMustBeInsideAssignedDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outside = workspace.CreateDirectory("outside").FullName;
        var run = workspace.CreateDirectory("run").FullName;
        Assert.Throws<InvalidDataException>(() => NextestJunitReport.ValidateDirectory(outside, run));
    }
}
