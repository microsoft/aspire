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
        // restore, including an auto-discovered local hive, rather than only the --source value
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

            IIntegrationRestorePlan? restorePlan = null;
            if (packageRefs.Count > 0 || projectRefs.Count > 0)
            {
                restorePlan = await ResolveIntegrationRestorePlanAsync(
                    sdkVersion,
                    requestedChannel,
                    effectivePackageSourceOverride,
                    packageSourceOverridePattern,
                    cancellationToken).ConfigureAwait(false);
                effectivePackageSourceOverride = restorePlan.EffectivePackageSourceOverride;
            }

            if (packageRefs.Count > 0)
            {
                _integrationProbeManifestPath = await RestoreNuGetPackagesAsync(
                    packageRefs,
                    restorePlan!,
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
                    restorePlan!,
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
        IIntegrationRestorePlan restorePlan,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Restoring {Count} integration packages via bundled NuGet", packageRefs.Count);

        var packages = packageRefs
            .Select(r => (r.Name, Version: restorePlan.GetRestoreVersion(r.Name, r.Version!)))
            .ToList();
        using var restoreConfiguration = await restorePlan.CreatePackageRestoreConfigurationAsync(
            cancellationToken).ConfigureAwait(false);

        return await _nugetService.RestorePackagesAsync(
            packages,
            workingDirectory: _appDirectoryPath,
            targetFramework: DotNetBasedAppHostServerProject.TargetFramework,
            runtimeIdentifier: RuntimeInformation.RuntimeIdentifier,
            sources: restoreConfiguration.Sources,
            nugetConfigPaths: restoreConfiguration.ConfigPaths,
            nugetSettingsCacheIdentity: restoreConfiguration.SettingsCacheIdentity,
            nugetConfigOverlayCacheIdentity: restoreConfiguration.OverlayCacheIdentity,
            additionalSensitiveSources: restoreConfiguration.SensitiveSources,
            globalPackagesFolderOverride: restoreConfiguration.GlobalPackagesFolder,
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
        IIntegrationRestorePlan restorePlan,
        CancellationToken cancellationToken)
    {
        var restoreDir = Path.Combine(_workingDirectory, "integration-restore");
        Directory.CreateDirectory(restoreDir);

        var policyDirectory = IntegrationClosureBuilder.GetAppHostIntegrationPolicyDirectory(
            new DirectoryInfo(_appDirectoryPath));
        var restoreConfiguration = await restorePlan.ApplyProjectRestoreConfigurationAsync(
            policyDirectory,
            cancellationToken).ConfigureAwait(false);
        var integrationPackageSources = IntegrationClosureBuilder.CreateRestoreAdditionalProjectSourcesValue(
            existingValue: null,
            restoreConfiguration.PackageSourceHints);
        var intermediateOutputPath = Path.Combine(restoreDir, "obj");
        var projectContent = GenerateIntegrationProjectFile(
            projectRefs,
            restorePlan.GetRestoreVersion("Aspire.Hosting", sdkVersion),
            restoreDir,
            restoreConfiguration.RootAdditionalSources);
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
                restoreConfiguration.RestoreRootConfigDirectory,
                globalPackagesFolder: null).ToString(),
            cancellationToken);

        // Write empty Directory.Build.targets to prevent parent targets imports.
        await WriteIfChangedAsync(
            Path.Combine(restoreDir, "Directory.Build.targets"), "<Project />", cancellationToken);

        _logger.LogDebug("Building integration project with {ProjectCount} project references", projectRefs.Count);

        var (exitCode, buildOutput) = await BuildIntegrationProjectAsync(
            projectFilePath,
            noRestore: false,
            restoreConfiguration.GlobalPackagesFolder,
            integrationHostingVersion: sdkVersion,
            integrationPackageSources,
            suppressLogging: restoreConfiguration.SensitiveSources.Length > 0,
            restoreConfiguration.SensitiveSources,
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
        string hostingPackageVersion,
        string restoreDir,
        IEnumerable<string>? additionalSources = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostingPackageVersion);

        var projectFile = IntegrationClosureBuilder.CreateClosureProjectFile(
            restoreDir,
            additionalSources);

        // Keep the pre-existing compatibility check between the AppHost SDK selected by the CLI and
        // project-referenced integrations. All other integration packages use the package-only path.
        projectFile.PackageReferences.Add(new CSharpPackageReference(
            "Aspire.Hosting",
            hostingPackageVersion));

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

    internal Task<IIntegrationRestorePlan> ResolveIntegrationRestorePlanAsync(
        string sdkVersion,
        string? requestedChannel,
        string? packageSourceOverride,
        string? packageSourceOverridePattern,
        CancellationToken cancellationToken)
        => new IntegrationRestorePlanResolver(
            _packagingService,
            _nugetService,
            _executionContext,
            _logger)
            .ResolveAsync(
                _appDirectoryPath,
                sdkVersion,
                requestedChannel,
                packageSourceOverride,
                packageSourceOverridePattern,
                cancellationToken);

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
