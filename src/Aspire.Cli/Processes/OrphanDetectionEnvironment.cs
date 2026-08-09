// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting;

namespace Aspire.Cli.Processes;

/// <summary>
/// Stamps a launching process's identity (PID plus a stable start-time value) into a child process's
/// environment so the child's parent-liveness watchdog / orphan detector can verify the parent by PID
/// <em>and</em> start time and therefore survive PID reuse. Centralizes the env-var writing that would
/// otherwise be duplicated at every process-launch site.
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
    /// When <see langword="true"/> (the default) existing values are replaced. 
    /// When <see langword="false"/> a value the caller already set is preserved.
    /// </param>
    public static void ApplyCurrentProcess(
        IDictionary<string, string> environment,
        string pidKey = KnownConfigNames.CliProcessId,
        string startedKey = KnownConfigNames.CliProcessStarted,
        bool overwrite = true)
    {
        // Widening a non-null-valued dictionary to the nullable-valued signature is safe: Apply only
        // ever writes non-null values, so the caller's non-null contract is never violated.
        Apply((IDictionary<string, string?>)environment, Environment.ProcessId, ProcessStartTimeHelper.GetCurrentProcessStartTimeUnixMilliseconds(), pidKey, startedKey, overwrite);
    }

    /// <summary>
    /// Stamps a specific process's identity, using an already-resolved <paramref name="stableStartTimeUnixMilliseconds"/>.
    /// Accepting the start time (rather than resolving it) lets a caller write the same identity under
    /// several key pairs while only reading the start time once. The nullable value type matches
    /// <see cref="System.Diagnostics.ProcessStartInfo.Environment"/> so it can be stamped directly.
    /// </summary>
    /// <param name="environment">The child environment to stamp.</param>
    /// <param name="pid">The parent process id.</param>
    /// <param name="stableStartTimeUnixMilliseconds">
    /// The parent's stable start time in Unix milliseconds, or <see langword="null"/> when it could
    /// not be read. When <see langword="null"/> only the PID is written; the watchdog then falls back
    /// to a PID-only existence check.
    /// </param>
    /// <param name="pidKey">The variable name to write the parent PID under.</param>
    /// <param name="startedKey">The variable name to write the parent start time under.</param>
    /// <param name="overwrite">
    /// When <see langword="true"/> (the default) existing values are replaced. 
    /// When <see langword="false"/> caller-supplied values are preserved.
    /// </param>
    public static void Apply(
        IDictionary<string, string?> environment,
        int pid,
        long? stableStartTimeUnixMilliseconds,
        string pidKey,
        string startedKey,
        bool overwrite = true)
    {
        var pidWritten = overwrite || !environment.ContainsKey(pidKey);
        if (pidWritten)
        {
            environment[pidKey] = pid.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            // Env var already exists and we are not allowed to overwrite it.
            return;
        }

        var isCliParentIdentity = pidKey == KnownConfigNames.CliProcessId && startedKey == KnownConfigNames.CliProcessStarted;

        // For the CLI parent identity, ASPIRE_CLI_STARTED is the value AppHosts <= Aspire version 13.4 
        // verify with their Process.StartTime-based check, so it MUST stay in whole Unix seconds. 
        // Every other identity's primary key carries the stable millisecond value directly. 
        // We need to use correct units here.
        long? startedValue = null;
        if (stableStartTimeUnixMilliseconds is { } stableStartedValue)
        {
            if (isCliParentIdentity)
            {
                startedValue = ProcessStartTimeHelper.TryGetRuntimeProcessStartTimeUnixSeconds(pid);
            }
            else
            {
                startedValue = stableStartedValue;
            }
        }

        // The start time can be unavailable (target already exited, privileged, etc.). When replacing
        // the PID, remove any inherited start time that cannot be replaced so the child does not verify
        // a mismatched PID/start-time identity.
        if (startedValue is { } started)
        {
            if (overwrite || !environment.ContainsKey(startedKey))
            {
                environment[startedKey] = started.ToString(CultureInfo.InvariantCulture);
            }
        }
        else if (overwrite)
        {
            environment.Remove(startedKey);
        }

        if (isCliParentIdentity)
        {
            if (stableStartTimeUnixMilliseconds is { } stableStarted)
            {
                if (overwrite || !environment.ContainsKey(KnownConfigNames.CliProcessStartedStable))
                {
                    // ASPIRE_CLI_STARTED_STABLE is the millisecond-precision companion current AppHosts prefer:
                    // it survives wall-clock steps and gives exact PID-reuse detection.
                    // AppHosts <= Aspire ver 13.4 ignore it and fall back to the seconds-based ASPIRE_CLI_STARTED above.
                    environment[KnownConfigNames.CliProcessStartedStable] = stableStarted.ToString(CultureInfo.InvariantCulture);
                }
            }
            else if (overwrite)
            {
                environment.Remove(KnownConfigNames.CliProcessStartedStable);
            }
        }
    }
}
