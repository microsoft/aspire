// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Backchannel;
using Aspire.Cli.DotNet;
using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Telemetry;

/// <summary>
/// Creates profiling-only activities used by CLI diagnostics.
/// </summary>
internal sealed class ProfilingTelemetry(IConfiguration configuration) : IDisposable
{
    public const string ActivitySourceName = "Aspire.Cli.Profiling";

    private readonly ActivitySource _activitySource = new(ActivitySourceName);
    private ProfilingTelemetryContext? _profilingTelemetryContext;
    private bool _profilingTelemetryContextInitialized;

    /// <summary>
    /// Activity names for profiling spans. These names describe local diagnostic
    /// work such as CLI orchestration and child-process lifetimes; they are not
    /// exported through customer telemetry.
    /// </summary>
    internal static class Activities
    {
        public const string RunCommand = "aspire/cli/run";
        public const string RunAppHostFindAppHost = "aspire/cli/run_apphost.find_apphost";
        public const string RunAppHostStopExistingInstance = "aspire/cli/run_apphost.stop_existing_instance";
        public const string RunAppHostStartProject = "aspire/cli/run_apphost.start_project";
        public const string RunAppHostWaitForBuild = "aspire/cli/run_apphost.wait_for_build";
        public const string RunAppHostWaitForBackchannel = "aspire/cli/run_apphost.wait_for_backchannel";
        public const string RunAppHostGetDashboardUrls = "aspire/cli/run_apphost.get_dashboard_urls";
        public const string RunAppHostLifetime = "aspire/cli/run_apphost.lifetime";
        public const string StartAppHostSpawnChild = "aspire/cli/start_apphost.spawn_child";
        public const string StartAppHostWaitForBackchannel = "aspire/cli/start_apphost.wait_for_backchannel";
        public const string StartAppHostGetDashboardUrls = "aspire/cli/start_apphost.get_dashboard_urls";
        public const string BackchannelConnect = "aspire/cli/backchannel.connect";
        public const string BackchannelGetDashboardUrls = "aspire/cli/backchannel.get_dashboard_urls";
        public const string AppHostRun = "aspire/cli/apphost.run";
        public const string AppHostConfigureIsolatedMode = "aspire/cli/apphost.configure_isolated_mode";
        public const string AppHostEnsureDevCertificates = "aspire/cli/apphost.ensure_dev_certificates";
        public const string AppHostBuild = "aspire/cli/apphost.build";
        public const string AppHostCheckCompatibility = "aspire/cli/apphost.check_compatibility";
        public const string AppHostRunDotnetLifetime = "aspire/cli/apphost.run_dotnet.lifetime";
        public const string AppHostServerLifetime = "aspire/cli/apphost_server.lifetime";
        public const string DotNetRunLifetime = "aspire/cli/dotnet.run.lifetime";
        public const string GuestInitializeCommand = "aspire/cli/guest.initialize_command";
        public const string GuestInstallDependencies = "aspire/cli/guest.install_dependencies";
        public const string GuestExecuteCommand = "aspire/cli/guest.execute_command";
        public const string NpmCommand = "aspire/cli/npm.command";

        public static string DotNetCommand(string command) => $"aspire/cli/dotnet.{command}";
    }

    /// <summary>
    /// Tag names for profiling spans. Tags capture low-cardinality dimensions
    /// and useful diagnostics such as process IDs, exit codes, command names,
    /// output counts, and emitted artifact paths.
    /// </summary>
    internal static class Tags
    {
        public const string ProfilingSessionId = "aspire.profiling.session_id";
        public const string LegacyStartupOperationId = "aspire.startup.operation_id";
        public const string DotNetCommand = "aspire.cli.dotnet.command";
        public const string DotNetProjectFile = "aspire.cli.dotnet.project_file";
        public const string DotNetWorkingDirectory = "aspire.cli.dotnet.working_directory";
        public const string DotNetNoLaunchProfile = "aspire.cli.dotnet.no_launch_profile";
        public const string DotNetStartDebugSession = "aspire.cli.dotnet.start_debug_session";
        public const string DotNetDebug = "aspire.cli.dotnet.debug";
        public const string DotNetMsBuildServer = "aspire.cli.dotnet.msbuild_server";
        public const string DotNetArgsCount = "aspire.cli.dotnet.args.count";
        public const string DotNetStdoutLines = "aspire.cli.dotnet.stdout_lines";
        public const string DotNetStderrLines = "aspire.cli.dotnet.stderr_lines";
        public const string DotNetBinlogEnabled = "aspire.cli.dotnet.binlog_enabled";
        public const string DotNetBinlogPath = "aspire.cli.dotnet.binlog_path";
        public const string DotNetBinlogArtifactType = "aspire.cli.dotnet.binlog_artifact_type";
        public const string DotNetBinlogSkipReason = "aspire.cli.dotnet.binlog_skip_reason";
        public const string AppHostProjectFileSpecified = "aspire.cli.apphost.project_file_specified";
        public const string AppHostRunningInstanceResult = "aspire.cli.apphost.running_instance_result";
        public const string AppHostLanguage = "aspire.cli.apphost.language";
        public const string AppHostNoBuild = "aspire.cli.apphost.no_build";
        public const string AppHostNoRestore = "aspire.cli.apphost.no_restore";
        public const string AppHostWaitForDebugger = "aspire.cli.apphost.wait_for_debugger";
        public const string AppHostBuildSuccess = "aspire.cli.apphost.build_success";
        public const string AppHostBackchannelConnected = "aspire.cli.apphost.backchannel_connected";
        public const string AppHostDashboardHealthy = "aspire.cli.apphost.dashboard_healthy";
        public const string AppHostDashboardHasUrl = "aspire.cli.apphost.dashboard_has_url";
        public const string AppHostDashboardHasCodespacesUrl = "aspire.cli.apphost.dashboard_has_codespaces_url";
        public const string AppHostExtensionHost = "aspire.cli.apphost.extension_host";
        public const string AppHostExtensionHasBuildCapability = "aspire.cli.apphost.extension_has_build_capability";
        public const string AppHostIsCompatible = "aspire.cli.apphost.is_compatible";
        public const string AppHostSupportsBackchannel = "aspire.cli.apphost.supports_backchannel";
        public const string AppHostAspireHostingVersion = "aspire.cli.apphost.aspire_hosting_version";
        public const string AppHostWatch = "aspire.cli.apphost.watch";
        public const string DevCertificateEnvironmentVariableCount = "aspire.cli.dev_cert.env_var_count";
        public const string BackchannelSocketFile = "aspire.cli.backchannel.socket_file";
        public const string BackchannelAutoReconnect = "aspire.cli.backchannel.auto_reconnect";
        public const string BackchannelRetryCount = "aspire.cli.backchannel.retry_count";
        public const string BackchannelExpectedHash = "aspire.cli.backchannel.expected_hash";
        public const string BackchannelHasLegacyHash = "aspire.cli.backchannel.has_legacy_hash";
        public const string BackchannelScanCount = "aspire.cli.backchannel.scan_count";
        public const string BackchannelCapabilityCount = "aspire.cli.backchannel.capability_count";
        public const string BackchannelHasBaselineCapability = "aspire.cli.backchannel.has_baseline_capability";
        public const string ChildCommand = "aspire.cli.child.command";
        public const string AppHostServerImplementation = "aspire.cli.apphost_server.implementation";
        public const string GuestRuntimeLanguage = "aspire.cli.guest.language";
        public const string GuestRuntimeDisplayName = "aspire.cli.guest.display_name";
        public const string GuestCommand = "aspire.cli.guest.command";
        public const string GuestWorkingDirectory = "aspire.cli.guest.working_directory";
        public const string NpmCommand = "aspire.cli.npm.command";
        public const string NpmWorkingDirectory = "aspire.cli.npm.working_directory";
        public const string ProcessCommandArgsCount = "process.command_args.count";
    }

    /// <summary>
    /// Event names for profiling spans. Events mark meaningful points within a
    /// span, such as process start, first output, retries, and readiness signals.
    /// </summary>
    internal static class Events
    {
        public const string DotNetProcessStarted = "aspire/cli/dotnet.process_started";
        public const string DotNetProcessStartFailed = "aspire/cli/dotnet.process_start_failed";
        public const string DotNetProcessExited = "aspire/cli/dotnet.process_exited";
        public const string DotNetFirstStdout = "aspire/cli/dotnet.first_stdout";
        public const string DotNetFirstStderr = "aspire/cli/dotnet.first_stderr";
        public const string BackchannelWaitForRpc = "aspire/cli/backchannel.wait_for_rpc";
        public const string BackchannelRpcReady = "aspire/cli/backchannel.rpc_ready";
        public const string BackchannelGetDashboardUrlsInvoke = "aspire/cli/backchannel.get_dashboard_urls.invoke";
        public const string BackchannelGetDashboardUrlsResponse = "aspire/cli/backchannel.get_dashboard_urls.response";
        public const string BackchannelConnectAttempt = "aspire/cli/backchannel.connect_attempt";
        public const string BackchannelConnected = "aspire/cli/backchannel.connected";
        public const string BackchannelSocketConnectStart = "aspire/cli/backchannel.socket_connect_start";
        public const string BackchannelSocketConnected = "aspire/cli/backchannel.socket_connected";
        public const string BackchannelRpcListening = "aspire/cli/backchannel.rpc_listening";
        public const string BackchannelGetCapabilitiesStart = "aspire/cli/backchannel.get_capabilities_start";
        public const string BackchannelGetCapabilitiesResponse = "aspire/cli/backchannel.get_capabilities_response";
        public const string StartAppHostBackchannelConnected = "aspire/cli/start_apphost.backchannel_connected";
        public const string RunAppHostStarted = "aspire/cli/run_apphost.started";
        public const string AuxBackchannelGetDashboardUrlsInvoke = "aspire/cli/aux_backchannel.get_dashboard_urls.invoke";
        public const string AuxBackchannelGetDashboardUrlsResponse = "aspire/cli/aux_backchannel.get_dashboard_urls.response";
        public const string AuxBackchannelGetDashboardUrlsNotFound = "aspire/cli/aux_backchannel.get_dashboard_urls.not_found";
        public const string AppHostBuildReady = "aspire/cli/apphost.build_ready";
    }

    /// <summary>
    /// Common profiling tag values. Values should be stable strings so trace
    /// queries can group by them across CLI versions.
    /// </summary>
    internal static class Values
    {
        public const string UnsupportedDotNetCommand = "unsupported_dotnet_command";
        public const string MsBuildBinlog = "msbuild.binlog";
    }

    public bool IsEnabled => ProfilingTelemetryContext.IsEnabled(configuration);

    public ActivityScope CurrentActivity => IsEnabled ? new(Activity.Current, ownsActivity: false) : default;

    public ProfilingTelemetryContext? Context
    {
        get
        {
            if (!_profilingTelemetryContextInitialized)
            {
                _profilingTelemetryContext = ProfilingTelemetryContext.FromConfiguration(configuration);
                _profilingTelemetryContextInitialized = true;
            }

            return _profilingTelemetryContext;
        }
    }

    public ProfilingTelemetryContext? CreateContext(ActivityScope parentActivity)
    {
        return parentActivity.CreateProfilingTelemetryContext(this);
    }

    private ProfilingTelemetryContext? CreateContext(Activity? parentActivity)
    {
        if (!IsEnabled)
        {
            return null;
        }

        var context = ProfilingTelemetryContext.Create(parentActivity);
        _profilingTelemetryContext = context;
        _profilingTelemetryContextInitialized = true;
        return context;
    }

    public ProfilingTelemetryContext? CreateContextFromCurrentActivity()
    {
        return CreateContext(Activity.Current);
    }

    internal ActivityScope StartAppHostBuild(bool noRestore, bool extensionHost, bool extensionHasBuildCapability)
    {
        var activity = StartActivity(Activities.AppHostBuild);
        activity.SetAppHostNoRestore(noRestore);
        activity.SetAppHostExtensionHost(extensionHost);
        activity.SetAppHostExtensionHasBuildCapability(extensionHasBuildCapability);
        return activity;
    }

    internal ActivityScope StartAppHostCheckCompatibility()
    {
        return StartActivity(Activities.AppHostCheckCompatibility);
    }

    internal ActivityScope StartAppHostConfigureIsolatedMode()
    {
        return StartActivity(Activities.AppHostConfigureIsolatedMode);
    }

    internal ActivityScope StartAppHostEnsureDevCertificates()
    {
        return StartActivity(Activities.AppHostEnsureDevCertificates);
    }

    internal ActivityScope StartAppHostRun()
    {
        return StartActivity(Activities.AppHostRun);
    }

    internal ActivityScope StartAppHostRunDotnetLifetime(bool watch, bool noBuild, bool noRestore)
    {
        var activity = StartActivity(Activities.AppHostRunDotnetLifetime);
        activity.SetAppHostWatch(watch);
        activity.SetAppHostNoBuild(noBuild);
        activity.SetAppHostNoRestore(noRestore);
        return activity;
    }

    internal ActivityScope StartAuxiliaryBackchannelGetDashboardUrls()
    {
        var activity = CurrentActivity;
        activity.AddAuxBackchannelGetDashboardUrlsInvokeEvent();
        return activity;
    }

    internal ActivityScope StartBackchannelConnect(string socketPath)
    {
        var activity = StartActivity(Activities.BackchannelConnect);
        activity.SetBackchannelSocketFile(socketPath);
        return activity;
    }

    internal ActivityScope StartBackchannelConnect(string socketPath, bool autoReconnect, int retryCount)
    {
        var activity = StartBackchannelConnect(socketPath);
        activity.SetBackchannelAutoReconnect(autoReconnect);
        activity.SetBackchannelRetryCount(retryCount);
        return activity;
    }

    internal ActivityScope StartBackchannelGetDashboardUrls()
    {
        return StartActivity(Activities.BackchannelGetDashboardUrls);
    }

    internal ActivityScope StartDetachedGetDashboardUrls(ProfilingTelemetryContext? profilingTelemetryContext)
    {
        return StartActivity(Activities.StartAppHostGetDashboardUrls, profilingTelemetryContext: profilingTelemetryContext);
    }

    internal ActivityScope StartDetachedSpawnChild(string executablePath, int argsCount, string childCommand, ProfilingTelemetryContext? profilingTelemetryContext)
    {
        var activity = StartActivity(Activities.StartAppHostSpawnChild, profilingTelemetryContext: profilingTelemetryContext);
        activity.SetProcessExecutableName(Path.GetFileName(executablePath));
        activity.SetProcessCommandArgsCount(argsCount);
        activity.SetChildCommand(childCommand);
        return activity;
    }

    internal ActivityScope StartDetachedWaitForBackchannel(int childProcessId, string expectedHash, bool hasLegacyHash, ProfilingTelemetryContext? profilingTelemetryContext)
    {
        var activity = StartActivity(Activities.StartAppHostWaitForBackchannel, profilingTelemetryContext: profilingTelemetryContext);
        activity.SetProcessId(childProcessId);
        activity.SetBackchannelExpectedHash(expectedHash);
        activity.SetBackchannelHasLegacyHash(hasLegacyHash);
        return activity;
    }

    internal ActivityScope StartDotNetProcess(string dotnetCommand, FileInfo? projectFile, DirectoryInfo workingDirectory, ProcessInvocationOptions options)
    {
        var activityName = string.Equals(dotnetCommand, "run", StringComparison.Ordinal)
            ? Activities.DotNetRunLifetime
            : Activities.DotNetCommand(dotnetCommand);
        var activity = StartActivity(activityName, ActivityKind.Client);
        activity.SetDotNetInvocation(dotnetCommand, projectFile, workingDirectory, options);
        return activity;
    }

    internal ActivityScope StartAppHostServerLifetime(string implementationName)
    {
        var activity = StartActivity(Activities.AppHostServerLifetime, ActivityKind.Client);
        activity.SetAppHostServerImplementation(implementationName);
        return activity;
    }

    internal ActivityScope StartGuestInitializeCommand(string languageId, string displayName, string command, int argsCount, DirectoryInfo workingDirectory)
    {
        var activity = StartGuestProcessActivity(Activities.GuestInitializeCommand, languageId, displayName, command, argsCount, workingDirectory);
        return activity;
    }

    internal ActivityScope StartGuestInstallDependencies(string languageId, string displayName, string command, int argsCount, DirectoryInfo workingDirectory)
    {
        var activity = StartGuestProcessActivity(Activities.GuestInstallDependencies, languageId, displayName, command, argsCount, workingDirectory);
        return activity;
    }

    internal ActivityScope StartGuestExecuteCommand(string languageId, string displayName, string command, int argsCount, DirectoryInfo workingDirectory)
    {
        var activity = StartGuestProcessActivity(Activities.GuestExecuteCommand, languageId, displayName, command, argsCount, workingDirectory);
        return activity;
    }

    internal ActivityScope StartNpmCommand(string command, int argsCount, string workingDirectory)
    {
        var activity = StartActivity(Activities.NpmCommand, ActivityKind.Client);
        activity.SetNpmInvocation(command, argsCount, workingDirectory);
        return activity;
    }

    internal ActivityScope StartRunAppHostFindAppHost(FileInfo? passedAppHostProjectFile, ProfilingTelemetryContext? profilingTelemetryContext)
    {
        var activity = StartActivity(Activities.RunAppHostFindAppHost, profilingTelemetryContext: profilingTelemetryContext);
        activity.SetAppHostProjectFileSpecified(passedAppHostProjectFile is not null);
        return activity;
    }

    internal ActivityScope StartRunAppHostGetDashboardUrls(ProfilingTelemetryContext? profilingTelemetryContext)
    {
        return StartActivity(Activities.RunAppHostGetDashboardUrls, profilingTelemetryContext: profilingTelemetryContext);
    }

    internal ActivityScope StartRunAppHostLifetime(ProfilingTelemetryContext? profilingTelemetryContext)
    {
        var activity = StartActivity(Activities.RunAppHostLifetime, profilingTelemetryContext: profilingTelemetryContext);
        activity.AddRunAppHostStartedEvent();
        return activity;
    }

    internal ActivityScope StartRunAppHostStartProject(string languageId, bool noBuild, bool waitForDebugger, ProfilingTelemetryContext? profilingTelemetryContext)
    {
        var activity = StartActivity(Activities.RunAppHostStartProject, profilingTelemetryContext: profilingTelemetryContext);
        activity.SetAppHostLanguage(languageId);
        activity.SetAppHostNoBuild(noBuild);
        activity.SetAppHostWaitForDebugger(waitForDebugger);
        return activity;
    }

    internal ActivityScope StartRunAppHostStopExistingInstance(ProfilingTelemetryContext? profilingTelemetryContext)
    {
        return StartActivity(Activities.RunAppHostStopExistingInstance, profilingTelemetryContext: profilingTelemetryContext);
    }

    internal ActivityScope StartRunAppHostWaitForBackchannel(ProfilingTelemetryContext? profilingTelemetryContext)
    {
        return StartActivity(Activities.RunAppHostWaitForBackchannel, profilingTelemetryContext: profilingTelemetryContext);
    }

    internal ActivityScope StartRunAppHostWaitForBuild(ProfilingTelemetryContext? profilingTelemetryContext)
    {
        return StartActivity(Activities.RunAppHostWaitForBuild, profilingTelemetryContext: profilingTelemetryContext);
    }

    internal ActivityScope StartRunCommand(ProfilingTelemetryContext? profilingTelemetryContext)
    {
        return StartActivity(
            Activities.RunCommand,
            profilingTelemetryContext: profilingTelemetryContext,
            startWithRemoteParent: profilingTelemetryContext is not null);
    }

    private ActivityScope StartActivity(
        string name,
        ActivityKind kind = ActivityKind.Internal,
        ProfilingTelemetryContext? profilingTelemetryContext = null,
        bool startWithRemoteParent = false)
    {
        if (!IsEnabled)
        {
            return default;
        }

        profilingTelemetryContext ??= Context;

        // Detached child processes need to continue the remote parent from the launcher,
        // while in-process profiling spans should just follow Activity.Current.
        Activity? activity;
        if (startWithRemoteParent &&
            profilingTelemetryContext is not null &&
            profilingTelemetryContext.TryGetActivityContext(out var parentContext))
        {
            activity = _activitySource.StartActivity(name, kind, parentContext);
        }
        else
        {
            activity = _activitySource.StartActivity(name, kind);
        }

        profilingTelemetryContext?.AddTags(activity);
        return new ActivityScope(activity);
    }

    private ActivityScope StartGuestProcessActivity(string activityName, string languageId, string displayName, string command, int argsCount, DirectoryInfo workingDirectory)
    {
        var activity = StartActivity(activityName, ActivityKind.Client);
        activity.SetGuestInvocation(languageId, displayName, command, argsCount, workingDirectory);
        return activity;
    }

    public void Dispose()
    {
        _activitySource.Dispose();
    }

    internal readonly struct ActivityScope(Activity? activity, bool ownsActivity = true) : IDisposable
    {
        public bool IsRunning => activity is not null;

        internal ProfilingTelemetryContext? CreateProfilingTelemetryContext(ProfilingTelemetry profilingTelemetry)
        {
            var context = profilingTelemetry.CreateContext(activity);
            context?.AddTags(activity);
            return context;
        }

        public void AddAppHostBuildReadyEvent() => AddEvent(Events.AppHostBuildReady);

        public void AddAuxBackchannelGetDashboardUrlsInvokeEvent() => AddEvent(Events.AuxBackchannelGetDashboardUrlsInvoke);

        public void AddAuxBackchannelGetDashboardUrlsNotFoundEvent() => AddEvent(Events.AuxBackchannelGetDashboardUrlsNotFound);

        public void AddAuxBackchannelGetDashboardUrlsResponseEvent() => AddEvent(Events.AuxBackchannelGetDashboardUrlsResponse);

        public void AddBackchannelConnectedEvent() => AddEvent(Events.BackchannelConnected);

        public void AddBackchannelConnectAttemptEvent(int retryCount)
        {
            activity?.AddEvent(new ActivityEvent(Events.BackchannelConnectAttempt, tags: new ActivityTagsCollection
            {
                [Tags.BackchannelRetryCount] = retryCount
            }));
        }

        public void AddBackchannelGetCapabilitiesStartEvent() => AddEvent(Events.BackchannelGetCapabilitiesStart);

        public void AddBackchannelGetCapabilitiesResponseEvent() => AddEvent(Events.BackchannelGetCapabilitiesResponse);

        public void AddBackchannelGetDashboardUrlsInvokeEvent() => AddEvent(Events.BackchannelGetDashboardUrlsInvoke);

        public void AddBackchannelGetDashboardUrlsResponseEvent() => AddEvent(Events.BackchannelGetDashboardUrlsResponse);

        public void AddBackchannelRpcListeningEvent() => AddEvent(Events.BackchannelRpcListening);

        public void AddBackchannelRpcReadyEvent() => AddEvent(Events.BackchannelRpcReady);

        public void AddBackchannelSocketConnectedEvent() => AddEvent(Events.BackchannelSocketConnected);

        public void AddBackchannelSocketConnectStartEvent() => AddEvent(Events.BackchannelSocketConnectStart);

        public void AddBackchannelWaitForRpcEvent() => AddEvent(Events.BackchannelWaitForRpc);

        public void AddDotNetFirstStderrEvent() => AddEvent(Events.DotNetFirstStderr);

        public void AddDotNetFirstStdoutEvent() => AddEvent(Events.DotNetFirstStdout);

        public void AddDotNetProcessExitedEvent() => AddEvent(Events.DotNetProcessExited);

        public void AddDotNetProcessStartFailedEvent() => AddEvent(Events.DotNetProcessStartFailed);

        public void AddDotNetProcessStartedEvent(int processId)
        {
            SetProcessId(processId);
            activity?.AddEvent(new ActivityEvent(Events.DotNetProcessStarted, tags: new ActivityTagsCollection
            {
                [TelemetryConstants.Tags.ProcessPid] = processId
            }));
        }

        public void AddDotNetProcessStartResult(bool started, int? processId)
        {
            if (started)
            {
                Debug.Assert(processId is not null);
                AddDotNetProcessStartedEvent(processId.Value);
            }
            else
            {
                AddDotNetProcessStartFailedEvent();
            }
        }

        public void AddRunAppHostStartedEvent() => AddEvent(Events.RunAppHostStarted);

        public void AddStartAppHostBackchannelConnectedEvent() => AddEvent(Events.StartAppHostBackchannelConnected);

        public void SetAppHostBackchannelConnected(bool connected) => SetTag(Tags.AppHostBackchannelConnected, connected);

        public void SetAppHostBuildSuccess(bool buildSuccess) => SetTag(Tags.AppHostBuildSuccess, buildSuccess);

        public void SetAppHostBuildExitCode(int exitCode)
        {
            SetProcessExitCode(exitCode);
            if (exitCode != 0)
            {
                SetError($"Build exited with code {exitCode}.");
            }
        }

        public void SetAppHostCompatibility(bool isCompatible, bool supportsBackchannel, string? aspireHostingVersion)
        {
            SetTag(Tags.AppHostIsCompatible, isCompatible);
            SetTag(Tags.AppHostSupportsBackchannel, supportsBackchannel);
            SetTag(Tags.AppHostAspireHostingVersion, aspireHostingVersion);
        }

        public void SetAppHostDashboardUrls(DashboardUrlsState? dashboardUrls)
        {
            SetTag(Tags.AppHostDashboardHealthy, dashboardUrls?.DashboardHealthy);
            SetTag(Tags.AppHostDashboardHasUrl, !string.IsNullOrEmpty(dashboardUrls?.BaseUrlWithLoginToken));
            SetTag(Tags.AppHostDashboardHasCodespacesUrl, !string.IsNullOrEmpty(dashboardUrls?.CodespacesUrlWithLoginToken));
        }

        public void SetAppHostDashboardHealthy(bool? healthy) => SetTag(Tags.AppHostDashboardHealthy, healthy);

        public void SetAppHostServerImplementation(string implementationName) => SetTag(Tags.AppHostServerImplementation, implementationName);

        public void SetAppHostExtensionHasBuildCapability(bool hasCapability) => SetTag(Tags.AppHostExtensionHasBuildCapability, hasCapability);

        public void SetAppHostExtensionHost(bool extensionHost) => SetTag(Tags.AppHostExtensionHost, extensionHost);

        public void SetAppHostLanguage(string? languageId) => SetTag(Tags.AppHostLanguage, languageId);

        public void SetAppHostNoBuild(bool noBuild) => SetTag(Tags.AppHostNoBuild, noBuild);

        public void SetAppHostNoRestore(bool noRestore) => SetTag(Tags.AppHostNoRestore, noRestore);

        public void SetAppHostProjectFileSpecified(bool specified) => SetTag(Tags.AppHostProjectFileSpecified, specified);

        public void SetAppHostRunningInstanceResult(object? result) => SetTag(Tags.AppHostRunningInstanceResult, result?.ToString());

        public void SetAppHostWatch(bool watch) => SetTag(Tags.AppHostWatch, watch);

        public void SetAppHostWaitForDebugger(bool waitForDebugger) => SetTag(Tags.AppHostWaitForDebugger, waitForDebugger);

        public void SetBackchannelAutoReconnect(bool autoReconnect) => SetTag(Tags.BackchannelAutoReconnect, autoReconnect);

        public void SetBackchannelCapabilitySummary(string[] capabilities, string baselineCapability)
        {
            SetTag(Tags.BackchannelCapabilityCount, capabilities.Length);
            SetTag(Tags.BackchannelHasBaselineCapability, capabilities.Any(capability => capability == baselineCapability));
        }

        public void SetBackchannelExpectedHash(string expectedHash) => SetTag(Tags.BackchannelExpectedHash, expectedHash);

        public void SetBackchannelHasLegacyHash(bool hasLegacyHash) => SetTag(Tags.BackchannelHasLegacyHash, hasLegacyHash);

        public void SetBackchannelRetryCount(int retryCount) => SetTag(Tags.BackchannelRetryCount, retryCount);

        public void SetBackchannelScanCount(int scanCount) => SetTag(Tags.BackchannelScanCount, scanCount);

        public void SetBackchannelSocketFile(string socketPath) => SetTag(Tags.BackchannelSocketFile, Path.GetFileName(socketPath));

        public void SetChildCommand(string command) => SetTag(Tags.ChildCommand, command);

        public void SetDevCertificateEnvironmentVariables(int count) => SetTag(Tags.DevCertificateEnvironmentVariableCount, count);

        public void SetDotNetArgsCount(int argsCount) => SetTag(Tags.DotNetArgsCount, argsCount);

        public void SetDotNetBinlogPath(string binlogPath)
        {
            SetTag(Tags.DotNetBinlogEnabled, true);
            SetTag(Tags.DotNetBinlogPath, binlogPath);
            SetTag(Tags.DotNetBinlogArtifactType, Values.MsBuildBinlog);
        }

        public void SetDotNetBinlogSkippedUnsupportedCommand()
        {
            SetTag(Tags.DotNetBinlogEnabled, false);
            SetTag(Tags.DotNetBinlogSkipReason, Values.UnsupportedDotNetCommand);
        }

        public void SetDotNetInvocation(string dotnetCommand, FileInfo? projectFile, DirectoryInfo workingDirectory, ProcessInvocationOptions options)
        {
            SetTag(Tags.DotNetCommand, dotnetCommand);
            SetTag(Tags.DotNetProjectFile, projectFile?.FullName);
            SetTag(Tags.DotNetWorkingDirectory, workingDirectory.FullName);
            SetTag(Tags.DotNetNoLaunchProfile, options.NoLaunchProfile);
            SetTag(Tags.DotNetStartDebugSession, options.StartDebugSession);
            SetTag(Tags.DotNetDebug, options.Debug);
        }

        public void SetGuestInvocation(string languageId, string displayName, string command, int argsCount, DirectoryInfo workingDirectory)
        {
            SetTag(Tags.GuestRuntimeLanguage, languageId);
            SetTag(Tags.GuestRuntimeDisplayName, displayName);
            SetTag(Tags.GuestCommand, command);
            SetTag(Tags.GuestWorkingDirectory, workingDirectory.FullName);
            SetProcessExecutableName(Path.GetFileName(command));
            SetProcessCommandArgsCount(argsCount);
        }

        public void SetNpmInvocation(string command, int argsCount, string workingDirectory)
        {
            SetTag(Tags.NpmCommand, command);
            SetTag(Tags.NpmWorkingDirectory, workingDirectory);
            SetProcessExecutableName(Path.GetFileName(command));
            SetProcessCommandArgsCount(argsCount);
        }

        public void SetDotNetMsBuildServer(string? msBuildServer) => SetTag(Tags.DotNetMsBuildServer, msBuildServer);

        public void SetDotNetResolvedExecutable(string dotnetPath, string? msBuildServer)
        {
            SetProcessExecutableName(Path.GetFileName(dotnetPath));
            SetDotNetMsBuildServer(msBuildServer);
        }

        public void SetDotNetCompleted(int exitCode, int stdoutLineCount, int stderrLineCount)
        {
            SetProcessExitCode(exitCode);
            SetDotNetOutputLineCounts(stdoutLineCount, stderrLineCount);
            AddDotNetProcessExitedEvent();
        }

        public void SetDotNetOutputLineCounts(int stdoutLineCount, int stderrLineCount)
        {
            SetTag(Tags.DotNetStdoutLines, stdoutLineCount);
            SetTag(Tags.DotNetStderrLines, stderrLineCount);
        }

        public void SetError(string description) => activity?.SetStatus(ActivityStatusCode.Error, description);

        public void SetProcessCommandArgsCount(int argsCount) => SetTag(Tags.ProcessCommandArgsCount, argsCount);

        public void SetProcessExecutableName(string? executableName) => SetTag(TelemetryConstants.Tags.ProcessExecutableName, executableName);

        public void SetProcessExitCode(int exitCode) => SetTag(TelemetryConstants.Tags.ProcessExitCode, exitCode);

        public void SetProcessId(int processId) => SetTag(TelemetryConstants.Tags.ProcessPid, processId);

        public void Dispose()
        {
            if (ownsActivity)
            {
                activity?.Dispose();
            }
        }

        private void AddEvent(string name) => activity?.AddEvent(new ActivityEvent(name));

        private void SetTag(string key, object? value) => activity?.SetTag(key, value);
    }
}
