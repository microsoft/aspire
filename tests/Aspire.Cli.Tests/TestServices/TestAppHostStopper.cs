// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Backchannel;
using Aspire.Cli.Processes;

namespace Aspire.Cli.Tests.TestServices;

/// <summary>
/// A test <see cref="IAppHostStopper"/> that records stop requests and returns a controllable result,
/// so <see cref="OrphanedAppHostCollector"/> orchestration can be tested without real process signalling.
/// </summary>
internal sealed class TestAppHostStopper : IAppHostStopper
{
    /// <summary>
    /// The AppHosts that <see cref="StopAppHostAsync"/> was asked to stop, in call order.
    /// </summary>
    public List<AppHostInformation?> StopRequests { get; } = [];

    /// <summary>
    /// Result to return for a given AppHost. When null, <see cref="DefaultResult"/> is used.
    /// </summary>
    public Func<AppHostInformation?, bool>? StopResultSelector { get; set; }

    /// <summary>
    /// When it returns a non-null exception for a given AppHost, that exception is thrown instead of returning a result.
    /// </summary>
    public Func<AppHostInformation?, Exception?>? ThrowSelector { get; set; }

    /// <summary>
    /// Result used when <see cref="StopResultSelector"/> is null or returns null-equivalent.
    /// </summary>
    public bool DefaultResult { get; set; } = true;

    public Task<bool> StopAppHostAsync(
        AppHostInformation? appHostInfo,
        Func<CancellationToken, Task<bool>>? requestRpcStopAsync,
        CancellationToken cancellationToken)
    {
        StopRequests.Add(appHostInfo);

        if (ThrowSelector?.Invoke(appHostInfo) is { } ex)
        {
            throw ex;
        }

        var result = StopResultSelector?.Invoke(appHostInfo) ?? DefaultResult;
        return Task.FromResult(result);
    }
}
