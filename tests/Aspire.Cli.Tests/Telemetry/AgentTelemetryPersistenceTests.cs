// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.Utils;
using Azure.Core.Pipeline;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.DotNet.RemoteExecutor;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Aspire.Cli.Tests.Telemetry;

public class AgentTelemetryPersistenceTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task ForceFlushReportedAsync_WithoutProviderSucceeds()
    {
        using var manager = new TelemetryManager(
            new TelemetryConfiguration { ReportedTelemetryEnabled = false },
            new TelemetryTagsSource(NullLogger<TelemetryTagsSource>.Instance));

        Assert.True(await manager.ForceFlushReportedAsync().DefaultTimeout());
    }

    [Fact]
    public void Uploader_DeliversPersistedEventAfterProducerExits()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var storage = workspace.CreateDirectory("storage").FullName;
        var lockPath = Path.Combine(workspace.Path, "uploader.lock");
        RemoteExecutor.Invoke(static async directory =>
        {
            TelemetryManager.ConfigureExporterForProcess(isAgentTelemetryInvocation: true);
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
            TelemetryManager.ConfigureExporterForProcess(isAgentTelemetryInvocation: true);
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
    [InlineData(true)]
    [InlineData(false)]
    public void ConfigureExporterForProcess_PreservesOrdinaryCommands(bool isAgentTelemetryInvocation)
    {
        using var process = RemoteExecutor.Invoke(static value =>
        {
            var isAgent = bool.Parse(value);
            TelemetryManager.ConfigureExporterForProcess(isAgent);

            Assert.True(AppContext.TryGetSwitch("Azure.Monitor.OpenTelemetry.Exporter.PersistOnForceFlush", out var persist));
            Assert.Equal(isAgent, persist);
            Assert.True(AppContext.TryGetSwitch("Azure.Monitor.OpenTelemetry.Exporter.DisablePersistOnShutdown", out var disable));
            Assert.Equal(!isAgent, disable);
            Assert.Equal(isAgent ? 0 : null, AppContext.GetData("Azure.Monitor.OpenTelemetry.Exporter.ShutdownDrainBudgetMilliseconds"));
        }, isAgentTelemetryInvocation.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ForceFlush_OnlyAgentTelemetryReturnsBeforeUpload(bool isAgentTelemetryInvocation)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var process = RemoteExecutor.Invoke(static async (storageDirectory, value) =>
        {
            var isAgent = bool.Parse(value);
            TelemetryManager.ConfigureExporterForProcess(isAgent);
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
                if (isAgent)
                {
                    // A network-backed ForceFlush cannot finish until releaseUpload is signalled.
                    Assert.True(await flush.DefaultTimeout());
                }

                await requestStarted.Task.DefaultTimeout();
                Assert.False(uploaded.Task.IsCompleted);
                if (isAgent)
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
        }, workspace.Path, isAgentTelemetryInvocation.ToString());
    }
}
