// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Native.Client;
using Aspire.Hosting.Native.Core.Tests.TestServices;
using Aspire.Hosting.Native.Dcp;
using Aspire.Hosting.Native.Runtime;
using Aspire.Hosting.Native.Server;
using System.Text.Json;
using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace Aspire.Hosting.Native.Core.Tests;

public class NativeProviderLifecycleTests
{
    [Fact]
    public async Task WorkspaceCleanupRetryDoesNotReplayProviderRetirementOrRetainItsOldWorkloadFailure()
    {
        var options = new NativeServerOptions { CleanupTimeout = TimeSpan.FromSeconds(10) };
        using var handler = new DcpCleanupTestHandler();
        var attempts = 0;
        handler.BeforeDelete = (_, _) => Interlocked.Increment(ref attempts) <= 2
            ? Task.FromException(new HttpRequestException("Transient failure"))
            : Task.CompletedTask;
        await using var dcp = handler.CreateClient(options);
        await using var host = await NativeClientTestHost.StartWithExecutorAsync(
            new DcpWorkloadExecutor(dcp, Task.CompletedTask, options), options);
        await using var app = await ConnectAsync(host);
        await using var provider = await ConnectAsync(host);
        var integration = new LifecycleProviderTestIntegration();
        await RegisterAsync(app, provider, integration);
        var workspace = await app.Server.CreateApplicationWorkspaceAsync();
        var revision = await workspace.BeginRevisionAsync();
        await LifecycleProviderTestIntegration.AddAsync(revision,
            await revision.AddSecretParameterAsync("password", "private"), "generic/service:1");
        await workspace.CommitRevisionAsync(revision);
        await (await revision.StartApplicationAsync()).WaitApplicationReadyAsync(15000);
        await Assert.ThrowsAsync<NativeCapabilityException>(() => workspace.RetireApplicationWorkspaceAsync());
        Assert.Single(handler.Resources);
        Assert.Equal(1, integration.Resources.Single().DisposeCount);
        await workspace.RetireApplicationWorkspaceAsync();
        Assert.Empty(handler.Resources);
        Assert.Equal(3, handler.Deletes.Count);
        Assert.Equal(["initialize", "allocated", "ready", "retiring"], integration.Phases.Select(item => item.Phase));
        Assert.Equal(1, integration.Resources.Single().DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResourceRetirementCancelsAndDrainsOrdinaryReceiverAndArgumentInvocations(bool retireArgument)
    {
        await using var host = await NativeClientTestHost.StartAsync();
        await using var app = await ConnectAsync(host);
        await using var provider = await ConnectAsync(host);
        var integration = new LifecycleProviderTestIntegration();
        await RegisterAsync(app, provider, integration);
        var firstSession = await app.Server.OpenApplicationAsync();
        var first = await firstSession.StartGenerationAsync();
        var firstHandle = await LifecycleProviderTestIntegration.AddAsync(first,
            await first.AddSecretParameterAsync("password", "private"), "generic/service:1");
        var secondSession = await app.Server.OpenApplicationAsync();
        var second = await secondSession.StartGenerationAsync();
        var secondHandle = await LifecycleProviderTestIntegration.AddAsync(second,
            await second.AddSecretParameterAsync("password", "private"), "generic/service:1");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unrelatedEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unrelatedCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        integration.OnInvocation = async (receiver, argument, token) =>
        {
            var unrelated = ReferenceEquals(receiver, argument);
            using var registration = token.Register(() => (unrelated ? unrelatedCanceled : canceled).TrySetResult());
            (unrelated ? unrelatedEntered : entered).TrySetResult();
            await release.Task;
            Assert.Equal(0, receiver.DisposeCount);
            Assert.Equal(0, argument.DisposeCount);
            token.ThrowIfCancellationRequested();
        };
        var invocation = app.InvokeAsync("example.lifecycle/invoke",
            new() { ["context"] = firstHandle, ["other"] = secondHandle }, TestContext.Current.CancellationToken);
        var retainedHandle = retireArgument ? firstHandle : secondHandle;
        var unrelatedInvocation = app.InvokeAsync("example.lifecycle/invoke",
            new() { ["context"] = retainedHandle, ["other"] = retainedHandle }, TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await unrelatedEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var retiring = retireArgument ? secondSession.RetireGenerationAsync(second) : firstSession.RetireGenerationAsync(first);
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(retiring.IsCompleted);
            Assert.False(unrelatedCanceled.Task.IsCompleted);
            Assert.All(integration.Resources, resource => Assert.Equal(0, resource.DisposeCount));
            var retired = retireArgument ? integration.Resources.Last() : integration.Resources.First();
            Assert.Throws<InvalidOperationException>(() =>
                integration.Context!.PublishResource(retired, "example.lifecycle/Resource"));
            release.TrySetResult();
            await Assert.ThrowsAsync<NativeCapabilityException>(() => invocation);
            Assert.Equal(JsonValueKind.Null, (await unrelatedInvocation).ValueKind);
            await retiring.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(1, retired.DisposeCount);
            var retained = retireArgument ? integration.Resources.First() : integration.Resources.Last();
            Assert.Equal(0, retained.DisposeCount);
        }
        finally
        {
            release.TrySetResult();
        }
        if (retireArgument)
        {
            await firstSession.RetireGenerationAsync(first);
        }
        else
        {
            await secondSession.RetireGenerationAsync(second);
        }
        Assert.All(integration.Resources, resource => Assert.Equal(1, resource.DisposeCount));
    }

    [Fact]
    public async Task DisconnectBoundsItsWaitButDefersDisposalOfUncooperativeResourceInvocation()
    {
        await using var host = await NativeClientTestHost.StartAsync();
        await using var app = await ConnectAsync(host);
        var clock = new AdjustableFakeTimeProvider();
        var provider = await NativeClient.ConnectAsync(host.Endpoint, host.Token, TimeSpan.FromSeconds(15),
            clock, TestContext.Current.CancellationToken);
        var integration = new LifecycleProviderTestIntegration();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? disposal = null;
        try
        {
            await RegisterAsync(app, provider, integration);
            var session = await app.Server.OpenApplicationAsync();
            var composition = await session.StartGenerationAsync();
            var handle = await LifecycleProviderTestIntegration.AddAsync(composition,
                await composition.AddSecretParameterAsync("password", "private"), "generic/service:1");
            integration.OnInvocation = async (receiver, _, token) =>
            {
                using var registration = token.Register(() => canceled.TrySetResult());
                entered.TrySetResult();
                await release.Task;
                Assert.Equal(0, receiver.DisposeCount);
                token.ThrowIfCancellationRequested();
            };
            var invocation = app.InvokeAsync("example.lifecycle/invoke",
                new() { ["context"] = handle, ["other"] = handle }, TestContext.Current.CancellationToken);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var timerCreated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            clock.TimerCreated = dueTime =>
            {
                if (dueTime == TimeSpan.FromSeconds(15))
                {
                    timerCreated.TrySetResult();
                }
            };
            disposal = provider.DisposeAsync().AsTask();
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await timerCreated.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            clock.Advance(TimeSpan.FromMilliseconds(14999));
            Assert.False(disposal.IsCompleted);
            clock.Advance(TimeSpan.FromMilliseconds(1));
            await Assert.ThrowsAsync<TimeoutException>(() =>
                disposal.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            var resource = Assert.Single(integration.Resources);
            Assert.Equal(0, resource.DisposeCount);
            Assert.Throws<InvalidOperationException>(() =>
                integration.Context!.PublishResource(resource, "example.lifecycle/Resource"));
            await Assert.ThrowsAsync<NativeCapabilityException>(() => invocation);
            release.TrySetResult();
            await resource.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(1, resource.DisposeCount);
        }
        finally
        {
            release.TrySetResult();
            if (disposal is null)
            {
                await provider.DisposeAsync();
            }
            else
            {
                var failure = await Record.ExceptionAsync(() => disposal.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.True(failure is null or TimeoutException);
            }
        }
    }

    [Fact]
    public async Task PhaseRevocationDrainsExecutionBeforeIdempotentCancellationSourceDisposal()
    {
        using var parent = new CancellationTokenSource();
        CancellationToken executionToken = default;
        using var authority = new ResourceLifecycleAuthority("ready", ImmutableDictionary<string, string>.Empty,
            async (_, token) =>
            {
                executionToken = token;
                await Task.Delay(Timeout.InfiniteTimeSpan, token);

                return true;
            }, (_, _) => { }, parent.Token);
        var execution = authority.RunExecutable(new ExecutableWorkload(Environment.ProcessPath!, AppContext.BaseDirectory,
            [], [], []), new SymbolicWorkloadBindings([], []));
        authority.Revoke();
        Assert.True(executionToken.IsCancellationRequested);
        await authority.DrainAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        authority.Dispose();
        authority.Dispose();
        Assert.True(authority.IsRevoked);
        Assert.ThrowsAny<OperationCanceledException>(() => authority.AppendLog(LogStream.Stdout, "late log"));
        Assert.Throws<ObjectDisposedException>(() => executionToken.WaitHandle);
        await parent.CancelAsync();
    }

    [Theory]
    [InlineData("phase")]
    [InlineData("scope")]
    [InlineData("receiver")]
    [InlineData("result")]
    [InlineData("duplicate")]
    public async Task InvalidLifecycleMetadataIsRejectedBeforeProviderActivation(string invalid)
    {
        await using var host = await NativeClientTestHost.StartWithOptionsAsync(new());
        await using var app = await ConnectAsync(host);
        await using var provider = await ConnectAsync(host);
        var definition = new LifecycleProviderTestIntegration().Definition;
        var manifest = JsonNode.Parse(definition.Metadata.GetRawText())!.AsObject();
        var methods = manifest["capabilities"]!.AsArray();
        var phase = methods[1]!.AsObject();
        switch (invalid)
        {
            case "phase": phase["lifecyclePhase"] = "continuous-health"; break;
            case "scope": phase["parameters"]![1]!["type"]!["typeId"] = "Aspire.Hosting.Native.Server/Aspire.Hosting.Native.Api.DeclaredResource"; break;
            case "receiver":
                phase["targetTypeId"] = "Aspire.Hosting.Native.Server/Aspire.Hosting.Native.Api.DeclaredResource";
                phase["parameters"]![0]!["type"]!["typeId"] = "Aspire.Hosting.Native.Server/Aspire.Hosting.Native.Api.DeclaredResource";
                break;
            case "result": phase["returnType"]!["typeId"] = "string"; break;
            case "duplicate":
                var duplicate = phase.DeepClone().AsObject();
                duplicate["id"] = "example.lifecycle/duplicate";
                methods.Add(duplicate);
                break;
        }
        var authority = await app.ExpectIntegrationHostAsync("example.lifecycle", CancellationToken.None);
        await Assert.ThrowsAsync<StreamJsonRpc.RemoteInvocationException>(() => provider.RegisterIntegrationHostAsync(
            authority,
            definition with { Metadata = JsonSerializer.SerializeToElement(manifest) }, CancellationToken.None));
    }

    [Fact]
    public async Task ScopedPhasesConsumeNativeSecretsAfterPublicRevocationAndReleaseExactlyOnce()
    {
        var executor = Executor();
        await using var host = await NativeClientTestHost.StartWithExecutorAsync(executor, new());
        await using var app = await ConnectAsync(host);
        await using var provider = await ConnectAsync(host);
        var integration = new LifecycleProviderTestIntegration();
        await RegisterAsync(app, provider, integration);
        NativeCapabilityException? revoked = null;
        integration.OnPhase = async (phase, scope, token) =>
        {
            if (phase == "retiring")
            {
                revoked = await Assert.ThrowsAsync<NativeCapabilityException>(() =>
                    integration.Resources.Last().Declaration.InspectResourceAsync(token));
            }
            if (phase is "ready" or "retiring")
            {
                Assert.True(await scope.RunLifecycleExecutableAsync(new()
                {
                    ExecutablePath = Environment.ProcessPath!,
                    WorkingDirectory = AppContext.BaseDirectory,
                    Arguments = ["probe"],
                    Environment = [],
                    Endpoints = []
                }, new()
                {
                    Environment = [new() { Name = "TOKEN", ConfigurationKey = "workload.env.TOKEN" }],
                    Arguments = []
                }, token));
            }
        };
        var session = await app.Server.OpenApplicationAsync();
        var composition = await session.StartGenerationAsync();
        await LifecycleProviderTestIntegration.AddAsync(composition,
            await composition.AddSecretParameterAsync("password", "native-private-token"), "generic/service:1");
        await (await composition.StartApplicationAsync()).WaitApplicationReadyAsync(15000);
        var expired = integration.Scopes.Last();
        Assert.Equal("HANDLE_NOT_FOUND", (await Assert.ThrowsAsync<NativeCapabilityException>(() =>
            expired.ReadLifecyclePhaseAsync())).Code);
        await session.RetireGenerationAsync(composition);
        Assert.Equal("HANDLE_NOT_FOUND", revoked?.Code);
        Assert.Equal(["initialize", "allocated", "ready", "retiring"], integration.Phases.Select(item => item.Phase));
        Assert.Equal(1, Assert.Single(integration.Resources).DisposeCount);
        Assert.Equal(2, executor.Plans.OfType<ExecutableWorkload>().Count());
        Assert.All(executor.Plans.OfType<ExecutableWorkload>(), plan => Assert.Equal("native-private-token", Assert.Single(plan.Environment).Value));
        Assert.Equal(executor.Started.Count, executor.Removed.Count);
        Assert.Throws<InvalidOperationException>(() => integration.Context!.PublishResource(integration.Resources.Single(),
            "example.lifecycle/Resource"));
    }

    [Fact]
    public async Task RetirementCancelsInFlightReadyBeforeDisposalAndCannotReuseItsAuthority()
    {
        var executor = Executor();
        await using var host = await NativeClientTestHost.StartWithExecutorAsync(executor, new());
        await using var app = await ConnectAsync(host);
        await using var provider = await ConnectAsync(host);
        var integration = new LifecycleProviderTestIntegration();
        await RegisterAsync(app, provider, integration);
        var entered = new TaskCompletionSource<ResourceLifecycleScope>(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        integration.OnPhase = async (phase, scope, token) =>
        {
            if (phase == "ready")
            {
                entered.TrySetResult(scope);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, token);
                }
                finally
                {
                    Assert.Equal(0, integration.Resources.Single().DisposeCount);
                    canceled.TrySetResult();
                }
            }
        };
        var workspace = await app.Server.CreateApplicationWorkspaceAsync();
        var revision = await workspace.BeginRevisionAsync();
        await LifecycleProviderTestIntegration.AddAsync(revision,
            await revision.AddSecretParameterAsync("password", "private"), "generic/service:1");
        await workspace.CommitRevisionAsync(revision);
        var execution = await revision.StartApplicationAsync();
        var readiness = execution.WaitApplicationReadyAsync(15000);
        var scope = await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), TimeProvider.System);
        await workspace.RetireApplicationWorkspaceAsync();
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(15), TimeProvider.System);
        await Assert.ThrowsAsync<NativeCapabilityException>(() => readiness);
        Assert.Equal("HANDLE_NOT_FOUND", (await Assert.ThrowsAsync<NativeCapabilityException>(() => scope.ReadLifecyclePhaseAsync())).Code);
        Assert.Equal(1, integration.Resources.Single().DisposeCount);
        Assert.Equal(["initialize", "allocated", "ready", "retiring"], integration.Phases.Select(item => item.Phase));
        Assert.Equal(executor.Started.Count, executor.Removed.Count);
    }

    [Fact]
    public async Task RetainedRevisionHandoffsObjectsWithoutReplayingInitializationOrRestartingWorkload()
    {
        var executor = Executor();
        await using var host = await NativeClientTestHost.StartWithExecutorAsync(executor, new());
        await using var app = await ConnectAsync(host);
        await using var provider = await ConnectAsync(host);
        var integration = new LifecycleProviderTestIntegration();
        await RegisterAsync(app, provider, integration);
        var workspace = await app.Server.CreateApplicationWorkspaceAsync();
        async Task<Composition> Declare(string image)
        {
            var revision = await workspace.BeginRevisionAsync();
            await LifecycleProviderTestIntegration.AddAsync(revision,
                await revision.AddSecretParameterAsync("password", "private"), image);

            return revision;
        }
        var first = await Declare("generic/service:1");
        await workspace.CommitRevisionAsync(first);
        await (await first.StartApplicationAsync()).WaitApplicationReadyAsync(15000);
        var firstObject = integration.Resources.Single();
        await workspace.CommitRevisionAsync(await Declare("generic/service:1"));
        Assert.Single(executor.Started);
        Assert.Equal(1, firstObject.DisposeCount);
        Assert.Equal(["initialize", "allocated", "ready"], integration.Phases.Select(item => item.Phase));
        var retainedObject = integration.Resources.Last();
        await workspace.CommitRevisionAsync(await Declare("generic/service:2"));
        Assert.Equal(2, executor.Started.Count);
        Assert.Equal(1, retainedObject.DisposeCount);
        Assert.Same(retainedObject, integration.Phases.ElementAt(3).Resource);
        Assert.Equal("retiring", integration.Phases.ElementAt(3).Phase);
        await workspace.RetireApplicationWorkspaceAsync();
        Assert.All(integration.Resources, resource => Assert.Equal(1, resource.DisposeCount));
        Assert.Equal(2, executor.Removed.Count);
    }

    [Fact]
    public async Task ProviderDisconnectDuringAllocationCleansNativeWorkloadAndReleasesLocalObject()
    {
        var executor = Executor();
        await using var host = await NativeClientTestHost.StartWithExecutorAsync(executor, new());
        await using var app = await ConnectAsync(host);
        var provider = await ConnectAsync(host);
        var integration = new LifecycleProviderTestIntegration();
        await RegisterAsync(app, provider, integration);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        integration.OnPhase = async (phase, _, token) =>
        {
            if (phase == "allocated")
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, token);
            }
        };
        var session = await app.Server.OpenApplicationAsync();
        var revision = await session.StartGenerationAsync();
        await LifecycleProviderTestIntegration.AddAsync(revision,
            await revision.AddSecretParameterAsync("password", "private"), "generic/service:1");
        var ready = (await revision.StartApplicationAsync()).WaitApplicationReadyAsync(15000);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), TimeProvider.System);
        await provider.DisposeAsync();
        await Assert.ThrowsAsync<NativeCapabilityException>(() => ready);
        // Startup already reports disconnect. Retirement can join that first
        // cleanup or observe its completed retry, but cannot retain a workload.
        var retirementFailure = await Record.ExceptionAsync(() => session.RetireGenerationAsync(revision));
        Assert.True(retirementFailure is null or NativeCapabilityException { Code: "PROVIDER_DISCONNECTED" });
        Assert.Equal(1, integration.Resources.Single().DisposeCount);
        Assert.Single(executor.Removed);
    }

    [Fact]
    public async Task FailedRetiringCallbackIsNotReplayedAndDoesNotKeepNativeWorkloadOrProviderObjectAlive()
    {
        var executor = Executor();
        await using var host = await NativeClientTestHost.StartWithExecutorAsync(executor, new());
        await using var app = await ConnectAsync(host);
        await using var provider = await ConnectAsync(host);
        var integration = new LifecycleProviderTestIntegration
        {
            OnPhase = (phase, _, _) => phase == "retiring" ? Task.FromException(new InvalidOperationException("private-provider-secret")) : Task.CompletedTask
        };
        await RegisterAsync(app, provider, integration);
        var session = await app.Server.OpenApplicationAsync();
        var revision = await session.StartGenerationAsync();
        await LifecycleProviderTestIntegration.AddAsync(revision,
            await revision.AddSecretParameterAsync("password", "private"), "generic/service:1");
        await (await revision.StartApplicationAsync()).WaitApplicationReadyAsync(15000);
        var failure = await Assert.ThrowsAsync<NativeCapabilityException>(() => session.RetireGenerationAsync(revision));
        Assert.Equal("PROVIDER_LIFECYCLE_FAILED", failure.Code);
        Assert.Equal(1, integration.Resources.Single().DisposeCount);
        Assert.Single(executor.Removed);
        Assert.Single(integration.Phases, item => item.Phase == "retiring");
    }

    [Fact]
    public async Task LifecycleDeadlineUsesInjectedClockCancelsOnceAndReleasesFailedInitialization()
    {
        var executor = Executor();
        var clock = new AdjustableFakeTimeProvider();
        await using var host = await NativeClientTestHost.StartWithExecutorAsync(executor, new NativeServerOptions
        {
            TimeProvider = clock,
            ProviderInvocationTimeout = TimeSpan.FromSeconds(5)
        });
        await using var app = await ConnectAsync(host);
        await using var provider = await ConnectAsync(host);
        var integration = new LifecycleProviderTestIntegration();
        await RegisterAsync(app, provider, integration);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        integration.OnPhase = async (phase, _, token) =>
        {
            if (phase == "initialize")
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, clock, token);
            }
        };
        var session = await app.Server.OpenApplicationAsync();
        var revision = await session.StartGenerationAsync();
        await LifecycleProviderTestIntegration.AddAsync(revision,
            await revision.AddSecretParameterAsync("password", "private"), "generic/service:1");
        var ready = (await revision.StartApplicationAsync()).WaitApplicationReadyAsync(15000);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), TimeProvider.System);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal("PROVIDER_LIFECYCLE_TIMEOUT", (await Assert.ThrowsAsync<NativeCapabilityException>(() => ready)).Code);
        await session.RetireGenerationAsync(revision);
        Assert.Equal(["initialize", "retiring"], integration.Phases.Select(item => item.Phase));
        Assert.Equal(1, integration.Resources.Single().DisposeCount);
        Assert.Empty(executor.Started);
    }

    [Fact]
    public async Task RemovedAndAbortedDeclarationsReleaseObjectsWithoutActivatingUncommittedCallbacks()
    {
        var executor = Executor();
        await using var host = await NativeClientTestHost.StartWithExecutorAsync(executor, new());
        await using var app = await ConnectAsync(host);
        await using var provider = await ConnectAsync(host);
        var integration = new LifecycleProviderTestIntegration();
        await RegisterAsync(app, provider, integration);
        var workspace = await app.Server.CreateApplicationWorkspaceAsync();
        var first = await workspace.BeginRevisionAsync();
        await LifecycleProviderTestIntegration.AddAsync(first,
            await first.AddSecretParameterAsync("password", "private"), "generic/service:1");
        await workspace.CommitRevisionAsync(first);
        await (await first.StartApplicationAsync()).WaitApplicationReadyAsync(15000);
        await workspace.CommitRevisionAsync(await workspace.BeginRevisionAsync());
        Assert.Equal(1, integration.Resources.Single().DisposeCount);
        Assert.Single(executor.Removed);
        var discarded = await workspace.BeginRevisionAsync();
        await LifecycleProviderTestIntegration.AddAsync(discarded,
            await discarded.AddSecretParameterAsync("password", "private"), "generic/service:1");
        var discardedObject = integration.Resources.Last();
        await workspace.AbortRevisionAsync(discarded);
        await discardedObject.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(15), TimeProvider.System);
        Assert.Equal(1, discardedObject.DisposeCount);
        Assert.Equal(["initialize", "allocated", "ready", "retiring"], integration.Phases.Select(item => item.Phase));
        await workspace.RetireApplicationWorkspaceAsync();
        Assert.All(integration.Resources, resource => Assert.Equal(1, resource.DisposeCount));
    }

    [Fact]
    public async Task GuestCannotInvokeProviderPhaseOrBorrowItsScopeAndScopeCannotConsumeUndeclaredFields()
    {
        var executor = Executor();
        await using var host = await NativeClientTestHost.StartWithExecutorAsync(executor, new());
        await using var app = await ConnectAsync(host);
        await using var provider = await ConnectAsync(host);
        var integration = new LifecycleProviderTestIntegration();
        await RegisterAsync(app, provider, integration);
        integration.OnPhase = async (_, scope, token) =>
        {
            var forged = await Assert.ThrowsAsync<NativeCapabilityException>(() =>
                app.InvokeAsync("Aspire.Hosting.Native.Api/readLifecyclePhase",
                    new() { ["context"] = scope.Handle }, token));
            Assert.Equal("HANDLE_NOT_FOUND", forged.Code);
            var foreign = await Assert.ThrowsAsync<NativeCapabilityException>(() =>
                scope.RunLifecycleExecutableAsync(new()
                {
                    ExecutablePath = Environment.ProcessPath!,
                    WorkingDirectory = AppContext.BaseDirectory,
                    Arguments = [],
                    Environment = [],
                    Endpoints = []
                }, new()
                {
                    Environment = [new() { Name = "TOKEN", ConfigurationKey = "another-resource.secret" }],
                    Arguments = []
                }, token));
            Assert.Equal("OPERATION_REJECTED", foreign.Code);
        };
        var session = await app.Server.OpenApplicationAsync();
        var composition = await session.StartGenerationAsync();
        var handle = await LifecycleProviderTestIntegration.AddAsync(composition,
            await composition.AddSecretParameterAsync("password", "private"), "generic/service:1");
        var rejected = await Assert.ThrowsAsync<NativeCapabilityException>(() =>
            app.InvokeAsync("example.lifecycle/ready", new()
            {
                ["context"] = handle,
                ["scope"] = composition.Handle
            }, CancellationToken.None));
        Assert.Equal("LIFECYCLE_AUTHORITY_REQUIRED", rejected.Code);
        await (await composition.StartApplicationAsync()).WaitApplicationReadyAsync(15000);
        await session.RetireGenerationAsync(composition);
        Assert.Single(executor.Plans);
    }

    [Fact]
    public async Task CancelingPhaseDrainsItsInFlightNativeExecutableBeforeObjectRelease()
    {
        var childEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new WorkloadTestExecutor
        {
            StatusForPlan = plan =>
            {
                if (plan is ExecutableWorkload)
                {
                    childEntered.TrySetResult();
                }

                return new(WorkloadPhase.Running, null);
            }
        };
        executor.ReleaseCleanup();
        await using var host = await NativeClientTestHost.StartWithExecutorAsync(executor, new());
        await using var app = await ConnectAsync(host);
        await using var provider = await ConnectAsync(host);
        var integration = new LifecycleProviderTestIntegration
        {
            OnPhase = async (phase, scope, token) =>
            {
                if (phase == "ready")
                {
                    await scope.RunLifecycleExecutableAsync(new()
                    {
                        ExecutablePath = Environment.ProcessPath!,
                        WorkingDirectory = AppContext.BaseDirectory,
                        Arguments = [],
                        Environment = [],
                        Endpoints = []
                    }, new() { Environment = [], Arguments = [] }, token);
                }
            }
        };
        await RegisterAsync(app, provider, integration);
        var workspace = await app.Server.CreateApplicationWorkspaceAsync();
        var revision = await workspace.BeginRevisionAsync();
        await LifecycleProviderTestIntegration.AddAsync(revision,
            await revision.AddSecretParameterAsync("password", "private"), "generic/service:1");
        await workspace.CommitRevisionAsync(revision);
        var ready = (await revision.StartApplicationAsync()).WaitApplicationReadyAsync(15000);
        await childEntered.Task.WaitAsync(TimeSpan.FromSeconds(15), TimeProvider.System);
        await workspace.RetireApplicationWorkspaceAsync();
        await Assert.ThrowsAsync<NativeCapabilityException>(() => ready);
        Assert.Equal(2, executor.Started.Count);
        Assert.Equal(2, executor.Removed.Count);
        Assert.Equal(1, integration.Resources.Single().DisposeCount);
    }

    [Fact]
    public async Task PeerDisconnectAutomaticallyReleasesObjectsWithoutExplicitProviderDispose()
    {
        await using var host = await NativeClientTestHost.StartWithOptionsAsync(new());
        var app = await ConnectAsync(host);
        await using var provider = await ConnectAsync(host);
        var integration = new LifecycleProviderTestIntegration();
        await RegisterAsync(app, provider, integration);
        var workspace = await app.Server.CreateApplicationWorkspaceAsync();
        var revision = await workspace.BeginRevisionAsync();
        await LifecycleProviderTestIntegration.AddAsync(revision,
            await revision.AddSecretParameterAsync("password", "private"), "generic/service:1");
        var resource = integration.Resources.Single();
        await app.DisposeAsync();
        await resource.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(15), TimeProvider.System);
        Assert.Equal(1, resource.DisposeCount);
        Assert.Empty(integration.Phases);
        Assert.Throws<InvalidOperationException>(() => integration.Context!.PublishResource(resource, "example.lifecycle/Resource"));
    }

    private static WorkloadTestExecutor Executor()
    {
        var executor = new WorkloadTestExecutor
        {
            StatusForPlan = plan => new(plan is ContainerWorkload ? WorkloadPhase.Running : WorkloadPhase.Exited, 0)
        };
        executor.ReleaseCleanup();

        return executor;
    }

    private static Task<NativeClient> ConnectAsync(NativeClientTestHost host) =>
        NativeClient.ConnectAsync(host.Endpoint, host.Token, TimeSpan.FromSeconds(15), TimeProvider.System);

    private static async Task RegisterAsync(NativeClient app, NativeClient provider, LifecycleProviderTestIntegration integration) =>
        await provider.RegisterIntegrationHostAsync(await app.ExpectIntegrationHostAsync("example.lifecycle", CancellationToken.None),
            integration.Definition, CancellationToken.None);
}
