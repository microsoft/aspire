// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Diagnostics;
using Microsoft.Extensions.Hosting;

#pragma warning disable ASPIRETERMINAL001 // Internal consumer of the experimental AppHost terminal API.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Stops AppHost-owned terminals during host shutdown rather than waiting for service-provider disposal.
/// </summary>
internal sealed class TerminalServiceHost(TerminalService terminalService) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return ShutdownDiagnostics.TraceTask("TerminalServiceHost.StopAsync", cancellationToken, () =>
        {
            ShutdownDiagnostics.Mark("TerminalServiceHost.DisposeAsync.enter", cancellationToken);
            var disposeTask = terminalService.DisposeAsync().AsTask();
            ShutdownDiagnostics.Mark($"TerminalServiceHost.DisposeAsync.returned-task={disposeTask.Status}", cancellationToken);
            // Cancellation bounds the host's wait, not teardown. Host disposal still joins the same cleanup task.
            return ShutdownDiagnostics.TraceTask("TerminalServiceHost.WaitAsync", cancellationToken,
                () => disposeTask.WaitAsync(cancellationToken),
                () => $"dispose-task={disposeTask.Status}");
        });
    }
}
