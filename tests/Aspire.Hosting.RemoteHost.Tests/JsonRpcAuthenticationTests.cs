// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;
using Aspire.Hosting.RemoteHost.Ats;
using Aspire.Hosting.RemoteHost.Diagnostics;
using Aspire.Shared;
using Aspire.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using StreamJsonRpc;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

public sealed class JsonRpcAuthenticationTests(ITestOutputHelper outputHelper)
{
    public static TheoryData<string, object?[]> ProtectedMethods => new()
    {
        { "cancelToken", ["ct_missing"] },
        { "invokeCapability", ["test-capability", null] },
        { "registerAsIntegrationHost", ["registration"] },
        { "invokeGuestCallback", ["callback", null] },
        { "getCapabilities", [] },
        { "generateCode", ["TypeScript"] },
        { "scaffoldAppHost", ["TypeScript", "/tmp/apphost", "AppHost"] },
        { "detectAppHostType", ["/tmp/apphost"] },
        { "getRuntimeSpec", ["TypeScript"] }
    };

    [Fact]
    public async Task Ping_DoesNotRequireAuthentication()
    {
        using var workspace = CreateSocketWorkspace();
        await using var server = await RemoteHostTestServer.StartAsync(workspace, existingDirectory: true);
        await using var client = await server.ConnectAsync();

        var result = await client.InvokeAsync<string>("ping");

        Assert.Equal("pong", result);
    }

    [Theory]
    [MemberData(nameof(ProtectedMethods))]
    public async Task ProtectedMethods_RequireAuthentication(string methodName, object?[] arguments)
    {
        using var workspace = CreateSocketWorkspace();
        await using var server = await RemoteHostTestServer.StartAsync(workspace, existingDirectory: true);
        await using var client = await server.ConnectAsync();

        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(
            () => client.InvokeAsync<JsonElement?>(methodName, arguments));

        Assert.Contains("Client must authenticate before invoking AppHost RPC methods.", ex.Message);
    }

    [Fact]
    public async Task FailedAuthentication_ClosesConnection_AndPreventsFurtherCalls()
    {
        using var workspace = CreateSocketWorkspace();
        await using var server = await RemoteHostTestServer.StartAsync(workspace, existingDirectory: true);
        await using var client = await server.ConnectAsync();

        Assert.Equal("pong", await client.InvokeAsync<string>("ping"));

        await AssertRejectedAuthenticationAsync(client);
        await RemoteHostTestServer.WaitForDisconnectAsync(client);

        await Assert.ThrowsAnyAsync<Exception>(() => client.InvokeAsync<string>("ping"));
        await Assert.ThrowsAnyAsync<Exception>(() => client.InvokeAsync<bool>("cancelToken", ["ct_missing"]));
    }

    private static async Task AssertRejectedAuthenticationAsync(JsonRpcClientHandle client)
    {
        try
        {
            var authenticated = await client.InvokeAsync<bool>("authenticate", ["wrong-token"]);
            Assert.False(authenticated);
        }
        catch (ConnectionLostException)
        {
            // The server closes the connection immediately after rejecting the token, so the client may observe
            // the disconnect before it receives the boolean response.
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SocketPermissions_AreRestrictedBeforeServingClients(bool existingDirectory)
    {
        using var workspace = CreateSocketWorkspace();
        await using var server = await RemoteHostTestServer.StartAsync(workspace, existingDirectory);
        await using var client = await server.ConnectAsync();

        Assert.Equal("pong", await client.InvokeAsync<string>("ping"));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.GetDirectoryName(server.SocketPath)!));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(server.SocketPath));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedSocketDirectory_DoesNotDeleteExistingFileOnDispose(bool symbolicLink)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var target = workspace.CreateDirectory("target");
        File.SetUnixFileMode(target.FullName,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherRead);
        var originalMode = File.GetUnixFileMode(target.FullName);
        var existingFile = Path.Combine(target.FullName, "rpc.sock");
        await File.WriteAllTextAsync(existingFile, "not our socket");
        workspace.CreateDirectory(Path.Combine(".aspire", "cli"));
        var link = Path.Combine(workspace.Path, ".aspire", "cli", "bch");
        Directory.CreateSymbolicLink(link, target.FullName);

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["REMOTE_APP_HOST_SOCKET_PATH"] = symbolicLink ? Path.Combine(link, "rpc.sock") : existingFile
        }).Build();
        using var services = new ServiceCollection().BuildServiceProvider();
        using var server = new JsonRpcServer(
            configuration,
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<JsonRpcServer>.Instance,
            new RemoteHostProfilingTelemetry(configuration));

        await server.StartAsync(CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(() => server.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10)));
        server.Dispose();

        Assert.Equal("not our socket", await File.ReadAllTextAsync(existingFile));
        Assert.Equal(originalMode, File.GetUnixFileMode(target.FullName));
    }

    [Fact]
    public async Task ConnectionSetupFailure_ClosesClientStreamAndLogsError()
    {
        using var workspace = CreateSocketWorkspace();
        var socketPath = OperatingSystem.IsWindows()
            ? $"aspire-remotehost-test-{Guid.NewGuid():N}"
            : Path.Combine(workspace.Path, "rpc", "rpc.sock");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["REMOTE_APP_HOST_SOCKET_PATH"] = socketPath
        }).Build();
        using var services = new ServiceCollection().BuildServiceProvider();
        var logger = new RecordingLogger<JsonRpcServer>();
        using var server = new JsonRpcServer(
            configuration,
            services.GetRequiredService<IServiceScopeFactory>(),
            logger,
            new RemoteHostProfilingTelemetry(configuration));

        await server.StartAsync(TestContext.Current.CancellationToken);
        await server.Listening.DefaultTimeout();
        await using var stream = await RemoteHostTestServer.ConnectToServerAsync(socketPath, TestContext.Current.CancellationToken);

        Assert.Equal(0, await stream.ReadAsync(new byte[1], TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        var entry = Assert.Single(logger.Entries, entry => entry.Level >= LogLevel.Warning);
        Assert.Equal(LogLevel.Error, entry.Level);
        var error = Assert.IsType<InvalidOperationException>(entry.Exception);
        Assert.Equal($"No service for type '{typeof(RemoteAppHostService).FullName}' has been registered.", error.Message);
        Assert.Matches("^Client [0-9a-f]{8} unexpected error$", entry.Message);

        await server.StopAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task StopAsync_AwaitsEveryAcceptedClientScopeCleanup(int clientCount)
    {
        using var workspace = CreateSocketWorkspace();
        var cleanups = Enumerable.Range(0, clientCount).Select(_ => new BlockingAsyncDisposable()).ToArray();
        var scopeIndex = -1;
        await using var server = await RemoteHostTestServer.StartAsync(workspace, existingDirectory: true, services =>
        {
            services.AddScoped(_ =>
            {
                var handles = new HandleRegistry();
                handles.Register(cleanups[Interlocked.Increment(ref scopeIndex)], "test/Cleanup");
                return handles;
            });
        });
        var clients = new List<JsonRpcClientHandle>();
        Task? stopping = null;
        try
        {
            for (var i = 0; i < clientCount; i++)
            {
                var client = await server.ConnectAsync();
                clients.Add(client);
                Assert.Equal("pong", await client.InvokeAsync<string>("ping"));
            }

            stopping = server.StopAsync(TestContext.Current.CancellationToken);
            await Task.WhenAll(cleanups.Select(cleanup => cleanup.Disposing.Task)).DefaultTimeout();
            Assert.False(stopping.IsCompleted);
            Assert.All(cleanups, cleanup => Assert.False(cleanup.Disposed.Task.IsCompleted));
        }
        finally
        {
            foreach (var cleanup in cleanups)
            {
                cleanup.ReleaseDisposal.TrySetResult();
            }
            if (stopping is not null)
            {
                await stopping.DefaultTimeout();
            }
            foreach (var client in clients)
            {
                await client.DisposeAsync();
            }
        }

        Assert.All(cleanups, cleanup => Assert.True(cleanup.Disposed.Task.IsCompletedSuccessfully));
    }

    [Fact]
    public async Task StopAsync_StopsAcceptedClientsApplicationBeforeDisposingIt()
    {
        using var workspace = CreateSocketWorkspace();
        var service = new BlockingHostedService();
        await using var application = RemoteHostShutdownTestServices.CreateApplication(service);
        await application.StartAsync(TestContext.Current.CancellationToken);
        await using var server = await RemoteHostTestServer.StartAsync(workspace, existingDirectory: true, services =>
        {
            services.AddScoped(_ =>
            {
                var handles = new HandleRegistry();
                handles.Register(application, "test/Application");
                return handles;
            });
        });
        await using var client = await server.ConnectAsync();
        Assert.Equal("pong", await client.InvokeAsync<string>("ping"));

        var stopping = server.StopAsync(TestContext.Current.CancellationToken);
        try
        {
            await service.Stopping.Task.DefaultTimeout();
            Assert.False(stopping.IsCompleted);
            Assert.False(service.Disposed.Task.IsCompleted);
        }
        finally
        {
            service.ReleaseStopping.TrySetResult();
            await stopping.DefaultTimeout();
        }

        Assert.Equal(["started", "stopping", "stopped", "disposed"], service.Events.ToArray());
    }

    [Fact]
    public async Task StopAsync_AwaitsNormalApplicationShutdownForInFlightRunRpc()
    {
        using var workspace = CreateSocketWorkspace();
        var service = new BlockingHostedService();
        await using var application = RemoteHostShutdownTestServices.CreateApplication(service);
        var applicationRun = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await RemoteHostTestServer.StartAsync(workspace, existingDirectory: true, services =>
        {
            services.AddScoped(sp =>
            {
                var dispatcher = new CapabilityDispatcher(
                    sp.GetRequiredService<HandleRegistry>(), sp.GetRequiredService<AtsMarshaller>(), []);
                dispatcher.Register("test/runApplication", async (_, handles) =>
                {
                    handles.Register(application, "test/Application");
                    // The application's run token is independent of JSON-RPC request cancellation,
                    // as with a guest invoking run without supplying an explicit cancellation token.
                    var running = application.RunAsync(CancellationToken.None);
                    applicationRun.TrySetResult(running);
                    await running;
                    return null;
                });
                return dispatcher;
            });
        });
        await using var client = await server.ConnectAsync();
        Assert.True(await client.InvokeAsync<bool>("authenticate", ["expected-token"]));
        var request = client.InvokeAsync<JsonElement?>("invokeCapability", ["test/runApplication", null]);
        var runningApplication = await applicationRun.Task.DefaultTimeout();
        await service.Started.Task.DefaultTimeout();

        var stopping = server.StopAsync(TestContext.Current.CancellationToken);
        try
        {
            await service.Stopping.Task.DefaultTimeout();
            Assert.False(stopping.IsCompleted);
            Assert.False(service.Disposed.Task.IsCompleted);
        }
        finally
        {
            service.ReleaseStopping.TrySetResult();
            try
            {
                await stopping.DefaultTimeout();
            }
            catch (TimeoutException)
            {
                // Keep a failing regression from leaving the independently running application alive.
                await application.StopAsync(TestContext.Current.CancellationToken);
                await stopping.DefaultTimeout();
                throw;
            }
        }

        await runningApplication.DefaultTimeout();
        await Assert.ThrowsAnyAsync<ConnectionLostException>(() => request.DefaultTimeout());
        var events = service.Events.ToArray();
        Assert.True(Array.IndexOf(events, "stopped") < Array.IndexOf(events, "disposed"));
        Assert.True(service.Disposed.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ClientDisconnect_StopsOwnedApplicationBeforeDisposalAndAllowsReconnect()
    {
        using var workspace = CreateSocketWorkspace();
        var firstService = new BlockingHostedService();
        var nextService = new BlockingHostedService();
        await using var firstApplication = RemoteHostShutdownTestServices.CreateApplication(firstService);
        await using var nextApplication = RemoteHostShutdownTestServices.CreateApplication(nextService);
        await firstApplication.StartAsync(TestContext.Current.CancellationToken);
        var applications = new[] { firstApplication, nextApplication };
        var scopeIndex = -1;
        await using var server = await RemoteHostTestServer.StartAsync(workspace, existingDirectory: true, services =>
        {
            services.AddScoped(_ =>
            {
                var handles = new HandleRegistry();
                handles.Register(applications[Interlocked.Increment(ref scopeIndex)], "test/Application");
                return handles;
            });
        });
        await using var firstClient = await server.ConnectAsync();
        Assert.Equal("pong", await firstClient.InvokeAsync<string>("ping"));

        try
        {
            await firstClient.DisposeAsync();
            await firstService.Stopping.Task.DefaultTimeout();
            Assert.False(firstService.Disposed.Task.IsCompleted);
            Assert.False(server.ExecuteTask.IsCompleted);
            firstService.ReleaseStopping.TrySetResult();
            await firstService.Disposed.Task.DefaultTimeout();
            Assert.Equal(["started", "stopping", "stopped", "disposed"], firstService.Events.ToArray());

            await nextApplication.StartAsync(TestContext.Current.CancellationToken);
            await using var nextClient = await server.ConnectAsync();
            Assert.Equal("pong", await nextClient.InvokeAsync<string>("ping"));
            Assert.False(nextService.Disposed.Task.IsCompleted);
            Assert.False(server.ExecuteTask.IsCompleted);
        }
        finally
        {
            firstService.ReleaseStopping.TrySetResult();
            nextService.ReleaseStopping.TrySetResult();
        }

        await nextService.Disposed.Task.DefaultTimeout();
        Assert.Equal(["started", "stopping", "stopped", "disposed"], nextService.Events.ToArray());
        Assert.False(server.ExecuteTask.IsCompleted);
    }

    [Fact]
    public async Task StopAsync_CancellationAfterAcceptStillDisposesClientScopeAndStream()
    {
        using var workspace = CreateSocketWorkspace();
        var cleanup = new BlockingAsyncDisposable();
        cleanup.ReleaseDisposal.TrySetResult();
        var sink = new TestSink();
        using var loggerFactory = new TestLoggerFactory(sink, enabled: true);
        await using var server = await RemoteHostTestServer.StartAsync(workspace, existingDirectory: true, services =>
        {
            services.AddSingleton<ILogger<JsonRpcServer>>(new TestLogger<JsonRpcServer>(loggerFactory));
            services.AddScoped(_ =>
            {
                var handles = new HandleRegistry();
                handles.Register(cleanup, "test/Cleanup");
                return handles;
            });
        });
        var stopping = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Cancel on the accept loop itself, after accept succeeds but before it queues the handler.
        // Passing that canceled token to Task.Run would skip the scope and strand the accepted stream.
        sink.MessageLogged += context =>
        {
            if (context.Message is "Client connected")
            {
                stopping.TrySetResult(server.StopAsync(TestContext.Current.CancellationToken));
            }
        };
        await using var stream = await RemoteHostTestServer.ConnectToServerAsync(server.SocketPath, TestContext.Current.CancellationToken);

        await cleanup.Disposed.Task.DefaultTimeout();
        await (await stopping.Task.DefaultTimeout()).DefaultTimeout();
        Assert.Equal(0, await stream.ReadAsync(new byte[1], TestContext.Current.CancellationToken).AsTask().DefaultTimeout());
        Assert.All(sink.Writes, entry => Assert.InRange(entry.LogLevel, LogLevel.Trace, LogLevel.Information));
    }

    [Fact]
    public async Task StopAsync_ShutdownDeadlineDoesNotAbandonTrackedClientCleanup()
    {
        using var workspace = CreateSocketWorkspace();
        var cleanup = new BlockingAsyncDisposable();
        var logger = new RecordingLogger<JsonRpcServer>();
        await using var server = await RemoteHostTestServer.StartAsync(workspace, existingDirectory: true, services =>
        {
            services.AddSingleton<ILogger<JsonRpcServer>>(logger);
            services.AddScoped(_ =>
            {
                var handles = new HandleRegistry();
                handles.Register(cleanup, "test/Cleanup");
                return handles;
            });
        });
        await using var client = await server.ConnectAsync();
        Assert.Equal("pong", await client.InvokeAsync<string>("ping"));
        using var deadline = new CancellationTokenSource();

        var stopping = server.StopAsync(deadline.Token);
        try
        {
            await cleanup.Disposing.Task.DefaultTimeout();
            deadline.Cancel();
            try
            {
                await stopping.DefaultTimeout();
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
            }

            Assert.False(server.ExecuteTask.IsCompleted);
            Assert.False(cleanup.Disposed.Task.IsCompleted);
            var entry = Assert.Single(logger.Entries, entry => entry.Level >= LogLevel.Warning);
            Assert.Equal(LogLevel.Warning, entry.Level);
        }
        finally
        {
            cleanup.ReleaseDisposal.TrySetResult();
            await server.ExecuteTask.DefaultTimeout();
        }

        Assert.True(cleanup.Disposed.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task StopAsync_ReportsAcceptedClientScopeCleanupFailure()
    {
        using var workspace = CreateSocketWorkspace();
        var exception = new InvalidOperationException("Client cleanup failed.");
        var cleanup = new BlockingAsyncDisposable { DisposalException = exception };
        cleanup.ReleaseDisposal.TrySetResult();
        var logger = new RecordingLogger<JsonRpcServer>();
        await using var server = await RemoteHostTestServer.StartAsync(workspace, existingDirectory: true, services =>
        {
            services.AddSingleton<ILogger<JsonRpcServer>>(logger);
            services.AddScoped(_ =>
            {
                var handles = new HandleRegistry();
                handles.Register(cleanup, "test/Cleanup");
                return handles;
            });
        });
        await using var client = await server.ConnectAsync();
        Assert.Equal("pong", await client.InvokeAsync<string>("ping"));

        await server.StopAsync(TestContext.Current.CancellationToken).DefaultTimeout();

        var entry = Assert.Single(logger.Entries, entry => entry.Level >= LogLevel.Warning);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(exception, entry.Exception);
        Assert.True(cleanup.Disposed.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Listening_CompletesBeforeFirstConnectionAttempt()
    {
        // Host startup does not wait for BackgroundService.ExecuteAsync to create the endpoint
        // (https://github.com/microsoft/aspire/issues/18840). Repeat to cover that scheduling race.
        for (var i = 0; i < 20; i++)
        {
            using var workspace = CreateSocketWorkspace();
            await using var server = await RemoteHostTestServer.StartAsync(workspace, existingDirectory: true);
            await using var client = await server.ConnectAsync();

            Assert.Equal("pong", await client.InvokeAsync<string>("ping"));
        }
    }

    [Fact]
    public async Task Listening_FaultsWhenServerFailsBeforeListening()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = CreateSocketWorkspace();
        var blockingFile = Path.Combine(workspace.Path, "not-a-directory");
        await File.WriteAllTextAsync(blockingFile, string.Empty, TestContext.Current.CancellationToken);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["REMOTE_APP_HOST_SOCKET_PATH"] = Path.Combine(blockingFile, "rpc.sock")
        }).Build();
        using var services = new ServiceCollection().BuildServiceProvider();
        using var server = new JsonRpcServer(
            configuration,
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<JsonRpcServer>.Instance,
            new RemoteHostProfilingTelemetry(configuration));

        await server.StartAsync(TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAnyAsync<IOException>(() => server.Listening.DefaultTimeout());
        Assert.Same(error, await Assert.ThrowsAnyAsync<IOException>(() => server.ExecuteTask!.DefaultTimeout()));
    }

    [Fact]
    public async Task Listening_IsCanceledWhenServerStopsBeforeListening()
    {
        using var workspace = CreateSocketWorkspace();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["REMOTE_APP_HOST_SOCKET_PATH"] = OperatingSystem.IsWindows()
                ? $"aspire-remotehost-test-{Guid.NewGuid():N}"
                : Path.Combine(workspace.Path, "rpc", "rpc.sock")
        }).Build();
        using var services = new ServiceCollection().BuildServiceProvider();
        using var server = new JsonRpcServer(
            configuration,
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<JsonRpcServer>.Instance,
            new RemoteHostProfilingTelemetry(configuration));

        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        await server.StartAsync(stopped.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server.Listening.DefaultTimeout());
    }

    private TemporaryWorkspace CreateSocketWorkspace()
    {
        // The default workspace path includes the assembly name and can exceed Unix socket
        // path limits. Use a short root while retaining TemporaryWorkspace's cleanup retries.
        return new TemporaryWorkspace(outputHelper, Directory.CreateTempSubdirectory());
    }

    private sealed class RemoteHostTestServer : IAsyncDisposable
    {
        private const string RemoteAppHostToken = "ASPIRE_REMOTE_APPHOST_TOKEN";
        private readonly IHost _host;
        private readonly string _socketPath;

        public string SocketPath => _socketPath;
        public Task ExecuteTask => _host.Services.GetServices<IHostedService>().OfType<JsonRpcServer>().Single().ExecuteTask!;

        private RemoteHostTestServer(IHost host, string socketPath)
        {
            _host = host;
            _socketPath = socketPath;
        }

        public static Task<RemoteHostTestServer> StartAsync(TemporaryWorkspace workspace, bool existingDirectory)
            => StartAsync(workspace, existingDirectory, static _ => { });

        public static async Task<RemoteHostTestServer> StartAsync(
            TemporaryWorkspace workspace, bool existingDirectory, Action<IServiceCollection> configureServices)
        {
            if (!OperatingSystem.IsWindows() && existingDirectory)
            {
                var directory = Path.Combine(workspace.Path, "rpc");
                Directory.CreateDirectory(directory);
                File.SetUnixFileMode(directory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var socketPath = OperatingSystem.IsWindows()
                ? $"aspire-remotehost-test-{Guid.NewGuid():N}"
                : Path.Combine(workspace.Path, "rpc", "rpc.sock");

            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["REMOTE_APP_HOST_SOCKET_PATH"] = socketPath,
                [RemoteAppHostToken] = "expected-token"
            });

            ConfigureServices(builder.Services);
            configureServices(builder.Services);
            builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            }));

            var host = builder.Build();
            await host.StartAsync();
            try
            {
                await host.Services.GetServices<IHostedService>().OfType<JsonRpcServer>().Single().Listening.DefaultTimeout();
            }
            catch
            {
                await host.StopAsync();
                host.Dispose();
                throw;
            }

            return new RemoteHostTestServer(host, socketPath);
        }

        public async Task<JsonRpcClientHandle> ConnectAsync()
        {
            var stream = await ConnectToServerAsync(_socketPath, CancellationToken.None);
            var formatter = new SystemTextJsonFormatter();
            var handler = new HeaderDelimitedMessageHandler(stream, stream, formatter);
            var rpc = new JsonRpc(handler);
            rpc.StartListening();

            return new JsonRpcClientHandle(stream, rpc);
        }

        public static async Task WaitForDisconnectAsync(JsonRpcClientHandle client)
        {
            var completedTask = await Task.WhenAny(client.Completion, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(client.Completion, completedTask);
        }

        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        public Task StopAsync(CancellationToken cancellationToken) => _host.StopAsync(cancellationToken);

        private static void ConfigureServices(IServiceCollection services)
        {
            services.AddHostedService<JsonRpcServer>();

            services.AddSingleton<RemoteHostProfilingTelemetry>();
            services.AddSingleton<AssemblyLoader>();
            services.AddSingleton<Aspire.Hosting.RemoteHost.AtsContextFactory>();
            services.AddSingleton(sp => sp.GetRequiredService<Aspire.Hosting.RemoteHost.AtsContextFactory>().GetContext());
            services.AddSingleton<CodeGeneration.CodeGeneratorResolver>();
            services.AddScoped<CodeGeneration.CodeGenerationService>();
            services.AddSingleton<Language.LanguageSupportResolver>();
            services.AddScoped<Language.LanguageService>();

            // CodeGenerationService depends on ExternalCapabilityRegistry + IntegrationHostLauncher
            services.AddSingleton<Ats.ExternalCapabilityRegistry>();
            services.AddSingleton<Language.IntegrationHostLauncher>();
            services.AddSingleton(sp => new Language.IntegrationHostConfiguration(sp.GetRequiredService<IConfiguration>()));
            services.AddSingleton<Language.IntegrationHostProcessLauncher>();
            services.AddSingleton<IChildProcessFactory, ChildProcessFactory>();
            services.AddSingleton(TimeProvider.System);

            services.AddScoped<JsonRpcAuthenticationState>();
            services.AddScoped<HandleRegistry>();
            services.AddScoped<CancellationTokenRegistry>();
            services.AddScoped<JsonRpcCallbackInvoker>();
            services.AddScoped<ICallbackInvoker>(sp => sp.GetRequiredService<JsonRpcCallbackInvoker>());
            services.AddScoped<Ats.AtsCallbackProxyFactory>();
            services.AddScoped(sp => new Lazy<Ats.AtsCallbackProxyFactory>(() => sp.GetRequiredService<Ats.AtsCallbackProxyFactory>()));
            services.AddScoped<Ats.AtsMarshaller>();
            services.AddScoped<Ats.CapabilityDispatcher>();
            services.AddScoped<RemoteAppHostService>();
        }

        // Callers must first observe JsonRpcServer.Listening, so a single connection attempt is expected to succeed.
        public static async Task<Stream> ConnectToServerAsync(string socketPath, CancellationToken cancellationToken)
        {
            // Bounds the single attempt (including time queued on the thread pool) so a stalled connect fails instead of hanging.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TestConstants.DefaultTimeoutTimeSpan);

            if (OperatingSystem.IsWindows())
            {
                var pipeClient = new NamedPipeClientStream(".", socketPath, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    // A zero timeout makes one attempt instead of polling until the pipe appears.
                    await pipeClient.ConnectAsync(timeout: 0, timeoutCts.Token);
                    return pipeClient;
                }
                catch
                {
                    await pipeClient.DisposeAsync();
                    throw;
                }
            }

            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), timeoutCts.Token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }

    private sealed class JsonRpcClientHandle : IAsyncDisposable
    {
        private readonly Stream _stream;
        private readonly JsonRpc _rpc;

        public JsonRpcClientHandle(Stream stream, JsonRpc rpc)
        {
            _stream = stream;
            _rpc = rpc;
        }

        public Task Completion => _rpc.Completion;

        public Task<T> InvokeAsync<T>(string methodName, params object?[] arguments)
            => _rpc.InvokeWithCancellationAsync<T>(methodName, arguments, CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            _rpc.Dispose();
            await _stream.DisposeAsync();
        }
    }
}
