// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Aspire.Cli.Bundles;
using Aspire.Cli.Configuration;
using Aspire.Cli.DotNet;
using Aspire.Cli.Layout;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Processes;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Projects;

internal sealed record NuGetConfigSource(
    string Key,
    string Source,
    bool IsAmbient,
    bool IsEnabled);

/// <summary>
/// Manages a pre-built AppHost server from the Aspire bundle layout.
/// This is used when running in bundle mode (without .NET SDK) to avoid
/// dynamic project generation and building.
/// </summary>
internal sealed class PrebuiltAppHostServer : IAppHostServerProject, IDisposable
{
    // Closure file names are owned by IntegrationClosureBuilder so generated integration
    // projects cannot drift from the post-build reader's MSBuild contract.
    internal const string ClosureManifestFileName = "closure-manifest.txt";
    internal const string IntegrationProjectFileName = "IntegrationRestore.csproj";

    internal const string IntegrationHostingVersionPropertyName = "AspireIntegrationHostingVersion";
    internal const string IntegrationPackageSourcesPropertyName = "AspireIntegrationPackageSources";
    private readonly string _appDirectoryPath;
    private readonly string _socketPath;
    private readonly LayoutConfiguration _layout;
    private readonly BundleNuGetService _nugetService;
    private readonly IDotNetCliRunner _dotNetCliRunner;
    private readonly IDotNetSdkInstaller _sdkInstaller;
    private readonly IPackagingService _packagingService;
    private readonly CliExecutionContext _executionContext;
    private readonly IProcessExecutionFactory _processExecutionFactory;
    private readonly IEnvironment _environment;
    private readonly ILogger _logger;
    private readonly BundleLayoutLease? _layoutLease;
    private readonly string _workingDirectory;
    private readonly string _projectReferencePrepareLockPath;
    private readonly AppHostServerProjectLayoutStore _projectLayoutStore;

    private string? _contentRootPath;
    private string? _integrationLibsPath;
    private string? _integrationProbeManifestPath;
    private AppHostServerProjectLayout? _selectedProjectLayout;

    /// <summary>
    /// Initializes a new instance of the PrebuiltAppHostServer class.
    /// </summary>
    /// <param name="appPath">The path to the user's polyglot app host directory (must be a directory path).</param>
    /// <param name="socketPath">The socket path for JSON-RPC communication.</param>
    /// <param name="layout">The bundle layout configuration.</param>
    /// <param name="nugetService">The NuGet service for restoring integration packages (NuGet-only path).</param>
    /// <param name="dotNetCliRunner">The .NET CLI runner for building project references.</param>
    /// <param name="sdkInstaller">The SDK installer for checking .NET SDK availability.</param>
    /// <param name="packagingService">The packaging service for channel resolution.</param>
    /// <param name="executionContext">The CLI execution context providing identity channel information.</param>
    /// <param name="processExecutionFactory">The factory used to spawn and manage the AppHost server child process.</param>
    /// <param name="environment">The environment abstraction for OS detection.</param>
    /// <param name="logger">The logger for diagnostic output.</param>
    /// <param name="layoutLease">The active bundle layout lease, if this server is running from a versioned bundle.</param>
    public PrebuiltAppHostServer(
        string appPath,
        string socketPath,
        LayoutConfiguration layout,
        BundleNuGetService nugetService,
        IDotNetCliRunner dotNetCliRunner,
        IDotNetSdkInstaller sdkInstaller,
        IPackagingService packagingService,
        CliExecutionContext executionContext,
        IProcessExecutionFactory processExecutionFactory,
        IEnvironment environment,
        ILogger logger,
        BundleLayoutLease? layoutLease = null)
    {
        _appDirectoryPath = Path.GetFullPath(appPath);
        _socketPath = socketPath;
        _layout = layout;
        _nugetService = nugetService;
        _dotNetCliRunner = dotNetCliRunner;
        _sdkInstaller = sdkInstaller;
        _packagingService = packagingService;
        _executionContext = executionContext;
        _processExecutionFactory = processExecutionFactory;
        _environment = environment;
        _logger = logger;
        _layoutLease = layoutLease;

        _workingDirectory = IntegrationClosureBuilder.GetAppHostIntegrationCacheDirectory(new DirectoryInfo(_appDirectoryPath)).FullName;
        Directory.CreateDirectory(_workingDirectory);
        _projectReferencePrepareLockPath = Path.Combine(_workingDirectory, "project-layouts", "prepare.lock");
        _projectLayoutStore = new AppHostServerProjectLayoutStore(_workingDirectory, _logger);
    }

    /// <inheritdoc />
    public string AppDirectoryPath => _appDirectoryPath;

    internal string? SelectedProjectLayoutFingerprint => _selectedProjectLayout?.Fingerprint;

    internal string? SelectedProjectLayoutPath => _selectedProjectLayout?.LayoutPath;

    internal string? IntegrationProbeManifestPath => _integrationProbeManifestPath;

    /// <summary>
    /// Gets the path to the aspire-managed executable (used as the server).
    /// </summary>
    public string GetServerPath()
    {
        var managedPath = _layout.GetManagedPath();
        if (managedPath is null || !File.Exists(managedPath))
        {
            throw new InvalidOperationException("aspire-managed not found in layout.");
        }

        return managedPath;
    }

    /// <inheritdoc />
    public async Task<AppHostServerPrepareResult> PrepareAsync(
        string sdkVersion,
        IEnumerable<IntegrationReference> integrations,
        string? requestedChannel = null,
        string? packageSourceOverride = null,
        string? packageSourceOverridePattern = null,
        CancellationToken cancellationToken = default)
    {
        var integrationList = integrations.ToList();
        var packageRefs = integrationList.Where(r => r.IsPackageReference).ToList();
        var projectRefs = integrationList.Where(r => r.IsProjectReference).ToList();
        // Lifted to outer scope so the failure footer reflects the source actually used by
        // restore — including the auto-discovered local hive resolved by
        // ResolveLocalPackageSourceOverrideAsync — rather than the unset --source the user
        // originally passed in.
        var effectivePackageSourceOverride = packageSourceOverride;

        try
        {
            _selectedProjectLayout = null;
            _contentRootPath = _workingDirectory;
            _integrationLibsPath = null;
            _integrationProbeManifestPath = null;

            // Resolve the channel the project requests for restore (aspire.config.json#channel,
            // with a legacy .aspire/settings.json#channel fallback). This is independent of the
            // running CLI's identity hive (CliExecutionContext.IdentityChannel).
            requestedChannel ??= ResolveRequestedChannel();
            if (string.IsNullOrWhiteSpace(effectivePackageSourceOverride))
            {
                var localPackageSourceChannel = requestedChannel;
                if (localPackageSourceChannel is null &&
                    string.Equals(sdkVersion, _executionContext.IdentitySdkVersion, StringComparison.OrdinalIgnoreCase))
                {
                    // An unpinned guest AppHost inherits the running CLI's SDK version. When that
                    // version comes from a local package hive or ASPIRE_CLI_PACKAGES, restore must
                    // use the same local source or it will request an unpublished build from the
                    // ambient feeds. This remains a source-only override: the project has not
                    // requested the CLI's channel policy.
                    localPackageSourceChannel = _executionContext.IdentityChannel;
                }

                effectivePackageSourceOverride = await ResolveLocalPackageSourceOverrideAsync(localPackageSourceChannel, cancellationToken).ConfigureAwait(false);
            }

            if (projectRefs.Count > 0)
            {
                // Project references require .NET SDK — verify it's available
                var (sdkAvailable, _, minimumRequired) = await _sdkInstaller.CheckAsync(cancellationToken);
                if (!sdkAvailable)
                {
                    throw new InvalidOperationException(
                        $"Project references in settings.json require .NET SDK {minimumRequired} or later. " +
                        "Install the .NET SDK from https://dotnet.microsoft.com/download or use NuGet package versions instead.");
                }
            }

            if (packageRefs.Count > 0)
            {
                _integrationProbeManifestPath = await RestoreNuGetPackagesAsync(
                    packageRefs,
                    requestedChannel,
                    effectivePackageSourceOverride,
                    packageSourceOverridePattern,
                    cancellationToken).ConfigureAwait(false);
            }

            if (projectRefs.Count > 0)
            {
                using var fileLock = await FileLock.AcquireAsync(_projectReferencePrepareLockPath, cancellationToken).ConfigureAwait(false);
                _projectLayoutStore.CleanupStagingDirectories();

                var closureManifest = await BuildIntegrationClosureManifestAsync(
                    packageRefs,
                    projectRefs,
                    sdkVersion,
                    requestedChannel,
                    effectivePackageSourceOverride,
                    packageSourceOverridePattern,
                    cancellationToken).ConfigureAwait(false);

                _selectedProjectLayout = await _projectLayoutStore.GetOrCreateAsync(closureManifest, cancellationToken).ConfigureAwait(false);
                if (_selectedProjectLayout is not null)
                {
                    _integrationLibsPath = _selectedProjectLayout.IntegrationLibsPath;
                }

                await WriteAppSettingsAsync(_workingDirectory, closureManifest.AppSettingsContent, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var appSettingsContent = CreateAppSettingsContent(packageRefs, []);
                await WriteAppSettingsAsync(_workingDirectory, appSettingsContent, cancellationToken).ConfigureAwait(false);
            }

            return new AppHostServerPrepareResult(
                Success: true,
                Output: null,
                ChannelName: requestedChannel,
                NeedsCodeGeneration: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AppHostServerPrepareFailedException ex)
        {
            _logger.LogError(ex, "Failed to prepare prebuilt AppHost server");
            AppendRestoreContextOnFailure(ex.Output, requestedChannel, effectivePackageSourceOverride, packageRefs);
            return new AppHostServerPrepareResult(
                Success: false,
                Output: ex.Output,
                ChannelName: requestedChannel,
                NeedsCodeGeneration: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to prepare prebuilt AppHost server");
            var output = new OutputCollector();
            output.AppendError($"Failed to prepare: {ex.Message}");
            AppendRestoreContextOnFailure(output, requestedChannel, effectivePackageSourceOverride, packageRefs);
            return new AppHostServerPrepareResult(
                Success: false,
                Output: output,
                ChannelName: requestedChannel,
                NeedsCodeGeneration: false);
        }
    }

    // Augment the failure output with the source / channel / requested versions so a user looking
    // at the displayed error after `aspire new --source <X>` can immediately see which inputs were
    // in play, instead of having to re-run with diagnostic logging. Called from both prepare
    // failure paths so every restore failure surfaces the same context shape.
    private static void AppendRestoreContextOnFailure(
        OutputCollector output,
        string? requestedChannel,
        string? packageSourceOverride,
        IReadOnlyList<IntegrationReference> packageRefs)
    {
        var hasOverride = !string.IsNullOrWhiteSpace(packageSourceOverride);
        var hasChannel = !string.IsNullOrEmpty(requestedChannel);
        if (!hasOverride && !hasChannel)
        {
            return;
        }

        if (hasOverride)
        {
            // NuGet feed URLs commonly embed credentials in UserInfo
            // (https://name:pat@host/...) or as SAS-style tokens in the query string.
            // This line ends up in the output users copy into bug reports and CI
            // transcripts, so strip the credential-carrying components before display.
            output.AppendError($"  --source: {RedactSourceForDisplay(packageSourceOverride!)}");
        }

        if (hasChannel)
        {
            output.AppendError($"  channel:  {requestedChannel}");
        }

        if (packageRefs.Count > 0)
        {
            var preview = packageRefs.Take(5).Select(static r => $"{r.Name} {r.Version}");
            output.AppendError($"  packages: {string.Join(", ", preview)}{(packageRefs.Count > 5 ? $", … (+{packageRefs.Count - 5} more)" : string.Empty)}");
        }
    }

    /// <summary>
    /// Restores NuGet packages using the bundled NuGet service (no .NET SDK required).
    /// </summary>
    private async Task<string> RestoreNuGetPackagesAsync(
        List<IntegrationReference> packageRefs,
        string? requestedChannel,
        string? packageSourceOverride,
        string? packageSourceOverridePattern,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Restoring {Count} integration packages via bundled NuGet", packageRefs.Count);

        var useExactPackageVersions = !string.IsNullOrWhiteSpace(packageSourceOverride);
        var packages = packageRefs
            .Select(r => (r.Name, Version: GetRestoreVersion(r.Name, r.Version!, useExactPackageVersions)))
            .ToList();
        var restoreSources = NormalizeIntegrationRestoreSources(
            await ResolveIntegrationRestoreSourcesAsync(
                requestedChannel,
                packageSourceOverride,
                packageSourceOverridePattern,
                cancellationToken).ConfigureAwait(false));
        var settings = await _nugetService.GetNuGetSettingsAsync(_appDirectoryPath, cancellationToken).ConfigureAwait(false);
        var configSources = ResolveNuGetConfigSources(
            restoreSources.PackageSourceMappings,
            settings.Sources,
            settings.ReservedPackageSourceKeys,
            settings.SourceIdentityKey);
        using var restoreOverlay = await CreateRestoreOverlayAsync(
            restoreSources,
            configSources,
            settings,
            cancellationToken).ConfigureAwait(false);
        var sources = GetNuGetSources(restoreSources)?.ToArray();
        IReadOnlyList<string> configPaths = restoreOverlay is null
            ? settings.ConfigPaths
            : [restoreOverlay.ConfigFile.FullName, .. settings.ConfigPaths];

        return await _nugetService.RestorePackagesAsync(
            packages,
            workingDirectory: _appDirectoryPath,
            targetFramework: DotNetBasedAppHostServerProject.TargetFramework,
            runtimeIdentifier: RuntimeInformation.RuntimeIdentifier,
            sources: sources,
            nugetConfigPaths: configPaths,
            nugetSettingsCacheIdentity: settings.CacheIdentity,
            nugetConfigOverlayCacheIdentity: restoreOverlay?.CacheIdentity,
            additionalSensitiveSources: settings.SensitiveSourceValues.Concat(
                restoreSources.PackageSourceMappings?
                    .Select(static mapping => mapping.Source)
                    .Where(PackageSourceOverrideMappings.HasCredentialMaterial) ?? []),
            globalPackagesFolderOverride: GetIntegrationRestoreGlobalPackagesFolder(restoreSources, restoreOverlay),
            ct: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes <paramref name="content" /> only when it differs from what is already on disk.
    /// </summary>
    /// <remarks>
    /// Rewriting an identical file still updates its timestamp, which MSBuild treats as a changed
    /// input and responds to by rebuilding. Writing only on a real change keeps the incremental
    /// build intact across launches.
    /// </remarks>
    internal static Task WriteIfChangedAsync(string path, string content, CancellationToken cancellationToken)
        => GeneratedFileWriter.WriteIfChangedAsync(path, content, cancellationToken);

    /// <summary>
    /// Produces the failure message for a failed integration build, recognizing the one failure
    /// mode that is a configuration problem rather than a build problem.
    /// </summary>
    /// <remarks>
    /// The AppHost server is the CLI itself, so the synthesized project pins Aspire.Hosting to the
    /// selected AppHost SDK version. A project reference that requires a newer Aspire.Hosting cannot
    /// be satisfied, and NuGet reports it as a downgrade:
    ///   error NU1605: Warning As Error: Detected package downgrade: Aspire.Hosting from 13.6.0-dev to 13.5.0
    /// The raw output is unusable here because MSBuild localizes it, so the diagnostic is matched on
    /// the error code alone and the actionable explanation is supplied in the CLI's own language.
    /// </remarks>
    internal static string GetIntegrationBuildFailureMessage(OutputCollector buildOutput)
    {
        var hasPackageDowngrade = buildOutput.GetLines()
            .Any(static l => l.Line.Contains("NU1605", StringComparison.Ordinal));

        return hasPackageDowngrade
            ? string.Format(
                CultureInfo.CurrentCulture,
                ErrorStrings.IntegrationBuildPackageDowngradeFailed,
                VersionHelper.GetDefaultTemplateVersion())
            : ErrorStrings.IntegrationBuildFailed;
    }

    private async Task<(int ExitCode, OutputCollector Output)> BuildIntegrationProjectAsync(
        string projectFilePath,
        bool noRestore,
        string? globalPackagesFolder,
        string integrationHostingVersion,
        string? integrationPackageSources,
        bool suppressLogging,
        IReadOnlyList<string> sensitiveSources,
        CancellationToken cancellationToken)
    {
        var buildOutput = new OutputCollector();
        // Environment-backed MSBuild properties are visible during NuGet's restore graph
        // evaluation, including Directory.Packages.props.
        var environmentVariables = new Dictionary<string, string>
        {
            [IntegrationHostingVersionPropertyName] = integrationHostingVersion
        };
        if (globalPackagesFolder is not null)
        {
            environmentVariables[CliPathHelper.NuGetPackagesEnvironmentVariable] = globalPackagesFolder;
        }
        if (integrationPackageSources is not null)
        {
            environmentVariables[IntegrationPackageSourcesPropertyName] = integrationPackageSources;
        }

        var exitCode = await _dotNetCliRunner.BuildAsync(
            new FileInfo(projectFilePath),
            noRestore,
            new ProcessInvocationOptions
            {
                StandardOutputCallback = line =>
                    buildOutput.AppendOutput(PackageSourceRedactor.RedactOccurrences(line, sensitiveSources)),
                StandardErrorCallback = line =>
                    buildOutput.AppendError(PackageSourceRedactor.RedactOccurrences(line, sensitiveSources)),
                EnvironmentVariableFilter = name =>
                    string.Equals(name, IntegrationHostingVersionPropertyName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, IntegrationPackageSourcesPropertyName, StringComparison.OrdinalIgnoreCase) ||
                    (globalPackagesFolder is not null &&
                        string.Equals(name, CliPathHelper.NuGetPackagesEnvironmentVariable, StringComparison.OrdinalIgnoreCase)),
                EnvironmentVariables = environmentVariables,
                SuppressLogging = suppressLogging
            },
            cancellationToken).ConfigureAwait(false);

        return (exitCode, buildOutput);
    }

    /// <summary>
    /// Creates a synthetic .csproj with the integration project references,
    /// then builds it to get their full copied-local closure.
    /// Requires .NET SDK.
    /// </summary>
    private async Task<AppHostServerClosureManifest> BuildIntegrationClosureManifestAsync(
        List<IntegrationReference> packageRefs,
        List<IntegrationReference> projectRefs,
        string sdkVersion,
        string? requestedChannel,
        string? packageSourceOverride,
        string? packageSourceOverridePattern,
        CancellationToken cancellationToken)
    {
        var restoreDir = Path.Combine(_workingDirectory, "integration-restore");
        Directory.CreateDirectory(restoreDir);

        var restoreSources = NormalizeIntegrationRestoreSources(
            await ResolveIntegrationRestoreSourcesAsync(
                requestedChannel,
                packageSourceOverride,
                packageSourceOverridePattern,
                cancellationToken).ConfigureAwait(false));
        var selectedSensitiveRestoreSources = restoreSources.AdditionalSources
            .Concat(restoreSources.PackageSourceMappings?.Select(static mapping => mapping.Source) ?? [])
            .Where(static source => PackageSourceOverrideMappings.HasCredentialMaterial(source))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var globalPackagesFolder = GetIntegrationRestoreGlobalPackagesFolder(restoreSources, restoreOverlay: null);
        var policyDirectory = IntegrationClosureBuilder.GetAppHostIntegrationPolicyDirectory(
            new DirectoryInfo(_appDirectoryPath));
        FileInfo? restoreOverlayFile = new(Path.Combine(policyDirectory.FullName, "NuGet.Config"));
        if (restoreOverlayFile.Exists)
        {
            restoreOverlayFile.Delete();
        }
        var settings = await _nugetService.GetNuGetSettingsAsync(_appDirectoryPath, cancellationToken).ConfigureAwait(false);
        var configSources = ResolveNuGetConfigSources(
            restoreSources.PackageSourceMappings,
            settings.Sources,
            settings.ReservedPackageSourceKeys,
            settings.SourceIdentityKey);
        if (restoreSources.PackageSourceMappings is null)
        {
            restoreOverlayFile = null;
        }
        else
        {
            var overlay = CreateNuGetConfigOverlay(
                restoreSources.PackageSourceMappings,
                settings,
                configSources,
                globalPackagesFolder);
            await _nugetService.WriteNuGetConfigOverlayAsync(
                overlay,
                restoreOverlayFile!.FullName,
                cancellationToken).ConfigureAwait(false);
        }

        var rootAdditionalSources = restoreSources.PackageSourceMappings is null
            ? GetNuGetSources(restoreSources)?.ToArray() ?? []
            : [];
        var integrationPackageSources = IntegrationClosureBuilder.CreateRestoreAdditionalProjectSourcesValue(
            existingValue: null,
            GetIntegrationPackageSourceHints(restoreSources));
        var sensitiveSources = settings.SensitiveSourceValues
            .Concat(selectedSensitiveRestoreSources)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var intermediateOutputPath = Path.Combine(restoreDir, "obj");
        var projectContent = GenerateIntegrationProjectFile(
            projectRefs,
            sdkVersion,
            restoreDir,
            rootAdditionalSources,
            useExactPackageVersions: !string.IsNullOrWhiteSpace(packageSourceOverride));
        var projectFilePath = Path.Combine(restoreDir, IntegrationProjectFileName);
        await WriteIfChangedAsync(projectFilePath, projectContent, cancellationToken);

        // Write a Directory.Packages.props to opt out of Central Package Management
        var directoryPackagesProps = """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
              </PropertyGroup>
            </Project>
            """;
        await WriteIfChangedAsync(
            Path.Combine(restoreDir, "Directory.Packages.props"), directoryPackagesProps, cancellationToken);

        // Directory.Build.props sets output paths before the SDK consumes them and also prevents
        // parent props from affecting the generated project.
        await WriteIfChangedAsync(
            Path.Combine(restoreDir, "Directory.Build.props"),
            IntegrationClosureBuilder.CreateClosureDirectoryBuildProps(
                restoreDir,
                intermediateOutputPath,
                restoreOverlayFile?.DirectoryName ?? _appDirectoryPath,
                globalPackagesFolder: null).ToString(),
            cancellationToken);

        // Write empty Directory.Build.targets to prevent parent targets imports.
        await WriteIfChangedAsync(
            Path.Combine(restoreDir, "Directory.Build.targets"), "<Project />", cancellationToken);

        _logger.LogDebug("Building integration project with {ProjectCount} project references", projectRefs.Count);

        var (exitCode, buildOutput) = await BuildIntegrationProjectAsync(
            projectFilePath,
            noRestore: false,
            globalPackagesFolder,
            integrationHostingVersion: sdkVersion,
            integrationPackageSources,
            suppressLogging: sensitiveSources.Length > 0,
            sensitiveSources,
            cancellationToken).ConfigureAwait(false);

        if (exitCode != 0)
        {
            var outputLines = string.Join(Environment.NewLine, buildOutput.GetLines().Select(l => l.Line));
            _logger.LogError("Integration project build failed. Output:\n{BuildOutput}", outputLines);
            throw new AppHostServerPrepareFailedException(GetIntegrationBuildFailureMessage(buildOutput), buildOutput);
        }

        var projectRefAssemblyNames = await IntegrationClosureBuilder.ReadProjectRefAssemblyNamesAsync(
            restoreDir,
            _logger,
            cancellationToken).ConfigureAwait(false);
        var appSettingsContent = CreateAppSettingsContent(packageRefs, projectRefAssemblyNames);

        var closureManifest = await IntegrationClosureBuilder.ReadClosureManifestAsync(
            restoreDir,
            Path.Combine(intermediateOutputPath, IntegrationClosureBuilder.ProjectAssetsFileName),
            appSettingsContent,
            ClosureFileMissingBehavior.Throw,
            _logger,
            cancellationToken).ConfigureAwait(false);

        // ReadClosureManifestAsync only returns null in ReturnNull mode; in Throw mode any
        // missing/inconsistent state has already raised an exception.
        Debug.Assert(closureManifest is not null);

        await File.WriteAllLinesAsync(
            Path.Combine(restoreDir, ClosureManifestFileName),
            closureManifest!.GetManifestLines(),
            cancellationToken).ConfigureAwait(false);
        return closureManifest;
    }

    /// <summary>
    /// Generates a synthetic .csproj file that pins Aspire.Hosting and references the integration projects.
    /// Building this project with CopyLocalLockFileAssemblies produces their full copied-local closure.
    /// </summary>
    internal static string GenerateIntegrationProjectFile(
        List<IntegrationReference> projectRefs,
        string sdkVersion,
        string restoreDir,
        IEnumerable<string>? additionalSources = null,
        bool useExactPackageVersions = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sdkVersion);

        var projectFile = IntegrationClosureBuilder.CreateClosureProjectFile(
            restoreDir,
            additionalSources);

        // Keep the pre-existing compatibility check between the AppHost SDK selected by the CLI and
        // project-referenced integrations. All other integration packages use the package-only path.
        projectFile.PackageReferences.Add(new CSharpPackageReference(
            "Aspire.Hosting",
            GetRestoreVersion("Aspire.Hosting", sdkVersion, useExactPackageVersions)));

        projectFile.ProjectReferences.AddRange(projectRefs.Select(p => new CSharpProjectReference(
            p.ProjectPath!,
            IsAspireProjectResource: false,
            ReferenceOutputAssembly: true)));

        return projectFile.ToXDocument().ToString();
    }

    /// <summary>
    /// Resolves the channel name the <em>project requests</em> for restore — read from the
    /// project's <c>aspire.config.json#channel</c> (or legacy <c>.aspire/settings.json#channel</c>).
    /// This is independent of the running CLI's <see cref="CliExecutionContext.IdentityChannel"/>.
    /// </summary>
    internal string? ResolveRequestedChannel()
    {
        // Check aspire.config.json first, then fall back to legacy .aspire/settings.json.
        var channelName = AspireConfigFile.Load(_appDirectoryPath)?.Channel
            ?? AspireJsonConfiguration.Load(_appDirectoryPath)?.Channel;

        if (!string.IsNullOrEmpty(channelName))
        {
            _logger.LogDebug("Resolved channel: {Channel}", channelName);
        }

        return channelName;
    }

    internal Task<IntegrationRestoreSources> ResolveIntegrationRestoreSourcesAsync(
        string? requestedChannel,
        string? packageSourceOverride,
        string? packageSourceOverridePattern,
        CancellationToken cancellationToken)
        => new IntegrationRestoreSourceResolver(_packagingService, _logger, _executionContext.NuGetServiceIndexOverride)
            .ResolveAsync(
                requestedChannel,
                packageSourceOverride,
                packageSourceOverridePattern,
                cancellationToken);

    private static IEnumerable<string>? GetNuGetSources(IntegrationRestoreSources restoreSources)
        => restoreSources.PackageSourceMappings is null && restoreSources.AdditionalSources.Count > 0
            ? restoreSources.AdditionalSources
            : null;

    private static string[] GetIntegrationPackageSourceHints(IntegrationRestoreSources restoreSources)
    {
        IEnumerable<string> candidateSources;
        if (restoreSources.PackageSourceMappings is { Length: > 0 } mappings)
        {
            var integrationSpecificSources = mappings
                .Where(static mapping => mapping.PackageFilter != PackageMapping.AllPackages)
                .Select(static mapping => mapping.Source)
                .ToArray();
            candidateSources = integrationSpecificSources.Length > 0
                ? integrationSpecificSources
                : mappings
                    .Where(static mapping => mapping.PackageFilter == PackageMapping.AllPackages)
                    .Select(static mapping => mapping.Source);
        }
        else
        {
            candidateSources = restoreSources.AdditionalSources;
        }

        // Source hints flow through the MSBuild environment and can be persisted in restore
        // artifacts. Redacting an inline credential would also make the source unusable, so omit
        // credential-bearing URLs and require those projects to use NuGet-owned authentication.
        return candidateSources
            .Where(static source => !PackageSourceOverrideMappings.HasCredentialMaterial(source))
            .Distinct(PackageSourceIdentity.Comparer)
            .ToArray();
    }

    private IntegrationRestoreSources NormalizeIntegrationRestoreSources(IntegrationRestoreSources restoreSources)
    {
        var appDirectory = new DirectoryInfo(_appDirectoryPath);
        var normalizedAdditionalSources = restoreSources.AdditionalSources
            .Select(source => PackageSourceOverrideMappings.ResolveForWorkingDirectory(source, appDirectory))
            .ToArray();
        var normalizedMappings = restoreSources.PackageSourceMappings?
            .Select(mapping => new PackageMapping(
                mapping.PackageFilter,
                PackageSourceOverrideMappings.ResolveForWorkingDirectory(mapping.Source, appDirectory)))
            .ToArray();

        return restoreSources with
        {
            AdditionalSources = normalizedAdditionalSources,
            PackageSourceMappings = normalizedMappings,
            GlobalPackagesFolderIdentity = restoreSources.ConfigureGlobalPackagesFolder
                ? IntegrationRestoreSourceResolver.CreateGlobalPackagesFolderIdentity(
                    normalizedAdditionalSources,
                    normalizedMappings)
                : null
        };
    }

    internal static NuGetConfigSource[] ResolveNuGetConfigSources(
        PackageMapping[]? mappings,
        IReadOnlyList<NuGetSourceInfo> ambientSources,
        IReadOnlyList<string> reservedPackageSourceKeys,
        ReadOnlySpan<byte> sourceIdentityKey)
    {
        if (mappings is null)
        {
            return [];
        }

        var usedKeys = reservedPackageSourceKeys
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nextAspireKey = 0;

        var resolvedSources = new List<NuGetConfigSource>();
        foreach (var source in mappings
            .Select(static mapping => mapping.Source)
            .Distinct(PackageSourceIdentity.Comparer))
        {
            var sourceIdentity = NuGetSourceIdentity.Compute(source, sourceIdentityKey);
            var ambientMatches = ambientSources
                .Where(candidate => string.Equals(candidate.Identity, sourceIdentity, StringComparison.Ordinal))
                .ToArray();
            if (ambientMatches.Length > 0)
            {
                var enabledMatches = ambientMatches
                    .Where(static ambientSource => ambientSource.IsEnabled)
                    .ToArray();
                var selectedMatches = enabledMatches;
                if (selectedMatches.Length == 0)
                {
                    // Credentials and client certificates are attached to source aliases. When an equivalent
                    // source must be re-enabled, preserve the alias that carries its authentication configuration.
                    var preferredDisabledMatch = ambientMatches.FirstOrDefault(static ambientSource =>
                        ambientSource.HasCredentials || ambientSource.HasClientCertificates);
                    selectedMatches = [preferredDisabledMatch ?? ambientMatches[0]];
                }

                foreach (var ambientSource in selectedMatches)
                {
                    resolvedSources.Add(new NuGetConfigSource(
                        ambientSource.Name,
                        source,
                        IsAmbient: true,
                        ambientSource.IsEnabled));
                }

                continue;
            }

            string key;
            do
            {
                key = $"aspire-{nextAspireKey++}";
            }
            while (!usedKeys.Add(key));

            resolvedSources.Add(new NuGetConfigSource(key, source, IsAmbient: false, IsEnabled: true));
        }

        return [.. resolvedSources];
    }

    internal static NuGetConfigOverlayInfo CreateNuGetConfigOverlay(
        PackageMapping[] selectedMappings,
        NuGetSettingsInfo settings,
        IReadOnlyList<NuGetConfigSource> selectedSources,
        string? globalPackagesFolder)
    {
        ArgumentNullException.ThrowIfNull(selectedMappings);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(selectedSources);

        var enabledSourceKeys = selectedSources
            .Where(static source => source.IsAmbient && !source.IsEnabled)
            .Select(static source => source.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var clearDisabledPackageSources = enabledSourceKeys.Count > 0;
        var disabledPackageSourceKeys = clearDisabledPackageSources
            ? settings.DisabledPackageSourceKeys
                .Where(key => !enabledSourceKeys.Contains(key))
                .ToArray()
            : [];

        return new NuGetConfigOverlayInfo(
            selectedSources
                .Where(static source => !source.IsAmbient)
                .Select(static source => new NuGetConfigSourceDefinition(source.Key, source.Source))
                .ToArray(),
            ComposePackageSourceMappings(
                selectedMappings,
                settings.PackageSourceMappings,
                settings.Sources,
                selectedSources),
            clearDisabledPackageSources,
            disabledPackageSourceKeys,
            globalPackagesFolder);
    }

    internal static NuGetPackageSourceMappingInfo[] ComposePackageSourceMappings(
        IReadOnlyList<PackageMapping> selectedMappings,
        IReadOnlyList<NuGetPackageSourceMappingInfo> ambientMappings,
        IReadOnlyList<NuGetSourceInfo> ambientSources,
        IReadOnlyList<NuGetConfigSource> selectedSources)
    {
        ArgumentNullException.ThrowIfNull(selectedMappings);
        ArgumentNullException.ThrowIfNull(ambientMappings);
        ArgumentNullException.ThrowIfNull(ambientSources);
        ArgumentNullException.ThrowIfNull(selectedSources);

        var patternsBySourceKey = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        if (ambientMappings.Count == 0)
        {
            // Enabling package-source mapping changes NuGet from "every enabled source can serve
            // every package" to deny-by-default. Reproduce that existing eligibility with wildcard
            // mappings before adding the more-specific Aspire channel policy.
            foreach (var ambientSource in ambientSources.Where(static source => source.IsEnabled))
            {
                AddPattern(patternsBySourceKey, ambientSource.Name, PackageMapping.AllPackages);
            }
        }
        else
        {
            foreach (var ambientMapping in ambientMappings)
            {
                foreach (var pattern in ambientMapping.Patterns)
                {
                    AddPattern(patternsBySourceKey, ambientMapping.SourceKey, pattern);
                }
            }
        }

        var authoritativePatterns = selectedMappings
            .Select(static mapping => mapping.PackageFilter)
            .Where(static pattern => pattern != PackageMapping.AllPackages)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var authoritativeSources = selectedMappings
            .Where(static mapping => mapping.PackageFilter != PackageMapping.AllPackages)
            .Select(static mapping => mapping.Source)
            .ToHashSet(PackageSourceIdentity.Comparer);
        foreach (var patterns in patternsBySourceKey.Values)
        {
            patterns.RemoveAll(pattern => authoritativePatterns.Any(
                authoritativePattern => CompetesWithAuthoritativePattern(pattern, authoritativePattern)));
        }

        foreach (var mapping in selectedMappings)
        {
            if (ambientMappings.Count > 0 &&
                mapping.PackageFilter == PackageMapping.AllPackages &&
                !authoritativeSources.Contains(mapping.Source))
            {
                // An existing mapping policy already defines eligibility for non-Aspire packages.
                // Channel fallback sources must not broaden that unrelated ambient policy. An
                // explicitly selected source remains generally eligible because --source is direct
                // user intent, while its exact package pattern still guarantees the root origin.
                continue;
            }

            foreach (var source in selectedSources.Where(
                source => PackageSourceIdentity.Comparer.Equals(source.Source, mapping.Source)))
            {
                AddPattern(patternsBySourceKey, source.Key, mapping.PackageFilter);
            }
        }

        return patternsBySourceKey
            .Where(static mapping => mapping.Value.Count > 0)
            .Select(static mapping => new NuGetPackageSourceMappingInfo(
                mapping.Key,
                [.. mapping.Value]))
            .ToArray();
    }

    private static void AddPattern(
        Dictionary<string, List<string>> patternsBySourceKey,
        string sourceKey,
        string pattern)
    {
        if (!patternsBySourceKey.TryGetValue(sourceKey, out var patterns))
        {
            patterns = [];
            patternsBySourceKey.Add(sourceKey, patterns);
        }

        if (!patterns.Contains(pattern, StringComparer.OrdinalIgnoreCase))
        {
            patterns.Add(pattern);
        }
    }

    private static bool CompetesWithAuthoritativePattern(
        string ambientPattern,
        string authoritativePattern)
    {
        if (authoritativePattern == PackageMapping.AllPackages)
        {
            return false;
        }

        if (!authoritativePattern.EndsWith('*'))
        {
            return string.Equals(
                ambientPattern,
                authoritativePattern,
                StringComparison.OrdinalIgnoreCase);
        }

        var authoritativePrefix = authoritativePattern[..^1];
        if (ambientPattern == PackageMapping.AllPackages)
        {
            return false;
        }

        var ambientPrefix = ambientPattern.EndsWith('*')
            ? ambientPattern[..^1]
            : ambientPattern;
        return ambientPrefix.Length >= authoritativePrefix.Length &&
            ambientPrefix.StartsWith(authoritativePrefix, StringComparison.OrdinalIgnoreCase);
    }

    private string? GetIntegrationRestoreGlobalPackagesFolder(
        IntegrationRestoreSources restoreSources,
        TemporaryNuGetConfig? restoreOverlay)
        => restoreSources.ConfigureGlobalPackagesFolder
            ? CliPathHelper.GetStagingNuGetPackagesIdentityDirectory(
                _executionContext.AspireHomeDirectory,
                restoreOverlay?.CacheIdentity ?? restoreSources.GlobalPackagesFolderIdentity)
            : null;

    internal async Task<TemporaryNuGetConfig?> CreateRestoreOverlayAsync(
        IntegrationRestoreSources restoreSources,
        IReadOnlyList<NuGetConfigSource> sources,
        NuGetSettingsInfo settings,
        CancellationToken cancellationToken)
    {
        if (restoreSources.PackageSourceMappings is null)
        {
            return null;
        }

        var overlay = CreateNuGetConfigOverlay(
            restoreSources.PackageSourceMappings,
            settings,
            sources,
            globalPackagesFolder: null);
        var config = await TemporaryNuGetConfig.CreateRestoreOverlayAsync(
            path => _nugetService.WriteNuGetConfigOverlayAsync(
                overlay,
                path,
                cancellationToken)).ConfigureAwait(false);
        return await ConfigureGlobalPackagesFolderAsync(
            config,
            restoreSources,
            overlay,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<TemporaryNuGetConfig> ConfigureGlobalPackagesFolderAsync(
        TemporaryNuGetConfig config,
        IntegrationRestoreSources restoreSources,
        NuGetConfigOverlayInfo overlay,
        CancellationToken cancellationToken)
    {
        var globalPackagesFolder = GetIntegrationRestoreGlobalPackagesFolder(restoreSources, config);
        if (globalPackagesFolder is null)
        {
            return config;
        }

        try
        {
            await config.RegenerateAsync(
                path => _nugetService.WriteNuGetConfigOverlayAsync(
                    overlay with { GlobalPackagesFolder = globalPackagesFolder },
                    path,
                    cancellationToken)).ConfigureAwait(false);
            return config;
        }
        catch
        {
            config.Dispose();
            throw;
        }
    }

    private async Task<string?> ResolveLocalPackageSourceOverrideAsync(string? requestedChannel, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(requestedChannel))
        {
            return null;
        }

        PackageChannel? channel;
        try
        {
            var channels = await _packagingService.GetChannelsAsync(cancellationToken, requestedChannel);
            channel = channels.FirstOrDefault(c =>
                c.Type == PackageChannelType.Explicit &&
                c.Mappings is { Length: > 0 } &&
                string.Equals(c.Name, requestedChannel, StringComparisons.ChannelName));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A transient packaging-service failure during auto-discovery must not turn
            // `aspire new` into a hard failure. Returning null leaves the resolved channel
            // sources and ambient NuGet settings unchanged.
            _logger.LogWarning(ex, "Failed to resolve local Aspire package source for channel '{Channel}'.", requestedChannel);
            return null;
        }

        var source = channel is null ? null : GetExistingLocalAspirePackageSource(channel);

        if (!string.IsNullOrWhiteSpace(source))
        {
            _logger.LogDebug("Using local package source '{Source}' for channel '{Channel}'.", source, requestedChannel);
        }

        return source;
    }

    private static string? GetExistingLocalAspirePackageSource(PackageChannel channel)
    {
        if (channel.Mappings is null)
        {
            return null;
        }

        foreach (var mapping in channel.Mappings)
        {
            if (!IsAspireSpecificMapping(mapping) ||
                PackageSourceOverrideMappings.GetNormalizedLocalDirectory(mapping.Source) is not { } localDirectory ||
                !Directory.Exists(localDirectory))
            {
                continue;
            }

            return mapping.Source;
        }

        return null;
    }

    private static bool IsAspireSpecificMapping(PackageMapping mapping) =>
        mapping.PackageFilter != PackageMapping.AllPackages &&
        mapping.PackageFilter.StartsWith("Aspire", StringComparison.OrdinalIgnoreCase);

    private static string GetRestoreVersion(string packageName, string version, bool useExactPackageVersions)
    {
        var shouldUseExactAspirePackageVersion = useExactPackageVersions && packageName.StartsWith("Aspire", StringComparison.OrdinalIgnoreCase);
        if (!shouldUseExactAspirePackageVersion || version.Length == 0 || version[0] is '[' or '(')
        {
            return version;
        }

        return $"[{version}]";
    }

    // Display-safe form of a NuGet source used in user-visible error footers. Delegates to the
    // shared helper so the same redaction is applied wherever sources appear (failure context,
    // debug logs in BundleNuGetService, etc.).
    internal static string RedactSourceForDisplay(string source) => PackageSourceRedactor.RedactForDisplay(source);

    /// <inheritdoc />
    public async Task<AppHostServerRunResult> RunAsync(
        int hostPid,
        IReadOnlyDictionary<string, string>? environmentVariables,
        string[]? additionalArgs,
        bool debug,
        AppHostServerRunControl? runControl)
    {
        var startInfo = CreateStartInfo(hostPid, environmentVariables, additionalArgs, debug);
        var outputCollector = new OutputCollector();

        // The execution local is forward-referenced by the log callbacks so they can read the
        // child's pid per line (ProcessInvocationOptions.StandardOutputCallback is line-only). The
        // log level + prefix differ from the dotnet-based server (#16729); keeping them here keeps
        // this server's per-line behavior in one place. ProcessExecution publishes the child pid before
        // it starts stdout/stderr pumps so immediate output can read ProcessId.
        IProcessExecution execution = null!;

        void OnStdout(string line)
        {
            // Promoted from LogTrace to LogDebug so that apphost-server stdout reaches the
            // CLI's on-disk log under the default file-logger filter (Debug). Previously
            // these lines were dropped entirely, which made apphost-side warnings
            // (for example, "LoaderExceptions" from the type-discovery path) invisible to
            // anyone diagnosing a "no code generator found" / "no language support found"
            // error. See https://github.com/microsoft/aspire/issues/16729.
            _logger.LogDebug("PrebuiltAppHostServer({ProcessId}) stdout: {Line}", execution.ProcessId, line);
            outputCollector.AppendOutput(line);
        }

        void OnStderr(string line)
        {
            // Promoted from LogTrace to LogInformation so that apphost-server stderr is
            // visible at the default console log level (Information). Stderr is reserved
            // for genuine problems in well-behaved server processes, so surfacing it
            // by default is appropriate. See https://github.com/microsoft/aspire/issues/16729.
            _logger.LogInformation("PrebuiltAppHostServer({ProcessId}) stderr: {Line}", execution.ProcessId, line);
            outputCollector.AppendError(line);
        }

        var options = new ProcessInvocationOptions
        {
            StandardOutputCallback = OnStdout,
            StandardErrorCallback = OnStderr,
            IsolateConsole = runControl?.IsolateConsole ?? false,
            KillOnParentExit = runControl?.KillOnParentExit ?? false,
            GracefulShutdownSignaler = runControl?.GracefulShutdownSignaler,
            ShutdownService = runControl?.ShutdownService,
            KillEntireProcessTreeOnCancel = !_environment.IsWindows(),
        };

        execution = _processExecutionFactory.CreateExecution(startInfo, options);

        try
        {
            await execution.StartAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            await execution.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new AppHostServerRunResult(_socketPath, outputCollector, execution);
    }

    internal ProcessStartInfo CreateStartInfo(
        int hostPid,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        string[]? additionalArgs = null,
        bool debug = false)
    {
        var serverPath = GetServerPath();
        var contentRootPath = _contentRootPath ?? _workingDirectory;

        var startInfo = new ProcessStartInfo(serverPath)
        {
            WorkingDirectory = contentRootPath,
            WindowStyle = ProcessWindowStyle.Minimized,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Insert "server" subcommand, then remaining args
        startInfo.ArgumentList.Add("server");
        startInfo.ArgumentList.Add("--contentRoot");
        startInfo.ArgumentList.Add(contentRootPath);

        // Add any additional arguments
        if (additionalArgs is { Length: > 0 })
        {
            foreach (var arg in additionalArgs)
            {
                startInfo.ArgumentList.Add(arg);
            }
        }

        // Configure environment
        startInfo.Environment["REMOTE_APP_HOST_SOCKET_PATH"] = _socketPath;
        startInfo.Environment[KnownConfigNames.CliLogFilePath] = _executionContext.LogFilePath;

        // Stamp the launching CLI (hostPid) as the parent under both the RemoteHost and generic CLI
        // key pairs. Resolve the start time once and pair it with the PID so the RemoteHost orphan
        // detector verifies both and does not keep the server alive against a recycled PID.
        var hostStartedUnix = ProcessStartTimeHelper.TryGetProcessStartTimeUnixMilliseconds(hostPid);
        OrphanDetectionEnvironment.Apply(startInfo.Environment, hostPid, hostStartedUnix, KnownConfigNames.RemoteAppHostProcessId, KnownConfigNames.RemoteAppHostProcessStarted);
        OrphanDetectionEnvironment.Apply(startInfo.Environment, hostPid, hostStartedUnix, KnownConfigNames.CliProcessId, KnownConfigNames.CliProcessStarted);

        IntegrationClosureEnvironment.Apply(
            (key, value) => startInfo.Environment[key] = value,
            key => startInfo.Environment.Remove(key),
            _integrationProbeManifestPath,
            _integrationLibsPath,
            _logger);

        // Set DCP and Dashboard paths from the layout
        var dcpPath = _layout.GetDcpPath();
        if (dcpPath is not null)
        {
            startInfo.Environment[BundleDiscovery.DcpPathEnvVar] = dcpPath;
        }
        else
        {
            // Without this variable the AppHost falls back to the DcpCliPath assembly metadata baked in
            // by the AppHost SDK, which points into ~/.nuget/packages. A guest-language AppHost never
            // restores that package, so the run fails with "The Aspire orchestration component is not
            // installed at <nuget path>" - a message that describes the fallback rather than the real
            // problem, which is that no layout supplied DCP. Log the real cause where the CLI logs are.
            _logger.LogWarning(
                "No layout supplied a DCP path, so {EnvironmentVariable} was not set. The AppHost will fall back to its baked-in NuGet package path, which a guest-language AppHost does not restore.",
                BundleDiscovery.DcpPathEnvVar);
        }

        // Set the dashboard path so the AppHost can locate and launch the dashboard binary
        var managedPath = _layout.GetManagedPath();
        if (managedPath is not null)
        {
            startInfo.Environment[BundleDiscovery.DashboardPathEnvVar] = managedPath;
        }

        // Apply environment variables from apphost.run.json
        if (environmentVariables is not null)
        {
            foreach (var (key, value) in environmentVariables)
            {
                startInfo.Environment[key] = value;
            }
        }

        _layoutLease?.AddEnvironment(startInfo);

        if (debug)
        {
            startInfo.Environment[KnownConfigNames.AspireLogLevel] = "Debug";
        }

        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        return startInfo;
    }

    /// <inheritdoc />
    public string GetInstanceIdentifier() => _appDirectoryPath;

    /// <inheritdoc />
    public void Dispose()
    {
        _layoutLease?.Dispose();
    }

    private static string CreateAppSettingsContent(
        List<IntegrationReference> packageRefs,
        List<string> projectRefAssemblyNames)
    {
        var atsAssemblies = new List<string> { "Aspire.Hosting" };

        foreach (var pkg in packageRefs)
        {
            if (pkg.Name.Equals("Aspire.Hosting.AppHost", StringComparison.OrdinalIgnoreCase) ||
                pkg.Name.StartsWith("Aspire.AppHost.Sdk", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!atsAssemblies.Contains(pkg.Name, StringComparer.OrdinalIgnoreCase))
            {
                atsAssemblies.Add(pkg.Name);
            }
        }

        foreach (var name in projectRefAssemblyNames)
        {
            if (!atsAssemblies.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                atsAssemblies.Add(name);
            }
        }

        var assembliesJson = string.Join(",\n      ", atsAssemblies.Select(a => $"\"{a}\""));
        return $$"""
            {
              "Logging": {
                "LogLevel": {
                  "Default": "Information",
                  "Microsoft.AspNetCore": "Warning",
                  "Aspire.Hosting.Dcp": "Warning"
                }
              },
              "AtsAssemblies": [
                {{assembliesJson}}
              ]
            }
            """;
    }

    private static async Task WriteAppSettingsAsync(string contentRootPath, string appSettingsContent, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(contentRootPath);
        await File.WriteAllTextAsync(
            Path.Combine(contentRootPath, "appsettings.json"),
            appSettingsContent,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Represents a prebuilt AppHost preparation failure with captured build output.
    /// </summary>
    private sealed class AppHostServerPrepareFailedException(string message, OutputCollector output) : Exception(message)
    {
        public OutputCollector Output { get; } = output;
    }

}
