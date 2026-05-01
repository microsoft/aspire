// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
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
internal sealed class PrebuiltAppHostServer : IAppHostServerProject
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
    private readonly IConfigurationService _configurationService;
    private readonly ILogger _logger;
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
    /// <param name="configurationService">The configuration service for reading channel settings.</param>
    /// <param name="logger">The logger for diagnostic output.</param>
    public PrebuiltAppHostServer(
        string appPath,
        string socketPath,
        LayoutConfiguration layout,
        BundleNuGetService nugetService,
        IDotNetCliRunner dotNetCliRunner,
        IDotNetSdkInstaller sdkInstaller,
        IPackagingService packagingService,
        IConfigurationService configurationService,
        ILogger logger)
    {
        _appDirectoryPath = Path.GetFullPath(appPath);
        _socketPath = socketPath;
        _layout = layout;
        _nugetService = nugetService;
        _dotNetCliRunner = dotNetCliRunner;
        _sdkInstaller = sdkInstaller;
        _packagingService = packagingService;
        _configurationService = configurationService;
        _logger = logger;

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
        CancellationToken cancellationToken = default)
    {
        var integrationList = integrations.ToList();
        var packageRefs = integrationList.Where(r => r.IsPackageReference).ToList();
        var projectRefs = integrationList.Where(r => r.IsProjectReference).ToList();
        string? channelName = null;

        try
        {
            _selectedProjectLayout = null;
            _contentRootPath = _workingDirectory;
            _integrationLibsPath = null;
            _integrationProbeManifestPath = null;

            // Resolve the configured channel (local settings.json → global config fallback)
            channelName = await ResolveChannelNameAsync(cancellationToken);

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

                var channelSources = await GetNuGetSourcesAsync(channelName, cancellationToken);
                using var fileLock = await FileLock.AcquireAsync(_projectReferencePrepareLockPath, cancellationToken).ConfigureAwait(false);
                _projectLayoutStore.CleanupStagingDirectories();

                var closureManifest = await BuildIntegrationClosureManifestAsync(
                    packageRefs,
                    projectRefs,
                    channelSources,
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
                        packageRefs, channelName, cancellationToken);
                }

                var appSettingsContent = CreateAppSettingsContent(packageRefs, []);
                await WriteAppSettingsAsync(_workingDirectory, appSettingsContent, cancellationToken).ConfigureAwait(false);
            }

            return new AppHostServerPrepareResult(
                Success: true,
                Output: null,
                ChannelName: channelName,
                NeedsCodeGeneration: true);
        }
        catch (AppHostServerPrepareFailedException ex)
        {
            _logger.LogError(ex, "Failed to prepare prebuilt AppHost server");
            return new AppHostServerPrepareResult(
                Success: false,
                Output: ex.Output,
                ChannelName: channelName,
                NeedsCodeGeneration: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to prepare prebuilt AppHost server");
            var output = new OutputCollector();
            output.AppendError($"Failed to prepare: {ex.Message}");
            return new AppHostServerPrepareResult(
                Success: false,
                Output: output,
                ChannelName: channelName,
                NeedsCodeGeneration: false);
        }
    }

    /// <summary>
    /// Restores NuGet packages using the bundled NuGet service (no .NET SDK required).
    /// </summary>
    private async Task<string> RestoreNuGetPackagesAsync(
        List<IntegrationReference> packageRefs,
        string? channelName,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Restoring {Count} integration packages via bundled NuGet", packageRefs.Count);

        var packages = packageRefs.Select(r => (r.Name, r.Version!)).ToList();
        using var temporaryNuGetConfig = await TryCreateTemporaryNuGetConfigAsync(channelName, cancellationToken);
        var sources = await GetNuGetSourcesAsync(channelName, cancellationToken);

        var result = await _nugetService.RestorePackagesAsync(
            packages,
            workingDirectory: _appDirectoryPath,
            targetFramework: DotNetBasedAppHostServerProject.TargetFramework,
            runtimeIdentifier: RuntimeInformation.RuntimeIdentifier,
            sources: sources,
            nugetConfigPath: temporaryNuGetConfig?.ConfigFile.FullName,
            ct: cancellationToken).ConfigureAwait(false);
        return result.ManifestPath;
    }

    /// <summary>
    /// Creates a synthetic .csproj with all package and project references,
    /// then builds it to get the full transitive DLL closure via CopyLocalLockFileAssemblies.
    /// Requires .NET SDK.
    /// </summary>
    private async Task<AppHostServerClosureManifest> BuildIntegrationClosureManifestAsync(
        List<IntegrationReference> packageRefs,
        List<IntegrationReference> projectRefs,
        IEnumerable<string>? channelSources,
        CancellationToken cancellationToken)
    {
        var restoreDir = Path.Combine(_workingDirectory, "integration-restore");
        Directory.CreateDirectory(restoreDir);

        var projectContent = GenerateIntegrationProjectFile(packageRefs, projectRefs, restoreDir, channelSources);
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
        IEnumerable<string>? additionalSources = null)
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

        // Add channel sources without replacing the user's nuget.config
        if (additionalSources is not null)
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
                        new XAttribute("Version", p.Version));
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
    /// Resolves the configured channel name from local project config or global config.
    /// </summary>
    private async Task<string?> ResolveChannelNameAsync(CancellationToken cancellationToken)
    {
        // Check aspire.config.json first, then fall back to legacy .aspire/settings.json.
        var channelName = AspireConfigFile.Load(_appDirectoryPath)?.Channel
            ?? AspireJsonConfiguration.Load(_appDirectoryPath)?.Channel;

        // Fall back to global config
        if (string.IsNullOrEmpty(channelName))
        {
            channelName = await _configurationService.GetConfigurationAsync("channel", cancellationToken);
        }

        if (!string.IsNullOrEmpty(channelName))
        {
            _logger.LogDebug("Resolved channel: {Channel}", channelName);
        }

        return channelName;
    }

    /// <summary>
    /// Gets NuGet sources from the resolved channel for bundled restore.
    /// </summary>
    private async Task<IEnumerable<string>?> GetNuGetSourcesAsync(string? channelName, CancellationToken cancellationToken)
    {
        var sources = new List<string>();

        try
        {
            var channels = await _packagingService.GetChannelsAsync(cancellationToken);

            IEnumerable<PackageChannel> explicitChannels;
            if (!string.IsNullOrEmpty(channelName))
            {
                var matchingChannel = channels.FirstOrDefault(c => string.Equals(c.Name, channelName, StringComparison.OrdinalIgnoreCase));
                explicitChannels = matchingChannel is not null ? [matchingChannel] : channels.Where(c => c.Type == PackageChannelType.Explicit);
            }
            else
            {
                explicitChannels = channels.Where(c => c.Type == PackageChannelType.Explicit);
            }

            foreach (var channel in explicitChannels)
            {
                if (channel.Mappings is null)
                {
                    continue;
                }

                foreach (var mapping in channel.Mappings)
                {
                    if (!sources.Contains(mapping.Source, StringComparer.OrdinalIgnoreCase))
                    {
                        sources.Add(mapping.Source);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get package channels, relying on nuget.config and nuget.org fallback");
        }

        return sources.Count > 0 ? sources : null;
    }

    private async Task<TemporaryNuGetConfig?> TryCreateTemporaryNuGetConfigAsync(string? channelName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(channelName))
        {
            return null;
        }

        var channels = await _packagingService.GetChannelsAsync(cancellationToken);
        var channel = channels.FirstOrDefault(c =>
            c.Type == PackageChannelType.Explicit &&
            c.Mappings is { Length: > 0 } &&
            string.Equals(c.Name, channelName, StringComparison.OrdinalIgnoreCase));

        if (channel?.Mappings is null)
        {
            return null;
        }

        // Materializing the temp config is required for explicit channels so that
        // restore honors the channel's package source mappings. Let IO/XML failures
        // surface instead of silently falling back to the caller's unmapped sources,
        // which could otherwise restore from an unintended feed.
        return await TemporaryNuGetConfig.CreateAsync(channel.Mappings, channel.ConfigureGlobalPackagesFolder);
    }

    /// <inheritdoc />
    public (string SocketPath, Process Process, OutputCollector OutputCollector) Run(
        int hostPid,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        string[]? additionalArgs = null,
        bool debug = false)
    {
        var serverPath = GetServerPath();
        var contentRootPath = _contentRootPath ?? _workingDirectory;

        // aspire-managed is self-contained - run directly
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

        if (debug)
        {
            startInfo.Environment["Logging__LogLevel__Default"] = "Debug";
        }

        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        var process = Process.Start(startInfo)!;

        var outputCollector = new OutputCollector();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                _logger.LogTrace("PrebuiltAppHostServer({ProcessId}) stdout: {Line}", process.Id, e.Data);
                outputCollector.AppendOutput(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                _logger.LogTrace("PrebuiltAppHostServer({ProcessId}) stderr: {Line}", process.Id, e.Data);
                outputCollector.AppendError(e.Data);
            }
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return (_socketPath, process, outputCollector);
    }

    /// <inheritdoc />
    public string GetInstanceIdentifier() => _appDirectoryPath;

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
