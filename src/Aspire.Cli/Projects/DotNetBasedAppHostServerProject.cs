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
using Aspire.Shared;
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

    private readonly string _projectModelPath;
    private readonly string _appPath;
    private readonly string _socketPath;
    private readonly string _userSecretsId;
    private readonly string _repoRoot;
    private readonly IDotNetCliRunner _dotNetCliRunner;
    private readonly IPackagingService _packagingService;
    private readonly IProcessExecutionFactory _processExecutionFactory;
    private readonly IEnvironment _environment;
    private readonly ILogger _logger;
    private readonly string? _logFilePath;

    public DotNetBasedAppHostServerProject(
        string appPath,
        string socketPath,
        string repoRoot,
        IDotNetCliRunner dotNetCliRunner,
        IPackagingService packagingService,
        IProcessExecutionFactory processExecutionFactory,
        IEnvironment environment,
        ILogger<DotNetBasedAppHostServerProject> logger,
        string? projectModelPath = null,
        string? logFilePath = null)
    {
        _appPath = Path.GetFullPath(appPath);
        _appPath = new Uri(_appPath).LocalPath;
        _appPath = OperatingSystem.IsWindows() ? _appPath.ToLowerInvariant() : _appPath;
        _socketPath = socketPath;
        _repoRoot = Path.GetFullPath(repoRoot) + Path.DirectorySeparatorChar;
        _dotNetCliRunner = dotNetCliRunner;
        _packagingService = packagingService;
        _processExecutionFactory = processExecutionFactory;
        _environment = environment;
        _logger = logger;
        _logFilePath = logFilePath;

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

    internal string? LogFilePath => _logFilePath;

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
    private CSharpProjectFile CreateProjectFile(IEnumerable<IntegrationReference> integrations)
    {
        // Determine OS/architecture for DCP package name
        var (buildOs, buildArch) = GetBuildPlatform(_environment);
        var dcpPackageName = $"microsoft.developercontrolplane.{buildOs}-{buildArch}";
        var dcpVersion = GetDcpVersionFromRepo(_repoRoot, buildOs, buildArch);

        var projectFile = new CSharpProjectFile();
        projectFile.AddProperty("OutputType", "exe");
        projectFile.AddProperty("TargetFramework", TargetFramework);
        projectFile.AddProperty("AssemblyName", AssemblyName);
        projectFile.AddProperty("OutDir", BuildFolder);
        projectFile.AddProperty("UserSecretsId", _userSecretsId);
        projectFile.AddProperty("IsAspireHost", "true");
        projectFile.AddProperty("IsPublishable", "false");
        projectFile.AddProperty("SelfContained", "false");
        projectFile.AddProperty("ImplicitUsings", "enable");
        projectFile.AddProperty("Nullable", "enable");
        projectFile.AddProperty("WarningLevel", "0");
        projectFile.AddProperty("EnableNETAnalyzers", "false");
        projectFile.AddProperty("EnableRoslynAnalyzers", "false");
        projectFile.AddProperty("RunAnalyzers", "false");
        projectFile.AddProperty("NoWarn", "$(NoWarn);1701;1702;1591;CS8019;CS1591;CS1573;CS0168;CS0219;CS8618;CS8625;CS1998;CS1999");
        projectFile.AddProperty("RepoRoot", _repoRoot);
        projectFile.AddProperty("SkipValidateAspireHostProjectResources", "true");
        projectFile.AddProperty("SkipAddAspireDefaultReferences", "true");
        projectFile.AddProperty("SkipAspireIntegrationAnalyzersReference", "true");
        projectFile.AddProperty("AspireHostingSDKVersion", "42.42.42");
        projectFile.AddProperty("DcpDir", $"$([MSBuild]::EnsureTrailingSlash('$(NuGetPackageRoot)')){dcpPackageName}/{dcpVersion}/tools/");
        projectFile.AddProperty("AspireDashboardDir", $"{_repoRoot}artifacts/bin/Aspire.Dashboard/Debug/net8.0/");
        projectFile.PackageReferences.Add(new CSharpPackageReference("StreamJsonRpc"));
        projectFile.PackageReferences.Add(new CSharpPackageReference("Google.Protobuf"));

        // Add project references for Aspire.Hosting.* packages, NuGet for others
        var addedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        projectFile.AddIntegrationReferences(
            integrations,
            _repoRoot,
            isAspireProjectResource: false,
            addedProjectPaths: addedProjects);

        // Always add Aspire.Hosting project reference
        projectFile.AddRepositoryProjectReferenceIfExists(
            _repoRoot,
            "Aspire.Hosting",
            isAspireProjectResource: false,
            addedProjectPaths: addedProjects);

        // Add imports for in-repo AppHost building
        var appHostInTargets = Path.Combine(_repoRoot, "src", "Aspire.Hosting.AppHost", "build", "Aspire.Hosting.AppHost.in.targets");
        var sdkInTargets = Path.Combine(_repoRoot, "src", "Aspire.AppHost.Sdk", "SDK", "Sdk.in.targets");

        if (File.Exists(appHostInTargets))
        {
            projectFile.Imports.Add(new CSharpProjectImport(appHostInTargets));
        }
        if (File.Exists(sdkInTargets))
        {
            projectFile.Imports.Add(new CSharpProjectImport(sdkInTargets));
        }

        projectFile.AddRepositoryProjectReferenceIfExists(
            _repoRoot,
            "Aspire.Dashboard",
            isAspireProjectResource: false,
            referenceOutputAssembly: false,
            privateReference: false);
        projectFile.AddRepositoryProjectReferenceIfExists(
            _repoRoot,
            "Aspire.Hosting.RemoteHost",
            isAspireProjectResource: false);

        // Disable Aspire SDK code generation
        projectFile.Targets.Add(new XElement("Target", new XAttribute("Name", "_CSharpWriteHostProjectMetadataSources")));
        projectFile.Targets.Add(new XElement("Target", new XAttribute("Name", "_CSharpWriteProjectMetadataSources")));

        return projectFile;
    }

    /// <summary>
    /// Scaffolds the project files.
    /// </summary>
    public async Task<(string ProjectPath, string? ChannelName)> CreateProjectFilesAsync(
        IEnumerable<IntegrationReference> integrations,
        string? requestedChannel = null,
        string? packageSourceOverride = null,
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

        var configuredChannelName = requestedChannel
            ?? AspireConfigFile.Load(_appPath)?.Channel
            ?? AspireJsonConfiguration.Load(_appPath)?.Channel;

        // Resolve channel sources and add them via RestoreAdditionalProjectSources
        // This is additive — it preserves the user's nuget.config and adds channel-specific sources
        var channelSources = new List<string>();
        if (!string.IsNullOrEmpty(configuredChannelName))
        {
            var channels = await _packagingService.GetChannelsAsync(cancellationToken, configuredChannelName);
            var matchedChannels = channels.Where(c => string.Equals(c.Name, configuredChannelName, StringComparison.OrdinalIgnoreCase));

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
        }

        // Thread an explicit `--source` override into the restore sources so the dogfood
        // `aspire new --source <pr-hive>` flow is honored in dev mode (in-repo). Prepending
        // makes the override the first source NuGet evaluates, which matters when the same
        // Aspire package version exists in both the hive and a channel feed. Note: unlike
        // PrebuiltAppHostServer this path does not emit Package Source Mappings, so NuGet
        // may still consult other sources if the override does not satisfy a request — the
        // override is best-effort here, sufficient for the in-repo developer scenario where
        // most Aspire.* dependencies come from ProjectReference, not PackageReference.
        if (!string.IsNullOrWhiteSpace(packageSourceOverride) &&
            !channelSources.Contains(packageSourceOverride, StringComparer.OrdinalIgnoreCase))
        {
            channelSources.Insert(0, packageSourceOverride);
        }

        // Create the project file
        var projectFile = CreateProjectFile(integrations);

        // Add channel sources to the project
        if (channelSources.Count > 0)
        {
            var sourceList = string.Join(";", channelSources);
            projectFile.AddProperty("RestoreAdditionalProjectSources", sourceList);
        }

        // Add appsettings.json to output
        projectFile.NoneItems.Add(new CSharpNoneItem("appsettings.json", CopyToOutputDirectory: "PreserveNewest"));

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
        var doc = projectFile.ToXDocument();

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
        string? requestedChannel = null,
        string? packageSourceOverride = null,
        CancellationToken cancellationToken = default)
    {
        var (_, channelName) = await CreateProjectFilesAsync(integrations, requestedChannel, packageSourceOverride, cancellationToken);
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
    public async Task<AppHostServerRunResult> RunAsync(
        int hostPid,
        IReadOnlyDictionary<string, string>? environmentVariables,
        string[]? additionalArgs,
        bool debug,
        AppHostServerRunControl? runControl)
    {
        var assemblyPath = Path.Combine(BuildPath, ProjectDllName);
        var dotnetExe = _environment.IsWindows() ? "dotnet.exe" : "dotnet";

        // Build the canonical ProcessStartInfo first, then translate to IsolatedProcessStartInfo
        // only if the isolated path is requested. Sharing the env/arg construction avoids drift
        // between the two branches — every env var and argument lives in exactly one place.
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
        startInfo.Environment[KnownConfigNames.CliLogFilePath] = _logFilePath;

        // Dev mode uses debug builds which require Development environment
        // for the dashboard to resolve static web assets correctly
        startInfo.Environment[KnownAspNetCoreConfigNames.Environment] = "Development";

        // Wire WithTerminal() for guest/polyglot AppHosts running from the repo. The
        // generated AppHostServer references Aspire.Hosting from the repo and DCP resolves
        // the terminal host via ASPIRE_TERMINAL_HOST_PATH or assembly metadata. No per-RID
        // NuGet stamps the metadata path today, so without this env var the AppHostServer
        // would always resolve to <unresolved-aspire-terminalhost> in repo mode.
        // Mirrors the same injection that DotNetAppHostProject performs for .NET AppHosts.
        // Skipped when the caller pre-populates the path so a user-side override always wins.
        if (BundleDiscovery.TryGetRepoLocalManagedPath(_repoRoot) is { } terminalHostPath
            && !ContainsKey(environmentVariables, BundleDiscovery.TerminalHostPathEnvVar))
        {
            startInfo.Environment[BundleDiscovery.TerminalHostPathEnvVar] = terminalHostPath;
            if (!ContainsKey(environmentVariables, BundleDiscovery.TerminalHostInvocationArgsEnvVar))
            {
                startInfo.Environment[BundleDiscovery.TerminalHostInvocationArgsEnvVar] = "terminalhost";
            }
        }

        if (environmentVariables is not null)
        {
            foreach (var (key, value) in environmentVariables)
            {
                startInfo.Environment[key] = value;
            }
        }

        if (debug)
        {
            startInfo.Environment[KnownConfigNames.AspireLogLevel] = "Debug";
            _logger.LogDebug("Enabling debug logging for AppHostServer");
        }

        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        var outputCollector = new OutputCollector();

        // The execution local is forward-referenced by the log callbacks so they can read the
        // child's pid per line. ProcessInvocationOptions.StandardOutputCallback is Action<string>
        // (line only), but the AppHost wants the pid in each trace line (#16729). The callbacks
        // only fire after Start(), by which point `execution` is assigned and ProcessId is valid.
        IProcessExecution execution = null!;

        void OnStdout(string line)
        {
            _logger.LogTrace("AppHostServer({ProcessId}) stdout: {Line}", execution.ProcessId, line);
            outputCollector.AppendOutput(line);
        }

        void OnStderr(string line)
        {
            _logger.LogTrace("AppHostServer({ProcessId}) stderr: {Line}", execution.ProcessId, line);
            outputCollector.AppendError(line);
        }

        var options = new ProcessInvocationOptions
        {
            StandardOutputCallback = OnStdout,
            StandardErrorCallback = OnStderr,
            IsolateConsole = runControl?.IsolateConsole ?? false,
            GracefulShutdownSignaler = runControl?.GracefulShutdownSignaler,
            ShutdownService = runControl?.ShutdownService,
            // The graceful ladder always tree-kills on escalation; this fallback only matters when
            // graceful services were not wired (non-Run callers), where it preserves the old session
            // behavior of force-killing the tree on Unix but only the root on Windows.
            KillEntireProcessTreeOnCancel = !_environment.IsWindows(),
        };

        execution = _processExecutionFactory.CreateExecution(startInfo, options);

        try
        {
            execution.Start();
        }
        catch
        {
            await execution.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new AppHostServerRunResult(_socketPath, outputCollector, execution);
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

    internal static (string Os, string Arch) GetBuildPlatform(IEnvironment environment)
    {
        var os = environment.IsLinux() ? "linux"
            : environment.IsMacOS() ? "darwin"
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

    internal static string GetDcpVersionFromRepo(string repoRoot, string buildOs, string buildArch)
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

    private static bool ContainsKey(IReadOnlyDictionary<string, string>? env, string key)
    {
        return env is not null && env.ContainsKey(key);
    }
}
