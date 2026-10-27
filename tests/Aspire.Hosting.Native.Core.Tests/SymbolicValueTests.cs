// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Native.Model;
using Aspire.Hosting.Native.Runtime;
using Aspire.Hosting.Native.Core.Tests.TestServices;

namespace Aspire.Hosting.Native.Core.Tests;

public class SymbolicValueTests
{
    [Fact]
    public void SecretCompositesRetainClassificationParametersAndResourceReferences()
    {
        using var model = new ApplicationModel();
        var redis = model.AddResource("cache", "example/redis");
        var password = model.AddSecretParameter("cache-password", "secret, /@");
        var uri = SymbolicValue.Literal(model.GenerationId, "redis://:")
            .Concat(password.UriEncode()).Concat(SymbolicValue.Literal(model.GenerationId, "@"))
            .Concat(SymbolicValue.Endpoint(redis, "tcp", EndpointValueProperty.HostAndPort));
        Assert.True(uri.IsSecret);
        Assert.Equal(["cache-password"], uri.ParameterReferences);
        Assert.Equal([redis], uri.References);
        Assert.Equal("[secret symbolic value]", uri.ToString());
        Assert.Equal("redis://:secret%2C%20%2F%40@127.0.0.1:6379",
            uri.Resolve(ValueNetworkContext.Host, (_, _, _) => "127.0.0.1:6379"));
        var error = Assert.Throws<SymbolicValueException>(() => uri.Resolve(ValueNetworkContext.Container, (_, _, _) => "must-not-be-used"));
        Assert.Equal("VALUE_NETWORK_CONTEXT_UNSUPPORTED", error.Code);
    }

    [Fact]
    public void SymbolicReferencesAllowCyclesWithoutCreatingReadinessDependencies()
    {
        using var model = new ApplicationModel();
        var first = model.AddResource("first", "example/service");
        var second = model.AddResource("second", "example/service");
        model.SetSymbolicConfiguration(first, "target", SymbolicValue.Endpoint(second, "tcp", EndpointValueProperty.Port));
        Assert.Empty(model.ReadResource(first).Dependencies);
        model.SetSymbolicConfiguration(second, "target", SymbolicValue.Endpoint(first, "tcp", EndpointValueProperty.Port));
        Assert.Equal([first], Assert.Single(model.ReadResource(second).SymbolicConfiguration).Value.References);
        Assert.Empty(model.ReadResource(second).Dependencies);
        model.SetSymbolicConfiguration(first, "target", SymbolicValue.Literal(model.GenerationId, "replacement"));
        Assert.Empty(model.ReadResource(first).Dependencies);
        model.SetSymbolicConfiguration(first, "target", SymbolicValue.Endpoint(second, "tcp", EndpointValueProperty.Port));
        model.RemoveSymbolicConfiguration(first, "target");
        Assert.Empty(model.ReadResource(first).Dependencies);
    }

    [Fact]
    public void ValuesRejectCrossGenerationAndUndeclaredParameterAuthority()
    {
        using var model = new ApplicationModel();
        using var other = new ApplicationModel();
        var resource = model.AddResource("service", "example/service");
        Assert.Throws<ArgumentException>(() => model.SetSymbolicConfiguration(resource, "value",
            SymbolicValue.Literal(other.GenerationId, "wrong")));
        var declared = model.AddSecretParameter("password", "expected");
        Assert.Throws<ArgumentException>(() => model.ValidateValue(SymbolicValue.SecretParameter(model.GenerationId, "password", "forged")));
        model.ValidateValue(declared);
        model.Dispose();
        Assert.Throws<ObjectDisposedException>(() => model.ValidateValue(declared));
    }

    [Fact]
    public async Task SymbolicLaunchUsesAllocationWithoutImplicitReadinessAndResolvesValuesServerSide()
    {
        using var model = new ApplicationModel();
        var source = model.AddResource("source", "example/service");
        var target = model.AddResource("target", "example/workload");
        var secret = model.AddSecretParameter("password", "launch-secret");
        var connection = SymbolicValue.Endpoint(source, "tcp", EndpointValueProperty.HostAndPort)
            .Concat(SymbolicValue.Literal(model.GenerationId, ",password=")).Concat(secret);
        model.SetSymbolicConfiguration(target, "connection", connection);
        var executor = new WorkloadTestExecutor();
        executor.ReleaseCleanup();
        using var runtime = new RuntimeGeneration(model.Seal(), executor);
        using var sourceOwner = runtime.ClaimResource(source);
        using var targetOwner = runtime.ClaimResource(target);
        var sourcePlan = new ContainerWorkload("redis:8", 6379, [], []);
        await sourceOwner.StartWorkload(sourcePlan);
        var executable = new ExecutableWorkload(Environment.ProcessPath!, AppContext.BaseDirectory, [], [""], []);
        var pending = targetOwner.StartSymbolicWorkload(1, executable, ValueNetworkContext.Host,
            new([new("CONNECTION", "connection")], [new(0, "connection")]));
        await pending.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System);
        Assert.False(runtime.Read().Resources.Single(resource => resource.Name == "source").Healthy);
        var launched = Assert.IsType<ExecutableWorkload>(executor.Plans.Last());
        Assert.Equal("127.0.0.1:5050,password=launch-secret", Assert.Single(launched.Environment).Value);
        Assert.Equal("127.0.0.1:5050,password=launch-secret", Assert.Single(launched.Arguments));
        targetOwner.AppendLog(LogStream.Stdout, "credential=launch-secret");
        Assert.Equal("credential=[REDACTED]", Assert.Single(runtime.ReadLogs(target.ResourceId, 0).Entries).Message);
    }

    [Fact]
    public async Task ChangedSymbolicRevisionRejectsPendingStaleLaunchWithoutExecutingOldSecrets()
    {
        using var model = new ApplicationModel();
        var source = model.AddResource("source", "example/service");
        var target = model.AddResource("target", "example/workload");
        model.AddDependency(target, source);
        model.SetSymbolicConfiguration(target, "password", model.AddSecretParameter("password", "old-secret"));
        var executor = new WorkloadTestExecutor();
        executor.ReleaseCleanup();
        using var runtime = new RuntimeGeneration(model.Seal(), executor);
        using var sourceOwner = runtime.ClaimResource(source);
        using var owner = runtime.ClaimResource(target);
        var executable = new ExecutableWorkload(Environment.ProcessPath!, AppContext.BaseDirectory, [], [], []);
        var pending = owner.StartSymbolicWorkload(1, executable, ValueNetworkContext.Host, new([new("PASSWORD", "password")], []));
        Assert.False(pending.IsCompleted);
        using var replacement = new ApplicationModel();
        var updatedSource = replacement.AddResource("source", "example/service");
        var updatedTarget = replacement.AddResource("target", "example/workload");
        replacement.AddDependency(updatedTarget, updatedSource);
        replacement.SetSymbolicConfiguration(updatedTarget, "password", replacement.AddSecretParameter("password", "new-secret"));
        await runtime.ApplyRevision(replacement.Seal());
        sourceOwner.Publish(ResourceState.Running, true, []);
        var error = await Assert.ThrowsAsync<SymbolicValueException>(() => pending);
        Assert.Equal("VALUE_REVISION_STALE", error.Code);
        Assert.Empty(executor.Plans);
        Assert.Equal("Workload failed: SymbolicValueException.", Assert.Single(runtime.ReadLogs(target.ResourceId, 0).Entries).Message);
    }

    [Fact]
    public async Task IdenticalSymbolicRevisionRetainsAppliedStateAndSecretChangeInvalidatesIt()
    {
        using var initial = new ApplicationModel();
        var resource = initial.AddResource("cache", "example/redis");
        initial.SetSymbolicConfiguration(resource, "password", initial.AddSecretParameter("password", "first-secret"));
        using var runtime = new RuntimeGeneration(initial.Seal());
        using var owner = runtime.ClaimResource(resource);
        owner.CompleteConfiguration(1, ConfigurationStatus.Succeeded, "applied");
        using var identical = new ApplicationModel();
        var identicalResource = identical.AddResource("cache", "example/redis");
        identical.SetSymbolicConfiguration(identicalResource, "password", identical.AddSecretParameter("password", "first-secret"));
        await runtime.ApplyRevision(identical.Seal());
        Assert.Equal(1, owner.ReadConfiguration().Revision);
        Assert.Equal(ConfigurationStatus.Succeeded, owner.ReadConfiguration().Status);
        using var changed = new ApplicationModel();
        var changedResource = changed.AddResource("cache", "example/redis");
        changed.SetSymbolicConfiguration(changedResource, "password", changed.AddSecretParameter("password", "second-secret"));
        await runtime.ApplyRevision(changed.Seal());
        Assert.Equal(2, owner.ReadConfiguration().Revision);
        Assert.Equal(ConfigurationStatus.Pending, owner.ReadConfiguration().Status);
        owner.AppendLog(LogStream.Stdout, "first-secret second-secret");
        Assert.Equal("[REDACTED] [REDACTED]", Assert.Single(runtime.ReadLogs(resource.ResourceId, 0).Entries).Message);
    }

    [Fact]
    public async Task ContainerReferencesAndCallerContextOverridesFailBeforeExecutorSideEffects()
    {
        using var model = new ApplicationModel();
        var source = model.AddResource("source", "example/service");
        var target = model.AddResource("target", "example/workload");
        model.SetSymbolicConfiguration(target, "endpoint", SymbolicValue.Endpoint(source, "tcp", EndpointValueProperty.Host));
        var executor = new WorkloadTestExecutor();
        using var runtime = new RuntimeGeneration(model.Seal(), executor);
        using var owner = runtime.ClaimResource(target);
        var plan = new ContainerWorkload("redis:8", 6379, [], []);
        foreach (var context in new[] { ValueNetworkContext.Host, ValueNetworkContext.Container })
        {
            var error = await Assert.ThrowsAsync<SymbolicValueException>(() => owner.StartSymbolicWorkload(1, plan, context, new([new("ENDPOINT", "endpoint")], [])));
            Assert.Equal("VALUE_NETWORK_CONTEXT_UNSUPPORTED", error.Code);
        }
        Assert.Empty(executor.Started);
    }

    [Fact]
    public async Task UnallocatedSelfEndpointFailsWithoutWaitingOrStartingExecutor()
    {
        using var model = new ApplicationModel();
        var target = model.AddResource("target", "example/workload");
        model.SetSymbolicConfiguration(target, "endpoint", SymbolicValue.Endpoint(target, "tcp", EndpointValueProperty.Host));
        Assert.Empty(model.ReadResource(target).Dependencies);
        var executor = new WorkloadTestExecutor();
        using var runtime = new RuntimeGeneration(model.Seal(), executor);
        using var owner = runtime.ClaimResource(target);
        var error = await Assert.ThrowsAsync<SymbolicValueException>(() => owner.StartSymbolicWorkload(1,
            new ExecutableWorkload(Environment.ProcessPath!, AppContext.BaseDirectory, [], [], []), ValueNetworkContext.Host,
            new([new("ENDPOINT", "endpoint")], [])));
        Assert.Equal("VALUE_ENDPOINT_UNAVAILABLE", error.Code);
        Assert.Empty(executor.Plans);
    }

    [Fact]
    public async Task SecretHistoryCapacityRejectsRevisionAtomicallyAndKeepsPriorRedaction()
    {
        using var initial = Declare(0);
        var resource = initial.Inspect().Resources.Single().Handle;
        using var runtime = new RuntimeGeneration(initial.Seal());
        using var owner = runtime.ClaimResource(resource);
        for (var revision = 1; revision < 32; revision++)
        {
            using var next = Declare(revision);
            await runtime.ApplyRevision(next.Seal());
        }
        using var overflow = Declare(32);
        var error = await Assert.ThrowsAsync<SymbolicValueException>(() => runtime.ApplyRevision(overflow.Seal()));
        Assert.Equal("VALUE_SECRET_HISTORY_LIMIT", error.Code);
        Assert.Equal(1, owner.ReadConfiguration().Revision);
        owner.AppendLog(LogStream.Stdout, "/0/0/ /31/63/");
        Assert.Equal("[REDACTED] [REDACTED]", Assert.Single(runtime.ReadLogs(resource.ResourceId, 0).Entries).Message);

        static ApplicationModel Declare(int revision)
        {
            var model = new ApplicationModel();
            model.AddResource("service", "example/service");
            for (var parameter = 0; parameter < 64; parameter++)
            {
                model.AddSecretParameter($"password-{parameter}", $"/{revision}/{parameter}/");
            }

            return model;
        }
    }

    [Fact]
    public void DeclaredSecretsCannotLeakThroughObservationUrlsOrCommandMetadata()
    {
        using var model = new ApplicationModel();
        var resource = model.AddResource("service", "example/service");
        model.AddSecretParameter("password", "metadata-secret");
        using var runtime = new RuntimeGeneration(model.Seal());
        using var owner = runtime.ClaimResource(resource);
        Assert.Throws<ArgumentException>(() => owner.Publish(ResourceState.Running, false, ["https://example.test/?token=metadata-secret"]));
        Assert.Throws<ArgumentException>(() => owner.DefineCommand("metadata-secret", "unsafe"));
        owner.DefineCommand("refresh", "Refresh metadata-secret");
        Assert.Equal("Refresh [REDACTED]", Assert.Single(Assert.Single(runtime.Read().Resources).Commands).DisplayName);
    }
}
