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
    private readonly IIntegrationClosureRestorer _integrationClosureRestorer;

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
        IIntegrationClosureRestorer integrationClosureRestorer,
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
        _integrationClosureRestorer = integrationClosureRestorer;
    }

    public override bool CanHandle(FileInfo appHostFile)
        => IsCliManagedSingleFileAppHost(appHostFile, _features);

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

        var restoreSucceeded = await _integrationClosureRestorer.RestoreAsync(
            appHostFile,
            new IntegrationClosureRestoreOptions { BuildOutputCollector = outputCollector },
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

    protected override void ConfigureAppHostInvocationOptions(ProcessInvocationOptions options)
        => CSharpCliManagedAppHostModuleGenerator.AddBuildProperty(options);

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
        await _cliManagedModuleGenerator.TryGenerateAsync(context.AppHostFile, config, configDirectory, packageSourceOverride, cancellationToken);

        // Persist config now so that any downstream consumer (including the closure restorer's
        // own internal call to TryGenerateAsync) sees the newly added package on disk.
        config.Save(configDirectory.FullName);

        // Re-materialize the integration closure cache (probe manifest + libs path) so the next
        // `aspire run` resolves the newly added package without requiring an explicit `aspire restore`.
        var restoreSucceeded = await _integrationClosureRestorer.RestoreAsync(
            context.AppHostFile,
            new IntegrationClosureRestoreOptions
            {
                BuildOutputCollector = outputCollector,
                PackageSourceOverride = packageSourceOverride,
            },
            cancellationToken);
        return restoreSucceeded;
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
        var closureLayout = _integrationClosureRestorer.TryLoad(appHostFile);
        if (closureLayout is not null)
        {
            // Only wire the probe-manifest env var when a manifest was actually written. An
            // AppHost with only project-ref integrations (no package-backed entries) has no
            // probe manifest, and setting the env var to a non-existent path would cause the
            // runtime AppHost to fail when it tries to read the file.
            if (!string.IsNullOrWhiteSpace(closureLayout.ProbeManifestPath))
            {
                env[KnownConfigNames.IntegrationProbeManifestPath] = closureLayout.ProbeManifestPath;
            }
            if (!string.IsNullOrWhiteSpace(closureLayout.IntegrationLibsPath))
            {
                env[KnownConfigNames.IntegrationLibsPath] = closureLayout.IntegrationLibsPath;
            }
        }

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
}
