// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Aspire.Cli.Bundles;
using Aspire.Cli.Configuration;
using Aspire.Cli.DotNet;
using Aspire.Cli.Layout;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Projects;

/// <summary>
/// Manages a pre-built AppHost server from the Aspire bundle layout.
/// This is used when running in bundle mode (without .NET SDK) to avoid
/// dynamic project generation and building.
/// </summary>
internal sealed class PrebuiltAppHostServer : IAppHostServerProject, IDisposable
{
    internal const string ClosureMetadataFileName = "closure-metadata.txt";
    internal const string ClosureSourcesFileName = "closure-sources.txt";
    internal const string ClosureTargetsFileName = "closure-targets.txt";
    internal const string ClosureManifestFileName = "closure-manifest.txt";
    internal const string IntegrationProjectFileName = "IntegrationRestore.csproj";
    internal const string ProjectRefAssemblyNamesFileName = "project-ref-assemblies.txt";

    private const string ProjectAssetsFileName = "project.assets.json";

    private readonly string _appDirectoryPath;
    private readonly string _socketPath;
    private readonly LayoutConfiguration _layout;
    private readonly BundleNuGetService _nugetService;
    private readonly IDotNetCliRunner _dotNetCliRunner;
    private readonly IDotNetSdkInstaller _sdkInstaller;
    private readonly IPackagingService _packagingService;
    private readonly CliExecutionContext _executionContext;
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
        _logger = logger;
        _layoutLease = layoutLease;

        // Create a working directory for this app host session
        var pathHash = SHA256.HashData(Encoding.UTF8.GetBytes(_appDirectoryPath));
        var pathDir = Convert.ToHexString(pathHash)[..12].ToLowerInvariant();
        var integrationCacheDirectory = ConfigurationHelper.GetIntegrationCacheDirectory(new DirectoryInfo(_appDirectoryPath));
        _workingDirectory = Path.Combine(integrationCacheDirectory.FullName, "apphosts", pathDir);
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
                effectivePackageSourceOverride = await ResolveLocalPackageSourceOverrideAsync(requestedChannel, cancellationToken).ConfigureAwait(false);
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

                using var fileLock = await FileLock.AcquireAsync(_projectReferencePrepareLockPath, cancellationToken).ConfigureAwait(false);
                _projectLayoutStore.CleanupStagingDirectories();

                var closureManifest = await BuildIntegrationClosureManifestAsync(
                    packageRefs,
                    projectRefs,
                    requestedChannel,
                    effectivePackageSourceOverride,
                    cancellationToken).ConfigureAwait(false);

                if (closureManifest.Entries.Any(static entry => entry.IsPackageBacked))
                {
                    _integrationProbeManifestPath = Path.Combine(_workingDirectory, IntegrationPackageProbeManifest.FileName);
                    await IntegrationPackageProbeManifest.WriteAsync(
                        _integrationProbeManifestPath,
                        closureManifest.CreatePackageProbeManifest(),
                        cancellationToken).ConfigureAwait(false);
                }

                _selectedProjectLayout = await _projectLayoutStore.GetOrCreateAsync(closureManifest, cancellationToken).ConfigureAwait(false);
                if (_selectedProjectLayout is not null)
                {
                    _integrationLibsPath = _selectedProjectLayout.IntegrationLibsPath;
                }

                await WriteAppSettingsAsync(_workingDirectory, closureManifest.AppSettingsContent, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (packageRefs.Count > 0)
                {
                    // NuGet-only — use the bundled NuGet service (no SDK required)
                    _integrationProbeManifestPath = await RestoreNuGetPackagesAsync(
                        packageRefs, requestedChannel, effectivePackageSourceOverride, cancellationToken);
                }

                var appSettingsContent = CreateAppSettingsContent(packageRefs, []);
                await WriteAppSettingsAsync(_workingDirectory, appSettingsContent, cancellationToken).ConfigureAwait(false);
            }

            return new AppHostServerPrepareResult(
                Success: true,
                Output: null,
                ChannelName: requestedChannel,
                NeedsCodeGeneration: true);
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
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Restoring {Count} integration packages via bundled NuGet", packageRefs.Count);

        var useExactPackageVersions = !string.IsNullOrWhiteSpace(packageSourceOverride);
        var packages = packageRefs
            .Select(r => (r.Name, Version: GetRestoreVersion(r.Name, r.Version!, useExactPackageVersions)))
            .ToList();
        using var temporaryNuGetConfig = await TryCreateTemporaryNuGetConfigAsync(requestedChannel, packageSourceOverride, cancellationToken);
        var sources = await GetNuGetSourcesAsync(requestedChannel, packageSourceOverride, cancellationToken);

        return await _nugetService.RestorePackagesAsync(
            packages,
            workingDirectory: _appDirectoryPath,
            targetFramework: DotNetBasedAppHostServerProject.TargetFramework,
            runtimeIdentifier: RuntimeInformation.RuntimeIdentifier,
            sources: sources,
            nugetConfigPath: temporaryNuGetConfig?.ConfigFile.FullName,
            ct: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a synthetic .csproj with all package and project references,
    /// then builds it to get the full transitive DLL closure via CopyLocalLockFileAssemblies.
    /// Requires .NET SDK.
    /// </summary>
    private async Task<AppHostServerClosureManifest> BuildIntegrationClosureManifestAsync(
        List<IntegrationReference> packageRefs,
        List<IntegrationReference> projectRefs,
        string? requestedChannel,
        string? packageSourceOverride,
        CancellationToken cancellationToken)
    {
        var restoreDir = Path.Combine(_workingDirectory, "integration-restore");
        Directory.CreateDirectory(restoreDir);

        // Only synthesize a temp NuGet.config (replacing nuget.config discovery via
        // RestoreConfigFile) when an explicit --source or auto-discovered local channel source
        // is in play. The explicit-channel-no-override path keeps the user's ambient
        // nuget.config in place and contributes channel mappings additively via
        // RestoreAdditionalProjectSources so private/internal feeds the user has configured
        // remain reachable for non-Aspire transitives during project-ref restore.
        using var temporaryNuGetConfig = !string.IsNullOrWhiteSpace(packageSourceOverride)
            ? await TryCreateTemporaryNuGetConfigAsync(requestedChannel, packageSourceOverride, cancellationToken)
            : null;
        var channelSources = temporaryNuGetConfig is null
            ? await GetNuGetSourcesAsync(requestedChannel, packageSourceOverride: null, cancellationToken)
            : null;
        var projectContent = GenerateIntegrationProjectFile(
            packageRefs,
            projectRefs,
            restoreDir,
            channelSources,
            useExactPackageVersions: !string.IsNullOrWhiteSpace(packageSourceOverride),
            restoreConfigFile: temporaryNuGetConfig?.ConfigFile.FullName);
        var projectFilePath = Path.Combine(restoreDir, IntegrationProjectFileName);
        await File.WriteAllTextAsync(projectFilePath, projectContent, cancellationToken);

        // Write a Directory.Packages.props to opt out of Central Package Management
        var directoryPackagesProps = """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
              </PropertyGroup>
            </Project>
            """;
        await File.WriteAllTextAsync(
            Path.Combine(restoreDir, "Directory.Packages.props"), directoryPackagesProps, cancellationToken);

        // Also write an empty Directory.Build.props/targets to prevent parent imports
        await File.WriteAllTextAsync(
            Path.Combine(restoreDir, "Directory.Build.props"), "<Project />", cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(restoreDir, "Directory.Build.targets"), "<Project />", cancellationToken);

        _logger.LogDebug("Building integration project with {PackageCount} packages and {ProjectCount} project references",
            packageRefs.Count, projectRefs.Count);

        var buildOutput = new OutputCollector();
        var exitCode = await _dotNetCliRunner.BuildAsync(
            new FileInfo(projectFilePath),
            noRestore: false,
            new ProcessInvocationOptions
            {
                StandardOutputCallback = buildOutput.AppendOutput,
                StandardErrorCallback = buildOutput.AppendError
            },
            cancellationToken);

        if (exitCode != 0)
        {
            var outputLines = string.Join(Environment.NewLine, buildOutput.GetLines().Select(l => l.Line));
            _logger.LogError("Integration project build failed. Output:\n{BuildOutput}", outputLines);
            throw new AppHostServerPrepareFailedException("Failed to build integration project.", buildOutput);
        }

        var closureSourcesPath = Path.Combine(restoreDir, ClosureSourcesFileName);
        var closureMetadataPath = Path.Combine(restoreDir, ClosureMetadataFileName);
        var closureTargetsPath = Path.Combine(restoreDir, ClosureTargetsFileName);

        var sourcePaths = await ReadManifestFileAsync(closureSourcesPath, cancellationToken).ConfigureAwait(false);
        var metadataLines = await ReadManifestFileAsync(closureMetadataPath, cancellationToken).ConfigureAwait(false);
        var targetPaths = await ReadManifestFileAsync(closureTargetsPath, cancellationToken).ConfigureAwait(false);
        if (sourcePaths.Count != metadataLines.Count || sourcePaths.Count != targetPaths.Count)
        {
            throw new InvalidOperationException(
                $"Integration closure manifest is inconsistent. Sources: {sourcePaths.Count}, metadata: {metadataLines.Count}, targets: {targetPaths.Count}.");
        }

        var projectRefAssemblyNames = await ReadProjectRefAssemblyNamesAsync(
            Path.Combine(restoreDir, ProjectRefAssemblyNamesFileName),
            cancellationToken).ConfigureAwait(false);
        var appSettingsContent = CreateAppSettingsContent(packageRefs, projectRefAssemblyNames);
        var packageFingerprints = await ReadPackageFingerprintsAsync(
            Path.Combine(restoreDir, "obj", ProjectAssetsFileName),
            cancellationToken).ConfigureAwait(false);

        var closureEntries = new List<AppHostServerClosureSource>(sourcePaths.Count);
        for (var i = 0; i < sourcePaths.Count; i++)
        {
            var metadata = ParseClosureMetadata(metadataLines[i]);
            var packageSha512 = TryGetPackageFingerprint(packageFingerprints, metadata);

            closureEntries.Add(new AppHostServerClosureSource(
                sourcePaths[i],
                targetPaths[i],
                metadata.NuGetPackageId,
                metadata.NuGetPackageVersion,
                metadata.PathInPackage,
                packageSha512,
                metadata.AssetType));
        }

        var closureManifest = AppHostServerClosureManifest.Create(closureEntries, appSettingsContent, cancellationToken);
        await File.WriteAllLinesAsync(
            Path.Combine(restoreDir, ClosureManifestFileName),
            closureManifest.GetManifestLines(),
            cancellationToken).ConfigureAwait(false);
        return closureManifest;
    }

    /// <summary>
    /// Generates a synthetic .csproj file that references all integration packages and projects.
    /// Building this project with CopyLocalLockFileAssemblies produces the full transitive DLL closure.
    /// </summary>
    internal static string GenerateIntegrationProjectFile(
        List<IntegrationReference> packageRefs,
        List<IntegrationReference> projectRefs,
        string restoreDir,
        IEnumerable<string>? additionalSources = null,
        bool useExactPackageVersions = false,
        string? restoreConfigFile = null)
    {
        var propertyGroup = new XElement("PropertyGroup",
            new XElement("TargetFramework", DotNetBasedAppHostServerProject.TargetFramework),
            new XElement("EnableDefaultItems", "false"),
            new XElement("CopyLocalLockFileAssemblies", "true"),
            new XElement("ProduceReferenceAssembly", "false"),
            new XElement("EnableNETAnalyzers", "false"),
            new XElement("GenerateDocumentationFile", "false"),
            new XElement("AspireClosureMetadataFile", Path.Combine(restoreDir, ClosureMetadataFileName)),
            new XElement("AspireClosureSourcesFile", Path.Combine(restoreDir, ClosureSourcesFileName)),
            new XElement("AspireClosureTargetsFile", Path.Combine(restoreDir, ClosureTargetsFileName)),
            new XElement("AspireProjectRefAssemblyNamesFile", Path.Combine(restoreDir, ProjectRefAssemblyNamesFileName)));

        if (!string.IsNullOrWhiteSpace(restoreConfigFile))
        {
            // RestoreAdditionalProjectSources can add feeds, but it cannot carry package source
            // mappings. Use the temp NuGet.config so Aspire* packages stay pinned to the
            // explicit source while non-Aspire dependencies can use fallback sources.
            propertyGroup.Add(new XElement("RestoreConfigFile", restoreConfigFile));
        }
        // Add channel sources without replacing the user's nuget.config.
        else if (additionalSources is not null)
        {
            var sourceList = string.Join(";", additionalSources);
            if (sourceList.Length > 0)
            {
                propertyGroup.Add(new XElement("RestoreAdditionalProjectSources", sourceList));
            }
        }

        var doc = new XDocument(
            new XElement("Project",
                new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                propertyGroup));

        if (packageRefs.Count > 0)
        {
            doc.Root!.Add(new XElement("ItemGroup",
                packageRefs.Select(p =>
                {
                    if (p.Version is null)
                    {
                        throw new InvalidOperationException($"Package reference '{p.Name}' is missing a version.");
                    }
                    return new XElement("PackageReference",
                        new XAttribute("Include", p.Name),
                        new XAttribute("Version", GetRestoreVersion(p.Name, p.Version, useExactPackageVersions)));
                })));
        }

        if (projectRefs.Count > 0)
        {
            doc.Root!.Add(new XElement("ItemGroup",
                projectRefs.Select(p => new XElement("ProjectReference",
                    new XAttribute("Include", p.ProjectPath!)))));
        }

        doc.Root!.Add(
            new XElement("Target",
                new XAttribute("Name", "_WriteAspireProjectRefAssemblyNames"),
                new XAttribute("AfterTargets", "Build"),
                new XElement("WriteLinesToFile",
                    new XAttribute("File", "$(AspireProjectRefAssemblyNamesFile)"),
                    new XAttribute("Lines", "@(_ResolvedProjectReferencePaths->'%(Filename)')"),
                    new XAttribute("Overwrite", "true"),
                    new XAttribute("WriteOnlyWhenDifferent", "true"))));

        doc.Root!.Add(
            new XElement("Target",
                new XAttribute("Name", "_WriteAspireClosureManifest"),
                new XAttribute("AfterTargets", "Build"),
                new XAttribute("DependsOnTargets", "ResolveLockFileCopyLocalFiles"),
                new XElement("WriteLinesToFile",
                    new XAttribute("File", "$(AspireClosureSourcesFile)"),
                    new XAttribute("Lines", "@(ReferenceCopyLocalPaths->'%(FullPath)')"),
                    new XAttribute("Overwrite", "true"),
                    new XAttribute("WriteOnlyWhenDifferent", "true")),
                new XElement("WriteLinesToFile",
                    new XAttribute("File", "$(AspireClosureMetadataFile)"),
                    new XAttribute("Lines", "@(ReferenceCopyLocalPaths->'%(NuGetPackageId)|%(NuGetPackageVersion)|%(PathInPackage)|%(AssetType)')"),
                    new XAttribute("Overwrite", "true"),
                    new XAttribute("WriteOnlyWhenDifferent", "true")),
                new XElement("WriteLinesToFile",
                    new XAttribute("File", "$(AspireClosureTargetsFile)"),
                    new XAttribute("Lines", "@(ReferenceCopyLocalPaths->'%(DestinationSubDirectory)%(Filename)%(Extension)')"),
                    new XAttribute("Overwrite", "true"),
                    new XAttribute("WriteOnlyWhenDifferent", "true"))));

        return doc.ToString();
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

    /// <summary>
    /// Throws when the caller asked for the staging channel but the running CLI's packaging
    /// service refuses to synthesize one (daily/local/pr-<c>N</c> identity without
    /// <c>overrideStagingFeed</c> or the <c>StagingChannelEnabled</c> feature flag). Surfaces
    /// the same actionable reason the <c>update</c> and <c>new</c> commands display so the
    /// bundled AppHost restore path doesn't silently downgrade to the daily feed.
    /// </summary>
    private void ThrowIfStagingUnavailable(string? requestedChannel)
    {
        if (!string.Equals(requestedChannel, PackageChannelNames.Staging, StringComparisons.ChannelName))
        {
            return;
        }

        var reason = _packagingService.GetStagingChannelUnavailableReason();
        if (reason is not null)
        {
            throw new InvalidOperationException(reason);
        }
    }

    /// <summary>
    /// Gets NuGet sources from the resolved channel for bundled restore.
    /// </summary>
    internal async Task<IEnumerable<string>?> GetNuGetSourcesAsync(string? requestedChannel, string? packageSourceOverride, CancellationToken cancellationToken)
    {
        // Refuse to silently downgrade staging restores to the shared daily feed when the running
        // CLI cannot synthesize a real staging channel (daily/local/pr-<N>). PackagingService omits
        // the staging channel in that case; without this check the lookup below falls through to
        // "all explicit channels" — which on a daily CLI is the shared daily feed — and restore
        // silently succeeds against the wrong feed. Surfacing the actionable
        // GetStagingChannelUnavailableReason() mirrors UpdateCommand/NewCommand and closes the
        // bundled-AppHost arm of https://github.com/microsoft/aspire/issues/16652.
        ThrowIfStagingUnavailable(requestedChannel);

        var sources = new List<string>();

        if (!string.IsNullOrWhiteSpace(packageSourceOverride))
        {
            sources.Add(packageSourceOverride);
        }

        try
        {
            // When --source is set without a specific channel, do NOT fold in every explicit
            // channel's sources: each built-in channel contributes its own Aspire* feed, and
            // letting all of them through would give NuGet multiple co-eligible sources for
            // Aspire packages and silently defeat the override. The temp NuGet.config below
            // emits PSM that constrains Aspire packages to the override; this list only needs
            // the override (plus a NuGet.org fallback) for non-Aspire transitives.
            var channels = !string.IsNullOrWhiteSpace(packageSourceOverride) && string.IsNullOrEmpty(requestedChannel)
                ? []
                : await GetExplicitRestoreChannelsAsync(requestedChannel, cancellationToken);
            var hasOverride = !string.IsNullOrWhiteSpace(packageSourceOverride);
            var matchedChannelHasAllPackagesMapping = false;
            foreach (var channel in channels)
            {
                if (channel.Mappings is null)
                {
                    continue;
                }

                foreach (var mapping in channel.Mappings)
                {
                    // Stay consistent with TryCreateTemporaryNuGetConfigAsync, which drops the
                    // matched channel's Aspire* mapping in the override branch: the bundled
                    // restore tool treats `--source` CLI args as co-eligible with config
                    // mappings, so re-adding the channel's Aspire feed here would silently
                    // defeat the override even though the temp NuGet.config's PSM tries to
                    // pin Aspire* to the override exclusively.
                    if (hasOverride && mapping.PackageFilter.StartsWith("Aspire", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (mapping.PackageFilter == PackageMapping.AllPackages)
                    {
                        matchedChannelHasAllPackagesMapping = true;
                    }

                    if (!sources.Contains(mapping.Source, StringComparer.OrdinalIgnoreCase))
                    {
                        sources.Add(mapping.Source);
                    }
                }
            }

            // Mirror the temp NuGet.config's catch-all decision: it adds `* -> NuGet.org`
            // only when the matched channel did not supply its own AllPackages mapping. The
            // --source argument list must agree so non-Aspire transitives have the same
            // catch-all source in both views.
            if (hasOverride && !matchedChannelHasAllPackagesMapping &&
                !sources.Contains(PackageSources.NuGetOrg, StringComparer.OrdinalIgnoreCase))
            {
                sources.Add(PackageSources.NuGetOrg);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get package channels, relying on nuget.config and nuget.org fallback");
        }

        return sources.Count > 0 ? sources : null;
    }

    internal async Task<TemporaryNuGetConfig?> TryCreateTemporaryNuGetConfigAsync(string? requestedChannel, string? packageSourceOverride, CancellationToken cancellationToken)
    {
        // Keep staging refusal consistent across both temp-config branches. The project-reference
        // restore path skips GetNuGetSourcesAsync when a temp config exists, so this method must
        // surface the actionable staging-unavailable reason before building any override config.
        ThrowIfStagingUnavailable(requestedChannel);

        if (!string.IsNullOrWhiteSpace(packageSourceOverride))
        {
            // Treat an explicit --source value as the preferred source for Aspire packages.
            // Build a temporary NuGet.config that routes Aspire* there, optionally preserves
            // non-Aspire channel mappings, and leaves a fallback source for non-Aspire deps.
            PackageChannel? matchedChannel = null;
            var configureGlobalPackagesFolder = false;

            try
            {
                // Only fold in mappings from an explicitly-requested, matched channel. Falling
                // back to "all explicit channels" here would pull in every built-in channel's
                // Aspire* mapping pointing at its own feed; NuGet would treat all of them as
                // co-eligible sources for Aspire packages and silently defeat the override.
                if (!string.IsNullOrEmpty(requestedChannel))
                {
                    var packageChannels = await _packagingService.GetChannelsAsync(cancellationToken, requestedChannel);
                    matchedChannel = packageChannels.FirstOrDefault(c =>
                        string.Equals(c.Name, requestedChannel, StringComparisons.ChannelName));
                    if (matchedChannel is not null)
                    {
                        configureGlobalPackagesFolder |= matchedChannel.ConfigureGlobalPackagesFolder;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get package channels while creating source override NuGet.config");
            }

            return await TemporaryNuGetConfig.CreateAsync(
                PackageSourceOverrideMappings.Create(packageSourceOverride, matchedChannel),
                configureGlobalPackagesFolder,
                configureGlobalPackagesFolder ? ResolveStableGlobalPackagesFolder(packageSourceOverride) : null);
        }

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
            // Mirror the defensive catch in the override branch above and in
            // ResolveLocalPackageSourceOverrideAsync / GetNuGetSourcesAsync: a transient
            // packaging-service failure must degrade to the ambient nuget.config + the
            // caller's separately resolved channel-source list, rather than failing the
            // whole PrepareAsync. Returning null skips the PSM-bearing temp config; for
            // non-staging channels the caller still gets channel sources via
            // GetNuGetSourcesAsync (which catches), and for staging the unavailable-reason
            // refusal above has already short-circuited before we reach this point.
            _logger.LogWarning(ex, "Failed to get package channels while creating channel NuGet.config for '{Channel}'.", requestedChannel);
            return null;
        }

        if (channel?.Mappings is null)
        {
            return null;
        }

        // Skip PSM only when the resolved channel is the local hive — that hive is a transient
        // dev-build artifact with no real package mappings, so emitting PSM for it would just
        // constrain restore to an empty source set. For every other channel (stable, staging,
        // daily, pr-*) PSM must emit so restore honours the channel's package source mappings —
        // regardless of which CLI identity (CliExecutionContext.IdentityChannel) is running.
        // Keying on the resolved channel.Name (rather than the input requestedChannel) is robust
        // to alias/normalization in the channel lookup above.
        if (string.Equals(channel.Name, PackageChannelNames.Local, StringComparisons.ChannelName))
        {
            return null;
        }

        // Materializing the temp config is required for explicit channels so that
        // restore honors the channel's package source mappings. Let IO/XML failures
        // surface instead of silently falling back to the caller's unmapped sources,
        // which could otherwise restore from an unintended feed.
        return await TemporaryNuGetConfig.CreateAsync(
            channel.Mappings,
            channel.ConfigureGlobalPackagesFolder,
            channel.ConfigureGlobalPackagesFolder ? ResolveStableGlobalPackagesFolder(GetPrimaryFeedUrl(channel.Mappings)) : null);
    }

    /// <summary>
    /// Returns the absolute <c>globalPackagesFolder</c> path to write into a temporary NuGet.config
    /// when the resolved channel asks for per-build cache isolation (today: <c>staging</c>).
    /// </summary>
    /// <remarks>
    /// The default <see cref="NuGetConfigMerger.DefaultGlobalPackagesFolderValue"/> is a relative
    /// <c>.nugetpackages</c> path that NuGet resolves next to the nuget.config it came from. For
    /// the <see cref="NuGetConfigMerger"/> workspace-merge flow that's fine — the merged config is
    /// persistent. For <see cref="PrebuiltAppHostServer"/>'s <see cref="TemporaryNuGetConfig"/>
    /// the config file lives in a Directory.CreateTempSubdirectory("aspire-nuget-config") folder
    /// that <see cref="TemporaryNuGetConfig.Dispose"/> recursively deletes after restore. NuGet
    /// would have just populated <c>&lt;temp&gt;/.nugetpackages/&lt;id&gt;/&lt;version&gt;/</c>
    /// with the staging assemblies, <see cref="NuGet.BundleNuGetService"/> would have baked those
    /// paths into <c>integration-package-probe-manifest.json</c>, and aspire-managed would then
    /// try to load assemblies the dispose just removed — observed as a hang during DI / assembly
    /// loading on macOS osx-arm64 polyglot staging builds. Anchoring the override at a stable
    /// per-build location keeps the cached packages alive for as long as any manifest references
    /// them.
    ///
    /// The cache lives under <see cref="CliExecutionContext.AspireHomeDirectory"/> (i.e. the
    /// <c>ASPIRE_HOME</c> override when set, otherwise <c>~/.aspire</c>) rather than under
    /// <see cref="_workingDirectory"/> so that two AppHosts running on the same machine against
    /// the same staging build can share a single restore — the unit of cache isolation here is
    /// the staging build, not the individual restore command.
    ///
    /// The cache subdirectory is keyed by a truncated hash of the resolved feed URL (first 8
    /// hex chars of <see cref="System.IO.Hashing.XxHash3"/> over the trimmed/lower-cased URL).
    /// Two staging builds of the same release branch — which share the same stable-shaped semver
    /// (e.g. <c>13.4.0</c>) but ship from different darc feeds — therefore each get their own
    /// cache. A user pointing the same CLI at multiple <c>overrideStagingFeed</c> values during
    /// dev/test also gets a distinct cache per feed, instead of one bucket silently shared across
    /// feeds. NuGet identifies packages by <c>(id, version)</c> only, so without that per-feed
    /// key the second feed's restore would silently reuse the first feed's now-stale
    /// <c>13.4.0</c> assemblies. When <paramref name="feedUrl"/> is null or empty (defensive —
    /// both call sites currently always pass a real URL) the key falls back to <c>"default"</c>
    /// so the path is still well-formed.
    /// </remarks>
    private string ResolveStableGlobalPackagesFolder(string? feedUrl)
    {
        var cacheKey = CliPathHelper.ComputeStagingFeedCacheKey(feedUrl) ?? "default";
        return Path.Combine(
            CliPathHelper.GetStagingNuGetPackagesDirectory(_executionContext.AspireHomeDirectory),
            cacheKey);
    }

    /// <summary>
    /// Returns the URL we use as the cache-key input when materializing a temp nuget.config from
    /// a <see cref="PackageChannel"/>. Prefers the explicit <c>Aspire*</c> mapping (the staging
    /// channel's primary feed and the one whose restored assemblies actually need cache
    /// isolation), falling back to the first mapping for forward compatibility with channel
    /// shapes we don't yet emit.
    /// </summary>
    private static string GetPrimaryFeedUrl(PackageMapping[] mappings)
    {
        var aspire = mappings.FirstOrDefault(m =>
            string.Equals(m.PackageFilter, "Aspire*", StringComparison.OrdinalIgnoreCase));
        return aspire?.Source ?? mappings[0].Source;
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
            // `aspire new` into a hard failure. Returning null falls through to the existing
            // ambient + channel-sources path, matching the defensive catches in
            // TryCreateTemporaryNuGetConfigAsync and GetNuGetSourcesAsync.
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
                UrlHelper.IsHttpUrl(mapping.Source) ||
                !Directory.Exists(mapping.Source))
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

    private async Task<IEnumerable<PackageChannel>> GetExplicitRestoreChannelsAsync(string? requestedChannel, CancellationToken cancellationToken)
    {
        var channels = await _packagingService.GetChannelsAsync(cancellationToken, requestedChannel);
        if (!string.IsNullOrEmpty(requestedChannel))
        {
            var matchingChannel = channels.FirstOrDefault(c => string.Equals(c.Name, requestedChannel, StringComparisons.ChannelName));
            if (matchingChannel is not null)
            {
                return [matchingChannel];
            }
        }

        return channels.Where(c => c.Type == PackageChannelType.Explicit).ToArray();
    }

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
    public (string SocketPath, Process Process, OutputCollector OutputCollector) Run(
        int hostPid,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        string[]? additionalArgs = null,
        bool debug = false)
    {
        var startInfo = CreateStartInfo(hostPid, environmentVariables, additionalArgs, debug);

        var process = Process.Start(startInfo)!;

        var outputCollector = new OutputCollector();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                // Promoted from LogTrace to LogDebug so that apphost-server stdout reaches the
                // CLI's on-disk log under the default file-logger filter (Debug). Previously
                // these lines were dropped entirely, which made apphost-side warnings
                // (for example, "LoaderExceptions" from the type-discovery path) invisible to
                // anyone diagnosing a "no code generator found" / "no language support found"
                // error. See https://github.com/microsoft/aspire/issues/16729.
                _logger.LogDebug("PrebuiltAppHostServer({ProcessId}) stdout: {Line}", process.Id, e.Data);
                outputCollector.AppendOutput(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                // Promoted from LogTrace to LogInformation so that apphost-server stderr is
                // visible at the default console log level (Information). Stderr is reserved
                // for genuine problems in well-behaved server processes, so surfacing it
                // by default is appropriate. See https://github.com/microsoft/aspire/issues/16729.
                _logger.LogInformation("PrebuiltAppHostServer({ProcessId}) stderr: {Line}", process.Id, e.Data);
                outputCollector.AppendError(e.Data);
            }
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return (_socketPath, process, outputCollector);
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
        startInfo.Environment["REMOTE_APP_HOST_PID"] = hostPid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        startInfo.Environment[KnownConfigNames.CliProcessId] = hostPid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        startInfo.Environment[KnownConfigNames.CliLogFilePath] = _executionContext.LogFilePath;

        if (_integrationLibsPath is not null)
        {
            _logger.LogDebug("Setting {EnvironmentVariable} to {Path}", KnownConfigNames.IntegrationLibsPath, _integrationLibsPath);
            startInfo.Environment[KnownConfigNames.IntegrationLibsPath] = _integrationLibsPath;
        }
        else
        {
            startInfo.Environment.Remove(KnownConfigNames.IntegrationLibsPath);
        }

        if (_integrationProbeManifestPath is not null)
        {
            _logger.LogDebug(
                "Setting {EnvironmentVariable} to {Path}",
                KnownConfigNames.IntegrationProbeManifestPath,
                _integrationProbeManifestPath);
            startInfo.Environment[KnownConfigNames.IntegrationProbeManifestPath] = _integrationProbeManifestPath;
        }
        else
        {
            startInfo.Environment.Remove(KnownConfigNames.IntegrationProbeManifestPath);
        }

        // Set DCP and Dashboard paths from the layout
        var dcpPath = _layout.GetDcpPath();
        if (dcpPath is not null)
        {
            startInfo.Environment[BundleDiscovery.DcpPathEnvVar] = dcpPath;
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

    /// <summary>
    /// Reads the project reference assembly names written by the MSBuild target during build.
    /// </summary>
    private async Task<List<string>> ReadProjectRefAssemblyNamesAsync(string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            _logger.LogWarning("Project reference assembly names file not found at {Path}", filePath);
            return [];
        }

        var lines = await File.ReadAllLinesAsync(filePath, cancellationToken);
        return lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).ToList();
    }

    private static async Task<List<string>> ReadManifestFileAsync(string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            throw new InvalidOperationException($"Integration closure manifest file '{filePath}' was not found after build.");
        }

        var lines = await File.ReadAllLinesAsync(filePath, cancellationToken).ConfigureAwait(false);
        return lines.Where(static line => !string.IsNullOrWhiteSpace(line)).Select(static line => line.Trim()).ToList();
    }

    private static ClosureMetadata ParseClosureMetadata(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var parts = line.Split('|', 4);
        if (parts.Length != 4)
        {
            throw new InvalidOperationException($"Integration closure metadata line '{line}' is invalid.");
        }

        return new ClosureMetadata(
            NormalizeClosureMetadataValue(parts[0]),
            NormalizeClosureMetadataValue(parts[1]),
            NormalizeClosureMetadataValue(parts[2]),
            NormalizeClosureMetadataValue(parts[3]));
    }

    private static string? NormalizeClosureMetadataValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static async Task<Dictionary<string, string>> ReadPackageFingerprintsAsync(string assetsFilePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(assetsFilePath))
        {
            throw new InvalidOperationException($"Integration assets file '{assetsFilePath}' was not found after build.");
        }

        await using var stream = File.OpenRead(assetsFilePath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        var packageFingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!document.RootElement.TryGetProperty("libraries", out var libraries))
        {
            return packageFingerprints;
        }

        foreach (var library in libraries.EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!library.Value.TryGetProperty("type", out var typeElement) ||
                !string.Equals(typeElement.GetString(), "package", StringComparison.OrdinalIgnoreCase) ||
                !library.Value.TryGetProperty("sha512", out var sha512Element))
            {
                continue;
            }

            var sha512 = sha512Element.GetString();
            if (string.IsNullOrWhiteSpace(sha512) ||
                TryParsePackageFingerprintKey(library.Name) is not { } packageKey)
            {
                continue;
            }

            packageFingerprints[CreatePackageFingerprintKey(packageKey.PackageId, packageKey.PackageVersion)] = sha512;
        }

        return packageFingerprints;
    }

    private static string? TryGetPackageFingerprint(
        IReadOnlyDictionary<string, string> packageFingerprints,
        ClosureMetadata metadata)
    {
        if (metadata.NuGetPackageId is null ||
            metadata.NuGetPackageVersion is null ||
            metadata.PathInPackage is null)
        {
            return null;
        }

        return packageFingerprints.TryGetValue(
            CreatePackageFingerprintKey(metadata.NuGetPackageId, metadata.NuGetPackageVersion),
            out var packageFingerprint)
            ? packageFingerprint
            : null;
    }

    private static string CreatePackageFingerprintKey(string packageId, string packageVersion)
    {
        return $"{packageId}/{packageVersion}";
    }

    private static PackageFingerprintKey? TryParsePackageFingerprintKey(string libraryName)
    {
        var separatorIndex = libraryName.IndexOf('/');
        if (separatorIndex <= 0 || separatorIndex == libraryName.Length - 1)
        {
            return null;
        }

        return new PackageFingerprintKey(
            libraryName[..separatorIndex],
            libraryName[(separatorIndex + 1)..]);
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
    private readonly record struct ClosureMetadata(
        string? NuGetPackageId,
        string? NuGetPackageVersion,
        string? PathInPackage,
        string? AssetType);

    private readonly record struct PackageFingerprintKey(
        string PackageId,
        string PackageVersion);

    private sealed class AppHostServerPrepareFailedException(string message, OutputCollector output) : Exception(message)
    {
        public OutputCollector Output { get; } = output;
    }
}
