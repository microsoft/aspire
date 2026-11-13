// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Azure.Monitor.OpenTelemetry.Exporter;

namespace Aspire.Cli.Tests.Telemetry;

/// <summary>
/// Sets the Azure Monitor exporter's own test switches. Call only inside a RemoteExecutor process because they are static.
/// </summary>
/// <remarks>
/// These are private static fields on <c>TransmitFromStorageHandler</c> that the exporter itself provides for tests that
/// cannot tolerate a background drain. Setting them changes only which storage drain uploads persisted telemetry, not
/// how the CLI persists or exports it, so the tests still exercise the real exporter. Verified against
/// Azure.Monitor.OpenTelemetry.Exporter 1.9.0 (AzureMonitorOpenTelemetryExporterVersion in eng/Versions.props).
/// A missing or retyped field fails loudly, but if a later version keeps a field and stops reading it, the
/// drain race these switches avoid can return silently. Recheck the switches when updating the exporter.
/// </remarks>
internal static class AzureMonitorExporterTestHooks
{
    private const string TransmitFromStorageHandlerTypeName = "Azure.Monitor.OpenTelemetry.Exporter.Internals.TransmitFromStorageHandler";

    /// <summary>
    /// Prevents the storage drain that each new transmitter starts 50 ms after creation.
    /// </summary>
    /// <remarks>
    /// Only one storage drain runs at a time; a drain requested while another is running returns without uploading.
    /// If the eager drain snapshots storage before a flush or shutdown persists telemetry, the drain started by that
    /// flush or shutdown is skipped and the persisted telemetry waits for the exporter's two-minute storage timer.
    /// Tests that wait for the flush or shutdown drain to upload must make it the only drain.
    /// </remarks>
    public static void DisableEagerStorageDrain() => SetHandlerSwitch("DisableEagerDrainForTesting");

    /// <summary>
    /// Prevents flush and shutdown from starting a storage drain, so persisted telemetry stays on disk.
    /// </summary>
    public static void DisableFlushAndShutdownStorageDrain() => SetHandlerSwitch("DisableShutdownDrainForTesting");

    private static void SetHandlerSwitch(string fieldName)
    {
        var handlerType = typeof(AzureMonitorExporterOptions).Assembly.GetType(TransmitFromStorageHandlerTypeName, throwOnError: true)!;
        var field = handlerType.GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(field);
        Assert.Equal(typeof(bool), field.FieldType);
        field.SetValue(null, true);
    }
}
