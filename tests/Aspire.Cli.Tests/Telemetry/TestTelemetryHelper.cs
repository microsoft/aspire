// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Telemetry;

/// <summary>
/// Provides test helpers for creating telemetry instances.
/// </summary>
internal static class TestTelemetryHelper
{
    /// <summary>
    /// Creates and initializes an <see cref="AspireCliTelemetry"/> instance for testing.
    /// </summary>
    public static AspireCliTelemetry CreateInitializedTelemetry()
    {
        var provider = new TestMachineInformationProvider();
        var ciDetector = new TestCIEnvironmentDetector();
        var telemetry = new AspireCliTelemetry(NullLogger<AspireCliTelemetry>.Instance, provider, ciDetector);
        telemetry.InitializeAsync().GetAwaiter().GetResult();
        return telemetry;
    }

    /// <summary>
    /// Creates and initializes an <see cref="AspireCliTelemetry"/> instance for testing with custom activity source names.
    /// </summary>
    public static AspireCliTelemetry CreateInitializedTelemetry(string reportedSourceName, string diagnosticsSourceName)
    {
        var provider = new TestMachineInformationProvider();
        var ciDetector = new TestCIEnvironmentDetector();
        var telemetry = new AspireCliTelemetry(NullLogger<AspireCliTelemetry>.Instance, provider, ciDetector, reportedSourceName, diagnosticsSourceName);
        telemetry.InitializeAsync().GetAwaiter().GetResult();
        return telemetry;
    }

    private sealed class TestMachineInformationProvider : IMachineInformationProvider
    {
        public Task<string?> GetOrCreateDeviceId() => Task.FromResult<string?>("test-device-id");
        public Task<string> GetMacAddressHash() => Task.FromResult("test-mac-hash");
    }

    private sealed class TestCIEnvironmentDetector : ICIEnvironmentDetector
    {
        public bool IsCIEnvironment() => false;
    }
}
