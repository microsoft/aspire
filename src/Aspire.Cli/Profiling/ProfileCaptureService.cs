// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Net.Http.Json;
using Aspire.Cli.Bundles;
using Aspire.Cli.Commands;
using Aspire.Cli.Diagnostics;
using Aspire.Cli.DotNet;
using Aspire.Cli.Interaction;
using Aspire.Cli.Layout;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Aspire.Dashboard.Utils;
using Aspire.Hosting;
using Aspire.Otlp.Serialization;
using Aspire.Shared;
using Aspire.Shared.Export;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using AspireCliProfilingTelemetry = Aspire.Cli.Telemetry.ProfilingTelemetry;

namespace Aspire.Cli.Profiling;

internal sealed class ProfileCaptureService(
    IBundleService bundleService,
    LayoutProcessRunner layoutProcessRunner,
    FileLoggerProvider fileLoggerProvider,
    IHttpClientFactory httpClientFactory,
    IInteractionService interactionService,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<ProfileCaptureService> logger)
{
    // The capture flow has two bounded polling loops: first wait for the private dashboard
    // collector to accept requests, then wait for this profiling session's span count to settle
    // before exporting. The quiet-poll threshold means "steady state" is currently eight
    // consecutive 250ms snapshots with the same span count, capped by the profile data timeout.
    private static readonly TimeSpan s_dashboardStartTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan s_profileDataTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan s_pollInterval = TimeSpan.FromMilliseconds(250);
    // Bound the wait for the dashboard process to exit during disposal. The underlying
    // WaitForExitAsync task was started with CancellationToken.None so it survives the capture
    // flow, which means a Kill that fails or a process that ignores termination would otherwise
    // hang CLI shutdown forever. Five seconds is generous for a local child process to exit after
    // Kill while still being short enough that interactive shutdown stays responsive.
    private static readonly TimeSpan s_dashboardDisposeTimeout = TimeSpan.FromSeconds(5);
    private const string DcpProfilingSessionIdTag = "dcp.profiling.session_id";
    private const int ProfileDataQuietPolls = 8;

    public async Task<ProfileCaptureSession> StartAsync(ProfileCaptureOptions options, CancellationToken cancellationToken)
        => await StartAsync(options, s_dashboardStartTimeout, s_pollInterval, cancellationToken).ConfigureAwait(false);

    internal async Task<ProfileCaptureSession> StartAsync(
        ProfileCaptureOptions options,
        TimeSpan dashboardStartTimeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var managedPath = ResolveManagedPathOverride(configuration);
        BundleLayoutLease? layoutLease = null;
        if (managedPath is null)
        {
            layoutLease = await bundleService.EnsureExtractedAndAcquireLayoutAsync("cli", "profile-dashboard", cancellationToken).ConfigureAwait(false);
            var layout = layoutLease?.Layout;
            managedPath = layout?.GetManagedPath();
        }

        // `ASPIRE_REPO_ROOT` is the shared opt-in for repo-local assets. Avoid independently
        // walking from the command directory or process path here, because installed CLIs should not
        // accidentally bind to a nearby checkout while profiling unrelated applications.
        managedPath ??= ResolveRepoLocalManagedPath(configuration[BundleDiscovery.RepoRootEnvVar]);

        if (managedPath is null || !File.Exists(managedPath))
        {
            layoutLease?.Dispose();
            throw new InvalidOperationException(DashboardCommandStrings.ManagedBinaryNotFound);
        }

        var outputCollector = new OutputCollector(fileLoggerProvider, "ProfileDashboard");
        var dashboardArgs = new[]
        {
            "dashboard",
            $"--{KnownConfigNames.AspNetCoreUrls}={options.DashboardUrl}",
            $"--{KnownConfigNames.DashboardOtlpGrpcEndpointUrl}={options.OtlpGrpcUrl}",
            $"--{KnownConfigNames.DashboardOtlpHttpEndpointUrl}={options.OtlpHttpUrl}",
            $"--{KnownConfigNames.DashboardUnsecuredAllowAnonymous}=true",
            $"--{KnownConfigNames.DashboardApiEnabled}=true"
        };

        var processOptions = new ProcessInvocationOptions
        {
            StandardOutputCallback = outputCollector.AppendOutput,
            StandardErrorCallback = outputCollector.AppendError
        };

        IProcessExecution dashboardProcess;
        try
        {
            var environmentVariables = CreateDashboardEnvironment();
            layoutLease?.AddEnvironment(environmentVariables);

            // Launch aspire-managed directly instead of calling `aspire dashboard run`. Calling
            // back through the CLI being profiled would recursively apply --capture-profile and
            // make the collector part of the measurement.
            dashboardProcess = layoutProcessRunner.Start(
                managedPath,
                dashboardArgs,
                environmentVariables: environmentVariables,
                options: processOptions);
        }
        catch (Exception ex)
        {
            layoutLease?.Dispose();
            throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, DashboardCommandStrings.DashboardFailedToStart, ex.Message), ex);
        }

        var session = new ProfileCaptureSession(
            options,
            dashboardProcess,
            httpClientFactory,
            interactionService,
            timeProvider,
            logger,
            s_profileDataTimeout,
            s_pollInterval,
            ProfileDataQuietPolls,
            layoutLease);
        try
        {
            await session.WaitForDashboardAsync(dashboardStartTimeout, pollInterval, cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static Dictionary<string, string> CreateDashboardEnvironment()
    {
        // The collector must not profile itself or export to its own OTLP endpoint. Clear the
        // process-wide profiling and OTEL variables so it does not inherit the capture settings that
        // are intended for the CLI and AppHost being measured.
        return new Dictionary<string, string>
        {
            [KnownConfigNames.ProfilingEnabled] = "false",
            [KnownConfigNames.Legacy.StartupProfilingEnabled] = "false",
            [KnownConfigNames.ProfilingSessionId] = string.Empty,
            [KnownConfigNames.Legacy.StartupOperationId] = string.Empty,
            [KnownOtelConfigNames.ExporterOtlpEndpoint] = string.Empty,
            [KnownOtelConfigNames.ExporterOtlpProtocol] = string.Empty,
            [KnownOtelConfigNames.ExporterOtlpHeaders] = string.Empty,
            [KnownOtelConfigNames.BspScheduleDelay] = string.Empty
        };
    }

    internal static string? ResolveManagedPathOverride(IConfiguration configuration)
    {
        // Honor explicit collector overrides before falling back to bundle/repo discovery. These are
        // configuration-backed so callers can set them via environment variables or other CLI config
        // providers without this path reading process environment directly.

        // ASPIRE_DASHBOARD_PATH is the older hosting/DCP override name. It is expected to point
        // directly at the aspire-managed executable that hosts the dashboard collector.
        if (configuration[BundleDiscovery.DashboardPathEnvVar] is { Length: > 0 } dashboardPath &&
            File.Exists(dashboardPath))
        {
            return dashboardPath;
        }

        // ASPIRE_MANAGED_PATH is the newer managed-binary override. Accept either a direct
        // executable path or the containing managed directory used by bundle layouts.
        if (configuration[BundleDiscovery.ManagedPathEnvVar] is { Length: > 0 } managedPath)
        {
            if (File.Exists(managedPath))
            {
                return managedPath;
            }

            var managedExecutable = Path.Combine(managedPath, BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName));
            if (File.Exists(managedExecutable))
            {
                return managedExecutable;
            }
        }

        return null;
    }

    internal static string? ResolveRepoLocalManagedPath(string? repoRoot)
    {
        if (string.IsNullOrEmpty(repoRoot))
        {
            return null;
        }

        // `ASPIRE_REPO_ROOT` is a dev-build escape hatch, so keep it predictable: use the same Debug
        // net10.0 output produced by the normal repo-local build instead of scanning every artifact
        // folder and guessing between configurations.
        var managedPath = Path.Combine(
            repoRoot,
            "artifacts",
            "bin",
            "Aspire.Managed",
            "Debug",
            "net10.0",
            BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName));
        return File.Exists(managedPath) ? managedPath : null;
    }

    internal sealed class ProfileCaptureSession : IAsyncDisposable
    {
        private readonly ProfileCaptureOptions _options;
        private readonly IProcessExecution _dashboardProcess;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IInteractionService _interactionService;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger _logger;
        private readonly Task<int> _dashboardExitTask;
        private readonly TimeSpan _profileDataTimeout;
        private readonly TimeSpan _profileDataPollInterval;
        private readonly int _profileDataQuietPolls;
        private readonly BundleLayoutLease? _layoutLease;

        public ProfileCaptureSession(
            ProfileCaptureOptions options,
            IProcessExecution dashboardProcess,
            IHttpClientFactory httpClientFactory,
            IInteractionService interactionService,
            ILogger logger)
            : this(options, dashboardProcess, httpClientFactory, interactionService, TimeProvider.System, logger, s_profileDataTimeout, s_pollInterval, ProfileDataQuietPolls)
        {
        }

        internal ProfileCaptureSession(
            ProfileCaptureOptions options,
            IProcessExecution dashboardProcess,
            IHttpClientFactory httpClientFactory,
            IInteractionService interactionService,
            TimeProvider timeProvider,
            ILogger logger,
            TimeSpan profileDataTimeout,
            TimeSpan profileDataPollInterval,
            int profileDataQuietPolls,
            BundleLayoutLease? layoutLease = null)
        {
            _options = options;
            _dashboardProcess = dashboardProcess;
            _httpClientFactory = httpClientFactory;
            _interactionService = interactionService;
            _timeProvider = timeProvider;
            _logger = logger;
            _dashboardExitTask = dashboardProcess.WaitForExitAsync(CancellationToken.None);
            _profileDataTimeout = profileDataTimeout;
            _profileDataPollInterval = profileDataPollInterval;
            _profileDataQuietPolls = profileDataQuietPolls;
            _layoutLease = layoutLease;
        }

        public async Task WaitForDashboardAsync(TimeSpan timeout, TimeSpan pollInterval, CancellationToken cancellationToken)
        {
            // The private dashboard starts asynchronously and only becomes useful once Kestrel is
            // accepting requests on its generated loopback URL. Poll the root endpoint instead of
            // waiting on process start alone, and watch the exit task so startup failures surface as
            // dashboard errors instead of generic connection timeouts.
            using var timeoutCts = new CancellationTokenSource(timeout, _timeProvider);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            while (true)
            {
                if (_dashboardExitTask.IsCompleted)
                {
                    var exitCode = await GetDashboardExitCodeAsync().ConfigureAwait(false);
                    throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, DashboardCommandStrings.DashboardExitedWithError, exitCode));
                }

                try
                {
                    using var client = _httpClientFactory.CreateClient();
                    // ProfileCaptureOptions allocates a loopback ephemeral port before the private
                    // dashboard starts. StartAsync passes that exact URL to Kestrel via
                    // --urls/ASPNETCORE_URLS, so probing _options.DashboardUrl verifies the collector
                    // bound to the random port reserved for this capture session.
                    using var response = await client.GetAsync(_options.DashboardUrl, linkedCts.Token).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        return;
                    }
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException(DashboardCommandStrings.DashboardStartTimedOut);
                }
                catch (HttpRequestException)
                {
                    // Connection refused while Kestrel is still binding is expected during startup.
                    // Keep polling until either the dashboard responds, exits, or the startup budget
                    // expires.
                }

                try
                {
                    await Task.Delay(pollInterval, _timeProvider, linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException(DashboardCommandStrings.DashboardStartTimedOut);
                }
            }
        }

        public async Task<int> ExportAsync(CancellationToken cancellationToken)
        {
            var traces = await WaitForProfileDataAsync(cancellationToken).ConfigureAwait(false);
            if (traces?.Data?.ResourceSpans is not { Length: > 0 })
            {
                _logger.LogError("No profiling traces were exported for session {SessionId}", _options.SessionId);
                return CliExitCodes.DashboardFailure;
            }

            if (CountSessionSpans(traces, _options.SessionId) == 0)
            {
                _logger.LogError("No exported spans matched profiling session {SessionId}", _options.SessionId);
                return CliExitCodes.DashboardFailure;
            }

            var outputDirectory = Path.GetDirectoryName(_options.OutputPath);
            if (!string.IsNullOrEmpty(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            var archive = new ExportArchive();
            archive.Traces["profile"] = traces.Data;
            archive.WriteToFile(_options.OutputPath);

            _interactionService.DisplayMessage(
                KnownEmojis.CheckMarkButton,
                string.Format(CultureInfo.CurrentCulture, ExportCommandStrings.ExportComplete, MarkupHelpers.SafeFileLink(_interactionService, _options.OutputPath)),
                allowMarkup: true);

            return CliExitCodes.Success;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_dashboardProcess.HasExited)
            {
                try
                {
                    _dashboardProcess.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to stop profile dashboard process.");
                }
            }

            try
            {
                // Bound the wait so a Kill that fails or a process that refuses to terminate cannot
                // hang CLI shutdown. _dashboardExitTask was created with CancellationToken.None so
                // it survives the capture flow; apply the timeout here via WaitAsync instead of
                // tearing the underlying wait down.
                await _dashboardExitTask.WaitAsync(s_dashboardDisposeTimeout, _timeProvider).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (TimeoutException ex)
            {
                _logger.LogDebug(ex, "Profile dashboard process did not exit within the disposal timeout.");
            }
            finally
            {
                _dashboardProcess.Dispose();
                _layoutLease?.Dispose();
            }
        }

        private async Task<TelemetryApiResponse?> WaitForProfileDataAsync(CancellationToken cancellationToken)
        {
            using var timeoutCts = new CancellationTokenSource(_profileDataTimeout, _timeProvider);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            TelemetryApiResponse? lastResult = null;
            var quietPolls = 0;
            var lastProfileSpanCount = -1;
            while (true)
            {
                try
                {
                    lastResult = await GetTracesAsync(linkedCts.Token).ConfigureAwait(false);
                    // The dashboard trace API returns a full snapshot. Poll for up to 10 seconds,
                    // count only spans that carry this capture session id, and treat the profile as
                    // steady once that count is unchanged for eight 250ms polls (~2 seconds). That
                    // steady-state check is the quietPolls/lastProfileSpanCount logic below, and it
                    // lets late batch exporter flushes from child AppHost processes arrive before
                    // writing. Aspire profiling spans carry the session id on each span:
                    //   "spans":[{"attributes":[{"key":"aspire.profiling.session_id","value":{"stringValue":"<session>"}}]}]
                    // DCP owns its profiling namespace and carries the session id on the resource:
                    //   "resource":{"attributes":[{"key":"dcp.profiling.session_id","value":{"stringValue":"<session>"}}]}
                    var profileSpanCount = CountSessionSpans(lastResult, _options.SessionId);
                    if (profileSpanCount > 0)
                    {
                        if (profileSpanCount == lastProfileSpanCount)
                        {
                            quietPolls++;
                        }
                        else
                        {
                            quietPolls = 0;
                            lastProfileSpanCount = profileSpanCount;
                        }

                        if (quietPolls >= _profileDataQuietPolls)
                        {
                            return lastResult;
                        }
                    }
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    return lastResult;
                }

                try
                {
                    await Task.Delay(_profileDataPollInterval, _timeProvider, linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    return lastResult;
                }
            }
        }

        private async Task<TelemetryApiResponse?> GetTracesAsync(CancellationToken cancellationToken)
        {
            using var client = _httpClientFactory.CreateClient();
            var url = DashboardUrls.TelemetryTracesApiUrl(_options.DashboardUrl, limit: TelemetryCommandHelpers.MaxTelemetryLimit);
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);

            // The dashboard serves a Blazor fallback page when the telemetry API is disabled or
            // missing. This helper turns that HTML response into the same failure shape as a 404
            // and enforces successful JSON before we deserialize the telemetry payload.
            TelemetryCommandHelpers.EnsureTelemetryApiResponse(response);

            return await response.Content.ReadFromJsonAsync(OtlpJsonSerializerContext.Default.TelemetryApiResponse, cancellationToken).ConfigureAwait(false);
        }

        private static int CountSessionSpans(TelemetryApiResponse? response, string sessionId)
        {
            var count = 0;
            foreach (var resourceSpans in response?.Data?.ResourceSpans ?? [])
            {
                var resourceHasSessionId = ContainsProfilingSessionId(resourceSpans.Resource?.Attributes, sessionId);
                foreach (var scopeSpans in resourceSpans.ScopeSpans ?? [])
                {
                    foreach (var span in scopeSpans.Spans ?? [])
                    {
                        if (resourceHasSessionId || ContainsProfilingSessionId(span.Attributes, sessionId))
                        {
                            count++;
                        }
                    }
                }
            }

            return count;
        }

        private static bool ContainsProfilingSessionId(IEnumerable<OtlpKeyValueJson>? attributes, string sessionId)
        {
            return attributes?.Any(attribute =>
                IsProfilingSessionIdTag(attribute.Key) &&
                string.Equals(attribute.Value?.StringValue, sessionId, StringComparison.Ordinal)) is true;
        }

        private static bool IsProfilingSessionIdTag(string? attributeName)
        {
            return string.Equals(attributeName, AspireCliProfilingTelemetry.Tags.ProfilingSessionId, StringComparison.Ordinal) ||
                string.Equals(attributeName, DcpProfilingSessionIdTag, StringComparison.Ordinal);
        }

        private async Task<int> GetDashboardExitCodeAsync()
        {
            try
            {
                return await _dashboardExitTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return CliExitCodes.Cancelled;
            }
        }
    }
}
