// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Bundles;
using Aspire.Cli.Certificates;
using Aspire.Cli.Configuration;
using Aspire.Cli.Diagnostics;
using Aspire.Cli.DotNet;
using Aspire.Cli.Interaction;
using Aspire.Cli.Packaging;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Projects;

/// <summary>
/// Handler for experimental Aspire CLI-managed file-based C# AppHosts.
/// </summary>
internal sealed class CliManagedDotNetAppHostProject : DotNetAppHostProject
{
    private readonly IFeatures _features;
    private readonly ICSharpCliManagedAppHostModuleGenerator _cliManagedModuleGenerator;
    private readonly CliManagedAppHostIntegrationClosureRestorer _integrationClosureRestorer;

    public CliManagedDotNetAppHostProject(
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
        FileLoggerProvider fileLoggerProvider,
        Program.CliLoggingOptions loggingOptions,
        IAppHostInfoResolver appHostInfoResolver,
        IConfigurationService configurationService,
        ICSharpCliManagedAppHostModuleGenerator cliManagedModuleGenerator,
        ILogger<CliManagedAppHostIntegrationClosureRestorer> integrationClosureRestorerLogger,
        TimeProvider? timeProvider = null)
        : base(
            runner,
            interactionService,
            certificateService,
            telemetry,
            profilingTelemetry,
            features,
            projectUpdater,
            sdkInstaller,
            bundleService,
            logger,
            fileLoggerProvider,
            loggingOptions,
            appHostInfoResolver,
            configurationService,
            timeProvider)
    {
        _features = features;
        _cliManagedModuleGenerator = cliManagedModuleGenerator;
        _integrationClosureRestorer = new CliManagedAppHostIntegrationClosureRestorer(runner, integrationClosureRestorerLogger);
    }

    public override bool CanHandle(FileInfo appHostFile)
        => IsCliManagedSingleFileAppHost(appHostFile, _features);

    public override bool RequiresStopForAddPackage => false;

    public override Task<AppHostValidationResult> ValidateAppHostAsync(FileInfo appHostFile, CancellationToken cancellationToken)
    {
        if (IsUnsupported)
        {
            return Task.FromResult(new AppHostValidationResult(IsValid: false, IsUnsupported: true));
        }

        return Task.FromResult(new AppHostValidationResult(IsValid: IsCliManagedSingleFileAppHost(appHostFile, _features)));
    }

    public override async Task<int> RestoreAsync(FileInfo appHostFile, OutputCollector outputCollector, CancellationToken cancellationToken)
    {
        if (!await EnsureSdkInstalledAsync(cancellationToken))
        {
            return CliExitCodes.SdkNotInstalled;
        }

        var moduleProjectFile = await _cliManagedModuleGenerator.TryGenerateAsync(appHostFile, cancellationToken);
        if (moduleProjectFile is null)
        {
            return CliExitCodes.FailedToBuildArtifacts;
        }

        var restoreSucceeded = await _integrationClosureRestorer.RestoreAsync(
            appHostFile,
            moduleProjectFile,
            new CliManagedAppHostIntegrationClosureRestoreOptions
            {
                BuildInvocationOptions = CreateModuleBuildInvocationOptions(appHostFile),
                BuildOutputCollector = outputCollector,
            },
            cancellationToken);

        return restoreSucceeded
            ? CliExitCodes.Success
            : CliExitCodes.FailedToBuildArtifacts;
    }

    internal static bool IsCliManagedSingleFileAppHost(FileInfo candidateFile, IFeatures features)
    {
        return IsCSharpCliManagedAppHostEnabled(candidateFile, features)
            && IsSingleFileAppHostCandidate(candidateFile)
            && !HasAspireAppHostSdkDirective(candidateFile);
    }

    protected override bool IsSingleFileAppHost(FileInfo appHostFile)
        => IsCliManagedSingleFileAppHost(appHostFile, _features);

    protected override async Task PrepareForRunAsync(FileInfo appHostFile, CancellationToken cancellationToken)
        => await _cliManagedModuleGenerator.TryGenerateAsync(appHostFile, cancellationToken);

    protected override async Task PrepareForPublishAsync(FileInfo appHostFile, CancellationToken cancellationToken)
        => await _cliManagedModuleGenerator.TryGenerateAsync(appHostFile, cancellationToken);

    protected override void ConfigureAppHostInvocationOptions(FileInfo appHostFile, ProcessInvocationOptions options)
    {
        CSharpCliManagedAppHostModuleGenerator.AddAppHostBuildProperties(appHostFile, options);
        options.EnvironmentVariablesToRemove.Add(KnownConfigNames.IntegrationProbeManifestPath);
        options.EnvironmentVariablesToRemove.Add(KnownConfigNames.IntegrationLibsPath);
    }

    protected override async Task<BundleLayoutLease?> ConfigureCliBundleEnvironmentForRunAsync(
        FileInfo appHostFile,
        Dictionary<string, string> env,
        bool isSingleFileAppHost,
        AppHostProjectContext context,
        CancellationToken cancellationToken)
    {
        return await ConfigureCliManagedAppHostEnvironmentAsync(appHostFile, env, cancellationToken);
    }

    protected override async Task<BundleLayoutLease?> ConfigureCliBundleEnvironmentForPublishAsync(
        FileInfo appHostFile,
        Dictionary<string, string> env,
        CancellationToken cancellationToken)
    {
        return await ConfigureCliManagedAppHostEnvironmentAsync(appHostFile, env, cancellationToken);
    }

    protected override async Task<bool> TryAddPackageAsync(AddPackageContext context, OutputCollector outputCollector, CancellationToken cancellationToken)
    {
        var appHostDirectory = context.AppHostFile.Directory;
        if (appHostDirectory is null)
        {
            return false;
        }

        var configDirectory = ConfigurationHelper.GetConfigRootDirectory(appHostDirectory);
        var config = AspireConfigFile.Load(configDirectory.FullName) ?? new AspireConfigFile();
        config.AddOrUpdatePackage(context.PackageId, context.PackageVersion);

        var packageSourceOverride = PackageSourceOverrideMappings.HasCredentialMaterial(context.Source ?? string.Empty)
            ? null
            : context.Source;
        var moduleProjectFile = await _cliManagedModuleGenerator.TryGenerateAsync(context.AppHostFile, config, configDirectory, packageSourceOverride, cancellationToken);
        if (moduleProjectFile is null)
        {
            return false;
        }

        // Persist config after generating the module from the in-memory package update so that the
        // module keeps source overrides with credential material out of aspire.config.json.
        config.Save(configDirectory.FullName);

        // Re-materialize the integration closure cache (probe manifest + libs path) so the next
        // `aspire run` resolves the newly added package without requiring an explicit `aspire restore`.
        var restoreSucceeded = await _integrationClosureRestorer.RestoreAsync(
            context.AppHostFile,
            moduleProjectFile,
            new CliManagedAppHostIntegrationClosureRestoreOptions
            {
                BuildInvocationOptions = CreateModuleBuildInvocationOptions(context.AppHostFile),
                BuildOutputCollector = outputCollector,
            },
            cancellationToken);
        return restoreSucceeded;
    }

    private static ProcessInvocationOptions CreateModuleBuildInvocationOptions(FileInfo appHostFile)
    {
        var options = new ProcessInvocationOptions();
        CSharpCliManagedAppHostModuleGenerator.AddBuildProperty(options);
        CSharpCliManagedAppHostModuleGenerator.AddRestoreConfigFilePropertyIfExists(appHostFile, options);
        return options;
    }

    private static bool IsCSharpCliManagedAppHostEnabled(FileInfo candidateFile, IFeatures features)
    {
        if (features.IsFeatureEnabled(KnownFeatures.CSharpCliManagedAppHostEnabled, defaultValue: false))
        {
            return true;
        }

        if (candidateFile.Directory is not { } appHostDirectory)
        {
            return false;
        }

        var configDirectory = ConfigurationHelper.GetConfigRootDirectory(appHostDirectory);
        var config = AspireConfigFile.Load(configDirectory.FullName);
        return config?.Features?.TryGetValue(KnownFeatures.CSharpCliManagedAppHostEnabled, out var enabled) == true && enabled;
    }

    private async Task<BundleLayoutLease?> ConfigureCliManagedAppHostEnvironmentAsync(FileInfo appHostFile, Dictionary<string, string> env, CancellationToken cancellationToken)
    {
        // CLI-managed single-file AppHosts always need DCP/Dashboard from the bundle (they
        // don't ship per-RID NuGet metadata for those), so inject unconditionally here.
        var layoutLease = await ConfigureCliBundleEnvironmentAsync(env, injectDcpAndDashboard: true, cancellationToken);

        // Attach the integration closure cache (probe manifest + libs path) materialized by
        // `aspire restore`. Mirrors PrebuiltAppHostServer.CreateStartInfo so the runtime AppHost
        // resolves integration assemblies from .aspire/integrations/apphosts/<hash>/ regardless of
        // whether we're in CLI-managed mode (this code path) or polyglot/prebuilt mode.
        var closureLayout = CliManagedAppHostIntegrationClosureRestorer.TryLoad(appHostFile);
        var (hasPackageReferences, hasProjectReferences) = GetConfiguredIntegrationKinds(appHostFile);
        IntegrationClosureEnvironment.Apply(
            (key, value) => env[key] = value,
            key => env.Remove(key),
            hasPackageReferences || hasProjectReferences ? closureLayout?.ProbeManifestPath : null,
            hasProjectReferences ? closureLayout?.IntegrationLibsPath : null);

        if (env.ContainsKey(BundleDiscovery.DcpPathEnvVar) &&
            env.ContainsKey(BundleDiscovery.DashboardPathEnvVar))
        {
            return layoutLease;
        }

        var repoRoot = AspireRepositoryDetector.DetectRepositoryRoot(appHostFile.Directory?.FullName);
        if (repoRoot is null)
        {
            return layoutLease;
        }

        if (!env.ContainsKey(BundleDiscovery.DcpPathEnvVar))
        {
            var (buildOs, buildArch) = DotNetBasedAppHostServerProject.GetBuildPlatform();
            var dcpPackageName = $"microsoft.developercontrolplane.{buildOs}-{buildArch}";
            var dcpVersion = DotNetBasedAppHostServerProject.GetDcpVersionFromRepo(repoRoot, buildOs, buildArch);
            env[BundleDiscovery.DcpPathEnvVar] = Path.Combine(GetNuGetPackageRoot(), dcpPackageName, dcpVersion, "tools");
        }

        if (!env.ContainsKey(BundleDiscovery.DashboardPathEnvVar))
        {
            env[BundleDiscovery.DashboardPathEnvVar] = Path.Combine(repoRoot, "artifacts", "bin", "Aspire.Dashboard", "Debug", "net8.0", "Aspire.Dashboard.dll");
        }

        return layoutLease;
    }

    private static string GetNuGetPackageRoot()
    {
        if (Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } packagesPath)
        {
            return packagesPath;
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
    }

    private static (bool HasPackageReferences, bool HasProjectReferences) GetConfiguredIntegrationKinds(FileInfo appHostFile)
    {
        if (appHostFile.Directory is not { } appHostDirectory)
        {
            return (false, false);
        }

        var configDirectory = ConfigurationHelper.GetConfigRootDirectory(appHostDirectory);
        var config = AspireConfigFile.Load(configDirectory.FullName);
        if (config?.Packages is null)
        {
            return (false, false);
        }

        var hasPackageReferences = false;
        var hasProjectReferences = false;
        foreach (var (name, value) in config.Packages)
        {
            if (string.Equals(name, "Aspire.Hosting", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Aspire.Hosting.AppHost", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var trimmedValue = value?.Trim();
            if (trimmedValue?.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) == true)
            {
                hasProjectReferences = true;
            }
            else
            {
                hasPackageReferences = true;
            }
        }

        return (hasPackageReferences, hasProjectReferences);
    }
}
