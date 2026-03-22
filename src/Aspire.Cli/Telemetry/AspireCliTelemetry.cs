// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Aspire.Hosting;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Telemetry;

/// <summary>
/// Provides a single ActivitySource for all Aspire CLI components.
/// </summary>
internal sealed class AspireCliTelemetry : IHostedService
{
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

    private readonly ActivitySource _diagnosticsActivitySource;
    private readonly ActivitySource _reportedActivitySource;
    private readonly IMachineInformationProvider _machineInformationProvider;
    private readonly ICIEnvironmentDetector _ciEnvironmentDetector;
    private readonly ILogger<AspireCliTelemetry> _logger;
    private readonly List<KeyValuePair<string, object?>> _tagsList = [];

    private bool _isInitialized;

    /// <summary>
    /// Initializes a new instance of the <see cref="AspireCliTelemetry"/> class.
    /// </summary>
    /// <param name="logger">The logger instance for recording errors.</param>
    /// <param name="machineInformationProvider">The machine information provider.</param>
    /// <param name="ciEnvironmentDetector">The CI environment detector.</param>
    public AspireCliTelemetry(ILogger<AspireCliTelemetry> logger, IMachineInformationProvider machineInformationProvider, ICIEnvironmentDetector ciEnvironmentDetector)
        : this(logger, machineInformationProvider, ciEnvironmentDetector, ReportedActivitySourceName, DiagnosticsActivitySourceName)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AspireCliTelemetry"/> class with custom activity source names.
    /// This constructor is intended for testing purposes only to enable thread-safe test isolation.
    /// </summary>
    /// <param name="logger">The logger instance for recording errors.</param>
    /// <param name="machineInformationProvider">The machine information provider.</param>
    /// <param name="ciEnvironmentDetector">The CI environment detector.</param>
    /// <param name="reportedSourceName">The name for the reported activity source.</param>
    /// <param name="diagnosticsSourceName">The name for the diagnostics activity source.</param>
    internal AspireCliTelemetry(ILogger<AspireCliTelemetry> logger, IMachineInformationProvider machineInformationProvider, ICIEnvironmentDetector ciEnvironmentDetector, string reportedSourceName, string diagnosticsSourceName)
    {
        _logger = logger;
        _machineInformationProvider = machineInformationProvider;
        _ciEnvironmentDetector = ciEnvironmentDetector;
        _reportedActivitySource = new ActivitySource(reportedSourceName);
        _diagnosticsActivitySource = new ActivitySource(diagnosticsSourceName);
    }

    /// <summary>
    /// TESTING PURPOSES ONLY: Gets the default tags used for telemetry.
    /// </summary>
    internal IReadOnlyList<KeyValuePair<string, object?>> GetDefaultTags()
    {
        CheckInitialization();
        return [.. _tagsList];
    }

    /// <summary>
    /// Starts a new activity for reported telemetry that is exported to external systems.
    /// </summary>
    /// <param name="name">The name of the activity.</param>
    /// <param name="kind">The activity kind.</param>
    /// <returns>The started activity, or null if no listeners are registered.</returns>
    public Activity? StartReportedActivity([CallerMemberName] string name = "", ActivityKind kind = ActivityKind.Internal)
    {
        return StartActivityCore(_reportedActivitySource, name, kind);
    }

    /// <summary>
    /// Starts a new activity for diagnostic telemetry used for internal diagnostics only.
    /// Uses the caller member name if no name is provided.
    /// </summary>
    /// <param name="name">The name of the activity. Defaults to the caller member name if not specified.</param>
    /// <param name="kind">The activity kind.</param>
    /// <returns>The started activity, or null if no listeners are registered.</returns>
    public Activity? StartDiagnosticActivity([CallerMemberName] string name = "", ActivityKind kind = ActivityKind.Internal)
    {
        return StartActivityCore(_diagnosticsActivitySource, name, kind);
    }

    private Activity? StartActivityCore(ActivitySource source, string name, ActivityKind kind)
    {
        CheckInitialization();

        // Activities must have a name.
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var activity = source.StartActivity(name, kind);

        if (activity is not null)
        {
            foreach (var tag in _tagsList)
            {
                activity.AddTag(tag.Key, tag.Value);
            }
        }

        return activity;
    }

    /// <summary>
    /// Records an error by logging it and adding an activity event to a CLI activity.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="exception">The exception that occurred.</param>
    public void RecordError(string message, Exception exception)
    {
        CheckInitialization();

        _logger.LogError(exception, message);

        var activity = FindReportedActivity(Activity.Current);
        if (activity is not null)
        {
            // This adds an activity event for the error. Capturing the data manually is intentional.
            // The reason is we want to record this information to the traces table instead of the exceptions table.
            var tags = new ActivityTagsCollection
            {
                [TelemetryConstants.Tags.ExceptionType] = exception.GetType().FullName,
                [TelemetryConstants.Tags.ExceptionMessage] = exception.Message,
                [TelemetryConstants.Tags.ExceptionStackTrace] = exception.StackTrace
            };

            foreach (var tag in _tagsList)
            {
                tags[tag.Key] = tag.Value;
            }

            activity.AddEvent(new ActivityEvent(TelemetryConstants.Events.Error, tags: tags));
        }
        else
        {
            // There should always be a reported activity. Sanity check in case something goes wrong.
            Debug.WriteLine("No reported activity found to record the error event.");
        }
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Initializes the telemetry service by collecting machine information.
    /// </summary>
    internal async Task InitializeAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        try
        {
            var macAddressHashTask = _machineInformationProvider.GetMacAddressHash();
            var deviceIdTask = _machineInformationProvider.GetOrCreateDeviceId();

            await Task.WhenAll(new Task[] { macAddressHashTask, deviceIdTask }).ConfigureAwait(false);

            _tagsList.Add(new(TelemetryConstants.Tags.MacAddressHash, macAddressHashTask.Result));
            _tagsList.Add(new(TelemetryConstants.Tags.DeviceId, deviceIdTask.Result));

            // This is consistent with dashboard version data.
            _tagsList.Add(new(TelemetryConstants.Tags.CliVersion, GetCliVersion()));
            _tagsList.Add(new(TelemetryConstants.Tags.CliBuildId, GetCliBuildId()));

            _tagsList.Add(new(TelemetryConstants.Tags.DeploymentEnvironmentName, _ciEnvironmentDetector.IsCIEnvironment() ? "ci" : "local"));

            _tagsList.Add(new(TelemetryConstants.Tags.OsName, GetOsName()));
            _tagsList.Add(new(TelemetryConstants.Tags.OsType, GetOsType()));
            _tagsList.Add(new(TelemetryConstants.Tags.OsVersion, Environment.OSVersion.Version.ToString()));
        }
        catch (Exception ex)
        {
            // Don't throw an error if there is a telemetry issue.
            _logger.LogError(ex, "Error occurred initializing telemetry service.");
        }
        finally
        {
            _isInitialized = true;
        }
    }

    private void CheckInitialization()
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException(
                $"Telemetry service has not been initialized. Use {nameof(InitializeAsync)}() before any other operations.");
        }
    }

    /// <summary>
    /// Searches the activity hierarchy to find the first reported activity.
    /// We want to log errors only to the reported activity so they're reported.
    /// </summary>
    private Activity? FindReportedActivity(Activity? activity)
    {
        while (activity is not null)
        {
            if (activity.Source == _reportedActivitySource)
            {
                return activity;
            }

            activity = activity.Parent;
        }

        return null;
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
    /// <returns>The CLI version string, or an empty string if not available.</returns>
    internal static string GetCliVersion()
    {
        return typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;
    }

    /// <summary>
    /// Gets the CLI build ID from the assembly's file version attribute.
    /// </summary>
    /// <returns>The CLI build ID string, or an empty string if not available.</returns>
    internal static string GetCliBuildId()
    {
        return typeof(Program).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? string.Empty;
    }
}
