// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIRECERTIFICATES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREPERSISTENCE001 // Resource lifetime APIs are experimental.
#pragma warning disable ASPIREUSERSECRETS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Aspire.Dashboard.Model;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Dcp.Model;
using Aspire.Hosting.DevTunnels;
using Aspire.Hosting.Diagnostics;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.UserSecrets;
using k8s.Models;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.DotNet.RemoteExecutor;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace Aspire.Hosting.Tests.Dcp;

[Trait("Partition", "4")]
public class DcpExecutorTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task ContainersArePassedOtelServiceName()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        builder.AddContainer("CustomName", "container").WithOtlpExporter();

        var kubernetesService = new TestKubernetesService();

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);

        // Act
        await appExecutor.RunApplicationAsync();

        // Assert
        var container = Assert.Single(kubernetesService.CreatedResources.OfType<Container>());
        Assert.Equal("CustomName", container.Metadata.Annotations["otel-service-name"]);
    }

    [Fact]
    public async Task DockerfileContainerBuildSpecIncludesPlatform()
    {
        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync(outputHelper);

        var builder = DistributedApplication.CreateBuilder();
#pragma warning disable ASPIREPIPELINES003 // ContainerBuildOptions APIs are experimental.
        builder.AddDockerfile("mycontainer", tempDockerfileContext.ContextPath, tempDockerfileContext.DockerfilePath)
               .WithContainerBuildOptions(ctx => ctx.TargetPlatform = ContainerTargetPlatform.LinuxArm64);
#pragma warning restore ASPIREPIPELINES003

        var kubernetesService = new TestKubernetesService();

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);

        await appExecutor.RunApplicationAsync();

        var container = Assert.Single(kubernetesService.CreatedResources.OfType<Container>());
        Assert.NotNull(container.Spec.Build);
        Assert.Equal("linux/arm64", container.Spec.Build!.Platform);
    }

    [Fact]
    public async Task DockerfileContainerBuildSpec_RunMode_DefaultsToHostPlatform()
    {
        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync(outputHelper);

        var builder = DistributedApplication.CreateBuilder();
        builder.AddDockerfile("mycontainer", tempDockerfileContext.ContextPath, tempDockerfileContext.DockerfilePath);

        var kubernetesService = new TestKubernetesService();

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);

        await appExecutor.RunApplicationAsync();

        var container = Assert.Single(kubernetesService.CreatedResources.OfType<Container>());
        Assert.NotNull(container.Spec.Build);
        Assert.Null(container.Spec.Build!.Platform);
    }

    [Fact]
    public async Task ResourceStarted_ProjectHasReplicas_EventRaisedOnce()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        var resource = builder.AddProject<Projects.ServiceA>("ServiceA")
            .WithReplicas(2).Resource;

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var dcpOptions = new DcpOptions { DashboardPath = "./dashboard", ResourceNameSuffix = "suffix" };

        var startingEvents = new List<OnResourceStartingContext>();
        var events = new DcpExecutorEvents();
        events.Subscribe<OnResourceStartingContext>((context) =>
        {
            startingEvents.Add(context);
            return Task.CompletedTask;
        });

        var channel = Channel.CreateUnbounded<string>();
        events.Subscribe<OnResourceChangedContext>(async (context) =>
        {
            if (context.Resource == resource)
            {
                await channel.Writer.WriteAsync(context.DcpResourceName);
            }
        });

        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, dcpOptions: dcpOptions, events: events);
        await appExecutor.RunApplicationAsync();

        var executables = GetCreatedExecutablesForResource(kubernetesService, "ServiceA");
        Assert.Equal(2, executables.Count);

        var e = Assert.Single(startingEvents);
        Assert.Equal(resource, e.Resource);

        var resourceIds = new HashSet<string>();
        var watchResourceTask = Task.Run(async () =>
        {
            await foreach (var item in channel.Reader.ReadAllAsync())
            {
                resourceIds.Add(item);
                if (resourceIds.Count == 2)
                {
                    break;
                }
            }
        });
        await watchResourceTask.DefaultTimeout();

        Assert.Equal(2, resourceIds.Count);
    }

    [Theory]
    [InlineData(ExecutionType.IDE, false, null, new string[] { "--test1", "--test2" })]
    [InlineData(ExecutionType.IDE, true, new string[] { "--withargs-test" }, new string[] { "--withargs-test" })]
    [InlineData(ExecutionType.Process, false, new string[] { "--test1", "--test2" }, new string[] { "--test1", "--test2" })]
    [InlineData(ExecutionType.Process, true, new string[] { "--", "--test1", "--test2", "--withargs-test" }, new string[] { "--", "--test1", "--test2", "--withargs-test" })]
    public async Task CreateExecutable_LaunchProfileHasCommandLineArgs_AnnotationsAdded(string executionType, bool addAppHostArgs, string[]? expectedArgs, string[]? expectedAnnotations)
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        IConfiguration? configuration = null;
        if (executionType == ExecutionType.IDE)
        {
            var configurationBuilder = new ConfigurationBuilder();
            configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DcpExecutor.DebugSessionPortVar] = "8080"
            });

            configuration = configurationBuilder.Build();
        }

        var resourceBuilder = builder.AddProject<Projects.ServiceA>("ServiceA");
        if (addAppHostArgs)
        {
            resourceBuilder
                .WithArgs(c =>
                {
                    c.Args.Add("--withargs-test");
                });
        }

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var dcpOptions = new DcpOptions { DashboardPath = "./dashboard", ResourceNameSuffix = "suffix" };

        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, dcpOptions: dcpOptions, events: events, configuration: configuration);
        await appExecutor.RunApplicationAsync();

        var executables = GetCreatedExecutablesForResource(kubernetesService, "ServiceA");
        var exe = Assert.Single(executables);

        // Ignore dotnet specific args for .NET project in process execution.
        var callArgs = executionType == ExecutionType.IDE ? exe.Spec.Args : exe.Spec.Args![^(expectedArgs?.Length ?? 0)..];
        Assert.Equal(expectedArgs, callArgs);

        Assert.True(exe.TryGetAnnotationAsObjectList<AppLaunchArgumentAnnotation>(CustomResource.ResourceAppArgsAnnotation, out var argAnnotations));
        Assert.Equal(expectedAnnotations, argAnnotations.Select(a => a.Argument));
        AssertEffectiveArgumentIndexesMatchSpecArgs(argAnnotations, exe.Spec.Args);
    }

    [Theory]
    [InlineData()]
    [InlineData("--arg1", "foo")]
    public async Task CreateExecutable_ToolHasCommandLineArgs_AnnotationsAdded(params string[] toolArgs)
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        var resourceBuilder = builder.AddDotnetTool("tool", "package")
            .WithArgs(toolArgs);

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var dcpOptions = new DcpOptions { DashboardPath = "./dashboard", ResourceNameSuffix = "suffix" };

        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, dcpOptions: dcpOptions, events: events);
        await appExecutor.RunApplicationAsync();

        var executables = kubernetesService.CreatedResources.OfType<Executable>().ToList();
        var exe = Assert.Single(executables);

        string[] dotnetToolExecArgs = ["tool", "exec", "package", "--yes", "--"];
        string[] callArgs = [.. dotnetToolExecArgs, .. toolArgs];

        Assert.Equal(callArgs, exe.Spec.Args);

        Assert.True(exe.TryGetAnnotationAsObjectList<AppLaunchArgumentAnnotation>(CustomResource.ResourceAppArgsAnnotation, out var argAnnotations));
        Assert.Equal(toolArgs, argAnnotations.Select(a => a.Argument));
        AssertEffectiveArgumentIndexesMatchSpecArgs(argAnnotations, exe.Spec.Args);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DotnetToolResource_ExtensionMode_OwnedLaunchToolArgsAreWithheldAndRespectCommandLineVisibility(bool showInCommandLine)
    {
        var builder = DistributedApplication.CreateBuilder();

        builder.AddDotnetTool("tool", "package")
            .WithArgs("app-arg")
            .WithLaunchToolArgs(
                static context =>
                {
                    context.Args.Add("tool");
                    context.Args.Add("exec");
                    context.Args.Add("package");
                    context.Args.Add("--yes");
                    context.Args.Add("--");
                },
                ownedByLaunchConfigurationType: "test",
                showInCommandLine: showInCommandLine)
            .WithDebugSupport(
                mode => new ExecutableLaunchConfiguration("test") { Mode = mode },
                "test");

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo { ProtocolsSupported = ["test"], SupportedLaunchConfigurations = ["test"] }),
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234",
            [KnownConfigNames.DebugSessionRunMode] = "Debug"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "tool");
        Assert.Equal(ExecutionType.IDE, exe.Spec.ExecutionType);
        Assert.Equal(["app-arg"], exe.Spec.Args);
        Assert.Null(exe.Spec.FallbackExecutionTypes);

        Assert.True(exe.TryGetAnnotationAsObjectList<AppLaunchArgumentAnnotation>(CustomResource.ResourceAppArgsAnnotation, out var displayArgs));
        string[] dotnetToolExecArgs = ["tool", "exec", "package", "--yes", "--"];
        string[] expectedDisplayArgs = showInCommandLine ? [.. dotnetToolExecArgs, "app-arg"] : ["app-arg"];
        Assert.Equal(expectedDisplayArgs, displayArgs.Select(a => a.Argument));
        Assert.All(displayArgs.Take(displayArgs.Count - 1), argument => Assert.Null(argument.EffectiveArgumentIndex));
        Assert.Equal(0, displayArgs[^1].EffectiveArgumentIndex);
        AssertEffectiveArgumentIndexesMatchSpecArgs(displayArgs, exe.Spec.Args);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DotnetToolResource_ProcessMode_LaunchToolArgsReplaceBuiltInInvocation(bool showInCommandLine)
    {
        var builder = DistributedApplication.CreateBuilder();

        builder.AddDotnetTool("tool", "package")
            .WithArgs("app-arg")
            .WithArgs(static context => context.Args.Insert(0, "prepended-arg"))
            .WithLaunchToolArgs(
                static context =>
                {
                    context.Args.Add("custom");
                    context.Args.Add("exec");
                    context.Args.Add("--");
                },
                showInCommandLine: showInCommandLine);

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);

        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "tool");
        Assert.Equal(ExecutionType.Process, exe.Spec.ExecutionType);
        Assert.Equal(["custom", "exec", "--", "prepended-arg", "app-arg"], exe.Spec.Args);

        Assert.True(exe.TryGetAnnotationAsObjectList<AppLaunchArgumentAnnotation>(CustomResource.ResourceAppArgsAnnotation, out var displayArgs));
        string[] expectedDisplayArgs = showInCommandLine
            ? new[] { "custom", "exec", "--", "prepended-arg", "app-arg" }
            : ["prepended-arg", "app-arg"];
        Assert.Equal(expectedDisplayArgs, displayArgs.Select(a => a.Argument));
        Assert.Equal(4, displayArgs[^1].EffectiveArgumentIndex);
        AssertEffectiveArgumentIndexesMatchSpecArgs(displayArgs, exe.Spec.Args);
    }

    [Fact]
    public async Task CreateExecutable_ProjectArgsResolvedInSnapshot_UsesEffectiveArgsFromCreatorIndexes()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        var project = builder.AddProject<Projects.ServiceA>("ServiceA", launchProfileName: null)
            .WithHttpEndpoint(targetPort: 8080);
        var endpoint = project.GetEndpoint("http");

        project.WithArgs("--port")
            .WithArgs(c => c.Args.Add(endpoint.Property(EndpointProperty.Port)));

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);
        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "ServiceA");
        Assert.True(exe.TryGetAnnotationAsObjectList<AppLaunchArgumentAnnotation>(CustomResource.ResourceAppArgsAnnotation, out var argAnnotations));
        Assert.Equal(2, argAnnotations.Count);
        AssertEffectiveArgumentIndexesMatchSpecArgs(argAnnotations, exe.Spec.Args);

        var effectiveArgs = exe.Spec.Args!.ToList();
        var portArgument = Assert.Single(argAnnotations, a => a.Argument != "--port");
        effectiveArgs[Assert.IsType<int>(portArgument.EffectiveArgumentIndex)] = "52731";
        exe.Status = new ExecutableStatus
        {
            EffectiveArgs = effectiveArgs
        };

        var snapshot = CreateSnapshotBuilder(distributedAppModel).ToSnapshot(exe, CreatePreviousSnapshot());

        Assert.Equal(["--port", "52731"], GetEnumerablePropertyValue<string>(snapshot, KnownProperties.Resource.AppArgs).ToArray());
        Assert.Equal(effectiveArgs, GetEnumerablePropertyValue<string>(snapshot, KnownProperties.Executable.Args).ToArray());
    }

    [Fact]
    public async Task CreateContainer_ArgsResolvedInSnapshot_UsesEffectiveArgsFromCreatorIndexes()
    {
        var builder = DistributedApplication.CreateBuilder();

        var executable = builder.AddExecutable("anExecutable", "command", "")
            .WithEndpoint(name: "http", targetPort: 1234, port: 5678, isProxied: true);

        builder.AddContainer("aContainer", "image")
            .WithArgs(c =>
            {
                c.Args.Add("--port");
                c.Args.Add(executable.GetEndpoint("http").Property(EndpointProperty.Port));
            });

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var dcpOptions = new DcpOptions
        {
            EnableAspireContainerTunnel = true,
        };

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, dcpOptions: dcpOptions);
        await appExecutor.RunApplicationAsync();

        var container = Assert.Single(kubernetesService.CreatedResources.OfType<Container>(), c => c.AppModelResourceName == "aContainer");
        Assert.True(container.TryGetAnnotationAsObjectList<AppLaunchArgumentAnnotation>(CustomResource.ResourceAppArgsAnnotation, out var argAnnotations));
        Assert.Equal(2, argAnnotations.Count);
        AssertEffectiveArgumentIndexesMatchSpecArgs(argAnnotations, container.Spec.Args);

        var effectiveArgs = container.Spec.Args!.ToList();
        var portArgument = Assert.Single(argAnnotations, a => a.Argument != "--port");
        effectiveArgs[Assert.IsType<int>(portArgument.EffectiveArgumentIndex)] = "5678";
        container.Status = new ContainerStatus
        {
            EffectiveArgs = effectiveArgs
        };

        var snapshot = CreateSnapshotBuilder(distributedAppModel).ToSnapshot(container, CreatePreviousSnapshot());

        Assert.Equal(["--port", "5678"], GetEnumerablePropertyValue<string>(snapshot, KnownProperties.Resource.AppArgs).ToArray());
        Assert.Equal(effectiveArgs, GetEnumerablePropertyValue<string>(snapshot, KnownProperties.Container.Args).ToArray());
    }

    [Theory]
    [InlineData("aspire")]
    [InlineData("ASPIRE")]
    public async Task RunApplicationAsync_ThrowsWhenContainerResourceNameConflictsWithContainerTunnelName(string containerName)
    {
        var builder = DistributedApplication.CreateBuilder();

        builder.AddContainer(containerName, "image");

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var appExecutor = CreateAppExecutor(
            distributedAppModel,
            kubernetesService: new TestKubernetesService(),
            dcpOptions: new DcpOptions { EnableAspireContainerTunnel = true });

        var ex = await Assert.ThrowsAsync<DistributedApplicationException>(() => appExecutor.RunApplicationAsync());
        Assert.Contains("container tunnel container name", ex.Message);
    }

    [Theory]
    [InlineData("aspire")]
    [InlineData("ASPIRE")]
    public async Task RunApplicationAsync_ThrowsWhenExplicitContainerNameConflictsWithContainerTunnelName(string containerName)
    {
        var builder = DistributedApplication.CreateBuilder();

        builder.AddContainer("aContainer", "image")
            .WithContainerName(containerName);

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var appExecutor = CreateAppExecutor(
            distributedAppModel,
            kubernetesService: new TestKubernetesService(),
            dcpOptions: new DcpOptions { EnableAspireContainerTunnel = true });

        var ex = await Assert.ThrowsAsync<DistributedApplicationException>(() => appExecutor.RunApplicationAsync());
        Assert.Contains("container tunnel container name", ex.Message);
    }

    [Theory]
    [InlineData("aspire")]
    [InlineData("ASPIRE")]
    public async Task RunApplicationAsync_ThrowsWhenNetworkAliasConflictsWithContainerTunnelName(string alias)
    {
        var builder = DistributedApplication.CreateBuilder();

        builder.AddContainer("aContainer", "image")
            .WithContainerNetworkAlias(alias);

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var appExecutor = CreateAppExecutor(
            distributedAppModel,
            kubernetesService: new TestKubernetesService(),
            dcpOptions: new DcpOptions { EnableAspireContainerTunnel = true });

        var ex = await Assert.ThrowsAsync<DistributedApplicationException>(() => appExecutor.RunApplicationAsync());
        Assert.Contains("container tunnel container name", ex.Message);
    }

    [Fact]
    public async Task RunApplicationAsync_AllowsContainerNameMatchingContainerTunnelNameWhenContainerTunnelDisabled()
    {
        var builder = DistributedApplication.CreateBuilder();

        builder.AddContainer("aspire", "image");
        builder.AddContainer("aContainer", "image")
            .WithContainerName("ASPIRE");
        builder.AddContainer("bContainer", "image")
            .WithContainerNetworkAlias("ASPIRE");

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var appExecutor = CreateAppExecutor(
            distributedAppModel,
            kubernetesService: kubernetesService,
            dcpOptions: new DcpOptions { EnableAspireContainerTunnel = false });

        await appExecutor.RunApplicationAsync();

        Assert.Equal(3, kubernetesService.CreatedResources.OfType<Container>().Count());
    }

    [Fact]
    public async Task ResourceRestarted_EnvironmentCallbacksApplied()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        var callCount = 0;
        var resource = builder.AddProject<Projects.ServiceA>("ServiceA")
            .WithArgs(c =>
            {
                c.Args.Add("--test");
            })
            .WithEnvironment(c =>
            {
                Interlocked.Increment(ref callCount);
                c.EnvironmentVariables["CALL_COUNT"] = callCount.ToString();
            }).Resource;

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var dcpOptions = new DcpOptions { DashboardPath = "./dashboard", ResourceNameSuffix = "suffix" };

        var events = new DcpExecutorEvents();
        var connectionStringAvailableCount = 0;
        events.Subscribe<OnConnectionStringAvailableContext>(context =>
        {
            if (ReferenceEquals(context.Resource, resource))
            {
                Interlocked.Increment(ref connectionStringAvailableCount);
            }

            return Task.CompletedTask;
        });
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, dcpOptions: dcpOptions, events: events);
        await appExecutor.RunApplicationAsync();

        var executables = GetCreatedExecutablesForResource(kubernetesService, resource.Name);

        var exe1 = Assert.Single(executables);
        var callCount1 = exe1.Spec.Env!.Single(e => e.Name == "CALL_COUNT");
        Assert.Equal("1", callCount1.Value);

        Assert.Single(exe1.Spec.Args!, a => a == "--no-build");
        Assert.Single(exe1.Spec.Args!, a => a == "--test");
        Assert.True(exe1.TryGetAnnotationAsObjectList<AppLaunchArgumentAnnotation>(CustomResource.ResourceAppArgsAnnotation, out var argAnnotations1));
        Assert.Single(argAnnotations1, a => a.Argument == "--test");
        AssertEffectiveArgumentIndexesMatchSpecArgs(argAnnotations1, exe1.Spec.Args);
        Assert.Equal(1, connectionStringAvailableCount);

        var reference = appExecutor.GetResource(exe1.Metadata.Name);

        await appExecutor.StopResourceAsync(reference, CancellationToken.None);

        await appExecutor.StartResourceAsync(reference, CancellationToken.None);

        executables = GetCreatedExecutablesForResource(kubernetesService, resource.Name);
        Assert.Equal(2, executables.Count);

        var exe2 = executables[1];
        var callCount2 = exe2.Spec.Env!.Single(e => e.Name == "CALL_COUNT");
        Assert.Equal("2", callCount2.Value);

        Assert.Single(exe2.Spec.Args!, a => a == "--no-build");
        Assert.Single(exe2.Spec.Args!, a => a == "--test");
        Assert.True(exe2.TryGetAnnotationAsObjectList<AppLaunchArgumentAnnotation>(CustomResource.ResourceAppArgsAnnotation, out var argAnnotations2));
        Assert.Single(argAnnotations2, a => a.Argument == "--test");
        AssertEffectiveArgumentIndexesMatchSpecArgs(argAnnotations2, exe2.Spec.Args);
        Assert.Equal(2, connectionStringAvailableCount);
    }

    [Fact]
    public async Task EndpointPortsExecutableNotReplicatedProxiedNoPortNoTargetPort()
    {
        var (allocatedTargetPort, _) = GetAvailableConsecutivePortPair();
        var builder = DistributedApplication.CreateBuilder();

        var exe = builder.AddExecutable("CoolProgram", "cool", Environment.CurrentDirectory, "--alpha", "--bravo")
            .WithEndpoint(name: "NoPortNoTargetPort", env: "NO_PORT_NO_TARGET_PORT", isProxied: true);

        var kubernetesService = new TestKubernetesService();
        var dcpOptions = new DcpOptions
        {
            DashboardPath = "./dashboard",
            ProxylessEndpointPortRangeStart = allocatedTargetPort,
            ProxylessEndpointPortRangeEnd = allocatedTargetPort
        };
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, dcpOptions: dcpOptions);
        await appExecutor.RunApplicationAsync();

        var dcpExe = Assert.Single(kubernetesService.CreatedResources.OfType<Executable>());
        Assert.True(dcpExe.TryGetAnnotationAsObjectList<ServiceProducerAnnotation>(CustomResource.ServiceProducerAnnotation, out var spAnnList));

        // Neither Port, nor TargetPort are set
        // Clients use proxy, MAY have the proxy port injected.
        // Proxy gets autogenerated port.
        // Aspire assigns the program a different non-ephemeral port that DCP injects via env var / startup param.
        var svc = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "CoolProgram");
        Assert.Equal(AddressAllocationModes.Localhost, svc.Spec.AddressAllocationMode);
        Assert.True(svc.Status?.EffectivePort >= TestKubernetesService.StartOfAutoPortRange);
        Assert.Equal(allocatedTargetPort, spAnnList.Single(ann => ann.ServiceName == "CoolProgram").Port);
        var envVarVal = dcpExe.Spec.Env?.Single(v => v.Name == "NO_PORT_NO_TARGET_PORT").Value;
        Assert.False(string.IsNullOrWhiteSpace(envVarVal));
        Assert.Contains("""portForServing "CoolProgram" """, envVarVal);
    }

    [Fact]
    public async Task EndpointPortsExecutableNotReplicatedProxiedPortSetNoTargetPort()
    {
        var (allocatedTargetPort, _) = GetAvailableConsecutivePortPair();
        var builder = DistributedApplication.CreateBuilder();

        const int desiredPort = TestKubernetesService.StartOfAutoPortRange - 1000;
        var exe = builder.AddExecutable("CoolProgram", "cool", Environment.CurrentDirectory, "--alpha", "--bravo")
            .WithEndpoint(name: "PortSetNoTargetPort", port: desiredPort, env: "PORT_SET_NO_TARGET_PORT");

        var kubernetesService = new TestKubernetesService();
        var dcpOptions = new DcpOptions
        {
            DashboardPath = "./dashboard",
            ProxylessEndpointPortRangeStart = allocatedTargetPort,
            ProxylessEndpointPortRangeEnd = allocatedTargetPort
        };
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, dcpOptions: dcpOptions);
        await appExecutor.RunApplicationAsync();

        var dcpExe = Assert.Single(kubernetesService.CreatedResources.OfType<Executable>());
        Assert.True(dcpExe.TryGetAnnotationAsObjectList<ServiceProducerAnnotation>(CustomResource.ServiceProducerAnnotation, out var spAnnList));

        // Port is set, but TargetPort is empty
        // Clients use proxy, MAY have the proxy port injected.
        // Proxy uses Port.
        // Aspire assigns the program a non-ephemeral port that DCP injects via env var / startup param.
        var svc = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "CoolProgram");
        Assert.Equal(AddressAllocationModes.Localhost, svc.Spec.AddressAllocationMode);
        Assert.Equal(desiredPort, svc.Status?.EffectivePort);
        Assert.Equal(allocatedTargetPort, spAnnList.Single(ann => ann.ServiceName == "CoolProgram").Port);
        var envVarVal = dcpExe.Spec.Env?.Single(v => v.Name == "PORT_SET_NO_TARGET_PORT").Value;
        Assert.False(string.IsNullOrWhiteSpace(envVarVal));
        Assert.Contains("""portForServing "CoolProgram" """, envVarVal);
    }

    [Fact]
    public async Task EndpointPortsExecutableNotReplicatedProxiedNoPortTargetPortSet()
    {
        var builder = DistributedApplication.CreateBuilder();

        const int desiredPort = TestKubernetesService.StartOfAutoPortRange - 999;
        var exe = builder.AddExecutable("CoolProgram", "cool", Environment.CurrentDirectory, "--alpha", "--bravo")
            .WithEndpoint(name: "NoPortTargetPortSet", targetPort: desiredPort, env: "NO_PORT_TARGET_PORT_SET", isProxied: true);

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);
        await appExecutor.RunApplicationAsync();

        var dcpExe = Assert.Single(kubernetesService.CreatedResources.OfType<Executable>());
        Assert.True(dcpExe.TryGetAnnotationAsObjectList<ServiceProducerAnnotation>(CustomResource.ServiceProducerAnnotation, out var spAnnList));

        // Port is empty, TargetPort is set
        // Clients use proxy, MAY have the proxy port injected.
        // Proxy gets autogenerated port.
        // Program uses TargetPort which MAY be injected via env var/ startup param.
        var svc = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "CoolProgram");
        Assert.Equal(AddressAllocationModes.Localhost, svc.Spec.AddressAllocationMode);
        Assert.True(svc.Status?.EffectivePort >= TestKubernetesService.StartOfAutoPortRange);
        // Desired port should be part of the service producer annotation.
        Assert.Equal(desiredPort, spAnnList.Single(ann => ann.ServiceName == "CoolProgram").Port);
        var envVarVal = dcpExe.Spec.Env?.Single(v => v.Name == "NO_PORT_TARGET_PORT_SET").Value;
        Assert.False(string.IsNullOrWhiteSpace(envVarVal));
        Assert.Equal(desiredPort, int.Parse(envVarVal, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task DynamicProxiedExecutableTargetPortExcludesFixedTargetPorts()
    {
        var (fixedTargetPort, allocatedTargetPort) = GetAvailableConsecutivePortPair();
        var builder = DistributedApplication.CreateBuilder();

        builder.AddExecutable("FixedProgram", "fixed", Environment.CurrentDirectory)
            .WithEndpoint(name: "fixed", targetPort: fixedTargetPort, isProxied: true);
        builder.AddExecutable("DynamicProgram", "dynamic", Environment.CurrentDirectory)
            .WithEndpoint(name: "dynamic", isProxied: true);

        var dcpOptions = new DcpOptions
        {
            DashboardPath = "./dashboard",
            ProxylessEndpointPortRangeStart = fixedTargetPort,
            ProxylessEndpointPortRangeEnd = allocatedTargetPort
        };
        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, dcpOptions: dcpOptions);

        await appExecutor.RunApplicationAsync();

        var fixedExecutable = kubernetesService.CreatedResources.OfType<Executable>().Single(e => e.AppModelResourceName == "FixedProgram");
        var dynamicExecutable = kubernetesService.CreatedResources.OfType<Executable>().Single(e => e.AppModelResourceName == "DynamicProgram");
        Assert.True(fixedExecutable.TryGetAnnotationAsObjectList<ServiceProducerAnnotation>(CustomResource.ServiceProducerAnnotation, out var fixedAnnotations));
        Assert.True(dynamicExecutable.TryGetAnnotationAsObjectList<ServiceProducerAnnotation>(CustomResource.ServiceProducerAnnotation, out var dynamicAnnotations));
        Assert.Equal(fixedTargetPort, Assert.Single(fixedAnnotations).Port);
        Assert.Equal(allocatedTargetPort, Assert.Single(dynamicAnnotations).Port);
    }

    [Fact]
    public async Task EndpointPortsExecutableNotReplicatedProxiedPortAndTargetPortSet()
    {
        var builder = DistributedApplication.CreateBuilder();

        const int desiredPort = TestKubernetesService.StartOfAutoPortRange - 998;
        const int desiredTargetPort = TestKubernetesService.StartOfAutoPortRange - 997;
        var exe = builder.AddExecutable("CoolProgram", "cool", Environment.CurrentDirectory, "--alpha", "--bravo")
            .WithEndpoint(name: "PortAndTargetPortSet", port: desiredPort, targetPort: desiredTargetPort, env: "PORT_AND_TARGET_PORT_SET", isProxied: true);

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);
        await appExecutor.RunApplicationAsync();

        var dcpExe = Assert.Single(kubernetesService.CreatedResources.OfType<Executable>());
        Assert.True(dcpExe.TryGetAnnotationAsObjectList<ServiceProducerAnnotation>(CustomResource.ServiceProducerAnnotation, out var spAnnList));

        // Port and TargetPort set (MUST be different).
        // Clients use proxy, MAY have the proxy port injected.
        // Proxy uses Port.
        // Program uses TargetPort with MAY be injected via env var/ startup param.
        var svc = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "CoolProgram");
        Assert.Equal(AddressAllocationModes.Localhost, svc.Spec.AddressAllocationMode);
        Assert.Equal(desiredPort, svc.Status?.EffectivePort);
        // Desired port should be part of the service producer annotation.
        Assert.Equal(desiredTargetPort, spAnnList.Single(ann => ann.ServiceName == "CoolProgram").Port);
        var envVarVal = dcpExe.Spec.Env?.Single(v => v.Name == "PORT_AND_TARGET_PORT_SET").Value;
        Assert.False(string.IsNullOrWhiteSpace(envVarVal));
        Assert.Equal(desiredTargetPort, int.Parse(envVarVal, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Verifies that applying unsupported endpoint port configuration to non-replicated, proxied Executable
    /// results in an error.
    /// </summary>
    [Fact]
    public async Task UnsupportedEndpointPortsExecutableNotReplicatedProxied()
    {
        // Invalid configuration: Port and TargetPort have the same value. This would result in a port conflict.
        var builder = DistributedApplication.CreateBuilder();

        const int desiredPort = TestKubernetesService.StartOfAutoPortRange - 1000;
        builder.AddExecutable("CoolProgram", "cool", Environment.CurrentDirectory, "--alpha", "--bravo")
            .WithEndpoint(name: "EqualPortAndTargetPort", port: desiredPort, targetPort: desiredPort, env: "EQUAL_PORT_AND_TARGET_PORT", isProxied: true);

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => appExecutor.RunApplicationAsync());
        Assert.Contains("cannot be proxied when both TargetPort and Port are specified with the same value", exception.Message);
    }

    [Fact]
    public async Task EndpointPortsExecutableWithEndpointProxySupportUsesProxylessEndpoint()
    {
        var builder = DistributedApplication.CreateBuilder();

        const int desiredPort = TestKubernetesService.StartOfAutoPortRange - 1001;
        builder.AddExecutable("CoolProgram", "cool", Environment.CurrentDirectory, "--alpha", "--bravo")
            .WithEndpoint(name: "PortSetNoTargetPort", port: desiredPort, env: "PORT_SET_NO_TARGET_PORT")
            .WithEndpointProxySupport(false);

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);
        await appExecutor.RunApplicationAsync();

        var dcpExe = Assert.Single(kubernetesService.CreatedResources.OfType<Executable>());
        Assert.True(dcpExe.TryGetAnnotationAsObjectList<ServiceProducerAnnotation>(CustomResource.ServiceProducerAnnotation, out var spAnnList));

        var svc = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "CoolProgram");
        Assert.Equal(AddressAllocationModes.Proxyless, svc.Spec.AddressAllocationMode);
        Assert.Equal(desiredPort, svc.Status?.EffectivePort);
        Assert.Equal(desiredPort, spAnnList.Single(ann => ann.ServiceName == "CoolProgram").Port);

        var envVarVal = dcpExe.Spec.Env?.Single(v => v.Name == "PORT_SET_NO_TARGET_PORT").Value;
        Assert.False(string.IsNullOrWhiteSpace(envVarVal));
        Assert.Equal(desiredPort, int.Parse(envVarVal, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task EndpointPortsExecutableWithEndpointProxySupportOverridesExplicitProxiedEndpoint()
    {
        var builder = DistributedApplication.CreateBuilder();

        const int desiredPort = TestKubernetesService.StartOfAutoPortRange - 1001;
        builder.AddExecutable("CoolProgram", "cool", Environment.CurrentDirectory, "--alpha", "--bravo")
            .WithEndpoint(name: "EqualPortAndTargetPort", port: desiredPort, targetPort: desiredPort, env: "EQUAL_PORT_AND_TARGET_PORT", isProxied: true)
            .WithEndpointProxySupport(false);

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);
        await appExecutor.RunApplicationAsync();

        var dcpExe = Assert.Single(kubernetesService.CreatedResources.OfType<Executable>());
        Assert.True(dcpExe.TryGetAnnotationAsObjectList<ServiceProducerAnnotation>(CustomResource.ServiceProducerAnnotation, out var spAnnList));

        var svc = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "CoolProgram");
        Assert.Equal(AddressAllocationModes.Proxyless, svc.Spec.AddressAllocationMode);
        Assert.Equal(desiredPort, svc.Status?.EffectivePort);
        Assert.Equal(desiredPort, spAnnList.Single(ann => ann.ServiceName == "CoolProgram").Port);

        var envVarVal = dcpExe.Spec.Env?.Single(v => v.Name == "EQUAL_PORT_AND_TARGET_PORT").Value;
        Assert.False(string.IsNullOrWhiteSpace(envVarVal));
        Assert.Equal(desiredPort, int.Parse(envVarVal, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task EndpointPortsPersistentExecutableDefaultsToProxylessEndpoint()
    {
        var builder = DistributedApplication.CreateBuilder();

        const int desiredPort = TestKubernetesService.StartOfAutoPortRange - 1002;
        builder.AddExecutable("CoolProgram", "cool", Environment.CurrentDirectory, "--alpha", "--bravo")
            .WithPersistentLifetime()
            .WithEndpoint(name: "PortSetNoTargetPort", port: desiredPort, env: "PORT_SET_NO_TARGET_PORT");

        var configDict = new Dictionary<string, string?>
        {
            ["AppHost:Sha256"] = "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);
        await appExecutor.RunApplicationAsync();

        var dcpExe = Assert.Single(kubernetesService.CreatedResources.OfType<Executable>());
        Assert.True(dcpExe.TryGetAnnotationAsObjectList<ServiceProducerAnnotation>(CustomResource.ServiceProducerAnnotation, out var spAnnList));

        var svc = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "CoolProgram");
        Assert.Equal(AddressAllocationModes.Proxyless, svc.Spec.AddressAllocationMode);
        Assert.Equal(desiredPort, svc.Status?.EffectivePort);
        Assert.Equal(desiredPort, spAnnList.Single(ann => ann.ServiceName == "CoolProgram").Port);

        var envVarVal = dcpExe.Spec.Env?.Single(v => v.Name == "PORT_SET_NO_TARGET_PORT").Value;
        Assert.False(string.IsNullOrWhiteSpace(envVarVal));
        Assert.Equal(desiredPort, int.Parse(envVarVal, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task EndpointPortsPersistentExecutableDefaultsToProxiedEndpointWhenPortsAreRandomized()
    {
        var (allocatedTargetPort, _) = GetAvailableConsecutivePortPair();
        var builder = DistributedApplication.CreateBuilder();

        const int desiredPort = TestKubernetesService.StartOfAutoPortRange - 1002;
        builder.AddExecutable("CoolProgram", "cool", Environment.CurrentDirectory, "--alpha", "--bravo")
            .WithPersistentLifetime()
            .WithEndpoint(name: "PortSetNoTargetPort", port: desiredPort, env: "PORT_SET_NO_TARGET_PORT");

        var configDict = new Dictionary<string, string?>
        {
            ["AppHost:Sha256"] = "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var dcpOptions = new DcpOptions
        {
            DashboardPath = "./dashboard",
            RandomizePorts = true,
            ProxylessEndpointPortRangeStart = allocatedTargetPort,
            ProxylessEndpointPortRangeEnd = allocatedTargetPort
        };
        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, dcpOptions: dcpOptions, configuration: configuration);
        await appExecutor.RunApplicationAsync();

        var dcpExe = Assert.Single(kubernetesService.CreatedResources.OfType<Executable>());
        Assert.True(dcpExe.TryGetAnnotationAsObjectList<ServiceProducerAnnotation>(CustomResource.ServiceProducerAnnotation, out var spAnnList));

        var svc = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "CoolProgram");
        Assert.Equal(AddressAllocationModes.Localhost, svc.Spec.AddressAllocationMode);
        Assert.Null(svc.Spec.Port);
        Assert.True(svc.Status?.EffectivePort >= TestKubernetesService.StartOfAutoPortRange);
        Assert.NotEqual(desiredPort, svc.Status?.EffectivePort);
        Assert.Equal(allocatedTargetPort, spAnnList.Single(ann => ann.ServiceName == "CoolProgram").Port);

        var envVarVal = dcpExe.Spec.Env?.Single(v => v.Name == "PORT_SET_NO_TARGET_PORT").Value;
        Assert.False(string.IsNullOrWhiteSpace(envVarVal));
        Assert.Contains("""portForServing "CoolProgram" """, envVarVal);
    }

    [Fact]
    public async Task EndpointPortsPersistentExecutableExplicitProxylessStaysProxylessWhenPortsAreRandomized()
    {
        var builder = DistributedApplication.CreateBuilder();

        const int desiredPort = TestKubernetesService.StartOfAutoPortRange - 1002;
        builder.AddExecutable("CoolProgram", "cool", Environment.CurrentDirectory, "--alpha", "--bravo")
            .WithPersistentLifetime()
            .WithEndpoint(name: "PortSetNoTargetPort", port: desiredPort, env: "PORT_SET_NO_TARGET_PORT", isProxied: false);

        var configDict = new Dictionary<string, string?>
        {
            ["AppHost:Sha256"] = "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var dcpOptions = new DcpOptions { DashboardPath = "./dashboard", RandomizePorts = true };
        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, dcpOptions: dcpOptions, configuration: configuration);
        await appExecutor.RunApplicationAsync();

        var dcpExe = Assert.Single(kubernetesService.CreatedResources.OfType<Executable>());
        Assert.True(dcpExe.TryGetAnnotationAsObjectList<ServiceProducerAnnotation>(CustomResource.ServiceProducerAnnotation, out var spAnnList));

        var svc = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "CoolProgram");
        Assert.Equal(AddressAllocationModes.Proxyless, svc.Spec.AddressAllocationMode);
        Assert.Equal(desiredPort, svc.Status?.EffectivePort);
        Assert.Equal(desiredPort, spAnnList.Single(ann => ann.ServiceName == "CoolProgram").Port);

        var envVarVal = dcpExe.Spec.Env?.Single(v => v.Name == "PORT_SET_NO_TARGET_PORT").Value;
        Assert.False(string.IsNullOrWhiteSpace(envVarVal));
        Assert.Equal(desiredPort, int.Parse(envVarVal, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task EndpointPortsExecutableNotReplicatedProxylessPortSetNoTargetPort()
    {
        var builder = DistributedApplication.CreateBuilder();

        const int desiredPort = TestKubernetesService.StartOfAutoPortRange - 1000;
        builder.AddExecutable("CoolProgram", "cool", Environment.CurrentDirectory, "--alpha", "--bravo")
            .WithEndpoint(name: "PortSetNoTargetPort", port: desiredPort, env: "PORT_SET_NO_TARGET_PORT", isProxied: false);

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);
        await appExecutor.RunApplicationAsync();

        var dcpExe = Assert.Single(kubernetesService.CreatedResources.OfType<Executable>());
        Assert.True(dcpExe.TryGetAnnotationAsObjectList<ServiceProducerAnnotation>(CustomResource.ServiceProducerAnnotation, out var spAnnList));

        // Port is set, but TargetPort is empty
        // Clients connect directly to the program, MAY have the program port injected.
        // Program uses TargetPort, which MAY be injected via env var / startup param.
        var svc = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "CoolProgram");
        Assert.Equal(AddressAllocationModes.Proxyless, svc.Spec.AddressAllocationMode);
        Assert.Equal(desiredPort, svc.Status?.EffectivePort);
        // Desired port should be part of the service producer annotation.
        Assert.Equal(desiredPort, spAnnList.Single(ann => ann.ServiceName == "CoolProgram").Port);
        var envVarVal = dcpExe.Spec.Env?.Single(v => v.Name == "PORT_SET_NO_TARGET_PORT").Value;
        Assert.False(string.IsNullOrWhiteSpace(envVarVal));
        Assert.Equal(desiredPort, int.Parse(envVarVal, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task EndpointPortsExecutableNotReplicatedProxylessNoPortTargetPortSet()
    {
        var builder = DistributedApplication.CreateBuilder();

        const int desiredPort = TestKubernetesService.StartOfAutoPortRange - 999;
        builder.AddExecutable("CoolProgram", "cool", Environment.CurrentDirectory, "--alpha", "--bravo")
            .WithEndpoint(name: "NoPortTargetPortSet", targetPort: desiredPort, env: "NO_PORT_TARGET_PORT_SET", isProxied: false);

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);
        await appExecutor.RunApplicationAsync();

        var dcpExe = Assert.Single(kubernetesService.CreatedResources.OfType<Executable>());
        Assert.True(dcpExe.TryGetAnnotationAsObjectList<ServiceProducerAnnotation>(CustomResource.ServiceProducerAnnotation, out var spAnnList));

        // Port is empty, TargetPort is set.
        // Clients connect directly to the program, MAY have the program port injected.
        // Program uses TargetPort, which MAY be injected via env var / startup param.
        var svc = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "CoolProgram");
        Assert.Equal(AddressAllocationModes.Proxyless, svc.Spec.AddressAllocationMode);
        Assert.Equal(desiredPort, svc.Status?.EffectivePort);
        Assert.Equal(desiredPort, svc.Spec.Port);
        // Desired port should be part of the service producer annotation.
        Assert.Equal(desiredPort, spAnnList.Single(ann => ann.ServiceName == "CoolProgram").Port);
        var envVarVal = dcpExe.Spec.Env?.Single(v => v.Name == "NO_PORT_TARGET_PORT_SET").Value;
        Assert.False(string.IsNullOrWhiteSpace(envVarVal));
        Assert.Equal(desiredPort, int.Parse(envVarVal, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task EndpointPortsExecutableNotReplicatedProxylessPortAndTargetPortSet()
    {
        var builder = DistributedApplication.CreateBuilder();

        const int desiredPort = TestKubernetesService.StartOfAutoPortRange - 998;
        builder.AddExecutable("CoolProgram", "cool", Environment.CurrentDirectory, "--alpha", "--bravo")
            .WithEndpoint(name: "PortAndTargetPortSet", port: desiredPort, targetPort: desiredPort, env: "PORT_AND_TARGET_PORT_SET", isProxied: false);

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);
        await appExecutor.RunApplicationAsync();

        var dcpExe = Assert.Single(kubernetesService.CreatedResources.OfType<Executable>());
        Assert.True(dcpExe.TryGetAnnotationAsObjectList<ServiceProducerAnnotation>(CustomResource.ServiceProducerAnnotation, out var spAnnList));

        // Port and target port set (MUST be the same).
        // Clients connect directly to the program, MAY have the program port injected.
        // Program uses TargetPort, which MAY be injected via env var / startup param.
        var svc = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "CoolProgram");
        Assert.Equal(AddressAllocationModes.Proxyless, svc.Spec.AddressAllocationMode);
        Assert.Equal(desiredPort, svc.Status?.EffectivePort);
        // Desired port should be part of the service producer annotation.
        Assert.Equal(desiredPort, spAnnList.Single(ann => ann.ServiceName == "CoolProgram").Port);
        var envVarVal = dcpExe.Spec.Env?.Single(v => v.Name == "PORT_AND_TARGET_PORT_SET").Value;
        Assert.False(string.IsNullOrWhiteSpace(envVarVal));
        Assert.Equal(desiredPort, int.Parse(envVarVal, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task EndpointPortsExecutableNotReplicatedProxylessNoPortNoTargetPortAllocated()
    {
        var builder = DistributedApplication.CreateBuilder();

        builder.AddExecutable("CoolProgram", "cool", Environment.CurrentDirectory, "--alpha", "--bravo")
            .WithEndpoint(name: "NoPortNoTargetPort", env: "NO_PORT_NO_TARGET_PORT", isProxied: false);

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);
        await appExecutor.RunApplicationAsync();

        var dcpExe = Assert.Single(kubernetesService.CreatedResources.OfType<Executable>());
        Assert.True(dcpExe.TryGetAnnotationAsObjectList<ServiceProducerAnnotation>(CustomResource.ServiceProducerAnnotation, out var spAnnList));

        var svc = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "CoolProgram");
        var allocatedPort = Assert.IsType<int>(svc.Status?.EffectivePort);
        Assert.Equal(AddressAllocationModes.Proxyless, svc.Spec.AddressAllocationMode);
        AssertPortAllocatedFromProxylessEndpointAllocatorRange(allocatedPort);
        Assert.Equal(allocatedPort, svc.Spec.Port);
        Assert.Equal(allocatedPort, spAnnList.Single(ann => ann.ServiceName == "CoolProgram").Port);

        var envVarVal = dcpExe.Spec.Env?.Single(v => v.Name == "NO_PORT_NO_TARGET_PORT").Value;
        Assert.False(string.IsNullOrWhiteSpace(envVarVal));
        Assert.Equal(allocatedPort, int.Parse(envVarVal, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task ProxylessPortAllocatorOnlyAllocatesPortsForDcpWorkloads()
    {
        var (rangeStart, rangeEnd) = GetAvailableConsecutivePortPair();
        var builder = DistributedApplication.CreateBuilder();

        var compute = builder.AddExecutable("compute", "compute", Environment.CurrentDirectory)
            .WithEndpoint(name: "tcp", isProxied: false);
        var target = builder.AddExecutable("target", "target", Environment.CurrentDirectory)
            .WithHttpEndpoint(targetPort: 8000, name: "http");
        builder.AddDevTunnel("tunnel")
            .WithReference(target);

        var dcpOptions = new DcpOptions
        {
            DashboardPath = "./dashboard",
            ProxylessEndpointPortRangeStart = rangeStart,
            ProxylessEndpointPortRangeEnd = rangeEnd
        };
        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var tunnelPort = Assert.Single(distributedAppModel.Resources.OfType<DevTunnelPortResource>());
        var computeEndpoint = compute.GetEndpoint("tcp").EndpointAnnotation;
        var tunnelEndpoint = Assert.Single(tunnelPort.Annotations.OfType<EndpointAnnotation>());
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, dcpOptions: dcpOptions);

        await appExecutor.RunApplicationAsync();

        Assert.NotNull(computeEndpoint.AllocatedEndpoint);
        var computePort = Assert.IsType<int>(computeEndpoint.Port);
        Assert.InRange(computePort, rangeStart, rangeEnd);
        Assert.Equal(computePort, computeEndpoint.TargetPort);
        Assert.Null(tunnelEndpoint.Port);
        Assert.Null(tunnelEndpoint.TargetPort);
        Assert.Null(tunnelEndpoint.AllocatedEndpoint);
    }

    [Fact]
    public async Task ProxylessPortAllocatorAllocatesPortForNonComputeContainerResource()
    {
        const int targetPort = 10000;
        var (allocatedPort, _) = GetAvailableConsecutivePortPair();
        var builder = DistributedApplication.CreateBuilder();

        var emulator = builder.AddResource(new TestContainerResource("emulator"))
            .WithAnnotation(new ContainerImageAnnotation { Image = "image" })
            .WithAnnotation(new ContainerLifetimeAnnotation { Lifetime = ContainerLifetime.Persistent })
            .WithHttpEndpoint(targetPort: targetPort, name: "http");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppHost:Sha256"] = "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef"
            })
            .Build();
        var dcpOptions = new DcpOptions
        {
            DashboardPath = "./dashboard",
            ProxylessEndpointPortRangeStart = allocatedPort,
            ProxylessEndpointPortRangeEnd = allocatedPort
        };
        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var endpoint = emulator.GetEndpoint("http").EndpointAnnotation;
        var appExecutor = CreateAppExecutor(
            distributedAppModel,
            configuration: configuration,
            kubernetesService: kubernetesService,
            dcpOptions: dcpOptions);

        await appExecutor.RunApplicationAsync();

        Assert.IsNotAssignableFrom<IComputeResource>(emulator.Resource);
        Assert.True(emulator.Resource.IsContainer());
        Assert.Equal(allocatedPort, endpoint.Port);
        Assert.Equal(targetPort, endpoint.TargetPort);
        Assert.Equal(allocatedPort, endpoint.AllocatedEndpoint?.Port);
        Assert.Single(kubernetesService.CreatedResources.OfType<Container>(), c => c.AppModelResourceName == emulator.Resource.Name);
    }

    [Fact]
    public async Task ProxylessPortAllocatorExcludesFixedPublicPorts()
    {
        var (fixedPort, allocatedPort) = GetAvailableConsecutivePortPair();
        var builder = DistributedApplication.CreateBuilder();

        builder.AddExecutable("FixedProgram", "fixed", Environment.CurrentDirectory)
            .WithEndpoint(name: "fixed", port: fixedPort, isProxied: false);
        builder.AddExecutable("DynamicProgram", "dynamic", Environment.CurrentDirectory)
            .WithEndpoint(name: "dynamic", env: "DYNAMIC_PORT", isProxied: false);

        var dcpOptions = new DcpOptions
        {
            DashboardPath = "./dashboard",
            ProxylessEndpointPortRangeStart = fixedPort,
            ProxylessEndpointPortRangeEnd = allocatedPort
        };
        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, dcpOptions: dcpOptions);
        await appExecutor.RunApplicationAsync();

        var fixedService = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "FixedProgram");
        var dynamicService = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "DynamicProgram");
        var dynamicExecutable = kubernetesService.CreatedResources.OfType<Executable>().Single(e => e.AppModelResourceName == "DynamicProgram");

        Assert.Equal(fixedPort, fixedService.Status?.EffectivePort);
        Assert.Equal(allocatedPort, dynamicService.Status?.EffectivePort);
        Assert.Equal(allocatedPort, dynamicService.Spec.Port);

        var envVarVal = dynamicExecutable.Spec.Env?.Single(v => v.Name == "DYNAMIC_PORT").Value;
        Assert.False(string.IsNullOrWhiteSpace(envVarVal));
        Assert.Equal(allocatedPort, int.Parse(envVarVal, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public async Task PersistentProxylessExecutableWithUnspecifiedPortPersistsAllocatedPort(int? port)
    {
        var (allocatedPort, _) = GetAvailableConsecutivePortPair();
        var builder = DistributedApplication.CreateBuilder();

        builder.AddExecutable("CoolProgram", "cool", Environment.CurrentDirectory, "--alpha", "--bravo")
            .WithPersistentLifetime()
            .WithEndpoint(name: "http", port: port, env: "HTTP_PORT");

        var configDict = new Dictionary<string, string?>
        {
            ["AppHost:Sha256"] = "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();
        var userSecretsManager = new MockUserSecretsManager();
        var dcpOptions = new DcpOptions
        {
            DashboardPath = "./dashboard",
            ProxylessEndpointPortRangeStart = allocatedPort,
            ProxylessEndpointPortRangeEnd = allocatedPort
        };

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration, dcpOptions: dcpOptions, userSecretsManager: userSecretsManager);
        await appExecutor.RunApplicationAsync();

        var svc = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "CoolProgram");
        Assert.Equal(AddressAllocationModes.Proxyless, svc.Spec.AddressAllocationMode);
        Assert.Equal(allocatedPort, svc.Status?.EffectivePort);
        Assert.Equal(allocatedPort.ToString(CultureInfo.InvariantCulture), userSecretsManager.Secrets["Resources:CoolProgram:http:port"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public async Task PersistentProxylessContainerWithUnspecifiedPortPersistsAllocatedPort(int? port)
    {
        var (allocatedPort, _) = GetAvailableConsecutivePortPair();
        var builder = DistributedApplication.CreateBuilder();

        const int targetPort = TestKubernetesService.StartOfAutoPortRange - 999;
        builder.AddContainer("database", "image")
            .WithPersistentLifetime()
            .WithEndpoint(name: "http", port: port, targetPort: targetPort);

        var configDict = new Dictionary<string, string?>
        {
            ["AppHost:Sha256"] = "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();
        var userSecretsManager = new MockUserSecretsManager();
        var dcpOptions = new DcpOptions
        {
            DashboardPath = "./dashboard",
            ProxylessEndpointPortRangeStart = allocatedPort,
            ProxylessEndpointPortRangeEnd = allocatedPort
        };

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration, dcpOptions: dcpOptions, userSecretsManager: userSecretsManager);
        await appExecutor.RunApplicationAsync();

        var dcpCtr = Assert.Single(kubernetesService.CreatedResources.OfType<Container>());
        var svc = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "database");
        Assert.Equal(AddressAllocationModes.Proxyless, svc.Spec.AddressAllocationMode);
        Assert.Equal(allocatedPort, svc.Status?.EffectivePort);
        Assert.Equal(allocatedPort, svc.Spec.Port);
        Assert.Contains(dcpCtr.Spec.Ports!, p => p.HostPort == allocatedPort && p.ContainerPort == targetPort);
        Assert.Equal(allocatedPort.ToString(CultureInfo.InvariantCulture), userSecretsManager.Secrets["Resources:database:http:port"]);
    }

    [Fact]
    public async Task PersistentProxylessWithoutPortLogsWarningButStillAllocatesWhenPersistenceFails()
    {
        var (allocatedPort, _) = GetAvailableConsecutivePortPair();
        var builder = DistributedApplication.CreateBuilder();

        builder.AddExecutable("CoolProgram", "cool", Environment.CurrentDirectory, "--alpha", "--bravo")
            .WithPersistentLifetime()
            .WithEndpoint(name: "http", env: "HTTP_PORT", isProxied: false);

        var configDict = new Dictionary<string, string?>
        {
            ["AppHost:Sha256"] = "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();
        var userSecretsManager = new MockUserSecretsManager(canSetSecret: false);
        var dcpOptions = new DcpOptions
        {
            DashboardPath = "./dashboard",
            ProxylessEndpointPortRangeStart = allocatedPort,
            ProxylessEndpointPortRangeEnd = allocatedPort
        };

        var testSink = new TestSink();
        var logger = new TestLogger<DcpExecutor>(new TestLoggerFactory(testSink, enabled: true));

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration, dcpOptions: dcpOptions, userSecretsManager: userSecretsManager, logger: logger);
        await appExecutor.RunApplicationAsync();

        // Persistence failed, but allocation must still proceed and assign the port to the endpoint.
        var svc = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "CoolProgram");
        Assert.Equal(allocatedPort, svc.Status?.EffectivePort);
        Assert.Empty(userSecretsManager.Secrets);

        // A warning must surface so the user knows the port won't be stable across runs.
        Assert.Contains(testSink.Writes, w => w.LogLevel == LogLevel.Warning
            && w.Message?.Contains("Failed to persist public port") == true
            && w.Message?.Contains("CoolProgram") == true
            && w.Message?.Contains("http") == true);
    }

    [Fact]
    public async Task ProxylessExecutableAllocatedPortIsStableOnResourceRestart()
    {
        var (allocatedPort, _) = GetAvailableConsecutivePortPair();
        var builder = DistributedApplication.CreateBuilder();

        builder.AddExecutable("CoolProgram", "cool", Environment.CurrentDirectory, "--alpha", "--bravo")
            .WithEndpoint(name: "http", env: "HTTP_PORT", isProxied: false);

        var dcpOptions = new DcpOptions
        {
            DashboardPath = "./dashboard",
            ProxylessEndpointPortRangeStart = allocatedPort,
            ProxylessEndpointPortRangeEnd = allocatedPort
        };

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, dcpOptions: dcpOptions);
        await appExecutor.RunApplicationAsync();

        var service = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "CoolProgram");
        var firstExecutable = Assert.Single(GetCreatedExecutablesForResource(kubernetesService, "CoolProgram"));
        Assert.Equal(allocatedPort, service.Status?.EffectivePort);
        Assert.Equal(allocatedPort.ToString(CultureInfo.InvariantCulture), firstExecutable.Spec.Env?.Single(v => v.Name == "HTTP_PORT").Value);

        var reference = appExecutor.GetResource(firstExecutable.Metadata.Name);
        await appExecutor.StopResourceAsync(reference, CancellationToken.None);
        await appExecutor.StartResourceAsync(reference, CancellationToken.None);

        var executables = GetCreatedExecutablesForResource(kubernetesService, "CoolProgram");
        Assert.Equal(2, executables.Count);
        Assert.Equal(allocatedPort, service.Status?.EffectivePort);
        Assert.Equal(allocatedPort.ToString(CultureInfo.InvariantCulture), executables[1].Spec.Env?.Single(v => v.Name == "HTTP_PORT").Value);
    }

    [Fact]
    public async Task ProxylessContainerAllocatedHostPortIsStableOnResourceRestart()
    {
        var (allocatedPort, _) = GetAvailableConsecutivePortPair();
        var builder = DistributedApplication.CreateBuilder();

        const int targetPort = TestKubernetesService.StartOfAutoPortRange - 999;
        builder.AddContainer("database", "image")
            .WithEndpoint(name: "http", targetPort: targetPort, isProxied: false);

        var dcpOptions = new DcpOptions
        {
            DashboardPath = "./dashboard",
            ProxylessEndpointPortRangeStart = allocatedPort,
            ProxylessEndpointPortRangeEnd = allocatedPort
        };

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, dcpOptions: dcpOptions);
        await appExecutor.RunApplicationAsync();

        var service = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "database");
        var firstContainer = Assert.Single(GetCreatedContainersForResource(kubernetesService, "database"));
        Assert.Equal(allocatedPort, service.Status?.EffectivePort);
        Assert.Contains(firstContainer.Spec.Ports!, p => p.HostPort == allocatedPort && p.ContainerPort == targetPort);

        var reference = appExecutor.GetResource(firstContainer.Metadata.Name);
        await appExecutor.StopResourceAsync(reference, CancellationToken.None);
        await appExecutor.StartResourceAsync(reference, CancellationToken.None);

        var containers = GetCreatedContainersForResource(kubernetesService, "database");
        Assert.Equal(2, containers.Count);
        Assert.Equal(allocatedPort, service.Status?.EffectivePort);
        Assert.Contains(containers[1].Spec.Ports!, p => p.HostPort == allocatedPort && p.ContainerPort == targetPort);
    }

    [Fact]
    public async Task PersistedProxylessEndpointPortIsReusedAndExcludedFromDynamicAllocation()
    {
        var (persistedPort, allocatedPort) = GetAvailableConsecutivePortPair();
        var builder = DistributedApplication.CreateBuilder();

        builder.AddExecutable("PersistentProgram", "persistent", Environment.CurrentDirectory)
            .WithPersistentLifetime()
            .WithEndpoint(name: "http", env: "PERSISTENT_PORT", isProxied: false);
        builder.AddExecutable("DynamicProgram", "dynamic", Environment.CurrentDirectory)
            .WithEndpoint(name: "http", env: "DYNAMIC_PORT", isProxied: false);

        var configDict = new Dictionary<string, string?>
        {
            ["AppHost:Sha256"] = "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef",
            ["Resources:PersistentProgram:http:port"] = persistedPort.ToString(CultureInfo.InvariantCulture)
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();
        var userSecretsManager = new MockUserSecretsManager();
        var dcpOptions = new DcpOptions
        {
            DashboardPath = "./dashboard",
            ProxylessEndpointPortRangeStart = persistedPort,
            ProxylessEndpointPortRangeEnd = allocatedPort
        };

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration, dcpOptions: dcpOptions, userSecretsManager: userSecretsManager);
        await appExecutor.RunApplicationAsync();

        var persistentService = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "PersistentProgram");
        var dynamicService = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "DynamicProgram");

        Assert.Equal(persistedPort, persistentService.Status?.EffectivePort);
        Assert.Equal(persistedPort, persistentService.Spec.Port);
        Assert.Equal(allocatedPort, dynamicService.Status?.EffectivePort);
        Assert.Equal(allocatedPort, dynamicService.Spec.Port);
        Assert.Empty(userSecretsManager.Secrets);
    }

    [Fact]
    public async Task IsolatedPersistentProxylessEndpointIgnoresAndDoesNotPersistPort()
    {
        var (persistedPort, allocatedPort) = GetAvailableConsecutivePortPair();
        var builder = DistributedApplication.CreateBuilder();

        builder.AddExecutable("PersistentProgram", "persistent", Environment.CurrentDirectory)
            .WithPersistentLifetime()
            .WithEndpoint(name: "http", env: "HTTP_PORT", isProxied: false);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppHost:Sha256"] = "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef",
                ["Resources:PersistentProgram:http:port"] = persistedPort.ToString(CultureInfo.InvariantCulture)
            })
            .Build();
        var userSecretsManager = new MockUserSecretsManager();
        var dcpOptions = new DcpOptions
        {
            DashboardPath = "./dashboard",
            RandomizePorts = true,
            ProxylessEndpointPortRangeStart = allocatedPort,
            ProxylessEndpointPortRangeEnd = allocatedPort
        };

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(
            distributedAppModel,
            configuration: configuration,
            kubernetesService: kubernetesService,
            dcpOptions: dcpOptions,
            userSecretsManager: userSecretsManager);

        await appExecutor.RunApplicationAsync();

        var service = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "PersistentProgram");
        Assert.Equal(AddressAllocationModes.Proxyless, service.Spec.AddressAllocationMode);
        Assert.Equal(allocatedPort, service.Status?.EffectivePort);
        Assert.NotEqual(persistedPort, service.Status?.EffectivePort);
        Assert.Empty(userSecretsManager.Secrets);
    }

    /// <summary>
    /// Verifies that applying unsupported endpoint port configuration to non-replicated, proxy-less Executables
    /// results in an error
    /// </summary>
    [Fact]
    public async Task UnsupportedEndpointPortsExecutableNotReplicatedProxyless()
    {
        const int desiredPortOne = TestKubernetesService.StartOfAutoPortRange - 1000;
        const int desiredPortTwo = TestKubernetesService.StartOfAutoPortRange - 999;

        (Action<IResourceBuilder<ExecutableResource>> AddEndpoint, string ErrorMessageFragment)[] testcases = [
            // Invalid configuration: both Port and TargetPort set, but to different values.
            (
                er => er.WithEndpoint(name: "PortAndTargetPortSetDifferently", port: desiredPortOne, targetPort: desiredPortTwo, env: "PORT_AND_TARGET_PORT_SET_DIFFERENTLY", isProxied: false),
                "has a value of Port property that is different from the value of TargetPort property"
            ),

            // Invalid configuration: Port requests dynamic allocation while TargetPort is fixed.
            (
                er => er.WithEndpoint(name: "ZeroPortAndTargetPortSetDifferently", port: 0, targetPort: desiredPortOne, env: "ZERO_PORT_AND_TARGET_PORT_SET_DIFFERENTLY", isProxied: false),
                "has a value of Port property that is different from the value of TargetPort property"
            )
        ];

        foreach (var tc in testcases)
        {
            var builder = DistributedApplication.CreateBuilder();

            var exe = builder.AddExecutable("CoolProgram", "cool", Environment.CurrentDirectory, "--alpha", "--bravo");
            tc.AddEndpoint(exe);

            var kubernetesService = new TestKubernetesService();
            using var app = builder.Build();
            var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
            var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => appExecutor.RunApplicationAsync());
            Assert.Contains(tc.ErrorMessageFragment, exception.Message);
        }
    }

    [Theory]
    [InlineData(1, "ServiceA")]
    [InlineData(2, "ServiceA")]
    public async Task EndpointOtelServiceName(int replicaCount, string expectedName)
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        builder.AddProject<Projects.ServiceA>("ServiceA")
            .WithReplicas(replicaCount);

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var dcpOptions = new DcpOptions { DashboardPath = "./dashboard", ResourceNameSuffix = "suffix" };
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, dcpOptions: dcpOptions);
        await appExecutor.RunApplicationAsync();

        var executables = GetCreatedExecutablesForResource(kubernetesService, "ServiceA");
        Assert.Equal(replicaCount, executables.Count);

        foreach (var exe in executables)
        {
            Assert.Equal(expectedName, exe.Metadata.Annotations[CustomResource.OtelServiceNameAnnotation]);
        }
    }

    [Fact]
    public async Task ResourceLogging_MultipleStreams_StreamedOverTime()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        builder.AddContainer("database", "image");

        var logStreamPipesChannel = Channel.CreateUnbounded<(string Type, Pipe Pipe)>();
        var kubernetesService = new TestKubernetesService(startStream: (obj, logStreamType) =>
        {
            var s = new Pipe();
            if (!logStreamPipesChannel.Writer.TryWrite((logStreamType, s)))
            {
                Assert.Fail("Pipe channel unexpectedly closed.");
            }

            return s.Reader.AsStream();
        });
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var dcpOptions = new DcpOptions { DashboardPath = "./dashboard" };
        var resourceLoggerService = new ResourceLoggerService();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, dcpOptions: dcpOptions, resourceLoggerService: resourceLoggerService);
        await appExecutor.RunApplicationAsync();

        var exeResource = Assert.Single(kubernetesService.CreatedResources.OfType<Container>());

        // Start watching logs for container.
        var watchCts = new CancellationTokenSource();
        var watchSubscribers = resourceLoggerService.WatchAnySubscribersAsync();
        var watchSubscribersEnumerator = watchSubscribers.GetAsyncEnumerator();
        var watchLogs = resourceLoggerService.WatchAsync(exeResource.Metadata.Name);
        var watchLogsEnumerator = watchLogs.GetAsyncEnumerator(watchCts.Token);

        var moveNextTask = watchLogsEnumerator.MoveNextAsync().AsTask();
        Assert.True(await moveNextTask);

        moveNextTask = watchLogsEnumerator.MoveNextAsync().AsTask();
        Assert.False(moveNextTask.IsCompletedSuccessfully, "No logs yet.");

        await watchSubscribersEnumerator.MoveNextAsync();
        Assert.Equal(exeResource.Metadata.Name, watchSubscribersEnumerator.Current.Name);
        Assert.True(watchSubscribersEnumerator.Current.AnySubscribers);

        exeResource.Status = new ContainerStatus { State = ContainerState.Running };
        kubernetesService.PushResourceModified(exeResource);

        var pipes = await GetStreamPipesAsync(logStreamPipesChannel);

        // Write content to container output stream. This is read by logging and creates log lines.
        await pipes.StandardOut.Writer.WriteAsync(Encoding.UTF8.GetBytes("2024-08-19T06:10:33.473275911Z Hello world" + Environment.NewLine));
        Assert.True(await moveNextTask);
        var logLine = watchLogsEnumerator.Current.Single();
        Assert.Equal("2024-08-19T06:10:33.4732759Z Hello world", logLine.Content);
        Assert.Equal(2, logLine.LineNumber);
        Assert.False(logLine.IsErrorMessage);

        moveNextTask = watchLogsEnumerator.MoveNextAsync().AsTask();
        Assert.False(moveNextTask.IsCompletedSuccessfully, "No logs yet.");

        // Note: This console log is earlier than the previous, but logs are displayed in real time as they're available.
        await pipes.StandardErr.Writer.WriteAsync(Encoding.UTF8.GetBytes("2024-08-19T06:10:32.661Z Next" + Environment.NewLine));
        Assert.True(await moveNextTask);
        logLine = watchLogsEnumerator.Current.Single();
        Assert.Equal("2024-08-19T06:10:32.6610000Z Next", logLine.Content);
        Assert.Equal(3, logLine.LineNumber);
        Assert.True(logLine.IsErrorMessage);

        var loggerState = resourceLoggerService.GetResourceLoggerState(exeResource.Metadata.Name);
        Assert.Collection(loggerState.GetBacklogSnapshot(),
            l => Assert.Equal("Next", l.Content),
            l => Assert.Equal("Hello world", l.Content),
            l => { });

        // Stop watching.
        moveNextTask = watchLogsEnumerator.MoveNextAsync().AsTask();
        watchCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await moveNextTask);

        await watchSubscribersEnumerator.MoveNextAsync();
        Assert.Equal(exeResource.Metadata.Name, watchSubscribersEnumerator.Current.Name);
        Assert.False(watchSubscribersEnumerator.Current.AnySubscribers);

        // State is clear when no longer watching.
        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => loggerState.GetBacklogSnapshot().Length == 0,
            "Backlog is asynchronously cleared after watch ends.");
    }

    [Fact]
    public async Task ResourceLogging_ReplayBacklog_SentInBatch()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        builder.AddContainer("database", "image");

        var kubernetesService = new TestKubernetesService(startStream: (obj, logStreamType) =>
        {
            switch (logStreamType)
            {
                case Logs.StreamTypeStdOut:
                    return new MemoryStream(Encoding.UTF8.GetBytes("2024-08-19T06:10:01.000Z First" + Environment.NewLine));
                case Logs.StreamTypeStdErr:
                    return new MemoryStream(Encoding.UTF8.GetBytes("2024-08-19T06:10:02.000Z Second" + Environment.NewLine));
                case Logs.StreamTypeStartupStdOut:
                    return new MemoryStream(Encoding.UTF8.GetBytes("2024-08-19T06:10:03.000Z Third" + Environment.NewLine));
                case Logs.StreamTypeStartupStdErr:
                    return new MemoryStream(Encoding.UTF8.GetBytes(
                        "2024-08-19T06:10:05.000Z Sixth" + Environment.NewLine +
                        "2024-08-19T06:10:05.000Z Seventh" + Environment.NewLine +
                        "2024-08-19T06:10:04.000Z Forth" + Environment.NewLine +
                        "2024-08-19T06:10:04.000Z Fifth" + Environment.NewLine));
                case Logs.StreamTypeSystem:
                    return new MemoryStream();
                default:
                    throw new InvalidOperationException("Unexpected type: " + logStreamType);
            }
        });
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var dcpOptions = new DcpOptions { DashboardPath = "./dashboard" };
        var resourceLoggerService = new ResourceLoggerService();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, dcpOptions: dcpOptions, resourceLoggerService: resourceLoggerService);
        await appExecutor.RunApplicationAsync();

        var exeResource = Assert.Single(kubernetesService.CreatedResources.OfType<Container>());

        // Start watching logs for container.
        var watchSubscribers = resourceLoggerService.WatchAnySubscribersAsync();
        var watchSubscribersEnumerator = watchSubscribers.GetAsyncEnumerator();
        var watchLogs1 = resourceLoggerService.WatchAsync(exeResource.Metadata.Name);
        var watchLogsTask1 = ConsoleLoggingTestHelpers.WatchForLogsAsync(watchLogs1, targetLogCount: 8);

        Assert.False(watchLogsTask1.IsCompletedSuccessfully, "Logs not available yet.");

        await watchSubscribersEnumerator.MoveNextAsync();
        Assert.Equal(exeResource.Metadata.Name, watchSubscribersEnumerator.Current.Name);
        Assert.True(watchSubscribersEnumerator.Current.AnySubscribers);

        exeResource.Status = new ContainerStatus { State = ContainerState.Running };
        kubernetesService.PushResourceModified(exeResource);

        var watchLogsResults1 = await watchLogsTask1;
        Assert.Equal(8, watchLogsResults1.Count);
        Assert.Contains(watchLogsResults1, l => l.Content.Contains("First"));
        Assert.Contains(watchLogsResults1, l => l.Content.Contains("Second"));
        Assert.Contains(watchLogsResults1, l => l.Content.Contains("Third"));
        Assert.Contains(watchLogsResults1, l => l.Content.Contains("Forth"));
        Assert.Contains(watchLogsResults1, l => l.Content.Contains("Fifth"));
        Assert.Contains(watchLogsResults1, l => l.Content.Contains("Sixth"));
        Assert.Contains(watchLogsResults1, l => l.Content.Contains("Seventh"));

        var watchLogs2 = resourceLoggerService.WatchAsync(exeResource.Metadata.Name);
        var watchLogsTask2 = ConsoleLoggingTestHelpers.WatchForLogsAsync(watchLogs2, targetLogCount: 8);

        var watchLogsResults2 = await watchLogsTask2;
        Assert.Contains(watchLogsResults2, l => l.Content.Contains("First"));
        Assert.Contains(watchLogsResults2, l => l.Content.Contains("Second"));
        Assert.Contains(watchLogsResults2, l => l.Content.Contains("Third"));
        Assert.Contains(watchLogsResults2, l => l.Content.Contains("Forth"));
        Assert.Contains(watchLogsResults2, l => l.Content.Contains("Fifth"));
        Assert.Contains(watchLogsResults2, l => l.Content.Contains("Sixth"));
        Assert.Contains(watchLogsResults2, l => l.Content.Contains("Seventh"));
    }

    [Fact]
    public async Task ResourceLogging_LateSubscriberReceivesFailedToStartLogsWithoutWatchReplay()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        builder.AddContainer("database", "image");

        const string failedToStartLogMessage = "failure discovered after the terminal notification";
        var followStreamStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var kubernetesService = new TestKubernetesService(startStreamWithFollow: (obj, logStreamType, follow) =>
        {
            if (obj is Container { Status.State: ContainerState.FailedToStart } &&
                logStreamType == Logs.StreamTypeStdErr &&
                follow == true)
            {
                followStreamStarted.TrySetResult();
                return new MemoryStream(Encoding.UTF8.GetBytes(
                    "2024-08-19T06:10:33.473275911Z " + failedToStartLogMessage + Environment.NewLine));
            }

            return new MemoryStream();
        });
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resourceLoggerService = new ResourceLoggerService();
        var terminalNotification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? dcpResourceName = null;

        var events = new DcpExecutorEvents();
        events.Subscribe<OnResourceChangedContext>(context =>
        {
            if (context.DcpResourceName == dcpResourceName &&
                context.Status.State == ContainerState.FailedToStart)
            {
                terminalNotification.TrySetResult();
            }

            return Task.CompletedTask;
        });

        var appExecutor = CreateAppExecutor(
            distributedAppModel,
            kubernetesService: kubernetesService,
            resourceLoggerService: resourceLoggerService,
            events: events);
        await appExecutor.RunApplicationAsync().DefaultTimeout();

        var container = Assert.Single(kubernetesService.CreatedResources.OfType<Container>());
        dcpResourceName = container.Metadata.Name;

        container.Status = new ContainerStatus { State = ContainerState.FailedToStart };
        kubernetesService.PushResourceModified(container);

        await terminalNotification.Task.DefaultTimeout();
        Assert.False(followStreamStarted.Task.IsCompleted, "A terminal log stream should not start without a subscriber.");

        var logLines = new ConcurrentQueue<LogLine>();
        using var subscription = resourceLoggerService.Subscribe(dcpResourceName, batch =>
        {
            foreach (var logLine in batch)
            {
                logLines.Enqueue(logLine);
            }
        });

        await followStreamStarted.Task.DefaultTimeout();
        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => logLines.Any(line => line.Content.Contains(failedToStartLogMessage, StringComparison.Ordinal)),
            "The subscriber-driven follow stream should deliver logs without a watch replay.");

        Assert.Single(logLines, line => line.Content.Contains(failedToStartLogMessage, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResourceLogging_ActiveSubscriberReceivesFailedToStartLogsAfterSnapshot()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        builder.AddContainer("database", "image");

        const string failedToStartLogMessage = "failure discovered after the point-in-time snapshot";
        var runningFollowStreamStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failedToStartSnapshotStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failedToStartFollowStreamStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failedToStartFollowStreamCount = 0;
        var kubernetesService = new TestKubernetesService(startStreamWithFollow: (obj, logStreamType, follow) =>
        {
            if (obj is Container { Status.State: ContainerState.Running } &&
                logStreamType == Logs.StreamTypeSystem &&
                follow == true)
            {
                runningFollowStreamStarted.TrySetResult();
            }

            if (obj is Container { Status.State: ContainerState.FailedToStart } &&
                logStreamType == Logs.StreamTypeStdErr)
            {
                if (follow == false)
                {
                    failedToStartSnapshotStarted.TrySetResult();
                }
                else if (follow == true)
                {
                    Interlocked.Increment(ref failedToStartFollowStreamCount);
                    failedToStartFollowStreamStarted.TrySetResult();
                    return new MemoryStream(Encoding.UTF8.GetBytes(
                        "2024-08-19T06:10:33.473275911Z " + failedToStartLogMessage + Environment.NewLine));
                }
            }

            return new MemoryStream();
        });
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resourceLoggerService = new ResourceLoggerService();
        var logLines = new ConcurrentQueue<LogLine>();
        var appExecutor = CreateAppExecutor(
            distributedAppModel,
            kubernetesService: kubernetesService,
            resourceLoggerService: resourceLoggerService);
        await appExecutor.RunApplicationAsync().DefaultTimeout();

        var container = Assert.Single(kubernetesService.CreatedResources.OfType<Container>());
        using var subscription = resourceLoggerService.Subscribe(container.Metadata.Name, batch =>
        {
            foreach (var logLine in batch)
            {
                logLines.Enqueue(logLine);
            }
        });

        container.Status = new ContainerStatus { State = ContainerState.Running };
        kubernetesService.PushResourceModified(container);

        await runningFollowStreamStarted.Task.DefaultTimeout();
        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => appExecutor.ResourceWatcher.GetLogStreamTask(container.Metadata.Name) is null,
            "The initial follow stream should complete before the terminal transition.");

        container.Status = new ContainerStatus { State = ContainerState.FailedToStart };
        kubernetesService.PushResourceModified(container);

        await failedToStartSnapshotStarted.Task.DefaultTimeout();
        await failedToStartFollowStreamStarted.Task.DefaultTimeout();
        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => logLines.Any(line => line.Content.Contains(failedToStartLogMessage, StringComparison.Ordinal)),
            "The normal follow stream should deliver logs after the point-in-time snapshot.");

        Assert.Equal(1, Volatile.Read(ref failedToStartFollowStreamCount));
        Assert.Single(logLines, line => line.Content.Contains(failedToStartLogMessage, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResourceLogging_TerminalStateFollowsLogsBeforeNotification()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        builder.AddContainer("database", "image");

        const string terminalLogMessage = "crash before terminal notification";
        bool? terminalStdErrFollow = null;
        var kubernetesService = new TestKubernetesService(startStreamWithFollow: (obj, logStreamType, follow) =>
        {
            if (obj is Container { Status.State: ContainerState.Exited } &&
                logStreamType == Logs.StreamTypeStdErr)
            {
                terminalStdErrFollow = follow;
                return new MemoryStream(Encoding.UTF8.GetBytes("2024-08-19T06:10:33.473275911Z " + terminalLogMessage + Environment.NewLine));
            }

            return new MemoryStream();
        });
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var dcpOptions = new DcpOptions { DashboardPath = "./dashboard" };
        var resourceLoggerService = new ResourceLoggerService();
        var logLines = new List<LogLine>();
        var logLinesLock = new object();
        var terminalLogCountAtNotification = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? dcpResourceName = null;

        var events = new DcpExecutorEvents();
        events.Subscribe<OnResourceChangedContext>(context =>
        {
            if (context.DcpResourceName == dcpResourceName && context.Status.State == ContainerState.Exited)
            {
                int logCount;
                lock (logLinesLock)
                {
                    logCount = logLines.Count(l => l.Content.Contains(terminalLogMessage));
                }

                terminalLogCountAtNotification.TrySetResult(logCount);
            }

            return Task.CompletedTask;
        });

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, dcpOptions: dcpOptions, resourceLoggerService: resourceLoggerService, events: events);
        await appExecutor.RunApplicationAsync().DefaultTimeout();

        var container = Assert.Single(kubernetesService.CreatedResources.OfType<Container>());
        dcpResourceName = container.Metadata.Name;

        using var subscription = resourceLoggerService.Subscribe(dcpResourceName, batch =>
        {
            lock (logLinesLock)
            {
                logLines.AddRange(batch);
            }
        });

        container.Status = new ContainerStatus { State = ContainerState.Exited };
        kubernetesService.PushResourceModified(container);

        Assert.Equal(1, await terminalLogCountAtNotification.Task.DefaultTimeout());
        Assert.True(terminalStdErrFollow == true);

        lock (logLinesLock)
        {
            Assert.Single(logLines, l => l.Content.Contains(terminalLogMessage));
        }
    }

    [Fact]
    public async Task ResourceLogging_TerminalLogFlushTimeoutDoesNotBlockOtherResourceNotifications()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        builder.AddContainer("blocking", "image");
        builder.AddContainer("other", "image");

        string? blockingDcpResourceName = null;
        var normalStreamStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockingTerminalFollowStreamCount = 0;
        var kubernetesService = new TestKubernetesService(startStreamWithFollow: (obj, logStreamType, follow) =>
        {
            if (obj.Metadata.Name == blockingDcpResourceName &&
                obj is Container container &&
                container.Status?.State != ContainerState.Exited &&
                logStreamType == Logs.StreamTypeSystem &&
                follow == true)
            {
                normalStreamStarted.TrySetResult();
            }

            if (obj.Metadata.Name == blockingDcpResourceName &&
                obj is Container { Status.State: ContainerState.Exited } &&
                logStreamType == Logs.StreamTypeStdErr &&
                follow == true)
            {
                if (Interlocked.Increment(ref blockingTerminalFollowStreamCount) == 1)
                {
                    return new Pipe().Reader.AsStream();
                }
            }

            return new MemoryStream();
        });
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var dcpOptions = new DcpOptions { DashboardPath = "./dashboard" };
        var resourceLoggerService = new ResourceLoggerService();
        var otherTerminalNotification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockingTerminalNotifications = 0;
        string? otherDcpResourceName = null;

        var events = new DcpExecutorEvents();
        events.Subscribe<OnResourceChangedContext>(context =>
        {
            if (context.Status.State == ContainerState.Exited)
            {
                if (context.DcpResourceName == blockingDcpResourceName)
                {
                    Interlocked.Increment(ref blockingTerminalNotifications);
                }
                else if (context.DcpResourceName == otherDcpResourceName)
                {
                    otherTerminalNotification.TrySetResult();
                }
            }

            return Task.CompletedTask;
        });

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, dcpOptions: dcpOptions, resourceLoggerService: resourceLoggerService, events: events);
        appExecutor.ResourceWatcher.TerminalLogFlushTimeout = TimeSpan.FromMilliseconds(100);
        await appExecutor.RunApplicationAsync().DefaultTimeout();

        var blockingContainer = Assert.Single(kubernetesService.CreatedResources.OfType<Container>(), c => c.AppModelResourceName == "blocking");
        var otherContainer = Assert.Single(kubernetesService.CreatedResources.OfType<Container>(), c => c.AppModelResourceName == "other");
        blockingDcpResourceName = blockingContainer.Metadata.Name;
        otherDcpResourceName = otherContainer.Metadata.Name;

        using var subscription = resourceLoggerService.Subscribe(blockingDcpResourceName, _ => { });
        blockingContainer.Status = new ContainerStatus { State = ContainerState.Running };
        kubernetesService.PushResourceModified(blockingContainer);
        await normalStreamStarted.Task.DefaultTimeout();

        blockingContainer.Status = new ContainerStatus { State = ContainerState.Exited };
        kubernetesService.PushResourceModified(blockingContainer);

        otherContainer.Status = new ContainerStatus { State = ContainerState.Exited };
        kubernetesService.PushResourceModified(otherContainer);

        await otherTerminalNotification.Task.DefaultTimeout();

        blockingContainer.Status = new ContainerStatus { State = ContainerState.Exited, ExitCode = 1 };
        kubernetesService.PushResourceModified(blockingContainer);

        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => Volatile.Read(ref blockingTerminalNotifications) >= 2,
            "The terminal state change after a timed-out flush should be reported.");

        Assert.Equal(2, Volatile.Read(ref blockingTerminalNotifications));
        // The first terminal event opens the timed-out flush and its recovery stream. The changed
        // terminal event then retries the flush because the first one was not recorded as complete.
        Assert.Equal(3, Volatile.Read(ref blockingTerminalFollowStreamCount));
    }

    [Fact]
    public async Task ResourceLogging_ActiveSubscriberContinuesAfterTerminalFlushTimeout()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        builder.AddContainer("database", "image");

        const string terminalLogMessage = "log delivered after terminal flush timeout";
        var timedOutFlushPipe = new Pipe();
        var runningFollowStreamStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminalFollowStreamStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminalStdErrFollowStreamCount = 0;
        var kubernetesService = new TestKubernetesService(startStreamWithFollow: (obj, logStreamType, follow) =>
        {
            if (obj is Container { Status.State: ContainerState.Running } &&
                logStreamType == Logs.StreamTypeSystem &&
                follow == true)
            {
                runningFollowStreamStarted.TrySetResult();
            }

            if (obj is Container { Status.State: ContainerState.Exited } &&
                logStreamType == Logs.StreamTypeStdErr &&
                follow == true)
            {
                if (Interlocked.Increment(ref terminalStdErrFollowStreamCount) == 1)
                {
                    return timedOutFlushPipe.Reader.AsStream();
                }

                terminalFollowStreamStarted.TrySetResult();
                return new MemoryStream(Encoding.UTF8.GetBytes(
                    "2024-08-19T06:10:33.473275911Z " + terminalLogMessage + Environment.NewLine));
            }

            return new MemoryStream();
        });
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resourceLoggerService = new ResourceLoggerService();
        var logLines = new ConcurrentQueue<LogLine>();
        var appExecutor = CreateAppExecutor(
            distributedAppModel,
            kubernetesService: kubernetesService,
            resourceLoggerService: resourceLoggerService);
        appExecutor.ResourceWatcher.TerminalLogFlushTimeout = TimeSpan.FromMilliseconds(100);
        await appExecutor.RunApplicationAsync().DefaultTimeout();

        var container = Assert.Single(kubernetesService.CreatedResources.OfType<Container>());
        using var subscription = resourceLoggerService.Subscribe(container.Metadata.Name, batch =>
        {
            foreach (var logLine in batch)
            {
                logLines.Enqueue(logLine);
            }
        });

        try
        {
            container.Status = new ContainerStatus { State = ContainerState.Running };
            kubernetesService.PushResourceModified(container);

            await runningFollowStreamStarted.Task.DefaultTimeout();
            await AsyncTestHelpers.AssertIsTrueRetryAsync(
                () => appExecutor.ResourceWatcher.GetLogStreamTask(container.Metadata.Name) is null,
                "The initial follow stream should complete before the terminal transition.");

            container.Status = new ContainerStatus { State = ContainerState.Exited };
            kubernetesService.PushResourceModified(container);

            await terminalFollowStreamStarted.Task.DefaultTimeout();
            await AsyncTestHelpers.AssertIsTrueRetryAsync(
                () => logLines.Any(line => line.Content.Contains(terminalLogMessage, StringComparison.Ordinal)),
                "The normal follow stream should deliver logs after the terminal flush times out.");

            Assert.Equal(2, Volatile.Read(ref terminalStdErrFollowStreamCount));
            Assert.Single(logLines, line => line.Content.Contains(terminalLogMessage, StringComparison.Ordinal));
        }
        finally
        {
            timedOutFlushPipe.Writer.Complete();
        }
    }

    [Fact]
    public async Task ResourceLogging_FollowStreamDeduplicatesOnlyPendingTerminalFlush()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        builder.AddContainer("database", "image");

        var followStdErrPipeChannel = Channel.CreateUnbounded<Pipe>();
        var followStdErrStreamCount = 0;
        var kubernetesService = new TestKubernetesService(startStreamWithFollow: (obj, logStreamType, follow) =>
        {
            if (logStreamType == Logs.StreamTypeStdErr)
            {
                if (follow == true)
                {
                    if (Interlocked.Increment(ref followStdErrStreamCount) == 1)
                    {
                        var pipe = new Pipe();
                        followStdErrPipeChannel.Writer.TryWrite(pipe);
                        return pipe.Reader.AsStream();
                    }

                    return new MemoryStream(Encoding.UTF8.GetBytes("same" + Environment.NewLine));
                }

                return new MemoryStream();
            }

            return new MemoryStream();
        });
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var dcpOptions = new DcpOptions { DashboardPath = "./dashboard" };
        var resourceLoggerService = new ResourceLoggerService();
        var logLines = new List<LogLine>();
        var logLinesLock = new object();

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, dcpOptions: dcpOptions, resourceLoggerService: resourceLoggerService);
        await appExecutor.RunApplicationAsync().DefaultTimeout();

        var container = Assert.Single(kubernetesService.CreatedResources.OfType<Container>());

        using var subscription = resourceLoggerService.Subscribe(container.Metadata.Name, batch =>
        {
            lock (logLinesLock)
            {
                logLines.AddRange(batch);
            }
        });

        container.Status = new ContainerStatus { State = ContainerState.Running };
        kubernetesService.PushResourceModified(container);

        var followStdErrPipe = await followStdErrPipeChannel.Reader.ReadAsync().AsTask().DefaultTimeout();

        container.Status = new ContainerStatus { State = ContainerState.Exited };
        kubernetesService.PushResourceModified(container);

        await AsyncTestHelpers.AssertIsTrueRetryAsync(() =>
        {
            lock (logLinesLock)
            {
                return logLines.Count(l => l.Content == "same") == 1;
            }
        },
        "Terminal flush should deliver the snapshot log.");

        Assert.True(appExecutor.ResourceWatcher.HasLogStreamPendingDeduplication(container.Metadata.Name));

        await followStdErrPipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("same" + Environment.NewLine + "same" + Environment.NewLine));

        await AsyncTestHelpers.AssertIsTrueRetryAsync(() =>
        {
            lock (logLinesLock)
            {
                return logLines.Count(l => l.Content == "same") == 2;
            }
        },
        "Follow stream should skip the overlapping flushed line but preserve a later identical line.");

        Assert.False(appExecutor.ResourceWatcher.HasLogStreamPendingDeduplication(container.Metadata.Name));
    }

    [Fact]
    public async Task ResourceLogging_CompletedFollowStreamIsRemovedAndCanRestartWithExistingSubscriber()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        builder.AddContainer("database", "image");

        const string restartedLogMessage = "log after restart";
        var restartedLogLine = "2024-08-19T06:10:33.473275911Z " + restartedLogMessage + Environment.NewLine;
        var firstFollowStream = new GatedReadStream();
        var secondFollowStreamStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stdoutFollowStreamCount = 0;
        var kubernetesService = new TestKubernetesService(startStreamWithFollow: (obj, logStreamType, follow) =>
        {
            if (obj is Container { Status.State: ContainerState.Running } &&
                logStreamType == Logs.StreamTypeStdOut &&
                follow == true)
            {
                if (Interlocked.Increment(ref stdoutFollowStreamCount) == 1)
                {
                    return firstFollowStream;
                }

                secondFollowStreamStarted.TrySetResult();
                return new MemoryStream(Encoding.UTF8.GetBytes(restartedLogLine));
            }

            return new MemoryStream();
        });
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resourceLoggerService = new ResourceLoggerService();
        var logLines = new ConcurrentQueue<LogLine>();
        var appExecutor = CreateAppExecutor(
            distributedAppModel,
            kubernetesService: kubernetesService,
            resourceLoggerService: resourceLoggerService);
        await appExecutor.RunApplicationAsync().DefaultTimeout();

        var container = Assert.Single(kubernetesService.CreatedResources.OfType<Container>());
        using var subscription = resourceLoggerService.Subscribe(container.Metadata.Name, batch =>
        {
            foreach (var logLine in batch)
            {
                logLines.Enqueue(logLine);
            }
        });

        try
        {
            container.Status = new ContainerStatus { State = ContainerState.Running };
            kubernetesService.PushResourceModified(container);

            await firstFollowStream.ReadStarted.DefaultTimeout();
            await AsyncTestHelpers.AssertIsTrueRetryAsync(
                () => appExecutor.ResourceWatcher.GetLogStreamTask(container.Metadata.Name) is not null,
                "The first subscription should start a log stream.");
            var firstLogStreamTask = appExecutor.ResourceWatcher.GetLogStreamTask(container.Metadata.Name);
            Assert.NotNull(firstLogStreamTask);

            firstFollowStream.Release();
            await firstLogStreamTask.DefaultTimeout();
            Assert.Null(appExecutor.ResourceWatcher.GetLogStreamTask(container.Metadata.Name));

            kubernetesService.PushResourceModified(container);
            await secondFollowStreamStarted.Task.DefaultTimeout();
            await AsyncTestHelpers.AssertIsTrueRetryAsync(
                () => logLines.Any(line => line.Content.Contains(restartedLogMessage, StringComparison.Ordinal)),
                "The follow stream started after the restart should deliver logs to the existing subscriber.");

            Assert.Equal(2, Volatile.Read(ref stdoutFollowStreamCount));
            Assert.Single(logLines, line => line.Content.Contains(restartedLogMessage, StringComparison.Ordinal));
        }
        finally
        {
            firstFollowStream.TryRelease();
        }
    }

    [Fact]
    public async Task ResourceLogging_OverlappingSameUidStreamCannotClearNewDeduplicationState()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        builder.AddContainer("database", "image");

        const string firstTerminalLogMessage = "first terminal period";
        const string secondTerminalLogMessage = "second terminal period";
        var firstTerminalLogLine = "2024-08-19T06:10:33.473275911Z " + firstTerminalLogMessage + Environment.NewLine;
        var secondTerminalLogLine = "2024-08-19T06:10:34.473275911Z " + secondTerminalLogMessage + Environment.NewLine;
        var previousFollowStream = new GatedReadStream();
        var currentFollowStream = new GatedReadStream();
        var logger = new GatedLogger<DcpExecutor>("was cancelled.");
        var runningFollowStreams = 0;
        var terminalFlushes = 0;

        var kubernetesService = new TestKubernetesService(startStreamWithFollow: (obj, logStreamType, follow) =>
        {
            if (obj is Container container &&
                logStreamType == Logs.StreamTypeStdErr &&
                follow == true)
            {
                if (container.Status?.State == ContainerState.Running)
                {
                    return Interlocked.Increment(ref runningFollowStreams) == 1
                        ? previousFollowStream
                        : currentFollowStream;
                }

                if (container.Status?.State == ContainerState.Exited)
                {
                    return new MemoryStream(Encoding.UTF8.GetBytes(
                        Interlocked.Increment(ref terminalFlushes) == 1
                            ? firstTerminalLogLine
                            : secondTerminalLogLine));
                }
            }

            return new MemoryStream();
        });

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resourceLoggerService = new ResourceLoggerService();
        var logLines = new ConcurrentQueue<LogLine>();
        var runningNotifications = 0;
        var terminalNotifications = 0;
        string? dcpResourceName = null;

        var events = new DcpExecutorEvents();
        events.Subscribe<OnResourceChangedContext>(context =>
        {
            if (context.DcpResourceName == dcpResourceName)
            {
                if (context.Status.State == ContainerState.Running)
                {
                    Interlocked.Increment(ref runningNotifications);
                }
                else if (context.Status.State == ContainerState.Exited)
                {
                    Interlocked.Increment(ref terminalNotifications);
                }
            }

            return Task.CompletedTask;
        });

        var appExecutor = CreateAppExecutor(
            distributedAppModel,
            kubernetesService: kubernetesService,
            resourceLoggerService: resourceLoggerService,
            events: events,
            logger: logger);
        await appExecutor.RunApplicationAsync().DefaultTimeout();

        var container = Assert.Single(kubernetesService.CreatedResources.OfType<Container>());
        dcpResourceName = container.Metadata.Name;
        Assert.False(string.IsNullOrEmpty(container.Metadata.Uid));

        IDisposable? firstSubscription = resourceLoggerService.Subscribe(dcpResourceName, AddLogLines);
        IDisposable? secondSubscription = null;

        try
        {
            container.Status = new ContainerStatus { State = ContainerState.Running };
            kubernetesService.PushResourceModified(container);
            await previousFollowStream.ReadStarted.DefaultTimeout();
            await AsyncTestHelpers.AssertIsTrueRetryAsync(
                () => appExecutor.ResourceWatcher.GetLogStreamTask(dcpResourceName) is not null,
                "The first terminal period should have an active follow stream.");
            var previousLogStreamTask = appExecutor.ResourceWatcher.GetLogStreamTask(dcpResourceName);
            Assert.NotNull(previousLogStreamTask);

            container.Status = new ContainerStatus { State = ContainerState.Exited };
            kubernetesService.PushResourceModified(container);
            await AsyncTestHelpers.AssertIsTrueRetryAsync(
                () => Volatile.Read(ref terminalNotifications) >= 1,
                "The first terminal period should be reported.");
            Assert.Single(logLines, line => line.Content.Contains(firstTerminalLogMessage, StringComparison.Ordinal));

            container.Status = new ContainerStatus { State = ContainerState.Running };
            kubernetesService.PushResourceModified(container);
            await AsyncTestHelpers.AssertIsTrueRetryAsync(
                () => Volatile.Read(ref runningNotifications) >= 2,
                "The restarted resource should be reported as running.");

            firstSubscription.Dispose();
            firstSubscription = null;
            await logger.Blocked.DefaultTimeout();

            secondSubscription = resourceLoggerService.Subscribe(dcpResourceName, AddLogLines);
            await currentFollowStream.ReadStarted.DefaultTimeout();
            await AsyncTestHelpers.AssertIsTrueRetryAsync(
                () => appExecutor.ResourceWatcher.GetLogStreamTask(dcpResourceName) is { } task && task != previousLogStreamTask,
                "The second subscription should start a new follow stream.");
            var currentLogStreamTask = appExecutor.ResourceWatcher.GetLogStreamTask(dcpResourceName);
            Assert.NotNull(currentLogStreamTask);

            container.Status = new ContainerStatus { State = ContainerState.Exited, ExitCode = 1 };
            kubernetesService.PushResourceModified(container);
            await AsyncTestHelpers.AssertIsTrueRetryAsync(
                () => Volatile.Read(ref terminalNotifications) >= 2,
                "The second terminal period should be reported.");
            Assert.Single(logLines, line => line.Content.Contains(secondTerminalLogMessage, StringComparison.Ordinal));

            // The canceled stream owns the first terminal period's deduplication state. Finishing it
            // now must not clear the newer state installed for the same UID's second terminal period.
            previousFollowStream.Release();
            logger.Release();
            await previousLogStreamTask.DefaultTimeout();
            Assert.Same(currentLogStreamTask, appExecutor.ResourceWatcher.GetLogStreamTask(dcpResourceName));

            currentFollowStream.Release(secondTerminalLogLine);
            await currentLogStreamTask.DefaultTimeout();

            Assert.Equal(2, Volatile.Read(ref terminalFlushes));
            Assert.Single(logLines, line => line.Content.Contains(secondTerminalLogMessage, StringComparison.Ordinal));
        }
        finally
        {
            logger.Release();
            previousFollowStream.TryRelease();
            currentFollowStream.TryRelease();
            firstSubscription?.Dispose();
            secondSubscription?.Dispose();
        }

        void AddLogLines(IReadOnlyList<LogLine> batch)
        {
            foreach (var logLine in batch)
            {
                logLines.Enqueue(logLine);
            }
        }
    }

    [Fact]
    public async Task ResourceLogging_CanceledSameUidStreamCannotClearHandedOffDeduplicationState()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        builder.AddContainer("database", "image");

        const string terminalLogMessage = "terminal period";
        var terminalLogLine = "2024-08-19T06:10:33.473275911Z " + terminalLogMessage + Environment.NewLine;
        var previousFollowStream = new GatedReadStream();
        var currentFollowStream = new GatedReadStream();
        var logger = new GatedLogger<DcpExecutor>("was cancelled.");
        var runningFollowStreams = 0;
        var terminalFlushes = 0;

        var kubernetesService = new TestKubernetesService(startStreamWithFollow: (obj, logStreamType, follow) =>
        {
            if (obj is Container container &&
                logStreamType == Logs.StreamTypeStdErr &&
                follow == true)
            {
                if (container.Status?.State == ContainerState.Running)
                {
                    return Interlocked.Increment(ref runningFollowStreams) == 1
                        ? previousFollowStream
                        : currentFollowStream;
                }

                if (container.Status?.State == ContainerState.Exited)
                {
                    Interlocked.Increment(ref terminalFlushes);
                    return new MemoryStream(Encoding.UTF8.GetBytes(terminalLogLine));
                }
            }

            return new MemoryStream();
        });

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resourceLoggerService = new ResourceLoggerService();
        var logLines = new ConcurrentQueue<LogLine>();
        var runningNotifications = 0;
        var terminalNotifications = 0;
        string? dcpResourceName = null;

        var events = new DcpExecutorEvents();
        events.Subscribe<OnResourceChangedContext>(context =>
        {
            if (context.DcpResourceName == dcpResourceName)
            {
                if (context.Status.State == ContainerState.Running)
                {
                    Interlocked.Increment(ref runningNotifications);
                }
                else if (context.Status.State == ContainerState.Exited)
                {
                    Interlocked.Increment(ref terminalNotifications);
                }
            }

            return Task.CompletedTask;
        });

        var appExecutor = CreateAppExecutor(
            distributedAppModel,
            kubernetesService: kubernetesService,
            resourceLoggerService: resourceLoggerService,
            events: events,
            logger: logger);
        await appExecutor.RunApplicationAsync().DefaultTimeout();

        var container = Assert.Single(kubernetesService.CreatedResources.OfType<Container>());
        dcpResourceName = container.Metadata.Name;
        Assert.False(string.IsNullOrEmpty(container.Metadata.Uid));

        IDisposable? firstSubscription = resourceLoggerService.Subscribe(dcpResourceName, AddLogLines);
        IDisposable? secondSubscription = null;

        try
        {
            container.Status = new ContainerStatus { State = ContainerState.Running };
            kubernetesService.PushResourceModified(container);
            await previousFollowStream.ReadStarted.DefaultTimeout();
            await AsyncTestHelpers.AssertIsTrueRetryAsync(
                () => appExecutor.ResourceWatcher.GetLogStreamTask(dcpResourceName) is not null,
                "The first subscription should start a log stream.");
            var previousLogStreamTask = appExecutor.ResourceWatcher.GetLogStreamTask(dcpResourceName);
            Assert.NotNull(previousLogStreamTask);

            container.Status = new ContainerStatus { State = ContainerState.Exited };
            kubernetesService.PushResourceModified(container);
            await AsyncTestHelpers.AssertIsTrueRetryAsync(
                () => Volatile.Read(ref terminalNotifications) >= 1,
                "The terminal period should be reported.");
            Assert.Single(logLines, line => line.Content.Contains(terminalLogMessage, StringComparison.Ordinal));

            container.Status = new ContainerStatus { State = ContainerState.Running };
            kubernetesService.PushResourceModified(container);
            await AsyncTestHelpers.AssertIsTrueRetryAsync(
                () => Volatile.Read(ref runningNotifications) >= 2,
                "The restarted resource should be reported as running.");

            firstSubscription.Dispose();
            firstSubscription = null;
            await logger.Blocked.DefaultTimeout();

            secondSubscription = resourceLoggerService.Subscribe(dcpResourceName, AddLogLines);
            await currentFollowStream.ReadStarted.DefaultTimeout();
            await AsyncTestHelpers.AssertIsTrueRetryAsync(
                () => appExecutor.ResourceWatcher.GetLogStreamTask(dcpResourceName) is { } task && task != previousLogStreamTask,
                "The second subscription should start a new follow stream.");
            var currentLogStreamTask = appExecutor.ResourceWatcher.GetLogStreamTask(dcpResourceName);
            Assert.NotNull(currentLogStreamTask);
            Assert.True(appExecutor.ResourceWatcher.HasLogStreamPendingDeduplication(dcpResourceName));

            // The second stream has adopted the same pending object retained by the canceled stream.
            // Late completion of the canceled stream must leave the handed-off state in place.
            previousFollowStream.Release();
            logger.Release();
            await previousLogStreamTask.DefaultTimeout();
            Assert.Same(currentLogStreamTask, appExecutor.ResourceWatcher.GetLogStreamTask(dcpResourceName));
            Assert.True(appExecutor.ResourceWatcher.HasLogStreamPendingDeduplication(dcpResourceName));

            currentFollowStream.Release(terminalLogLine);
            await currentLogStreamTask.DefaultTimeout();

            Assert.Equal(1, Volatile.Read(ref terminalFlushes));
            Assert.Single(logLines, line => line.Content.Contains(terminalLogMessage, StringComparison.Ordinal));
            Assert.False(appExecutor.ResourceWatcher.HasLogStreamPendingDeduplication(dcpResourceName));
        }
        finally
        {
            logger.Release();
            previousFollowStream.TryRelease();
            currentFollowStream.TryRelease();
            firstSubscription?.Dispose();
            secondSubscription?.Dispose();
        }

        void AddLogLines(IReadOnlyList<LogLine> batch)
        {
            foreach (var logLine in batch)
            {
                logLines.Enqueue(logLine);
            }
        }
    }

    [Fact]
    public async Task ResourceWatch_ResourceWithoutResourceVersionIsAlwaysProcessed()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        builder.AddContainer("database", "image");

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var observedStates = new ConcurrentQueue<string?>();

        var events = new DcpExecutorEvents();
        events.Subscribe<OnResourceChangedContext>(context =>
        {
            if (context.Resource.Name == "database")
            {
                observedStates.Enqueue(context.Status.State);
            }

            return Task.CompletedTask;
        });

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, events: events);
        await appExecutor.RunApplicationAsync().DefaultTimeout();

        var container = Assert.Single(kubernetesService.CreatedResources.OfType<Container>());

        container.Status = new ContainerStatus { State = ContainerState.Running };
        kubernetesService.PushResourceModified(container);

        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => observedStates.Contains(ContainerState.Running),
            "The state change to Running should be reported.");

        // Without a resource version there is no way to tell a replay from a change, so the watcher
        // must fall back to processing the event. Suppressing it instead would freeze the resource.
        container.Metadata.ResourceVersion = null;
        container.Status = new ContainerStatus { State = ContainerState.Exited };
        kubernetesService.PushResourceUnchanged(container);
        kubernetesService.PushResourceUnchanged(container);

        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => observedStates.Count(s => s == ContainerState.Exited) >= 2,
            "Events without a resource version should always be processed.");

        Assert.Equal(2, observedStates.Count(s => s == ContainerState.Exited));
    }

    [Fact]
    public async Task ResourceWatch_UnchangedResourceNotificationIsIgnored()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        builder.AddContainer("database", "image");

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var observedStates = new ConcurrentQueue<string?>();

        var events = new DcpExecutorEvents();
        events.Subscribe<OnResourceChangedContext>(context =>
        {
            if (context.Resource.Name == "database")
            {
                observedStates.Enqueue(context.Status.State);
            }

            return Task.CompletedTask;
        });

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, events: events);
        await appExecutor.RunApplicationAsync().DefaultTimeout();

        var container = Assert.Single(kubernetesService.CreatedResources.OfType<Container>());

        container.Status = new ContainerStatus { State = ContainerState.Running };
        kubernetesService.PushResourceModified(container);

        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => observedStates.Contains(ContainerState.Running),
            "The state change to Running should be reported.");

        // Re-delivering the same resource UID without a new resource version means nothing was stored,
        // so it does not describe a change no matter which event type carries it.
        kubernetesService.PushResourceUnchanged(container);
        kubernetesService.PushResourceUnchanged(container, k8s.WatchEventType.Added);

        // Pushing a real change afterwards acts as a barrier: the container watch is processed in
        // order, so once Exited is observed the replays above have already been handled or dropped.
        container.Status = new ContainerStatus { State = ContainerState.Exited };
        kubernetesService.PushResourceModified(container);

        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => observedStates.Contains(ContainerState.Exited),
            "The state change to Exited should be reported.");

        AssertStatesReportedAfter(observedStates, ContainerState.Running, ContainerState.Exited);
    }

    [Fact]
    public async Task ResourceWatch_RecreatedResourceWithPreviouslySeenVersionIsProcessed()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        builder.AddContainer("database", "image");

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var observedStates = new ConcurrentQueue<string?>();

        var events = new DcpExecutorEvents();
        events.Subscribe<OnResourceChangedContext>(context =>
        {
            if (context.Resource.Name == "database")
            {
                observedStates.Enqueue(context.Status.State);
            }

            return Task.CompletedTask;
        });

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, events: events);
        await appExecutor.RunApplicationAsync().DefaultTimeout();

        var container = Assert.Single(kubernetesService.CreatedResources.OfType<Container>());

        container.Status = new ContainerStatus { State = ContainerState.Running };
        kubernetesService.PushResourceModified(container);

        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => observedStates.Contains(ContainerState.Running),
            "The state change to Running should be reported.");

        // Resource versions are opaque, so a recreated object can carry a value that was already
        // observed for the previous object with the same kind and name. The Deleted event must clear
        // that version before the recreated object is delivered as Added.
        kubernetesService.PushResourceDeleted(container);
        container.Metadata.Uid = "database-instance-2";
        container.Status = new ContainerStatus { State = ContainerState.Exited };
        kubernetesService.PushResourceUnchanged(container, k8s.WatchEventType.Added);

        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => observedStates.Contains(ContainerState.Exited),
            "The recreated resource should be reported even when its resource version was seen before deletion.");

        AssertStatesReportedAfter(observedStates, ContainerState.Running, ContainerState.Exited);
    }

    [Fact]
    public async Task ResourceWatch_RecreatedResourceAfterMissedDeleteIsProcessedAndResetsLogState()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        builder.AddContainer("database", "image");

        const string previousLateLogMessage = "late log from previous resource";
        const string replacementLogMessage = "replacement terminal log";
        var previousLateLogLine = "2024-08-19T06:10:32.473275911Z " + previousLateLogMessage + Environment.NewLine;
        var replacementLogLine = "2024-08-19T06:10:33.473275911Z " + replacementLogMessage + Environment.NewLine;
        var previousFollowStream = new GatedReadStream();
        var replacementFollowStream = new GatedReadStream();
        var previousBatchReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePreviousBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementFollowStreamStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementStdErrFollowStreams = 0;
        string? previousResourceUid = null;
        string? replacementResourceUid = null;

        var kubernetesService = new TestKubernetesService(startStreamWithFollow: (obj, logStreamType, follow) =>
        {
            if (obj is Container container &&
                logStreamType == Logs.StreamTypeStdErr &&
                follow == true)
            {
                if (container.Metadata.Uid == previousResourceUid &&
                    container.Status?.State == ContainerState.Running)
                {
                    return previousFollowStream;
                }

                if (container.Metadata.Uid == replacementResourceUid &&
                    container.Status?.State == ContainerState.Exited)
                {
                    if (Interlocked.Increment(ref replacementStdErrFollowStreams) == 1)
                    {
                        return new MemoryStream(Encoding.UTF8.GetBytes(replacementLogLine));
                    }

                    replacementFollowStreamStarted.TrySetResult();
                    return replacementFollowStream;
                }
            }

            return new MemoryStream();
        });

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resourceLoggerService = new ResourceLoggerService();
        var logLines = new ConcurrentQueue<LogLine>();
        var terminalNotifications = 0;
        string? dcpResourceName = null;

        var events = new DcpExecutorEvents();
        events.Subscribe<OnResourceChangedContext>(context =>
        {
            if (context.DcpResourceName == dcpResourceName &&
                context.Status.State == ContainerState.Exited)
            {
                Interlocked.Increment(ref terminalNotifications);
            }

            return Task.CompletedTask;
        });

        var appExecutor = CreateAppExecutor(
            distributedAppModel,
            kubernetesService: kubernetesService,
            resourceLoggerService: resourceLoggerService,
            events: events);
        appExecutor.ResourceWatcher.BeforeLogBatchDeliveryAsync = async resourceUid =>
        {
            if (resourceUid == previousResourceUid)
            {
                previousBatchReady.TrySetResult();
                await releasePreviousBatch.Task.ConfigureAwait(false);
            }
        };
        await appExecutor.RunApplicationAsync().DefaultTimeout();

        var container = Assert.Single(kubernetesService.CreatedResources.OfType<Container>());
        dcpResourceName = container.Metadata.Name;
        previousResourceUid = container.Metadata.Uid;
        Assert.False(string.IsNullOrEmpty(previousResourceUid));

        using var subscription = resourceLoggerService.Subscribe(dcpResourceName, batch =>
        {
            foreach (var logLine in batch)
            {
                logLines.Enqueue(logLine);
            }
        });

        try
        {
            container.Status = new ContainerStatus { State = ContainerState.Running };
            kubernetesService.PushResourceModified(container);
            await previousFollowStream.ReadStarted.DefaultTimeout();
            await AsyncTestHelpers.AssertIsTrueRetryAsync(
                () => appExecutor.ResourceWatcher.GetLogStreamTask(dcpResourceName) is not null,
                "The first resource incarnation should have an active log stream.");
            var previousLogStreamTask = appExecutor.ResourceWatcher.GetLogStreamTask(dcpResourceName);
            Assert.NotNull(previousLogStreamTask);

            container.Status = new ContainerStatus { State = ContainerState.Exited };
            kubernetesService.PushResourceModified(container);

            await AsyncTestHelpers.AssertIsTrueRetryAsync(
                () => Volatile.Read(ref terminalNotifications) >= 1,
                "The first resource incarnation should report its terminal state.");

            // Pause after the old stream has yielded a batch to DcpResourceWatcher. Cancellation can stop
            // future reads, but it cannot revoke a batch that the outer await-foreach loop already received.
            previousFollowStream.Release(previousLateLogLine);
            await previousBatchReady.Task.DefaultTimeout();

            // Simulate a delete and recreation that happened while the watch was disconnected. The fresh
            // list-and-watch reports only Added for the new UID, and resourceVersion is opaque enough that
            // it can equal the value last observed for the previous object.
            replacementResourceUid = "database-instance-2";
            container.Metadata.Uid = replacementResourceUid;
            container.Status = new ContainerStatus { State = ContainerState.Exited, ExitCode = 1 };
            kubernetesService.PushResourceUnchanged(container, k8s.WatchEventType.Added);

            await AsyncTestHelpers.AssertIsTrueRetryAsync(
                () => Volatile.Read(ref terminalNotifications) >= 2,
                "The replacement resource should be processed even though no delete event was observed.");
            await replacementFollowStreamStarted.Task.DefaultTimeout();
            await AsyncTestHelpers.AssertIsTrueRetryAsync(
                () => appExecutor.ResourceWatcher.GetLogStreamTask(dcpResourceName) is { } task && task != previousLogStreamTask,
                "The replacement resource should start a new log stream.");
            var replacementLogStreamTask = appExecutor.ResourceWatcher.GetLogStreamTask(dcpResourceName);
            Assert.NotNull(replacementLogStreamTask);
            Assert.Single(logLines, line => line.Content.Contains(replacementLogMessage, StringComparison.Ordinal));

            // Let the old batch continue only after the replacement stream has installed its pending
            // deduplication state. It must neither publish under the replacement's name nor remove that state.
            releasePreviousBatch.TrySetResult();
            await previousLogStreamTask.DefaultTimeout();

            replacementFollowStream.Release(replacementLogLine);
            await replacementLogStreamTask.DefaultTimeout();

            Assert.Equal(2, Volatile.Read(ref replacementStdErrFollowStreams));
            Assert.Collection(
                logLines.Where(line =>
                    line.Content.Contains(previousLateLogMessage, StringComparison.Ordinal) ||
                    line.Content.Contains(replacementLogMessage, StringComparison.Ordinal)),
                line => Assert.Contains(replacementLogMessage, line.Content, StringComparison.Ordinal));
        }
        finally
        {
            appExecutor.ResourceWatcher.BeforeLogBatchDeliveryAsync = null;
            releasePreviousBatch.TrySetResult();
            previousFollowStream.TryRelease();
            replacementFollowStream.TryRelease();
        }
    }

    [Fact]
    public async Task ResourceWatch_WatchRestartDoesNotRepublishUnchangedResources()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        builder.AddContainer("database", "image");

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var observedStates = new ConcurrentQueue<string?>();

        var events = new DcpExecutorEvents();
        events.Subscribe<OnResourceChangedContext>(context =>
        {
            if (context.Resource.Name == "database")
            {
                observedStates.Enqueue(context.Status.State);
            }

            return Task.CompletedTask;
        });

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, events: events);
        await appExecutor.RunApplicationAsync().DefaultTimeout();

        var container = Assert.Single(kubernetesService.CreatedResources.OfType<Container>());

        // Park the container in a terminal state it will never leave, which is the situation
        // described in https://github.com/microsoft/aspire/issues/18869.
        container.Status = new ContainerStatus { State = ContainerState.FailedToStart };
        kubernetesService.PushResourceModified(container);

        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => observedStates.Contains(ContainerState.FailedToStart),
            "The state change to FailedToStart should be reported.");

        // Re-establishing a watch replays every existing object, over and over for as long as the
        // AppHost runs. None of those replays describe a change.
        kubernetesService.SimulateWatchRestart();
        kubernetesService.SimulateWatchRestart();

        // A real change afterwards acts as a barrier: the container watch is processed in order, so
        // once Exited is observed the replays above have already been handled or dropped.
        container.Status = new ContainerStatus { State = ContainerState.Exited };
        kubernetesService.PushResourceModified(container);

        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => observedStates.Contains(ContainerState.Exited),
            "The state change to Exited should be reported.");

        AssertStatesReportedAfter(observedStates, ContainerState.FailedToStart, ContainerState.Exited);
    }

    [Fact]
    public async Task ResourceWatch_FailedToStartLogsAreRetriedForEachChangedTerminalNotification()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        builder.AddContainer("database", "image");

        var flushStreamOpens = 0;
        var kubernetesService = new TestKubernetesService(startStreamWithFollow: (obj, logStreamType, follow) =>
        {
            if (obj is Container { Status.State: ContainerState.FailedToStart } &&
                logStreamType == Logs.StreamTypeSystem &&
                follow == false)
            {
                Interlocked.Increment(ref flushStreamOpens);
            }

            return new MemoryStream();
        });

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resourceLoggerService = new ResourceLoggerService();

        string? dcpResourceName = null;
        var flushesAtNotification = new ConcurrentQueue<int>();

        var events = new DcpExecutorEvents();
        events.Subscribe<OnResourceChangedContext>(context =>
        {
            // The flush completes before the resource change is published, so the count read here
            // includes any flush performed for this notification.
            if (context.DcpResourceName == dcpResourceName && context.Status.State == ContainerState.FailedToStart)
            {
                flushesAtNotification.Enqueue(Volatile.Read(ref flushStreamOpens));
            }

            return Task.CompletedTask;
        });

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, resourceLoggerService: resourceLoggerService, events: events);
        await appExecutor.RunApplicationAsync().DefaultTimeout();

        var container = Assert.Single(kubernetesService.CreatedResources.OfType<Container>());
        dcpResourceName = container.Metadata.Name;

        using var subscription = resourceLoggerService.Subscribe(dcpResourceName, _ => { });

        container.Status = new ContainerStatus { State = ContainerState.FailedToStart };
        kubernetesService.PushResourceModified(container);

        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => !flushesAtNotification.IsEmpty,
            "The terminal state should be reported.");

        // FailedToStart uses a point-in-time snapshot because there is no process stream that can
        // complete. A later changed notification must retry in case DCP drained more logs meanwhile.
        container.Status = new ContainerStatus { State = ContainerState.FailedToStart, ExitCode = 1 };
        kubernetesService.PushResourceModified(container);

        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => flushesAtNotification.Count >= 2,
            "The follow-up change should be reported.");

        Assert.Equal([1, 2], flushesAtNotification.ToArray());
    }

    [Fact]
    public async Task ResourceWatch_TerminalLogsAreFlushedOnlyOncePerTerminalPeriod()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        builder.AddContainer("database", "image");

        var normalStreamStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var flushStreamOpens = 0;
        var kubernetesService = new TestKubernetesService(startStreamWithFollow: (obj, logStreamType, follow) =>
        {
            if (obj is Container container &&
                container.Status?.State != ContainerState.Exited &&
                logStreamType == Logs.StreamTypeSystem &&
                follow == true)
            {
                normalStreamStarted.TrySetResult();
            }

            if (obj is Container { Status.State: ContainerState.Exited } &&
                logStreamType == Logs.StreamTypeSystem &&
                follow == true)
            {
                Interlocked.Increment(ref flushStreamOpens);
            }

            return new MemoryStream();
        });

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resourceLoggerService = new ResourceLoggerService();

        string? dcpResourceName = null;
        var flushesAtNotification = new ConcurrentQueue<int>();
        var runningNotifications = 0;

        var events = new DcpExecutorEvents();
        events.Subscribe<OnResourceChangedContext>(context =>
        {
            if (context.DcpResourceName == dcpResourceName)
            {
                if (context.Status.State == ContainerState.Exited)
                {
                    flushesAtNotification.Enqueue(Volatile.Read(ref flushStreamOpens));
                }
                else if (context.Status.State == ContainerState.Running)
                {
                    Interlocked.Increment(ref runningNotifications);
                }
            }

            return Task.CompletedTask;
        });

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, resourceLoggerService: resourceLoggerService, events: events);
        await appExecutor.RunApplicationAsync().DefaultTimeout();

        var container = Assert.Single(kubernetesService.CreatedResources.OfType<Container>());
        dcpResourceName = container.Metadata.Name;

        using var subscription = resourceLoggerService.Subscribe(dcpResourceName, _ => { });
        container.Status = new ContainerStatus { State = ContainerState.Running };
        kubernetesService.PushResourceModified(container);
        await normalStreamStarted.Task.DefaultTimeout();

        container.Status = new ContainerStatus { State = ContainerState.Exited };
        kubernetesService.PushResourceModified(container);

        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => !flushesAtNotification.IsEmpty,
            "The first terminal state should be reported.");

        container.Status = new ContainerStatus { State = ContainerState.Exited, ExitCode = 1 };
        kubernetesService.PushResourceModified(container);

        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => flushesAtNotification.Count >= 2,
            "The changed terminal state should be reported.");

        container.Status = new ContainerStatus { State = ContainerState.Running };
        kubernetesService.PushResourceModified(container);
        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => Volatile.Read(ref runningNotifications) >= 2,
            "The resource restart should be reported.");

        container.Status = new ContainerStatus { State = ContainerState.Exited, ExitCode = 2 };
        kubernetesService.PushResourceModified(container);

        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => flushesAtNotification.Count >= 3,
            "The terminal state after restart should be reported.");

        Assert.Equal([1, 1, 2], flushesAtNotification.ToArray());
    }

    /// <summary>
    /// Asserts that the only states reported after the first occurrence of <paramref name="afterState"/>
    /// are <paramref name="expectedStates"/>.
    /// </summary>
    /// <remarks>
    /// Anchoring on a state instead of clearing the queue keeps the assertion independent of the
    /// notifications raised while the app was starting, without racing against the watcher. The queue
    /// must not be cleared here: <see cref="ConcurrentQueue{T}.Clear"/> is not safe to call while
    /// another thread is still enqueuing.
    /// </remarks>
    private static void AssertStatesReportedAfter(ConcurrentQueue<string?> observedStates, string afterState, params string?[] expectedStates)
    {
        var states = observedStates.ToArray();
        var anchor = Array.IndexOf(states, afterState);

        Assert.True(anchor >= 0, $"Expected '{afterState}' to be reported but saw: {string.Join(", ", states)}");
        Assert.Equal(expectedStates, states.Skip(anchor + 1));
    }

    [Fact]
    public async Task ResourceLogging_SystemStream_FormatsWithSysPrefix()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        builder.AddContainer("database", "image");

        var kubernetesService = new TestKubernetesService(startStream: (obj, logStreamType) =>
        {
            switch (logStreamType)
            {
                case Logs.StreamTypeStdOut:
                    return new MemoryStream();
                case Logs.StreamTypeStdErr:
                    return new MemoryStream();
                case Logs.StreamTypeStartupStdOut:
                    return new MemoryStream();
                case Logs.StreamTypeStartupStdErr:
                    return new MemoryStream();
                case Logs.StreamTypeSystem:
                    // Simulate real DCP system log format with JSON metadata
                    var systemLogs =
                        "2024-08-19T06:10:01.000Z\tinfo\tdcp.ExecutableReconciler\tStarting process...\t{\"Executable\": \"/foo-pwrqgpew\", \"Reconciliation\": 4, \"Cmd\": \"bla\", \"Args\": []}" + Environment.NewLine +
                        "2024-08-19T06:10:02.000Z\terror\tdcp.ExecutableReconciler\tFailed to start process\t{\"Executable\": \"/foo-pwrqgpew\", \"Reconciliation\": 4, \"Cmd\": \"bla\", \"Args\": [], \"error\": \"exec: \\\"bla\\\": executable file not found in $PATH\"}" + Environment.NewLine;
                    return new MemoryStream(Encoding.UTF8.GetBytes(systemLogs));
                default:
                    throw new InvalidOperationException("Unexpected type: " + logStreamType);
            }
        });

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var dcpOptions = new DcpOptions { DashboardPath = "./dashboard" };
        var resourceLoggerService = new ResourceLoggerService();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, dcpOptions: dcpOptions, resourceLoggerService: resourceLoggerService);
        await appExecutor.RunApplicationAsync();

        var exeResource = Assert.Single(kubernetesService.CreatedResources.OfType<Container>());

        // Start watching logs for container
        var watchSubscribers = resourceLoggerService.WatchAnySubscribersAsync();
        var watchSubscribersEnumerator = watchSubscribers.GetAsyncEnumerator();
        var watchLogs = resourceLoggerService.WatchAsync(exeResource.Metadata.Name);
        // Wait for at least 3 logs (there might be additional logs like certificate authority messages)
        var watchLogsTask = ConsoleLoggingTestHelpers.WatchForLogsAsync(watchLogs, targetLogCount: 3);

        await watchSubscribersEnumerator.MoveNextAsync();
        Assert.Equal(exeResource.Metadata.Name, watchSubscribersEnumerator.Current.Name);
        Assert.True(watchSubscribersEnumerator.Current.AnySubscribers);

        exeResource.Status = new ContainerStatus { State = ContainerState.Running };
        kubernetesService.PushResourceModified(exeResource);

        var watchLogsResults = await watchLogsTask;
        Assert.True(watchLogsResults.Count >= 2, $"Expected at least 2 log entries, got {watchLogsResults.Count}");

        // Verify the system logs are formatted with [sys] prefix and proper formatting
        Assert.Contains(watchLogsResults, l => l.Content.Contains("[sys] Starting process...: Cmd = bla, Args = []"));
        Assert.Contains(watchLogsResults, l => l.Content.Contains("[sys] Failed to start process: Cmd = bla, Args = [], Error = exec: \"bla\": executable file not found in $PATH"));
        Assert.Contains(watchLogsResults, l => l.Content.Contains("2024-08-19T06:10:01.0000000Z [sys] Starting process...: Cmd = bla, Args = []"));
        Assert.Contains(watchLogsResults, l => l.Content.Contains("2024-08-19T06:10:02.0000000Z [sys] Failed to start process: Cmd = bla, Args = [], Error = exec: \"bla\": executable file not found in $PATH"));
    }

    [Fact]
    public async Task ResourceLogging_CarriageReturnProgressOutput_NormalizesOverwrittenLines()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        builder.AddContainer("database", "image");

        var kubernetesService = new TestKubernetesService(startStream: (obj, logStreamType) =>
        {
            switch (logStreamType)
            {
                case Logs.StreamTypeStdOut:
                    var stdout =
                        "2024-08-19T06:10:01.000Z   0%\r 50%\r100%" + Environment.NewLine +
                        "2024-08-19T06:10:02.000Z Windows line" + "\r\n" +
                        "2024-08-19T06:10:03.000Z Done" + Environment.NewLine;
                    return new MemoryStream(Encoding.UTF8.GetBytes(stdout));
                case Logs.StreamTypeStdErr:
                case Logs.StreamTypeStartupStdOut:
                case Logs.StreamTypeStartupStdErr:
                case Logs.StreamTypeSystem:
                    return new MemoryStream();
                default:
                    throw new InvalidOperationException("Unexpected type: " + logStreamType);
            }
        });
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var dcpOptions = new DcpOptions { DashboardPath = "./dashboard" };
        var resourceLoggerService = new ResourceLoggerService();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, dcpOptions: dcpOptions, resourceLoggerService: resourceLoggerService);
        await appExecutor.RunApplicationAsync();

        var exeResource = Assert.Single(kubernetesService.CreatedResources.OfType<Container>());

        var watchSubscribers = resourceLoggerService.WatchAnySubscribersAsync();
        var watchSubscribersEnumerator = watchSubscribers.GetAsyncEnumerator();
        var watchLogs = resourceLoggerService.WatchAsync(exeResource.Metadata.Name);
        // Wait for all three stdout records plus the certificate authority message, which can arrive first in CI.
        var watchLogsTask = ConsoleLoggingTestHelpers.WatchForLogsAsync(watchLogs, targetLogCount: 4);

        await watchSubscribersEnumerator.MoveNextAsync();
        Assert.Equal(exeResource.Metadata.Name, watchSubscribersEnumerator.Current.Name);
        Assert.True(watchSubscribersEnumerator.Current.AnySubscribers);

        exeResource.Status = new ContainerStatus { State = ContainerState.Running };
        kubernetesService.PushResourceModified(exeResource);

        var watchLogsResults = await watchLogsTask;

        Assert.Contains(watchLogsResults, l => l.Content.Contains("2024-08-19T06:10:01.0000000Z 100%"));
        Assert.Contains(watchLogsResults, l => l.Content.Contains("2024-08-19T06:10:02.0000000Z Windows line"));
        Assert.Contains(watchLogsResults, l => l.Content.Contains("2024-08-19T06:10:03.0000000Z Done"));
        Assert.DoesNotContain(watchLogsResults, l => l.Content.Contains("  0%"));
        Assert.DoesNotContain(watchLogsResults, l => l.Content.Contains("50%"));
    }

    [Fact]
    public async Task ResourceLogging_SystemStreamWithCarriageReturnInMessage_ParsesCorrectly()
    {
        // Regression test: NormalizeCarriageReturns must not be applied to the full DCP raw line
        // before parsing; doing so would corrupt the tab-delimited structure and cause the parser
        // to fail, dropping the [sys] prefix and timestamp.
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        builder.AddContainer("database", "image");

        var kubernetesService = new TestKubernetesService(startStream: (obj, logStreamType) =>
        {
            switch (logStreamType)
            {
                case Logs.StreamTypeStdOut:
                case Logs.StreamTypeStdErr:
                case Logs.StreamTypeStartupStdOut:
                case Logs.StreamTypeStartupStdErr:
                    return new MemoryStream();
                case Logs.StreamTypeSystem:
                    // A DCP log line whose message content contains \r (e.g. a progress-style
                    // overwrite inside a system log message).  The tab-delimited header must
                    // be parsed first so the \r normalization only applies to the message part.
                    var systemLogs =
                        "2024-08-19T06:10:01.000Z\tinfo\tdcp.ExecutableReconciler\tfirst\rsecond\rthird" + Environment.NewLine;
                    return new MemoryStream(Encoding.UTF8.GetBytes(systemLogs));
                default:
                    throw new InvalidOperationException("Unexpected type: " + logStreamType);
            }
        });

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var dcpOptions = new DcpOptions { DashboardPath = "./dashboard" };
        var resourceLoggerService = new ResourceLoggerService();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, dcpOptions: dcpOptions, resourceLoggerService: resourceLoggerService);
        await appExecutor.RunApplicationAsync();

        var exeResource = Assert.Single(kubernetesService.CreatedResources.OfType<Container>());

        var watchSubscribers = resourceLoggerService.WatchAnySubscribersAsync();
        var watchSubscribersEnumerator = watchSubscribers.GetAsyncEnumerator();
        var watchLogs = resourceLoggerService.WatchAsync(exeResource.Metadata.Name);
        var watchLogsTask = ConsoleLoggingTestHelpers.WatchForLogsAsync(watchLogs, targetLogCount: 2); // 1 DCP system log + 1 certificate authority message

        await watchSubscribersEnumerator.MoveNextAsync();
        Assert.Equal(exeResource.Metadata.Name, watchSubscribersEnumerator.Current.Name);
        Assert.True(watchSubscribersEnumerator.Current.AnySubscribers);

        exeResource.Status = new ContainerStatus { State = ContainerState.Running };
        kubernetesService.PushResourceModified(exeResource);

        var watchLogsResults = await watchLogsTask;

        // The entry should be parsed as a DCP [sys] log (not plain stdout) with the
        // last \r-overwritten segment ("third") preserved as the message content.
        Assert.Contains(watchLogsResults, l => l.Content.Contains("[sys] third"));
        Assert.DoesNotContain(watchLogsResults, l => l.Content.Contains("first"));
    }

    private sealed class LogStreamPipes
    {
        public Pipe StandardOut { get; set; } = default!;
        public Pipe StandardErr { get; set; } = default!;
        public Pipe StartupOut { get; set; } = default!;
        public Pipe StartupErr { get; set; } = default!;
        public Pipe System { get; set; } = default!;
    }

    private static async Task<LogStreamPipes> GetStreamPipesAsync(Channel<(string Type, Pipe Pipe)> logStreamPipesChannel)
    {
        var pipeCount = 0;
        var result = new LogStreamPipes();

        await foreach (var item in logStreamPipesChannel.Reader.ReadAllAsync())
        {
            switch (item.Type)
            {
                case Logs.StreamTypeStdOut:
                    result.StandardOut = item.Pipe;
                    break;
                case Logs.StreamTypeStdErr:
                    result.StandardErr = item.Pipe;
                    break;
                case Logs.StreamTypeStartupStdOut:
                    result.StartupOut = item.Pipe;
                    break;
                case Logs.StreamTypeStartupStdErr:
                    result.StartupErr = item.Pipe;
                    break;
                case Logs.StreamTypeSystem:
                    result.System = item.Pipe;
                    break;
                default:
                    throw new InvalidOperationException("Unexpected type: " + item.Type);
            }

            pipeCount++;
            if (pipeCount == 5)
            {
                logStreamPipesChannel.Writer.Complete();
            }
        }

        return result;
    }

    [Fact]
    public async Task EndpointPortsProjectNoPortNoTargetPort()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        builder.AddProject<Projects.ServiceA>("ServiceA")
            .WithEndpoint(name: "NoPortNoTargetPort", env: "NO_PORT_NO_TARGET_PORT", isProxied: true)
            .WithHttpEndpoint(name: "hp1", port: 5001)
            .WithHttpEndpoint(name: "dontinjectme", port: 5002)
            .WithEndpointsInEnvironment(e => e.Name != "dontinjectme")
            .WithReplicas(3);

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);
        await appExecutor.RunApplicationAsync();

        var exes = GetCreatedExecutablesForResource(kubernetesService, "ServiceA");
        Assert.Equal(3, exes.Count);
        var targetPorts = new HashSet<int>();

        foreach (var dcpExe in exes)
        {
            Assert.True(dcpExe.TryGetAnnotationAsObjectList<ServiceProducerAnnotation>(CustomResource.ServiceProducerAnnotation, out var spAnnList));

            // Neither Port, nor TargetPort are set
            // Clients use proxy, MAY have the proxy port injected.
            // Proxy gets autogenerated port.
            // Aspire assigns each replica a different non-ephemeral port that DCP injects via env var/startup param.
            var svc = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "ServiceA-NoPortNoTargetPort");
            Assert.Equal(AddressAllocationModes.Localhost, svc.Spec.AddressAllocationMode);
            Assert.True(svc.Status?.EffectivePort >= TestKubernetesService.StartOfAutoPortRange);
            var targetPort = Assert.IsType<int>(spAnnList.Single(ann => ann.ServiceName == "ServiceA-NoPortNoTargetPort").Port);
            AssertPortAllocatedFromProxylessEndpointAllocatorRange(targetPort);
            Assert.True(targetPorts.Add(targetPort));
            var envVarVal = dcpExe.Spec.Env?.Single(v => v.Name == "NO_PORT_NO_TARGET_PORT").Value;
            Assert.False(string.IsNullOrWhiteSpace(envVarVal));
            Assert.Contains("""portForServing "ServiceA-NoPortNoTargetPort" """, envVarVal);

            // ASPNETCORE_URLS should not include dontinjectme, as it was excluded using WithEndpointsInEnvironment
            var aspnetCoreUrls = dcpExe.Spec.Env?.Single(v => v.Name == KnownAspNetCoreConfigNames.Urls).Value;
            Assert.Equal("http://localhost:{{- portForServing \"ServiceA-http\" -}};http://localhost:{{- portForServing \"ServiceA-hp1\" -}}", aspnetCoreUrls);
        }
    }

    [Fact]
    public async Task EndpointPortsProjectPortSetNoTargetPort()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        const int desiredPortOne = TestKubernetesService.StartOfAutoPortRange - 1000;
        builder.AddProject<Projects.ServiceA>("ServiceA")
            .WithEndpoint(name: "PortSetNoTargetPort", port: desiredPortOne, env: "PORT_SET_NO_TARGET_PORT")
            .WithReplicas(3);

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);
        await appExecutor.RunApplicationAsync();

        var exes = GetCreatedExecutablesForResource(kubernetesService, "ServiceA");
        Assert.Equal(3, exes.Count);
        var targetPorts = new HashSet<int>();

        foreach (var dcpExe in exes)
        {
            Assert.True(dcpExe.TryGetAnnotationAsObjectList<ServiceProducerAnnotation>(CustomResource.ServiceProducerAnnotation, out var spAnnList));

            // Port is set, but TargetPort is empty.
            // Clients use proxy, MAY have the proxy port injected.
            // Proxy uses Port.
            // Aspire assigns each replica a different non-ephemeral port that DCP injects via env var/startup param.
            var svc = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "ServiceA-PortSetNoTargetPort");
            Assert.Equal(AddressAllocationModes.Localhost, svc.Spec.AddressAllocationMode);
            Assert.Equal(desiredPortOne, svc.Status?.EffectivePort);
            var targetPort = Assert.IsType<int>(spAnnList.Single(ann => ann.ServiceName == "ServiceA-PortSetNoTargetPort").Port);
            AssertPortAllocatedFromProxylessEndpointAllocatorRange(targetPort);
            Assert.True(targetPorts.Add(targetPort));
            var envVarVal = dcpExe.Spec.Env?.Single(v => v.Name == "PORT_SET_NO_TARGET_PORT").Value;
            Assert.False(string.IsNullOrWhiteSpace(envVarVal));
            Assert.Contains("""portForServing "ServiceA-PortSetNoTargetPort" """, envVarVal);
        }
    }

    [Fact]
    public async Task EndpointPortsProjectWithEndpointProxySupportUsesProxylessEndpoint()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        const int desiredPort = TestKubernetesService.StartOfAutoPortRange - 1001;
        builder.AddProject<Projects.ServiceA>("ServiceA", launchProfileName: null)
            .WithHttpEndpoint(name: "stable", port: desiredPort)
            .WithEndpointProxySupport(false);

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);
        await appExecutor.RunApplicationAsync();

        var dcpExe = GetCreatedExecutableForResource(kubernetesService, "ServiceA");
        Assert.True(dcpExe.TryGetAnnotationAsObjectList<ServiceProducerAnnotation>(CustomResource.ServiceProducerAnnotation, out var spAnnList));

        var svc = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "ServiceA");
        Assert.Equal(AddressAllocationModes.Proxyless, svc.Spec.AddressAllocationMode);
        Assert.Equal(desiredPort, svc.Status?.EffectivePort);
        Assert.Equal(desiredPort, spAnnList.Single(ann => ann.ServiceName == "ServiceA").Port);

        var aspnetCoreUrls = dcpExe.Spec.Env?.Single(v => v.Name == KnownAspNetCoreConfigNames.Urls).Value;
        Assert.Equal($"http://localhost:{desiredPort}", aspnetCoreUrls);
    }

    [Fact]
    public async Task EndpointPortsPersistentProjectDefaultsToProxylessEndpoint()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        const int desiredPort = TestKubernetesService.StartOfAutoPortRange - 1002;
        builder.AddProject<Projects.ServiceA>("ServiceA", launchProfileName: null)
            .WithPersistentLifetime()
            .WithHttpEndpoint(name: "stable", port: desiredPort);

        var configDict = new Dictionary<string, string?>
        {
            ["AppHost:Sha256"] = "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);
        await appExecutor.RunApplicationAsync();

        var dcpExe = GetCreatedExecutableForResource(kubernetesService, "ServiceA");
        Assert.True(dcpExe.TryGetAnnotationAsObjectList<ServiceProducerAnnotation>(CustomResource.ServiceProducerAnnotation, out var spAnnList));

        var svc = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "ServiceA");
        Assert.Equal(AddressAllocationModes.Proxyless, svc.Spec.AddressAllocationMode);
        Assert.Equal(desiredPort, svc.Status?.EffectivePort);
        Assert.Equal(desiredPort, spAnnList.Single(ann => ann.ServiceName == "ServiceA").Port);

        var aspnetCoreUrls = dcpExe.Spec.Env?.Single(v => v.Name == KnownAspNetCoreConfigNames.Urls).Value;
        Assert.Equal($"http://localhost:{desiredPort}", aspnetCoreUrls);
    }

    [Fact]
    public async Task EndpointPortsPersistentProjectDefaultsToProxiedEndpointWhenPortsAreRandomized()
    {
        var (allocatedTargetPort, _) = GetAvailableConsecutivePortPair();
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        const int desiredPort = TestKubernetesService.StartOfAutoPortRange - 1002;
        builder.AddProject<Projects.ServiceA>("ServiceA", launchProfileName: null)
            .WithPersistentLifetime()
            .WithHttpEndpoint(name: "stable", port: desiredPort);

        var configDict = new Dictionary<string, string?>
        {
            ["AppHost:Sha256"] = "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var dcpOptions = new DcpOptions
        {
            DashboardPath = "./dashboard",
            RandomizePorts = true,
            ProxylessEndpointPortRangeStart = allocatedTargetPort,
            ProxylessEndpointPortRangeEnd = allocatedTargetPort
        };
        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, dcpOptions: dcpOptions, configuration: configuration);
        await appExecutor.RunApplicationAsync();

        var dcpExe = GetCreatedExecutableForResource(kubernetesService, "ServiceA");
        Assert.True(dcpExe.TryGetAnnotationAsObjectList<ServiceProducerAnnotation>(CustomResource.ServiceProducerAnnotation, out var spAnnList));

        var svc = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "ServiceA");
        Assert.Equal(AddressAllocationModes.Localhost, svc.Spec.AddressAllocationMode);
        Assert.Null(svc.Spec.Port);
        Assert.True(svc.Status?.EffectivePort >= TestKubernetesService.StartOfAutoPortRange);
        Assert.NotEqual(desiredPort, svc.Status?.EffectivePort);
        Assert.Equal(allocatedTargetPort, spAnnList.Single(ann => ann.ServiceName == "ServiceA").Port);

        var aspnetCoreUrls = dcpExe.Spec.Env?.Single(v => v.Name == KnownAspNetCoreConfigNames.Urls).Value;
        Assert.Contains("""portForServing "ServiceA" """, aspnetCoreUrls);
    }

    [Fact]
    public async Task EndpointPortsConainerProxiedNoPortTargetPortSet()
    {
        var builder = DistributedApplication.CreateBuilder();

        const int desiredTargetPort = TestKubernetesService.StartOfAutoPortRange - 999;
        builder.AddContainer("database", "image")
            .WithEndpoint(name: "NoPortTargetPortSet", targetPort: desiredTargetPort, env: "NO_PORT_TARGET_PORT_SET", isProxied: true);

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);
        await appExecutor.RunApplicationAsync();

        var dcpCtr = Assert.Single(kubernetesService.CreatedResources.OfType<Container>());
        Assert.True(dcpCtr.TryGetAnnotationAsObjectList<ServiceProducerAnnotation>(CustomResource.ServiceProducerAnnotation, out var spAnnList));

        // Port is empty, TargetPort is set
        // Clients use proxy, MAY have the proxy port injected.
        // Proxy gets autogenerated port.
        // Container is using TargetPort inside the container. Container host port is auto-allocated by Docker/Podman.
        var svc = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "database");
        Assert.Equal(AddressAllocationModes.Localhost, svc.Spec.AddressAllocationMode);
        Assert.True(svc.Status?.EffectivePort >= TestKubernetesService.StartOfAutoPortRange);
        Assert.NotNull(dcpCtr.Spec.Ports);
        Assert.Contains(dcpCtr.Spec.Ports!, p => p.HostPort is null && p.ContainerPort == desiredTargetPort);
        // Desired port should be part of the service producer annotation.
        Assert.Equal(desiredTargetPort, spAnnList.Single(ann => ann.ServiceName == "database").Port);
        var envVarVal = dcpCtr.Spec.Env?.Single(v => v.Name == "NO_PORT_TARGET_PORT_SET").Value;
        Assert.False(string.IsNullOrWhiteSpace(envVarVal));
        Assert.Equal(desiredTargetPort, int.Parse(envVarVal, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task EndpointPortsContainerProxiedPortAndTargetPortSet()
    {
        var builder = DistributedApplication.CreateBuilder();

        const int desiredPort = TestKubernetesService.StartOfAutoPortRange - 998;
        const int desiredTargetPort = TestKubernetesService.StartOfAutoPortRange - 997;
        builder.AddContainer("database", "image")
            .WithEndpoint(name: "PortAndTargetPortSet", port: desiredPort, targetPort: desiredTargetPort, env: "PORT_AND_TARGET_PORT_SET", isProxied: true);

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);
        await appExecutor.RunApplicationAsync();

        var dcpCtr = Assert.Single(kubernetesService.CreatedResources.OfType<Container>());
        Assert.True(dcpCtr.TryGetAnnotationAsObjectList<ServiceProducerAnnotation>(CustomResource.ServiceProducerAnnotation, out var spAnnList));

        // Port and TargetPort are set.
        // Clients use proxy, MAY have the proxy port injected.
        // Proxy uses Port.
        // Container is using TargetPort inside the container. Container host port is auto-allocated by Docker/Podman.
        var svc = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "database");
        Assert.Equal(AddressAllocationModes.Localhost, svc.Spec.AddressAllocationMode);
        Assert.Equal(desiredPort, svc.Status?.EffectivePort);
        Assert.NotNull(dcpCtr.Spec.Ports);
        Assert.Contains(dcpCtr.Spec.Ports!, p => p.HostPort is null && p.ContainerPort == desiredTargetPort);
        // Desired port should be part of the service producer annotation.
        Assert.Equal(desiredTargetPort, spAnnList.Single(ann => ann.ServiceName == "database").Port);
        var envVarVal = dcpCtr.Spec.Env?.Single(v => v.Name == "PORT_AND_TARGET_PORT_SET").Value;
        Assert.False(string.IsNullOrWhiteSpace(envVarVal));
        Assert.Equal(desiredTargetPort, int.Parse(envVarVal, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Verifies that applying unsupported endpoint port configuration to Containers results in an error.
    /// </summary>
    [Fact]
    public async Task UnsupportedEndpointPortsContainer()
    {
        const int desiredPortOne = TestKubernetesService.StartOfAutoPortRange - 1000;

        (Action<IResourceBuilder<ContainerResource>> AddEndpoint, string ErrorMessageFragment)[] testcases = [
            // Invalid configuration: TargetPort is empty (and Port too) (proxied).
            (
                cr => cr.WithEndpoint(name: "NoPortNoTargetPortProxied", env: "NO_PORT_NO_TARGET_PORT_PROXIED", isProxied: true),
                "must specify the TargetPort"
            ),

            // Invalid configuration: TargetPort is empty (Port is set but it should not matter) (proxied).
            (
                cr => cr.WithEndpoint(name: "PortSetNoTargetPort", port: desiredPortOne, env: "PORT_SET_NO_TARGET_PORT", isProxied: true),
                "must specify the TargetPort"
            ),

            // Invalid configuration: TargetPort is empty (and Port too) (proxy-less).
            (
                cr => cr.WithEndpoint(name: "NoPortNoTargetPortProxyless", env: "NO_PORT_NO_TARGET_PORT_PROXYLESS", isProxied: false),
                "must specify the TargetPort"
            ),

            // Invalid configuration: Port requests dynamic allocation, but no container target port is available.
            (
                cr => cr.WithEndpoint(name: "ZeroPortNoTargetPortProxyless", port: 0, env: "ZERO_PORT_NO_TARGET_PORT_PROXYLESS", isProxied: false),
                "must specify the TargetPort"
            ),
        ];

        foreach (var tc in testcases)
        {
            var builder = DistributedApplication.CreateBuilder();

            var ctr = builder.AddContainer("database", "image");
            tc.AddEndpoint(ctr);

            var kubernetesService = new TestKubernetesService();
            using var app = builder.Build();
            var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
            var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => appExecutor.RunApplicationAsync());
            Assert.Contains(tc.ErrorMessageFragment, exception.Message);
        }
    }

    [Fact]
    public async Task EndpointPortsContainerProxylessPortSetNoTargetPort()
    {
        var builder = DistributedApplication.CreateBuilder();

        const int desiredPort = TestKubernetesService.StartOfAutoPortRange - 1000;
        builder.AddContainer("database", "image")
            .WithEndpoint(name: "PortSetNoTargetPort", port: desiredPort, env: "PORT_SET_NO_TARGET_PORT", isProxied: false);

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);
        await appExecutor.RunApplicationAsync();

        var dcpCtr = Assert.Single(kubernetesService.CreatedResources.OfType<Container>());
        Assert.True(dcpCtr.TryGetAnnotationAsObjectList<ServiceProducerAnnotation>(CustomResource.ServiceProducerAnnotation, out var spAnnList));

        // Neither Port, nor TargetPort are set.
        // Clients connect directly to the container host port, MAY have the container host port injected.
        // Container is using TargetPort for BOTH listening inside the container and as a host port.
        var svc = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "database");
        Assert.Equal(AddressAllocationModes.Proxyless, svc.Spec.AddressAllocationMode);
        Assert.Equal(desiredPort, svc.Status?.EffectivePort);
        Assert.NotNull(dcpCtr.Spec.Ports);
        Assert.Contains(dcpCtr.Spec.Ports!, p => p.HostPort == desiredPort && p.ContainerPort == desiredPort);
        // Desired port should be part of the service producer annotation.
        Assert.Equal(desiredPort, spAnnList.Single(ann => ann.ServiceName == "database").Port);
        var envVarVal = dcpCtr.Spec.Env?.Single(v => v.Name == "PORT_SET_NO_TARGET_PORT").Value;
        Assert.False(string.IsNullOrWhiteSpace(envVarVal));
        Assert.Equal(desiredPort, int.Parse(envVarVal, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task EndpointPortsContainerProxylessNoPortTargetPortSet()
    {
        var builder = DistributedApplication.CreateBuilder();

        const int desiredTargetPort = TestKubernetesService.StartOfAutoPortRange - 999;
        builder.AddContainer("database", "image")
            .WithEndpoint(name: "NoPortTargetPortSet", targetPort: desiredTargetPort, env: "NO_PORT_TARGET_PORT_SET", isProxied: false);

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);
        await appExecutor.RunApplicationAsync();

        var dcpCtr = Assert.Single(kubernetesService.CreatedResources.OfType<Container>());
        Assert.True(dcpCtr.TryGetAnnotationAsObjectList<ServiceProducerAnnotation>(CustomResource.ServiceProducerAnnotation, out var spAnnList));

        var svc = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "database");
        var allocatedPort = Assert.IsType<int>(svc.Status?.EffectivePort);
        Assert.Equal(AddressAllocationModes.Proxyless, svc.Spec.AddressAllocationMode);
        Assert.Equal(allocatedPort, svc.Spec.Port);
        AssertPortAllocatedFromProxylessEndpointAllocatorRange(allocatedPort);
        Assert.NotNull(dcpCtr.Spec.Ports);
        Assert.Contains(dcpCtr.Spec.Ports!, p => p.HostPort == allocatedPort && p.ContainerPort == desiredTargetPort);
        // Desired port should be part of the service producer annotation.
        Assert.Equal(desiredTargetPort, spAnnList.Single(ann => ann.ServiceName == "database").Port);
        var envVarVal = dcpCtr.Spec.Env?.Single(v => v.Name == "NO_PORT_TARGET_PORT_SET").Value;
        Assert.False(string.IsNullOrWhiteSpace(envVarVal));
        Assert.Equal(desiredTargetPort, int.Parse(envVarVal, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task EndpointPortsContainerProxylessNoPortTargetPortSetPublishesAllocatedEndpoint()
    {
        var builder = DistributedApplication.CreateBuilder();

        const int desiredTargetPort = TestKubernetesService.StartOfAutoPortRange - 999;
        var database = builder.AddContainer("database", "image")
            .WithEndpoint(name: "NoPortTargetPortSet", targetPort: desiredTargetPort, isProxied: false);

        var allocatedPortChannel = Channel.CreateUnbounded<int>();
        var connectionStringAvailableChannel = Channel.CreateUnbounded<IResource>();
        var observedEvents = new ConcurrentQueue<string>();
        var eventing = new Hosting.Eventing.DistributedApplicationEventing();
        eventing.Subscribe<ResourceEndpointsAllocatedEvent>((@event, ct) =>
        {
            if (@event.Resource.Name == "database")
            {
                observedEvents.Enqueue(nameof(ResourceEndpointsAllocatedEvent));
                var endpoint = ((IResourceWithEndpoints)@event.Resource).GetEndpoint("NoPortTargetPortSet");
                if (endpoint.AllocatedEndpoint is { } allocatedEndpoint)
                {
                    allocatedPortChannel.Writer.TryWrite(allocatedEndpoint.Port);
                }
            }

            return Task.CompletedTask;
        });
        var events = new DcpExecutorEvents();
        events.Subscribe<OnConnectionStringAvailableContext>(context =>
        {
            if (context.Resource.Name == "database")
            {
                observedEvents.Enqueue(nameof(OnConnectionStringAvailableContext));
                connectionStringAvailableChannel.Writer.TryWrite(context.Resource);
            }

            return Task.CompletedTask;
        });
        events.Subscribe<OnResourceStartingContext>(context =>
        {
            if (context.Resource.Name == "database")
            {
                observedEvents.Enqueue(nameof(OnResourceStartingContext));
            }

            return Task.CompletedTask;
        });

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, events: events, distributedApplicationEventing: eventing);
        await appExecutor.RunApplicationAsync();

        var allocatedPort = await allocatedPortChannel.Reader.ReadAsync().AsTask().DefaultTimeout();
        var connectionStringAvailableResource = await connectionStringAvailableChannel.Reader.ReadAsync().AsTask().DefaultTimeout();
        var dcpCtr = Assert.Single(kubernetesService.CreatedResources.OfType<Container>());
        var svc = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "database");

        Assert.Same(database.Resource, connectionStringAvailableResource);
        Assert.NotNull(dcpCtr.Spec.Ports);
        Assert.Contains(dcpCtr.Spec.Ports!, p => p.HostPort == allocatedPort && p.ContainerPort == desiredTargetPort);
        Assert.Equal(allocatedPort, svc.Status?.EffectivePort);
        Assert.Equal(allocatedPort, svc.Spec.Port);
        Assert.NotEqual(desiredTargetPort, allocatedPort);
        AssertPortAllocatedFromProxylessEndpointAllocatorRange(allocatedPort);
        Assert.Equal(allocatedPort.ToString(CultureInfo.InvariantCulture), await database.GetEndpoint("NoPortTargetPortSet").Property(EndpointProperty.Port).GetValueAsync());
        Assert.Collection(
            observedEvents,
            eventName => Assert.Equal(nameof(ResourceEndpointsAllocatedEvent), eventName),
            eventName => Assert.Equal(nameof(OnConnectionStringAvailableContext), eventName),
            eventName => Assert.Equal(nameof(OnResourceStartingContext), eventName));
    }

    [Fact]
    public async Task EndpointPortsContainerProxylessNoPortTargetPortSetAllocatesHostPortAndInjectsTargetPortForContainerSelfReference()
    {
        var builder = DistributedApplication.CreateBuilder();

        const int desiredTargetPort = TestKubernetesService.StartOfAutoPortRange - 999;
        var database = builder.AddContainer("database", "image")
            .WithEndpoint(name: "NoPortTargetPortSet", targetPort: desiredTargetPort, isProxied: false);
        database.WithEnvironment("PUBLIC_PORT", database.GetEndpoint("NoPortTargetPortSet").Property(EndpointProperty.Port));
        database.WithEnvironment("PUBLIC_PORT_AGAIN", database.GetEndpoint("NoPortTargetPortSet").Property(EndpointProperty.Port));

        var connectionStringAvailableChannel = Channel.CreateUnbounded<IResource>();
        var events = new DcpExecutorEvents();
        events.Subscribe<OnConnectionStringAvailableContext>(context =>
        {
            if (context.Resource.Name == "database")
            {
                connectionStringAvailableChannel.Writer.TryWrite(context.Resource);
            }

            return Task.CompletedTask;
        });

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, events: events);
        await appExecutor.RunApplicationAsync();

        var connectionStringAvailableResource = await connectionStringAvailableChannel.Reader.ReadAsync().AsTask().DefaultTimeout();
        var dcpCtr = Assert.Single(kubernetesService.CreatedResources.OfType<Container>());
        var svc = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "database");

        Assert.Same(database.Resource, connectionStringAvailableResource);
        Assert.Equal(AddressAllocationModes.Proxyless, svc.Spec.AddressAllocationMode);
        var allocatedPort = Assert.IsType<int>(svc.Status?.EffectivePort);
        Assert.Equal(allocatedPort, svc.Spec.Port);
        AssertPortAllocatedFromProxylessEndpointAllocatorRange(allocatedPort);
        Assert.NotNull(dcpCtr.Spec.Ports);
        Assert.Contains(dcpCtr.Spec.Ports!, p => p.HostPort == allocatedPort && p.ContainerPort == desiredTargetPort);
        var envVarVal = dcpCtr.Spec.Env?.Single(v => v.Name == "PUBLIC_PORT").Value;
        Assert.False(string.IsNullOrWhiteSpace(envVarVal));
        Assert.Equal(desiredTargetPort, int.Parse(envVarVal, CultureInfo.InvariantCulture));
        var secondEnvVarVal = dcpCtr.Spec.Env?.Single(v => v.Name == "PUBLIC_PORT_AGAIN").Value;
        Assert.False(string.IsNullOrWhiteSpace(secondEnvVarVal));
        Assert.Equal(desiredTargetPort, int.Parse(secondEnvVarVal, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task EndpointPortsContainerProxylessNoPortTargetPortSetAllocatesHostPortAndInjectsTargetHostAndPortForContainerSelfReference()
    {
        var builder = DistributedApplication.CreateBuilder();

        const int desiredTargetPort = TestKubernetesService.StartOfAutoPortRange - 999;
        var database = builder.AddContainer("database", "image")
            .WithEndpoint(name: "NoPortTargetPortSet", targetPort: desiredTargetPort, isProxied: false);
        database.WithEnvironment("PUBLIC_HOST_AND_PORT", database.GetEndpoint("NoPortTargetPortSet").Property(EndpointProperty.HostAndPort));

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);
        await appExecutor.RunApplicationAsync();

        var dcpCtr = Assert.Single(kubernetesService.CreatedResources.OfType<Container>());
        var svc = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "database");

        Assert.Equal(AddressAllocationModes.Proxyless, svc.Spec.AddressAllocationMode);
        var allocatedPort = Assert.IsType<int>(svc.Status?.EffectivePort);
        Assert.Equal(allocatedPort, svc.Spec.Port);
        AssertPortAllocatedFromProxylessEndpointAllocatorRange(allocatedPort);
        Assert.NotNull(dcpCtr.Spec.Ports);
        Assert.Contains(dcpCtr.Spec.Ports!, p => p.HostPort == allocatedPort && p.ContainerPort == desiredTargetPort);
        var envVarVal = dcpCtr.Spec.Env?.Single(v => v.Name == "PUBLIC_HOST_AND_PORT").Value;
        Assert.Equal($"database.dev.internal:{desiredTargetPort}", envVarVal);
    }

    [Fact]
    public async Task EndpointPortsContainerProxylessNoPortTargetPortSetCanBeResolvedWhileDependentResourceIsStarting()
    {
        var builder = DistributedApplication.CreateBuilder();

        const int desiredTargetPort = TestKubernetesService.StartOfAutoPortRange - 999;
        var client = builder.AddContainer("client", "image");
        var database = builder.AddContainer("database", "image")
            .WithEndpoint(name: "NoPortTargetPortSet", scheme: "http", targetPort: desiredTargetPort, isProxied: false)
            .WaitFor(client);

        var resolvedUrlChannel = Channel.CreateUnbounded<string?>();
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var events = new DcpExecutorEvents();
        events.Subscribe<OnResourceStartingContext>(async context =>
        {
            if (ReferenceEquals(context.Resource, client.Resource))
            {
                var url = await database.GetEndpoint("NoPortTargetPortSet").GetValueAsync(new ValueProviderContext
                {
                    Caller = context.Resource,
                    ExecutionContext = executionContext
                }, context.CancellationToken).ConfigureAwait(false);

                resolvedUrlChannel.Writer.TryWrite(url);
            }
        });

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, events: events);
        await appExecutor.RunApplicationAsync();

        var resolvedUrl = await resolvedUrlChannel.Reader.ReadAsync().AsTask().DefaultTimeout();
        var dcpCtr = kubernetesService.CreatedResources.OfType<Container>().Single(c => c.AppModelResourceName == "database");
        var svc = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "database");

        var allocatedPort = Assert.IsType<int>(svc.Status?.EffectivePort);
        Assert.Equal($"http://database.dev.internal:{desiredTargetPort}", resolvedUrl);
        Assert.Equal(AddressAllocationModes.Proxyless, svc.Spec.AddressAllocationMode);
        Assert.Equal(allocatedPort, svc.Spec.Port);
        AssertPortAllocatedFromProxylessEndpointAllocatorRange(allocatedPort);
        Assert.NotNull(dcpCtr.Spec.Ports);
        Assert.Contains(dcpCtr.Spec.Ports!, p => p.HostPort == allocatedPort && p.ContainerPort == desiredTargetPort);
    }

    [Fact]
    public async Task EndpointPortsContainerProxylessNoPortTargetPortSetCanBeResolvedWithoutCallerWhileDependentResourceIsStarting()
    {
        var builder = DistributedApplication.CreateBuilder();

        const int desiredTargetPort = TestKubernetesService.StartOfAutoPortRange - 999;
        var client = builder.AddContainer("client", "image");
        var database = builder.AddContainer("database", "image")
            .WithEndpoint(name: "NoPortTargetPortSet", scheme: "http", targetPort: desiredTargetPort, isProxied: false)
            .WaitFor(client);

        var resolvedUrlChannel = Channel.CreateUnbounded<string?>();
        var events = new DcpExecutorEvents();
        events.Subscribe<OnResourceStartingContext>(async context =>
        {
            if (ReferenceEquals(context.Resource, client.Resource))
            {
                var url = await database.GetEndpoint("NoPortTargetPortSet").GetValueAsync(context.CancellationToken).ConfigureAwait(false);

                resolvedUrlChannel.Writer.TryWrite(url);
            }
        });

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, events: events);
        await appExecutor.RunApplicationAsync();

        var resolvedUrl = await resolvedUrlChannel.Reader.ReadAsync().AsTask().DefaultTimeout();
        var dcpCtr = kubernetesService.CreatedResources.OfType<Container>().Single(c => c.AppModelResourceName == "database");
        var svc = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "database");

        var allocatedPort = Assert.IsType<int>(svc.Status?.EffectivePort);
        Assert.Equal($"http://localhost:{allocatedPort}", resolvedUrl);
        Assert.Equal(AddressAllocationModes.Proxyless, svc.Spec.AddressAllocationMode);
        Assert.Equal(allocatedPort, svc.Spec.Port);
        AssertPortAllocatedFromProxylessEndpointAllocatorRange(allocatedPort);
        Assert.NotNull(dcpCtr.Spec.Ports);
        Assert.Contains(dcpCtr.Spec.Ports!, p => p.HostPort == allocatedPort && p.ContainerPort == desiredTargetPort);
    }

    [Fact]
    public async Task ResourceEndpointsAllocatedEventSubscribersBlockDcpStartup()
    {
        var builder = DistributedApplication.CreateBuilder();

        builder.AddContainer("database", "image")
            .WithHttpEndpoint(targetPort: 8080);

        var subscriberEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSubscriber = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var eventing = new Hosting.Eventing.DistributedApplicationEventing();
        eventing.Subscribe<ResourceEndpointsAllocatedEvent>(async (@event, ct) =>
        {
            if (@event.Resource.Name == "database")
            {
                subscriberEntered.TrySetResult();
                await releaseSubscriber.Task.WaitAsync(ct).ConfigureAwait(false);
            }
        });

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, distributedApplicationEventing: eventing);

        var runTask = appExecutor.RunApplicationAsync();
        await subscriberEntered.Task.DefaultTimeout();

        var startupWasBlocked = !runTask.IsCompleted;
        releaseSubscriber.SetResult();
        await runTask.DefaultTimeout();

        Assert.True(startupWasBlocked);
    }

    [Fact]
    public async Task EndpointPortsContainerProxylessPortAndTargetPortSet()
    {
        var builder = DistributedApplication.CreateBuilder();

        const int desiredPort = TestKubernetesService.StartOfAutoPortRange - 998;
        const int desiredTargetPort = TestKubernetesService.StartOfAutoPortRange - 997;
        builder.AddContainer("database", "image")
            .WithEndpoint(name: "PortAndTargetPortSet", port: desiredPort, targetPort: desiredTargetPort, env: "PORT_AND_TARGET_PORT_SET", isProxied: false);

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);
        await appExecutor.RunApplicationAsync();

        var dcpCtr = Assert.Single(kubernetesService.CreatedResources.OfType<Container>());
        Assert.True(dcpCtr.TryGetAnnotationAsObjectList<ServiceProducerAnnotation>(CustomResource.ServiceProducerAnnotation, out var spAnnList));

        // Port and TargetPort are set.
        // Clients connect directly to the container host port, MAY have the container host port injected.
        // Container is using TargetPort for listening inside the container and the Port as the host port.
        var svc = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "database");
        Assert.Equal(AddressAllocationModes.Proxyless, svc.Spec.AddressAllocationMode);
        Assert.Equal(desiredPort, svc.Status?.EffectivePort);
        Assert.NotNull(dcpCtr.Spec.Ports);
        Assert.Contains(dcpCtr.Spec.Ports!, p => p.HostPort == desiredPort && p.ContainerPort == desiredTargetPort);
        // Desired port should be part of the service producer annotation.
        Assert.Equal(desiredTargetPort, spAnnList.Single(ann => ann.ServiceName == "database").Port);
        var envVarVal = dcpCtr.Spec.Env?.Single(v => v.Name == "PORT_AND_TARGET_PORT_SET").Value;
        Assert.False(string.IsNullOrWhiteSpace(envVarVal));
        Assert.Equal(desiredTargetPort, int.Parse(envVarVal, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task EndpointPortsContainerWithEndpointProxySupportOverridesExplicitProxiedEndpoint()
    {
        var builder = DistributedApplication.CreateBuilder();

        const int desiredPort = TestKubernetesService.StartOfAutoPortRange - 998;
        const int desiredTargetPort = TestKubernetesService.StartOfAutoPortRange - 997;
        builder.AddContainer("database", "image")
            .WithEndpoint(name: "PortAndTargetPortSet", port: desiredPort, targetPort: desiredTargetPort, env: "PORT_AND_TARGET_PORT_SET", isProxied: true)
            .WithEndpointProxySupport(false);

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);
        await appExecutor.RunApplicationAsync();

        var dcpCtr = Assert.Single(kubernetesService.CreatedResources.OfType<Container>());
        Assert.True(dcpCtr.TryGetAnnotationAsObjectList<ServiceProducerAnnotation>(CustomResource.ServiceProducerAnnotation, out var spAnnList));

        var svc = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "database");
        Assert.Equal(AddressAllocationModes.Proxyless, svc.Spec.AddressAllocationMode);
        Assert.Equal(desiredPort, svc.Status?.EffectivePort);
        Assert.NotNull(dcpCtr.Spec.Ports);
        Assert.Contains(dcpCtr.Spec.Ports!, p => p.HostPort == desiredPort && p.ContainerPort == desiredTargetPort);
        Assert.Equal(desiredTargetPort, spAnnList.Single(ann => ann.ServiceName == "database").Port);

        var envVarVal = dcpCtr.Spec.Env?.Single(v => v.Name == "PORT_AND_TARGET_PORT_SET").Value;
        Assert.False(string.IsNullOrWhiteSpace(envVarVal));
        Assert.Equal(desiredTargetPort, int.Parse(envVarVal, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task EndpointPortsContainerProxylessProtocolSet()
    {
        var builder = DistributedApplication.CreateBuilder();

        const int desiredPort = TestKubernetesService.StartOfAutoPortRange - 998;
        const int desiredTargetPort = TestKubernetesService.StartOfAutoPortRange - 997;
        builder.AddContainer("database", "image")
            .WithEndpoint(name: "PortAndProtocolSet", port: desiredPort, targetPort: desiredTargetPort, env: "PORT_AND_PROTOCOL_SET", isProxied: false, protocol: System.Net.Sockets.ProtocolType.Udp);

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);
        await appExecutor.RunApplicationAsync();

        var dcpCtr = Assert.Single(kubernetesService.CreatedResources.OfType<Container>());
        Assert.True(dcpCtr.TryGetAnnotationAsObjectList<ServiceProducerAnnotation>(CustomResource.ServiceProducerAnnotation, out var spAnnList));

        // Port and TargetPort are set.
        // Clients connect directly to the container host port, MAY have the container host port injected.
        // Container is using TargetPort for listening inside the container and the Port as the host port.
        var svc = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "database");
        Assert.Equal(AddressAllocationModes.Proxyless, svc.Spec.AddressAllocationMode);
        Assert.Equal(desiredPort, svc.Status?.EffectivePort);
        Assert.NotNull(dcpCtr.Spec.Ports);
        Assert.Contains(dcpCtr.Spec.Ports!, p => p.HostPort == desiredPort && p.ContainerPort == desiredTargetPort && p.Protocol == "UDP");
        // Desired port should be part of the service producer annotation.
        Assert.Equal(desiredTargetPort, spAnnList.Single(ann => ann.ServiceName == "database").Port);
        var envVarVal = dcpCtr.Spec.Env?.Single(v => v.Name == "PORT_AND_PROTOCOL_SET").Value;
        Assert.False(string.IsNullOrWhiteSpace(envVarVal));
        Assert.Equal(desiredTargetPort, int.Parse(envVarVal, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task ErrorIfResourceNotDeletedBeforeRestart()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.AddContainer("database", "image");

        var kubernetesService = new TestKubernetesService(ignoreDeletes: true);
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var dcpEvents = new DcpExecutorEvents();
        var tcs = new TaskCompletionSource<OnResourceFailedToStartContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        dcpEvents.Subscribe<OnResourceFailedToStartContext>(c =>
        {
            tcs.SetResult(c);
            return Task.CompletedTask;
        });

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, events: dcpEvents);

        // Set a custom pipeline without retries or delays to avoid waiting.
        appExecutor.DeleteResourceRetryPipeline = new ResiliencePipelineBuilder<bool>().Build();

        await appExecutor.RunApplicationAsync();

        var dcpCtr = Assert.Single(kubernetesService.CreatedResources.OfType<Container>());

        var resourceReference = appExecutor.GetResource(dcpCtr.Metadata.Name);

        var ex = await Assert.ThrowsAsync<DistributedApplicationException>(async () => await appExecutor.StartResourceAsync(resourceReference, CancellationToken.None));
        Assert.Equal($"Failed to delete '{dcpCtr.Metadata.Name}' successfully before restart.", ex.Message);

        var failedContext = await tcs.Task.DefaultTimeout();
        Assert.Equal(ex.Message, failedContext.ErrorMessage);
    }

    [Fact]
    public async Task AddsDefaultsCommandsToResources()
    {
        var builder = DistributedApplication.CreateBuilder();
        var container = builder.AddContainer("database", "image");
        var exe = builder.AddExecutable("node", "node.exe", ".");
        var project = builder.AddProject<TestProject>("project");

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);
        await appExecutor.RunApplicationAsync();

        HasKnownCommandAnnotations(exe.Resource);
        HasKnownCommandAnnotations(container.Resource);
        HasKnownProjectCommandAnnotations(project.Resource);
    }

    [Fact]
    public async Task ContainersArePassedExpectedImagePullPolicy()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        builder.AddContainer("ImplicitDefault", "container");
        builder.AddContainer("ExplicitDefault", "container").WithImagePullPolicy(ImagePullPolicy.Default);
        builder.AddContainer("ExplicitAlways", "container").WithImagePullPolicy(ImagePullPolicy.Always);
        builder.AddContainer("ExplicitMissing", "container").WithImagePullPolicy(ImagePullPolicy.Missing);
        builder.AddContainer("ExplicitNever", "container").WithImagePullPolicy(ImagePullPolicy.Never);

        var kubernetesService = new TestKubernetesService();

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);

        // Act
        await appExecutor.RunApplicationAsync();

        // Assert
        Assert.Equal(5, kubernetesService.CreatedResources.OfType<Container>().Count());
        var implicitDefaultContainer = Assert.Single(kubernetesService.CreatedResources.OfType<Container>(), c => c.AppModelResourceName == "ImplicitDefault");
        Assert.Null(implicitDefaultContainer.Spec.PullPolicy);

        var explicitDefaultContainer = Assert.Single(kubernetesService.CreatedResources.OfType<Container>(), c => c.AppModelResourceName == "ExplicitDefault");
        Assert.Null(explicitDefaultContainer.Spec.PullPolicy);

        var explicitAlwaysContainer = Assert.Single(kubernetesService.CreatedResources.OfType<Container>(), c => c.AppModelResourceName == "ExplicitAlways");
        Assert.Equal(ContainerPullPolicy.Always, explicitAlwaysContainer.Spec.PullPolicy);

        var explicitMissingContainer = Assert.Single(kubernetesService.CreatedResources.OfType<Container>(), c => c.AppModelResourceName == "ExplicitMissing");
        Assert.Equal(ContainerPullPolicy.Missing, explicitMissingContainer.Spec.PullPolicy);

        var explicitNeverContainer = Assert.Single(kubernetesService.CreatedResources.OfType<Container>(), c => c.AppModelResourceName == "ExplicitNever");
        Assert.Equal(ContainerPullPolicy.Never, explicitNeverContainer.Spec.PullPolicy);
    }

    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("[::1]", "[::1]")]
    [InlineData("localhost", "localhost")]
    [InlineData("0.0.0.0", "localhost")]
    [InlineData("[::]", "localhost")]
    [InlineData("machine-name", "localhost")]
    [InlineData("10.0.0.1", "10.0.0.1")]
    public async Task ServiceProducerHasCorrectAddress(string bindingAddress, string serviceAddress)
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        builder.AddContainer("CustomName", "container")
            .WithHttpEndpoint(port: 5000, targetPort: 5000, name: "customendpoint")
            .WithEndpoint("customendpoint", (endpoint) =>
            {
                endpoint.TargetHost = bindingAddress;
            });

        var kubernetesService = new TestKubernetesService();

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);

        // Act
        await appExecutor.RunApplicationAsync();

        // Assert
        var container = Assert.Single(kubernetesService.CreatedResources.OfType<Container>());
        var annotations = container.Metadata.EnsureAnnotations();
        var serviceProducers = JsonSerializer.Deserialize<List<ServiceProducerAnnotation>>(annotations[CustomResource.ServiceProducerAnnotation]);
        Assert.NotNull(serviceProducers);
        var serviceProducer = Assert.Single(serviceProducers);
        Assert.Equal(serviceAddress, serviceProducer.Address);
    }

    [Fact]
    public async Task ProjectLaunchConfiguration_Populated_WhenLaunchProfileSpecified_InDebugSession()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        builder.AddProject<Projects.ServiceA>("proj", launchProfileName: "http");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var kubernetes = new TestKubernetesService();
        var configBuilder = new ConfigurationBuilder();
        configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [KnownConfigNames.DashboardOtlpGrpcEndpointUrl] = "http://localhost",
            ["AppHost:BrowserToken"] = "token",
            ["AppHost:OtlpApiKey"] = "otlp-key",
            [DcpExecutor.DebugSessionPortVar] = "12345" // Force IDE execution path
        });
        var configuration = configBuilder.Build();

        var executor = CreateAppExecutor(model, configuration: configuration, kubernetesService: kubernetes);

        // Act
        await executor.RunApplicationAsync();

        // Assert
        var exe = GetCreatedExecutableForResource(kubernetes, "proj");
        Assert.True(exe.TryGetProjectLaunchConfiguration(out var plc));
        Assert.NotNull(plc);
        Assert.False(plc!.DisableLaunchProfile);
        Assert.Equal("http", plc.LaunchProfile);
    }

    [Theory]
    [InlineData("Debug", ExecutableLaunchMode.Debug)]
    [InlineData("NoDebug", ExecutableLaunchMode.NoDebug)]
    public async Task ProjectLaunchConfiguration_RespectsDebugSessionRunMode(string runMode, string expectedMode)
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.AddProject<Projects.ServiceA>("proj", launchProfileName: "http");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var kubernetes = new TestKubernetesService();
        var configBuilder = new ConfigurationBuilder();
        configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [KnownConfigNames.DashboardOtlpGrpcEndpointUrl] = "http://localhost",
            ["AppHost:BrowserToken"] = "token",
            ["AppHost:OtlpApiKey"] = "otlp-key",
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionRunMode] = runMode
        });
        var configuration = configBuilder.Build();

        var executor = CreateAppExecutor(model, configuration: configuration, kubernetesService: kubernetes);

        await executor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetes, "proj");
        Assert.True(exe.TryGetProjectLaunchConfiguration(out var plc));
        Assert.NotNull(plc);
        Assert.Equal(expectedMode, plc!.Mode);
    }

    [Fact]
    public async Task ProjectLaunchConfiguration_UsesProjectDebugSupportProducer_InDebugSession()
    {
        // The producer owns the whole launch configuration: nothing downstream overwrites what it returns,
        // not even the project path, which differs here from the one on the resource's project metadata.
        var builder = DistributedApplication.CreateBuilder();
        var projectBuilder = builder.AddProject<TestProject>("proj", launchProfileName: null);
        var annotationToRemove = projectBuilder.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().FirstOrDefault();
        if (annotationToRemove is not null)
        {
            projectBuilder.Resource.Annotations.Remove(annotationToRemove);
        }

        projectBuilder.WithDebugSupport(_ => new ProjectLaunchConfiguration
        {
            Mode = ExecutableLaunchMode.NoDebug,
            ProjectPath = "ProducerSuppliedPath",
            DisableLaunchProfile = true
        }, "project");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var kubernetes = new TestKubernetesService();
        var configBuilder = new ConfigurationBuilder();
        configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionRunMode] = ExecutableLaunchMode.Debug
        });
        var configuration = configBuilder.Build();

        var executor = CreateAppExecutor(model, configuration: configuration, kubernetesService: kubernetes);

        await executor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetes, "proj");
        Assert.True(exe.TryGetProjectLaunchConfiguration(out var plc));
        Assert.NotNull(plc);
        Assert.Equal("ProducerSuppliedPath", plc!.ProjectPath);
        Assert.Equal(ExecutableLaunchMode.NoDebug, plc.Mode);
        Assert.True(plc.DisableLaunchProfile);
    }

    [Fact]
    public async Task ProjectLaunchConfiguration_Disabled_WhenLaunchProfileExcluded_InDebugSession()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        // Passing null launchProfileName applies ExcludeLaunchProfileAnnotation
        builder.AddProject<Projects.ServiceA>("proj", launchProfileName: null);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var kubernetes = new TestKubernetesService();
        var configBuilder = new ConfigurationBuilder();
        configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [KnownConfigNames.DashboardOtlpGrpcEndpointUrl] = "http://localhost",
            ["AppHost:BrowserToken"] = "token",
            ["AppHost:OtlpApiKey"] = "otlp-key",
            [DcpExecutor.DebugSessionPortVar] = "12345" // Force IDE execution path
        });
        var configuration = configBuilder.Build();

        var executor = CreateAppExecutor(model, configuration: configuration, kubernetesService: kubernetes);

        // Act
        await executor.RunApplicationAsync();

        // Assert
        var exe = GetCreatedExecutableForResource(kubernetes, "proj");
        Assert.True(exe.TryGetProjectLaunchConfiguration(out var plc));
        Assert.NotNull(plc);
        Assert.True(plc!.DisableLaunchProfile);
        Assert.Equal(string.Empty, plc.LaunchProfile);
    }

    [Fact]
    public async Task ProjectLaunchConfiguration_DefaultLaunchProfileAnnotationFallsBack_WhenProfileMissing_InDebugSession()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        // Configure a default launch profile name that does NOT exist in TestProjectWithLaunchSettings (profiles: Foo, http)
        builder.Configuration["AppHost:DefaultLaunchProfileName"] = "DoesNotExistProfile";
        builder.AddProject<TestProjectWithLaunchSettings>("proj");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var kubernetes = new TestKubernetesService();
        var configBuilder = new ConfigurationBuilder();
        configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [KnownConfigNames.DashboardOtlpGrpcEndpointUrl] = "http://localhost",
            ["AppHost:BrowserToken"] = "token",
            ["AppHost:OtlpApiKey"] = "otlp-key",
            [DcpExecutor.DebugSessionPortVar] = "12345" // Force IDE execution path
        });
        var configuration = configBuilder.Build();

        var executor = CreateAppExecutor(model, configuration: configuration, kubernetesService: kubernetes);

        // Act
        await executor.RunApplicationAsync();

        // Assert
        var exe = GetCreatedExecutableForResource(kubernetes, "proj");
        Assert.True(exe.TryGetProjectLaunchConfiguration(out var plc));
        Assert.NotNull(plc);
        // Should have fallen back to the first available profile (in insertion order) which is Foo, not the missing one.
        Assert.False(plc!.DisableLaunchProfile);
        Assert.Equal("Foo", plc.LaunchProfile);
        Assert.NotEqual("DoesNotExistProfile", plc.LaunchProfile);
        // DOTNET_LAUNCH_PROFILE env var should reflect the effective profile name.
        Assert.NotNull(exe.Spec.Env);
        var effectiveLaunchProfileEnv = exe.Spec.Env.SingleOrDefault(v => v.Name == "DOTNET_LAUNCH_PROFILE")?.Value;
        Assert.Equal("Foo", effectiveLaunchProfileEnv);
    }

    [Fact]
    public async Task ProjectLaunchConfiguration_DefaultLaunchProfileAnnotationSelectsExisting_InDebugSession()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.Configuration["AppHost:DefaultLaunchProfileName"] = "http"; // existing profile
        builder.AddProject<TestProjectWithLaunchSettings>("proj");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var kubernetes = new TestKubernetesService();
        var configBuilder = new ConfigurationBuilder();
        configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [KnownConfigNames.DashboardOtlpGrpcEndpointUrl] = "http://localhost",
            ["AppHost:BrowserToken"] = "token",
            ["AppHost:OtlpApiKey"] = "otlp-key",
            [DcpExecutor.DebugSessionPortVar] = "12345"
        });
        var configuration = configBuilder.Build();
        var executor = CreateAppExecutor(model, configuration: configuration, kubernetesService: kubernetes);
        await executor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetes, "proj");
        Assert.True(exe.TryGetProjectLaunchConfiguration(out var plc));
        Assert.False(plc!.DisableLaunchProfile);
        Assert.Equal("http", plc.LaunchProfile);
        var envVal = exe.Spec.Env!.SingleOrDefault(e => e.Name == "DOTNET_LAUNCH_PROFILE")?.Value;
        Assert.Equal("http", envVal);
    }

    [Fact]
    public async Task ProjectLaunchConfiguration_ExplicitLaunchProfileOverridesDefault_InDebugSession()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.Configuration["AppHost:DefaultLaunchProfileName"] = "Foo"; // default points to Foo
        builder.AddProject<TestProjectWithLaunchSettings>("proj", launchProfileName: "http"); // explicit different

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var kubernetes = new TestKubernetesService();
        var configBuilder = new ConfigurationBuilder();
        configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [KnownConfigNames.DashboardOtlpGrpcEndpointUrl] = "http://localhost",
            ["AppHost:BrowserToken"] = "token",
            ["AppHost:OtlpApiKey"] = "otlp-key",
            [DcpExecutor.DebugSessionPortVar] = "12345"
        });
        var configuration = configBuilder.Build();
        var executor = CreateAppExecutor(model, configuration: configuration, kubernetesService: kubernetes);
        await executor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetes, "proj");
        Assert.True(exe.TryGetProjectLaunchConfiguration(out var plc));
        Assert.False(plc!.DisableLaunchProfile);
        Assert.Equal("http", plc.LaunchProfile); // explicit wins
        var envVal = exe.Spec.Env!.SingleOrDefault(e => e.Name == "DOTNET_LAUNCH_PROFILE")?.Value;
        Assert.Equal("http", envVal);
    }

    [Fact]
    public async Task ProjectLaunchConfiguration_DefaultIgnoredWhenExcluded_InDebugSession()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.Configuration["AppHost:DefaultLaunchProfileName"] = "Foo";
        builder.AddProject<TestProjectWithLaunchSettings>("proj", launchProfileName: null); // exclude

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var kubernetes = new TestKubernetesService();
        var configBuilder = new ConfigurationBuilder();
        configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [KnownConfigNames.DashboardOtlpGrpcEndpointUrl] = "http://localhost",
            ["AppHost:BrowserToken"] = "token",
            ["AppHost:OtlpApiKey"] = "otlp-key",
            [DcpExecutor.DebugSessionPortVar] = "12345"
        });
        var configuration = configBuilder.Build();
        var executor = CreateAppExecutor(model, configuration: configuration, kubernetesService: kubernetes);
        await executor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetes, "proj");
        Assert.True(exe.TryGetProjectLaunchConfiguration(out var plc));
        Assert.True(plc!.DisableLaunchProfile);
        Assert.Equal(string.Empty, plc.LaunchProfile);
        Assert.DoesNotContain(exe.Spec.Env ?? [], e => e.Name == "DOTNET_LAUNCH_PROFILE");
    }

    [Fact]
    public async Task ProjectLaunchConfiguration_NoProfiles_NoLaunchProfileSelected_InDebugSession()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.Configuration["AppHost:DefaultLaunchProfileName"] = "Foo"; // won't match anything
        builder.AddProject<TestProjectNoProfiles>("proj");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var kubernetes = new TestKubernetesService();
        var configBuilder = new ConfigurationBuilder();
        configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [KnownConfigNames.DashboardOtlpGrpcEndpointUrl] = "http://localhost",
            ["AppHost:BrowserToken"] = "token",
            ["AppHost:OtlpApiKey"] = "otlp-key",
            [DcpExecutor.DebugSessionPortVar] = "12345"
        });
        var configuration = configBuilder.Build();
        var executor = CreateAppExecutor(model, configuration: configuration, kubernetesService: kubernetes);
        await executor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetes, "proj");
        Assert.True(exe.TryGetProjectLaunchConfiguration(out var plc));
        Assert.False(plc!.DisableLaunchProfile); // not excluded
        Assert.Equal(string.Empty, plc.LaunchProfile); // nothing selected
        Assert.DoesNotContain(exe.Spec.Env ?? [], e => e.Name == "DOTNET_LAUNCH_PROFILE");
    }

    [Fact]
    public async Task ProjectLaunchConfiguration_FallbackToFirstProfileInsertionOrder_InDebugSession()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.AddProject<TestProjectMultiProfileOrder>("proj");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var kubernetes = new TestKubernetesService();
        var configBuilder = new ConfigurationBuilder();
        configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [KnownConfigNames.DashboardOtlpGrpcEndpointUrl] = "http://localhost",
            ["AppHost:BrowserToken"] = "token",
            ["AppHost:OtlpApiKey"] = "otlp-key",
            [DcpExecutor.DebugSessionPortVar] = "12345"
        });
        var configuration = configBuilder.Build();
        var executor = CreateAppExecutor(model, configuration: configuration, kubernetesService: kubernetes);
        await executor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetes, "proj");
        Assert.True(exe.TryGetProjectLaunchConfiguration(out var plc));
        Assert.False(plc!.DisableLaunchProfile);
        Assert.Equal("Zed", plc.LaunchProfile); // first inserted wins
    }

    [Fact]
    public async Task PlainExecutable_LaunchConfigurationProducerReceivesResolvedEnvironmentVariables()
    {
        var builder = DistributedApplication.CreateBuilder();
        LaunchConfigurationCallbackContext? launchContext = null;
        var environmentCallbackInvocationCount = 0;
        var debugSessionInfo = JsonSerializer.Serialize(new RunSessionInfo
        {
            ProtocolsSupported = ["test"],
            SupportedLaunchConfigurations = ["test"]
        });
        builder.Configuration[DcpExecutor.DebugSessionPortVar] = "12345";
        builder.Configuration[KnownConfigNames.DebugSessionInfo] = debugSessionInfo;
        builder.Configuration[KnownConfigNames.DebugSessionRunMode] = ExecutableLaunchMode.Debug;

        var resource = new TestExecutableResource("test-working-directory");
        builder.AddResource(resource)
            .WithArgs("app-arg")
            .WithEnvironment(context =>
            {
                var currentInvocation = Interlocked.Increment(ref environmentCallbackInvocationCount);
                context.EnvironmentVariables["DEBUG_VALUE"] = $"resolved-{currentInvocation}";
            })
            .WithDebugSupport(
                context =>
                {
                    launchContext = context;
                    return Task.FromResult(new ExecutableLaunchConfiguration("test")
                    {
                        Mode = context.Mode
                    });
                },
                "test");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DcpExecutor.DebugSessionPortVar] = "12345",
                [KnownConfigNames.DebugSessionInfo] = debugSessionInfo,
                [KnownConfigNames.DebugSessionRunMode] = ExecutableLaunchMode.Debug
            })
            .Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(
            distributedAppModel,
            kubernetesService: kubernetesService,
            configuration: configuration);
        using var cts = new CancellationTokenSource();

        await appExecutor.RunApplicationAsync(cts.Token);

        Assert.NotNull(launchContext);
        Assert.Equal(ExecutableLaunchMode.Debug, launchContext.Mode);
        Assert.Same(resource, launchContext.Resource);
        Assert.Equal(cts.Token, launchContext.CancellationToken);

        var executable = GetCreatedExecutableForResource(kubernetesService, resource.Name);
        var debugValue = Assert.Single(executable.Spec.Env!, variable => variable.Name == "DEBUG_VALUE").Value;
        Assert.Equal(1, Volatile.Read(ref environmentCallbackInvocationCount));
        Assert.Equal("resolved-1", debugValue);
        Assert.Equal(debugValue, launchContext.EnvironmentVariables["DEBUG_VALUE"]);
        Assert.Equal(["app-arg"], executable.Spec.Args);
    }

    [Fact]
    public async Task PlainExecutable_ExtensionMode_SupportedDebugMode_RunsInIde()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();

        // Create executable resources with SupportsDebuggingAnnotation
        var debuggableExecutable = new TestExecutableResource("test-working-directory");
        builder.AddResource(debuggableExecutable).WithDebugSupport(mode => new ExecutableLaunchConfiguration("test") { Mode = mode }, "test");

        var nonDebuggableExecutable = new TestOtherExecutableResource("test-working-directory-2");
        // No SupportsDebuggingAnnotation for this one
        builder.AddResource(nonDebuggableExecutable);

        // Simulate debug session port and extension endpoint (extension mode)
        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo { ProtocolsSupported = ["test"], SupportedLaunchConfigurations = ["test"] }),
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234",
            [KnownConfigNames.DebugSessionRunMode] = "Debug"
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        // Act
        await appExecutor.RunApplicationAsync();

        // Assert
        List<Executable> dcpExes = [];
        var haveExes = RetryTillTrueOrTimeout(() =>
        {
            dcpExes.Clear();
            dcpExes.AddRange(kubernetesService.CreatedResources.OfType<Executable>());
            return dcpExes.Count == 2;
        }, TestConstants.DefaultOrchestratorTestTimeout);
        Assert.True(haveExes, $"Expected two running but instead got {dcpExes.Count}");

        var debuggableExe = Assert.Single(dcpExes, e => e.AppModelResourceName == "TestExecutable");
        Assert.Equal(ExecutionType.IDE, debuggableExe.Spec.ExecutionType);
        Assert.Null(debuggableExe.Spec.FallbackExecutionTypes);
        Assert.True(debuggableExe.TryGetAnnotationAsObjectList<ExecutableLaunchConfiguration>(Executable.LaunchConfigurationsAnnotation, out var launchConfigs1));
        var config1 = Assert.Single(launchConfigs1);
        Assert.Equal(ExecutableLaunchMode.Debug, config1.Mode);
        Assert.Equal("test", config1.Type);

        var nonDebuggableExe = Assert.Single(dcpExes, e => e.AppModelResourceName == "TestOtherExecutable");
        Assert.Equal(ExecutionType.Process, nonDebuggableExe.Spec.ExecutionType);
        Assert.False(nonDebuggableExe.TryGetAnnotationAsObjectList<ProjectLaunchConfiguration>(Executable.LaunchConfigurationsAnnotation, out _));
    }

    [Fact]
    public async Task PersistentPlainExecutable_ExtensionMode_RunsInProcess()
    {
        var builder = DistributedApplication.CreateBuilder();

        var executable = new TestExecutableResource("test-working-directory");
        builder.AddResource(executable)
            .WithDebugSupport(mode => new ExecutableLaunchConfiguration("test") { Mode = mode }, "test")
            .WithPersistentLifetime();

        var configDict = new Dictionary<string, string?>
        {
            ["AppHost:Sha256"] = "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef",
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo { ProtocolsSupported = ["test"], SupportedLaunchConfigurations = ["test"] }),
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234",
            [KnownConfigNames.DebugSessionRunMode] = "Debug"
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        await appExecutor.RunApplicationAsync();

        var exe = Assert.Single(kubernetesService.CreatedResources.OfType<Executable>(), e => e.AppModelResourceName == "TestExecutable");
        Assert.Equal("TestExecutable-12345678", exe.Metadata.Name);
        Assert.True(exe.Spec.Persistent.GetValueOrDefault());
        Assert.Equal(ExecutionType.Process, exe.Spec.ExecutionType);
        Assert.Null(exe.Spec.FallbackExecutionTypes);
    }

    [Fact]
    public async Task ProjectResource_WithLaunchToolArgs_ReplacesDotnetRunScaffolding_InProcessMode()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.AddProject<TestProjectWithLaunchProfileCommandLineArgs>("proj", launchProfileName: "http")
            .WithLaunchToolArgs(ctx =>
            {
                ctx.Args.Add("tool");
                ctx.Args.Add("exec");
                ctx.Args.Add("package");
                ctx.Args.Add("--");
            })
            .WithArgs("app-arg");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var kubernetes = new TestKubernetesService();
        var executor = CreateAppExecutor(model, kubernetesService: kubernetes);

        await executor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetes, "proj");
        var expectedArgs = new[] { "tool", "exec", "package", "--", "--profile-arg", "profile value", "app-arg" };

        Assert.Equal(ExecutionType.Process, exe.Spec.ExecutionType);
        Assert.Equal(expectedArgs, exe.Spec.Args);
        Assert.True(exe.TryGetAnnotationAsObjectList<AppLaunchArgumentAnnotation>(CustomResource.ResourceAppArgsAnnotation, out var argAnnotations));
        Assert.Equal(expectedArgs, argAnnotations.Select(a => a.Argument));
        AssertEffectiveArgumentIndexesMatchSpecArgs(argAnnotations, exe.Spec.Args);
    }

    [Fact]
    public async Task ProjectResource_WithDotnetToolRunLaunchArgs_DoesNotInjectProjectLaunchOptions_InProcessMode()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.AddProject<TestProjectWithLaunchProfileCommandLineArgs>("proj", launchProfileName: "http")
            .WithAnnotation(new ExecutableAnnotation
            {
                Command = "dotnet",
                WorkingDirectory = "/tmp/project"
            })
            .WithLaunchToolArgs(ctx =>
            {
                ctx.Args.Add("tool");
                ctx.Args.Add("run");
                ctx.Args.Add("package");
                ctx.Args.Add("--");
            })
            .WithArgs("app-arg");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var kubernetes = new TestKubernetesService();
        var executor = CreateAppExecutor(model, kubernetesService: kubernetes);

        await executor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetes, "proj");
        var expectedArgs = new[] { "tool", "run", "package", "--", "--profile-arg", "profile value", "app-arg" };

        Assert.Equal(ExecutionType.Process, exe.Spec.ExecutionType);
        Assert.Equal(expectedArgs, exe.Spec.Args);
        Assert.True(exe.TryGetAnnotationAsObjectList<AppLaunchArgumentAnnotation>(CustomResource.ResourceAppArgsAnnotation, out var argAnnotations));
        Assert.Equal(expectedArgs, argAnnotations.Select(a => a.Argument));
        AssertEffectiveArgumentIndexesMatchSpecArgs(argAnnotations, exe.Spec.Args);
    }

    [Fact]
    public async Task ProjectResource_EmptyLaunchToolArgs_KeepsDotnetRunScaffolding_InProcessMode()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.AddProject<TestProject>("proj", launchProfileName: null)
            .WithArgs("app-arg")
            .WithLaunchToolArgs(static _ => { });

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var kubernetes = new TestKubernetesService();
        var executor = CreateAppExecutor(model, kubernetesService: kubernetes);

        await executor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetes, "proj");
        Assert.Equal(ExecutionType.Process, exe.Spec.ExecutionType);
        AssertDefaultProjectProcessArgs(exe.Spec.Args, "app-arg");
    }

    [Fact]
    public async Task ProjectResource_WithLaunchToolArgsDebugSupport_WithholdsOwnedPrefix_InDebugSession()
    {
        // A ProjectResource can, via the generic WithLaunchToolArgs API, declare a tool
        // invocation prefix (ProjectResource implements IResourceWithArgs). The matching IDE launch configuration
        // owns that prefix, so it is withheld from Spec.Args.
        var builder = DistributedApplication.CreateBuilder();
        var projectBuilder = builder.AddProject<TestProject>("proj", launchProfileName: null);

        // Replace the default "project" debug support with a custom launch type that also owns the launch tool arguments.
        var defaultAnnotation = projectBuilder.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().FirstOrDefault();
        if (defaultAnnotation is not null)
        {
            projectBuilder.Resource.Annotations.Remove(defaultAnnotation);
        }

        projectBuilder
            .WithLaunchToolArgs(ctx => ctx.Args.Add("run"), ownedByLaunchConfigurationType: "test")
            .WithDebugSupport(
                mode => new ExecutableLaunchConfiguration("test") { Mode = mode },
                "test");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var kubernetes = new TestKubernetesService();
        var configBuilder = new ConfigurationBuilder();
        configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo { ProtocolsSupported = ["test"], SupportedLaunchConfigurations = ["test"] })
        });
        var configuration = configBuilder.Build();

        var executor = CreateAppExecutor(model, configuration: configuration, kubernetesService: kubernetes);

        await executor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetes, "proj");
        Assert.Equal(ExecutionType.IDE, exe.Spec.ExecutionType);
        Assert.Null(exe.Spec.FallbackExecutionTypes);
    }

    [Fact]
    public async Task ProjectResource_EmptyOwnedLaunchToolArgs_DoesNotConfigureRuntimeFallback()
    {
        var builder = DistributedApplication.CreateBuilder();
        var projectBuilder = builder.AddProject<TestProject>("proj", launchProfileName: null);

        var defaultAnnotation = projectBuilder.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().FirstOrDefault();
        if (defaultAnnotation is not null)
        {
            projectBuilder.Resource.Annotations.Remove(defaultAnnotation);
        }

        projectBuilder
            .WithArgs("app-arg")
            .WithLaunchToolArgs(static _ => { }, ownedByLaunchConfigurationType: "test")
            .WithDebugSupport(
                mode => new ExecutableLaunchConfiguration("test") { Mode = mode },
                "test");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var kubernetes = new TestKubernetesService();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DcpExecutor.DebugSessionPortVar] = "12345",
                [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo { ProtocolsSupported = ["test"], SupportedLaunchConfigurations = ["test"] })
            })
            .Build();

        var executor = CreateAppExecutor(model, configuration: configuration, kubernetesService: kubernetes);

        await executor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetes, "proj");
        Assert.Equal(ExecutionType.IDE, exe.Spec.ExecutionType);
        Assert.Equal(["app-arg"], exe.Spec.Args);
        Assert.Null(exe.Spec.FallbackExecutionTypes);
    }

    [Fact]
    public async Task ProjectResource_CustomIdeLaunchWithoutProcessInvocation_UsesApplicationArgumentsOnly()
    {
        var builder = DistributedApplication.CreateBuilder();
        var projectBuilder = builder.AddProject<TestProject>("proj", launchProfileName: null);

        var defaultAnnotation = projectBuilder.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().FirstOrDefault();
        if (defaultAnnotation is not null)
        {
            projectBuilder.Resource.Annotations.Remove(defaultAnnotation);
        }

        projectBuilder
            .WithArgs("app-arg")
            .WithDebugSupport(
                mode => new ExecutableLaunchConfiguration("azure-functions") { Mode = mode },
                "azure-functions");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var kubernetes = new TestKubernetesService();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DcpExecutor.DebugSessionPortVar] = "12345",
                [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo
                {
                    ProtocolsSupported = ["coreclr"],
                    SupportedLaunchConfigurations = ["azure-functions"]
                })
            })
            .Build();

        var executor = CreateAppExecutor(model, configuration: configuration, kubernetesService: kubernetes);

        await executor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetes, "proj");
        Assert.Equal(ExecutionType.IDE, exe.Spec.ExecutionType);
        Assert.Equal(["app-arg"], exe.Spec.Args);
        Assert.Null(exe.Spec.FallbackExecutionTypes);
    }

    [Fact]
    public async Task ProjectResource_CustomIdeLaunchWithoutProcessInvocation_DoesNotAddDotnetArgumentSeparator()
    {
        var builder = DistributedApplication.CreateBuilder();
        var projectBuilder = builder.AddProject<TestProjectWithLaunchProfileCommandLineArgs>("proj", launchProfileName: "http");

        var defaultAnnotation = projectBuilder.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().FirstOrDefault();
        if (defaultAnnotation is not null)
        {
            projectBuilder.Resource.Annotations.Remove(defaultAnnotation);
        }

        projectBuilder
            .WithArgs("app-arg")
            .WithDebugSupport(
                mode => new ExecutableLaunchConfiguration("azure-functions") { Mode = mode },
                "azure-functions");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var kubernetes = new TestKubernetesService();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DcpExecutor.DebugSessionPortVar] = "12345",
                [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo
                {
                    ProtocolsSupported = ["coreclr"],
                    SupportedLaunchConfigurations = ["azure-functions"]
                })
            })
            .Build();

        var executor = CreateAppExecutor(model, configuration: configuration, kubernetesService: kubernetes);

        await executor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetes, "proj");
        Assert.Equal(ExecutionType.IDE, exe.Spec.ExecutionType);
        Assert.Equal(["--profile-arg", "profile value", "app-arg"], exe.Spec.Args);

        Assert.True(exe.TryGetAnnotationAsObjectList<AppLaunchArgumentAnnotation>(CustomResource.ResourceAppArgsAnnotation, out var displayArgs));
        Assert.Equal(["--profile-arg", "profile value", "app-arg"], displayArgs.Select(a => a.Argument));
        AssertEffectiveArgumentIndexesMatchSpecArgs(displayArgs, exe.Spec.Args);
    }

    [Fact]
    public async Task ProjectResource_EmptyOwnedLaunchToolArgs_LaunchConfigurationFailureFailsResource()
    {
        var builder = DistributedApplication.CreateBuilder();
        var projectBuilder = builder.AddProject<TestProject>("proj", launchProfileName: null);

        var defaultAnnotation = projectBuilder.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().FirstOrDefault();
        if (defaultAnnotation is not null)
        {
            projectBuilder.Resource.Annotations.Remove(defaultAnnotation);
        }

        projectBuilder
            .WithArgs("app-arg")
            .WithLaunchToolArgs(static _ => { }, ownedByLaunchConfigurationType: "test")
            .WithDebugSupport(ThrowingLaunchConfiguration, "test");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var kubernetes = new TestKubernetesService();
        var failedResources = new List<IResource>();
        var events = new DcpExecutorEvents();
        events.Subscribe<OnResourceFailedToStartContext>(context =>
        {
            failedResources.Add(context.Resource);
            return Task.CompletedTask;
        });
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DcpExecutor.DebugSessionPortVar] = "12345",
                [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo { ProtocolsSupported = ["test"], SupportedLaunchConfigurations = ["test"] })
            })
            .Build();

        var executor = CreateAppExecutor(
            model,
            configuration: configuration,
            kubernetesService: kubernetes,
            events: events);

        await executor.RunApplicationAsync();

        Assert.Empty(GetCreatedExecutablesForResource(kubernetes, "proj"));
        Assert.Same(projectBuilder.Resource, Assert.Single(failedResources));

        static ExecutableLaunchConfiguration ThrowingLaunchConfiguration(string mode)
        {
            throw new InvalidOperationException("Launch configuration failed.");
        }
    }

    [Fact]
    public async Task ProjectResource_WithoutLaunchToolArgs_DoesNotConfigureRuntimeFallback_InDebugSession()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.AddProject<Projects.ServiceA>("proj", launchProfileName: null);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var kubernetes = new TestKubernetesService();
        var configBuilder = new ConfigurationBuilder();
        configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [KnownConfigNames.DashboardOtlpGrpcEndpointUrl] = "http://localhost",
            ["AppHost:BrowserToken"] = "token",
            ["AppHost:OtlpApiKey"] = "otlp-key",
            [DcpExecutor.DebugSessionPortVar] = "12345"
        });
        var configuration = configBuilder.Build();

        var executor = CreateAppExecutor(model, configuration: configuration, kubernetesService: kubernetes);

        await executor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetes, "proj");
        Assert.Equal(ExecutionType.IDE, exe.Spec.ExecutionType);
        Assert.Null(exe.Spec.FallbackExecutionTypes);
    }

    [Fact]
    public async Task PersistentDcpResourcesDoNotIncludeMonitorProcessByDefault()
    {
        var builder = DistributedApplication.CreateBuilder();

        builder.AddContainer("database", "image")
            .WithPersistentLifetime();
        builder.AddExecutable("worker", "worker", Environment.CurrentDirectory)
            .WithPersistentLifetime();
        builder.AddProject<TestProject>("project", launchProfileName: null)
            .WithPersistentLifetime();

        var configDict = new Dictionary<string, string?>
        {
            ["AppHost:Sha256"] = "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(
            distributedAppModel,
            kubernetesService: kubernetesService,
            configuration: configuration);

        await appExecutor.RunApplicationAsync();

        var container = Assert.Single(kubernetesService.CreatedResources.OfType<Container>());
        Assert.True(container.Spec.Persistent.GetValueOrDefault());
        Assert.Null(container.Spec.MonitorPid);
        Assert.Null(container.Spec.MonitorTimestamp);

        var executables = kubernetesService.CreatedResources.OfType<Executable>()
            .Where(e => e.AppModelResourceName is "worker" or "project")
            .ToArray();
        Assert.Equal(2, executables.Length);
        Assert.All(executables, exe =>
        {
            Assert.True(exe.Spec.Persistent.GetValueOrDefault());
            Assert.Null(exe.Spec.MonitorPid);
            Assert.Null(exe.Spec.MonitorTimestamp);
            Assert.Equal(ExecutionType.Process, exe.Spec.ExecutionType);
        });
    }

    [Fact]
    public async Task PersistentProjectWithReplicasThrows()
    {
        var builder = DistributedApplication.CreateBuilder();

        builder.AddProject<TestProject>("project", launchProfileName: null)
            .WithReplicas(2)
            .WithPersistentLifetime();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => appExecutor.RunApplicationAsync());
        Assert.Equal("Resource 'project' uses multiple replicas and a persistent lifetime. These features do not work together.", exception.Message);
    }

    [Fact]
    public async Task PersistentPlainExecutableWithReplicasThrows()
    {
        var builder = DistributedApplication.CreateBuilder();

        builder.AddExecutable("worker", "worker", Environment.CurrentDirectory)
            .WithAnnotation(new ReplicaAnnotation(2))
            .WithPersistentLifetime();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => appExecutor.RunApplicationAsync());
        Assert.Equal("Resource 'worker' uses multiple replicas and a persistent lifetime. These features do not work together.", exception.Message);
    }

    [Fact]
    public async Task PersistentContainerWithOtlpExporterUsesStableServiceInstanceId()
    {
        var first = await CreateOtlpServiceInstanceIdAsync(builder =>
        {
            builder.AddContainer("database", "image")
                .WithPersistentLifetime()
                .WithOtlpExporter();
        });
        var second = await CreateOtlpServiceInstanceIdAsync(builder =>
        {
            builder.AddContainer("database", "image")
                .WithPersistentLifetime()
                .WithOtlpExporter();
        });

        Assert.Equal("database-12345678", first);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task PersistentExecutableWithOtlpExporterUsesStableServiceInstanceId()
    {
        var first = await CreateOtlpServiceInstanceIdAsync(builder =>
        {
            builder.AddExecutable("worker", "worker", Environment.CurrentDirectory)
                .WithPersistentLifetime()
                .WithOtlpExporter();
        });
        var second = await CreateOtlpServiceInstanceIdAsync(builder =>
        {
            builder.AddExecutable("worker", "worker", Environment.CurrentDirectory)
                .WithPersistentLifetime()
                .WithOtlpExporter();
        });

        Assert.Equal("worker-12345678", first);
        Assert.Equal(first, second);
    }

    private static async Task<string> CreateOtlpServiceInstanceIdAsync(Action<IDistributedApplicationBuilder> configureBuilder)
    {
        var builder = DistributedApplication.CreateBuilder();
        configureBuilder(builder);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppHost:Sha256"] = "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef"
            })
            .Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(
            distributedAppModel,
            kubernetesService: kubernetesService,
            configuration: configuration);

        await appExecutor.RunApplicationAsync();

        var resource = Assert.Single(kubernetesService.CreatedResources, r =>
            r.Metadata.Annotations is not null &&
            r.Metadata.Annotations.ContainsKey(CustomResource.OtelServiceInstanceIdAnnotation));

        return resource.Metadata.Annotations![CustomResource.OtelServiceInstanceIdAnnotation];
    }

    [Fact]
    public async Task ExplicitParentProcessLifetimeIncludesMonitorProcess()
    {
        var builder = DistributedApplication.CreateBuilder();
        using var parentProcess = Process.GetCurrentProcess();
        var parentProcessIdentity = DcpProcessMonitor.GetMonitorProcessIdentity(parentProcess);

        builder.AddContainer("database", "image")
            .WithParentProcessLifetime(parentProcess.Id);
        builder.AddExecutable("worker", "worker", Environment.CurrentDirectory)
            .WithParentProcessLifetime(parentProcess.Id);
        builder.AddProject<TestProject>("project", launchProfileName: null)
            .WithParentProcessLifetime(parentProcess.Id);

        var configDict = new Dictionary<string, string?>
        {
            ["AppHost:Sha256"] = "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(
            distributedAppModel,
            kubernetesService: kubernetesService,
            configuration: configuration);

        await appExecutor.RunApplicationAsync();

        var container = Assert.Single(kubernetesService.CreatedResources.OfType<Container>());
        Assert.True(container.Spec.Persistent.GetValueOrDefault());
        Assert.Equal(parentProcessIdentity.ProcessId, container.Spec.MonitorPid);
        Assert.NotNull(container.Spec.MonitorTimestamp);
        Assert.Equal(parentProcessIdentity.Timestamp, container.Spec.MonitorTimestamp.Value, TimeSpan.FromMicroseconds(1));

        var executables = kubernetesService.CreatedResources.OfType<Executable>()
            .Where(e => e.AppModelResourceName is "worker" or "project")
            .ToArray();
        Assert.Equal(2, executables.Length);
        Assert.All(executables, exe =>
        {
            Assert.True(exe.Spec.Persistent.GetValueOrDefault());
            Assert.Equal(parentProcessIdentity.ProcessId, exe.Spec.MonitorPid);
            Assert.NotNull(exe.Spec.MonitorTimestamp);
            Assert.Equal(parentProcessIdentity.Timestamp, exe.Spec.MonitorTimestamp.Value, TimeSpan.FromMicroseconds(1));
            Assert.Equal(ExecutionType.Process, exe.Spec.ExecutionType);
        });
    }

    [Fact]
    public async Task PersistentPlainExecutable_UsesStableCertificateOutputPath()
    {
        var builder = DistributedApplication.CreateBuilder();
        using var fileSystemService = new FileSystemService(new ConfigurationBuilder().Build());
        using var aspireStoreDirectory = fileSystemService.TempDirectory.CreateTempSubdirectory("aspire-store");

        using var certificate = CreateTestCertificate();
        var certificateAuthorities = builder.AddCertificateAuthorityCollection("certificates")
            .WithCertificate(certificate);

        var executable = new TestExecutableResource("test-working-directory");
        builder.AddResource(executable)
            .WithCertificateAuthorityCollection(certificateAuthorities)
            .WithCertificateTrustScope(CertificateTrustScope.Override)
            .WithPersistentLifetime();

        var configDict = new Dictionary<string, string?>
        {
            [AspireStore.AspireStorePathKeyName] = aspireStoreDirectory.Path,
            ["AppHost:Sha256"] = "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef"
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        await appExecutor.RunApplicationAsync();

        var exe = Assert.Single(kubernetesService.CreatedResources.OfType<Executable>(), e => e.AppModelResourceName == "TestExecutable");
        var sslCertDir = Assert.Single(exe.Spec.Env!, e => e.Name == "SSL_CERT_DIR").Value;
        var sslCertFile = Assert.Single(exe.Spec.Env!, e => e.Name == "SSL_CERT_FILE").Value;
        var expectedCertificatesRoot = Path.Join(aspireStoreDirectory.Path, ".aspire", "dcp", "executables", "TestExecutable-12345678", "certificates");

        Assert.Equal(Path.Join(expectedCertificatesRoot, "certs"), sslCertDir);
        Assert.Equal(Path.Join(expectedCertificatesRoot, "cert.pem"), sslCertFile);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Unix file modes do not apply on Windows, where the directory inherits its ACL from the parent.")]
    public async Task PersistentPlainExecutable_WritesCustomBundleDirectoryOwnerOnly()
    {
        var builder = DistributedApplication.CreateBuilder();
        using var fileSystemService = new FileSystemService(new ConfigurationBuilder().Build());
        using var aspireStoreDirectory = fileSystemService.TempDirectory.CreateTempSubdirectory("aspire-store");

        using var certificate = CreateTestCertificate();
        var certificateAuthorities = builder.AddCertificateAuthorityCollection("certificates")
            .WithCertificate(certificate);

        var executable = new TestExecutableResource("test-working-directory");
        builder.AddResource(executable)
            .WithCertificateAuthorityCollection(certificateAuthorities)
            .WithCertificateTrustScope(CertificateTrustScope.Override)
            // A custom bundle is what Aspire.Hosting.Java writes for JAVAX_NET_SSL_TRUSTSTORE, and it is
            // the only thing that causes the bundles/ directory to be created.
            .WithCertificateTrustConfiguration(static ctx =>
            {
                ctx.EnvironmentVariables["TEST_BUNDLE"] = ctx.CreateCustomBundle(static (_, _) => Task.FromResult(new byte[] { 1, 2, 3 }));
                return Task.CompletedTask;
            })
            // Persistent lifetime is the case that matters: the bundle lands in the stable Aspire store
            // path rather than inside the session-scoped temp directory, which is already owner-only.
            .WithPersistentLifetime();

        var configDict = new Dictionary<string, string?>
        {
            [AspireStore.AspireStorePathKeyName] = aspireStoreDirectory.Path,
            ["AppHost:Sha256"] = "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef"
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        await appExecutor.RunApplicationAsync();

        var certificatesRoot = Path.Join(aspireStoreDirectory.Path, ".aspire", "dcp", "executables", "TestExecutable-12345678", "certificates");
        var bundlesDirectory = Path.Join(certificatesRoot, "bundles");
        Assert.True(Directory.Exists(bundlesDirectory), $"Expected the custom bundle directory to exist at {bundlesDirectory}.");

        var mode = GetUnixFileModeForTest(bundlesDirectory);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, mode);
    }

    private static UnixFileMode GetUnixFileModeForTest(string path)
    {
        // The caller guards on platform, but the analyzer cannot see through [SkipOnPlatform].
#pragma warning disable CA1416
        return File.GetUnixFileMode(path);
#pragma warning restore CA1416
    }

    [Fact]
    public void PlainExecutableCertificateDirectoriesPath_IncludesExistingWellKnownDirectoriesForAppendWhenSslCertDirIsUnsetOnLinux()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "OpenSSL default certificate directories are only inferred on Linux.");

        var options = new RemoteInvokeOptions();
        options.StartInfo.Environment.Remove("SSL_CERT_DIR");

        RemoteExecutor.Invoke(static async () =>
        {
            Environment.SetEnvironmentVariable("SSL_CERT_DIR", null);

            var expectedWellKnownCertificateDirectories = ContainerCertificatePathsAnnotation.DefaultCertificateDirectoriesPaths
                .Where(Directory.Exists)
                .ToArray();
            Assert.NotEmpty(expectedWellKnownCertificateDirectories);

            var builder = DistributedApplication.CreateBuilder();
            using var certificate = CreateTestCertificate();
            var certificateAuthorities = builder.AddCertificateAuthorityCollection("certificates")
                .WithCertificate(certificate);

            var executable = new TestExecutableResource("test-working-directory");
            builder.AddResource(executable)
                .WithCertificateAuthorityCollection(certificateAuthorities);

            var kubernetesService = new TestKubernetesService();
            using var app = builder.Build();
            var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
            var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);

            await appExecutor.RunApplicationAsync();

            var exe = Assert.Single(kubernetesService.CreatedResources.OfType<Executable>(), e => e.AppModelResourceName == "TestExecutable");
            var sslCertDir = Assert.Single(exe.Spec.Env!, e => e.Name == "SSL_CERT_DIR").Value;

            Assert.NotNull(sslCertDir);
            var sslCertDirs = sslCertDir.Split(Path.PathSeparator);
            Assert.EndsWith($"{Path.DirectorySeparatorChar}certs", sslCertDirs[0]);
            Assert.Equal(expectedWellKnownCertificateDirectories, sslCertDirs.Skip(1));
        }, options).Dispose();
    }

    [Fact]
    public void PlainExecutableCertificateDirectoriesPath_PreservesAppHostSslCertDirForAppend()
    {
        var expectedExistingSslCertDirs = new[] { "/custom/certs", "/home/me/.aspnet/dev-certs/trust" };
        var options = new RemoteInvokeOptions();
        options.StartInfo.Environment["SSL_CERT_DIR"] = string.Join(Path.PathSeparator, expectedExistingSslCertDirs);

        RemoteExecutor.Invoke(static expectedExistingSslCertDir =>
        {
            Environment.SetEnvironmentVariable("SSL_CERT_DIR", expectedExistingSslCertDir);

            var sslCertDir = GetPlainExecutableSslCertDirAsync().GetAwaiter().GetResult();

            Assert.NotNull(sslCertDir);
            var sslCertDirs = sslCertDir.Split(Path.PathSeparator);
            Assert.EndsWith($"{Path.DirectorySeparatorChar}certs", sslCertDirs[0]);
            Assert.Equal(expectedExistingSslCertDir.Split(Path.PathSeparator), sslCertDirs.Skip(1));
        }, string.Join(Path.PathSeparator, expectedExistingSslCertDirs), options).Dispose();
    }

    [Fact]
    public void PlainExecutableCertificateDirectoriesPath_PreservesEmptyAppHostSslCertDirForAppend()
    {
        var options = new RemoteInvokeOptions();
        options.StartInfo.Environment["SSL_CERT_DIR"] = string.Empty;

        RemoteExecutor.Invoke(static () =>
        {
            var sslCertDir = GetPlainExecutableSslCertDirAsync().GetAwaiter().GetResult();

            Assert.NotNull(sslCertDir);
            var sslCertDirs = sslCertDir.Split(Path.PathSeparator);
            Assert.Single(sslCertDirs);
            Assert.EndsWith($"{Path.DirectorySeparatorChar}certs", sslCertDirs[0]);
        }, options).Dispose();
    }

    [Fact]
    public void PlainExecutableCertificateDirectoriesPath_DoesNotIncludeAppHostSslCertDirForOverride()
    {
        var options = new RemoteInvokeOptions();
        options.StartInfo.Environment["SSL_CERT_DIR"] = "/custom/certs";

        RemoteExecutor.Invoke(static () =>
        {
            Environment.SetEnvironmentVariable("SSL_CERT_DIR", "/custom/certs");

            var sslCertDir = GetPlainExecutableSslCertDirAsync(builder => builder.WithCertificateTrustScope(CertificateTrustScope.Override)).GetAwaiter().GetResult();

            Assert.NotNull(sslCertDir);
            var sslCertDirs = sslCertDir.Split(Path.PathSeparator);
            Assert.Single(sslCertDirs);
            Assert.EndsWith($"{Path.DirectorySeparatorChar}certs", sslCertDirs[0]);
        }, options).Dispose();
    }

    [Fact]
    public async Task PlainExecutableCertificateDirectoriesPath_IgnoresResourceSslCertDirForAppend()
    {
        var customSslCertDir = $"/resource-certs-{Guid.NewGuid():N}";

        var sslCertDir = await GetPlainExecutableSslCertDirAsync(builder => builder.WithEnvironment("SSL_CERT_DIR", customSslCertDir));

        Assert.NotNull(sslCertDir);
        var sslCertDirs = sslCertDir.Split(Path.PathSeparator);
        Assert.EndsWith($"{Path.DirectorySeparatorChar}certs", sslCertDirs[0]);
        Assert.All(sslCertDirs, dir => Assert.NotEqual(customSslCertDir, dir));
    }

    [Fact]
    public async Task SessionScopedExplicitStartPlainExecutable_DefersDcpObjectCreationUntilManualStart()
    {
        var builder = DistributedApplication.CreateBuilder();

        var resource = builder.AddExecutable("CoolProgram", "cool", Environment.CurrentDirectory, "--alpha", "--bravo")
            .WithExplicitStart();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);

        await appExecutor.RunApplicationAsync();

        Assert.Empty(GetCreatedExecutablesForResource(kubernetesService, "CoolProgram"));

        var reference = appExecutor.GetResource(DcpExecutor.GetDcpInstance(resource.Resource, instanceIndex: 0).Name);
        await appExecutor.StartResourceAsync(reference, CancellationToken.None);

        var exe = Assert.Single(GetCreatedExecutablesForResource(kubernetesService, "CoolProgram"));
        Assert.True(exe.Spec.Start);
    }

    [Fact]
    public async Task PlainExecutable_MultipleLaunchRecipes_ReportsLaunchPlanFailure()
    {
        var builder = DistributedApplication.CreateBuilder();
        var resource = builder.AddExecutable("app", "tool", Environment.CurrentDirectory)
            .WithExplicitStart();
        resource.Resource.Annotations.Add(
            new ExecutableLaunchRecipeAnnotation(DirectExecutableLaunchRecipe.Instance));

        var kubernetesService = new TestKubernetesService();
        var failures = new ConcurrentQueue<OnResourceFailedToStartContext>();
        var events = new DcpExecutorEvents();
        events.Subscribe<OnResourceFailedToStartContext>(context =>
        {
            failures.Enqueue(context);
            return Task.CompletedTask;
        });

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(
            distributedAppModel,
            kubernetesService: kubernetesService,
            events: events);

        await appExecutor.RunApplicationAsync();

        var reference = appExecutor.GetResource(DcpExecutor.GetDcpInstance(resource.Resource, instanceIndex: 0).Name);
        var exception = await Assert.ThrowsAsync<FailedToApplyEnvironmentException>(
            () => appExecutor.StartResourceAsync(reference, CancellationToken.None));

        const string innerMessage =
            "Resource 'app' must have exactly one executable launch recipe, but 2 were found.";
        const string expectedMessage =
            "Failed to create executable launch plan for resource 'app'. " + innerMessage;
        Assert.Equal(expectedMessage, exception.Message);
        var innerException = Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Equal(innerMessage, innerException.Message);

        var failure = Assert.Single(failures);
        Assert.Same(resource.Resource, failure.Resource);
        Assert.Equal(expectedMessage, failure.ErrorMessage);
        Assert.Empty(GetCreatedExecutablesForResource(kubernetesService, "app"));
    }

    [Fact]
    public async Task PlainExecutable_ExtensionMode_UnsupportedDebugMode_RunsInProcess()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();

        // Create executable resources with SupportsDebuggingAnnotation
        var executable = new TestExecutableResource("test-working-directory");
        builder.AddResource(executable).WithDebugSupport(_ => new ExecutableLaunchConfiguration("test"), "test");

        // Simulate debug session port and extension endpoint (extension mode)
        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo { ProtocolsSupported = ["test"], SupportedLaunchConfigurations = ["other_executable"] }),
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234",
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        // Act
        await appExecutor.RunApplicationAsync();

        // Assert
        var dcpExes = kubernetesService.CreatedResources.OfType<Executable>().ToList();
        Assert.Single(dcpExes);

        var exe = Assert.Single(dcpExes, e => e.AppModelResourceName == "TestExecutable");
        Assert.Equal(ExecutionType.Process, exe.Spec.ExecutionType);
    }

    [Fact]
    public async Task PlainExecutable_NoExtensionMode_RunInProcess()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();

        // Create executable resources with SupportsDebuggingAnnotation
        var debuggableExecutable = new TestExecutableResource("test-working-directory");
        builder.AddResource(debuggableExecutable).WithDebugSupport(_ => new ExecutableLaunchConfiguration("test"), "test");

        var nonDebuggableExecutable = new TestOtherExecutableResource("test-working-directory-2");
        builder.AddResource(nonDebuggableExecutable);

        // Simulate no extension endpoint (no extension mode) - this means no debug session port
        var configDict = new Dictionary<string, string?>
        {
            [KnownConfigNames.ExtensionEndpoint] = null
            // No DEBUG_SESSION_PORT set
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        // Act
        await appExecutor.RunApplicationAsync();

        // Assert
        var dcpExes = kubernetesService.CreatedResources.OfType<Executable>().ToList();
        Assert.Equal(2, dcpExes.Count);

        var debuggableExe = Assert.Single(dcpExes, e => e.AppModelResourceName == "TestExecutable");
        Assert.Equal(ExecutionType.Process, debuggableExe.Spec.ExecutionType);
        Assert.False(debuggableExe.TryGetAnnotationAsObjectList<ProjectLaunchConfiguration>(Executable.LaunchConfigurationsAnnotation, out _));

        var nonDebuggableExe = Assert.Single(dcpExes, e => e.AppModelResourceName == "TestOtherExecutable");
        Assert.Equal(ExecutionType.Process, nonDebuggableExe.Spec.ExecutionType);
        Assert.False(nonDebuggableExe.TryGetAnnotationAsObjectList<ProjectLaunchConfiguration>(Executable.LaunchConfigurationsAnnotation, out _));
    }

    [Fact]
    public async Task CustomExecutable_NoDebugSessionInfo_RunInProcess()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();

        // Create executable resources with SupportsDebuggingAnnotation
        var debuggableExecutable = new TestExecutableResource("test-working-directory");
        builder.AddResource(debuggableExecutable).WithDebugSupport(_ => new ExecutableLaunchConfiguration("test"), "test");

        // Simulate no debug session port and no extension endpoint (no debug session info)
        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234"
            // No DebugSessionInfo set
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        // Act
        await appExecutor.RunApplicationAsync();

        // Assert
        var dcpExes = kubernetesService.CreatedResources.OfType<Executable>().ToList();
        Assert.Single(dcpExes);

        var exe = Assert.Single(dcpExes, e => e.AppModelResourceName == "TestExecutable");
        Assert.Equal(ExecutionType.Process, exe.Spec.ExecutionType);
    }

    [Fact]
    public async Task CustomExecutable_InvalidDebugSessionInfo_RunInProcess()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();

        // Create executable resources with SupportsDebuggingAnnotation
        var debuggableExecutable = new TestExecutableResource("test-working-directory");
        builder.AddResource(debuggableExecutable).WithDebugSupport(_ => new ExecutableLaunchConfiguration("test"), "test");

        // Simulate debug session port with invalid JSON in DebugSessionInfo
        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = "{invalid json}",
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234"
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        // Act
        await appExecutor.RunApplicationAsync();

        // Assert
        var dcpExes = kubernetesService.CreatedResources.OfType<Executable>().ToList();
        Assert.Single(dcpExes);

        var exe = Assert.Single(dcpExes, e => e.AppModelResourceName == "TestExecutable");
        Assert.Equal(ExecutionType.Process, exe.Spec.ExecutionType);
    }

    [Fact]
    public async Task CustomExecutable_DebugSessionInfoWithNullSupportedLaunchConfigurations_RunInProcess()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();

        // Create executable resources with SupportsDebuggingAnnotation
        var debuggableExecutable = new TestExecutableResource("test-working-directory");
        builder.AddResource(debuggableExecutable).WithDebugSupport(_ => new ExecutableLaunchConfiguration("test"), "test");

        // Simulate debug session info with null SupportedLaunchConfigurations
        var runSessionInfo = new RunSessionInfo
        {
            ProtocolsSupported = ["test"],
            SupportedLaunchConfigurations = null
        };

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(runSessionInfo),
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234"
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        // Act
        await appExecutor.RunApplicationAsync();

        // Assert
        var dcpExes = kubernetesService.CreatedResources.OfType<Executable>().ToList();
        Assert.Single(dcpExes);

        var exe = Assert.Single(dcpExes, e => e.AppModelResourceName == "TestExecutable");
        Assert.Equal(ExecutionType.Process, exe.Spec.ExecutionType);
    }

    [Fact]
    public async Task CustomExecutable_DebugSessionInfoNotContainingType_RunInProcess()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();

        // Create executable resources with SupportsDebuggingAnnotation
        var debuggableExecutable = new TestExecutableResource("test-working-directory");
        builder.AddResource(debuggableExecutable).WithDebugSupport(_ => new ExecutableLaunchConfiguration("test"), "test");

        // Simulate debug session info with SupportedLaunchConfigurations that do not match the executable type
        var runSessionInfo = new RunSessionInfo
        {
            ProtocolsSupported = ["test"],
            SupportedLaunchConfigurations = ["other_type"]
        };

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(runSessionInfo),
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234"
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        // Act
        await appExecutor.RunApplicationAsync();

        // Assert
        var dcpExes = kubernetesService.CreatedResources.OfType<Executable>().ToList();
        Assert.Single(dcpExes);

        var exe = Assert.Single(dcpExes, e => e.AppModelResourceName == "TestExecutable");
        Assert.Equal(ExecutionType.Process, exe.Spec.ExecutionType);
    }

    [Fact]
    public async Task CustomExecutable_DebugSessionInfoContainsType_RunInIde()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();

        // Create executable resources with SupportsDebuggingAnnotation
        var debuggableExecutable = new TestExecutableResource("test-working-directory");
        builder.AddResource(debuggableExecutable).WithDebugSupport(_ => new ExecutableLaunchConfiguration("test"), "test");

        // Simulate debug session info with SupportedLaunchConfigurations that match the executable type
        var runSessionInfo = new RunSessionInfo
        {
            ProtocolsSupported = ["test"],
            SupportedLaunchConfigurations = ["test"]
        };

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(runSessionInfo),
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234"
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        // Act
        await appExecutor.RunApplicationAsync();

        // Assert
        var dcpExes = kubernetesService.CreatedResources.OfType<Executable>().ToList();
        Assert.Single(dcpExes);

        var exe = Assert.Single(dcpExes, e => e.AppModelResourceName == "TestExecutable");
        Assert.Equal(ExecutionType.IDE, exe.Spec.ExecutionType);
    }

    [Fact]
    public async Task ProjectExecutable_NoDebugSessionInfo_DefaultsToProjectSupport()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        builder.AddProject<Projects.ServiceA>("ServiceA");

        // Simulate debug session port but no DebugSessionInfo (simulates missing or null configuration)
        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234"
            // No DebugSessionInfo set - should default to ["project"]
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        // Act
        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "ServiceA");
        Assert.Equal(ExecutionType.IDE, exe.Spec.ExecutionType);
    }

    [Fact]
    public async Task Project_WithTerminal_RunsAsProcess_InDebugSessionWhenDebugSupportIsAddedLater()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        var resource = builder.AddProject<Projects.ServiceA>("ServiceA").WithTerminal();
        resource
            .WithLaunchToolArgs(ctx => ctx.Args.Add("launch-tool-arg"), ownedByLaunchConfigurationType: "project")
            .WithDebugSupport(
                mode => new ProjectLaunchConfiguration { ProjectPath = "/test/path", Mode = mode },
                "project");

        // Simulate a debug session whose capability list advertises "project" support.
        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo { ProtocolsSupported = ["test"], SupportedLaunchConfigurations = ["project"] }),
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234"
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Materialize the per-replica terminal hosts (see Project_WithTerminal_PopulatesPerReplicaTerminalSpec).
        await builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services, distributedAppModel));

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "ServiceA");
        Assert.Equal(ExecutionType.Process, exe.Spec.ExecutionType);
        // Process execution keeps the full command line, so the tool-invocation prefix is passed through.
        Assert.NotNull(exe.Spec.Args);
        Assert.Contains("launch-tool-arg", exe.Spec.Args);
        Assert.NotNull(exe.Spec.Terminal);
    }

    [Fact]
    public async Task Project_WithTerminal_RunsAsProcess_NoDebugSessionInfo()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        builder.AddProject<Projects.ServiceA>("ServiceA").WithTerminal();

        // No DebugSessionInfo simulates the Visual Studio scenario, where project resources normally
        // fall back to IDE execution. A terminal-attached resource must still run as a plain process,
        // because attaching the debugger would break the PTY flow and leave the user with an empty
        // terminal. Temporary until DCP supports both at once: https://github.com/microsoft/dcp/issues/189.
        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234"
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        await builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services, distributedAppModel));

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "ServiceA");
        Assert.Equal(ExecutionType.Process, exe.Spec.ExecutionType);
    }

    [Fact]
    public async Task ProjectExecutable_InvalidDebugSessionInfo_DefaultsToProjectSupport()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        builder.AddProject<Projects.ServiceA>("ServiceA");

        // Simulate debug session port with invalid JSON in DebugSessionInfo
        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = "{invalid json}",
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234"
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        // Act
        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "ServiceA");
        Assert.Equal(ExecutionType.IDE, exe.Spec.ExecutionType);
    }

    [Fact]
    public async Task ProjectExecutable_DebugSessionInfoWithNullSupportedLaunchConfigurations_DefaultsToProjectSupport()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        builder.AddProject<Projects.ServiceA>("ServiceA");

        // Simulate debug session info with null SupportedLaunchConfigurations
        var runSessionInfo = new RunSessionInfo
        {
            ProtocolsSupported = ["test"],
            SupportedLaunchConfigurations = null
        };

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(runSessionInfo),
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234"
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        // Act
        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "ServiceA");
        Assert.Equal(ExecutionType.IDE, exe.Spec.ExecutionType);
    }

    [Fact]
    public async Task ProjectExecutable_DebugSessionInfoWithoutProject_SelectsProcess()
    {
        // When the IDE explicitly advertises a SupportedLaunchConfigurations list that does NOT
        // include "project", honor it: the IDE cannot launch project resources, so we must run
        // them as a Process from the AppHost. The VS Code extension behaves this way when the
        // C# extension is not installed; routing project resources to the extension in that case
        // would result in them never starting (the extension returns 400 UnsupportedLaunchConfiguration).
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        builder.AddProject<Projects.ServiceA>("ServiceA");

        var runSessionInfo = new RunSessionInfo
        {
            ProtocolsSupported = ["coreclr"],
            SupportedLaunchConfigurations = ["azure-functions"]
        };

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(runSessionInfo),
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234"
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "ServiceA");
        Assert.Equal(ExecutionType.Process, exe.Spec.ExecutionType);
    }

    [Fact]
    public async Task ProjectWithNonProjectAnnotation_DebugSessionWithoutInfo_UsesProjectIdeExecution()
    {
        // Bug #15378: Simulates the Visual Studio scenario for projects with custom debug types.
        // VS sets DEBUG_SESSION_PORT but does NOT send DEBUG_SESSION_INFO. A project resource
        // with a non-"project" SupportsDebuggingAnnotation (e.g., "azure-functions") should still
        // get ExecutionType.IDE with a ProjectLaunchConfiguration so VS can launch and debug it.
        var builder = DistributedApplication.CreateBuilder();

        var projectBuilder = builder.AddProject<TestProject>("proj", launchProfileName: null);
        var annotationToRemove = projectBuilder.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().FirstOrDefault();
        if (annotationToRemove is not null)
        {
            projectBuilder.Resource.Annotations.Remove(annotationToRemove);
        }
        projectBuilder.WithDebugSupport(mode => new ExecutableLaunchConfiguration("azure-functions") { Mode = mode }, "azure-functions");

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "proj");
        Assert.Equal(ExecutionType.IDE, exe.Spec.ExecutionType);
        Assert.Null(exe.Spec.Args);
        Assert.Null(exe.Spec.FallbackExecutionTypes);

        Assert.True(exe.TryGetAnnotationAsObjectList<ProjectLaunchConfiguration>(Executable.LaunchConfigurationsAnnotation, out var launchConfigs));
        Assert.Single(launchConfigs);
        Assert.Equal("project", launchConfigs[0].Type);
    }

    [Fact]
    public async Task ProjectWithNonProjectAnnotation_VSCodeExplicitlyUnsupported_RunsInProcess()
    {
        // Guard: When VS Code extension sends DEBUG_SESSION_INFO with SupportedLaunchConfigurations
        // that do NOT include the custom type, the resource should fall to Process mode. This ensures
        // the else-if branch doesn't over-capture VS Code scenarios.
        var builder = DistributedApplication.CreateBuilder();

        var projectBuilder = builder.AddProject<TestProject>("proj", launchProfileName: null);
        var annotationToRemove = projectBuilder.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().FirstOrDefault();
        if (annotationToRemove is not null)
        {
            projectBuilder.Resource.Annotations.Remove(annotationToRemove);
        }
        projectBuilder.WithDebugSupport(mode => new ExecutableLaunchConfiguration("azure-functions") { Mode = mode }, "azure-functions");

        var runSessionInfo = new RunSessionInfo
        {
            ProtocolsSupported = ["coreclr"],
            SupportedLaunchConfigurations = ["project"]
        };

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(runSessionInfo),
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234"
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "proj");
        Assert.Equal(ExecutionType.Process, exe.Spec.ExecutionType);
    }

    public static TheoryData<string, string[]> MauiProjectLaunchConfigurationsForProcessExecution => new()
    {
        { "maui", ["run", "-f", "net10.0-windows10.0.19041.0"] },
        { "maui", ["run", "-f", "net10.0-maccatalyst", "-p:OpenArguments=-W"] },
        { "maui", ["run", "-f", "net10.0-ios", "-p:_DeviceName=:v2:udid=E25BBE37-69BA-4720-B6FD-D54C97791E79"] },
        { "maui", ["run", "-f", "net10.0-ios", "-p:RuntimeIdentifier=ios-arm64"] },
        { "maui", ["run", "-f", "net10.0-android", "-p:AdbTarget=-e"] },
        { "maui", ["run", "-f", "net10.0-android", "-p:AdbTarget=-d"] }
    };

    [Theory]
    [MemberData(nameof(MauiProjectLaunchConfigurationsForProcessExecution))]
    public async Task ProjectWithNonProjectAnnotationAndExecutableAnnotation_VSCodeExplicitlyUnsupported_RunsInProcessWithResourceArgs(string launchConfigurationType, string[] resourceArgs)
    {
        var builder = DistributedApplication.CreateBuilder();

        var projectBuilder = builder.AddProject<TestProject>("proj", launchProfileName: null);
        var annotationToRemove = projectBuilder.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().FirstOrDefault();
        if (annotationToRemove is not null)
        {
            projectBuilder.Resource.Annotations.Remove(annotationToRemove);
        }
        projectBuilder
            .WithAnnotation(new ExecutableAnnotation
            {
                Command = "dotnet",
                WorkingDirectory = "/tmp/mauiapp"
            })
            .WithDebugSupport(mode => new ExecutableLaunchConfiguration(launchConfigurationType) { Mode = mode }, launchConfigurationType)
            .WithArgs(resourceArgs);

        var runSessionInfo = new RunSessionInfo
        {
            ProtocolsSupported = ["coreclr"],
            SupportedLaunchConfigurations = ["project"]
        };

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(runSessionInfo),
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234"
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var distributedApplicationOptions = new DistributedApplicationOptions { AssemblyName = typeof(DcpExecutorTests).Assembly.FullName };
        var expectedConfiguration = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyConfigurationAttribute>(typeof(DcpExecutorTests).Assembly)?.Configuration;
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration, distributedApplicationOptions: distributedApplicationOptions);

        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "proj");
        Assert.Equal(ExecutionType.Process, exe.Spec.ExecutionType);
        Assert.Equal("dotnet", exe.Spec.ExecutablePath);
        Assert.Equal("/tmp/mauiapp", exe.Spec.WorkingDirectory);
        var expectedArgs = new List<string> { resourceArgs[0] };
        if (!string.IsNullOrEmpty(expectedConfiguration))
        {
            expectedArgs.AddRange(["--configuration", expectedConfiguration]);
        }
        expectedArgs.Add("--no-launch-profile");
        expectedArgs.AddRange(resourceArgs.Skip(1));

        Assert.Equal(expectedArgs, exe.Spec.Args);
    }

    [Theory]
    [MemberData(nameof(MauiProjectLaunchConfigurationsForProcessExecution))]
    public async Task ProjectWithNonProjectAnnotationAndExecutableAnnotation_NoDebugSessionInfo_RunsInProcessWithResourceArgs(string launchConfigurationType, string[] resourceArgs)
    {
        var builder = DistributedApplication.CreateBuilder();

        var projectBuilder = builder.AddProject<TestProject>("proj", launchProfileName: null);
        var annotationToRemove = projectBuilder.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().FirstOrDefault();
        if (annotationToRemove is not null)
        {
            projectBuilder.Resource.Annotations.Remove(annotationToRemove);
        }
        projectBuilder
            .WithAnnotation(new ExecutableAnnotation
            {
                Command = "dotnet",
                WorkingDirectory = "/tmp/mauiapp"
            })
            .WithDebugSupport(mode => new ExecutableLaunchConfiguration(launchConfigurationType) { Mode = mode }, launchConfigurationType)
            .WithArgs(resourceArgs);

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345"
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var distributedApplicationOptions = new DistributedApplicationOptions { AssemblyName = typeof(DcpExecutorTests).Assembly.FullName };
        var expectedConfiguration = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyConfigurationAttribute>(typeof(DcpExecutorTests).Assembly)?.Configuration;
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration, distributedApplicationOptions: distributedApplicationOptions);

        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "proj");
        Assert.Equal(ExecutionType.Process, exe.Spec.ExecutionType);
        Assert.Equal("dotnet", exe.Spec.ExecutablePath);
        Assert.Equal("/tmp/mauiapp", exe.Spec.WorkingDirectory);
        var expectedArgs = new List<string> { resourceArgs[0] };
        if (!string.IsNullOrEmpty(expectedConfiguration))
        {
            expectedArgs.AddRange(["--configuration", expectedConfiguration]);
        }
        expectedArgs.Add("--no-launch-profile");
        expectedArgs.AddRange(resourceArgs.Skip(1));

        Assert.Equal(expectedArgs, exe.Spec.Args);
    }

    [Theory]
    [MemberData(nameof(MauiProjectLaunchConfigurationsForProcessExecution))]
    public async Task ProjectWithNonProjectAnnotationAndExecutableAnnotation_NoDebugSession_RunsInProcessWithResourceArgs(string launchConfigurationType, string[] resourceArgs)
    {
        var builder = DistributedApplication.CreateBuilder();

        var projectBuilder = builder.AddProject<TestProject>("proj", launchProfileName: null);
        var annotationToRemove = projectBuilder.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().FirstOrDefault();
        if (annotationToRemove is not null)
        {
            projectBuilder.Resource.Annotations.Remove(annotationToRemove);
        }
        projectBuilder
            .WithAnnotation(new ExecutableAnnotation
            {
                Command = "dotnet",
                WorkingDirectory = "/tmp/mauiapp"
            })
            .WithDebugSupport(mode => new ExecutableLaunchConfiguration(launchConfigurationType) { Mode = mode }, launchConfigurationType)
            .WithArgs(resourceArgs);

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var distributedApplicationOptions = new DistributedApplicationOptions { AssemblyName = typeof(DcpExecutorTests).Assembly.FullName };
        var expectedConfiguration = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyConfigurationAttribute>(typeof(DcpExecutorTests).Assembly)?.Configuration;
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, distributedApplicationOptions: distributedApplicationOptions);

        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "proj");
        Assert.Equal(ExecutionType.Process, exe.Spec.ExecutionType);
        Assert.Equal("dotnet", exe.Spec.ExecutablePath);
        Assert.Equal("/tmp/mauiapp", exe.Spec.WorkingDirectory);
        var expectedArgs = new List<string> { resourceArgs[0] };
        if (!string.IsNullOrEmpty(expectedConfiguration))
        {
            expectedArgs.AddRange(["--configuration", expectedConfiguration]);
        }
        expectedArgs.Add("--no-launch-profile");
        expectedArgs.AddRange(resourceArgs.Skip(1));

        Assert.Equal(expectedArgs, exe.Spec.Args);
    }

    [Fact]
    public async Task MauiProjectWithExecutableAnnotationAndSupportedLaunchConfiguration_RunsInIdeWithLaunchMetadata()
    {
        var builder = DistributedApplication.CreateBuilder();

        var projectBuilder = builder.AddProject<TestProject>("proj", launchProfileName: null);
        var annotationToRemove = projectBuilder.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().FirstOrDefault();
        if (annotationToRemove is not null)
        {
            projectBuilder.Resource.Annotations.Remove(annotationToRemove);
        }
        projectBuilder
            .WithAnnotation(new ExecutableAnnotation
            {
                Command = "dotnet",
                WorkingDirectory = "/tmp/mauiapp"
            })
            .WithDebugSupport(mode => new TestMauiLaunchConfiguration
            {
                Mode = mode,
                ProjectPath = "/tmp/mauiapp/MauiApp.csproj",
                TargetFramework = "net10.0-android",
                Platform = "android",
                TargetKind = "emulator",
                MsBuildProperties = new Dictionary<string, string>
                {
                    ["AdbTarget"] = "-e"
                }
            }, "maui")
            .WithArgs("run", "-f", "net10.0-android", "-p:AdbTarget=-e");

        var runSessionInfo = new RunSessionInfo
        {
            ProtocolsSupported = ["coreclr"],
            SupportedLaunchConfigurations = ["maui"]
        };

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(runSessionInfo),
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234",
            [KnownConfigNames.DebugSessionRunMode] = "Debug"
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var distributedApplicationOptions = new DistributedApplicationOptions { AssemblyName = typeof(DcpExecutorTests).Assembly.FullName };
        var expectedConfiguration = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyConfigurationAttribute>(typeof(DcpExecutorTests).Assembly)?.Configuration;
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration, distributedApplicationOptions: distributedApplicationOptions);

        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "proj");
        Assert.Equal(ExecutionType.IDE, exe.Spec.ExecutionType);
        Assert.Equal("dotnet", exe.Spec.ExecutablePath);
        Assert.Equal("/tmp/mauiapp", exe.Spec.WorkingDirectory);
        Assert.Null(exe.Spec.FallbackExecutionTypes);
        var expectedArgs = new List<string> { "run" };
        if (!string.IsNullOrEmpty(expectedConfiguration))
        {
            expectedArgs.AddRange(["--configuration", expectedConfiguration]);
        }
        expectedArgs.Add("--no-launch-profile");
        expectedArgs.AddRange(["-f", "net10.0-android", "-p:AdbTarget=-e"]);
        Assert.Equal(expectedArgs, exe.Spec.Args);

        Assert.True(exe.TryGetAnnotationAsObjectList<TestMauiLaunchConfiguration>(Executable.LaunchConfigurationsAnnotation, out var launchConfigs));
        var launchConfig = Assert.Single(launchConfigs);
        Assert.Equal("maui", launchConfig.Type);
        Assert.Equal(ExecutableLaunchMode.Debug, launchConfig.Mode);
        Assert.Equal("/tmp/mauiapp/MauiApp.csproj", launchConfig.ProjectPath);
        Assert.Equal("net10.0-android", launchConfig.TargetFramework);
        Assert.Equal("android", launchConfig.Platform);
        Assert.Equal("emulator", launchConfig.TargetKind);
        Assert.Equal("-e", launchConfig.MsBuildProperties!["AdbTarget"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MauiProjectWithLaunchArgsOverrideAndSupportedLaunchConfiguration_PreservesProjectMetadataAndAppliesMauiLaunchConfiguration(bool useContextOverload)
    {
        var builder = DistributedApplication.CreateBuilder();
        var projectBuilder = builder.AddProject<TestProject>("proj", launchProfileName: null);
        var projectResource = projectBuilder.Resource;
        var defaultDebugSupport = projectBuilder.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().FirstOrDefault();
        if (defaultDebugSupport is not null)
        {
            projectBuilder.Resource.Annotations.Remove(defaultDebugSupport);
        }

#pragma warning disable ASPIREPROJECTS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        projectBuilder.Resource.Annotations.Add(
            new ProjectLaunchArgsOverrideAnnotation(
                ["build", "--no-restore", "/t:Run", "-p:NoBuild=true"],
                leadingResourceArgumentToRemove: "run"));
#pragma warning restore ASPIREPROJECTS001

        var producerInvocationCount = 0;
        LaunchConfigurationCallbackContext? launchContext = null;

        if (useContextOverload)
        {
            projectBuilder.WithDebugSupport(
                context =>
                {
                    Interlocked.Increment(ref producerInvocationCount);
                    launchContext = context;
                    return Task.FromResult(CreateMauiLaunchConfiguration(context.Mode));
                },
                "maui");
        }
        else
        {
            projectBuilder.WithDebugSupport(
                mode =>
                {
                    Interlocked.Increment(ref producerInvocationCount);
                    return CreateMauiLaunchConfiguration(mode);
                },
                "maui");
        }

        projectBuilder.WithArgs("run", "-f", "net10.0-android");

        var debugSessionInfo = JsonSerializer.Serialize(new RunSessionInfo
        {
            ProtocolsSupported = ["coreclr"],
            SupportedLaunchConfigurations = ["maui"]
        });
        builder.Configuration[DcpExecutor.DebugSessionPortVar] = "12345";
        builder.Configuration[KnownConfigNames.DebugSessionInfo] = debugSessionInfo;
        builder.Configuration[KnownConfigNames.DebugSessionRunMode] = ExecutableLaunchMode.Debug;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DcpExecutor.DebugSessionPortVar] = "12345",
                [KnownConfigNames.DebugSessionInfo] = debugSessionInfo,
                [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234",
                [KnownConfigNames.DebugSessionRunMode] = ExecutableLaunchMode.Debug
            })
            .Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedApplicationOptions = new DistributedApplicationOptions { AssemblyName = typeof(DcpExecutorTests).Assembly.FullName };
        var expectedConfiguration = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyConfigurationAttribute>(typeof(DcpExecutorTests).Assembly)?.Configuration;
        var appExecutor = CreateAppExecutor(
            app.Services.GetRequiredService<DistributedApplicationModel>(),
            kubernetesService: kubernetesService,
            configuration: configuration,
            distributedApplicationOptions: distributedApplicationOptions);

        await appExecutor.RunApplicationAsync();

        var executable = GetCreatedExecutableForResource(kubernetesService, "proj");
        Assert.Equal(ExecutionType.Process, executable.Spec.ExecutionType);
        Assert.Equal(1, Volatile.Read(ref producerInvocationCount));
        var expectedArgs = new List<string>
        {
            "build",
            "--no-restore",
            "/t:Run",
            "-p:NoBuild=true",
            "TestProject"
        };
        if (!string.IsNullOrEmpty(expectedConfiguration))
        {
            expectedArgs.AddRange(["--configuration", expectedConfiguration]);
        }
        expectedArgs.AddRange(["-f", "net10.0-android"]);
        Assert.Equal(expectedArgs, executable.Spec.Args);
        Assert.True(executable.Metadata.Annotations.TryGetValue(
            Executable.LaunchConfigurationsAnnotation,
            out var launchConfigurationsJson));
        using var launchConfigurations = JsonDocument.Parse(launchConfigurationsJson);
        Assert.Collection(
            launchConfigurations.RootElement.EnumerateArray(),
            projectLaunchConfiguration =>
            {
                Assert.Equal(KnownLaunchConfigurationTypes.Project, projectLaunchConfiguration.GetProperty("type").GetString());
                Assert.Equal(ExecutableLaunchMode.Debug, projectLaunchConfiguration.GetProperty("mode").GetString());
                Assert.Equal("TestProject", projectLaunchConfiguration.GetProperty("project_path").GetString());
                Assert.True(projectLaunchConfiguration.GetProperty("disable_launch_profile").GetBoolean());
            },
            mauiLaunchConfiguration =>
            {
                Assert.Equal("maui", mauiLaunchConfiguration.GetProperty("type").GetString());
                Assert.Equal(ExecutableLaunchMode.Debug, mauiLaunchConfiguration.GetProperty("mode").GetString());
                Assert.Equal("/mauiapp/MauiApp.csproj", mauiLaunchConfiguration.GetProperty("project_path").GetString());
            });

        if (useContextOverload)
        {
            Assert.NotNull(launchContext);
            Assert.Equal(ExecutableLaunchMode.Debug, launchContext.Mode);
            Assert.Same(projectResource, launchContext.Resource);
        }

        static TestMauiLaunchConfiguration CreateMauiLaunchConfiguration(string mode) => new()
        {
            Mode = mode,
            ProjectPath = "/mauiapp/MauiApp.csproj",
            TargetFramework = "net10.0-android",
            Platform = "android",
            TargetKind = "emulator"
        };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MauiProjectWithLaunchArgsOverride_LaunchConfigurationProducerThrows_RemainsInProcessExecution(bool useContextOverload)
    {
        // The launch override already provides a runnable Process command. A custom launch producer can add
        // metadata in that mode, but a producer fault must not discard the process invocation.
        var builder = DistributedApplication.CreateBuilder();
        var projectBuilder = builder.AddProject<TestProject>("proj", launchProfileName: null);
        var defaultDebugSupport = projectBuilder.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().FirstOrDefault();
        if (defaultDebugSupport is not null)
        {
            projectBuilder.Resource.Annotations.Remove(defaultDebugSupport);
        }

#pragma warning disable ASPIREPROJECTS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        projectBuilder.Resource.Annotations.Add(
            new ProjectLaunchArgsOverrideAnnotation(
                ["build", "--no-restore", "/t:Run", "-p:NoBuild=true"],
                leadingResourceArgumentToRemove: "run"));
#pragma warning restore ASPIREPROJECTS001

        var producerInvocationCount = 0;
        var producerFailureMessage = useContextOverload
            ? "Test exception from async launch configuration producer"
            : "Test exception from launch configuration producer";
        if (useContextOverload)
        {
            projectBuilder.WithDebugSupport(
                async Task<TestMauiLaunchConfiguration> (context) =>
                {
                    Interlocked.Increment(ref producerInvocationCount);
                    await Task.Yield();
                    throw new InvalidOperationException(producerFailureMessage);
                },
                "maui");
        }
        else
        {
            projectBuilder.WithDebugSupport(
                TestMauiLaunchConfiguration (mode) =>
                {
                    Interlocked.Increment(ref producerInvocationCount);
                    throw new InvalidOperationException(producerFailureMessage);
                },
                "maui");
        }

        projectBuilder.WithArgs("run", "-f", "net10.0-android");

        var debugSessionInfo = JsonSerializer.Serialize(new RunSessionInfo
        {
            ProtocolsSupported = ["coreclr"],
            SupportedLaunchConfigurations = ["maui"]
        });
        builder.Configuration[DcpExecutor.DebugSessionPortVar] = "12345";
        builder.Configuration[KnownConfigNames.DebugSessionInfo] = debugSessionInfo;
        builder.Configuration[KnownConfigNames.DebugSessionRunMode] = ExecutableLaunchMode.Debug;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DcpExecutor.DebugSessionPortVar] = "12345",
                [KnownConfigNames.DebugSessionInfo] = debugSessionInfo,
                [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234",
                [KnownConfigNames.DebugSessionRunMode] = ExecutableLaunchMode.Debug
            })
            .Build();

        var kubernetesService = new TestKubernetesService();
        using var resourceLoggerService = new ResourceLoggerService();
        var failedResources = new ConcurrentQueue<IResource>();
        var events = new DcpExecutorEvents();
        events.Subscribe<OnResourceFailedToStartContext>(context =>
        {
            failedResources.Enqueue(context.Resource);
            return Task.CompletedTask;
        });
        using var app = builder.Build();
        var distributedApplicationOptions = new DistributedApplicationOptions { AssemblyName = typeof(DcpExecutorTests).Assembly.FullName };
        var appExecutor = CreateAppExecutor(
            app.Services.GetRequiredService<DistributedApplicationModel>(),
            kubernetesService: kubernetesService,
            configuration: configuration,
            distributedApplicationOptions: distributedApplicationOptions,
            resourceLoggerService: resourceLoggerService,
            events: events);

        await appExecutor.RunApplicationAsync();

        var executable = GetCreatedExecutableForResource(kubernetesService, "proj");
        Assert.Equal(ExecutionType.Process, executable.Spec.ExecutionType);
        Assert.Equal(1, Volatile.Read(ref producerInvocationCount));
        Assert.Empty(failedResources);

        var expectedArgs = new List<string>
        {
            "build",
            "--no-restore",
            "/t:Run",
            "-p:NoBuild=true",
            "TestProject"
        };
        if (GetTestAssemblyConfiguration() is { } configurationName)
        {
            expectedArgs.AddRange(["--configuration", configurationName]);
        }
        expectedArgs.AddRange(["-f", "net10.0-android"]);
        Assert.Equal(expectedArgs, executable.Spec.Args);

        Assert.True(executable.TryGetAnnotationAsObjectList<JsonElement>(
            Executable.LaunchConfigurationsAnnotation,
            out var launchConfigurations));
        var projectLaunchConfiguration = Assert.Single(launchConfigurations);
        Assert.Equal(
            KnownLaunchConfigurationTypes.Project,
            projectLaunchConfiguration.GetProperty("type").GetString());

        var logLines = new List<LogLine>();
        await foreach (var lines in resourceLoggerService.GetAllAsync(projectBuilder.Resource).DefaultTimeout())
        {
            logLines.AddRange(lines);
        }

        Assert.Contains(logLines, line =>
            !line.IsErrorMessage &&
            line.Content.Contains(
                "Failed to apply optional launch configuration metadata of type 'maui' for Process resource 'proj'. Continuing with Process execution.",
                StringComparison.Ordinal) &&
            line.Content.Contains(producerFailureMessage, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProjectResource_CustomIdeLaunch_OwnedDotnetToolRunArgsPreserveLaunchProfileArgs()
    {
        var builder = DistributedApplication.CreateBuilder();
        var projectBuilder = builder.AddProject<TestProjectWithLaunchProfileCommandLineArgs>("proj", launchProfileName: "http");
        var defaultAnnotation = projectBuilder.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().FirstOrDefault();
        if (defaultAnnotation is not null)
        {
            projectBuilder.Resource.Annotations.Remove(defaultAnnotation);
        }

        projectBuilder
            .WithArgs("app-arg")
            .WithLaunchToolArgs(
                static context =>
                {
                    context.Args.Add("tool");
                    context.Args.Add("run");
                    context.Args.Add("package");
                    context.Args.Add("--");
                },
                ownedByLaunchConfigurationType: "custom")
            .WithDebugSupport(
                mode => new ExecutableLaunchConfiguration("custom") { Mode = mode },
                "custom");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DcpExecutor.DebugSessionPortVar] = "12345",
                [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo { ProtocolsSupported = ["test"], SupportedLaunchConfigurations = ["custom"] })
            })
            .Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "proj");
        Assert.Equal(ExecutionType.IDE, exe.Spec.ExecutionType);
        Assert.Equal(["--profile-arg", "profile value", "app-arg"], exe.Spec.Args);
        Assert.Null(exe.Spec.FallbackExecutionTypes);

        Assert.True(exe.TryGetAnnotationAsObjectList<AppLaunchArgumentAnnotation>(CustomResource.ResourceAppArgsAnnotation, out var displayArgs));
        Assert.Equal(["tool", "run", "package", "--", "--profile-arg", "profile value", "app-arg"], displayArgs.Select(a => a.Argument));
        Assert.All(displayArgs.Take(4), argument => Assert.Null(argument.EffectiveArgumentIndex));
        AssertEffectiveArgumentIndexesMatchSpecArgs(displayArgs, exe.Spec.Args);
    }

    [Theory]
    [InlineData("run", false, null, null, null)]
    [InlineData("run", true, null, null, null)]
    [InlineData("watch", false, null, null, null)]
    [InlineData("watch", true, null, null, null)]
    [InlineData("run", false, "-d", null, null)]
    [InlineData("watch", false, "--diagnostics", null, null)]
    [InlineData("run", false, null, new string[] { "[env:ASPIRE_PREFIX_PROBE=1]" }, null)]
    [InlineData("run", false, "--diagnostics", new string[] { "[env:ASPIRE_PREFIX_PROBE=1]" }, null)]
    [InlineData("run", false, "--diagnostics", new string[] { "[env:ASPIRE_PREFIX_PROBE_A=1]", "[env:ASPIRE_PREFIX_PROBE_B=2]" }, null)]
    [InlineData("run", false, "--diagnostics", new string[] { "[env:ASPIRE_PREFIX_PROBE=1]" }, "app-arg")]
    public async Task ProjectResource_CustomIdeLaunch_ExecutableAnnotatedProjectPreservesLaunchProfileArgs(
        string launchVerb,
        bool useFullDotnetPath,
        string? sdkOption,
        string[]? environmentVariableDirectives,
        string? applicationArgument)
    {
        var builder = DistributedApplication.CreateBuilder();
        var dotnetCommand = useFullDotnetPath
            ? Path.GetFullPath(Path.Combine("test-dotnet-root", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))
            : "dotnet";
        var resourceArgs = new List<string>();
        if (environmentVariableDirectives is not null)
        {
            resourceArgs.AddRange(environmentVariableDirectives);
        }

        if (sdkOption is not null)
        {
            resourceArgs.Add(sdkOption);
        }

        resourceArgs.AddRange([launchVerb, "-f", "net10.0-ios"]);
        if (applicationArgument is not null)
        {
            resourceArgs.AddRange(["--", applicationArgument]);
        }

        var projectBuilder = builder.AddProject<TestProjectWithLaunchProfileCommandLineArgs>("proj", launchProfileName: "http");
        var defaultAnnotation = projectBuilder.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().FirstOrDefault();
        if (defaultAnnotation is not null)
        {
            projectBuilder.Resource.Annotations.Remove(defaultAnnotation);
        }

        projectBuilder
            .WithAnnotation(new ExecutableAnnotation
            {
                Command = dotnetCommand,
                WorkingDirectory = "/tmp/mauiapp"
            })
            .WithDebugSupport(
                mode => new TestMauiLaunchConfiguration
                {
                    Mode = mode,
                    ProjectPath = "/tmp/mauiapp/MauiApp.csproj",
                    TargetFramework = "net10.0-ios",
                    Platform = "ios",
                    TargetKind = "simulator"
                },
                "maui")
            .WithArgs([.. resourceArgs]);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DcpExecutor.DebugSessionPortVar] = "12345",
                [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo { ProtocolsSupported = ["coreclr"], SupportedLaunchConfigurations = ["maui"] })
            })
            .Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "proj");
        Assert.Equal(ExecutionType.IDE, exe.Spec.ExecutionType);
        Assert.Equal(dotnetCommand, exe.Spec.ExecutablePath);
        var expectedArgs = new List<string>();
        if (environmentVariableDirectives is not null)
        {
            expectedArgs.AddRange(environmentVariableDirectives);
        }

        if (sdkOption is not null)
        {
            expectedArgs.Add(sdkOption);
        }

        expectedArgs.Add(launchVerb);
        if (GetTestAssemblyConfiguration() is { } configurationName)
        {
            expectedArgs.AddRange(["--configuration", configurationName]);
        }

        expectedArgs.AddRange(["--no-launch-profile", "-f", "net10.0-ios", "--", "--profile-arg", "profile value"]);
        if (applicationArgument is not null)
        {
            expectedArgs.Add(applicationArgument);
        }

        Assert.Equal(expectedArgs, exe.Spec.Args);
        Assert.Null(exe.Spec.FallbackExecutionTypes);

        Assert.True(exe.TryGetAnnotationAsObjectList<AppLaunchArgumentAnnotation>(CustomResource.ResourceAppArgsAnnotation, out var displayArgs));
        var expectedDisplayArgs = new List<string>();
        if (environmentVariableDirectives is not null)
        {
            expectedDisplayArgs.AddRange(environmentVariableDirectives);
        }

        if (sdkOption is not null)
        {
            expectedDisplayArgs.Add(sdkOption);
        }

        expectedDisplayArgs.AddRange([launchVerb, "-f", "net10.0-ios", "--", "--profile-arg", "profile value"]);
        if (applicationArgument is not null)
        {
            expectedDisplayArgs.Add(applicationArgument);
        }

        Assert.Equal(expectedDisplayArgs, displayArgs.Select(a => a.Argument));
        AssertEffectiveArgumentIndexesMatchSpecArgs(displayArgs, exe.Spec.Args);
    }

    [Theory]
    [InlineData("run", true, false, null)]
    [InlineData("watch", true, false, null)]
    [InlineData("run", false, false, null)]
    [InlineData("watch", false, false, null)]
    [InlineData("run", false, true, null)]
    [InlineData("watch", false, true, null)]
    [InlineData("watch", false, false, "[env:ASPIRE_PREFIX_PROBE=1]")]
    [InlineData("run", false, false, "@options.rsp")]
    [InlineData("watch", false, false, "@options.rsp")]
    public async Task ProjectResource_CustomIdeLaunch_PreservesOpaqueDotnetApplicationArguments(
        string nonLeadingLaunchVerb,
        bool useExec,
        bool useRuntimeOptions,
        string? commandPrefix)
    {
        var builder = DistributedApplication.CreateBuilder();
        var projectBuilder = builder.AddProject<TestProjectWithLaunchProfileCommandLineArgs>("proj", launchProfileName: "http");
        var defaultAnnotation = projectBuilder.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().FirstOrDefault();
        if (defaultAnnotation is not null)
        {
            projectBuilder.Resource.Annotations.Remove(defaultAnnotation);
        }

        string[] resourceArgs = commandPrefix is not null
            ? [commandPrefix, nonLeadingLaunchVerb]
            : useRuntimeOptions
                ? ["--roll-forward", "LatestMajor", "app.dll", nonLeadingLaunchVerb]
                : useExec
                    ? ["exec", "app.dll", nonLeadingLaunchVerb]
                    : ["app.dll", nonLeadingLaunchVerb];
        projectBuilder
            .WithAnnotation(new ExecutableAnnotation
            {
                Command = "dotnet",
                WorkingDirectory = "/tmp/mauiapp"
            })
            .WithDebugSupport(
                mode => new TestMauiLaunchConfiguration
                {
                    Mode = mode,
                    ProjectPath = "/tmp/mauiapp/MauiApp.csproj",
                    TargetFramework = "net10.0-ios",
                    Platform = "ios",
                    TargetKind = "simulator"
                },
                "maui")
            .WithArgs(resourceArgs);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DcpExecutor.DebugSessionPortVar] = "12345",
                [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo { ProtocolsSupported = ["coreclr"], SupportedLaunchConfigurations = ["maui"] })
            })
            .Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "proj");
        Assert.Equal(ExecutionType.IDE, exe.Spec.ExecutionType);
        string[] expectedArgs = ["--profile-arg", "profile value", .. resourceArgs];
        Assert.Equal(expectedArgs, exe.Spec.Args);
        Assert.Null(exe.Spec.FallbackExecutionTypes);

        Assert.True(exe.TryGetAnnotationAsObjectList<AppLaunchArgumentAnnotation>(CustomResource.ResourceAppArgsAnnotation, out var displayArgs));
        Assert.Equal(expectedArgs, displayArgs.Select(a => a.Argument));
        AssertEffectiveArgumentIndexesMatchSpecArgs(displayArgs, exe.Spec.Args);
    }

    [Theory]
    [InlineData(new string[] { "exec", "app.dll", "run" }, true)]
    [InlineData(new string[] { "app.dll", "run" }, true)]
    [InlineData(new string[] { "[env:ASPIRE_PREFIX_PROBE=1]", "watch" }, false)]
    public async Task ProjectResource_CustomIdeLaunch_ExecutableAnnotatedDotnetApplicationDoesNotConfigureRuntimeFallback(
        string[] resourceArgs,
        bool _)
    {
        var builder = DistributedApplication.CreateBuilder();
        var projectBuilder = builder.AddProject<TestProject>("proj", launchProfileName: null);
        var defaultAnnotation = projectBuilder.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().FirstOrDefault();
        if (defaultAnnotation is not null)
        {
            projectBuilder.Resource.Annotations.Remove(defaultAnnotation);
        }

        projectBuilder
            .WithAnnotation(new ExecutableAnnotation
            {
                Command = "dotnet",
                WorkingDirectory = "/tmp/mauiapp"
            })
            .WithDebugSupport(
                mode => new ExecutableLaunchConfiguration("custom") { Mode = mode },
                "custom")
            .WithArgs(resourceArgs);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DcpExecutor.DebugSessionPortVar] = "12345",
                [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo { ProtocolsSupported = ["coreclr"], SupportedLaunchConfigurations = ["custom"] })
            })
            .Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "proj");
        Assert.Equal(ExecutionType.IDE, exe.Spec.ExecutionType);
        Assert.Equal(resourceArgs, exe.Spec.Args);
        Assert.Null(exe.Spec.FallbackExecutionTypes);
    }

    [Fact]
    public async Task ProjectWithNonProjectAnnotationAndExecutableAnnotation_LaunchProfileArgsStayAfterDotnetRunArgs()
    {
        var builder = DistributedApplication.CreateBuilder();

        var projectBuilder = builder.AddProject<TestProjectWithLaunchProfileCommandLineArgs>("proj", launchProfileName: "http");
        var annotationToRemove = projectBuilder.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().FirstOrDefault();
        if (annotationToRemove is not null)
        {
            projectBuilder.Resource.Annotations.Remove(annotationToRemove);
        }
        projectBuilder
            .WithAnnotation(new ExecutableAnnotation
            {
                Command = "dotnet",
                WorkingDirectory = "/tmp/mauiapp"
            })
            .WithDebugSupport(mode => new ExecutableLaunchConfiguration("maui") { Mode = mode }, "maui")
            .WithArgs("run", "-f", "net10.0-ios", "-p:_DeviceName=:v2:udid=E25BBE37-69BA-4720-B6FD-D54C97791E79");

        var runSessionInfo = new RunSessionInfo
        {
            ProtocolsSupported = ["coreclr"],
            SupportedLaunchConfigurations = ["project"]
        };

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(runSessionInfo),
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234"
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var distributedApplicationOptions = new DistributedApplicationOptions { AssemblyName = typeof(DcpExecutorTests).Assembly.FullName };
        var expectedConfiguration = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyConfigurationAttribute>(typeof(DcpExecutorTests).Assembly)?.Configuration;
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration, distributedApplicationOptions: distributedApplicationOptions);

        await appExecutor.RunApplicationAsync();

        var expectedArgs = new List<string> { "run" };
        if (!string.IsNullOrEmpty(expectedConfiguration))
        {
            expectedArgs.AddRange(["--configuration", expectedConfiguration]);
        }
        expectedArgs.AddRange([
            "--no-launch-profile",
            "-f",
            "net10.0-ios",
            "-p:_DeviceName=:v2:udid=E25BBE37-69BA-4720-B6FD-D54C97791E79",
            "--",
            "--profile-arg",
            "profile value"
        ]);

        var exe = GetCreatedExecutableForResource(kubernetesService, "proj");
        Assert.Equal(ExecutionType.Process, exe.Spec.ExecutionType);
        Assert.Equal(expectedArgs, exe.Spec.Args);
    }

    [Fact]
    public async Task ProjectWithNonProjectAnnotation_NoDebugSession_RunsInProcess()
    {
        // Guard: When there's no debug session (CLI scenario, no DEBUG_SESSION_PORT),
        // projects with custom annotations should fall to Process execution.
        var builder = DistributedApplication.CreateBuilder();

        var projectBuilder = builder.AddProject<TestProject>("proj", launchProfileName: null);
        var annotationToRemove = projectBuilder.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().FirstOrDefault();
        if (annotationToRemove is not null)
        {
            projectBuilder.Resource.Annotations.Remove(annotationToRemove);
        }
        projectBuilder.WithDebugSupport(mode => new ExecutableLaunchConfiguration("azure-functions") { Mode = mode }, "azure-functions");

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);

        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "proj");
        Assert.Equal(ExecutionType.Process, exe.Spec.ExecutionType);
    }

    [Fact]
    public async Task ProjectWithNonProjectAnnotation_VSCodeWithMatchingSupport_RunsInIde()
    {
        // When VS Code extension sends DEBUG_SESSION_INFO with SupportedLaunchConfigurations
        // that DO include the custom type, the resource should get IDE execution via the
        // primary SupportsDebugging path (not the VS fallback).
        var builder = DistributedApplication.CreateBuilder();

        var projectBuilder = builder.AddProject<TestProject>("proj", launchProfileName: null);
        var annotationToRemove = projectBuilder.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().FirstOrDefault();
        if (annotationToRemove is not null)
        {
            projectBuilder.Resource.Annotations.Remove(annotationToRemove);
        }
        projectBuilder.WithDebugSupport(mode => new ExecutableLaunchConfiguration("azure-functions") { Mode = mode }, "azure-functions");

        var runSessionInfo = new RunSessionInfo
        {
            ProtocolsSupported = ["coreclr"],
            SupportedLaunchConfigurations = ["project", "azure-functions"]
        };

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(runSessionInfo),
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234"
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "proj");
        Assert.Equal(ExecutionType.IDE, exe.Spec.ExecutionType);
    }

    [Fact]
    public async Task StandardAndCustomProjects_VSScenario_BothRunInIde()
    {
        // End-to-end VS scenario: a standard project and a custom-debug-type project both
        // in the same AppHost. Both should get IDE execution when launched from VS
        // (DEBUG_SESSION_PORT set, no DEBUG_SESSION_INFO).
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        builder.AddProject<Projects.ServiceA>("standard-project");

        var customProject = builder.AddProject<TestProject>("custom-project", launchProfileName: null);
        var annotationToRemove = customProject.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().FirstOrDefault();
        if (annotationToRemove is not null)
        {
            customProject.Resource.Annotations.Remove(annotationToRemove);
        }
        customProject.WithDebugSupport(mode => new ExecutableLaunchConfiguration("azure-functions") { Mode = mode }, "azure-functions");

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        await appExecutor.RunApplicationAsync();

        var standardExe = GetCreatedExecutableForResource(kubernetesService, "standard-project");
        Assert.Equal(ExecutionType.IDE, standardExe.Spec.ExecutionType);

        var customExe = GetCreatedExecutableForResource(kubernetesService, "custom-project");
        Assert.Equal(ExecutionType.IDE, customExe.Spec.ExecutionType);
        Assert.Null(customExe.Spec.Args);
        Assert.Null(customExe.Spec.FallbackExecutionTypes);

        Assert.True(customExe.TryGetAnnotationAsObjectList<ProjectLaunchConfiguration>(Executable.LaunchConfigurationsAnnotation, out var launchConfigs));
        Assert.Single(launchConfigs);
        Assert.Equal("project", launchConfigs[0].Type);
    }

    [Fact]
    public async Task StandardAndCustomProjects_VSCodeScenario_BothRunInIde()
    {
        // Combined VS Code scenario for class library projects:
        // VS Code extension sends SupportedLaunchConfigurations=["azure-functions"] (without "project").
        // A standard project (type "project") falls to Process execution because the IDE explicitly
        // did not advertise project support — the AppHost spawns dotnet itself.
        // A project with "azure-functions" annotation gets IDE (explicit match).
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        builder.AddProject<Projects.ServiceA>("standard-project");

        var customProject = builder.AddProject<TestProject>("functions-project", launchProfileName: null);
        var annotationToRemove = customProject.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().FirstOrDefault();
        if (annotationToRemove is not null)
        {
            customProject.Resource.Annotations.Remove(annotationToRemove);
        }
        customProject.WithDebugSupport(mode => new ExecutableLaunchConfiguration("azure-functions") { Mode = mode }, "azure-functions");

        var runSessionInfo = new RunSessionInfo
        {
            ProtocolsSupported = ["coreclr"],
            SupportedLaunchConfigurations = ["azure-functions"]
        };

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(runSessionInfo),
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234"
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        await appExecutor.RunApplicationAsync();

        // Standard project: Process execution because the IDE did not advertise "project" support.
        var standardExe = GetCreatedExecutableForResource(kubernetesService, "standard-project");
        Assert.Equal(ExecutionType.Process, standardExe.Spec.ExecutionType);

        // Azure Functions project: IDE via explicit "azure-functions" support.
        var functionsExe = GetCreatedExecutableForResource(kubernetesService, "functions-project");
        Assert.Equal(ExecutionType.IDE, functionsExe.Spec.ExecutionType);
    }

    [Fact]
    public async Task ProjectWithNonProjectAnnotation_VSCompatibilityLaunch_UsesApplicationArgumentsOnly()
    {
        // VS falls back to a project launch configuration for custom project types without DEBUG_SESSION_INFO.
        // Ordinary resource arguments are application arguments, so `dotnet app-arg` is not a runnable fallback.
        var builder = DistributedApplication.CreateBuilder();

        var projectBuilder = builder.AddProject<TestProject>("proj", launchProfileName: null);
        var annotationToRemove = projectBuilder.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().FirstOrDefault();
        if (annotationToRemove is not null)
        {
            projectBuilder.Resource.Annotations.Remove(annotationToRemove);
        }
        projectBuilder
            .WithArgs("app-arg")
            .WithDebugSupport(mode => new ExecutableLaunchConfiguration("azure-functions") { Mode = mode }, "azure-functions");

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "proj");
        Assert.Equal(ExecutionType.IDE, exe.Spec.ExecutionType);
        Assert.Equal(["app-arg"], exe.Spec.Args);
        Assert.Null(exe.Spec.FallbackExecutionTypes);
    }

    [Theory]
    [InlineData()]
    [InlineData("alias1", "alias2")]
    public async Task ContainerNetworkAliases(params string[]? aliases)
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var ctr = builder.AddContainer("mycontainer", "myimage");
        foreach (var alias in aliases ?? Array.Empty<string>())
        {
            ctr.WithContainerNetworkAlias(alias);
        }

        var kubernetesService = new TestKubernetesService();

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);

        // Act
        await appExecutor.RunApplicationAsync();

        // Assert
        var container = Assert.Single(kubernetesService.CreatedResources.OfType<Container>());
        Assert.NotNull(container.Spec.Networks);
        var network = Assert.Single(container.Spec.Networks);
        Assert.NotNull(network.Aliases);
        Assert.Equal(2 + (aliases?.Length ?? 0), network.Aliases.Count);
        Assert.Contains("mycontainer", network.Aliases);
        Assert.Contains("mycontainer.dev.internal", network.Aliases);
        foreach (var alias in aliases ?? Array.Empty<string>())
        {
            Assert.Contains(alias, network.Aliases);
        }
    }

    [Fact]
    public async Task ProjectExecutable_NoSupportsDebuggingAnnotation_InDebugSession_RunsInIdeMode()
    {
        // ProjectResource subclasses added via AddResource (not AddProject) may not have
        // a SupportsDebuggingAnnotation (e.g. third-party integrations). When in a debug session, these
        // should still default to IDE execution with ProjectLaunchConfiguration — matching
        // the pre-13.2 behavior. External integrations should not be forced to call the
        // experimental WithDebugSupport API to get basic IDE execution.
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        // Add project but ensure it doesn't have SupportsDebuggingAnnotation
        var projectBuilder = builder.AddProject<Projects.ServiceA>("ServiceA", launchProfileName: null);
        // Remove the SupportsDebuggingAnnotation that AddProject adds by default
        var annotationToRemove = projectBuilder.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().FirstOrDefault();
        if (annotationToRemove is not null)
        {
            projectBuilder.Resource.Annotations.Remove(annotationToRemove);
        }

        // Simulate debug session port to indicate we're in a debug session
        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234"
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "ServiceA");
        Assert.Equal(ExecutionType.IDE, exe.Spec.ExecutionType);
        Assert.Null(exe.Spec.FallbackExecutionTypes);

        Assert.True(exe.TryGetAnnotationAsObjectList<ProjectLaunchConfiguration>(Executable.LaunchConfigurationsAnnotation, out var launchConfigs));
        Assert.Single(launchConfigs);
        Assert.Equal("project", launchConfigs[0].Type);
    }

    [Fact]
    public async Task FileBasedProjectResource_InDebugSession_UsesIdeWithoutProcessFallback()
    {
        var builder = DistributedApplication.CreateBuilder();
        var projectPath = Path.Combine("src", "app.cs");
        builder.AddResource(new ProjectResource("file-project"))
            .WithAnnotation(new TestFileBasedProject(projectPath));

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "file-project");
        Assert.Equal(ExecutionType.IDE, exe.Spec.ExecutionType);
        // File-based projects used to keep a `dotnet run --file` candidate for Process fallback.
        // IDE-only launch is now intentional, so no second command remains in the rendered spec.
        Assert.Null(exe.Spec.Args);
        Assert.Null(exe.Spec.FallbackExecutionTypes);

        Assert.True(exe.TryGetProjectLaunchConfiguration(out var launchConfiguration));
        Assert.Equal(projectPath, launchConfiguration.ProjectPath);
        Assert.Equal(KnownLaunchConfigurationTypes.Project, launchConfiguration.Type);
    }

    [Fact]
    public async Task ProjectExecutable_AsyncLaunchConfigurationProducer_IsAwaitedDuringCreate()
    {
        // Project launch configuration producers run after the execution configuration has been resolved.
        // A producer that genuinely suspends must still complete before the executable is handed to DCP.
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        var projectBuilder = builder.AddProject<Projects.ServiceA>("ServiceA", launchProfileName: null);
        projectBuilder.WithDebugSupport(
            async (mode, _) =>
            {
                // Yield so the producer completes asynchronously rather than returning an already-completed task.
                await Task.Yield();
                return new ProjectLaunchConfiguration { ProjectPath = "AsyncProducerPath", Mode = mode, LaunchProfile = "async-profile" };
            },
            KnownLaunchConfigurationTypes.Project);

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo { ProtocolsSupported = ["test"], SupportedLaunchConfigurations = ["project"] }),
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234",
            [KnownConfigNames.DebugSessionRunMode] = ExecutableLaunchMode.Debug
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "ServiceA");
        Assert.Equal(ExecutionType.IDE, exe.Spec.ExecutionType);
        Assert.True(exe.TryGetProjectLaunchConfiguration(out var plc));
        Assert.Equal("AsyncProducerPath", plc.ProjectPath);
        Assert.Equal("async-profile", plc.LaunchProfile);
        Assert.Equal(ExecutableLaunchMode.Debug, plc.Mode);
    }

    [Fact]
    public async Task PlainExecutable_AsyncLaunchConfigurationProducer_IsAwaitedDuringCreate()
    {
        // The companion to ProjectExecutable_AsyncLaunchConfigurationProducer_IsAwaitedDuringPrepare: a
        // non-"project" launch configuration is applied when the Executable is created (after endpoints are
        // allocated), which is the other producer call site.
        var builder = DistributedApplication.CreateBuilder();

        var debuggableExecutable = new TestExecutableResource("test-working-directory");
        builder.AddResource(debuggableExecutable).WithDebugSupport(
            async (mode, _) =>
            {
                await Task.Yield();
                return new ExecutableLaunchConfiguration("test") { Mode = mode };
            },
            "test");

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo { ProtocolsSupported = ["test"], SupportedLaunchConfigurations = ["test"] }),
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234",
            [KnownConfigNames.DebugSessionRunMode] = ExecutableLaunchMode.Debug
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "TestExecutable");
        Assert.Equal(ExecutionType.IDE, exe.Spec.ExecutionType);
        Assert.True(exe.TryGetAnnotationAsObjectList<ExecutableLaunchConfiguration>(Executable.LaunchConfigurationsAnnotation, out var launchConfigs));
        var launchConfig = Assert.Single(launchConfigs);
        Assert.Equal("test", launchConfig.Type);
        Assert.Equal(ExecutableLaunchMode.Debug, launchConfig.Mode);
    }

    [Fact]
    public async Task PlainExecutable_AsyncLaunchConfigurationProducerFaults_FailsResource()
    {
        // An async producer that faults after suspending must surface through the resource-start failure path.
        var builder = DistributedApplication.CreateBuilder();

        var debuggableExecutable = new TestExecutableResource("test-working-directory");
        builder.AddResource(debuggableExecutable).WithDebugSupport<TestExecutableResource, ExecutableLaunchConfiguration>(
            async (_, _) =>
            {
                await Task.Yield();
                throw new InvalidOperationException("Test exception from async launch configuration producer");
            },
            "test");

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo { ProtocolsSupported = ["test"], SupportedLaunchConfigurations = ["test"] }),
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        var failedResources = new List<IResource>();
        var events = new DcpExecutorEvents();
        events.Subscribe<OnResourceFailedToStartContext>(context =>
        {
            failedResources.Add(context.Resource);
            return Task.CompletedTask;
        });
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(
            distributedAppModel,
            kubernetesService: kubernetesService,
            configuration: configuration,
            events: events);

        await appExecutor.RunApplicationAsync();

        Assert.Empty(GetCreatedExecutablesForResource(kubernetesService, "TestExecutable"));
        Assert.Same(debuggableExecutable, Assert.Single(failedResources));
    }

    [Fact]
    public async Task ProjectExecutable_WithLaunchArgsOverride_InDebugSession_RunsInProcessMode()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        var projectBuilder = builder.AddProject<Projects.ServiceA>("ServiceA", launchProfileName: null);
#pragma warning disable ASPIREPROJECTS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        projectBuilder.Resource.Annotations.Add(new ProjectLaunchArgsOverrideAnnotation(["build", "/t:Run"]));
#pragma warning restore ASPIREPROJECTS001

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234"
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "ServiceA");
        Assert.Equal(ExecutionType.Process, exe.Spec.ExecutionType);
        Assert.Null(exe.Spec.FallbackExecutionTypes);

        Assert.Collection(
            exe.Spec.Args!,
            arg => Assert.Equal("build", arg),
            arg => Assert.Equal("/t:Run", arg),
            arg => Assert.EndsWith("ServiceA.csproj", arg, StringComparison.Ordinal),
            arg => Assert.Equal("--configuration", arg),
            arg => Assert.Equal(GetTestAssemblyConfiguration(), arg));
    }

    [Fact]
    public async Task ProjectExecutable_WithLaunchArgsOverride_AndExecutableAnnotatedSdkRunArgs_DoesNotMutateRunArgs()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        var projectBuilder = builder.AddProject<Projects.ServiceA>("ServiceA")
            .WithAnnotation(new ExecutableAnnotation
            {
                Command = "dotnet",
                WorkingDirectory = "/tmp/mauiapp"
            })
            .WithArgs("run", "-f", "net10.0-ios");
#pragma warning disable ASPIREPROJECTS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        projectBuilder.Resource.Annotations.Add(new ProjectLaunchArgsOverrideAnnotation(["build", "/t:Run"]));
#pragma warning restore ASPIREPROJECTS001

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);

        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "ServiceA");

        Assert.Equal(ExecutionType.Process, exe.Spec.ExecutionType);
        Assert.Collection(
            exe.Spec.Args!,
            arg => Assert.Equal("build", arg),
            arg => Assert.Equal("/t:Run", arg),
            arg => Assert.EndsWith("ServiceA.csproj", arg, StringComparison.Ordinal),
            arg => Assert.Equal("--configuration", arg),
            arg => Assert.Equal(GetTestAssemblyConfiguration(), arg),
            arg => Assert.Equal("run", arg),
            arg => Assert.Equal("-f", arg),
            arg => Assert.Equal("net10.0-ios", arg));
        Assert.DoesNotContain("--no-launch-profile", exe.Spec.Args!);
    }

    [Fact]
    public async Task ProjectExecutable_WithLaunchArgsOverride_AndLeadingResourceArgumentToRemove_DropsRunBeforeExecuting()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        var projectBuilder = builder.AddProject<Projects.ServiceA>("ServiceA")
            .WithArgs("run", "-f", "net10.0-ios");
#pragma warning disable ASPIREPROJECTS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        projectBuilder.Resource.Annotations.Add(new ProjectLaunchArgsOverrideAnnotation(["build", "/t:Run"], leadingResourceArgumentToRemove: "run"));
#pragma warning restore ASPIREPROJECTS001

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);

        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "ServiceA");

        Assert.Equal(ExecutionType.Process, exe.Spec.ExecutionType);
        Assert.Collection(
            exe.Spec.Args!,
            arg => Assert.Equal("build", arg),
            arg => Assert.Equal("/t:Run", arg),
            arg => Assert.EndsWith("ServiceA.csproj", arg, StringComparison.Ordinal),
            arg => Assert.Equal("--configuration", arg),
            arg => Assert.Equal(GetTestAssemblyConfiguration(), arg),
            arg => Assert.Equal("-f", arg),
            arg => Assert.Equal("net10.0-ios", arg));
    }

    [Fact]
    public async Task ProjectExecutable_WithLaunchArgsOverride_EmptyLaunchToolArgsKeepOverride()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        var projectBuilder = builder.AddProject<Projects.ServiceA>("ServiceA")
            .WithArgs("run", "-f", "net10.0-ios")
            .WithLaunchToolArgs(static _ => { });
#pragma warning disable ASPIREPROJECTS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        projectBuilder.Resource.Annotations.Add(new ProjectLaunchArgsOverrideAnnotation(["build", "/t:Run"], leadingResourceArgumentToRemove: "run"));
#pragma warning restore ASPIREPROJECTS001

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);

        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "ServiceA");
        Assert.Equal(ExecutionType.Process, exe.Spec.ExecutionType);
        Assert.Collection(
            exe.Spec.Args!,
            arg => Assert.Equal("build", arg),
            arg => Assert.Equal("/t:Run", arg),
            arg => Assert.EndsWith("ServiceA.csproj", arg, StringComparison.Ordinal),
            arg => Assert.Equal("--configuration", arg),
            arg => Assert.Equal(GetTestAssemblyConfiguration(), arg),
            arg => Assert.Equal("-f", arg),
            arg => Assert.Equal("net10.0-ios", arg));
    }

    [Fact]
    public async Task ProjectExecutable_WithLaunchArgsOverride_NonEmptyLaunchToolArgsReplaceOverride()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        var projectBuilder = builder.AddProject<Projects.ServiceA>("ServiceA", launchProfileName: null)
            .WithArgs("-f", "net10.0-ios")
            .WithLaunchToolArgs(static ctx => ctx.Args.Add("run"), showInCommandLine: false);
#pragma warning disable ASPIREPROJECTS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        projectBuilder.Resource.Annotations.Add(new ProjectLaunchArgsOverrideAnnotation(["build", "/t:Run"], leadingResourceArgumentToRemove: "run"));
#pragma warning restore ASPIREPROJECTS001

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);

        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "ServiceA");

        Assert.Equal(ExecutionType.Process, exe.Spec.ExecutionType);
        Assert.Collection(
            exe.Spec.Args!,
            arg => Assert.Equal("run", arg),
            arg => Assert.Equal("-f", arg),
            arg => Assert.Equal("net10.0-ios", arg));

        Assert.True(exe.TryGetAnnotationAsObjectList<AppLaunchArgumentAnnotation>(CustomResource.ResourceAppArgsAnnotation, out var displayArgs));
        Assert.Collection(displayArgs,
            arg =>
            {
                Assert.Equal("-f", arg.Argument);
                Assert.Equal(1, arg.EffectiveArgumentIndex);
            },
            arg =>
            {
                Assert.Equal("net10.0-ios", arg.Argument);
                Assert.Equal(2, arg.EffectiveArgumentIndex);
            });
        AssertEffectiveArgumentIndexesMatchSpecArgs(displayArgs, exe.Spec.Args);
    }

    [Fact]
    public async Task ProjectExecutable_WithLaunchArgsOverride_AndPersistentLifetime_RunsOverrideInProcessMode()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        var projectBuilder = builder.AddProject<Projects.ServiceA>("ServiceA", launchProfileName: null)
            .WithPersistentLifetime();
#pragma warning disable ASPIREPROJECTS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        projectBuilder.Resource.Annotations.Add(new ProjectLaunchArgsOverrideAnnotation(["build", "/t:Run"]));
#pragma warning restore ASPIREPROJECTS001

        var configDict = new Dictionary<string, string?>
        {
            ["AppHost:Sha256"] = "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "ServiceA");

        Assert.True(exe.Spec.Persistent);
        Assert.Equal(ExecutionType.Process, exe.Spec.ExecutionType);

        Assert.Collection(
            exe.Spec.Args!,
            arg => Assert.Equal("build", arg),
            arg => Assert.Equal("/t:Run", arg),
            arg => Assert.EndsWith("ServiceA.csproj", arg, StringComparison.Ordinal),
            arg => Assert.Equal("--configuration", arg),
            arg => Assert.Equal(GetTestAssemblyConfiguration(), arg));
    }

    [Fact]
    public async Task ProjectExecutable_NoSupportsDebuggingAnnotation_NoDebugSession_RunsInProcessMode()
    {
        // When there's no debug session (CLI scenario), projects without annotations
        // should still run in Process mode.
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(DistributedApplicationTests).Assembly.FullName
        });

        var projectBuilder = builder.AddProject<Projects.ServiceA>("ServiceA", launchProfileName: null);
        var annotationToRemove = projectBuilder.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().FirstOrDefault();
        if (annotationToRemove is not null)
        {
            projectBuilder.Resource.Annotations.Remove(annotationToRemove);
        }

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);

        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "ServiceA");
        Assert.Equal(ExecutionType.Process, exe.Spec.ExecutionType);
    }

    [Fact]
    public async Task ProjectExecutable_NoAnnotation_ExecutableLaunchProfile_InDebugSession_RunsInIdeMode()
    {
        // Class library projects with commandName=Executable launch profiles (e.g. AWS Lambda
        // using "dotnet exec ...") should get IDE execution so both VS and VS Code can debug them.
        // VS natively handles Executable command profiles; VS Code's extension detects the
        // Executable commandName and uses the profile's executablePath + args.
        var builder = DistributedApplication.CreateBuilder();

        var projectBuilder = builder.AddProject<TestProjectWithExecutableLaunchProfile>("TestFunction",
            launchProfileName: "Aspire_TestFunction");
        var annotationToRemove = projectBuilder.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().FirstOrDefault();
        if (annotationToRemove is not null)
        {
            projectBuilder.Resource.Annotations.Remove(annotationToRemove);
        }

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "TestFunction");
        Assert.Equal(ExecutionType.IDE, exe.Spec.ExecutionType);
        Assert.Null(exe.Spec.FallbackExecutionTypes);

        Assert.True(exe.TryGetAnnotationAsObjectList<ProjectLaunchConfiguration>(Executable.LaunchConfigurationsAnnotation, out var launchConfigs));
        Assert.Single(launchConfigs);
        Assert.Equal("Aspire_TestFunction", launchConfigs[0].LaunchProfile);

        Assert.Null(exe.Spec.Args);
    }

    [Fact]
    public async Task ProjectExecutable_NoAnnotation_ProjectLaunchProfile_InDebugSession_RunsInIdeMode()
    {
        // When a project without SupportsDebuggingAnnotation has a normal Project launch profile
        // (not Executable), it should still get IDE execution in a debug session.
        var builder = DistributedApplication.CreateBuilder();

        var projectBuilder = builder.AddProject<TestProjectWithLaunchSettings>("proj", launchProfileName: "http");
        var annotationToRemove = projectBuilder.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().FirstOrDefault();
        if (annotationToRemove is not null)
        {
            projectBuilder.Resource.Annotations.Remove(annotationToRemove);
        }

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "proj");
        // Should be IDE, because it's a normal Project profile
        Assert.Equal(ExecutionType.IDE, exe.Spec.ExecutionType);
        Assert.Null(exe.Spec.FallbackExecutionTypes);
    }

    [Fact]
    public async Task DotnetProjectExecutable_InDebugSession_GetsIdeExecutionWithProjectLaunchConfig()
    {
        // A plain ExecutableResource that carries IProjectMetadata + a "project" SupportsDebuggingAnnotation
        // (e.g. DotnetProjectResource, which launches `dotnet run --project`) must be launched/debugged like
        // AddProject: IDE execution with a ProjectLaunchConfiguration (project_path + launch profile) so F5 works.
        var builder = DistributedApplication.CreateBuilder();

        var resource = new TestDotnetProjectExecutableResource("test-working-directory");
        builder.AddResource(resource)
            .WithAnnotation(new TestProjectWithLaunchSettings())
            .WithAnnotation(new LaunchProfileAnnotation("http"))
            .WithDebugSupport(mode => ProjectLaunchConfigurationFactory.Create(resource, mode), KnownLaunchConfigurationTypes.Project);

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo { ProtocolsSupported = ["test"], SupportedLaunchConfigurations = ["project"] }),
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "TestDotnetProject");
        Assert.Equal(ExecutionType.IDE, exe.Spec.ExecutionType);
        Assert.Null(exe.Spec.FallbackExecutionTypes);

        Assert.True(exe.TryGetProjectLaunchConfiguration(out var plc));
        Assert.Equal("TestProjectWithLaunchSettings", plc.ProjectPath);
        Assert.Equal("http", plc.LaunchProfile);
        Assert.Equal(ExecutableLaunchMode.NoDebug, plc.Mode);
    }

    [Fact]
    public void GetResourceType_DcpExecutable_DelegatesToAppModelClassifier()
    {
        // Regression guard for the DCP resource-type classifier. A DotnetProjectResource is an ExecutableResource
        // that carries IProjectMetadata, so DCP realizes it as an Executable (not a Container). The dashboard
        // snapshot classifies it as "Project" (via ResourceExtensions.GetResourceType); DcpExecutor.GetResourceType
        // must agree, otherwise the same resource reports "Executable" in DCP create/start/watch events and
        // profiling telemetry while showing "Project" everywhere else. A plain ExecutableResource must still
        // classify as "Executable".
        var dcpExecutable = Executable.Create("test-exe", "dotnet");

        var dotnetProject = new TestDotnetProjectExecutableResource("test-working-directory");
        dotnetProject.Annotations.Add(new TestProjectWithLaunchSettings());
        Assert.Equal(KnownResourceTypes.Project, DcpExecutor.GetResourceType(dcpExecutable, dotnetProject));

        var plainExecutable = new TestExecutableResource("test-working-directory");
        Assert.Equal(KnownResourceTypes.Executable, DcpExecutor.GetResourceType(dcpExecutable, plainExecutable));

        // A DotnetToolResource is also realized as a DCP Executable but the app-model classifier reports "Tool"
        // so the dashboard can render it distinctly. ApplicationOrchestrator.OnResourceStarting handles "Tool" like an
        // executable so the resource still transitions to the Starting state.
#pragma warning disable ASPIREDOTNETTOOL // DotnetToolResource is experimental.
        var dotnetTool = new DotnetToolResource("test-tool", "SomePackage.Id");
#pragma warning restore ASPIREDOTNETTOOL
        Assert.Equal(KnownResourceTypes.Tool, DcpExecutor.GetResourceType(dcpExecutable, dotnetTool));
    }

    [Fact]
    public async Task DotnetProjectExecutable_ProjectLaunchUnsupported_RunsInProcess()
    {
        // When the IDE does not advertise "project" support, the resource should run as a plain process with
        // no ProjectLaunchConfiguration applied.
        var builder = DistributedApplication.CreateBuilder();

        var resource = new TestDotnetProjectExecutableResource("test-working-directory");
        builder.AddResource(resource)
            .WithAnnotation(new TestProjectWithLaunchSettings())
            .WithDebugSupport(mode => new ProjectLaunchConfiguration { ProjectPath = "TestProjectWithLaunchSettings", Mode = mode }, "project");

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo { ProtocolsSupported = ["test"], SupportedLaunchConfigurations = ["python"] }),
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "TestDotnetProject");
        Assert.Equal(ExecutionType.Process, exe.Spec.ExecutionType);
        Assert.False(exe.TryGetProjectLaunchConfiguration(out _));
    }

    [Fact]
    public async Task DotnetProjectExecutable_PersistentLifetime_InDebugSession_RunsInProcessWithoutProjectLaunchConfig()
    {
        var builder = DistributedApplication.CreateBuilder();

        var resource = new TestDotnetProjectExecutableResource("test-working-directory");
        builder.AddResource(resource)
            .WithAnnotation(new TestProjectWithLaunchSettings())
            .WithDebugSupport(mode => new ProjectLaunchConfiguration { ProjectPath = "TestProjectWithLaunchSettings", Mode = mode }, "project")
            .WithPersistentLifetime();

        var configDict = new Dictionary<string, string?>
        {
            ["AppHost:Sha256"] = "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef",
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo { ProtocolsSupported = ["test"], SupportedLaunchConfigurations = ["project"] }),
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "TestDotnetProject");
        Assert.True(exe.Spec.Persistent);
        Assert.Equal(ExecutionType.Process, exe.Spec.ExecutionType);
        Assert.False(exe.TryGetProjectLaunchConfiguration(out _));
    }

    [Fact]
    public async Task DotnetProjectExecutable_ProjectLaunchConfigurationFailure_FailsResource()
    {
        var builder = DistributedApplication.CreateBuilder();

        var resource = new TestDotnetProjectExecutableResource("test-working-directory");
        builder.AddResource(resource)
            .WithAnnotation(new TestProjectWithLaunchSettings())
            .WithDebugSupport(CreateProjectLaunchConfiguration, "project");

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo { ProtocolsSupported = ["test"], SupportedLaunchConfigurations = ["project"] }),
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var resourceLoggerService = new ResourceLoggerService();
        var failedResources = new List<IResource>();
        var events = new DcpExecutorEvents();
        events.Subscribe<OnResourceFailedToStartContext>(context =>
        {
            failedResources.Add(context.Resource);
            return Task.CompletedTask;
        });

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(
            distributedAppModel,
            kubernetesService: kubernetesService,
            configuration: configuration,
            resourceLoggerService: resourceLoggerService,
            events: events);

        await appExecutor.RunApplicationAsync();

        Assert.Empty(kubernetesService.CreatedResources.OfType<Executable>());
        Assert.Same(resource, Assert.Single(failedResources));

        var logLines = new List<LogLine>();
        await foreach (var lines in resourceLoggerService.GetAllAsync(resource).DefaultTimeout())
        {
            logLines.AddRange(lines);
        }

        Assert.Contains(logLines, line => line.Content.Contains("Project launch configuration failed.", StringComparison.Ordinal));

        static Task<ProjectLaunchConfiguration> CreateProjectLaunchConfiguration(string mode, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Project launch configuration failed.");
        }
    }

    [Fact]
    public async Task PlainExecutable_ExtensionMode_LaunchToolArgsDebugSupport_WithholdsOwnedPrefix()
    {
        // A non-"project" debuggable executable that declares launch tool arguments (e.g. Go/Python, where the IDE
        // debugger owns the `go run <pkg>` / `python -m <mod>` tool invocation) must not pass the prefix to the
        // launched program.
        var builder = DistributedApplication.CreateBuilder();

        var debuggableExecutable = new TestExecutableResource("test-working-directory");
        builder.AddResource(debuggableExecutable)
            .WithArgs("app-arg")
            .WithLaunchToolArgs(static ctx => ctx.Args.Add("run"), ownedByLaunchConfigurationType: "test")
            .WithDebugSupport(
                mode => new ExecutableLaunchConfiguration("test") { Mode = mode },
                "test");

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo { ProtocolsSupported = ["test"], SupportedLaunchConfigurations = ["test"] }),
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234",
            [KnownConfigNames.DebugSessionRunMode] = "Debug"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "TestExecutable");
        Assert.Equal(ExecutionType.IDE, exe.Spec.ExecutionType);
        Assert.Equal(["app-arg"], exe.Spec.Args);
        Assert.Null(exe.Spec.FallbackExecutionTypes);

        // The dashboard still shows the resource's real command line, prefix included. The prefix has no effective
        // argument index because it is not passed to the launched program.
        Assert.True(exe.TryGetAnnotationAsObjectList<AppLaunchArgumentAnnotation>(CustomResource.ResourceAppArgsAnnotation, out var displayArgs));
        Assert.Collection(displayArgs,
            arg =>
            {
                Assert.Equal("run", arg.Argument);
                Assert.Null(arg.EffectiveArgumentIndex);
            },
            arg =>
            {
                Assert.Equal("app-arg", arg.Argument);
                Assert.Equal(0, arg.EffectiveArgumentIndex);
            });
    }

    [Fact]
    public async Task PlainExecutable_ExtensionMode_OwnedLaunchToolArgsCanBeHiddenFromCommandLine()
    {
        // A matching IDE launch configuration both performs the owned tool invocation and can hide that plumbing from
        // the dashboard, leaving only the ordinary program arguments in both observable argument lists.
        var builder = DistributedApplication.CreateBuilder();

        var resource = new TestExecutableResource("test-working-directory");
        builder.AddResource(resource)
            .WithArgs("app-arg")
            .WithLaunchToolArgs(
                static ctx => ctx.Args.Add("run"),
                ownedByLaunchConfigurationType: "test",
                showInCommandLine: false)
            .WithDebugSupport(
                mode => new ExecutableLaunchConfiguration("test") { Mode = mode },
                "test");

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo { ProtocolsSupported = ["test"], SupportedLaunchConfigurations = ["test"] }),
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234",
            [KnownConfigNames.DebugSessionRunMode] = "Debug"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "TestExecutable");
        Assert.Equal(ExecutionType.IDE, exe.Spec.ExecutionType);
        Assert.Equal(["app-arg"], exe.Spec.Args);
        Assert.Null(exe.Spec.FallbackExecutionTypes);

        Assert.True(exe.TryGetAnnotationAsObjectList<AppLaunchArgumentAnnotation>(CustomResource.ResourceAppArgsAnnotation, out var displayArgs));
        var displayArg = Assert.Single(displayArgs);
        Assert.Equal("app-arg", displayArg.Argument);
        Assert.Equal(0, displayArg.EffectiveArgumentIndex);
    }

    [Fact]
    public async Task PlainExecutable_ExtensionMode_CertificateCallbackCannotShiftLaunchToolPrefixBoundary()
    {
        // Certificate callbacks run after launch tool arguments are gathered and can mutate ordinary arguments.
        // Keep the prefix in a separate segment so inserting at the front cannot change which arguments the IDE owns.
        var builder = DistributedApplication.CreateBuilder();
        using var certificate = CreateTestCertificate();
        var certificateAuthorities = builder.AddCertificateAuthorityCollection("certificates")
            .WithCertificate(certificate);

        var resource = new TestExecutableResource("test-working-directory");
        builder.AddResource(resource)
            .WithArgs("app-arg")
            .WithLaunchToolArgs(static ctx => ctx.Args.Add("run"), ownedByLaunchConfigurationType: "test")
            .WithDebugSupport(
                mode => new ExecutableLaunchConfiguration("test") { Mode = mode },
                "test")
            .WithCertificateAuthorityCollection(certificateAuthorities)
            .WithCertificateTrustConfiguration(static ctx =>
            {
                ctx.Arguments.Insert(0, "certificate-arg");
                return Task.CompletedTask;
            });

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo { ProtocolsSupported = ["test"], SupportedLaunchConfigurations = ["test"] }),
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234",
            [KnownConfigNames.DebugSessionRunMode] = "Debug"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "TestExecutable");
        Assert.Equal(ExecutionType.IDE, exe.Spec.ExecutionType);
        Assert.Equal(["certificate-arg", "app-arg"], exe.Spec.Args);
        Assert.Null(exe.Spec.FallbackExecutionTypes);

        Assert.True(exe.TryGetAnnotationAsObjectList<AppLaunchArgumentAnnotation>(CustomResource.ResourceAppArgsAnnotation, out var displayArgs));
        Assert.Collection(displayArgs,
            arg =>
            {
                Assert.Equal("run", arg.Argument);
                Assert.Null(arg.EffectiveArgumentIndex);
            },
            arg =>
            {
                Assert.Equal("certificate-arg", arg.Argument);
                Assert.Equal(0, arg.EffectiveArgumentIndex);
            },
            arg =>
            {
                Assert.Equal("app-arg", arg.Argument);
                Assert.Equal(1, arg.EffectiveArgumentIndex);
            });
    }

    [Fact]
    public async Task PlainExecutable_ExtensionMode_EmptyLaunchToolArgs_DoesNotConfigureRuntimeFallback()
    {
        var builder = DistributedApplication.CreateBuilder();

        var debuggableExecutable = new TestExecutableResource("test-working-directory");
        builder.AddResource(debuggableExecutable)
            .WithArgs("app-arg")
            .WithLaunchToolArgs(static _ => { }, ownedByLaunchConfigurationType: "test")
            .WithDebugSupport(
                mode => new ExecutableLaunchConfiguration("test") { Mode = mode },
                "test");

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo { ProtocolsSupported = ["test"], SupportedLaunchConfigurations = ["test"] }),
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234",
            [KnownConfigNames.DebugSessionRunMode] = "Debug"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "TestExecutable");
        Assert.Equal(ExecutionType.IDE, exe.Spec.ExecutionType);
        Assert.Equal(["app-arg"], exe.Spec.Args);
        Assert.Null(exe.Spec.FallbackExecutionTypes);
    }

    [Fact]
    public async Task PlainExecutable_UnownedLaunchToolArgs_AreExecutedButCanBeHiddenFromTheCommandLine()
    {
        // Forward-compatibility coverage for https://github.com/microsoft/aspire/issues/18904: a tool-invocation
        // prefix that is not a debugging concern at all (the `dotnet tool exec <pkg> --yes --` shape) declares no
        // owning launch configuration type, so it is always executed, and opts out of the displayed command line
        // because it is plumbing the user neither wrote nor can act on.
        var builder = DistributedApplication.CreateBuilder();

        var resource = new TestExecutableResource("test-working-directory");
        builder.AddResource(resource)
            .WithArgs("app-arg")
            .WithLaunchToolArgs(
                static ctx =>
                {
                    ctx.Args.Add("tool");
                    ctx.Args.Add("exec");
                },
                showInCommandLine: false);

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);

        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "TestExecutable");
        Assert.Equal(ExecutionType.Process, exe.Spec.ExecutionType);
        Assert.Equal(["tool", "exec", "app-arg"], exe.Spec.Args);

        // The prefix runs but is absent from the dashboard command line. It stays visible in the resource details
        // pane regardless, because that pane reports the process's effective arguments.
        Assert.True(exe.TryGetAnnotationAsObjectList<AppLaunchArgumentAnnotation>(CustomResource.ResourceAppArgsAnnotation, out var displayArgs));
        var displayArg = Assert.Single(displayArgs);
        Assert.Equal("app-arg", displayArg.Argument);
        Assert.Equal(2, displayArg.EffectiveArgumentIndex);
    }

    [Fact]
    public async Task PlainExecutable_ExtensionMode_UnownedLaunchToolArgs_AreNotWithheldFromTheLaunchedProgram()
    {
        // Launch tool arguments that name no owning launch configuration type are not a debugging concern, so an
        // active launch configuration must not withhold them — otherwise the launched program would lose a prefix
        // no debugger ever performs.
        var builder = DistributedApplication.CreateBuilder();

        var resource = new TestExecutableResource("test-working-directory");
        builder.AddResource(resource)
            .WithArgs("app-arg")
            .WithLaunchToolArgs(static ctx => ctx.Args.Add("run"))
            .WithDebugSupport(
                mode => new ExecutableLaunchConfiguration("test") { Mode = mode },
                "test");

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo { ProtocolsSupported = ["test"], SupportedLaunchConfigurations = ["test"] }),
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234",
            [KnownConfigNames.DebugSessionRunMode] = "Debug"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "TestExecutable");
        Assert.Equal(ExecutionType.IDE, exe.Spec.ExecutionType);
        Assert.Equal(["run", "app-arg"], exe.Spec.Args);
        Assert.Null(exe.Spec.FallbackExecutionTypes);
    }

    [Fact]
    public async Task PlainExecutable_ExtensionMode_LaunchToolArgsDebugSupport_LaunchConfigFailure_FailsResource()
    {
        var builder = DistributedApplication.CreateBuilder();

        var resource = new TestExecutableResource("test-working-directory");
        builder.AddResource(resource)
            .WithArgs("app-arg")
            .WithLaunchToolArgs(static ctx => ctx.Args.Add("run"), ownedByLaunchConfigurationType: "test")
            .WithDebugSupport(
                ThrowingLaunchConfiguration,
                "test");

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo { ProtocolsSupported = ["test"], SupportedLaunchConfigurations = ["test"] }),
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        var failedResources = new List<IResource>();
        var events = new DcpExecutorEvents();
        events.Subscribe<OnResourceFailedToStartContext>(context =>
        {
            failedResources.Add(context.Resource);
            return Task.CompletedTask;
        });

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration, events: events);

        await appExecutor.RunApplicationAsync();

        Assert.Empty(GetCreatedExecutablesForResource(kubernetesService, "TestExecutable"));
        Assert.Same(resource, Assert.Single(failedResources));

        static Task<ExecutableLaunchConfiguration> ThrowingLaunchConfiguration(string mode, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Launch configuration failed.");
        }
    }

    [Fact]
    public async Task PlainExecutable_ExtensionMode_RestartLaunchConfigCancellationIsPropagated()
    {
        var builder = DistributedApplication.CreateBuilder();
        var callbackCount = 0;
        using var restartCancellation = new CancellationTokenSource();

        var resource = new TestExecutableResource("test-working-directory");
        builder.AddResource(resource)
            .WithArgs("app-arg")
            .WithDebugSupport(
                (mode, cancellationToken) =>
                {
                    if (Interlocked.Increment(ref callbackCount) == 2)
                    {
                        restartCancellation.Cancel();
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    return Task.FromResult(new ExecutableLaunchConfiguration("test") { Mode = mode });
                },
                "test");

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo { ProtocolsSupported = ["test"], SupportedLaunchConfigurations = ["test"] }),
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        await appExecutor.RunApplicationAsync();

        var exe = Assert.Single(GetCreatedExecutablesForResource(kubernetesService, "TestExecutable"));
        var reference = appExecutor.GetResource(exe.Metadata.Name);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => appExecutor.StartResourceAsync(reference, restartCancellation.Token));

        Assert.Equal(2, callbackCount);
        Assert.Equal(ExecutionType.IDE, exe.Spec.ExecutionType);
        Assert.Single(GetCreatedExecutablesForResource(kubernetesService, "TestExecutable"));
        Assert.Equal([exe.Metadata.Name], kubernetesService.DeletedResources);
    }

    [Fact]
    public async Task PlainExecutable_ExtensionMode_NullLaunchToolArgument_DoesNotOmitApplicationArgument()
    {
        // Launch tool argument values can resolve to null and disappear from the final argument list. The resolved prefix
        // boundary must shrink with them so the first ordinary argument remains executable.
        var builder = DistributedApplication.CreateBuilder();

        var resource = new TestExecutableResource("test-working-directory");
        builder.AddResource(resource)
            .WithArgs("app-arg")
            .WithLaunchToolArgs(static ctx => ctx.Args.Add(NullValueProvider.Instance), ownedByLaunchConfigurationType: "test")
            .WithDebugSupport(
                mode => new ExecutableLaunchConfiguration("test") { Mode = mode },
                "test");

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo { ProtocolsSupported = ["test"], SupportedLaunchConfigurations = ["test"] }),
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "TestExecutable");
        Assert.Equal(ExecutionType.IDE, exe.Spec.ExecutionType);
        Assert.Equal(["app-arg"], exe.Spec.Args);
        Assert.Null(exe.Spec.FallbackExecutionTypes);
    }

    [Fact]
    public async Task DotnetProjectExecutable_EmptyOwnedLaunchToolArgs_UsesApplicationArgumentsOnly()
    {
        // DotnetProjectResource suppresses its `dotnet run` scaffold when a custom launch configuration owns the
        // tool invocation. An empty prefix therefore leaves only the application arguments for the IDE.
        var builder = DistributedApplication.CreateBuilder();

        var resource = new TestDotnetProjectExecutableResource("test-working-directory");
        builder.AddResource(resource)
            .WithAnnotation(new TestProjectWithLaunchSettings())
            .WithArgs("app-arg")
            .WithLaunchToolArgs(static _ => { }, ownedByLaunchConfigurationType: "custom")
            .WithDebugSupport(
                mode => new ExecutableLaunchConfiguration("custom") { Mode = mode },
                "custom");

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo { ProtocolsSupported = ["test"], SupportedLaunchConfigurations = ["custom"] }),
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "TestDotnetProject");
        Assert.Equal(ExecutionType.IDE, exe.Spec.ExecutionType);
        Assert.Equal(["app-arg"], exe.Spec.Args);
        Assert.Null(exe.Spec.FallbackExecutionTypes);
    }

    [Fact]
    public async Task DotnetProjectExecutable_EmptyOwnedLaunchToolArgs_LaunchConfigFailureDoesNotRunBrokenProcessCommand()
    {
        var builder = DistributedApplication.CreateBuilder();

        var resource = new TestDotnetProjectExecutableResource("test-working-directory");
        builder.AddResource(resource)
            .WithAnnotation(new TestProjectWithLaunchSettings())
            .WithArgs("app-arg")
            .WithLaunchToolArgs(static _ => { }, ownedByLaunchConfigurationType: "custom")
            .WithDebugSupport(
                ThrowingLaunchConfiguration,
                "custom");

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo { ProtocolsSupported = ["test"], SupportedLaunchConfigurations = ["custom"] }),
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var resourceLoggerService = new ResourceLoggerService();
        var failedResources = new List<(IResource Resource, string? ErrorMessage)>();
        var events = new DcpExecutorEvents();
        events.Subscribe<OnResourceFailedToStartContext>(context =>
        {
            failedResources.Add((context.Resource, context.ErrorMessage));
            return Task.CompletedTask;
        });

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(
            distributedAppModel,
            kubernetesService: kubernetesService,
            configuration: configuration,
            resourceLoggerService: resourceLoggerService,
            events: events);

        await appExecutor.RunApplicationAsync();

        Assert.Empty(GetCreatedExecutablesForResource(kubernetesService, "TestDotnetProject"));
        var failure = Assert.Single(failedResources);
        Assert.Same(resource, failure.Resource);
        Assert.NotNull(failure.ErrorMessage);
        Assert.Contains("Failed to apply launch configuration", failure.ErrorMessage);
        Assert.Contains("does not retry launch configuration failures using DCP process fallback", failure.ErrorMessage);

        var logLines = new List<LogLine>();
        await foreach (var lines in resourceLoggerService.GetAllAsync(resource).DefaultTimeout())
        {
            logLines.AddRange(lines);
        }

        Assert.Contains(logLines, line =>
            line.IsErrorMessage &&
            line.Content.Contains("Launch configuration failed.", StringComparison.Ordinal) &&
            line.Content.Contains("does not retry launch configuration failures using DCP process fallback", StringComparison.Ordinal));

        static ExecutableLaunchConfiguration ThrowingLaunchConfiguration(string mode)
        {
            throw new InvalidOperationException("Launch configuration failed.");
        }
    }

    [Fact]
    public async Task DotnetProjectExecutable_EmptyOwnedLaunchToolArgs_LaunchConfigFailureOnRestartPropagatesFailureAndRetainsDiagnostic()
    {
        var builder = DistributedApplication.CreateBuilder();
        var launchConfigurationCallCount = 0;

        var resource = new TestDotnetProjectExecutableResource("test-working-directory");
        builder.AddResource(resource)
            .WithAnnotation(new TestProjectWithLaunchSettings())
            .WithArgs("app-arg")
            .WithLaunchToolArgs(static _ => { }, ownedByLaunchConfigurationType: "custom")
            .WithDebugSupport(
                mode =>
                {
                    if (Interlocked.Increment(ref launchConfigurationCallCount) == 2)
                    {
                        throw new InvalidOperationException("Launch configuration failed.");
                    }

                    return new ExecutableLaunchConfiguration("custom") { Mode = mode };
                },
                "custom");

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo { ProtocolsSupported = ["test"], SupportedLaunchConfigurations = ["custom"] }),
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var resourceLoggerService = new ResourceLoggerService();
        var failedResources = new ConcurrentQueue<(IResource Resource, string? ErrorMessage)>();
        var events = new DcpExecutorEvents();
        events.Subscribe<OnResourceFailedToStartContext>(context =>
        {
            failedResources.Enqueue((context.Resource, context.ErrorMessage));
            return Task.CompletedTask;
        });

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(
            distributedAppModel,
            kubernetesService: kubernetesService,
            configuration: configuration,
            resourceLoggerService: resourceLoggerService,
            events: events);

        await appExecutor.RunApplicationAsync();

        var executable = Assert.Single(GetCreatedExecutablesForResource(kubernetesService, "TestDotnetProject"));
        var reference = appExecutor.GetResource(executable.Metadata.Name);

        var exception = await Assert.ThrowsAsync<FailedToApplyEnvironmentException>(
            () => appExecutor.StartResourceAsync(reference, CancellationToken.None));

        Assert.Contains("Failed to apply launch configuration", exception.Message);
        Assert.Contains("does not retry launch configuration failures using DCP process fallback", exception.Message);
        Assert.Equal(2, launchConfigurationCallCount);
        Assert.Equal([executable.Metadata.Name], kubernetesService.DeletedResources);
        Assert.Single(GetCreatedExecutablesForResource(kubernetesService, "TestDotnetProject"));

        var failure = Assert.Single(failedResources);
        Assert.Same(resource, failure.Resource);
        Assert.NotNull(failure.ErrorMessage);
        Assert.Contains("Failed to apply launch configuration", failure.ErrorMessage);
        Assert.Contains("does not retry launch configuration failures using DCP process fallback", failure.ErrorMessage);

        var logLines = new List<LogLine>();
        await foreach (var lines in resourceLoggerService.GetAllAsync(resource).DefaultTimeout())
        {
            logLines.AddRange(lines);
        }

        Assert.Contains(logLines, line =>
            line.IsErrorMessage &&
            line.Content.Contains("Launch configuration failed.", StringComparison.Ordinal) &&
            line.Content.Contains("does not retry launch configuration failures using DCP process fallback", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PlainExecutable_ExtensionMode_LaunchToolArgumentsAreRecomputedOnRestart()
    {
        // Restart invalidates launch tool callback caches without rerunning preparation. Vary the owned prefix
        // across creations to prove each launch plan and dashboard projection use the newly resolved arguments.
        var builder = DistributedApplication.CreateBuilder();
        var callbackCount = 0;

        var resource = new TestExecutableResource("test-working-directory");
        builder.AddResource(resource)
            .WithArgs("app-arg")
            .WithLaunchToolArgs(
                ctx =>
                {
                    if (Interlocked.Increment(ref callbackCount) == 2)
                    {
                        ctx.Args.Add("run");
                    }
                },
                ownedByLaunchConfigurationType: "test")
            .WithDebugSupport(
                mode => new ExecutableLaunchConfiguration("test") { Mode = mode },
                "test");

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo { ProtocolsSupported = ["test"], SupportedLaunchConfigurations = ["test"] }),
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        await appExecutor.RunApplicationAsync();

        var exe1 = Assert.Single(GetCreatedExecutablesForResource(kubernetesService, "TestExecutable"));
        Assert.Null(exe1.Spec.FallbackExecutionTypes);
        Assert.True(exe1.TryGetAnnotationAsObjectList<AppLaunchArgumentAnnotation>(CustomResource.ResourceAppArgsAnnotation, out var displayArgs1));
        Assert.Equal(["app-arg"], displayArgs1.Select(static argument => argument.Argument));

        var reference = appExecutor.GetResource(exe1.Metadata.Name);
        await appExecutor.StartResourceAsync(reference, CancellationToken.None);

        var executables = GetCreatedExecutablesForResource(kubernetesService, "TestExecutable");
        Assert.Equal(2, executables.Count);
        var exe2 = executables[1];
        Assert.Null(exe2.Spec.FallbackExecutionTypes);
        Assert.True(exe2.TryGetAnnotationAsObjectList<AppLaunchArgumentAnnotation>(CustomResource.ResourceAppArgsAnnotation, out var displayArgs2));
        Assert.Equal(["run", "app-arg"], displayArgs2.Select(static argument => argument.Argument));

        await appExecutor.StartResourceAsync(reference, CancellationToken.None);

        executables = GetCreatedExecutablesForResource(kubernetesService, "TestExecutable");
        Assert.Equal(3, executables.Count);
        var exe3 = executables[2];
        Assert.Null(exe3.Spec.FallbackExecutionTypes);
        Assert.True(exe3.TryGetAnnotationAsObjectList<AppLaunchArgumentAnnotation>(CustomResource.ResourceAppArgsAnnotation, out var displayArgs3));
        Assert.Equal(["app-arg"], displayArgs3.Select(static argument => argument.Argument));
        Assert.Equal(3, callbackCount);
    }

    [Fact]
    public async Task PlainExecutable_ExtensionMode_LaunchConfigurationFailureDoesNotReusePriorPlanOnRestart()
    {
        var builder = DistributedApplication.CreateBuilder();
        var callbackCount = 0;

        var resource = new TestExecutableResource("test-working-directory");
        builder.AddResource(resource)
            .WithArgs("app-arg")
            .WithDebugSupport(
                mode =>
                {
                    if (Interlocked.Increment(ref callbackCount) == 2)
                    {
                        throw new InvalidOperationException("Launch configuration failed.");
                    }

                    return new ExecutableLaunchConfiguration("test") { Mode = mode };
                },
                "test");

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo { ProtocolsSupported = ["test"], SupportedLaunchConfigurations = ["test"] }),
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        await appExecutor.RunApplicationAsync();

        var exe1 = Assert.Single(GetCreatedExecutablesForResource(kubernetesService, "TestExecutable"));
        Assert.Equal(ExecutionType.IDE, exe1.Spec.ExecutionType);
        Assert.Null(exe1.Spec.FallbackExecutionTypes);

        var reference = appExecutor.GetResource(exe1.Metadata.Name);
        var exception = await Assert.ThrowsAsync<FailedToApplyEnvironmentException>(
            () => appExecutor.StartResourceAsync(reference, CancellationToken.None));
        Assert.Contains("Failed to apply launch configuration", exception.Message);
        Assert.Contains("does not retry launch configuration failures using DCP process fallback", exception.Message);

        var executables = GetCreatedExecutablesForResource(kubernetesService, "TestExecutable");
        Assert.Single(executables);
        Assert.Equal(ExecutionType.IDE, exe1.Spec.ExecutionType);
        Assert.Null(exe1.Spec.FallbackExecutionTypes);

        await appExecutor.StartResourceAsync(reference, CancellationToken.None);

        executables = GetCreatedExecutablesForResource(kubernetesService, "TestExecutable");
        Assert.Equal(2, executables.Count);
        var exe2 = executables[1];
        Assert.Equal(ExecutionType.IDE, exe2.Spec.ExecutionType);
        Assert.Null(exe2.Spec.FallbackExecutionTypes);
        Assert.Equal(3, callbackCount);
    }

    [Fact]
    public async Task PlainExecutable_ProjectDebugSupportWithoutProjectMetadata_FailsToStart()
    {
        // "project" is reserved for resources carrying IProjectMetadata, so a plain executable must fail with an
        // actionable message instead of sending an incomplete launch configuration to the IDE.
        var builder = DistributedApplication.CreateBuilder();

        var resource = new TestExecutableResource("test-working-directory");
        builder.AddResource(resource)
            .WithDebugSupport(mode => new ProjectLaunchConfiguration { ProjectPath = "/test/path", Mode = mode }, "project");

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo { ProtocolsSupported = ["test"], SupportedLaunchConfigurations = ["project"] }),
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        var failedResources = new List<(IResource Resource, string? ErrorMessage)>();
        var events = new DcpExecutorEvents();
        events.Subscribe<OnResourceFailedToStartContext>(context =>
        {
            failedResources.Add((context.Resource, context.ErrorMessage));
            return Task.CompletedTask;
        });

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration, events: events);

        await appExecutor.RunApplicationAsync();

        Assert.Empty(kubernetesService.CreatedResources.OfType<Executable>());
        var failure = Assert.Single(failedResources);
        Assert.Same(resource, failure.Resource);
        Assert.NotNull(failure.ErrorMessage);
        Assert.Contains("project metadata", failure.ErrorMessage);
    }

    [Theory]
    [InlineData(ExecutableLaunchMode.Debug, ExecutableLaunchMode.Debug)]
    [InlineData(ExecutableLaunchMode.NoDebug, ExecutableLaunchMode.NoDebug)]
    public async Task DotnetProjectExecutable_RespectsDebugSessionRunMode(string runMode, string expectedMode)
    {
        var builder = DistributedApplication.CreateBuilder();

        var resource = new TestDotnetProjectExecutableResource("test-working-directory");
        builder.AddResource(resource)
            .WithAnnotation(new TestProjectWithLaunchSettings())
            .WithAnnotation(new LaunchProfileAnnotation("http"))
            .WithDebugSupport(mode => new ProjectLaunchConfiguration { ProjectPath = "TestProjectWithLaunchSettings", Mode = mode }, "project");

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionRunMode] = runMode,
            [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo { ProtocolsSupported = ["test"], SupportedLaunchConfigurations = ["project"] }),
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        await appExecutor.RunApplicationAsync();

        var exe = GetCreatedExecutableForResource(kubernetesService, "TestDotnetProject");
        Assert.True(exe.TryGetProjectLaunchConfiguration(out var plc));
        Assert.Equal(expectedMode, plc.Mode);
    }

    [Theory]
    [InlineData(true, null, "aspire.dev.internal")]
    [InlineData(false, null, "host.docker.internal")]
    [InlineData(true, "super.star", "aspire.dev.internal")]
    [InlineData(false, "mega.mushroom", "mega.mushroom")]
    public async Task EndpointsAllocatedCorrectly(bool useTunnel, string? containerHostName, string expectedContainerHost)
    {
        var builder = DistributedApplication.CreateBuilder();
        var executable = builder.AddExecutable("anExecutable", "command", "")
            .WithEndpoint(name: "proxied", targetPort: 1234, port: 5678, isProxied: true)
            .WithEndpoint(name: "notProxied", port: 8765, isProxied: false);

        var container = builder.AddContainer("aContainer", "image")
            .WithEndpoint(name: "proxied", port: 15678, targetPort: 11234, isProxied: true)
            .WithEndpoint(name: "notProxied", port: 18765, isProxied: false)
            .WithEnvironment("EXE_PROXIED_PORT", executable.GetEndpoint("proxied").Property(EndpointProperty.Port))
            .WithEnvironment("EXE_NOTPROXIED_PORT", executable.GetEndpoint("notProxied").Property(EndpointProperty.Port));

        var containerWithAlias = builder.AddContainer("containerWithAlias", "image")
            .WithEndpoint(name: "proxied", port: 25678, targetPort: 21234, isProxied: true)
            .WithEndpoint(name: "notProxied", port: 28765, isProxied: false)
            .WithContainerNetworkAlias("custom.alias")
            .WithEnvironment("EXE_PROXIED_PORT", executable.GetEndpoint("proxied").Property(EndpointProperty.Port))
            .WithEnvironment("EXE_NOTPROXIED_PORT", executable.GetEndpoint("notProxied").Property(EndpointProperty.Port));

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var configDict = new Dictionary<string, string?>
        {
            ["AppHost:ContainerHostname"] = containerHostName
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var dcpOptions = new DcpOptions
        {
            EnableAspireContainerTunnel = useTunnel,
        };

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration, dcpOptions: dcpOptions);

        await appExecutor.RunApplicationAsync();

        await AssertEndpoint(executable.Resource, "proxied", KnownNetworkIdentifiers.LocalhostNetwork, KnownHostNames.Localhost, 5678);
        await AssertEndpoint(executable.Resource, "notProxied", KnownNetworkIdentifiers.LocalhostNetwork, KnownHostNames.Localhost, 8765);

        if (useTunnel)
        {
            await AssertTunneledPort(executable.Resource, "proxied", 5678);
            await AssertTunneledPort(executable.Resource, "notProxied", 8765);

            async ValueTask AssertTunneledPort(IResourceWithEndpoints resource, string endpointName, int hostPort)
            {
                var svcs = kubernetesService.CreatedResources
                    .OfType<Service>()
                    .Where(x => x.AppModelResourceName == resource.Name
                        && x.EndpointName == endpointName
                        && x.Metadata.Annotations.ContainsKey(CustomResource.ContainerTunnelInstanceName))
                    .ToList();

                var svc = svcs.Single();

                int port = svc.AllocatedPort!.Value;
                await AssertEndpoint(executable.Resource, endpointName, KnownNetworkIdentifiers.DefaultAspireContainerNetwork, expectedContainerHost, port);

                await AssertEndpoint(executable.Resource, endpointName, KnownNetworkIdentifiers.LocalhostNetwork, KnownHostNames.Localhost, hostPort);

                var dcpContainer = kubernetesService.CreatedResources
                    .OfType<Container>()
                    .Where(c => c.AppModelResourceName == container.Resource.Name)
                    .Single();
                var exePortEnvVal = dcpContainer.Spec?.Env?.Where(e => e.Name == $"EXE_{endpointName.ToUpper()}_PORT").Single().Value;
                Assert.Equal(port.ToString(), exePortEnvVal);
            }
        }
        else
        {
            await AssertEndpoint(executable.Resource, "proxied", KnownNetworkIdentifiers.LocalhostNetwork, KnownHostNames.Localhost, 5678);
            await AssertEndpoint(executable.Resource, "notProxied", KnownNetworkIdentifiers.LocalhostNetwork, KnownHostNames.Localhost, 8765);
            await AssertEndpoint(executable.Resource, "proxied", KnownNetworkIdentifiers.DefaultAspireContainerNetwork, expectedContainerHost, 5678);
            await AssertEndpoint(executable.Resource, "notProxied", KnownNetworkIdentifiers.DefaultAspireContainerNetwork, expectedContainerHost, 8765);
        }

        await AssertEndpoint(container.Resource, "proxied", KnownNetworkIdentifiers.LocalhostNetwork, KnownHostNames.Localhost, 15678);
        await AssertEndpoint(container.Resource, "notProxied", KnownNetworkIdentifiers.LocalhostNetwork, KnownHostNames.Localhost, 18765);

        await AssertEndpoint(container.Resource, "proxied", KnownNetworkIdentifiers.DefaultAspireContainerNetwork, $"{container.Resource.Name}.dev.internal", 11234);
        await AssertEndpoint(container.Resource, "notProxied", KnownNetworkIdentifiers.DefaultAspireContainerNetwork, $"{container.Resource.Name}.dev.internal", 18765);

        await AssertEndpoint(containerWithAlias.Resource, "proxied", KnownNetworkIdentifiers.LocalhostNetwork, KnownHostNames.Localhost, 25678);
        await AssertEndpoint(containerWithAlias.Resource, "notProxied", KnownNetworkIdentifiers.LocalhostNetwork, KnownHostNames.Localhost, 28765);

        await AssertEndpoint(containerWithAlias.Resource, "proxied", KnownNetworkIdentifiers.DefaultAspireContainerNetwork, $"{containerWithAlias.Resource.Name}.dev.internal", 21234);
        await AssertEndpoint(containerWithAlias.Resource, "notProxied", KnownNetworkIdentifiers.DefaultAspireContainerNetwork, $"{containerWithAlias.Resource.Name}.dev.internal", 28765);

        async ValueTask AssertEndpoint(IResourceWithEndpoints resource, string name, NetworkIdentifier network, string address, int port)
        {
            var endpoint = resource.GetEndpoint(name).EndpointAnnotation;
            var allocatedEndpoints = endpoint.AllAllocatedEndpoints;

            Assert.Contains(allocatedEndpoints, a => a.NetworkID == network);

            var allocatedEndpoint = await endpoint.AllAllocatedEndpoints.Single(x => x.NetworkID == network).Snapshot.GetValueAsync().DefaultTimeout();

            Assert.Equal(endpoint, allocatedEndpoint.Endpoint);
            Assert.Equal(address, allocatedEndpoint.Address);
            Assert.Equal(EndpointBindingMode.SingleAddress, allocatedEndpoint.BindingMode);
            Assert.Equal(port, allocatedEndpoint.Port);
            Assert.Equal(endpoint.UriScheme, allocatedEndpoint.UriScheme);
            Assert.Equal($"{address}:{port}", allocatedEndpoint.EndPointString);
        }
    }

    [Fact]
    public async Task ContainerHostUrlWithoutMatchingHostEndpointUsesContainerHostBridge()
    {
        var builder = DistributedApplication.CreateBuilder();

        builder.AddContainer("aContainer", "image")
            .WithEnvironment("URL", new HostUrl("https://localhost:17092/path"));

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var dcpOptions = new DcpOptions
        {
            EnableAspireContainerTunnel = true,
        };

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, dcpOptions: dcpOptions);
        await appExecutor.RunApplicationAsync();

        var dcpContainer = Assert.Single(kubernetesService.CreatedResources.OfType<Container>(), c => c.AppModelResourceName == "aContainer");
        Assert.NotNull(dcpContainer.Spec.Env);
        var url = Assert.Single(dcpContainer.Spec.Env, e => e.Name == "URL").Value;
        Assert.Equal("https://host.docker.internal:17092/path", url);

        Assert.DoesNotContain(kubernetesService.CreatedResources.OfType<Service>(),
            s => s.Metadata.Annotations.ContainsKey(CustomResource.ContainerTunnelInstanceName));
    }

    [Fact]
    public async Task ContainerHostUrlMatchingHostEndpointUsesTunnelPort()
    {
        var builder = DistributedApplication.CreateBuilder();

        var executable = builder.AddExecutable("anExecutable", "command", "")
            .WithEndpoint(name: "http", targetPort: 1234, port: 5678, isProxied: true);

        builder.AddContainer("aContainer", "image")
            .WithEnvironment("URL", new HostUrl("https://localhost:5678/path"));

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var failedResources = new ConcurrentBag<string>();
        var events = new DcpExecutorEvents();
        events.Subscribe<OnResourceFailedToStartContext>(context =>
        {
            failedResources.Add(context.Resource.Name);
            return Task.CompletedTask;
        });

        var dcpOptions = new DcpOptions
        {
            EnableAspireContainerTunnel = true,
        };

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, dcpOptions: dcpOptions, events: events);
        await appExecutor.RunApplicationAsync();

        Assert.DoesNotContain("aContainer", failedResources);

        var dcpContainer = Assert.Single(kubernetesService.CreatedResources.OfType<Container>(), c => c.AppModelResourceName == "aContainer");
        var tunnelService = Assert.Single(kubernetesService.CreatedResources.OfType<Service>(),
            s => s.AppModelResourceName == executable.Resource.Name
                && s.EndpointName == "http"
                && s.Metadata.Annotations.ContainsKey(CustomResource.ContainerTunnelInstanceName));

        Assert.NotNull(dcpContainer.Spec.Env);
        var url = Assert.Single(dcpContainer.Spec.Env, e => e.Name == "URL").Value;
        Assert.Equal($"https://{KnownHostNames.DefaultContainerTunnelHostName}:{tunnelService.AllocatedPort}/path", url);
    }

    // Verifies that environment value callbacks are invoked only once per container startup.
    [Fact]
    public async Task EnvironmentCallbacksInvokedOnceOnContainer()
    {
        var builder = DistributedApplication.CreateBuilder();

        var executable = builder.AddExecutable("anExecutable", "command", "")
            .WithEndpoint(name: "http", targetPort: 1234, port: 5678, isProxied: true);

        var callCount = 0;
        builder.AddContainer("aContainer", "image")
            .WithEnvironment(c =>
            {
                Interlocked.Increment(ref callCount);
                c.EnvironmentVariables["EXEC_PORT"] = executable.GetEndpoint("http").Property(EndpointProperty.Port);
            });

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var dcpOptions = new DcpOptions
        {
            EnableAspireContainerTunnel = true,
        };

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, dcpOptions: dcpOptions);
        await appExecutor.RunApplicationAsync();

        Assert.Equal(1, callCount);
    }

    // Ensures that environment value callbacks are invoked after the OnResourceStarting event is raised for the resource,
    // allowing users to rely on any state set during that event in their environment callbacks.
    [Fact]
    public async Task EnvironmentCallbacksInvokedAfterBeforeResourceStartEvent()
    {
        var builder = DistributedApplication.CreateBuilder();
        var envCallCount = 0;
        var resourceStartingRaised = false;
        var resourceStartingCalledBeforeEnvCallback = false;

        var executable = builder.AddExecutable("anExecutable", "command", "")
            .WithEndpoint(name: "http", targetPort: 1234, port: 5678, isProxied: true);

        builder.AddContainer("aContainer", "image")
            .WithEnvironment(c =>
            {
                Interlocked.Increment(ref envCallCount);
                c.EnvironmentVariables["EXEC_PORT"] = executable.GetEndpoint("http").Property(EndpointProperty.Port);
            });

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var events = new DcpExecutorEvents();
        events.Subscribe<OnResourceStartingContext>(context =>
        {
            if (context.ResourceType == "Container")
            {
                resourceStartingRaised = true;
                resourceStartingCalledBeforeEnvCallback = envCallCount == 0;
            }
            return Task.CompletedTask;
        });

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, events: events);
        await appExecutor.RunApplicationAsync();

        Assert.Equal(1, envCallCount);
        Assert.True(resourceStartingRaised, "OnResourceStarting should be raised for the container");
        Assert.True(resourceStartingCalledBeforeEnvCallback, "OnResourceStarting should be raised before the environment callback is invoked");
    }

    // Verifies that command-line argument callbacks are invoked only once per container startup.
    [Fact]
    public async Task ArgsCallbacksInvokedOnceOnContainer()
    {
        var builder = DistributedApplication.CreateBuilder();

        var executable = builder.AddExecutable("anExecutable", "command", "")
            .WithEndpoint(name: "http", targetPort: 1234, port: 5678, isProxied: true);

        var callCount = 0;
        builder.AddContainer("aContainer", "image")
            .WithArgs(c =>
            {
                Interlocked.Increment(ref callCount);
                c.Args.Add("--port");
                c.Args.Add(executable.GetEndpoint("http").Property(EndpointProperty.Port));
            });

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var dcpOptions = new DcpOptions
        {
            EnableAspireContainerTunnel = true,
        };

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, dcpOptions: dcpOptions);
        await appExecutor.RunApplicationAsync();

        Assert.Equal(1, callCount);

        var container = Assert.Single(kubernetesService.CreatedResources.OfType<Container>(), c => c.AppModelResourceName == "aContainer");
        Assert.True(container.TryGetAnnotationAsObjectList<AppLaunchArgumentAnnotation>(CustomResource.ResourceAppArgsAnnotation, out var argAnnotations));
        AssertEffectiveArgumentIndexesMatchSpecArgs(argAnnotations, container.Spec.Args);
    }

    [Fact]
    public async Task ExecutionConfigurationCallbacksDeferredForExplicitStartExecutableUntilManualStart()
    {
        var builder = DistributedApplication.CreateBuilder();

        var argsCallCount = 0;
        var envCallCount = 0;
        var resource = builder.AddExecutable("anExecutable", "command", "")
            .WithExplicitStart()
            .WithArgs(c =>
            {
                Interlocked.Increment(ref argsCallCount);
                c.Args.Add("--deferred");
            })
            .WithEnvironment(c =>
            {
                Interlocked.Increment(ref envCallCount);
                c.EnvironmentVariables["DEFERRED_ENV"] = "true";
            });

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);
        await appExecutor.RunApplicationAsync();

        Assert.Equal(0, argsCallCount);
        Assert.Equal(0, envCallCount);
        Assert.Empty(GetCreatedExecutablesForResource(kubernetesService, "anExecutable"));

        var reference = appExecutor.GetResource(DcpExecutor.GetDcpInstance(resource.Resource, instanceIndex: 0).Name);
        await appExecutor.StartResourceAsync(reference, CancellationToken.None);

        Assert.Equal(1, argsCallCount);
        Assert.Equal(1, envCallCount);

        var startedExecutable = Assert.Single(GetCreatedExecutablesForResource(kubernetesService, "anExecutable"), e => e.Spec.Start == true);
        Assert.Contains("--deferred", startedExecutable.Spec.Args!);
        Assert.Contains(startedExecutable.Spec.Env!, e => e is { Name: "DEFERRED_ENV", Value: "true" });
        Assert.True(startedExecutable.TryGetAnnotationAsObjectList<AppLaunchArgumentAnnotation>(CustomResource.ResourceAppArgsAnnotation, out var argAnnotations));
        Assert.Single(argAnnotations, a => a.Argument == "--deferred");
        AssertEffectiveArgumentIndexesMatchSpecArgs(argAnnotations, startedExecutable.Spec.Args);
    }

    [Fact]
    public async Task ExecutionConfigurationCallbacksDeferredForExplicitStartContainerUntilManualStart()
    {
        var builder = DistributedApplication.CreateBuilder();

        var argsCallCount = 0;
        var envCallCount = 0;
        var resource = builder.AddContainer("aContainer", "image")
            .WithExplicitStart()
            .WithArgs(c =>
            {
                Interlocked.Increment(ref argsCallCount);
                c.Args.Add("--deferred");
            })
            .WithEnvironment(c =>
            {
                Interlocked.Increment(ref envCallCount);
                c.EnvironmentVariables["DEFERRED_ENV"] = "true";
            });

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);
        await appExecutor.RunApplicationAsync();

        Assert.Equal(0, argsCallCount);
        Assert.Equal(0, envCallCount);
        Assert.DoesNotContain(kubernetesService.CreatedResources.OfType<Container>(), c => c.AppModelResourceName == "aContainer");

        var reference = appExecutor.GetResource(DcpExecutor.GetDcpInstance(resource.Resource, instanceIndex: 0).Name);
        await appExecutor.StartResourceAsync(reference, CancellationToken.None);

        Assert.Equal(1, argsCallCount);
        Assert.Equal(1, envCallCount);

        var startedContainer = Assert.Single(kubernetesService.CreatedResources.OfType<Container>(), c => c.AppModelResourceName == "aContainer" && c.Spec.Start == true);
        Assert.Contains("--deferred", startedContainer.Spec.Args!);
        Assert.Contains(startedContainer.Spec.Env!, e => e is { Name: "DEFERRED_ENV", Value: "true" });
        Assert.True(startedContainer.TryGetAnnotationAsObjectList<AppLaunchArgumentAnnotation>(CustomResource.ResourceAppArgsAnnotation, out var argAnnotations));
        Assert.Single(argAnnotations, a => a.Argument == "--deferred");
        AssertEffectiveArgumentIndexesMatchSpecArgs(argAnnotations, startedContainer.Spec.Args);
    }

    [Fact]
    public async Task ExecutionConfigurationCallbacksNotReevaluatedWhenStartingCreatedExplicitStartPersistentExecutable()
    {
        var builder = DistributedApplication.CreateBuilder();

        var argsCallCount = 0;
        var envCallCount = 0;
        var resource = builder.AddExecutable("anExecutable", "command", "")
            .WithPersistentLifetime()
            .WithExplicitStart()
            .WithArgs(c =>
            {
                Interlocked.Increment(ref argsCallCount);
                c.Args.Add("--persistent");
            })
            .WithEnvironment(c =>
            {
                Interlocked.Increment(ref envCallCount);
                c.EnvironmentVariables["PERSISTENT_ENV"] = "true";
            });

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var configDict = new Dictionary<string, string?>
        {
            ["AppHost:Sha256"] = "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);
        await appExecutor.RunApplicationAsync();

        Assert.Equal(1, argsCallCount);
        Assert.Equal(1, envCallCount);

        var executable = Assert.Single(GetCreatedExecutablesForResource(kubernetesService, "anExecutable"));
        Assert.False(executable.Spec.Start);
        Assert.True(executable.Spec.Persistent);
        Assert.Contains("--persistent", executable.Spec.Args!);
        Assert.Contains(executable.Spec.Env!, e => e is { Name: "PERSISTENT_ENV", Value: "true" });

        var reference = appExecutor.GetResource(DcpExecutor.GetDcpInstance(resource.Resource, instanceIndex: 0).Name);
        await appExecutor.StartResourceAsync(reference, CancellationToken.None);

        Assert.Equal(1, argsCallCount);
        Assert.Equal(1, envCallCount);
        Assert.Empty(kubernetesService.DeletedResources);
        Assert.Single(GetCreatedExecutablesForResource(kubernetesService, "anExecutable"));
        Assert.True(executable.Spec.Start);
    }

    [Fact]
    public async Task ExecutionConfigurationCallbacksNotDeferredForExplicitStartPersistentContainer()
    {
        var builder = DistributedApplication.CreateBuilder();

        var argsCallCount = 0;
        var envCallCount = 0;
        var resource = builder.AddContainer("aContainer", "image")
            .WithPersistentLifetime()
            .WithExplicitStart()
            .WithArgs(c =>
            {
                Interlocked.Increment(ref argsCallCount);
                c.Args.Add("--persistent");
            })
            .WithEnvironment(c =>
            {
                Interlocked.Increment(ref envCallCount);
                c.EnvironmentVariables["PERSISTENT_ENV"] = "true";
            });

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var configDict = new Dictionary<string, string?>
        {
            ["AppHost:Sha256"] = "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);
        await appExecutor.RunApplicationAsync();

        Assert.Equal(1, argsCallCount);
        Assert.Equal(1, envCallCount);

        var container = Assert.Single(kubernetesService.CreatedResources.OfType<Container>(), c => c.AppModelResourceName == "aContainer");
        Assert.False(container.Spec.Start);
        Assert.True(container.Spec.Persistent);
        Assert.Contains("--persistent", container.Spec.Args!);
        Assert.Contains(container.Spec.Env!, e => e is { Name: "PERSISTENT_ENV", Value: "true" });

        var reference = appExecutor.GetResource(DcpExecutor.GetDcpInstance(resource.Resource, instanceIndex: 0).Name);
        await appExecutor.StartResourceAsync(reference, CancellationToken.None);

        Assert.Equal(1, argsCallCount);
        Assert.Equal(1, envCallCount);
        Assert.Empty(kubernetesService.DeletedResources);
        Assert.Single(kubernetesService.CreatedResources.OfType<Container>(), c => c.AppModelResourceName == "aContainer");
        Assert.True(container.Spec.Start);
    }

    // Ensures that command-line argument callbacks are invoked after the OnResourceStarting event is raised for the resource,
    // allowing users to rely on any state set during that event in their argument callbacks.
    [Fact]
    public async Task ArgsCallbacksInvokedAfterBeforeResourceStartEvent()
    {
        var builder = DistributedApplication.CreateBuilder();
        var argsCallCount = 0;
        var resourceStartingRaised = false;
        var resourceStartingCalledBeforeArgsCallback = false;

        var executable = builder.AddExecutable("anExecutable", "command", "")
            .WithEndpoint(name: "http", targetPort: 1234, port: 5678, isProxied: true);

        builder.AddContainer("aContainer", "image")
            .WithArgs(c =>
            {
                Interlocked.Increment(ref argsCallCount);
                c.Args.Add("--port");
                c.Args.Add(executable.GetEndpoint("http").Property(EndpointProperty.Port));
            });

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var events = new DcpExecutorEvents();
        events.Subscribe<OnResourceStartingContext>(context =>
        {
            if (context.ResourceType == "Container")
            {
                resourceStartingRaised = true;
                resourceStartingCalledBeforeArgsCallback = argsCallCount == 0;
            }
            return Task.CompletedTask;
        });

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, events: events);
        await appExecutor.RunApplicationAsync();

        Assert.Equal(1, argsCallCount);
        Assert.True(resourceStartingRaised, "OnResourceStarting should be raised for the container");
        Assert.True(resourceStartingCalledBeforeArgsCallback, "OnResourceStarting should be raised before the args callback is invoked");
    }

    [Fact]
    public async Task TunnelDependentAndIndependentContainersCanStartTogether()
    {
        var builder = DistributedApplication.CreateBuilder();

        // An executable with an endpoint — containers that reference it will be tunnel-dependent.
        var executable = builder.AddExecutable("anExecutable", "command", "")
            .WithEndpoint(name: "http", targetPort: 1234, port: 5678, isProxied: true);

        // A container that references the executable's endpoint — this makes it tunnel-dependent.
        builder.AddContainer("tunnelDependent", "image")
            .WithEnvironment("EXEC_PORT", executable.GetEndpoint("http").Property(EndpointProperty.Port));

        // A container that does NOT reference any host resource — this is tunnel-independent.
        builder.AddContainer("tunnelIndependent", "image");

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var dcpOptions = new DcpOptions
        {
            EnableAspireContainerTunnel = true,
        };

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, dcpOptions: dcpOptions);
        await appExecutor.RunApplicationAsync();

        // Both containers should have been created successfully.
        var createdContainers = kubernetesService.CreatedResources.OfType<Container>().ToList();
        Assert.Single(createdContainers, c => c.AppModelResourceName == "tunnelDependent");
        Assert.Single(createdContainers, c => c.AppModelResourceName == "tunnelIndependent");
    }

    [Fact]
    public async Task WaitingTunnelDependentContainersDoNotBlockTunnelCreation()
    {
        var builder = DistributedApplication.CreateBuilder();

        var executableA = builder.AddExecutable("executableA", "command", "")
            .WithEndpoint(name: "http", targetPort: 1234, port: 5678, isProxied: true);

        var executableB = builder.AddExecutable("executableB", "command", "")
            .WithEndpoint(name: "http", targetPort: 1235, port: 5679, isProxied: true);

        var container = builder.AddContainer("container", "image")
            .WithEnvironment("EXEC_A_PORT", executableA.GetEndpoint("http").Property(EndpointProperty.Port));

        var waiting = builder.AddContainer("waiting", "image")
            .WaitFor(container);

        var waitingConsumingEndpoint = builder.AddContainer("waitingConsumingEndpoint", "image")
            .WithEnvironment("EXEC_B_PORT", executableB.GetEndpoint("http").Property(EndpointProperty.Port))
            .WaitFor(container);

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var events = new DcpExecutorEvents();
        events.Subscribe<OnResourceStartingContext>(async context =>
        {
            if (context.Resource == waiting.Resource || context.Resource == waitingConsumingEndpoint.Resource)
            {
                while (!kubernetesService.CreatedResources.OfType<Container>().Any(c => c.AppModelResourceName == container.Resource.Name))
                {
                    await Task.Delay(10, context.CancellationToken).ConfigureAwait(false);
                }
            }
        });

        var dcpOptions = new DcpOptions
        {
            EnableAspireContainerTunnel = true,
        };

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, dcpOptions: dcpOptions, events: events);
        await appExecutor.RunApplicationAsync().DefaultTimeout();

        var createdContainers = kubernetesService.CreatedResources.OfType<Container>().ToList();
        Assert.Single(createdContainers, c => c.AppModelResourceName == container.Resource.Name);
        Assert.Single(createdContainers, c => c.AppModelResourceName == waiting.Resource.Name);
        var waitingConsumingContainer = Assert.Single(createdContainers, c => c.AppModelResourceName == waitingConsumingEndpoint.Resource.Name);

        var tunnelServices = kubernetesService.CreatedResources
            .OfType<Service>()
            .Where(s => s.Metadata.Annotations.ContainsKey(CustomResource.ContainerTunnelInstanceName))
            .ToList();

        Assert.Single(tunnelServices, s => s.AppModelResourceName == executableA.Resource.Name);
        var executableBTunnelService = Assert.Single(tunnelServices, s => s.AppModelResourceName == executableB.Resource.Name);

        Assert.NotNull(waitingConsumingContainer.Spec.Env);
        var executableBPort = Assert.Single(waitingConsumingContainer.Spec.Env, e => e.Name == "EXEC_B_PORT").Value;
        Assert.Equal(executableBTunnelService.AllocatedPort.ToString(), executableBPort);

        var tunnelProxy = Assert.Single(kubernetesService.CreatedResources.OfType<ContainerNetworkTunnelProxy>());
        Assert.Equal(2, tunnelProxy.Spec.Tunnels?.Count);
    }

    [Fact]
    public async Task HostResourceCanWaitForTunnelDependentContainer()
    {
        var builder = DistributedApplication.CreateBuilder();

        var upstreamExecutable = builder.AddExecutable("upstreamExecutable", "command", "")
            .WithEndpoint(name: "http", targetPort: 1234, port: 5678, isProxied: true);

        var tunnelDependentContainer = builder.AddContainer("tunnelDependentContainer", "image")
            .WithEnvironment("UPSTREAM_PORT", upstreamExecutable.GetEndpoint("http").Property(EndpointProperty.Port))
            .WaitFor(upstreamExecutable);

        var downstreamExecutable = builder.AddExecutable("downstreamExecutable", "command", "")
            .WithEndpoint(name: "http", targetPort: 1235, port: 5679, isProxied: true)
            .WaitFor(tunnelDependentContainer);

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var events = new DcpExecutorEvents();
        events.Subscribe<OnResourceStartingContext>(async context =>
        {
            if (context.Resource == tunnelDependentContainer.Resource)
            {
                while (!kubernetesService.CreatedResources.OfType<Executable>().Any(e => e.AppModelResourceName == upstreamExecutable.Resource.Name))
                {
                    await Task.Delay(10, context.CancellationToken).ConfigureAwait(false);
                }
            }

            if (context.Resource == downstreamExecutable.Resource)
            {
                while (!kubernetesService.CreatedResources.OfType<Container>().Any(c => c.AppModelResourceName == tunnelDependentContainer.Resource.Name))
                {
                    await Task.Delay(10, context.CancellationToken).ConfigureAwait(false);
                }
            }
        });

        var dcpOptions = new DcpOptions
        {
            EnableAspireContainerTunnel = true,
        };

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, dcpOptions: dcpOptions, events: events);
        await appExecutor.RunApplicationAsync().DefaultTimeout();

        var createdResources = kubernetesService.CreatedResources.ToList();
        var upstreamExecutableResource = Assert.Single(createdResources.OfType<Executable>(), e => e.AppModelResourceName == upstreamExecutable.Resource.Name);
        var downstreamExecutableResource = Assert.Single(createdResources.OfType<Executable>(), e => e.AppModelResourceName == downstreamExecutable.Resource.Name);
        var tunnelDependentDcpContainer = Assert.Single(createdResources.OfType<Container>(), c => c.AppModelResourceName == tunnelDependentContainer.Resource.Name);

        Assert.True(
            createdResources.IndexOf(tunnelDependentDcpContainer) < createdResources.IndexOf(downstreamExecutableResource),
            "The downstream host resource should not be created until the tunnel-dependent container it waits for has been created.");

        var tunnelServices = createdResources
            .OfType<Service>()
            .Where(s => s.Metadata.Annotations.ContainsKey(CustomResource.ContainerTunnelInstanceName))
            .ToList();

        var upstreamTunnelService = Assert.Single(tunnelServices, s => s.AppModelResourceName == upstreamExecutable.Resource.Name);

        Assert.NotNull(tunnelDependentDcpContainer.Spec.Env);
        var upstreamPort = Assert.Single(tunnelDependentDcpContainer.Spec.Env, e => e.Name == "UPSTREAM_PORT").Value;
        Assert.Equal(upstreamTunnelService.AllocatedPort.ToString(), upstreamPort);

        var tunnelProxy = Assert.Single(createdResources.OfType<ContainerNetworkTunnelProxy>());
        Assert.Equal(1, tunnelProxy.Spec.Tunnels?.Count);
        Assert.NotNull(upstreamExecutableResource);
    }

    [Fact]
    public async Task EnvironmentCallbackThrows_OtherResourcesStillStart()
    {
        var builder = DistributedApplication.CreateBuilder();

        builder.AddContainer("failing", "image")
            .WithEnvironment(c =>
            {
                throw new InvalidOperationException("env callback failure");
            });

        builder.AddContainer("healthy", "image");

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var failedResources = new List<string>();
        var events = new DcpExecutorEvents();
        events.Subscribe<OnResourceFailedToStartContext>(c =>
        {
            failedResources.Add(c.Resource.Name);
            return Task.CompletedTask;
        });

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, events: events);
        await appExecutor.RunApplicationAsync();

        // The healthy container should have been created successfully.
        var createdContainers = kubernetesService.CreatedResources.OfType<Container>().ToList();
        Assert.Single(createdContainers, c => c.AppModelResourceName == "healthy");

        // The failing container should not have been created and should be reported as failed.
        Assert.DoesNotContain(createdContainers, c => c.AppModelResourceName == "failing");
        Assert.Single(failedResources, name => name == "failing");
    }

    [Fact]
    public async Task ArgsCallbackThrows_OtherResourcesStillStart()
    {
        var builder = DistributedApplication.CreateBuilder();

        builder.AddContainer("failing", "image")
            .WithArgs(c =>
            {
                throw new InvalidOperationException("args callback failure");
            });

        builder.AddContainer("healthy", "image");

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var failedResources = new List<string>();
        var events = new DcpExecutorEvents();
        events.Subscribe<OnResourceFailedToStartContext>(c =>
        {
            failedResources.Add(c.Resource.Name);
            return Task.CompletedTask;
        });

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, events: events);
        await appExecutor.RunApplicationAsync();

        // The healthy container should have been created successfully.
        var createdContainers = kubernetesService.CreatedResources.OfType<Container>().ToList();
        Assert.Single(createdContainers, c => c.AppModelResourceName == "healthy");

        // The failing container should not have been created and should be reported as failed.
        Assert.DoesNotContain(createdContainers, c => c.AppModelResourceName == "failing");
        Assert.Single(failedResources, name => name == "failing");
    }

    private static void HasKnownCommandAnnotations(IResource resource)
    {
        var commandAnnotations = resource.Annotations.OfType<ResourceCommandAnnotation>().ToList();
        Assert.Collection(commandAnnotations,
            a => Assert.Equal(KnownResourceCommands.StartCommand, a.Name),
            a => Assert.Equal(KnownResourceCommands.StopCommand, a.Name),
            a => Assert.Equal(KnownResourceCommands.RestartCommand, a.Name));
    }

    private static void HasKnownProjectCommandAnnotations(IResource resource)
    {
        var commandAnnotations = resource.Annotations.OfType<ResourceCommandAnnotation>().ToList();
        Assert.Collection(commandAnnotations,
            a => Assert.Equal(KnownResourceCommands.StartCommand, a.Name),
            a => Assert.Equal(KnownResourceCommands.StopCommand, a.Name),
            a => Assert.Equal(KnownResourceCommands.RestartCommand, a.Name),
            a => Assert.Equal(KnownResourceCommands.RebuildCommand, a.Name));
    }

    private static void AssertDefaultProjectProcessArgs(IReadOnlyList<string>? actualArgs, params string[] appArgs)
    {
        var expectedArgs = new List<string> { "run", "--project", "TestProject" };
        if (GetTestAssemblyConfiguration() is { } configuration)
        {
            expectedArgs.AddRange(["--configuration", configuration]);
        }
        expectedArgs.Add("--no-launch-profile");
        expectedArgs.AddRange(appArgs);

        Assert.Equal(expectedArgs, actualArgs);
    }

    private static string? GetTestAssemblyConfiguration() =>
        (Attribute.GetCustomAttribute(typeof(DcpExecutorTests).Assembly, typeof(System.Reflection.AssemblyConfigurationAttribute)) as System.Reflection.AssemblyConfigurationAttribute)?.Configuration;

    [Fact]
    public async Task PlainExecutable_LaunchConfigurationProducerThrows_FailsResource()
    {
        var builder = DistributedApplication.CreateBuilder();

        var debuggableExecutable = new TestExecutableResource("test-working-directory");
        builder.AddResource(debuggableExecutable).WithDebugSupport<TestExecutableResource, ExecutableLaunchConfiguration>(
            _ => throw new InvalidOperationException("Test exception from launch configuration producer"),
            "test");

        var runSessionInfo = new RunSessionInfo
        {
            ProtocolsSupported = ["test"],
            SupportedLaunchConfigurations = ["test"]
        };

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(runSessionInfo),
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234"
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        var failedResources = new List<IResource>();
        var events = new DcpExecutorEvents();
        events.Subscribe<OnResourceFailedToStartContext>(context =>
        {
            failedResources.Add(context.Resource);
            return Task.CompletedTask;
        });
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(
            distributedAppModel,
            kubernetesService: kubernetesService,
            configuration: configuration,
            events: events);

        await appExecutor.RunApplicationAsync();

        Assert.Empty(GetCreatedExecutablesForResource(kubernetesService, "TestExecutable"));
        Assert.Same(debuggableExecutable, Assert.Single(failedResources));
    }

    [Fact]
    public async Task Project_NonProjectLaunchConfig_ExtensionMode_RunsInIde()
    {
        // Arrange: A ProjectResource with a non-"project" launch config type (like Azure Functions)
        // should get ExecutionType.IDE and have its launch config applied in CreateExecutableAsync.
        var builder = DistributedApplication.CreateBuilder();

        var projectBuilder = builder.AddProject<TestProject>("proj", launchProfileName: null);
        // Remove the default "project" SupportsDebuggingAnnotation and replace with a non-"project" type
        var annotationToRemove = projectBuilder.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().FirstOrDefault();
        if (annotationToRemove is not null)
        {
            projectBuilder.Resource.Annotations.Remove(annotationToRemove);
        }
        projectBuilder.WithDebugSupport(mode => new ExecutableLaunchConfiguration("azure-functions") { Mode = mode }, "azure-functions");

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo { ProtocolsSupported = ["coreclr"], SupportedLaunchConfigurations = ["azure-functions"] }),
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234",
            [KnownConfigNames.DebugSessionRunMode] = "Debug"
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        // Act
        await appExecutor.RunApplicationAsync();

        // Assert
        var exe = GetCreatedExecutableForResource(kubernetesService, "proj");
        Assert.Equal(ExecutionType.IDE, exe.Spec.ExecutionType);

        // The launch config should have been applied in CreateExecutableAsync (not PrepareProjectExecutables)
        Assert.True(exe.TryGetAnnotationAsObjectList<ExecutableLaunchConfiguration>(Executable.LaunchConfigurationsAnnotation, out var launchConfigs));
        var config = Assert.Single(launchConfigs);
        Assert.Equal("azure-functions", config.Type);
        Assert.Equal(ExecutableLaunchMode.Debug, config.Mode);
    }

    [Fact]
    public async Task Project_NonProjectLaunchConfig_AnnotatorThrows_FailsResource()
    {
        var builder = DistributedApplication.CreateBuilder();

        var projectBuilder = builder.AddProject<TestProject>("proj", launchProfileName: null);
        var annotationToRemove = projectBuilder.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().FirstOrDefault();
        if (annotationToRemove is not null)
        {
            projectBuilder.Resource.Annotations.Remove(annotationToRemove);
        }
        projectBuilder.WithDebugSupport<ProjectResource, ExecutableLaunchConfiguration>(
            _ => throw new InvalidOperationException("Test exception from launch configuration producer"),
            "azure-functions");

        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo { ProtocolsSupported = ["coreclr"], SupportedLaunchConfigurations = ["azure-functions"] }),
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234"
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        var failedResources = new List<IResource>();
        var events = new DcpExecutorEvents();
        events.Subscribe<OnResourceFailedToStartContext>(context =>
        {
            failedResources.Add(context.Resource);
            return Task.CompletedTask;
        });

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(
            distributedAppModel,
            kubernetesService: kubernetesService,
            configuration: configuration,
            events: events);

        await appExecutor.RunApplicationAsync();

        Assert.Empty(kubernetesService.CreatedResources.OfType<Executable>());
        Assert.Same(projectBuilder.Resource, Assert.Single(failedResources));
    }

    [Fact]
    public async Task Project_NonProjectLaunchConfig_UnsupportedByExtension_RunsInProcess()
    {
        // Arrange: A ProjectResource with a non-"project" launch config type that the extension
        // does not support should run as ExecutionType.Process.
        var builder = DistributedApplication.CreateBuilder();

        var projectBuilder = builder.AddProject<TestProject>("proj", launchProfileName: null);
        // Remove the default "project" SupportsDebuggingAnnotation and replace with "azure-functions"
        var annotationToRemove = projectBuilder.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().FirstOrDefault();
        if (annotationToRemove is not null)
        {
            projectBuilder.Resource.Annotations.Remove(annotationToRemove);
        }
        projectBuilder.WithDebugSupport(mode => new ExecutableLaunchConfiguration("azure-functions") { Mode = mode }, "azure-functions");

        // Extension does NOT list "azure-functions" in SupportedLaunchConfigurations
        var configDict = new Dictionary<string, string?>
        {
            [DcpExecutor.DebugSessionPortVar] = "12345",
            [KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo { ProtocolsSupported = ["coreclr"], SupportedLaunchConfigurations = ["project"] }),
            [KnownConfigNames.ExtensionEndpoint] = "http://localhost:1234"
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, configuration: configuration);

        // Act
        await appExecutor.RunApplicationAsync();

        // Assert
        var exe = GetCreatedExecutableForResource(kubernetesService, "proj");
        Assert.Equal(ExecutionType.Process, exe.Spec.ExecutionType);
    }

    private static Executable GetCreatedExecutableForResource(TestKubernetesService kubernetesService, string appModelResourceName)
    {
        return Assert.Single(GetCreatedExecutablesForResource(kubernetesService, appModelResourceName));
    }

    private static List<Executable> GetCreatedExecutablesForResource(TestKubernetesService kubernetesService, string appModelResourceName)
    {
        return [.. kubernetesService.CreatedResources
            .OfType<Executable>()
            .Where(e => e.AppModelResourceName == appModelResourceName)];
    }

    private static List<Container> GetCreatedContainersForResource(TestKubernetesService kubernetesService, string appModelResourceName)
    {
        return [.. kubernetesService.CreatedResources
            .OfType<Container>()
            .Where(c => c.AppModelResourceName == appModelResourceName)];
    }

    [Fact]
    public async Task Project_WithTerminal_PopulatesPerReplicaTerminalSpec()
    {
        // When a project resource is configured with WithTerminal(), each replica's
        // Executable spec should carry a Terminal block whose UdsPath matches the
        // per-replica producer path from TerminalHostLayout.ProducerUdsPaths.

        var builder = DistributedApplication.CreateBuilder();
        var resource = builder.AddProject<Projects.ServiceA>("ServiceA")
            .WithReplicas(2)
            .WithTerminal(options =>
            {
                options.Columns = 100;
                options.Rows = 30;
            })
            .Resource;

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // The per-replica TerminalHostResources are now materialized inside BeforeStartEvent
        // (see TerminalResourceBuilderExtensions.WithTerminal). DcpExecutor.RunApplicationAsync
        // does not raise that event itself, so the test publishes it manually before running
        // the executor — otherwise TerminalAnnotation.TerminalHosts would still be empty when
        // ExecutableCreator looks up the per-replica producer UDS path.
        await builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services, distributedAppModel));

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);
        await appExecutor.RunApplicationAsync();

        var exes = kubernetesService.CreatedResources.OfType<Executable>()
            .Where(e => e.AppModelResourceName == "ServiceA")
            .OrderBy(e => e.Metadata.Annotations?[CustomResource.ResourceReplicaIndex], StringComparer.Ordinal)
            .ToList();
        Assert.Equal(2, exes.Count);

        // Each parent replica gets its own per-replica TerminalHostResource — the
        // annotation now carries the list, and the Executable for replica i must
        // dial the producer UDS owned by TerminalHosts[i].
        var hosts = resource.Annotations.OfType<TerminalAnnotation>().Single().TerminalHosts;
        Assert.Equal(2, hosts.Count);

        for (var i = 0; i < exes.Count; i++)
        {
            var spec = exes[i].Spec.Terminal;
            Assert.NotNull(spec);
            Assert.Equal(hosts[i].Layout.ProducerUdsPath, spec!.UdsPath);
            Assert.Equal(100, spec.Cols);
            Assert.Equal(30, spec.Rows);
            // Aspire's terminal host owns the listener at UdsPath, so DCP must dial it.
            // Changing this to "listen" (the DCP default) would silently break attach.
            Assert.Equal("connect", spec.SocketMode);
        }
    }

    [Fact]
    public async Task Project_WithoutTerminal_HasNullTerminalSpec()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.AddProject<Projects.ServiceA>("ServiceA");

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);
        await appExecutor.RunApplicationAsync();

        var exe = Assert.Single(kubernetesService.CreatedResources.OfType<Executable>(), e => e.AppModelResourceName == "ServiceA");
        Assert.Null(exe.Spec.Terminal);
    }

    [Fact]
    public async Task PlainExecutable_WithTerminal_PopulatesTerminalSpec()
    {
        // Regression test: plain executables added via AddExecutable() were missing
        // the ResourceReplicaIndex/ResourceReplicaCount annotations, which caused
        // BuildExecutableConfiguration to skip the spec.Terminal wire-up entirely
        // (the per-replica producer UDS path lookup in TerminalHostLayout was guarded
        // by a successful TryGetReplicaIndex).

        var builder = DistributedApplication.CreateBuilder();
        var resource = builder.AddExecutable("shell", "cmd.exe", ".")
            .WithTerminal(options =>
            {
                options.Columns = 100;
                options.Rows = 30;
            })
            .Resource;

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // BeforeStartEvent is where the per-replica TerminalHostResources are now
        // materialized. See the matching note in Project_WithTerminal_PopulatesPerReplicaTerminalSpec.
        await builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services, distributedAppModel));

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);
        await appExecutor.RunApplicationAsync();

        var exe = Assert.Single(kubernetesService.CreatedResources.OfType<Executable>(), e => e.AppModelResourceName == "shell");
        // Plain executables are always single-replica — there's exactly one host
        // at index 0, owning the only producer UDS.
        var host = Assert.Single(resource.Annotations.OfType<TerminalAnnotation>().Single().TerminalHosts);

        Assert.NotNull(exe.Spec.Terminal);
        Assert.Equal(host.Layout.ProducerUdsPath, exe.Spec.Terminal!.UdsPath);
        Assert.Equal(100, exe.Spec.Terminal.Cols);
        Assert.Equal(30, exe.Spec.Terminal.Rows);
        // Aspire's terminal host owns the listener, so DCP must dial it. The DCP
        // default is "listen", so we have to explicitly opt into "connect".
        Assert.Equal("connect", exe.Spec.Terminal.SocketMode);

        // Plain executables are always single-replica today; both annotations must
        // be present for the per-replica lookup to succeed.
        Assert.Equal("1", exe.Metadata.Annotations?[CustomResource.ResourceReplicaCount]);
        Assert.Equal("0", exe.Metadata.Annotations?[CustomResource.ResourceReplicaIndex]);
    }

    [Fact]
    public async Task Container_WithTerminal_PopulatesTerminalSpec()
    {
        // Containers are always single-replica today, so ContainerCreator wires
        // spec.Terminal from TerminalHosts[0]. This guards the DCP wire-up so a
        // future refactor that drops the assignment (or changes SocketMode away
        // from "connect") gets caught here rather than at attach time.
        var builder = DistributedApplication.CreateBuilder();
        var resource = builder.AddContainer("aContainer", "image")
            .WithTerminal(options =>
            {
                options.Columns = 100;
                options.Rows = 30;
            })
            .Resource;

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // BeforeStartEvent materializes the per-replica TerminalHostResources;
        // see the matching note in Project_WithTerminal_PopulatesPerReplicaTerminalSpec.
        await builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services, distributedAppModel));

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);
        await appExecutor.RunApplicationAsync();

        var container = Assert.Single(kubernetesService.CreatedResources.OfType<Container>(), c => c.AppModelResourceName == "aContainer");
        var host = Assert.Single(resource.Annotations.OfType<TerminalAnnotation>().Single().TerminalHosts);

        Assert.NotNull(container.Spec.Terminal);
        Assert.Equal(host.Layout.ProducerUdsPath, container.Spec.Terminal!.UdsPath);
        Assert.Equal(100, container.Spec.Terminal.Cols);
        Assert.Equal(30, container.Spec.Terminal.Rows);
        // Aspire's terminal host owns the listener, so DCP must dial it.
        Assert.Equal("connect", container.Spec.Terminal.SocketMode);
    }

    [Fact]
    public void TerminalSpec_SerializesToDcpWireContract()
    {
        // The DCP terminal contract is a JSON document with camelCase property names
        // (driven by [JsonPropertyName] attributes on TerminalSpec). DCP parses the
        // payload into api/v1/terminal_types.go; field names and SocketMode values
        // must stay in lockstep with the Go side. This test guards the wire shape so
        // that a rename or attribute removal in the Aspire model gets caught here
        // rather than at runtime when DCP rejects the spec.
        var spec = new TerminalSpec
        {
            UdsPath = "/tmp/aspire/term.sock",
            SocketMode = "connect",
            Cols = 100,
            Rows = 30
        };

        var json = JsonSerializer.Serialize(spec);

        Assert.Equal(
            "{\"udsPath\":\"/tmp/aspire/term.sock\",\"socketMode\":\"connect\",\"cols\":100,\"rows\":30}",
            json);
    }

    [Fact]
    public void MonitorTimestamps_SerializeToDcpMicroTimeWireContract()
    {
        var wholeSecondTimestamp = DateTime.SpecifyKind(DateTime.MinValue.AddMinutes(6).AddSeconds(30), DateTimeKind.Utc);
        var fractionalSecondTimestamp = DateTime.SpecifyKind(DateTime.MinValue.AddMinutes(6).AddSeconds(30).AddMilliseconds(123), DateTimeKind.Utc);

        AssertMonitorTimestamp(new ContainerSpec { MonitorPid = 1234, MonitorTimestamp = wholeSecondTimestamp }, "0001-01-01T00:06:30.000000Z");
        AssertMonitorTimestamp(new ContainerSpec { MonitorPid = 1234, MonitorTimestamp = fractionalSecondTimestamp }, "0001-01-01T00:06:30.123000Z");
        AssertMonitorTimestamp(new ExecutableSpec { MonitorPid = 1234, MonitorTimestamp = wholeSecondTimestamp }, "0001-01-01T00:06:30.000000Z");
        AssertMonitorTimestamp(new ExecutableSpec { MonitorPid = 1234, MonitorTimestamp = fractionalSecondTimestamp }, "0001-01-01T00:06:30.123000Z");

        static void AssertMonitorTimestamp<T>(T spec, string expected)
        {
            // DCP models monitorTimestamp as Kubernetes metav1.MicroTime:
            //   "0001-01-01T00:06:30.000000Z"
            // Kubernetes requires exactly six fractional digits, while System.Text.Json's
            // default DateTime converter trims trailing zeroes and can produce values
            // such as "0001-01-01T00:06:30Z" that DCP rejects.
            var json = JsonSerializer.Serialize(spec);
            using var document = JsonDocument.Parse(json);

            Assert.Equal(expected, document.RootElement.GetProperty("monitorTimestamp").GetString());
        }
    }

    [Fact]
    public void MonitorTimestamps_DeserializeFromDcpMicroTimeWireContract()
    {
        var containerSpec = JsonSerializer.Deserialize<ContainerSpec>("""{"monitorPid":1234,"monitorTimestamp":"0001-01-01T00:06:30.123000Z"}""");
        var executableSpec = JsonSerializer.Deserialize<ExecutableSpec>("""{"monitorPid":1234,"monitorTimestamp":"0001-01-01T00:06:30.123000Z"}""");
        var expectedTimestamp = DateTime.SpecifyKind(DateTime.MinValue.AddMinutes(6).AddSeconds(30).AddMilliseconds(123), DateTimeKind.Utc);

        Assert.NotNull(containerSpec);
        Assert.Equal(expectedTimestamp, containerSpec.MonitorTimestamp);
        Assert.NotNull(executableSpec);
        Assert.Equal(expectedTimestamp, executableSpec.MonitorTimestamp);
    }

    private static DcpExecutor CreateAppExecutor(
        DistributedApplicationModel distributedAppModel,
        IHostEnvironment? hostEnvironment = null,
        IConfiguration? configuration = null,
        IKubernetesService? kubernetesService = null,
        DcpOptions? dcpOptions = null,
        IUserSecretsManager? userSecretsManager = null,
        ResourceLoggerService? resourceLoggerService = null,
        DcpExecutorEvents? events = null,
        Hosting.Eventing.IDistributedApplicationEventing? distributedApplicationEventing = null,
        ILogger<ContainerCreator>? containerCreatorLogger = null,
        ILogger<DcpExecutor>? logger = null,
        DistributedApplicationOptions? distributedApplicationOptions = null)
    {
        if (configuration == null)
        {
            var builder = new ConfigurationBuilder();
            builder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [KnownConfigNames.DashboardOtlpGrpcEndpointUrl] = "http://localhost",
                ["AppHost:BrowserToken"] = "TestBrowserToken!",
                ["AppHost:OtlpApiKey"] = "TestOtlpApiKey!"
            });

            configuration = builder.Build();
        }

        resourceLoggerService ??= new ResourceLoggerService();
        dcpOptions ??= new DcpOptions { DashboardPath = "./dashboard" };

        var developerCertificateService = new TestDeveloperCertificateService(new List<X509Certificate2>(), false, false, false);

        var nameGenerator = new DcpNameGenerator(configuration, Options.Create(dcpOptions));
        var executionContext = new DistributedApplicationExecutionContext(new DistributedApplicationExecutionContextOptions(DistributedApplicationOperation.Run)
        {
            Services = new TestServiceProvider(configuration)
                .AddService<IDeveloperCertificateService>(developerCertificateService)
                .AddService(distributedAppModel)
                .AddService(Options.Create(dcpOptions))
                .AddService(resourceLoggerService)
        });
        var ks = kubernetesService ?? new TestKubernetesService();
        var dcpEvts = events ?? new DcpExecutorEvents();
        var fileSystemService = new FileSystemService(configuration);
        var locations = new Locations(fileSystemService);
        var aspireStoreDirectory = configuration[AspireStore.AspireStorePathKeyName];
        if (string.IsNullOrWhiteSpace(aspireStoreDirectory))
        {
            aspireStoreDirectory = fileSystemService.TempDirectory.CreateTempSubdirectory("aspire-store").Path;
        }

        var aspireStore = new AspireStore(Path.Join(aspireStoreDirectory, ".aspire"), fileSystemService);
        var hostEnv = hostEnvironment ?? new TestHostEnvironment();
        var dcpDependencyCheckService = new TestDcpDependencyCheckService();

        var appResources = new DcpAppResourceStore();
        var proxylessEndpointPortAllocator = new ProxylessEndpointPortAllocator(Options.Create(dcpOptions));
        var applicationOptions = distributedApplicationOptions ?? new DistributedApplicationOptions();
        var executableConfigurationResolver = new ExecutableConfigurationResolver(executionContext, locations, aspireStore);
        var executableLaunchPolicy = new ExecutableLaunchPolicy(configuration);

        var executableCreator = new ExecutableCreator(
            nameGenerator,
            distributedAppModel,
            appResources,
            executableConfigurationResolver,
            configuration,
            applicationOptions,
            executableLaunchPolicy,
            NullLogger<ExecutableCreator>.Instance);

        var containerCreator = new ContainerCreator(
            configuration,
            Options.Create(dcpOptions),
            nameGenerator,
            distributedAppModel,
            executionContext,
            resourceLoggerService,
            dcpDependencyCheckService,
            hostEnv,
            containerCreatorLogger ?? NullLogger<ContainerCreator>.Instance,
            appResources);

        return new DcpExecutor(
            logger ?? NullLogger<DcpExecutor>.Instance,
            NullLogger<DistributedApplication>.Instance,
            distributedAppModel,
            ks,
            configuration,
            distributedApplicationEventing ?? new Hosting.Eventing.DistributedApplicationEventing(),
            Options.Create(dcpOptions),
            executionContext,
            resourceLoggerService,
            dcpDependencyCheckService,
            nameGenerator,
            dcpEvts,
            appResources,
            executableCreator,
            containerCreator,
            new ProfilingTelemetry(configuration),
            proxylessEndpointPortAllocator,
            userSecretsManager ?? NoopUserSecretsManager.Instance);
    }

    private static async Task<string?> GetPlainExecutableSslCertDirAsync(Action<IResourceBuilder<TestExecutableResource>>? configure = null)
    {
        var builder = DistributedApplication.CreateBuilder();
        using var certificate = CreateTestCertificate();
        var certificateAuthorities = builder.AddCertificateAuthorityCollection("certificates")
            .WithCertificate(certificate);

        var executable = new TestExecutableResource("test-working-directory");
        var executableBuilder = builder.AddResource(executable)
            .WithCertificateAuthorityCollection(certificateAuthorities);
        configure?.Invoke(executableBuilder);

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);

        await appExecutor.RunApplicationAsync();

        var exe = Assert.Single(kubernetesService.CreatedResources.OfType<Executable>(), e => e.AppModelResourceName == "TestExecutable");
        return Assert.Single(exe.Spec.Env!, e => e.Name == "SSL_CERT_DIR").Value;
    }

    private static void AssertPortAllocatedFromProxylessEndpointAllocatorRange(int port)
    {
        var defaultOptions = new DcpOptions();
        Assert.InRange(port, defaultOptions.ProxylessEndpointPortRangeStart, defaultOptions.ProxylessEndpointPortRangeEnd);
    }

    private static (int First, int Second) GetAvailableConsecutivePortPair()
    {
        // Tests configure single-port allocation ranges, so the helper must agree exactly with
        // the allocator on what "available" means. Reuse the allocator's IPv4+IPv6 probe instead
        // of a separate IPv4-only bind that could return a port the allocator later rejects.
        //
        // Scan from a random offset rather than always starting at the bottom of the range. Test
        // classes run in parallel and a deterministic start makes concurrent runs converge on the
        // same low ports, where a transient probe collision throws against a zero-slack range.
        const int rangeStart = 10000;
        const int rangeEndExclusive = 32767; // Leave room for port + 1 within the proxyless default range.
        var span = rangeEndExclusive - rangeStart;
        var offset = Random.Shared.Next(span);

        for (var i = 0; i < span; i++)
        {
            var port = rangeStart + ((offset + i) % span);
            if (ProxylessEndpointPortAllocator.TryProbePort(port, ProtocolType.Tcp) &&
                ProxylessEndpointPortAllocator.TryProbePort(port + 1, ProtocolType.Tcp))
            {
                return (port, port + 1);
            }
        }

        throw new InvalidOperationException("Could not find two consecutive available ports.");
    }

    private static bool RetryTillTrueOrTimeout(Func<bool> check, int timeoutMilliseconds)
    {
        var retry = new ResiliencePipelineBuilder<bool>()
            .AddRetry(new RetryStrategyOptions<bool>
            {
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromMilliseconds(200),
                MaxDelay = TimeSpan.FromSeconds(2),
                MaxRetryAttempts = int.MaxValue,
                ShouldHandle = args => ValueTask.FromResult(!args.Outcome.Result)
            })
            .AddTimeout(TimeSpan.FromMilliseconds(timeoutMilliseconds))
            .Build();
        return retry.Execute(check);
    }

    private static void AssertEffectiveArgumentIndexesMatchSpecArgs(IReadOnlyList<AppLaunchArgumentAnnotation> argAnnotations, IReadOnlyList<string>? specArgs)
    {
        foreach (var annotation in argAnnotations)
        {
            if (annotation.EffectiveArgumentIndex is not int index)
            {
                continue;
            }

            Assert.NotNull(specArgs);
            Assert.InRange(index, 0, specArgs.Count - 1);
            Assert.Equal(annotation.Argument, specArgs[index]);
        }
    }

    private static Aspire.Hosting.Dcp.ResourceSnapshotBuilder CreateSnapshotBuilder(DistributedApplicationModel model)
    {
        return new(new DcpResourceState(model.Resources.ToDictionary(r => r.Name), []));
    }

    private static CustomResourceSnapshot CreatePreviousSnapshot()
    {
        return new()
        {
            ResourceType = "resource",
            Properties = []
        };
    }

    private static ResourcePropertySnapshot GetProperty(CustomResourceSnapshot snapshot, string name)
    {
        return Assert.Single(snapshot.Properties, p => p.Name == name);
    }

    private static IEnumerable<T> GetEnumerablePropertyValue<T>(CustomResourceSnapshot snapshot, string name)
    {
        var property = GetProperty(snapshot, name);
        return Assert.IsAssignableFrom<IEnumerable<T>>(property.Value);
    }

    private static X509Certificate2 CreateTestCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            new X500DistinguishedName("CN=test"),
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var serialNumber = new byte[16];
        RandomNumberGenerator.Fill(serialNumber);
        var generator = X509SignatureGenerator.CreateForRSA(rsa, RSASignaturePadding.Pkcs1);

        return request.Create(
            request.SubjectName,
            generator,
            DateTimeOffset.Now,
            DateTimeOffset.Now.AddYears(1),
            serialNumber);
    }

    private sealed class TestExecutableResource(string directory) : ExecutableResource("TestExecutable", "test", directory);
    private sealed class TestOtherExecutableResource(string directory) : ExecutableResource("TestOtherExecutable", "test-other", directory);
    private sealed class TestContainerResource(string name) : Resource(name), IResourceWithEndpoints;

    private sealed class NullValueProvider : IValueProvider
    {
        public static NullValueProvider Instance { get; } = new();

        public ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<string?>(null);
        }
    }

    // Models a DotnetProjectResource: a plain ExecutableResource (launches `dotnet`) that carries
    // IProjectMetadata and a "project" SupportsDebuggingAnnotation. Used to verify the DCP project-launch
    // generalization without taking a dependency on Aspire.Hosting.Dotnet.
    private sealed class TestDotnetProjectExecutableResource(string directory) : ExecutableResource("TestDotnetProject", "dotnet", directory);

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string ApplicationName { get; set; } = default!;
        public IFileProvider ContentRootFileProvider { get; set; } = default!;
        public string ContentRootPath { get; set; } = default!;
        public string EnvironmentName { get; set; } = default!;
    }

    private sealed class TestProject : IProjectMetadata
    {
        public string ProjectPath => "TestProject";
        public LaunchSettings LaunchSettings { get; } = new();
    }

    private sealed class TestFileBasedProject(string projectPath) : IProjectMetadata
    {
        public string ProjectPath { get; } = projectPath;
        public LaunchSettings LaunchSettings { get; } = new();
    }

    private sealed class TestMauiLaunchConfiguration : ExecutableLaunchConfiguration
    {
        public TestMauiLaunchConfiguration() : base("maui")
        {
        }

        [JsonPropertyName("project_path")]
        public string ProjectPath { get; set; } = string.Empty;

        [JsonPropertyName("target_framework")]
        public string TargetFramework { get; set; } = string.Empty;

        [JsonPropertyName("platform")]
        public string Platform { get; set; } = string.Empty;

        [JsonPropertyName("target_kind")]
        public string TargetKind { get; set; } = string.Empty;

        [JsonPropertyName("msbuild_properties")]
        public Dictionary<string, string>? MsBuildProperties { get; set; }
    }

    private sealed class TestProjectWithLaunchSettings : IProjectMetadata
    {
        public string ProjectPath => "TestProjectWithLaunchSettings";
        public LaunchSettings LaunchSettings { get; } = CreateLaunchSettings();

        private static LaunchSettings CreateLaunchSettings()
        {
            var settings = new LaunchSettings();
            settings.Profiles["Foo"] = new LaunchProfile
            {
                CommandName = "Project",
                LaunchUrl = "http://localhost:5000",
                ApplicationUrl = "http://localhost:5000;https://localhost:5001",
                EnvironmentVariables = new Dictionary<string, string>
                {
                    ["ASPNETCORE_ENVIRONMENT"] = "Development"
                }
            };
            settings.Profiles["http"] = new LaunchProfile
            {
                CommandName = "Project",
                LaunchUrl = "http://localhost:5003",
                ApplicationUrl = "http://localhost:5003;",
                EnvironmentVariables = new Dictionary<string, string>
                {
                    ["ASPNETCORE_ENVIRONMENT"] = "Development"
                }
            };
            return settings;
        }
    }

    private sealed class TestProjectWithLaunchProfileCommandLineArgs : IProjectMetadata
    {
        public string ProjectPath => "TestProjectWithLaunchProfileCommandLineArgs";
        public LaunchSettings LaunchSettings { get; } = CreateLaunchSettings();

        private static LaunchSettings CreateLaunchSettings()
        {
            var settings = new LaunchSettings();
            settings.Profiles["http"] = new LaunchProfile
            {
                CommandName = "Project",
                CommandLineArgs = "--profile-arg \"profile value\""
            };

            return settings;
        }
    }

    private sealed class TestProjectNoProfiles : IProjectMetadata
    {
        public string ProjectPath => "TestProjectNoProfiles";
        public LaunchSettings LaunchSettings { get; } = new();
    }

    private sealed class TestProjectMultiProfileOrder : IProjectMetadata
    {
        public string ProjectPath => "TestProjectMultiProfileOrder";
        public LaunchSettings LaunchSettings { get; } = CreateLaunchSettings();

        private static LaunchSettings CreateLaunchSettings()
        {
            var settings = new LaunchSettings();
            // Intentionally non-alphabetical insertion order to verify iteration order.
            settings.Profiles["Zed"] = new LaunchProfile { CommandName = "Project", ApplicationUrl = "http://localhost:6001" };
            settings.Profiles["Alpha"] = new LaunchProfile { CommandName = "Project", ApplicationUrl = "http://localhost:6002" };
            settings.Profiles["Beta"] = new LaunchProfile { CommandName = "Project", ApplicationUrl = "http://localhost:6003" };
            return settings;
        }
    }

    private sealed class CustomChildResource(string name, IResource parent) : Resource(name), IResourceWithParent
    {
        public IResource Parent => parent;
    }

    private sealed class TestProjectWithExecutableLaunchProfile : IProjectMetadata
    {
        public string ProjectPath => "TestProjectWithExecutableLaunchProfile";
        public LaunchSettings LaunchSettings { get; } = CreateLaunchSettings();

        private static LaunchSettings CreateLaunchSettings()
        {
            var settings = new LaunchSettings();
            settings.Profiles["Aspire_TestFunction"] = new LaunchProfile
            {
                CommandName = "Executable",
                ExecutablePath = "dotnet",
                CommandLineArgs = "exec --depsfile ./TestLib.deps.json --runtimeconfig ./TestLib.runtimeconfig.json $(HOME)/.dotnet/tools/TestTool.dll TestLib::TestLib.Functions::Handler"
            };
            return settings;
        }
    }

}
