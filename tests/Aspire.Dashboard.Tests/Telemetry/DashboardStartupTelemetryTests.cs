// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Telemetry;
using Aspire.Shared;
using Xunit;

namespace Aspire.Dashboard.Tests.Telemetry;

public class DashboardStartupTelemetryTests
{
    [Theory]
    [InlineData(true, null, DashboardPersistenceMode.None, "none")]
    [InlineData(true, null, DashboardPersistenceMode.Run, "run")]
    [InlineData(true, null, DashboardPersistenceMode.Resume, "resume")]
    [InlineData(false, KnownDashboardStartupFailureReasons.AddressInUse, DashboardPersistenceMode.None, "none")]
    [InlineData(false, KnownDashboardStartupFailureReasons.AddressInUse, DashboardPersistenceMode.Run, "run")]
    [InlineData(false, KnownDashboardStartupFailureReasons.AddressInUse, DashboardPersistenceMode.Resume, "resume")]
    [InlineData(false, KnownDashboardStartupFailureReasons.ConfigurationValidation, (DashboardPersistenceMode)999, "unknown")]
    [InlineData(false, KnownDashboardStartupFailureReasons.ConfigurationValidation, (DashboardPersistenceMode)(-1), "unknown")]
    public void Record_RecordsStartupResultOnce(bool success, string? errorType, DashboardPersistenceMode persistenceMode, string expectedPersistenceMode)
    {
        using var fixture = new DashboardTelemetryFixture();
        var options = new DashboardOptions
        {
            LaunchContext = "AppHost",
            Data = new DashboardDataOptions { PersistenceMode = persistenceMode }
        };
        var startupTimestamp = Stopwatch.GetTimestamp() - Stopwatch.Frequency;
        var startupTelemetry = new DashboardStartupTelemetry(fixture.Telemetry, options, startupTimestamp);

        if (success)
        {
            startupTelemetry.RecordSuccess();
            startupTelemetry.RecordFailure("ignored");
        }
        else
        {
            startupTelemetry.RecordFailure(errorType!);
            startupTelemetry.RecordSuccess();
        }

        Assert.True(fixture.LogChannel.Reader.TryRead(out var log));
        Assert.Equal(TelemetryEventKeys.Startup, log.Message);
        var attributes = log.Attributes.ToDictionary(p => p.Key, p => p.Value);
        var duration = Assert.IsType<double>(attributes[TelemetryPropertyKeys.StartupDurationMilliseconds]);
        Assert.True(duration >= 900);
        List<KeyValuePair<string, object?>> expectedEventProperties =
        [
            new("microsoft.operation_name", TelemetryEventKeys.Startup),
            new("{OriginalFormat}", TelemetryEventKeys.Startup),
            new(TelemetryPropertyKeys.StartupSuccess, success),
            new(TelemetryPropertyKeys.StartupLaunchContext, KnownDashboardLaunchContexts.AppHost),
            new(TelemetryPropertyKeys.StartupPersistenceMode, expectedPersistenceMode),
            new(TelemetryPropertyKeys.StartupDurationMilliseconds, duration)
        ];
        if (errorType is not null)
        {
            expectedEventProperties.Add(new(TelemetryPropertyKeys.ErrorType, errorType));
        }
        fixture.AssertAttributes(log.Attributes, [.. expectedEventProperties]);
        Assert.False(fixture.LogChannel.Reader.TryPeek(out _));
    }

    [Theory]
    [InlineData(DashboardPersistenceMode.None)]
    [InlineData(DashboardPersistenceMode.Run)]
    [InlineData(DashboardPersistenceMode.Resume)]
    public void Record_RecordsUnknownPersistenceModeWhenParsingFails(DashboardPersistenceMode persistenceMode)
    {
        using var fixture = new DashboardTelemetryFixture();
        var options = new DashboardOptions
        {
            Data = new DashboardDataOptions
            {
                PersistenceMode = persistenceMode,
                PersistenceModeParseError = "Invalid persistence mode."
            }
        };
        var startupTelemetry = new DashboardStartupTelemetry(fixture.Telemetry, options, Stopwatch.GetTimestamp());

        startupTelemetry.RecordFailure(KnownDashboardStartupFailureReasons.ConfigurationValidation);

        Assert.True(fixture.LogChannel.Reader.TryRead(out var log));
        var attributes = log.Attributes.ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal("unknown", attributes[TelemetryPropertyKeys.StartupPersistenceMode]);
        Assert.Equal(false, attributes[TelemetryPropertyKeys.StartupSuccess]);
        Assert.Equal(KnownDashboardStartupFailureReasons.ConfigurationValidation, attributes[TelemetryPropertyKeys.ErrorType]);
        Assert.False(fixture.LogChannel.Reader.TryPeek(out _));
    }

    [Theory]
    [InlineData("AppHost", KnownDashboardLaunchContexts.AppHost)]
    [InlineData(" CLI ", KnownDashboardLaunchContexts.Cli)]
    [InlineData("Container", KnownDashboardLaunchContexts.Container)]
    [InlineData("unknown", KnownDashboardLaunchContexts.Unknown)]
    [InlineData("custom-context", KnownDashboardLaunchContexts.Unknown)]
    [InlineData("", KnownDashboardLaunchContexts.Unknown)]
    [InlineData(" ", KnownDashboardLaunchContexts.Unknown)]
    [InlineData(null, KnownDashboardLaunchContexts.Unknown)]
    public void Record_NormalizesLaunchContext(string? launchContext, string expected)
    {
        using var fixture = new DashboardTelemetryFixture();
        var options = new DashboardOptions { LaunchContext = launchContext };
        var startupTelemetry = new DashboardStartupTelemetry(fixture.Telemetry, options, Stopwatch.GetTimestamp());

        startupTelemetry.RecordSuccess();

        Assert.True(fixture.LogChannel.Reader.TryRead(out var log));
        var attributes = log.Attributes.ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal(expected, attributes[TelemetryPropertyKeys.StartupLaunchContext]);
        Assert.Equal("none", attributes[TelemetryPropertyKeys.StartupPersistenceMode]);
    }

    [Theory]
    [InlineData(true, true, "ci")]
    [InlineData(true, false, "local")]
    [InlineData(false, true, "ci")]
    [InlineData(false, false, "local")]
    public void Record_RecordsDeploymentEnvironment(bool success, bool isCIEnvironment, string expectedEnvironment)
    {
        using var fixture = new DashboardTelemetryFixture(isCIEnvironment: isCIEnvironment);
        var startupTelemetry = new DashboardStartupTelemetry(
            fixture.Telemetry, new DashboardOptions(), Stopwatch.GetTimestamp());

        if (success)
        {
            startupTelemetry.RecordSuccess();
        }
        else
        {
            startupTelemetry.RecordFailure(KnownDashboardStartupFailureReasons.ConfigurationValidation);
        }

        Assert.True(fixture.LogChannel.Reader.TryRead(out var log));
        var attributes = log.Attributes.ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal(expectedEnvironment, Assert.IsType<string>(attributes["deployment.environment.name"]));
        Assert.Equal(success, attributes[TelemetryPropertyKeys.StartupSuccess]);
        Assert.False(fixture.LogChannel.Reader.TryPeek(out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Record_RecordsOperatingSystem(bool success)
    {
        using var fixture = new DashboardTelemetryFixture();
        var startupTelemetry = new DashboardStartupTelemetry(
            fixture.Telemetry, new DashboardOptions(), Stopwatch.GetTimestamp());

        if (success)
        {
            startupTelemetry.RecordSuccess();
        }
        else
        {
            startupTelemetry.RecordFailure(KnownDashboardStartupFailureReasons.ConfigurationValidation);
        }

        var expectedOsName = OperatingSystem.IsWindows() ? "Windows"
            : OperatingSystem.IsLinux() ? "Linux"
            : OperatingSystem.IsMacOS() ? "macOS"
            : RuntimeInformation.OSDescription;
        var expectedOsType = OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsLinux() ? "linux"
            : OperatingSystem.IsMacOS() ? "darwin"
            : "unknown";
        Assert.True(fixture.LogChannel.Reader.TryRead(out var log));
        Assert.Collection(log.Attributes.Where(p => p.Key.StartsWith("os.", StringComparison.Ordinal)).OrderBy(p => p.Key, StringComparer.Ordinal),
            attribute => Assert.Equal(new KeyValuePair<string, object?>("os.name", expectedOsName), attribute),
            attribute => Assert.Equal(new KeyValuePair<string, object?>("os.type", expectedOsType), attribute),
            attribute => Assert.Equal(new KeyValuePair<string, object?>("os.version", Environment.OSVersion.Version.ToString()), attribute));
        Assert.False(fixture.LogChannel.Reader.TryPeek(out _));
    }
}
