// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Bundles;
using Aspire.Cli.Certificates;
using Aspire.Cli.Configuration;
using Aspire.Cli.Diagnostics;
using Aspire.Cli.DotNet;
using Aspire.Cli.Exceptions;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Aspire.Shared;
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
    private readonly ProfilingTelemetry _profilingTelemetry;
    private readonly IFeatures _features;
    private readonly ILogger<DotNetAppHostProject> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IProjectUpdater _projectUpdater;
    private readonly IDotNetSdkInstaller _sdkInstaller;
    private readonly IBundleService _bundleService;
    private readonly RunningInstanceManager _runningInstanceManager;
    private readonly Diagnostics.FileLoggerProvider _fileLoggerProvider;
    private readonly Program.CliLoggingOptions _loggingOptions;
    private readonly IAppHostInfoResolver _appHostInfoResolver;

    private static readonly string[] s_detectionPatterns = ["*.csproj", "*.fsproj", "*.vbproj", "apphost.cs"];
    internal static IReadOnlyCollection<string> ProjectExtensions { get; } =
        Array.AsReadOnly([".csproj", ".fsproj", ".vbproj"]);

    public DotNetAppHostProject(
        IDotNetCliRunner runner,
        IInteractionService interactionService,
        ICertificateService certificateService,
        AspireCliTelemetry telemetry,
        ProfilingTelemetry profilingTelemetry,
        IFeatures features,
        IProjectUpdater projectUpdater,
        IDotNetSdkInstaller sdkInstaller,
        IBundleService bundleService,
        ILogger<DotNetAppHostProject> logger,
        Diagnostics.FileLoggerProvider fileLoggerProvider,
        Program.CliLoggingOptions loggingOptions,
        IAppHostInfoResolver appHostInfoResolver,
        TimeProvider? timeProvider = null)
    {
        _runner = runner;
        _interactionService = interactionService;
        _certificateService = certificateService;
        _telemetry = telemetry;
        _profilingTelemetry = profilingTelemetry;
        _features = features;
        _projectUpdater = projectUpdater;
        _sdkInstaller = sdkInstaller;
        _bundleService = bundleService;
        _logger = logger;
        _fileLoggerProvider = fileLoggerProvider;
        _loggingOptions = loggingOptions;
        _appHostInfoResolver = appHostInfoResolver;
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

        // The resolver owns the cache/MSBuild fallback so validation and later run/publish
        // decisions share a single source of truth for AppHost project metadata.
        var information = await _appHostInfoResolver.GetAppHostInfoAsync(appHostFile, cancellationToken);

        if (information.ExitCode == 0 && information.IsAspireHost)
        {
            return new AppHostValidationResult(IsValid: true, AspireHostingVersion: information.AspireHostingVersion);
        }

        // Check if it's possibly an unbuildable AppHost (has the right name pattern but couldn't be validated)
        var isPossiblyUnbuildable = IsPossiblyUnbuildableAppHost(appHostFile);

        return new AppHostValidationResult(
            IsValid: false,
            IsPossiblyUnbuildable: isPossiblyUnbuildable);
    }

    /// <inheritdoc />
    public async Task<string?> GetAspireHostingVersionAsync(FileInfo appHostFile, CancellationToken cancellationToken)
    {
        // Use the same MSBuild-based inspection as validation so version resolution
        // follows the project model that run/publish already rely on, including
        // SDK-style projects, package references, and Central Package Management.
        var information = await _appHostInfoResolver.GetAppHostInfoAsync(appHostFile, cancellationToken);
        return information.ExitCode == 0 && information.IsAspireHost
            ? information.AspireHostingVersion
            : null;
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
            return CliExitCodes.SdkNotInstalled;
        }

        var effectiveAppHostFile = context.AppHostFile;
        var isExtensionHost = ExtensionHelper.IsExtensionHost(_interactionService, out _, out var extensionBackchannel);

        var buildOutputCollector = new OutputCollector(_fileLoggerProvider, CliLogFormat.Categories.Build);

        using var activity = _profilingTelemetry.StartAppHostRun();

        var isSingleFileAppHost = effectiveAppHostFile.Extension != ".csproj";

        var env = new Dictionary<string, string>(context.EnvironmentVariables);

        // Handle isolated mode - randomize ports and isolate user secrets
        string? isolatedUserSecretsId = null;
        if (context.Isolated)
        {
            using var isolatedModeActivity = _profilingTelemetry.StartAppHostConfigureIsolatedMode();
            try
            {
                isolatedUserSecretsId = await ConfigureIsolatedModeAsync(effectiveAppHostFile, env, cancellationToken);
                _logger.LogInformation("Aspire run isolated. Isolated UserSecretsId: {IsolatedUserSecretsId}", isolatedUserSecretsId);
            }
            catch (Exception ex)
            {
                isolatedModeActivity.SetError(ex.Message);
                throw;
            }
        }

        // Enable debug logging in the app host so that debug-level output is
        // captured in the CLI log file for diagnostics. Defaults to Debug but
        // can be overridden via --log-level.
        var aspireLogLevel = _loggingOptions.ConsoleLogLevel ?? LogLevel.Debug;
        env[KnownConfigNames.AspireLogLevel] = aspireLogLevel.ToString();

        if (context.WaitForDebugger)
        {
            env[KnownConfigNames.WaitForDebugger] = "true";
        }

        await EnsureDevCertificatesTrustedAsync(context, env, cancellationToken);

        var watch = !isSingleFileAppHost && _features.IsFeatureEnabled(KnownFeatures.DefaultWatchEnabled, defaultValue: false);
        var preparationExitCode = await PrepareAppHostAsync(
            context,
            effectiveAppHostFile,
            isSingleFileAppHost,
            isExtensionHost,
            extensionBackchannel,
            buildOutputCollector,
            cancellationToken);
        if (preparationExitCode is { } exitCode)
        {
            return exitCode;
        }

        var canQueryCliBundleProperty = !isSingleFileAppHost || !context.NoBuild;
        BundleLayoutLease? cliBundleLease = null;
        if (canQueryCliBundleProperty && await IsUsingCliBundleAsync(effectiveAppHostFile, cancellationToken))
        {
            cliBundleLease = await ConfigureCliBundleEnvironmentAsync(env, cancellationToken);
        }
        using var cliBundleLeaseScope = cliBundleLease;

        // RunCommand may display captured AppHost output as soon as BuildCompletionSource is signaled.
        // Store the collector first so failures that occur immediately after preparation are not lost
        // to a race between the AppHost process and RunCommand's UX path.
        var runOutputCollector = new OutputCollector(_fileLoggerProvider, CliLogFormat.Categories.AppHost);
        context.OutputCollector = runOutputCollector;

        // Signal that build/preparation is complete
        context.BuildCompletionSource?.TrySetResult(true);
        activity.AddAppHostBuildReadyEvent();

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
            // The AppHost may already have been built above, but watch mode intentionally still
            // runs with builds enabled. Passing --no-build through to dotnet watch breaks hot reload
            // because watch owns the incremental build loop and its environment setup.
            //
            // This means watch mode can do a second no-op build after the CLI pre-build succeeds.
            // That tradeoff is intentional: the pre-build makes initial compiler errors terminate
            // aspire run instead of leaving dotnet watch idle waiting for edits before a backchannel
            // ever becomes available.
            //
            // noRestore is only relevant when noBuild is false because --no-build implies --no-restore.
            var noBuild = !watch || context.NoBuild;
            using var runDotnetActivity = _profilingTelemetry.StartAppHostRunDotnetLifetime(watch, noBuild, context.NoRestore);
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

    private async Task EnsureDevCertificatesTrustedAsync(AppHostProjectContext context, Dictionary<string, string> env, CancellationToken cancellationToken)
    {
        try
        {
            EnsureCertificatesTrustedResult certResult;
            using (var certActivity = _profilingTelemetry.StartAppHostEnsureDevCertificates())
            {
                certResult = await _certificateService.EnsureCertificatesTrustedAsync(cancellationToken);
                certActivity.SetDevCertificateEnvironmentVariables(certResult.EnvironmentVariables.Count);
            }

            // Certificate trust can add platform-specific variables such as SSL_CERT_DIR on Linux.
            // These must flow into the AppHost process because the dashboard/resource service may
            // start immediately after preparation and depend on the same trust roots the CLI just
            // verified.
            foreach (var kvp in certResult.EnvironmentVariables)
            {
                env[kvp.Key] = kvp.Value;
            }
        }
        catch
        {
            // RunCommand waits on this source before it waits for the AppHost backchannel. Any
            // exception during preparation must signal failure, otherwise the command can hang
            // forever on a backchannel that will never be created.
            context.BuildCompletionSource?.TrySetResult(false);
            throw;
        }
    }

    private async Task<int?> PrepareAppHostAsync(
        AppHostProjectContext context,
        FileInfo effectiveAppHostFile,
        bool isSingleFileAppHost,
        bool isExtensionHost,
        IExtensionBackchannel? extensionBackchannel,
        OutputCollector buildOutputCollector,
        CancellationToken cancellationToken)
    {
        try
        {
            var buildExitCode = await BuildAppHostIfNeededAsync(
                context,
                effectiveAppHostFile,
                isExtensionHost,
                extensionBackchannel,
                buildOutputCollector,
                cancellationToken);
            if (buildExitCode is not null)
            {
                return buildExitCode;
            }

            var compatibilityCheck = await CheckAppHostCompatibilityAsync(effectiveAppHostFile, isSingleFileAppHost, cancellationToken);
            if (!compatibilityCheck.IsCompatibleAppHost)
            {
                context.BuildCompletionSource?.TrySetResult(false);
                return CliExitCodes.FailedToDotnetRunAppHost;
            }

            return null;
        }
        catch
        {
            // RunCommand has already started awaiting preparation before the AppHost process exists.
            // Signal failure for both expected failures and exceptions so callers do not wait for
            // a backchannel that preparation prevented from starting.
            context.BuildCompletionSource?.TrySetResult(false);
            throw;
        }
    }

    private async Task<int?> BuildAppHostIfNeededAsync(
        AppHostProjectContext context,
        FileInfo effectiveAppHostFile,
        bool isExtensionHost,
        IExtensionBackchannel? extensionBackchannel,
        OutputCollector buildOutputCollector,
        CancellationToken cancellationToken)
    {
        if (context.NoBuild)
        {
            return null;
        }

        var extensionHasBuildCapability = extensionBackchannel is not null && await extensionBackchannel.HasCapabilityAsync(KnownCapabilities.BuildDotnetUsingCli, cancellationToken);
        if (isExtensionHost && !extensionHasBuildCapability)
        {
            // Older extension hosts own the AppHost build themselves. Building again in the CLI would
            // duplicate work and could race the extension's diagnostics/launch pipeline. Newer hosts
            // opt in with build-dotnet-using-cli when they want the CLI to own this pre-build.
            return null;
        }

        using var buildActivity = _profilingTelemetry.StartAppHostBuild(context.NoRestore, isExtensionHost, extensionHasBuildCapability);

        var buildOptions = new ProcessInvocationOptions
        {
            StandardOutputCallback = buildOutputCollector.AppendOutput,
            StandardErrorCallback = buildOutputCollector.AppendError,
        };

        var buildExitCode = await AppHostHelper.BuildAppHostAsync(_runner, _interactionService, effectiveAppHostFile, context.NoRestore, buildOptions, context.WorkingDirectory, cancellationToken);
        buildActivity.SetAppHostBuildExitCode(buildExitCode);

        if (buildExitCode == 0)
        {
            return null;
        }

        // Preserve the build output before signaling failure. RunCommand reads this collector after
        // BuildCompletionSource completes so users see the compiler diagnostics instead of only a
        // generic "project could not be built" message.
        context.OutputCollector = buildOutputCollector;
        context.BuildCompletionSource?.TrySetResult(false);
        return CliExitCodes.FailedToBuildArtifacts;
    }

    private async Task<(bool IsCompatibleAppHost, string? AspireHostingVersion)> CheckAppHostCompatibilityAsync(
        FileInfo effectiveAppHostFile,
        bool isSingleFileAppHost,
        CancellationToken cancellationToken)
    {
        if (isSingleFileAppHost)
        {
            return (true, VersionHelper.GetDefaultTemplateVersion());
        }

        using var compatibilityActivity = _profilingTelemetry.StartAppHostCheckCompatibility();

        // Reuse the cached MSBuild result from ValidateAppHostAsync so we do not pay for a
        // second `dotnet msbuild -getProperty/-getItem` invocation just to gate compatibility.
        // Issue #17197: the legacy code path went runner → MSBuild for both validation and
        // the compatibility gate, doubling project inspection cost on every `aspire run`.
        var info = await _appHostInfoResolver.GetAppHostInfoAsync(effectiveAppHostFile, cancellationToken);
        var appHostCompatibilityCheck = AppHostHelper.EvaluateAppHostCompatibility(
            info.ExitCode,
            info.IsAspireHost,
            info.AspireHostingVersion,
            _interactionService,
            _fileLoggerProvider.LogFilePath);

        compatibilityActivity.SetAppHostCompatibility(
            appHostCompatibilityCheck.IsCompatibleAppHost,
            supportsBackchannel: appHostCompatibilityCheck.IsCompatibleAppHost,
            appHostCompatibilityCheck.AspireHostingVersion);

        return appHostCompatibilityCheck;
    }

    internal static void ConfigureSingleFileRunEnvironment(
        FileInfo appHostFile,
        Dictionary<string, string> env,
        IReadOnlyDictionary<string, string?>? inheritedEnvironmentVariables = null,
        string[]? args = null)
    {
        var runJsonFilePath = appHostFile.FullName[..^2] + "run.json";
        if (File.Exists(runJsonFilePath))
        {
            // dotnet run reads the launch profile from apphost.run.json natively, so the CLI
            // does not need to inject any environment variables itself.
            return;
        }

        // No apphost.run.json — fall back to aspire.config.json profiles (if any), then to
        // hardcoded defaults. ApplyEffectiveEnvironment is always called last so that explicit
        // --environment arguments still win.
        if (!TryApplyAspireConfigProfile(appHostFile, env, filterEnvironmentNames: false))
        {
            ApplyDefaultSingleFileEndpoints(env);
        }

        AppHostEnvironmentDefaults.ApplyEffectiveEnvironment(
            env,
            AppHostEnvironmentDefaults.DevelopmentEnvironmentName,
            inheritedEnvironmentVariables,
            args);
    }

    internal static void ConfigureSingleFilePublishEnvironment(
        FileInfo appHostFile,
        Dictionary<string, string> env,
        IReadOnlyDictionary<string, string?>? inheritedEnvironmentVariables = null,
        string[]? args = null)
    {
        if (!TryApplySingleFileLaunchProfileEnvironmentVariables(appHostFile, env)
            && !TryApplyAspireConfigProfile(appHostFile, env, filterEnvironmentNames: true))
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
        return TryApplyProfile(profiles, env, filterEnvironmentNames: true);
    }

    private static bool TryApplyAspireConfigProfile(
        FileInfo appHostFile,
        Dictionary<string, string> env,
        bool filterEnvironmentNames)
    {
        if (appHostFile.DirectoryName is not { Length: > 0 } directoryName)
        {
            return false;
        }

        AspireConfigFile? config;
        try
        {
            config = AspireConfigFile.Load(directoryName);
        }
        catch (JsonException)
        {
            // Malformed aspire.config.json — fall back to the next source rather than failing
            // the run/publish. This mirrors what happens when apphost.run.json is malformed.
            return false;
        }

        if (config?.Profiles is null)
        {
            return false;
        }

        // If aspire.config.json names a different AppHost file, don't apply its profile to
        // this AppHost. (Covers layouts where multiple AppHosts share a directory.)
        if (!string.IsNullOrEmpty(config.AppHost?.Path))
        {
            var resolvedAppHostPath = Path.GetFullPath(Path.Combine(directoryName, config.AppHost.Path));
            if (!string.Equals(resolvedAppHostPath, appHostFile.FullName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return TryApplyProfile(config.Profiles, env, filterEnvironmentNames);
    }

    private static bool TryApplyProfile(
        IReadOnlyDictionary<string, AspireConfigProfile>? profiles,
        Dictionary<string, string> env,
        bool filterEnvironmentNames)
    {
        AspireConfigProfile? profile;

        if (profiles?.TryGetValue("https", out var httpsProfile) == true)
        {
            profile = httpsProfile;
        }
        else
        {
            profile = profiles?.Values.FirstOrDefault();
        }

        if (profile is null || string.IsNullOrEmpty(profile.ApplicationUrl))
        {
            return false;
        }

        env["ASPNETCORE_URLS"] = profile.ApplicationUrl;

        if (profile.EnvironmentVariables is not null)
        {
            foreach (var (key, value) in profile.EnvironmentVariables)
            {
                if (filterEnvironmentNames && AppHostEnvironmentDefaults.IsEnvironmentVariableName(key))
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
            // Route through the cached helper so publish shares the same MSBuild
            // inspection result that PublishCommand's earlier ValidateAppHostAsync
            // populated. Issue #17197.
            var compatibilityCheck = await CheckAppHostCompatibilityAsync(
                effectiveAppHostFile,
                isSingleFileAppHost: false,
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
        }

        using var cliBundleLease = await IsUsingCliBundleAsync(effectiveAppHostFile, cancellationToken)
            ? await ConfigureCliBundleEnvironmentAsync(env, cancellationToken)
            : null;

        // Build the apphost (unless --no-build is specified)
        if (!isSingleFileAppHost && !context.NoBuild)
        {
            var buildOutputCollector = new OutputCollector(_fileLoggerProvider, CliLogFormat.Categories.Build);
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
                return CliExitCodes.FailedToBuildArtifacts;
            }
        }

        // Create collector and store in context for exception handling
        var runOutputCollector = new OutputCollector(_fileLoggerProvider, CliLogFormat.Categories.AppHost);
        context.OutputCollector = runOutputCollector;

        var runOptions = new ProcessInvocationOptions
        {
            StandardOutputCallback = runOutputCollector.AppendOutput,
            StandardErrorCallback = runOutputCollector.AppendError,
            NoLaunchProfile = true,
            StartDebugSession = context.StartDebugSession
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
        var outputCollector = new OutputCollector(_fileLoggerProvider, CliLogFormat.Categories.Package);
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
            // Read UserSecretsId from the shared AppHost build info cache so isolated mode
            // does not pay for a second `dotnet msbuild -getProperty` invocation when the
            // run path already fetched the AppHost metadata for validation/compat.
            var info = await _appHostInfoResolver.GetAppHostInfoAsync(projectFile, cancellationToken);
            return info.UserSecretsId;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get UserSecretsId from project file");
            return null;
        }
    }

    private async Task<bool> IsUsingCliBundleAsync(FileInfo projectFile, CancellationToken cancellationToken)
    {
        // Reuse the cached MSBuild result so `AspireUseCliBundle` is fetched alongside the
        // IsAspireHost/version inspection rather than triggering a third project evaluation.
        var info = await _appHostInfoResolver.GetAppHostInfoAsync(projectFile, cancellationToken);
        return info.IsUsingCliBundle;
    }

    private async Task<BundleLayoutLease?> ConfigureCliBundleEnvironmentAsync(Dictionary<string, string> env, CancellationToken cancellationToken)
    {
        var layoutLease = await _bundleService.EnsureExtractedAndAcquireLayoutAsync("cli", "dotnet-apphost", cancellationToken);
        var layout = layoutLease?.Layout;
        if (layout is null)
        {
            layoutLease?.Dispose();
            _logger.LogDebug("AspireUseCliBundle is enabled, but the Aspire CLI bundle layout was not available from this CLI process.");
            return null;
        }

        if (!env.ContainsKey(BundleDiscovery.DcpPathEnvVar) && layout.GetDcpPath() is { } dcpPath)
        {
            env[BundleDiscovery.DcpPathEnvVar] = dcpPath;
        }

        if (!env.ContainsKey(BundleDiscovery.DashboardPathEnvVar) && layout.GetManagedPath() is { } managedPath)
        {
            env[BundleDiscovery.DashboardPathEnvVar] = managedPath;
        }

        layoutLease?.AddEnvironment(env);
        return layoutLease;
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
