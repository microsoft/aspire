// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Aspire.Hosting.Native.Client;
using Aspire.Hosting.Native.Core.Tests.TestServices;
using Aspire.Hosting.RemoteHost;
using Microsoft.Extensions.Time.Testing;

namespace Aspire.Hosting.Native.Core.Tests;

public class NativeCSharpClientTests
{
    [Fact]
    public void Projection_CoversEveryScannedCapabilityAndDtoWithoutRuntimeDependencies()
    {
        var contract = AtsCapabilityScanner.ScanAssemblies([typeof(Api.CompositionSession).Assembly]).ToAtsContext();
        var clientAssembly = typeof(NativeClient).Assembly;
        foreach (var capability in contract.Capabilities)
        {
            var source = contract.Methods[capability.CapabilityId];
            var projectedType = source.IsStatic ? typeof(NativeFactories) : ProjectType(source.DeclaringType!);
            var projectedMethod = projectedType.GetMethod(source.Name + "Async")!;
            Assert.NotNull(projectedMethod);
            var expected = source.GetParameters().Select(parameter => ProjectType(parameter.ParameterType));
            if (source.IsStatic)
            {
                expected = new[] { typeof(NativeClient) }.Concat(expected);
            }
            Assert.Equal(expected.Append(typeof(CancellationToken)), projectedMethod.GetParameters().Select(parameter => parameter.ParameterType));
            var sourceReturn = source.ReturnType == typeof(Task) ? typeof(void) :
                source.ReturnType.IsGenericType && source.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)
                    ? source.ReturnType.GetGenericArguments()[0] : source.ReturnType;
            Assert.Equal(sourceReturn == typeof(void) ? typeof(Task) : typeof(Task<>).MakeGenericType(ProjectType(sourceReturn)),
                projectedMethod.ReturnType);
        }
        foreach (var dto in contract.DtoTypes)
        {
            var source = dto.ClrType!;
            var projected = ProjectType(source);
            Assert.Equal(source.GetProperties().Select(property => (property.Name, ProjectType(property.PropertyType))),
                projected.GetProperties().Select(property => (property.Name, property.PropertyType)));
        }
        Assert.All(clientAssembly.GetReferencedAssemblies(), assembly =>
        {
            Assert.False(assembly.Name is "Aspire.Hosting" or "Aspire.Hosting.Native.Server" or "Aspire.Hosting.Native.Core" or
                "Aspire.Hosting.Native.CodeGeneration" or "Aspire.TypeSystem" or "Aspire.Hosting.Ats.Abstractions");
        });
        Assert.Equal(contract.Capabilities.Count,
            clientAssembly.GetTypes().Where(type => type.IsSubclassOf(typeof(NativeRemoteObject)) || type == typeof(NativeFactories))
                .Sum(type => type.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Count(method => method.Name.EndsWith("Async", StringComparison.Ordinal) && type != typeof(NativeClient))));

        Type ProjectType(Type source) => source.IsArray ? ProjectType(source.GetElementType()!).MakeArrayType() :
            source.IsGenericType ? source.GetGenericTypeDefinition().MakeGenericType(source.GetGenericArguments().Select(ProjectType).ToArray()) :
            source.Assembly == typeof(Api.CompositionSession).Assembly
                ? clientAssembly.GetType($"Aspire.Hosting.Native.Client.{source.Name}", throwOnError: true)! : source;
    }

    [Fact]
    public async Task SocketClient_ComposesConfigurationsAndRejectsRetiredCapabilities()
    {
        await using var host = await NativeClientTestHost.StartAsync();
        await using var client = await NativeClient.ConnectAsync(host.Endpoint, host.Token, TimeSpan.FromSeconds(10), TimeProvider.System);
        var session = await client.Server.OpenApplicationAsync();
        var composition = await session.StartGenerationAsync();
        var service = await composition.AddResourceAsync("cache", "example/cache");
        var consumer = await composition.AddResourceAsync("consumer", "example/workload");
        await consumer.WaitForAsync(service);
        await service.SetResourceConfigurationAsync(new ResourceConfiguration
        {
            Properties = [new ResourceConfigurationProperty { Name = "image", Value = "redis:8" }]
        });
        var snapshot = await composition.InspectAsync();
        Assert.Collection(snapshot.Resources,
            cache =>
            {
                Assert.Equal("cache", cache.Name);
                Assert.Equal("example/cache", cache.TypeId);
                Assert.Empty(cache.Dependencies);
                Assert.Collection(cache.Configuration, property =>
                {
                    Assert.Equal("image", property.Name);
                    Assert.Equal("redis:8", property.Value);
                });
            },
            app => Assert.Equal([snapshot.Resources[0].ResourceId], app.Dependencies));
        await session.RetireGenerationAsync(composition);
        var error = await Assert.ThrowsAsync<NativeCapabilityException>(() => composition.InspectAsync());
        Assert.Equal("HANDLE_NOT_FOUND", error.Code);
    }

    [Fact]
    public async Task SocketClient_ProjectsResourceScopedLogsObservationsAndFailureResults()
    {
        await using var host = await NativeClientTestHost.StartAsync();
        await using var client = await NativeClient.ConnectAsync(host.Endpoint, host.Token, TimeSpan.FromSeconds(10), TimeProvider.System);
        var session = await client.Server.OpenApplicationAsync();
        var composition = await session.StartGenerationAsync();
        var declaration = await composition.AddResourceAsync("service", "example/service");
        var execution = await composition.CreateExecutionAsync();
        var resource = await client.Server.JoinResourceExecutionAsync(await execution.InviteResourceExecutionAsync(declaration));
        var observer = await client.Server.JoinApplicationObserverAsync(await execution.InviteApplicationObserverAsync());
        var configuration = await resource.ReadResourceConfigurationAsync();
        Assert.Equal(configuration.Revision, configuration.AppliedRevision);
        var completed = await Assert.ThrowsAsync<NativeCapabilityException>(() =>
            resource.CompleteResourceConfigurationAsync(configuration.Revision,
                new RuntimeOperationResult { Status = "succeeded", Message = "Configured" }));
        Assert.Equal("OPERATION_REJECTED", completed.Code);
        await resource.AppendResourceLogAsync("stdout", "Projection protocol exercise");
        await resource.PublishObservationAsync(new ResourceObservation { State = "Running", Healthy = true, Urls = [] });
        var observed = await observer.ReadResourceObservationsAsync();
        Assert.Collection(observed.Resources, item =>
        {
            Assert.Equal("service", item.Name);
            Assert.Equal("Running", item.State);
            Assert.True(item.Healthy);
        });
        var logs = await observer.ReadResourceLogsAsync((await declaration.InspectResourceAsync()).ResourceId, 0);
        Assert.Collection(logs.Entries, entry => Assert.Equal("Projection protocol exercise", entry.Message));
        var error = await Assert.ThrowsAsync<NativeCapabilityException>(() => resource.StartContainerWorkloadAsync(new ContainerWorkloadLaunch
        {
            Image = "redis:8", Entrypoint = "", Mounts = [], Arguments = [], Environment = [],
            Endpoints = [new WorkloadEndpointDeclaration
            {
                Name = "tcp", Protocol = "TCP", UriScheme = "redis", TargetPort = 6379, HostPort = 0, EnvironmentVariable = ""
            }]
        }));
        Assert.Equal("NOT_SUPPORTED", error.Code);
        await session.RetireGenerationAsync(composition);
    }

    [Fact]
    public async Task SocketClient_RejectsAuthenticationAndCrossConnectionHandles()
    {
        await using var host = await NativeClientTestHost.StartAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            NativeClient.ConnectAsync(host.Endpoint, "wrong-token", TimeSpan.FromSeconds(10), TimeProvider.System));
        await using var first = await NativeClient.ConnectAsync(host.Endpoint, host.Token, TimeSpan.FromSeconds(10), TimeProvider.System);
        await using var second = await NativeClient.ConnectAsync(host.Endpoint, host.Token, TimeSpan.FromSeconds(10), TimeProvider.System);
        var firstSession = await first.Server.OpenApplicationAsync();
        var firstComposition = await firstSession.StartGenerationAsync();
        var firstResource = await firstComposition.AddResourceAsync("first", "example/service");
        var secondSession = await second.Server.OpenApplicationAsync();
        var secondComposition = await secondSession.StartGenerationAsync();
        var secondResource = await secondComposition.AddResourceAsync("second", "example/service");
        await Assert.ThrowsAsync<ArgumentException>(() => firstResource.WaitForAsync(secondResource));
        await firstSession.RetireGenerationAsync(firstComposition);
        await secondSession.RetireGenerationAsync(secondComposition);
    }

    [Fact]
    public async Task SocketClient_CallerCancellationDoesNotDeadlockOtherCapabilities()
    {
        await using var host = await NativeClientTestHost.StartAsync();
        await using var client = await NativeClient.ConnectAsync(host.Endpoint, host.Token, TimeSpan.FromSeconds(10), TimeProvider.System);
        var session = await client.Server.OpenApplicationAsync();
        var composition = await session.StartGenerationAsync();
        var execution = await composition.CreateExecutionAsync();
        var observer = await client.Server.JoinApplicationObserverAsync(await execution.InviteApplicationObserverAsync());
        var observed = await observer.ReadResourceObservationsAsync();
        using var cancellation = new CancellationTokenSource();
        var waiting = observer.WaitResourceObservationsAsync(observed.Version, 500, cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Empty((await observer.ReadResourceObservationsAsync()).Resources);
        await session.RetireGenerationAsync(composition);
    }

    [Fact]
    public async Task SocketClient_AuthenticationHasAnIndependentBoundedDeadline()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var clock = new FakeTimeProvider();
        var connecting = NativeClient.ConnectAsync(listener.LocalEndpoint, "test-authentication-token",
            TimeSpan.FromSeconds(10), clock);
        using var accepted = await listener.AcceptSocketAsync().WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.False(connecting.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task SocketClient_CancelledWaitCanBeDisposedBeforeRemoteCompletion()
    {
        await using var host = await NativeClientTestHost.StartAsync();
        await using var client = await NativeClient.ConnectAsync(host.Endpoint, host.Token, TimeSpan.FromSeconds(10), TimeProvider.System);
        var session = await client.Server.OpenApplicationAsync();
        var composition = await session.StartGenerationAsync();
        var execution = await composition.CreateExecutionAsync();
        var observer = await client.Server.JoinApplicationObserverAsync(await execution.InviteApplicationObserverAsync());
        var observed = await observer.ReadResourceObservationsAsync();
        using var cancellation = new CancellationTokenSource();
        var waiting = observer.WaitResourceObservationsAsync(observed.Version, 30000, cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task SocketClient_RejectsNonLocalEndpointsBeforeConnecting()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => NativeClient.ConnectAsync(
            new IPEndPoint(IPAddress.Parse("192.0.2.1"), 1234), "test-token", TimeSpan.FromSeconds(1), TimeProvider.System));
    }

    [Theory]
    [InlineData("""{"$error":null}""")]
    [InlineData("""{"$error":"failure"}""")]
    [InlineData("""{"$error":{}}""")]
    [InlineData("""{"$error":{"code":null,"message":"redacted"}}""")]
    [InlineData("""{"$error":{"code":42,"message":"redacted"}}""")]
    [InlineData("""{"$error":{"code":"","message":"redacted"}}""")]
    [InlineData("""{"$error":{"code":"FAILURE","message":null}}""")]
    [InlineData("""{"$error":{"code":"FAILURE","message":[]}}""")]
    [InlineData("""{"$error":{"code":"FAILURE"}}""")]
    [InlineData("""{"$error":{"code":"FAILURE","message":"redacted","extra":"secret"}}""")]
    [InlineData("""{"$error":{"code":"FAILURE","message":"redacted"},"extra":"secret"}""")]
    [InlineData("""{"$error":{"code":"FAILURE","code":"DUPLICATE","message":"redacted"}}""")]
    public void CapabilityResult_RejectsMalformedFailureWithoutLeakingPayload(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var error = Assert.Throws<InvalidDataException>(() => NativeCapabilityResult.Parse(document.RootElement));
        Assert.Equal("The native capability failure response is malformed.", error.Message);
    }

    [Fact]
    public void CapabilityResult_ParsesValidTypedFailure()
    {
        using var document = JsonDocument.Parse("""{"$error":{"code":"HANDLE_NOT_FOUND","message":"The capability lifetime ended."}}""");
        var error = Assert.Throws<NativeCapabilityException>(() => NativeCapabilityResult.Parse(document.RootElement));
        Assert.Equal("HANDLE_NOT_FOUND", error.Code);
        Assert.Equal("The capability lifetime ended.", error.Message);
    }
}
