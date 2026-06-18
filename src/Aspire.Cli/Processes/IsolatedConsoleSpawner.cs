// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Cli.Processes;

/// <summary>
/// Shared helper for the isolated-console spawn path: translates a fully-populated
/// <see cref="ProcessStartInfo"/> into the <see cref="IsolatedProcessStartInfo"/> shape
/// <see cref="IsolatedProcess.Start"/> expects, then spawns the child. Centralizes the
/// translation so every caller (AppHost server, guest apphost, future spawners) sees the
/// same env/arg shape and the same fail-fast contract on Windows.
/// </summary>
internal static class IsolatedConsoleSpawner
{
    /// <summary>
    /// Spawns the process described by <paramref name="startInfo"/> into an isolated console
    /// group (new hidden console on Windows; effectively a thin <see cref="Process.Start(ProcessStartInfo)"/>
    /// wrapper on Unix), bound on Windows to the process-wide kill-on-close job.
    /// </summary>
    /// <remarks>
    /// On Windows the spawned child is assigned to <see cref="WindowsConsoleProcessJob.Shared"/>
    /// (created on first use). Without the kill-on-close job an isolated child could survive a
    /// CLI crash as an orphan in its new console group, defeating the entire point of the safety
    /// net the new-console isolation is supposed to enable — so the job is resolved here rather
    /// than threaded in by callers, who cannot then forget to supply it.
    /// </remarks>
    public static IsolatedProcess StartIsolated(
        ProcessStartInfo startInfo,
        Action<int, string> standardOutputHandler,
        Action<int, string> standardErrorHandler)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(standardOutputHandler);
        ArgumentNullException.ThrowIfNull(standardErrorHandler);

        var isolatedStartInfo = new IsolatedProcessStartInfo
        {
            FileName = startInfo.FileName,
            WorkingDirectory = startInfo.WorkingDirectory,
            JobHandle = OperatingSystem.IsWindows() ? WindowsConsoleProcessJob.Shared.Handle : null,
        };

        foreach (var arg in startInfo.ArgumentList)
        {
            isolatedStartInfo.ArgumentList.Add(arg);
        }

        // Replace (not overlay) the env block so callers that did startInfo.Environment.Remove(key)
        // see that removal honored — e.g. PrebuiltAppHostServer.CreateStartInfo explicitly removes
        // KnownConfigNames.IntegrationLibsPath / IntegrationProbeManifestPath when they aren't
        // configured, to suppress any value the parent CLI happens to have set in its own env.
        // ProcessStartInfo.Environment is eagerly snapshotted from the parent, so iterating its
        // Keys gives us the authoritative "what the child should see" view; a missing key here
        // really does mean "do not pass this to the child" — including for vars the parent inherits.
        // Overlay-without-clear (a prior approach) silently re-inherited any removed key on the
        // isolated path, which the Unix partial also avoids via Environment.Clear() + add.
        isolatedStartInfo.Environment.Clear();
        foreach (var (key, value) in startInfo.Environment)
        {
            // Match ProcessStartInfo.Environment semantics: a null value means "do not set this
            // variable in the child" — we get there by simply not adding it.
            if (value is not null)
            {
                isolatedStartInfo.Environment[key] = value;
            }
        }

        return IsolatedProcess.Start(
            isolatedStartInfo,
            (sender, line) => standardOutputHandler(sender.Id, line),
            (sender, line) => standardErrorHandler(sender.Id, line));
    }
}
