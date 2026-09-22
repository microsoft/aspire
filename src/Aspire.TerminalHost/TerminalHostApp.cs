// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.Tracing;
using Aspire.Hosting;
using Aspire.Shared.TerminalHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry;
using OpenTelemetry.Resources;

namespace Aspire.TerminalHost;

/// <summary>
/// In-process entry point for the Aspire terminal host. Owns the single
/// per-replica relay terminal, the control listener, and the lifecycle/shutdown
/// handshake.
///
/// Each <c>aspire.terminalhost</c> process serves exactly one replica. Replica
/// fan-out happens at the AppHost level: a target resource with N replicas
/// causes N independent terminal host processes to be spawned, each with its
/// own producer/consumer/control UDS triple. The host has no notion of its
/// global replica index — that's encoded in the UDS paths and is opaque here.
///
/// Exposed as a class so tests can drive the host without spawning a process.
/// </summary>
public sealed class TerminalHostApp : IAsyncDisposable
{
    private readonly TerminalHostArgs _args;
    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly object _gate = new();
    private TerminalReplica? _replica;
    private TerminalHostControlListener? _controlListener;
    private bool _disposed;

    internal TerminalHostApp(TerminalHostArgs args, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _args = args;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<TerminalHostApp>();
    }

    /// <summary>
    /// Snapshot of the host's single replica session, suitable for marshalling
    /// to the AppHost via the control protocol.
    /// </summary>
    internal TerminalHostSessionInfo SnapshotSession()
    {
        TerminalReplica? replica;
        lock (_gate)
        {
            replica = _replica;
        }

        if (replica is null)
        {
            // Pre-start (replica not yet created). Report a placeholder consistent with
            // "no producer connected" so callers can still read configured paths.
            return new TerminalHostSessionInfo
            {
                ProducerUdsPath = _args.ProducerUdsPath,
                ConsumerUdsPath = _args.ConsumerUdsPath,
                IsAlive = false,
                ExitCode = null,
                ProducerConnected = false,
                RestartCount = 0,
                CurrentColumns = _args.Columns,
                CurrentRows = _args.Rows,
                AttachedPeerCount = 0,
                Peers = Array.Empty<TerminalHostPeerInfo>(),
            };
        }

        return new TerminalHostSessionInfo
        {
            ProducerUdsPath = replica.ProducerUdsPath,
            ConsumerUdsPath = replica.ConsumerUdsPath,
            IsAlive = replica.IsAlive,
            ExitCode = replica.ExitCode,
            ProducerConnected = replica.ProducerConnected,
            RestartCount = replica.RestartCount,
            CurrentColumns = replica.CurrentColumns,
            CurrentRows = replica.CurrentRows,
            AttachedPeerCount = replica.AttachedPeerCount,
            Peers = replica.SnapshotPeers(),
        };
    }

    /// <summary>
    /// Starts the replica relay and the control listener, then waits for either
    /// the external cancellation token or a shutdown request to fire. Returns the
    /// process exit code.
    /// </summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Aspire terminal host starting: size={Cols}x{Rows}, producer='{Producer}', consumer='{Consumer}'.",
            _args.Columns, _args.Rows, _args.ProducerUdsPath, _args.ConsumerUdsPath);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _shutdownCts.Token);
        var token = linkedCts.Token;

        try
        {
            // Start the replica first, then the control listener; that way as soon as
            // the AppHost can connect to control, the consumer UDS is bound.
            var replica = TerminalReplica.Start(
                _args.ProducerUdsPath,
                _args.ConsumerUdsPath,
                _args.Columns,
                _args.Rows,
                _loggerFactory,
                token);
            lock (_gate)
            {
                _replica = replica;
            }

            _controlListener = new TerminalHostControlListener(
                _args.ControlUdsPath,
                new TerminalHostControlRpcTarget(this),
                _loggerFactory.CreateLogger<TerminalHostControlListener>());
            await _controlListener.StartAsync().ConfigureAwait(false);

            _logger.LogInformation("Terminal host ready.");

            // Observe the replica's recycle loop alongside cancellation. If the loop
            // exits unexpectedly (e.g. permission error binding the consumer UDS,
            // EADDRINUSE on a stale .sock, parent dir missing) the host would
            // otherwise stay "ready" with no producer ever serving traffic; the
            // AppHost could only detect the stall via getSession.RestartCount.
            // Surface the failure as a non-zero exit so DCP can either restart the
            // host or propagate the failure to the user.
            var replicaRun = replica.RunTask;
            await WaitForShutdownAsync(replicaRun, token).ConfigureAwait(false);

            if (replicaRun.IsCompleted && !token.IsCancellationRequested)
            {
                if (replicaRun.IsFaulted)
                {
                    var fault = replicaRun.Exception?.GetBaseException();
                    _logger.LogError(fault, "Replica recycle loop terminated with a fault; exiting non-zero.");
                    return 1;
                }

                // RanToCompletion without cancellation means the recycle loop hit a
                // permanent condition it considered fatal but didn't throw. Treat as
                // an abnormal exit so DCP doesn't quietly keep a wedged process.
                _logger.LogError("Replica recycle loop completed unexpectedly without cancellation; exiting non-zero.");
                return 1;
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Terminal host failed.");
            return 1;
        }
        finally
        {
            await TearDownAsync().ConfigureAwait(false);
        }
    }

    private static async Task WaitForShutdownAsync(Task replicaRunTask, CancellationToken cancellationToken)
    {
        // Wait for external cancellation, an explicit shutdown request via the control
        // protocol, OR the replica recycle loop completing (clean or faulted). The
        // recycle loop is expected to run for the lifetime of the host - early
        // completion almost always signals a fatal misconfiguration (bad UDS path,
        // permission denied, etc.) and the caller surfaces that as a non-zero exit
        // code so DCP can react rather than letting the process wedge silently.
        var cancellation = Task.Delay(Timeout.Infinite, cancellationToken);
        try
        {
            await Task.WhenAny(cancellation, replicaRunTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Signals a graceful shutdown. Returns immediately;
    /// <see cref="RunAsync(CancellationToken)"/> will exit shortly after.
    /// </summary>
    public void RequestShutdown()
    {
        if (!_shutdownCts.IsCancellationRequested)
        {
            _logger.LogInformation("Shutdown requested.");
            _shutdownCts.Cancel();
        }
    }

    private async Task TearDownAsync()
    {
        if (_controlListener is not null)
        {
            await _controlListener.DisposeAsync().ConfigureAwait(false);
            _controlListener = null;
        }

        TerminalReplica? toDispose;
        lock (_gate)
        {
            toDispose = _replica;
            _replica = null;
        }
        if (toDispose is not null)
        {
            try
            {
                await toDispose.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error while disposing replica.");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        RequestShutdown();
        await TearDownAsync().ConfigureAwait(false);
        _shutdownCts.Dispose();
    }

    /// <summary>
    /// Convenience entry point used by both <c>Program.Main</c> and tests.
    /// Catches argument-parsing errors and writes a friendly message to stderr.
    /// </summary>
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        TerminalHostArgs parsed;
        try
        {
            parsed = TerminalHostArgs.Parse(args);
        }
        catch (TerminalHostArgsException ex)
        {
            await Console.Error.WriteLineAsync($"[Aspire.TerminalHost] {ex.Message}")
                .ConfigureAwait(false);
            return 64; // EX_USAGE
        }

        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var hostBuilder = CreateTelemetryHostBuilder(configuration);

        ILoggerFactory loggerFactory;
        IHost? host = null;
        OtelSelfDiagnosticsListener? selfDiag = null;
        if (hostBuilder is not null)
        {
            var otlpEndpoint = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
            var otlpProtocol = configuration["OTEL_EXPORTER_OTLP_PROTOCOL"];
            var otlpHeaders = configuration["OTEL_EXPORTER_OTLP_HEADERS"];
            var serviceName = configuration["OTEL_SERVICE_NAME"];
            var resourceAttrs = configuration["OTEL_RESOURCE_ATTRIBUTES"];

            // Log only the header length: its value contains the dashboard OTLP API key.
            await Console.Error.WriteLineAsync(
                $"[Aspire.TerminalHost] startup pid={Environment.ProcessId} otel=on endpoint='{otlpEndpoint}' protocol='{otlpProtocol}' headers.len={otlpHeaders?.Length ?? 0} service='{serviceName}' resource='{resourceAttrs}'")
                .ConfigureAwait(false);

            // Surface OTLP exporter failures (cert trust, connection refused, 401, schema
            // mismatches) to stderr. Without this, every Warning/Error from
            // OpenTelemetry-Exporter-OpenTelemetryProtocol (and the SDK proper) is swallowed
            // and the dashboard just stays empty — the symptom that brought us here originally.
            // Listener is disposed in the finally below.
            selfDiag = new OtelSelfDiagnosticsListener();

            // Starting the host eagerly initializes the tracer and meter providers through
            // OpenTelemetry's hosted service, rather than only activating the logging pipeline.
            host = hostBuilder.Build();
            await host.StartAsync(cancellationToken).ConfigureAwait(false);

            loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();

            // Emit one log immediately so the dashboard's structured-logs tab for this resource
            // has at least one record before DCP dials in or a viewer attaches. If this doesn't
            // show up but the startup stderr line above does, look at the otel-diag lines for
            // the exporter-side failure.
            //
            // The metric ping uses Add(1) rather than Add(0) because the OTel SDK suppresses
            // zero-delta cumulative export aggregations — a counter that's never moved off zero
            // never produces a data point, so the dashboard's metrics tab stays empty until the
            // first real upstream recycle. We pre-bump it once with a startup-tagged dimension
            // so the metric is visible from the moment the host attaches, and a real recycle
            // still increments on top.
            var initLogger = loggerFactory.CreateLogger("Aspire.TerminalHost.Startup");
            initLogger.LogInformation(
                "Terminal host telemetry initialized. pid={Pid} endpoint={Endpoint} protocol={Protocol} service={Service}",
                Environment.ProcessId, otlpEndpoint, otlpProtocol, serviceName);
            TerminalHostTelemetry.UpstreamRecycles.Add(1, new KeyValuePair<string, object?>("phase", "startup"));
        }
        else
        {
            loggerFactory = NullLoggerFactory.Instance;
        }

        try
        {
            await using var app = new TerminalHostApp(parsed, loggerFactory);
            return await app.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Stopping the IHost runs IHostedService.StopAsync for all registered services
            // (including TelemetryHostedService, which disposes the meter/tracer providers in
            // the right order and forces a final flush of pending OTLP batches). Disposing the
            // host then tears down the underlying ServiceProvider — also flushing the logger
            // factory. The self-diag listener is dropped last so any errors emitted during the
            // flush still surface.
            if (host is not null)
            {
                try
                {
                    using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await host.StopAsync(stopCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Best effort: the flush window expired. Continue with Dispose, which is
                    // synchronous and best-effort by design — the dashboard may miss the last
                    // few seconds of telemetry but the host is on its way out anyway.
                }
                host.Dispose();
            }
            selfDiag?.Dispose();
        }
    }

    internal static HostApplicationBuilder? CreateTelemetryHostBuilder(IConfiguration configuration)
    {
        // An inherited OTLP endpoint must not turn a hidden implementation detail into
        // a telemetry resource. The AppHost explicitly sets this flag from ShowTerminalHost.
        // Malformed values (e.g. "not-a-bool") must leave telemetry disabled, not stop the terminal.
        if (!configuration.GetBool(KnownConfigNames.TerminalHostTelemetryEnabled, defaultValue: false) ||
            string.IsNullOrEmpty(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            return null;
        }

        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Configuration.AddConfiguration(configuration);
        hostBuilder.Logging.SetMinimumLevel(ParseLogLevel(configuration["ASPIRE_TERMINAL_HOST_LOG_LEVEL"]));
        hostBuilder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        // Preserve DCP's service name and instance ID so telemetry matches the helper
        // resource instead of creating an unrelated resource in the dashboard.
        var serviceName = configuration["OTEL_SERVICE_NAME"];
        hostBuilder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(
                string.IsNullOrEmpty(serviceName) ? TerminalHostTelemetry.SourceName : serviceName,
                autoGenerateServiceInstanceId: false))
            .WithTracing(t => t.AddSource(TerminalHostTelemetry.SourceName))
            .WithMetrics(m => m.AddMeter(TerminalHostTelemetry.SourceName))
            .UseOtlpExporter();

        return hostBuilder;
    }

    // Parse a friendly LogLevel string from the env var. Case-insensitive; bad/empty values
    // fall back to Information. We accept the standard Microsoft.Extensions.Logging.LogLevel
    // names (Trace, Debug, Information, Warning, Error, Critical, None).
    private static LogLevel ParseLogLevel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return LogLevel.Information;
        }

        return Enum.TryParse<LogLevel>(value, ignoreCase: true, out var parsed)
            ? parsed
            : LogLevel.Information;
    }
}

/// <summary>
/// Bridges OpenTelemetry SDK and OTLP exporter internal EventSources to stderr so wire-level
/// failures (cert trust, connection refused, HTTP non-success status, schema mismatches) surface
/// in the dashboard's resource log tab instead of silently dropping every signal.
/// </summary>
/// <remarks>
/// Subscribes to the well-known SDK event sources by name:
/// <list type="bullet">
///   <item><c>OpenTelemetry-Sdk</c> — batch processor failures, resource detector errors.</item>
///   <item><c>OpenTelemetry-Exporter-OpenTelemetryProtocol</c> — the OTLP exporter itself; this
///   is where "request failed" / "deadline exceeded" / "HTTP 401" / "channel error" events live.</item>
///   <item><c>OpenTelemetry-Instrumentation-*</c> — instrumentation-side warnings.</item>
/// </list>
/// We log every event at Warning or above; Informational is too noisy (resource detection, etc.).
/// Format: <c>[Aspire.TerminalHost.otel-diag] {EventSource}/{EventName} {payload}</c> — single-line,
/// no stack traces, because each diag line lands in the consumer-facing log tab.
/// </remarks>
internal sealed class OtelSelfDiagnosticsListener : EventListener
{
    private static readonly string[] s_knownSources =
    {
        "OpenTelemetry-Sdk",
        "OpenTelemetry-Exporter-OpenTelemetryProtocol",
    };

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        // EventSources can appear before our constructor runs (the base ctor enumerates them) and
        // also lazily as the OTel SDK loads exporter assemblies, so we check both ways.
        if (eventSource.Name is { } name &&
            (Array.IndexOf(s_knownSources, name) >= 0 || name.StartsWith("OpenTelemetry-", StringComparison.Ordinal)))
        {
            EnableEvents(eventSource, EventLevel.Warning, EventKeywords.All);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        // Render payload as key=value pairs. Avoid `string.Join` allocations for the empty case
        // and skip null payloads (some events have no args) to keep the line compact.
        string payload = string.Empty;
        if (eventData.Payload is { Count: > 0 } payloadList && eventData.PayloadNames is { Count: > 0 } payloadNames)
        {
            var pairs = new List<string>(payloadList.Count);
            for (var i = 0; i < payloadList.Count; i++)
            {
                var name = i < payloadNames.Count ? payloadNames[i] : $"arg{i}";
                pairs.Add($"{name}={payloadList[i]}");
            }
            payload = string.Join(" ", pairs);
        }

        // Console.Error.WriteLine is intentional: this listener is invoked synchronously from the
        // OTel SDK's logging path and we don't want to re-enter the logger factory we're trying
        // to diagnose. DCP captures stderr into the resource log stream.
        Console.Error.WriteLine($"[Aspire.TerminalHost.otel-diag] {eventData.EventSource.Name}/{eventData.EventName} level={eventData.Level} {payload}");
    }
}
