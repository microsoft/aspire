// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Aspire.Hosting;
using Aspire.Shared;
using Aspire.Shared.Telemetry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Telemetry;

/// <summary>
/// Provides a single ActivitySource for all Aspire CLI components.
/// </summary>
internal sealed class AspireCliTelemetry : AspireTelemetryBase, IHostedService
{
    private static readonly TimeSpan s_internalMicrosoftDiagnosticsCompletionTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The name of the ActivitySource for report telemetry. This telemetry is exported to external systems.
    /// </summary>
    public const string ReportedActivitySourceName = "Aspire.Cli.Reported";

    /// <summary>
    /// The name of the ActivitySource for diagnostics telemetry. This telemetry is used for internal diagnostics only.
    /// </summary>
    public const string DiagnosticsActivitySourceName = "Aspire.Cli.Diagnostics";

    /// <summary>
    /// Environment variable to opt out of telemetry. Set to "1" or "true" to disable.
    /// </summary>
    internal const string TelemetryOptOutConfigKey = "ASPIRE_CLI_TELEMETRY_OPTOUT";

    /// <summary>
    /// Environment variable for OpenTelemetry Protocol exporter endpoint.
    /// </summary>
    internal const string OtlpExporterEndpointConfigKey = KnownOtelConfigNames.ExporterOtlpEndpoint;

    /// <summary>
    /// Environment variable to specify the console exporter level for debugging.
    /// Set to "Reported" to export reported telemetry, or "Diagnostic" to export diagnostic telemetry.
    /// </summary>
    internal const string ConsoleExporterLevelConfigKey = "ASPIRE_CLI_CONSOLE_EXPORTER_LEVEL";

    private readonly IMachineInformationProvider _machineInformationProvider;
    private readonly ICIEnvironmentDetector _ciEnvironmentDetector;
    private readonly ICodingAgentDetector _codingAgentDetector;
    private readonly IInternalMicrosoftDetector _internalMicrosoftDetector;
    private readonly TelemetryConfiguration _telemetryConfiguration;
    private readonly ILogger<AspireCliTelemetry> _logger;
    private readonly CliExecutionContext _executionContext;
    private readonly TelemetryTagsSource _tagsSource;
    private Task _internalMicrosoftDiagnosticsTask = Task.CompletedTask;
    private Task? _internalMicrosoftDiagnosticsCompletionTask;

    private bool _isInitialized;

    /// <summary>
    /// Initializes a new instance of the <see cref="AspireCliTelemetry"/> class.
    /// </summary>
    /// <param name="logger">The logger instance for recording errors.</param>
    /// <param name="machineInformationProvider">The machine information provider.</param>
    /// <param name="ciEnvironmentDetector">The CI environment detector.</param>
    /// <param name="codingAgentDetector">The coding agent detector.</param>
    /// <param name="internalMicrosoftDetector">The internal Microsoft detector.</param>
    /// <param name="telemetryConfiguration">The telemetry configuration.</param>
    /// <param name="executionContext">
    /// The CLI execution context carrying the effective identity. Required: the DI
    /// container injects the registered singleton, so identity telemetry tags are
    /// always emitted from it.
    /// </param>
    /// <param name="tagsSource">The shared source for background-calculated telemetry tags.</param>
    public AspireCliTelemetry(ILogger<AspireCliTelemetry> logger, IMachineInformationProvider machineInformationProvider, ICIEnvironmentDetector ciEnvironmentDetector, ICodingAgentDetector codingAgentDetector, IInternalMicrosoftDetector internalMicrosoftDetector, TelemetryConfiguration telemetryConfiguration, CliExecutionContext executionContext, TelemetryTagsSource tagsSource)
        : this(logger, machineInformationProvider, ciEnvironmentDetector, codingAgentDetector, internalMicrosoftDetector, telemetryConfiguration, ReportedActivitySourceName, DiagnosticsActivitySourceName, executionContext, tagsSource)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AspireCliTelemetry"/> class with custom activity source names.
    /// This constructor is intended for testing purposes only to enable thread-safe test isolation.
    /// </summary>
    /// <param name="logger">The logger instance for recording errors.</param>
    /// <param name="machineInformationProvider">The machine information provider.</param>
    /// <param name="ciEnvironmentDetector">The CI environment detector.</param>
    /// <param name="codingAgentDetector">The coding agent detector.</param>
    /// <param name="internalMicrosoftDetector">The internal Microsoft detector.</param>
    /// <param name="reportedSourceName">The name for the reported activity source.</param>
    /// <param name="diagnosticsSourceName">The name for the diagnostics activity source.</param>
    /// <param name="executionContext">The CLI execution context carrying the effective identity.</param>
    /// <param name="tagsSource">The shared source for background-calculated telemetry tags.</param>
    internal AspireCliTelemetry(ILogger<AspireCliTelemetry> logger, IMachineInformationProvider machineInformationProvider, ICIEnvironmentDetector ciEnvironmentDetector, ICodingAgentDetector codingAgentDetector, IInternalMicrosoftDetector internalMicrosoftDetector, string reportedSourceName, string diagnosticsSourceName, CliExecutionContext executionContext, TelemetryTagsSource tagsSource)
        : this(logger, machineInformationProvider, ciEnvironmentDetector, codingAgentDetector, internalMicrosoftDetector, new TelemetryConfiguration { ReportedTelemetryEnabled = true }, reportedSourceName, diagnosticsSourceName, executionContext, tagsSource)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AspireCliTelemetry"/> class with custom telemetry enablement.
    /// </summary>
    /// <param name="logger">The logger instance for recording errors.</param>
    /// <param name="machineInformationProvider">The machine information provider.</param>
    /// <param name="ciEnvironmentDetector">The CI environment detector.</param>
    /// <param name="codingAgentDetector">The coding agent detector.</param>
    /// <param name="internalMicrosoftDetector">The internal Microsoft detector.</param>
    /// <param name="telemetryConfiguration">The telemetry configuration.</param>
    /// <param name="reportedSourceName">The name for the reported activity source.</param>
    /// <param name="diagnosticsSourceName">The name for the diagnostics activity source.</param>
    /// <param name="executionContext">The CLI execution context carrying the effective identity.</param>
    /// <param name="tagsSource">The shared source for background-calculated telemetry tags.</param>
    internal AspireCliTelemetry(ILogger<AspireCliTelemetry> logger, IMachineInformationProvider machineInformationProvider, ICIEnvironmentDetector ciEnvironmentDetector, ICodingAgentDetector codingAgentDetector, IInternalMicrosoftDetector internalMicrosoftDetector, TelemetryConfiguration telemetryConfiguration, string reportedSourceName, string diagnosticsSourceName, CliExecutionContext executionContext, TelemetryTagsSource tagsSource)
        : base(logger, logger, reportedSourceName, diagnosticsSourceName, TelemetryConstants.Events.Error)
    {
        _logger = logger;
        _machineInformationProvider = machineInformationProvider;
        _ciEnvironmentDetector = ciEnvironmentDetector;
        _codingAgentDetector = codingAgentDetector;
        _internalMicrosoftDetector = internalMicrosoftDetector;
        _telemetryConfiguration = telemetryConfiguration;
        _executionContext = executionContext;
        _tagsSource = tagsSource;
    }

    /// <summary>
    /// TESTING PURPOSES ONLY: Gets the default tags used for telemetry.
    /// </summary>
    internal async Task<IReadOnlyList<KeyValuePair<string, object?>>> GetDefaultTagsAsync()
    {
        var tags = await _tagsSource.TagsTask.ConfigureAwait(false);
        await _internalMicrosoftDiagnosticsTask.ConfigureAwait(false);
        return tags;
    }

    protected override IReadOnlyList<KeyValuePair<string, object?>> GetDefaultTags() => _tagsSource.GetResolvedTags();

    protected override bool IsReportedTelemetryEnabled => _telemetryConfiguration.ReportedTelemetryEnabled;

    /// <inheritdoc />
    protected override bool TrySanitizeProperty(string key, object? value, out object? sanitizedValue)
    {
        // CLI properties are supplied by internal instrumentation, which retains its existing
        // tag set and exception details rather than adopting the dashboard's privacy policy.
        sanitizedValue = value;
        return true;
    }

    /// <summary>
    /// Records a CLI product event immediately as a structured log.
    /// </summary>
    /// <param name="eventName">The event name.</param>
    /// <param name="properties">The CLI-specific event properties.</param>
    public void RecordEvent(string eventName, IEnumerable<KeyValuePair<string, object?>>? properties = null) =>
        RecordEventCore(eventName, properties);

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Initialize();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => CompleteInternalMicrosoftDiagnosticsAsync(cancellationToken);

    internal Task CompleteInternalMicrosoftDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        var completionTask = Volatile.Read(ref _internalMicrosoftDiagnosticsCompletionTask);
        if (completionTask is null)
        {
            var newCompletionTask = WaitForInternalMicrosoftDiagnosticsAsync();
            completionTask = Interlocked.CompareExchange(ref _internalMicrosoftDiagnosticsCompletionTask, newCompletionTask, comparand: null) ?? newCompletionTask;
        }

        return cancellationToken.CanBeCanceled
            ? completionTask.WaitAsync(cancellationToken)
            : completionTask;
    }

    private async Task WaitForInternalMicrosoftDiagnosticsAsync()
    {
        try
        {
            await _internalMicrosoftDiagnosticsTask.WaitAsync(s_internalMicrosoftDiagnosticsCompletionTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            // Telemetry must never prevent the CLI from exiting. Detector probes are individually
            // bounded, but this also protects shutdown from unexpected filesystem or provider stalls.
            _logger.LogDebug(ex, "Timed out waiting for internal Microsoft diagnostics to complete.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Activity listeners/processors can throw during emission, after detection has finished.
            // Completion is awaited from shutdown's finally block and must not replace the command's
            // exit code or prevent application/provider shutdown. Caller cancellation still propagates.
            _logger.LogDebug(ex, "Failed to complete internal Microsoft diagnostics.");
        }
    }

    /// <summary>
    /// Starts background tag calculation. Returns immediately; the tags become available
    /// asynchronously through <see cref="TelemetryTagsSource.TagsTask"/>.
    /// </summary>
    internal void Initialize()
    {
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;

        var internalMicrosoftResultSource = new TaskCompletionSource<InternalMicrosoftDetectionResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _tagsSource.StartCalculation(async () =>
        {
            InternalMicrosoftDetectionResult? internalMicrosoftResult = null;
            CancellationTokenSource? internalMicrosoftTimeoutSource = null;
            try
            {
                var tagsList = new List<KeyValuePair<string, object?>>();

                var macAddressHashTask = _machineInformationProvider.GetMacAddressHash();
                var deviceIdTask = _machineInformationProvider.GetOrCreateDeviceId();

                Task<InternalMicrosoftDetectionResult>? internalMicrosoftTask = null;
                if (_telemetryConfiguration.ReportedTelemetryEnabled)
                {
                    // The internal Microsoft check can be slow and can perform multiple async operations in parallel, so only run it if reported
                    // telemetry is enabled. Ordinary commands are not interrupted by app shutdown. The
                    // high-frequency agent hook has its own 10-second process deadline, so it uses a
                    // shorter detector budget to leave time for activity export and process teardown.
                    if (_telemetryConfiguration.InternalMicrosoftDetectionTimeout is { } timeout)
                    {
                        internalMicrosoftTimeoutSource = new(timeout);
                    }
                    internalMicrosoftTask = GetInternalMicrosoftResultAsync(internalMicrosoftTimeoutSource, TimeProvider.System);
                }

                await Task.WhenAll(new Task[] { macAddressHashTask, deviceIdTask }).ConfigureAwait(false);

                if (internalMicrosoftTask is not null)
                {
                    internalMicrosoftResult = await internalMicrosoftTask.ConfigureAwait(false);
                }

                var isCIEnvironment = _ciEnvironmentDetector.IsCIEnvironment();
                tagsList.Add(new(TelemetryConstants.Tags.MacAddressHash, macAddressHashTask.Result));
                tagsList.Add(new(TelemetryConstants.Tags.DeviceId, deviceIdTask.Result));
                if (internalMicrosoftResult is not null)
                {
                    tagsList.Add(new(TelemetryConstants.Tags.InternalMicrosoft, internalMicrosoftResult.IsInternalMicrosoft));

                    if (internalMicrosoftResult.IsInternalMicrosoft && !string.IsNullOrEmpty(internalMicrosoftResult.Source))
                    {
                        tagsList.Add(new(TelemetryConstants.Tags.InternalMicrosoftSource, internalMicrosoftResult.Source));
                    }

                    if (!isCIEnvironment && internalMicrosoftResult.IsInternalMicrosoft && !string.IsNullOrEmpty(internalMicrosoftResult.Alias))
                    {
                        tagsList.Add(new(TelemetryConstants.Tags.InternalMicrosoftAlias, internalMicrosoftResult.Alias));
                    }

                    if (!isCIEnvironment && internalMicrosoftResult.IsInternalMicrosoft && !string.IsNullOrEmpty(internalMicrosoftResult.Domain))
                    {
                        tagsList.Add(new(TelemetryConstants.Tags.InternalMicrosoftDomain, internalMicrosoftResult.Domain));
                    }
                }

                // This is consistent with dashboard version data.
                tagsList.Add(new(TelemetryConstants.Tags.CliVersion, GetCliVersion()));
                tagsList.Add(new(TelemetryConstants.Tags.CliBuildId, GetCliBuildId()));

                // Identity tags describe the build the CLI is *behaving* as (env / sidecar overrides),
                // kept separate from the physical binary's cli.version/cli.build_id above so emulated
                // runs are distinguishable in telemetry. See docs/specs/cli-identity-sidecar.md.
                tagsList.Add(new(TelemetryConstants.Tags.IdentityVersion, _executionContext.IdentityVersion));
                tagsList.Add(new(TelemetryConstants.Tags.IdentityChannel, _executionContext.IdentityChannel));
                if (!string.IsNullOrEmpty(_executionContext.IdentityCommit))
                {
                    tagsList.Add(new(TelemetryConstants.Tags.IdentityCommit, _executionContext.IdentityCommit));
                }

                var codingAgent = _codingAgentDetector.GetCodingAgent();
                if (codingAgent is not null)
                {
                    tagsList.Add(new(TelemetryConstants.Tags.CodingAgent, codingAgent));
                }

                tagsList.Add(new(TelemetryConstants.Tags.DeploymentEnvironmentName, isCIEnvironment ? "ci" : "local"));

                tagsList.Add(new(TelemetryConstants.Tags.OsName, GetOsName()));
                tagsList.Add(new(TelemetryConstants.Tags.OsType, GetOsType()));
                tagsList.Add(new(TelemetryConstants.Tags.OsVersion, Environment.OSVersion.Version.ToString()));

                return (IReadOnlyList<KeyValuePair<string, object?>>)tagsList;
            }
            catch (Exception ex)
            {
                // Don't throw an error if there is a telemetry issue.
                _logger.LogError(ex, "Error occurred initializing telemetry service.");
                return Array.Empty<KeyValuePair<string, object?>>();
            }
            finally
            {
                internalMicrosoftTimeoutSource?.Dispose();
                internalMicrosoftResultSource.TrySetResult(internalMicrosoftResult);
            }
        });

        // Detector diagnostics are reported only after default tag calculation completes. Reported
        // activities are enriched on stop, so emitting from inside the calculation would make the
        // enrichment processor synchronously wait on the task that is currently producing the activity.
        _internalMicrosoftDiagnosticsTask = EmitInternalMicrosoftDetectorDiagnosticsAsync(internalMicrosoftResultSource.Task);
    }

    internal async Task<InternalMicrosoftDetectionResult> GetInternalMicrosoftResultAsync(CancellationTokenSource? timeoutSource, TimeProvider timeProvider)
    {
        var startTimestamp = timeProvider.GetTimestamp();
        var cancellationToken = timeoutSource?.Token ?? CancellationToken.None;

        try
        {
            return await _internalMicrosoftDetector
                .IsInternalMicrosoftMachineAsync(cancellationToken)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutSource?.IsCancellationRequested == true)
        {
            return new InternalMicrosoftDetectionResult(
                IsInternalMicrosoft: false,
                Source: null,
                Alias: null,
                Domain: null,
                Outcome: InternalMicrosoftDetectorOutcome.TimedOut,
                CacheStatus: InternalMicrosoftDetectorCacheStatus.Miss,
                Duration: timeProvider.GetElapsedTime(startTimestamp),
                ProbeDiagnostics: []);
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Internal Microsoft detection failed.");
            }

            return new InternalMicrosoftDetectionResult(
                IsInternalMicrosoft: false,
                Source: null,
                Alias: null,
                Domain: null,
                Outcome: InternalMicrosoftDetectorOutcome.Failed,
                CacheStatus: InternalMicrosoftDetectorCacheStatus.Miss,
                Duration: timeProvider.GetElapsedTime(startTimestamp),
                ProbeDiagnostics: []);
        }
    }

    private async Task EmitInternalMicrosoftDetectorDiagnosticsAsync(Task<InternalMicrosoftDetectionResult?> resultTask)
    {
        if (!_telemetryConfiguration.ReportedTelemetryEnabled ||
            !_telemetryConfiguration.EmitInternalMicrosoftDiagnostics)
        {
            return;
        }

        var result = await resultTask.ConfigureAwait(false);
        await _tagsSource.TagsTask.ConfigureAwait(false);
        if (result is null)
        {
            return;
        }

        using var activity = StartReportedActivity(TelemetryConstants.Activities.InternalMicrosoftDetector);
        if (activity is null)
        {
            return;
        }

        SetActivityProperties(activity,
        [
            new(TelemetryConstants.Tags.InternalMicrosoftDetectorOutcome, result.Outcome),
            new(TelemetryConstants.Tags.InternalMicrosoftDetectorCacheStatus, result.CacheStatus),
            new(TelemetryConstants.Tags.InternalMicrosoftDetectorDurationMs, (long)result.Duration.TotalMilliseconds),
            new(TelemetryConstants.Tags.InternalMicrosoftDetectorHasAlias, !string.IsNullOrEmpty(result.Alias)),
            new(TelemetryConstants.Tags.InternalMicrosoftDetectorHasDomain, !string.IsNullOrEmpty(result.Domain))
        ]);
        if (!string.IsNullOrEmpty(result.Source))
        {
            SetActivityProperty(activity, TelemetryConstants.Tags.InternalMicrosoftSource, result.Source);
        }

        foreach (var probe in result.ProbeDiagnostics)
        {
            var tags = new ActivityTagsCollection
            {
                [TelemetryConstants.Tags.InternalMicrosoftSource] = probe.Source,
                [TelemetryConstants.Tags.InternalMicrosoftProbeOutcome] = probe.Outcome,
                [TelemetryConstants.Tags.InternalMicrosoftProbeDurationMs] = (long)probe.Duration.TotalMilliseconds,
                [TelemetryConstants.Tags.InternalMicrosoftProbeHasAlias] = probe.HasAlias,
                [TelemetryConstants.Tags.InternalMicrosoftProbeHasDomain] = probe.HasDomain
            };

            if (probe.Failure is { } failure)
            {
                tags[TelemetryConstants.Tags.InternalMicrosoftProbeFailureCode] = failure.Code;
                tags[TelemetryConstants.Tags.InternalMicrosoftProbeFailureStage] = failure.Stage;
                if (failure.ExceptionType is not null)
                {
                    tags[TelemetryConstants.Tags.InternalMicrosoftProbeExceptionType] = failure.ExceptionType;
                }
                if (failure.ProcessExitCode is not null)
                {
                    tags[TelemetryConstants.Tags.InternalMicrosoftProbeProcessExitCode] = failure.ProcessExitCode;
                }
                if (failure.HttpStatusCode is not null)
                {
                    tags[TelemetryConstants.Tags.InternalMicrosoftProbeHttpStatusCode] = failure.HttpStatusCode;
                }
            }

            activity.AddEvent(new ActivityEvent(TelemetryConstants.Events.InternalMicrosoftProbe, tags: tags));
        }
    }

    /// <summary>
    /// Gets the human-readable operating system name for the <c>os.name</c> semantic convention.
    /// </summary>
    internal static string GetOsName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "Windows";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return "Linux";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "macOS";
        }

        return RuntimeInformation.OSDescription;
    }

    /// <summary>
    /// Gets the OpenTelemetry semantic convention value for the <c>os.type</c> attribute.
    /// </summary>
    internal static string GetOsType()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "windows";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return "linux";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "darwin";
        }

        return "unknown";
    }

    /// <summary>
    /// Gets the CLI version from the assembly's informational version attribute.
    /// </summary>
    /// <remarks>
    /// physical-binary-version-by-design (see docs/specs/cli-identity-sidecar.md): the
    /// <c>cli.version</c> telemetry tag identifies the actual running binary, so it reads the
    /// assembly directly and is NOT replaced by an emulated <c>ASPIRE_CLI_VERSION</c> identity.
    /// The emulated identity is emitted separately via the <c>identity.*</c> tags.
    /// </remarks>
    /// <returns>The CLI version string, or an empty string if not available.</returns>
    internal static string GetCliVersion()
    {
        return AssemblyVersionHelper.GetInformationalVersion(typeof(Program).Assembly);
    }

    /// <summary>
    /// Gets the CLI build ID from the assembly's file version attribute.
    /// </summary>
    /// <returns>The CLI build ID string, or an empty string if not available.</returns>
    internal static string GetCliBuildId()
    {
        return AssemblyVersionHelper.GetFileVersion(typeof(Program).Assembly);
    }
}
