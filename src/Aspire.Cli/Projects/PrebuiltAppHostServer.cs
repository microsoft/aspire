// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
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

    // Path to restored integration libraries (set during PrepareAsync)
    private string? _integrationLibsPath;

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
        _workingDirectory = Path.Combine(CliPathHelper.GetAspireHomeDirectory(), "bundle-hosts", pathDir);
        Directory.CreateDirectory(_workingDirectory);
    }

    /// <inheritdoc />
    public string AppDirectoryPath => _appDirectoryPath;

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
        var packageRefs = integrationList.Where(r => r.Source == IntegrationSource.Nuget).ToList();
        var projectRefs = integrationList.Where(r => r.Source == IntegrationSource.Project).ToList();
        var npmIntegrations = integrationList.Where(r => r.Source == IntegrationSource.Npm).ToList();

        try
        {
            // Resolve the configured channel (local settings.json → global config fallback)
            var channelName = await ResolveChannelNameAsync(cancellationToken);

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

                // Build a synthetic project with all package and project references
                _integrationLibsPath = await BuildIntegrationProjectAsync(
                    packageRefs, projectRefs, channelName, cancellationToken);
            }
            else if (packageRefs.Count > 0)
            {
                // NuGet-only — use the bundled NuGet service (no SDK required)
                _integrationLibsPath = await RestoreNuGetPackagesAsync(
                    packageRefs, channelName, cancellationToken);
            }

            // Generate appsettings.json after build/restore so we can use actual assembly names
            // from the build output (project references may have custom <AssemblyName>)
            var projectRefAssemblyNames = _integrationLibsPath is not null
                ? await ReadProjectRefAssemblyNamesAsync(_integrationLibsPath, cancellationToken)
                : [];
            await GenerateAppSettingsAsync(packageRefs, projectRefAssemblyNames, npmIntegrations, cancellationToken);

            return new AppHostServerPrepareResult(
                Success: true,
                Output: null,
                ChannelName: channelName,
                NeedsCodeGeneration: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to prepare prebuilt AppHost server");
            var output = new OutputCollector();
            output.AppendError($"Failed to prepare: {ex.Message}");
            return new AppHostServerPrepareResult(
                Success: false,
                Output: output,
                ChannelName: null,
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
        var sources = await GetNuGetSourcesAsync(channelName, cancellationToken);

        return await _nugetService.RestorePackagesAsync(
            packages,
            DotNetBasedAppHostServerProject.TargetFramework,
            runtimeIdentifier: RuntimeInformation.RuntimeIdentifier,
            sources: sources,
            workingDirectory: _appDirectoryPath,
            ct: cancellationToken);
    }

    /// <summary>
    /// Creates a synthetic .csproj with all package and project references,
    /// then builds it to get the full transitive DLL closure via CopyLocalLockFileAssemblies.
    /// Requires .NET SDK.
    /// </summary>
    private async Task<string> BuildIntegrationProjectAsync(
        List<IntegrationReference> packageRefs,
        List<IntegrationReference> projectRefs,
        string? channelName,
        CancellationToken cancellationToken)
    {
        var restoreDir = Path.Combine(_workingDirectory, "integration-restore");
        Directory.CreateDirectory(restoreDir);

        var outputDir = Path.Combine(restoreDir, "libs");
        // Clean stale DLLs from previous builds to prevent leftover assemblies
        // from removed integrations being picked up by the assembly resolver
        if (Directory.Exists(outputDir))
        {
            Directory.Delete(outputDir, recursive: true);
        }
        Directory.CreateDirectory(outputDir);

        // Resolve channel sources to add via RestoreAdditionalProjectSources
        IEnumerable<string>? channelSources = null;
        try
        {
            channelSources = await GetNuGetSourcesAsync(channelName, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to configure NuGet sources for integration project build");
        }

        var projectContent = GenerateIntegrationProjectFile(packageRefs, projectRefs, outputDir, channelSources);
        var projectFilePath = Path.Combine(restoreDir, "IntegrationRestore.csproj");
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
            throw new InvalidOperationException($"Failed to build integration project. Exit code: {exitCode}");
        }

        return outputDir;
    }

    /// <summary>
    /// Generates a synthetic .csproj file that references all integration packages and projects.
    /// Building this project with CopyLocalLockFileAssemblies produces the full transitive DLL closure.
    /// </summary>
    internal static string GenerateIntegrationProjectFile(
        List<IntegrationReference> packageRefs,
        List<IntegrationReference> projectRefs,
        string outputDir,
        IEnumerable<string>? additionalSources = null)
    {
        var propertyGroup = new XElement("PropertyGroup",
            new XElement("TargetFramework", DotNetBasedAppHostServerProject.TargetFramework),
            new XElement("EnableDefaultItems", "false"),
            new XElement("CopyLocalLockFileAssemblies", "true"),
            new XElement("ProduceReferenceAssembly", "false"),
            new XElement("EnableNETAnalyzers", "false"),
            new XElement("GenerateDocumentationFile", "false"),
            new XElement("OutDir", outputDir));

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
                    new XAttribute("Include", p.Path!)))));

            // Add a target that writes the resolved project reference assembly names to a file.
            // This lets us discover the actual assembly names after build (which may differ from
            // the settings.json key or csproj filename if <AssemblyName> is overridden).
            doc.Root!.Add(
                new XElement("Target",
                    new XAttribute("Name", "_WriteProjectRefAssemblyNames"),
                    new XAttribute("AfterTargets", "Build"),
                    new XElement("WriteLinesToFile",
                        new XAttribute("File", Path.Combine(outputDir, "_project-ref-assemblies.txt")),
                        new XAttribute("Lines", "@(_ResolvedProjectReferencePaths->'%(Filename)')"),
                        new XAttribute("Overwrite", "true"))));
        }

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

    /// <inheritdoc />
    public (string SocketPath, Process Process, OutputCollector OutputCollector) Run(
        int hostPid,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        string[]? additionalArgs = null,
        bool debug = false)
    {
        var serverPath = GetServerPath();

        // aspire-managed is self-contained - run directly
        var startInfo = new ProcessStartInfo(serverPath)
        {
            WorkingDirectory = _workingDirectory,
            WindowStyle = ProcessWindowStyle.Minimized,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Insert "server" subcommand, then remaining args
        startInfo.ArgumentList.Add("server");
        startInfo.ArgumentList.Add("--contentRoot");
        startInfo.ArgumentList.Add(_workingDirectory);

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

        // Pass the integration libs path so the server can resolve assemblies via AssemblyLoader
        if (_integrationLibsPath is not null)
        {
            _logger.LogDebug("Setting ASPIRE_INTEGRATION_LIBS_PATH to {Path}", _integrationLibsPath);
            startInfo.Environment["ASPIRE_INTEGRATION_LIBS_PATH"] = _integrationLibsPath;
        }
        else
        {
            _logger.LogWarning("Integration libs path is null - assemblies may not resolve correctly");
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
                _logger.LogInformation("AppHostServer: {Line}", e.Data);
                outputCollector.AppendOutput(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                _logger.LogWarning("AppHostServer: {Line}", e.Data);
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
    private async Task<List<string>> ReadProjectRefAssemblyNamesAsync(string libsPath, CancellationToken cancellationToken)
    {
        var filePath = Path.Combine(libsPath, "_project-ref-assemblies.txt");
        if (!File.Exists(filePath))
        {
            _logger.LogWarning("Project reference assembly names file not found at {Path}", filePath);
            return [];
        }

        var lines = await File.ReadAllLinesAsync(filePath, cancellationToken);
        return lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).ToList();
    }

    private async Task GenerateAppSettingsAsync(
        List<IntegrationReference> packageRefs,
        List<string> projectRefAssemblyNames,
        List<IntegrationReference> npmIntegrations,
        CancellationToken cancellationToken)
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

        var appSettingsJson = AppHostServerAppSettingsWriter.Generate(atsAssemblies, npmIntegrations);

        await File.WriteAllTextAsync(
            Path.Combine(_workingDirectory, "appsettings.json"),
            appSettingsJson,
            cancellationToken);
    }
}
