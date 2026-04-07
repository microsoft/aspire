// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Aspire.Cli.Configuration;
using Aspire.Cli.DotNet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Projects;

/// <summary>
/// AppHost server project for local Aspire development that uses the .NET SDK to build.
/// Uses project references to the local Aspire repository (ASPIRE_REPO_ROOT).
/// </summary>
internal sealed class DotNetBasedAppHostServerProject : IAppHostServerProject
{
    private const string ProjectHashFileName = ".projecthash";
    private const string AppsFolder = "hosts";
    public const string ProjectFileName = "AppHostServer.csproj";
    private const string ProjectDllName = "AppHostServer.dll";
    internal const string TargetFramework = "net10.0";
    public const string BuildFolder = "build";
    private const string AssemblyName = "AppHostServer";

    /// <summary>
    /// Gets the default Aspire SDK version based on the CLI version.
    /// </summary>
    public static string DefaultSdkVersion => VersionHelper.GetDefaultSdkVersion();

    private readonly string _projectModelPath;
    private readonly string _appPath;
    private readonly string _socketPath;
    private readonly string _userSecretsId;
    private readonly string _repoRoot;
    private readonly IDotNetCliRunner _dotNetCliRunner;
    private readonly IPackagingService _packagingService;
    private readonly IConfigurationService _configurationService;
    private readonly ILogger _logger;

    public DotNetBasedAppHostServerProject(
        string appPath,
        string socketPath,
        string repoRoot,
        IDotNetCliRunner dotNetCliRunner,
        IPackagingService packagingService,
        IConfigurationService configurationService,
        ILogger<DotNetBasedAppHostServerProject> logger,
        string? projectModelPath = null)
    {
        _appPath = Path.GetFullPath(appPath);
        _appPath = new Uri(_appPath).LocalPath;
        _appPath = OperatingSystem.IsWindows() ? _appPath.ToLowerInvariant() : _appPath;
        _socketPath = socketPath;
        _repoRoot = Path.GetFullPath(repoRoot) + Path.DirectorySeparatorChar;
        _dotNetCliRunner = dotNetCliRunner;
        _packagingService = packagingService;
        _configurationService = configurationService;
        _logger = logger;

        var pathHash = SHA256.HashData(Encoding.UTF8.GetBytes(_appPath));

        if (projectModelPath is not null)
        {
            _projectModelPath = projectModelPath;
        }
        else
        {
            var pathDir = Convert.ToHexString(pathHash)[..12].ToLowerInvariant();
            _projectModelPath = Path.Combine(CliPathHelper.GetAspireHomeDirectory(), AppsFolder, pathDir);
        }

        // Create a stable UserSecretsId based on the app path hash
        _userSecretsId = new Guid(pathHash[..16]).ToString();

        Directory.CreateDirectory(_projectModelPath);
    }

    /// <inheritdoc />
    public string AppDirectoryPath => _appPath;

    public string ProjectModelPath => _projectModelPath;
    public string UserSecretsId => _userSecretsId;
    public string BuildPath => Path.Combine(_projectModelPath, BuildFolder);

    /// <summary>
    /// Gets the full path to the AppHost server project file.
    /// </summary>
    public string GetProjectFilePath() => Path.Combine(_projectModelPath, ProjectFileName);

    public string GetProjectHash()
    {
        var hashFilePath = Path.Combine(_projectModelPath, ProjectHashFileName);

        if (File.Exists(hashFilePath))
        {
            return File.ReadAllText(hashFilePath);
        }

        return string.Empty;
    }

    public void SaveProjectHash(string hash)
    {
        var hashFilePath = Path.Combine(_projectModelPath, ProjectHashFileName);
        File.WriteAllText(hashFilePath, hash);
    }

    /// <summary>
    /// Creates the project .csproj content using project references to the local Aspire repository.
    /// </summary>
    private XDocument CreateProjectFile(IEnumerable<IntegrationReference> integrations)
    {
        // Determine OS/architecture for DCP package name
        var (buildOs, buildArch) = GetBuildPlatform();
        var dcpPackageName = $"microsoft.developercontrolplane.{buildOs}-{buildArch}";
        var dcpVersion = GetDcpVersionFromRepo(_repoRoot, buildOs, buildArch);

        var template = $"""
            <Project Sdk="Microsoft.NET.Sdk">
                <PropertyGroup>
                    <OutputType>exe</OutputType>
                    <TargetFramework>{TargetFramework}</TargetFramework>
                    <AssemblyName>{AssemblyName}</AssemblyName>
                    <OutDir>{BuildFolder}</OutDir>
                    <UserSecretsId>{_userSecretsId}</UserSecretsId>
                    <IsAspireHost>true</IsAspireHost>
                    <IsPublishable>false</IsPublishable>
                    <SelfContained>false</SelfContained>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                    <WarningLevel>0</WarningLevel>
                    <EnableNETAnalyzers>false</EnableNETAnalyzers>
                    <EnableRoslynAnalyzers>false</EnableRoslynAnalyzers>
                    <RunAnalyzers>false</RunAnalyzers>
                    <NoWarn>$(NoWarn);1701;1702;1591;CS8019;CS1591;CS1573;CS0168;CS0219;CS8618;CS8625;CS1998;CS1999</NoWarn>
                    <!-- Properties for in-repo building -->
                    <RepoRoot>{_repoRoot}</RepoRoot>
                    <SkipValidateAspireHostProjectResources>true</SkipValidateAspireHostProjectResources>
                    <SkipAddAspireDefaultReferences>true</SkipAddAspireDefaultReferences>
                    <AspireHostingSDKVersion>42.42.42</AspireHostingSDKVersion>
                    <!-- DCP and Dashboard paths for local development -->
                    <DcpDir>$([MSBuild]::EnsureTrailingSlash('$(NuGetPackageRoot)')){dcpPackageName}/{dcpVersion}/tools/</DcpDir>
                    <AspireDashboardDir>{_repoRoot}artifacts/bin/Aspire.Dashboard/Debug/net8.0/</AspireDashboardDir>
                </PropertyGroup>
                <ItemGroup>
                    <PackageReference Include="StreamJsonRpc" />
                    <PackageReference Include="Google.Protobuf" />
                </ItemGroup>
            </Project>
            """;

        var doc = XDocument.Parse(template);

        // Add project references for Aspire.Hosting.* packages, NuGet for others
        var projectRefGroup = new XElement("ItemGroup");
        var addedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var otherPackages = new List<(string Name, string Version)>();

        foreach (var integration in integrations)
        {
            if (integration.IsProjectReference)
            {
                // Explicit project reference from settings.json
                if (addedProjects.Add(integration.Name))
                {
                    projectRefGroup.Add(new XElement("ProjectReference",
                        new XAttribute("Include", integration.ProjectPath!),
                        new XElement("IsAspireProjectResource", "false")));
                }
            }
            else if (integration.Name.StartsWith("Aspire.Hosting", StringComparison.OrdinalIgnoreCase))
            {
                var projectPath = Path.Combine(_repoRoot, "src", integration.Name, $"{integration.Name}.csproj");
                if (File.Exists(projectPath) && addedProjects.Add(integration.Name))
                {
                    projectRefGroup.Add(new XElement("ProjectReference",
                        new XAttribute("Include", projectPath),
                        new XElement("IsAspireProjectResource", "false")));
                }
            }
            else
            {
                if (integration.Version is null)
                {
                    throw new InvalidOperationException($"Integration '{integration.Name}' is neither a project reference nor a package reference (both Version and ProjectPath are null).");
                }
                otherPackages.Add((integration.Name, integration.Version));
            }
        }

        // Always add Aspire.Hosting project reference
        var hostingPath = Path.Combine(_repoRoot, "src", "Aspire.Hosting", "Aspire.Hosting.csproj");
        if (File.Exists(hostingPath) && addedProjects.Add("Aspire.Hosting"))
        {
            projectRefGroup.Add(new XElement("ProjectReference",
                new XAttribute("Include", hostingPath),
                new XElement("IsAspireProjectResource", "false")));
        }

        if (projectRefGroup.HasElements)
        {
            doc.Root!.Add(projectRefGroup);
        }

        if (otherPackages.Count > 0)
        {
            doc.Root!.Add(new XElement("ItemGroup",
                otherPackages.Select(p => new XElement("PackageReference",
                    new XAttribute("Include", p.Name),
                    new XAttribute("Version", p.Version)))));
        }

        // Add imports for in-repo AppHost building
        var appHostInTargets = Path.Combine(_repoRoot, "src", "Aspire.Hosting.AppHost", "build", "Aspire.Hosting.AppHost.in.targets");
        var sdkInTargets = Path.Combine(_repoRoot, "src", "Aspire.AppHost.Sdk", "SDK", "Sdk.in.targets");

        if (File.Exists(appHostInTargets))
        {
            doc.Root!.Add(new XElement("Import", new XAttribute("Project", appHostInTargets)));
        }
        if (File.Exists(sdkInTargets))
        {
            doc.Root!.Add(new XElement("Import", new XAttribute("Project", sdkInTargets)));
        }

        // Add Dashboard and RemoteHost project references
        var dashboardProject = Path.Combine(_repoRoot, "src", "Aspire.Dashboard", "Aspire.Dashboard.csproj");
        if (File.Exists(dashboardProject))
        {
            doc.Root!.Add(new XElement("ItemGroup",
                new XElement("ProjectReference", new XAttribute("Include", dashboardProject))));
        }

        var remoteHostProject = Path.Combine(_repoRoot, "src", "Aspire.Hosting.RemoteHost", "Aspire.Hosting.RemoteHost.csproj");
        if (File.Exists(remoteHostProject))
        {
            doc.Root!.Add(new XElement("ItemGroup",
                new XElement("ProjectReference", new XAttribute("Include", remoteHostProject))));
        }

        // Disable Aspire SDK code generation
        doc.Root!.Add(new XElement("Target", new XAttribute("Name", "_CSharpWriteHostProjectMetadataSources")));
        doc.Root!.Add(new XElement("Target", new XAttribute("Name", "_CSharpWriteProjectMetadataSources")));

        return doc;
    }

    /// <summary>
    /// Scaffolds the project files.
    /// </summary>
    public async Task<(string ProjectPath, string? ChannelName)> CreateProjectFilesAsync(
        IEnumerable<IntegrationReference> integrations,
        CancellationToken cancellationToken = default)
    {
        // Clean obj folder to ensure fresh NuGet restore
        var objPath = Path.Combine(_projectModelPath, "obj");
        if (Directory.Exists(objPath))
        {
            try
            {
                Directory.Delete(objPath, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to delete obj folder at {ObjPath}", objPath);
            }
        }

        // Create Program.cs
        var programCs = """
            await Aspire.Hosting.RemoteHost.RemoteHostServer.RunAsync(args);
            """;
        File.WriteAllText(Path.Combine(_projectModelPath, "Program.cs"), programCs);

        // Create appsettings.json with ATS assemblies
        var atsAssemblies = new List<string> { "Aspire.Hosting" };
        foreach (var integration in integrations)
        {
            // Skip SDK-only packages that don't produce runtime assemblies
            if (integration.Name.Equals("Aspire.Hosting.AppHost", StringComparison.OrdinalIgnoreCase) ||
                integration.Name.StartsWith("Aspire.AppHost.Sdk", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!atsAssemblies.Contains(integration.Name, StringComparer.OrdinalIgnoreCase))
            {
                atsAssemblies.Add(integration.Name);
            }
        }

        var assembliesJson = string.Join(",\n      ", atsAssemblies.Select(a => $"\"{a}\""));
        var appSettingsJson = $$"""
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
        File.WriteAllText(Path.Combine(_projectModelPath, "appsettings.json"), appSettingsJson);

        // Handle NuGet config and channel resolution
        string? channelName = null;

        var userNugetConfig = FindNuGetConfig(_appPath);
        if (userNugetConfig is not null)
        {
            var nugetConfigPath = Path.Combine(_projectModelPath, "nuget.config");
            File.Copy(userNugetConfig, nugetConfigPath, overwrite: true);
        }

        var channels = await _packagingService.GetChannelsAsync(cancellationToken);
        var localConfig = AspireJsonConfiguration.Load(_appPath);
        var configuredChannelName = localConfig?.Channel;

        if (string.IsNullOrEmpty(configuredChannelName))
        {
            configuredChannelName = await _configurationService.GetConfigurationAsync("channel", cancellationToken);
        }

        // Resolve channel sources and add them via RestoreAdditionalProjectSources
        // This is additive — it preserves the user's nuget.config and adds channel-specific sources
        var channelSources = new List<string>();
        var matchedChannels = !string.IsNullOrEmpty(configuredChannelName)
            ? channels.Where(c => string.Equals(c.Name, configuredChannelName, StringComparison.OrdinalIgnoreCase))
            : channels.Where(c => c.Type == PackageChannelType.Explicit);

        foreach (var ch in matchedChannels)
        {
            channelName ??= ch.Name;
            if (ch.Mappings is not null)
            {
                foreach (var mapping in ch.Mappings)
                {
                    if (!channelSources.Contains(mapping.Source, StringComparer.OrdinalIgnoreCase))
                    {
                        channelSources.Add(mapping.Source);
                    }
                }
            }
        }

        // Create the project file
        var doc = CreateProjectFile(integrations);

        // Add channel sources to the project
        if (channelSources.Count > 0)
        {
            var sourceList = string.Join(";", channelSources);
            doc.Root!.Descendants("PropertyGroup").First()
                .Add(new XElement("RestoreAdditionalProjectSources", sourceList));
        }

        // Add appsettings.json to output
        doc.Root!.Add(new XElement("ItemGroup",
            new XElement("None",
                new XAttribute("Include", "appsettings.json"),
                new XAttribute("CopyToOutputDirectory", "PreserveNewest"))));

        // Create Directory.Packages.props to enable central package management
        // This ensures transitive dependencies use versions from the repo's Directory.Packages.props
        var repoDirectoryPackagesProps = Path.Combine(_repoRoot, "Directory.Packages.props");

        var directoryPackagesProps = $"""
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
              </PropertyGroup>
              <Import Project="{repoDirectoryPackagesProps}" />
            </Project>
            """;
        File.WriteAllText(Path.Combine(_projectModelPath, "Directory.Packages.props"), directoryPackagesProps);

        // Write empty Directory.Build.props/targets to prevent MSBuild from walking up and
        // importing the repo's build infrastructure (Arcade SDK, etc.) which can rewrite
        // project reference paths and cause resolution failures from the temp directory.
        File.WriteAllText(Path.Combine(_projectModelPath, "Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Combine(_projectModelPath, "Directory.Build.targets"), "<Project />");

        var projectFileName = Path.Combine(_projectModelPath, ProjectFileName);

        // Log the full project XML for debugging
        _logger.LogTrace("Generated AppHostServer project file:\n{ProjectXml}", doc.ToString());

        doc.Save(projectFileName);

        return (projectFileName, channelName);
    }

    /// <summary>
    /// Restores and builds the project.
    /// </summary>
    public async Task<(bool Success, OutputCollector Output)> BuildAsync(CancellationToken cancellationToken = default)
    {
        var outputCollector = new OutputCollector();
        var projectFile = new FileInfo(Path.Combine(_projectModelPath, ProjectFileName));

        var options = new ProcessInvocationOptions
        {
            StandardOutputCallback = outputCollector.AppendOutput,
            StandardErrorCallback = outputCollector.AppendError
        };

        var exitCode = await _dotNetCliRunner.BuildAsync(projectFile, noRestore: false, options, cancellationToken);

        return (exitCode == 0, outputCollector);
    }

    /// <inheritdoc />
    public async Task<AppHostServerPrepareResult> PrepareAsync(
        string sdkVersion,
        IEnumerable<IntegrationReference> integrations,
        CancellationToken cancellationToken = default)
    {
        var (_, channelName) = await CreateProjectFilesAsync(integrations, cancellationToken);
        var (buildSuccess, buildOutput) = await BuildAsync(cancellationToken);

        if (!buildSuccess)
        {
            return new AppHostServerPrepareResult(
                Success: false,
                Output: buildOutput,
                ChannelName: channelName,
                NeedsCodeGeneration: false);
        }

        return new AppHostServerPrepareResult(
            Success: true,
            Output: buildOutput,
            ChannelName: channelName,
            NeedsCodeGeneration: true);
    }

    /// <inheritdoc />
    public string GetInstanceIdentifier() => GetProjectFilePath();

    /// <inheritdoc />
    public (string SocketPath, Process Process, OutputCollector OutputCollector) Run(
        int hostPid,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        string[]? additionalArgs = null,
        bool debug = false)
    {
        var assemblyPath = Path.Combine(BuildPath, ProjectDllName);
        var dotnetExe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

        var startInfo = new ProcessStartInfo(dotnetExe)
        {
            WorkingDirectory = _projectModelPath,
            WindowStyle = ProcessWindowStyle.Minimized,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(assemblyPath);

        if (additionalArgs is { Length: > 0 })
        {
            startInfo.ArgumentList.Add("--");
            foreach (var arg in additionalArgs)
            {
                startInfo.ArgumentList.Add(arg);
            }
        }

        startInfo.Environment["REMOTE_APP_HOST_SOCKET_PATH"] = _socketPath;
        startInfo.Environment["REMOTE_APP_HOST_PID"] = hostPid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        startInfo.Environment[KnownConfigNames.CliProcessId] = hostPid.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Dev mode uses debug builds which require Development environment
        // for the dashboard to resolve static web assets correctly
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";

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
            _logger.LogDebug("Enabling debug logging for AppHostServer");
        }

        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        var process = Process.Start(startInfo)!;

        var outputCollector = new OutputCollector();
        process.OutputDataReceived += (sender, e) =>
        {
            if (e.Data is not null)
            {
                _logger.LogTrace("AppHostServer({ProcessId}) stdout: {Line}", process.Id, e.Data);
                outputCollector.AppendOutput(e.Data);
            }
        };
        process.ErrorDataReceived += (sender, e) =>
        {
            if (e.Data is not null)
            {
                _logger.LogTrace("AppHostServer({ProcessId}) stderr: {Line}", process.Id, e.Data);
                outputCollector.AppendError(e.Data);
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return (_socketPath, process, outputCollector);
    }

    private static string? FindNuGetConfig(string workingDirectory)
    {
        try
        {
            var startInfo = new ProcessStartInfo("dotnet")
            {
                Arguments = "nuget config paths",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                return null;
            }

            var configPaths = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            var workingDirFullPath = Path.GetFullPath(workingDirectory);
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var globalNuGetPath = Path.Combine(userProfile, ".nuget");

            foreach (var configPath in configPaths)
            {
                if (File.Exists(configPath))
                {
                    var configFullPath = Path.GetFullPath(configPath);
                    var configDir = Path.GetDirectoryName(configFullPath);

                    if (configDir is not null &&
                        !configDir.StartsWith(globalNuGetPath, StringComparison.OrdinalIgnoreCase) &&
                        (workingDirFullPath.StartsWith(configDir, StringComparison.OrdinalIgnoreCase) ||
                         configDir.StartsWith(workingDirFullPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        return configFullPath;
                    }
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static (string Os, string Arch) GetBuildPlatform()
    {
        var os = OperatingSystem.IsLinux() ? "linux"
            : OperatingSystem.IsMacOS() ? "darwin"
            : "windows";

        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X86 => "386",
            Architecture.X64 => "amd64",
            Architecture.Arm64 => "arm64",
            _ => "amd64"
        };

        return (os, arch);
    }

    private static string GetDcpVersionFromRepo(string repoRoot, string buildOs, string buildArch)
    {
        const string fallbackVersion = "0.21.1";

        try
        {
            var versionsPropsPath = Path.Combine(repoRoot, "eng", "Versions.props");
            if (!File.Exists(versionsPropsPath))
            {
                return fallbackVersion;
            }

            var doc = XDocument.Load(versionsPropsPath);

            var propertyName = $"MicrosoftDeveloperControlPlane{buildOs}{buildArch}Version";

            var version = doc.Descendants(propertyName).FirstOrDefault()?.Value;
            return version ?? fallbackVersion;
        }
        catch
        {
            return fallbackVersion;
        }
    }
}
