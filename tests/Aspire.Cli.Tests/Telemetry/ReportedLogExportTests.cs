// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Interaction;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Shared.Telemetry;
using Azure.Core.Pipeline;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.DotNet.RemoteExecutor;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using OpenTelemetry.Trace;

namespace Aspire.Cli.Tests.Telemetry;

public class ReportedLogExportTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task ReportedResource_ExportsOnlyApprovedAttributes()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using (var process = RemoteExecutor.Invoke(static async directory =>
        {
            // Statsbeat uses a separate transport; this test covers the product envelopes only.
            Environment.SetEnvironmentVariable("APPLICATIONINSIGHTS_STATSBEAT_DISABLED", "true");
            Environment.SetEnvironmentVariable("OTEL_DOTNET_AZURE_MONITOR_ENABLE_RESOURCE_METRICS", "true");
            Environment.SetEnvironmentVariable("OTEL_SERVICE_NAME", "sensitive-service");
            Environment.SetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES",
                "customer.tenant=sensitive-tenant,deployment.path=sensitive-path,service.namespace=sensitive-namespace,service.instance.id=sensitive-instance");
            var configuration = new TelemetryConfiguration { ReportedTelemetryEnabled = true };
            using var fixture = new TelemetryFixture(telemetryConfiguration: configuration, initialize: false);
            var exported = new ConcurrentQueue<JsonElement>();
            using var handler = CreateHandler(exported);
            using var client = new HttpClient(handler);
            using var manager = new TelemetryManager(configuration, fixture.TagsSource, fixture.Telemetry, NullLogger<TelemetryManager>.Instance,
                (resource, _) => AzureMonitorTelemetryProvider.Create(new ServiceCollection(), resource,
                    AspireCliTelemetry.ReportedActivitySourceName, AspireCliTelemetry.EventLogCategoryName,
                    $"InstrumentationKey={Guid.NewGuid()};IngestionEndpoint=https://localhost/", directory,
                    builder => builder.ConfigureServices(services => services.Configure<AzureMonitorExporterOptions>(
                        options => options.Transport = new HttpClientTransport(client))),
                    options => options.Transport = new HttpClientTransport(client)));
            manager.Initialize();
            using var source = new ActivitySource(AspireCliTelemetry.ReportedActivitySourceName);
            using (var activity = source.StartActivity("reported-operation"))
            {
                Assert.NotNull(activity);
                fixture.Telemetry.RecordEvent("reported-event", [new("test.property", "approved")]);
            }

            Assert.True(await manager.ForceFlushReportedAsync());
            Assert.True(await manager.TryShutdownAsync());
            var records = exported.OrderBy(record => record.GetProperty("data").GetProperty("baseType").GetString(), StringComparer.Ordinal).ToArray();
            Assert.Equal(["MessageData", "MetricData", "RemoteDependencyData"],
                records.Select(record => record.GetProperty("data").GetProperty("baseType").GetString()).ToArray());
            await File.WriteAllTextAsync(Path.Combine(directory, "envelopes.json"), JsonSerializer.Serialize(records));
        }, workspace.Path))
        {
        }

        var envelopes = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(workspace.Path, "envelopes.json")))!.AsArray();
        foreach (var envelope in envelopes)
        {
            var tags = envelope!["tags"]!.AsObject();
            envelope["time"] = "timestamp";
            envelope["iKey"] = "instrumentation-key";
            tags["ai.cloud.roleInstance"] = "instance-id";
            tags["ai.application.ver"] = "cli-version";
            tags["ai.internal.sdkVersion"] = "sdk-version";
            if (tags.ContainsKey("ai.operation.id"))
            {
                tags["ai.operation.id"] = "trace-id";
            }
            if (tags.ContainsKey("ai.operation.parentId"))
            {
                tags["ai.operation.parentId"] = "span-id";
            }

            var data = envelope["data"]!;
            var baseData = data["baseData"]!;
            switch (data["baseType"]!.GetValue<string>())
            {
                case "MetricData":
                    baseData["properties"]!["service.instance.id"] = "instance-id";
                    baseData["properties"]!["service.version"] = "cli-version";
                    break;
                case "RemoteDependencyData":
                    baseData["id"] = "span-id";
                    baseData["duration"] = "duration";
                    break;
            }
        }

        await Verify(envelopes.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), "json").UseDirectory("Snapshots");
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void ReportedLogs_HonorConsentAndFlushWithoutExportingOrdinaryLogs(bool enabled, bool forceFlush)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var process = RemoteExecutor.Invoke(static async (directory, enabledValue, flushValue) =>
        {
            Environment.SetEnvironmentVariable("APPLICATIONINSIGHTS_STATSBEAT_DISABLED", "true");
            Environment.SetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES", null);
            var isEnabled = bool.Parse(enabledValue);
            var configuration = new TelemetryConfiguration { ReportedTelemetryEnabled = isEnabled };
            var loggingOptions = new Program.CliLoggingOptions(LogLevel.Error, false, directory, Path.Combine(directory, "cli.log"));
            var (factory, fileProvider) = Program.CreateLoggerFactory(
                [], loggingOptions, new TestStartupErrorWriter(), new ConsoleLogBufferContext());
            using var loggerFactory = factory;
            using var fileLogger = fileProvider;
            var localSink = new TestSink();
            factory.AddProvider(new TestLoggerProvider(localSink));
            var tagsSource = new TelemetryTagsSource(NullLogger<TelemetryTagsSource>.Instance);
            using var telemetry = CreateTelemetry(factory, configuration, tagsSource);
            telemetry.Initialize();
            await tagsSource.TagsTask;
            var exported = new ConcurrentQueue<JsonElement>();
            using var handler = CreateHandler(exported);
            using var client = new HttpClient(handler);
            var configured = 0;
            using var manager = new TelemetryManager(configuration, tagsSource, telemetry, factory.CreateLogger<TelemetryManager>(),
                (resource, storage) =>
                {
                    configured++;
                    Assert.Equal(Aspire.Shared.Telemetry.AspireTelemetryExporter.GetTelemetryStoragePath("cli"), storage);
                    return AzureMonitorTelemetryProvider.Create(new ServiceCollection(), resource,
                        AspireCliTelemetry.ReportedActivitySourceName, AspireCliTelemetry.EventLogCategoryName,
                        $"InstrumentationKey={Guid.NewGuid()};IngestionEndpoint=https://localhost/",
                        Path.Combine(directory, "storage"), builder =>
                        {
                            // Azure Monitor shares a transmitter across signals for this destination.
                            builder.ConfigureServices(services => services.Configure<AzureMonitorExporterOptions>(
                                options => options.Transport = new HttpClientTransport(client)));
                        },
                        options =>
                        {
                            Assert.Equal(Path.Combine(directory, "storage"), options.StorageDirectory);
                            options.Transport = new HttpClientTransport(client);
                        });
                });

            telemetry.RecordEvent("before-initialization");
            using var reportedSource = new ActivitySource(AspireCliTelemetry.ReportedActivitySourceName);
            manager.Initialize();
            manager.Initialize();
            Assert.Equal(isEnabled ? 1 : 0, configured);
            Assert.Equal(isEnabled, manager.HasAzureMonitor);
            Assert.Equal(isEnabled, reportedSource.HasListeners());

            var ordinaryLogger = factory.CreateLogger<AspireCliTelemetry>();
            ordinaryLogger.LogError(new InvalidOperationException("local diagnostic"), "Ordinary CLI error");
            var eventLogger = factory.CreateLogger(AspireCliTelemetry.EventLogCategoryName);
            eventLogger.LogDebug("Verbose product log");
            factory.CreateLogger(AspireCliTelemetry.EventLogCategoryName + ".Other").LogError("Wrong event category");
            using var activity = new Activity("ambient").Start();
            using (eventLogger.BeginScope(new Dictionary<string, object?> { ["secret"] = "workspace path" }))
            {
                telemetry.RecordEvent("test-event", [new("test.property", 42)]);
                telemetry.RecordError("Local error", new InvalidOperationException("reported error"));
            }

            if (bool.Parse(flushValue))
            {
                Assert.True(await manager.ForceFlushReportedAsync());
            }
            Assert.True(await manager.TryShutdownAsync());
            Assert.False(reportedSource.HasListeners());
            Assert.Same(manager.TryShutdownAsync(), manager.TryShutdownAsync());
            var records = exported.ToArray();
            Assert.Equal(isEnabled ? ["test-event", TelemetryConstants.Events.Error] : [],
                records.Select(record => record.GetProperty("data").GetProperty("baseData").GetProperty("message").GetString()).ToArray());
            foreach (var record in records)
            {
                Assert.Equal("MessageData", record.GetProperty("data").GetProperty("baseType").GetString());
                Assert.Equal("aspire-cli", record.GetProperty("tags").GetProperty("ai.cloud.role").GetString());
                var properties = record.GetProperty("data").GetProperty("baseData").GetProperty("properties");
                Assert.Equal(AspireCliTelemetry.EventLogCategoryName, properties.GetProperty("CategoryName").GetString());
                Assert.False(properties.TryGetProperty("secret", out _));
                Assert.Equal(activity.TraceId.ToHexString(), record.GetProperty("tags").GetProperty("ai.operation.id").GetString());
                Assert.Equal(activity.SpanId.ToHexString(), record.GetProperty("tags").GetProperty("ai.operation.parentId").GetString());
            }
            if (isEnabled)
            {
                Assert.Equal("test-event", records[0].GetProperty("tags").GetProperty("ai.operation.name").GetString());
                Assert.Equal("System.InvalidOperationException", records[1].GetProperty("data").GetProperty("baseData")
                    .GetProperty("properties").GetProperty("exception.type").GetString());
            }

            ordinaryLogger.LogError("Still logging locally");
            Assert.Equal("Still logging locally", localSink.Writes.Last().Message);
            manager.Dispose();
            telemetry.RecordEvent("after-disposal");
            ordinaryLogger.LogError("Still logging after telemetry disposal");
            Assert.Equal("Still logging after telemetry disposal", localSink.Writes.Last().Message);
            Assert.Equal(records.Length, exported.Count);
        }, workspace.Path, enabled.ToString(), forceFlush.ToString());
    }

    [Theory]
    [InlineData("Array")]
    [InlineData("List")]
    [InlineData("Enumerable")]
    public void ReportedCollections_ReachAzureMonitorAsCommaSeparatedStrings(string collectionType)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var process = RemoteExecutor.Invoke(static async (directory, shape) =>
        {
            Environment.SetEnvironmentVariable("APPLICATIONINSIGHTS_STATSBEAT_DISABLED", "true");
            using var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.None));
            using var telemetry = CreateTelemetry(factory, new TelemetryConfiguration { ReportedTelemetryEnabled = true },
                new TelemetryTagsSource(NullLogger<TelemetryTagsSource>.Instance));
            var exported = new ConcurrentQueue<JsonElement>();
            using var handler = CreateHandler(exported);
            using var client = new HttpClient(handler);
            using var logProvider = AzureMonitorTelemetryProvider.Create(new ServiceCollection(), TelemetryManager.CreateReportedResourceBuilder(),
                AspireCliTelemetry.ReportedActivitySourceName, AspireCliTelemetry.EventLogCategoryName,
                $"InstrumentationKey={Guid.NewGuid()};IngestionEndpoint=https://localhost/",
                directory, builder =>
                {
                    builder.ConfigureServices(services => services.Configure<AzureMonitorExporterOptions>(
                        options => options.Transport = new HttpClientTransport(client)));
                },
                options => options.Transport = new HttpClientTransport(client));
            telemetry.SetEventLogger(logProvider.EventLogger);
            string[] expected = ["container", "project", "quote\"and\\slash", "line\nbreak", "comma,value", ""];
            IEnumerable<string> values = shape switch
            {
                "Array" => expected,
                "List" => expected.ToList(),
                "Enumerable" => expected.Select(value => value),
                _ => throw new ArgumentOutOfRangeException(nameof(shape))
            };
            using var activity = new Activity("typed-span");
            telemetry.SetActivityProperty(activity, "test.collection", expected);
            telemetry.RecordEvent("collection-event", [new("test.collection", values), new("test.empty", Array.Empty<string>())]);
            Assert.Same(expected, activity.GetTagItem("test.collection"));
            Assert.True(await logProvider.ForceFlushAsync(5000));

            var record = Assert.Single(exported);
            Assert.Equal("MessageData", record.GetProperty("data").GetProperty("baseType").GetString());
            var actual = record.GetProperty("data").GetProperty("baseData").GetProperty("properties")
                .GetProperty("test.collection").GetString();
            Assert.Equal("container,project,quote\"and\\slash,line\nbreak,comma,value,", actual);
            Assert.Equal("", record.GetProperty("data").GetProperty("baseData").GetProperty("properties").GetProperty("test.empty").GetString());
        }, workspace.Path, collectionType);
    }

    private static AspireCliTelemetry CreateTelemetry(ILoggerFactory factory, TelemetryConfiguration configuration, TelemetryTagsSource tagsSource) =>
        new(factory.CreateLogger<AspireCliTelemetry>(),
            new TelemetryFixture.TestMachineInformationProvider(), new TelemetryFixture.TestCIEnvironmentDetector(),
            new TelemetryFixture.TestCodingAgentDetector(), new TelemetryFixture.TestInternalMicrosoftDetector(),
            configuration, TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(AppContext.BaseDirectory)), tagsSource);

    private static MockHttpMessageHandler CreateHandler(ConcurrentQueue<JsonElement> exported) =>
        new(async (request, cancellationToken) =>
        {
            var payload = await request.Content!.ReadAsStringAsync(cancellationToken);
            // Application Insights ingestion accepts newline-delimited envelopes:
            // {"data":{"baseType":"MessageData","baseData":{"message":"test-event","properties":{...}}},...}
            var lines = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                using var envelope = JsonDocument.Parse(line);
                exported.Enqueue(envelope.RootElement.Clone());
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { itemsReceived = lines.Length, itemsAccepted = lines.Length, errors = Array.Empty<object>() }))
            };
        });
}
