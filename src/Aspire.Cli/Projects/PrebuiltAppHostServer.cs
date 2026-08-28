// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
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
internal sealed partial class PrebuiltAppHostServer : IAppHostServerProject, IDisposable
{
    // Closure file names are owned by IntegrationClosureBuilder so generated integration
    // projects cannot drift from the post-build reader's MSBuild contract.
    internal const string ClosureManifestFileName = "closure-manifest.txt";
    internal const string IntegrationProjectFileName = "IntegrationRestore.csproj";

    private const string ProjectAssetsFileName = "project.assets.json";
    private const string RestoreStampFileName = "aspire-restore.stamp";

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
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Restoring {Count} integration packages via bundled NuGet", packageRefs.Count);

        var useExactPackageVersions = !string.IsNullOrWhiteSpace(packageSourceOverride);
        var packages = packageRefs
            .Select(r => (r.Name, Version: GetRestoreVersion(r.Name, r.Version!, useExactPackageVersions)))
            .ToList();
        var restoreSources = await ResolveIntegrationRestoreSourcesAsync(requestedChannel, packageSourceOverride, cancellationToken).ConfigureAwait(false);
        using var temporaryNuGetConfig = await CreateTemporaryNuGetConfigAsync(restoreSources).ConfigureAwait(false);
        var sources = GetNuGetSources(restoreSources);

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
    /// Reads every restore input, returning its fingerprint and whether the closure is eligible
    /// for a skipped restore at all.
    /// </summary>
    /// <remarks>
    /// The generated project file and optional synthesized NuGet.config encode package identities
    /// and versions, project reference paths, and channel sources. Referenced project files are hashed
    /// as well because restore resolves their dependencies too: a referenced project bumping its own
    /// Aspire.Hosting version changes the resolved closure without changing a single byte of the
    /// generated project file.
    /// <para>
    /// The whole project-reference graph is walked, not just its first level, because restore
    /// resolves the graph: a package bump two hops out changes the closure exactly as much as one
    /// hop out does. Each project's directory-scoped MSBuild imports are hashed with it, since under
    /// central package management the reference carries no version at all and bumping
    /// Directory.Packages.props changes what restore resolves while every project file stays
    /// byte-for-byte identical.
    /// </para>
    /// <para>
    /// Every project in that closure is also scanned for floating versions, because a float anywhere
    /// in it can resolve to a different package without any local input changing.
    /// </para>
    /// </remarks>
    internal static async Task<RestoreInputs> ComputeRestoreInputsAsync(
        string projectContent,
        IReadOnlyList<IntegrationReference> packageRefs,
        IReadOnlyList<IntegrationReference> projectRefs,
        CancellationToken cancellationToken)
        => await ComputeRestoreInputsAsync(projectContent, packageRefs, projectRefs, restoreConfigContent: null, cancellationToken).ConfigureAwait(false);

    internal static async Task<RestoreInputs> ComputeRestoreInputsAsync(
        string projectContent,
        IReadOnlyList<IntegrationReference> packageRefs,
        IReadOnlyList<IntegrationReference> projectRefs,
        string? restoreConfigContent,
        CancellationToken cancellationToken)
    {
        var hash = new XxHash3();
        hash.Append(Encoding.UTF8.GetBytes(projectContent));
        if (restoreConfigContent is not null)
        {
            hash.Append(Encoding.UTF8.GetBytes(restoreConfigContent));
        }

        var isFloating = HasFloatingPackageVersion(packageRefs);

        var pending = new Queue<string>();
        // Ordinal rather than a path-aware comparer: a duplicate spelling of the same path costs one
        // extra read, whereas treating two genuinely different paths as one would drop an input.
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var closure = new List<string>();

        foreach (var projectRef in projectRefs)
        {
            if (projectRef.ProjectPath is { } path)
            {
                pending.Enqueue(path);
            }
        }

        while (pending.Count > 0)
        {
            var projectPath = pending.Dequeue();
            var normalizedPath = NormalizeProjectPath(projectPath);

            // Terminates on its own rather than hanging the launch: MSBuild rejects a project
            // reference cycle, but the fingerprint is computed before anything validates the graph.
            if (!visited.Add(normalizedPath))
            {
                continue;
            }

            closure.Add(normalizedPath);

            foreach (var referenced in ReadProjectReferences(normalizedPath))
            {
                pending.Enqueue(referenced);
            }
        }

        // Ordering makes the fingerprint independent of the order the graph happened to be walked in.
        // Hash the path as well as the content so that repointing a reference at a different project
        // with identical content is still seen as a change.
        foreach (var projectPath in closure.OrderBy(static path => path, StringComparer.Ordinal))
        {
            hash.Append(Encoding.UTF8.GetBytes(projectPath));

            if (!File.Exists(projectPath))
            {
                continue;
            }

            var projectBytes = await File.ReadAllBytesAsync(projectPath, cancellationToken).ConfigureAwait(false);
            hash.Append(projectBytes);

            if (!isFloating && HasFloatingVersionAttribute(Encoding.UTF8.GetString(projectBytes)))
            {
                isFloating = true;
            }
        }

        foreach (var importPath in FindDirectoryScopedImports(closure).OrderBy(static path => path, StringComparer.Ordinal))
        {
            hash.Append(Encoding.UTF8.GetBytes(importPath));

            var importBytes = await File.ReadAllBytesAsync(importPath, cancellationToken).ConfigureAwait(false);
            hash.Append(importBytes);

            if (!isFloating && HasFloatingVersionAttribute(Encoding.UTF8.GetString(importBytes)))
            {
                isFloating = true;
            }
        }

        return new RestoreInputs(Convert.ToHexString(hash.GetCurrentHash()), IsEligibleForSkip: !isFloating);
    }

    /// <summary>
    /// Resolves a project path to a comparable absolute form so the same project reached by two
    /// different spellings is hashed once.
    /// </summary>
    private static string NormalizeProjectPath(string projectPath)
    {
        try
        {
            return Path.GetFullPath(projectPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // An unresolvable path is still hashed verbatim: it cannot be read, but the fact that the
            // closure names it is itself an input, and a later change to a valid path is then seen.
            return projectPath;
        }
    }

    /// <summary>
    /// Reads the &lt;ProjectReference Include="..." /&gt; paths a project declares, resolved against
    /// the project's own directory the way MSBuild resolves them.
    /// </summary>
    /// <remarks>
    /// Parsed as XML rather than with a regex because an Include can be spread across attributes and
    /// whitespace. A project that cannot be read or parsed contributes no references: the file itself
    /// is still hashed above, so a later fix to it changes the fingerprint.
    /// </remarks>
    private static List<string> ReadProjectReferences(string projectPath)
    {
        var references = new List<string>();

        if (!File.Exists(projectPath))
        {
            return references;
        }

        XDocument document;
        try
        {
            using var stream = File.OpenRead(projectPath);
            // DTD processing stays off: these files are inputs from the user's checkout and an
            // external entity must never be fetched while computing a fingerprint.
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return references;
        }

        var projectDirectory = Path.GetDirectoryName(projectPath);
        if (projectDirectory is null)
        {
            return references;
        }

        foreach (var element in document.Descendants().Where(static e => e.Name.LocalName == "ProjectReference"))
        {
            var include = element.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(include))
            {
                continue;
            }

            // An MSBuild property in the path ($(RepoRoot)/...) cannot be expanded without evaluating
            // the project, so the reference is skipped rather than hashed under a nonsense path.
            if (include.Contains("$(", StringComparison.Ordinal))
            {
                continue;
            }

            references.Add(Path.Combine(projectDirectory, include.Replace('\\', Path.DirectorySeparatorChar)));
        }

        return references;
    }

    /// <summary>
    /// Finds the directory-scoped files MSBuild and NuGet import automatically for the projects in a
    /// closure, by walking from each project's directory to the root the way they do.
    /// </summary>
    /// <remarks>
    /// These carry version information that never appears in the project file itself - most
    /// importantly Directory.Packages.props under central package management, where the reference is
    /// written without a version at all.
    /// <list type="bullet">
    /// <item>https://learn.microsoft.com/nuget/consume-packages/central-package-management</item>
    /// <item>https://learn.microsoft.com/visualstudio/msbuild/customize-by-directory</item>
    /// </list>
    /// </remarks>
    private static HashSet<string> FindDirectoryScopedImports(IReadOnlyList<string> closure)
    {
        var imports = new HashSet<string>(StringComparer.Ordinal);
        var scannedDirectories = new HashSet<string>(StringComparer.Ordinal);

        foreach (var projectPath in closure)
        {
            var directory = Path.GetDirectoryName(projectPath);

            while (directory is not null && scannedDirectories.Add(directory))
            {
                foreach (var fileName in s_directoryScopedImportFileNames)
                {
                    var candidate = Path.Combine(directory, fileName);
                    if (File.Exists(candidate))
                    {
                        imports.Add(candidate);
                    }
                }

                directory = Path.GetDirectoryName(directory);
            }
        }

        return imports;
    }

    // NuGet.config is matched case-insensitively by NuGet itself, but the two spellings below are the
    // ones it documents and the ones repositories actually use.
    private static readonly string[] s_directoryScopedImportFileNames =
    [
        "Directory.Packages.props",
        "Directory.Build.props",
        "Directory.Build.targets",
        "NuGet.config",
        "nuget.config"
    ];

    /// <summary>
    /// The restore inputs for one integration closure.
    /// </summary>
    /// <param name="Fingerprint">Identifies the exact set of inputs the restore reads.</param>
    /// <param name="IsEligibleForSkip">
    /// Whether an unchanged fingerprint is enough to prove the resolved closure is unchanged.
    /// </param>
    internal readonly record struct RestoreInputs(string Fingerprint, bool IsEligibleForSkip);

    /// <summary>
    /// Returns <see langword="true" /> when a project file declares a package version that NuGet
    /// resolves against the feed rather than pinning exactly.
    /// </summary>
    /// <remarks>
    /// Matches the version attribute of a reference, for example
    /// <c>&lt;PackageReference Include="Aspire.Hosting" Version="13.4.*" /&gt;</c> or
    /// <c>VersionOverride="[13.4,14)"</c>. The word boundary keeps unrelated attributes that merely
    /// end in "Version" (such as <c>ToolsVersion</c>) from matching. A false positive only forces a
    /// restore, which is the safe direction.
    /// </remarks>
    internal static bool HasFloatingVersionAttribute(string projectText)
        => FloatingVersionAttributeRegex().IsMatch(projectText);

    [GeneratedRegex("""\b(?:VersionOverride|Version)\s*=\s*"[^"]*[*\[(,]""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FloatingVersionAttributeRegex();

    /// <summary>
    /// Returns <see langword="true" /> when any package version can resolve to a different package
    /// without any local input changing, which makes the closure ineligible for a skipped restore.
    /// </summary>
    /// <remarks>
    /// A floating version ("13.4.*") or a range ("[13.4,14)") is resolved by NuGet at restore time
    /// against the feed, so an unchanged fingerprint does not imply an unchanged closure.
    /// </remarks>
    internal static bool HasFloatingPackageVersion(IReadOnlyList<IntegrationReference> packageRefs)
        => packageRefs.Any(static r => r.Version is { } version && version.AsSpan().ContainsAny(s_floatingVersionChars));

    // '*' is a float, and '[', '(', ',' delimit a version range. An exact version contains none of them.
    private static readonly SearchValues<char> s_floatingVersionChars = SearchValues.Create("*[(,");

    /// <summary>
    /// Determines whether the last successful restore already saw this exact set of inputs.
    /// </summary>
    /// <remarks>
    /// The stamp is written only after a restore succeeds, so its presence with a matching
    /// fingerprint means a complete restore has run for these inputs. This is compared by content
    /// rather than by timestamp because file modification times are unreliable across coarse
    /// filesystems, clock skew, and caches that restore mtimes.
    /// </remarks>
    internal static bool CanSkipIntegrationRestore(string restoreDir, string expectedFingerprint, ILogger logger)
    {
        var assetsPath = Path.Combine(restoreDir, "obj", ProjectAssetsFileName);
        var stampPath = Path.Combine(restoreDir, "obj", RestoreStampFileName);
        if (!File.Exists(assetsPath) || !File.Exists(stampPath))
        {
            return false;
        }

        try
        {
            return string.Equals(File.ReadAllText(stampPath), expectedFingerprint, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Unable to read the integration restore stamp; restoring.");
            return false;
        }
    }

    /// <summary>
    /// Records that a restore completed successfully for <paramref name="fingerprint" />.
    /// </summary>
    private static async Task WriteRestoreStampAsync(string restoreDir, string fingerprint, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            var objDir = Path.Combine(restoreDir, "obj");
            Directory.CreateDirectory(objDir);
            await File.WriteAllTextAsync(Path.Combine(objDir, RestoreStampFileName), fingerprint, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A missing stamp only costs a restore on the next launch, so this is not worth failing over.
            logger.LogDebug(ex, "Unable to write the integration restore stamp.");
        }
    }

    /// <summary>
    /// Returns <see langword="true" /> when a build failure looks like one that restoring would fix.
    /// </summary>
    /// <remarks>
    /// Only a package-resolution failure is worth a second build. Retrying every failure would
    /// double the cost of an ordinary compile error and would replace its diagnostic with whatever
    /// the restore attempt produced.
    /// The restore fingerprint covers this app's own inputs but cannot see the shared global package
    /// cache, so a `dotnet nuget locals all --clear` (or any cache eviction) leaves the fingerprint
    /// unchanged while the packages it assumes are gone. Because the stamp is only ever written
    /// after a successful restore and is never cleared, a no-restore build that fails this way would
    /// otherwise fail identically on every subsequent run until the user manually deleted obj/.
    /// Examples of the failures this matches:
    ///   error NETSDK1004: Assets file '/path/obj/project.assets.json' not found. Run a NuGet package restore.
    ///   error NETSDK1064: Package Aspire.Hosting.Redis, version 13.5.0 was not found. It might have been deleted since NuGet restore.
    ///   error NU1101: Unable to find package Aspire.Hosting.Java. No packages exist with this id in source(s): dotnet-public
    ///   error NU1102: Unable to find package Aspire.Hosting with version (&gt;= 13.6.0-dev)
    /// </remarks>
    internal static bool ShouldRetryWithRestore(OutputCollector buildOutput)
        => buildOutput.GetLines().Any(static l =>
            l.Line.Contains("NETSDK1004", StringComparison.Ordinal) ||
            l.Line.Contains("NETSDK1064", StringComparison.Ordinal) ||
            l.Line.Contains("NU1101", StringComparison.Ordinal) ||
            l.Line.Contains("NU1102", StringComparison.Ordinal) ||
            l.Line.Contains(ProjectAssetsFileName, StringComparison.Ordinal));

    /// <summary>
    /// Produces the failure message for a failed integration build, recognizing the one failure
    /// mode that is a configuration problem rather than a build problem.
    /// </summary>
    /// <remarks>
    /// The AppHost server is the CLI itself, so the synthesized project pins Aspire.Hosting to the
    /// CLI's own version. A project reference that requires a newer Aspire.Hosting cannot be
    /// satisfied, and NuGet reports it as a downgrade:
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
        CancellationToken cancellationToken)
    {
        var buildOutput = new OutputCollector();
        var exitCode = await _dotNetCliRunner.BuildAsync(
            new FileInfo(projectFilePath),
            noRestore,
            new ProcessInvocationOptions
            {
                StandardOutputCallback = buildOutput.AppendOutput,
                StandardErrorCallback = buildOutput.AppendError
            },
            cancellationToken).ConfigureAwait(false);

        return (exitCode, buildOutput);
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

        var restoreSources = await ResolveIntegrationRestoreSourcesAsync(requestedChannel, packageSourceOverride, cancellationToken).ConfigureAwait(false);
        var useMappedRestoreConfig = !string.IsNullOrWhiteSpace(packageSourceOverride) &&
            restoreSources.PackageSourceMappings is not null;
        var hasCredentialBearingMappedSource = useMappedRestoreConfig &&
            restoreSources.PackageSourceMappings!.Any(
                static mapping => PackageSourceOverrideMappings.HasCredentialMaterial(mapping.Source));
        var hasCredentialBearingAdditionalSource = !useMappedRestoreConfig &&
            restoreSources.AdditionalSources.Any(
                static source => PackageSourceOverrideMappings.HasCredentialMaterial(source));
        var hasCredentialBearingRestoreSource =
            hasCredentialBearingMappedSource || hasCredentialBearingAdditionalSource;

        if (!useMappedRestoreConfig || hasCredentialBearingMappedSource)
        {
            var persistentRestoreConfigFile = new FileInfo(Path.Combine(restoreDir, "nuget.config"));
            if (persistentRestoreConfigFile.Exists)
            {
                persistentRestoreConfigFile.Delete();
            }
        }

        if (hasCredentialBearingRestoreSource)
        {
            var restoreStampFile = new FileInfo(Path.Combine(restoreDir, "obj", RestoreStampFileName));
            if (restoreStampFile.Exists)
            {
                restoreStampFile.Delete();
            }
        }

        using var temporaryRestoreConfig = hasCredentialBearingMappedSource
            ? await CreateTemporaryNuGetConfigAsync(restoreSources).ConfigureAwait(false)
            : null;
        using var temporaryRestoreSourcesProps = hasCredentialBearingAdditionalSource
            ? await TemporaryRestoreSourcesProps.CreateAsync(restoreSources.AdditionalSources, cancellationToken).ConfigureAwait(false)
            : null;

        FileInfo? restoreConfigFile;
        string? restoreConfigContent;
        if (!useMappedRestoreConfig)
        {
            // Channel-only restores remain additive so NuGet continues discovering the user's
            // ambient configuration, including private feeds used by the project-reference graph.
            restoreConfigFile = null;
            restoreConfigContent = null;
        }
        else if (temporaryRestoreConfig is not null)
        {
            // Credential-bearing mapped URLs must exist only for the duration of this build.
            // Persisting the generated config under .aspire/integrations would retain credentials
            // from sources such as overrideStagingFeed after the command exits.
            restoreConfigFile = temporaryRestoreConfig.ConfigFile;
            restoreConfigContent = null;
        }
        else
        {
            restoreConfigFile = await WriteRestoreNuGetConfigAsync(restoreDir, restoreSources, cancellationToken).ConfigureAwait(false);
            restoreConfigContent = restoreConfigFile is null
                ? null
                : await File.ReadAllTextAsync(restoreConfigFile.FullName, cancellationToken).ConfigureAwait(false);
        }

        var channelSources = restoreConfigFile is null && temporaryRestoreSourcesProps is null
            ? GetNuGetSources(restoreSources)
            : null;
        var projectContent = GenerateIntegrationProjectFile(
            packageRefs,
            projectRefs,
            restoreDir,
            channelSources,
            useExactPackageVersions: !string.IsNullOrWhiteSpace(packageSourceOverride),
            restoreConfigFile: restoreConfigFile?.FullName,
            restoreSourcesPropsFile: temporaryRestoreSourcesProps?.PropsFile.FullName);
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
            IntegrationClosureBuilder.CreateClosureDirectoryBuildProps(restoreDir).ToString(),
            cancellationToken);

        // Write empty Directory.Build.targets to prevent parent targets imports.
        await WriteIfChangedAsync(
            Path.Combine(restoreDir, "Directory.Build.targets"), "<Project />", cancellationToken);

        // Restore dominates this build - measured at 5.6s of a 6.7s warm build - and it only needs to
        // run again when something restore actually reads has changed. That set of inputs is captured
        // as a content fingerprint rather than a timestamp comparison, and the stamp recording it is
        // written only after a restore succeeds.
        //
        // Skipping restore never skips the build itself, so an edit to a referenced project is still
        // compiled. And because a stale or partially cleaned obj/ directory is the one thing the
        // fingerprint cannot see, a no-restore build that fails on the assets file is retried with
        // restore rather than reported.
        string? restoreFingerprint = null;
        if (!hasCredentialBearingRestoreSource)
        {
            var restoreInputs = await ComputeRestoreInputsAsync(projectContent, packageRefs, projectRefs, restoreConfigContent, cancellationToken).ConfigureAwait(false);
            restoreFingerprint = restoreInputs.IsEligibleForSkip ? restoreInputs.Fingerprint : null;
        }

        var skipRestore = restoreFingerprint is not null && CanSkipIntegrationRestore(restoreDir, restoreFingerprint, _logger);

        _logger.LogDebug("Building integration project with {PackageCount} packages and {ProjectCount} project references (restore {RestoreState})",
            packageRefs.Count, projectRefs.Count, skipRestore ? "skipped" : "requested");

        var (exitCode, buildOutput) = await BuildIntegrationProjectAsync(projectFilePath, noRestore: skipRestore, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0 && skipRestore && ShouldRetryWithRestore(buildOutput))
        {
            _logger.LogDebug("Integration project build failed on the restore assets; retrying with restore. First attempt output:\n{BuildOutput}",
                string.Join(Environment.NewLine, buildOutput.GetLines().Select(l => l.Line)));
            (exitCode, buildOutput) = await BuildIntegrationProjectAsync(projectFilePath, noRestore: false, cancellationToken).ConfigureAwait(false);
        }

        if (exitCode != 0)
        {
            var outputLines = string.Join(Environment.NewLine, buildOutput.GetLines().Select(l => l.Line));
            _logger.LogError("Integration project build failed. Output:\n{BuildOutput}", outputLines);
            throw new AppHostServerPrepareFailedException(GetIntegrationBuildFailureMessage(buildOutput), buildOutput);
        }

        if (restoreFingerprint is not null && !skipRestore)
        {
            await WriteRestoreStampAsync(restoreDir, restoreFingerprint, _logger, cancellationToken).ConfigureAwait(false);
        }

        var projectRefAssemblyNames = await IntegrationClosureBuilder.ReadProjectRefAssemblyNamesAsync(
            restoreDir,
            _logger,
            cancellationToken).ConfigureAwait(false);
        var appSettingsContent = CreateAppSettingsContent(packageRefs, projectRefAssemblyNames);

        var closureManifest = await IntegrationClosureBuilder.ReadClosureManifestAsync(
            restoreDir,
            Path.Combine(restoreDir, "obj", IntegrationClosureBuilder.ProjectAssetsFileName),
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
    /// Generates a synthetic .csproj file that references all integration packages and projects.
    /// Building this project with CopyLocalLockFileAssemblies produces the full transitive DLL closure.
    /// </summary>
    internal static string GenerateIntegrationProjectFile(
        List<IntegrationReference> packageRefs,
        List<IntegrationReference> projectRefs,
        string restoreDir,
        IEnumerable<string>? additionalSources = null,
        bool useExactPackageVersions = false,
        string? restoreConfigFile = null,
        string? restoreSourcesPropsFile = null)
    {
        IEnumerable<string>? restoreAdditionalSources = additionalSources;
        if (!string.IsNullOrWhiteSpace(restoreConfigFile))
        {
            // RestoreAdditionalProjectSources can add feeds, but it cannot carry package source
            // mappings. Use the generated NuGet.config so Aspire* packages stay pinned to the
            // explicit source while non-Aspire dependencies can use fallback sources.
            restoreAdditionalSources = null;
        }

        var projectFile = IntegrationClosureBuilder.CreateClosureProjectFile(
            restoreDir,
            restoreAdditionalSources,
            restoreConfigFile);

        if (!string.IsNullOrWhiteSpace(restoreSourcesPropsFile))
        {
            projectFile.Imports.Add(new CSharpProjectImport(restoreSourcesPropsFile));
        }

        foreach (var packageReference in packageRefs)
        {
            if (packageReference.Version is null)
            {
                throw new InvalidOperationException($"Package reference '{packageReference.Name}' is missing a version.");
            }

            projectFile.PackageReferences.Add(new CSharpPackageReference(
                packageReference.Name,
                GetRestoreVersion(packageReference.Name, packageReference.Version, useExactPackageVersions)));
        }

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

    /// <summary>
    /// Gets NuGet sources from the resolved channel for bundled restore.
    /// </summary>
    internal async Task<IEnumerable<string>?> GetNuGetSourcesAsync(string? requestedChannel, string? packageSourceOverride, CancellationToken cancellationToken)
    {
        var restoreSources = await ResolveIntegrationRestoreSourcesAsync(requestedChannel, packageSourceOverride, cancellationToken).ConfigureAwait(false);
        return GetNuGetSources(restoreSources);
    }

    internal async Task<TemporaryNuGetConfig?> TryCreateTemporaryNuGetConfigAsync(string? requestedChannel, string? packageSourceOverride, CancellationToken cancellationToken)
    {
        var restoreSources = await ResolveIntegrationRestoreSourcesAsync(requestedChannel, packageSourceOverride, cancellationToken).ConfigureAwait(false);
        return await CreateTemporaryNuGetConfigAsync(restoreSources).ConfigureAwait(false);
    }

    private Task<IntegrationRestoreSources> ResolveIntegrationRestoreSourcesAsync(string? requestedChannel, string? packageSourceOverride, CancellationToken cancellationToken)
        => new IntegrationRestoreSourceResolver(_packagingService, _logger, _executionContext.NuGetServiceIndexOverride)
            .ResolveAsync(requestedChannel, packageSourceOverride, cancellationToken);

    private static IEnumerable<string>? GetNuGetSources(IntegrationRestoreSources restoreSources)
        => restoreSources.AdditionalSources.Count > 0 ? restoreSources.AdditionalSources : null;

    private async Task<TemporaryNuGetConfig?> CreateTemporaryNuGetConfigAsync(IntegrationRestoreSources restoreSources)
    {
        if (restoreSources.PackageSourceMappings is null)
        {
            return null;
        }

        return await TemporaryNuGetConfig.CreateAsync(
            restoreSources.PackageSourceMappings,
            restoreSources.ConfigureGlobalPackagesFolder,
            restoreSources.ConfigureGlobalPackagesFolder
                ? CliPathHelper.GetStagingNuGetPackagesFeedDirectory(_executionContext.AspireHomeDirectory, restoreSources.GlobalPackagesFolderSource)
                : null).ConfigureAwait(false);
    }

    private async Task<FileInfo?> WriteRestoreNuGetConfigAsync(string restoreDir, IntegrationRestoreSources restoreSources, CancellationToken cancellationToken)
    {
        var restoreConfigFile = new FileInfo(Path.Combine(restoreDir, "nuget.config"));
        if (restoreSources.PackageSourceMappings is null)
        {
            if (restoreConfigFile.Exists)
            {
                restoreConfigFile.Delete();
            }

            return null;
        }

        using var temporaryConfig = await CreateTemporaryNuGetConfigAsync(restoreSources).ConfigureAwait(false);
        if (temporaryConfig is null)
        {
            return null;
        }

        var content = await File.ReadAllTextAsync(temporaryConfig.ConfigFile.FullName, cancellationToken).ConfigureAwait(false);
        await WriteIfChangedAsync(restoreConfigFile.FullName, content, cancellationToken).ConfigureAwait(false);
        return restoreConfigFile;
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

    private sealed class TemporaryRestoreSourcesProps : IDisposable
    {
        private readonly DirectoryInfo _directory;

        private TemporaryRestoreSourcesProps(DirectoryInfo directory, FileInfo propsFile)
        {
            _directory = directory;
            PropsFile = propsFile;
        }

        public FileInfo PropsFile { get; }

        public static async Task<TemporaryRestoreSourcesProps> CreateAsync(
            IReadOnlyList<string> sources,
            CancellationToken cancellationToken)
        {
            var directory = Directory.CreateTempSubdirectory("aspire-restore-sources");
            try
            {
                var propsFile = new FileInfo(Path.Combine(directory.FullName, "IntegrationRestoreSources.props"));
                var document = new XDocument(
                    new XElement("Project",
                        new XElement("PropertyGroup",
                            new XElement("RestoreAdditionalProjectSources", string.Join(";", sources)))));
                await File.WriteAllTextAsync(propsFile.FullName, document.ToString(), cancellationToken).ConfigureAwait(false);
                return new TemporaryRestoreSourcesProps(directory, propsFile);
            }
            catch
            {
                try
                {
                    directory.Delete(recursive: true);
                }
                catch
                {
                    // Ignore cleanup failures; surface the original exception instead.
                }

                throw;
            }
        }

        public void Dispose()
        {
            try
            {
                _directory.Delete(recursive: true);
            }
            catch
            {
                // Temporary source properties are best-effort cleanup after the build completes.
            }
        }
    }
}
