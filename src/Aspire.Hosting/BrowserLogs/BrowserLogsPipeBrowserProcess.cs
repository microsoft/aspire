// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dcp.Process;

namespace Aspire.Hosting;

internal interface IBrowserLogsPipeBrowserProcess : IAsyncDisposable
{
    int ProcessId { get; }

    Stream BrowserOutput { get; }

    Stream BrowserInput { get; }

    Task<ProcessResult> ProcessTask { get; }

    ValueTask DisposeAsync(bool terminateProcess);
}

internal sealed class BrowserLogsPipeBrowserProcess(
    int processId,
    Stream browserOutput,
    Stream browserInput,
    Task<ProcessResult> processTask,
    IBrowserLogsPipeBrowserProcessLifetime processLifetime) : IBrowserLogsPipeBrowserProcess
{
    private readonly Stream _browserInput = browserInput;
    private readonly Stream _browserOutput = browserOutput;
    private readonly IBrowserLogsPipeBrowserProcessLifetime _processLifetime = processLifetime;
    private int _disposed;

    public int ProcessId { get; } = processId;

    public Stream BrowserOutput => _browserOutput;

    public Stream BrowserInput => _browserInput;

    public Task<ProcessResult> ProcessTask { get; } = processTask;

    public ValueTask DisposeAsync() => DisposeAsync(terminateProcess: true);

    public async ValueTask DisposeAsync(bool terminateProcess)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await _browserInput.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await _browserOutput.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await _processLifetime.DisposeAsync(terminateProcess).ConfigureAwait(false);
            }
        }
    }
}

internal interface IBrowserLogsPipeBrowserProcessLifetime
{
    ValueTask DisposeAsync(bool terminateProcess);
}
