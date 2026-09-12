// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Hex1b;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.Tests.Utils;

internal sealed class TestResourceTerminalHost : IAsyncDisposable
{
    private readonly Hex1bTerminal _terminal;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _runTask;
    private Task? _disposeTask;

    private TestResourceTerminalHost(string socketPath)
    {
        SocketPath = socketPath;
        _terminal = Hex1bTerminal.CreateBuilder()
            .WithHeadless()
            .WithDimensions(120, 40)
            .WithPtyProcess("bash")
            .WithHmp1UdsServer(socketPath)
            .Build();
        _runTask = _terminal.RunAsync(_cts.Token);
    }

    public string SocketPath { get; }

    public static async Task<TestResourceTerminalHost> StartAsync(string socketPath)
    {
        var host = new TestResourceTerminalHost(socketPath);
        try
        {
            await AsyncTestHelpers.AssertIsTrueRetryAsync(() =>
            {
                if (host._runTask.IsCompleted)
                {
                    host._runTask.GetAwaiter().GetResult();
                    throw new InvalidOperationException("The terminal host exited before its socket became available.");
                }

                return File.Exists(socketPath);
            }, $"The terminal host did not begin listening on '{socketPath}'.");

            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    public ValueTask DisposeAsync() => new(_disposeTask ??= DisposeCoreAsync());

    private async Task DisposeCoreAsync()
    {
        await _cts.CancelAsync();
        try
        {
            await _runTask;
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // Expected when the test shuts down the terminal host.
        }
        finally
        {
            await _terminal.DisposeAsync();
            _cts.Dispose();
        }
    }
}
