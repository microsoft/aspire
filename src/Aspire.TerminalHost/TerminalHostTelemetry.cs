// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Aspire.TerminalHost;

/// <summary>
/// Shared <see cref="System.Diagnostics.ActivitySource"/> and <see cref="System.Diagnostics.Metrics.Meter"/>
/// for the Aspire terminal host. Telemetry is exported via OTLP to the Aspire dashboard so failures
/// like "DCP never dialed in" or "control socket bound but no clients" are diagnosable without
/// resorting to attaching a debugger.
/// </summary>
/// <remarks>
/// <para>
/// The OTLP exporter wiring in <see cref="TerminalHostApp.RunAsync(string[], System.Threading.CancellationToken)"/>
/// only attaches when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set in the environment. The Aspire
/// AppHost injects that via <c>OtlpConfigurationExtensions.AddOtlpEnvironment</c> on each
/// <c>TerminalHostResource</c>, so production runs always have it. Standalone debug runs of the
/// host (<c>dotnet run --project src/Aspire.TerminalHost</c>) drop telemetry silently.
/// </para>
/// <para>
/// Source / meter names follow the assembly name convention
/// (<see href="https://learn.microsoft.com/dotnet/core/diagnostics/observability-with-otel#naming-conventions"/>)
/// so dashboard categorisation matches every other Aspire component.
/// </para>
/// </remarks>
internal static class TerminalHostTelemetry
{
    public const string SourceName = "Aspire.TerminalHost";

    /// <summary>
    /// Activity source for terminal host lifecycle spans (process boot, replica build, DCP-dial wait,
    /// consumer client accept, control-listener accept, shutdown).
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(SourceName);

    /// <summary>
    /// Meter for terminal host counters and gauges. Disposed when the host shuts down so the OTLP
    /// metric exporter can flush a final reading.
    /// </summary>
    public static readonly Meter Meter = new(SourceName);

    /// <summary>
    /// Incremented when the upstream producer connection drops and the replica recycles its
    /// <c>DcpUpstreamAdapter</c>. Diagnoses DCP restart / crash loops — a steadily growing value
    /// on a single host process means DCP keeps reconnecting, which usually means DCP itself
    /// is being restarted by its supervisor.
    /// </summary>
    public static readonly Counter<long> UpstreamRecycles = Meter.CreateCounter<long>(
        name: "aspire.terminalhost.upstream.recycles",
        unit: "{recycle}",
        description: "Number of times the upstream (DCP) connection dropped and was re-listened for.");

    /// <summary>
    /// Incremented every time a downstream viewer (dashboard tab, CLI <c>aspire terminal attach</c>)
    /// successfully accepts on the consumer UDS. Diagnoses "I attached but see nothing" by letting
    /// you confirm the accept actually happened on the host side.
    /// </summary>
    public static readonly Counter<long> ConsumerConnections = Meter.CreateCounter<long>(
        name: "aspire.terminalhost.consumer.connections",
        unit: "{connection}",
        description: "Number of downstream consumer (dashboard/CLI viewer) connections accepted.");

    /// <summary>
    /// Incremented on viewer disconnect. Pairs with <see cref="ConsumerConnections"/> so a
    /// monotonically-growing delta between the two on the dashboard exposes leaked / orphaned
    /// peer sessions.
    /// </summary>
    public static readonly Counter<long> ConsumerDisconnections = Meter.CreateCounter<long>(
        name: "aspire.terminalhost.consumer.disconnections",
        unit: "{connection}",
        description: "Number of downstream consumer (dashboard/CLI viewer) disconnections observed.");

    /// <summary>
    /// Current attached-peer count. Up/down counter so the dashboard can chart "is the terminal
    /// idle right now?" without subtracting two monotonic counters and dealing with restarts.
    /// </summary>
    public static readonly UpDownCounter<long> ConsumerPeersActive = Meter.CreateUpDownCounter<long>(
        name: "aspire.terminalhost.consumer.peers.active",
        unit: "{peer}",
        description: "Current number of downstream consumer (dashboard/CLI viewer) peers attached.");

    /// <summary>
    /// Resize events. Tagged with <c>direction</c> = <c>downstream</c> (consumer-side primary
    /// peer changed dims) or <c>upstream</c> (host wrote a <c>FrameResize</c> to DCP), and for
    /// <c>upstream</c> also a <c>result</c> = <c>ok</c> | <c>failed</c> tag.
    /// </summary>
    public static readonly Counter<long> ResizeRequests = Meter.CreateCounter<long>(
        name: "aspire.terminalhost.resize.requests",
        unit: "{resize}",
        description: "Number of resize events observed/forwarded.");

    /// <summary>
    /// Bytes transferred. Tagged with <c>socket</c> = <c>producer</c> | <c>consumer</c> |
    /// <c>control</c> and <c>direction</c> = <c>in</c> (host received) | <c>out</c> (host sent).
    /// Counts wire bytes — HMP1 frame headers are included because they're real socket bytes
    /// (5B per frame), so a "bytes out" growing faster than "payload bytes" is itself a signal.
    /// </summary>
    public static readonly Counter<long> Bytes = Meter.CreateCounter<long>(
        name: "aspire.terminalhost.bytes",
        unit: "By",
        description: "Bytes transferred over the per-replica UDS sockets.");
}
