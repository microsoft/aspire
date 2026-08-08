// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Aspire.Cli.Configuration;
using Aspire.Cli.DotNet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Processes;
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
    private const string PackageProbeSourcesFileName = "package-probe-sources.txt";
    private const string PackageProbeMetadataFileName = "package-probe-metadata.txt";
    private const string PackageProbeTargetsFileName = "package-probe-targets.txt";

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
    private string? _integrationProbeManifestPath;

    // Boxed so "not read yet" and "read, and there is no answer" stay distinguishable without
    // re-parsing eng/Versions.props on every lookup.
    private StrongBox<string?>? _repositoryVersionPrefix;

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
                    <SkipAspireIntegrationAnalyzersReference>true</SkipAspireIntegrationAnalyzersReference>
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
        var otherPackages = new List<IntegrationReference>();

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
                if (GetLocalProjectSubstitution(integration.Name) is { } substitution)
                {
                    if (addedProjects.Add(integration.Name))
                    {
                        projectRefGroup.Add(new XElement("ProjectReference",
                            new XAttribute("Include", substitution.ProjectPath),
                            new XElement("IsAspireProjectResource", "false")));
                    }
                }
                else if (integration.RequireExactVersion)
                {
                    // A first-party name does not mean this checkout can supply it. Dropping the
                    // reference made `sdk export --package Aspire.Hosting.DoesNotExist@13.5.0-dev`
                    // scan clean and publish an empty module under a package id that has never
                    // existed, so a caller that demands an exact version gets a real package
                    // reference instead and the restore fails (NU1101) as it should.
                    if (integration.Version is null)
                    {
                        throw new InvalidOperationException($"Integration '{integration.Name}' is neither a project reference nor a package reference (both Version and ProjectPath are null).");
                    }
                    otherPackages.Add(integration);
                }

                // Everything else keeps dropping the reference. Only `sdk export` asks for exactness,
                // and only it names the package explicitly; `aspire run`, `sdk dump`, and `sdk
                // generate` take their integrations from aspire.config.json, where a version-less
                // entry resolves to this CLI's identity (`13.5.0-dev` in a checkout). Restoring that
                // as a package could never succeed, so failing here would turn one unavailable
                // integration into a build failure for the whole AppHost.
            }
            else
            {
                if (integration.Version is null)
                {
                    throw new InvalidOperationException($"Integration '{integration.Name}' is neither a project reference nor a package reference (both Version and ProjectPath are null).");
                }
                otherPackages.Add(integration);
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
            // This project always gets a generated Directory.Packages.props that turns central package
            // management on, so an inline Version attribute is rejected with NU1008. VersionOverride is
            // the CPM-sanctioned way to pin a single reference, and it lets us scan an integration that
            // the repo's Directory.Packages.props has no PackageVersion entry for.
            doc.Root!.Add(new XElement("ItemGroup",
                otherPackages.Select(p => new XElement("PackageReference",
                    new XAttribute("Include", p.Name),
                    new XAttribute("VersionOverride", p.GetRestoreVersionRange(forceExact: false))))));
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
        doc.Root!.Add(
            new XElement("Target",
                new XAttribute("Name", "_WriteAspirePackageProbeManifestInputs"),
                new XAttribute("AfterTargets", "Build"),
                new XAttribute("DependsOnTargets", "ResolveLockFileCopyLocalFiles"),
                new XElement("WriteLinesToFile",
                    new XAttribute("File", Path.Combine(_projectModelPath, PackageProbeSourcesFileName)),
                    new XAttribute("Lines", "@(ReferenceCopyLocalPaths->'%(FullPath)')"),
                    new XAttribute("Overwrite", "true"),
                    new XAttribute("WriteOnlyWhenDifferent", "true")),
                new XElement("WriteLinesToFile",
                    new XAttribute("File", Path.Combine(_projectModelPath, PackageProbeMetadataFileName)),
                    new XAttribute("Lines", "@(ReferenceCopyLocalPaths->'%(NuGetPackageId)|%(NuGetPackageVersion)|%(AssetType)')"),
                    new XAttribute("Overwrite", "true"),
                    new XAttribute("WriteOnlyWhenDifferent", "true")),
                new XElement("WriteLinesToFile",
                    new XAttribute("File", Path.Combine(_projectModelPath, PackageProbeTargetsFileName)),
                    new XAttribute("Lines", "@(ReferenceCopyLocalPaths->'%(DestinationSubDirectory)%(Filename)%(Extension)')"),
                    new XAttribute("Overwrite", "true"),
                    new XAttribute("WriteOnlyWhenDifferent", "true"))));

        return doc;
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
        var channels = await _packagingService.GetChannelsAsync(cancellationToken, configuredChannelName);

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

        await WriteIntegrationProbeManifestAsync(cancellationToken).ConfigureAwait(false);

        return new AppHostServerPrepareResult(
            Success: true,
            Output: buildOutput,
            ChannelName: channelName,
            NeedsCodeGeneration: true);
    }

    /// <inheritdoc />
    public string GetInstanceIdentifier() => GetProjectFilePath();

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// This is the same decision <see cref="CreateProjectFile"/> makes, kept in one place so a
    /// caller asking "will my requested version survive?" cannot drift from what the generated
    /// project actually does. Only first-party <c>Aspire.Hosting.*</c> packages live under
    /// <c>src/</c>, so a third-party integration is always restored from a feed even here.
    /// </para>
    /// <para>
    /// The name is matched case-insensitively, because a NuGet package id is
    /// (<see href="https://learn.microsoft.com/nuget/consume-packages/finding-and-choosing-packages#package-identifiers"/>)
    /// while the filesystem this resolves through is not on Linux. Probing the caller's spelling
    /// directly meant <c>aspire.hosting.redis</c> found nothing there and <c>src/Aspire.Hosting.Redis</c>
    /// on macOS and Windows, so how a package was spelled decided whether the checkout was used at
    /// all — and callers that publish version-keyed artifacts saw no substitution to guard against.
    /// The returned path is always the on-disk spelling so the generated project reference and the
    /// caller's check name the same project.
    /// </para>
    /// </remarks>
    public LocalProjectSubstitution? GetLocalProjectSubstitution(string packageName)
    {
        if (!packageName.StartsWith("Aspire.Hosting", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var srcPath = Path.Combine(_repoRoot, "src");
        if (!Directory.Exists(srcPath))
        {
            return null;
        }

        // The directory listing settles the spelling on every platform. Probing
        // Path.Combine(src, packageName) instead would hand back the caller's spelling wherever
        // File.Exists is case-insensitive, so the same request produced two different project paths
        // depending on the filesystem. Enumerate and compare rather than passing the caller's name
        // as a search pattern, so a package id that happens to contain a wildcard cannot match a
        // directory it does not name.
        //
        // Enumerating can throw where the old File.Exists probe could not, and this runs on the
        // `aspire run` path via CreateProjectFile, so an unreadable or concurrently removed src/
        // reports "no substitution" the same way GetRepositoryVersionPrefix does rather than
        // costing the caller its scanner.
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(srcPath))
            {
                var canonicalName = Path.GetFileName(directory);
                if (!string.Equals(canonicalName, packageName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var projectPath = Path.Combine(directory, $"{canonicalName}.csproj");
                return File.Exists(projectPath)
                    ? new LocalProjectSubstitution(projectPath, GetRepositoryVersionPrefix())
                    : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Reads the <c>Major.Minor.Patch</c> this checkout builds from <c>eng/Versions.props</c>, or
    /// <see langword="null"/> when it cannot be established.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The repository states its version line as three properties that <c>VersionPrefix</c> is
    /// composed from:
    /// <code>
    /// &lt;MajorVersion&gt;13&lt;/MajorVersion&gt;
    /// &lt;MinorVersion&gt;5&lt;/MinorVersion&gt;
    /// &lt;PatchVersion&gt;0&lt;/PatchVersion&gt;
    /// &lt;VersionPrefix&gt;$(MajorVersion).$(MinorVersion).$(PatchVersion)&lt;/VersionPrefix&gt;
    /// </code>
    /// <c>VersionPrefix</c> itself is read as the unexpanded MSBuild expression, so the three parts
    /// are read directly. The prerelease suffix (<c>-preview.1.25366.3</c>) is assigned by Arcade at
    /// build time and is not in the checkout, which is why only the prefix can be established here.
    /// </para>
    /// <para>
    /// Any failure returns <see langword="null"/> rather than throwing: this only informs callers
    /// that need provenance, and no other caller should lose a scanner over an unreadable file.
    /// </para>
    /// </remarks>
    private string? GetRepositoryVersionPrefix()
    {
        if (_repositoryVersionPrefix is { } cached)
        {
            return cached.Value;
        }

        _repositoryVersionPrefix = ReadRepositoryVersionPrefix(_repoRoot);
        return _repositoryVersionPrefix.Value;
    }

    private static StrongBox<string?> ReadRepositoryVersionPrefix(string repoRoot)
    {
        try
        {
            var versionsPropsPath = Path.Combine(repoRoot, "eng", "Versions.props");
            if (!File.Exists(versionsPropsPath))
            {
                return new StrongBox<string?>(null);
            }

            var doc = XDocument.Load(versionsPropsPath);

            var major = doc.Descendants("MajorVersion").FirstOrDefault()?.Value;
            var minor = doc.Descendants("MinorVersion").FirstOrDefault()?.Value;
            var patch = doc.Descendants("PatchVersion").FirstOrDefault()?.Value;

            return new StrongBox<string?>(
                string.IsNullOrWhiteSpace(major) || string.IsNullOrWhiteSpace(minor) || string.IsNullOrWhiteSpace(patch)
                    ? null
                    : $"{major.Trim()}.{minor.Trim()}.{patch.Trim()}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return new StrongBox<string?>(null);
        }
    }

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
        startInfo.Environment[KnownConfigNames.CliLogFilePath] = _logFilePath;

        // Stamp the launching CLI (hostPid) as the parent under both the RemoteHost and generic CLI
        // key pairs. Resolve the start time once and pair it with the PID so the RemoteHost orphan
        // detector verifies both and does not keep the server alive against a recycled PID.
        var hostStartedUnix = ProcessStartTimeHelper.TryGetProcessStartTimeUnixMilliseconds(hostPid);
        OrphanDetectionEnvironment.Apply(startInfo.Environment, hostPid, hostStartedUnix, KnownConfigNames.RemoteAppHostProcessId, KnownConfigNames.RemoteAppHostProcessStarted);
        OrphanDetectionEnvironment.Apply(startInfo.Environment, hostPid, hostStartedUnix, KnownConfigNames.CliProcessId, KnownConfigNames.CliProcessStarted);

        // Dev mode uses debug builds which require Development environment
        // for the dashboard to resolve static web assets correctly
        startInfo.Environment[KnownAspNetCoreConfigNames.Environment] = "Development";

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
        // (line only), but the AppHost wants the pid in each trace line (#16729). ProcessExecution
        // publishes the child pid before it starts stdout/stderr pumps so immediate output can read
        // ProcessId.
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
            KillOnParentExit = runControl?.KillOnParentExit ?? false,
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
            await execution.StartAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            await execution.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new AppHostServerRunResult(_socketPath, outputCollector, execution);
    }

    private async Task WriteIntegrationProbeManifestAsync(CancellationToken cancellationToken)
    {
        var sourcesPath = Path.Combine(_projectModelPath, PackageProbeSourcesFileName);
        var metadataPath = Path.Combine(_projectModelPath, PackageProbeMetadataFileName);
        var targetsPath = Path.Combine(_projectModelPath, PackageProbeTargetsFileName);

        if (!File.Exists(sourcesPath) || !File.Exists(metadataPath) || !File.Exists(targetsPath))
        {
            _integrationProbeManifestPath = null;
            return;
        }

        var sourcePaths = await File.ReadAllLinesAsync(sourcesPath, cancellationToken).ConfigureAwait(false);
        var metadataLines = await File.ReadAllLinesAsync(metadataPath, cancellationToken).ConfigureAwait(false);
        var targetPaths = await File.ReadAllLinesAsync(targetsPath, cancellationToken).ConfigureAwait(false);
        if (sourcePaths.Length != metadataLines.Length || sourcePaths.Length != targetPaths.Length)
        {
            throw new InvalidOperationException(
                $"Package probe manifest inputs are inconsistent. Sources: {sourcePaths.Length}, metadata: {metadataLines.Length}, targets: {targetPaths.Length}.");
        }

        var managedAssemblies = new List<IntegrationPackageManagedAssembly>();
        var nativeLibraries = new List<IntegrationPackageNativeLibrary>();

        for (var i = 0; i < sourcePaths.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var metadata = ParsePackageProbeMetadata(metadataLines[i]);
            if (string.IsNullOrWhiteSpace(metadata.PackageId) ||
                string.IsNullOrWhiteSpace(metadata.PackageVersion))
            {
                continue;
            }

            var sourcePath = sourcePaths[i];
            var targetPath = NormalizePackageProbeTargetPath(targetPaths[i], sourcePath);
            if (string.Equals(metadata.AssetType, "native", StringComparison.OrdinalIgnoreCase))
            {
                nativeLibraries.Add(new IntegrationPackageNativeLibrary
                {
                    FileName = Path.GetFileName(targetPath),
                    Path = sourcePath
                });
                continue;
            }

            if (!sourcePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            managedAssemblies.Add(new IntegrationPackageManagedAssembly
            {
                Name = Path.GetFileNameWithoutExtension(targetPath),
                Culture = TryGetSatelliteCulture(targetPath, metadata.AssetType),
                Path = sourcePath,
                PackageId = metadata.PackageId,
                PackageVersion = metadata.PackageVersion
            });
        }

        if (managedAssemblies.Count == 0 && nativeLibraries.Count == 0)
        {
            _integrationProbeManifestPath = null;
            return;
        }

        _integrationProbeManifestPath = Path.Combine(_projectModelPath, IntegrationPackageProbeManifest.FileName);
        await IntegrationPackageProbeManifest.WriteAsync(
            _integrationProbeManifestPath,
            IntegrationPackageProbeManifest.Create(managedAssemblies, nativeLibraries),
            cancellationToken).ConfigureAwait(false);
    }

    private static PackageProbeMetadata ParsePackageProbeMetadata(string line)
    {
        // Written from ReferenceCopyLocalPaths as:
        //   Contoso.Aspire.MetaPackage|1.2.3|runtime
        // Empty fields are possible for project references; those entries are intentionally
        // ignored because the probe manifest only preserves NuGet package ownership.
        var parts = line.Split('|');
        if (parts.Length != 3)
        {
            throw new InvalidOperationException($"Package probe manifest metadata line has an unexpected format: '{line}'.");
        }

        return new PackageProbeMetadata(
            NormalizeOptionalValue(parts[0]),
            NormalizeOptionalValue(parts[1]),
            NormalizeOptionalValue(parts[2]));
    }

    private static string NormalizePackageProbeTargetPath(string targetPath, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return Path.GetFileName(sourcePath);
        }

        return targetPath.Replace('\\', '/').TrimStart('/');
    }

    private static string? TryGetSatelliteCulture(string relativePath, string? assetType)
    {
        if (!string.Equals(assetType, "resources", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var directoryName = Path.GetDirectoryName(relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(directoryName))
        {
            return null;
        }

        return directoryName.Replace('\\', '/').Trim('/');
    }

    private static string? NormalizeOptionalValue(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record PackageProbeMetadata(string? PackageId, string? PackageVersion, string? AssetType);

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

    private (string Os, string Arch) GetBuildPlatform()
    {
        var os = _environment.IsLinux() ? "linux"
            : _environment.IsMacOS() ? "darwin"
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

    private static bool ContainsKey(IReadOnlyDictionary<string, string>? env, string key)
    {
        return env is not null && env.ContainsKey(key);
    }
}
