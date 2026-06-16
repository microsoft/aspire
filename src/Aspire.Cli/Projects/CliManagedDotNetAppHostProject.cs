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
    private const string IntegrationLibsPathStateFileName = "integration-libs-path.txt";

    private readonly IFeatures _features;
    private readonly IDotNetCliRunner _runner;
    private readonly ILogger<DotNetAppHostProject> _logger;
    private readonly CSharpCliManagedAppHostModuleGenerator _cliManagedModuleGenerator;

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
        IPackagingService packagingService,
        ILogger<CSharpCliManagedAppHostModuleGenerator> cliManagedModuleGeneratorLogger,
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
        _runner = runner;
        _logger = logger;
        _cliManagedModuleGenerator = new CSharpCliManagedAppHostModuleGenerator(packagingService, cliManagedModuleGeneratorLogger);
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

        var restoreSucceeded = await RestoreIntegrationClosureAsync(
            appHostFile,
            moduleProjectFile,
            CreateModuleBuildInvocationOptions(appHostFile),
            outputCollector,
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
        var restoreSucceeded = await RestoreIntegrationClosureAsync(
            context.AppHostFile,
            moduleProjectFile,
            CreateModuleBuildInvocationOptions(context.AppHostFile),
            outputCollector,
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
        var closureLayout = TryLoadIntegrationClosure(appHostFile);
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

    private async Task<bool> RestoreIntegrationClosureAsync(
        FileInfo appHostFile,
        FileInfo moduleProjectFile,
        ProcessInvocationOptions buildOptions,
        OutputCollector buildOutputCollector,
        CancellationToken cancellationToken)
    {
        var appHostDirectory = appHostFile.Directory
            ?? throw new InvalidOperationException($"AppHost file '{appHostFile.FullName}' has no parent directory.");
        var workingDirectory = IntegrationClosureBuilder.GetAppHostIntegrationCacheDirectory(appHostDirectory);
        Directory.CreateDirectory(workingDirectory.FullName);
        var restoreDir = Path.Combine(workingDirectory.FullName, IntegrationClosureBuilder.IntegrationRestoreFolderName);
        Directory.CreateDirectory(restoreDir);

        var existingStandardOutputCallback = buildOptions.StandardOutputCallback;
        var existingStandardErrorCallback = buildOptions.StandardErrorCallback;

        buildOptions.StandardOutputCallback = line =>
        {
            existingStandardOutputCallback?.Invoke(line);
            buildOutputCollector.AppendOutput(line);
        };
        buildOptions.StandardErrorCallback = line =>
        {
            existingStandardErrorCallback?.Invoke(line);
            buildOutputCollector.AppendError(line);
        };

        var exitCode = await _runner.BuildAsync(moduleProjectFile, noRestore: false, buildOptions, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            _logger.LogError("Failed to build CLI-managed AppHost integration module (exit code {ExitCode}).", exitCode);
            return false;
        }

        var closureManifest = await ReadClosureManifestAsync(restoreDir, cancellationToken).ConfigureAwait(false);
        if (closureManifest is null)
        {
            return false;
        }

        if (closureManifest.Entries.Any(static entry => entry.IsPackageBacked))
        {
            var probeManifestPath = Path.Combine(workingDirectory.FullName, IntegrationPackageProbeManifest.FileName);
            await IntegrationPackageProbeManifest.WriteAsync(
                probeManifestPath,
                closureManifest.CreatePackageProbeManifest(),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var staleProbeManifestPath = Path.Combine(workingDirectory.FullName, IntegrationPackageProbeManifest.FileName);
            if (File.Exists(staleProbeManifestPath))
            {
                File.Delete(staleProbeManifestPath);
            }
        }

        string? integrationLibsPath = null;
        if (closureManifest.Entries.Any(static entry => !entry.IsPackageBacked))
        {
            var layoutStore = new AppHostServerProjectLayoutStore(workingDirectory.FullName, _logger);
            var layout = await layoutStore.GetOrCreateAsync(closureManifest, cancellationToken).ConfigureAwait(false);
            if (layout is not null)
            {
                integrationLibsPath = layout.IntegrationLibsPath;
            }
        }

        // Persist the resolved libs path so future run/publish invocations can attach the cached
        // project-reference closure without rebuilding the generated module first.
        await PersistIntegrationClosureStateAsync(workingDirectory.FullName, integrationLibsPath, cancellationToken).ConfigureAwait(false);

        return true;
    }

    private async Task<AppHostServerClosureManifest?> ReadClosureManifestAsync(string restoreDir, CancellationToken cancellationToken)
    {
        // The generated module's Directory.Build.props sets BaseIntermediateOutputPath under the
        // same integration-restore directory that receives the closure files, matching the
        // polyglot/prebuilt generated-project layout even though Aspire.csproj itself lives under
        // .aspire/modules so file-based AppHosts can reference it with #:project.
        var assetsFilePath = Path.Combine(restoreDir, "obj", IntegrationClosureBuilder.ProjectAssetsFileName);

        // The CLI-managed path treats missing closure files as a soft failure (log + return null)
        // so the caller surfaces "build did not emit closure" rather than crashing. We pre-compute
        // the appsettings content from project-ref assembly names because the CLI-managed path
        // doesn't have the polyglot's IntegrationReference list available.
        var projectRefAssemblyNames = await IntegrationClosureBuilder.ReadProjectRefAssemblyNamesAsync(
            restoreDir, _logger, cancellationToken).ConfigureAwait(false);
        var appSettings = CreateAppSettingsContent(projectRefAssemblyNames);

        return await IntegrationClosureBuilder.ReadClosureManifestAsync(
            restoreDir,
            assetsFilePath,
            appSettings,
            ClosureFileMissingBehavior.ReturnNull,
            _logger,
            cancellationToken).ConfigureAwait(false);
    }

    private static string CreateAppSettingsContent(IReadOnlyList<string> projectRefAssemblyNames)
    {
        // appsettings.json content is hashed into the closure manifest as a cache-invalidation
        // signal; for the CLI-managed path we only contribute the project-ref assembly names
        // (package ids are already captured via closure metadata). The CLI-managed AppHost itself
        // doesn't consume this file today, but keeping it stable keeps the cache layout symmetric
        // with PrebuiltAppHostServer.
        var atsAssemblies = new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { "Aspire.Hosting" };
        foreach (var name in projectRefAssemblyNames)
        {
            atsAssemblies.Add(name);
        }

        var assembliesJson = string.Join(",\n      ", atsAssemblies.Select(static a => $"\"{a}\""));
        return $$"""
            {
              "AtsAssemblies": [
                {{assembliesJson}}
              ]
            }
            """;
    }

    private static (string? ProbeManifestPath, string? IntegrationLibsPath)? TryLoadIntegrationClosure(FileInfo appHostFile)
    {
        if (appHostFile.Directory is not { } appHostDirectory)
        {
            return null;
        }

        var workingDirectory = IntegrationClosureBuilder.GetAppHostIntegrationCacheDirectory(appHostDirectory);
        var probeManifestPath = Path.Combine(workingDirectory.FullName, IntegrationPackageProbeManifest.FileName);
        var integrationLibsPath = TryReadIntegrationLibsPathFromState(workingDirectory.FullName);
        var manifestExists = File.Exists(probeManifestPath);

        // Surface null when neither artifact is present so callers can skip wiring env vars that
        // point at missing files.
        if (!manifestExists && integrationLibsPath is null)
        {
            return null;
        }

        return (manifestExists ? probeManifestPath : null, integrationLibsPath);
    }

    private static async Task PersistIntegrationClosureStateAsync(string workingDirectory, string? integrationLibsPath, CancellationToken cancellationToken)
    {
        var statePath = Path.Combine(workingDirectory, IntegrationLibsPathStateFileName);
        if (string.IsNullOrWhiteSpace(integrationLibsPath))
        {
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }
            return;
        }

        await File.WriteAllTextAsync(statePath, integrationLibsPath, cancellationToken).ConfigureAwait(false);
    }

    private static string? TryReadIntegrationLibsPathFromState(string workingDirectory)
    {
        var statePath = Path.Combine(workingDirectory, IntegrationLibsPathStateFileName);
        if (!File.Exists(statePath))
        {
            return null;
        }

        try
        {
            var value = File.ReadAllText(statePath).Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
