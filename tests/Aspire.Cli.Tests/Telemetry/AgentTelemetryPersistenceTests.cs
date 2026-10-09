// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.Utils;
using Aspire.Shared;
using Aspire.Shared.Telemetry;
using Aspire.TestUtilities;
using Azure.Core.Pipeline;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.DotNet.RemoteExecutor;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Aspire.Cli.Tests.Telemetry;

public class AgentTelemetryPersistenceTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void SharedExporter_DisablesStandardMetricsAndPerformanceCounters()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var process = RemoteExecutor.Invoke(static async storageDirectory =>
        {
            // Isolate cached exporter settings and prevent SDK diagnostics from making real requests.
            Environment.SetEnvironmentVariable("APPLICATIONINSIGHTS_STATSBEAT_DISABLED", "true");
            Environment.SetEnvironmentVariable("OTEL_DOTNET_AZURE_MONITOR_ENABLE_RESOURCE_METRICS", "true");
            TelemetryManager.ConfigureExporterForProcess(isAgentTelemetryInvocation: false, isCIEnvironment: true);
            using var fixture = new TelemetryFixture(initialize: false);
            fixture.TagsSource.StartCalculation(() => Task.FromResult<IReadOnlyList<KeyValuePair<string, object?>>>(
            [
                new(TelemetryConstants.Tags.CliVersion, "1.0.0-test"),
                new("machine.device_id", "test-device-id")
            ]));
            var exportedItems = new ConcurrentQueue<(string BaseType, string Name)>();
            using var handler = new MockHttpMessageHandler(async (request, cancellationToken) =>
            {
                var payload = await request.Content!.ReadAsStringAsync(cancellationToken);
                // The ingestion request contains newline-delimited Application Insights envelopes:
                // {"data":{"baseType":"RemoteDependencyData","baseData":{...}},...}
                var lines = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    using var envelope = JsonDocument.Parse(line);
                    var data = envelope.RootElement.GetProperty("data");
                    var baseType = data.GetProperty("baseType").GetString()!;
                    var baseData = data.GetProperty("baseData");
                    var name = baseType == "MetricData"
                        ? baseData.GetProperty("metrics")[0].GetProperty("name").GetString()!
                        : baseData.GetProperty("name").GetString()!;
                    if (baseType == "RemoteDependencyData")
                    {
                        var properties = baseData.GetProperty("properties");
                        Assert.Equal("1.0.0-test", properties.GetProperty(TelemetryConstants.Tags.CliVersion).GetString());
                        Assert.Equal("test-device-id", properties.GetProperty("machine.device_id").GetString());
                    }
                    exportedItems.Enqueue((baseType, name));
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { itemsReceived = lines.Length, itemsAccepted = lines.Length, errors = Array.Empty<object>() }))
                };
            });
            using var client = new HttpClient(handler);
            AzureMonitorExporterOptions? exporterOptions = null;
            using var provider = AzureMonitorTelemetryProvider.Create(new ServiceCollection(),
                ResourceBuilder.CreateEmpty().AddService("aspire-cli"), "MetricsDisabledTest", AspireCliTelemetry.EventLogCategoryName,
                $"InstrumentationKey={Guid.NewGuid()};IngestionEndpoint=https://localhost/", storageDirectory,
                builder =>
                {
                    builder.AddProcessor(new CliTagEnrichmentProcessor(fixture.TagsSource, fixture.Telemetry));
                    builder.ConfigureServices(services => services.Configure<AzureMonitorExporterOptions>(options =>
                    {
                        exporterOptions = options;
                        options.Transport = new HttpClientTransport(client);
                    }));
                });

            Assert.NotNull(exporterOptions);
            Assert.False(exporterOptions.EnableLiveMetrics);
            Assert.False(exporterOptions.EnableStandardMetrics);
            Assert.False(exporterOptions.EnablePerformanceCounters);
            Assert.Null(exporterOptions.TracesPerSecond);
            Assert.Equal(1.0f, exporterOptions.SamplingRatio);
            Assert.Equal("true", Environment.GetEnvironmentVariable("OTEL_DOTNET_AZURE_MONITOR_ENABLE_RESOURCE_METRICS"));
            Assert.Equal("aspire-cli", provider.Resource.Attributes.Single(attribute => attribute.Key == "service.name").Value);
            using var diagnosticSource = new ActivitySource(AspireCliTelemetry.DiagnosticsActivitySourceName);
            Assert.False(diagnosticSource.HasListeners());
            using var profilingSource = new ActivitySource(ProfilingTelemetry.ActivitySourceName);
            Assert.False(profilingSource.HasListeners());

            using var source = new ActivitySource("MetricsDisabledTest");
            using (var activity = source.StartActivity("reported-operation"))
            {
                Assert.NotNull(activity);
                Assert.Null(activity.GetTagItem(TelemetryConstants.Tags.CliVersion));
            }

            Assert.True(await provider.ForceFlushAsync(10_000));
            Assert.Equal(
                [("MetricData", "_OTELRESOURCE_"), ("RemoteDependencyData", "reported-operation")],
                exportedItems.ToArray());
        }, workspace.WorkspaceRoot.FullName);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SharedLogExporter_PersistsOnlyWhenConfigured(bool shutdown, bool persist)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var process = RemoteExecutor.Invoke(static async (storageDirectory, shutdownValue, persistValue) =>
        {
            // Exporter switches and cached transmitters must not affect other tests or send SDK diagnostics.
            Environment.SetEnvironmentVariable("APPLICATIONINSIGHTS_STATSBEAT_DISABLED", "true");
            var shutdown = bool.Parse(shutdownValue);
            var persist = bool.Parse(persistValue);
            TelemetryManager.ConfigureExporterForProcess(isAgentTelemetryInvocation: false, isCIEnvironment: !persist);
            var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseUpload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var uploaded = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var handler = new MockHttpMessageHandler(async (request, cancellationToken) =>
            {
                var payload = await request.Content!.ReadAsStringAsync(cancellationToken);
                requestStarted.TrySetResult();
                await releaseUpload.Task.WaitAsync(cancellationToken);
                uploaded.TrySetResult(payload);
                var count = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { itemsReceived = count, itemsAccepted = count, errors = Array.Empty<object>() }))
                };
            });
            using var client = new HttpClient(handler);
            var connectionString = $"InstrumentationKey={Guid.NewGuid()};IngestionEndpoint=https://localhost/";
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.AddOpenTelemetry(logging =>
            {
                logging.IncludeFormattedMessage = true;
                logging.IncludeScopes = false;
                logging.AddAspireAzureMonitorExporter(connectionString, storageDirectory, options =>
                {
                    Assert.Equal(connectionString, options.ConnectionString);
                    Assert.Equal(storageDirectory, options.StorageDirectory);
                    Assert.False(options.EnableLiveMetrics);
                    Assert.False(options.EnableStandardMetrics);
                    Assert.False(options.EnablePerformanceCounters);
                    Assert.False(options.EnableTraceBasedLogsSampler);
                    options.Transport = new HttpClientTransport(client);
                });
            }));
            using var serviceProvider = services.BuildServiceProvider();
            var provider = serviceProvider.GetRequiredService<LoggerProvider>();
            var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Test.Product.Events");
            string[] eventNames = ["product-event-1", "product-event-2", "product-event-3"];
            foreach (var eventName in eventNames)
            {
                logger.LogInformation("{EventName}", eventName);
            }

            try
            {
                var drain = Task.Run(() => shutdown ? provider.Shutdown(10_000) : provider.ForceFlush(10_000));
                if (persist)
                {
                    // No trace provider is present to put the shared transmitter into persistence mode.
                    Assert.True(await drain.DefaultTimeout());
                }

                await requestStarted.Task.DefaultTimeout();
                Assert.False(uploaded.Task.IsCompleted);
                if (persist)
                {
                    Assert.NotEmpty(Directory.EnumerateFiles(storageDirectory, "*", SearchOption.AllDirectories));
                }
                else
                {
                    Assert.False(drain.IsCompleted);
                }

                releaseUpload.SetResult();
                Assert.True(await drain.DefaultTimeout());
                var payload = await uploaded.Task.DefaultTimeout();
                // Ingestion receives newline-delimited envelopes such as:
                // {"data":{"baseType":"MessageData","baseData":{"message":"product-event-1",...}},...}
                var actualEventNames = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
                {
                    using var envelope = JsonDocument.Parse(line);
                    var data = envelope.RootElement.GetProperty("data");
                    Assert.Equal("MessageData", data.GetProperty("baseType").GetString());
                    return data.GetProperty("baseData").GetProperty("message").GetString();
                });
                Assert.Equal(eventNames, actualEventNames);
            }
            finally
            {
                releaseUpload.TrySetResult();
                provider.Shutdown(10_000);
            }
        }, workspace.Path, shutdown.ToString(), persist.ToString());
    }

    [Fact]
    public async Task TelemetryManager_RequiresInitializationBeforeUse()
    {
        using var fixture = new TelemetryFixture(initialize: false);
        using var manager = CreateDisabledManager(fixture);

        Assert.False(manager.IsInitialized);
        Assert.Throws<InvalidOperationException>(() => manager.HasAzureMonitor);
        Assert.Throws<InvalidOperationException>(() => manager.HasProfilingProvider);
        Assert.Throws<InvalidOperationException>(() => manager.HasDiagnosticProvider);
        await Assert.ThrowsAsync<InvalidOperationException>(manager.ForceFlushProfilingAsync);
        await Assert.ThrowsAsync<InvalidOperationException>(manager.ForceFlushReportedAsync);
        Assert.False(await manager.TryShutdownAsync(CliExitCodes.Success, ReportedTelemetryMode.Local));

        manager.Initialize();
        Assert.True(manager.IsInitialized);
        Assert.False(manager.HasAzureMonitor);
        Assert.True(await manager.TryShutdownAsync(CliExitCodes.Success, ReportedTelemetryMode.Local));
        Assert.False(manager.IsInitialized);
        Assert.Throws<InvalidOperationException>(manager.Initialize);
        Assert.Throws<InvalidOperationException>(() => manager.HasAzureMonitor);
    }

    [Theory]
    [InlineData(true)]
#if DEBUG
    [InlineData(false)]
#endif
    public void TelemetryManager_IsolatesProductResourceFromDiagnosticAndProfilingResources(bool profilingEnabled)
    {
        using var process = RemoteExecutor.Invoke(static profilingValue =>
        {
            Environment.SetEnvironmentVariable("OTEL_SERVICE_NAME", "environment-service");
            Environment.SetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES", "customer.tenant=synthetic-tenant,deployment.path=synthetic-path");
            var profilingEnabled = bool.Parse(profilingValue);
            var configuration = new TelemetryConfiguration
            {
                ReportedTelemetryEnabled = true,
                ProfilingEnabled = profilingEnabled,
                RequestedOtlpExporter = true
            };
            using var fixture = new TelemetryFixture(telemetryConfiguration: configuration, initialize: false);
            Resource? azureTraceResource = null;
            Resource? azureLogResource = null;
            using var manager = new TelemetryManager(configuration, fixture.TagsSource, fixture.Telemetry, NullLogger<TelemetryManager>.Instance,
                (resource, _) =>
                {
                    var provider = AzureMonitorTelemetryProvider.Create(new ServiceCollection(), resource, AspireCliTelemetry.EventLogCategoryName,
                        () => Sdk.CreateTracerProviderBuilder().SetResourceBuilder(resource).Build(),
                        loggerProvider => azureLogResource = loggerProvider.GetResource());
                    azureTraceResource = provider.Resource;
                    return provider;
                });

            manager.Initialize();

            Assert.True(manager.HasAzureMonitor);
            Assert.Equal(profilingEnabled, manager.HasProfilingProvider);
            Assert.Equal(!profilingEnabled, manager.HasDiagnosticProvider);
            Assert.NotNull(azureTraceResource);
            Assert.NotNull(azureLogResource);
            Assert.Equal(azureTraceResource.Attributes.ToArray(), azureLogResource.Attributes.ToArray());
            Assert.Equal("aspire-cli", azureTraceResource.Attributes.Single(attribute => attribute.Key == "service.name").Value);

            // Inspect the actual OTLP provider without exposing manager-owned providers to callers.
            var field = typeof(TelemetryManager).GetField(profilingEnabled ? "_profilingProvider" : "_debugDiagnosticProvider",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            var otlpProvider = Assert.IsAssignableFrom<TracerProvider>(field.GetValue(manager));
            var otlpResource = otlpProvider.GetResource();
            Assert.Equal("aspire-cli", otlpResource.Attributes.Single(attribute => attribute.Key == "service.name").Value);
            var expectedVersion = AssemblyVersionHelper.GetInformationalVersion(typeof(Program).Assembly);
            Assert.NotEmpty(expectedVersion);
            Assert.Collection(azureTraceResource.Attributes.OrderBy(attribute => attribute.Key, StringComparer.Ordinal),
                attribute =>
                {
                    Assert.Equal("service.instance.id", attribute.Key);
                    Assert.True(Guid.TryParse(Assert.IsType<string>(attribute.Value), out var instanceId));
                    Assert.NotEqual(Guid.Empty, instanceId);
                },
                attribute => Assert.Equal(new KeyValuePair<string, object>("service.name", "aspire-cli"), attribute),
                attribute => Assert.Equal(new KeyValuePair<string, object>("service.version", expectedVersion), attribute));
            Assert.Equal(expectedVersion, otlpResource.Attributes.Single(attribute => attribute.Key == "service.version").Value);
            Assert.Equal("synthetic-tenant", otlpResource.Attributes.Single(attribute => attribute.Key == "customer.tenant").Value);
            Assert.Equal("synthetic-path", otlpResource.Attributes.Single(attribute => attribute.Key == "deployment.path").Value);
        }, profilingEnabled.ToString());
    }

    [Fact]
    public async Task TelemetryManager_ConcurrentInitializationAndRepeatedShutdownAreIdempotent()
    {
        using var fixture = new TelemetryFixture(initialize: false);
        using var manager = CreateDisabledManager(fixture);
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(manager.Initialize)));

        Assert.True(manager.IsInitialized);
        var shutdownTasks = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => manager.TryShutdownAsync(CliExitCodes.Success, ReportedTelemetryMode.Local)))
            .ToArray();
        Assert.All(await Task.WhenAll(shutdownTasks), Assert.True);
        Assert.False(manager.IsInitialized);
        Assert.Throws<InvalidOperationException>(manager.Initialize);
    }

    [Fact]
    public async Task TelemetryManager_ConcurrentInitializeAndTryShutdownLeaveConsistentState()
    {
        using var fixture = new TelemetryFixture(initialize: false);
        using var manager = CreateDisabledManager(fixture);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initialization = Task.Run(async () =>
        {
            await start.Task;
            manager.Initialize();
        });
        var shutdown = Task.Run(async () =>
        {
            await start.Task;
            return await manager.TryShutdownAsync(CliExitCodes.Success, ReportedTelemetryMode.Local);
        });

        start.SetResult();
        await Task.WhenAll(initialization, shutdown);

        var wasShutDown = await shutdown;
        Assert.Equal(!wasShutDown, manager.IsInitialized);
        if (!wasShutDown)
        {
            Assert.True(await manager.TryShutdownAsync(CliExitCodes.Success, ReportedTelemetryMode.Local));
        }
    }

    [Fact]
    public async Task TelemetryManager_DisposeBeforeInitializationPreventsInitialization()
    {
        using var fixture = new TelemetryFixture(initialize: false);
        var manager = CreateDisabledManager(fixture);
        manager.Dispose();

        Assert.Throws<InvalidOperationException>(manager.Initialize);
        Assert.False(await manager.TryShutdownAsync(CliExitCodes.Success, ReportedTelemetryMode.Local));
    }

    [Fact]
    public async Task ForceFlushReportedAsync_WithoutProviderSucceeds()
    {
        using var fixture = new TelemetryFixture(initialize: false);
        using var manager = CreateDisabledManager(fixture);

        manager.Initialize();
        Assert.True(await manager.ForceFlushReportedAsync().DefaultTimeout());
    }

    private static TelemetryManager CreateDisabledManager(TelemetryFixture fixture)
        => new(
            new TelemetryConfiguration { ReportedTelemetryEnabled = false },
            fixture.TagsSource,
            fixture.Telemetry,
            NullLogger<TelemetryManager>.Instance);

    [Fact]
    [OuterloopTest("Exercises the exporter's real three-minute lease expiry across processes.")]
    public void Uploader_DeliversPersistedEventAfterProducerExits()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var storage = workspace.CreateDirectory("storage").FullName;
        var lockPath = Path.Combine(workspace.Path, "uploader.lock");
        RemoteExecutor.Invoke(static async directory =>
        {
            TelemetryManager.ConfigureExporterForProcess(isAgentTelemetryInvocation: true, isCIEnvironment: false);
            using var handler = new MockHttpMessageHandler(async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new InvalidOperationException("The producer must exit without an upload completing.");
            });
            using var client = new HttpClient(handler);
            using var provider = CreateRecoveryProvider(directory, client);
            using var source = new ActivitySource("AgentRecoveryTest");
            using (var activity = source.StartActivity(TelemetryConstants.Activities.AgentTelemetry))
            {
                Assert.NotNull(activity);
                activity.SetTag(TelemetryConstants.Tags.AgentEventType, "skill_invocation");
                activity.SetTag(TelemetryConstants.Tags.AgentSkillName, "aspire");
            }
            Assert.True(await Task.Run(() => provider.ForceFlush(10_000)));
            Assert.True(AgentTelemetryUploader.HasPendingTelemetry(directory));
        }, storage).Dispose();

        // A producer can exit while holding the exporter's three-minute blob lease. Exercise real
        // expiry/recovery rather than editing private storage names or disabling exporter behavior.
        RemoteExecutor.Invoke(static async (directory, lockPath) =>
        {
            TelemetryManager.ConfigureExporterForProcess(isAgentTelemetryInvocation: true, isCIEnvironment: false);
            var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var handler = new MockHttpMessageHandler(async (request, cancellationToken) =>
            {
                var payload = await request.Content!.ReadAsStringAsync(cancellationToken);
                foreach (var line in payload.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    using var envelope = JsonDocument.Parse(line);
                    // Resource and process metrics can share a batch and need not have properties.
                    if (envelope.RootElement.GetProperty("data").GetProperty("baseData").TryGetProperty("properties", out var properties)
                        && properties.TryGetProperty(TelemetryConstants.Tags.AgentEventType, out var eventType))
                    {
                        Assert.Equal("skill_invocation", eventType.GetString());
                        Assert.Equal("aspire", properties.GetProperty(TelemetryConstants.Tags.AgentSkillName).GetString());
                        delivered.TrySetResult();
                    }
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"itemsReceived":1,"itemsAccepted":1,"errors":[]}""")
                };
            });
            using var client = new HttpClient(handler);
            using var provider = CreateRecoveryProvider(directory, client);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await AgentTelemetryUploader.DrainAsync(directory, lockPath, timeout.Token);
            Assert.True(delivered.Task.IsCompletedSuccessfully);
        }, storage, lockPath, new RemoteInvokeOptions { TimeOut = 360_000 }).Dispose();
    }

    private static TracerProvider CreateRecoveryProvider(string storage, HttpClient client)
        => Sdk.CreateTracerProviderBuilder()
            .AddSource("AgentRecoveryTest")
            .AddAzureMonitorTraceExporter(options =>
            {
                options.ConnectionString = "InstrumentationKey=11111111-1111-1111-1111-111111111111;IngestionEndpoint=https://localhost/";
                options.StorageDirectory = storage;
                options.EnableLiveMetrics = false;
                options.TracesPerSecond = null;
                options.SamplingRatio = 1;
                options.Transport = new HttpClientTransport(client);
            })
            .Build();

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ConfigureExporterForProcess_SelectsLocalCIAndAgentPolicies(bool isAgentTelemetryInvocation, bool isCIEnvironment)
    {
        using var process = RemoteExecutor.Invoke(static (agentValue, ciValue) =>
        {
            var isAgent = bool.Parse(agentValue);
            var isCI = bool.Parse(ciValue);
            var mode = TelemetryManager.ConfigureExporterForProcess(isAgent, isCI);
            var persist = isAgent || !isCI;

            Assert.Equal(isAgent ? ReportedTelemetryMode.Agent : isCI ? ReportedTelemetryMode.CI : ReportedTelemetryMode.Local, mode);
            Assert.True(AppContext.TryGetSwitch("Azure.Monitor.OpenTelemetry.Exporter.PersistOnForceFlush", out var persistOnForceFlush));
            Assert.Equal(persist, persistOnForceFlush);
            Assert.True(AppContext.TryGetSwitch("Azure.Monitor.OpenTelemetry.Exporter.DisablePersistOnShutdown", out var disable));
            Assert.Equal(!persist, disable);
            Assert.Equal(0, AppContext.GetData("Azure.Monitor.OpenTelemetry.Exporter.ShutdownDrainBudgetMilliseconds"));
        }, isAgentTelemetryInvocation.ToString(), isCIEnvironment.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ForceFlush_LocalTelemetryReturnsBeforeUpload(bool isCIEnvironment)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var process = RemoteExecutor.Invoke(static async (storageDirectory, value) =>
        {
            var persist = !bool.Parse(value);
            TelemetryManager.ConfigureExporterForProcess(isAgentTelemetryInvocation: false, isCIEnvironment: !persist);
            var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseUpload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var uploaded = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var handler = new MockHttpMessageHandler(async (request, cancellationToken) =>
            {
                var payload = await request.Content!.ReadAsStringAsync(cancellationToken);
                // The exporter also sends its resource/SDK metrics. Only gate our trace batch.
                if (payload.Contains(TelemetryConstants.Tags.AgentEventType, StringComparison.Ordinal))
                {
                    requestStarted.TrySetResult();
                    await releaseUpload.Task.WaitAsync(cancellationToken);
                    uploaded.TrySetResult(payload);
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"itemsReceived":3,"itemsAccepted":3,"errors":[]}""")
                };
            });
            using var httpClient = new HttpClient(handler);
            using var provider = Sdk.CreateTracerProviderBuilder()
                .AddSource("AgentPersistenceTest")
                .AddAzureMonitorTraceExporter(options =>
                {
                    // Synthetic data never reaches Azure: all requests use the in-process handler.
                    options.ConnectionString = $"InstrumentationKey={Guid.NewGuid()};IngestionEndpoint=https://localhost/";
                    options.StorageDirectory = storageDirectory;
                    options.EnableLiveMetrics = false;
                    options.TracesPerSecond = null;
                    options.SamplingRatio = 1;
                    options.Transport = new HttpClientTransport(httpClient);
                })
                .Build();
            using var source = new ActivitySource("AgentPersistenceTest");
            string[] eventTypes = ["skill_invocation", "tool_invocation", "reference_file_read"];
            foreach (var eventType in eventTypes)
            {
                using var activity = source.StartActivity(TelemetryConstants.Activities.AgentTelemetry);
                Assert.NotNull(activity);
                activity.SetTag(TelemetryConstants.Tags.AgentEventType, eventType);
            }

            try
            {
                var flush = Task.Run(() => provider.ForceFlush(10_000));
                if (persist)
                {
                    // A network-backed ForceFlush cannot finish until releaseUpload is signalled.
                    Assert.True(await flush.DefaultTimeout());
                }

                await requestStarted.Task.DefaultTimeout();
                Assert.False(uploaded.Task.IsCompleted);
                if (persist)
                {
                    Assert.NotEmpty(Directory.EnumerateFiles(storageDirectory, "*", SearchOption.AllDirectories));
                }
                else
                {
                    Assert.False(flush.IsCompleted);
                }

                releaseUpload.SetResult();
                Assert.True(await flush.DefaultTimeout());
                var payload = await uploaded.Task.DefaultTimeout();
                var actualEventTypes = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line =>
                    {
                        using var envelope = JsonDocument.Parse(line);
                        return envelope.RootElement.GetProperty("data").GetProperty("baseData").TryGetProperty("properties", out var properties)
                            && properties.TryGetProperty(TelemetryConstants.Tags.AgentEventType, out var eventType)
                            ? eventType.GetString()
                            : null;
                    })
                    .Where(eventType => eventType is not null);
                Assert.Equal(eventTypes.Order(), actualEventTypes.Order());
            }
            finally
            {
                releaseUpload.TrySetResult();
                provider.Shutdown();
            }
        }, workspace.Path, isCIEnvironment.ToString());
    }
}
