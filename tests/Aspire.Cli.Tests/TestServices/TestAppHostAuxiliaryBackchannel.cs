// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Text.Json;
using Aspire.Cli.Backchannel;
using ModelContextProtocol.Protocol;

namespace Aspire.Cli.Tests.TestServices;

/// <summary>
/// A test implementation of IAppHostAuxiliaryBackchannel for unit testing.
/// </summary>
internal sealed class TestAppHostAuxiliaryBackchannel : IAppHostAuxiliaryBackchannel
{
    public string Hash { get; set; } = "test-hash";
    public string SocketPath { get; set; } = "/tmp/test.sock";
    public AppHostInformation? AppHostInfo { get; set; }
    public bool IsInScope { get; set; } = true;
    public DateTimeOffset ConnectedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool SupportsV2 { get; set; } = true;

    /// <summary>
    /// Gets or sets the resource snapshots to return from GetResourceSnapshotsAsync and WatchResourceSnapshotsAsync.
    /// </summary>
    public List<ResourceSnapshot> ResourceSnapshots { get; set; } = [];

    /// <summary>
    /// Gets or sets the dashboard URLs state to return from GetDashboardUrlsAsync.
    /// </summary>
    public DashboardUrlsState? DashboardUrlsState { get; set; }

    /// <summary>
    /// Gets or sets the log lines to return from GetResourceLogsAsync.
    /// </summary>
    public List<ResourceLogLine> LogLines { get; set; } = [];

    /// <summary>
    /// Gets or sets the result to return from StopAppHostAsync.
    /// </summary>
    public bool StopAppHostResult { get; set; } = true;

    /// <summary>
    /// Gets or sets the function to call when CallResourceMcpToolAsync is invoked.
    /// </summary>
    public Func<string, string, IReadOnlyDictionary<string, JsonElement>?, CancellationToken, Task<CallToolResult>>? CallResourceMcpToolHandler { get; set; }

    /// <summary>
    /// Gets or sets the function to call when GetResourceSnapshotsAsync is invoked.
    /// If null, returns the ResourceSnapshots list.
    /// </summary>
    public Func<CancellationToken, Task<List<ResourceSnapshot>>>? GetResourceSnapshotsHandler { get; set; }

    /// <summary>
    /// Gets or sets the function to call when WatchResourceSnapshotsAsync is invoked.
    /// If null, yields the ResourceSnapshots list.
    /// </summary>
    public Func<bool, CancellationToken, IAsyncEnumerable<ResourceSnapshot>>? WatchResourceSnapshotsHandler { get; set; }

    public Task<DashboardUrlsState?> GetDashboardUrlsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(DashboardUrlsState);
    }

    public Task<List<ResourceSnapshot>> GetResourceSnapshotsAsync(bool includeHidden, CancellationToken cancellationToken = default)
    {
        if (GetResourceSnapshotsHandler is not null)
        {
            return GetResourceSnapshotsHandler(cancellationToken);
        }

        var snapshots = includeHidden
            ? ResourceSnapshots
            : ResourceSnapshots.Where(s => !ResourceSnapshotMapper.IsHiddenResource(s)).ToList();
        return Task.FromResult(snapshots);
    }

    public async IAsyncEnumerable<ResourceSnapshot> WatchResourceSnapshotsAsync(bool includeHidden, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (WatchResourceSnapshotsHandler is not null)
        {
            await foreach (var snapshot in WatchResourceSnapshotsHandler(includeHidden, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                yield return snapshot;
            }
            yield break;
        }

        foreach (var snapshot in ResourceSnapshots)
        {
            if (!includeHidden && ResourceSnapshotMapper.IsHiddenResource(snapshot))
            {
                continue;
            }

            yield return snapshot;
        }
        await Task.CompletedTask;
    }

    public async IAsyncEnumerable<ResourceLogLine> GetResourceLogsAsync(
        string? resourceName = null,
        bool follow = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var lines = resourceName is null
            ? LogLines
            : LogLines.Where(l => string.Equals(l.ResourceName, resourceName, StringComparison.OrdinalIgnoreCase)
                                || l.ResourceName.StartsWith(resourceName + "-", StringComparison.OrdinalIgnoreCase));

        foreach (var line in lines)
        {
            yield return line;
        }
        await Task.CompletedTask;
    }

    public Task<bool> StopAppHostAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(StopAppHostResult);
    }

    /// <summary>
    /// Gets or sets the result to return from ExecuteResourceCommandAsync.
    /// </summary>
    public ExecuteResourceCommandResponse ExecuteResourceCommandResult { get; set; } = new ExecuteResourceCommandResponse { Success = true };

    public Task<ExecuteResourceCommandResponse> ExecuteResourceCommandAsync(
        string resourceName,
        string commandName,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(ExecuteResourceCommandResult);
    }

    /// <summary>
    /// Gets or sets the result to return from WaitForResourceAsync.
    /// </summary>
    public WaitForResourceResponse WaitForResourceResult { get; set; } = new WaitForResourceResponse { Success = true, State = "Running" };

    public Task<WaitForResourceResponse> WaitForResourceAsync(
        string resourceName,
        string status,
        int timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(WaitForResourceResult);
    }

    public Task<CallToolResult> CallResourceMcpToolAsync(
        string resourceName,
        string toolName,
        IReadOnlyDictionary<string, JsonElement>? arguments,
        CancellationToken cancellationToken = default)
    {
        if (CallResourceMcpToolHandler is not null)
        {
            return CallResourceMcpToolHandler(resourceName, toolName, arguments, cancellationToken);
        }

        return Task.FromResult(new CallToolResult
        {
            Content = [new TextContentBlock { Text = $"Mock result for {resourceName}/{toolName}" }]
        });
    }

    /// <summary>
    /// Gets or sets the dashboard info response to return from GetDashboardInfoV2Async.
    /// </summary>
    public GetDashboardInfoResponse? DashboardInfoResponse { get; set; }

    public Task<GetDashboardInfoResponse?> GetDashboardInfoV2Async(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(DashboardInfoResponse);
    }

    /// <summary>
    /// Gets or sets the terminal info response to return from GetTerminalInfoAsync.
    /// </summary>
    public GetTerminalInfoResponse TerminalInfoResponse { get; set; } = new GetTerminalInfoResponse { IsAvailable = false };

    public Task<GetTerminalInfoResponse> GetTerminalInfoAsync(string resourceName, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(TerminalInfoResponse);
    }

    public void Dispose()
    {
        // Nothing to dispose in the test implementation
    }
}
