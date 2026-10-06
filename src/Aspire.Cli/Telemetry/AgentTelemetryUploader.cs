// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Agents.Hooks;
using Aspire.Shared;

namespace Aspire.Cli.Telemetry;

/// <summary>
/// Keeps the existing exporter alive until its durable backlog has been delivered.
/// </summary>
internal static class AgentTelemetryUploader
{
    private const string UploaderName = "agent-telemetry";
    private const string LockFileName = UploaderName + ".lock";

    internal static string LockPath => Path.Combine(Path.GetDirectoryName(TelemetryManager.GetTelemetryStoragePath())!, LockFileName);

    internal static bool HasPendingTelemetry(string storagePath)
        => Directory.Exists(storagePath) && Directory.EnumerateFiles(storagePath, "*", SearchOption.AllDirectories).Any();

    internal static void EnsureRunning()
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

        var (command, args) = AgentTelemetryHook.GetCommand(AgentTelemetryProtocol.DrainOptionName);
        var startInfo = new ProcessStartInfo(command, args)
        {
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };

        if (OperatingSystem.IsWindows())
        {
            // Share the hook's console instead of StartDetached (DETACHED_PROCESS), matching the
            // other detached CLI children. Inherit no handles so the hook host (which may be
            // waiting for its pipes to close) is not held open by the drainer.
            startInfo.InheritedHandles = [];
        }
        else
        {
            // setsid() so the drainer outlives the hook's session and terminal.
            startInfo.StartDetached = true;
        }

        // A drainer must neither attach to an IDE session nor export profiling data.
        foreach (var key in startInfo.Environment.Keys.Where(key => key.StartsWith("ASPIRE_EXTENSION_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            startInfo.Environment.Remove(key);
        }

        // StartAndForget connects stdio to the null device and releases the process handle, so the
        // drainer is fully independent of this short-lived hook invocation.
        Process.StartAndForget(startInfo);
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
