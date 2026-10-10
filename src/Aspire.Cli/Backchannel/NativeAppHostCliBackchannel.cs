// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Text.Json;
using Aspire.Cli.Projects;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Backchannel;

// The native server does not speak the managed streaming backchannel protocol.
// This CLI-side adapter exposes its bounded, headless control-plane contract.
internal sealed class NativeAppHostCliBackchannel(IAppHostServerSession session, TimeSpan startupTimeout) : IAppHostCliBackchannel
{
    public Task ConnectAsync(string socketPath, int retryCount, CancellationToken cancellationToken)
        => ConnectAsync(socketPath, false, retryCount, cancellationToken);

    public async Task ConnectAsync(string socketPath, bool autoReconnect, int retryCount, CancellationToken cancellationToken)
    {
        if (autoReconnect)
        {
            throw new NotSupportedException("Native CLI watch mode is not implemented. Use an explicit guest graph replacement.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(startupTimeout);
        try
        {
            var rpc = await session.GetRpcClientAsync(timeout.Token).ConfigureAwait(false);
            while (true)
            {
                var state = await rpc.InvokeAsync<JsonElement>("getRuntimeState", [], timeout.Token).ConfigureAwait(false);
                if (state.GetProperty("ready").GetBoolean())
                {
                    return;
                }

                if (session.HasServerExited == true)
                {
                    throw new InvalidOperationException("The native AppHost server exited before graph readiness.");
                }

                await Task.Delay(100, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The native graph did not become ready within {startupTimeout}.", error);
        }
    }

    public async Task RequestStopAsync(CancellationToken cancellationToken)
    {
        var rpc = await session.GetRpcClientAsync(cancellationToken).ConfigureAwait(false);
        await rpc.InvokeAsync("requestStop", [], cancellationToken).ConfigureAwait(false);
    }

    public async Task NotifyAppHostReadyAsync(CancellationToken cancellationToken)
    {
        var rpc = await session.GetRpcClientAsync(cancellationToken).ConfigureAwait(false);
        await rpc.InvokeAsync("notifyCliReady", [], cancellationToken).ConfigureAwait(false);
    }

    public Task<DashboardUrlsState> GetDashboardUrlsAsync(CancellationToken cancellationToken)
        => Task.FromResult(new DashboardUrlsState { DashboardHealthy = false });

    public async IAsyncEnumerable<BackchannelLogEntry> GetAppHostLogEntriesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var generation = Guid.NewGuid();
        // OutputCollector is a bounded ring. This adapter emits retained startup
        // output once, then only the newly retained suffix on each observation.
        var previous = Array.Empty<(Utils.OutputLineStream Stream, string Line)>();
        long sequence = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var lines = session.Output?.GetLines().ToArray() ?? [];
            var overlap = Math.Min(previous.Length, lines.Length);
            while (overlap > 0 && !previous.AsSpan(previous.Length - overlap).SequenceEqual(lines.AsSpan(0, overlap)))
            {
                overlap--;
            }

            foreach (var (_, line) in lines.Skip(overlap))
            {
                yield return new BackchannelLogEntry
                {
                    SequenceNumber = sequence++, GenerationId = generation, EventId = default,
                    LogLevel = LogLevel.Information, Message = line,
                    Timestamp = DateTimeOffset.UtcNow, CategoryName = "NativeAppHost"
                };
            }

            previous = lines;
            if (session.HasServerExited == true)
            {
                yield break;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    public async IAsyncEnumerable<RpcResourceState> GetResourceStatesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var rpc = await session.GetRpcClientAsync(cancellationToken).ConfigureAwait(false);
        while (session.HasServerExited != true)
        {
            var snapshot = await rpc.InvokeAsync<JsonElement>("getRuntimeState", [], cancellationToken).ConfigureAwait(false);
            foreach (var resource in snapshot.GetProperty("resources").EnumerateArray())
            {
                yield return new RpcResourceState
                {
                    Resource = resource.GetProperty("name").GetString()!,
                    Type = resource.GetProperty("kind").GetString()!,
                    State = resource.GetProperty("state").GetString()!,
                    Endpoints = resource.GetProperty("urls").EnumerateArray().Select(url => url.GetString()!).ToArray()
                };
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task WaitForDisconnectAsync(CancellationToken cancellationToken) => session.WaitForExitAsync().WaitAsync(cancellationToken);
    public Task<string[]> GetCapabilitiesAsync(CancellationToken cancellationToken) => Task.FromResult<string[]>(["native.headless.v0"]);
    public IAsyncEnumerable<PublishingActivity> GetPublishingActivitiesAsync(CancellationToken cancellationToken) => throw Unsupported();
    public Task CompletePromptResponseAsync(string promptId, PublishingPromptInputAnswer[] answers, CancellationToken cancellationToken) => throw Unsupported();
    public Task UpdatePromptResponseAsync(string promptId, PublishingPromptInputAnswer[] answers, CancellationToken cancellationToken) => throw Unsupported();
    public Task<GetPipelineStepsResponse> GetPipelineStepsAsync(string? step, CancellationToken cancellationToken) => throw Unsupported();
    public Task<UploadFileResponse> UploadFileAsync(string filePath, string fileName, int interactionId, string inputName, CancellationToken cancellationToken) => throw Unsupported();
    private static NotSupportedException Unsupported() => new("The native exploration does not implement publishing, prompts, or file uploads.");
}
