// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Compression;
using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Otlp.Serialization;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aspire.Cli.Tests.Commands;

public class ExportCommandTests(ITestOutputHelper outputHelper)
{
    private static readonly DateTime s_testTime = TelemetryTestHelper.s_testTime;

    [Fact]
    public async Task ExportCommand_WritesZipWithExpectedData()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputPath = Path.Combine(workspace.WorkspaceRoot.FullName, "export.zip");

        var resources = new[]
        {
            new ResourceInfoJson { Name = "redis", InstanceId = null },
            new ResourceInfoJson { Name = "apiservice", InstanceId = null },
        };

        var logsJson = BuildLogsJson(
            ("redis", null, 9, "Information", "Ready to accept connections", s_testTime),
            ("apiservice", null, 9, "Information", "Request received", s_testTime.AddSeconds(1)));

        var tracesJson = BuildTracesJson(
            ("apiservice", null, "span001", "GET /api/products", s_testTime, s_testTime.AddMilliseconds(50), false));

        var provider = CreateExportTestServices(workspace, resources,
            telemetryEndpoints: new Dictionary<string, string>
            {
                ["/api/telemetry/logs"] = logsJson,
                ["/api/telemetry/traces"] = tracesJson,
            },
            resourceSnapshots:
            [
                new ResourceSnapshot { Name = "redis", DisplayName = "redis", ResourceType = "Container", State = "Running" },
                new ResourceSnapshot { Name = "apiservice", DisplayName = "apiservice", ResourceType = "Project", State = "Running" },
            ],
            logLines:
            [
                new ResourceLogLine { ResourceName = "redis", LineNumber = 1, Content = "Redis is starting" },
                new ResourceLogLine { ResourceName = "redis", LineNumber = 2, Content = "Ready to accept connections" },
                new ResourceLogLine { ResourceName = "apiservice", LineNumber = 1, Content = "Now listening on: https://localhost:5001" },
            ]);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"export --output {outputPath}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.True(File.Exists(outputPath), "Export zip file should be created");

        using var archive = ZipFile.OpenRead(outputPath);
        var entryNames = archive.Entries.Select(e => e.FullName).OrderBy(n => n).ToList();

        Assert.Collection(entryNames,
            entry => Assert.Equal("consolelogs/apiservice.txt", entry),
            entry => Assert.Equal("consolelogs/redis.txt", entry),
            entry => Assert.Equal("resources/apiservice.json", entry),
            entry => Assert.Equal("resources/redis.json", entry),
            entry => Assert.Equal("structuredlogs/apiservice.json", entry),
            entry => Assert.Equal("structuredlogs/redis.json", entry),
            entry => Assert.Equal("traces/apiservice.json", entry));

        // Verify console log content
        var redisConsoleLog = ReadEntryText(archive, "consolelogs/redis.txt");
        Assert.Contains("Redis is starting", redisConsoleLog);
        Assert.Contains("Ready to accept connections", redisConsoleLog);

        var apiConsoleLog = ReadEntryText(archive, "consolelogs/apiservice.txt");
        Assert.Contains("Now listening on: https://localhost:5001", apiConsoleLog);

        // Verify resource JSON content
        var redisResourceJson = ReadEntryText(archive, "resources/redis.json");
        Assert.Contains("redis", redisResourceJson);
        Assert.Contains("Container", redisResourceJson);

        // Verify structured logs content is valid JSON (per resource)
        var apiStructuredLogsJson = ReadEntryText(archive, "structuredlogs/apiservice.json");
        var apiStructuredLogsData = JsonSerializer.Deserialize(apiStructuredLogsJson, OtlpJsonSerializerContext.Default.OtlpTelemetryDataJson);
        Assert.NotNull(apiStructuredLogsData?.ResourceLogs);
        Assert.NotEmpty(apiStructuredLogsData.ResourceLogs);

        var redisStructuredLogsJson = ReadEntryText(archive, "structuredlogs/redis.json");
        var redisStructuredLogsData = JsonSerializer.Deserialize(redisStructuredLogsJson, OtlpJsonSerializerContext.Default.OtlpTelemetryDataJson);
        Assert.NotNull(redisStructuredLogsData?.ResourceLogs);
        Assert.NotEmpty(redisStructuredLogsData.ResourceLogs);

        // Verify traces content is valid JSON (per resource)
        var tracesContent = ReadEntryText(archive, "traces/apiservice.json");
        var tracesData = JsonSerializer.Deserialize(tracesContent, OtlpJsonSerializerContext.Default.OtlpTelemetryDataJson);
        Assert.NotNull(tracesData?.ResourceSpans);
        Assert.NotEmpty(tracesData.ResourceSpans);
    }

    [Fact]
    public async Task ExportCommand_OutputOption_ConfiguresArchiveOutputLocation()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var customDir = Path.Combine(workspace.WorkspaceRoot.FullName, "custom", "nested");
        var outputPath = Path.Combine(customDir, "my-export.zip");

        var provider = CreateExportTestServices(workspace,
            resources: [new ResourceInfoJson { Name = "redis", InstanceId = null }],
            telemetryEndpoints: new Dictionary<string, string>
            {
                ["/api/telemetry/logs"] = BuildLogsJson(),
                ["/api/telemetry/traces"] = BuildTracesJson(),
            },
            resourceSnapshots:
            [
                new ResourceSnapshot { Name = "redis", DisplayName = "redis", ResourceType = "Container", State = "Running" },
            ],
            logLines: []);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"export --output {outputPath}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.True(File.Exists(outputPath), $"Export zip file should be created at the specified path: {outputPath}");
        Assert.True(Directory.Exists(customDir), "Nested output directory should be created automatically");
    }

    [Fact]
    public async Task ExportCommand_AppHostOption_UsesSpecifiedAppHost()
    {
        // When --apphost is specified, AppHostConnectionResolver uses a fast path
        // that looks for matching socket files on disk. Since no real socket exists
        // in tests, the command gracefully reports that no running AppHost was found.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var outputPath = Path.Combine(workspace.WorkspaceRoot.FullName, "export.zip");

        // Create the apphost project file on disk so the FileInfo option resolves
        var appHostDir = Path.Combine(workspace.WorkspaceRoot.FullName, "MyAppHost");
        Directory.CreateDirectory(appHostDir);
        var appHostProjectPath = Path.Combine(appHostDir, "MyAppHost.csproj");
        File.WriteAllText(appHostProjectPath, "<Project />");

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = outputWriter;
            options.DisableAnsi = true;
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"export --apphost {appHostProjectPath} --output {outputPath}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // The command succeeds but displays "not found" because there is no
        // socket file for the specified apphost.
        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.False(File.Exists(outputPath), "No zip should be created when the AppHost is not running");
    }

    [Fact]
    public async Task ExportCommand_SingleInScopeConnection_ExportsCorrectData()
    {
        // When --apphost is NOT specified and only one in-scope connection exists,
        // the resolver automatically selects it and exports data from that connection.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var outputPath = Path.Combine(workspace.WorkspaceRoot.FullName, "export.zip");

        var monitor = new TestAuxiliaryBackchannelMonitor();

        // Connection 1 – out-of-scope, should NOT be used
        var outOfScopeConnection = new TestAppHostAuxiliaryBackchannel
        {
            IsInScope = false,
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = Path.Combine("C:", "other", "OtherAppHost", "OtherAppHost.csproj"),
                ProcessId = 1111
            },
            DashboardInfoResponse = new GetDashboardInfoResponse
            {
                ApiBaseUrl = "http://localhost:19999",
                ApiToken = "other-token",
                DashboardUrls = ["http://localhost:19999/login?t=other"],
                IsHealthy = true
            },
            ResourceSnapshots =
            [
                new ResourceSnapshot { Name = "other-resource", DisplayName = "other-resource", ResourceType = "Project", State = "Running" },
            ]
        };
        monitor.AddConnection("hash-other", "socket.hash-other", outOfScopeConnection);

        // Connection 2 – the only in-scope connection, should be auto-selected
        var targetConnection = new TestAppHostAuxiliaryBackchannel
        {
            IsInScope = true,
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "TargetAppHost", "TargetAppHost.csproj"),
                ProcessId = 2222
            },
            DashboardInfoResponse = new GetDashboardInfoResponse
            {
                ApiBaseUrl = "http://localhost:18888",
                ApiToken = "test-token",
                DashboardUrls = ["http://localhost:18888/login?t=test"],
                IsHealthy = true
            },
            ResourceSnapshots =
            [
                new ResourceSnapshot { Name = "target-resource", DisplayName = "target-resource", ResourceType = "Container", State = "Running" },
            ],
            LogLines =
            [
                new ResourceLogLine { ResourceName = "target-resource", LineNumber = 1, Content = "Target resource log" },
            ]
        };
        monitor.AddConnection("hash-target", "socket.hash-target", targetConnection);

        var resourcesJson = JsonSerializer.Serialize(
            new[] { new ResourceInfoJson { Name = "target-resource", InstanceId = null } },
            OtlpJsonSerializerContext.Default.ResourceInfoJsonArray);

        var handler = new MockHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/api/telemetry/resources"))
            {
                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(resourcesJson, System.Text.Encoding.UTF8, "application/json")
                };
            }
            if (url.Contains("/api/telemetry/logs"))
            {
                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(BuildLogsJson(), System.Text.Encoding.UTF8, "application/json")
                };
            }
            if (url.Contains("/api/telemetry/traces"))
            {
                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(BuildTracesJson(), System.Text.Encoding.UTF8, "application/json")
                };
            }
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        });

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
            options.OutputTextWriter = outputWriter;
            options.DisableAnsi = true;
        });

        services.AddSingleton(handler);
        services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(new MockHttpClientFactory(handler)));

        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"export --output {outputPath}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.True(File.Exists(outputPath), "Export zip file should be created");

        using var archive = ZipFile.OpenRead(outputPath);
        var entryNames = archive.Entries.Select(e => e.FullName).Order().ToList();

        Assert.Collection(entryNames,
            entry => Assert.Equal("consolelogs/target-resource.txt", entry),
            entry => Assert.Equal("resources/target-resource.json", entry));
        var logContent = ReadEntryText(archive, "consolelogs/target-resource.txt");
        Assert.Contains("Target resource log", logContent);
    }

    [Fact]
    public async Task ExportCommand_ReplicaResources_GroupsDataByResolvedResourceName()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputPath = Path.Combine(workspace.WorkspaceRoot.FullName, "export.zip");

        // 3 telemetry resources: redis (singleton) + apiservice with 2 replicas
        var resources = new[]
        {
            new ResourceInfoJson { Name = "redis", InstanceId = null },
            new ResourceInfoJson { Name = "apiservice", InstanceId = "abc" },
            new ResourceInfoJson { Name = "apiservice", InstanceId = "def" },
        };

        // Structured logs from all 3 resources
        var logsJson = BuildLogsJson(
            ("redis", null, 9, "Information", "Cache ready", s_testTime),
            ("apiservice", "abc", 9, "Information", "Replica 1 started", s_testTime.AddSeconds(1)),
            ("apiservice", "def", 13, "Warning", "Replica 2 slow startup", s_testTime.AddSeconds(2)));

        // Traces from both replicas (redis has no traces)
        var tracesJson = BuildTracesJson(
            ("apiservice", "abc", "span001", "GET /api/products", s_testTime, s_testTime.AddMilliseconds(50), false),
            ("apiservice", "def", "span002", "GET /api/orders", s_testTime.AddSeconds(1), s_testTime.AddSeconds(1).AddMilliseconds(80), false));

        var provider = CreateExportTestServices(workspace, resources,
            telemetryEndpoints: new Dictionary<string, string>
            {
                ["/api/telemetry/logs"] = logsJson,
                ["/api/telemetry/traces"] = tracesJson,
            },
            resourceSnapshots:
            [
                new ResourceSnapshot { Name = "redis", DisplayName = "redis", ResourceType = "Container", State = "Running" },
                new ResourceSnapshot { Name = "apiservice-abc", DisplayName = "apiservice", ResourceType = "Project", State = "Running" },
                new ResourceSnapshot { Name = "apiservice-def", DisplayName = "apiservice", ResourceType = "Project", State = "Running" },
            ],
            logLines:
            [
                new ResourceLogLine { ResourceName = "redis", LineNumber = 1, Content = "Redis ready" },
                new ResourceLogLine { ResourceName = "apiservice-abc", LineNumber = 1, Content = "Replica 1 console log" },
                new ResourceLogLine { ResourceName = "apiservice-def", LineNumber = 1, Content = "Replica 2 console log" },
            ]);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"export --output {outputPath}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.True(File.Exists(outputPath), "Export zip file should be created");

        using var archive = ZipFile.OpenRead(outputPath);
        var entryNames = archive.Entries.Select(e => e.FullName).OrderBy(n => n).ToList();

        // Replicas should produce separate files with resolved names (apiservice-abc, apiservice-def)
        Assert.Collection(entryNames,
            entry => Assert.Equal("consolelogs/apiservice-abc.txt", entry),
            entry => Assert.Equal("consolelogs/apiservice-def.txt", entry),
            entry => Assert.Equal("consolelogs/redis.txt", entry),
            entry => Assert.Equal("resources/apiservice-abc.json", entry),
            entry => Assert.Equal("resources/apiservice-def.json", entry),
            entry => Assert.Equal("resources/redis.json", entry),
            entry => Assert.Equal("structuredlogs/apiservice-abc.json", entry),
            entry => Assert.Equal("structuredlogs/apiservice-def.json", entry),
            entry => Assert.Equal("structuredlogs/redis.json", entry),
            entry => Assert.Equal("traces/apiservice-abc.json", entry),
            entry => Assert.Equal("traces/apiservice-def.json", entry));

        // Verify console logs are separated by replica
        var replica1Console = ReadEntryText(archive, "consolelogs/apiservice-abc.txt").Trim();
        Assert.Equal("Replica 1 console log", replica1Console);

        var replica2Console = ReadEntryText(archive, "consolelogs/apiservice-def.txt").Trim();
        Assert.Equal("Replica 2 console log", replica2Console);

        // Verify structured logs are grouped per resource
        var replica1Logs = JsonSerializer.Deserialize(
            ReadEntryText(archive, "structuredlogs/apiservice-abc.json"),
            OtlpJsonSerializerContext.Default.OtlpTelemetryDataJson);
        Assert.NotNull(replica1Logs?.ResourceLogs);
        Assert.Single(replica1Logs.ResourceLogs);
        Assert.Equal("apiservice", replica1Logs.ResourceLogs[0].Resource?.GetServiceName());
        Assert.Equal("abc", replica1Logs.ResourceLogs[0].Resource?.GetServiceInstanceId());

        var replica2Logs = JsonSerializer.Deserialize(
            ReadEntryText(archive, "structuredlogs/apiservice-def.json"),
            OtlpJsonSerializerContext.Default.OtlpTelemetryDataJson);
        Assert.NotNull(replica2Logs?.ResourceLogs);
        Assert.Single(replica2Logs.ResourceLogs);
        Assert.Equal("def", replica2Logs.ResourceLogs[0].Resource?.GetServiceInstanceId());

        var redisLogs = JsonSerializer.Deserialize(
            ReadEntryText(archive, "structuredlogs/redis.json"),
            OtlpJsonSerializerContext.Default.OtlpTelemetryDataJson);
        Assert.NotNull(redisLogs?.ResourceLogs);
        Assert.Single(redisLogs.ResourceLogs);
        Assert.Equal("redis", redisLogs.ResourceLogs[0].Resource?.GetServiceName());

        // Verify traces are grouped per replica (redis has no traces)
        var replica1Traces = JsonSerializer.Deserialize(
            ReadEntryText(archive, "traces/apiservice-abc.json"),
            OtlpJsonSerializerContext.Default.OtlpTelemetryDataJson);
        Assert.NotNull(replica1Traces?.ResourceSpans);
        Assert.Single(replica1Traces.ResourceSpans);
        var replica1Span = Assert.Single(replica1Traces.ResourceSpans[0].ScopeSpans![0].Spans!);
        Assert.Equal("GET /api/products", replica1Span.Name);

        var replica2Traces = JsonSerializer.Deserialize(
            ReadEntryText(archive, "traces/apiservice-def.json"),
            OtlpJsonSerializerContext.Default.OtlpTelemetryDataJson);
        Assert.NotNull(replica2Traces?.ResourceSpans);
        Assert.Single(replica2Traces.ResourceSpans);
        var replica2Span = Assert.Single(replica2Traces.ResourceSpans[0].ScopeSpans![0].Spans!);
        Assert.Equal("GET /api/orders", replica2Span.Name);

        // Verify resource JSON has correct types for replicas
        var replica1ResourceData = JsonSerializer.Deserialize(ReadEntryText(archive, "resources/apiservice-abc.json"), OtlpJsonSerializerContext.Default.ResourceJson);
        Assert.NotNull(replica1ResourceData);
        Assert.Equal("Project", replica1ResourceData.ResourceType);

        var redisResourceData = JsonSerializer.Deserialize(ReadEntryText(archive, "resources/redis.json"), OtlpJsonSerializerContext.Default.ResourceJson);
        Assert.NotNull(redisResourceData);
        Assert.Equal("Container", redisResourceData.ResourceType);
    }

    [Fact]
    public async Task ExportCommand_ResourceFilter_ExportsOnlyFilteredResource()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputPath = Path.Combine(workspace.WorkspaceRoot.FullName, "export.zip");

        var resources = new[]
        {
            new ResourceInfoJson { Name = "redis", InstanceId = null },
            new ResourceInfoJson { Name = "apiservice", InstanceId = null },
        };

        // Provide only redis telemetry data (simulates server-side filtering by the Dashboard API)
        var filteredLogsJson = BuildLogsJson(
            ("redis", null, 9, "Information", "Ready to accept connections", s_testTime));

        var filteredTracesJson = BuildTracesJson(
            ("redis", null, "span001", "SET mykey", s_testTime, s_testTime.AddMilliseconds(10), false));

        var provider = CreateExportTestServices(workspace, resources,
            telemetryEndpoints: new Dictionary<string, string>
            {
                ["/api/telemetry/logs"] = filteredLogsJson,
                ["/api/telemetry/traces"] = filteredTracesJson,
            },
            resourceSnapshots:
            [
                new ResourceSnapshot { Name = "redis", DisplayName = "redis", ResourceType = "Container", State = "Running" },
                new ResourceSnapshot { Name = "apiservice", DisplayName = "apiservice", ResourceType = "Project", State = "Running" },
            ],
            logLines:
            [
                new ResourceLogLine { ResourceName = "redis", LineNumber = 1, Content = "Redis is starting" },
                new ResourceLogLine { ResourceName = "apiservice", LineNumber = 1, Content = "Now listening on: https://localhost:5001" },
            ]);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"export redis --output {outputPath}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.True(File.Exists(outputPath), "Export zip file should be created");

        using var archive = ZipFile.OpenRead(outputPath);
        var entryNames = archive.Entries.Select(e => e.FullName).OrderBy(n => n).ToList();

        // Only redis data should be present; apiservice should be excluded
        Assert.Collection(entryNames,
            entry => Assert.Equal("consolelogs/redis.txt", entry),
            entry => Assert.Equal("resources/redis.json", entry),
            entry => Assert.Equal("structuredlogs/redis.json", entry),
            entry => Assert.Equal("traces/redis.json", entry));

        var redisConsoleLog = ReadEntryText(archive, "consolelogs/redis.txt");
        Assert.Contains("Redis is starting", redisConsoleLog);
    }

    [Fact]
    public async Task ExportCommand_ResourceFilter_NoTelemetryData_SkipsStructuredLogsAndTraces()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputPath = Path.Combine(workspace.WorkspaceRoot.FullName, "export.zip");

        // Telemetry resources do NOT include "webfrontend" - it hasn't sent any telemetry yet
        var resources = new[]
        {
            new ResourceInfoJson { Name = "apiservice", InstanceId = null },
        };

        var logsJson = BuildLogsJson(
            ("apiservice", null, 9, "Information", "Request received", s_testTime));

        var tracesJson = BuildTracesJson(
            ("apiservice", null, "span001", "GET /api/products", s_testTime, s_testTime.AddMilliseconds(50), false));

        var provider = CreateExportTestServices(workspace, resources,
            telemetryEndpoints: new Dictionary<string, string>
            {
                ["/api/telemetry/logs"] = logsJson,
                ["/api/telemetry/traces"] = tracesJson,
            },
            resourceSnapshots:
            [
                new ResourceSnapshot { Name = "apiservice", DisplayName = "apiservice", ResourceType = "Project", State = "Running" },
                new ResourceSnapshot { Name = "webfrontend", DisplayName = "webfrontend", ResourceType = "Project", State = "Running" },
            ],
            logLines:
            [
                new ResourceLogLine { ResourceName = "apiservice", LineNumber = 1, Content = "API log" },
                new ResourceLogLine { ResourceName = "webfrontend", LineNumber = 1, Content = "Frontend log" },
            ]);

        var command = provider.GetRequiredService<RootCommand>();
        // Filter to webfrontend which exists in snapshots but has no telemetry data
        var result = command.Parse($"export webfrontend --output {outputPath}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.True(File.Exists(outputPath), "Export zip file should be created");

        using var archive = ZipFile.OpenRead(outputPath);
        var entryNames = archive.Entries.Select(e => e.FullName).OrderBy(n => n).ToList();

        // Only webfrontend resources and console logs; no structured logs or traces
        // (webfrontend has no telemetry data)
        Assert.Collection(entryNames,
            entry => Assert.Equal("consolelogs/webfrontend.txt", entry),
            entry => Assert.Equal("resources/webfrontend.json", entry));
    }

    [Fact]
    public async Task ExportCommand_ResourceFilter_ReplicasByDisplayName_ExportsAllReplicas()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputPath = Path.Combine(workspace.WorkspaceRoot.FullName, "export.zip");

        var resources = new[]
        {
            new ResourceInfoJson { Name = "redis", InstanceId = null },
            new ResourceInfoJson { Name = "apiservice", InstanceId = "abc" },
            new ResourceInfoJson { Name = "apiservice", InstanceId = "def" },
        };

        // Provide only apiservice telemetry data (simulates server-side filtering by the Dashboard API)
        var filteredLogsJson = BuildLogsJson(
            ("apiservice", "abc", 9, "Information", "Replica 1 started", s_testTime.AddSeconds(1)),
            ("apiservice", "def", 13, "Warning", "Replica 2 slow startup", s_testTime.AddSeconds(2)));

        var filteredTracesJson = BuildTracesJson(
            ("apiservice", "abc", "span002", "GET /api/products", s_testTime, s_testTime.AddMilliseconds(50), false),
            ("apiservice", "def", "span003", "GET /api/orders", s_testTime.AddSeconds(1), s_testTime.AddSeconds(1).AddMilliseconds(80), false));

        var provider = CreateExportTestServices(workspace, resources,
            telemetryEndpoints: new Dictionary<string, string>
            {
                ["/api/telemetry/logs"] = filteredLogsJson,
                ["/api/telemetry/traces"] = filteredTracesJson,
            },
            resourceSnapshots:
            [
                new ResourceSnapshot { Name = "redis", DisplayName = "redis", ResourceType = "Container", State = "Running" },
                new ResourceSnapshot { Name = "apiservice-abc", DisplayName = "apiservice", ResourceType = "Project", State = "Running" },
                new ResourceSnapshot { Name = "apiservice-def", DisplayName = "apiservice", ResourceType = "Project", State = "Running" },
            ],
            logLines:
            [
                new ResourceLogLine { ResourceName = "redis", LineNumber = 1, Content = "Redis ready" },
                new ResourceLogLine { ResourceName = "apiservice-abc", LineNumber = 1, Content = "Replica 1 console log" },
                new ResourceLogLine { ResourceName = "apiservice-def", LineNumber = 1, Content = "Replica 2 console log" },
            ]);

        var command = provider.GetRequiredService<RootCommand>();
        // Filter by display name "apiservice" should include both replicas
        var result = command.Parse($"export apiservice --output {outputPath}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.True(File.Exists(outputPath), "Export zip file should be created");

        using var archive = ZipFile.OpenRead(outputPath);
        var entryNames = archive.Entries.Select(e => e.FullName).OrderBy(n => n).ToList();

        // Only apiservice replicas should be present; redis should be excluded
        Assert.Collection(entryNames,
            entry => Assert.Equal("consolelogs/apiservice-abc.txt", entry),
            entry => Assert.Equal("consolelogs/apiservice-def.txt", entry),
            entry => Assert.Equal("resources/apiservice-abc.json", entry),
            entry => Assert.Equal("resources/apiservice-def.json", entry),
            entry => Assert.Equal("structuredlogs/apiservice-abc.json", entry),
            entry => Assert.Equal("structuredlogs/apiservice-def.json", entry),
            entry => Assert.Equal("traces/apiservice-abc.json", entry),
            entry => Assert.Equal("traces/apiservice-def.json", entry));
    }

    [Fact]
    public async Task ExportCommand_ResourceFilter_NonExistentResource_ReturnsError()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputPath = Path.Combine(workspace.WorkspaceRoot.FullName, "export.zip");

        var provider = CreateExportTestServices(workspace,
            resources: [new ResourceInfoJson { Name = "redis", InstanceId = null }],
            telemetryEndpoints: new Dictionary<string, string>
            {
                ["/api/telemetry/logs"] = BuildLogsJson(),
                ["/api/telemetry/traces"] = BuildTracesJson(),
            },
            resourceSnapshots:
            [
                new ResourceSnapshot { Name = "redis", DisplayName = "redis", ResourceType = "Container", State = "Running" },
            ],
            logLines: []);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"export nonexistent --output {outputPath}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
        Assert.False(File.Exists(outputPath), "No zip should be created when the resource doesn't exist");
    }

    [Fact]
    public async Task ExportCommand_DashboardUrl_ExportsTelemetryData()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputPath = Path.Combine(workspace.WorkspaceRoot.FullName, "export.zip");

        var resources = new[]
        {
            new ResourceInfoJson { Name = "redis", InstanceId = null },
        };

        var logsJson = BuildLogsJson(
            ("redis", null, 9, "Information", "Ready to accept connections", s_testTime));

        // CreateExportTestServices sets up a backchannel, but --dashboard-url bypasses it entirely
        var provider = CreateExportTestServices(workspace, resources,
            telemetryEndpoints: new Dictionary<string, string>
            {
                ["/api/telemetry/logs"] = logsJson,
                ["/api/telemetry/traces"] = BuildTracesJson(),
            },
            resourceSnapshots:
            [
                new ResourceSnapshot { Name = "redis", DisplayName = "redis", ResourceType = "Container", State = "Running" },
            ],
            logLines:
            [
                new ResourceLogLine { ResourceName = "redis", LineNumber = 1, Content = "Redis is starting" },
            ]);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"export --dashboard-url http://localhost:18888 --api-key test-token --output {outputPath}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.True(File.Exists(outputPath), "Export zip file should be created");

        using var archive = ZipFile.OpenRead(outputPath);
        var entryNames = archive.Entries.Select(e => e.FullName).OrderBy(n => n).ToList();

        // With --dashboard-url there is no backchannel, so no resources or console logs
        Assert.Collection(entryNames,
            entry => Assert.Equal("structuredlogs/redis.json", entry));

        // Verify structured log content
        var redisLogs = JsonSerializer.Deserialize(
            ReadEntryText(archive, "structuredlogs/redis.json"),
            OtlpJsonSerializerContext.Default.OtlpTelemetryDataJson);
        Assert.NotNull(redisLogs?.ResourceLogs);
        Assert.Single(redisLogs.ResourceLogs);
    }

    [Fact]
    public async Task ExportCommand_DashboardUnavailable_ExportsResourcesAndConsoleLogs()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputPath = Path.Combine(workspace.WorkspaceRoot.FullName, "export.zip");

        var testInteractionService = new TestInteractionService();

        var provider = CreateExportTestServices(workspace,
            resources: [],
            telemetryEndpoints: new Dictionary<string, string>(),
            resourceSnapshots:
            [
                new ResourceSnapshot { Name = "redis", DisplayName = "redis", ResourceType = "Container", State = "Running" },
                new ResourceSnapshot { Name = "apiservice", DisplayName = "apiservice", ResourceType = "Project", State = "Running" },
            ],
            logLines:
            [
                new ResourceLogLine { ResourceName = "redis", LineNumber = 1, Content = "Redis is starting" },
                new ResourceLogLine { ResourceName = "apiservice", LineNumber = 1, Content = "Now listening on: https://localhost:5001" },
            ],
            dashboardAvailable: false,
            interactionService: testInteractionService);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"export --output {outputPath}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.True(File.Exists(outputPath), "Export zip file should be created even without dashboard");

        using var archive = ZipFile.OpenRead(outputPath);
        var entryNames = archive.Entries.Select(e => e.FullName).OrderBy(n => n).ToList();

        // Should have resources and console logs, but no structured logs or traces
        Assert.Collection(entryNames,
            entry => Assert.Equal("consolelogs/apiservice.txt", entry),
            entry => Assert.Equal("consolelogs/redis.txt", entry),
            entry => Assert.Equal("resources/apiservice.json", entry),
            entry => Assert.Equal("resources/redis.json", entry));

        // Verify console log content is still present
        var redisConsoleLog = ReadEntryText(archive, "consolelogs/redis.txt");
        Assert.Contains("Redis is starting", redisConsoleLog);

        // Verify warning was displayed
        Assert.Contains(testInteractionService.DisplayedMessages, m => m.Message.Contains(ExportCommandStrings.DashboardNotAvailable));
    }

    /// <summary>
    /// Creates a configured <see cref="ServiceProvider"/> for export command tests,
    /// with a mock backchannel and HTTP handler that serves resource and telemetry data.
    /// </summary>
    private ServiceProvider CreateExportTestServices(
        TemporaryWorkspace workspace,
        ResourceInfoJson[] resources,
        Dictionary<string, string> telemetryEndpoints,
        List<ResourceSnapshot> resourceSnapshots,
        List<ResourceLogLine> logLines,
        bool dashboardAvailable = true,
        IInteractionService? interactionService = null)
    {
        var resourcesJson = JsonSerializer.Serialize(resources, OtlpJsonSerializerContext.Default.ResourceInfoJsonArray);

        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            IsInScope = true,
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "TestAppHost", "TestAppHost.csproj"),
                ProcessId = 1234
            },
            DashboardInfoResponse = dashboardAvailable ? new GetDashboardInfoResponse
            {
                ApiBaseUrl = "http://localhost:18888",
                ApiToken = "test-token",
                DashboardUrls = ["http://localhost:18888/login?t=test"],
                IsHealthy = true
            } : null,
            ResourceSnapshots = resourceSnapshots,
            LogLines = logLines
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var handler = new MockHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/api/telemetry/resources"))
            {
                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(resourcesJson, System.Text.Encoding.UTF8, "application/json")
                };
            }

            foreach (var (urlPattern, json) in telemetryEndpoints)
            {
                if (url.Contains(urlPattern))
                {
                    return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                    };
                }
            }

            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        });

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
            if (interactionService is not null)
            {
                options.InteractionServiceFactory = _ => interactionService;
            }
            options.DisableAnsi = true;
        });

        services.AddSingleton(handler);
        services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(new MockHttpClientFactory(handler)));

        return services.BuildServiceProvider();
    }

    private static string ReadEntryText(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName);
        Assert.NotNull(entry);
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string BuildLogsJson(params (string serviceName, string? instanceId, int severityNumber, string severityText, string body, DateTime time)[] entries)
    {
        if (entries.Length == 0)
        {
            var emptyResponse = new TelemetryApiResponse
            {
                Data = new OtlpTelemetryDataJson { ResourceLogs = [] },
                TotalCount = 0,
                ReturnedCount = 0
            };
            return JsonSerializer.Serialize(emptyResponse, OtlpJsonSerializerContext.Default.TelemetryApiResponse);
        }

        var resourceLogs = entries
            .GroupBy(e => (e.serviceName, e.instanceId))
            .Select(g => new OtlpResourceLogsJson
            {
                Resource = TelemetryTestHelper.CreateOtlpResource(g.Key.serviceName, g.Key.instanceId),
                ScopeLogs =
                [
                    new OtlpScopeLogsJson
                    {
                        LogRecords = g.Select(e => new OtlpLogRecordJson
                        {
                            TimeUnixNano = TelemetryTestHelper.DateTimeToUnixNanoseconds(e.time),
                            SeverityNumber = e.severityNumber,
                            SeverityText = e.severityText,
                            Body = new OtlpAnyValueJson { StringValue = e.body }
                        }).ToArray()
                    }
                ]
            }).ToArray();

        var response = new TelemetryApiResponse
        {
            Data = new OtlpTelemetryDataJson { ResourceLogs = resourceLogs },
            TotalCount = entries.Length,
            ReturnedCount = entries.Length
        };

        return JsonSerializer.Serialize(response, OtlpJsonSerializerContext.Default.TelemetryApiResponse);
    }

    private static string BuildTracesJson(params (string serviceName, string? instanceId, string spanId, string name, DateTime startTime, DateTime endTime, bool hasError)[] entries)
    {
        if (entries.Length == 0)
        {
            var emptyResponse = new TelemetryApiResponse
            {
                Data = new OtlpTelemetryDataJson { ResourceSpans = [] },
                TotalCount = 0,
                ReturnedCount = 0
            };
            return JsonSerializer.Serialize(emptyResponse, OtlpJsonSerializerContext.Default.TelemetryApiResponse);
        }

        var resourceSpans = entries
            .GroupBy(e => (e.serviceName, e.instanceId))
            .Select(g => new OtlpResourceSpansJson
            {
                Resource = TelemetryTestHelper.CreateOtlpResource(g.Key.serviceName, g.Key.instanceId),
                ScopeSpans =
                [
                    new OtlpScopeSpansJson
                    {
                        Spans = g.Select(e => new OtlpSpanJson
                        {
                            SpanId = e.spanId,
                            TraceId = "trace001",
                            Name = e.name,
                            StartTimeUnixNano = TelemetryTestHelper.DateTimeToUnixNanoseconds(e.startTime),
                            EndTimeUnixNano = TelemetryTestHelper.DateTimeToUnixNanoseconds(e.endTime),
                            Status = e.hasError ? new OtlpSpanStatusJson { Code = 2 } : new OtlpSpanStatusJson { Code = 1 }
                        }).ToArray()
                    }
                ]
            }).ToArray();

        var response = new TelemetryApiResponse
        {
            Data = new OtlpTelemetryDataJson { ResourceSpans = resourceSpans },
            TotalCount = entries.Length,
            ReturnedCount = entries.Length
        };

        return JsonSerializer.Serialize(response, OtlpJsonSerializerContext.Default.TelemetryApiResponse);
    }
}
