// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Aspire.TestUtilities;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;
using Polly.Timeout;
using SamplesIntegrationTests;
using SamplesIntegrationTests.Infrastructure;
using Xunit;
using Xunit.Sdk;

namespace Aspire.Playground.Tests;

[RequiresFeature(TestFeature.Docker)]
public class AppHostTests
{
    private readonly ITestOutputHelper _testOutput;
    private static readonly string? s_appHostNameFilter = Environment.GetEnvironmentVariable("TEST_PLAYGROUND_APPHOST_FILTER");

    public AppHostTests(ITestOutputHelper testOutput)
    {
        _testOutput = testOutput;
    }

    [Theory]
    [MemberData(nameof(TestEndpoints))]
    [QuarantinedTest("https://github.com/microsoft/aspire/issues/6866")]
    public async Task TestEndpointsReturnOk(TestEndpoints testEndpoints)
    {
        var appHostType = testEndpoints.AppHostType!;
        var resourceEndpoints = testEndpoints.ResourceEndpoints!;
        var appHost = await DistributedApplicationTestFactory.CreateAsync(appHostType, _testOutput);
        var projects = appHost.Resources.OfType<ProjectResource>();
        await using var app = await appHost.BuildAsync();

        await app.StartAsync();

        if (testEndpoints.WaitForTexts != null)
        {
            // If specific ready to start texts are available use it
            var tasks = testEndpoints.WaitForTexts.Select(x => app.WaitForTextAsync(log => new Regex(x.Pattern).IsMatch(log), x.ResourceName)).ToArray();
            try
            {
                await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromMinutes(5));
            }
            catch (TimeoutException te)
            {
                StringBuilder sb = new();
                for (int i = 0; i < testEndpoints.WaitForTexts.Count; i++)
                {
                    if (!tasks[i].IsCompleted)
                    {
                        sb.AppendLine($"Timed out waiting for this text from resource {testEndpoints.WaitForTexts[i].ResourceName}: {testEndpoints.WaitForTexts[i].Pattern}");
                    }
                }

                throw new XunitException(sb.ToString(), te);
            }
        }
        else
        {
            var applicationModel = app.Services.GetRequiredService<DistributedApplicationModel>();

            await app.WaitForResources().WaitAsync(TimeSpan.FromMinutes(5));

            if (testEndpoints.WaitForResources?.Count > 0)
            {
                // Wait until each resource transitions to the required state
                var timeout = TimeSpan.FromMinutes(5);
                foreach (var (ResourceName, TargetState) in testEndpoints.WaitForResources)
                {
                    _testOutput.WriteLine($"Waiting for resource '{ResourceName}' to reach state '{TargetState}' in app '{appHost.AppHostAssembly}'");
                    await app.WaitForResource(ResourceName, TargetState).WaitAsync(TimeSpan.FromMinutes(5));
                }
            }
        }

        foreach (var resource in resourceEndpoints.Keys)
        {
            var endpoints = resourceEndpoints[resource];

            if (endpoints.Count == 0)
            {
                // No test endpoints so ignore this resource
                continue;
            }

            HttpResponseMessage? response = null;
            using var client = CreateHttpClientWithResilience(app, resource);

            foreach (var path in endpoints)
            {
                _testOutput.WriteLine($"Calling endpoint '{client.BaseAddress}{path.TrimStart('/')} for resource '{resource}' in app '{appHost.AppHostAssembly}'");
                try
                {
                    response = await client.GetAsync(path);
                }
                catch (TimeoutRejectedException tre)
                {
                    throw new XunitException($"Endpoint '{client.BaseAddress}{path.TrimStart('/')}' for resource '{resource}' in app '{appHost.AppHostAssembly}' timed out", tre);
                }

                Assert.True(HttpStatusCode.OK == response.StatusCode, $"Endpoint '{client.BaseAddress}{path.TrimStart('/')}' for resource '{resource}' in app '{appHost.AppHostAssembly}' returned status code {response.StatusCode}");
            }
        }

        app.EnsureNoErrorsLogged();
        await app.StopAsync();
    }

    public static HttpClient CreateHttpClientWithResilience(DistributedApplication app, string resource)
        => app.CreateHttpClient(resource, null, clientBuilder =>
        {
            clientBuilder
                .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan)
                .AddStandardResilienceHandler(resilience =>
                {
                    resilience.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(120);
                    resilience.AttemptTimeout.Timeout = TimeSpan.FromSeconds(60);
                    resilience.Retry.MaxRetryAttempts = 30;
                    resilience.CircuitBreaker.SamplingDuration = resilience.AttemptTimeout.Timeout * 2;
                });
        });

    public static IList<TestEndpoints> GetAllTestEndpoints()
    {
        IList<TestEndpoints> candidates =
        [
            // Disable due to https://github.com/microsoft/aspire/issues/5959
            //new TestEndpoints(typeof(Projects.EventHubs.AppHost",
            //    resourceEndpoints: new() { { "api", ["/alive", "/health"] } },
            //    waitForTexts: [
            //        new ("eventhubns", "Emulator Service is Successfully Up"),
            //        new ("eventhubns-storage", "Azurite Table service is successfully listening"),
            //        new ("ehstorage", "Azurite Table service is successfully listening"),
            //        new ("consumer", "Completed retrieving properties for Event Hub")
            //    ],
            //    whenReady: TestEventHubsAppHost),
            new TestEndpoints(typeof(Projects.Redis_AppHost),
                resourceEndpoints: new() { { "apiservice", ["/alive", "/health", "/garnet/ping", "/garnet/get", "/garnet/set", "/redis/ping", "/redis/get", "/redis/set", "/valkey/ping", "/valkey/get", "/valkey/set"] } },
                waitForTexts: [
                    new ("redis", "Ready to accept connections tcp"),
                    new ("valkey", "Ready to accept connections tcp"),
                    new ("garnet", "Ready to accept connections"),
                    new ("apiservice", "Application started")
                ]),
            new TestEndpoints(typeof(Projects.AzureStorageEndToEnd_AppHost),
                resourceEndpoints: new() { { "api", ["/alive", "/health", "/"] } },
                waitForTexts: [
                    new ("storage", "Azurite Table service is successfully listening")
                ]),
            new TestEndpoints(typeof(Projects.MilvusPlayground_AppHost),
                resourceEndpoints: new() { { "apiservice", ["/alive", "/health", "/create", "/search"] } },
                waitForTexts: [
                    new ("milvus", "Milvus Proxy successfully initialized and ready to serve"),
                ]),
            // Cosmos emulator is extremely slow to start up and unreliable in CI
            //new TestEndpoints(typeof(Projects.CosmosEndToEnd_AppHost),
            //    resourceEndpoints: new() { { "api", ["/alive", "/health", "/"] } },
            //    // "/ef" - disabled due to https://github.com/microsoft/aspire/issues/5415
            //    waitForTexts: [
            //        new ("cosmos", "Started$"),
            //        new ("api", "Application started")
            //    ]),
            new TestEndpoints(typeof(Projects.Keycloak_AppHost),
                resourceEndpoints: new() { { "apiservice", ["/alive", "/health"] } }),
            new TestEndpoints(typeof(Projects.Mongo_AppHost),
                resourceEndpoints: new() { { "api", ["/alive", "/health", "/"] } },
                waitForTexts: [
                    new ("mongo", "Waiting for connections"),
                    new ("mongo-mongoexpress", "Mongo Express server listening"),
                    new("api", "Application started.")
                ]),
            new TestEndpoints(typeof(Projects.MySqlDb_AppHost),
                resourceEndpoints: new() { { "apiservice", ["/alive", "/health", "/catalog"] } },
                waitForTexts: [
                    new ("mysql", "ready for connections.* port: 33060"),
                    new ("apiservice", "Application started")
                ]),
            new TestEndpoints(typeof(Projects.Nats_AppHost),
                resourceEndpoints: new() {
                    { "api", ["/alive", "/health"] },
                    { "backend", ["/alive", "/health"] }
                },
                waitForTexts: [
                    new ("nats", "Server is ready"),
                    new("api", "Application started")
                ]),
            new TestEndpoints(typeof(Projects.ParameterEndToEnd_AppHost),
                resourceEndpoints: new() { { "api", ["/", "/alive", "/health"] } },
                waitForTexts: [
                    new ("sql", "SQL Server is now ready for client connections."),
                ]),
            new TestEndpoints(typeof(Projects.PostgresEndToEnd_AppHost),
                resourceEndpoints: new() {
                    // Invoking "/" first as that *creates* the databases
                    { "api", ["/", "/alive", "/health"] }
                },
                waitForTexts: [
                    new ("pg1", "PostgreSQL init process complete; ready for start up"),
                    new ("pg2", "PostgreSQL init process complete; ready for start up"),
                    new ("pg3", "PostgreSQL init process complete; ready for start up"),
                    new ("pg4", "PostgreSQL init process complete; ready for start up"),
                    new ("pg5", "PostgreSQL init process complete; ready for start up"),
                    new ("pg6", "PostgreSQL init process complete; ready for start up"),
                    new ("pg10", "PostgreSQL init process complete; ready for start up"),
                ]),
            new TestEndpoints(typeof(Projects.ProxylessEndToEnd_AppHost),
                resourceEndpoints: new() { { "api", ["/alive", "/health", "/redis"] } },
                waitForTexts: [
                    new ("redis", "Ready to accept connections"),
                    new("api", "Application started"),
                    new("api2", "Application started")
                ]),
            new TestEndpoints(typeof(Projects.Qdrant_AppHost),
                resourceEndpoints: new() { { "apiservice", ["/alive", "/health"] } },
                waitForTexts: [
                    new ("qdrant", "Qdrant HTTP listening"),
                    new ("apiservice", "Application started")
                ]),
            new TestEndpoints(typeof(Projects.Seq_AppHost),
                resourceEndpoints: new() { { "api", ["/alive", "/health"] } },
                waitForTexts: [
                    new ("seq", "Seq listening"),
                    new ("api", "Application started")
                ]),
            // Invoke "/" first to create the databases
            new TestEndpoints(typeof(Projects.SqlServerEndToEnd_AppHost),
                resourceEndpoints: new() { { "api", ["/", "/alive", "/health"] } },
                waitForTexts: [
                    new ("sql1", "SQL Server is now ready for client connections"),
                    new ("sql2", "SQL Server is now ready for client connections"),
                ]),
            new TestEndpoints(typeof(Projects.TestShop_AppHost),
                resourceEndpoints: new() {
                    { "catalogdbapp", ["/alive", "/health"] },
                    { "frontend", ["/alive", "/health"] }
                },
                waitForTexts: [
                    new ("messaging", "started TCP listener"),
                    new ("basketcache", "Ready to accept connections"),
                    new ("frontend", "Application started"),
                    new ("catalogdbapp", "Application started"),
                    new ("basketservice", "Application started"),
                    new ("postgres", "database system is ready to accept connections"),
                ]),
        ];

        return candidates;
    }

    public static TheoryData<TestEndpoints> TestEndpoints()
    {
        TheoryData<TestEndpoints> theoryData = new();
        foreach (var candidateTestEndpoint in GetAllTestEndpoints())
        {
            if (string.IsNullOrEmpty(s_appHostNameFilter) || candidateTestEndpoint.AppHostType?.Name.Contains(s_appHostNameFilter, StringComparison.OrdinalIgnoreCase) == true)
            {
                theoryData.Add(candidateTestEndpoint);
            }
        }

        if (!theoryData.Any() && !string.IsNullOrEmpty(s_appHostNameFilter))
        {
            Assert.Skip($"No test endpoints found matching filter '{s_appHostNameFilter}'");
        }

        return theoryData;
    }
}

public class TestEndpoints
{
    public TestEndpoints(Type appHostType,
                         Dictionary<string, List<string>> resourceEndpoints,
                         List<ReadyStateText>? waitForTexts = null)
    {
        AppHostType = appHostType;
        ResourceEndpoints = resourceEndpoints;
        WaitForTexts = waitForTexts;
    }

    public Type AppHostType { get; set; }

    public List<ResourceWait>? WaitForResources { get; set; }

    public List<ReadyStateText>? WaitForTexts { get; set; }

    public Dictionary<string, List<string>>? ResourceEndpoints { get; set; }

    public override string? ToString() => $"{AppHostType} ({ResourceEndpoints?.Count ?? 0} resources)";

    public class ResourceWait(string resourceName, string targetState)
    {
        public string ResourceName { get; } = resourceName;

        public string TargetState { get; } = targetState;

        public void Deconstruct(out string resourceName, out string targetState)
        {
            resourceName = ResourceName;
            targetState = TargetState;
        }
    }

    public record class ReadyStateText(string ResourceName, string Pattern);
}
