// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting;

namespace Aspire.Cli.Processes;

/// <summary>
/// Stamps a launching process's identity (PID plus start time, as whole Unix seconds) into a child
/// process's environment so the child's parent-liveness watchdog / orphan detector can verify the
/// parent by PID <em>and</em> start time and therefore survive PID reuse. Centralizes the env-var
/// writing that would otherwise be duplicated at every process-launch site.
/// </summary>
internal static class OrphanDetectionEnvironment
{
    /// <summary>
    /// Stamps the current CLI process's identity under the given key names, defaulting to the CLI
    /// orphan-detection keys (<see cref="KnownConfigNames.CliProcessId"/> /
    /// <see cref="KnownConfigNames.CliProcessStarted"/>).
    /// </summary>
    /// <param name="environment">The child environment to stamp.</param>
    /// <param name="pidKey">The variable name to write the parent PID under.</param>
    /// <param name="startedKey">The variable name to write the parent start time under.</param>
    /// <param name="overwrite">
    /// When <see langword="true"/> (the default) existing values are replaced. When
    /// <see langword="false"/> a value the caller already set is preserved, so an explicit override
    /// always wins.
    /// </param>
    public static void ApplyCurrentProcess(
        IDictionary<string, string> environment,
        string pidKey = KnownConfigNames.CliProcessId,
        string startedKey = KnownConfigNames.CliProcessStarted,
        bool overwrite = true)
    {
        // Widening a non-null-valued dictionary to the nullable-valued signature is safe: Apply only
        // ever writes non-null values, so the caller's non-null contract is never violated.
        Apply((IDictionary<string, string?>)environment, Environment.ProcessId, ProcessStartTimeHelper.GetCurrentProcessStartTimeUnixSeconds(), pidKey, startedKey, overwrite);
    }

    /// <summary>
    /// Stamps a specific process's identity, using an already-resolved <paramref name="startTimeUnixSeconds"/>.
    /// Accepting the start time (rather than resolving it) lets a caller write the same identity under
    /// several key pairs while only reading the start time once. The nullable value type matches
    /// <see cref="System.Diagnostics.ProcessStartInfo.Environment"/> so it can be stamped directly.
    /// </summary>
    /// <param name="environment">The child environment to stamp.</param>
    /// <param name="pid">The parent process id.</param>
    /// <param name="startTimeUnixSeconds">
    /// The parent's start time in whole Unix seconds, or <see langword="null"/> when it could not be
    /// read. When <see langword="null"/> only the PID is written; the watchdog then falls back to a
    /// PID-only existence check.
    /// </param>
    /// <param name="pidKey">The variable name to write the parent PID under.</param>
    /// <param name="startedKey">The variable name to write the parent start time under.</param>
    /// <param name="overwrite">
    /// When <see langword="true"/> (the default) existing values are replaced; when
    /// <see langword="false"/> caller-supplied values are preserved.
    /// </param>
    public static void Apply(
        IDictionary<string, string?> environment,
        int pid,
        long? startTimeUnixSeconds,
        string pidKey,
        string startedKey,
        bool overwrite = true)
    {
        if (overwrite || !environment.ContainsKey(pidKey))
        {
            environment[pidKey] = pid.ToString(CultureInfo.InvariantCulture);
        }

        // The start time can be unavailable (target already exited, privileged, etc.); only stamp it
        // when it is known so a stale/empty value never masquerades as a verified identity.
        if (startTimeUnixSeconds is { } started && (overwrite || !environment.ContainsKey(startedKey)))
        {
            environment[startedKey] = started.ToString(CultureInfo.InvariantCulture);
        }
    }
}
