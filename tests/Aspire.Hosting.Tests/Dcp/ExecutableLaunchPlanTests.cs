// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001

using System.Text.Json;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Dcp.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Tests.Dcp;

[Trait("Partition", "4")]
public class ExecutableLaunchPlanTests
{
    [Fact]
    public async Task OwnedLaunchToolArgumentsAreSeparatedFromIdeArguments()
    {
        var resource = new ExecutableResource("app", "tool", "/tmp");
        resource.Annotations.Add(new LaunchToolArgsCallbackAnnotation(
            static _ => Task.CompletedTask,
            owningLaunchConfigurationType: "test",
            showInCommandLine: true));
        resource.Annotations.Add(SupportsDebuggingAnnotation.Create(
            resource.Name,
            "test",
            static _ => Task.FromResult(new ExecutableLaunchConfiguration("test"))));
        var configurationValues =
            new Dictionary<string, string?>
            {
                [DcpExecutor.DebugSessionPortVar] = "12345",
                [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo
                {
                    ProtocolsSupported = ["test"],
                    SupportedLaunchConfigurations = ["test"]
                })
            };
        var configuration = CreateExecutionConfiguration(
            [("run", false), ("app-arg", false)],
            launchToolArgumentCount: 1);

        var plan = await ResolveLaunchPlanAsync(resource, configuration, configurationValues);

        Assert.Equal(ExecutableLaunchMechanism.Ide, plan.Mechanism);
        Assert.Equal(["app-arg"], plan.Arguments);
        Assert.Collection(
            plan.DisplayArguments,
            argument =>
            {
                Assert.Equal("run", argument.Value);
                Assert.Equal(ExecutableLaunchArgumentRole.LaunchTool, argument.Role);
                Assert.Null(argument.EffectiveArgumentIndex);
            },
            argument =>
            {
                Assert.Equal("app-arg", argument.Value);
                Assert.Equal(ExecutableLaunchArgumentRole.Application, argument.Role);
                Assert.Equal(0, argument.EffectiveArgumentIndex);
            });
        Assert.Single(plan.LaunchConfigurations);
    }

    [Fact]
    public async Task UnsupportedIdeCapabilitySelectsCompleteProcessPlan()
    {
        var resource = new ExecutableResource("app", "tool", "/tmp");
        resource.Annotations.Add(new LaunchToolArgsCallbackAnnotation(
            static _ => Task.CompletedTask,
            owningLaunchConfigurationType: "test",
            showInCommandLine: true));
        resource.Annotations.Add(SupportsDebuggingAnnotation.Create(
            resource.Name,
            "test",
            static _ => Task.FromResult(new ExecutableLaunchConfiguration("test"))));
        var configurationValues =
            new Dictionary<string, string?>
            {
                [DcpExecutor.DebugSessionPortVar] = "12345",
                [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo
                {
                    ProtocolsSupported = ["test"],
                    SupportedLaunchConfigurations = ["other"]
                })
            };
        var configuration = CreateExecutionConfiguration(
            [("run", false), ("app-arg", false)],
            launchToolArgumentCount: 1);

        var plan = await ResolveLaunchPlanAsync(resource, configuration, configurationValues);

        Assert.Equal(ExecutableLaunchMechanism.Process, plan.Mechanism);
        Assert.Equal(["run", "app-arg"], plan.Arguments);
        Assert.Empty(plan.LaunchConfigurations);
    }

    [Fact]
    public async Task LegacyProjectRecipeProducesCompleteProcessInvocation()
    {
        var projectPath = Path.Combine("tmp", "project.csproj");
        var resource = new ProjectResource("project");
        resource.Annotations.Add(new TestProjectMetadata(projectPath));

        var plan = await ResolveLaunchPlanAsync(
            resource,
            CreateExecutionConfiguration([]),
            []);

        Assert.Equal(ExecutableLaunchMechanism.Process, plan.Mechanism);
        Assert.Equal("dotnet", plan.Command);
        Assert.Equal(Path.GetDirectoryName(projectPath), plan.WorkingDirectory);
        var expectedArguments = new List<string> { "run", "--project", projectPath };
        if (new DistributedApplicationOptions().Configuration is { } configuration)
        {
            expectedArguments.AddRange(["--configuration", configuration]);
        }
        expectedArguments.Add("--no-launch-profile");
        Assert.Equal(expectedArguments, plan.Arguments);
        Assert.Empty(plan.DisplayArguments);
    }

    [Fact]
    public async Task CompatibilityProjectLaunchDoesNotWithholdInactiveOwnedToolArguments()
    {
        var resource = new ProjectResource("project");
        resource.Annotations.Add(new TestProjectMetadata("/tmp/project.csproj"));
        resource.Annotations.Add(new LaunchToolArgsCallbackAnnotation(
            static _ => Task.CompletedTask,
            owningLaunchConfigurationType: KnownLaunchConfigurationTypes.Project,
            showInCommandLine: true));
        resource.Annotations.Add(SupportsDebuggingAnnotation.Create(
            resource.Name,
            "custom",
            static _ => Task.FromResult(new ExecutableLaunchConfiguration("custom"))));
        var configurationValues =
            new Dictionary<string, string?>
            {
                [DcpExecutor.DebugSessionPortVar] = "12345"
            };
        var configuration = CreateExecutionConfiguration(
            [("run", false), ("app-arg", false)],
            launchToolArgumentCount: 1);

        var plan = await ResolveLaunchPlanAsync(resource, configuration, configurationValues);

        Assert.Equal(ExecutableLaunchMechanism.Ide, plan.Mechanism);
        Assert.Equal(["run", "app-arg"], plan.Arguments);
        Assert.Single(plan.LaunchConfigurations);
    }

    [Fact]
    public void ForcedProcessProjectRetainsProjectLaunchMode()
    {
        var resource = new ProjectResource("project");
        resource.Annotations.Add(new ForceProcessExecutionAnnotation());
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [KnownConfigNames.DebugSessionRunMode] = ExecutableLaunchMode.Debug
            })
            .Build();
        var policy = new ExecutableLaunchPolicy(configuration);

        var decision = policy.Decide(resource);

        Assert.Equal(ExecutableLaunchMechanism.Process, decision.Mechanism);
        Assert.Equal(ExecutableLaunchMode.NoDebug, decision.LaunchMode);
        Assert.Equal(ExecutableLaunchMode.Debug, decision.ProjectLaunchMode);
    }

    [Fact]
    public void RendererAppliesPlanWithoutRuntimeFallback()
    {
        var resource = new ExecutableResource("app", "tool", "/tmp");
        var executable = Executable.Create("app-12345678", "stale-tool");
        executable.Spec.ExecutionType = ExecutionType.IDE;
        executable.Spec.FallbackExecutionTypes = [ExecutionType.Process];
        executable.Spec.Args = ["stale"];
        var renderedResource = new RenderedModelResource<Executable>(resource, executable);
        var plan = new ExecutableLaunchPlan(
            "tool",
            "/tmp",
            ExecutableLaunchMechanism.Process,
            ["app-arg"],
            [new("ENV", "value")],
            [],
            [new ExecutableLaunchArgument(
                "app-arg",
                isSensitive: false,
                executable: true,
                display: true,
                effectiveArgumentIndex: 0,
                role: ExecutableLaunchArgumentRole.Application)]);
        ExecutableCreator.Render(
            renderedResource,
            plan,
            pemCertificates: null,
            NullLogger<ExecutableCreator>.Instance);

        Assert.Equal(ExecutionType.Process, executable.Spec.ExecutionType);
        Assert.Null(executable.Spec.FallbackExecutionTypes);
        Assert.Equal(["app-arg"], executable.Spec.Args);
        Assert.False(executable.Metadata.Annotations?.ContainsKey(Executable.LaunchConfigurationsAnnotation) is true);
        Assert.Collection(
            executable.Spec.Env!,
            variable =>
            {
                Assert.Equal("ENV", variable.Name);
                Assert.Equal("value", variable.Value);
            });
    }

    [Fact]
    public async Task ResolverRejectsMultipleLaunchRecipes()
    {
        var resource = new ExecutableResource("app", "tool", "/tmp");
        resource.Annotations.Add(new ExecutableLaunchRecipeAnnotation(DirectExecutableLaunchRecipe.Instance));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ResolveLaunchPlanAsync(
                resource,
                CreateExecutionConfiguration([]),
                []));

        Assert.Equal(
            "Resource 'app' must have exactly one executable launch recipe, but 2 were found.",
            exception.Message);
    }

    private static Task<ExecutableLaunchPlan> ResolveLaunchPlanAsync(
        IResource resource,
        IExecutionConfigurationResult executionConfiguration,
        IEnumerable<KeyValuePair<string, string?>> configurationValues)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationValues)
            .Build();

        return ExecutableCreator.ResolveLaunchPlanAsync(
            resource,
            executionConfiguration,
            configuration,
            new DistributedApplicationOptions(),
            new ExecutableLaunchPolicy(configuration),
            NullLogger.Instance,
            CancellationToken.None);
    }

    private static ExecutionConfigurationResult CreateExecutionConfiguration(
        IReadOnlyList<(string Value, bool IsSensitive)> arguments,
        int launchToolArgumentCount = 0)
    {
        IExecutionConfigurationData[] additionalData = launchToolArgumentCount > 0
            ? [new LaunchToolArgumentsData(launchToolArgumentCount, ShowInCommandLine: true)]
            : [];
        return new()
        {
            References = [],
            ArgumentsWithUnprocessed = arguments.Select(static argument =>
                ((object)argument.Value, argument.Value, argument.IsSensitive)),
            EnvironmentVariablesWithUnprocessed = [],
            AdditionalConfigurationData = additionalData,
            Exception = null
        };
    }

    private sealed class TestProjectMetadata(string projectPath) : IProjectMetadata
    {
        public string ProjectPath { get; } = projectPath;

        public LaunchSettings LaunchSettings { get; } = new();
    }
}
