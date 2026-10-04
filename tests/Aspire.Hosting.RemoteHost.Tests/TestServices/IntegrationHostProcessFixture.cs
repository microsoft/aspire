// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;
using StreamJsonRpc;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

public sealed class IntegrationHostProcessFixture : IAsyncLifetime
{
#if DEBUG
    private const string BuildConfiguration = "Debug";
#else
    private const string BuildConfiguration = "Release";
#endif
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory();
    private string _hostAssembly = "";

    public async ValueTask InitializeAsync()
    {
        // A separate entry point exercises the production guardian re-entry path.
        // The MTP executable cannot be reused: it would start another test run.
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(repository.FullName, "Aspire.slnx")))
        {
            repository = repository.Parent ?? throw new InvalidOperationException("Cannot find the repository root.");
        }
        var project = Path.Combine(_directory.FullName, "LifetimeTestHost.csproj");
        await File.WriteAllTextAsync(project, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <DisableTransitiveProjectReferences>true</DisableTransitiveProjectReferences>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="{Path.Combine(repository.FullName, "src", "Aspire.Hosting.RemoteHost", "Aspire.Hosting.RemoteHost.csproj")}" />
                <ProjectReference Include="{Path.Combine(repository.FullName, "src", "Aspire.TypeSystem", "Aspire.TypeSystem.csproj")}" />
              </ItemGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(_directory.FullName, "Program.cs"), HostProgram);
        await RunDotnetAsync(repository.FullName,
            ["restore", project, $"--property:Configuration={BuildConfiguration}", "--configfile", Path.Combine(repository.FullName, "NuGet.config"), "--verbosity", "quiet"]);
        await RunDotnetAsync(repository.FullName,
            ["build", project, "--configuration", BuildConfiguration, "--no-restore", "--property:BuildProjectReferences=false", "--verbosity", "quiet"]);
        _hostAssembly = Path.Combine(_directory.FullName, "bin", BuildConfiguration, "net10.0", "LifetimeTestHost.dll");
    }

    internal async Task<IntegrationHostServerProcess> StartAsync(bool failStartup = false, bool skipRegistration = false, bool useAppHostExecutable = false)
    {
        var directory = _directory.CreateSubdirectory(Guid.NewGuid().ToString("N")[..8]);
        var script = Path.Combine(directory.FullName, "host.js");
        await File.WriteAllTextAsync(script, HostScript);
        await File.WriteAllTextAsync(Path.Combine(directory.FullName, "appsettings.json"), JsonSerializer.Serialize(new
        {
            AtsAssemblies = new[] { "LifetimeTestHost" },
            IntegrationHosts = new[]
            {
                new { Language = "test/node", PackageName = "lifetime-test", HostEntryPoint = script }
            }
        }));
        if (failStartup)
        {
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "fail-startup"), "");
        }
        if (skipRegistration)
        {
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "skip-registration"), "");
        }

        return new IntegrationHostServerProcess(directory.FullName, _hostAssembly, useAppHostExecutable);
    }

    private static async Task RunDotnetAsync(string workingDirectory, string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet", arguments)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromMinutes(2));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            await Task.WhenAll(output, error);
        }
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Fixture build failed: {await output}\n{await error}");
        }
    }

    public ValueTask DisposeAsync()
    {
        _directory.Delete(recursive: true);

        return ValueTask.CompletedTask;
    }

    private const string HostProgram = """
        using System.Reflection;
        using System.Text.Json;
        using Aspire.Hosting.RemoteHost;
        using Aspire.TypeSystem;
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.Hosting;

        if (Environment.GetEnvironmentVariable("ASPIRE_INTEGRATION_HOST_SUPERVISOR_COMMAND") is not null)
        {
            await RemoteHostServer.RunAsync(args);
            return;
        }

        var createBuilder = typeof(RemoteHostServer).GetMethod("CreateBuilder", BindingFlags.NonPublic | BindingFlags.Static)!;
        var builder = (HostApplicationBuilder)createBuilder.Invoke(null, new object[] { args })!;
        builder.Services.Insert(0, ServiceDescriptor.Singleton<IHostedService, StopSignal>());
        using var host = builder.Build();
        var launcher = host.Services.GetServices<IHostedService>().Single(service => service.GetType().Name == "IntegrationHostLauncher");
        await host.RunAsync();
        launcher.GetType().GetMethod("ThrowIfFailed", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(launcher, null);

        public sealed class NodeLanguageSupport : ILanguageSupport
        {
            public string Language => "test/node";
            public Dictionary<string, string> Scaffold(ScaffoldRequest request) => throw new NotSupportedException();
            public DetectionResult Detect(string directoryPath) => throw new NotSupportedException();
            public RuntimeSpec GetRuntimeSpec() => throw new NotSupportedException();
            public JsonElement GetIntegrationHostSpec() => JsonSerializer.SerializeToElement(new
            {
                execute = new { command = "node", args = new[] { "{entryPoint}" } }
            });
        }

        public sealed class StopSignal(IHostApplicationLifetime lifetime) : BackgroundService
        {
            protected override async Task ExecuteAsync(CancellationToken stoppingToken)
            {
                while (!File.Exists("stop-server"))
                {
                    await Task.Delay(50, stoppingToken);
                }
                lifetime.StopApplication();
            }
        }
        """;

    // HeaderDelimitedMessageHandler uses Content-Length framing, not newline JSON.
    // Example: Content-Length: 62\r\n\r\n{"jsonrpc":"2.0","id":1,...}
    // Accumulate split headers/bodies and dispatch every complete frame in a chunk.
    private const string HostScript = """
        const fs = require('node:fs');
        const net = require('node:net');
        const { spawn } = require('node:child_process');
        if (fs.existsSync('fail-startup')) {
            console.error('original-runtime-startup-error');
            process.exit(23);
        }
        const generation = fs.existsSync('generation') ? Number(fs.readFileSync('generation', 'utf8')) + 1 : 1;
        fs.writeFileSync('generation', String(generation));
        fs.appendFileSync('host-pids', `${process.pid}\n`);
        if (fs.existsSync('skip-registration')) Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0);
        const path = process.env.REMOTE_APP_HOST_SOCKET_PATH;
        const socket = net.createConnection(process.platform === 'win32' ? `\\\\.\\pipe\\${path}` : path);
        socket.on('close', () => process.exit(0));
        socket.on('error', error => { console.error(error); process.exit(1); });
        let buffer = Buffer.alloc(0);
        let nextId = 1;
        const pending = new Map();
        const send = value => {
            const body = Buffer.from(JSON.stringify(value));
            socket.write(Buffer.concat([Buffer.from(`Content-Length: ${body.length}\r\n\r\n`), body]));
        };
        const request = (method, params) => new Promise((resolve, reject) => {
            const id = nextId++;
            pending.set(id, { resolve, reject });
            send({ jsonrpc: '2.0', id, method, params });
        });
        socket.on('data', chunk => {
            buffer = Buffer.concat([buffer, chunk]);
            while (true) {
                const headerEnd = buffer.indexOf('\r\n\r\n');
                if (headerEnd < 0) return;
                const length = Number(/Content-Length:\s*(\d+)/i.exec(buffer.subarray(0, headerEnd).toString())[1]);
                if (buffer.length < headerEnd + 4 + length) return;
                const message = JSON.parse(buffer.subarray(headerEnd + 4, headerEnd + 4 + length));
                buffer = buffer.subarray(headerEnd + 4 + length);
                if (!message.method) {
                    const call = pending.get(message.id);
                    pending.delete(message.id);
                    if (message.error) call?.reject(new Error(message.error.message)); else call?.resolve(message.result);
                    continue;
                }
                if (message.method === 'getCapabilities') {
                    send({ jsonrpc: '2.0', id: message.id, result: { capabilities: [{
                        id: 'test.external/value', method: 'value',
                        returnType: { typeId: 'string', category: 'Primitive' }
                    }] } });
                    if (fs.existsSync('crash-loop')) setTimeout(() => process.exit(24), 100);
                } else if (message.method === 'handleExternalCapability') {
                    const args = message.params[1] ?? {};
                    if (args.action === 'crash' || args.action === 'crashLoop') {
                        if (args.action === 'crashLoop') fs.writeFileSync('crash-loop', '');
                        fs.appendFileSync('side-effects', 'once\n');
                        process.exit(23);
                    }
                    if (args.action === 'supervisor') {
                        send({ jsonrpc: '2.0', id: message.id, result: String(process.ppid) });
                    } else if (args.action === 'disconnect') {
                        socket.destroy();
                        Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0);
                    } else if (args.action === 'worker') {
                        const child = spawn(process.execPath, ['-e', 'Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0)'], { stdio: 'inherit' });
                        fs.writeFileSync('worker-pid', String(child.pid));
                        send({ jsonrpc: '2.0', id: message.id, result: String(child.pid) });
                    } else if (args.action === 'block') {
                        fs.writeFileSync('host-blocked', '');
                        Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0);
                    } else {
                        send({ jsonrpc: '2.0', id: message.id, result: `generation-${generation}` });
                    }
                }
            }
        });
        socket.once('connect', async () => {
            try {
                await request('authenticate', [process.env.ASPIRE_REMOTE_APPHOST_TOKEN]);
                await request('registerAsIntegrationHost', [process.env.ASPIRE_INTEGRATION_HOST_REGISTRATION_ID]);
            } catch (error) {
                console.error(error);
                process.exit(1);
            }
        });
        """;
}

internal sealed class IntegrationHostServerProcess : IAsyncDisposable
{
    private readonly Process _server;
    private readonly string _socketPath;
    private readonly ConcurrentQueue<string> _diagnostics = new();
    private readonly Task _readers;
    private JsonRpc? _rpc;
    private Task? _disposeTask;
    private int? _exitCode;

    public IntegrationHostServerProcess(string directory, string hostAssembly, bool useAppHostExecutable)
    {
        Directory = directory;
        _socketPath = OperatingSystem.IsWindows()
            ? $"aspire-host-lifetime-{Guid.NewGuid():N}"
            : Path.Combine(directory, "r", "s");
        var executable = OperatingSystem.IsWindows() ? Path.ChangeExtension(hostAssembly, ".exe") : Path.ChangeExtension(hostAssembly, null);
        var startInfo = useAppHostExecutable ? new ProcessStartInfo(executable) : new ProcessStartInfo("dotnet", [hostAssembly]);
        startInfo.WorkingDirectory = directory;
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.Environment["REMOTE_APP_HOST_SOCKET_PATH"] = _socketPath;
        startInfo.Environment["ASPIRE_REMOTE_APPHOST_TOKEN"] = "lifetime-test-token";
        _server = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start test server.");
        Exit = _server.WaitForExitAsync();
        _readers = Task.WhenAll(ReadAsync(_server.StandardOutput), ReadAsync(_server.StandardError));
    }

    public string Directory { get; }
    public Task Exit { get; }
    public int ExitCode => _exitCode ?? _server.ExitCode;
    public string Diagnostics => string.Join(Environment.NewLine, _diagnostics);

    public async Task ReadyAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (_rpc is null)
        {
            if (_server.HasExited)
            {
                throw new InvalidOperationException($"Test server exited during startup: {Diagnostics}");
            }
            Stream? stream = null;
            Socket? socket = null;
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var pipe = new NamedPipeClientStream(".", _socketPath, PipeDirection.InOut, PipeOptions.Asynchronous);
                    stream = pipe;
                    await pipe.ConnectAsync(timeout.Token);
                }
                else
                {
                    socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), timeout.Token);
                    stream = new NetworkStream(socket, ownsSocket: true);
                    socket = null;
                }
                _rpc = new JsonRpc(new HeaderDelimitedMessageHandler(stream, stream, new SystemTextJsonFormatter()));
                _rpc.StartListening();
            }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                stream?.Dispose();
                socket?.Dispose();
                await Task.Delay(50, timeout.Token);
            }
        }
        Assert.True(await _rpc.InvokeWithCancellationAsync<bool>("authenticate", ["lifetime-test-token"], timeout.Token));
        await _rpc.InvokeWithCancellationAsync<JsonElement>("getCapabilities", [], timeout.Token);
    }

    public Task<JsonElement> InvokeAsync(string? action = null)
        => (_rpc ?? throw new InvalidOperationException("Wait for server readiness before invoking."))
            .InvokeWithCancellationAsync<JsonElement>("invokeCapability",
                ["test.external/value", action is null ? null : new { action }], TestContext.Current.CancellationToken);

    public void KillServer() => _server.Kill(entireProcessTree: false);

    public async Task WaitForRecoveryAsync(string expected)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            JsonElement result;
            try
            {
                result = await InvokeAsync().WaitAsync(timeout.Token);
            }
            catch (ConnectionLostException ex)
            {
                throw new InvalidOperationException($"Server exited during recovery: {Diagnostics}", ex);
            }
            if (result.ValueKind == JsonValueKind.String && result.GetString() == expected)
            {
                return;
            }
            await Task.Delay(50, timeout.Token);
        }
    }

    public async Task WaitForFileAsync(string name)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!File.Exists(Path.Combine(Directory, name)))
        {
            await Task.Delay(25, timeout.Token);
        }
    }

    public int[] HostProcessIds => File.ReadAllLines(Path.Combine(Directory, "host-pids")).Select(int.Parse).ToArray();

    public static async Task AssertExitedAsync(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(process.HasExited);
        }
        catch (ArgumentException)
        {
            // Already reaped before the observer acquired a process handle.
        }
    }

    private async Task ReadAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            _diagnostics.Enqueue(line);
        }
    }

    public ValueTask DisposeAsync() => new(_disposeTask ??= DisposeCoreAsync());

    private async Task DisposeCoreAsync()
    {
        _rpc?.Dispose();
        if (!_server.HasExited)
        {
            await File.WriteAllTextAsync(Path.Combine(Directory, "stop-server"), "");
        }
        try
        {
            await Exit.WaitAsync(TimeSpan.FromSeconds(15));
            _exitCode = _server.ExitCode;
        }
        catch (TimeoutException)
        {
            KillServer();
            await Exit.WaitAsync(TimeSpan.FromSeconds(10));
            throw new TimeoutException($"Test server did not stop: {Diagnostics}");
        }
        finally
        {
            await _readers.WaitAsync(TimeSpan.FromSeconds(10));
            _server.Dispose();
        }
    }
}
