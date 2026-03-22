// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "5")]
public class EndpointReferenceTests
{
    [Fact]
    public async Task GetValueAsync_WaitsForEndpointAllocation()
    {
        var resource = new TestResource("test");
        var annotation = new EndpointAnnotation(ProtocolType.Tcp, uriScheme: "http", name: "http");
        resource.Annotations.Add(annotation);

        var endpointRef = new EndpointReference(resource, annotation);

        var getValueTask = endpointRef.GetValueAsync(CancellationToken.None);
        Assert.False(getValueTask.IsCompleted);

        annotation.AllocatedEndpoint = new AllocatedEndpoint(annotation, "localhost", 8080);

        var url = await getValueTask;
        Assert.Equal("http://localhost:8080", url);
    }

    [Fact]
    public async Task GetUrlPropertyValueAsync_WaitsForEndpointAllocation()
    {
        var resource = new TestResource("test");
        var annotation = new EndpointAnnotation(ProtocolType.Tcp, uriScheme: "http", name: "http");
        resource.Annotations.Add(annotation);

        var endpointRef = new EndpointReference(resource, annotation);
        var endpointExpr = endpointRef.Property(EndpointProperty.Url);

        var getValueTask = endpointExpr.GetValueAsync(CancellationToken.None);
        Assert.False(getValueTask.IsCompleted);

        annotation.AllocatedEndpoint = new AllocatedEndpoint(annotation, "localhost", 8080);

        var url = await getValueTask;
        Assert.Equal("http://localhost:8080", url);
    }

    [Fact]
    public async Task GetValueAsync_ReturnsImmediatelyWhenAlreadyAllocated()
    {
        var resource = new TestResource("test");
        var annotation = new EndpointAnnotation(ProtocolType.Tcp, uriScheme: "http", name: "http");
        resource.Annotations.Add(annotation);
        annotation.AllocatedEndpoint = new AllocatedEndpoint(annotation, "localhost", 8080);

        var endpointRef = new EndpointReference(resource, annotation);
        var endpointExpr = endpointRef.Property(EndpointProperty.Url);

        var url = await endpointExpr.GetValueAsync(CancellationToken.None);
        Assert.Equal("http://localhost:8080", url);
    }

    [Fact]
    public async Task GetValueAsync_Host_WaitsForAllocation()
    {
        var resource = new TestResource("test");
        var annotation = new EndpointAnnotation(ProtocolType.Tcp, uriScheme: "http", name: "http");
        resource.Annotations.Add(annotation);

        var endpointRef = new EndpointReference(resource, annotation);
        var hostExpr = endpointRef.Property(EndpointProperty.Host);

        var getValueTask = hostExpr.GetValueAsync(CancellationToken.None);
        Assert.False(getValueTask.IsCompleted);

        annotation.AllocatedEndpoint = new AllocatedEndpoint(annotation, "192.168.1.100", 8080);

        var host = await getValueTask;
        Assert.Equal("192.168.1.100", host);
    }

    [Fact]
    public async Task GetValueAsync_Port_WaitsForAllocation()
    {
        var resource = new TestResource("test");
        var annotation = new EndpointAnnotation(ProtocolType.Tcp, uriScheme: "http", name: "http");
        resource.Annotations.Add(annotation);

        var endpointRef = new EndpointReference(resource, annotation);
        var portExpr = endpointRef.Property(EndpointProperty.Port);

        var getValueTask = portExpr.GetValueAsync(CancellationToken.None);
        Assert.False(getValueTask.IsCompleted);

        annotation.AllocatedEndpoint = new AllocatedEndpoint(annotation, "localhost", 9090);

        var port = await getValueTask;
        Assert.Equal("9090", port);
    }

    [Fact]
    public async Task GetValueAsync_Scheme_ReturnsImmediately()
    {
        var resource = new TestResource("test");
        var annotation = new EndpointAnnotation(ProtocolType.Tcp, uriScheme: "https", name: "https");
        resource.Annotations.Add(annotation);

        var endpointRef = new EndpointReference(resource, annotation);
        var schemeExpr = endpointRef.Property(EndpointProperty.Scheme);

        var scheme = await schemeExpr.GetValueAsync(CancellationToken.None);
        Assert.Equal("https", scheme);
    }

    [Fact]
    public async Task GetValueAsync_IPV4Host_ReturnsImmediately()
    {
        var resource = new TestResource("test");
        var annotation = new EndpointAnnotation(ProtocolType.Tcp, uriScheme: "http", name: "http");
        resource.Annotations.Add(annotation);

        var endpointRef = new EndpointReference(resource, annotation);
        var ipv4Expr = endpointRef.Property(EndpointProperty.IPV4Host);

        var ipv4 = await ipv4Expr.GetValueAsync(CancellationToken.None);
        Assert.Equal("127.0.0.1", ipv4);
    }

    [Fact]
    public async Task GetValueAsync_TargetPort_WithStaticPort_ReturnsImmediately()
    {
        var resource = new TestResource("test");
        var annotation = new EndpointAnnotation(ProtocolType.Tcp, uriScheme: "http", name: "http", targetPort: 5000);
        resource.Annotations.Add(annotation);

        var endpointRef = new EndpointReference(resource, annotation);
        var targetPortExpr = endpointRef.Property(EndpointProperty.TargetPort);

        var targetPort = await targetPortExpr.GetValueAsync(CancellationToken.None);
        Assert.Equal("5000", targetPort);
    }

    [Fact]
    public async Task GetValueAsync_TargetPort_WithExpression_WaitsForAllocation()
    {
        var resource = new TestResource("test");
        var annotation = new EndpointAnnotation(ProtocolType.Tcp, uriScheme: "http", name: "http");
        resource.Annotations.Add(annotation);

        var endpointRef = new EndpointReference(resource, annotation);
        var targetPortExpr = endpointRef.Property(EndpointProperty.TargetPort);

        var getValueTask = targetPortExpr.GetValueAsync(CancellationToken.None);
        Assert.False(getValueTask.IsCompleted);

        annotation.AllocatedEndpoint = new AllocatedEndpoint(annotation, "localhost", 8080, targetPortExpression: "5000");

        var targetPort = await getValueTask;
        Assert.Equal("5000", targetPort);
    }

    [Fact]
    public async Task GetValueAsync_SupportsCancellation()
    {
        var resource = new TestResource("test");
        var annotation = new EndpointAnnotation(ProtocolType.Tcp, uriScheme: "http", name: "http");
        resource.Annotations.Add(annotation);

        var endpointRef = new EndpointReference(resource, annotation);
        var endpointExpr = endpointRef.Property(EndpointProperty.Url);

        using var cts = new CancellationTokenSource();
        var getValueTask = endpointExpr.GetValueAsync(cts.Token);

        cts.Cancel();

        await Assert.ThrowsAsync<TaskCanceledException>(async () => await getValueTask);
    }

    [Fact]
    public async Task GetValueAsync_MultipleWaiters_AllCompleteWhenAllocated()
    {
        var resource = new TestResource("test");
        var annotation = new EndpointAnnotation(ProtocolType.Tcp, uriScheme: "http", name: "http");
        resource.Annotations.Add(annotation);

        var endpointRef = new EndpointReference(resource, annotation);
        var expr1 = endpointRef.Property(EndpointProperty.Url);
        var expr2 = endpointRef.Property(EndpointProperty.Host);
        var expr3 = endpointRef.Property(EndpointProperty.Port);

        var task1 = expr1.GetValueAsync(CancellationToken.None);
        var task2 = expr2.GetValueAsync(CancellationToken.None);
        var task3 = expr3.GetValueAsync(CancellationToken.None);

        Assert.False(task1.IsCompleted);
        Assert.False(task2.IsCompleted);
        Assert.False(task3.IsCompleted);

        annotation.AllocatedEndpoint = new AllocatedEndpoint(annotation, "localhost", 8080);

        var url = await task1;
        var host = await task2;
        var port = await task3;

        Assert.Equal("http://localhost:8080", url);
        Assert.Equal("localhost", host);
        Assert.Equal("8080", port);
    }

    [Fact]
    public void Port_ThrowsWhenEndpointNotAllocated()
    {
        var resource = new TestResource("test");
        var annotation = new EndpointAnnotation(ProtocolType.Tcp, uriScheme: "http", name: "http");
        resource.Annotations.Add(annotation);

        var endpointRef = new EndpointReference(resource, annotation);

        var ex = Assert.Throws<InvalidOperationException>(() => endpointRef.Port);
        Assert.Equal("The endpoint `http` is not allocated for the resource `test`.", ex.Message);
    }

    [Fact]
    public void Host_ThrowsWhenEndpointNotAllocated()
    {
        var resource = new TestResource("test");
        var annotation = new EndpointAnnotation(ProtocolType.Tcp, uriScheme: "http", name: "http");
        resource.Annotations.Add(annotation);

        var endpointRef = new EndpointReference(resource, annotation);

        var ex = Assert.Throws<InvalidOperationException>(() => endpointRef.Host);
        Assert.Equal("The endpoint `http` is not allocated for the resource `test`.", ex.Message);
    }

    [Fact]
    public void Url_ThrowsWhenEndpointNotAllocated()
    {
        var resource = new TestResource("test");
        var annotation = new EndpointAnnotation(ProtocolType.Tcp, uriScheme: "http", name: "http");
        resource.Annotations.Add(annotation);

        var endpointRef = new EndpointReference(resource, annotation);

        var ex = Assert.Throws<InvalidOperationException>(() => endpointRef.Url);
        Assert.Equal("The endpoint `http` is not allocated for the resource `test`.", ex.Message);
    }

    [Fact]
    public void Scheme_DoesNotThrowWhenEndpointNotAllocated()
    {
        var resource = new TestResource("test");
        var annotation = new EndpointAnnotation(ProtocolType.Tcp, uriScheme: "https", name: "https");
        resource.Annotations.Add(annotation);

        var endpointRef = new EndpointReference(resource, annotation);

        var scheme = endpointRef.Scheme;
        Assert.Equal("https", scheme);
    }

    [Fact]
    public void TargetPort_DoesNotThrowWhenStaticPortDefined()
    {
        var resource = new TestResource("test");
        var annotation = new EndpointAnnotation(ProtocolType.Tcp, uriScheme: "http", name: "http", targetPort: 5000);
        resource.Annotations.Add(annotation);

        var endpointRef = new EndpointReference(resource, annotation);

        var targetPort = endpointRef.TargetPort;
        Assert.Equal(5000, targetPort);
    }

    [Fact]
    public void TargetPort_ReturnsNullWhenNotDefined()
    {
        var resource = new TestResource("test");
        var annotation = new EndpointAnnotation(ProtocolType.Tcp, uriScheme: "http", name: "http");
        resource.Annotations.Add(annotation);

        var endpointRef = new EndpointReference(resource, annotation);

        var targetPort = endpointRef.TargetPort;
        Assert.Null(targetPort);
    }

    [Fact]
    public void AllocatedEndpoint_ThrowsWhenNetworkIdDoesNotMatch()
    {
        var annotation = new EndpointAnnotation(ProtocolType.Tcp, KnownNetworkIdentifiers.LocalhostNetwork, uriScheme: "http", name: "http");

        // Create an AllocatedEndpoint with a different network ID.
        var mismatchedEndpoint = new AllocatedEndpoint(
            annotation, "localhost", 8080,
            EndpointBindingMode.SingleAddress,
            targetPortExpression: null,
            networkID: KnownNetworkIdentifiers.DefaultAspireContainerNetwork);

        var ex = Assert.Throws<InvalidOperationException>(() => annotation.AllocatedEndpoint = mismatchedEndpoint);
    }

    [Theory]
    [InlineData(EndpointProperty.Url, ResourceKind.Host, ResourceKind.Host, "blah://localhost:1234")]
    [InlineData(EndpointProperty.Url, ResourceKind.Host, ResourceKind.Container, "blah://localhost:1234")]
    [InlineData(EndpointProperty.Url, ResourceKind.Container, ResourceKind.Host, "blah://host.docker.internal:1234")]
    [InlineData(EndpointProperty.Url, ResourceKind.Container, ResourceKind.Container, "blah://destination.dev.internal:4567")]
    [InlineData(EndpointProperty.Host, ResourceKind.Host, ResourceKind.Host, "localhost")]
    [InlineData(EndpointProperty.Host, ResourceKind.Host, ResourceKind.Container, "localhost")]
    [InlineData(EndpointProperty.Host, ResourceKind.Container, ResourceKind.Host, "host.docker.internal")]
    [InlineData(EndpointProperty.Host, ResourceKind.Container, ResourceKind.Container, "destination.dev.internal")]
    [InlineData(EndpointProperty.IPV4Host, ResourceKind.Host, ResourceKind.Host, "127.0.0.1")]
    [InlineData(EndpointProperty.IPV4Host, ResourceKind.Host, ResourceKind.Container, "127.0.0.1")]
    [InlineData(EndpointProperty.IPV4Host, ResourceKind.Container, ResourceKind.Host, "host.docker.internal")]
    [InlineData(EndpointProperty.IPV4Host, ResourceKind.Container, ResourceKind.Container, "destination.dev.internal")]
    [InlineData(EndpointProperty.Port, ResourceKind.Host, ResourceKind.Host, "1234")]
    [InlineData(EndpointProperty.Port, ResourceKind.Host, ResourceKind.Container, "1234")]
    [InlineData(EndpointProperty.Port, ResourceKind.Container, ResourceKind.Host, "1234")]
    [InlineData(EndpointProperty.Port, ResourceKind.Container, ResourceKind.Container, "4567")]
    [InlineData(EndpointProperty.Scheme, ResourceKind.Host, ResourceKind.Host, "blah")]
    [InlineData(EndpointProperty.Scheme, ResourceKind.Host, ResourceKind.Container, "blah")]
    [InlineData(EndpointProperty.Scheme, ResourceKind.Container, ResourceKind.Host, "blah")]
    [InlineData(EndpointProperty.Scheme, ResourceKind.Container, ResourceKind.Container, "blah")]
    [InlineData(EndpointProperty.HostAndPort, ResourceKind.Host, ResourceKind.Host, "localhost:1234")]
    [InlineData(EndpointProperty.HostAndPort, ResourceKind.Host, ResourceKind.Container, "localhost:1234")]
    [InlineData(EndpointProperty.HostAndPort, ResourceKind.Container, ResourceKind.Host, "host.docker.internal:1234")]
    [InlineData(EndpointProperty.HostAndPort, ResourceKind.Container, ResourceKind.Container, "destination.dev.internal:4567")]
    public async Task PropertyResolutionTest(EndpointProperty property, ResourceKind sourceKind, ResourceKind destinationKind, object expectedResult)
    {
        int port = 1234;
        int targetPort = 4567;

        var source = CreateResource("caller", sourceKind);
        var destination = CreateResource("destination", destinationKind);

        var network = source.GetDefaultResourceNetwork();

        // This logic is tightly coupled to how `DcpExecutor` allocates endpoints
        var annotation = new EndpointAnnotation(ProtocolType.Tcp, uriScheme: "blah", name: "http");
        annotation.AllocatedEndpoint = new(annotation, "localhost", port);
        destination.Annotations.Add(annotation);

        (string containerHost, int containerPort) = destination.IsContainer()
            ? ("destination.dev.internal", targetPort)
            : ("host.docker.internal", port);

        var containerEndpoint = new AllocatedEndpoint(annotation, containerHost, containerPort, EndpointBindingMode.SingleAddress, targetPortExpression: targetPort.ToString(), KnownNetworkIdentifiers.DefaultAspireContainerNetwork);
        annotation.AllAllocatedEndpoints.AddOrUpdateAllocatedEndpoint(KnownNetworkIdentifiers.DefaultAspireContainerNetwork, containerEndpoint);

        var expression = destination.GetEndpoint(annotation.Name).Property(property);

        var resultFromCaller = await expression.GetValueAsync(new ValueProviderContext
        {
            Caller = source
        });
        Assert.Equal(expectedResult, resultFromCaller);

        var resultFromNetwork = await expression.GetValueAsync(new ValueProviderContext
        {
            Network = network
        });
        Assert.Equal(expectedResult, resultFromNetwork);

        static IResourceWithEndpoints CreateResource(string name, ResourceKind kind)
        {
            if (kind == ResourceKind.Container)
            {
                var resource = new TestResource(name);
                resource.Annotations.Add(new ContainerImageAnnotation { Image = "test-image" });
                return resource;
            }
            else
            {
                return new TestResource(name);
            }
        }
    }

    [Fact]
    public async Task WaitingForAllocatedEndpointWorks()
    {
        var resource = new TestResource("test");
        var annotation = new EndpointAnnotation(ProtocolType.Tcp, uriScheme: "http", name: "http");
        resource.Annotations.Add(annotation);
        var endpointRef = new EndpointReference(resource, annotation);

        var waitStarted = new SemaphoreSlim(0, 1);

#pragma warning disable CA2012 // Use ValueTasks correctly
        var consumer = new WithWaitStartedNotification<string?>(waitStarted, endpointRef.GetValueAsync(CancellationToken.None).GetAwaiter());
#pragma warning restore CA2012 // Use ValueTasks correctly

        await Task.WhenAll
        (
            Task.Run(async() =>
            {
                await waitStarted.WaitAsync();
                var allocatedEndpoint = new AllocatedEndpoint(annotation, "localhost", 5000);
                annotation.AllAllocatedEndpoints.AddOrUpdateAllocatedEndpoint(KnownNetworkIdentifiers.LocalhostNetwork, allocatedEndpoint);
            }),
            Task.Run(async () =>
            {
                var url = await consumer;
                Assert.Equal("http://localhost:5000", url);
            })
        ).WaitAsync(TimeSpan.FromSeconds(10));
    }

    public enum ResourceKind
    {
        Host,
        Container
    }

    private sealed class TestResource(string name) : Resource(name), IResourceWithEndpoints
    {
    }

    private struct WithWaitStartedNotification<T>
    {
        private readonly WaitStartedNotificationAwaiter<T> _awaiter;

        public WithWaitStartedNotification(SemaphoreSlim waitStarted, ValueTaskAwaiter<T> inner)
        {
            _awaiter = new WaitStartedNotificationAwaiter<T>(waitStarted, inner);
        }
        public WaitStartedNotificationAwaiter<T> GetAwaiter() => _awaiter;
    }

    private struct WaitStartedNotificationAwaiter<T>: INotifyCompletion
    {
        private readonly ValueTaskAwaiter<T> _inner;
        private readonly SemaphoreSlim _waitStarted;

        public WaitStartedNotificationAwaiter(SemaphoreSlim waitStarted, ValueTaskAwaiter<T> inner)
        {
            _waitStarted = waitStarted;
            _inner = inner;
        }

        public bool IsCompleted => false; // Force continuation

        public void OnCompleted(Action continuation)
        {
            _waitStarted.Release();
            _inner.OnCompleted(continuation);
        }

        public T GetResult() => _inner.GetResult();
    }
}
