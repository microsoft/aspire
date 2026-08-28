// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Bundles;
using Aspire.Cli.Certificates;
using Aspire.Cli.Configuration;
using Aspire.Cli.Diagnostics;
using Aspire.Cli.DotNet;
using Aspire.Cli.Exceptions;
using Aspire.Cli.Interaction;
using Aspire.Cli.Processes;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Aspire.Hosting.Backchannel;
using Aspire.Hosting.Utils;
using Aspire.Shared;
using Aspire.Shared.UserSecrets;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Projects;

/// <summary>
/// Handler for .NET AppHost projects (.csproj and single-file .cs).
/// </summary>
internal sealed partial class DotNetAppHostProject : IAppHostProject
{
    private readonly IDotNetCliRunner _runner;
    private readonly IInteractionService _interactionService;
    private readonly ICertificateService _certificateService;
    private readonly AspireCliTelemetry _telemetry;
    private readonly ProfilingTelemetry _profilingTelemetry;
    private readonly IFeatures _features;
    private readonly ILogger<DotNetAppHostProject> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IProjectUpdater _projectUpdater;
    private readonly IDotNetSdkInstaller _sdkInstaller;
    private readonly IBundleService _bundleService;
    private readonly RunningInstanceManager _runningInstanceManager;
    private readonly Diagnostics.FileLoggerProvider _fileLoggerProvider;
    private readonly Program.CliLoggingOptions _loggingOptions;
    private readonly IAppHostInfoResolver _appHostInfoResolver;
    private readonly IConfigurationService _configurationService;
    private readonly IGracefulShutdownWindow _shutdownService;
    private readonly IProcessTreeGracefulShutdownSignaler _gracefulShutdownSignaler;
    private readonly CliExecutionContext _executionContext;
    private readonly IEnvironment _environment;

    private static readonly string[] s_detectionPatterns = ["*.csproj", "*.fsproj", "*.vbproj", "apphost.cs"];
    private const string DirectLaunchDisabledConfigKey = "dotnetAppHostDirectLaunchDisabled";

    private const string AspireAppHostSdkName = "Aspire.AppHost.Sdk";
    private const string IsAspireHostProperty = "IsAspireHost";
    private const string ProjectAppHostSourceFileName = "AppHost.cs";
    private const string DirectoryBuildPropsName = "Directory.Build.props";
    private const string DirectoryBuildTargetsName = "Directory.Build.targets";

    internal static IReadOnlyCollection<string> ProjectExtensions { get; } =
        Array.AsReadOnly([".csproj", ".fsproj", ".vbproj"]);

    /// <summary>
    /// Test seam: overrides <see cref="TryGetRepoLocalManagedPath"/>. When set, the override
    /// is invoked instead of probing the real Aspire repo checkout. Tests use this so the
    /// in-repo build artifact doesn't shadow the fake bundle layout they set up.
    /// </summary>
    internal static Func<string?>? RepoLocalManagedPathProviderOverride { get; set; }

    public DotNetAppHostProject(
        IDotNetCliRunner runner,
        IInteractionService interactionService,
        ICertificateService certificateService,
        AspireCliTelemetry telemetry,
        ProfilingTelemetry profilingTelemetry,
        IFeatures features,
        IProjectUpdater projectUpdater,
        IDotNetSdkInstaller sdkInstaller,
        IBundleService bundleService,
        IEnvironment environment,
        ILogger<DotNetAppHostProject> logger,
        Diagnostics.FileLoggerProvider fileLoggerProvider,
        Program.CliLoggingOptions loggingOptions,
        IAppHostInfoResolver appHostInfoResolver,
        IConfigurationService configurationService,
        IGracefulShutdownWindow shutdownService,
        IProcessTreeGracefulShutdownSignaler gracefulShutdownSignaler,
        CliExecutionContext executionContext,
        TimeProvider timeProvider)
    {
        _runner = runner;
        _interactionService = interactionService;
        _certificateService = certificateService;
        _telemetry = telemetry;
        _profilingTelemetry = profilingTelemetry;
        _features = features;
        _projectUpdater = projectUpdater;
        _sdkInstaller = sdkInstaller;
        _bundleService = bundleService;
        _environment = environment;
        _logger = logger;
        _fileLoggerProvider = fileLoggerProvider;
        _loggingOptions = loggingOptions;
        _appHostInfoResolver = appHostInfoResolver;
        _configurationService = configurationService;
        _shutdownService = shutdownService;
        _gracefulShutdownSignaler = gracefulShutdownSignaler;
        _executionContext = executionContext;
        _timeProvider = timeProvider;
        _runningInstanceManager = new RunningInstanceManager(_logger, _interactionService, _timeProvider, _profilingTelemetry);
    }

    // ═══════════════════════════════════════════════════════════════
    // IDENTITY
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public bool IsUnsupported { get; set; }

    /// <inheritdoc />
    public string LanguageId => KnownLanguageId.CSharp;

    /// <inheritdoc />
    public string DisplayName => "C# (.NET)";

    /// <inheritdoc />
    public bool SupportsLaunchProfiles => true;

    // ═══════════════════════════════════════════════════════════════
    // DETECTION
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public Task<string[]> GetDetectionPatternsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(s_detectionPatterns);

    /// <inheritdoc />
    public bool CanHandle(FileInfo appHostFile)
    {
        var extension = appHostFile.Extension.ToLowerInvariant();

        // Handle project files (.csproj, .fsproj, .vbproj)
        if (ProjectExtensions.Contains(extension))
        {
            // We can handle any project file - ValidateAsync will do deeper validation
            return true;
        }

        // Handle single-file apphosts (apphost.cs)
        if (extension == ".cs" && appHostFile.Name.Equals("apphost.cs", StringComparison.OrdinalIgnoreCase))
        {
            // Check for #:sdk Aspire.AppHost.Sdk directive
            return IsValidSingleFileAppHost(appHostFile);
        }

        return false;
    }

    private static bool IsValidSingleFileAppHost(FileInfo candidateFile)
    {
        // Check no sibling .csproj files exist
        var siblingCsprojFiles = candidateFile.Directory!.EnumerateFiles("*.csproj", SearchOption.TopDirectoryOnly);
        if (siblingCsprojFiles.Any())
        {
            return false;
        }

        // Check for #:sdk Aspire.AppHost.Sdk directive
        try
        {
            using var reader = candidateFile.OpenText();
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var trimmedLine = line.TrimStart();
                if (trimmedLine.StartsWith("#:sdk Aspire.AppHost.Sdk", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    internal static bool IsLikelyAppHost(FileInfo projectFile)
    {
        if (!TryLoadProjectRoot(projectFile.FullName, out var root) || root is null)
        {
            // The file is missing, unreadable, or not well-formed XML. Before falling back to the
            // name heuristic, still consult ancestor Directory.Build.* markers — those can promote
            // an ordinary-named broken project to a real AppHost candidate, which should flow to
            // MSBuild as "possibly unbuildable" rather than be silently rejected here.
            if (AncestorDirectoryContainsAppHostMarker(projectFile.Directory))
            {
                return true;
            }
            return MatchesAppHostNameHeuristics(projectFile);
        }

        // 1) An Aspire AppHost marker declared inline in the project file itself.
        if (ContainsAppHostMarker(root))
        {
            return true;
        }

        // 1b) The project file itself can also pull in a marker via a dynamic walk-up Import — for
        //     example
        //       <Import Project="$([MSBuild]::GetPathOfFileAbove('Aspire.Common.props', ...))" />
        //     where the resolved file sets <IsAspireHost>true</IsAspireHost> or imports
        //     Aspire.AppHost.Sdk. The same fragility that prevents us from following these statically
        //     in ancestor Directory.Build.* files applies here, so apply the same narrow fallback:
        //     dynamic walk-up imports → candidate, ordinary static or unrelated-SDK imports → still
        //     filtered out by the cheap pre-check. Skipping this check leaves a regression hole where
        //     a normal-named project gets silently rejected before MSBuild evaluation runs.
        if (ContainsDynamicWalkUpImport(root))
        {
            return true;
        }

        // 2) A co-located Directory.Build.props/.targets can promote an otherwise ordinary-looking project to
        //    an Aspire AppHost during MSBuild evaluation (for example by setting
        //    <IsAspireHost>true</IsAspireHost> or importing the Aspire.AppHost.Sdk). Tests in this repo do
        //    exactly this. Those files are parsed as XML and matched on element names, so a real *setter*
        //    element is detected while a mere *consumer* of the property
        //    (Condition="'$(IsAspireHost)' == 'true'") is ignored. A loose substring match would instead
        //    over-promote every sibling that only reads the property.
        //
        //    MSBuild walks up the directory tree to import Directory.Build.props/.targets from the nearest
        //    ancestor that has one (see
        //    https://learn.microsoft.com/visualstudio/msbuild/customize-by-directory#search-scope), and that
        //    ancestor commonly chains to further parents via $(DirectoryBuildPropsPath)-style imports. Walk
        //    *all* ancestors here rather than stopping at the project's own directory: missing an ancestor
        //    marker would falsely reject a legal AppHost before MSBuild ever runs, which is the exact failure
        //    mode that broke `aspire run` against explicit/settings AppHost paths.
        if (AncestorDirectoryContainsAppHostMarker(projectFile.Directory))
        {
            return true;
        }

        // No inline or co-located Aspire marker. Fall back to the name heuristic.
        return MatchesAppHostNameHeuristics(projectFile);
    }

    private static bool TryLoadProjectRoot(string path, out XElement? root)
    {
        try
        {
            root = XDocument.Load(path).Root;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            root = null;
            return false;
        }
    }

    private static bool AncestorDirectoryContainsAppHostMarker(DirectoryInfo? directory)
    {
        // MSBuild imports only the NEAREST Directory.Build.props and the NEAREST Directory.Build.targets,
        // discovered by walking parent directories up to the filesystem root. That discovery has NO .git
        // boundary (see https://learn.microsoft.com/visualstudio/msbuild/customize-by-directory), so a
        // valid AppHost inside a nested repo, submodule, or worktree can still inherit a marker from above
        // its inner .git. Crucially, MSBuild does NOT import every ancestor: an outer file is invisible to
        // the project unless the nearest file explicitly chains to its parent. Verified against real
        // MSBuild:
        //   outer/Directory.Build.props        <IsAspireHost>true</IsAspireHost>
        //   outer/repo/Directory.Build.props   (no marker, does NOT import its parent)
        //   outer/repo/proj/proj.csproj  =>  dotnet msbuild -getProperty:IsAspireHost prints EMPTY
        // Adding <Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', ...))" /> to the
        // nested file makes the same command print `true`. Walking every ancestor and accepting any marker
        // would report a false positive for the first layout and force MSBuild evaluation for every ordinary
        // project below a shadowed marker — the exact "evaluation storm" this prefilter exists to prevent.
        //
        // props and targets are searched independently because MSBuild resolves them independently: a marker
        // in the nearest Directory.Build.targets counts even when the nearest Directory.Build.props does not
        // chain, and vice versa.
        return NearestDirectoryBuildFileChainContainsMarker(directory, DirectoryBuildPropsName)
            || NearestDirectoryBuildFileChainContainsMarker(directory, DirectoryBuildTargetsName);
    }

    private static bool NearestDirectoryBuildFileChainContainsMarker(DirectoryInfo? directory, string fileName)
    {
        // Walk up from the project directory following MSBuild's "nearest file, then explicit chain" rule
        // for a single Directory.Build.* file name. At the first level that actually has the file we stop —
        // unless that file has no marker but chains to its parent (continue up), or pulls in content we
        // cannot resolve statically (conservatively treat the project as a candidate).
        for (var current = directory; current is not null; current = current.Parent)
        {
            var filePath = Path.Combine(current.FullName, fileName);
            if (!File.Exists(filePath))
            {
                continue;
            }

            if (!TryLoadProjectRoot(filePath, out var root) || root is null)
            {
                // The nearest file exists but can't be read/parsed. MSBuild would still evaluate it and it
                // could set <IsAspireHost>true</IsAspireHost> or import Aspire.AppHost.Sdk, so keep the
                // project as a candidate rather than silently rejecting it.
                return true;
            }

            if (ContainsAppHostMarker(root))
            {
                return true;
            }

            // No marker in the nearest file. Whether MSBuild ever imports an OUTER file of the same name
            // depends entirely on whether this file chains to its parent.
            switch (ClassifyDirectoryBuildChaining(root, fileName))
            {
                case DirectoryBuildChaining.Uncertain:
                    // The file pulls in content we cannot resolve statically (a walk-up import to a
                    // non-conventional or out-of-tree target, or one we cannot parse). A marker could live
                    // behind it, so let the authoritative MSBuild evaluation decide.
                    return true;
                case DirectoryBuildChaining.ChainsToParent:
                    // This file imports its parent of the same name, so MSBuild keeps reading upward.
                    // Continue the walk to the next nearest file to look for the marker there.
                    continue;
                case DirectoryBuildChaining.StopsHere:
                default:
                    // MSBuild imports only this nearest file for this name and it declares no marker; any
                    // outer file is shadowed and never evaluated. Promoting the project here would be a
                    // false positive, so stop.
                    return false;
            }
        }

        return false;
    }

    private enum DirectoryBuildChaining
    {
        // The nearest marker-less file terminates the import chain for this name — MSBuild imports nothing
        // further up, so any outer marker is shadowed.
        StopsHere,

        // The file imports its parent of the same name, so the walk should continue to the next file up.
        ChainsToParent,

        // The file pulls in content we cannot resolve statically; a marker could hide behind it.
        Uncertain,
    }

    private static IEnumerable<string> SplitImportProjectEntries(string projectAttributeValue)
    {
        // MSBuild accepts import lists such as:
        //   <Import Project="../Directory.Build.props;Shared.props" />
        // Each entry resolves independently, so classifying the unsplit value as one path can hide a
        // conventional parent-chain import behind an unrelated entry.
        return projectAttributeValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static DirectoryBuildChaining ClassifyDirectoryBuildChaining(XElement root, string chainFileName)
    {
        // Decide, for a marker-less Directory.Build.<props|targets> file, whether MSBuild would keep
        // importing an OUTER file of the same name (ChainsToParent), whether it pulls in unresolvable
        // content that could hide a marker (Uncertain), or whether it terminates the chain for this name
        // (StopsHere). Only imports that provably reach the parent-of-same-name advance the chain; ordinary
        // shared infrastructure imports (Arcade, analyzer polyfills, Directory.Packages.props, ...) are
        // inert here — otherwise essentially every project in a .NET repo would be promoted.
        var chains = false;

        foreach (var import in root.Descendants().Where(e => e.Name.LocalName.Equals("Import", StringComparison.Ordinal)))
        {
            var projectAttributeValue = import.Attribute("Project")?.Value;
            if (projectAttributeValue is null)
            {
                continue;
            }

            foreach (var project in SplitImportProjectEntries(projectAttributeValue))
            {
                if (WalkUpFunctionCallStartRegex().IsMatch(project))
                {
                    // A tree-walking import: $([MSBuild]::GetPathOfFileAbove/GetDirectoryNameOfFileAbove(...)).
                    if (TryGetWalkUpImport(project, out var walkUpImport) && IsAlreadyEnumeratedDirectoryBuildChain(walkUpImport))
                    {
                        // Immediate-parent chaining import of a conventional Directory.Build.* file: its target
                        // is the nearest file at or above this file's parent — the exact next level this walk
                        // visits by name. This shortcut is only valid for the same-name chain. A props file can
                        // explicitly import a parent targets file while a nearer targets file shadows the
                        // normal targets pass, so cross-name imports must remain uncertain.
                        if (walkUpImport.AnchorFileName.Equals(chainFileName, StringComparison.Ordinal))
                        {
                            chains = true;
                            continue;
                        }

                        return DirectoryBuildChaining.Uncertain;
                    }

                    // Walk-up import to a non-conventional target, a level-skipping or out-of-tree starting
                    // directory, or one we cannot parse — MSBuild may import a file this loop would never reach,
                    // so the whole file is uncertain and the project stays a candidate.
                    return DirectoryBuildChaining.Uncertain;
                }

                switch (ClassifyStaticImportChaining(project, chainFileName).Chaining)
                {
                    case DirectoryBuildChaining.Uncertain:
                        return DirectoryBuildChaining.Uncertain;
                    case DirectoryBuildChaining.ChainsToParent:
                        chains = true;
                        break;
                    case DirectoryBuildChaining.StopsHere:
                    default:
                        break;
                }
            }
        }

        return chains ? DirectoryBuildChaining.ChainsToParent : DirectoryBuildChaining.StopsHere;
    }

    /// <summary>
    /// Result of classifying one ordinary (non-walk-up) <c>&lt;Import Project="..."&gt;</c>.
    /// </summary>
    /// <param name="Chaining">How the import affects the Directory.Build.* chain walk.</param>
    /// <param name="UncertainTargetPath">
    /// When <paramref name="Chaining"/> is <see cref="DirectoryBuildChaining.Uncertain"/>, the imported path
    /// as written after expanding <c>$(MSBuildThisFileDirectory)</c> — relative to the importing file's
    /// directory, or rooted. <see langword="null"/> when the target cannot be resolved without MSBuild
    /// (an unexpandable property/item reference, or a wildcard). Callers that need to fingerprint the
    /// admitted dependency use this to tell "exact file" apart from "unknowable file".
    /// </param>
    private readonly record struct StaticImportClassification(
        DirectoryBuildChaining Chaining,
        string? UncertainTargetPath);

    private static StaticImportClassification ClassifyStaticImportChaining(string projectAttributeValue, string chainFileName)
    {
        // Classify one ordinary (non-walk-up) <Import Project="..."> inside a marker-less
        // Directory.Build.<props|targets>. Raw values seen in the wild, and how each is treated:
        //
        //   "../Directory.Build.props"                              -> ChainsToParent (verified under real MSBuild)
        //   "$(MSBuildThisFileDirectory)..\Directory.Build.props"   -> ChainsToParent; $(MSBuildThisFileDirectory)
        //                                                             is a reserved property that always expands
        //                                                             to THIS file's directory plus a trailing
        //                                                             slash, so the target is statically knowable
        //   "$(MSBuildThisFileDirectory)../../Directory.Build.props"-> Uncertain (skips the level this walk
        //                                                             visits next, so an outer marker could be
        //                                                             imported that the walk never sees)
        //   "$(RepoRoot)Directory.Build.props"                      -> Uncertain (conventional name, but at a
        //                                                             directory only MSBuild can compute)
        //   "$(RepositoryEngineeringDir)NullablePolyfill.targets"   -> StopsHere (ordinary shared infrastructure)
        //   "Sdk.props" / "../Versions.props"                       -> StopsHere (ordinary static import)
        //
        // Only imports that provably reach the parent-of-same-name advance the chain. Ordinary shared
        // infrastructure imports stay inert — following those would over-promote essentially every project in
        // a .NET repo (this repo's own root Directory.Build.targets imports
        // $(RepositoryEngineeringDir)/NullablePolyfill.targets). But an import that lands on a *conventional*
        // Directory.Build.* file at a location we cannot pin down must NOT be treated as inert: MSBuild
        // resolves it and can see an outer marker there, while reporting StopsHere would reject the project.
        var trimmed = projectAttributeValue.Trim();
        if (trimmed.Length == 0)
        {
            return new StaticImportClassification(DirectoryBuildChaining.StopsHere, null);
        }

        // $(MSBuildThisFileDirectory) is the one property we can expand ourselves: MSBuild defines it as the
        // directory of the file containing the import, with a trailing slash.
        // https://learn.microsoft.com/visualstudio/msbuild/msbuild-reserved-and-well-known-properties
        const string thisFileDirectory = "$(MSBuildThisFileDirectory)";
        var startsWithThisFileDirectory = trimmed.StartsWith(thisFileDirectory, StringComparison.OrdinalIgnoreCase);
        var relativePath = startsWithThisFileDirectory
            ? trimmed[thisFileDirectory.Length..].TrimStart('/', '\\')
            : trimmed;

        if (relativePath.Contains('$') || relativePath.Contains('@'))
        {
            // An unexpandable property/item reference remains. The literal tail of the final path segment is
            // often still readable (e.g. "$(RepoRoot)Directory.Build.props"), and that is enough to tell an
            // inert infrastructure import apart from one that could pull in an outer Directory.Build.* marker.
            // The directory it lands in is still unknown, so there is no path to fingerprint.
            var expressionFileName = GetLiteralFileNameSuffix(GetFinalPathSegment(relativePath));
            return CanMatchConventionalDirectoryBuildFileName(expressionFileName)
                ? new StaticImportClassification(DirectoryBuildChaining.Uncertain, null)
                : new StaticImportClassification(DirectoryBuildChaining.StopsHere, null);
        }

        var fileName = GetFinalPathSegment(relativePath);
        var uncertainTargetPath = ContainsWildcard(relativePath) ? null : relativePath;
        if (!IsConventionalDirectoryBuildFileName(fileName))
        {
            // A differently-cased conventional name can resolve to the same file on Windows, or to a
            // distinct explicitly imported file on a case-sensitive filesystem. Wildcards can also match
            // one of those files. Any such target can carry the marker, so it is not safe to classify the
            // import as unrelated.
            return CanMatchConventionalDirectoryBuildFileName(fileName)
                ? new StaticImportClassification(DirectoryBuildChaining.Uncertain, uncertainTargetPath)
                : new StaticImportClassification(DirectoryBuildChaining.StopsHere, null);
        }

        // A wildcard import expands to every match at evaluation time, so the imported set is not a single
        // knowable path. IsConventionalDirectoryBuildFileName above already rejects a wildcard in the file
        // name itself, so only the directory portion can still hold one (e.g. "../*/Directory.Build.props").
        if (Path.IsPathRooted(relativePath))
        {
            // An absolute path can point at a Directory.Build.* anywhere on disk, including outside this
            // project's ancestor chain, so the walk cannot prove the outer file is shadowed.
            return new StaticImportClassification(
                DirectoryBuildChaining.Uncertain,
                startsWithThisFileDirectory ? null : uncertainTargetPath);
        }

        // Count how many levels up the directory portion travels. Anything other than "exactly one level up"
        // either cannot advance this walk correctly or leaves the enumerated chain entirely.
        var upLevels = 0;
        var directorySegments = relativePath.Split('/', '\\');
        for (var i = 0; i < directorySegments.Length - 1; i++)
        {
            switch (directorySegments[i].Trim())
            {
                case "" or ".":
                    continue;
                case "..":
                    upLevels++;
                    continue;
                default:
                    // A named segment descends into a directory this walk never enumerates.
                    return new StaticImportClassification(DirectoryBuildChaining.Uncertain, uncertainTargetPath);
            }
        }

        return upLevels switch
        {
            // Same directory: a same-name import is a self-import, which MSBuild ignores. A cross-name
            // sibling import is not necessarily covered by the other pass because that pass can stop at a
            // file nearer to the project before reaching this directory.
            0 => fileName.Equals(chainFileName, StringComparison.Ordinal)
                ? new StaticImportClassification(DirectoryBuildChaining.StopsHere, null)
                : new StaticImportClassification(DirectoryBuildChaining.Uncertain, uncertainTargetPath),
            // Immediate parent: advances the chain when it targets THIS name. A cross-name import is not
            // covered by the other pass because that pass starts at the project directory and can stop at a
            // nearer marker-less file before reaching the explicitly imported parent file.
            1 => fileName.Equals(chainFileName, StringComparison.Ordinal)
                ? new StaticImportClassification(DirectoryBuildChaining.ChainsToParent, null)
                : new StaticImportClassification(DirectoryBuildChaining.Uncertain, uncertainTargetPath),
            // Two or more levels up jumps over the file this walk visits next, exactly like a level-skipping
            // GetPathOfFileAbove start, so an outer marker could be imported that this walk never reaches.
            _ => new StaticImportClassification(DirectoryBuildChaining.Uncertain, uncertainTargetPath),
        };
    }

    private static bool ContainsWildcard(string path) => path.Contains('*') || path.Contains('?');

    private static string GetFinalPathSegment(string path)
    {
        var trimmed = path.Trim();
        var lastSeparator = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\'));
        return lastSeparator >= 0 ? trimmed[(lastSeparator + 1)..].Trim() : trimmed;
    }

    private static string GetLiteralFileNameSuffix(string segment)
    {
        // Recover the literal file name from a path segment that may begin with an MSBuild expression. The
        // expression itself expands to a directory prefix, so only the text after its closing parenthesis
        // names the file:
        //   "$(RepoRoot)Directory.Build.props"   -> "Directory.Build.props"
        //   "Directory.Build.props"              -> "Directory.Build.props"
        //   "$(Flavor).props"                    -> ".props"
        //   "$(SharedPropsFileName)"             -> "" (nothing literal left to identify)
        var lastExpressionEnd = segment.LastIndexOf(')');
        var tail = (lastExpressionEnd >= 0 ? segment[(lastExpressionEnd + 1)..] : segment).Trim();
        return tail.Contains('$') || tail.Contains('@') ? string.Empty : tail;
    }

    private static bool IsImmediateParentChainStart(string? startDirectoryArg)
    {
        // A conventional Directory.Build.* walk-up import only lets this ancestor walk keep going upward when
        // its search begins at the importing file's IMMEDIATE parent directory — the exact
        // '$(MSBuildThisFileDirectory)../' shape MSBuild's own Directory.Build.* chaining uses (verified: it
        // is the shape in this repo's src/ and tests/ Directory.Build.* files). GetPathOfFileAbove searches
        // the starting directory AND every directory above it, so starting at the immediate parent means
        // "nearest Directory.Build.* at or above the parent" — exactly the next file this loop visits by name.
        //
        // Two other shapes must NOT advance the loop:
        //  * A level-skipping start ('$(MSBuildThisFileDirectory)../../', etc.) begins the search ABOVE the
        //    immediate parent, so MSBuild can jump over an intervening marker-less Directory.Build.props and
        //    import a marker farther up. This loop would instead visit that skipped intermediate next and
        //    stop there, producing a false negative. Verified against real MSBuild: with a '../../' start and
        //    a marker two levels up, `dotnet msbuild -getProperty:IsAspireHost` prints `true`, while a
        //    nearest-file walk that stops at the intermediate would say false.
        //  * An omitted or same-directory start ('$(MSBuildThisFileDirectory)') resolves to the importing
        //    file's own directory. GetPathOfFileAbove includes the starting directory, so it finds THIS file
        //    (a self-import) and never reaches the parent — it does not chain upward at all.
        // GetPathOfFileAbove / GetDirectoryNameOfFileAbove also accept an ARBITRARY starting directory, e.g.
        //   $([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(ExternalBuildRoot)'))
        // which resolves a Directory.Build.props this walk never inspects (verified to pull in an external
        // IsAspireHost=true under real MSBuild). Any of these non-immediate-parent shapes is left to the
        // caller as "uncertain", keeping the project a candidate rather than risking a false negative.
        if (string.IsNullOrWhiteSpace(startDirectoryArg))
        {
            return false;
        }

        var start = StripQuotes(startDirectoryArg).Trim();
        const string thisFileDirectory = "$(MSBuildThisFileDirectory)";
        if (!start.StartsWith(thisFileDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // The remainder must resolve to exactly one directory level up: a single '..' segment, plus any
        // number of '.'/empty no-op segments. A second '..' skips past the immediate parent, and any named
        // segment could descend or jump out of the enumerated chain — both disqualify the shortcut.
        var upLevels = 0;
        foreach (var rawSegment in start[thisFileDirectory.Length..].Split('/', '\\'))
        {
            switch (rawSegment.Trim())
            {
                case "" or ".":
                    continue;
                case "..":
                    if (++upLevels > 1)
                    {
                        return false;
                    }
                    continue;
                default:
                    return false;
            }
        }

        return upLevels == 1;
    }

    private static bool IsAlreadyEnumeratedDirectoryBuildChain(in WalkUpImport import)
    {
        // Decide whether a walk-up import provably resolves to a file the ancestor walk already enumerates
        // by name, so skipping it cannot hide a marker. That is true for exactly one shape:
        //
        //   <Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))" />
        //
        // GetPathOfFileAbove(file, start) returns the nearest <file> at or above <start>, so with an
        // immediate-parent start and a conventional name the target is literally "the next Directory.Build.*
        // of this name the walk visits". Three restrictions keep the shortcut sound:
        //
        //  * Only GetPathOfFileAbove. GetDirectoryNameOfFileAbove(start, anchor) returns the DIRECTORY that
        //    holds <anchor>, and the imported file is whatever is appended after the call. The anchor — not
        //    the appended name — selects the directory, so
        //      $([MSBuild]::GetDirectoryNameOfFileAbove('$(MSBuildThisFileDirectory)../', 'Repo.marker'))/Directory.Build.props
        //    imports Directory.Build.props from whichever ancestor carries Repo.marker. That can be far above
        //    a nearer marker-less Directory.Build.props which shadows the ordinary chain, so the walk would
        //    stop early and reject a project MSBuild promotes.
        //  * No appended path. Once anything is concatenated onto the call, the anchor argument stops being
        //    the imported file for GetPathOfFileAbove too (e.g. `))/../Directory.Build.props`), which brings
        //    back the same anchor/target confusion.
        //  * A conventional target name and an immediate-parent start (see IsImmediateParentChainStart).
        return import.IsGetPathOfFileAbove
            && !import.HasAppendedPath
            && IsConventionalDirectoryBuildFileName(import.AnchorFileName)
            && IsImmediateParentChainStart(import.StartDirectoryArg);
    }

    /// <summary>
    /// Files outside the conventional <c>Directory.Build.*</c> / <c>Directory.Packages.*</c> set that the
    /// prefilter allowed to decide whether a project is an AppHost candidate.
    /// </summary>
    /// <param name="AncestorSearchFileNames">
    /// File names reached by accepted walk-up imports, ordinal-sorted and de-duplicated. A walk-up import
    /// binds to the nearest file of that name at or above the importing file, so statting each name at every
    /// ancestor level covers every file MSBuild could bind it to.
    /// </param>
    /// <param name="ExactFilePaths">
    /// Absolute paths of accepted static imports, ordinal-sorted and de-duplicated. Unlike walk-up imports
    /// these name exactly one file, so they are statted directly — including targets outside the project's
    /// ancestor chain, such as <c>../shared/Directory.Build.props</c>.
    /// </param>
    /// <param name="HasUnfingerprintableImport">
    /// <see langword="true"/> when at least one accepted import resolves somewhere no filesystem fingerprint
    /// can cover: an MSBuild expression where the file name belongs, a walk-up rooted outside the ancestor
    /// chain, an appended path that does not name a single file in the resolved directory, or a wildcard.
    /// </param>
    internal readonly record struct AppHostImportDependencies(
        IReadOnlyCollection<string> AncestorSearchFileNames,
        IReadOnlyCollection<string> ExactFilePaths,
        bool HasUnfingerprintableImport);

    /// <summary>
    /// Reports the custom imports that <see cref="IsLikelyAppHost"/> honors for <paramref name="projectFile"/>,
    /// so <c>AppHostInfoDiskCache</c> can either fingerprint them or refuse to cache. Those imports are the
    /// paths by which a marker reaches MSBuild without touching any file the cache's conventional walk
    /// already stats — for example <c>$([MSBuild]::GetPathOfFileAbove('Aspire.Common.props'))</c> or
    /// <c>&lt;Import Project="$(MSBuildThisFileDirectory)../shared/Directory.Build.props" /&gt;</c>, where
    /// flipping <c>IsAspireHost</c> inside the imported file changes the answer while leaving every tracked
    /// mtime untouched.
    /// </summary>
    internal static AppHostImportDependencies CollectImportDependencies(FileInfo projectFile)
    {
        var builder = new ImportDependencyBuilder();

        // Mirror IsLikelyAppHost exactly: it consults the project file's own walk-up imports (step 1b) and
        // then the NEAREST Directory.Build.props / Directory.Build.targets chain (step 2). Files outside
        // those reachable chains are shadowed — MSBuild never evaluates them — so an unresolvable import
        // sitting in one must not disable caching for this project.
        if (TryLoadProjectRoot(projectFile.FullName, out var projectRoot) && projectRoot is not null)
        {
            CollectFileImportDependencies(projectRoot, projectFile.Directory, DirectoryBuildPropsName, builder, staticImportsAdmitProject: false);
        }

        CollectDirectoryBuildChainDependencies(projectFile.Directory, DirectoryBuildPropsName, builder);
        CollectDirectoryBuildChainDependencies(projectFile.Directory, DirectoryBuildTargetsName, builder);

        return builder.Build();
    }

    private static void CollectDirectoryBuildChainDependencies(DirectoryInfo? directory, string fileName, ImportDependencyBuilder builder)
    {
        // Replay NearestDirectoryBuildFileChainContainsMarker's traversal for one conventional file name and
        // record what each visited file lets the classifier accept. Anything the walk does not visit cannot
        // have contributed to the verdict, so it contributes no cache dependency either.
        for (var current = directory; current is not null; current = current.Parent)
        {
            var filePath = Path.Combine(current.FullName, fileName);
            if (!File.Exists(filePath))
            {
                continue;
            }

            if (!TryLoadProjectRoot(filePath, out var root) || root is null || ContainsAppHostMarker(root))
            {
                // The verdict was decided by this file, whose own mtime the conventional walk already
                // fingerprints. Nothing further up is imported for this name.
                return;
            }

            CollectFileImportDependencies(root, current, fileName, builder, staticImportsAdmitProject: true);

            switch (ClassifyDirectoryBuildChaining(root, fileName))
            {
                case DirectoryBuildChaining.ChainsToParent:
                    continue;
                case DirectoryBuildChaining.Uncertain:
                case DirectoryBuildChaining.StopsHere:
                default:
                    return;
            }
        }
    }

    private static void CollectFileImportDependencies(
        XElement root,
        DirectoryInfo? containingDirectory,
        string chainFileName,
        ImportDependencyBuilder builder,
        bool staticImportsAdmitProject)
    {
        foreach (var import in root.Descendants().Where(e => e.Name.LocalName.Equals("Import", StringComparison.Ordinal)))
        {
            var projectAttributeValue = import.Attribute("Project")?.Value;
            if (projectAttributeValue is null)
            {
                continue;
            }

            foreach (var project in SplitImportProjectEntries(projectAttributeValue))
            {
                if (WalkUpFunctionCallStartRegex().IsMatch(project))
                {
                    RecordWalkUpImportDependency(project, builder);
                    continue;
                }

                if (!staticImportsAdmitProject)
                {
                    // Only ContainsDynamicWalkUpImport runs over the .csproj itself, so a static import there
                    // never admits the project and cannot make the prefilter's answer depend on that file.
                    continue;
                }

                var classification = ClassifyStaticImportChaining(project, chainFileName);
                if (classification.Chaining is not DirectoryBuildChaining.Uncertain)
                {
                    // ChainsToParent lands on the next file this walk visits by name, which the conventional
                    // fingerprint already stats; StopsHere means the import never contributed to the verdict.
                    continue;
                }

                if (classification.UncertainTargetPath is not { } targetPath
                    || containingDirectory is null
                    || !TryResolveImportPath(containingDirectory.FullName, targetPath, out var resolvedPath))
                {
                    builder.MarkUnfingerprintable();
                    continue;
                }

                builder.AddExactPath(resolvedPath);
            }
        }
    }

    private static void RecordWalkUpImportDependency(string projectAttributeValue, ImportDependencyBuilder builder)
    {
        if (!TryGetWalkUpImport(projectAttributeValue, out var walkUpImport))
        {
            builder.MarkUnfingerprintable();
            return;
        }

        if (IsAlreadyEnumeratedDirectoryBuildChain(walkUpImport))
        {
            // Conventional Directory.Build.* chaining resolves to files the cache already stats at every
            // ancestor level.
            return;
        }

        if (walkUpImport.AnchorFileName.Length == 0
            || ContainsWildcard(walkUpImport.AnchorFileName)
            || !IsAncestorChainSearchStart(walkUpImport.StartDirectoryArg))
        {
            // The searched file name is an MSBuild expression or a glob, or the search starts outside this
            // project's ancestor chain (e.g. '$(ExternalBuildRoot)'). Either way the resolved file is not
            // reachable by the ancestor walk that produces the fingerprint.
            builder.MarkUnfingerprintable();
            return;
        }

        // The anchor is tracked even when it is not the imported file: it is what selects WHICH ancestor
        // directory the import resolves in, so creating or deleting an anchor changes the resolved import.
        builder.AddAncestorSearchName(walkUpImport.AnchorFileName);

        if (!walkUpImport.HasAppendedPath)
        {
            return;
        }

        if (!TryGetAppendedImportFileName(walkUpImport, out var appendedFileName))
        {
            builder.MarkUnfingerprintable();
            return;
        }

        builder.AddAncestorSearchName(appendedFileName);
    }

    private static bool TryGetAppendedImportFileName(in WalkUpImport import, out string fileName)
    {
        // Work out the single file name an appended suffix produces, which differs per helper because the
        // two return different things. Raw values and the file MSBuild ends up importing:
        //
        //   $([MSBuild]::GetPathOfFileAbove('Aspire.Common', '$(MSBuildThisFileDirectory)../')).props
        //     -> GetPathOfFileAbove returns "<dir>/Aspire.Common", so the text concatenates onto the FILE
        //        NAME and the import is "<dir>/Aspire.Common.props".
        //   $([MSBuild]::GetDirectoryNameOfFileAbove('$(MSBuildThisFileDirectory)../', 'Repo.marker'))/Custom.props
        //     -> GetDirectoryNameOfFileAbove returns "<dir>" with no trailing separator, so the suffix must
        //        start with one and the import is "<dir>/Custom.props".
        //
        // Anything else — an expression, a glob, a nested sub-directory, or a suffix whose separator usage
        // does not match the helper (which would treat a file as a directory, or splice a directory name and
        // a file name together) — cannot be reduced to one name that statting ancestor directories covers.
        // Docs: https://learn.microsoft.com/visualstudio/msbuild/property-functions#msbuild-property-functions
        fileName = string.Empty;
        var appended = import.AppendedPath;
        if (appended.Length == 0 || appended.Contains('$') || appended.Contains('@') || ContainsWildcard(appended))
        {
            return false;
        }

        if (import.IsGetPathOfFileAbove)
        {
            if (appended.AsSpan().IndexOfAny('/', '\\') >= 0)
            {
                return false;
            }

            fileName = import.AnchorFileName + appended;
            return true;
        }

        if (appended[0] is not ('/' or '\\'))
        {
            return false;
        }

        var remainder = appended[1..];
        if (remainder.Length == 0 || remainder.AsSpan().IndexOfAny('/', '\\') >= 0)
        {
            return false;
        }

        fileName = remainder;
        return true;
    }

    private static bool TryResolveImportPath(string containingDirectory, string importPath, out string fullPath)
    {
        try
        {
            // MSBuild accepts '\' as a separator on every platform and normalizes it, but Path.GetFullPath
            // only does so on Windows — so "..\shared\Directory.Build.props" would collapse into a single
            // bogus segment on macOS/Linux. Normalize first so both spellings resolve identically.
            var normalized = importPath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            fullPath = Path.GetFullPath(Path.Combine(containingDirectory, normalized));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException or IOException)
        {
            fullPath = string.Empty;
            return false;
        }
    }

    private sealed class ImportDependencyBuilder
    {
        private readonly SortedSet<string> _ancestorSearchFileNames = new(StringComparer.Ordinal);
        private readonly SortedSet<string> _exactFilePaths = new(StringComparer.Ordinal);
        private bool _hasUnfingerprintableImport;

        public void AddAncestorSearchName(string fileName) => _ancestorSearchFileNames.Add(fileName);

        public void AddExactPath(string fullPath) => _exactFilePaths.Add(fullPath);

        public void MarkUnfingerprintable() => _hasUnfingerprintableImport = true;

        public AppHostImportDependencies Build()
            => new(_ancestorSearchFileNames, _exactFilePaths, _hasUnfingerprintableImport);
    }

    private static bool IsAncestorChainSearchStart(string? startDirectoryArg)
    {
        // Whether a walk-up search is guaranteed to land on a directory the ancestor walk enumerates. Unlike
        // IsImmediateParentChainStart this accepts ANY number of '..' levels, because every one of them is
        // still an ancestor of the importing file (and therefore of the project). Raw forms:
        //   null / ''                              -> defaults to $(MSBuildThisFileDirectory): in chain
        //   '$(MSBuildThisFileDirectory)../'       -> in chain
        //   '$(MSBuildThisFileDirectory)../../'    -> in chain
        //   '$(MSBuildThisFileDirectory)../peer/'  -> NOT in chain (a named segment leaves the ancestor path)
        //   '$(ExternalBuildRoot)' / '/abs/path'   -> NOT in chain
        if (startDirectoryArg is null)
        {
            return true;
        }

        var start = StripQuotes(startDirectoryArg).Trim();
        if (start.Length == 0)
        {
            return true;
        }

        const string thisFileDirectory = "$(MSBuildThisFileDirectory)";
        if (!start.StartsWith(thisFileDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var rawSegment in start[thisFileDirectory.Length..].Split('/', '\\'))
        {
            switch (rawSegment.Trim())
            {
                case "" or "." or "..":
                    continue;
                default:
                    return false;
            }
        }

        return true;
    }

    private static bool ContainsDynamicWalkUpImport(XElement root)
    {
        // Match <Import Project="..."> values that use MSBuild's tree-walking path helpers:
        //   $([MSBuild]::GetPathOfFileAbove('<filename>', '<starting-dir>'))
        //   $([MSBuild]::GetDirectoryNameOfFileAbove('<starting-dir>', '<filename>'))
        // These resolve at evaluation time by walking parent directories, and they routinely point at
        // files (RepoTesting.props, custom shared targets) that we do not enumerate by name in the
        // ancestor walk. A statically-named Import like <Import Project="NullablePolyfill.targets" />
        // does NOT match — it points at a fixed file the project author already named, and treating
        // those as uncertain would over-promote every project in a repo whose root Directory.Build.*
        // imports Arcade or common polyfills. The Sdk attribute is intentionally not consulted here:
        // <Import Sdk="Aspire.AppHost.Sdk" .../> is already recognized as a positive marker by
        // ContainsAppHostMarker, and any other <Import Sdk="..."> brings in an unrelated SDK whose
        // contents will not declare Aspire markers.
        //
        // Match case-insensitively because MSBuild property function names are themselves
        // case-insensitive — `$([MSBuild]::getpathoffileabove(...))` and
        // `$([MSBuild]::GetPathOfFileAbove(...))` resolve to the same path at evaluation time, so a
        // case-sensitive substring check here would silently filter out the lower/mixed-case variants
        // and re-open the false-negative window this fallback is meant to close.
        //
        // Do not apply the conventional Directory.Build.* chaining shortcut here. That shortcut is sound
        // only while following a Directory.Build.* file: a project-file import can start at its parent,
        // bypass a nearer marker-less auto-import, and explicitly reach an outer marker-bearing file.
        // Docs: https://learn.microsoft.com/visualstudio/msbuild/property-functions#msbuild-property-functions
        foreach (var import in root.Descendants().Where(e => e.Name.LocalName.Equals("Import", StringComparison.Ordinal)))
        {
            var projectAttributeValue = import.Attribute("Project")?.Value;
            if (projectAttributeValue is null)
            {
                continue;
            }

            foreach (var project in SplitImportProjectEntries(projectAttributeValue))
            {
                // Match the function-call *shape* (name followed by `(`) rather than a raw substring — a static
                // import like <Import Project="build/GetPathOfFileAbove.props" /> contains the helper name as
                // path text but is not a function call, and treating it as uncertain would over-promote ordinary
                // projects.
                if (!WalkUpFunctionCallStartRegex().IsMatch(project))
                {
                    continue;
                }

                // Either a walk-up we cannot resolve statically (the arguments are themselves MSBuild
                // expressions, the call is malformed) or one that lands somewhere the ancestor walk does not
                // enumerate. Be conservative and treat the project as a candidate.
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// One parsed <c>$([MSBuild]::GetPathOfFileAbove(...))</c> / <c>$([MSBuild]::GetDirectoryNameOfFileAbove(...))</c>
    /// import, split into the pieces the classifier and the cache fingerprint need.
    /// </summary>
    /// <param name="IsGetPathOfFileAbove">
    /// <see langword="true"/> for the file-returning helper, <see langword="false"/> for the
    /// directory-returning one. The two differ in which argument is the anchor and in whether the call alone
    /// identifies an imported file.
    /// </param>
    /// <param name="AnchorFileName">
    /// The file name the walk-up searches for — argument 1 of <c>GetPathOfFileAbove(file, start)</c> and
    /// argument 2 of <c>GetDirectoryNameOfFileAbove(start, file)</c>. Empty when it is not statically
    /// determinable (for example <c>GetPathOfFileAbove($(SharedPropsName))</c>).
    /// </param>
    /// <param name="StartDirectoryArg">
    /// The directory the search starts from, or <see langword="null"/> when omitted — in which case
    /// <c>GetPathOfFileAbove</c> defaults to <c>$(MSBuildThisFileDirectory)</c>.
    /// </param>
    /// <param name="AppendedPath">
    /// Raw path text concatenated after the call, or empty when there is none. It is kept verbatim rather
    /// than reduced to a file name because how it combines with the call result depends on the helper — see
    /// <see cref="TryGetAppendedImportFileName"/>.
    /// </param>
    private readonly record struct WalkUpImport(
        bool IsGetPathOfFileAbove,
        string AnchorFileName,
        string? StartDirectoryArg,
        string AppendedPath)
    {
        public bool HasAppendedPath => AppendedPath.Length > 0;
    }

    private static bool TryGetWalkUpImport(string projectAttributeValue, out WalkUpImport import)
    {
        // Parse one MSBuild property-function walk-up call out of the Import Project value. Raw forms:
        //   $([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))
        //   $([MSBuild]::GetPathOfFileAbove('Aspire.Common.props'))
        //   $([MSBuild]::GetDirectoryNameOfFileAbove('$(MSBuildThisFileDirectory)..', 'Directory.Build.props'))/Custom.props
        //   $([MSBuild]::getpathoffileabove(Directory.Packages.props, $(MSBuildThisFileDirectory)..))
        // Arguments may be single-quoted, double-quoted, or unquoted, and the helper names are matched
        // case-insensitively because MSBuild property function names are case-insensitive.
        //
        // The imported file is NOT always the anchor argument: when path text follows the call, that
        // appended text names the imported file and the anchor only selects the directory. Callers need both
        // parts, so they are reported separately instead of being collapsed into a single "target".
        //
        // Returns false when no walk-up call is recognized or when the call is malformed (unbalanced
        // parentheses); callers treat that as "uncertain".
        import = default;
        var callMatch = WalkUpFunctionCallStartRegex().Match(projectAttributeValue);
        if (!callMatch.Success || projectAttributeValue[..callMatch.Index].Trim().Length > 0)
        {
            return false;
        }

        var isGetPathOfFileAbove = callMatch.Groups[1].Value.Equals("GetPathOfFileAbove", StringComparison.OrdinalIgnoreCase);
        var argsStart = callMatch.Index + callMatch.Length;

        if (!TryParseFunctionCallArgs(projectAttributeValue, argsStart, out var args, out var afterCloseParen))
        {
            return false;
        }

        // The anchor file name and the starting directory sit in opposite argument slots for the two helpers.
        var anchorArg = isGetPathOfFileAbove
            ? (args.Count >= 1 ? args[0] : null)
            : (args.Count >= 2 ? args[1] : null);
        var startDirectoryArg = isGetPathOfFileAbove
            ? (args.Count >= 2 ? args[1] : null)
            : (args.Count >= 1 ? args[0] : null);

        var anchorFileName = anchorArg is null ? string.Empty : StripQuotes(anchorArg).Trim();
        if (anchorFileName.Contains('$') || anchorFileName.Contains('@'))
        {
            // The anchor is itself an MSBuild expression; only MSBuild knows which file is searched for.
            anchorFileName = string.Empty;
        }

        // The supported fingerprintable shape is a standalone $([MSBuild]::...) expression with an
        // optional literal suffix. Prefixes or enclosing property functions can change the imported path
        // in ways this parser cannot model, so callers must treat them as unfingerprintable.
        var suffixStart = afterCloseParen;
        while (suffixStart < projectAttributeValue.Length && char.IsWhiteSpace(projectAttributeValue[suffixStart]))
        {
            suffixStart++;
        }
        if (suffixStart >= projectAttributeValue.Length || projectAttributeValue[suffixStart] != ')')
        {
            return false;
        }
        suffixStart++;

        var appendedPath = suffixStart < projectAttributeValue.Length
            ? projectAttributeValue[suffixStart..].Trim()
            : string.Empty;
        if (appendedPath.Contains('(') || appendedPath.Contains(')'))
        {
            return false;
        }

        import = new WalkUpImport(
            isGetPathOfFileAbove,
            anchorFileName,
            startDirectoryArg,
            appendedPath);
        return true;
    }

    private static bool TryParseFunctionCallArgs(string text, int startIndex, out List<string> args, out int afterCloseParen)
    {
        // Hand-rolled comma-splitter with balanced-paren and quote awareness. MSBuild property-
        // function arguments can be:
        //   * Single-quoted strings ('like this')
        //   * Double-quoted strings ("like this")
        //   * Unquoted scalars (Directory.Build.props)
        //   * MSBuild property references that contain parens, e.g. $(MSBuildThisFileDirectory)..
        //   * Other nested function calls
        // We need to find the top-level commas separating arguments and the `)` that closes the
        // call we started inside. Regex with `[^,)]` would prematurely terminate on the `)` inside
        // a $(...) reference; a tiny state machine handles this correctly.
        args = new List<string>();
        var current = new StringBuilder();
        var depth = 1;
        var inSingleQuote = false;
        var inDoubleQuote = false;

        for (var i = startIndex; i < text.Length; i++)
        {
            var c = text[i];

            if (inSingleQuote)
            {
                if (c == '\'')
                {
                    inSingleQuote = false;
                }
                current.Append(c);
                continue;
            }
            if (inDoubleQuote)
            {
                if (c == '"')
                {
                    inDoubleQuote = false;
                }
                current.Append(c);
                continue;
            }

            switch (c)
            {
                case '\'':
                    inSingleQuote = true;
                    current.Append(c);
                    break;
                case '"':
                    inDoubleQuote = true;
                    current.Append(c);
                    break;
                case '(':
                    depth++;
                    current.Append(c);
                    break;
                case ')':
                    depth--;
                    if (depth == 0)
                    {
                        args.Add(current.ToString().Trim());
                        afterCloseParen = i + 1;
                        return true;
                    }
                    current.Append(c);
                    break;
                case ',' when depth == 1:
                    args.Add(current.ToString().Trim());
                    current.Clear();
                    break;
                default:
                    current.Append(c);
                    break;
            }
        }

        afterCloseParen = text.Length;
        return false;
    }

    private static string StripQuotes(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2
            && ((trimmed[0] == '\'' && trimmed[^1] == '\'')
                || (trimmed[0] == '"' && trimmed[^1] == '"')))
        {
            return trimmed[1..^1];
        }
        return trimmed;
    }

    private static bool IsConventionalDirectoryBuildFileName(string fileName)
    {
        // Only skip dynamic walk-up imports for the exact file names the ancestor walk probes.
        // On case-sensitive filesystems, differently-cased names can resolve to different files;
        // keep those candidates flowing to MSBuild instead of assuming we already inspected them.
        return fileName.Equals(DirectoryBuildPropsName, StringComparison.Ordinal)
            || fileName.Equals(DirectoryBuildTargetsName, StringComparison.Ordinal);
    }

    private static bool CanMatchConventionalDirectoryBuildFileName(string fileName)
        => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(fileName, DirectoryBuildPropsName, ignoreCase: true)
            || System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(fileName, DirectoryBuildTargetsName, ignoreCase: true);

    // Strict MSBuild property-function call shape: the full $([MSBuild]::Function( prefix. Static
    // import paths that merely contain the helper name as text (e.g.
    // "build/GetPathOfFileAbove('Shared.props').props") do NOT match because they lack the
    // $([MSBuild]::...) wrapper that MSBuild requires for property-function invocation.
    // Docs: https://learn.microsoft.com/visualstudio/msbuild/property-functions#calling-static-methods
    [GeneratedRegex(
        @"\$\(\s*\[MSBuild\]\s*::\s*(GetPathOfFileAbove|GetDirectoryNameOfFileAbove)\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WalkUpFunctionCallStartRegex();

    private static bool ContainsAppHostMarker(XElement root)
    {
        // Only MSBuild project files declare Aspire markers, so ignore any other well-formed XML whose root
        // is not <Project>. Compare on Name.LocalName so projects using the legacy MSBuild XML namespace
        // (xmlns="http://schemas.microsoft.com/developer/msbuild/2003") are still recognized.
        if (!root.Name.LocalName.Equals("Project", StringComparison.Ordinal))
        {
            return false;
        }

        // 1) SDK-style reference declared via the Sdk attribute on <Project>, which may list multiple
        //    SDKs with optional versions, e.g.:
        //      <Project Sdk="Microsoft.NET.Sdk;Aspire.AppHost.Sdk/9.0.0">
        var sdkAttribute = root.Attribute("Sdk")?.Value;
        if (sdkAttribute is not null && ContainsAspireAppHostSdk(sdkAttribute))
        {
            return true;
        }

        // The remaining checks compare on Name.LocalName so that projects declaring the legacy MSBuild
        // XML namespace (xmlns="http://schemas.microsoft.com/developer/msbuild/2003") are matched the
        // same as SDK-style projects that omit it.

        // 2) Nested SDK reference element, e.g.:
        //      <Sdk Name="Aspire.AppHost.Sdk" Version="9.0.0" />
        var hasSdkElement = root.Descendants()
            .Any(e => e.Name.LocalName.Equals("Sdk", StringComparison.Ordinal)
                && string.Equals(e.Attribute("Name")?.Value, AspireAppHostSdkName, StringComparison.OrdinalIgnoreCase));
        if (hasSdkElement)
        {
            return true;
        }

        // 3) <Import> form of an SDK reference, e.g.:
        //      <Import Project="Sdk.props" Sdk="Aspire.AppHost.Sdk" Version="9.0.0" />
        //      <Import Project="Sdk.targets" Sdk="Aspire.AppHost.Sdk" />
        //    This is functionally equivalent to the previous two forms — it lets a project import an SDK's
        //    Sdk.props/Sdk.targets at a specific point in the file. Missing it means an AppHost using this
        //    form is silently rejected by the cheap pre-check. See:
        //    https://learn.microsoft.com/visualstudio/msbuild/how-to-use-project-sdk#import-an-sdk-into-your-project
        var hasImportSdk = root.Descendants()
            .Any(e => e.Name.LocalName.Equals("Import", StringComparison.Ordinal)
                && string.Equals(e.Attribute("Sdk")?.Value, AspireAppHostSdkName, StringComparison.OrdinalIgnoreCase));
        if (hasImportSdk)
        {
            return true;
        }

        // 4) Explicit <IsAspireHost>true</IsAspireHost> property element. The Aspire.AppHost.Sdk sets this
        //    during evaluation, but it can also appear literally in a project or build file. Matching on the
        //    element (rather than a substring) means a consumer condition such as
        //    Condition="'$(IsAspireHost)' == 'true'" is correctly not treated as a marker.
        //
        //    Match the element name case-insensitively because MSBuild property names are themselves
        //    case-insensitive — `<isaspirehost>true</isaspirehost>` sets `$(IsAspireHost)` to `true` at
        //    evaluation time just as the PascalCase form does, so a case-sensitive comparison here would
        //    silently reject a real AppHost using lower- or mixed-case marker syntax.
        //    Docs: https://learn.microsoft.com/visualstudio/msbuild/msbuild-properties
        return root.Descendants()
            .Any(e => e.Name.LocalName.Equals(IsAspireHostProperty, StringComparison.OrdinalIgnoreCase)
                && string.Equals(e.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesAppHostNameHeuristics(FileInfo projectFile)
    {
        // Convention 1: the project file is named like an AppHost, e.g. "MyApp.AppHost.csproj" or
        // "AppHost.csproj". Compare on the name without extension so both "Foo.AppHost" and "AppHost" match.
        if (Path.GetFileNameWithoutExtension(projectFile.Name).EndsWith("AppHost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Convention 2: a sibling AppHost.cs source file lives next to the project. Project-based AppHosts
        // created by the templates (e.g. `aspire new`) ship an AppHost.cs (PascalCase) with the builder
        // entrypoint, so its presence is a strong signal even when the csproj carries no inline marker.
        // (The lowercase apphost.cs is the separate single-file AppHost convention, which has no csproj.)
        //
        // Match the file name case-insensitively by enumerating the directory rather than calling
        // File.Exists with a fixed-case name: File.Exists is case-sensitive on Linux/macOS and would miss
        // the PascalCase AppHost.cs there. This preserves the behavior of the previous discovery heuristic.
        var directory = projectFile.Directory;
        return directory is not null
            && directory.EnumerateFiles("*.cs", SearchOption.TopDirectoryOnly)
                .Any(file => file.Name.Equals(ProjectAppHostSourceFileName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsAspireAppHostSdk(string sdkAttribute)
    {
        // SDK references resolve through NuGet, whose package IDs are case-insensitive, so a project could
        // legitimately write the SDK name in any casing (e.g. "aspire.apphost.sdk") and still build as an
        // AppHost. Match case-insensitively so this cheap pre-check agrees with MSBuild rather than wrongly
        // skipping a real AppHost over a casing difference.
        var sdks = sdkAttribute.Split(';');
        foreach (var sdk in sdks)
        {
            var trimmedSdk = sdk.Trim();

            if (trimmedSdk.Equals(AspireAppHostSdkName, StringComparison.OrdinalIgnoreCase) ||
                trimmedSdk.StartsWith(AspireAppHostSdkName + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // ═══════════════════════════════════════════════════════════════
    // CREATION
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public string? AppHostFileName => "apphost.cs";

    /// <inheritdoc />
    public bool IsUsingProjectReferences(FileInfo appHostFile)
    {
        return false;
    }

    // ═══════════════════════════════════════════════════════════════
    // EXECUTION
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<AppHostValidationResult> ValidateAppHostAsync(FileInfo appHostFile, CancellationToken cancellationToken)
    {
        if (IsUnsupported)
        {
            return new AppHostValidationResult(IsValid: false, IsUnsupported: true);
        }

        var isSingleFile = appHostFile.Extension.Equals(".cs", StringComparison.OrdinalIgnoreCase);

        if (isSingleFile)
        {
            // For single-file apphosts, validate that:
            // 1. No sibling .csproj files exist (otherwise it's part of a project)
            // 2. The file contains the #:sdk Aspire.AppHost.Sdk directive
            return new AppHostValidationResult(IsValid: IsValidSingleFileAppHost(appHostFile));
        }

        // Fast path that mitigates the MSBuild "evaluation storm": cheaply reject project-file
        // candidates that are not likely AppHosts before paying for MSBuild evaluation below.
        if (!IsLikelyAppHost(appHostFile))
        {
            return new AppHostValidationResult(IsValid: false);
        }

        // The resolver owns the cache/MSBuild fallback so validation and later run/publish
        // decisions share a single source of truth for AppHost project metadata.
        var information = await _appHostInfoResolver.GetAppHostInfoAsync(appHostFile, cancellationToken);

        if (information.ExitCode == 0 && information.IsAspireHost)
        {
            return new AppHostValidationResult(IsValid: true, AspireHostingVersion: information.AspireHostingVersion);
        }

        // MSBuild evaluated the project cleanly (exit code 0) but it is not an Aspire host. That is an
        // authoritative "no": for example a Microsoft.NET.Sdk.Web project that merely sits next to an
        // apphost.cs and so passed the name heuristic above. Reject it quietly rather than surfacing a
        // spurious possibly-unbuildable warning for a project that evaluates fine and simply isn't an AppHost.
        if (information.ExitCode == 0)
        {
            return new AppHostValidationResult(IsValid: false);
        }

        // MSBuild failed to evaluate the project (non-zero exit). The cheap classifier judged it a likely
        // AppHost (an inline/co-located marker or the name heuristic), so surface it as a possibly-unbuildable
        // AppHost (kept as a candidate with a warning) rather than silently discarding what may be a real
        // AppHost that currently fails to build.
        return new AppHostValidationResult(
            IsValid: false,
            IsPossiblyUnbuildable: true);
    }

    /// <inheritdoc />
    public async Task<string?> GetAspireHostingVersionAsync(FileInfo appHostFile, CancellationToken cancellationToken)
    {
        // Use the same MSBuild-based inspection as validation so version resolution
        // follows the project model that run/publish already rely on, including
        // SDK-style projects, package references, and Central Package Management.
        var information = await _appHostInfoResolver.GetAppHostInfoAsync(appHostFile, cancellationToken);
        return information.ExitCode == 0 && information.IsAspireHost
            ? information.AspireHostingVersion
            : null;
    }

    /// <inheritdoc />
    public async Task<int> RunAsync(AppHostProjectContext context, CancellationToken cancellationToken)
    {
        // .NET projects require the SDK to be installed
        if (!await SdkInstallHelper.EnsureSdkInstalledAsync(_sdkInstaller, _interactionService, _telemetry, cancellationToken: cancellationToken))
        {
            // Signal build failure so RunCommand doesn't wait forever
            context.BuildCompletionSource?.TrySetResult(false);
            return CliExitCodes.SdkNotInstalled;
        }

        var effectiveAppHostFile = context.AppHostFile;
        var isExtensionHost = ExtensionHelper.IsExtensionHost(_interactionService, out _, out var extensionBackchannel);

        var buildOutputCollector = new OutputCollector(_fileLoggerProvider, CliLogFormat.Categories.Build);

        using var activity = _profilingTelemetry.StartAppHostRun();

        var isSingleFileAppHost = !IsProjectFile(effectiveAppHostFile);

        var env = new Dictionary<string, string>(context.EnvironmentVariables);

        // Handle isolated mode - randomize ports and isolate user secrets
        string? isolatedUserSecretsId = null;
        if (context.Isolated)
        {
            using var isolatedModeActivity = _profilingTelemetry.StartAppHostConfigureIsolatedMode();
            try
            {
                isolatedUserSecretsId = await ConfigureIsolatedModeAsync(effectiveAppHostFile, env, cancellationToken);
                _logger.LogInformation("Aspire run isolated. Isolated UserSecretsId: {IsolatedUserSecretsId}", isolatedUserSecretsId);
            }
            catch (Exception ex)
            {
                isolatedModeActivity.SetError(ex.Message);
                throw;
            }
        }

        // Enable debug logging in the app host so that debug-level output is
        // captured in the CLI log file for diagnostics. Defaults to Debug but
        // can be overridden via --log-level.
        var aspireLogLevel = _loggingOptions.ConsoleLogLevel ?? LogLevel.Debug;
        env[KnownConfigNames.AspireLogLevel] = aspireLogLevel.ToString();

        if (context.WaitForDebugger)
        {
            env[KnownConfigNames.WaitForDebugger] = "true";
        }

        await EnsureDevCertificatesTrustedAsync(context, env, cancellationToken);

        var cliBundleLease = await AcquireCliBundleLayoutAsync(cancellationToken);
        using var cliBundleLeaseScope = cliBundleLease;
        ConfigureCliBundleEnvironment(env, cliBundleLease, injectDcpAndDashboard: false);

        var watch = !isSingleFileAppHost && _features.IsFeatureEnabled(KnownFeatures.DefaultWatchEnabled, defaultValue: false);
        var preparationExitCode = await PrepareAppHostAsync(
            context,
            effectiveAppHostFile,
            isSingleFileAppHost,
            isExtensionHost,
            extensionBackchannel,
            buildOutputCollector,
            cancellationToken);
        if (preparationExitCode is { } exitCode)
        {
            return exitCode;
        }

        // Two separate bundle interactions:
        //  - injectDcpAndDashboard: only true when the AppHost opted into AspireUseCliBundle.
        //    Those env vars would clobber the per-RID NuGet metadata path otherwise.
        //  - terminal host env vars: always injected when the bundle is available, because
        //    no per-RID NuGet ships the terminal host today. Skipping ResolveAspireCliBundle
        //    is fine for non-CliBundle AppHosts that don't use WithTerminal() — the lease
        //    is best-effort and a missing layout just means no terminal host env vars.
        var canQueryCliBundleProperty = !isSingleFileAppHost || !context.NoBuild;
        var appHostInfo = canQueryCliBundleProperty
            ? await _appHostInfoResolver.GetAppHostInfoAsync(effectiveAppHostFile, cancellationToken)
            : null;
        var injectDcpAndDashboard = appHostInfo?.IsUsingCliBundle == true;
        ConfigureCliBundleEnvironment(env, cliBundleLease, injectDcpAndDashboard);

        // RunCommand may display captured AppHost output as soon as BuildCompletionSource is signaled.
        // Store the collector first so failures that occur immediately after preparation are not lost
        // to a race between the AppHost process and RunCommand's UX path.
        var runOutputCollector = new OutputCollector(_fileLoggerProvider, CliLogFormat.Categories.AppHost);
        context.OutputCollector = runOutputCollector;

        // Signal that build/preparation is complete
        context.BuildCompletionSource?.TrySetResult(true);
        activity.AddAppHostBuildReadyEvent();

        var runOptions = new ProcessInvocationOptions
        {
            StandardOutputCallback = runOutputCollector.AppendOutput,
            StandardErrorCallback = runOutputCollector.AppendError,
            StartDebugSession = context.StartDebugSession,
            Debug = context.Debug,
            KillEntireProcessTreeOnCancel = ShouldKillEntireProcessTreeOnCancel(_environment.IsWindows()),
            // Run path opts into the shared shutdown ladder so pure .NET AppHosts get the
            // same graceful-then-tree-kill semantics as TypeScript AppHosts (which already
            // route through AppHostServerSession/ProcessGuestLauncher). Build, restore,
            // package add, layout, and other short-lived invocations leave these unset so
            // they continue to use the shared ladder's force-kill mode.
            IsolateConsole = true,
            KillOnParentExit = true,
            GracefulShutdownSignaler = _gracefulShutdownSignaler,
            ShutdownService = _shutdownService,
            LaunchProfile = context.LaunchProfile,
        };

        // The backchannel completion source is the contract with RunCommand
        // We signal this when the backchannel is ready, RunCommand uses it for UX
        var backchannelCompletionSource = context.BackchannelCompletionSource ?? new TaskCompletionSource<IAppHostCliBackchannel>();

        if (isSingleFileAppHost)
        {
            ConfigureSingleFileRunEnvironment(effectiveAppHostFile, env, args: context.UnmatchedTokens);
        }

        env[KnownConfigNames.DcpWorkloadId] = AppHostWorkloadId.Create(effectiveAppHostFile);

        var directRun = !isSingleFileAppHost && !watch && !isExtensionHost
            ? await TryCreateDirectRunSpecAsync(effectiveAppHostFile, env, context.UnmatchedTokens, runOptions.NoLaunchProfile, runOptions.LaunchProfile, cancellationToken)
            : null;

        // Start the apphost - the runner will signal the backchannel when ready
        try
        {
            // The AppHost may already have been built above, but watch mode intentionally still
            // runs with builds enabled. Passing --no-build through to dotnet watch breaks hot reload
            // because watch owns the incremental build loop and its environment setup.
            //
            // This means watch mode can do a second no-op build after the CLI pre-build succeeds.
            // That tradeoff is intentional: the pre-build makes initial compiler errors terminate
            // aspire run instead of leaving dotnet watch idle waiting for edits before a backchannel
            // ever becomes available.
            //
            // noRestore is only relevant when noBuild is false because --no-build implies --no-restore.
            var noBuild = !watch || context.NoBuild;
            using var runDotnetActivity = _profilingTelemetry.StartAppHostRunDotnetLifetime(watch, noBuild, context.NoRestore);
            if (directRun is not null)
            {
                // The direct command line has no "--" separator, so the forwarded-argument boundary
                // has to be carried alongside it for logging. Clone rather than mutate because the
                // caller may reuse runOptions for other invocations.
                var directRunOptions = runOptions.Clone();
                directRunOptions.AppHostArgumentStartIndex = directRun.AppHostArgumentStartIndex;

                return await _runner.RunAppHostCommandAsync(
                    effectiveAppHostFile,
                    directRun.Command,
                    directRun.WorkingDirectory,
                    directRun.Arguments,
                    directRun.Environment,
                    backchannelCompletionSource,
                    directRunOptions,
                    cancellationToken);
            }

            return await _runner.RunAsync(
                effectiveAppHostFile,
                watch,
                noBuild,
                context.NoRestore,
                context.UnmatchedTokens,
                env,
                backchannelCompletionSource,
                runOptions,
                cancellationToken);
        }
        finally
        {
            // Clean up isolated user secrets when the run completes
            if (!string.IsNullOrEmpty(isolatedUserSecretsId))
            {
                IsolatedUserSecretsHelper.CleanupIsolatedUserSecrets(isolatedUserSecretsId);
            }
        }
    }

    internal static bool ShouldKillEntireProcessTreeOnCancel(bool isWindows) => !isWindows;

    private async Task EnsureDevCertificatesTrustedAsync(AppHostProjectContext context, Dictionary<string, string> env, CancellationToken cancellationToken)
    {
        try
        {
            EnsureCertificatesTrustedResult certResult;
            using (var certActivity = _profilingTelemetry.StartAppHostEnsureDevCertificates())
            {
                certResult = await _certificateService.EnsureCertificatesTrustedAsync(cancellationToken);
                certActivity.SetDevCertificateEnvironmentVariables(certResult.EnvironmentVariables.Count);
            }

            // Certificate trust can add platform-specific variables such as SSL_CERT_DIR on Linux.
            // These must flow into the AppHost process because the dashboard/resource service may
            // start immediately after preparation and depend on the same trust roots the CLI just
            // verified.
            foreach (var kvp in certResult.EnvironmentVariables)
            {
                env[kvp.Key] = kvp.Value;
            }
        }
        catch
        {
            // RunCommand waits on this source before it waits for the AppHost backchannel. Any
            // exception during preparation must signal failure, otherwise the command can hang
            // forever on a backchannel that will never be created.
            context.BuildCompletionSource?.TrySetResult(false);
            throw;
        }
    }

    private async Task<int?> PrepareAppHostAsync(
        AppHostProjectContext context,
        FileInfo effectiveAppHostFile,
        bool isSingleFileAppHost,
        bool isExtensionHost,
        IExtensionBackchannel? extensionBackchannel,
        OutputCollector buildOutputCollector,
        CancellationToken cancellationToken)
    {
        try
        {
            var buildExitCode = await BuildAppHostIfNeededAsync(
                context,
                effectiveAppHostFile,
                isExtensionHost,
                extensionBackchannel,
                buildOutputCollector,
                cancellationToken);
            if (buildExitCode is not null)
            {
                return buildExitCode;
            }

            var compatibilityCheck = await CheckAppHostCompatibilityAsync(effectiveAppHostFile, isSingleFileAppHost, cancellationToken);
            if (!compatibilityCheck.IsCompatibleAppHost)
            {
                context.BuildCompletionSource?.TrySetResult(false);
                return CliExitCodes.FailedToDotnetRunAppHost;
            }

            return null;
        }
        catch
        {
            // RunCommand has already started awaiting preparation before the AppHost process exists.
            // Signal failure for both expected failures and exceptions so callers do not wait for
            // a backchannel that preparation prevented from starting.
            context.BuildCompletionSource?.TrySetResult(false);
            throw;
        }
    }

    private async Task<int?> BuildAppHostIfNeededAsync(
        AppHostProjectContext context,
        FileInfo effectiveAppHostFile,
        bool isExtensionHost,
        IExtensionBackchannel? extensionBackchannel,
        OutputCollector buildOutputCollector,
        CancellationToken cancellationToken)
    {
        if (context.NoBuild)
        {
            return null;
        }

        var extensionHasBuildCapability = extensionBackchannel is not null && await extensionBackchannel.HasCapabilityAsync(KnownCapabilities.BuildDotnetUsingCli, cancellationToken);
        if (isExtensionHost && !extensionHasBuildCapability)
        {
            // Older extension hosts own the AppHost build themselves. Building again in the CLI would
            // duplicate work and could race the extension's diagnostics/launch pipeline. Newer hosts
            // opt in with build-dotnet-using-cli when they want the CLI to own this pre-build.
            return null;
        }

        using var buildActivity = _profilingTelemetry.StartAppHostBuild(context.NoRestore, isExtensionHost, extensionHasBuildCapability);

        var buildOptions = new ProcessInvocationOptions
        {
            StandardOutputCallback = buildOutputCollector.AppendOutput,
            StandardErrorCallback = buildOutputCollector.AppendError,
        };

        var buildExitCode = await AppHostHelper.BuildAppHostAsync(_runner, _interactionService, effectiveAppHostFile, context.NoRestore, buildOptions, context.WorkingDirectory, cancellationToken);
        buildActivity.SetAppHostBuildExitCode(buildExitCode);

        if (buildExitCode == 0)
        {
            return null;
        }

        // Preserve the build output before signaling failure. RunCommand reads this collector after
        // BuildCompletionSource completes so users see the compiler diagnostics instead of only a
        // generic "project could not be built" message.
        context.OutputCollector = buildOutputCollector;
        context.BuildCompletionSource?.TrySetResult(false);
        return CliExitCodes.FailedToBuildArtifacts;
    }

    private async Task<(bool IsCompatibleAppHost, string? AspireHostingVersion)> CheckAppHostCompatibilityAsync(
        FileInfo effectiveAppHostFile,
        bool isSingleFileAppHost,
        CancellationToken cancellationToken)
    {
        if (isSingleFileAppHost)
        {
            // A single-file apphost pins its Aspire.Hosting version via the
            // `#:sdk Aspire.AppHost.Sdk@<version>` directive, which uses IdentitySdkVersion (the
            // identity version with build metadata stripped, matching the published NuGet package
            // version). Report that same value here so the compatibility check reflects what the
            // apphost actually pins, honoring ASPIRE_CLI_VERSION / sidecar overrides rather than
            // the physical assembly version.
            return (true, _executionContext.IdentitySdkVersion);
        }

        using var compatibilityActivity = _profilingTelemetry.StartAppHostCheckCompatibility();

        // Reuse the cached MSBuild result from ValidateAppHostAsync so we do not pay for a
        // second `dotnet msbuild -getProperty/-getItem` invocation just to gate compatibility.
        // Issue #17197: the legacy code path went runner → MSBuild for both validation and
        // the compatibility gate, doubling project inspection cost on every `aspire run`.
        var info = await _appHostInfoResolver.GetAppHostInfoAsync(effectiveAppHostFile, cancellationToken);
        var appHostCompatibilityCheck = AppHostHelper.EvaluateAppHostCompatibility(
            info.ExitCode,
            info.IsAspireHost,
            info.AspireHostingVersion,
            _interactionService,
            _fileLoggerProvider.LogFilePath);

        compatibilityActivity.SetAppHostCompatibility(
            appHostCompatibilityCheck.IsCompatibleAppHost,
            supportsBackchannel: appHostCompatibilityCheck.IsCompatibleAppHost,
            appHostCompatibilityCheck.AspireHostingVersion);

        return appHostCompatibilityCheck;
    }

    private async Task<DirectAppHostRunSpec?> TryCreateDirectRunSpecAsync(
        FileInfo effectiveAppHostFile,
        Dictionary<string, string> env,
        string[] unmatchedTokens,
        bool noLaunchProfile,
        string? launchProfile,
        CancellationToken cancellationToken)
    {
        if (await IsDirectLaunchDisabledAsync(effectiveAppHostFile, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogDebug("Falling back to dotnet run for {Project}; direct AppHost launch is disabled by configuration.", effectiveAppHostFile.FullName);
            return null;
        }

        // Direct launch intentionally uses the same cached AppHost inspection as validation. The
        // disk cache fingerprint includes the project file and conventional imported build files
        // (Directory.Build.*, Directory.Packages.*, global.json, and project.assets.json), so edits
        // that change AssemblyName/OutputPath/UseAppHost through those inputs force a fresh
        // ComputeRunArguments probe before RunCommand is used. If a project relies on custom
        // imports outside that tracked set, the cache can be disabled with
        // dotnetAppHostInfoCacheDisabled rather than paying an extra MSBuild evaluation on every run.
        var info = await _appHostInfoResolver.GetAppHostInfoAsync(effectiveAppHostFile, cancellationToken).ConfigureAwait(false);
        var arguments = ParseArguments(info.RunArguments);
        var hasRunArguments = arguments.Count > 0;

        if (!TryResolveDirectRunTarget(info, effectiveAppHostFile, arguments, out var command, out var workingDirectory))
        {
            return null;
        }

        var directEnv = new Dictionary<string, string>();
        if (!TryApplyProjectLaunchSettings(
                effectiveAppHostFile,
                directEnv,
                arguments,
                noLaunchProfile,
                launchProfile,
                hasExplicitApplicationArgs: unmatchedTokens.Length > 0,
                hasRunArguments))
        {
            return null;
        }

        foreach (var (name, value) in env)
        {
            directEnv[name] = value;
        }

        arguments.AddRange(unmatchedTokens);

        // Everything before this index came from MSBuild RunArguments or the launch profile; the
        // tail is user-supplied AppHost input that can carry connection strings and API keys.
        var appHostArgumentStartIndex = arguments.Count - unmatchedTokens.Length;

        _logger.LogDebug(
            "Launching AppHost directly via {Command} in {WorkingDirectory} with arguments {Arguments}.",
            command,
            workingDirectory.FullName,
            AppHostArgumentRedactor.RedactFromToString(arguments, appHostArgumentStartIndex));

        return new DirectAppHostRunSpec(command, workingDirectory, [.. arguments], directEnv, appHostArgumentStartIndex);
    }

    private async Task<bool> IsDirectLaunchDisabledAsync(FileInfo effectiveAppHostFile, CancellationToken cancellationToken)
    {
        var startDirectory = effectiveAppHostFile.Directory ?? new DirectoryInfo(Environment.CurrentDirectory);
        var value = await _configurationService.GetConfigurationFromDirectoryAsync(DirectLaunchDisabledConfigKey, startDirectory, cancellationToken: cancellationToken).ConfigureAwait(false);
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryResolveDirectRunTarget(
        AppHostProjectInfo info,
        FileInfo effectiveAppHostFile,
        IReadOnlyList<string> runArguments,
        out string command,
        out DirectoryInfo workingDirectory)
    {
        command = null!;
        workingDirectory = effectiveAppHostFile.Directory!;

        if (HasMultipleTargetFrameworks(info))
        {
            _logger.LogDebug(
                "Falling back to dotnet run for {Project}; direct AppHost launch does not support multi-targeted projects ({TargetFrameworks}).",
                effectiveAppHostFile.FullName,
                info.TargetFrameworks);
            return false;
        }

        if (string.IsNullOrWhiteSpace(info.RunCommand))
        {
            _logger.LogDebug(
                "Falling back to dotnet run for {Project}; MSBuild did not provide RunCommand.",
                effectiveAppHostFile.FullName);
            return false;
        }

        var projectDirectory = effectiveAppHostFile.Directory!;
        var runCommand = CommandPathResolver.NormalizeRunCommand(info.RunCommand);

        // The SDK emits RunCommand="dotnet" for executable .NETCoreApp projects without an apphost,
        // with RunArguments shaped as:
        //   exec "<TargetPath>" [StartArguments...]
        // Treat that as a direct-launchable SDK run command instead of looking for a literal
        // "dotnet" executable next to the project. DotNetCliRunner later substitutes Aspire's
        // resolved dotnet muxer so private SDK selection stays consistent.
        // https://github.com/dotnet/sdk/blob/main/src/Tasks/Microsoft.NET.Build.Tasks/targets/Microsoft.NET.Sdk.targets
        if (IsDotNetMuxerCommand(runCommand))
        {
            if (runArguments.Count < 2 || !string.Equals(runArguments[0], "exec", StringComparison.Ordinal))
            {
                _logger.LogDebug(
                    "Falling back to dotnet run for {Project}; RunCommand uses dotnet but RunArguments do not start with 'exec'.",
                    effectiveAppHostFile.FullName);
                return false;
            }

            var resolvedTargetPath = ResolvePath(runArguments[1], projectDirectory);
            if (!File.Exists(resolvedTargetPath))
            {
                _logger.LogDebug(
                    "Falling back to dotnet run for {Project}; RunArguments target {TargetPath} does not exist.",
                    effectiveAppHostFile.FullName,
                    resolvedTargetPath);
                return false;
            }

            var runtimeConfigPath = Path.ChangeExtension(resolvedTargetPath, ".runtimeconfig.json");
            if (!File.Exists(runtimeConfigPath))
            {
                _logger.LogDebug(
                    "Falling back to dotnet run for {Project}; runtimeconfig {RuntimeConfigPath} does not exist.",
                    effectiveAppHostFile.FullName,
                    runtimeConfigPath);
                return false;
            }

            command = runCommand;
        }
        else
        {
            var resolvedRunCommand = ResolvePath(runCommand, projectDirectory);
            if (!File.Exists(resolvedRunCommand))
            {
                _logger.LogDebug(
                    "Falling back to dotnet run for {Project}; RunCommand {RunCommand} does not exist.",
                    effectiveAppHostFile.FullName,
                    resolvedRunCommand);
                return false;
            }

            command = resolvedRunCommand;
        }

        if (!string.IsNullOrWhiteSpace(info.RunWorkingDirectory))
        {
            workingDirectory = new DirectoryInfo(ResolvePath(info.RunWorkingDirectory, projectDirectory));
        }

        return true;
    }

    private static bool HasMultipleTargetFrameworks(AppHostProjectInfo info)
        => info.TargetFrameworks?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length > 1;

    private static string ResolvePath(string path, DirectoryInfo baseDirectory)
        => Path.IsPathFullyQualified(path) ? path : Path.GetFullPath(Path.Combine(baseDirectory.FullName, path));

    private static bool IsDotNetMuxerCommand(string command)
        => string.Equals(Path.GetFileNameWithoutExtension(CommandPathResolver.NormalizeRunCommand(command)), "dotnet", StringComparison.OrdinalIgnoreCase);

    private bool TryApplyProjectLaunchSettings(
        FileInfo effectiveAppHostFile,
        Dictionary<string, string> env,
        List<string> arguments,
        bool noLaunchProfile,
        string? launchProfile,
        bool hasExplicitApplicationArgs,
        bool hasRunArguments)
    {
        if (noLaunchProfile)
        {
            return true;
        }

        try
        {
            if (!TryGetLaunchSettingsPath(effectiveAppHostFile, out var launchSettingsPath))
            {
                // An explicitly selected profile must not be silently ignored. Let the SDK path
                // remain authoritative for its missing launch-settings/profile diagnostic.
                return string.IsNullOrEmpty(launchProfile);
            }

            if (!TryGetLaunchProfile(launchSettingsPath, launchProfile, out var profileName, out var profile))
            {
                _logger.LogDebug(
                    "Falling back to dotnet run for {Project}; launch settings do not contain the requested or a supported default profile.",
                    effectiveAppHostFile.FullName);
                return false;
            }

            if (!IsProjectLaunchProfile(profile))
            {
                _logger.LogDebug(
                    "Falling back to dotnet run for {Project}; launch profile {LaunchProfile} uses commandName {CommandName}.",
                    effectiveAppHostFile.FullName,
                    profileName,
                    profile.CommandName);
                return false;
            }

            // Project launchSettings.json uses the .NET launch profile shape:
            //   { "profiles": { "https": { "commandName": "Project",
            //       "applicationUrl": "https://localhost:1234;http://localhost:5678",
            //       "commandLineArgs": "--flag \"two words\"",
            //       "environmentVariables": { "DOTNET_ENVIRONMENT": "Development" } } } }
            // `dotnet run` selects the first supported profile in file order when no profile is
            // explicitly named. Direct launch can only preserve Project profiles, so Executable
            // profiles fall back to the SDK command path.
            // See https://learn.microsoft.com/aspnet/core/fundamentals/environments#lsj and
            // https://json.schemastore.org/launchsettings.json.
            env["DOTNET_LAUNCH_PROFILE"] = profileName;

            if (!string.IsNullOrWhiteSpace(profile.ApplicationUrl))
            {
                env[KnownAspNetCoreConfigNames.Urls] = profile.ApplicationUrl;
            }

            if (profile.EnvironmentVariables is not null)
            {
                foreach (var (name, value) in profile.EnvironmentVariables)
                {
                    if (value is null)
                    {
                        // `System.Text.Json` will deserialize `"FOO": null` into a null dictionary
                        // value even though the value type is non-nullable. Skip those entries.
                        continue;
                    }

                    // Match Aspire project-resource launch-profile behavior rather than `dotnet run`:
                    // Aspire expands environment-variable references before starting child resources.
                    env[name] = Environment.ExpandEnvironmentVariables(value);
                }
            }

            if (!hasExplicitApplicationArgs && !hasRunArguments && !string.IsNullOrEmpty(profile.CommandLineArgs))
            {
                // Keep command-line argument expansion aligned with the environment-variable
                // handling above so direct-launch AppHosts behave like Aspire child resources.
                AppendParsedArguments(Environment.ExpandEnvironmentVariables(profile.CommandLineArgs), arguments);
            }

            return true;
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Falling back to dotnet run because launch settings could not be parsed for {Project}.", effectiveAppHostFile.FullName);
            return false;
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Falling back to dotnet run because launch settings could not be read for {Project}.", effectiveAppHostFile.FullName);
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "Falling back to dotnet run because launch settings could not be read for {Project}.", effectiveAppHostFile.FullName);
            return false;
        }
    }

    private static bool TryGetLaunchSettingsPath(FileInfo projectFile, out string launchSettingsPath)
    {
        var directory = projectFile.Directory!.FullName;

        // Keep this lookup in sync with the SDK's `dotnet run` launch-settings discovery:
        // first check Properties/launchSettings.json (or My Project/launchSettings.json for VB),
        // then fall back to the flat <ProjectName>.run.json file. Profile parsing intentionally
        // stays separate because it must preserve raw JSON property enumeration to match SDK
        // duplicate-profile detection.
        // https://github.com/dotnet/sdk/blob/main/src/Microsoft.DotNet.ProjectTools/LaunchSettings/LaunchSettings.cs
        var propertiesDirectoryName = projectFile.Extension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase)
            ? "My Project"
            : "Properties";

        var propertiesLaunchSettingsPath = Path.Combine(directory, propertiesDirectoryName, "launchSettings.json");
        if (File.Exists(propertiesLaunchSettingsPath))
        {
            launchSettingsPath = propertiesLaunchSettingsPath;
            return true;
        }

        var runJsonPath = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(projectFile.Name)}.run.json");
        if (File.Exists(runJsonPath))
        {
            launchSettingsPath = runJsonPath;
            return true;
        }

        launchSettingsPath = null!;
        return false;
    }

    private static bool TryGetLaunchProfile(
        string launchSettingsPath,
        string? requestedProfileName,
        out string profileName,
        out AppHostLaunchProfile profile)
    {
        using var stream = File.OpenRead(launchSettingsPath);
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        if (document.RootElement.ValueKind is not JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("profiles", out var profiles) ||
            profiles.ValueKind is not JsonValueKind.Object)
        {
            profileName = null!;
            profile = null!;
            return false;
        }

        JsonProperty selectedProfile = default;

        if (!string.IsNullOrEmpty(requestedProfileName))
        {
            var hasMatch = false;
            foreach (var candidate in profiles.EnumerateObject())
            {
                if (!string.Equals(candidate.Name, requestedProfileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // The SDK enumerates raw JSON properties so both duplicate names and names that
                // differ only by casing remain visible. Preserve that behavior instead of letting
                // dictionary deserialization silently replace an earlier property.
                if (hasMatch)
                {
                    profileName = null!;
                    profile = null!;
                    return false;
                }

                selectedProfile = candidate;
                hasMatch = true;
            }

            if (!hasMatch || selectedProfile.Value.ValueKind is not JsonValueKind.Object)
            {
                profileName = null!;
                profile = null!;
                return false;
            }
        }
        else
        {
            foreach (var candidate in profiles.EnumerateObject())
            {
                if (candidate.Value.ValueKind is not JsonValueKind.Object ||
                    !candidate.Value.TryGetProperty("commandName", out var commandName) ||
                    commandName.ValueKind is not JsonValueKind.String ||
                    commandName.GetString() is not ("Project" or "Executable"))
                {
                    continue;
                }

                selectedProfile = candidate;
                break;
            }

            if (selectedProfile.Value.ValueKind is not JsonValueKind.Object)
            {
                profileName = null!;
                profile = null!;
                return false;
            }
        }

        var selectedProfileValue = selectedProfile.Value.Deserialize(AppHostLaunchSettingsSerializerContext.Default.AppHostLaunchProfile);
        if (selectedProfileValue is null)
        {
            profileName = null!;
            profile = null!;
            return false;
        }

        profileName = string.IsNullOrEmpty(requestedProfileName)
            ? selectedProfile.Name
            : requestedProfileName;
        profile = selectedProfileValue;
        return true;
    }

    private static bool IsProjectLaunchProfile(AppHostLaunchProfile profile)
        => string.Equals(profile.CommandName, "Project", StringComparison.Ordinal);

    private static bool IsProjectFile(FileInfo appHostFile)
        => ProjectExtensions.Contains(appHostFile.Extension.ToLowerInvariant());

    private static List<string> ParseArguments(string? rawArguments)
        => string.IsNullOrWhiteSpace(rawArguments)
            ? []
            : CommandLineArgsParser.Parse(rawArguments);

    private static void AppendParsedArguments(string? rawArguments, List<string> arguments)
    {
        if (!string.IsNullOrWhiteSpace(rawArguments))
        {
            arguments.AddRange(CommandLineArgsParser.Parse(rawArguments));
        }
    }

    internal static void ConfigureSingleFileRunEnvironment(
        FileInfo appHostFile,
        Dictionary<string, string> env,
        IReadOnlyDictionary<string, string?>? inheritedEnvironmentVariables = null,
        string[]? args = null)
    {
        var runJsonFilePath = appHostFile.FullName[..^2] + "run.json";
        if (File.Exists(runJsonFilePath))
        {
            // dotnet run reads the launch profile from apphost.run.json natively, so the CLI
            // does not need to inject any environment variables itself.
            return;
        }

        // No apphost.run.json — fall back to aspire.config.json profiles (if any), then to
        // hardcoded defaults. ApplyEffectiveEnvironment is always called last so that explicit
        // --environment arguments still win.
        if (!TryApplyAspireConfigProfile(appHostFile, env, filterEnvironmentNames: false))
        {
            ApplyDefaultSingleFileEndpoints(env);
        }

        AppHostEnvironmentDefaults.ApplyEffectiveEnvironment(
            env,
            AppHostEnvironmentDefaults.DevelopmentEnvironmentName,
            inheritedEnvironmentVariables,
            args);
    }

    internal static void ConfigureSingleFilePublishEnvironment(
        FileInfo appHostFile,
        Dictionary<string, string> env,
        IReadOnlyDictionary<string, string?>? inheritedEnvironmentVariables = null,
        string[]? args = null)
    {
        if (!TryApplySingleFileLaunchProfileEnvironmentVariables(appHostFile, env)
            && !TryApplyAspireConfigProfile(appHostFile, env, filterEnvironmentNames: true))
        {
            ApplyDefaultSingleFileEndpoints(env);
        }

        AppHostEnvironmentDefaults.ApplyEffectiveEnvironment(
            env,
            AppHostEnvironmentDefaults.ProductionEnvironmentName,
            inheritedEnvironmentVariables,
            args);
    }

    private static bool TryApplySingleFileLaunchProfileEnvironmentVariables(
        FileInfo appHostFile,
        Dictionary<string, string> env)
    {
        var profiles = AspireConfigFile.ReadApphostRunProfiles(appHostFile.FullName[..^2] + "run.json");
        return TryApplyProfile(profiles, env, filterEnvironmentNames: true);
    }

    private static bool TryApplyAspireConfigProfile(
        FileInfo appHostFile,
        Dictionary<string, string> env,
        bool filterEnvironmentNames)
    {
        if (appHostFile.DirectoryName is not { Length: > 0 } directoryName)
        {
            return false;
        }

        AspireConfigFile? config;
        try
        {
            config = AspireConfigFile.Load(directoryName);
        }
        catch (JsonException)
        {
            // Malformed aspire.config.json — fall back to the next source rather than failing
            // the run/publish. This mirrors what happens when apphost.run.json is malformed.
            return false;
        }

        if (config?.Profiles is null)
        {
            return false;
        }

        // If aspire.config.json names a different AppHost file, don't apply its profile to
        // this AppHost. (Covers layouts where multiple AppHosts share a directory.)
        if (!string.IsNullOrEmpty(config.AppHost?.Path))
        {
            var configuredAppHostPath = PathNormalizer.ResolveToFilesystemPath(
                Path.GetFullPath(Path.Combine(directoryName, config.AppHost.Path)));
            var selectedAppHostPath = PathNormalizer.ResolveToFilesystemPath(appHostFile.FullName);
            if (!string.Equals(configuredAppHostPath, selectedAppHostPath, StringComparisons.FileSystemPath))
            {
                return false;
            }
        }

        return TryApplyProfile(config.Profiles, env, filterEnvironmentNames);
    }

    private static bool TryApplyProfile(
        IReadOnlyDictionary<string, AspireConfigProfile>? profiles,
        Dictionary<string, string> env,
        bool filterEnvironmentNames)
    {
        AspireConfigProfile? profile;

        if (profiles?.TryGetValue("https", out var httpsProfile) == true)
        {
            profile = httpsProfile;
        }
        else
        {
            profile = profiles?.Values.FirstOrDefault();
        }

        if (profile is null || string.IsNullOrEmpty(profile.ApplicationUrl))
        {
            return false;
        }

        env[KnownAspNetCoreConfigNames.Urls] = profile.ApplicationUrl;

        if (profile.EnvironmentVariables is not null)
        {
            foreach (var (key, value) in profile.EnvironmentVariables)
            {
                if (filterEnvironmentNames && AppHostEnvironmentDefaults.IsEnvironmentVariableName(key))
                {
                    continue;
                }

                env[key] = value;
            }
        }

        return true;
    }

    private static void ApplyDefaultSingleFileEndpoints(IDictionary<string, string> env)
    {
        env[KnownAspNetCoreConfigNames.Urls] = "https://localhost:17193;http://localhost:15069";
        env["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"] = "https://localhost:21293";
        env["ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"] = "https://localhost:22086";
    }

    /// <inheritdoc />
    public async Task<int> PublishAsync(PublishContext context, CancellationToken cancellationToken)
    {
        // .NET projects require the SDK to be installed
        if (!await SdkInstallHelper.EnsureSdkInstalledAsync(_sdkInstaller, _interactionService, _telemetry, cancellationToken: cancellationToken))
        {
            // Throw an exception that will be caught by the command and result in SdkNotInstalled exit code
            // This is cleaner than trying to signal through the backchannel pattern
            throw new DotNetSdkNotInstalledException();
        }

        var effectiveAppHostFile = context.AppHostFile;
        var isSingleFileAppHost = !IsProjectFile(effectiveAppHostFile) && IsValidSingleFileAppHost(effectiveAppHostFile);
        var env = new Dictionary<string, string>(context.EnvironmentVariables);

        // Check compatibility for project-based apphosts
        if (!isSingleFileAppHost)
        {
            // Route through the cached helper so publish shares the same MSBuild
            // inspection result that PublishCommand's earlier ValidateAppHostAsync
            // populated. Issue #17197.
            var compatibilityCheck = await CheckAppHostCompatibilityAsync(
                effectiveAppHostFile,
                isSingleFileAppHost: false,
                cancellationToken);

            if (!compatibilityCheck.IsCompatibleAppHost)
            {
                var exception = new AppHostIncompatibleException(
                    $"The app host is not compatible. Aspire.Hosting version: {compatibilityCheck.AspireHostingVersion}",
                    "Aspire.Hosting",
                    compatibilityCheck.AspireHostingVersion);
                // Signal the backchannel completion source so the caller doesn't wait forever
                context.BackchannelCompletionSource?.TrySetException(exception);
                throw exception;
            }
        }

        // Build the apphost (unless --no-build is specified)
        if (!isSingleFileAppHost && !context.NoBuild)
        {
            var buildOutputCollector = new OutputCollector(_fileLoggerProvider, CliLogFormat.Categories.Build);
            var buildOptions = new ProcessInvocationOptions
            {
                StandardOutputCallback = buildOutputCollector.AppendOutput,
                StandardErrorCallback = buildOutputCollector.AppendError,
            };

            var buildExitCode = await AppHostHelper.BuildAppHostAsync(
                _runner,
                _interactionService,
                effectiveAppHostFile,
                noRestore: false,
                buildOptions,
                context.WorkingDirectory,
                cancellationToken);

            if (buildExitCode != 0)
            {
                // Set OutputCollector so PipelineCommandBase can display errors
                context.OutputCollector = buildOutputCollector;
                // Signal the backchannel completion source so the caller doesn't wait forever
                context.BackchannelCompletionSource?.TrySetException(
                    new InvalidOperationException("The app host build failed."));
                return CliExitCodes.FailedToBuildArtifacts;
            }
        }

        // Create collector and store in context for exception handling
        var runOutputCollector = new OutputCollector(_fileLoggerProvider, CliLogFormat.Categories.AppHost);
        context.OutputCollector = runOutputCollector;

        var runOptions = new ProcessInvocationOptions
        {
            StandardOutputCallback = runOutputCollector.AppendOutput,
            StandardErrorCallback = runOutputCollector.AppendError,
            NoLaunchProfile = true,
            StartDebugSession = context.StartDebugSession
        };

        if (isSingleFileAppHost)
        {
            ConfigureSingleFilePublishEnvironment(effectiveAppHostFile, env, args: context.Arguments);
        }

        return await _runner.RunAsync(
            effectiveAppHostFile,
            watch: false,
            noBuild: true,
            noRestore: false,
            context.Arguments,
            env,
            context.BackchannelCompletionSource,
            runOptions,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> AddPackageAsync(AddPackageContext context, CancellationToken cancellationToken)
    {
        var outputCollector = new OutputCollector(_fileLoggerProvider, CliLogFormat.Categories.Package);
        context.OutputCollector = outputCollector;

        var options = new ProcessInvocationOptions
        {
            StandardOutputCallback = outputCollector.AppendOutput,
            StandardErrorCallback = outputCollector.AppendError,
        };
        var result = await _runner.AddPackageAsync(
            context.AppHostFile,
            context.PackageId,
            context.PackageVersion,
            context.Source,
            noRestore: false,
            options,
            cancellationToken);

        return result == 0;
    }

    /// <inheritdoc />
    public async Task<UpdatePackagesResult> UpdatePackagesAsync(UpdatePackagesContext context, CancellationToken cancellationToken)
    {
        var result = await _projectUpdater.UpdateProjectAsync(context, cancellationToken);
        return new UpdatePackagesResult { UpdatesApplied = result.UpdatedApplied };
    }

    /// <inheritdoc />
    public async Task<RunningInstanceResult> FindAndStopRunningInstanceAsync(FileInfo appHostFile, DirectoryInfo homeDirectory, CancellationToken cancellationToken)
    {
        var matchingSockets = AppHostSocketManager.FindSockets(
            appHostFile.FullName,
            homeDirectory.FullName,
            Environment.ProcessId,
            _logger);

        // Check if any socket files exist
        if (matchingSockets.Count == 0)
        {
            return RunningInstanceResult.NoRunningInstance;
        }

        // Stop all running instances
        var stopTasks = matchingSockets.Select(socket =>
            _runningInstanceManager.StopRunningInstanceAsync(socket, cancellationToken));
        var results = await Task.WhenAll(stopTasks);
        return results.All(r => r) ? RunningInstanceResult.InstanceStopped : RunningInstanceResult.StopFailed;
    }

    /// <summary>
    /// Gets the UserSecretsId from a project file, optionally initializing if not configured.
    /// </summary>
    public async Task<string?> GetUserSecretsIdAsync(FileInfo projectFile, bool autoInit, CancellationToken cancellationToken)
    {
        var userSecretsId = await QueryUserSecretsIdAsync(projectFile, cancellationToken);

        if (!string.IsNullOrEmpty(userSecretsId) || !autoInit)
        {
            return userSecretsId;
        }

        // Auto-initialize user secrets (only for csproj projects - file-based apphosts
        // always have a UserSecretsId provided by the SDK)
        if (!ProjectExtensions.Contains(projectFile.Extension.ToLowerInvariant()))
        {
            return userSecretsId;
        }

        _logger.LogInformation("No UserSecretsId found. Initializing user secrets for {Project}...", projectFile.Name);
        _interactionService.DisplayMessage(KnownEmojis.Key, $"Initializing user secrets for {projectFile.Name}...");

        await _runner.InitUserSecretsAsync(
            projectFile,
            new ProcessInvocationOptions(),
            cancellationToken);

        // Re-query
        return await QueryUserSecretsIdAsync(projectFile, cancellationToken);
    }

    private async Task<string?> QueryUserSecretsIdAsync(FileInfo projectFile, CancellationToken cancellationToken)
    {
        try
        {
            // Read UserSecretsId from the shared AppHost build info cache so isolated mode
            // does not pay for a second `dotnet msbuild -getProperty` invocation when the
            // run path already fetched the AppHost metadata for validation/compat.
            var info = await _appHostInfoResolver.GetAppHostInfoAsync(projectFile, cancellationToken);
            return info.UserSecretsId;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get UserSecretsId from project file");
            return null;
        }
    }

    private Task<BundleLayoutLease?> AcquireCliBundleLayoutAsync(CancellationToken cancellationToken)
        => _bundleService.EnsureExtractedAndAcquireLayoutAsync("cli", "dotnet-apphost", cancellationToken);

    private void ConfigureCliBundleEnvironment(
        Dictionary<string, string> env,
        BundleLayoutLease? layoutLease,
        bool injectDcpAndDashboard)
    {
        var layout = layoutLease?.Layout;
        if (layout is null)
        {
            // Only log when the AppHost actually opted into the bundle; for non-CliBundle
            // AppHosts a missing layout is expected (e.g. the CLI may not have a bundle on
            // disk) and would otherwise spam the debug log on every run.
            if (injectDcpAndDashboard)
            {
                _logger.LogDebug("AspireUseCliBundle is enabled, but the Aspire CLI bundle layout was not available from this CLI process. The AppHost will resolve configured, inherited, or assembly-metadata paths.");
            }
            // Don't return yet — repo-mode runs (DEBUG, `dotnet run --project src/Aspire.Cli`)
            // can still inject the terminal host path from the just-built artifact even when
            // no bundle layout exists at all (e.g. clean dev machine with no `aspire` install).
        }

        if (!HasEnvironmentOverride(env, "AspireCliBundlePath") && !string.IsNullOrEmpty(layout?.LayoutPath))
        {
            env["AspireCliBundlePath"] = layout.LayoutPath;
        }

        if (injectDcpAndDashboard && layout is not null)
        {
            if (!IsUsableDcpDirectory(GetEffectiveEnvironmentValue(env, BundleDiscovery.DcpPathEnvVar)) &&
                layout.GetDcpPath() is { } layoutDcpPath &&
                IsUsableDcpDirectory(layoutDcpPath))
            {
                env[BundleDiscovery.DcpPathEnvVar] = layoutDcpPath;
            }

            if (!IsUsableDashboardPath(GetEffectiveEnvironmentValue(env, BundleDiscovery.DashboardPathEnvVar)) &&
                layout.GetManagedPath() is { } layoutManagedPath &&
                IsUsableDashboardPath(layoutManagedPath))
            {
                env[BundleDiscovery.DashboardPathEnvVar] = layoutManagedPath;
            }
        }

        // Terminal host injection is unconditional: aspire-managed in the bundle exposes
        // the `terminalhost` subcommand regardless of whether the AppHost opted into
        // AspireUseCliBundle, and no per-RID NuGet stamps the metadata path today. This
        // is what lets `aspire run` light up WithTerminal() for AppHosts created by
        // `aspire new` (which default to per-RID NuGets, not the bundle).
        //
        // Path and args are treated as a pair: if a user pre-populated the path env var
        // (e.g. side-loading a custom terminal host build), don't overwrite the args —
        // their binary may not understand the "terminalhost" dispatcher arg.
        //
        // Preference order for the terminal host binary:
        //  1) Pre-populated env var — user override always wins.
        //  2) Repo-local built artifact when running `dotnet run` inside the Aspire repo
        //     (DEBUG only — AspireRepositoryDetector walks for Aspire.slnx in DEBUG builds).
        //     Without this, repo-mode runs pick up the bundle layout cached at the user's
        //     installed CLI location (e.g. ~/.aspire/bundle/), whose aspire-managed predates
        //     the `terminalhost` subcommand and fails the AppHost launch with a confusing
        //     "older CLI" diagnostic. Installed CLIs are unaffected because DetectRepositoryRoot
        //     only resolves via env var in release builds.
        //  3) Bundle layout aspire-managed (normal `aspire run` install path).
        if (!HasEnvironmentOverride(env, BundleDiscovery.TerminalHostPathEnvVar))
        {
            var terminalHostPath = TryGetRepoLocalManagedPath() ?? layout?.GetManagedPath();
            if (terminalHostPath is not null && IsUsableDashboardPath(terminalHostPath))
            {
                env[BundleDiscovery.TerminalHostPathEnvVar] = terminalHostPath;
                if (!HasEnvironmentOverride(env, BundleDiscovery.TerminalHostInvocationArgsEnvVar))
                {
                    env[BundleDiscovery.TerminalHostInvocationArgsEnvVar] = "terminalhost";
                }
            }
        }

        layoutLease?.AddEnvironment(env);
    }

    private bool HasEnvironmentOverride(IReadOnlyDictionary<string, string> env, string name)
        => !string.IsNullOrWhiteSpace(GetEffectiveEnvironmentValue(env, name));

    private string? GetEffectiveEnvironmentValue(IReadOnlyDictionary<string, string> env, string name)
        => env.TryGetValue(name, out var value) ? value : _environment.GetEnvironmentVariable(name);

    private static bool IsUsableDcpDirectory(string? path)
        => !string.IsNullOrWhiteSpace(path) &&
            Directory.Exists(path) &&
            File.Exists(BundleDiscovery.GetDcpExecutablePath(path));

    private static bool IsUsableDashboardPath(string? path)
        => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    /// <summary>
    /// Resolves the repo-local <c>aspire-managed</c> binary when the CLI is running from
    /// an Aspire repo checkout (typically <c>dotnet run --project src/Aspire.Cli</c>).
    /// Returns <c>null</c> in release builds and when no repo-local build exists.
    /// </summary>
    private static string? TryGetRepoLocalManagedPath()
    {
        if (RepoLocalManagedPathProviderOverride is { } overrideProvider)
        {
            return overrideProvider();
        }

        var repoRoot = AspireRepositoryDetector.DetectRepositoryRoot();
        return BundleDiscovery.TryGetRepoLocalManagedPath(repoRoot);
    }

    /// <summary>
    /// Configures isolated mode by enabling port randomization and isolating user secrets.
    /// </summary>
    /// <param name="appHostFile">The app host project file.</param>
    /// <param name="env">The environment variables dictionary to modify.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The isolated user secrets ID if created, or null if no isolation was needed.</returns>
    private async Task<string?> ConfigureIsolatedModeAsync(
        FileInfo appHostFile,
        Dictionary<string, string> env,
        CancellationToken cancellationToken)
    {
        // Enable port randomization for isolated mode
        env["DcpPublisher__RandomizePorts"] = "true";

        // Get the UserSecretsId from the project and create isolated copy
        var userSecretsId = await QueryUserSecretsIdAsync(appHostFile, cancellationToken);
        if (!string.IsNullOrEmpty(userSecretsId))
        {
            _interactionService.DisplayMessage(KnownEmojis.Key, RunCommandStrings.CopyingUserSecrets);
            var isolatedUserSecretsId = IsolatedUserSecretsHelper.CreateIsolatedUserSecrets(userSecretsId);
            if (!string.IsNullOrEmpty(isolatedUserSecretsId))
            {
                // Override the user secrets ID for this run
                env["DOTNET_USER_SECRETS_ID"] = isolatedUserSecretsId;
                return isolatedUserSecretsId;
            }
        }

        return null;
    }

    private sealed record DirectAppHostRunSpec(
        string Command,
        DirectoryInfo WorkingDirectory,
        string[] Arguments,
        Dictionary<string, string> Environment,
        int AppHostArgumentStartIndex);
}
