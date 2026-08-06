// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Backchannel;

namespace Aspire.Cli.Processes;

/// <summary>
/// Abstraction over "gracefully stop this AppHost process tree (with force-kill fallback)".
/// Implemented by <see cref="ProcessTreeGracefulShutdownService"/>; exists as an interface so the
/// orphan-collection path can be unit tested without real process signalling or DCP plumbing.
/// </summary>
internal interface IAppHostStopper
{
    Task<bool> StopAppHostAsync(
        AppHostInformation? appHostInfo,
        Func<CancellationToken, Task<bool>>? requestRpcStopAsync,
        CancellationToken cancellationToken);
}
