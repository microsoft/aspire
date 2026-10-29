// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Aspire.Hosting.Native.Rpc;
using Microsoft.Extensions.Logging.Abstractions;
using Aspire.Hosting.Native.Runtime;

namespace Aspire.Hosting.Native.Core.Tests.TestServices;

/// <summary>Runs the actual native socket protocol, optionally against a separately published executable.</summary>
internal sealed class NativeClientTestHost(TimeProvider timeProvider) : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private NativeRpcListener? _listener;
    private Task? _listening;
    private Process? _process;
    private Task<string>? _stderr;
    private Task<string>? _stdout;

    public string Token { get; } = RandomNumberGenerator.GetHexString(64);
    public EndPoint Endpoint { get; private set; } = null!;

    public static Task<NativeClientTestHost> StartAsync() =>
        StartCoreAsync(Environment.GetEnvironmentVariable("ASPIRE_NATIVE_CSHARP_TEST_SERVER"), null, TimeProvider.System, null);

    public static Task<NativeClientTestHost> StartWithWorkloadsAsync(string executable, string dcpPath) =>
        StartCoreAsync(executable, dcpPath, TimeProvider.System, null);
    public static Task<NativeClientTestHost> StartWithWorkloadsAsync(string executable, string dcpPath,
        IReadOnlyDictionary<string, string> environment) =>
        StartCoreAsync(executable, dcpPath, TimeProvider.System, environment);
    public static Task<NativeClientTestHost> StartWithOptionsAsync(Aspire.Hosting.Native.Server.NativeServerOptions options) =>
        StartCoreAsync(null, null, TimeProvider.System, null, options);
    public static Task<NativeClientTestHost> StartWithExecutorAsync(IWorkloadExecutor executor,
        Aspire.Hosting.Native.Server.NativeServerOptions options) =>
        StartCoreAsync(null, null, TimeProvider.System, null, options, executor);

    private static async Task<NativeClientTestHost> StartCoreAsync(string? executablePath, string? dcpPath, TimeProvider timeProvider,
        IReadOnlyDictionary<string, string>? environment,
        Aspire.Hosting.Native.Server.NativeServerOptions? options = null, IWorkloadExecutor? executor = null)
    {
        var host = new NativeClientTestHost(timeProvider);
        try
        {
            if (executablePath is { Length: > 0 } executable)
            {
                // Both native implementations expose the same --port transport and
                // authentication environment. There is no backend-specific client path.
                var start = new ProcessStartInfo(executable)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                start.ArgumentList.Add("--port");
                start.ArgumentList.Add("0");
                start.Environment["ASPIRE_NATIVE_RPC_TOKEN"] = host.Token;
                foreach (var name in new[]
                {
                    "ASPIRE_NATIVE_DCP_PATH", "ASPIRE_DASHBOARD_PATH", "ASPIRE_NATIVE_DASHBOARD_PORT",
                    "ASPIRE_BACKCHANNEL_PATH", "REMOTE_APP_HOST_SOCKET_PATH", "ASPIRE_NATIVE_SERVER_OPTIONS_PATH"
                })
                {
                    start.Environment.Remove(name);
                }
                if (dcpPath is not null)
                {
                    start.Environment["ASPIRE_NATIVE_DCP_PATH"] = dcpPath;
                }
                if (environment is not null)
                {
                    foreach (var variable in environment)
                    {
                        start.Environment[variable.Key] = variable.Value;
                    }
                }
                host._process = Process.Start(start) ?? throw new InvalidOperationException("The native test server did not start.");
                host._stderr = host._process.StandardError.ReadToEndAsync();
                using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(15), timeProvider);
                // Direct native launches announce:
                //   Native AppHost server listening on 127.0.0.1:<allocated-port>.
                // This discovers transport coordinates only; readiness is authenticated RPC.
                const string prefix = "Native AppHost server listening on ";
                while (await host._process.StandardOutput.ReadLineAsync(startup.Token) is { } line)
                {
                    if (line.StartsWith(prefix, StringComparison.Ordinal) &&
                        IPEndPoint.TryParse(line[prefix.Length..].TrimEnd('.'), out var endpoint))
                    {
                        host.Endpoint = endpoint;
                        host._stdout = host._process.StandardOutput.ReadToEndAsync();

                        return host;
                    }
                }
                throw new InvalidOperationException("The native test server did not publish its endpoint.");
            }
            host._listener = options is null
                ? new NativeRpcListener(new IPEndPoint(IPAddress.Loopback, 0), host.Token, NullLogger.Instance)
                : new NativeRpcListener(new IPEndPoint(IPAddress.Loopback, 0), host.Token, NullLogger.Instance,
                    new Aspire.Hosting.Native.Api.NativeApplicationServer(executor ?? UnavailableWorkloadExecutor.Instance, options.Runtime), null, options);
            host.Endpoint = host._listener.EndPoint;
            host._listening = host._listener.RunAsync(host._lifetime.Token);

            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        _listener?.Dispose();
        if (_listening is not null)
        {
            await _listening.WaitAsync(TimeSpan.FromSeconds(15), timeProvider);
        }
        if (_process is { } process)
        {
            // Only this host's explicitly tracked process is terminated.
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15), timeProvider);
            if (_stdout is not null)
            {
                await _stdout;
            }
            if (_stderr is not null)
            {
                await _stderr;
            }
            process.Dispose();
        }
        _lifetime.Dispose();
    }
}
