// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents.Hooks;
using Aspire.Cli.Bundles;
using Aspire.Cli.Layout;
using Aspire.Cli.Processes;
using Aspire.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Telemetry;

/// <summary>
/// Keeps the existing exporter alive until its durable backlog has been delivered.
/// </summary>
internal static class AgentTelemetryUploader
{
    internal static string LockPath => Path.Combine(Path.GetDirectoryName(TelemetryManager.GetTelemetryStoragePath())!, "agent-telemetry.lock");

    internal static bool HasPendingTelemetry(string storagePath)
        => Directory.Exists(storagePath) && Directory.EnumerateFiles(storagePath, "*", SearchOption.AllDirectories).Any();

    internal static async Task EnsureRunningAsync(IServiceProvider services)
    {
        if (!HasPendingTelemetry(TelemetryManager.GetTelemetryStoragePath()))
        {
            return;
        }

        // Probe without waiting. The child takes the same lock; concurrent launches are harmless.
        using (var probe = FileLock.TryAcquire(LockPath))
        {
            if (probe is null)
            {
                return;
            }
        }

        var (command, args) = AgentTelemetryHook.GetCommand("--drain");
        var startInfo = new IsolatedProcessStartInfo
        {
            FileName = command,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Detached = true,
            IsolateConsole = false
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        // A drainer must neither attach to an IDE session nor export profiling data.
        foreach (var key in startInfo.Environment.Keys.Where(key => key.StartsWith("ASPIRE_EXTENSION_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            startInfo.Environment.Remove(key);
        }

        // Windows launches the self-contained CLI without needing any bundle component. Unix uses
        // the existing DCP detach helper; retain its layout while handing the lease to the child.
        using var dcp = OperatingSystem.IsWindows() ? null : await DcpExecutableResolver.TryGetDcpExecutableAsync(
            services.GetRequiredService<ILayoutDiscovery>(), services.GetRequiredService<IBundleService>(),
            services.GetRequiredService<CliExecutionContext>(), "agent-telemetry", CancellationToken.None).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows())
        {
            startInfo.DetachedUnixLauncherPath = dcp?.ExecutablePath
                ?? throw new InvalidOperationException("Could not resolve DCP for the telemetry uploader.");
        }
        if (dcp?.LayoutLease is { } layoutLease)
        {
            var childEnvironment = new Dictionary<string, string>();
            layoutLease.AddEnvironment(childEnvironment);
            foreach (var (key, value) in childEnvironment)
            {
                startInfo.Environment[key] = value;
            }
        }

        // IsolatedProcess disposal releases launch handles, not the independent process.
        await using var process = await IsolatedProcess.StartAsync(startInfo, CancellationToken.None).ConfigureAwait(false);
    }

    internal static async Task DrainAsync(string storagePath, string lockPath, CancellationToken cancellationToken)
    {
        do
        {
            using (var lease = FileLock.TryAcquire(lockPath))
            {
                if (lease is null)
                {
                    return;
                }
                while (HasPendingTelemetry(storagePath))
                {
                    // The exporter owns batching, retries, lease recovery and retention. We only keep
                    // its process alive; no private storage formats or retry algorithms are duplicated.
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                }
            }
            // Release before rechecking: a producer racing idle shutdown either starts a successor
            // or leaves work we see here. It cannot strand an event behind a departing worker's lock.
        }
        while (HasPendingTelemetry(storagePath));
    }
}
