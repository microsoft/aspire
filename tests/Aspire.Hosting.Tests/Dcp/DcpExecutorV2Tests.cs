// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPERSISTENCE001
#pragma warning disable ASPIRECERTIFICATES001
#pragma warning disable ASPIREEXTENSION001
#pragma warning disable CS0618 // Verify legacy ProjectResource plain-process compatibility.

using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Text;
using System.IO.Pipelines;
using System.Threading.Channels;
using Aspire.Hosting.Orchestrator;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Dcp.Model;
using Aspire.Hosting.Tests.Utils;
using Aspire.TestUtilities;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using V2 = Aspire.Hosting.Dcp.Model.V2;

namespace Aspire.Hosting.Tests.Dcp;

[Trait("Partition", "4")]
public class DcpExecutorV2Tests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectionUsesOnePipelineAndDefaultsToV1(bool v2)
    {
        var builder = CreateBuilder(new(), v2);
        if (v2)
        {
            builder.Services.AddSingleton<DcpExecutor>(_ => throw new InvalidOperationException("V1 must not be constructed."));
        }
        else
        {
            builder.Services.AddSingleton<DcpExecutorV2>(_ => throw new InvalidOperationException("V2 must not be constructed."));
        }
        await using var app = builder.Build();
        var executor = app.Services.GetRequiredService<IDcpExecutor>();
        Assert.Equal(v2 ? typeof(DcpExecutorV2) : typeof(DcpExecutor), executor.GetType());
        Assert.Equal(v2, app.Services.GetRequiredService<IOptions<DcpOptions>>().Value.UseV2);
        Assert.False(new DcpOptions().UseV2);
    }

    [Theory]
    [InlineData(typeof(V2.Namespace))]
    [InlineData(typeof(V2.LogicalService))]
    [InlineData(typeof(V2.ServiceEndpoint))]
    [InlineData(typeof(V2.LogicalProcess))]
    [InlineData(typeof(V2.LogicalContainer))]
    [InlineData(typeof(V2.LogicalContainerExec))]
    [InlineData(typeof(V2.LogicalContainerNetwork))]
    [InlineData(typeof(V2.PhysicalContainerVolume))]
    public async Task MissingV2ApiFailsBeforeCreatingWorkloads(Type missing)
    {
        var kubernetes = new TestKubernetesService { EmulateV2Controllers = true, UnsupportedResourceType = missing };
        var builder = CreateBuilder(kubernetes);
        builder.AddExecutable("app", "program", ".");
        await using var app = builder.Build();
        var executor = app.Services.GetRequiredService<DcpExecutorV2>();

        var error = await Assert.ThrowsAsync<DistributedApplicationException>(() => executor.RunApplicationAsync(CancellationToken.None)).DefaultTimeout();

        Assert.Contains(missing.Name, error.Message);
        Assert.Contains("disable DcpPublisher:UseV2", error.Message);
        Assert.Equal(missing == typeof(V2.Namespace) ? 0 : 1, kubernetes.CreatedResources.Count);
        Assert.All(kubernetes.CreatedResources, r => Assert.IsType<V2.Namespace>(r));
    }

    [Fact]
    public async Task NamespaceMustBeActiveBeforeAnyChildrenAreCreated()
    {
        var kubernetes = new TestKubernetesService { EmulateV2Controllers = true, ActivateV2Namespaces = false };
        var builder = CreateBuilder(kubernetes);
        builder.AddExecutable("app", "program", ".");
        await using var app = builder.Build();
        var executor = app.Services.GetRequiredService<DcpExecutorV2>();
        var run = executor.RunApplicationAsync(TestContext.Current.CancellationToken);
        await AsyncTestHelpers.AssertIsTrueRetryAsync(() => kubernetes.CreatedResources.OfType<V2.Namespace>().Any(), "The run creates its namespace.");
        var ns = Assert.IsType<V2.Namespace>(Assert.Single(kubernetes.CreatedResources));
        Assert.False(run.IsCompleted);

        ns.Status = new() { Phase = V2.NamespacePhase.Active };
        kubernetes.PushResourceModified(ns);
        await run.DefaultTimeout();

        var process = Assert.Single(kubernetes.CreatedResources.OfType<V2.LogicalProcess>());
        Assert.Equal(ns.Metadata.Name, process.Metadata.NamespaceProperty);
        await executor.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal([ns.Metadata.Name], kubernetes.DeletedResources);
    }

    [Fact]
    public async Task NamespaceWaitHonorsCancellationAndDisposalCleansItUp()
    {
        var kubernetes = new TestKubernetesService { EmulateV2Controllers = true, ActivateV2Namespaces = false };
        var builder = CreateBuilder(kubernetes);
        builder.AddExecutable("app", "program", ".");
        await using var app = builder.Build();
        var executor = app.Services.GetRequiredService<DcpExecutorV2>();
        using var cts = new CancellationTokenSource();
        var run = executor.RunApplicationAsync(cts.Token);
        await AsyncTestHelpers.AssertIsTrueRetryAsync(() => kubernetes.CreatedResources.Any(), "The run creates its namespace.");
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run).DefaultTimeout();
        Assert.Single(kubernetes.CreatedResources);
        await executor.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal([executor.NamespaceName], kubernetes.DeletedResources);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProcessPortsPreservePublicAndServingValues(bool proxied)
    {
        var kubernetes = new TestKubernetesService { EmulateV2Controllers = true };
        var builder = CreateBuilder(kubernetes);
        var producer = builder.AddExecutable("producer", "program", ".")
            .WithHttpEndpoint(name: "http", env: "PORT", isProxied: proxied);
        builder.AddExecutable("consumer", "program", ".").WithEnvironment("URL", producer.GetEndpoint("http"));
        var events = new DcpExecutorEvents();
        var allocated = 0;
        events.Subscribe<OnEndpointsAllocatedContext>(context =>
        {
            Assert.All(context.Events, e => Assert.True(DcpModelUtilities.AreResourceEndpointsAllocated(e.Resource)));
            Interlocked.Increment(ref allocated);
            return Task.CompletedTask;
        });
        builder.Services.AddSingleton(events);
        await using var app = builder.Build();
        await app.Services.GetRequiredService<DcpExecutorV2>().RunApplicationAsync(TestContext.Current.CancellationToken).DefaultTimeout();

        var service = Assert.Single(kubernetes.CreatedResources.OfType<V2.LogicalService>());
        var processes = kubernetes.CreatedResources.OfType<V2.LogicalProcess>().ToArray();
        var process = Assert.Single(processes, p => p.AppModelResourceName == "producer");
        var consumer = Assert.Single(processes, p => p.AppModelResourceName == "consumer");
        var port = Assert.Single(process.Spec.Ports!);
        Assert.Equal(service.Metadata.Name, port.ServiceRef);
        Assert.Equal("port-0", port.Name);
        Assert.InRange(port.Port, 10000, 32767);
        Assert.Equal(proxied ? """{{- portForServing "port-0" -}}""" : port.Port.ToString(),
            Assert.Single(process.Spec.Process!.Env!, e => e.Name == "PORT").Value);
        Assert.Equal(producer.GetEndpoint("http").Url, Assert.Single(consumer.Spec.Process!.Env!, e => e.Name == "URL").Value);
        Assert.Equal(1, allocated);
        if (proxied)
        {
            Assert.NotEqual(service.Status!.EffectiveAddresses![0].Port, port.Port);
        }
        else
        {
            Assert.Equal(service.Spec.Port, port.Port);
        }
        Assert.Null(process.Spec.Mode == V2.LogicalProcessMode.Session ? process.Spec.MonitorPid : 1);
    }

    [Fact]
    public async Task LegacyProjectsUsePlainProcessLaunchPlansEvenWithIdeConfiguration()
    {
        var kubernetes = new TestKubernetesService { EmulateV2Controllers = true };
        var builder = CreateBuilder(kubernetes);
        builder.Configuration["DEBUG_SESSION_PORT"] = "12345";
        builder.AddProject<Projects.ServiceA>("project", launchProfileName: null).WithArgs("argument");
        await using var app = builder.Build();
        await app.Services.GetRequiredService<DcpExecutorV2>().RunApplicationAsync(TestContext.Current.CancellationToken).DefaultTimeout();

        var process = Assert.Single(kubernetes.CreatedResources.OfType<V2.LogicalProcess>());
        Assert.Equal("dotnet", process.Spec.Process!.ExecutablePath);
        Assert.Contains("run", process.Spec.Process.Args!);
        Assert.Contains("argument", process.Spec.Process.Args!);
        Assert.False(process.Metadata.Annotations.ContainsKey(Executable.LaunchConfigurationsAnnotation));
        Assert.False(process.Metadata.Annotations.ContainsKey(CustomResource.ServiceProducerAnnotation));
    }

    [Fact]
    public async Task ReplicasUseIndividualLogicalProcessesAndDistinctServingPorts()
    {
        var kubernetes = new TestKubernetesService { EmulateV2Controllers = true };
        var builder = CreateBuilder(kubernetes);
        builder.AddProject<Projects.ServiceA>("project", launchProfileName: null).WithReplicas(3).WithHttpEndpoint(name: "http");
        await using var app = builder.Build();
        await app.Services.GetRequiredService<DcpExecutorV2>().RunApplicationAsync(TestContext.Current.CancellationToken).DefaultTimeout();
        var processes = kubernetes.CreatedResources.OfType<V2.LogicalProcess>().ToArray();
        Assert.Equal(3, processes.Length);
        Assert.Equal(3, processes.Select(p => p.Metadata.Name).Distinct().Count());
        Assert.Equal(3, processes.Select(p => Assert.Single(p.Spec.Ports!).Port).Distinct().Count());
        Assert.Single(processes.Select(p => Assert.Single(p.Spec.Ports!).ServiceRef).Distinct());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ContainerPortsMountsAndHostConnectivityAreTypedAndNamespaced(bool persistent)
    {
        var kubernetes = new TestKubernetesService { EmulateV2Controllers = true };
        var builder = CreateBuilder(kubernetes);
        var host = builder.AddExecutable("host", "program", ".").WithHttpEndpoint(name: "http");
        var container = builder.AddContainer("container", "image")
            .WithHttpEndpoint(targetPort: 80, name: "http", isProxied: !persistent)
            .WithVolume("shared-data", "/data").WithBindMount(".", "/host", isReadOnly: true)
            .WithContainerRuntimeArgs("--label", "test=value")
            .WithEnvironment("HOST_URL", host.GetEndpoint("http"));
        if (persistent)
        {
            container.WithLifetime(ContainerLifetime.Persistent);
        }
        await using var app = builder.Build();
        var executor = app.Services.GetRequiredService<DcpExecutorV2>();
        await executor.RunApplicationAsync(TestContext.Current.CancellationToken).DefaultTimeout();

        var rendered = Assert.Single(kubernetes.CreatedResources.OfType<V2.LogicalContainer>());
        var spec = rendered.Spec.Container!;
        Assert.Equal("image:latest", spec.Image!.Image);
        Assert.Equal(persistent ? V2.LogicalContainerMode.Persistent : V2.LogicalContainerMode.Session, rendered.Spec.Mode);
        Assert.Equal(rendered.Metadata.Name, rendered.Spec.ContainerName);
        Assert.Equal(80, Assert.Single(rendered.Spec.Ports!).ContainerPort);
        var volume = Assert.Single(kubernetes.CreatedResources.OfType<V2.PhysicalContainerVolume>());
        Assert.Equal("shared-data", volume.Spec.VolumeName);
        Assert.False(volume.Spec.RemoveRuntimeVolumeOnDelete);
        Assert.Equal(volume.Metadata.Name, Assert.Single(spec.VolumeMounts!, m => m.Target == "/data").VolumeRef);
        Assert.True(Assert.Single(spec.VolumeMounts!, m => m.Target == "/host").ReadOnly);
        Assert.Equal("test", Assert.Single(spec.Labels!).Key);
        var network = Assert.Single(kubernetes.CreatedResources.OfType<V2.LogicalContainerNetwork>());
        Assert.Equal(network.Metadata.Name, Assert.Single(spec.Networks!).NetworkRef);
        Assert.Equal(persistent ? V2.LogicalContainerNetworkMode.Persistent : V2.LogicalContainerNetworkMode.Session, network.Spec.Mode);
        var hostService = Assert.Single(kubernetes.CreatedResources.OfType<V2.LogicalService>(),
            s => s.Spec.AddressAllocationMode == V2.ServiceAddressAllocationMode.AllInterfaces);
        Assert.Equal($"http://host.docker.internal:{hostService.Status!.EffectiveAddresses![0].Port}",
            Assert.Single(spec.Env!, e => e.Name == "HOST_URL").Value);
        Assert.All(kubernetes.CreatedResources.Where(r => r is not V2.Namespace), r => Assert.Equal(executor.NamespaceName, r.Metadata.NamespaceProperty));
    }

    [Fact]
    public async Task PersistentContainersReuseStableRuntimeIdentityAcrossRunNamespaces()
    {
        var first = new TestKubernetesService { EmulateV2Controllers = true };
        var second = new TestKubernetesService { EmulateV2Controllers = true };
        var firstBuilder = CreateBuilder(first);
        var secondBuilder = CreateBuilder(second);
        firstBuilder.AddContainer("data", "image").WithLifetime(ContainerLifetime.Persistent).WithVolume("shared", "/data");
        secondBuilder.AddContainer("data", "image").WithLifetime(ContainerLifetime.Persistent).WithVolume("shared", "/data");
        await using var firstApp = firstBuilder.Build();
        await using var secondApp = secondBuilder.Build();
        await firstApp.Services.GetRequiredService<DcpExecutorV2>().RunApplicationAsync(TestContext.Current.CancellationToken).DefaultTimeout();
        await secondApp.Services.GetRequiredService<DcpExecutorV2>().RunApplicationAsync(TestContext.Current.CancellationToken).DefaultTimeout();
        var one = Assert.Single(first.CreatedResources.OfType<V2.LogicalContainer>());
        var two = Assert.Single(second.CreatedResources.OfType<V2.LogicalContainer>());
        Assert.NotEqual(one.Metadata.NamespaceProperty, two.Metadata.NamespaceProperty);
        Assert.Equal(one.Spec.ContainerName, two.Spec.ContainerName);
        Assert.Null(one.Spec.LifecycleKey);
        Assert.Null(two.Spec.LifecycleKey);
        Assert.Equal(Assert.Single(one.Spec.Container!.VolumeMounts!).VolumeRef, Assert.Single(two.Spec.Container!.VolumeMounts!).VolumeRef);
    }

    [Fact]
    public async Task ExplicitStartDefersCallbacksAndRestartReplacesIdentity()
    {
        var kubernetes = new TestKubernetesService { EmulateV2Controllers = true };
        var builder = CreateBuilder(kubernetes);
        var calls = 0;
        var model = builder.AddExecutable("app", "program", ".").WithExplicitStart().WithEnvironment(context =>
        {
            context.EnvironmentVariables["ATTEMPT"] = Interlocked.Increment(ref calls).ToString();
        });
        var states = new ConcurrentQueue<(string? State, int? ExitCode)>();
        var events = new DcpExecutorEvents();
        events.Subscribe<OnResourceChangedContext>(context =>
        {
            states.Enqueue((context.Status.State, context.UpdateSnapshot(new() { ResourceType = "Executable", Properties = [] }).ExitCode));
            return Task.CompletedTask;
        });
        builder.Services.AddSingleton(events);
        await using var app = builder.Build();
        var executor = app.Services.GetRequiredService<DcpExecutorV2>();
        await executor.RunApplicationAsync(TestContext.Current.CancellationToken).DefaultTimeout();
        Assert.Equal(0, calls);
        Assert.Empty(kubernetes.CreatedResources.OfType<V2.LogicalProcess>());
        var reference = executor.GetResource(model.Resource.GetResolvedResourceName());
        await executor.StartResourceAsync(reference, TestContext.Current.CancellationToken).DefaultTimeout();
        var previous = Assert.Single(kubernetes.CreatedResources.OfType<V2.LogicalProcess>());
        await executor.StopResourceAsync(reference, TestContext.Current.CancellationToken).DefaultTimeout();
        await executor.StartResourceAsync(reference, TestContext.Current.CancellationToken).DefaultTimeout();
        var replacement = kubernetes.CreatedResources.OfType<V2.LogicalProcess>().Last();
        Assert.NotEqual(previous.Metadata.Uid, replacement.Metadata.Uid);
        Assert.False(replacement.Spec.Stop);
        Assert.Equal(2, calls);
        Assert.Equal("2", Assert.Single(replacement.Spec.Process!.Env!, e => e.Name == "ATTEMPT").Value);
        previous.Status = new() { Phase = V2.LogicalProcessPhase.Exited, ExitCode = 7 };
        replacement.Status = new() { Phase = V2.LogicalProcessPhase.Exited, ExitCode = 2 };
        kubernetes.PushResourceModified(previous);
        kubernetes.PushResourceModified(replacement);
        await AsyncTestHelpers.AssertIsTrueRetryAsync(() => states.Any(s => s.ExitCode == 2), "The replacement can report its final exit code.");
        Assert.All(states, s => Assert.NotEqual(7, s.ExitCode));
    }

    [Fact]
    public async Task WatcherIgnoresOtherNamespacesAndProjectsUnknownExitCodes()
    {
        var kubernetes = new TestKubernetesService { EmulateV2Controllers = true };
        var builder = CreateBuilder(kubernetes);
        builder.AddExecutable("app", "program", ".");
        var snapshots = new ConcurrentQueue<CustomResourceSnapshot>();
        var events = new DcpExecutorEvents();
        events.Subscribe<OnResourceChangedContext>(context =>
        {
            snapshots.Enqueue(context.UpdateSnapshot(new() { ResourceType = "Executable", Properties = [] }));
            return Task.CompletedTask;
        });
        builder.Services.AddSingleton(events);
        await using var app = builder.Build();
        await app.Services.GetRequiredService<DcpExecutorV2>().RunApplicationAsync(TestContext.Current.CancellationToken).DefaultTimeout();
        var process = Assert.Single(kubernetes.CreatedResources.OfType<V2.LogicalProcess>());
        var other = V2.LogicalProcess.Create(process.Metadata.Name, "other");
        other.Metadata.Uid = "foreign";
        other.Status = new() { Phase = V2.LogicalProcessPhase.Exited, ExitCode = 7 };
        kubernetes.PushResourceModified(other);
        process.Status = new() { Phase = V2.LogicalProcessPhase.Exited };
        kubernetes.PushResourceModified(process);
        await AsyncTestHelpers.AssertIsTrueRetryAsync(() => snapshots.Any(s => s.State?.Text == KnownResourceStates.Finished), "The process exits with an unknown exit code.");
        Assert.Null(snapshots.Last().ExitCode);
        Assert.All(snapshots, s => Assert.NotEqual(7, s.ExitCode));
    }

    [Fact]
    public async Task ConfigurationFailureIsIsolatedFromUnrelatedWorkloads()
    {
        var kubernetes = new TestKubernetesService { EmulateV2Controllers = true };
        var builder = CreateBuilder(kubernetes);
        builder.AddExecutable("bad", "program", ".").WithEnvironment(_ => throw new InvalidOperationException("configuration failed"));
        builder.AddExecutable("good", "program", ".");
        var failures = new ConcurrentQueue<OnResourceFailedToStartContext>();
        var events = new DcpExecutorEvents();
        events.Subscribe<OnResourceFailedToStartContext>(context =>
        {
            failures.Enqueue(context);
            return Task.CompletedTask;
        });
        builder.Services.AddSingleton(events);
        await using var app = builder.Build();
        await app.Services.GetRequiredService<DcpExecutorV2>().RunApplicationAsync(TestContext.Current.CancellationToken).DefaultTimeout();
        Assert.Equal("good", Assert.Single(kubernetes.CreatedResources.OfType<V2.LogicalProcess>()).AppModelResourceName);
        Assert.Equal("bad", Assert.Single(failures).Resource.Name);
    }

    [Theory]
    [InlineData("localhost", V2.ServiceAddressAllocationMode.Localhost)]
    [InlineData("0.0.0.0", V2.ServiceAddressAllocationMode.IPv4AllInterfaces)]
    [InlineData("::", V2.ServiceAddressAllocationMode.IPv6AllInterfaces)]
    [InlineData("*", V2.ServiceAddressAllocationMode.AllInterfaces)]
    public void AllocationModesPreserveEndpointBinding(string host, string mode)
    {
        Assert.Equal(mode, DcpExecutorV2.GetAllocationMode(host));
    }

    [Fact]
    public void EffectiveAddressSelectionHandlesBothFamiliesAndUnknownPorts()
    {
        Assert.Equal("127.0.0.1", DcpExecutorV2.SelectAddress(
            [new() { Address = "[::1]", Port = 5000 }, new() { Address = "127.0.0.1", Port = 5000 }])!.Address);
        Assert.Equal("[::1]", DcpExecutorV2.SelectAddress([new() { Address = "[::1]", Port = 5000 }])!.Address);
        Assert.Null(DcpExecutorV2.SelectAddress([new() { Address = "127.0.0.1", Port = 0 }]));
    }

    [Fact]
    public async Task PersistentProcessesKeepNamespaceNameLifetimeAndParentIdentityAcrossRuns()
    {
        var first = new TestKubernetesService { EmulateV2Controllers = true };
        var second = new TestKubernetesService { EmulateV2Controllers = true };
        var firstBuilder = CreateBuilder(first);
        var secondBuilder = CreateBuilder(second);
        firstBuilder.AddExecutable("persistent", "program", ".").WithParentProcessLifetime(Environment.ProcessId);
        secondBuilder.AddExecutable("persistent", "program", ".").WithParentProcessLifetime(Environment.ProcessId);
        await using var firstApp = firstBuilder.Build();
        await using var secondApp = secondBuilder.Build();
        var firstExecutor = firstApp.Services.GetRequiredService<DcpExecutorV2>();
        var secondExecutor = secondApp.Services.GetRequiredService<DcpExecutorV2>();
        await firstExecutor.RunApplicationAsync(TestContext.Current.CancellationToken).DefaultTimeout();
        await secondExecutor.RunApplicationAsync(TestContext.Current.CancellationToken).DefaultTimeout();
        var one = Assert.Single(first.CreatedResources.OfType<V2.LogicalProcess>());
        var two = Assert.Single(second.CreatedResources.OfType<V2.LogicalProcess>());
        Assert.Equal(one.Metadata.NamespaceProperty, two.Metadata.NamespaceProperty);
        Assert.Equal(one.Metadata.Name, two.Metadata.Name);
        Assert.Equal(V2.LogicalProcessMode.Persistent, one.Spec.Mode);
        Assert.Equal(Environment.ProcessId, one.Spec.MonitorPid);
        Assert.Equal(one.Spec.MonitorTimestamp, two.Spec.MonitorTimestamp);
        Assert.NotNull(one.Spec.MonitorTimestamp);
        Assert.Null(one.Spec.LifecycleKey);
        Assert.Null(two.Spec.LifecycleKey);
        await firstExecutor.StopAsync(TestContext.Current.CancellationToken);
        Assert.Single(await second.ListAsync<V2.LogicalProcess>(secondExecutor.NamespaceName));
        Assert.Single(await second.ListAsync<V2.Namespace>());
        Assert.Equal([firstExecutor.NamespaceName], first.DeletedResources);
    }

    [Fact]
    public void PersistentNamespaceSeparatesAppHostsAndResourceSuffixes()
    {
        var resource = new ExecutableResource("app", "program", ".");
        resource.Annotations.Add(new PersistenceAnnotation { Mode = PersistenceMode.Persistent });
        var model = new DistributedApplicationModel([resource]);
        var first = new Microsoft.Extensions.Configuration.ConfigurationManager();
        first["AppHost:PathSha256"] = "first";
        var second = new Microsoft.Extensions.Configuration.ConfigurationManager();
        second["AppHost:PathSha256"] = "second";
        Assert.Equal(DcpExecutorV2.GetNamespaceName(model, first, new()), DcpExecutorV2.GetNamespaceName(model, first, new()));
        Assert.NotEqual(DcpExecutorV2.GetNamespaceName(model, first, new()), DcpExecutorV2.GetNamespaceName(model, second, new()));
        Assert.NotEqual(DcpExecutorV2.GetNamespaceName(model, first, new()), DcpExecutorV2.GetNamespaceName(model, first, new() { ResourceNameSuffix = "isolated" }));
    }

    [Fact]
    public async Task ConflictingNamespaceIsNotAdoptedDeletedOrStopped()
    {
        var kubernetes = new TestKubernetesService { EmulateV2Controllers = true };
        var firstBuilder = CreateBuilder(kubernetes);
        var secondBuilder = CreateBuilder(kubernetes);
        firstBuilder.AddExecutable("app", "program", ".").WithPersistentLifetime();
        secondBuilder.AddExecutable("app", "program", ".").WithPersistentLifetime();
        await using var first = firstBuilder.Build();
        await using var second = secondBuilder.Build();
        var firstExecutor = first.Services.GetRequiredService<DcpExecutorV2>();
        var secondExecutor = second.Services.GetRequiredService<DcpExecutorV2>();
        await firstExecutor.RunApplicationAsync(TestContext.Current.CancellationToken).DefaultTimeout();
        var error = await Assert.ThrowsAsync<DistributedApplicationException>(() =>
            secondExecutor.RunApplicationAsync(TestContext.Current.CancellationToken)).DefaultTimeout();
        Assert.Contains("will not adopt or clean up another run", error.Message);
        await secondExecutor.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, kubernetes.StopServerCalls);
        Assert.Empty(kubernetes.DeletedResources);
        Assert.Single(await kubernetes.ListAsync<V2.LogicalProcess>(firstExecutor.NamespaceName));
    }

    [Fact]
    public async Task CleanupRefusesToDeleteReplacedNamespace()
    {
        var kubernetes = new TestKubernetesService { EmulateV2Controllers = true };
        var builder = CreateBuilder(kubernetes);
        builder.AddExecutable("app", "program", ".");
        await using var app = builder.Build();
        var executor = app.Services.GetRequiredService<DcpExecutorV2>();
        await executor.RunApplicationAsync(TestContext.Current.CancellationToken).DefaultTimeout();
        var original = Assert.Single(kubernetes.CreatedResources.OfType<V2.Namespace>());
        kubernetes.MarkResourceDeleted(original);
        var replacement = await kubernetes.CreateAsync(V2.Namespace.Create(original.Metadata.Name));
        await Assert.ThrowsAsync<k8s.Autorest.HttpOperationException>(() =>
            executor.StopAsync(TestContext.Current.CancellationToken)).DefaultTimeout();
        Assert.Equal(replacement.Metadata.Uid, (await kubernetes.GetAsync<V2.Namespace>(replacement.Metadata.Name)).Metadata.Uid);
        Assert.Equal(0, kubernetes.StopServerCalls);
        Assert.Single(kubernetes.DeletedResources);
    }

    [Fact]
    public async Task ContainerExecutablesWaitForParentAndAreRecreatedOnRestart()
    {
        var kubernetes = new TestKubernetesService { EmulateV2Controllers = true };
        var builder = CreateBuilder(kubernetes);
        var container = builder.AddContainer("container", "image").WithExplicitStart();
        var exec = builder.AddResource(new ContainerExecutableResource("exec", container.Resource, "tool", "/work") { Args = ["initial"] })
            .WithArgs("argument").WithEnvironment("VALUE", "value");
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new DcpExecutorEvents();
        events.Subscribe<OnResourceChangedContext>(context =>
        {
            if (context.Resource.Name == "exec" && context.Status.State == KnownResourceStates.Exited)
            {
                Assert.Null(context.UpdateSnapshot(new() { ResourceType = "Executable", Properties = [] }).ExitCode);
                stopped.TrySetResult();
            }
            return Task.CompletedTask;
        });
        builder.Services.AddSingleton(events);
        await using var app = builder.Build();
        var executor = app.Services.GetRequiredService<DcpExecutorV2>();
        await executor.RunApplicationAsync(TestContext.Current.CancellationToken).DefaultTimeout();
        Assert.Empty(kubernetes.CreatedResources.OfType<V2.LogicalContainerExec>());
        await executor.StartResourceAsync(executor.GetResource(container.Resource.GetResolvedResourceName()), TestContext.Current.CancellationToken).DefaultTimeout();
        var created = Assert.Single(kubernetes.CreatedResources.OfType<V2.LogicalContainerExec>());
        Assert.Equal(container.Resource.GetResolvedResourceName(), created.Spec.ContainerRef);
        Assert.Equal(["initial", "argument"], created.Spec.Execution.Args);
        Assert.Equal("/work", created.Spec.Execution.WorkingDirectory);
        Assert.Equal("value", Assert.Single(created.Spec.Execution.Env!).Value);
        await executor.StartResourceAsync(executor.GetResource(exec.Resource.GetResolvedResourceName()), TestContext.Current.CancellationToken).DefaultTimeout();
        var replacement = kubernetes.CreatedResources.OfType<V2.LogicalContainerExec>().Last();
        Assert.NotEqual(created.Metadata.Uid, replacement.Metadata.Uid);
        await executor.StopResourceAsync(executor.GetResource(exec.Resource.GetResolvedResourceName()), TestContext.Current.CancellationToken).DefaultTimeout();
        await stopped.Task.DefaultTimeout();
    }

    [Fact]
    public async Task PersistentExplicitStartContainerObservesWithoutEvaluatingCallbacks()
    {
        var kubernetes = new TestKubernetesService { EmulateV2Controllers = true };
        var builder = CreateBuilder(kubernetes);
        var calls = 0;
        var container = builder.AddContainer("container", "image").WithLifetime(ContainerLifetime.Persistent).WithExplicitStart()
            .WithEnvironment(_ => calls++);
        await using var app = builder.Build();
        var executor = app.Services.GetRequiredService<DcpExecutorV2>();
        await executor.RunApplicationAsync(TestContext.Current.CancellationToken).DefaultTimeout();
        var observed = Assert.Single(kubernetes.CreatedResources.OfType<V2.LogicalContainer>());
        Assert.Equal(V2.LogicalContainerMode.Existing, observed.Spec.Mode);
        Assert.Null(observed.Spec.Container);
        Assert.Equal(0, calls);
        await executor.StartResourceAsync(executor.GetResource(container.Resource.GetResolvedResourceName()), TestContext.Current.CancellationToken).DefaultTimeout();
        var started = kubernetes.CreatedResources.OfType<V2.LogicalContainer>().Last();
        Assert.Equal(V2.LogicalContainerMode.Persistent, started.Spec.Mode);
        Assert.NotNull(started.Spec.Container);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ContainerFilesUseV2FileModesAndResolveCallbacks()
    {
        var kubernetes = new TestKubernetesService { EmulateV2Controllers = true };
        var builder = CreateBuilder(kubernetes);
        builder.AddContainer("container", "image").WithContainerFiles("/data",
            [new ContainerFile { Name = "config", Contents = "contents", Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite }],
            defaultOwner: 1000, defaultGroup: 1001, umask: UnixFileMode.OtherRead);
        await using var app = builder.Build();
        await app.Services.GetRequiredService<DcpExecutorV2>().RunApplicationAsync(TestContext.Current.CancellationToken).DefaultTimeout();
        var files = Assert.Single(Assert.Single(kubernetes.CreatedResources.OfType<V2.LogicalContainer>()).Spec.Container!.CreateFiles!);
        Assert.Equal("/data", files.Destination);
        Assert.Equal(1000, files.DefaultOwner);
        Assert.Equal(1001, files.DefaultGroup);
        Assert.Equal((uint)UnixFileMode.OtherRead, files.Umask);
        var entry = Assert.Single(files.Entries!);
        Assert.Equal("config", entry.Name);
        Assert.Equal("contents", entry.Contents);
        Assert.Equal((uint)(UnixFileMode.UserRead | UnixFileMode.UserWrite), entry.Mode);
    }

    [Fact]
    public async Task FailedDependenciesPreventDependentLaunchWithoutBlockingOtherResources()
    {
        var kubernetes = new TestKubernetesService { EmulateV2Controllers = true };
        var builder = CreateBuilder(kubernetes);
        var bad = builder.AddExecutable("bad", "program", ".").WithEnvironment(_ => throw new InvalidOperationException("failure"));
        builder.AddExecutable("dependent", "program", ".").WaitFor(bad);
        builder.AddExecutable("independent", "program", ".");
        await using var app = builder.Build();
        _ = app.Services.GetRequiredService<ApplicationOrchestrator>();
        await app.Services.GetRequiredService<DcpExecutorV2>().RunApplicationAsync(TestContext.Current.CancellationToken).DefaultTimeout();
        Assert.Equal("independent", Assert.Single(kubernetes.CreatedResources.OfType<V2.LogicalProcess>()).AppModelResourceName);
    }

    [Fact]
    public async Task WatcherCanRestartFromStoppedCallbackWithoutAcceptingLateStatus()
    {
        var kubernetes = new TestKubernetesService { EmulateV2Controllers = true };
        var builder = CreateBuilder(kubernetes);
        builder.AddExecutable("app", "program", ".");
        var events = new DcpExecutorEvents();
        var restarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DcpExecutorV2? executor = null;
        var requested = 0;
        events.Subscribe<OnResourceChangedContext>(async context =>
        {
            if (context.Status.State == KnownResourceStates.Finished && Interlocked.Exchange(ref requested, 1) == 0)
            {
                try
                {
                    await executor!.StartResourceAsync(executor.GetResource(context.DcpResourceName), TestContext.Current.CancellationToken);
                    restarted.TrySetResult();
                }
                catch (Exception ex)
                {
                    restarted.TrySetException(ex);
                }
            }
        });
        builder.Services.AddSingleton(events);
        await using var app = builder.Build();
        executor = app.Services.GetRequiredService<DcpExecutorV2>();
        await executor.RunApplicationAsync(TestContext.Current.CancellationToken).DefaultTimeout();
        var original = Assert.Single(kubernetes.CreatedResources.OfType<V2.LogicalProcess>());
        original.Status = new() { Phase = V2.LogicalProcessPhase.Exited, ExitCode = 0 };
        kubernetes.PushResourceModified(original);
        await restarted.Task.DefaultTimeout();
        Assert.Equal(2, kubernetes.CreatedResources.OfType<V2.LogicalProcess>().Count());
    }

    [Fact]
    public async Task LogAvailabilityAndCaptureIdentityControlFollowStreamsAndShutdown()
    {
        var streams = Channel.CreateUnbounded<(string Source, string? Capture, Pipe Pipe)>();
        var kubernetes = new TestKubernetesService(startStream: (resource, source) =>
        {
            var pipe = new Pipe();
            streams.Writer.TryWrite((source, ((V2.LogicalProcess)resource).Status?.RuntimeLogs?.Uid, pipe));
            return pipe.Reader.AsStream();
        }) { EmulateV2Controllers = true };
        var builder = CreateBuilder(kubernetes);
        builder.AddExecutable("app", "program", ".");
        await using var app = builder.Build();
        var executor = app.Services.GetRequiredService<DcpExecutorV2>();
        await executor.RunApplicationAsync(TestContext.Current.CancellationToken).DefaultTimeout();
        var process = Assert.Single(kubernetes.CreatedResources.OfType<V2.LogicalProcess>());
        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource();
        var logService = app.Services.GetRequiredService<ResourceLoggerService>();
        await using var logs = logService.WatchAsync(process.Metadata.Name).GetAsyncEnumerator(cts.Token);
        Assert.True(await logs.MoveNextAsync());
        var next = logs.MoveNextAsync().AsTask();
        var system = await streams.Reader.ReadAsync(cts.Token);
        Assert.Equal(V2.Logs.System, system.Source);
        process.Status!.RuntimeLogs = new() { Name = "physical", Uid = "capture-one", Available = true };
        kubernetes.PushResourceModified(process);
        var runtime = new[] { await streams.Reader.ReadAsync(cts.Token), await streams.Reader.ReadAsync(cts.Token) };
        Assert.Equal([V2.Logs.Stderr, V2.Logs.Stdout], runtime.Select(s => s.Source).Order().ToArray());
        var stdout = Assert.Single(runtime, s => s.Source == V2.Logs.Stdout);
        await stdout.Pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("2026-11-09T12:00:00Z first\n"), cts.Token);
        Assert.True(await next.DefaultTimeout());
        Assert.Contains("first", Assert.Single(logs.Current).Content);
        process.Status.RuntimeLogs.Uid = "capture-two";
        kubernetes.PushResourceModified(process);
        var changed = new[] { await streams.Reader.ReadAsync(cts.Token), await streams.Reader.ReadAsync(cts.Token) };
        Assert.All(changed, s => Assert.Equal("capture-two", s.Capture));
        await executor.StopAsync(cts.Token).DefaultTimeout();
        foreach (var stream in runtime.Concat(changed).Prepend(system))
        {
            Assert.True((await stream.Pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("after shutdown\n"), cts.Token)).IsCompleted);
            await stream.Pipe.Writer.CompleteAsync();
        }
    }

    [Fact]
    public async Task PersistentProxiedProcessReusesItsSavedServingPort()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var first = new TestKubernetesService { EmulateV2Controllers = true };
        var firstBuilder = CreateBuilder(first);
        firstBuilder.Configuration["AppHost:Directory"] = workspace.WorkspaceRoot.FullName;
        firstBuilder.AddExecutable("persistent-proxy", "program", ".").WithPersistentLifetime()
            .WithHttpEndpoint(name: "http", isProxied: true);
        int firstPort;
        await using (var firstApp = firstBuilder.Build())
        {
            await firstApp.Services.GetRequiredService<DcpExecutorV2>().RunApplicationAsync(TestContext.Current.CancellationToken).DefaultTimeout();
            firstPort = Assert.Single(Assert.Single(first.CreatedResources.OfType<V2.LogicalProcess>()).Spec.Ports!).Port;
            Assert.True(firstApp.Services.GetRequiredService<PersistentPortAllocator>().PersistenceEnabled);
        }
        var second = new TestKubernetesService { EmulateV2Controllers = true };
        var secondBuilder = CreateBuilder(second);
        secondBuilder.Configuration["AppHost:Directory"] = workspace.WorkspaceRoot.FullName;
        secondBuilder.AddExecutable("persistent-proxy", "program", ".").WithPersistentLifetime()
            .WithHttpEndpoint(name: "http", isProxied: true);
        await using var secondApp = secondBuilder.Build();
        await secondApp.Services.GetRequiredService<DcpExecutorV2>().RunApplicationAsync(TestContext.Current.CancellationToken).DefaultTimeout();
        var secondPort = Assert.Single(Assert.Single(second.CreatedResources.OfType<V2.LogicalProcess>()).Spec.Ports!).Port;
        Assert.Equal(firstPort, secondPort);
        var store = secondApp.Services.GetRequiredService<PersistentPortAllocationStore>();
        Assert.Equal(secondPort, store.GetAllocations()[new("persistent-proxy", "$target:http")]);
    }

    [Fact]
    public async Task UrlActivityTracksControllerEndpointsAndServiceAvailability()
    {
        var kubernetes = new TestKubernetesService { EmulateV2Controllers = true };
        var builder = CreateBuilder(kubernetes);
        var producer = builder.AddExecutable("producer", "program", ".").WithHttpEndpoint(name: "http");
        var consumer = builder.AddExecutable("consumer", "program", ".");
        producer.Resource.Annotations.Add(new ResourceUrlAnnotation { Url = "http://localhost", Endpoint = producer.GetEndpoint("http") });
        consumer.Resource.Annotations.Add(new ResourceUrlAnnotation { Url = "http://localhost", Endpoint = producer.GetEndpoint("http") });
        var snapshots = new ConcurrentDictionary<string, CustomResourceSnapshot>();
        var events = new DcpExecutorEvents();
        events.Subscribe<OnResourceChangedContext>(context =>
        {
            snapshots[context.Resource.Name] = context.UpdateSnapshot(new() { ResourceType = "Executable", Properties = [] });
            return Task.CompletedTask;
        });
        builder.Services.AddSingleton(events);
        await using var app = builder.Build();
        await app.Services.GetRequiredService<DcpExecutorV2>().RunApplicationAsync(TestContext.Current.CancellationToken).DefaultTimeout();
        await AsyncTestHelpers.AssertIsTrueRetryAsync(() =>
            snapshots.Count == 2 && snapshots.Values.All(s => s.Urls.Length == 1 && !s.Urls[0].IsInactive),
            "Own and cross-resource URLs become active after controller endpoint publication.");
        var service = Assert.Single(kubernetes.CreatedResources.OfType<V2.LogicalService>());
        service.Status!.Phase = V2.LogicalServicePhase.Failed;
        kubernetes.PushResourceModified(service);
        await AsyncTestHelpers.AssertIsTrueRetryAsync(() => snapshots.Values.All(s => s.Urls[0].IsInactive),
            "A listener failure makes both own and cross-resource URLs inactive.");
    }

    [Fact]
    [RequiresTools(["openssl"])]
    public async Task ProcessCertificateTrustIsMaterializedAtResolvedPaths()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=aspire-v2-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var kubernetes = new TestKubernetesService { EmulateV2Controllers = true };
        var builder = CreateBuilder(kubernetes);
        var authorities = builder.AddCertificateAuthorityCollection("certificates").WithCertificate(certificate);
        builder.AddExecutable("app", "program", ".").WithCertificateAuthorityCollection(authorities)
            .WithCertificateTrustScope(CertificateTrustScope.Override);
        await using var app = builder.Build();
        var executor = app.Services.GetRequiredService<DcpExecutorV2>();
        await executor.RunApplicationAsync(TestContext.Current.CancellationToken).DefaultTimeout();
        var process = Assert.Single(kubernetes.CreatedResources.OfType<V2.LogicalProcess>());
        var bundle = Assert.Single(process.Spec.Process!.Env!, e => e.Name == "SSL_CERT_FILE").Value;
        var directory = Assert.Single(process.Spec.Process.Env!, e => e.Name == "SSL_CERT_DIR").Value;
        Assert.Equal(certificate.ExportCertificatePem(), await File.ReadAllTextAsync(bundle));
        Assert.Equal(OperatingSystem.IsWindows() ? 1 : 2, Directory.GetFiles(directory).Length);
    }

    [Fact]
    public async Task ImageAnnotatedResourcesUseContainersRegardlessOfClrResourceType()
    {
        var kubernetes = new TestKubernetesService { EmulateV2Controllers = true };
        var builder = CreateBuilder(kubernetes);
        var resource = builder.AddExecutable("container", "ignored", ".");
        resource.Resource.Annotations.Add(new ContainerImageAnnotation { Image = "image", Tag = "latest" });
        await using var app = builder.Build();
        await app.Services.GetRequiredService<DcpExecutorV2>().RunApplicationAsync(TestContext.Current.CancellationToken).DefaultTimeout();
        Assert.Equal("container", Assert.Single(kubernetes.CreatedResources.OfType<V2.LogicalContainer>()).AppModelResourceName);
        Assert.Empty(kubernetes.CreatedResources.OfType<V2.LogicalProcess>());
    }

    private static IDistributedApplicationBuilder CreateBuilder(TestKubernetesService kubernetes, bool v2 = true)
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { DisableDashboard = true });
        builder.Configuration["DcpPublisher:UseV2"] = v2.ToString();
        builder.Configuration["AppHost:Sha256"] = "0123456789abcdef";
        builder.Configuration["AppHost:PathSha256"] = "0123456789abcdef";
        builder.Services.Configure<DcpOptions>(options =>
        {
            options.CliPath = "./dcp";
            options.DashboardPath = "./dashboard";
            options.WaitForResourceCleanup = true;
        });
        builder.Services.AddSingleton<IKubernetesService>(kubernetes);
        builder.Services.AddSingleton<IDcpDependencyCheckService>(new TestDcpDependencyCheckService());
        builder.Services.AddSingleton<IDeveloperCertificateService>(new TestDeveloperCertificateService(
            new List<X509Certificate2>(), false, false, false));
        return builder;
    }
}
