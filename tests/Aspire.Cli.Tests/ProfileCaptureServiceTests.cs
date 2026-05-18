// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Aspire.Cli.Bundles;
using Aspire.Cli.Commands;
using Aspire.Cli.Diagnostics;
using Aspire.Cli.DotNet;
using Aspire.Cli.Interaction;
using Aspire.Cli.Layout;
using Aspire.Cli.Profiling;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.Commands;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Hosting;
using Aspire.Otlp.Serialization;
using Aspire.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aspire.Cli.Tests;

public class ProfileCaptureServiceTests(ITestOutputHelper outputHelper)
{
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan s_testPollInterval = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task StartAsync_LaunchesPrivateDashboardWithConfiguredPortsAndCollectorEnvironment()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var fileLoggerProvider = CreateFileLoggerProvider(workspace);
        var managedPath = CreateFile(workspace, "aspire-managed");
        var options = CreateOptions(workspace);
        var processFactory = CreateRunningProcessFactory();
        var handler = new MockHttpMessageHandler(request =>
        {
            Assert.Equal(options.DashboardUrl, request.RequestUri?.GetLeftPart(UriPartial.Authority));
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var service = CreateService(
            fileLoggerProvider,
            processFactory,
            handler,
            CreateConfiguration((BundleDiscovery.DashboardPathEnvVar, managedPath)));

        await using var session = await service.StartAsync(options, s_testTimeout, s_testPollInterval, CancellationToken.None);

        Assert.Equal(managedPath, processFactory.LastFileName);
        var arguments = processFactory.LastArguments;
        Assert.NotNull(arguments);
        Assert.Equal(
            [
                "dashboard",
                $"--{KnownConfigNames.AspNetCoreUrls}={options.DashboardUrl}",
                $"--{KnownConfigNames.DashboardOtlpGrpcEndpointUrl}={options.OtlpGrpcUrl}",
                $"--{KnownConfigNames.DashboardOtlpHttpEndpointUrl}={options.OtlpHttpUrl}",
                $"--{KnownConfigNames.DashboardUnsecuredAllowAnonymous}=true",
                $"--{KnownConfigNames.DashboardApiEnabled}=true"
            ],
            arguments);

        var environmentVariables = processFactory.LastEnvironmentVariables;
        Assert.NotNull(environmentVariables);
        Assert.Equal("false", environmentVariables[KnownConfigNames.ProfilingEnabled]);
        Assert.Equal("false", environmentVariables[KnownConfigNames.Legacy.StartupProfilingEnabled]);
        Assert.Equal(string.Empty, environmentVariables[KnownConfigNames.ProfilingSessionId]);
        Assert.Equal(string.Empty, environmentVariables[KnownConfigNames.Legacy.StartupOperationId]);
        Assert.Equal(string.Empty, environmentVariables[KnownOtelConfigNames.ExporterOtlpEndpoint]);
        Assert.Equal(string.Empty, environmentVariables[KnownOtelConfigNames.ExporterOtlpProtocol]);
        Assert.Equal(string.Empty, environmentVariables[KnownOtelConfigNames.ExporterOtlpHeaders]);
        Assert.Equal(string.Empty, environmentVariables[KnownOtelConfigNames.BspScheduleDelay]);
        Assert.NotNull(processFactory.LastProcessInvocationOptions?.StandardOutputCallback);
        Assert.NotNull(processFactory.LastProcessInvocationOptions?.StandardErrorCallback);

        var process = Assert.IsType<TestProcessExecution>(Assert.Single(processFactory.CreatedExecutions));
        Assert.True(process.Started);
    }

    [Fact]
    public async Task StartAsync_UsesBundleLayoutManagedPath_WhenOverrideIsAbsent()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var fileLoggerProvider = CreateFileLoggerProvider(workspace);
        var layoutRoot = workspace.WorkspaceRoot.CreateSubdirectory("bundle");
        var managedDirectory = layoutRoot.CreateSubdirectory(BundleDiscovery.ManagedDirectoryName);
        var managedPath = Path.Combine(managedDirectory.FullName, BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName));
        File.WriteAllText(managedPath, string.Empty);

        var bundleService = new TestBundleService(isBundle: true)
        {
            Layout = new LayoutConfiguration { LayoutPath = layoutRoot.FullName }
        };
        var processFactory = CreateRunningProcessFactory();
        var service = CreateService(
            fileLoggerProvider,
            processFactory,
            new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)),
            CreateConfiguration(),
            bundleService);

        await using var session = await service.StartAsync(CreateOptions(workspace), s_testTimeout, s_testPollInterval, CancellationToken.None);

        Assert.Equal(managedPath, processFactory.LastFileName);
    }

    [Fact]
    public async Task StartAsync_ThrowsManagedBinaryNotFound_WhenNoManagedBinaryCanBeResolved()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var fileLoggerProvider = CreateFileLoggerProvider(workspace);
        var service = CreateService(
            fileLoggerProvider,
            CreateRunningProcessFactory(),
            new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)),
            CreateConfiguration(),
            new NullBundleService());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartAsync(CreateOptions(workspace), s_testTimeout, s_testPollInterval, CancellationToken.None));

        Assert.Equal(DashboardCommandStrings.ManagedBinaryNotFound, exception.Message);
    }

    [Fact]
    public async Task StartAsync_WrapsProcessFactoryFailure()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var fileLoggerProvider = CreateFileLoggerProvider(workspace);
        var managedPath = CreateFile(workspace, "aspire-managed");
        var processFactory = new TestProcessExecutionFactory
        {
            CreateExecutionWithFileNameCallback = (_, _, _, _, _) => throw new InvalidOperationException("boom")
        };
        var service = CreateService(
            fileLoggerProvider,
            processFactory,
            new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)),
            CreateConfiguration((BundleDiscovery.DashboardPathEnvVar, managedPath)));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartAsync(CreateOptions(workspace), s_testTimeout, s_testPollInterval, CancellationToken.None));

        Assert.Contains("boom", exception.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    [Fact]
    public async Task StartAsync_WrapsProcessStartFailureAndDisposesExecution()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var fileLoggerProvider = CreateFileLoggerProvider(workspace);
        var managedPath = CreateFile(workspace, "aspire-managed");
        TestProcessExecution? process = null;
        var processFactory = new TestProcessExecutionFactory
        {
            CreateExecutionWithFileNameCallback = (fileName, args, env, _, options) =>
            {
                process = new TestProcessExecution(fileName, args, env, options, (_, _, _) => Task.FromResult((0, (string?)null)), () => 1)
                {
                    StartReturnValue = false
                };
                return process;
            }
        };
        var service = CreateService(
            fileLoggerProvider,
            processFactory,
            new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)),
            CreateConfiguration((BundleDiscovery.DashboardPathEnvVar, managedPath)));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartAsync(CreateOptions(workspace), s_testTimeout, s_testPollInterval, CancellationToken.None));

        Assert.Contains(managedPath, exception.Message, StringComparison.Ordinal);
        Assert.NotNull(process);
        Assert.Equal(1, process.DisposeCount);
    }

    [Fact]
    public async Task StartAsync_DisposesDashboardProcess_WhenReadinessTimesOut()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var fileLoggerProvider = CreateFileLoggerProvider(workspace);
        var managedPath = CreateFile(workspace, "aspire-managed");
        var processFactory = CreateRunningProcessFactory();
        var timeProvider = new FakeTimeProvider();
        var service = CreateService(
            fileLoggerProvider,
            processFactory,
            new MockHttpMessageHandler(_ =>
            {
                timeProvider.Advance(s_testTimeout + TimeSpan.FromTicks(1));
                throw new HttpRequestException("not ready");
            }),
            CreateConfiguration((BundleDiscovery.DashboardPathEnvVar, managedPath)),
            timeProvider: timeProvider);

        var exception = await Assert.ThrowsAsync<TimeoutException>(
            () => service.StartAsync(CreateOptions(workspace), s_testTimeout, s_testPollInterval, CancellationToken.None));

        Assert.Equal(DashboardCommandStrings.DashboardStartTimedOut, exception.Message);
        var process = Assert.IsType<TestProcessExecution>(Assert.Single(processFactory.CreatedExecutions));
        Assert.Equal(1, process.KillCount);
        Assert.True(process.KilledEntireProcessTree);
        Assert.Equal(1, process.DisposeCount);
    }

    [Fact]
    public async Task WaitForDashboardAsync_ReturnsAfterTransientConnectionFailures()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var options = CreateOptions(workspace);
        var attempts = 0;
        var handler = new MockHttpMessageHandler(_ =>
        {
            if (Interlocked.Increment(ref attempts) <= 2)
            {
                throw new HttpRequestException("connection refused");
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        await using var session = CreateSession(options, handler, timeProvider: new FakeTimeProvider());

        await session.WaitForDashboardAsync(s_testTimeout, TimeSpan.Zero, CancellationToken.None);

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task WaitForDashboardAsync_ThrowsDashboardExited_WhenProcessExitsBeforeReady()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var process = CreateStartedProcess((_, _) => Task.FromResult(42));
        await using var session = CreateSession(
            CreateOptions(workspace),
            new MockHttpMessageHandler(new HttpRequestException("not ready")),
            process);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.WaitForDashboardAsync(s_testTimeout, s_testPollInterval, CancellationToken.None));

        Assert.Contains("42", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitForDashboardAsync_ThrowsTimeout_WhenDashboardNeverResponds()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var timeProvider = new FakeTimeProvider();
        await using var session = CreateSession(
            CreateOptions(workspace),
            new MockHttpMessageHandler(_ =>
            {
                timeProvider.Advance(s_testTimeout + TimeSpan.FromTicks(1));
                throw new HttpRequestException("not ready");
            }),
            timeProvider: timeProvider);

        var exception = await Assert.ThrowsAsync<TimeoutException>(
            () => session.WaitForDashboardAsync(s_testTimeout, s_testPollInterval, CancellationToken.None));

        Assert.Equal(DashboardCommandStrings.DashboardStartTimedOut, exception.Message);
    }

    [Fact]
    public async Task DisposeAsync_StopsWaitingForExitAfterBoundedTimeout()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var hangingExit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Simulate a dashboard process that ignores Kill and never finishes WaitForExitAsync. Without
        // a bounded disposal timeout the CLI would hang on shutdown waiting on this task.
        var process = new TestProcessExecution(
            fileName: "aspire-managed",
            args: [],
            env: null,
            options: new ProcessInvocationOptions(),
            attemptCallback: (_, _, _) => Task.FromResult((0, (string?)null)),
            attemptCounter: () => 1)
        {
            WaitForExitAsyncCallback = (_, ct) => hangingExit.Task.WaitAsync(ct)
        };
        Assert.True(process.Start());

        var timeProvider = new FakeTimeProvider();
        var session = CreateSession(
            CreateOptions(workspace),
            new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)),
            process,
            timeProvider: timeProvider);

        var disposeTask = session.DisposeAsync().AsTask();

        // Advance the fake clock until DisposeAsync's WaitAsync timer fires. The yield gives
        // DisposeAsync a chance to register that timer with the FakeTimeProvider before each advance.
        for (var attempt = 0; attempt < 100 && !disposeTask.IsCompleted; attempt++)
        {
            timeProvider.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }

        await disposeTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, process.KillCount);
        Assert.Equal(1, process.DisposeCount);
        hangingExit.TrySetResult(0);
    }

    [Fact]
    public async Task ExportAsync_WritesArchiveAfterSessionSpansReachSteadyState()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputPath = Path.Combine(workspace.WorkspaceRoot.FullName, "nested", "profile.zip");
        var options = CreateOptions(workspace, outputPath: outputPath, sessionId: "session-a");
        var interactionService = new TestInteractionService();
        var handler = CreateTelemetryHandler(_ => JsonResponse(CreateTracesResponse(options.SessionId)));

        await using var session = CreateSession(
            options,
            handler,
            interactionService: interactionService,
            profileDataTimeout: TimeSpan.FromSeconds(1),
            profileDataPollInterval: TimeSpan.Zero,
            profileDataQuietPolls: 2);

        var exitCode = await session.ExportAsync(CancellationToken.None);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.True(File.Exists(outputPath));
        Assert.True(Directory.Exists(Path.GetDirectoryName(outputPath)));
        using var archive = ZipFile.OpenRead(outputPath);
        var entry = Assert.Single(archive.Entries);
        Assert.Equal("traces/profile.json", entry.FullName);

        var exportedJson = ReadEntryText(entry);
        var exportedData = JsonSerializer.Deserialize(exportedJson, OtlpJsonSerializerContext.Default.OtlpTelemetryDataJson);
        var span = Assert.Single(Assert.Single(Assert.Single(exportedData!.ResourceSpans!).ScopeSpans!).Spans!);
        Assert.Equal("profile-span-0", span.Name);

        var displayedMessage = Assert.Single(interactionService.DisplayedMessages);
        Assert.Equal(KnownEmojis.CheckMarkButton, displayedMessage.Emoji);
        Assert.Contains(outputPath, displayedMessage.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportAsync_ReturnsFailure_WhenNoResourceSpansAreExported()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var options = CreateOptions(workspace);
        var timeProvider = new FakeTimeProvider();
        var handler = CreateTelemetryHandler(_ => JsonResponse(new TelemetryApiResponse
        {
            Data = new OtlpTelemetryDataJson(),
            TotalCount = 0,
            ReturnedCount = 0
        }), timeProvider);

        await using var session = CreateSession(options, handler, timeProvider: timeProvider);

        var exitCode = await session.ExportAsync(CancellationToken.None);

        Assert.Equal(CliExitCodes.DashboardFailure, exitCode);
        Assert.False(File.Exists(options.OutputPath));
    }

    [Fact]
    public async Task ExportAsync_ReturnsFailure_WhenOnlyOtherSessionSpansAreExported()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var options = CreateOptions(workspace, sessionId: "session-a");
        var timeProvider = new FakeTimeProvider();
        var handler = CreateTelemetryHandler(_ => JsonResponse(CreateTracesResponse("session-b")), timeProvider);

        await using var session = CreateSession(options, handler, timeProvider: timeProvider);

        var exitCode = await session.ExportAsync(CancellationToken.None);

        Assert.Equal(CliExitCodes.DashboardFailure, exitCode);
        Assert.False(File.Exists(options.OutputPath));
    }

    [Fact]
    public async Task ExportAsync_ThrowsHttpRequestException_WhenTelemetryApiReturnsHtmlFallback()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var handler = CreateTelemetryHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html></html>", Encoding.UTF8, "text/html")
        });

        await using var session = CreateSession(CreateOptions(workspace), handler);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => session.ExportAsync(CancellationToken.None));

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
    }

    [Fact]
    public async Task ExportAsync_ThrowsJsonException_WhenTelemetryApiReturnsInvalidJson()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var handler = CreateTelemetryHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{", Encoding.UTF8, "application/json")
        });

        await using var session = CreateSession(CreateOptions(workspace), handler);

        await Assert.ThrowsAsync<JsonException>(() => session.ExportAsync(CancellationToken.None));
    }

    [Fact]
    public void CreateDashboardEnvironment_DisablesCollectorProfilingAndExporterInheritance()
    {
        var environmentVariables = ProfileCaptureService.CreateDashboardEnvironment();

        Assert.Equal("false", environmentVariables[KnownConfigNames.ProfilingEnabled]);
        Assert.Equal("false", environmentVariables[KnownConfigNames.Legacy.StartupProfilingEnabled]);
        Assert.Equal(string.Empty, environmentVariables[KnownConfigNames.ProfilingSessionId]);
        Assert.Equal(string.Empty, environmentVariables[KnownConfigNames.Legacy.StartupOperationId]);
        Assert.Equal(string.Empty, environmentVariables[KnownOtelConfigNames.ExporterOtlpEndpoint]);
        Assert.Equal(string.Empty, environmentVariables[KnownOtelConfigNames.ExporterOtlpProtocol]);
        Assert.Equal(string.Empty, environmentVariables[KnownOtelConfigNames.ExporterOtlpHeaders]);
        Assert.Equal(string.Empty, environmentVariables[KnownOtelConfigNames.BspScheduleDelay]);
    }

    [Fact]
    public void ResolveManagedPathOverride_UsesManagedDirectoryContainingExecutable()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var managedDirectory = workspace.WorkspaceRoot.CreateSubdirectory("managed");
        var managedPath = Path.Combine(managedDirectory.FullName, BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName));
        File.WriteAllText(managedPath, string.Empty);

        var resolvedPath = ProfileCaptureService.ResolveManagedPathOverride(
            CreateConfiguration((BundleDiscovery.ManagedPathEnvVar, managedDirectory.FullName)));

        Assert.Equal(managedPath, resolvedPath);
    }

    [Fact]
    public void ResolveManagedPathOverride_IgnoresMissingOverrides()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var missingPath = Path.Combine(workspace.WorkspaceRoot.FullName, "missing");

        var resolvedPath = ProfileCaptureService.ResolveManagedPathOverride(
            CreateConfiguration(
                (BundleDiscovery.DashboardPathEnvVar, missingPath),
                (BundleDiscovery.ManagedPathEnvVar, missingPath)));

        Assert.Null(resolvedPath);
    }

    private static FileLoggerProvider CreateFileLoggerProvider(TemporaryWorkspace workspace)
    {
        var logPath = Path.Combine(workspace.WorkspaceRoot.FullName, "profile-service.log");
        return new FileLoggerProvider(logPath, new TestStartupErrorWriter());
    }

    private static ProfileCaptureService CreateService(
        FileLoggerProvider fileLoggerProvider,
        TestProcessExecutionFactory processFactory,
        HttpMessageHandler handler,
        IConfiguration configuration,
        IBundleService? bundleService = null,
        TestInteractionService? interactionService = null,
        TimeProvider? timeProvider = null)
    {
        return new ProfileCaptureService(
            bundleService ?? new NullBundleService(),
            new LayoutProcessRunner(processFactory),
            fileLoggerProvider,
            new MockHttpClientFactory(handler),
            interactionService ?? new TestInteractionService(),
            configuration,
            timeProvider ?? TimeProvider.System,
            NullLogger<ProfileCaptureService>.Instance);
    }

    private static TestProcessExecutionFactory CreateRunningProcessFactory()
    {
        return new TestProcessExecutionFactory
        {
            CreateExecutionWithFileNameCallback = (fileName, args, env, _, options) => CreateRunningProcess(fileName, args, env, options)
        };
    }

    private static ProfileCaptureService.ProfileCaptureSession CreateSession(
        ProfileCaptureOptions options,
        HttpMessageHandler handler,
        TestProcessExecution? process = null,
        TestInteractionService? interactionService = null,
        TimeProvider? timeProvider = null,
        TimeSpan? profileDataTimeout = null,
        TimeSpan? profileDataPollInterval = null,
        int profileDataQuietPolls = 1)
    {
        if (process is null)
        {
            process = CreateRunningProcess("aspire-managed", [], env: null, new ProcessInvocationOptions());
            Assert.True(process.Start());
        }

        return new ProfileCaptureService.ProfileCaptureSession(
            options,
            process,
            new MockHttpClientFactory(handler),
            interactionService ?? new TestInteractionService(),
            timeProvider ?? TimeProvider.System,
            NullLogger<ProfileCaptureService>.Instance,
            profileDataTimeout ?? s_testTimeout,
            profileDataPollInterval ?? s_testPollInterval,
            profileDataQuietPolls);
    }

    private static TestProcessExecution CreateStartedProcess(Func<ProcessInvocationOptions, CancellationToken, Task<int>> waitForExitAsync)
    {
        var process = new TestProcessExecution(
            fileName: "aspire-managed",
            args: [],
            env: null,
            options: new ProcessInvocationOptions(),
            attemptCallback: (_, _, _) => Task.FromResult((0, (string?)null)),
            attemptCounter: () => 1)
        {
            WaitForExitAsyncCallback = waitForExitAsync
        };
        Assert.True(process.Start());
        return process;
    }

    private static TestProcessExecution CreateRunningProcess(
        string fileName,
        string[] args,
        IDictionary<string, string>? env,
        ProcessInvocationOptions options)
    {
        var exitCompletionSource = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        return new TestProcessExecution(
            fileName,
            args,
            env,
            options,
            (_, _, _) => Task.FromResult((0, (string?)null)),
            () => 1)
        {
            WaitForExitAsyncCallback = (_, ct) => exitCompletionSource.Task.WaitAsync(ct),
            KillCallback = _ => exitCompletionSource.TrySetResult(CliExitCodes.Success)
        };
    }

    private static HttpMessageHandler CreateTelemetryHandler(
        Func<HttpRequestMessage, HttpResponseMessage> telemetryResponseFactory,
        FakeTimeProvider? timeoutTimeProvider = null)
    {
        return new MockHttpMessageHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/api/telemetry/traces", StringComparison.Ordinal) is true)
            {
                var response = telemetryResponseFactory(request);
                timeoutTimeProvider?.Advance(s_testTimeout + TimeSpan.FromTicks(1));
                return response;
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
    }

    private static HttpResponseMessage JsonResponse(TelemetryApiResponse response)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(response, OtlpJsonSerializerContext.Default.TelemetryApiResponse),
                Encoding.UTF8,
                "application/json")
        };
    }

    private static TelemetryApiResponse CreateTracesResponse(string sessionId)
    {
        return new TelemetryApiResponse
        {
            Data = new OtlpTelemetryDataJson
            {
                ResourceSpans =
                [
                    new OtlpResourceSpansJson
                    {
                        Resource = TelemetryTestHelper.CreateOtlpResource("apphost", instanceId: null),
                        ScopeSpans =
                        [
                            new OtlpScopeSpansJson
                            {
                                Scope = new OtlpInstrumentationScopeJson { Name = "Aspire.Cli.Profiling" },
                                Spans =
                                [
                                    new OtlpSpanJson
                                    {
                                        TraceId = "11111111111111111111111111111111",
                                        SpanId = "2222222222222222",
                                        Name = "profile-span-0",
                                        StartTimeUnixNano = 1,
                                        EndTimeUnixNano = 2,
                                        Attributes =
                                        [
                                            new OtlpKeyValueJson
                                            {
                                                Key = ProfilingTelemetry.Tags.ProfilingSessionId,
                                                Value = new OtlpAnyValueJson { StringValue = sessionId }
                                            }
                                        ]
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            TotalCount = 1,
            ReturnedCount = 1
        };
    }

    private static string ReadEntryText(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string CreateFile(TemporaryWorkspace workspace, string fileName)
    {
        var path = Path.Combine(workspace.WorkspaceRoot.FullName, fileName);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    private static ProfileCaptureOptions CreateOptions(
        TemporaryWorkspace workspace,
        string? outputPath = null,
        string sessionId = "session-a")
    {
        return new ProfileCaptureOptions(
            outputPath ?? Path.Combine(workspace.WorkspaceRoot.FullName, "profile.zip"),
            "http://127.0.0.1:5001",
            "http://127.0.0.1:5002",
            "http://127.0.0.1:5003",
            sessionId,
            TimeSpan.Zero);
    }

    private static IConfiguration CreateConfiguration(params (string Key, string? Value)[] values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(value => value.Key, value => value.Value))
            .Build();
    }
}
