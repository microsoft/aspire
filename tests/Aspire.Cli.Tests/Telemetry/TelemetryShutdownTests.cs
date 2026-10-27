// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.Utils;
using Aspire.Shared.Telemetry;
using Aspire.Tests.Shared.Telemetry;
using Azure.Core.Pipeline;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.DotNet.RemoteExecutor;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;

namespace Aspire.Cli.Tests.Telemetry;

public class TelemetryShutdownTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(nameof(ReportedTelemetryMode.Local), CliExitCodes.Success, Timeout.Infinite)]
    [InlineData(nameof(ReportedTelemetryMode.Local), CliExitCodes.Cancelled, Timeout.Infinite)]
    [InlineData(nameof(ReportedTelemetryMode.Local), CliExitCodes.InvalidCommand, 300)]
    [InlineData(nameof(ReportedTelemetryMode.CI), CliExitCodes.Success, 5000)]
    [InlineData(nameof(ReportedTelemetryMode.CI), CliExitCodes.Cancelled, 5000)]
    [InlineData(nameof(ReportedTelemetryMode.CI), CliExitCodes.InvalidCommand, 5000)]
    [InlineData(nameof(ReportedTelemetryMode.Agent), CliExitCodes.Success, Timeout.Infinite)]
    [InlineData(nameof(ReportedTelemetryMode.Agent), CliExitCodes.InvalidCommand, Timeout.Infinite)]
    public void Shutdown_SelectsBudgetForBothSignalsAndRestoresDrainBudget(string modeName, int exitCode, int expectedTimeout)
    {
        using var process = RemoteExecutor.Invoke(static async (modeValue, exitValue, timeoutValue) =>
        {
            var mode = Enum.Parse<ReportedTelemetryMode>(modeValue);
            var exitCode = int.Parse(exitValue);
            var expectedTimeout = int.Parse(timeoutValue);
            TelemetryManager.ConfigureExporterForProcess(mode == ReportedTelemetryMode.Agent, mode == ReportedTelemetryMode.CI);
            var calls = new ConcurrentQueue<(string Signal, int Timeout, int DrainBudget)>();
            bool Shutdown(string signal, int timeout)
            {
                calls.Enqueue((signal, timeout,
                    Assert.IsType<int>(AppContext.GetData("Azure.Monitor.OpenTelemetry.Exporter.ShutdownDrainBudgetMilliseconds"))));
                return true;
            }

            var traceProcessor = new TestTelemetryProcessor<Activity> { ShutdownCallback = timeout => Shutdown("trace", timeout) };
            var logProcessor = new TestTelemetryProcessor<LogRecord> { ShutdownCallback = timeout => Shutdown("log", timeout) };
            using var fixture = new TelemetryFixture(initialize: false);
            using var manager = new TelemetryManager(
                new TelemetryConfiguration { ReportedTelemetryEnabled = true }, fixture.Telemetry,
                NullLogger<TelemetryManager>.Instance,
                (resource, _) => AzureMonitorTelemetryProvider.Create(new ServiceCollection(), resource, AspireCliTelemetry.EventLogCategoryName,
                    () => Sdk.CreateTracerProviderBuilder().AddProcessor(traceProcessor).Build(),
                    provider => provider.AddProcessor(logProcessor)));
            manager.Initialize();

            var shutdown = manager.TryShutdownAsync(exitCode, mode);
            Assert.Same(shutdown, manager.TryShutdownAsync(CliExitCodes.Success, mode));
            Assert.True(await shutdown);
            var expectedDrainBudget = expectedTimeout == 300 ? 300 : 0;
            Assert.Equal(
                [("log", expectedTimeout, expectedDrainBudget), ("trace", expectedTimeout, expectedDrainBudget)],
                calls.OrderBy(call => call.Signal).ToArray());
            Assert.Equal(0, AppContext.GetData("Azure.Monitor.OpenTelemetry.Exporter.ShutdownDrainBudgetMilliseconds"));
            manager.Dispose();
            Assert.Equal(1, traceProcessor.DisposeCount);
            Assert.Equal(1, logProcessor.DisposeCount);
        }, modeName, exitCode.ToString(), expectedTimeout.ToString());
    }

    [Fact]
    public async Task Shutdown_FailedDrainLogsWarningAndRestoresFailureBudget()
    {
        outputHelper.WriteLine($"[{DateTimeOffset.UtcNow:O}] Starting remote telemetry shutdown test.");
        using (var process = RemoteExecutor.Invoke(static async () =>
        {
            Console.WriteLine($"[{DateTimeOffset.UtcNow:O}] Telemetry test child PID={Environment.ProcessId} entered.");
            var mode = TelemetryManager.ConfigureExporterForProcess(isAgentTelemetryInvocation: false, isCIEnvironment: false);
            var sink = new TestSink();
            using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new TestLoggerProvider(sink)));
            using var fixture = new TelemetryFixture(initialize: false);
            var logProcessor = new TestTelemetryProcessor<LogRecord> { ShutdownCallback = static _ => false };
            using var manager = new TelemetryManager(
                new TelemetryConfiguration { ReportedTelemetryEnabled = true }, fixture.Telemetry,
                loggerFactory.CreateLogger<TelemetryManager>(),
                (resource, _) => AzureMonitorTelemetryProvider.Create(new ServiceCollection(), resource, AspireCliTelemetry.EventLogCategoryName,
                    () => Sdk.CreateTracerProviderBuilder().Build(), provider => provider.AddProcessor(logProcessor)));
            Console.WriteLine($"[{DateTimeOffset.UtcNow:O}] Initializing telemetry manager.");
            manager.Initialize();
            Console.WriteLine($"[{DateTimeOffset.UtcNow:O}] Telemetry initialized; requesting failure-budget shutdown.");

            Assert.True(await manager.TryShutdownAsync(CliExitCodes.InvalidCommand, mode));
            Console.WriteLine($"[{DateTimeOffset.UtcNow:O}] Telemetry shutdown returned; validating warning and restored budget.");

            var warning = Assert.Single(sink.Writes);
            Assert.Equal(LogLevel.Warning, warning.LogLevel);
            Assert.Equal("Timed out shutting down CLI reported telemetry.", warning.Message);
            Assert.Equal(0, AppContext.GetData("Azure.Monitor.OpenTelemetry.Exporter.ShutdownDrainBudgetMilliseconds"));
            Console.WriteLine($"[{DateTimeOffset.UtcNow:O}] Telemetry assertions passed; disposing child services.");
        }, new RemoteInvokeOptions { StartInfo = { RedirectStandardOutput = true } }))
        {
            outputHelper.WriteLine($"[{DateTimeOffset.UtcNow:O}] Remote telemetry child PID={process.Process.Id}; waiting for completion and disposal.");
            // Forward while the remote handle waits, so the last child phase is retained in
            // this test's TRX output even if remote execution hangs.
            var output = ForwardOutputAsync(process.Process.StandardOutput);
            await output.DefaultTimeout();
        }
        outputHelper.WriteLine($"[{DateTimeOffset.UtcNow:O}] Remote telemetry test completed and disposed.");

        async Task ForwardOutputAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                outputHelper.WriteLine(line);
            }
        }
    }

    [Theory]
    [InlineData("Local")]
    [InlineData("CI")]
    [InlineData("Agent")]
    [InlineData("Dashboard")]
    [InlineData("RetryableCI")]
    [InlineData("TimedOutCI")]
    [InlineData("UnavailableStorage")]
    public void Shutdown_HandlesBlockedDeliveryAndStorage(string scenario)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var process = RemoteExecutor.Invoke(static async (directory, scenario) =>
        {
            Environment.SetEnvironmentVariable("APPLICATIONINSIGHTS_STATSBEAT_DISABLED", "true");
            var dashboard = scenario == "Dashboard";
            var isCI = scenario is "CI" or "RetryableCI" or "TimedOutCI";
            var persistsBeforeDelivery = scenario is "Local" or "Agent" or "Dashboard";
            var mode = TelemetryManager.ConfigureExporterForProcess(scenario == "Agent", isCI);
            if (dashboard)
            {
                // The dashboard deliberately retains the service defaults, not the CLI's zero drain.
                AppContext.SetSwitch("Azure.Monitor.OpenTelemetry.Exporter.PersistOnForceFlush", false);
                AppContext.SetData("Azure.Monitor.OpenTelemetry.Exporter.ShutdownDrainBudgetMilliseconds", null);
            }
            var storage = Path.Combine(directory, "storage");
            if (scenario == "UnavailableStorage")
            {
                await File.WriteAllTextAsync(storage, "A file occupies the requested storage directory.");
            }
            var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var exported = new ConcurrentQueue<(string Type, string Name)>();
            (string Type, string Name)[] expectedRecords =
                [("MessageData", "event-0"), ("MessageData", "event-1"), ("MessageData", "event-2"),
                 ("RemoteDependencyData", "operation-0"), ("RemoteDependencyData", "operation-1"), ("RemoteDependencyData", "operation-2")];

            static List<(string Type, string Name)> ReadRecords(IEnumerable<string> lines)
            {
                // Azure sends and persists newline-delimited envelopes:
                // {"data":{"baseType":"MessageData","baseData":{"message":"event-0",...}},...}
                // Trace envelopes use RemoteDependencyData/name; resource metrics share this transport.
                var records = new List<(string Type, string Name)>();
                foreach (var line in lines)
                {
                    using var envelope = JsonDocument.Parse(line);
                    var data = envelope.RootElement.GetProperty("data");
                    var type = data.GetProperty("baseType").GetString();
                    var baseData = data.GetProperty("baseData");
                    if (type is "MessageData" or "RemoteDependencyData")
                    {
                        records.Add((type, baseData.GetProperty(type == "MessageData" ? "message" : "name").GetString()!));
                    }
                }
                return records;
            }

            using var handler = new MockHttpMessageHandler(async (request, cancellationToken) =>
            {
                var payload = await request.Content!.ReadAsStringAsync(cancellationToken);
                var lines = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var records = ReadRecords(lines);
                if (records.Count > 0)
                {
                    requestStarted.TrySetResult();
                    await releaseResponse.Task.WaitAsync(cancellationToken);
                    if (scenario == "RetryableCI")
                    {
                        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                    }
                    foreach (var record in records)
                    {
                        exported.Enqueue(record);
                    }
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { itemsReceived = lines.Length, itemsAccepted = lines.Length, errors = Array.Empty<object>() }))
                };
            });
            using var client = new HttpClient(handler);
            void ConfigureTransport(AzureMonitorExporterOptions options)
            {
                options.Transport = new HttpClientTransport(client);
                // Exercise the exporter's retryable-response storage behavior without Azure.Core backoff.
                options.Retry.MaxRetries = 0;
            }

            using var fixture = new TelemetryFixture(initialize: false);
            AzureMonitorTelemetryProvider? reportedProvider = null;
            AzureMonitorExporterOptions? traceOptions = null;
            using var manager = new TelemetryManager(
                new TelemetryConfiguration { ReportedTelemetryEnabled = true }, fixture.Telemetry,
                NullLogger<TelemetryManager>.Instance,
                (resource, _) => reportedProvider = AzureMonitorTelemetryProvider.Create(new ServiceCollection(), resource,
                    AspireCliTelemetry.ReportedActivitySourceName, AspireCliTelemetry.EventLogCategoryName,
                    $"InstrumentationKey={Guid.NewGuid()};IngestionEndpoint=https://localhost/", storage,
                    builder =>
                    {
                        if (persistsBeforeDelivery)
                        {
                            // Start the log drain before traces are persisted, reproducing a drain
                            // whose storage snapshot contains only one of the two signals.
                            builder.AddProcessor(new TestTelemetryProcessor<Activity>
                            {
                                ShutdownCallback = _ =>
                                {
                                    requestStarted.Task.DefaultTimeout().GetAwaiter().GetResult();
                                    return true;
                                }
                            });
                        }
                        builder.ConfigureServices(services => services.Configure<AzureMonitorExporterOptions>(options =>
                        {
                            traceOptions = options;
                            ConfigureTransport(options);
                        }));
                    },
                    options =>
                    {
                        Assert.Equal(TimeSpan.FromSeconds(5), options.Retry.NetworkTimeout);
                        ConfigureTransport(options);
                    }));
            manager.Initialize();
            Assert.NotNull(reportedProvider);
            Assert.NotNull(traceOptions);
            Assert.Equal(TimeSpan.FromSeconds(5), traceOptions.Retry.NetworkTimeout);
            using var source = new ActivitySource(AspireCliTelemetry.ReportedActivitySourceName);
            for (var i = 0; i < 3; i++)
            {
                using var activity = source.StartActivity($"operation-{i}");
                Assert.NotNull(activity);
                fixture.Telemetry.RecordEvent($"event-{i}");
            }

            try
            {
                var shutdown = dashboard ? reportedProvider.ShutdownAsync(5000)
                    : manager.TryShutdownAsync(CliExitCodes.Success, mode);
                await requestStarted.Task.DefaultTimeout();
                if (persistsBeforeDelivery)
                {
                    Assert.True(await shutdown.DefaultTimeout());
                    Assert.Empty(exported);
                    // The shared drain snapshots storage before both signals necessarily persist.
                    // Late batches remain durable for a later pass, so immediate delivery of all
                    // records is not guaranteed. Check every persisted record while uploads block.
                    var storedRecords = ReadRecords(Directory.EnumerateFiles(storage, "*", SearchOption.AllDirectories).SelectMany(File.ReadLines));
                    Assert.Equal(expectedRecords, storedRecords.OrderBy(record => record.Type).ThenBy(record => record.Name).ToArray());
                    return;
                }
                else if (scenario == "TimedOutCI")
                {
                    // Provider and network budgets must permit shutdown without a response. Its result
                    // means lifecycle completion, not ingestion acceptance or a changed command result.
                    Assert.True(await shutdown.DefaultTimeout());
                    Assert.Empty(exported);
                    return;
                }
                else
                {
                    Assert.False(shutdown.IsCompleted);
                }

                releaseResponse.SetResult();
                Assert.True(await shutdown.DefaultTimeout());
                if (scenario == "RetryableCI")
                {
                    Assert.Empty(exported);
                    Assert.NotEmpty(Directory.EnumerateFiles(storage, "*", SearchOption.AllDirectories));
                }
                else
                {
                    Assert.Equal(expectedRecords, exported.OrderBy(record => record.Type).ThenBy(record => record.Name).ToArray());
                }
            }
            finally
            {
                releaseResponse.TrySetResult();
                await manager.TryShutdownAsync(CliExitCodes.Success, mode).DefaultTimeout();
            }
        }, workspace.Path, scenario);
    }
}
