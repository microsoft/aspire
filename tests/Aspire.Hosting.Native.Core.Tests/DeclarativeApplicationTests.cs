// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.Native.Api;
using Aspire.Hosting.Native.Core.Tests.TestServices;
using Aspire.Hosting.Native.Model;
using Aspire.Hosting.Native.Runtime;
using static Aspire.Hosting.Native.Core.Tests.TestServices.RpcTestClient;

namespace Aspire.Hosting.Native.Core.Tests;

public class DeclarativeApplicationTests
{
    [Fact]
    public void ConcurrentRpcWorkloadArgumentsReceiveDistinctSymbolicSlots()
    {
        using var rpc = new RpcTestClient(authenticate: true);
        var session = rpc.CreateSession();
        var composition = rpc.StartGeneration(session);
        var resource = Result(rpc.Invoke("addContainer", Arguments(("context", composition),
            ("name", JsonValue.Create("service")),
            ("image", JsonValue.Create("generic/service:1")))));
        var value = Result(rpc.Invoke("createLiteralValue", Arguments(("context", composition),
            ("value", JsonValue.Create("value")))));
        Parallel.For(0, 32, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ =>
            WorkspaceRpcTestContext.EnsureSuccess(rpc.Invoke("withArgument", Arguments(("context", resource), ("value", value)))));
        var configuration = Result(rpc.Invoke("inspectSymbolicConfiguration", Arguments(("context", resource))));
        Assert.Equal(Enumerable.Range(0, 32).Select(index => $"workload.arg.{index}").Order(),
            configuration["properties"]!.AsArray().Select(entry => entry!["name"]!.GetValue<string>()).Order());
        WorkspaceRpcTestContext.EnsureSuccess(rpc.Invoke("retireGeneration", Arguments(("context", session), ("composition", composition))));
    }

    [Fact]
    public async Task SealingWaitsForTheWholeWorkloadAndSymbolicMutation()
    {
        using var model = new ApplicationModel();
        var resource = model.AddResource("service", "hosting/container");
        model.SetWorkload(resource, DeclaredWorkload.Create(new ContainerWorkload("generic/service:1", null, [], [], [], [])));
        var value = SymbolicValue.Literal(model.GenerationId, "value");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sealing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mutation = Task.Run(() => model.MutateWorkload(resource, current =>
        {
            entered.TrySetResult();
            release.Task.WaitAsync(TimeSpan.FromSeconds(15), TimeProvider.System).GetAwaiter().GetResult();

            return (current with
            {
                Bindings = new([new("VALUE", "workload.env.VALUE")], [])
            }, new("workload.env.VALUE", value));
        }));
        Task<ApplicationSnapshot> seal;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), TimeProvider.System);
            seal = Task.Run(() =>
            {
                sealing.TrySetResult();

                return model.Seal();
            });
            await sealing.Task.WaitAsync(TimeSpan.FromSeconds(15), TimeProvider.System);
            Assert.False(seal.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await mutation.WaitAsync(TimeSpan.FromSeconds(15), TimeProvider.System);
        }
        var snapshot = Assert.Single((await seal.WaitAsync(TimeSpan.FromSeconds(15), TimeProvider.System)).Resources);
        Assert.Equal(new SymbolicEnvironmentBinding("VALUE", "workload.env.VALUE"), Assert.Single(snapshot.Workload!.Bindings.Environment));
        Assert.Same(value, Assert.Single(snapshot.SymbolicConfiguration).Value);
        Assert.Throws<InvalidOperationException>(() => model.MutateWorkload(resource, current => (current, null)));
        Assert.Same(snapshot.Workload, model.ReadResource(resource).Workload);
    }

    [Fact]
    public void FailedWorkloadMutationLeavesPlanSymbolicValuesAndDependenciesUnchanged()
    {
        using var model = new ApplicationModel();
        var resource = model.AddResource("service", "hosting/container");
        var dependency = model.AddResource("dependency", "hosting/container");
        var original = DeclaredWorkload.Create(new ContainerWorkload("generic/service:1", null, [], [], [], []));
        model.SetWorkload(resource, original);
        var value = SymbolicValue.Endpoint(dependency, "tcp", EndpointValueProperty.Host);
        Assert.Throws<ArgumentException>(() => model.MutateWorkload(resource, current => (current with
        {
            Plan = new ContainerWorkload("invalid image", null, [], [], [], [])
        }, new("workload.env.VALUE", value))));
        Assert.Same(original, model.ReadResource(resource).Workload);
        Assert.Empty(model.ReadResource(resource).SymbolicConfiguration);
        Assert.Empty(model.ReadResource(resource).Dependencies);

        model.AddDependency(dependency, resource);
        using var foreign = new ApplicationModel();
        var foreignResource = foreign.AddResource("foreign", "hosting/container");
        var foreignValue = SymbolicValue.Endpoint(foreignResource, "tcp", EndpointValueProperty.Host);
        Assert.Throws<ArgumentException>(() => model.MutateWorkload(resource, current => (current with
        {
            Bindings = new([new("VALUE", "workload.env.VALUE")], [])
        }, new("workload.env.VALUE", foreignValue))));
        Assert.Same(original, model.ReadResource(resource).Workload);
        Assert.Empty(model.ReadResource(resource).SymbolicConfiguration);
        Assert.Empty(model.ReadResource(resource).Dependencies);
    }

    [Fact]
    public async Task ConcurrentWorkloadMutatorsPreserveEveryDeclarationAndSymbolicBinding()
    {
        var session = new NativeApplicationServer().OpenApplication();
        var composition = session.StartGeneration();
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var resource = composition.AddContainer($"service-{iteration}", "generic/service:1");
            var value = composition.CreateLiteralValue("value");
            Parallel.For(0, 16, new ParallelOptions { MaxDegreeOfParallelism = 8 }, index =>
            {
                resource.WithArgument(value);
                resource.WithEnvironment($"VALUE_{index}", value);
                resource.WithEndpoint(new()
                {
                    Name = $"port-{index}",
                    Protocol = "TCP",
                    UriScheme = "tcp",
                    TargetPort = 6000 + index,
                    HostPort = 0,
                    EnvironmentVariable = ""
                });
                resource.WithVolume($"data-{index}", $"/data/{index}", false);
                resource.WithEntrypoint("service");
                resource.WithLifecycleExecutable("health", new()
                {
                    ExecutablePath = Environment.ProcessPath!,
                    WorkingDirectory = AppContext.BaseDirectory,
                    Arguments = ["health"],
                    Environment = [],
                    Endpoints = []
                }, new() { Arguments = [], Environment = [] });
            });
            var snapshot = composition.Model.ReadResource(resource.Handle);
            var workload = Assert.IsType<DeclaredWorkload>(snapshot.Workload);
            Assert.Equal(16, workload.Plan.Arguments.Length);
            Assert.Equal(Enumerable.Range(0, 16), workload.Bindings.Arguments.Select(binding => binding.Index).Order());
            Assert.Equal(Enumerable.Range(0, 16).Select(index => $"workload.arg.{index}").Order(),
                workload.Bindings.Arguments.Select(binding => binding.ConfigurationKey).Order());
            Assert.Equal(Enumerable.Range(0, 16).Select(index => $"VALUE_{index}").Order(),
                workload.Bindings.Environment.Select(binding => binding.Name).Order());
            Assert.Equal(Enumerable.Range(0, 16).Select(index => $"port-{index}").Order(),
                workload.Plan.Endpoints.Select(port => port.Name).Order());
            var container = Assert.IsType<ContainerWorkload>(workload.Plan);
            Assert.Equal(Enumerable.Range(0, 16).Select(index => $"/data/{index}").Order(),
                container.Mounts.Select(mount => mount.Target).Order());
            Assert.Equal("service", container.Command);
            Assert.Equal("health", Assert.Single(workload.Lifecycle).Phase);
            Assert.Equal(workload.Bindings.Arguments.Select(binding => binding.ConfigurationKey)
                .Concat(workload.Bindings.Environment.Select(binding => binding.ConfigurationKey)).Order(),
                snapshot.SymbolicConfiguration.Select(entry => entry.Name).Order());
            Assert.All(snapshot.SymbolicConfiguration, entry => Assert.Same(value.Value, entry.Value));
        }
        await session.RetireGeneration(composition);
    }

    [Fact]
    public async Task WorkspaceRejectsRevisionWhileDeclaredStartupStillOwnsItsDesiredPlan()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new WorkloadTestExecutor
        {
            BeforeStart = token =>
            {
                entered.TrySetResult();
                return release.Task.WaitAsync(token);
            }
        };
        executor.ReleaseCleanup();
        var workspace = new NativeApplicationServer(executor).CreateApplicationWorkspace();
        var first = workspace.BeginRevision();
        first.AddContainer("service", "generic/service:1");
        await workspace.CommitRevision(first);
        var application = first.StartApplication();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), TimeProvider.System);
        var next = workspace.BeginRevision();
        next.AddContainer("service", "generic/service:2");
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.CommitRevision(next));
        Assert.Equal("The declared application is still activating.", failure.Message);
        workspace.AbortRevision(next);
        release.TrySetResult();
        await application.WaitApplicationReady(15000);
        Assert.False(first.IsRetired);
        await workspace.RetireApplicationWorkspace();
    }

    [Fact]
    public async Task DeclaredRetirementStopsConsumersBeforeProducersRegardlessOfNameOrder()
    {
        var executor = new WorkloadTestExecutor();
        executor.ReleaseCleanup();
        var session = new NativeApplicationServer(executor).OpenApplication();
        var composition = session.StartGeneration();
        var producer = composition.AddContainer("a-producer", "generic/service:1");
        var consumer = composition.AddContainer("z-consumer", "generic/consumer:1");
        consumer.WaitFor(producer);
        await composition.StartApplication().WaitApplicationReady(15000);
        await session.RetireGeneration(composition);
        Assert.Equal([consumer.Handle.ResourceId, producer.Handle.ResourceId], executor.Removed.Select(identity => identity.ResourceId));
    }

    [Fact]
    public async Task RemovedDeclaredWorkloadsRetireUsingThePreviousDependencyGraph()
    {
        var executor = new WorkloadTestExecutor();
        executor.ReleaseCleanup();
        var workspace = new NativeApplicationServer(executor).CreateApplicationWorkspace();
        var first = workspace.BeginRevision();
        var producer = first.AddContainer("a-producer", "generic/service:1");
        var consumer = first.AddContainer("z-consumer", "generic/consumer:1");
        consumer.WaitFor(producer);
        await workspace.CommitRevision(first);
        await first.StartApplication().WaitApplicationReady(15000);

        await workspace.CommitRevision(workspace.BeginRevision());
        Assert.Equal([consumer.Handle.ResourceId, producer.Handle.ResourceId],
            executor.Removed.Select(identity => identity.ResourceId));
        await workspace.RetireApplicationWorkspace();
        Assert.Equal(2, executor.Removed.Count);
    }

    [Fact]
    public async Task InitializationFailureRunsDeclaredCleanupEvenWhenMainWorkloadNeverStarted()
    {
        var executor = new WorkloadTestExecutor
        {
            StatusForPlan = plan => new(WorkloadPhase.Exited, plan.Arguments.SequenceEqual(["initialize"]) ? 1 : 0)
        };
        executor.ReleaseCleanup();
        var session = new NativeApplicationServer(executor).OpenApplication();
        var composition = session.StartGeneration();
        var resource = composition.AddContainer("service", "generic/service:1");
        foreach (var phase in new[] { "initialize", "retire" })
        {
            resource.WithLifecycleExecutable(phase, new()
            {
                ExecutablePath = Environment.ProcessPath!,
                WorkingDirectory = AppContext.BaseDirectory,
                Arguments = [phase],
                Environment = [],
                Endpoints = []
            }, new() { Environment = [], Arguments = [] });
        }
        var application = composition.StartApplication();
        await Assert.ThrowsAsync<InvalidOperationException>(() => application.WaitApplicationReady(15000));
        await session.RetireGeneration(composition);
        Assert.Equal(["initialize", "retire"], executor.Plans.Select(plan => Assert.Single(plan.Arguments)));
        Assert.Equal(2, executor.Removed.Count);
    }

    [Fact]
    public async Task DeclaredWorkspaceRevisionsRetainUnchangedWorkloadsAndRestartAffectedConsumers()
    {
        var executor = new WorkloadTestExecutor();
        executor.ReleaseCleanup();
        var workspace = new NativeApplicationServer(executor).CreateApplicationWorkspace();
        Composition Declare(string image)
        {
            var revision = workspace.BeginRevision();
            var producer = revision.AddContainer("producer", image);
            producer.WithEndpoint(new() { Name = "tcp", Protocol = "TCP", UriScheme = "tcp", TargetPort = 6379, HostPort = 0, EnvironmentVariable = "" });
            var consumer = revision.AddExecutable("consumer", Environment.ProcessPath!, AppContext.BaseDirectory);
            consumer.WithReference("HOST", revision.CreateEndpointValue(producer, "tcp", "host"));
            return revision;
        }
        var first = Declare("generic/service:1");
        await workspace.CommitRevision(first);
        await first.StartApplication().WaitApplicationReady(15000);
        Assert.Equal(2, executor.Started.Count);
        await workspace.CommitRevision(Declare("generic/service:1"));
        Assert.Equal(2, executor.Started.Count);
        await workspace.CommitRevision(Declare("generic/service:2"));
        Assert.Equal(4, executor.Started.Count);
        Assert.Equal(2, executor.Removed.Count);
        Assert.True(first.IsRetired);
        await workspace.RetireApplicationWorkspace();
        Assert.Equal(4, executor.Removed.Count);
    }

    [Fact]
    public async Task FailedProtocolHealthUsesInjectedClockAndCleansContainerWithoutStartingDependents()
    {
        var probeWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRetirement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayedCleanupResource = Guid.Empty;
        var clock = new AdjustableFakeTimeProvider
        {
            TimerCreated = due => { if (due == TimeSpan.FromSeconds(1)) { probeWaiting.TrySetResult(); } }
        };
        var executor = new WorkloadTestExecutor
        {
            StatusForPlan = plan => new(plan is ContainerWorkload ? WorkloadPhase.Running : WorkloadPhase.Exited, 1),
            BeforeCleanup = identity => identity.ResourceId == delayedCleanupResource ? releaseRetirement.Task : Task.CompletedTask
        };
        executor.ReleaseCleanup();
        var options = new NativeRuntimeOptions
        {
            TimeProvider = clock,
            WorkloadStartupTimeout = TimeSpan.FromSeconds(10),
            ObservationInterval = TimeSpan.FromSeconds(1)
        };
        var session = new NativeApplicationServer(executor, options).OpenApplication();
        var composition = session.StartGeneration();
        var service = composition.AddContainer("service", "generic/service:1");
        service.WithLifecycleExecutable("health", new()
        {
            ExecutablePath = Environment.ProcessPath!,
            WorkingDirectory = AppContext.BaseDirectory,
            Arguments = ["reject-authentication"],
            Environment = [],
            Endpoints = []
        }, new() { Arguments = [], Environment = [] });
        var consumer = composition.AddExecutable("consumer", Environment.ProcessPath!, AppContext.BaseDirectory);
        consumer.WaitFor(service);
        // Hold an independent leaf so retirement is observed before it reaches
        // the failed producer, rather than depending on continuation scheduling.
        delayedCleanupResource = composition.AddContainer("independent", "generic/service:1").Handle.ResourceId;
        var application = composition.StartApplication();
        Task retirement;
        try
        {
            var readiness = application.WaitApplicationReady(15000);
            await probeWaiting.Task.WaitAsync(TimeSpan.FromSeconds(15), TimeProvider.System);
            clock.Advance(TimeSpan.FromSeconds(10));
            var failure = await Record.ExceptionAsync(() => readiness);
            Assert.True(failure is OperationCanceledException or InvalidOperationException);
            retirement = session.RetireGeneration(composition);
            Assert.False(retirement.IsCompleted);
        }
        finally
        {
            releaseRetirement.TrySetResult();
        }
        await retirement.WaitAsync(TimeSpan.FromSeconds(15), TimeProvider.System);
        Assert.All(executor.Plans.OfType<ExecutableWorkload>(), plan => Assert.Equal(["reject-authentication"], plan.Arguments));
        Assert.Equal(executor.Started.Count, executor.Removed.Count);
    }

    [Fact]
    public async Task NativeApplicationStartsDeclaredWorkloadsAndAuthenticatedLifecycleWithoutInvitations()
    {
        var executor = new WorkloadTestExecutor
        {
            StatusForPlan = plan => new(plan is ContainerWorkload ? WorkloadPhase.Running : WorkloadPhase.Exited, 0)
        };
        executor.ReleaseCleanup();
        var server = new NativeApplicationServer(executor);
        var session = server.OpenApplication();
        var composition = session.StartGeneration();
        var secret = composition.AddSecretParameter("password", "native-only-secret");
        var service = composition.AddContainer("service", "generic/service:1");
        foreach (var phase in new[] { "initialize", "ready" })
        {
            service.WithLifecycleExecutable(phase, new()
            {
                ExecutablePath = Environment.ProcessPath!,
                WorkingDirectory = AppContext.BaseDirectory,
                Environment = [],
                Arguments = [phase],
                Endpoints = []
            }, new() { Environment = [], Arguments = [] });
        }
        service.WithEnvironment("PASSWORD", secret);
        service.WithEndpoint(new() { Name = "tcp", Protocol = "TCP", UriScheme = "tcp", TargetPort = 6379, HostPort = 0, EnvironmentVariable = "" });
        service.SetSymbolicConfiguration("probe.host", composition.CreateEndpointValue(service, "tcp", "host"));
        service.SetSymbolicConfiguration("probe.password", secret);
        service.WithLifecycleExecutable("health", new()
        {
            ExecutablePath = Environment.ProcessPath!,
            WorkingDirectory = AppContext.BaseDirectory,
            Environment = [],
            Arguments = ["health"],
            Endpoints = []
        }, new()
        {
            Environment = [new() { Name = "HOST", ConfigurationKey = "probe.host" }, new() { Name = "PASSWORD", ConfigurationKey = "probe.password" }],
            Arguments = []
        });
        service.WithLifecycleExecutable("retire", new()
        {
            ExecutablePath = Environment.ProcessPath!,
            WorkingDirectory = AppContext.BaseDirectory,
            Environment = [],
            Arguments = ["retire"],
            Endpoints = []
        }, new() { Environment = [], Arguments = [] });
        var consumer = composition.AddExecutable("consumer", Environment.ProcessPath!, AppContext.BaseDirectory);
        consumer.WithReference("SERVICE_HOST", composition.CreateEndpointValue(service, "tcp", "host"));
        Assert.Empty(executor.Started);
        var application = composition.StartApplication();
        await application.WaitApplicationReady(15000);
        Assert.True(await application.WaitResourceCompletion(consumer, 15000));
        Assert.Equal(5, executor.Started.Count);
        var health = Assert.Single(executor.Plans.OfType<ExecutableWorkload>(), plan => plan.Arguments.SequenceEqual(["health"]));
        Assert.Collection(health.Environment, entry => Assert.Equal(new WorkloadEnvironment("HOST", "127.0.0.1"), entry),
            entry => Assert.Equal(new WorkloadEnvironment("PASSWORD", "native-only-secret"), entry));
        await session.RetireGeneration(composition);
        Assert.Equal(6, executor.Removed.Count);
        Assert.Single(executor.Plans.OfType<ExecutableWorkload>(), plan => plan.Arguments.SequenceEqual(["retire"]));
    }

    [Fact]
    public async Task ContainerReferenceRejectsUnsupportedNetworkContextInsteadOfUsingHostLoopback()
    {
        var executor = new WorkloadTestExecutor();
        executor.ReleaseCleanup();
        var session = new NativeApplicationServer(executor).OpenApplication();
        var composition = session.StartGeneration();
        var service = composition.AddContainer("service", "generic/service:1");
        var consumer = composition.AddContainer("consumer", "generic/consumer:1");
        consumer.WithReference("SERVICE", composition.CreateEndpointValue(service, "tcp", "host"));
        var application = composition.StartApplication();
        await Assert.ThrowsAsync<SymbolicValueException>(() => application.WaitApplicationReady(15000));
        await session.RetireGeneration(composition);
    }
}
