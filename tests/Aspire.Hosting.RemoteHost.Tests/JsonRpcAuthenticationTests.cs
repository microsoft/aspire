// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;
using Aspire.Hosting.RemoteHost.Ats;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StreamJsonRpc;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

public sealed class JsonRpcAuthenticationTests
{
    public static TheoryData<string, object?[]> ProtectedMethods => new()
    {
        { "cancelToken", ["ct_missing"] },
        { "invokeCapability", ["test-capability", null] },
        { "getCapabilities", [] },
        { "generateCode", ["TypeScript"] },
        { "scaffoldAppHost", ["TypeScript", "/tmp/apphost", "AppHost"] },
        { "detectAppHostType", ["/tmp/apphost"] },
        { "getRuntimeSpec", ["TypeScript"] }
    };

    [Fact]
    public async Task Ping_DoesNotRequireAuthentication()
    {
        await using var server = await RemoteHostTestServer.StartAsync();
        await using var client = await server.ConnectAsync();

        var result = await client.InvokeAsync<string>("ping");

        Assert.Equal("pong", result);
    }

    [Theory]
    [MemberData(nameof(ProtectedMethods))]
    public async Task ProtectedMethods_RequireAuthentication(string methodName, object?[] arguments)
    {
        await using var server = await RemoteHostTestServer.StartAsync();
        await using var client = await server.ConnectAsync();

        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(
            () => client.InvokeAsync<JsonElement?>(methodName, arguments));

        Assert.Contains("Client must authenticate before invoking AppHost RPC methods.", ex.Message);
    }

    [Fact]
    public async Task FailedAuthentication_ClosesConnection_AndPreventsFurtherCalls()
    {
        await using var server = await RemoteHostTestServer.StartAsync();
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

    private sealed class RemoteHostTestServer : IAsyncDisposable
    {
        private const string RemoteAppHostToken = "ASPIRE_REMOTE_APPHOST_TOKEN";
        private readonly IHost _host;
        private readonly string _socketPath;
        private readonly string? _socketDirectory;

        private RemoteHostTestServer(IHost host, string socketPath, string? socketDirectory)
        {
            _host = host;
            _socketPath = socketPath;
            _socketDirectory = socketDirectory;
        }

        public static async Task<RemoteHostTestServer> StartAsync()
        {
            var socketDirectory = OperatingSystem.IsWindows()
                ? null
                : Path.Combine(Path.GetTempPath(), $"arh-{Guid.NewGuid():N}"[..12]);

            if (socketDirectory is not null)
            {
                Directory.CreateDirectory(socketDirectory);
            }

            var socketPath = OperatingSystem.IsWindows()
                ? $"aspire-remotehost-test-{Guid.NewGuid():N}"
                : Path.Combine(socketDirectory!, "rpc.sock");

            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["REMOTE_APP_HOST_SOCKET_PATH"] = socketPath,
                [RemoteAppHostToken] = "expected-token"
            });

            ConfigureServices(builder.Services);

            var host = builder.Build();
            await host.StartAsync();

            return new RemoteHostTestServer(host, socketPath, socketDirectory);
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

            if (!OperatingSystem.IsWindows() && File.Exists(_socketPath))
            {
                File.Delete(_socketPath);
            }

            if (!string.IsNullOrEmpty(_socketDirectory) && Directory.Exists(_socketDirectory))
            {
                Directory.Delete(_socketDirectory, recursive: true);
            }
        }

        private static void ConfigureServices(IServiceCollection services)
        {
            services.AddHostedService<JsonRpcServer>();

            services.AddSingleton<AssemblyLoader>();
            services.AddSingleton<Aspire.Hosting.RemoteHost.AtsContextFactory>();
            services.AddSingleton(sp => sp.GetRequiredService<Aspire.Hosting.RemoteHost.AtsContextFactory>().GetContext());
            services.AddSingleton<CodeGeneration.CodeGeneratorResolver>();
            services.AddScoped<CodeGeneration.CodeGenerationService>();
            services.AddSingleton<Language.LanguageSupportResolver>();
            services.AddScoped<Language.LanguageService>();

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

        private static async Task<Stream> ConnectToServerAsync(string socketPath, CancellationToken cancellationToken)
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            Exception? lastException = null;

            while (!linkedCts.Token.IsCancellationRequested)
            {
                try
                {
                    if (OperatingSystem.IsWindows())
                    {
                        var pipeClient = new NamedPipeClientStream(".", socketPath, PipeDirection.InOut, PipeOptions.Asynchronous);
                        await pipeClient.ConnectAsync(linkedCts.Token);
                        return pipeClient;
                    }

                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), linkedCts.Token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or OperationCanceledException)
                {
                    lastException = ex;

                    if (timeoutCts.IsCancellationRequested)
                    {
                        break;
                    }

                    await Task.Delay(100, cancellationToken);
                }
            }

            throw new TimeoutException($"Timed out connecting to test RPC server '{socketPath}'.", lastException);
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
