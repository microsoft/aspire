// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Native.Client;
using Aspire.Hosting.Native.Core.Tests.TestServices;

namespace Aspire.Hosting.Native.Core.Tests;

public class NativeSymbolicValueClientTests
{
    [Fact]
    public async Task SocketValuesKeepSecretsAndReferencesOnServerAndRejectCrossCompositionAuthority()
    {
        await using var host = await NativeClientTestHost.StartAsync();
        await using var client = await NativeClient.ConnectAsync(host.Endpoint, host.Token, TimeSpan.FromSeconds(10), TimeProvider.System);
        var session = await client.Server.OpenApplicationAsync();
        var composition = await session.StartGenerationAsync();
        var source = await composition.AddResourceAsync("cache", "example/redis");
        var target = await composition.AddResourceAsync("consumer", "example/executable");
        var password = await composition.AddSecretParameterAsync("password", "secret-value");
        var endpoint = await composition.CreateEndpointValueAsync(source, "tcp", "hostAndPort");
        var prefix = await composition.CreateLiteralValueAsync("redis://:");
        var uri = await prefix.ConcatValueAsync(await password.EncodeUriValueAsync());
        uri = await uri.ConcatValueAsync(await composition.CreateLiteralValueAsync("@"));
        uri = await uri.ConcatValueAsync(endpoint);
        await target.SetSymbolicConfigurationAsync("connection", uri);
        var metadata = Assert.Single((await target.InspectSymbolicConfigurationAsync()).Properties);
        Assert.Equal("connection", metadata.Name);
        Assert.True(metadata.IsSecret);
        Assert.Equal(["password"], metadata.ParameterReferences);
        Assert.Equal([(await source.InspectResourceAsync()).ResourceId], metadata.References);
        Assert.Empty((await target.InspectResourceAsync()).Dependencies);
        Assert.Empty((await target.InspectResourceAsync()).Configuration);
        var otherSession = await client.Server.OpenApplicationAsync();
        var other = await otherSession.StartGenerationAsync();
        var otherValue = await other.CreateLiteralValueAsync("other");
        var mismatch = await Assert.ThrowsAsync<NativeCapabilityException>(() => uri.ConcatValueAsync(otherValue));
        Assert.Equal("INVALID_ARGUMENT", mismatch.Code);
        await target.RemoveSymbolicConfigurationAsync("connection");
        Assert.Empty((await target.InspectResourceAsync()).Dependencies);
        await session.RetireGenerationAsync(composition);
        var retired = await Assert.ThrowsAsync<NativeCapabilityException>(() => password.EncodeUriValueAsync());
        Assert.Equal("HANDLE_NOT_FOUND", retired.Code);
        await otherSession.RetireGenerationAsync(other);
    }

    [Fact]
    public async Task WorkspaceValuesRebindRevisionsAndRejectStaleLaunchesWithoutInspectingSecrets()
    {
        await using var host = await NativeClientTestHost.StartAsync();
        await using var client = await NativeClient.ConnectAsync(host.Endpoint, host.Token, TimeSpan.FromSeconds(10), TimeProvider.System);
        var workspace = await client.Server.CreateApplicationWorkspaceAsync();
        var first = await workspace.BeginRevisionAsync();
        var declaration = await first.AddResourceAsync("service", "example/service");
        var original = await first.AddSecretParameterAsync("password", "original-secret");
        await declaration.SetSymbolicConfigurationAsync("password", original);
        await workspace.CommitRevisionAsync(first);
        var execution = await workspace.GetApplicationExecutionAsync();
        var owner = await client.Server.JoinResourceExecutionAsync(await execution.InviteResourceExecutionAsync(declaration));
        var observer = await client.Server.JoinApplicationObserverAsync(await execution.InviteApplicationObserverAsync());
        await owner.CompleteResourceConfigurationAsync(1, new RuntimeOperationResult { Status = "succeeded", Message = "original-secret" });
        Assert.Equal("[REDACTED]", (await owner.ReadResourceConfigurationAsync()).Message);
        var revision = await workspace.BeginRevisionAsync();
        var updated = await revision.AddResourceAsync("service", "example/service");
        await updated.SetSymbolicConfigurationAsync("password", await revision.AddSecretParameterAsync("password", "updated-secret"));
        await workspace.CommitRevisionAsync(revision);
        var configuration = await owner.ReadResourceConfigurationAsync();
        Assert.Equal(2, configuration.Revision);
        Assert.Equal("pending", configuration.Status);
        Assert.Empty(configuration.Properties);
        Assert.True(Assert.Single((await owner.InspectExecutionSymbolicConfigurationAsync()).Properties).IsSecret);
        var stale = await Assert.ThrowsAsync<NativeCapabilityException>(() => owner.StartContainerWithValuesAsync(1, "container",
            new ContainerWorkloadLaunch { Image = "redis:8", Entrypoint = "", Mounts = [], Environment = [], Arguments = [], Endpoints = [] },
            new SymbolicLaunchBindings { Environment = [new SymbolicLaunchEnvironment { Name = "PASSWORD", ConfigurationKey = "password" }], Arguments = [] }));
        Assert.Equal("VALUE_REVISION_STALE", stale.Code);
        var revoked = await Assert.ThrowsAsync<NativeCapabilityException>(() => original.EncodeUriValueAsync());
        Assert.Equal("HANDLE_NOT_FOUND", revoked.Code);
        await owner.AppendResourceLogAsync("stdout", "original-secret updated-secret");
        var resourceId = Assert.Single((await observer.ReadResourceObservationsAsync()).Resources).ResourceId;
        Assert.Equal("[REDACTED] [REDACTED]", Assert.Single((await observer.ReadResourceLogsAsync(resourceId, 0)).Entries).Message);
        await workspace.RetireApplicationWorkspaceAsync();
    }

    [Fact]
    public async Task SymbolicContainerEndpointReferencesHaveAnExplicitTypedUnsupportedFailure()
    {
        await using var host = await NativeClientTestHost.StartAsync();
        await using var client = await NativeClient.ConnectAsync(host.Endpoint, host.Token, TimeSpan.FromSeconds(10), TimeProvider.System);
        var session = await client.Server.OpenApplicationAsync();
        var composition = await session.StartGenerationAsync();
        var source = await composition.AddResourceAsync("source", "example/source");
        var target = await composition.AddResourceAsync("target", "example/target");
        await target.SetSymbolicConfigurationAsync("endpoint", await composition.CreateEndpointValueAsync(source, "tcp", "host"));
        var execution = await composition.CreateExecutionAsync();
        var owner = await client.Server.JoinResourceExecutionAsync(await execution.InviteResourceExecutionAsync(target));
        var error = await Assert.ThrowsAsync<NativeCapabilityException>(() => owner.StartContainerWithValuesAsync(1, "container",
            new ContainerWorkloadLaunch { Image = "redis:8", Entrypoint = "", Mounts = [], Environment = [], Arguments = [], Endpoints = [] },
            new SymbolicLaunchBindings { Environment = [new SymbolicLaunchEnvironment { Name = "ENDPOINT", ConfigurationKey = "endpoint" }], Arguments = [] }));
        Assert.Equal("VALUE_NETWORK_CONTEXT_UNSUPPORTED", error.Code);
        await session.RetireGenerationAsync(composition);
    }
}
