using Xunit;
using Aspire.Hosting.Testing;
using Aspire.Hosting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Aspire.Hosting.ApplicationModel;

namespace Testing.Tests;

// These tests cover various scenarios in which a wait operation fails to see how helpful the
// resulting errors / logs are
public class WaitFailures
{
    private static CancellationTokenSource DefaultCancellationTokenSource()
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(1));
        return cts;
    }

    public class WaitForExplicitStartResources
    {
        [Fact]
        public async Task Container()
        {
            var cts = DefaultCancellationTokenSource();
            await using var builder = DistributedApplicationTestingBuilder.Create();

            var nginx = builder.AddContainer("nginx", "nginx", "")
                .WithExplicitStart();

            await using var app = builder.Build();
            await app.StartAsync(cts.Token);

            await app.ResourceNotifications.WaitForResourceAsync(nginx.Resource.Name, cancellationToken: cts.Token);
        }

        [Fact]
        public async Task Executable()
        {
            var cts = DefaultCancellationTokenSource();
            await using var builder = DistributedApplicationTestingBuilder.Create();

            var pwsh = builder.AddExecutable("pwsh", "pwsh", "")
                .WithExplicitStart();

            await using var app = builder.Build();
            await app.StartAsync(cts.Token);

            await app.ResourceNotifications.WaitForResourceAsync(pwsh.Resource.Name, cancellationToken: cts.Token);
        }
    }

    public class WaitForResource
    {
        [Fact]
        public async Task WaitForResourceThatNeverCompletes()
        {
            var cts = DefaultCancellationTokenSource();
            await using var builder = DistributedApplicationTestingBuilder.Create();

            var nginx = builder.AddContainer("nginx", "nginx");

            await using var app = builder.Build();
            await app.StartAsync(cts.Token);

            _ = Task.Run(async () =>
            {
                await app.ResourceNotifications.WaitForResourceAsync(nginx.Resource.Name, cancellationToken: cts.Token);
                cts.Cancel();
            });

            await app.ResourceNotifications.WaitForResourceAsync(nginx.Resource.Name, KnownResourceStates.TerminalStates, cancellationToken: cts.Token);
        }

        [Fact]
        public async Task WaitForResourceThatNeverHitsState()
        {
            var cts = DefaultCancellationTokenSource();
            await using var builder = DistributedApplicationTestingBuilder.Create();

            var nginx = builder.AddContainer("nginx", "nginx");

            await using var app = builder.Build();
            await app.StartAsync(cts.Token);

            cts.CancelAfter(TimeSpan.FromMilliseconds(100));
            await app.ResourceNotifications.WaitForResourceAsync(nginx.Resource.Name, "StateThatIsNeverUsed", cts.Token);
        }

        [Fact]
        public async Task WaitForResourceThatNeverHitsStates()
        {
            var cts = DefaultCancellationTokenSource();
            await using var builder = DistributedApplicationTestingBuilder.Create();

            var nginx = builder.AddContainer("nginx", "nginx");

            await using var app = builder.Build();
            await app.StartAsync(cts.Token);

            cts.CancelAfter(TimeSpan.FromMilliseconds(100));
            await app.ResourceNotifications.WaitForResourceAsync(nginx.Resource.Name, ["States", "That", "Are", "Never", "Used"], cts.Token);
        }

        [Fact]
        public async Task WaitForResourceThatNeverHitsPredicate()
        {
            var cts = DefaultCancellationTokenSource();
            await using var builder = DistributedApplicationTestingBuilder.Create();

            var nginx = builder.AddContainer("nginx", "nginx");

            await using var app = builder.Build();
            await app.StartAsync(cts.Token);

            cts.CancelAfter(TimeSpan.FromMilliseconds(100));
            await app.ResourceNotifications.WaitForResourceAsync(nginx.Resource.Name, x => false, cts.Token);
        }
    }

    public class WaitForResourceHealthyAsync
    {
        [Fact]
        public async Task WaitForHealthyOnResourceThatNeverStarts()
        {
            var cts = DefaultCancellationTokenSource();
            await using var builder = DistributedApplicationTestingBuilder.Create();

            var nginx = builder.AddContainer("nginx", "nginx")
                .WithExplicitStart();

            await using var app = builder.Build();
            await app.StartAsync(cts.Token);

            cts.CancelAfter(TimeSpan.FromMilliseconds(100));
            await app.ResourceNotifications.WaitForResourceHealthyAsync(nginx.Resource.Name, cts.Token);
        }

        [Fact]
        public async Task WaitForHealthyOnResourceThatStartsButNeverGoesHealthy()
        {
            var cts = DefaultCancellationTokenSource();
            await using var builder = DistributedApplicationTestingBuilder.Create();

            var nginx = builder.AddContainer("nginx", "nginx")
                .WithHttpEndpoint(targetPort: 80)
                .WithHttpEndpoint(name: "fake", targetPort: 1234)
                .WithHttpHealthCheck("/does-not-exist")
                .WithHttpHealthCheck("/throws-exception", endpointName: "fake");

            await using var app = builder.Build();
            await app.StartAsync(cts.Token);

            _ = Task.Run(async () =>
            {
                await app.ResourceNotifications.WaitForResourceAsync(nginx.Resource.Name, x => x.Snapshot.HealthReports.All(x => x.Status.HasValue), cts.Token);
                cts.Cancel();
            });
            await app.ResourceNotifications.WaitForResourceHealthyAsync(nginx.Resource.Name, cts.Token);
        }

        [Fact]
        public async Task WaitForHealthyOnResourceThatGoesHealthyButNeverCompletesResourceReady()
        {
            var cts = DefaultCancellationTokenSource();
            await using var builder = DistributedApplicationTestingBuilder.Create();

            var nginx = builder.AddContainer("nginx", "nginx")
                .OnResourceReady((_, _, token) => Task.Delay(Timeout.Infinite, token));

            await using var app = builder.Build();
            await app.StartAsync(cts.Token);

            _ = Task.Run(async () =>
            {
                await app.ResourceNotifications.WaitForResourceAsync(nginx.Resource.Name, x => x.Snapshot.HealthStatus == HealthStatus.Healthy, cts.Token);
                cts.Cancel();
            });

            await app.ResourceNotifications.WaitForResourceHealthyAsync(nginx.Resource.Name, cts.Token);
        }
    }

    public class WaitForDependencies
    {
        [Fact]
        public async Task DependencyNeverStarts()
        {
            var cts = DefaultCancellationTokenSource();
            await using var builder = DistributedApplicationTestingBuilder.Create();

            var dependency = builder.AddContainer("dependency", "nginx")
                .WithExplicitStart();

            var consumer = builder.AddContainer("consumer", "nginx")
                .WaitForStart(dependency);

            await using var app = builder.Build();

            // Chicken and egg problem - need `WaitFor` to test `WaitForDependenciesAsync`
            // but adding `WaitFor` causes `StartAsync` to block until all resource dependencies are started
            // But I'm trying to test what happens when the waits are not complete.
            _ = app.StartAsync(cts.Token);

            cts.CancelAfter(TimeSpan.FromMilliseconds(100));
            await app.ResourceNotifications.WaitForDependenciesAsync(consumer.Resource, cts.Token);
        }

        [Fact]
        public async Task DependencyNeverGoesHealthy()
        {
            var cts = DefaultCancellationTokenSource();

            await using var builder = DistributedApplicationTestingBuilder.Create();

            var dependency = builder.AddContainer("dependency", "nginx")
                .WithHttpEndpoint(targetPort: 80)
                .WithHttpHealthCheck("/does-not-exist");

            var consumer = builder.AddContainer("consumer", "nginx")
                .WaitFor(dependency);

            await using var app = builder.Build();
            _ = app.StartAsync(cts.Token);

            _ = Task.Run(async () =>
            {
                await app.ResourceNotifications.WaitForResourceAsync(dependency.Resource.Name, cancellationToken: cts.Token);
                cts.Cancel();
            });

            await app.ResourceNotifications.WaitForDependenciesAsync(consumer.Resource, cts.Token);
        }

        [Fact]
        public async Task DependencyNeverCompletes()
        {
            var cts = DefaultCancellationTokenSource();

            await using var builder = DistributedApplicationTestingBuilder.Create();

            var dependency = builder.AddContainer("dependency", "nginx");

            var consumer = builder.AddContainer("consumer", "nginx")
                .WaitForCompletion(dependency);

            await using var app = builder.Build();
            _ = app.StartAsync(cts.Token);

            _ = Task.Run(async () =>
            {
                await app.ResourceNotifications.WaitForResourceAsync(dependency.Resource.Name, cancellationToken: cts.Token);
                cts.Cancel();
            });

            await app.ResourceNotifications.WaitForDependenciesAsync(consumer.Resource, cancellationToken: cts.Token);
        }
    }
}