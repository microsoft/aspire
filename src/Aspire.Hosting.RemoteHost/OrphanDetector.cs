// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.RemoteHost;

internal sealed class OrphanDetector : BackgroundService
{
    private const string HostProcessId = "REMOTE_APP_HOST_PID";
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<OrphanDetector> _logger;

    public OrphanDetector(IHostApplicationLifetime lifetime, ILogger<OrphanDetector> logger)
    {
        _lifetime = lifetime;
        _logger = logger;
    }

    internal Func<int, bool> IsProcessRunning { get; set; } = (int pid) =>
    {
        try
        {
            return !Process.GetProcessById(pid).HasExited;
        }
        catch (ArgumentException)
        {
            // If Process.GetProcessById throws it means the process is not running.
            return false;
        }
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (Environment.GetEnvironmentVariable(HostProcessId) is not { } pidString || !int.TryParse(pidString, out var pid))
            {
                // If there is no PID environment variable, we assume that the process is not a child process
                // of the Aspire CLI and we won't continue monitoring.
                _logger.LogDebug("No parent PID specified, orphan detection disabled");
                return;
            }

            _logger.LogDebug("Monitoring parent process PID: {ParentPid}", pid);

            using var periodic = new PeriodicTimer(TimeSpan.FromSeconds(1), TimeProvider.System);

            do
            {
                if (!IsProcessRunning(pid))
                {
                    _logger.LogWarning("Parent process {ParentPid} is no longer running, shutting down...", pid);
                    _lifetime.StopApplication();
                    return;
                }
            } while (await periodic.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // This is expected when the app is shutting down.
            _logger.LogDebug("OrphanDetector: Stopped");
        }
    }
}
