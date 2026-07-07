// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Processes;
using Aspire.Hosting;

namespace Aspire.Cli.Tests.Processes;

public class OrphanDetectionEnvironmentTests
{
    [Fact]
    public void ApplyCurrentProcess_StampsPidAndStartTime()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);

        OrphanDetectionEnvironment.ApplyCurrentProcess(environment);

        Assert.Equal(Environment.ProcessId.ToString(CultureInfo.InvariantCulture), environment[KnownConfigNames.CliProcessId]);
        Assert.NotNull(ProcessStartTimeHelper.TryParseStartTimeUnixSeconds(environment[KnownConfigNames.CliProcessStarted]));
        Assert.NotNull(ProcessStartTimeHelper.TryParseStartTimeUnixSeconds(environment[KnownConfigNames.CliProcessStartedStable]));
    }

    [Fact]
    public void ApplyCurrentProcess_UsesSuppliedKeyNames()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);

        OrphanDetectionEnvironment.ApplyCurrentProcess(environment, KnownConfigNames.CliLauncherProcessId, KnownConfigNames.CliLauncherProcessStarted);

        Assert.Equal(Environment.ProcessId.ToString(CultureInfo.InvariantCulture), environment[KnownConfigNames.CliLauncherProcessId]);
        Assert.NotNull(ProcessStartTimeHelper.TryParseStartTimeUnixSeconds(environment[KnownConfigNames.CliLauncherProcessStarted]));
        Assert.False(environment.ContainsKey(KnownConfigNames.CliProcessId));
        Assert.False(environment.ContainsKey(KnownConfigNames.CliProcessStartedStable));
    }

    [Fact]
    public void ApplyCurrentProcess_WithoutOverwrite_PreservesCallerValues()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [KnownConfigNames.CliProcessId] = "999",
        };

        OrphanDetectionEnvironment.ApplyCurrentProcess(environment, overwrite: false);

        Assert.Equal("999", environment[KnownConfigNames.CliProcessId]);
        // The caller-supplied PID is authoritative, so no start time is stamped: doing so would pair
        // PID 999 with the current process's start time. Leaving the keys unset keeps the identity
        // consistent and lets the watchdog fall back to a PID-only existence check.
        Assert.False(environment.ContainsKey(KnownConfigNames.CliProcessStarted));
        Assert.False(environment.ContainsKey(KnownConfigNames.CliProcessStartedStable));
    }

    [Fact]
    public void ApplyCurrentProcess_WithoutOverwrite_StampsFullIdentityWhenNoPidPresent()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);

        OrphanDetectionEnvironment.ApplyCurrentProcess(environment, overwrite: false);

        // With no caller-supplied PID (the common LayoutProcessRunner path), the current process's
        // identity is stamped in full and the PID and start-time values describe the same process.
        Assert.Equal(Environment.ProcessId.ToString(CultureInfo.InvariantCulture), environment[KnownConfigNames.CliProcessId]);
        Assert.NotNull(ProcessStartTimeHelper.TryParseStartTimeUnixSeconds(environment[KnownConfigNames.CliProcessStarted]));
        Assert.NotNull(ProcessStartTimeHelper.TryParseStartTimeUnixSeconds(environment[KnownConfigNames.CliProcessStartedStable]));
    }

    [Fact]
    public void ApplyCurrentProcess_WithOverwrite_ReplacesCallerValues()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [KnownConfigNames.CliProcessId] = "999",
        };

        OrphanDetectionEnvironment.ApplyCurrentProcess(environment, overwrite: true);

        Assert.Equal(Environment.ProcessId.ToString(CultureInfo.InvariantCulture), environment[KnownConfigNames.CliProcessId]);
    }

    [Fact]
    public void Apply_WithStartTime_StampsBothKeys()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);

        OrphanDetectionEnvironment.Apply(environment, pid: 4321, startTimeUnixSeconds: 1000, KnownConfigNames.RemoteAppHostProcessId, KnownConfigNames.RemoteAppHostProcessStarted);

        Assert.Equal("4321", environment[KnownConfigNames.RemoteAppHostProcessId]);
        Assert.Equal("1000", environment[KnownConfigNames.RemoteAppHostProcessStarted]);
    }

    [Fact]
    public void Apply_WithNullStartTime_StampsPidOnly()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);

        OrphanDetectionEnvironment.Apply(environment, pid: 4321, startTimeUnixSeconds: null, KnownConfigNames.CliProcessId, KnownConfigNames.CliProcessStarted);

        Assert.Equal("4321", environment[KnownConfigNames.CliProcessId]);
        Assert.False(environment.ContainsKey(KnownConfigNames.CliProcessStarted));
        Assert.False(environment.ContainsKey(KnownConfigNames.CliProcessStartedStable));
    }

    [Fact]
    public void Apply_WithoutOverwrite_PreservesCallerValues()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [KnownConfigNames.CliProcessId] = "999",
            [KnownConfigNames.CliProcessStarted] = "111",
        };

        OrphanDetectionEnvironment.Apply(environment, pid: 4321, startTimeUnixSeconds: 1000, KnownConfigNames.CliProcessId, KnownConfigNames.CliProcessStarted, overwrite: false);

        Assert.Equal("999", environment[KnownConfigNames.CliProcessId]);
        Assert.Equal("111", environment[KnownConfigNames.CliProcessStarted]);
        // The existing PID (999) is authoritative, so the stable start time for the passed pid (4321)
        // is not stamped — doing so would pair PID 999 with a different process's start time.
        Assert.False(environment.ContainsKey(KnownConfigNames.CliProcessStartedStable));
    }
}
