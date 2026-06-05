// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Certificates;
using Aspire.Cli.Configuration;
using Aspire.Cli.Diagnostics;
using Aspire.Cli.DotNet;
using Aspire.Cli.Interaction;
using Aspire.Cli.Packaging;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Aspire.Shared.UserSecrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Semver;
using Spectre.Console;

namespace Aspire.Cli.Projects;

/// <summary>
/// Handler for guest (non-.NET) AppHost projects.
/// Supports any language registered via <see cref="ILanguageDiscovery"/>.
/// </summary>
internal sealed class GuestAppHostProject : IAppHostProject, IGuestAppHostSdkGenerator
{
    private const string TypeScriptAppHostFileName = "apphost.ts";
    private const string TypeScriptMtsAppHostFileName = "apphost.mts";

    private readonly IInteractionService _interactionService;
    private readonly IAppHostCliBackchannel _backchannel;
    private readonly IAppHostServerProjectFactory _appHostServerProjectFactory;
    private readonly ICertificateService _certificateService;
    private readonly IDotNetCliRunner _runner;
    private readonly IPackagingService _packagingService;
    private readonly IConfiguration _configuration;
    private readonly IFeatures _features;
    private readonly ILanguageDiscovery _languageDiscovery;
    private readonly CliExecutionContext _executionContext;
    private readonly ILogger<GuestAppHostProject> _logger;
    private readonly FileLoggerProvider _fileLoggerProvider;
    private readonly TimeProvider _timeProvider;
    private readonly RunningInstanceManager _runningInstanceManager;
    private readonly ProfilingTelemetry _profilingTelemetry;

    // Language is always resolved via constructor
    private readonly LanguageInfo _resolvedLanguage;
    private GuestRuntime? _guestRuntime;

    public GuestAppHostProject(
        LanguageInfo language,
        IInteractionService interactionService,
        IAppHostCliBackchannel backchannel,
        IAppHostServerProjectFactory appHostServerProjectFactory,
        ICertificateService certificateService,
        IDotNetCliRunner runner,
        IPackagingService packagingService,
        IConfiguration configuration,
        IFeatures features,
        ILanguageDiscovery languageDiscovery,
        CliExecutionContext executionContext,
        ILogger<GuestAppHostProject> logger,
        FileLoggerProvider fileLoggerProvider,
        ProfilingTelemetry profilingTelemetry,
        TimeProvider? timeProvider = null)
    {
        _resolvedLanguage = language;
        _interactionService = interactionService;
        _backchannel = backchannel;
        _appHostServerProjectFactory = appHostServerProjectFactory;
        _certificateService = certificateService;
        _runner = runner;
        _packagingService = packagingService;
        _configuration = configuration;
        _features = features;
        _languageDiscovery = languageDiscovery;
        _executionContext = executionContext;
        _logger = logger;
        _fileLoggerProvider = fileLoggerProvider;
        _profilingTelemetry = profilingTelemetry;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _runningInstanceManager = new RunningInstanceManager(_logger, _interactionService, _timeProvider);
    }

    // ═══════════════════════════════════════════════════════════════
    // IDENTITY (Always resolved via constructor)
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public bool IsUnsupported { get; set; }

    /// <inheritdoc />
    public string LanguageId => _resolvedLanguage.LanguageId;

    /// <inheritdoc />
    public string DisplayName => _resolvedLanguage.DisplayName;

    /// <summary>
    /// Gets the effective SDK version from configuration (inherits from parent directories)
    /// or falls back to the default SDK version.
    /// </summary>
    private string GetEffectiveSdkVersion()
    {
        // IConfiguration merges settings from parent directories and global settings.
        // Prefer the new nested sdk:version key and fall back to the legacy sdkVersion key.
        var configuredVersion = _configuration["sdk:version"] ?? _configuration["sdkVersion"];
        if (!string.IsNullOrEmpty(configuredVersion))
        {
            _logger.LogDebug("Using SDK version from configuration: {Version}", configuredVersion);
            return configuredVersion;
        }

        _logger.LogDebug("Using default SDK version: {Version}", DotNetBasedAppHostServerProject.DefaultSdkVersion);
        return DotNetBasedAppHostServerProject.DefaultSdkVersion;
    }

    // ═══════════════════════════════════════════════════════════════
    // DETECTION
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public Task<string[]> GetDetectionPatternsAsync(CancellationToken cancellationToken = default)
    {
        // Return the detection patterns for this specific language
        return Task.FromResult(_resolvedLanguage.DetectionPatterns);
    }

    /// <inheritdoc />
    public bool CanHandle(FileInfo appHostFile)
    {
        // Check if file matches this language's detection patterns
        return _resolvedLanguage.DetectionPatterns.Any(p =>
            appHostFile.Name.Equals(p, StringComparison.OrdinalIgnoreCase));
    }

    // ═══════════════════════════════════════════════════════════════
    // CREATION
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public string? AppHostFileName => _resolvedLanguage.AppHostFileName;

    /// <inheritdoc />
    public bool IsUsingProjectReferences(FileInfo appHostFile)
    {
        return AspireRepositoryDetector.DetectRepositoryRoot(appHostFile.Directory?.FullName) is not null;
    }

    /// <summary>
    /// Gets all integration references including the code generation package for the current language.
    /// </summary>
    private async Task<List<IntegrationReference>> GetIntegrationReferencesAsync(
        AspireConfigFile config,
        DirectoryInfo directory,
        CancellationToken cancellationToken)
    {
        var defaultSdkVersion = GetEffectiveSdkVersion();
        var integrations = config.GetIntegrationReferences(defaultSdkVersion, directory.FullName).ToList();
        var codeGenPackage = await _languageDiscovery.GetPackageForLanguageAsync(_resolvedLanguage.LanguageId, cancellationToken);
        if (codeGenPackage is not null)
        {
            var codeGenVersion = config.GetEffectiveSdkVersion(defaultSdkVersion);
            integrations.Add(IntegrationReference.FromPackage(codeGenPackage, codeGenVersion));
        }
        return integrations;
    }

    /// <summary>
    /// Resolves the directory containing the nearest aspire.config.json (or legacy settings file)
    /// by searching upward from <paramref name="appHostDirectory"/>.
    /// Falls back to <paramref name="appHostDirectory"/> when no config file is found.
    /// </summary>
    private static DirectoryInfo GetConfigDirectory(DirectoryInfo appHostDirectory)
        => ConfigurationHelper.GetConfigRootDirectory(appHostDirectory);

    private AspireConfigFile LoadConfiguration(DirectoryInfo directory)
    {
        var configDir = GetConfigDirectory(directory);
        try
        {
            var config = AspireConfigFile.LoadOrCreate(configDir.FullName, GetEffectiveSdkVersion());
            _logger.LogInformation("Loaded config from {Directory} (file exists: {Exists})", configDir.FullName, AspireConfigFile.Exists(configDir.FullName));
            return config;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to load configuration from {Directory}", configDir.FullName);
            throw;
        }
    }

    private static void SaveConfiguration(AspireConfigFile config, DirectoryInfo directory)
    {
        var configDir = GetConfigDirectory(directory);
        config.Save(configDir.FullName);
    }

    private string GetPrepareSdkVersion(AspireConfigFile config)
    {
        return config.GetEffectiveSdkVersion(GetEffectiveSdkVersion());
    }

    /// <inheritdoc />
    public Task<string?> GetAspireHostingVersionAsync(FileInfo appHostFile, CancellationToken cancellationToken)
    {
        var defaultSdkVersion = GetEffectiveSdkVersion();

        // Version inspection is read-only. Load an existing config from the same
        // inherited config root used by guest AppHost operations, but do not call
        // LoadOrCreate because merely checking the version must not write config files.
        var config = appHostFile.Directory is { } directory
            ? AspireConfigFile.Load(GetConfigDirectory(directory).FullName)
            : null;

        return Task.FromResult<string?>(config?.GetEffectiveSdkVersion(defaultSdkVersion) ?? defaultSdkVersion);
    }

    /// <summary>
    /// Prepares the AppHost server (creates files and builds for dev mode, restores packages for prebuilt mode).
    /// </summary>
    private static async Task<(bool Success, OutputCollector? Output, string? ChannelName, bool NeedsCodeGen)> PrepareAppHostServerAsync(
        IAppHostServerProject appHostServerProject,
        string sdkVersion,
        List<IntegrationReference> integrations,
        string? requestedChannel,
        string? packageSourceOverride = null,
        CancellationToken cancellationToken = default)
    {
        var result = await appHostServerProject.PrepareAsync(sdkVersion, integrations, requestedChannel, packageSourceOverride, cancellationToken);
        return (result.Success, result.Output, result.ChannelName, result.NeedsCodeGeneration);
    }

    /// <summary>
    /// Builds the AppHost server project and generates SDK code.
    /// </summary>
    /// <returns><see langword="true"/> if the code was generated successfully; otherwise, <see langword="false"/>.</returns>
    internal async Task<bool> BuildAndGenerateSdkAsync(DirectoryInfo directory, string? packageSourceOverride = null, CancellationToken cancellationToken = default)
    {
        var config = LoadConfiguration(directory);
        return await BuildAndGenerateSdkAsync(directory, config, packageSourceOverride, cancellationToken);
    }

    private async Task<bool> BuildAndGenerateSdkAsync(DirectoryInfo directory, AspireConfigFile config, string? packageSourceOverride = null, CancellationToken cancellationToken = default)
    {
        var appHostServerProject = await _appHostServerProjectFactory.CreateAsync(directory.FullName, cancellationToken);

        // Step 1: Use the supplied config as the source of truth. Update uses an
        // in-memory config here so a failed generation does not leave
        // aspire.config.json pinned to versions the current CLI cannot run.
        var integrations = await GetIntegrationReferencesAsync(config, directory, cancellationToken);
        var sdkVersion = GetPrepareSdkVersion(config);

        var (buildSuccess, buildOutput, _, _) = await PrepareAppHostServerAsync(appHostServerProject, sdkVersion, integrations, config.Channel, packageSourceOverride, cancellationToken);
        if (!buildSuccess)
        {
            if (buildOutput is not null)
            {
                _interactionService.DisplayLines(buildOutput.GetLines());
            }
            _interactionService.DisplayError("Failed to prepare AppHost server.");
            return false;
        }

        // Step 2: Start the AppHost server temporarily for code generation
        await using var serverSession = AppHostServerSession.Start(
            appHostServerProject,
            environmentVariables: null,
            debug: false,
            _logger,
            _profilingTelemetry);

        // Step 3: Connect to server
        var rpcClient = await serverSession.GetRpcClientAsync(cancellationToken);

        // Step 4: Generate SDK code via RPC
        // This must happen before dependency installation because the generated
        // code directory (.aspire/modules) may not exist yet and dependency files reference it.
        await GenerateCodeViaRpcAsync(
            directory.FullName,
            appHostFile: null,
            rpcClient,
            integrations,
            cancellationToken);

        // Step 5: Install dependencies using GuestRuntime (best effort - don't block code generation)
        await InstallDependenciesAsync(directory, rpcClient, treatMissingJavaScriptToolAsWarning: true, cancellationToken: cancellationToken);

        return true;
    }

    Task<bool> IGuestAppHostSdkGenerator.BuildAndGenerateSdkAsync(DirectoryInfo directory, string? packageSourceOverride, CancellationToken cancellationToken)
    {
        return BuildAndGenerateSdkAsync(directory, packageSourceOverride, cancellationToken);
    }

    // ═══════════════════════════════════════════════════════════════
    // EXECUTION
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public Task<AppHostValidationResult> ValidateAppHostAsync(FileInfo appHostFile, CancellationToken cancellationToken)
    {
        if (IsUnsupported)
        {
            return Task.FromResult(new AppHostValidationResult(IsValid: false, IsUnsupported: true));
        }

        // Check if the file exists
        if (!appHostFile.Exists)
        {
            _logger.LogDebug("AppHost file {File} does not exist", appHostFile.FullName);
            return Task.FromResult(new AppHostValidationResult(IsValid: false));
        }

        // Use the resolved language's detection patterns (set in constructor)
        var patterns = _resolvedLanguage.DetectionPatterns;
        if (!patterns.Any(p => appHostFile.Name.Equals(p, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogDebug("AppHost file {File} does not match {Language} detection patterns: {Patterns}",
                appHostFile.Name, _resolvedLanguage.DisplayName, string.Join(", ", patterns));
            return Task.FromResult(new AppHostValidationResult(IsValid: false));
        }

        // Guest languages don't have the "possibly unbuildable" concept
        // Detailed validation is delegated to the server-side language support
        _logger.LogDebug("Validated {Language} AppHost: {File}", _resolvedLanguage.DisplayName, appHostFile.FullName);
        return Task.FromResult(new AppHostValidationResult(IsValid: true));
    }

    /// <inheritdoc />
    public async Task<int> RunAsync(AppHostProjectContext context, CancellationToken cancellationToken)
    {
        var appHostFile = context.AppHostFile;
        var directory = appHostFile.Directory!;

        _logger.LogDebug("Running {Language} AppHost: {AppHostFile}", DisplayName, appHostFile.FullName);
        var startProjectContext = Activity.Current?.Context ?? default;

        try
        {
            // Step 1: Ensure certificates are trusted
            Dictionary<string, string> certEnvVars;
            try
            {
                var certResult = await _certificateService.EnsureCertificatesTrustedAsync(cancellationToken);
                certEnvVars = new Dictionary<string, string>(certResult.EnvironmentVariables);
            }
            catch
            {
                context.BuildCompletionSource?.TrySetResult(false);
                throw;
            }

            // Build phase: build AppHost server (dependency install happens after server starts)
            var appHostServerProject = await _appHostServerProjectFactory.CreateAsync(directory.FullName, cancellationToken);

            // Load config - source of truth for SDK version and packages
            var config = LoadConfiguration(directory);
            var integrations = await GetIntegrationReferencesAsync(config, directory, cancellationToken);
            var sdkVersion = GetPrepareSdkVersion(config);

            var buildResult = await _interactionService.ShowStatusAsync(
                "Preparing Aspire server...",
                async () =>
                {
                    // Prepare the AppHost server (build for dev mode, restore for prebuilt)
                    var (prepareSuccess, prepareOutput, channelName, needsCodeGen) = await PrepareAppHostServerAsync(appHostServerProject, sdkVersion, integrations, config.Channel, cancellationToken: cancellationToken);
                    if (!prepareSuccess)
                    {
                        return (Success: false, Output: prepareOutput, Error: "Failed to prepare app host.", ChannelName: (string?)null, NeedsCodeGen: false);
                    }

                    return (Success: true, Output: prepareOutput, Error: (string?)null, ChannelName: channelName, NeedsCodeGen: needsCodeGen);
                }, emoji: KnownEmojis.Gear);

            if (!buildResult.Success)
            {
                // Set OutputCollector so RunCommand can display errors
                context.OutputCollector = buildResult.Output;
                context.BuildCompletionSource?.TrySetResult(false);
                return CliExitCodes.FailedToBuildArtifacts;
            }

            // Store output collector in context for exception handling by RunCommand
            // This must be set BEFORE signaling build completion to avoid a race condition
            context.OutputCollector = buildResult.Output;

            // Signal that build/preparation is complete
            context.BuildCompletionSource?.TrySetResult(true);

            // Read launch settings once and reuse them for both the temporary server and guest AppHost.
            var launchProfileEnvironmentVariables = ReadLaunchSettingsEnvironmentVariables(directory);
            var launchSettingsEnvVars = GetServerEnvironmentVariables(
                launchProfileEnvironmentVariables,
                defaultEnvironment: AppHostEnvironmentDefaults.DevelopmentEnvironmentName,
                args: context.UnmatchedTokens);

            // Apply certificate environment variables (e.g., SSL_CERT_DIR on Linux)
            foreach (var kvp in certEnvVars)
            {
                launchSettingsEnvVars[kvp.Key] = kvp.Value;
            }

            // Generate a backchannel socket path for CLI to connect to AppHost server
            var backchannelSocketPath = GetBackchannelSocketPath();

            // Pass the backchannel socket path to AppHost server so it opens a server for CLI communication
            launchSettingsEnvVars[KnownConfigNames.UnixSocketPath] = backchannelSocketPath;

            // Pass synthetic UserSecretsId so AppHost Server can read secrets set via 'aspire secret'
            launchSettingsEnvVars[KnownConfigNames.AspireUserSecretsId] = UserSecretsPathHelper.ComputeSyntheticUserSecretsId(appHostFile.FullName);

            // Check if hot reload (watch mode) is enabled
            var enableHotReload = _features.IsFeatureEnabled(KnownFeatures.DefaultWatchEnabled, defaultValue: false);

            // Start the AppHost server process
            AppHostServerSession serverSession;
            IAppHostRpcClient rpcClient;
            using (_profilingTelemetry.StartRunAppHostStartAppHostServer())
            {
                serverSession = AppHostServerSession.Start(
                    appHostServerProject,
                    launchSettingsEnvVars,
                    context.Debug,
                    _logger,
                    _profilingTelemetry);
                try
                {
                    // Give the server a moment to start
                    await Task.Delay(500, cancellationToken);

                    if (serverSession.ServerProcess.HasExited)
                    {
                        _interactionService.DisplayLines(serverSession.Output.GetLines());
                        _interactionService.DisplayError("App host exited unexpectedly.");
                        await serverSession.DisposeAsync();
                        return CliExitCodes.FailedToDotnetRunAppHost;
                    }

                    // Step 5: Connect to server for RPC calls
                    rpcClient = await serverSession.GetRpcClientAsync(cancellationToken);

                    // Step 6: Generate SDK code via RPC if needed
                    // This must happen before dependency installation because the generated
                    // code directory (.aspire/modules) may not exist yet (e.g., freshly cloned project)
                    // and dependency files (pylock.toml, requirements.txt) reference it.
                    if (buildResult.NeedsCodeGen)
                    {
                        await GenerateCodeViaRpcAsync(
                            directory.FullName,
                            appHostFile,
                            rpcClient,
                            integrations,
                            cancellationToken);
                    }

                    await EnsureRuntimeCreatedAsync(directory, rpcClient, cancellationToken);
                }
                catch
                {
                    // Once Start() succeeds we own the server process, so dispose it here when
                    // post-start work fails - the `await using` below isn't in scope yet.
                    await serverSession.DisposeAsync();
                    throw;
                }
            }
            await using var serverSessionScope = serverSession;
            var socketPath = serverSession.SocketPath;
            var appHostServerProcess = serverSession.ServerProcess;
            var appHostServerOutputCollector = serverSession.Output;
            var authenticationToken = serverSession.AuthenticationToken;

            // The backchannel completion source is the contract with RunCommand
            // We signal this when the backchannel is ready, RunCommand uses it for UX
            var backchannelCompletionSource = context.BackchannelCompletionSource ?? new TaskCompletionSource<IAppHostCliBackchannel>();

            // Internal escalation CTS for the AppHost system. We cancel this when something fatal
            // happens to either the server or the guest (e.g. backchannel polling fails after the
            // 60s timeout, the server exits unexpectedly) so the remaining process gets torn down
            // promptly. Without this, a hung guest can keep pendingRun alive forever after the CLI
            // has already given up on the backchannel, causing aspire run/start to hang instead of
            // surfacing the failure. Linked to the outer cancellationToken so user Ctrl+C still
            // propagates.
            using var appHostSystemCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var appHostSystemToken = appHostSystemCts.Token;

            // When the backchannel polling task gives up (timeout, server process exit, or other
            // fatal connection error), escalate to tearing down the whole AppHost system. The
            // BackchannelCompletionSource only signals readiness/connectivity - it never causes the
            // server or guest to be killed on its own, so we wire that here.
            _ = backchannelCompletionSource.Task.ContinueWith(
                t =>
                {
                    if (t.IsFaulted && !appHostSystemCts.IsCancellationRequested)
                    {
                        try
                        {
                            appHostSystemCts.Cancel();
                        }
                        catch (ObjectDisposedException)
                        {
                            // RunAsync already returned and disposed the CTS; nothing to do.
                        }
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            int guestExitCode;
            OutputCollector? guestOutput;
            IGuestProcessLauncher? launcher = null;
            using (var guestStartupActivity = _profilingTelemetry.StartRunAppHostStartGuestAppHost(_resolvedLanguage.LanguageId))
            {
                // Step 7: Install dependencies (using GuestRuntime)
                // The GuestRuntime will skip if the RuntimeSpec doesn't have InstallDependencies configured
                var installResult = await InstallDependenciesAsync(directory, rpcClient, treatMissingJavaScriptToolAsWarning: false, cancellationToken: cancellationToken);
                if (installResult != 0)
                {
                    context.BackchannelCompletionSource?.TrySetException(
                        new InvalidOperationException($"Failed to install {DisplayName} dependencies."));

                    if (!appHostServerProcess.HasExited)
                    {
                        try
                        {
                            appHostServerProcess.Kill(entireProcessTree: true);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Error killing AppHost server process after dependency install failure");
                        }
                    }

                    return installResult;
                }

                // Step 8: Execute the guest apphost

                // Pass the launch profile and certificate environment variables through to the guest AppHost
                // so it sees the same dashboard and resource service endpoints as the temporary .NET server.
                var environmentVariables = CreateGuestEnvironmentVariables(
                    context.EnvironmentVariables,
                    launchProfileEnvironmentVariables,
                    certEnvVars,
                    defaultEnvironment: AppHostEnvironmentDefaults.DevelopmentEnvironmentName,
                    args: context.UnmatchedTokens);
                environmentVariables["REMOTE_APP_HOST_SOCKET_PATH"] = socketPath;
                environmentVariables["ASPIRE_PROJECT_DIRECTORY"] = directory.FullName;
                environmentVariables["ASPIRE_APPHOST_FILEPATH"] = appHostFile.FullName;
                environmentVariables[KnownConfigNames.RemoteAppHostToken] = authenticationToken;

                // Pass debug flag to the guest process
                if (context.Debug)
                {
                    environmentVariables["ASPIRE_DEBUG"] = "true";
                }

                // Check if the extension should launch the guest app host (for VS Code debugging).
                // This mirrors the pattern in DotNetCliRunner.ExecuteAsync for .NET app hosts.
                // The RuntimeSpec declares the required extension capability (e.g., "node" for TypeScript);
                // only use the extension launcher when the runtime requests it and the extension supports it.
                if (_guestRuntime is null)
                {
                    _interactionService.DisplayError("GuestRuntime not initialized.");
                    return CliExitCodes.FailedToDotnetRunAppHost;
                }

                if (_guestRuntime.ExtensionLaunchCapability is { } requiredCapability
                    && ExtensionHelper.IsExtensionHost(_interactionService, out var extensionInteractionService, out var extensionBackchannel)
                    && await extensionBackchannel.HasCapabilityAsync(requiredCapability, cancellationToken))
                {
                    launcher = new ExtensionGuestLauncher(extensionInteractionService, appHostFile, context.StartDebugSession);
                }
                else
                {
                    launcher = _guestRuntime.CreateDefaultLauncher();
                }

                // Start guest apphost - it will connect to AppHost server, define resources.
                // If launcher is an ExtensionGuestLauncher, it delegates to the VS Code extension.
                Task StartBackchannelConnectionAfterGuestAppHostLaunchesAsync()
                {
                    // Guest runtimes can fail during dependency installation or pre-execute checks before
                    // the AppHost is invoked. Defer polling the server backchannel until the launcher has
                    // started or delegated the AppHost so those early failures don't leave the CLI waiting
                    // on an unused stream.
                    //
                    // Use the AppHost system token so that a guest-side failure (which faults the
                    // backchannel completion source and cancels appHostSystemCts) stops the polling loop
                    // promptly.
                    _ = StartBackchannelConnectionAsync(appHostServerProcess, backchannelSocketPath, backchannelCompletionSource, enableHotReload, startProjectContext, appHostSystemToken);
                    return Task.CompletedTask;
                }

                // Pass appHostSystemToken so a fatal backchannel failure (or user cancellation, since
                // appHostSystemCts is linked to cancellationToken) tears down the guest process. The
                // launcher will kill the guest's process tree when this token cancels.
                (guestExitCode, guestOutput) = await ExecuteGuestAppHostAsync(
                    appHostFile, directory, environmentVariables, enableHotReload, rpcClient, launcher, StartBackchannelConnectionAfterGuestAppHostLaunchesAsync, appHostSystemToken);
            }

            // If the user cancelled (Ctrl+C), surface that as cancellation instead of a "guest failed"
            // run. ProcessGuestLauncher swallows the OperationCanceledException internally so the
            // process exit code can flow back, so we re-derive cancellation from the outer token here.
            if (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            // A non-zero exit code at this point means either:
            //  - The in-CLI portion of the guest run (e.g. a TypeScript PreExecute `tsc --noEmit` step)
            //    failed before the actual AppHost was launched.
            //  - The guest AppHost itself failed (syntax error, unhandled exception, etc).
            //  - The AppHost system escalation killed the guest because the server backchannel never
            //    came up (appHostSystemCts was cancelled by a backchannel failure). In this case the
            //    guest's own output may be empty - the relevant diagnostics are in the server output
            //    (e.g. DCP model validation errors).
            // Surface the failure regardless of launcher type, otherwise the extension flow would
            // silently hang in appHostServerProcess.WaitForExitAsync waiting for an apphost that was
            // never started.
            if (guestExitCode != 0)
            {
                _logger.LogError("{Language} apphost exited with code {ExitCode}", DisplayName, guestExitCode);

                // Merge any captured AppHost server output into context.OutputCollector so RunCommand's
                // post-failure UX (DisplayRecentAppHostStartupOutput) surfaces it. This is especially
                // important when the run failed because of a server-side issue like a DCP model
                // validation error that the user would otherwise never see - the guest is just hung
                // waiting on the RPC at that point and produces no output of its own.
                MergeServerOutputIntoContextCollector(context, appHostServerOutputCollector);

                // Surface the captured output (e.g. tsc errors from a TypeScript PreExecute step)
                // so the user can see why the apphost failed. In the extension flow,
                // ExtensionInteractionService.DisplayLines routes these lines through the
                // backchannel without also writing them to the CLI's captured stdout/stderr.
                if (guestOutput is not null)
                {
                    _interactionService.DisplayLines(guestOutput.GetLines());
                }

                // Signal failure to RunCommand so it doesn't hang waiting for the backchannel.
                // RunCommand's startup catch path wraps the message with the localized
                // InteractionServiceStrings.UnexpectedErrorOccurred template before surfacing
                // it to the user, matching the pre-PR behavior where this exception fell
                // through to RunCommand's generic exception handler.
                var error = new InvalidOperationException($"The {DisplayName} apphost failed.");
                context.BackchannelCompletionSource?.TrySetException(error);

                // Kill the AppHost server since the apphost failed
                if (!appHostServerProcess.HasExited)
                {
                    try
                    {
                        appHostServerProcess.Kill(entireProcessTree: true);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Error killing AppHost server process after {Language} failure", DisplayName);
                    }
                }

                return guestExitCode;
            }

            if (launcher is ExtensionGuestLauncher)
            {
                // Extension manages the guest app host lifecycle via VS Code debug session.
                // Wait for the AppHost server to exit (Ctrl+C or extension termination).
                await appHostServerProcess.WaitForExitAsync(cancellationToken);
                return appHostServerProcess.ExitCode;
            }

            // In watch mode, wait for server to exit (Ctrl+C or orphan detection)
            // In non-watch mode, kill the server now that the apphost has exited
            if (!enableHotReload && !appHostServerProcess.HasExited)
            {
                try
                {
                    appHostServerProcess.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error killing AppHost server process");
                }
            }

            await appHostServerProcess.WaitForExitAsync(cancellationToken);

            return appHostServerProcess.ExitCode;
        }
        catch (OperationCanceledException)
        {
            // Signal that build/preparation failed so RunCommand doesn't hang waiting
            context.BuildCompletionSource?.TrySetResult(false);
            return CliExitCodes.Cancelled;
        }
        catch (AppHostCodeGenerationException ex)
        {
            // We already rendered an actionable, tiered diagnostic in GenerateCodeViaRpcAsync.
            // Avoid double-printing here — just log and return the standard failure exit code.
            context.BuildCompletionSource?.TrySetResult(false);
            _logger.LogError(ex, "Code generation failed for {Language} AppHost", DisplayName);
            return CliExitCodes.FailedToDotnetRunAppHost;
        }
        catch (Exception ex)
        {
            // Signal that build/preparation failed so RunCommand doesn't hang waiting
            context.BuildCompletionSource?.TrySetResult(false);
            _logger.LogError(ex, "Failed to run {Language} AppHost", DisplayName);
            _interactionService.DisplayError($"Failed to run {DisplayName} AppHost: {ex.Message}");
            return CliExitCodes.FailedToDotnetRunAppHost;
        }
    }

    internal Dictionary<string, string> GetServerEnvironmentVariables(
        DirectoryInfo directory,
        string? defaultEnvironment = AppHostEnvironmentDefaults.DevelopmentEnvironmentName,
        bool includeLaunchProfileEnvironmentVariables = true,
        string[]? args = null)
    {
        return GetServerEnvironmentVariables(
            ReadLaunchSettingsEnvironmentVariables(directory),
            defaultEnvironment,
            includeLaunchProfileEnvironmentVariables,
            args: args);
    }

    internal static Dictionary<string, string> GetServerEnvironmentVariables(
        IDictionary<string, string>? launchProfileEnvironmentVariables,
        string? defaultEnvironment = AppHostEnvironmentDefaults.DevelopmentEnvironmentName,
        bool includeLaunchProfileEnvironmentVariables = true,
        IReadOnlyDictionary<string, string?>? inheritedEnvironmentVariables = null,
        string[]? args = null)
    {
        var envVars = new Dictionary<string, string>();
        MergeLaunchProfileEnvironmentVariables(launchProfileEnvironmentVariables, envVars, includeLaunchProfileEnvironmentVariables);
        AppHostEnvironmentDefaults.ApplyEffectiveEnvironment(envVars, defaultEnvironment, inheritedEnvironmentVariables, args);
        return envVars;
    }

    internal Dictionary<string, string> CreateGuestEnvironmentVariables(
        DirectoryInfo directory,
        IDictionary<string, string> contextEnvironmentVariables,
        IDictionary<string, string>? additionalEnvironmentVariables = null,
        string? defaultEnvironment = null,
        bool includeLaunchProfileEnvironmentVariables = true,
        string[]? args = null)
    {
        return CreateGuestEnvironmentVariables(
            contextEnvironmentVariables,
            ReadLaunchSettingsEnvironmentVariables(directory),
            additionalEnvironmentVariables,
            defaultEnvironment,
            includeLaunchProfileEnvironmentVariables,
            args: args);
    }

    internal static Dictionary<string, string> CreateGuestEnvironmentVariables(
        IDictionary<string, string> contextEnvironmentVariables,
        IDictionary<string, string>? launchProfileEnvironmentVariables,
        IDictionary<string, string>? additionalEnvironmentVariables = null,
        string? defaultEnvironment = null,
        bool includeLaunchProfileEnvironmentVariables = true,
        IReadOnlyDictionary<string, string?>? inheritedEnvironmentVariables = null,
        string[]? args = null)
    {
        var environmentVariables = new Dictionary<string, string>(contextEnvironmentVariables);

        MergeLaunchProfileEnvironmentVariables(
            launchProfileEnvironmentVariables,
            environmentVariables,
            includeLaunchProfileEnvironmentVariables);

        if (additionalEnvironmentVariables is not null)
        {
            foreach (var (key, value) in additionalEnvironmentVariables)
            {
                environmentVariables[key] = value;
            }
        }

        AppHostEnvironmentDefaults.ApplyEffectiveEnvironment(environmentVariables, defaultEnvironment, inheritedEnvironmentVariables, args);

        return environmentVariables;
    }

    private static void MergeLaunchProfileEnvironmentVariables(
        IDictionary<string, string>? launchProfileEnvironmentVariables,
        IDictionary<string, string> environmentVariables,
        bool includeLaunchProfileEnvironmentVariables = true)
    {
        if (launchProfileEnvironmentVariables is not null)
        {
            foreach (var (key, value) in launchProfileEnvironmentVariables)
            {
                if (!includeLaunchProfileEnvironmentVariables && AppHostEnvironmentDefaults.IsEnvironmentVariableName(key))
                {
                    continue;
                }

                environmentVariables[key] = value;
            }
        }
    }

    private Dictionary<string, string>? ReadLaunchSettingsEnvironmentVariables(DirectoryInfo directory)
    {
        // Check aspire.config.json first for launch profiles (may be in a parent directory)
        var configDir = GetConfigDirectory(directory);
        try
        {
            var aspireConfig = AspireConfigFile.Load(configDir.FullName);
            if (aspireConfig?.Profiles is { Count: > 0 })
            {
                return ReadProfileFromAspireConfig(aspireConfig);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to load config for launch profiles from {Directory}", configDir.FullName);
        }

        // Fall back to apphost.run.json / launchSettings.json
        var apphostRunPath = Path.Combine(directory.FullName, "apphost.run.json");
        var launchSettingsPath = Path.Combine(directory.FullName, "Properties", "launchSettings.json");

        var configPath = File.Exists(apphostRunPath) ? apphostRunPath : launchSettingsPath;

        if (!File.Exists(configPath))
        {
            _logger.LogDebug("No aspire.config.json, apphost.run.json, or launchSettings.json found in {Path}", directory.FullName);
            return null;
        }

        try
        {
            _logger.LogDebug("Reading launch settings from {ConfigPath}", configPath);
            var json = File.ReadAllText(configPath);
            using var doc = JsonDocument.Parse(json, ConfigurationHelper.ParseOptions);

            if (!doc.RootElement.TryGetProperty("profiles", out var profiles))
            {
                return null;
            }

            // Try to find the 'https' profile first, then fall back to the first profile
            JsonElement? profileElement = null;
            if (profiles.TryGetProperty("https", out var httpsProfile))
            {
                profileElement = httpsProfile;
            }
            else
            {
                // Use the first profile
                using var enumerator = profiles.EnumerateObject();
                if (enumerator.MoveNext())
                {
                    profileElement = enumerator.Current.Value;
                }
            }

            if (profileElement == null)
            {
                return null;
            }

            var result = new Dictionary<string, string>();

            // Read applicationUrl and convert to ASPNETCORE_URLS
            if (profileElement.Value.TryGetProperty("applicationUrl", out var appUrl) &&
                appUrl.ValueKind == JsonValueKind.String)
            {
                result["ASPNETCORE_URLS"] = appUrl.GetString()!;
            }

            // Read environment variables
            if (profileElement.Value.TryGetProperty("environmentVariables", out var envVars))
            {
                foreach (var prop in envVars.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        result[prop.Name] = prop.Value.GetString()!;
                    }
                }
            }

            if (result.Count == 0)
            {
                return null;
            }

            _logger.LogDebug("Read {Count} environment variables from {ConfigPath}", result.Count, configPath);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read {ConfigPath}", configPath);
            return null;
        }
    }

    private Dictionary<string, string>? ReadProfileFromAspireConfig(AspireConfigFile aspireConfig)
    {
        AspireConfigProfile? profile;

        // Prefer 'https' profile, then fall back to first
        if (aspireConfig.Profiles!.TryGetValue("https", out var httpsProfile))
        {
            profile = httpsProfile;
        }
        else
        {
            profile = aspireConfig.Profiles.Values.FirstOrDefault();
        }

        if (profile is null)
        {
            return null;
        }

        var result = new Dictionary<string, string>();

        if (!string.IsNullOrEmpty(profile.ApplicationUrl))
        {
            result["ASPNETCORE_URLS"] = profile.ApplicationUrl;
        }

        if (profile.EnvironmentVariables is not null)
        {
            foreach (var kvp in profile.EnvironmentVariables)
            {
                result[kvp.Key] = kvp.Value;
            }
        }

        if (result.Count == 0)
        {
            return null;
        }

        _logger.LogDebug("Read {Count} environment variables from aspire.config.json", result.Count);
        return result;
    }

    /// <inheritdoc />
    public async Task<int> PublishAsync(PublishContext context, CancellationToken cancellationToken)
    {
        var appHostFile = context.AppHostFile;
        var directory = appHostFile.Directory!;

        _logger.LogDebug("Publishing guest AppHost: {AppHostFile}", appHostFile.FullName);
        var startProjectContext = Activity.Current?.Context ?? default;

        try
        {
            // Step 1: Load config - source of truth for SDK version and packages
            var appHostServerProject = await _appHostServerProjectFactory.CreateAsync(directory.FullName, cancellationToken);
            var config = LoadConfiguration(directory);
            var integrations = await GetIntegrationReferencesAsync(config, directory, cancellationToken);
            var sdkVersion = GetPrepareSdkVersion(config);

            // Prepare the AppHost server (build for dev mode, restore for prebuilt)
            var (prepareSuccess, prepareOutput, _, needsCodeGen) = await PrepareAppHostServerAsync(appHostServerProject, sdkVersion, integrations, config.Channel, cancellationToken: cancellationToken);
            if (!prepareSuccess)
            {
                // Set OutputCollector so PipelineCommandBase can display errors
                context.OutputCollector = prepareOutput;
                // Signal the backchannel completion source so the caller doesn't wait forever
                context.BackchannelCompletionSource?.TrySetException(
                    new InvalidOperationException("The app host preparation failed."));
                return CliExitCodes.FailedToBuildArtifacts;
            }

            // Store output collector in context for exception handling
            context.OutputCollector = prepareOutput;

            // Read launch settings once and reuse them for both the temporary server and guest AppHost.
            var launchProfileEnvironmentVariables = ReadLaunchSettingsEnvironmentVariables(directory);
            var launchSettingsEnvVars = GetServerEnvironmentVariables(
                launchProfileEnvironmentVariables,
                defaultEnvironment: AppHostEnvironmentDefaults.ProductionEnvironmentName,
                includeLaunchProfileEnvironmentVariables: false,
                args: context.Arguments);

            // Generate a backchannel socket path for CLI to connect to AppHost server
            var backchannelSocketPath = GetBackchannelSocketPath();

            // Pass the backchannel socket path to AppHost server so it opens a server
            launchSettingsEnvVars[KnownConfigNames.UnixSocketPath] = backchannelSocketPath;

            // Pass synthetic UserSecretsId so AppHost Server can read secrets set via 'aspire secret'
            launchSettingsEnvVars[KnownConfigNames.AspireUserSecretsId] = UserSecretsPathHelper.ComputeSyntheticUserSecretsId(appHostFile.FullName);

            // Step 2: Start the AppHost server process(it opens the backchannel for progress reporting)
            AppHostServerSession serverSession;
            IAppHostRpcClient rpcClient;
            using (_profilingTelemetry.StartRunAppHostStartAppHostServer())
            {
                serverSession = AppHostServerSession.Start(
                    appHostServerProject,
                    launchSettingsEnvVars,
                    context.Debug,
                    _logger,
                    _profilingTelemetry);

                // Start connecting to the backchannel (fire-and-forget) so the caller is unblocked
                // as soon as the server is reachable; the post-start work below races alongside it.
                if (context.BackchannelCompletionSource is not null)
                {
                    _ = StartBackchannelConnectionAsync(serverSession.ServerProcess, backchannelSocketPath, context.BackchannelCompletionSource, enableHotReload: false, startProjectContext, cancellationToken);
                }

                try
                {
                    // Give the server a moment to start
                    await Task.Delay(500, cancellationToken);

                    if (serverSession.ServerProcess.HasExited)
                    {
                        _interactionService.DisplayLines(serverSession.Output.GetLines());
                        _interactionService.DisplayError("App host exited unexpectedly.");
                        await serverSession.DisposeAsync();
                        return CliExitCodes.FailedToDotnetRunAppHost;
                    }

                    // Step 3: Connect to server for RPC calls
                    rpcClient = await serverSession.GetRpcClientAsync(cancellationToken);

                    // Step 4: Generate code via RPC if needed
                    // This must happen before dependency installation because the generated
                    // code directory (.aspire/modules) may not exist yet (e.g., freshly cloned project)
                    // and dependency files (pylock.toml, requirements.txt) reference it.
                    if (needsCodeGen)
                    {
                        await GenerateCodeViaRpcAsync(
                            directory.FullName,
                            appHostFile,
                            rpcClient,
                            integrations,
                            cancellationToken);
                    }

                    await EnsureRuntimeCreatedAsync(directory, rpcClient, cancellationToken);
                }
                catch (Exception ex)
                {
                    // The backchannel connection task was started before code generation
                    // (see StartBackchannelConnectionAsync above); fault it eagerly so the
                    // caller doesn't wait out the connection timeout when generateCode fails.
                    context.BackchannelCompletionSource?.TrySetException(ex);

                    // Once Start() succeeds we own the server process, so dispose it here when
                    // post-start work fails - the `await using` below isn't in scope yet.
                    await serverSession.DisposeAsync();
                    throw;
                }
            }
            await using var serverSessionScope = serverSession;
            var jsonRpcSocketPath = serverSession.SocketPath;
            var appHostServerProcess = serverSession.ServerProcess;
            var appHostServerOutputCollector = serverSession.Output;
            var authenticationToken = serverSession.AuthenticationToken;

            int guestExitCode;
            OutputCollector? guestOutput;
            using (var guestStartupActivity = _profilingTelemetry.StartRunAppHostStartGuestAppHost(_resolvedLanguage.LanguageId))
            {
                // Step 5: Install dependencies if needed (using GuestRuntime)
                // The GuestRuntime will skip if the RuntimeSpec doesn't have InstallDependencies configured
                var installResult = await InstallDependenciesAsync(directory, rpcClient, treatMissingJavaScriptToolAsWarning: false, cancellationToken: cancellationToken);
                if (installResult != 0)
                {
                    context.BackchannelCompletionSource?.TrySetException(
                        new InvalidOperationException($"Failed to install {DisplayName} dependencies."));

                    if (!appHostServerProcess.HasExited)
                    {
                        try
                        {
                            appHostServerProcess.Kill(entireProcessTree: true);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Error killing AppHost server process after dependency install failure");
                        }
                    }

                    return installResult;
                }

                // Pass the launch profile environment variables through to the guest AppHost so publish mode
                // uses the same dashboard and resource service endpoints as the temporary .NET server.
                var environmentVariables = CreateGuestEnvironmentVariables(
                    context.EnvironmentVariables,
                    launchProfileEnvironmentVariables,
                    defaultEnvironment: AppHostEnvironmentDefaults.ProductionEnvironmentName,
                    includeLaunchProfileEnvironmentVariables: false,
                    args: context.Arguments);
                environmentVariables["REMOTE_APP_HOST_SOCKET_PATH"] = jsonRpcSocketPath;
                environmentVariables["ASPIRE_PROJECT_DIRECTORY"] = directory.FullName;
                environmentVariables["ASPIRE_APPHOST_FILEPATH"] = appHostFile.FullName;
                environmentVariables[KnownConfigNames.RemoteAppHostToken] = authenticationToken;

                // Step 6: Execute the guest apphost for publishing
                // Pass the publish arguments (e.g., --operation publish --step deploy)
                (guestExitCode, guestOutput) = await ExecuteGuestAppHostForPublishAsync(
                    appHostFile, directory, environmentVariables, context.Arguments, rpcClient, cancellationToken);
            }

            if (guestExitCode != 0)
            {
                _logger.LogError("{Language} apphost exited with code {ExitCode}", DisplayName, guestExitCode);

                // Display the output (same pattern as DotNetCliRunner)
                if (guestOutput is not null)
                {
                    _interactionService.DisplayLines(guestOutput.GetLines());
                }

                // Signal failure so callers don't hang waiting for the backchannel. The caller
                // (e.g. PipelineCommandBase) wraps the message with the localized
                // InteractionServiceStrings.UnexpectedErrorOccurred template before surfacing
                // it to the user.
                var error = new InvalidOperationException($"The {DisplayName} apphost failed.");
                context.BackchannelCompletionSource?.TrySetException(error);

                // Kill the AppHost server since the apphost failed
                if (!appHostServerProcess.HasExited)
                {
                    try
                    {
                        appHostServerProcess.Kill(entireProcessTree: true);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Error killing AppHost server process after {Language} failure", DisplayName);
                    }
                }

                return guestExitCode;
            }

            // Kill the server after the guest apphost exits
            if (!appHostServerProcess.HasExited)
            {
                try
                {
                    appHostServerProcess.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error killing AppHost server process");
                }
            }

            await appHostServerProcess.WaitForExitAsync(cancellationToken);

            // The guest apphost's publish result determines command success.
            // The helper server may be terminated by the CLI as part of normal cleanup,
            // which can yield a non-zero process exit code on Unix-like systems.
            return guestExitCode;
        }
        catch (OperationCanceledException)
        {
            return CliExitCodes.Cancelled;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish {Language} AppHost", DisplayName);
            _interactionService.DisplayError($"Failed to publish {DisplayName} AppHost: {ex.Message}");
            return CliExitCodes.FailedToDotnetRunAppHost;
        }
    }

    /// <summary>
    /// Gets the backchannel socket path for CLI communication.
    /// </summary>
    private static string GetBackchannelSocketPath()
    {
        return CliPathHelper.CreateUnixDomainSocketPath("cli.sock");
    }

    /// <summary>
    /// Copies the AppHost server's captured output into <see cref="AppHostProjectContext.OutputCollector"/>
    /// so RunCommand's post-failure UX (which only reads from context.OutputCollector) can surface
    /// server-side diagnostics like DCP model validation errors. The build/run collector lives on the
    /// context and is the canonical place for AppHost output; the server's collector is internal to
    /// AppHostServerSession and isn't otherwise visible to commands.
    /// </summary>
    private static void MergeServerOutputIntoContextCollector(AppHostProjectContext context, OutputCollector serverOutput)
    {
        if (context.OutputCollector is not { } target || ReferenceEquals(target, serverOutput))
        {
            return;
        }

        foreach (var (stream, line) in serverOutput.GetLines())
        {
            if (stream == OutputLineStream.StdErr)
            {
                target.AppendError(line);
            }
            else
            {
                target.AppendOutput(line);
            }
        }
    }

    /// <summary>
    /// Starts connecting to the AppHost server's backchannel server.
    /// </summary>
    private async Task StartBackchannelConnectionAsync(
        Process process,
        string socketPath,
        TaskCompletionSource<IAppHostCliBackchannel> backchannelCompletionSource,
        bool enableHotReload,
        ActivityContext parentContext,
        CancellationToken cancellationToken)
    {
        const int ConnectionTimeoutSeconds = 60;

        using var activity = _profilingTelemetry.StartBackchannelConnect(socketPath, parentContext, enableHotReload, retryCount: 0);
        var startTime = DateTimeOffset.UtcNow;
        var connectionAttempts = 0;

        _logger.LogDebug("Starting backchannel connection to AppHost server at {SocketPath}", socketPath);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogTrace("Attempting to connect to AppHost server backchannel at {SocketPath} (attempt {Attempt})", socketPath, connectionAttempts);
                if (connectionAttempts == 0 || connectionAttempts % 10 == 0)
                {
                    activity.AddBackchannelConnectAttemptEvent(connectionAttempts);
                }
                // Pass enableHotReload as autoReconnect - the backchannel will handle reconnection internally
                await _backchannel.ConnectAsync(socketPath, autoReconnect: enableHotReload, retryCount: connectionAttempts, cancellationToken).ConfigureAwait(false);
                activity.SetBackchannelRetryCount(connectionAttempts);
                activity.AddBackchannelConnectedEvent();
                backchannelCompletionSource.TrySetResult(_backchannel);
                _logger.LogDebug("Connected to AppHost server backchannel at {SocketPath}", socketPath);
                return;
            }
            catch (SocketException ex) when (process.HasExited)
            {
                _logger.LogError("AppHost server process has exited with code {ExitCode}. Unable to connect to backchannel at {SocketPath}", process.ExitCode, socketPath);
                var message = process.ExitCode == CliExitCodes.Success
                    ? "AppHost server process has exited"
                    : "AppHost server process has exited unexpectedly";
                var backchannelException = new FailedToConnectBackchannelConnection(message, ex);
                activity.SetError(backchannelException);
                backchannelCompletionSource.TrySetException(backchannelException);
                return;
            }
            catch (SocketException)
            {
                var waitingFor = DateTimeOffset.UtcNow - startTime;

                // Timeout after ConnectionTimeoutSeconds - the AppHost server should have started by now
                if (waitingFor > TimeSpan.FromSeconds(ConnectionTimeoutSeconds))
                {
                    _logger.LogError("Timed out waiting for AppHost server to start after {Timeout} seconds", ConnectionTimeoutSeconds);
                    var timeoutException = new TimeoutException($"Timed out waiting for AppHost server to start after {ConnectionTimeoutSeconds} seconds. Check the debug logs for more details.");
                    activity.SetError(timeoutException);
                    backchannelCompletionSource.TrySetException(timeoutException);
                    return;
                }

                // Slow down polling after 10 seconds
                if (waitingFor > TimeSpan.FromSeconds(10))
                {
                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to AppHost server backchannel");
                activity.SetError(ex);
                backchannelCompletionSource.TrySetException(ex);
                return;
            }
            finally
            {
                connectionAttempts++;
            }
        }
    }

    /// <inheritdoc />
    public async Task<bool> AddPackageAsync(AddPackageContext context, CancellationToken cancellationToken)
    {
        var directory = context.AppHostFile.Directory;
        if (directory is null)
        {
            return false;
        }

        // Load config - source of truth for SDK version and packages
        var config = LoadConfiguration(directory);

        // Update configuration with the new package
        config.AddOrUpdatePackage(context.PackageId, context.PackageVersion);

        // Build and regenerate SDK code with the new package
        var regenerateSuccess = await BuildAndGenerateSdkAsync(directory, config, cancellationToken: cancellationToken);
        if (!regenerateSuccess)
        {
            return false;
        }

        SaveConfiguration(config, directory);
        return true;
    }

    /// <inheritdoc />
    public async Task<UpdatePackagesResult> UpdatePackagesAsync(UpdatePackagesContext context, CancellationToken cancellationToken)
    {
        var directory = context.AppHostFile.Directory;
        if (directory is null)
        {
            return new UpdatePackagesResult { UpdatesApplied = false };
        }

        // Load config - source of truth for SDK version and packages
        var config = LoadConfiguration(directory);

        // Find updates for SDK version and packages
        string? newSdkVersion = null;
        var updates = await _interactionService.ShowStatusAsync(
            UpdateCommandStrings.AnalyzingProjectStatus,
            async () =>
            {
                var packageUpdates = new List<(string PackageId, string CurrentVersion, string NewVersion)>();

                // Check for SDK version update (silently - it's an implementation detail)
                try
                {
                    var latestSdkPackage = await context.Channel.GetLatestGuestAppHostSdkPackageAsync(directory, cancellationToken);

                    if (latestSdkPackage is not null && latestSdkPackage.Version != config.SdkVersion)
                    {
                        newSdkVersion = latestSdkPackage.Version;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to check for SDK version updates");
                }

                // Check for package updates
                if (config.Packages is not null)
                {
                    foreach (var (packageId, currentVersion) in config.Packages)
                    {
                        try
                        {
                            var packages = await context.Channel.GetPackagesAsync(packageId, directory, cancellationToken);
                            var latestPackage = packages
                                .Where(p => SemVersion.TryParse(p.Version, SemVersionStyles.Strict, out _))
                                .OrderByDescending(p => SemVersion.Parse(p.Version, SemVersionStyles.Strict), SemVersion.PrecedenceComparer)
                                .FirstOrDefault();

                            if (latestPackage is not null && latestPackage.Version != currentVersion)
                            {
                                packageUpdates.Add((packageId, currentVersion, latestPackage.Version));
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to check for updates to package {PackageId}", packageId);
                        }
                    }
                }

                return packageUpdates;
            });

        var explicitChannelName = context.Channel.ShouldPersistChannelName() ? context.Channel.Name : null;
        var explicitChannelChanged = explicitChannelName is not null && !string.Equals(config.Channel, explicitChannelName, StringComparisons.CliInputOrOutput);

        if (updates.Count == 0 && newSdkVersion is null)
        {
            if (explicitChannelChanged)
            {
                config.Channel = explicitChannelName;
                SaveConfiguration(config, directory);
            }

            _interactionService.DisplayMessage(KnownEmojis.CheckMarkButton, UpdateCommandStrings.ProjectUpToDateMessage);
            return new UpdatePackagesResult { UpdatesApplied = explicitChannelChanged };
        }

        // Display pending updates
        _interactionService.DisplayEmptyLine();
        if (newSdkVersion is not null)
        {
            _interactionService.DisplayMessage(KnownEmojis.Package, $"[bold yellow]Aspire SDK[/] [bold green]{config.SdkVersion.EscapeMarkup()}[/] to [bold green]{newSdkVersion.EscapeMarkup()}[/]", allowMarkup: true);
        }
        foreach (var (packageId, currentVersion, newVersion) in updates)
        {
            _interactionService.DisplayMessage(KnownEmojis.Package, $"[bold yellow]{packageId.EscapeMarkup()}[/] [bold green]{currentVersion.EscapeMarkup()}[/] to [bold green]{newVersion.EscapeMarkup()}[/]", allowMarkup: true);
        }
        _interactionService.DisplayEmptyLine();

        // Confirm with user
        if (!await _interactionService.PromptConfirmAsync(UpdateCommandStrings.PerformUpdatesPrompt, context.ConfirmBinding, cancellationToken: cancellationToken))
        {
            return new UpdatePackagesResult { UpdatesApplied = false };
        }

        // Apply updates to settings.json
        if (newSdkVersion is not null)
        {
            config.SdkVersion = newSdkVersion;
        }
        // Persist the channel when update resolved a non-stable explicit channel. That can
        // come from --channel, per-project/global config, prompt selection, or the
        // UpdateCommand identity-channel fallback for non-project-reference AppHosts. When
        // the resolved channel is Implicit or stable, leave the project's existing setting
        // untouched rather than pinning the default public-feed behavior.
        if (explicitChannelName is not null)
        {
            config.Channel = explicitChannelName;
        }
        foreach (var (packageId, _, newVersion) in updates)
        {
            config.AddOrUpdatePackage(packageId, newVersion);
        }
        // Rebuild and regenerate SDK code with updated packages
        _interactionService.DisplayEmptyLine();
        var regenerateResult = await _interactionService.ShowStatusAsync(
            UpdateCommandStrings.RegeneratingSdkCode,
            async () =>
            {
                var regenerateSuccess = await BuildAndGenerateSdkAsync(directory, config, cancellationToken: cancellationToken);

                if (!regenerateSuccess)
                {
                    return new UpdatePackagesResult { UpdatesApplied = false };
                }

                return new UpdatePackagesResult { UpdatesApplied = true };
            });

        if (!regenerateResult.UpdatesApplied)
        {
            return regenerateResult;
        }

        SaveConfiguration(config, directory);

        _interactionService.DisplayMessage(KnownEmojis.Package, UpdateCommandStrings.RegeneratedSdkCode);

        _interactionService.DisplayEmptyLine();
        _interactionService.DisplaySuccess(UpdateCommandStrings.UpdateSuccessfulMessage);

        return new UpdatePackagesResult { UpdatesApplied = true };
    }

    /// <inheritdoc />
    public async Task<RunningInstanceResult> FindAndStopRunningInstanceAsync(FileInfo appHostFile, DirectoryInfo homeDirectory, CancellationToken cancellationToken)
    {
        // For guest projects, we use the AppHost server's path to compute the socket path
        // The AppHost server is created in a subdirectory of the guest apphost directory
        var directory = appHostFile.Directory;
        if (directory is null)
        {
            return RunningInstanceResult.NoRunningInstance; // No directory, nothing to check
        }

        var appHostServerProject = await _appHostServerProjectFactory.CreateAsync(directory.FullName, cancellationToken);
        var genericAppHostPath = appHostServerProject.GetInstanceIdentifier();

        // Find matching sockets for this AppHost
        var matchingSockets = AppHostHelper.FindMatchingSockets(genericAppHostPath, homeDirectory.FullName);

        // Check if any socket files exist
        if (matchingSockets.Length == 0)
        {
            return RunningInstanceResult.NoRunningInstance; // No running instance, continue
        }

        // Stop all running instances
        var stopTasks = matchingSockets.Select(socketPath =>
            _runningInstanceManager.StopRunningInstanceAsync(socketPath, cancellationToken));
        var results = await Task.WhenAll(stopTasks);
        return results.All(r => r) ? RunningInstanceResult.InstanceStopped : RunningInstanceResult.StopFailed;
    }

    /// <summary>
    /// Generates SDK code by calling the AppHost server's generateCode RPC method.
    /// </summary>
    private async Task GenerateCodeViaRpcAsync(
        string appPath,
        FileInfo? appHostFile,
        IAppHostRpcClient rpcClient,
        IEnumerable<IntegrationReference> integrations,
        CancellationToken cancellationToken)
    {
        var integrationsList = integrations.ToList();

        // Use CodeGenerator (e.g., "TypeScript") not LanguageId (e.g., "typescript/nodejs")
        // The code generator is registered by its Language property, not the runtime ID
        var codeGenerator = _resolvedLanguage.CodeGenerator;

        WarnIfCliSdkVersionSkew(appPath);

        _logger.LogDebug("Generating {CodeGenerator} code via RPC for {Count} packages", codeGenerator, integrationsList.Count);

        // Use the typed RPC method
        Dictionary<string, string> files;
        try
        {
            files = await rpcClient.GenerateCodeAsync(codeGenerator, cancellationToken);
        }
        catch (AppHostCodeGenerationException ex)
        {
            RenderCodeGenerationFailure(ex);
            throw;
        }

        var outputPath = Path.Combine(appPath, LanguageInfo.GeneratedFolderName);
        // Legacy TypeScript AppHosts (`apphost.ts`) still import generated files from
        // `./.modules/aspire.js`. When that scaffold shape is detected, convert the
        // generated `.mts/.mjs` outputs back to `.ts/.js` AND write them to the legacy
        // `.modules/` folder so the existing import paths resolve.
        if (ShouldEmitLegacyTypeScriptGeneratedFiles(appPath, appHostFile))
        {
            files = ConvertGeneratedFilesForLegacyTypeScriptAppHost(files);
            outputPath = Path.Combine(appPath, LanguageInfo.LegacyGeneratedFolderName);
        }

        // Write generated files to the output directory
        Directory.CreateDirectory(outputPath);

        foreach (var (fileName, content) in files)
        {
            var filePath = Path.Combine(outputPath, fileName);
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            await File.WriteAllTextAsync(filePath, content, cancellationToken);
        }

        // Write generation hash for caching
        SaveGenerationHash(outputPath, integrationsList);

        _logger.LogInformation("Generated {Count} {CodeGenerator} files in {Path}",
            files.Count, codeGenerator, outputPath);
    }

    internal static Dictionary<string, string> ConvertGeneratedFilesForLegacyTypeScriptAppHost(Dictionary<string, string> files)
    {
        var convertedFiles = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (fileName, content) in files)
        {
            var convertedFileName = fileName switch
            {
                "aspire.mts" => "aspire.ts",
                "base.mts" => "base.ts",
                "transport.mts" => "transport.ts",
                _ => fileName
            };

            convertedFiles[convertedFileName] = convertedFileName.EndsWith(".ts", StringComparison.Ordinal)
                ? content
                    .Replace(".mjs", ".js", StringComparison.Ordinal)
                    .Replace("aspire.mts", "aspire.ts", StringComparison.Ordinal)
                    .Replace("base.mts", "base.ts", StringComparison.Ordinal)
                    .Replace("transport.mts", "transport.ts", StringComparison.Ordinal)
                : content;
        }

        return convertedFiles;
    }

    private bool ShouldEmitLegacyTypeScriptGeneratedFiles(string appPath, FileInfo? appHostFile)
    {
        if (!TypeScriptAppHostToolchainResolver.IsTypeScriptLanguage(_resolvedLanguage))
        {
            return false;
        }

        return appHostFile is not null
            ? appHostFile.Name.Equals(TypeScriptAppHostFileName, StringComparison.OrdinalIgnoreCase)
            : File.Exists(Path.Combine(appPath, TypeScriptAppHostFileName)) &&
                !File.Exists(Path.Combine(appPath, TypeScriptMtsAppHostFileName));
    }

    /// <summary>
    /// Emits a single pre-flight warning when the installed CLI version doesn't match the SDK
    /// version pinned in <c>aspire.config.json</c>. This is a best-effort heuristic — we keep it
    /// purely informational and let code-generation try first so that benign skew (e.g. a
    /// daily-build CLI against a stable SDK) doesn't block valid scenarios.
    /// </summary>
    private void WarnIfCliSdkVersionSkew(string appPath)
    {
        try
        {
            var configDir = ConfigurationHelper.GetConfigRootDirectory(new DirectoryInfo(appPath));
            var config = AspireConfigFile.Load(configDir.FullName);
            var configuredSdkVersion = config?.SdkVersion;
            if (string.IsNullOrWhiteSpace(configuredSdkVersion))
            {
                return;
            }

            var cliVersion = VersionHelper.GetDefaultSdkVersion();
            if (!IsKnownIncompatibleSkew(cliVersion, configuredSdkVersion))
            {
                return;
            }

            var message = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                ErrorStrings.CodegenVersionSkewWarning,
                cliVersion,
                configuredSdkVersion);
            _interactionService.DisplayMessage(KnownEmojis.Warning, $"[yellow]{Markup.Escape(message)}[/]", allowMarkup: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to evaluate CLI/SDK version skew prior to code generation.");
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when the supplied CLI and SDK versions look mismatched in a
    /// way that is worth warning about. We deliberately tolerate metadata-only differences
    /// (build suffixes, +commit hashes) and only flag a skew when the parsed major/minor/patch
    /// numbers disagree.
    /// </summary>
    /// <summary>
    /// Returns <see langword="true"/> when the supplied CLI and SDK versions differ in a way that
    /// is known to produce ABI incompatibilities — specifically when they differ in
    /// <see cref="SemVersion.Major"/>, <see cref="SemVersion.Minor"/>, <see cref="SemVersion.Patch"/>,
    /// or in their prerelease identifiers (e.g. <c>13.4.0-preview.1.26218.1</c> vs
    /// <c>13.4.0-preview.1.26227.1</c>, which was the exact reproduction case in
    /// <see href="https://github.com/microsoft/aspire/issues/16709"/>). Build metadata
    /// (everything after <c>+</c>) is ignored per the SemVer spec.
    /// </summary>
    internal static bool IsKnownIncompatibleSkew(string cliVersion, string sdkVersion)
    {
        if (!SemVersion.TryParse(NormalizeVersion(cliVersion), SemVersionStyles.Any, out var cli) ||
            !SemVersion.TryParse(NormalizeVersion(sdkVersion), SemVersionStyles.Any, out var sdk))
        {
            return !string.Equals(cliVersion, sdkVersion, StringComparison.OrdinalIgnoreCase);
        }

        // Compare full precedence, which covers Major/Minor/Patch *and* prerelease identifiers
        // but (per the SemVer spec) ignores build metadata. NormalizeVersion already strips '+'
        // suffixes defensively for parsers that include them in precedence.
        return SemVersion.ComparePrecedence(cli, sdk) != 0;
    }

    internal static string NormalizeVersion(string version)
    {
        var plusIndex = version.IndexOf('+');
        return plusIndex > 0 ? version[..plusIndex] : version;
    }

    /// <summary>
    /// Renders a <see cref="AppHostCodeGenerationException"/> to the user with .NET-specific
    /// details tiered behind <c>--debug</c> so that polyglot AppHost authors aren't confronted
    /// with C#/CLR jargon by default. The full structured payload is always written to the debug
    /// log file via the logger's <c>LogDebug</c> call regardless of mode.
    /// </summary>
    private void RenderCodeGenerationFailure(AppHostCodeGenerationException exception)
    {
        var summary = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            ErrorStrings.CodegenIncompatibleSdkSummary,
            DisplayName);
        _interactionService.DisplayError(summary);

        var hint = exception.Diagnostic.RemediationHint;
        if (!string.IsNullOrWhiteSpace(hint))
        {
            _interactionService.DisplayMessage(KnownEmojis.Information, $"[grey]{Markup.Escape(hint!)}[/]", allowMarkup: true);
        }

        _logger.LogDebug(
            "Code generation failed. OriginalExceptionType={OriginalExceptionType}, TypeName={TypeName}, MemberName={MemberName}, RuntimeAspireHostingVersion={RuntimeVersion}, LoadedAssemblies={LoadedCount}",
            exception.Diagnostic.OriginalExceptionType,
            exception.Diagnostic.TypeName ?? "<none>",
            exception.Diagnostic.MemberName ?? "<none>",
            exception.Diagnostic.RuntimeAspireHostingVersion ?? "<none>",
            exception.Diagnostic.LoadedAssemblies.Count);
        _logger.LogDebug(
            "Code generation diagnostic payload: {DiagnosticPayload}",
            JsonSerializer.Serialize(
                exception.Diagnostic,
                BackchannelJsonSerializerContext.Default.AppHostCodeGenerationDiagnostic));

        if (!_executionContext.DebugMode)
        {
            _interactionService.DisplayMessage(KnownEmojis.Information, $"[grey]{Markup.Escape(ErrorStrings.CodegenDebugHint)}[/]", allowMarkup: true);
            return;
        }

        _interactionService.DisplayMessage(KnownEmojis.Microscope, $"[grey]{Markup.Escape(ErrorStrings.CodegenDebugHeader)}[/]", allowMarkup: true);
        var diagnostic = exception.Diagnostic;
        if (!string.IsNullOrWhiteSpace(diagnostic.OriginalExceptionType))
        {
            _interactionService.DisplayPlainText($"   Exception: {diagnostic.OriginalExceptionType}");
        }
        if (!string.IsNullOrWhiteSpace(diagnostic.TypeName))
        {
            _interactionService.DisplayPlainText($"   Type: {diagnostic.TypeName}");
        }
        if (!string.IsNullOrWhiteSpace(diagnostic.MemberName))
        {
            _interactionService.DisplayPlainText($"   Member: {diagnostic.MemberName}");
        }
        if (!string.IsNullOrWhiteSpace(diagnostic.RuntimeAspireHostingVersion))
        {
            _interactionService.DisplayPlainText($"   Runtime Aspire.Hosting: {diagnostic.RuntimeAspireHostingVersion}");
        }
        foreach (var assembly in diagnostic.LoadedAssemblies)
        {
            var version = assembly.InformationalVersion ?? "<unknown>";
            _interactionService.DisplayPlainText($"   • {assembly.Name} {version}");
        }
    }

    /// <summary>
    /// Saves a hash of the integrations to avoid regenerating code unnecessarily.
    /// When project references are present, the hash is always unique to force regeneration
    /// since project outputs are mutable.
    /// </summary>
    private static void SaveGenerationHash(string generatedPath, List<IntegrationReference> integrations)
    {
        var hashPath = Path.Combine(generatedPath, ".codegen-hash");
        var hash = ComputeIntegrationsHash(integrations);
        File.WriteAllText(hashPath, hash);
    }

    /// <summary>
    /// Computes a hash of the integration list for caching purposes.
    /// If any project references are present, includes a timestamp to force regeneration
    /// since project outputs can change between builds.
    /// </summary>
    private static string ComputeIntegrationsHash(List<IntegrationReference> integrations)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var integration in integrations.OrderBy(p => p.Name))
        {
            sb.Append(integration.Name);
            sb.Append(':');
            sb.Append(integration.Version ?? integration.ProjectPath ?? "");
            sb.Append(';');
        }

        // Project references are mutable — always regenerate when they're present
        if (integrations.Any(i => i.IsProjectReference))
        {
            sb.Append("timestamp:");
            sb.Append(DateTime.UtcNow.Ticks);
        }

        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes);
    }

    // ═══════════════════════════════════════════════════════════════
    // RUNTIME MANAGEMENT
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Ensures the GuestRuntime is created.
    /// </summary>
    private async Task EnsureRuntimeCreatedAsync(
        DirectoryInfo directory,
        IAppHostRpcClient rpcClient,
        CancellationToken cancellationToken)
    {
        if (_guestRuntime is null)
        {
            var runtimeSpec = await rpcClient.GetRuntimeSpecAsync(_resolvedLanguage.LanguageId, cancellationToken);
            if (TypeScriptAppHostToolchainResolver.IsTypeScriptLanguage(_resolvedLanguage))
            {
                var toolchain = TypeScriptAppHostToolchainResolver.Resolve(directory, _logger);
                runtimeSpec = TypeScriptAppHostToolchainResolver.ApplyToRuntimeSpec(runtimeSpec, toolchain);
            }

            _guestRuntime = new GuestRuntime(runtimeSpec, _logger, _fileLoggerProvider, profilingTelemetry: _profilingTelemetry);

            _logger.LogDebug("Created GuestRuntime for {RuntimeDisplayName}: Execute={Command} {Args}",
                runtimeSpec.DisplayName,
                runtimeSpec.Execute.Command,
                string.Join(" ", runtimeSpec.Execute.Args));
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // GUEST RUNTIME HELPERS
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Installs dependencies for the guest AppHost using GuestRuntime.
    /// </summary>
    private async Task<int> InstallDependenciesAsync(
        DirectoryInfo directory,
        IAppHostRpcClient rpcClient,
        bool treatMissingJavaScriptToolAsWarning,
        CancellationToken cancellationToken)
    {
        await EnsureRuntimeCreatedAsync(directory, rpcClient, cancellationToken);

        if (_guestRuntime is null)
        {
            _interactionService.DisplayError("GuestRuntime not initialized. This is a bug.");
            return CliExitCodes.FailedToBuildArtifacts;
        }

        var (initResult, initOutput) = await _guestRuntime.InitializeAsync(directory, cancellationToken);
        if (initResult != 0)
        {
            var lines = initOutput.GetLines().ToArray();
            if (lines.Length > 0)
            {
                _interactionService.DisplayLines(lines);
            }
            else
            {
                _interactionService.DisplayError($"Failed to initialize {_resolvedLanguage?.DisplayName ?? "guest"} environment.");
            }
            return initResult;
        }

        var (result, output) = await _guestRuntime.InstallDependenciesAsync(directory, cancellationToken);
        if (result != 0)
        {
            var lines = output.GetLines().ToArray();
            if (lines.Length > 0)
            {
                _interactionService.DisplayLines(lines);
            }
            else
            {
                _interactionService.DisplayError($"Failed to install {_resolvedLanguage?.DisplayName ?? "guest"} dependencies.");
            }

            if (treatMissingJavaScriptToolAsWarning && MissingJavaScriptToolWarning.IsMatch(lines))
            {
                _interactionService.DisplayMessage(KnownEmojis.Warning, MissingJavaScriptToolWarning.GetMessage(directory, _resolvedLanguage));
                return 0;
            }
        }

        return result;
    }

    /// <summary>
    /// Executes the guest AppHost using GuestRuntime.
    /// </summary>
    private async Task<(int ExitCode, OutputCollector? Output)> ExecuteGuestAppHostAsync(
        FileInfo appHostFile,
        DirectoryInfo directory,
        IDictionary<string, string> environmentVariables,
        bool watchMode,
        IAppHostRpcClient rpcClient,
        IGuestProcessLauncher launcher,
        Func<Task>? afterAppHostLaunchedAsync,
        CancellationToken cancellationToken)
    {
        await EnsureRuntimeCreatedAsync(directory, rpcClient, cancellationToken);

        if (_guestRuntime is null)
        {
            _interactionService.DisplayError("GuestRuntime not initialized. This is a bug.");
            return (CliExitCodes.FailedToDotnetRunAppHost, new OutputCollector());
        }

        return await _guestRuntime.RunAsync(appHostFile, directory, environmentVariables, watchMode, launcher, cancellationToken, afterAppHostLaunchedAsync: afterAppHostLaunchedAsync);
    }

    /// <summary>
    /// Executes the guest AppHost for publishing using GuestRuntime.
    /// </summary>
    private async Task<(int ExitCode, OutputCollector? Output)> ExecuteGuestAppHostForPublishAsync(
        FileInfo appHostFile,
        DirectoryInfo directory,
        IDictionary<string, string> environmentVariables,
        string[]? publishArgs,
        IAppHostRpcClient rpcClient,
        CancellationToken cancellationToken)
    {
        await EnsureRuntimeCreatedAsync(directory, rpcClient, cancellationToken);

        if (_guestRuntime is null)
        {
            _interactionService.DisplayError("GuestRuntime not initialized. This is a bug.");
            return (CliExitCodes.FailedToDotnetRunAppHost, new OutputCollector());
        }

        return await _guestRuntime.PublishAsync(appHostFile, directory, environmentVariables, publishArgs, _guestRuntime.CreateDefaultLauncher(), cancellationToken);
    }

    /// <summary>
    /// Computes a deterministic synthetic UserSecretsId from the AppHost file path.
    /// </summary>
    public Task<string?> GetUserSecretsIdAsync(FileInfo appHostFile, bool autoInit, CancellationToken cancellationToken)
    {
        var id = UserSecretsPathHelper.ComputeSyntheticUserSecretsId(appHostFile.FullName);
        return Task.FromResult<string?>(id);
    }
}
