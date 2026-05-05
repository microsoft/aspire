// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIRECERTIFICATES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipelines;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Aspire.Dashboard.Model;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Dcp.Model;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Tests.Utils;
using k8s.Models;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace Aspire.Hosting.Tests.Dcp;

[Trait("Partition", "4")]
public class DcpExecutorTests
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
        using var tempDockerfileContext = await DockerfileUtils.CreateTemporaryDockerfileAsync();

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

        var executables = kubernetesService.CreatedResources.OfType<Executable>().ToList();
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

        var executables = kubernetesService.CreatedResources.OfType<Executable>().ToList();
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
        string[] callArgs = [..dotnetToolExecArgs, ..toolArgs];

        Assert.Equal(callArgs, exe.Spec.Args);

        Assert.True(exe.TryGetAnnotationAsObjectList<AppLaunchArgumentAnnotation>(CustomResource.ResourceAppArgsAnnotation, out var argAnnotations));
        Assert.Equal(toolArgs, argAnnotations.Select(a => a.Argument));
        AssertEffectiveArgumentIndexesMatchSpecArgs(argAnnotations, exe.Spec.Args);
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

        var exe = Assert.Single(kubernetesService.CreatedResources.OfType<Executable>());
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
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();

        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService, dcpOptions: dcpOptions, events: events);
        await appExecutor.RunApplicationAsync();

        var executables = kubernetesService.CreatedResources.OfType<Executable>().ToList();

        var exe1 = Assert.Single(executables);
        var callCount1 = exe1.Spec.Env!.Single(e => e.Name == "CALL_COUNT");
        Assert.Equal("1", callCount1.Value);

        Assert.Single(exe1.Spec.Args!, a => a == "--no-build");
        Assert.Single(exe1.Spec.Args!, a => a == "--test");
        Assert.True(exe1.TryGetAnnotationAsObjectList<AppLaunchArgumentAnnotation>(CustomResource.ResourceAppArgsAnnotation, out var argAnnotations1));
        Assert.Single(argAnnotations1, a => a.Argument == "--test");
        AssertEffectiveArgumentIndexesMatchSpecArgs(argAnnotations1, exe1.Spec.Args);

        var reference = appExecutor.GetResource(exe1.Metadata.Name);

        await appExecutor.StopResourceAsync(reference, CancellationToken.None);

        await appExecutor.StartResourceAsync(reference, CancellationToken.None);

        executables = kubernetesService.CreatedResources.OfType<Executable>().ToList();
        Assert.Equal(2, executables.Count);

        var exe2 = executables[1];
        var callCount2 = exe2.Spec.Env!.Single(e => e.Name == "CALL_COUNT");
        Assert.Equal("2", callCount2.Value);

        Assert.Single(exe2.Spec.Args!, a => a == "--no-build");
        Assert.Single(exe2.Spec.Args!, a => a == "--test");
        Assert.True(exe2.TryGetAnnotationAsObjectList<AppLaunchArgumentAnnotation>(CustomResource.ResourceAppArgsAnnotation, out var argAnnotations2));
        Assert.Single(argAnnotations2, a => a.Argument == "--test");
        AssertEffectiveArgumentIndexesMatchSpecArgs(argAnnotations2, exe2.Spec.Args);
    }

    [Fact]
    public async Task EndpointPortsExecutableNotReplicatedProxiedNoPortNoTargetPort()
    {
        var builder = DistributedApplication.CreateBuilder();

        var exe = builder.AddExecutable("CoolProgram", "cool", Environment.CurrentDirectory, "--alpha", "--bravo")
            .WithEndpoint(name: "NoPortNoTargetPort", env: "NO_PORT_NO_TARGET_PORT", isProxied: true);

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);
        await appExecutor.RunApplicationAsync();

        var dcpExe = Assert.Single(kubernetesService.CreatedResources.OfType<Executable>());
        Assert.True(dcpExe.TryGetAnnotationAsObjectList<ServiceProducerAnnotation>(CustomResource.ServiceProducerAnnotation, out var spAnnList));

        // Neither Port, nor TargetPort are set
        // Clients use proxy, MAY have the proxy port injected.
        // Proxy gets autogenerated port.
        // Program gets (different) autogenerated port that MUST be injected via env var / startup param.
        var svc = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "CoolProgram");
        Assert.Equal(AddressAllocationModes.Localhost, svc.Spec.AddressAllocationMode);
        Assert.True(svc.Status?.EffectivePort >= TestKubernetesService.StartOfAutoPortRange);
        Assert.True(spAnnList.Single(ann => ann.ServiceName == "CoolProgram").Port is null,
            "Expected service producer (target) port to not be set (leave allocation to DCP)");
        var envVarVal = dcpExe.Spec.Env?.Single(v => v.Name == "NO_PORT_NO_TARGET_PORT").Value;
        Assert.False(string.IsNullOrWhiteSpace(envVarVal));
        Assert.Contains("""portForServing "CoolProgram" """, envVarVal);
    }

    [Fact]
    public async Task EndpointPortsExecutableNotReplicatedProxiedPortSetNoTargetPort()
    {
        var builder = DistributedApplication.CreateBuilder();

        const int desiredPort = TestKubernetesService.StartOfAutoPortRange - 1000;
        var exe = builder.AddExecutable("CoolProgram", "cool", Environment.CurrentDirectory, "--alpha", "--bravo")
            .WithEndpoint(name: "PortSetNoTargetPort", port: desiredPort, env: "PORT_SET_NO_TARGET_PORT", isProxied: true);

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);
        await appExecutor.RunApplicationAsync();

        var dcpExe = Assert.Single(kubernetesService.CreatedResources.OfType<Executable>());
        Assert.True(dcpExe.TryGetAnnotationAsObjectList<ServiceProducerAnnotation>(CustomResource.ServiceProducerAnnotation, out var spAnnList));

        // Port is set, but TargetPort is empty
        // Clients use proxy, MAY have the proxy port injected.
        // Proxy uses Port.
        // Program gets autogenerated port that MUST be injected via env var / startup param.
        var svc = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "CoolProgram");
        Assert.Equal(AddressAllocationModes.Localhost, svc.Spec.AddressAllocationMode);
        Assert.Equal(desiredPort, svc.Status?.EffectivePort);
        Assert.True(spAnnList.Single(ann => ann.ServiceName == "CoolProgram").Port is null,
            "Expected service producer (target) port to not be set (leave allocation to DCP)");
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
    public async Task EndpointPortsExecutableNotReplicatedProxylessPortSetNoTargetPort()
    {
        var builder = DistributedApplication.CreateBuilder();

        const int desiredPort = TestKubernetesService.StartOfAutoPortRange - 1000;
        builder.AddExecutable("CoolProgram", "cool", Environment.CurrentDirectory, "--alpha", "--bravo")
            .WithEndpoint(name: "PortSetNoTargetPort", port: desiredPort, env: "PORT_SET_NO_TARGET_PORT", isProxied: false);

        // All these configurations are effectively the same because EndpointAnnotation constructor for proxy-less endpoints
        // will make sure Port and TargetPort have the same value if one is specified but the other is not.

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

        // All these configurations are effectively the same because EndpointAnnotation constructor for proxy-less endpoints
        // will make sure Port and TargetPort have the same value if one is specified but the other is not.

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

        // All these configurations are effectively the same because EndpointAnnotation constructor for proxy-less endpoints
        // will make sure Port and TargetPort have the same value if one is specified but the other is not.

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
            // Note: this configuration (neither Endpoint.Port, nor Endpoint.TargetPort set) COULD be supported as follows:
            // Clients connect directly to the program, MAY have the program port injected.
            // Program gets autogenerated port that MUST be injected via env var/startup param.
            //
            // BUT
            //
            // as of Aspire GA (May 2024) this is not supported due to how Aspire app model consumes autogenerated ports.
            // Namely, the Aspire ApplicationExecutor creates Services and waits for Services to have ports allocated (by DCP)
            // before creating Executables and Containers that implement these services.
            // This does not work for proxy-less Services backed by Executables with auto-generated ports, because these Services
            // get their ports from Executables that are backing them, and those Executables, in turn, get their ports when they get started.
            // Delaying Executable creation like Aspire ApplicationExecutor does means the Services will never get their ports.
            (
                er => er.WithEndpoint(name: "NoPortNoTargetPort", env: "NO_PORT_NO_TARGET_PORT", isProxied: false),
                "needs to specify a port for endpoint"
            ),

            // Invalid configuration: both Port and TargetPort set, but to different values.
            (
                er => er.WithEndpoint(name: "PortAndTargetPortSetDifferently", port: desiredPortOne, targetPort: desiredPortTwo, env: "PORT_AND_TARGET_PORT_SET_DIFFERENTLY", isProxied: false),
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

        var executables = kubernetesService.CreatedResources.OfType<Executable>().ToList();
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

        var exes = kubernetesService.CreatedResources.OfType<Executable>().ToList();
        Assert.Equal(3, exes.Count);

        foreach (var dcpExe in exes)
        {
            Assert.True(dcpExe.TryGetAnnotationAsObjectList<ServiceProducerAnnotation>(CustomResource.ServiceProducerAnnotation, out var spAnnList));

            // Neither Port, nor TargetPort are set
            // Clients use proxy, MAY have the proxy port injected.
            // Proxy gets autogenerated port.
            // Each replica gets a different autogenerated port that MUST be injected via env var/startup param.
            var svc = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "ServiceA-NoPortNoTargetPort");
            Assert.Equal(AddressAllocationModes.Localhost, svc.Spec.AddressAllocationMode);
            Assert.True(svc.Status?.EffectivePort >= TestKubernetesService.StartOfAutoPortRange);
            Assert.True(spAnnList.Single(ann => ann.ServiceName == "ServiceA-NoPortNoTargetPort").Port is null,
                "Expected service producer (target) port to not be set (leave allocation to DCP)");
            var envVarVal = dcpExe.Spec.Env?.Single(v => v.Name == "NO_PORT_NO_TARGET_PORT").Value;
            Assert.False(string.IsNullOrWhiteSpace(envVarVal));
            Assert.Contains("""portForServing "ServiceA-NoPortNoTargetPort" """, envVarVal);

            // ASPNETCORE_URLS should not include dontinjectme, as it was excluded using WithEndpointsInEnvironment
            var aspnetCoreUrls = dcpExe.Spec.Env?.Single(v => v.Name == "ASPNETCORE_URLS").Value;
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
            .WithEndpoint(name: "PortSetNoTargetPort", port: desiredPortOne, env: "PORT_SET_NO_TARGET_PORT", isProxied: true)
            .WithReplicas(3);

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);
        await appExecutor.RunApplicationAsync();

        var exes = kubernetesService.CreatedResources.OfType<Executable>().ToList();
        Assert.Equal(3, exes.Count);

        foreach (var dcpExe in exes)
        {
            Assert.True(dcpExe.TryGetAnnotationAsObjectList<ServiceProducerAnnotation>(CustomResource.ServiceProducerAnnotation, out var spAnnList));

            // Port is set, but TargetPort is empty.
            // Clients use proxy, MAY have the proxy port injected.
            // Proxy uses Port.
            // Each replica gets a different autogenerated port that MUST be injected via env var/startup param.
            var svc = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "ServiceA-PortSetNoTargetPort");
            Assert.Equal(AddressAllocationModes.Localhost, svc.Spec.AddressAllocationMode);
            Assert.Equal(desiredPortOne, svc.Status?.EffectivePort);
            Assert.True(spAnnList.Single(ann => ann.ServiceName == "ServiceA-PortSetNoTargetPort").Port is null,
                "Expected service producer (target) port to not be set (leave allocation to DCP)");
            var envVarVal = dcpExe.Spec.Env?.Single(v => v.Name == "PORT_SET_NO_TARGET_PORT").Value;
            Assert.False(string.IsNullOrWhiteSpace(envVarVal));
            Assert.Contains("""portForServing "ServiceA-PortSetNoTargetPort" """, envVarVal);
        }
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

        // All these configurations are effectively the same because EndpointAnnotation constructor for proxy-less endpoints
        // will make sure Port and TargetPort have the same value if one is specified but the other is not.

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

        // All these configurations are effectively the same because EndpointAnnotation constructor for proxy-less endpoints
        // will make sure Port and TargetPort have the same value if one is specified but the other is not.

        var kubernetesService = new TestKubernetesService();
        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var appExecutor = CreateAppExecutor(distributedAppModel, kubernetesService: kubernetesService);
        await appExecutor.RunApplicationAsync();

        var dcpCtr = Assert.Single(kubernetesService.CreatedResources.OfType<Container>());
        Assert.True(dcpCtr.TryGetAnnotationAsObjectList<ServiceProducerAnnotation>(CustomResource.ServiceProducerAnnotation, out var spAnnList));

        // Port is empty, TargetPort is set
        // Clients connect directly to the container host port, MAY have the container host port injected.
        // Container is using TargetPort for BOTH listening inside the container and as a host port.
        var svc = kubernetesService.CreatedResources.OfType<Service>().Single(s => s.Name() == "database");
        Assert.Equal(AddressAllocationModes.Proxyless, svc.Spec.AddressAllocationMode);
        Assert.Equal(desiredTargetPort, svc.Status?.EffectivePort);
        Assert.NotNull(dcpCtr.Spec.Ports);
        Assert.Contains(dcpCtr.Spec.Ports!, p => p.HostPort == desiredTargetPort && p.ContainerPort == desiredTargetPort);
        // Desired port should be part of the service producer annotation.
        Assert.Equal(desiredTargetPort, spAnnList.Single(ann => ann.ServiceName == "database").Port);
        var envVarVal = dcpCtr.Spec.Env?.Single(v => v.Name == "NO_PORT_TARGET_PORT_SET").Value;
        Assert.False(string.IsNullOrWhiteSpace(envVarVal));
        Assert.Equal(desiredTargetPort, int.Parse(envVarVal, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task EndpointPortsContainerProxylessPortAndTargetPortSet()
    {
        var builder = DistributedApplication.CreateBuilder();

        const int desiredPort = TestKubernetesService.StartOfAutoPortRange - 998;
        const int desiredTargetPort = TestKubernetesService.StartOfAutoPortRange - 997;
        builder.AddContainer("database", "image")
            .WithEndpoint(name: "PortAndTargetPortSet", port: desiredPort, targetPort: desiredTargetPort, env: "PORT_AND_TARGET_PORT_SET", isProxied: false);

        // All these configurations are effectively the same because EndpointAnnotation constructor for proxy-less endpoints
        // will make sure Port and TargetPort have the same value if one is specified but the other is not.

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
    public async Task EndpointPortsContainerProxylessProtocolSet()
    {
        var builder = DistributedApplication.CreateBuilder();

        const int desiredPort = TestKubernetesService.StartOfAutoPortRange - 998;
        const int desiredTargetPort = TestKubernetesService.StartOfAutoPortRange - 997;
        builder.AddContainer("database", "image")
            .WithEndpoint(name: "PortAndProtocolSet", port: desiredPort, targetPort: desiredTargetPort, env: "PORT_AND_PROTOCOL_SET", isProxied: false, protocol: System.Net.Sockets.ProtocolType.Udp);

        // All these configurations are effectively the same because EndpointAnnotation constructor for proxy-less endpoints
        // will make sure Port and TargetPort have the same value if one is specified but the other is not.

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
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dcpEvents.Subscribe<OnResourceFailedToStartContext>(c =>
        {
            tcs.SetResult();
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

        // Verify failed to start event.
        await tcs.Task.DefaultTimeout();
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
        var exe = Assert.Single(kubernetes.CreatedResources.OfType<Executable>());
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

        var exe = Assert.Single(kubernetes.CreatedResources.OfType<Executable>());
        Assert.True(exe.TryGetProjectLaunchConfiguration(out var plc));
        Assert.NotNull(plc);
        Assert.Equal(expectedMode, plc!.Mode);
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
        var exe = Assert.Single(kubernetes.CreatedResources.OfType<Executable>());
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
        var exe = Assert.Single(kubernetes.CreatedResources.OfType<Executable>());
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

        var exe = Assert.Single(kubernetes.CreatedResources.OfType<Executable>());
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

        var exe = Assert.Single(kubernetes.CreatedResources.OfType<Executable>());
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

        var exe = Assert.Single(kubernetes.CreatedResources.OfType<Executable>());
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

        var exe = Assert.Single(kubernetes.CreatedResources.OfType<Executable>());
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

        var exe = Assert.Single(kubernetes.CreatedResources.OfType<Executable>());
        Assert.True(exe.TryGetProjectLaunchConfiguration(out var plc));
        Assert.False(plc!.DisableLaunchProfile);
        Assert.Equal("Zed", plc.LaunchProfile); // first inserted wins
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
        Assert.True(debuggableExe.TryGetAnnotationAsObjectList<ExecutableLaunchConfiguration>(Executable.LaunchConfigurationsAnnotation, out var launchConfigs1));
        var config1 = Assert.Single(launchConfigs1);
        Assert.Equal(ExecutableLaunchMode.Debug, config1.Mode);
        Assert.Equal("test", config1.Type);

        var nonDebuggableExe = Assert.Single(dcpExes, e => e.AppModelResourceName == "TestOtherExecutable");
        Assert.Equal(ExecutionType.Process, nonDebuggableExe.Spec.ExecutionType);
        Assert.False(nonDebuggableExe.TryGetAnnotationAsObjectList<ProjectLaunchConfiguration>(Executable.LaunchConfigurationsAnnotation, out _));
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

        // Assert
        var dcpExes = kubernetesService.CreatedResources.OfType<Executable>().ToList();
        Assert.Single(dcpExes);

        var exe = Assert.Single(dcpExes, e => e.AppModelResourceName == "ServiceA");
        Assert.Equal(ExecutionType.IDE, exe.Spec.ExecutionType);
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

        // Assert
        var dcpExes = kubernetesService.CreatedResources.OfType<Executable>().ToList();
        Assert.Single(dcpExes);

        var exe = Assert.Single(dcpExes, e => e.AppModelResourceName == "ServiceA");
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

        // Assert
        var dcpExes = kubernetesService.CreatedResources.OfType<Executable>().ToList();
        Assert.Single(dcpExes);

        var exe = Assert.Single(dcpExes, e => e.AppModelResourceName == "ServiceA");
        Assert.Equal(ExecutionType.IDE, exe.Spec.ExecutionType);
    }

    [Fact]
    public async Task ProjectExecutable_DebugSessionInfoWithoutProjectStillDefaultsToProjectSupport()
    {
        // Bug #15606/#15647: VS Code extension sends SupportedLaunchConfigurations=["azure-functions"]
        // (not including "project"). Standard project resources should still get IDE execution because
        // "project" launch support is implicit in DCP.
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

        var exe = Assert.Single(kubernetesService.CreatedResources.OfType<Executable>(), e => e.AppModelResourceName == "ServiceA");
        Assert.Equal(ExecutionType.IDE, exe.Spec.ExecutionType);

        Assert.True(exe.TryGetAnnotationAsObjectList<ProjectLaunchConfiguration>(Executable.LaunchConfigurationsAnnotation, out var launchConfigs));
        Assert.Single(launchConfigs);
        Assert.Equal("project", launchConfigs[0].Type);
    }

    [Fact]
    public async Task ProjectWithNonProjectAnnotation_DebugSessionWithoutInfo_FallsBackToProjectIdeExecution()
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

        var exe = Assert.Single(kubernetesService.CreatedResources.OfType<Executable>(), e => e.AppModelResourceName == "proj");
        Assert.Equal(ExecutionType.IDE, exe.Spec.ExecutionType);
        Assert.NotNull(exe.Spec.FallbackExecutionTypes);
        Assert.Equal(ExecutionType.Process, Assert.Single(exe.Spec.FallbackExecutionTypes));

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

        var exe = Assert.Single(kubernetesService.CreatedResources.OfType<Executable>(), e => e.AppModelResourceName == "proj");
        Assert.Equal(ExecutionType.Process, exe.Spec.ExecutionType);
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

        var exe = Assert.Single(kubernetesService.CreatedResources.OfType<Executable>(), e => e.AppModelResourceName == "proj");
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

        var exe = Assert.Single(kubernetesService.CreatedResources.OfType<Executable>(), e => e.AppModelResourceName == "proj");
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

        var dcpExes = kubernetesService.CreatedResources.OfType<Executable>().ToList();
        Assert.Equal(2, dcpExes.Count);

        var standardExe = Assert.Single(dcpExes, e => e.AppModelResourceName == "standard-project");
        Assert.Equal(ExecutionType.IDE, standardExe.Spec.ExecutionType);

        var customExe = Assert.Single(dcpExes, e => e.AppModelResourceName == "custom-project");
        Assert.Equal(ExecutionType.IDE, customExe.Spec.ExecutionType);
        Assert.NotNull(customExe.Spec.FallbackExecutionTypes);
        Assert.Equal(ExecutionType.Process, Assert.Single(customExe.Spec.FallbackExecutionTypes));

        Assert.True(customExe.TryGetAnnotationAsObjectList<ProjectLaunchConfiguration>(Executable.LaunchConfigurationsAnnotation, out var launchConfigs));
        Assert.Single(launchConfigs);
        Assert.Equal("project", launchConfigs[0].Type);
    }

    [Fact]
    public async Task StandardAndCustomProjects_VSCodeScenario_BothRunInIde()
    {
        // Combined VS Code scenario for bugs #15606/#15647 and class library projects:
        // VS Code extension sends SupportedLaunchConfigurations=["azure-functions"] (not "project").
        // A standard project (type "project") should still get IDE (implicit support).
        // A project with "azure-functions" annotation should also get IDE (explicit match).
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

        var dcpExes = kubernetesService.CreatedResources.OfType<Executable>().ToList();
        Assert.Equal(2, dcpExes.Count);

        // Standard project: IDE via implicit "project" support (bug #15606/#15647 fix)
        var standardExe = Assert.Single(dcpExes, e => e.AppModelResourceName == "standard-project");
        Assert.Equal(ExecutionType.IDE, standardExe.Spec.ExecutionType);
        Assert.True(standardExe.TryGetAnnotationAsObjectList<ProjectLaunchConfiguration>(Executable.LaunchConfigurationsAnnotation, out var standardConfigs));
        Assert.Single(standardConfigs);
        Assert.Equal("project", standardConfigs[0].Type);

        // Azure Functions project: IDE via explicit "azure-functions" support
        var functionsExe = Assert.Single(dcpExes, e => e.AppModelResourceName == "functions-project");
        Assert.Equal(ExecutionType.IDE, functionsExe.Spec.ExecutionType);
    }

    [Fact]
    public async Task ProjectWithNonProjectAnnotation_VSFallback_HasProcessFallbackExecutionType()
    {
        // Verifies the VS else-if branch sets FallbackExecutionTypes to [Process],
        // ensuring DCP can fall back gracefully if IDE launch fails.
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

        var exe = Assert.Single(kubernetesService.CreatedResources.OfType<Executable>(), e => e.AppModelResourceName == "proj");
        Assert.Equal(ExecutionType.IDE, exe.Spec.ExecutionType);
        Assert.NotNull(exe.Spec.FallbackExecutionTypes);
        Assert.Single(exe.Spec.FallbackExecutionTypes);
        Assert.Equal(ExecutionType.Process, exe.Spec.FallbackExecutionTypes[0]);
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

        var exe = Assert.Single(kubernetesService.CreatedResources.OfType<Executable>(), e => e.AppModelResourceName == "ServiceA");
        Assert.Equal(ExecutionType.IDE, exe.Spec.ExecutionType);
        Assert.NotNull(exe.Spec.FallbackExecutionTypes);
        Assert.Equal(ExecutionType.Process, Assert.Single(exe.Spec.FallbackExecutionTypes));

        Assert.True(exe.TryGetAnnotationAsObjectList<ProjectLaunchConfiguration>(Executable.LaunchConfigurationsAnnotation, out var launchConfigs));
        Assert.Single(launchConfigs);
        Assert.Equal("project", launchConfigs[0].Type);
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

        var exe = Assert.Single(kubernetesService.CreatedResources.OfType<Executable>(), e => e.AppModelResourceName == "ServiceA");
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

        var exe = Assert.Single(kubernetesService.CreatedResources.OfType<Executable>(), e => e.AppModelResourceName == "TestFunction");
        Assert.Equal(ExecutionType.IDE, exe.Spec.ExecutionType);
        Assert.NotNull(exe.Spec.FallbackExecutionTypes);
        Assert.Equal(ExecutionType.Process, Assert.Single(exe.Spec.FallbackExecutionTypes));

        Assert.True(exe.TryGetAnnotationAsObjectList<ProjectLaunchConfiguration>(Executable.LaunchConfigurationsAnnotation, out var launchConfigs));
        Assert.Single(launchConfigs);
        Assert.Equal("Aspire_TestFunction", launchConfigs[0].LaunchProfile);

        // Project args should be empty — the Executable profile's command line args are NOT
        // injected into project args (that was the old Process fallback behavior).
        Assert.True(exe.TryGetAnnotationAsObjectList<string>(CustomResource.ResourceProjectArgsAnnotation, out var projectArgs));
        Assert.Empty(projectArgs);
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

        var exe = Assert.Single(kubernetesService.CreatedResources.OfType<Executable>(), e => e.AppModelResourceName == "proj");
        // Should be IDE, because it's a normal Project profile
        Assert.Equal(ExecutionType.IDE, exe.Spec.ExecutionType);
        Assert.NotNull(exe.Spec.FallbackExecutionTypes);
        Assert.Equal(ExecutionType.Process, Assert.Single(exe.Spec.FallbackExecutionTypes));
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

    [Fact]
    public async Task PlainExecutable_LaunchConfigurationProducerThrows_FallsBackToProcess()
    {
        var builder = DistributedApplication.CreateBuilder();

        var debuggableExecutable = new TestExecutableResource("test-working-directory");
        builder.AddResource(debuggableExecutable).WithDebugSupport<TestExecutableResource, ExecutableLaunchConfiguration>(_ => throw new InvalidOperationException("Test exception from launch configuration producer"), "test");

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

        await appExecutor.RunApplicationAsync();

        List<Executable> dcpExes = [];
        var haveExes = RetryTillTrueOrTimeout(() =>
        {
            dcpExes.Clear();
            dcpExes.AddRange(kubernetesService.CreatedResources.OfType<Executable>());
            return dcpExes.Count == 1;
        }, TestConstants.DefaultOrchestratorTestTimeout);
        Assert.True(haveExes, $"Expected one executable but instead got {dcpExes.Count}");

        var exe = Assert.Single(dcpExes, e => e.AppModelResourceName == "TestExecutable");
        // Should fall back to Process execution when the launch configuration producer throws
        Assert.Equal(ExecutionType.Process, exe.Spec.ExecutionType);
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
        List<Executable> dcpExes = [];
        var haveExes = RetryTillTrueOrTimeout(() =>
        {
            dcpExes.Clear();
            dcpExes.AddRange(kubernetesService.CreatedResources.OfType<Executable>());
            return dcpExes.Count == 1;
        }, TestConstants.DefaultOrchestratorTestTimeout);
        Assert.True(haveExes, $"Expected one executable but instead got {dcpExes.Count}");

        var exe = Assert.Single(dcpExes, e => e.AppModelResourceName == "proj");
        Assert.Equal(ExecutionType.IDE, exe.Spec.ExecutionType);

        // The launch config should have been applied in CreateExecutableAsync (not PrepareProjectExecutables)
        Assert.True(exe.TryGetAnnotationAsObjectList<ExecutableLaunchConfiguration>(Executable.LaunchConfigurationsAnnotation, out var launchConfigs));
        var config = Assert.Single(launchConfigs);
        Assert.Equal("azure-functions", config.Type);
        Assert.Equal(ExecutableLaunchMode.Debug, config.Mode);
    }

    [Fact]
    public async Task Project_NonProjectLaunchConfig_AnnotatorThrows_FallsBackToProcess()
    {
        // Arrange: A ProjectResource with a non-"project" launch config where the annotator throws
        // should fall back to ExecutionType.Process.
        var builder = DistributedApplication.CreateBuilder();

        var projectBuilder = builder.AddProject<TestProject>("proj", launchProfileName: null);
        // Remove the default "project" SupportsDebuggingAnnotation and replace with a throwing one
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
            return dcpExes.Count == 1;
        }, TestConstants.DefaultOrchestratorTestTimeout);
        Assert.True(haveExes, $"Expected one executable but instead got {dcpExes.Count}");

        var exe = Assert.Single(dcpExes, e => e.AppModelResourceName == "proj");
        // Should fall back to Process execution when the launch configuration producer throws
        Assert.Equal(ExecutionType.Process, exe.Spec.ExecutionType);
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
        List<Executable> dcpExes = [];
        var haveExes = RetryTillTrueOrTimeout(() =>
        {
            dcpExes.Clear();
            dcpExes.AddRange(kubernetesService.CreatedResources.OfType<Executable>());
            return dcpExes.Count == 1;
        }, TestConstants.DefaultOrchestratorTestTimeout);
        Assert.True(haveExes, $"Expected one executable but instead got {dcpExes.Count}");

        var exe = Assert.Single(dcpExes, e => e.AppModelResourceName == "proj");
        Assert.Equal(ExecutionType.Process, exe.Spec.ExecutionType);
    }

    private static DcpExecutor CreateAppExecutor(
        DistributedApplicationModel distributedAppModel,
        IHostEnvironment? hostEnvironment = null,
        IConfiguration? configuration = null,
        IKubernetesService? kubernetesService = null,
        DcpOptions? dcpOptions = null,
        ResourceLoggerService? resourceLoggerService = null,
        DcpExecutorEvents? events = null)
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
                ServiceProvider = new TestServiceProvider(configuration)
                    .AddService<IDeveloperCertificateService>(developerCertificateService)
                    .AddService(distributedAppModel)
                    .AddService(Options.Create(dcpOptions))
                    .AddService(resourceLoggerService)
            });
        var ks = kubernetesService ?? new TestKubernetesService();
        var dcpEvts = events ?? new DcpExecutorEvents();
        var locations = new Locations(new FileSystemService(configuration));
        var hostEnv = hostEnvironment ?? new TestHostEnvironment();
        var dcpDependencyCheckService = new TestDcpDependencyCheckService();

        var appResources = new DcpAppResourceStore();

        var executableCreator = new ExecutableCreator(
            configuration,
            nameGenerator,
            distributedAppModel,
            new DistributedApplicationOptions(),
            executionContext,
            locations,
            NullLogger<ExecutableCreator>.Instance,
            appResources);

        var containerCreator = new ContainerCreator(
            configuration,
            Options.Create(dcpOptions),
            nameGenerator,
            distributedAppModel,
            executionContext,
            resourceLoggerService,
            dcpDependencyCheckService,
            hostEnv,
            NullLogger<ContainerCreator>.Instance,
            appResources);

        return new DcpExecutor(
            NullLogger<DcpExecutor>.Instance,
            NullLogger<DistributedApplication>.Instance,
            distributedAppModel,
            ks,
            configuration,
            new Hosting.Eventing.DistributedApplicationEventing(),
            Options.Create(dcpOptions),
            executionContext,
            resourceLoggerService,
            dcpDependencyCheckService,
            nameGenerator,
            dcpEvts,
            appResources,
            executableCreator,
            containerCreator);
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

    private sealed class TestExecutableResource(string directory) : ExecutableResource("TestExecutable", "test", directory);
    private sealed class TestOtherExecutableResource(string directory) : ExecutableResource("TestOtherExecutable", "test-other", directory);

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
