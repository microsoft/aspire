// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dashboard;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "5")]
public class WithUrlsTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void WithUrlsAddsAnnotationForAsyncCallback()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        Func<ResourceUrlsCallbackContext, Task> callback = c => Task.CompletedTask;

        var projectA = builder.AddProject<ProjectA>("projecta")
                              .WithUrls(callback);

        var urlsCallback = projectA.Resource.Annotations.OfType<ResourceUrlsCallbackAnnotation>()
            .Where(a => a.Callback == callback).FirstOrDefault();

        Assert.NotNull(urlsCallback);
    }

    [Fact]
    public void WithUrlsAddsAnnotationForSyncCallback()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var projectA = builder.AddProject<ProjectA>("projecta");

        Assert.Empty(projectA.Resource.Annotations.OfType<ResourceUrlsCallbackAnnotation>());

        projectA.WithUrls(c => { });

        Assert.NotEmpty(projectA.Resource.Annotations.OfType<ResourceUrlsCallbackAnnotation>());
    }

    [Fact]
    public async Task WithUrlsCallsCallbackAfterBeforeResourceStartedEvent()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var called = false;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        builder.AddProject<ProjectA>("projecta")
            .WithUrls(c => called = true)
            .OnResourceEndpointsAllocated((_, _, _) =>
            {
                // Should not be called at this point
                Assert.False(called);
                return Task.CompletedTask;
            })
            .OnBeforeResourceStarted((_, _, _) =>
            {
                // Should be called by the time resource is started
                Assert.True(called);
                tcs.SetResult();
                return Task.CompletedTask;
            });

        await using var app = await builder.BuildAsync();
        await app.StartAsync();

        await tcs.Task.DefaultTimeout();

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task WithUrlsProvidesLoggerInstanceOnCallbackContextAllocated()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        ILogger logger = NullLogger.Instance;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var projectA = builder.AddProject<ProjectA>("projecta")
            .WithUrls(c => logger = c.Logger)
            .OnBeforeResourceStarted((_, _, _) =>
            {
                tcs.SetResult();
                return Task.CompletedTask;
            });

        await using var app = await builder.BuildAsync();
        await app.StartAsync();

        await tcs.Task.DefaultTimeout();

        Assert.NotNull(logger);
        Assert.True(logger is not NullLogger);

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task WithUrlsProvidesServiceProviderInstanceOnCallbackContextAllocated()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var tcs = new TaskCompletionSource<IServiceProvider>(TaskCreationOptions.RunContinuationsAsynchronously);

        var projectA = builder.AddProject<ProjectA>("projecta")
            .WithUrls(c =>
            {
                try
                {
                    tcs.TrySetResult(c.ExecutionContext.ServiceProvider);
                }
                catch (InvalidOperationException ex)
                {
                    tcs.TrySetException(ex);
                }
            });

        await using var app = await builder.BuildAsync();

        await app.StartAsync();

        Assert.NotNull(await tcs.Task.DefaultTimeout());

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task WithUrlsAddsUrlAnnotations()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var projectA = builder.AddProject<ProjectA>("projecta")
            .WithUrls(c => c.Urls.Add(new() { Url = "https://example.com", DisplayText = "Example" }))
            .OnBeforeResourceStarted((_, _, _) =>
            {
                tcs.SetResult();
                return Task.CompletedTask;
            });

        await using var app = await builder.BuildAsync();
        await app.StartAsync();
        await tcs.Task.DefaultTimeout();

        var urls = projectA.Resource.Annotations.OfType<ResourceUrlAnnotation>();
        Assert.Single(urls, u => u.Url == "https://example.com" && u.DisplayText == "Example");

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task WithUrlAddsUrlAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var projectA = builder.AddProject<ProjectA>("projecta")
            .WithUrl("https://example.com", "Example")
            .OnBeforeResourceStarted((_, _, _) =>
        {
            tcs.SetResult();
            return Task.CompletedTask;
        });

        await using var app = await builder.BuildAsync();
        await app.StartAsync();
        await tcs.Task.DefaultTimeout();

        var urls = projectA.Resource.Annotations.OfType<ResourceUrlAnnotation>();
        Assert.Single(urls, u => u.Url == "https://example.com" && u.DisplayText == "Example");

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task WithUrlInterpolatedStringAddsUrlAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var projectA = builder.AddProject<ProjectA>("projecta")
            .WithHttpsEndpoint();

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        projectA.WithUrl($"{projectA.Resource.GetEndpoint("https")}/test", "Example")
            .OnBeforeResourceStarted((_, _, _) =>
            {
                tcs.SetResult();
                return Task.CompletedTask;
            });

        await using var app = await builder.BuildAsync();
        await app.StartAsync();
        await tcs.Task.DefaultTimeout();

        var urls = projectA.Resource.Annotations.OfType<ResourceUrlAnnotation>();
        var endpointUrl = urls.First(u => u.Endpoint is not null);
        Assert.Collection(urls,
            u => Assert.True(u.Url == endpointUrl.Url && u.DisplayText is null),
            u => Assert.True(u.Url.EndsWith("/test") && u.DisplayText == "Example")
        );

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task EndpointsResultInUrls()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var projectA = builder.AddProject<ProjectA>("projecta")
            .WithHttpEndpoint(name: "test")
            .OnBeforeResourceStarted((_, _, _) =>
            {
                tcs.SetResult();
                return Task.CompletedTask;
            });

        await using var app = await builder.BuildAsync();
        await app.StartAsync();
        await tcs.Task.DefaultTimeout();

        var urls = projectA.Resource.Annotations.OfType<ResourceUrlAnnotation>();
        Assert.Single(urls, u => u.Url.StartsWith("http://localhost") && u.Endpoint?.EndpointName == "test");

        await app.StopAsync().DefaultTimeout();
    }

    [Theory]
    [InlineData("myapp.dev.localhost", "-myapp.dev.localhost")]
    [InlineData("myapp-apphost.dev.localhost", "-myapp.dev.localhost")]
    [InlineData("myapp_apphost.dev.localhost", "-myapp.dev.localhost")]
    [InlineData("myapp.apphost.dev.localhost", "-myapp.dev.localhost")]
    public async Task EndpointsGetDevLocalhostUrlsWhenDashboardHasDevLocalhostUrl(string dashboardHost, string expectedHostSuffix)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        builder.Services.Configure<DashboardOptions>(options =>
        {
            options.DashboardUrl = $"http://{dashboardHost}:12345";
        });

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var projectB = builder.AddProject<ProjectB>("projectb")
            .WithEndpoint(scheme: "tcp")
            .WithUrlForEndpoint("http", u => u.DisplayText = "Custom Display Text")
            .OnBeforeResourceStarted((_, _, _) =>
            {
                tcs.SetResult();
                return Task.CompletedTask;
            });

        await using var app = await builder.BuildAsync();
        await app.StartAsync();
        await tcs.Task.DefaultTimeout();

        var urls = projectB.Resource.Annotations.OfType<ResourceUrlAnnotation>();
        Assert.Collection(urls,
            u =>
            {
                Assert.StartsWith($"http://{projectB.Resource.Name.ToLowerInvariant()}{expectedHostSuffix}", u.Url);
                Assert.EndsWith("/sub-path", u.Url);
                Assert.Equal("http", u.Endpoint?.EndpointName);
                Assert.Equal(UrlDisplayLocation.SummaryAndDetails, u.DisplayLocation);
                Assert.Equal("Custom Display Text", u.DisplayText);
            },
            u =>
            {
                Assert.StartsWith("http://localhost", u.Url);
                Assert.Equal("http", u.Endpoint?.EndpointName);
                Assert.Equal(UrlDisplayLocation.DetailsOnly, u.DisplayLocation);
            },
            u =>
            {
                Assert.StartsWith("tcp://localhost", u.Url);
                Assert.Equal("tcp", u.Endpoint?.EndpointName);
            }
        );

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task ProjectLaunchProfileRelativeLaunchUrlIsAddedToEndpointUrl()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var projectA = builder.AddProject<ProjectB>("projectb")
            .OnBeforeResourceStarted((_, _, _) =>
            {
                tcs.SetResult();
                return Task.CompletedTask;
            });

        await using var app = await builder.BuildAsync();
        await app.StartAsync();
        await tcs.Task.DefaultTimeout();

        var urls = projectA.Resource.Annotations.OfType<ResourceUrlAnnotation>();
        Assert.Single(urls, u => u.Url.EndsWith("/sub-path") && u.Endpoint?.EndpointName == "http");

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task ProjectLaunchProfileAbsoluteLaunchUrlIsUsedAsEndpointUrl()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var projectA = builder.AddProject<ProjectB>("projectb", launchProfileName: "custom")
            .OnBeforeResourceStarted((_, _, _) =>
            {
                tcs.SetResult();
                return Task.CompletedTask;
            });

        await using var app = await builder.BuildAsync();
        await app.StartAsync();
        await tcs.Task.DefaultTimeout();

        var urls = projectA.Resource.Annotations.OfType<ResourceUrlAnnotation>();
        Assert.Single(urls, u => u.Url == "http://custom.localhost:23456/home" && u.Endpoint?.EndpointName == "http");

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task WithUrlForEndpointUpdatesUrlForEndpoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var projectA = builder.AddProject<ProjectA>("projecta")
            .WithHttpEndpoint(name: "test")
            .WithUrlForEndpoint("test", u =>
            {
                u.Url = "https://example.com";
                u.DisplayText = "Link Text";
                u.DisplayOrder = 1000;
            })
            .OnBeforeResourceStarted((_, _, _) =>
            {
                tcs.SetResult();
                return Task.CompletedTask;
            });

        await using var app = await builder.BuildAsync();
        await app.StartAsync();
        await tcs.Task.DefaultTimeout();

        var urls = projectA.Resource.Annotations.OfType<ResourceUrlAnnotation>();
        Assert.Single(urls, u =>
            u.Url == "https://example.com"
            && u.DisplayText == "Link Text"
            && u.Endpoint?.EndpointName == "test"
            && u.DisplayOrder == 1000);

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task EndpointUrlsAreInitiallyInactive()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var servicea = builder.AddProject<Projects.ServiceA>("servicea")
            .WithUrlForEndpoint("http", u => u.Url = "https://example.com");

        var httpEndpoint = servicea.Resource.GetEndpoint("http");

        await using var app = await builder.BuildAsync();
        var rns = app.Services.GetRequiredService<ResourceNotificationService>();

        await app.StartAsync();

        // Wait for the resource to have URLs allocated (before it starts running)
        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource();
        var resourceEvent = await rns.WaitForResourceAsync(
            servicea.Resource.Name,
            e => e.Snapshot.Urls.Length > 0,
            cts.Token);

        await app.StopAsync().DefaultTimeout();

        Assert.Single(resourceEvent.Snapshot.Urls, s => s.Name == httpEndpoint.EndpointName && s.IsInactive && s.Url == "https://example.com");
    }

    [Fact]
    public async Task MultipleUrlsForSingleEndpointAreIncludedInUrlSnapshot()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var servicea = builder.AddProject<Projects.ServiceA>("servicea");
        var httpEndpoint = servicea.Resource.GetEndpoint("http");
        servicea.WithUrl($"{httpEndpoint}/one", "Example 1");
        servicea.WithUrl($"{httpEndpoint}/two", "Example 2");

        await using var app = await builder.BuildAsync();
        var rns = app.Services.GetRequiredService<ResourceNotificationService>();

        await app.StartAsync();

        // Wait for URLs to be populated
        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource();
        var resourceEvent = await rns.WaitForResourceAsync(
            servicea.Resource.Name,
            e => e.Snapshot.Urls.Length > 0,
            cts.Token);

        await app.StopAsync().DefaultTimeout();

        Assert.Collection(resourceEvent.Snapshot.Urls,
            s => Assert.True(s.Name == httpEndpoint.EndpointName && s.DisplayProperties.DisplayName == ""), // <-- this is the default URL added for the endpoint
            s => Assert.True(s.Name == httpEndpoint.EndpointName && s.Url.EndsWith("/one") && s.DisplayProperties.DisplayName == "Example 1"),
            s => Assert.True(s.Name == httpEndpoint.EndpointName && s.Url.EndsWith("/two") && s.DisplayProperties.DisplayName == "Example 2")
        );
    }

    [Fact]
    public async Task ExpectedNumberOfUrlsForReplicatedResource()
    {
        // This test creates a single project resource with a custom URL and
        // a replica count of 3. It then checks that the number of URLs
        // generated isn't impacted by the number of replicas.
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var servicea = builder.AddProject<Projects.ServiceA>("servicea")
            .WithUrl("https://example.com/project")
            .WithReplicas(3);

        await using var app = await builder.BuildAsync();
        var rns = app.Services.GetRequiredService<ResourceNotificationService>();

        await app.StartAsync();

        // Wait for the resource to be running
        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource();
        var resourceEvent = await rns.WaitForResourceAsync(
            servicea.Resource.Name,
            e => e.Snapshot.State == KnownResourceStates.Running,
            cts.Token);

        await app.StopAsync().DefaultTimeout();

        Assert.Equal(2, resourceEvent.Snapshot.Urls.Length);
        Assert.Collection(resourceEvent.Snapshot.Urls,
            url => Assert.StartsWith("http://localhost:", url.Url), // The default project URL
            url => Assert.Equal("https://example.com/project", url.Url) // Static URL
        );
    }

    [Fact]
    public async Task ProjectResourceUrlsTransitionThroughExpectedLifecycleStates()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var servicea = builder.AddProject<Projects.ServiceA>("servicea")
            .WithUrl("https://example.com/project");

        await using var app = await builder.BuildAsync();
        var rns = app.Services.GetRequiredService<ResourceNotificationService>();

        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.LongTimeoutDuration);

        var urlSnapshots = new List<UrlSnapshot[]>();

        static string FormatUrls(IEnumerable<UrlSnapshot> urls) =>
            string.Join(", ", urls.Select(u => $"[{u.Name ?? "null"}] {u.Url} (inactive={u.IsInactive})"));

        var watchTask = Task.Run(async () =>
        {
            await foreach (var notification in rns.WatchAsync(cts.Token))
            {
                if (notification.Resource == servicea.Resource && notification.Snapshot.Urls.Length > 0)
                {
                    urlSnapshots.Add(notification.Snapshot.Urls.ToArray());
                    testOutputHelper.WriteLine($"Captured snapshot #{urlSnapshots.Count}: State={notification.Snapshot.State}, URLs: {FormatUrls(notification.Snapshot.Urls)}");

                    // Stop when running and all URLs are active
                    if (notification.Snapshot.State == KnownResourceStates.Running &&
                        notification.Snapshot.Urls.All(u => !u.IsInactive))
                    {
                        break;
                    }
                }
            }
        });

        await app.StartAsync();
        await rns.WaitForResourceAsync(servicea.Resource.Name, KnownResourceStates.Running, cts.Token);
        await watchTask;
        await app.StopAsync().DefaultTimeout();

        // Log all captured snapshots for diagnostics
        testOutputHelper.WriteLine($"Total snapshots captured: {urlSnapshots.Count}");
        for (var i = 0; i < urlSnapshots.Count; i++)
        {
            testOutputHelper.WriteLine($"  [{i}] URLs: {FormatUrls(urlSnapshots[i])}");
        }

        // Find snapshots for each lifecycle stage
        var initialized = urlSnapshots.FirstOrDefault(s => s.Length == 1);
        Assert.True(initialized is not null, $"Expected 'initialized' snapshot (1 URL) but none found. Captured {urlSnapshots.Count} snapshots.");

        var endpointsAllocated = urlSnapshots.FirstOrDefault(s => s.Length == 2 && s.Any(u => u.IsInactive));
        Assert.True(endpointsAllocated is not null, $"Expected 'endpointsAllocated' snapshot (2 URLs, some inactive) but none found. Captured {urlSnapshots.Count} snapshots.");

        var running = urlSnapshots.FirstOrDefault(s => s.Length == 2 && s.All(u => !u.IsInactive));
        Assert.True(running is not null, $"Expected 'running' snapshot (2 URLs, all active) but none found. Captured {urlSnapshots.Count} snapshots.");

        // Assert initialized: only static URL, active
        var initUrl = Assert.Single(initialized);
        Assert.False(initUrl.IsInactive);
        Assert.Null(initUrl.Name);
        Assert.Equal("https://example.com/project", initUrl.Url);

        // Assert endpoints allocated: endpoint URL inactive, static URL active
        Assert.Collection(endpointsAllocated,
            s => { Assert.True(s.IsInactive); Assert.NotNull(s.Name); Assert.StartsWith("http://localhost", s.Url); },
            s => { Assert.False(s.IsInactive); Assert.Null(s.Name); Assert.Equal("https://example.com/project", s.Url); }
        );

        // Assert running: both URLs active
        Assert.Collection(running,
            s => { Assert.False(s.IsInactive); Assert.NotNull(s.Name); Assert.StartsWith("http://localhost", s.Url); },
            s => { Assert.False(s.IsInactive); Assert.Null(s.Name); Assert.Equal("https://example.com/project", s.Url); }
        );
    }

    [Fact]
    public async Task CustomResourceUrlsTransitionThroughExpectedLifecycleStates()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var custom = builder.AddResource(new CustomResource("custom"))
            .WithHttpEndpoint()
            .WithUrl("https://example.com/custom")
            .WithInitialState(new()
            {
                ResourceType = "Custom",
                CreationTimeStamp = DateTime.UtcNow,
                State = KnownResourceStates.NotStarted,
                Properties = []
            })
            .OnInitializeResource(async (custom, e, ct) =>
            {
                // Mark all the endpoints on custom resource as allocated so that the URLs are initialized
                if (custom.TryGetEndpoints(out var endpoints))
                {
                    var startingPort = 1234;
                    foreach (var endpoint in endpoints)
                    {
                        endpoint.AllocatedEndpoint = new(endpoint, endpoint.TargetHost, endpoint.Port ?? endpoint.TargetPort ?? startingPort++);
                    }
                }

                // Publish the ResourceEndpointsAllocatedEvent for the resource
                await e.Eventing.PublishAsync(new ResourceEndpointsAllocatedEvent(custom, e.Services), EventDispatchBehavior.BlockingConcurrent, ct);

                // Publish the BeforeResourceStartedEvent for the resource
                await e.Eventing.PublishAsync(new BeforeResourceStartedEvent(custom, e.Services), EventDispatchBehavior.BlockingSequential, ct);

                // Mark all the endpoint URLs as active (this makes them visible in the dashboard)
                await e.Notifications.PublishUpdateAsync(custom, s => s with
                {
                    Urls = [.. s.Urls.Select(u => u with { IsInactive = false })]
                });

                // Move resource to the running state
                await e.Services.GetRequiredService<ResourceNotificationService>()
                    .PublishUpdateAsync(e.Resource, s => s with
                    {
                        StartTimeStamp = DateTime.UtcNow,
                        State = KnownResourceStates.Running
                    });
            });

        await using var app = await builder.BuildAsync();
        var rns = app.Services.GetRequiredService<ResourceNotificationService>();

        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.LongTimeoutDuration);

        var urlSnapshots = new List<UrlSnapshot[]>();

        static string FormatUrls(IEnumerable<UrlSnapshot> urls) =>
            string.Join(", ", urls.Select(u => $"[{u.Name ?? "null"}] {u.Url} (inactive={u.IsInactive})"));

        var watchTask = Task.Run(async () =>
        {
            await foreach (var notification in rns.WatchAsync(cts.Token))
            {
                if (notification.Resource == custom.Resource && notification.Snapshot.Urls.Length > 0)
                {
                    urlSnapshots.Add(notification.Snapshot.Urls.ToArray());
                    testOutputHelper.WriteLine($"Captured snapshot #{urlSnapshots.Count}: State={notification.Snapshot.State}, URLs: {FormatUrls(notification.Snapshot.Urls)}");

                    // Stop when running and all URLs are active
                    if (notification.Snapshot.State == KnownResourceStates.Running &&
                        notification.Snapshot.Urls.All(u => !u.IsInactive))
                    {
                        break;
                    }
                }
            }
        });

        await app.StartAsync();
        await rns.WaitForResourceAsync(custom.Resource.Name, KnownResourceStates.Running, cts.Token);
        await watchTask;
        await app.StopAsync().DefaultTimeout();

        // Log all captured snapshots for diagnostics
        testOutputHelper.WriteLine($"Total snapshots captured: {urlSnapshots.Count}");
        for (var i = 0; i < urlSnapshots.Count; i++)
        {
            testOutputHelper.WriteLine($"  [{i}] URLs: {FormatUrls(urlSnapshots[i])}");
        }

        // Find snapshots for each lifecycle stage
        var initialized = urlSnapshots.FirstOrDefault(s => s.Length == 1);
        Assert.True(initialized is not null, $"Expected 'initialized' snapshot (1 URL) but none found. Captured {urlSnapshots.Count} snapshots.");

        var endpointsAllocated = urlSnapshots.FirstOrDefault(s => s.Length == 2 && s.Any(u => u.IsInactive));
        Assert.True(endpointsAllocated is not null, $"Expected 'endpointsAllocated' snapshot (2 URLs, some inactive) but none found. Captured {urlSnapshots.Count} snapshots.");

        var running = urlSnapshots.FirstOrDefault(s => s.Length == 2 && s.All(u => !u.IsInactive));
        Assert.True(running is not null, $"Expected 'running' snapshot (2 URLs, all active) but none found. Captured {urlSnapshots.Count} snapshots.");

        // Assert initialized: only static URL, active
        var initUrl = Assert.Single(initialized);
        Assert.False(initUrl.IsInactive);
        Assert.Null(initUrl.Name);
        Assert.Equal("https://example.com/custom", initUrl.Url);

        // Assert endpoints allocated: endpoint URL inactive, static URL active
        Assert.Collection(endpointsAllocated,
            s => { Assert.True(s.IsInactive); Assert.NotNull(s.Name); Assert.StartsWith("http://localhost", s.Url); },
            s => { Assert.False(s.IsInactive); Assert.Null(s.Name); Assert.Equal("https://example.com/custom", s.Url); }
        );

        // Assert running: both URLs active
        Assert.Collection(running,
            s => { Assert.False(s.IsInactive); Assert.NotNull(s.Name); Assert.StartsWith("http://localhost", s.Url); },
            s => { Assert.False(s.IsInactive); Assert.Null(s.Name); Assert.Equal("https://example.com/custom", s.Url); }
        );
    }

    [Fact]
    public async Task UrlsAreMarkedAsInternalDependingOnDisplayLocation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        builder.AddProject<Projects.ServiceA>("servicea")
            .WithUrls(c =>
            {
                c.Urls.Add(new() { Url = "http://example.com/", DisplayLocation = UrlDisplayLocation.SummaryAndDetails });
                c.Urls.Add(new() { Url = "http://example.com/internal", DisplayLocation = UrlDisplayLocation.DetailsOnly });
                c.Urls.Add(new() { Url = "http://example.com/out-of-range", DisplayLocation = (UrlDisplayLocation)100 });
            });

        await using var app = await builder.BuildAsync();
        var rns = app.Services.GetRequiredService<ResourceNotificationService>();

        await app.StartAsync();

        // Wait for running state with multiple URLs
        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource();
        var resourceEvent = await rns.WaitForResourceAsync(
            "servicea",
            e => e.Snapshot.State == KnownResourceStates.Running && e.Snapshot.Urls.Length > 1,
            cts.Token);

        await app.StopAsync().DefaultTimeout();

        Assert.Collection(resourceEvent.Snapshot.Urls,
            url => { Assert.Equal("http", url.Name); Assert.False(url.IsInternal); },
            url => { Assert.Equal("http://example.com/", url.Url); Assert.False(url.IsInternal); },
            url => { Assert.Equal("http://example.com/internal", url.Url); Assert.True(url.IsInternal); },
            url => { Assert.Equal("http://example.com/out-of-range", url.Url); Assert.False(url.IsInternal); }
        );
    }

    [Fact]
    public async Task WithUrlForEndpointUpdateDoesNotThrowOrCallCallbackIfEndpointNotFound()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var called = false;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var projectA = builder.AddProject<ProjectA>("projecta")
            .WithHttpEndpoint(name: "test")
            .WithUrlForEndpoint("non-existant", u =>
            {
                called = true;
            })
            .OnBeforeResourceStarted((_, _, _) =>
            {
                tcs.SetResult();
                return Task.CompletedTask;
            });

        await using var app = await builder.BuildAsync();
        await app.StartAsync();
        await tcs.Task.DefaultTimeout();

        Assert.False(called);

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task WithUrlForEndpointAddDoesNotThrowOrCallCallbackIfEndpointNotFound()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var called = false;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var projectA = builder.AddProject<ProjectA>("projecta")
            .WithHttpEndpoint(name: "test")
            .WithUrlForEndpoint("non-existant", ep =>
            {
                called = true;
                return new() { Url = "https://example.com" };
            })
            .OnBeforeResourceStarted((_, _, _) =>
            {
                tcs.SetResult();
                return Task.CompletedTask;
            });

        await using var app = await builder.BuildAsync();
        await app.StartAsync();
        await tcs.Task.DefaultTimeout();

        Assert.False(called);

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task WithUrlWithRelativeUrlAppliesPathToExpectedUrls()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var projectA = builder.AddProject<ProjectA>("projecta")
            .WithHttpEndpoint(name: "test")
            .WithUrl("https://static-before.com")
            .WithUrls(c => c.Urls.Add(new() { Url = "https://callback-before.com/sub-path", DisplayText = "Example" }))
            .WithUrl("/test", "Example") // This should update all URLs added to this point
            .WithUrl("https://static-after.com/sub-path") // This will get updated too because it's a static URL so order doesn't matter
            .WithUrls(c => c.Urls.Add(new() { Url = "https://callback-after.com/sub-path" })) // This won't get updated because it's added after the relative URL
            .OnBeforeResourceStarted((_, _, _) =>
            {
                tcs.SetResult();
                return Task.CompletedTask;
            });

        await using var app = await builder.BuildAsync();
        await app.StartAsync();
        await tcs.Task.DefaultTimeout();

        var allUrls = projectA.Resource.Annotations.OfType<ResourceUrlAnnotation>();
        var endpointUrl = allUrls.FirstOrDefault(u => u.Endpoint?.EndpointName == "test");
        var staticBeforeUrl = allUrls.FirstOrDefault(u => u.Endpoint is null && u.Url.StartsWith("https://static-before.com"));
        var callbackBeforeUrl = allUrls.FirstOrDefault(u => u.Endpoint is null && u.Url.StartsWith("https://callback-before.com"));
        var staticAfter = allUrls.FirstOrDefault(u => u.Endpoint is null && u.Url.StartsWith("https://static-after.com"));
        var callbackAfter = allUrls.FirstOrDefault(u => u.Endpoint is null && u.Url.StartsWith("https://callback-after.com"));

        Assert.NotNull(endpointUrl);
        Assert.Equal("Example", endpointUrl.DisplayText);
        Assert.True(endpointUrl.Url.StartsWith("http://localhost") && endpointUrl.Url.EndsWith("/test"));

        Assert.NotNull(staticBeforeUrl);
        Assert.Equal("https://static-before.com/test", staticBeforeUrl.Url);

        Assert.NotNull(callbackBeforeUrl);
        Assert.Equal("https://callback-before.com/test", callbackBeforeUrl.Url);

        Assert.NotNull(staticAfter);
        Assert.Equal("https://static-after.com/test", staticAfter.Url);

        Assert.NotNull(callbackAfter);
        Assert.Equal("https://callback-after.com/sub-path", callbackAfter.Url);

        await app.StopAsync().DefaultTimeout();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithUrlForEndpointUpdateTurnsRelativeUrlIntoAbsoluteUrl(bool useHttps)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var project = builder.AddProject<ProjectA>("project");

        if (useHttps)
        {
            project.WithHttpsEndpoint(name: "test");
        }
        else
        {
            project.WithHttpEndpoint(name: "test");
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        project
            .WithUrlForEndpoint("test", url =>
            {
                url.Url = "/test-sub-path";
                url.DisplayText = "Test Link";
            })
            .OnBeforeResourceStarted((_, _, _) =>
            {
                tcs.SetResult();
                return Task.CompletedTask;
            });

        await using var app = await builder.BuildAsync();
        await app.StartAsync();
        await tcs.Task.DefaultTimeout();

        var endpointUrl = project.Resource.Annotations.OfType<ResourceUrlAnnotation>().FirstOrDefault(u => u.Endpoint?.EndpointName == "test");

        Assert.NotNull(endpointUrl);
        Assert.StartsWith(useHttps ? "https://localhost" : "http://localhost", endpointUrl.Url);
        Assert.EndsWith("/test-sub-path", endpointUrl.Url);
        Assert.Equal("Test Link", endpointUrl.DisplayText);

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task WithUrlForEndpointSupportsMultipleRelativeUrlsOnLaunchProfileEndpoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var project = builder.AddProject<ProjectB>("project", launchProfileName: "http")
            // Update the URL from the launch profile
            .WithUrlForEndpoint("http", url =>
            {
                url.Url = "/test-sub-path";
                url.DisplayText = "Test Link";
            })
            // Add another relative URL
            .WithUrlForEndpoint("http", _ => new()
            {
                Url = "/test-another-sub-path",
                DisplayText = "Another Test Link"
            })
            .OnBeforeResourceStarted((_, _, _) =>
            {
                tcs.SetResult();
                return Task.CompletedTask;
            });

        await using var app = await builder.BuildAsync();
        await app.StartAsync();
        await tcs.Task.DefaultTimeout();

        var launchProfileUrls = project.Resource.Annotations.OfType<ResourceUrlAnnotation>().Where(u => u.Endpoint?.EndpointName == "http");
        Assert.Collection(launchProfileUrls,
            url =>
            {
                Assert.StartsWith("http://localhost", url.Url);
                Assert.EndsWith("/test-sub-path", url.Url);
                Assert.Equal("Test Link", url.DisplayText);
            },
            url =>
            {
                Assert.StartsWith("http://localhost", url.Url);
                Assert.EndsWith("/test-another-sub-path", url.Url);
                Assert.Equal("Another Test Link", url.DisplayText);
            }
        );

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task WithUrlForEndpointAddTurnsRelativeUrlIntoAbsoluteUrl()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var projectA = builder.AddProject<ProjectA>("projecta")
            .WithHttpEndpoint(name: "test")
            .WithUrlForEndpoint("test", ep =>
            {
                return new() { Url = "/sub-path" };
            })
            .OnBeforeResourceStarted((_, _, _) =>
            {
                tcs.SetResult();
                return Task.CompletedTask;
            });

        await using var app = await builder.BuildAsync();
        await app.StartAsync();
        await tcs.Task.DefaultTimeout();

        var endpointUrl = projectA.Resource.Annotations.OfType<ResourceUrlAnnotation>().FirstOrDefault(u => u.Endpoint?.EndpointName == "test" && u.Url.EndsWith("/sub-path"));

        Assert.NotNull(endpointUrl);
        Assert.True(endpointUrl.Url.StartsWith("http://localhost") && endpointUrl.Url.EndsWith("/sub-path"));

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task WithUrlsTurnsRelativeEndpointUrlsIntoAbsoluteUrls()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var projectA = builder.AddProject<ProjectA>("projecta")
            .WithHttpEndpoint(name: "test")
            .WithUrls(c =>
            {
                c.Urls.Add(new() { Endpoint = c.GetEndpoint("test"), Url = "/sub-path" });
            })
            .OnBeforeResourceStarted((_, _, _) =>
            {
                tcs.SetResult();
                return Task.CompletedTask;
            });

        await using var app = await builder.BuildAsync();
        await app.StartAsync();
        await tcs.Task.DefaultTimeout();

        var endpointUrl = projectA.Resource.Annotations.OfType<ResourceUrlAnnotation>().FirstOrDefault(u => u.Endpoint?.EndpointName == "test" && u.Url.EndsWith("/sub-path"));

        Assert.NotNull(endpointUrl);
        Assert.True(endpointUrl.Url.StartsWith("http://localhost") && endpointUrl.Url.EndsWith("/sub-path"));

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task WithUrlCanAddUrlFromAnotherResourcesEndpoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        // Resource A has the endpoint
        var resourceA = builder.AddProject<Projects.ServiceA>("resourcea")
            .WithHttpEndpoint(name: "api");

        // Resource B gets a URL that references resource A's endpoint via the object model
        var resourceB = builder.AddProject<Projects.ServiceA>("resourceb")
            .WaitFor(resourceA)
            .WithUrls(c =>
            {
                c.Urls.Add(new()
                {
                    DisplayText = "API Docs",
                    Url = "/",
                    Endpoint = resourceA.Resource.GetEndpoint("api")
                });
            });

        await using var app = await builder.BuildAsync();
        var rns = app.Services.GetRequiredService<ResourceNotificationService>();

        await app.StartAsync();

        // Wait for resource B to be running & have the expected URLs
        var resourceEvent = await rns.WaitForResourceAsync(
            resourceB.Resource.Name,
            e => e.Snapshot.State == KnownResourceStates.Running
                    && e.Snapshot.Urls.Length == resourceB.Resource.GetEndpoints().ToArray().Length + 1
                    && e.Snapshot.Urls.All(u => !u.IsInactive),
            default).DefaultTimeout();

        await app.StopAsync().DefaultTimeout();

        // Verify that the URL from resource A's endpoint appears in resource B's snapshot
        var crossResourceUrl = resourceEvent.Snapshot.Urls.FirstOrDefault(u => u.DisplayProperties.DisplayName == "API Docs");

        Assert.NotNull(crossResourceUrl);
        Assert.StartsWith("http://localhost", crossResourceUrl.Url);
        Assert.Equal("api", crossResourceUrl.Name);
        Assert.False(crossResourceUrl.IsInactive);
    }

    private sealed class CustomResource(string name) : Resource(name), IResourceWithEndpoints
    {

    }

    private sealed class ProjectA : IProjectMetadata
    {
        public string ProjectPath => "projectA";

        public LaunchSettings LaunchSettings { get; } = new();
    }

    private sealed class ProjectB : IProjectMetadata
    {
        public string ProjectPath => "project";

        public LaunchSettings LaunchSettings { get; } = new()
        {
            Profiles = new Dictionary<string, LaunchProfile>
            {
                ["http"] = new()
                {
                    CommandName = "Project",
                    ApplicationUrl = "http://localhost:23456",
                    LaunchUrl = "/sub-path"
                },
                ["custom"] = new()
                {
                    CommandName = "Project",
                    ApplicationUrl = "http://localhost:23456",
                    LaunchUrl = "http://custom.localhost:23456/home"
                }
            }
        };
    }
}
