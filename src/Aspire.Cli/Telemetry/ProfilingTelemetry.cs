// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Backchannel;
using Aspire.Cli.DotNet;
using Aspire.Cli.Projects;
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Telemetry;

/// <summary>
/// Creates profiling-only activities used by CLI diagnostics.
/// </summary>
internal sealed class ProfilingTelemetry(IConfiguration configuration) : IDisposable
{
    public const string ActivitySourceName = "Aspire.Cli.Profiling";

    private readonly ActivitySource _activitySource = new(ActivitySourceName);

    internal ActivitySource ActivitySource => _activitySource;

    /// <summary>
    /// Environment variable names used to propagate profiling state between CLI processes.
    /// </summary>
    internal static class EnvironmentVariables
    {
        public const string Enabled = KnownConfigNames.ProfilingEnabled;
        public const string SessionId = KnownConfigNames.ProfilingSessionId;
        public const string TraceParent = KnownConfigNames.ProfilingTraceParent;
        public const string TraceState = KnownConfigNames.ProfilingTraceState;
    }

    /// <summary>
    /// Activity baggage keys used to keep profiling context flowing through ambient activities.
    /// </summary>
    internal static class Baggage
    {
        public const string SessionId = "aspire.profiling.session_id";
    }

    /// <summary>
    /// Span names for profiling activities. These names describe local diagnostic
    /// work such as CLI orchestration and child-process lifetimes; they are not
    /// exported through customer telemetry.
    /// </summary>
    internal static class Activities
    {
        public const string Command = "aspire/cli/command";
        public const string Process = "aspire/cli/process";
        public const string AddCommand = "aspire/cli/add";
        public const string AddFindAppHost = "aspire/cli/add.find_apphost";
        public const string AddGetConfiguredChannel = "aspire/cli/add.get_configured_channel";
        public const string AddSearchPackages = "aspire/cli/add.search_packages";
        public const string AddSelectPackage = "aspire/cli/add.select_package";
        public const string AddSelectPackagePrompt = "aspire/cli/add.select_package.prompt";
        public const string AddStopExistingInstance = "aspire/cli/add.stop_existing_instance";
        public const string AddPackage = "aspire/cli/add.package";
        public const string RunCommand = "aspire/cli/run";
        public const string LsCommand = "aspire/cli/ls";
        public const string LsFindAppHosts = "aspire/cli/ls.find_apphosts";
        public const string AppHostCandidateDiscovery = "aspire/cli/apphost_candidate_discovery";
        public const string AppHostCandidateGitMatch = "aspire/cli/apphost_candidate_discovery.match_git_files";
        public const string AppHostCandidateFilesystemWalk = "aspire/cli/apphost_candidate_discovery.filesystem_walk";
        public const string RunAppHostFindAppHost = "aspire/cli/run_apphost.find_apphost";
        public const string RunAppHostStopExistingInstance = "aspire/cli/run_apphost.stop_existing_instance";
        public const string RunAppHostStartProject = "aspire/cli/run_apphost.start_project";
        public const string RunAppHostStartAppHostServer = "aspire/cli/run_apphost.start_apphost_server";
        public const string RunAppHostStartGuestAppHost = "aspire/cli/run_apphost.start_guest_apphost";
        public const string RunAppHostWaitForBuild = "aspire/cli/run_apphost.wait_for_build";
        public const string RunAppHostWaitForBackchannel = "aspire/cli/run_apphost.wait_for_backchannel";
        public const string RunAppHostGetDashboardUrls = "aspire/cli/run_apphost.get_dashboard_urls";
        public const string RunAppHostLifetime = "aspire/cli/run_apphost.lifetime";
        public const string StartAppHostWaitForBackchannel = "aspire/cli/start_apphost.wait_for_backchannel";
        public const string StartAppHostGetDashboardUrls = "aspire/cli/start_apphost.get_dashboard_urls";
        public const string BackchannelConnect = "aspire/cli/backchannel.connect";
        public const string BackchannelGetDashboardUrls = "aspire/cli/backchannel.get_dashboard_urls";
        public const string JsonRpcClientCall = "aspire/cli/jsonrpc.client";
        public const string AppHostRun = "aspire/cli/apphost.run";
        public const string AppHostConfigureIsolatedMode = "aspire/cli/apphost.configure_isolated_mode";
        public const string AppHostEnsureDevCertificates = "aspire/cli/apphost.ensure_dev_certificates";
        public const string AppHostBuild = "aspire/cli/apphost.build";
        public const string AppHostCheckCompatibility = "aspire/cli/apphost.check_compatibility";
        public const string AppHostRunDotnetLifetime = "aspire/cli/apphost.run_dotnet.lifetime";
        public const string ProfileCaptureDelay = "aspire/cli/profile.capture_delay";
        public const string StopCommand = "aspire/cli/stop";
        public const string StopAppHost = "aspire/cli/stop_apphost";
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
        public const string AppHostDiscoveryScope = "aspire.cli.apphost.discovery_scope";
        public const string AppHostDiscoverySearchDirectory = "aspire.cli.apphost.discovery.search_directory";
        public const string AppHostDiscoverySource = "aspire.cli.apphost.discovery.source";
        public const string AppHostDiscoveryPatternCount = "aspire.cli.apphost.discovery.patterns.count";
        public const string AppHostDiscoveryIncludedFileCount = "aspire.cli.apphost.discovery.included_files.count";
        public const string AppHostDiscoveryWalkFileCount = "aspire.cli.apphost.discovery.walk.files.count";
        public const string AppHostDiscoveryWalkDirectoryCount = "aspire.cli.apphost.discovery.walk.directories.count";
        public const string AppHostDiscoveryWalkSkippedDirectoryCount = "aspire.cli.apphost.discovery.walk.skipped_directories.count";
        public const string AppHostDiscoverySkipListEnabled = "aspire.cli.apphost.discovery.skip_list_enabled";
        public const string AppHostDiscoveryNuGetCacheExcluded = "aspire.cli.apphost.discovery.nuget_cache_excluded";
        public const string AppHostCandidateCount = "aspire.cli.apphost.candidate_count";
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
        public const string AppHostStopAll = "aspire.cli.apphost.stop_all";
        public const string AppHostStopCount = "aspire.cli.apphost.stop_count";
        public const string ProfileCaptureDelayMilliseconds = "aspire.cli.profile.capture_delay_ms";
        public const string DevCertificateEnvironmentVariableCount = "aspire.cli.dev_cert.env_var_count";
        public const string BackchannelSocketFile = "aspire.cli.backchannel.socket_file";
        public const string BackchannelAutoReconnect = "aspire.cli.backchannel.auto_reconnect";
        public const string BackchannelRetryCount = "aspire.cli.backchannel.retry_count";
        public const string BackchannelExpectedHash = "aspire.cli.backchannel.expected_hash";
        public const string BackchannelHasLegacyHash = "aspire.cli.backchannel.has_legacy_hash";
        public const string BackchannelScanCount = "aspire.cli.backchannel.scan_count";
        public const string BackchannelCapabilityCount = "aspire.cli.backchannel.capability_count";
        public const string BackchannelHasBaselineCapability = "aspire.cli.backchannel.has_baseline_capability";
        public const string JsonRpcConnection = "aspire.cli.jsonrpc.connection";
        public const string JsonRpcMethod = "rpc.method";
        public const string JsonRpcStreaming = "aspire.cli.jsonrpc.streaming";
        public const string JsonRpcStreamItemCount = "aspire.cli.jsonrpc.stream.item_count";
        public const string ChildCommand = "aspire.cli.child.command";
        public const string AppHostServerImplementation = "aspire.cli.apphost_server.implementation";
        public const string GuestRuntimeLanguage = "aspire.cli.guest.language";
        public const string GuestRuntimeDisplayName = "aspire.cli.guest.display_name";
        public const string GuestCommand = "aspire.cli.guest.command";
        public const string GuestCommandPhase = "aspire.cli.guest.command.phase";
        public const string GuestWorkingDirectory = "aspire.cli.guest.working_directory";
        public const string GitCommand = "aspire.cli.git.command";
        public const string GitWorkingDirectory = "aspire.cli.git.working_directory";
        public const string GitStdoutLength = "aspire.cli.git.stdout.length";
        public const string GitStderrLength = "aspire.cli.git.stderr.length";
        public const string NpmCommand = "aspire.cli.npm.command";
        public const string NpmWorkingDirectory = "aspire.cli.npm.working_directory";
        public const string LsIncludeAll = "aspire.cli.ls.include_all";
        public const string LsOutputFormat = "aspire.cli.ls.output_format";
        public const string ProcessCommandArgs = "process.command_args";
        public const string ProcessCommandArgsCount = "process.command_args.count";
        public const string AddIntegrationName = "aspire.cli.add.integration.name";
        public const string AddVersionSpecified = "aspire.cli.add.version_specified";
        public const string AddSourceSpecified = "aspire.cli.add.source_specified";
        public const string AddConfiguredChannel = "aspire.cli.add.configured_channel";
        public const string AddPackageSearchResultCount = "aspire.cli.add.package.search_result_count";
        public const string AddPackageMatchCount = "aspire.cli.add.package.match_count";
        public const string AddPackageMatchKind = "aspire.cli.add.package.match_kind";
        public const string AddPackageId = "aspire.cli.add.package.id";
        public const string AddPackageVersion = "aspire.cli.add.package.version";
        public const string AddPackageChannel = "aspire.cli.add.package.channel";
        public const string AddPackageSuccess = "aspire.cli.add.package.success";
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
        public const string JsonRpcResponseReceived = "aspire/cli/jsonrpc.response_received";
        public const string JsonRpcStreamFirstItem = "aspire/cli/jsonrpc.stream.first_item";
        public const string JsonRpcStreamCompleted = "aspire/cli/jsonrpc.stream.completed";
        public const string GuestProcessResolveStart = "aspire/cli/guest.process_resolve_start";
        public const string GuestProcessResolved = "aspire/cli/guest.process_resolved";
        public const string GuestProcessResolveFailed = "aspire/cli/guest.process_resolve_failed";
        public const string GuestProcessStart = "aspire/cli/guest.process_start";
        public const string GuestProcessStarted = "aspire/cli/guest.process_started";
        public const string GuestProcessExited = "aspire/cli/guest.process_exited";
        public const string GuestFirstStdout = "aspire/cli/guest.first_stdout";
        public const string GuestFirstStderr = "aspire/cli/guest.first_stderr";
        public const string GuestOutputDrainTimeout = "aspire/cli/guest.output_drain_timeout";
    }

    /// <summary>
    /// Common profiling tag values. Values should be stable strings so trace
    /// queries can group by them across CLI versions.
    /// </summary>
    internal static class Values
    {
        public const string UnsupportedDotNetCommand = "unsupported_dotnet_command";
        public const string MsBuildBinlog = "msbuild.binlog";
        public const string AppHostDiscoverySourceNone = "none";
        public const string AppHostDiscoverySourceGit = "git";
        public const string AppHostDiscoverySourceFilesystem = "filesystem";
        public const string GuestCommandPhaseInitialize = "initialize";
        public const string GuestCommandPhaseInstallDependencies = "install_dependencies";
        public const string GuestCommandPhasePreExecute = "pre_execute";
        public const string GuestCommandPhaseExecute = "execute";
        public const string GuestCommandPhaseWatchExecute = "watch_execute";
        public const string GuestCommandPhasePublishExecute = "publish_execute";
        public const string AddPackageMatchKindExact = "exact";
        public const string AddPackageMatchKindFuzzy = "fuzzy";
        public const string AddPackageMatchKindNone = "none";
    }

    public bool IsEnabled => IsProfilingEnabled(configuration);

    public ActivityScope CurrentActivity => IsEnabled ? new(Activity.Current, ownsActivity: false) : default;

    public static bool IsProfilingEnabled(IConfiguration configuration)
    {
        return IsTruthy(configuration[EnvironmentVariables.Enabled]) ||
            IsTruthy(configuration[KnownConfigNames.Legacy.StartupProfilingEnabled]);
    }

    public static void AddCurrentContextToEnvironment(IDictionary<string, string> environment)
    {
        AddActivityContextToEnvironment(Activity.Current, environment);
    }

    public static void AddActivityContextToEnvironment(Activity? activity, IDictionary<string, string> environment)
    {
        if (activity is null)
        {
            return;
        }

        var sessionId = GetProfilingSessionId(activity);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        environment[EnvironmentVariables.Enabled] = "true";
        environment[KnownConfigNames.Legacy.StartupProfilingEnabled] = "true";
        environment[EnvironmentVariables.SessionId] = sessionId;
        environment[KnownConfigNames.Legacy.StartupOperationId] = sessionId;

        if (!string.IsNullOrWhiteSpace(activity.Id))
        {
            environment[EnvironmentVariables.TraceParent] = activity.Id;
            environment[KnownConfigNames.Legacy.StartupTraceParent] = activity.Id;
        }

        if (!string.IsNullOrWhiteSpace(activity.TraceStateString))
        {
            environment[EnvironmentVariables.TraceState] = activity.TraceStateString;
            environment[KnownConfigNames.Legacy.StartupTraceState] = activity.TraceStateString;
        }
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
        // Backchannel connection has two entry points: callers like GuestAppHostProject and
        // DotNetCliRunner that start a parent BackchannelConnect activity with explicit context
        // (the overloads below), and the inner AppHostCliBackchannel.ConnectAsync, which is
        // invoked from inside that parent and also wants to record the connection. To avoid a
        // nested duplicate span when the parent is already current, reuse it (non-owning) and
        // just decorate it with the socket path.
        if (IsCurrentActivity(Activities.BackchannelConnect))
        {
            var currentActivity = CurrentActivity;
            currentActivity.SetBackchannelSocketFile(socketPath);
            return currentActivity;
        }

        var activity = StartActivity(Activities.BackchannelConnect);
        activity.SetBackchannelSocketFile(socketPath);
        return activity;
    }

    internal ActivityScope StartBackchannelConnect(string socketPath, ActivityContext parentContext)
    {
        var activity = StartActivity(Activities.BackchannelConnect, parentContext: parentContext);
        activity.SetBackchannelSocketFile(socketPath);
        return activity;
    }

    internal ActivityScope StartBackchannelConnect(string socketPath, ActivityContext parentContext, bool autoReconnect, int retryCount)
    {
        var activity = StartBackchannelConnect(socketPath, parentContext);
        activity.SetBackchannelAutoReconnect(autoReconnect);
        activity.SetBackchannelRetryCount(retryCount);
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

    internal ActivityScope StartJsonRpcClientCall(string connectionName, string methodName, bool streaming)
    {
        var activity = StartActivity(Activities.JsonRpcClientCall, ActivityKind.Client);
        activity.SetJsonRpcCall(connectionName, methodName, streaming);
        return activity;
    }

    internal ActivityScope StartProfileCaptureDelay(TimeSpan delay)
    {
        var activity = StartActivity(Activities.ProfileCaptureDelay);
        activity.SetProfileCaptureDelay(delay);
        return activity;
    }

    internal ActivityScope StartDetachedGetDashboardUrls()
    {
        return StartActivity(Activities.StartAppHostGetDashboardUrls);
    }

    internal ActivityScope StartDetachedSpawnChild(string executablePath, IReadOnlyList<string> args, string childCommand)
    {
        var activity = StartActivity(Activities.Process, ActivityKind.Client);
        activity.SetProcessInvocation(executablePath, args);
        activity.SetChildCommand(childCommand);
        return activity;
    }

    internal ActivityScope StartDetachedWaitForBackchannel(int childProcessId, string expectedHash, bool hasLegacyHash)
    {
        var activity = StartActivity(Activities.StartAppHostWaitForBackchannel);
        activity.SetProcessId(childProcessId);
        activity.SetBackchannelExpectedHash(expectedHash);
        activity.SetBackchannelHasLegacyHash(hasLegacyHash);
        return activity;
    }

    internal ActivityScope StartDotNetProcess(string dotnetCommand, FileInfo? projectFile, DirectoryInfo workingDirectory, ProcessInvocationOptions options)
    {
        var activity = StartActivity(Activities.Process, ActivityKind.Client);
        activity.SetDotNetInvocation(dotnetCommand, projectFile, workingDirectory, options);
        return activity;
    }

    internal ActivityScope StartAppHostServerLifetime(string implementationName)
    {
        // The server process span stays open for the process lifetime, but it should not become
        // the ambient parent for later CLI operations such as backchannel connection attempts.
        var activity = StartActivity(Activities.Process, ActivityKind.Client, restoreAmbientActivity: true);
        activity.SetAppHostServerImplementation(implementationName);
        return activity;
    }

    internal ActivityScope StartGuestInitializeCommand(string languageId, string displayName, string command, string[] args, DirectoryInfo workingDirectory)
    {
        var activity = StartGuestProcessActivity(languageId, displayName, command, args, workingDirectory, Values.GuestCommandPhaseInitialize);
        return activity;
    }

    internal ActivityScope StartGuestInstallDependencies(string languageId, string displayName, string command, string[] args, DirectoryInfo workingDirectory)
    {
        var activity = StartGuestProcessActivity(languageId, displayName, command, args, workingDirectory, Values.GuestCommandPhaseInstallDependencies);
        return activity;
    }

    internal ActivityScope StartGuestExecuteCommand(string languageId, string displayName, string command, string[] args, DirectoryInfo workingDirectory, string phase)
    {
        var activity = StartGuestProcessActivity(languageId, displayName, command, args, workingDirectory, phase);
        return activity;
    }

    internal ActivityScope StartNpmCommand(string command, IReadOnlyList<string> args, string workingDirectory)
    {
        var activity = StartActivity(Activities.Process, ActivityKind.Client);
        activity.SetNpmInvocation(command, args, workingDirectory);
        return activity;
    }

    internal ActivityScope StartGitCommand(string command, string executablePath, IReadOnlyList<string> args, DirectoryInfo workingDirectory)
    {
        var activity = StartActivity(Activities.Process, ActivityKind.Client);
        activity.SetGitInvocation(command, executablePath, args, workingDirectory);
        return activity;
    }

    internal ActivityScope StartRunAppHostFindAppHost(FileInfo? passedAppHostProjectFile)
    {
        var activity = StartActivity(Activities.RunAppHostFindAppHost);
        activity.SetAppHostProjectFileSpecified(passedAppHostProjectFile is not null);
        return activity;
    }

    internal ActivityScope StartAddCommand(string? integrationName, string? version, string? source, FileInfo? passedAppHostProjectFile)
    {
        var activity = StartActivity(Activities.AddCommand, startWithRemoteParent: true);
        activity.SetAddInvocation(integrationName, version, source, passedAppHostProjectFile);
        return activity;
    }

    internal ActivityScope StartAddFindAppHost(FileInfo? passedAppHostProjectFile)
    {
        var activity = StartActivity(Activities.AddFindAppHost);
        activity.SetAppHostProjectFileSpecified(passedAppHostProjectFile is not null);
        return activity;
    }

    internal ActivityScope StartAddGetConfiguredChannel()
    {
        return StartActivity(Activities.AddGetConfiguredChannel);
    }

    internal ActivityScope StartAddSearchPackages(string? configuredChannel)
    {
        var activity = StartActivity(Activities.AddSearchPackages);
        activity.SetAddConfiguredChannel(configuredChannel);
        return activity;
    }

    internal ActivityScope StartAddSelectPackage(string? integrationName, string? version)
    {
        var activity = StartActivity(Activities.AddSelectPackage);
        activity.SetAddPackageSelectionRequest(integrationName, version);
        return activity;
    }

    internal ActivityScope StartAddSelectPackagePrompt()
    {
        return StartActivity(Activities.AddSelectPackagePrompt);
    }

    internal ActivityScope StartAddStopExistingInstance()
    {
        return StartActivity(Activities.AddStopExistingInstance);
    }

    internal ActivityScope StartAddPackage(string packageId, string packageVersion, string? source)
    {
        var activity = StartActivity(Activities.AddPackage);
        activity.SetAddPackage(packageId, packageVersion);
        activity.SetAddSourceSpecified(!string.IsNullOrEmpty(source));
        return activity;
    }

    internal ActivityScope StartLsCommand(string outputFormat, bool includeAll)
    {
        var activity = StartActivity(Activities.LsCommand);
        activity.SetLsInvocation(outputFormat, includeAll);
        return activity;
    }

    internal ActivityScope StartLsFindAppHosts(string discoveryScope)
    {
        var activity = StartActivity(Activities.LsFindAppHosts);
        activity.SetAppHostDiscoveryScope(discoveryScope);
        return activity;
    }

    internal ActivityScope StartAppHostCandidateDiscovery(DirectoryInfo searchDirectory, AppHostDiscoveryScope scope, int patternsCount, bool nugetCacheExcluded)
    {
        var activity = StartActivity(Activities.AppHostCandidateDiscovery);
        activity.SetAppHostDiscoverySearchDirectory(searchDirectory);
        activity.SetAppHostDiscoveryScope(scope.ToString());
        activity.SetAppHostDiscoveryPatternCount(patternsCount);
        activity.SetAppHostDiscoveryNuGetCacheExcluded(nugetCacheExcluded);
        return activity;
    }

    internal ActivityScope StartAppHostCandidateGitMatch(int includedFileCount, int patternsCount)
    {
        var activity = StartActivity(Activities.AppHostCandidateGitMatch);
        activity.SetAppHostDiscoverySource(Values.AppHostDiscoverySourceGit);
        activity.SetAppHostDiscoveryIncludedFileCount(includedFileCount);
        activity.SetAppHostDiscoveryPatternCount(patternsCount);
        return activity;
    }

    internal ActivityScope StartAppHostCandidateFilesystemWalk(DirectoryInfo searchDirectory, int patternsCount, bool skipListEnabled, bool nugetCacheExcluded)
    {
        var activity = StartActivity(Activities.AppHostCandidateFilesystemWalk);
        activity.SetAppHostDiscoverySearchDirectory(searchDirectory);
        activity.SetAppHostDiscoverySource(Values.AppHostDiscoverySourceFilesystem);
        activity.SetAppHostDiscoveryPatternCount(patternsCount);
        activity.SetAppHostDiscoverySkipListEnabled(skipListEnabled);
        activity.SetAppHostDiscoveryNuGetCacheExcluded(nugetCacheExcluded);
        return activity;
    }

    internal ActivityScope StartRunAppHostGetDashboardUrls()
    {
        return StartActivity(Activities.RunAppHostGetDashboardUrls);
    }

    internal ActivityScope StartRunAppHostLifetime()
    {
        var activity = StartActivity(Activities.RunAppHostLifetime);
        activity.AddRunAppHostStartedEvent();
        return activity;
    }

    internal ActivityScope StartRunAppHostStartProject(string languageId, bool noBuild, bool waitForDebugger)
    {
        var activity = StartActivity(Activities.RunAppHostStartProject);
        activity.SetAppHostLanguage(languageId);
        activity.SetAppHostNoBuild(noBuild);
        activity.SetAppHostWaitForDebugger(waitForDebugger);
        return activity;
    }

    internal ActivityScope StartRunAppHostStartAppHostServer()
    {
        return StartActivity(Activities.RunAppHostStartAppHostServer);
    }

    internal ActivityScope StartRunAppHostStartGuestAppHost(string languageId)
    {
        var activity = StartActivity(Activities.RunAppHostStartGuestAppHost);
        activity.SetAppHostLanguage(languageId);
        return activity;
    }

    internal ActivityScope StartRunAppHostStopExistingInstance()
    {
        return StartActivity(Activities.RunAppHostStopExistingInstance);
    }

    internal ActivityScope StartRunAppHostWaitForBackchannel()
    {
        return StartActivity(Activities.RunAppHostWaitForBackchannel);
    }

    internal ActivityScope StartRunAppHostWaitForBuild()
    {
        return StartActivity(Activities.RunAppHostWaitForBuild);
    }

    internal ActivityScope StartRunCommand()
    {
        return StartActivity(Activities.RunCommand, startWithRemoteParent: true);
    }

    internal ActivityScope StartCommand(string commandName)
    {
        var activity = StartActivity(Activities.Command, startWithRemoteParent: true);
        activity.SetCommandName(commandName);
        return activity;
    }

    internal ActivityScope StartStopCommand(bool stopAll, bool passedAppHostProjectFile)
    {
        var activity = StartActivity(Activities.StopCommand, startWithRemoteParent: true);
        activity.SetAppHostStopAll(stopAll);
        activity.SetAppHostProjectFileSpecified(passedAppHostProjectFile);
        return activity;
    }

    internal ActivityScope StartStopAppHost(AppHostInformation? appHostInfo)
    {
        var activity = StartActivity(Activities.StopAppHost);
        if (appHostInfo is not null)
        {
            activity.SetProcessId(appHostInfo.ProcessId);
        }

        return activity;
    }

    private ActivityScope StartActivity(
        string name,
        ActivityKind kind = ActivityKind.Internal,
        bool startWithRemoteParent = false,
        ActivityContext? parentContext = null,
        bool restoreAmbientActivity = false)
    {
        if (!IsEnabled)
        {
            return default;
        }

        var ambientActivity = Activity.Current;
        Activity? activity;
        if (parentContext is { } explicitParentContext)
        {
            activity = _activitySource.StartActivity(name, kind, explicitParentContext);
        }
        else if (startWithRemoteParent &&
            TryGetConfiguredActivityContext(out var configuredParentContext))
        {
            activity = _activitySource.StartActivity(name, kind, configuredParentContext);
        }
        else
        {
            activity = _activitySource.StartActivity(name, kind);
        }

        AddProfilingSession(activity, ambientActivity);
        // StartActivity makes the new span ambient when it is sampled. For long-lived process
        // spans that are only used for lifetime/export context, immediately put the previous
        // CLI activity back so unrelated follow-up operations do not become children of the
        // process span. Only restore when our activity is still current; otherwise we could
        // clobber a listener or helper that legitimately changed Activity.Current.
        if (restoreAmbientActivity && ReferenceEquals(Activity.Current, activity))
        {
            Activity.Current = ambientActivity;
        }

        return new ActivityScope(activity);
    }

    private void AddProfilingSession(Activity? activity, Activity? ambientActivity)
    {
        if (activity is null)
        {
            return;
        }

        // Profiling spans can be siblings under short-lived reported/diagnostic activities.
        // Seed the ambient ancestor chain with baggage so later profiling siblings reuse the
        // same session after an intermediate parent activity has ended.
        var sessionId = GetProfilingSessionIdFromAncestors(ambientActivity) ?? GetProfilingSessionId(activity) ?? GetConfiguredSessionId() ?? Guid.NewGuid().ToString("N");
        AddProfilingSessionBaggage(ambientActivity, sessionId);

        // Keep profiling tags on profiling spans only. Reported/customer activities only
        // carry the session as baggage so it can flow across async and process boundaries.
        activity.SetBaggage(Baggage.SessionId, sessionId);
        activity.SetTag(Tags.ProfilingSessionId, sessionId);
        activity.SetTag(Tags.LegacyStartupOperationId, sessionId);
    }

    private bool TryGetConfiguredActivityContext(out ActivityContext activityContext)
    {
        var traceParent = GetConfigurationValue(configuration, EnvironmentVariables.TraceParent, KnownConfigNames.Legacy.StartupTraceParent);
        var traceState = GetConfigurationValue(configuration, EnvironmentVariables.TraceState, KnownConfigNames.Legacy.StartupTraceState);
        if (!string.IsNullOrWhiteSpace(traceParent) &&
            ActivityContext.TryParse(traceParent, traceState, out activityContext))
        {
            return true;
        }

        activityContext = default;
        return false;
    }

    private string? GetConfiguredSessionId()
    {
        return GetConfigurationValue(configuration, EnvironmentVariables.SessionId, KnownConfigNames.Legacy.StartupOperationId);
    }

    private static string? GetProfilingSessionId(Activity? activity)
    {
        return activity?.GetBaggageItem(Baggage.SessionId) is { Length: > 0 } sessionId ? sessionId : null;
    }

    private static string? GetProfilingSessionIdFromAncestors(Activity? activity)
    {
        for (var current = activity; current is not null; current = current.Parent)
        {
            if (GetProfilingSessionId(current) is { } sessionId)
            {
                return sessionId;
            }
        }

        return null;
    }

    private static void AddProfilingSessionBaggage(Activity? activity, string sessionId)
    {
        for (var current = activity; current is not null; current = current.Parent)
        {
            if (GetProfilingSessionId(current) is null)
            {
                current.SetBaggage(Baggage.SessionId, sessionId);
            }
        }
    }

    private static string? GetConfigurationValue(IConfiguration configuration, string name, string legacyName)
    {
        return configuration[name] is { Length: > 0 } value ? value : configuration[legacyName];
    }

    private static bool IsTruthy(string? value)
    {
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";
    }

    internal static void SetProcessInvocation(Activity? activity, string executablePath, IReadOnlyList<string> args)
    {
        if (activity is null)
        {
            return;
        }

        var executableName = Path.GetFileName(executablePath);
        if (string.IsNullOrEmpty(executableName))
        {
            executableName = executablePath;
        }

        activity.DisplayName = $"process {executableName}";
        activity.SetTag(TelemetryConstants.Tags.ProcessExecutableName, executableName);
        activity.SetTag(TelemetryConstants.Tags.ProcessExecutablePath, executablePath);
        activity.SetTag(Tags.ProcessCommandArgs, args.ToArray());
        activity.SetTag(Tags.ProcessCommandArgsCount, args.Count);
    }

    private ActivityScope StartGuestProcessActivity(string languageId, string displayName, string command, string[] args, DirectoryInfo workingDirectory, string phase)
    {
        var activity = StartActivity(Activities.Process, ActivityKind.Client);
        activity.SetGuestInvocation(languageId, displayName, command, args, workingDirectory, phase);
        return activity;
    }

    public void Dispose()
    {
        _activitySource.Dispose();
    }

    private static bool IsCurrentActivity(string name)
    {
        var currentActivity = Activity.Current;
        return currentActivity is not null &&
            currentActivity.Source.Name == ActivitySourceName &&
            currentActivity.OperationName == name;
    }

    internal readonly struct ActivityScope(Activity? activity, bool ownsActivity = true) : IDisposable
    {
        public bool IsRunning => activity is not null;

        public void AddAppHostBuildReadyEvent() => AddEvent(Events.AppHostBuildReady);

        public void AddContextToEnvironment(IDictionary<string, string> environment) => AddActivityContextToEnvironment(activity, environment);

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

        public void AddJsonRpcResponseReceivedEvent() => AddEvent(Events.JsonRpcResponseReceived);

        public void AddJsonRpcStreamFirstItemEvent() => AddEvent(Events.JsonRpcStreamFirstItem);

        public void AddJsonRpcStreamCompletedEvent() => AddEvent(Events.JsonRpcStreamCompleted);

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

        public void SetAddConfiguredChannel(string? configuredChannel) => SetTag(Tags.AddConfiguredChannel, configuredChannel);

        public void SetAddInvocation(string? integrationName, string? version, string? source, FileInfo? passedAppHostProjectFile)
        {
            SetTag(Tags.AddIntegrationName, integrationName);
            SetTag(Tags.AddVersionSpecified, !string.IsNullOrEmpty(version));
            SetTag(Tags.AddSourceSpecified, !string.IsNullOrEmpty(source));
            SetAppHostProjectFileSpecified(passedAppHostProjectFile is not null);
        }

        public void SetAddPackage(string packageId, string packageVersion)
        {
            SetTag(Tags.AddPackageId, packageId);
            SetTag(Tags.AddPackageVersion, packageVersion);
        }

        public void SetAddPackageSelectionRequest(string? integrationName, string? version)
        {
            SetTag(Tags.AddIntegrationName, integrationName);
            SetTag(Tags.AddVersionSpecified, !string.IsNullOrEmpty(version));
        }

        public void SetAddPackageMatch(int count, string matchKind)
        {
            SetTag(Tags.AddPackageMatchCount, count);
            SetTag(Tags.AddPackageMatchKind, matchKind);
        }

        public void SetAddPackageSearchResultCount(int count) => SetTag(Tags.AddPackageSearchResultCount, count);

        public void SetAddPackageSuccess(bool success) => SetTag(Tags.AddPackageSuccess, success);

        public void SetAddSourceSpecified(bool sourceSpecified) => SetTag(Tags.AddSourceSpecified, sourceSpecified);

        public void SetAddSelectedPackage(string packageId, string packageVersion, string channelName)
        {
            SetAddPackage(packageId, packageVersion);
            SetTag(Tags.AddPackageChannel, channelName);
        }

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

        public void SetAppHostDiscoveryScope(string discoveryScope) => SetTag(Tags.AppHostDiscoveryScope, discoveryScope);

        public void SetAppHostDiscoverySearchDirectory(DirectoryInfo searchDirectory) => SetTag(Tags.AppHostDiscoverySearchDirectory, searchDirectory.FullName);

        public void SetAppHostDiscoverySource(string source) => SetTag(Tags.AppHostDiscoverySource, source);

        public void SetAppHostDiscoveryPatternCount(int count) => SetTag(Tags.AppHostDiscoveryPatternCount, count);

        public void SetAppHostDiscoveryIncludedFileCount(int count) => SetTag(Tags.AppHostDiscoveryIncludedFileCount, count);

        public void SetAppHostDiscoveryWalkCounts(int files, int directories, int skippedDirectories)
        {
            SetTag(Tags.AppHostDiscoveryWalkFileCount, files);
            SetTag(Tags.AppHostDiscoveryWalkDirectoryCount, directories);
            SetTag(Tags.AppHostDiscoveryWalkSkippedDirectoryCount, skippedDirectories);
        }

        public void SetAppHostDiscoverySkipListEnabled(bool enabled) => SetTag(Tags.AppHostDiscoverySkipListEnabled, enabled);

        public void SetAppHostDiscoveryNuGetCacheExcluded(bool excluded) => SetTag(Tags.AppHostDiscoveryNuGetCacheExcluded, excluded);

        public void SetAppHostCandidateCount(int count) => SetTag(Tags.AppHostCandidateCount, count);

        public void SetAppHostServerImplementation(string implementationName) => SetTag(Tags.AppHostServerImplementation, implementationName);

        public void SetAppHostExtensionHasBuildCapability(bool hasCapability) => SetTag(Tags.AppHostExtensionHasBuildCapability, hasCapability);

        public void SetAppHostExtensionHost(bool extensionHost) => SetTag(Tags.AppHostExtensionHost, extensionHost);

        public void SetAppHostLanguage(string? languageId) => SetTag(Tags.AppHostLanguage, languageId);

        public void SetAppHostNoBuild(bool noBuild) => SetTag(Tags.AppHostNoBuild, noBuild);

        public void SetAppHostNoRestore(bool noRestore) => SetTag(Tags.AppHostNoRestore, noRestore);

        public void SetAppHostProjectFileSpecified(bool specified) => SetTag(Tags.AppHostProjectFileSpecified, specified);

        public void SetAppHostRunningInstanceResult(object? result) => SetTag(Tags.AppHostRunningInstanceResult, result?.ToString());

        public void SetAppHostStopAll(bool stopAll) => SetTag(Tags.AppHostStopAll, stopAll);

        public void SetAppHostStopCount(int count) => SetTag(Tags.AppHostStopCount, count);

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

        public void SetCommandName(string commandName) => SetTag(TelemetryConstants.Tags.CommandName, commandName);

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

        public void SetGuestInvocation(string languageId, string displayName, string command, string[] args, DirectoryInfo workingDirectory, string phase)
        {
            SetTag(Tags.GuestRuntimeLanguage, languageId);
            SetTag(Tags.GuestRuntimeDisplayName, displayName);
            SetTag(Tags.GuestCommand, command);
            SetTag(Tags.GuestCommandPhase, phase);
            SetTag(Tags.GuestWorkingDirectory, workingDirectory.FullName);
            SetProcessInvocation(command, args);
        }

        public void SetGitInvocation(string command, string executablePath, IReadOnlyList<string> args, DirectoryInfo workingDirectory)
        {
            SetTag(Tags.GitCommand, command);
            SetTag(Tags.GitWorkingDirectory, workingDirectory.FullName);
            SetProcessInvocation(executablePath, args);
        }

        public void SetGitOutputLengths(int stdoutLength, int stderrLength)
        {
            SetTag(Tags.GitStdoutLength, stdoutLength);
            SetTag(Tags.GitStderrLength, stderrLength);
        }

        public void SetJsonRpcCall(string connectionName, string methodName, bool streaming)
        {
            SetTag(Tags.JsonRpcConnection, connectionName);
            SetTag(Tags.JsonRpcMethod, methodName);
            SetTag(Tags.JsonRpcStreaming, streaming);
        }

        public void SetJsonRpcStreamItemCount(int count) => SetTag(Tags.JsonRpcStreamItemCount, count);

        public void SetProfileCaptureDelay(TimeSpan delay) => SetTag(Tags.ProfileCaptureDelayMilliseconds, delay.TotalMilliseconds);

        public BackchannelTraceContext? CreateBackchannelTraceContext()
        {
            if (activity is null)
            {
                return null;
            }

            var baggage = new Dictionary<string, string>();
            foreach (var (key, value) in activity.Baggage)
            {
                if (value is not null)
                {
                    baggage[key] = value;
                }
            }

            return new BackchannelTraceContext
            {
                TraceParent = activity.Id,
                TraceState = activity.TraceStateString,
                Baggage = baggage
            };
        }

        public void SetNpmInvocation(string command, IReadOnlyList<string> args, string workingDirectory)
        {
            SetTag(Tags.NpmCommand, command);
            SetTag(Tags.NpmWorkingDirectory, workingDirectory);
            SetProcessInvocation(command, args);
        }

        public void SetLsInvocation(string outputFormat, bool includeAll)
        {
            SetTag(Tags.LsOutputFormat, outputFormat);
            SetTag(Tags.LsIncludeAll, includeAll);
        }

        public void SetDotNetMsBuildServer(string? msBuildServer) => SetTag(Tags.DotNetMsBuildServer, msBuildServer);

        public void SetDotNetResolvedExecutable(string dotnetPath, IReadOnlyList<string> args, string? msBuildServer)
        {
            SetProcessInvocation(dotnetPath, args);
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

        public void SetError(Exception exception) => activity?.SetStatus(ActivityStatusCode.Error, exception.Message);

        public void SetProcessInvocation(string executablePath, IReadOnlyList<string> args)
        {
            ProfilingTelemetry.SetProcessInvocation(activity, executablePath, args);
        }

        public void SetProcessCommandArgsCount(int argsCount) => SetTag(Tags.ProcessCommandArgsCount, argsCount);

        public void SetProcessCommandArgs(IReadOnlyList<string> args) => SetTag(Tags.ProcessCommandArgs, args.ToArray());

        public void SetProcessExecutableName(string? executableName) => SetTag(TelemetryConstants.Tags.ProcessExecutableName, executableName);

        public void SetProcessExecutablePath(string? executablePath) => SetTag(TelemetryConstants.Tags.ProcessExecutablePath, executablePath);

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
