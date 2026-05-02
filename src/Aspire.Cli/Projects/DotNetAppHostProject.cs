// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Backchannel;
using Aspire.Cli.Certificates;
using Aspire.Cli.Configuration;
using Aspire.Cli.DotNet;
using Aspire.Cli.Exceptions;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Aspire.Shared.UserSecrets;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Projects;

/// <summary>
/// Handler for .NET AppHost projects (.csproj and single-file .cs).
/// </summary>
internal sealed class DotNetAppHostProject : IAppHostProject
{
    private readonly IDotNetCliRunner _runner;
    private readonly IInteractionService _interactionService;
    private readonly ICertificateService _certificateService;
    private readonly AspireCliTelemetry _telemetry;
    private readonly IFeatures _features;
    private readonly ILogger<DotNetAppHostProject> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IProjectUpdater _projectUpdater;
    private readonly IDotNetSdkInstaller _sdkInstaller;
    private readonly RunningInstanceManager _runningInstanceManager;
    private readonly Diagnostics.FileLoggerProvider _fileLoggerProvider;
    private readonly Program.CliLoggingOptions _loggingOptions;

    private static readonly string[] s_detectionPatterns = ["*.csproj", "*.fsproj", "*.vbproj", "apphost.cs"];
    internal static IReadOnlyCollection<string> ProjectExtensions { get; } =
        Array.AsReadOnly([".csproj", ".fsproj", ".vbproj"]);

    public DotNetAppHostProject(
        IDotNetCliRunner runner,
        IInteractionService interactionService,
        ICertificateService certificateService,
        AspireCliTelemetry telemetry,
        IFeatures features,
        IProjectUpdater projectUpdater,
        IDotNetSdkInstaller sdkInstaller,
        ILogger<DotNetAppHostProject> logger,
        Diagnostics.FileLoggerProvider fileLoggerProvider,
        Program.CliLoggingOptions loggingOptions,
        TimeProvider? timeProvider = null)
    {
        _runner = runner;
        _interactionService = interactionService;
        _certificateService = certificateService;
        _telemetry = telemetry;
        _features = features;
        _projectUpdater = projectUpdater;
        _sdkInstaller = sdkInstaller;
        _logger = logger;
        _fileLoggerProvider = fileLoggerProvider;
        _loggingOptions = loggingOptions;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _runningInstanceManager = new RunningInstanceManager(_logger, _interactionService, _timeProvider);
    }

    // ═══════════════════════════════════════════════════════════════
    // IDENTITY
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public bool IsUnsupported { get; set; }

    /// <inheritdoc />
    public string LanguageId => KnownLanguageId.CSharp;

    /// <inheritdoc />
    public string DisplayName => "C# (.NET)";

    // ═══════════════════════════════════════════════════════════════
    // DETECTION
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public Task<string[]> GetDetectionPatternsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(s_detectionPatterns);

    /// <inheritdoc />
    public bool CanHandle(FileInfo appHostFile)
    {
        var extension = appHostFile.Extension.ToLowerInvariant();

        // Handle project files (.csproj, .fsproj, .vbproj)
        if (ProjectExtensions.Contains(extension))
        {
            // We can handle any project file - ValidateAsync will do deeper validation
            return true;
        }

        // Handle single-file apphosts (apphost.cs)
        if (extension == ".cs" && appHostFile.Name.Equals("apphost.cs", StringComparison.OrdinalIgnoreCase))
        {
            // Check for #:sdk Aspire.AppHost.Sdk directive
            return IsValidSingleFileAppHost(appHostFile);
        }

        return false;
    }

    private static bool IsValidSingleFileAppHost(FileInfo candidateFile)
    {
        // Check no sibling .csproj files exist
        var siblingCsprojFiles = candidateFile.Directory!.EnumerateFiles("*.csproj", SearchOption.TopDirectoryOnly);
        if (siblingCsprojFiles.Any())
        {
            return false;
        }

        // Check for #:sdk Aspire.AppHost.Sdk directive
        try
        {
            using var reader = candidateFile.OpenText();
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var trimmedLine = line.TrimStart();
                if (trimmedLine.StartsWith("#:sdk Aspire.AppHost.Sdk", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    // ═══════════════════════════════════════════════════════════════
    // CREATION
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public string? AppHostFileName => "apphost.cs";

    /// <inheritdoc />
    public bool IsUsingProjectReferences(FileInfo appHostFile)
    {
        return false;
    }

    // ═══════════════════════════════════════════════════════════════
    // EXECUTION
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<AppHostValidationResult> ValidateAppHostAsync(FileInfo appHostFile, CancellationToken cancellationToken)
    {
        if (IsUnsupported)
        {
            return new AppHostValidationResult(IsValid: false, IsUnsupported: true);
        }

        var isSingleFile = appHostFile.Extension.Equals(".cs", StringComparison.OrdinalIgnoreCase);

        if (isSingleFile)
        {
            // For single-file apphosts, validate that:
            // 1. No sibling .csproj files exist (otherwise it's part of a project)
            // 2. The file contains the #:sdk Aspire.AppHost.Sdk directive
            return new AppHostValidationResult(IsValid: IsValidSingleFileAppHost(appHostFile));
        }

        // For project files, check if it's a valid Aspire AppHost using GetAppHostInformationAsync
        var information = await _runner.GetAppHostInformationAsync(appHostFile, new ProcessInvocationOptions(), cancellationToken);

        if (information.ExitCode == 0 && information.IsAspireHost)
        {
            return new AppHostValidationResult(IsValid: true);
        }

        // Check if it's possibly an unbuildable AppHost (has the right name pattern but couldn't be validated)
        var isPossiblyUnbuildable = IsPossiblyUnbuildableAppHost(appHostFile);

        return new AppHostValidationResult(
            IsValid: false,
            IsPossiblyUnbuildable: isPossiblyUnbuildable);
    }

    private static bool IsPossiblyUnbuildableAppHost(FileInfo projectFile)
    {
        var fileNameSuggestsAppHost = projectFile.Name.EndsWith("AppHost.csproj", StringComparison.OrdinalIgnoreCase);
        var folderContainsAppHostCSharpFile = projectFile.Directory!
            .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
            .Any(f => f.Name.Equals("AppHost.cs", StringComparison.OrdinalIgnoreCase));
        return fileNameSuggestsAppHost || folderContainsAppHostCSharpFile;
    }

    /// <inheritdoc />
    public async Task<int> RunAsync(AppHostProjectContext context, CancellationToken cancellationToken)
    {
        // .NET projects require the SDK to be installed
        if (!await SdkInstallHelper.EnsureSdkInstalledAsync(_sdkInstaller, _interactionService, _telemetry, cancellationToken: cancellationToken))
        {
            // Signal build failure so RunCommand doesn't wait forever
            context.BuildCompletionSource?.TrySetResult(false);
            return ExitCodeConstants.SdkNotInstalled;
        }

        var effectiveAppHostFile = context.AppHostFile;
        var isExtensionHost = ExtensionHelper.IsExtensionHost(_interactionService, out _, out var extensionBackchannel);

        var buildOutputCollector = new OutputCollector(_fileLoggerProvider, "Build");

        (bool IsCompatibleAppHost, bool SupportsBackchannel, string? AspireHostingVersion)? appHostCompatibilityCheck = null;

        using var activity = _telemetry.StartDiagnosticActivity("run");

        var isSingleFileAppHost = effectiveAppHostFile.Extension != ".csproj";

        var env = new Dictionary<string, string>(context.EnvironmentVariables);

        // Handle isolated mode - randomize ports and isolate user secrets
        string? isolatedUserSecretsId = null;
        if (context.Isolated)
        {
            isolatedUserSecretsId = await ConfigureIsolatedModeAsync(effectiveAppHostFile, env, cancellationToken);
            _logger.LogInformation("Aspire run isolated. Isolated UserSecretsId: {IsolatedUserSecretsId}", isolatedUserSecretsId);
        }

        // Enable debug logging in the app host so that debug-level output is
        // captured in the CLI log file for diagnostics. Defaults to Debug but
        // can be overridden via --log-level.
        var appHostLogLevel = _loggingOptions.ConsoleLogLevel ?? LogLevel.Debug;
        env["Logging__LogLevel__Default"] = appHostLogLevel.ToString();

        if (context.WaitForDebugger)
        {
            env[KnownConfigNames.WaitForDebugger] = "true";
        }

        try
        {
            var certResult = await _certificateService.EnsureCertificatesTrustedAsync(cancellationToken);

            // Apply any environment variables returned by the certificate service (e.g., SSL_CERT_DIR on Linux)
            foreach (var kvp in certResult.EnvironmentVariables)
            {
                env[kvp.Key] = kvp.Value;
            }
        }
        catch
        {
            // Signal that build/preparation failed so RunCommand doesn't hang waiting
            context.BuildCompletionSource?.TrySetResult(false);
            throw;
        }

        var watch = !isSingleFileAppHost && (_features.IsFeatureEnabled(KnownFeatures.DefaultWatchEnabled, defaultValue: false) || (isExtensionHost && !context.StartDebugSession));

        try
        {
            if (!context.NoBuild)
            {
                // Build in CLI if either not running under extension host, or the extension reports 'build-dotnet-using-cli' capability.
                var extensionHasBuildCapability = extensionBackchannel is not null && await extensionBackchannel.HasCapabilityAsync(KnownCapabilities.BuildDotnetUsingCli, cancellationToken);
                var shouldBuildInCli = !isExtensionHost || extensionHasBuildCapability;
                if (shouldBuildInCli)
                {
                    var buildOptions = new ProcessInvocationOptions
                    {
                        StandardOutputCallback = buildOutputCollector.AppendOutput,
                        StandardErrorCallback = buildOutputCollector.AppendError,
                    };

                    var buildExitCode = await AppHostHelper.BuildAppHostAsync(_runner, _interactionService, effectiveAppHostFile, context.NoRestore, buildOptions, context.WorkingDirectory, cancellationToken);

                    if (buildExitCode != 0)
                    {
                        // Set OutputCollector so RunCommand can display errors
                        context.OutputCollector = buildOutputCollector;
                        context.BuildCompletionSource?.TrySetResult(false);
                        return ExitCodeConstants.FailedToBuildArtifacts;
                    }
                }
            }

            if (isSingleFileAppHost)
            {
                appHostCompatibilityCheck = (true, true, VersionHelper.GetDefaultTemplateVersion());
            }
            else
            {
                appHostCompatibilityCheck = await AppHostHelper.CheckAppHostCompatibilityAsync(_runner, _interactionService, effectiveAppHostFile, _telemetry, context.WorkingDirectory, _fileLoggerProvider.LogFilePath, cancellationToken);
            }
        }
        catch
        {
            // Signal that build/preparation failed so RunCommand doesn't hang waiting
            context.BuildCompletionSource?.TrySetResult(false);
            throw;
        }

        if (!appHostCompatibilityCheck?.IsCompatibleAppHost ?? throw new InvalidOperationException(RunCommandStrings.IsCompatibleAppHostIsNull))
        {
            context.BuildCompletionSource?.TrySetResult(false);
            return ExitCodeConstants.FailedToDotnetRunAppHost;
        }

        // Create collector and store in context for exception handling
        // This must be set BEFORE signaling build completion to avoid a race condition
        var runOutputCollector = new OutputCollector(_fileLoggerProvider, "AppHost");
        context.OutputCollector = runOutputCollector;

        // Signal that build/preparation is complete
        context.BuildCompletionSource?.TrySetResult(true);

        var runOptions = new ProcessInvocationOptions
        {
            StandardOutputCallback = runOutputCollector.AppendOutput,
            StandardErrorCallback = runOutputCollector.AppendError,
            StartDebugSession = context.StartDebugSession,
            Debug = context.Debug
        };

        // The backchannel completion source is the contract with RunCommand
        // We signal this when the backchannel is ready, RunCommand uses it for UX
        var backchannelCompletionSource = context.BackchannelCompletionSource ?? new TaskCompletionSource<IAppHostCliBackchannel>();

        if (isSingleFileAppHost)
        {
            ConfigureSingleFileRunEnvironment(effectiveAppHostFile, env, args: context.UnmatchedTokens);
        }

        // Start the apphost - the runner will signal the backchannel when ready
        try
        {
            // The app host may already have been built above (in the CLI, or by the extension).
            // For non-watch runs we pass noBuild=true so dotnet run doesn't rebuild. For watch runs
            // we leave builds enabled unless the user explicitly requested --no-build, so dotnet watch
            // can perform its own build/incremental build and hot reload even if an up-front build
            // already happened earlier in this method.
            // noRestore: only relevant when noBuild is false (since --no-build implies --no-restore)
            var noBuild = !watch || context.NoBuild;
            return await _runner.RunAsync(
                effectiveAppHostFile,
                watch,
                noBuild,
                context.NoRestore,
                context.UnmatchedTokens,
                env,
                backchannelCompletionSource,
                runOptions,
                cancellationToken);
        }
        finally
        {
            // Clean up isolated user secrets when the run completes
            if (!string.IsNullOrEmpty(isolatedUserSecretsId))
            {
                IsolatedUserSecretsHelper.CleanupIsolatedUserSecrets(isolatedUserSecretsId);
            }
        }
    }

    internal static void ConfigureSingleFileRunEnvironment(
        FileInfo appHostFile,
        Dictionary<string, string> env,
        IReadOnlyDictionary<string, string?>? inheritedEnvironmentVariables = null,
        string[]? args = null)
    {
        var runJsonFilePath = appHostFile.FullName[..^2] + "run.json";
        if (!File.Exists(runJsonFilePath))
        {
            AppHostEnvironmentDefaults.ApplyEffectiveEnvironment(
                env,
                AppHostEnvironmentDefaults.DevelopmentEnvironmentName,
                inheritedEnvironmentVariables,
                args);
            ApplyDefaultSingleFileEndpoints(env);
        }
    }

    internal static void ConfigureSingleFilePublishEnvironment(
        FileInfo appHostFile,
        Dictionary<string, string> env,
        IReadOnlyDictionary<string, string?>? inheritedEnvironmentVariables = null,
        string[]? args = null)
    {
        if (!TryApplySingleFileLaunchProfileEnvironmentVariables(appHostFile, env))
        {
            ApplyDefaultSingleFileEndpoints(env);
        }

        AppHostEnvironmentDefaults.ApplyEffectiveEnvironment(
            env,
            AppHostEnvironmentDefaults.ProductionEnvironmentName,
            inheritedEnvironmentVariables,
            args);
    }

    private static bool TryApplySingleFileLaunchProfileEnvironmentVariables(
        FileInfo appHostFile,
        Dictionary<string, string> env)
    {
        var profiles = AspireConfigFile.ReadApphostRunProfiles(appHostFile.FullName[..^2] + "run.json");
        AspireConfigProfile? profile;

        if (profiles?.TryGetValue("https", out var httpsProfile) == true)
        {
            profile = httpsProfile;
        }
        else
        {
            profile = profiles?.Values.FirstOrDefault();
        }

        if (profile is null)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(profile.ApplicationUrl))
        {
            env["ASPNETCORE_URLS"] = profile.ApplicationUrl;
        }

        if (profile.EnvironmentVariables is not null)
        {
            foreach (var (key, value) in profile.EnvironmentVariables)
            {
                if (AppHostEnvironmentDefaults.IsEnvironmentVariableName(key))
                {
                    continue;
                }

                env[key] = value;
            }
        }

        return true;
    }

    private static void ApplyDefaultSingleFileEndpoints(IDictionary<string, string> env)
    {
        env["ASPNETCORE_URLS"] = "https://localhost:17193;http://localhost:15069";
        env["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"] = "https://localhost:21293";
        env["ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"] = "https://localhost:22086";
    }

    /// <inheritdoc />
    public async Task<int> PublishAsync(PublishContext context, CancellationToken cancellationToken)
    {
        // .NET projects require the SDK to be installed
        if (!await SdkInstallHelper.EnsureSdkInstalledAsync(_sdkInstaller, _interactionService, _telemetry, cancellationToken: cancellationToken))
        {
            // Throw an exception that will be caught by the command and result in SdkNotInstalled exit code
            // This is cleaner than trying to signal through the backchannel pattern
            throw new DotNetSdkNotInstalledException();
        }

        var effectiveAppHostFile = context.AppHostFile;
        var isSingleFileAppHost = effectiveAppHostFile.Extension != ".csproj" && IsValidSingleFileAppHost(effectiveAppHostFile);
        var env = new Dictionary<string, string>(context.EnvironmentVariables);

        // Check compatibility for project-based apphosts
        if (!isSingleFileAppHost)
        {
            var compatibilityCheck = await AppHostHelper.CheckAppHostCompatibilityAsync(
                _runner,
                _interactionService,
                effectiveAppHostFile,
                _telemetry,
                context.WorkingDirectory,
                _fileLoggerProvider.LogFilePath,
                cancellationToken);

            if (!compatibilityCheck.IsCompatibleAppHost)
            {
                var exception = new AppHostIncompatibleException(
                    $"The app host is not compatible. Aspire.Hosting version: {compatibilityCheck.AspireHostingVersion}",
                    "Aspire.Hosting",
                    compatibilityCheck.AspireHostingVersion);
                // Signal the backchannel completion source so the caller doesn't wait forever
                context.BackchannelCompletionSource?.TrySetException(exception);
                throw exception;
            }

            // Build the apphost (unless --no-build is specified)
            if (!context.NoBuild)
            {
                var buildOutputCollector = new OutputCollector(_fileLoggerProvider, "Build");
                var buildOptions = new ProcessInvocationOptions
                {
                    StandardOutputCallback = buildOutputCollector.AppendOutput,
                    StandardErrorCallback = buildOutputCollector.AppendError,
                };

                var buildExitCode = await AppHostHelper.BuildAppHostAsync(
                    _runner,
                    _interactionService,
                    effectiveAppHostFile,
                    noRestore: false,
                    buildOptions,
                    context.WorkingDirectory,
                    cancellationToken);

                if (buildExitCode != 0)
                {
                    // Set OutputCollector so PipelineCommandBase can display errors
                    context.OutputCollector = buildOutputCollector;
                    // Signal the backchannel completion source so the caller doesn't wait forever
                    context.BackchannelCompletionSource?.TrySetException(
                        new InvalidOperationException("The app host build failed."));
                    return ExitCodeConstants.FailedToBuildArtifacts;
                }
            }
        }

        // Create collector and store in context for exception handling
        var runOutputCollector = new OutputCollector(_fileLoggerProvider, "AppHost");
        context.OutputCollector = runOutputCollector;

        var runOptions = new ProcessInvocationOptions
        {
            StandardOutputCallback = runOutputCollector.AppendOutput,
            StandardErrorCallback = runOutputCollector.AppendError,
            NoLaunchProfile = true,
            StartDebugSession = context.StartDebugSession,
            // When not starting a debug session, prevent DotNetCliRunner from delegating the
            // apphost launch to the extension — pipeline commands should run the apphost directly.
            NoExtensionLaunch = !context.StartDebugSession,
        };

        if (isSingleFileAppHost)
        {
            ConfigureSingleFilePublishEnvironment(effectiveAppHostFile, env, args: context.Arguments);
        }

        return await _runner.RunAsync(
            effectiveAppHostFile,
            watch: false,
            noBuild: true,
            noRestore: false,
            context.Arguments,
            env,
            context.BackchannelCompletionSource,
            runOptions,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> AddPackageAsync(AddPackageContext context, CancellationToken cancellationToken)
    {
        var outputCollector = new OutputCollector(_fileLoggerProvider, "Package");
        context.OutputCollector = outputCollector;

        var options = new ProcessInvocationOptions
        {
            StandardOutputCallback = outputCollector.AppendOutput,
            StandardErrorCallback = outputCollector.AppendError,
        };
        var result = await _runner.AddPackageAsync(
            context.AppHostFile,
            context.PackageId,
            context.PackageVersion,
            context.Source,
            noRestore: false,
            options,
            cancellationToken);

        return result == 0;
    }

    /// <inheritdoc />
    public async Task<UpdatePackagesResult> UpdatePackagesAsync(UpdatePackagesContext context, CancellationToken cancellationToken)
    {
        var result = await _projectUpdater.UpdateProjectAsync(context, cancellationToken);
        return new UpdatePackagesResult { UpdatesApplied = result.UpdatedApplied };
    }

    /// <inheritdoc />
    public async Task<RunningInstanceResult> FindAndStopRunningInstanceAsync(FileInfo appHostFile, DirectoryInfo homeDirectory, CancellationToken cancellationToken)
    {
        var matchingSockets = AppHostHelper.FindMatchingSockets(appHostFile.FullName, homeDirectory.FullName);

        // Check if any socket files exist
        if (matchingSockets.Length == 0)
        {
            return RunningInstanceResult.NoRunningInstance;
        }

        // Stop all running instances
        var stopTasks = matchingSockets.Select(socketPath =>
            _runningInstanceManager.StopRunningInstanceAsync(socketPath, cancellationToken));
        var results = await Task.WhenAll(stopTasks);
        return results.All(r => r) ? RunningInstanceResult.InstanceStopped : RunningInstanceResult.StopFailed;
    }

    /// <summary>
    /// Gets the UserSecretsId from a project file, optionally initializing if not configured.
    /// </summary>
    public async Task<string?> GetUserSecretsIdAsync(FileInfo projectFile, bool autoInit, CancellationToken cancellationToken)
    {
        var userSecretsId = await QueryUserSecretsIdAsync(projectFile, cancellationToken);

        if (!string.IsNullOrEmpty(userSecretsId) || !autoInit)
        {
            return userSecretsId;
        }

        // Auto-initialize user secrets (only for csproj projects - file-based apphosts
        // always have a UserSecretsId provided by the SDK)
        if (!ProjectExtensions.Contains(projectFile.Extension.ToLowerInvariant()))
        {
            return userSecretsId;
        }

        _logger.LogInformation("No UserSecretsId found. Initializing user secrets for {Project}...", projectFile.Name);
        _interactionService.DisplayMessage(KnownEmojis.Key, $"Initializing user secrets for {projectFile.Name}...");

        await _runner.InitUserSecretsAsync(
            projectFile,
            new ProcessInvocationOptions(),
            cancellationToken);

        // Re-query
        return await QueryUserSecretsIdAsync(projectFile, cancellationToken);
    }

    private async Task<string?> QueryUserSecretsIdAsync(FileInfo projectFile, CancellationToken cancellationToken)
    {
        try
        {
            var (exitCode, jsonDocument) = await _runner.GetProjectItemsAndPropertiesAsync(
                projectFile,
                items: [],
                properties: ["UserSecretsId"],
                new ProcessInvocationOptions(),
                cancellationToken);

            if (exitCode != 0 || jsonDocument is null)
            {
                return null;
            }

            var rootElement = jsonDocument.RootElement;
            if (rootElement.TryGetProperty("Properties", out var properties) &&
                properties.TryGetProperty("UserSecretsId", out var userSecretsIdElement))
            {
                var value = userSecretsIdElement.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get UserSecretsId from project file");
            return null;
        }
    }

    /// <summary>
    /// Configures isolated mode by enabling port randomization and isolating user secrets.
    /// </summary>
    /// <param name="appHostFile">The app host project file.</param>
    /// <param name="env">The environment variables dictionary to modify.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The isolated user secrets ID if created, or null if no isolation was needed.</returns>
    private async Task<string?> ConfigureIsolatedModeAsync(
        FileInfo appHostFile,
        Dictionary<string, string> env,
        CancellationToken cancellationToken)
    {
        // Enable port randomization for isolated mode
        env["DcpPublisher__RandomizePorts"] = "true";

        // Get the UserSecretsId from the project and create isolated copy
        var userSecretsId = await QueryUserSecretsIdAsync(appHostFile, cancellationToken);
        if (!string.IsNullOrEmpty(userSecretsId))
        {
            _interactionService.DisplayMessage(KnownEmojis.Key, RunCommandStrings.CopyingUserSecrets);
            var isolatedUserSecretsId = IsolatedUserSecretsHelper.CreateIsolatedUserSecrets(userSecretsId);
            if (!string.IsNullOrEmpty(isolatedUserSecretsId))
            {
                // Override the user secrets ID for this run
                env["DOTNET_USER_SECRETS_ID"] = isolatedUserSecretsId;
                return isolatedUserSecretsId;
            }
        }

        return null;
    }
}
