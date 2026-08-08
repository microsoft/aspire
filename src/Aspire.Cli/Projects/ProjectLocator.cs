// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Aspire.Cli.Configuration;
using Aspire.Cli.DotNet;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Aspire.Cli.Projects;

internal interface IProjectLocator
{
    /// <summary>
    /// Finds all candidate AppHost projects in the specified search directory.
    /// </summary>
    /// <param name="searchDirectory">The directory to search recursively.</param>
    /// <param name="scope">Controls which files are considered. See <see cref="AppHostDiscoveryScope"/>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of candidate AppHost projects with language metadata sorted by full path.</returns>
    Task<List<AppHostProjectCandidate>> FindAppHostProjectsAsync(
        DirectoryInfo searchDirectory,
        AppHostDiscoveryScope scope,
        CancellationToken cancellationToken);

    /// <summary>
    /// Streams candidate AppHost projects as discovery/validation completes.
    /// </summary>
    /// <param name="searchDirectory">The directory to search recursively.</param>
    /// <param name="scope">Controls which files are considered. See <see cref="AppHostDiscoveryScope"/>.</param>
    /// <param name="onDirectoryEnumerated">
    /// Optional callback invoked synchronously on the discovery thread with the running total of directories
    /// enumerated so callers can render progress before validation completes. See
    /// <see cref="IAppHostCandidateFinder.FindCandidateFilesAsync"/> for caller obligations.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An async stream of candidate AppHost projects in completion order.</returns>
    async IAsyncEnumerable<AppHostProjectCandidate> FindAppHostProjectsStreamAsync(
        DirectoryInfo searchDirectory,
        AppHostDiscoveryScope scope,
        Action<int>? onDirectoryEnumerated = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var candidates = await FindAppHostProjectsAsync(searchDirectory, scope, cancellationToken).ConfigureAwait(false);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return candidate;
        }
    }

    /// <summary>
    /// Finds all candidate AppHost projects in the specified search directory up to the specified depth.
    /// </summary>
    /// <param name="searchDirectory">The directory to search.</param>
    /// <param name="scope">Controls which files are considered. See <see cref="AppHostDiscoveryScope"/>.</param>
    /// <param name="maxDepth">The maximum subdirectory depth to search, where 0 only considers files in <paramref name="searchDirectory"/>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of candidate AppHost projects with language metadata sorted by full path.</returns>
    Task<List<AppHostProjectCandidate>> FindAppHostProjectsAsync(DirectoryInfo searchDirectory, AppHostDiscoveryScope scope, int? maxDepth, CancellationToken cancellationToken)
        => maxDepth is null
            ? FindAppHostProjectsAsync(searchDirectory, scope, cancellationToken)
            : throw new NotSupportedException();

    /// <summary>
    /// Finds all candidate AppHost project files in the specified search directory, without language metadata.
    /// </summary>
    /// <param name="searchDirectory">The directory to search recursively.</param>
    /// <param name="scope">Controls which files are considered. See <see cref="AppHostDiscoveryScope"/>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of candidate AppHost project files sorted by full path.</returns>
    Task<List<FileInfo>> FindAppHostProjectFilesAsync(DirectoryInfo searchDirectory, AppHostDiscoveryScope scope, CancellationToken cancellationToken);

    /// <summary>
    /// Finds all candidate AppHost project files in the specified search directory up to the specified depth, without language metadata.
    /// </summary>
    /// <param name="searchDirectory">The directory to search.</param>
    /// <param name="scope">Controls which files are considered. See <see cref="AppHostDiscoveryScope"/>.</param>
    /// <param name="maxDepth">The maximum subdirectory depth to search, where 0 only considers files in <paramref name="searchDirectory"/>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of candidate AppHost project files sorted by full path.</returns>
    Task<List<FileInfo>> FindAppHostProjectFilesAsync(DirectoryInfo searchDirectory, AppHostDiscoveryScope scope, int? maxDepth, CancellationToken cancellationToken)
        => maxDepth is null
            ? FindAppHostProjectFilesAsync(searchDirectory, scope, cancellationToken)
            : throw new NotSupportedException();
    Task<AppHostProjectSearchResult> UseOrFindAppHostProjectFileAsync(FileInfo? projectFile, MultipleAppHostProjectsFoundBehavior multipleAppHostProjectsFoundBehavior, bool createSettingsFile, CancellationToken cancellationToken = default);

    Task<AppHostProjectSearchResult> UseOrFindAppHostProjectFileAsync(FileInfo? projectFile, MultipleAppHostProjectsFoundBehavior multipleAppHostProjectsFoundBehavior, bool createSettingsFile, bool displayProgress, CancellationToken cancellationToken = default)
        => UseOrFindAppHostProjectFileAsync(projectFile, multipleAppHostProjectsFoundBehavior, createSettingsFile, cancellationToken);

    Task<FileInfo?> UseOrFindAppHostProjectFileAsync(FileInfo? projectFile, bool createSettingsFile, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves the AppHost project file from Aspire settings, without any user interaction,
    /// recursive filesystem scanning, or MSBuild-based validation of the configured path.
    /// Returns <c>null</c> when no settings file is found, when the path entry is absent,
    /// when the configured file does not exist, or when no registered handler can process it.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="UseOrFindAppHostProjectFileAsync(FileInfo?, bool, CancellationToken)"/>,
    /// this method intentionally does not call into MSBuild to validate the configured AppHost.
    /// Callers like <c>aspire update</c> need to operate on an AppHost whose pinned SDK no
    /// longer resolves (that's the very condition the command exists to repair); environment
    /// checks similarly just need the configured path so they can run their own targeted
    /// inspections against it.
    /// </remarks>
    Task<FileInfo?> GetAppHostFromSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// As <see cref="GetAppHostFromSettingsAsync(CancellationToken)"/>, but rooted at a specific
    /// directory.
    /// </summary>
    Task<FileInfo?> GetAppHostFromSettingsAsync(DirectoryInfo searchDirectory, bool searchParentDirectories, CancellationToken cancellationToken = default)
        => GetAppHostFromSettingsAsync(cancellationToken);
}

internal sealed record AppHostProjectCandidate(FileInfo AppHostFile, string Language, AppHostProjectCandidateStatus Status = AppHostProjectCandidateStatus.Buildable);

internal enum AppHostProjectCandidateStatus
{
    Buildable,
    PossiblyUnbuildable
}

internal sealed class ProjectLocator(
    ILogger<ProjectLocator> logger,
    CliExecutionContext executionContext,
    IEnvironment environment,
    IInteractionService interactionService,
    IConfigurationService configurationService,
    IAppHostProjectFactory projectFactory,
    ILanguageDiscovery languageDiscovery,
    IDotNetSdkInstaller sdkInstaller,
    IAppHostCandidateFinder appHostCandidateFinder,
    AspireCliTelemetry telemetry,
    IConfiguration configuration) : IProjectLocator
{
    private const string AspireConfigAppHostPathKey = "appHost.path";
    private const string LegacySettingsAppHostPathKey = "appHostPath";

    /// <summary>
    /// Identifies a CLI invocation whose AppHost target came from an editor launch configuration
    /// (for example a VS Code <c>launch.json</c> entry with an explicit <c>program</c>). Such a target
    /// is owned by the individual debug session, so it must never become the workspace default.
    /// </summary>
    private const string ExplicitLaunchConfigurationSelectionOrigin = "explicit-launch-configuration";

    /// <summary>
    /// Identifies a CLI invocation whose AppHost target was chosen by an agent or language model
    /// tool (for example the extension's <c>#aspireStartAppHost</c> tool) rather than by the user.
    /// </summary>
    private const string AgentSelectionOrigin = "agent-selection";

    /// <summary>
    /// The selection origins that name a target for one invocation rather than stating which AppHost
    /// the workspace defaults to.
    /// </summary>
    /// <remarks>
    /// Membership is decided here rather than by each producer so the persistence policy stays in
    /// one place: <c>aspire.config.json</c> "describes what a <em>project</em> wants, not what the
    /// <em>CLI binary</em> is" (<c>docs/specs/cli-identity-sidecar.md</c>), and an origin that is not
    /// a user's statement about the project must not be able to rewrite it. Origins the CLI does not
    /// know are treated as user selections, because an unrecognized value is far more likely to be a
    /// newer editor than a new class of non-user launch.
    /// </remarks>
    private static readonly HashSet<string> s_sessionScopedSelectionOrigins = new(StringComparer.OrdinalIgnoreCase)
    {
        ExplicitLaunchConfigurationSelectionOrigin,
        AgentSelectionOrigin
    };

    /// <summary>
    /// How long to wait for the workspace config lock before giving up on it.
    /// </summary>
    /// <remarks>
    /// Deliberately far below <see cref="FileLock"/>'s five-minute default. The critical section is
    /// a handful of small file reads and writes, so anything past a few seconds means the holder is
    /// wedged rather than busy, and this runs on the path of an interactive command where a long
    /// silent stall is worse than losing the serialization guarantee.
    /// </remarks>
    private static readonly TimeSpan s_workspaceConfigLockTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Finds all candidate AppHost projects in the specified search directory with language metadata.
    /// </summary>
    /// <param name="searchDirectory">The directory to search recursively.</param>
    /// <param name="scope">Controls which files are considered. See <see cref="AppHostDiscoveryScope"/>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of candidate AppHost projects with language metadata sorted by full path.</returns>
    public async Task<List<AppHostProjectCandidate>> FindAppHostProjectsAsync(
        DirectoryInfo searchDirectory,
        AppHostDiscoveryScope scope,
        CancellationToken cancellationToken)
    {
        return await FindAppHostProjectsAsync(searchDirectory, scope, maxDepth: null, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Finds all candidate AppHost projects in the specified search directory with language metadata.
    /// </summary>
    /// <param name="searchDirectory">The directory to search.</param>
    /// <param name="scope">Controls which files are considered. See <see cref="AppHostDiscoveryScope"/>.</param>
    /// <param name="maxDepth">The maximum subdirectory depth to search, where 0 only considers files in <paramref name="searchDirectory"/>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of candidate AppHost projects with language metadata sorted by full path.</returns>
    public async Task<List<AppHostProjectCandidate>> FindAppHostProjectsAsync(DirectoryInfo searchDirectory, AppHostDiscoveryScope scope, int? maxDepth, CancellationToken cancellationToken)
    {
        var allCandidates = await FindAppHostProjectFilesAsync(searchDirectory, stopAfterMultipleBuildableAppHosts: false, displayProgress: false, scope, maxDepth, cancellationToken: cancellationToken);
        var candidates = allCandidates.BuildableAppHost.Concat(allCandidates.UnbuildableSuspectedAppHostProjects).ToList();
        candidates.Sort((x, y) => string.Compare(x.AppHostFile.FullName, y.AppHostFile.FullName, StringComparison.Ordinal));
        return candidates;
    }

    public async IAsyncEnumerable<AppHostProjectCandidate> FindAppHostProjectsStreamAsync(
        DirectoryInfo searchDirectory,
        AppHostDiscoveryScope scope,
        Action<int>? onDirectoryEnumerated = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<AppHostProjectCandidate>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        using var discoveryCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var discoveryTask = CompleteFindAppHostProjectsStreamAsync(searchDirectory, scope, channel.Writer, onDirectoryEnumerated, discoveryCancellationTokenSource.Token);

        try
        {
            await foreach (var candidate in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return candidate;
            }

            await discoveryTask.ConfigureAwait(false);
        }
        finally
        {
            if (!discoveryTask.IsCompleted)
            {
                discoveryCancellationTokenSource.Cancel();
            }

            try
            {
                await discoveryTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (discoveryCancellationTokenSource.IsCancellationRequested)
            {
                // Enumeration can stop before discovery finishes (for example Ctrl+C). In that case
                // cancellation is already being surfaced to the consumer through ReadAllAsync.
            }
        }
    }

    private async Task CompleteFindAppHostProjectsStreamAsync(
        DirectoryInfo searchDirectory,
        AppHostDiscoveryScope scope,
        ChannelWriter<AppHostProjectCandidate> candidateWriter,
        Action<int>? onDirectoryEnumerated,
        CancellationToken cancellationToken)
    {
        try
        {
            await FindAppHostProjectFilesAsync(searchDirectory, stopAfterMultipleBuildableAppHosts: false, displayProgress: false, scope, maxDepth: null, candidateWriter, onDirectoryEnumerated, cancellationToken).ConfigureAwait(false);
            candidateWriter.TryComplete();
        }
        catch (Exception ex)
        {
            candidateWriter.TryComplete(ex);
        }
    }

    /// <summary>
    /// Finds all candidate AppHost project files in the specified search directory path.
    /// </summary>
    /// <param name="searchDirectory">The directory path to search recursively.</param>
    /// <param name="scope">Controls which files are considered. See <see cref="AppHostDiscoveryScope"/>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of candidate AppHost project files sorted by full path.</returns>
    public async Task<List<FileInfo>> FindAppHostProjectFilesAsync(DirectoryInfo searchDirectory, AppHostDiscoveryScope scope, CancellationToken cancellationToken)
    {
        return await FindAppHostProjectFilesAsync(searchDirectory, scope, maxDepth: null, cancellationToken);
    }

    /// <summary>
    /// Finds all candidate AppHost project files in the specified search directory path.
    /// </summary>
    /// <param name="searchDirectory">The directory path to search.</param>
    /// <param name="scope">Controls which files are considered. See <see cref="AppHostDiscoveryScope"/>.</param>
    /// <param name="maxDepth">The maximum subdirectory depth to search, where 0 only considers files in <paramref name="searchDirectory"/>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of candidate AppHost project files sorted by full path.</returns>
    public async Task<List<FileInfo>> FindAppHostProjectFilesAsync(DirectoryInfo searchDirectory, AppHostDiscoveryScope scope, int? maxDepth, CancellationToken cancellationToken)
    {
        var candidates = await FindAppHostProjectsAsync(searchDirectory, scope, maxDepth, cancellationToken);
        return candidates.Select(c => c.AppHostFile).ToList();
    }

    /// <summary>
    /// Finds all candidate AppHost project files in the specified search directory.
    /// </summary>
    /// <param name="searchDirectory">The directory to search recursively.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of candidate AppHost project files sorted by full path.</returns>
    public async Task<List<FileInfo>> FindAppHostProjectFilesAsync(string searchDirectory, CancellationToken cancellationToken)
    {
        // Preserve this legacy overload's previous "find anywhere under this path"
        // behavior. New command paths use the overload that requires an explicit
        // AppHostDiscoveryScope so callers must choose git-aware/default filtering,
        // explicit-directory filtering, or the legacy all-files walk deliberately.
        return await FindAppHostProjectFilesAsync(new DirectoryInfo(searchDirectory), AppHostDiscoveryScope.AllFiles, cancellationToken);
    }

    private async Task<(List<AppHostProjectCandidate> BuildableAppHost, List<AppHostProjectCandidate> UnbuildableSuspectedAppHostProjects, bool HasUnsupportedProjects)> FindAppHostProjectFilesAsync(DirectoryInfo searchDirectory, bool stopAfterMultipleBuildableAppHosts, bool displayProgress, AppHostDiscoveryScope scope, int? maxDepth, ChannelWriter<AppHostProjectCandidate>? candidateWriter = null, Action<int>? onDirectoryEnumerated = null, CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.StartDiagnosticActivity();

        async Task<(List<AppHostProjectCandidate> BuildableAppHost, List<AppHostProjectCandidate> UnbuildableSuspectedAppHostProjects, bool HasUnsupportedProjects)> FindAppHostsAsync()
        {
            var appHostProjects = new List<AppHostProjectCandidate>();
            var unbuildableSuspectedAppHostProjects = new List<AppHostProjectCandidate>();
            var hasUnsupportedProjects = false;
            var lockObject = new object();
            logger.LogDebug("Searching for project files in {SearchDirectory}", searchDirectory.FullName);

            async ValueTask ReportCandidateFoundAsync(AppHostProjectCandidate appHostProject, CancellationToken cancellationToken)
            {
                if (candidateWriter is null)
                {
                    return;
                }

                // Candidate validation runs in parallel, but consumers want one async stream they can
                // await in command code. A channel bridges those parallel workers to IAsyncEnumerable<T>
                // without letting terminal or JSON rendering re-enter state protected by lockObject.
                await candidateWriter.WriteAsync(appHostProject, cancellationToken).ConfigureAwait(false);
            }

            using var validationCancellationTokenSource = stopAfterMultipleBuildableAppHosts
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;
            var validationCancellationToken = validationCancellationTokenSource?.Token ?? cancellationToken;

            var parallelOptions = new ParallelOptions
            {
                CancellationToken = validationCancellationToken,
                MaxDegreeOfParallelism = Environment.ProcessorCount
            };

            // Get detection patterns from all languages
            var allLanguages = await languageDiscovery.GetAvailableLanguagesAsync(cancellationToken);
            var allPatterns = allLanguages.SelectMany(l => l.DetectionPatterns).Distinct().ToArray();

            logger.LogDebug("Searching for patterns: {Patterns}", string.Join(", ", allPatterns));

            var nugetCachePath = GetNuGetPackagesCachePath();
            logger.LogDebug("NuGet cache path to exclude: {NuGetCachePath}", nugetCachePath ?? "(none)");

            // Collect all candidates with their handlers across all patterns.
            var candidatesWithHandlers = new List<(FileInfo File, IAppHostProject Handler)>();
            var candidateSearchResult = await appHostCandidateFinder.FindCandidateFilesAsync(searchDirectory, allPatterns, nugetCachePath, scope, cancellationToken, maxDepth, onDirectoryEnumerated);
            var candidateFiles = candidateSearchResult.Files;
            var candidateCountsByPattern = candidateSearchResult.CountsByPattern;

            foreach (var pattern in allPatterns)
            {
                logger.LogDebug("Found {CandidateCount} files matching pattern '{Pattern}'", candidateCountsByPattern[pattern], pattern);
            }

            logger.LogDebug("Found {CandidateCount} unique candidate files matching AppHost detection patterns", candidateFiles.Length);

            foreach (var candidateFile in candidateFiles)
            {
                logger.LogDebug("Checking candidate file {CandidateFile}", candidateFile.FullName);

                var handler = projectFactory.TryGetProject(candidateFile);
                if (handler is null)
                {
                    logger.LogTrace("No handler found for {CandidateFile}", candidateFile.FullName);
                    continue;
                }

                candidatesWithHandlers.Add((candidateFile, handler));
            }

            // If any candidates are .NET projects, ensure the SDK is available
            var dotNetCandidate = candidatesWithHandlers.FirstOrDefault(c => c.Handler.LanguageId.Equals(KnownLanguageId.CSharp, StringComparison.OrdinalIgnoreCase));
            if (dotNetCandidate.Handler is { } dotNetHandler)
            {
                // TODO: Consider moving this check inside the handler.
                // Would need to support caching and reusing check across validations.
                if (!await SdkInstallHelper.EnsureSdkInstalledAsync(sdkInstaller, interactionService, telemetry, displayError: displayProgress, cancellationToken: cancellationToken))
                {
                    if (!displayProgress)
                    {
                        interactionService.DisplayRawText(ErrorStrings.DotNetSdkUnavailableAppHostDiscoveryWarning, ConsoleOutput.Error);
                    }

                    logger.LogWarning("The .NET SDK is not available. Marking .NET projects as unsupported.");
                    dotNetHandler.IsUnsupported = true;
                }
            }

            try
            {
                await Parallel.ForEachAsync(candidatesWithHandlers, parallelOptions, async (candidate, ct) =>
                {
                    var (candidateFile, handler) = candidate;

                    // Validate the candidate file using the handler
                    var validationResult = await handler.ValidateAppHostAsync(candidateFile, ct);

                    if (validationResult.IsValid)
                    {
                        logger.LogDebug("Found {Language} apphost {CandidateFile}", handler.DisplayName, candidateFile.FullName);
                        var relativePath = Path.GetRelativePath(executionContext.WorkingDirectory.FullName, candidateFile.FullName);
                        AppHostProjectCandidate appHostProject;
                        if (displayProgress)
                        {
                            interactionService.DisplaySubtleMessage(relativePath);
                        }
                        lock (lockObject)
                        {
                            appHostProject = new AppHostProjectCandidate(candidateFile, handler.LanguageId);
                            appHostProjects.Add(appHostProject);

                            if (stopAfterMultipleBuildableAppHosts && appHostProjects.Count >= 2)
                            {
                                validationCancellationTokenSource?.Cancel();
                            }
                        }
                        await ReportCandidateFoundAsync(appHostProject, ct).ConfigureAwait(false);
                    }
                    else if (validationResult.IsUnsupported)
                    {
                        var relativePath = Path.GetRelativePath(executionContext.WorkingDirectory.FullName, candidateFile.FullName);
                        if (displayProgress)
                        {
                            interactionService.DisplayMessage(KnownEmojis.Warning, string.Format(CultureInfo.CurrentCulture, ErrorStrings.ProjectFileUnsupportedInCurrentEnvironment, relativePath));
                        }
                        logger.LogDebug("Skipping unsupported project {CandidateFile}", candidateFile.FullName);
                        lock (lockObject)
                        {
                            hasUnsupportedProjects = true;
                        }
                    }
                    else if (validationResult.IsPossiblyUnbuildable)
                    {
                        var relativePath = Path.GetRelativePath(executionContext.WorkingDirectory.FullName, candidateFile.FullName);
                        AppHostProjectCandidate appHostProject;
                        if (displayProgress)
                        {
                            interactionService.DisplayMessage(KnownEmojis.Warning, string.Format(CultureInfo.CurrentCulture, ErrorStrings.ProjectFileMayBeUnbuildableAppHost, relativePath));
                        }
                        lock (lockObject)
                        {
                            appHostProject = new AppHostProjectCandidate(candidateFile, handler.LanguageId, AppHostProjectCandidateStatus.PossiblyUnbuildable);
                            unbuildableSuspectedAppHostProjects.Add(appHostProject);
                        }
                        await ReportCandidateFoundAsync(appHostProject, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        logger.LogTrace("File {CandidateFile} is not a valid Aspire host", candidateFile.FullName);
                    }
                });
            }
            catch (OperationCanceledException) when (validationCancellationTokenSource?.IsCancellationRequested is true && !cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug("Stopping AppHost discovery early after finding multiple valid AppHost projects.");
            }

            await AddSettingsAppHostCandidateAsync().ConfigureAwait(false);

            // This sort is done here to make results deterministic since we get all the app
            // host information in parallel and the order may vary.
            appHostProjects.Sort((x, y) => string.Compare(x.AppHostFile.FullName, y.AppHostFile.FullName, StringComparison.Ordinal));

            return (appHostProjects, unbuildableSuspectedAppHostProjects, hasUnsupportedProjects);

            async Task AddSettingsAppHostCandidateAsync()
            {
                var settingsAppHost = await GetAppHostProjectFileFromSettingsAsync(searchDirectory, searchParentDirectories: true, silent: false, cancellationToken).ConfigureAwait(false);
                if (settingsAppHost is null)
                {
                    return;
                }

                // Windows and default macOS APFS volumes are case-insensitive, so a
                // differently-cased settings path can still refer to the same file found
                // by the discovery walk. See https://github.com/microsoft/aspire/issues/17635.
                var pathComparison = environment.IsWindows() || environment.IsMacOS()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;

                // Canonicalize symlinks before comparing so a settings-derived candidate
                // like /tmp/L5/x.cs does not produce a duplicate entry next to the
                // discovery-walked /private/tmp/L5/x.cs on macOS, where /tmp is a symlink
                // to /private/tmp. See https://github.com/microsoft/aspire/issues/17626.
                // Resolved paths are used as comparison keys only — the surfaced
                // AppHostProjectCandidate keeps the original FileInfo so display paths are
                // unchanged from what the user-authored settings file pointed at.
                //
                // Symlink resolution does ~one syscall per path segment, so we keep it
                // off the hot path: the exact-string compare below short-circuits before
                // the per-candidate resolve runs at all in the common case (no symlinks
                // involved). Pre-materializing canonical paths for every candidate would
                // force the resolve even when the cheap compare would have matched.
                var settingsCanonicalPath = PathNormalizer.ResolveSymlinks(settingsAppHost.FullName);
                bool IsDuplicate(AppHostProjectCandidate candidate)
                {
                    if (string.Equals(candidate.AppHostFile.FullName, settingsAppHost.FullName, pathComparison))
                    {
                        return true;
                    }

                    var candidateCanonicalPath = PathNormalizer.ResolveSymlinks(candidate.AppHostFile.FullName);
                    return string.Equals(candidateCanonicalPath, settingsCanonicalPath, pathComparison);
                }

                if (appHostProjects.Any(IsDuplicate) || unbuildableSuspectedAppHostProjects.Any(IsDuplicate))
                {
                    return;
                }

                var handler = projectFactory.TryGetProject(settingsAppHost);
                if (handler is null)
                {
                    var relativePath = Path.GetRelativePath(executionContext.WorkingDirectory.FullName, settingsAppHost.FullName);
                    if (displayProgress)
                    {
                        interactionService.DisplayMessage(KnownEmojis.Warning, string.Format(CultureInfo.CurrentCulture, ErrorStrings.ProjectFileUnsupportedInCurrentEnvironment, relativePath));
                    }

                    logger.LogDebug("Skipping configured AppHost project {SettingsAppHost} because no project handler was found.", settingsAppHost.FullName);
                    hasUnsupportedProjects = true;
                    return;
                }

                var validationResult = await handler.ValidateAppHostAsync(settingsAppHost, cancellationToken).ConfigureAwait(false);
                var settingsAppHostRelativePath = Path.GetRelativePath(executionContext.WorkingDirectory.FullName, settingsAppHost.FullName);
                if (validationResult.IsValid)
                {
                    if (displayProgress)
                    {
                        interactionService.DisplaySubtleMessage(settingsAppHostRelativePath);
                    }

                    var appHostProject = new AppHostProjectCandidate(settingsAppHost, handler.LanguageId);
                    appHostProjects.Add(appHostProject);
                    await ReportCandidateFoundAsync(appHostProject, cancellationToken).ConfigureAwait(false);
                }
                else if (validationResult.IsPossiblyUnbuildable)
                {
                    if (displayProgress)
                    {
                        interactionService.DisplayMessage(KnownEmojis.Warning, string.Format(CultureInfo.CurrentCulture, ErrorStrings.ProjectFileMayBeUnbuildableAppHost, settingsAppHostRelativePath));
                    }

                    var appHostProject = new AppHostProjectCandidate(settingsAppHost, handler.LanguageId, AppHostProjectCandidateStatus.PossiblyUnbuildable);
                    unbuildableSuspectedAppHostProjects.Add(appHostProject);
                    await ReportCandidateFoundAsync(appHostProject, cancellationToken).ConfigureAwait(false);
                }
                else if (validationResult.IsUnsupported)
                {
                    if (displayProgress)
                    {
                        interactionService.DisplayMessage(KnownEmojis.Warning, string.Format(CultureInfo.CurrentCulture, ErrorStrings.ProjectFileUnsupportedInCurrentEnvironment, settingsAppHostRelativePath));
                    }

                    logger.LogDebug("Skipping unsupported configured AppHost project {SettingsAppHost}", settingsAppHost.FullName);
                    hasUnsupportedProjects = true;
                }
            }
        }

        if (displayProgress)
        {
            return await interactionService.ShowStatusAsync(InteractionServiceStrings.FindingAppHosts, FindAppHostsAsync);
        }

        return await FindAppHostsAsync();
    }

    /// <inheritdoc />
    public async Task<FileInfo?> GetAppHostFromSettingsAsync(CancellationToken cancellationToken = default)
    {
        return await GetAppHostFromSettingsAsync(executionContext.WorkingDirectory, searchParentDirectories: true, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<FileInfo?> GetAppHostFromSettingsAsync(DirectoryInfo searchDirectory, bool searchParentDirectories, CancellationToken cancellationToken = default)
    {
        // Intentionally does not call ValidateAppHostAsync. See interface XML docs for rationale.
        // Probe-style callers (DotNetSdkCheck, AspireVersionCheck, TypeScriptAppHostToolingCheck,
        // UpdateCommand, IntegrationPackageSearchService) drive this path and expect a
        // non-interactive answer; the user-facing legacy-migration warning is emitted from the
        // discovery walk (AddSettingsAppHostCandidateAsync) instead.
        var settingsAppHost = await GetAppHostProjectFileFromSettingsAsync(searchDirectory, searchParentDirectories, silent: true, cancellationToken);
        if (settingsAppHost is null)
        {
            return null;
        }

        var handler = projectFactory.TryGetProject(settingsAppHost);
        if (handler is null)
        {
            logger.LogWarning("Ignoring AppHost path '{AppHostPath}' from settings because no project handler can process it.", settingsAppHost.FullName);
            return null;
        }

        return settingsAppHost;
    }

    private async Task<FileInfo?> GetValidatedAppHostProjectFileFromSettingsAsync(DirectoryInfo searchDirectory, bool searchParentDirectories, CancellationToken cancellationToken)
    {
        // This is reached from UseOrFindAppHostProjectFileAsync. When the configured
        // legacy settings point at a missing file we still want the warning to surface,
        // but the discovery walk that runs afterwards (AddSettingsAppHostCandidateAsync)
        // will emit the same warning. Stay silent here to avoid a duplicate.
        var settingsAppHost = await GetAppHostProjectFileFromSettingsAsync(searchDirectory, searchParentDirectories, silent: true, cancellationToken);
        if (settingsAppHost is null)
        {
            return null;
        }

        var handler = projectFactory.TryGetProject(settingsAppHost);
        if (handler is null)
        {
            logger.LogWarning("Ignoring AppHost path '{AppHostPath}' from settings because no project handler can process it.", settingsAppHost.FullName);
            return null;
        }

        var validationResult = await handler.ValidateAppHostAsync(settingsAppHost, cancellationToken);
        if (validationResult.IsValid)
        {
            return settingsAppHost;
        }

        var messageSuffix = validationResult.Message is { Length: > 0 } message ? $": {message}" : string.Empty;
        if (validationResult.IsUnsupported)
        {
            logger.LogWarning("Ignoring AppHost path '{AppHostPath}' from settings because it is not supported in the current environment{MessageSuffix}.", settingsAppHost.FullName, messageSuffix);
        }
        else if (validationResult.IsPossiblyUnbuildable)
        {
            logger.LogWarning("Ignoring AppHost path '{AppHostPath}' from settings because it may not be a buildable AppHost project{MessageSuffix}.", settingsAppHost.FullName, messageSuffix);
        }
        else
        {
            logger.LogWarning("Ignoring AppHost path '{AppHostPath}' from settings because it is no longer a valid AppHost project{MessageSuffix}.", settingsAppHost.FullName, messageSuffix);
        }

        return null;
    }

    private async Task<FileInfo?> GetAppHostProjectFileFromSettingsAsync(DirectoryInfo searchDirectory, bool searchParentDirectories, bool silent, CancellationToken cancellationToken)
    {
        while (true)
        {
            // Check aspire.config.json first
            AspireConfigFile? aspireConfig;
            try
            {
                aspireConfig = AspireConfigFile.Load(searchDirectory.FullName);
            }
            catch (JsonException ex)
            {
                ReportInvalidConfigurationFile(ex, ex.Message, silent);
                return null;
            }

            if (aspireConfig?.AppHost?.Path is { } configAppHostPath)
            {
                var configFilePath = Path.Combine(searchDirectory.FullName, AspireConfigFile.FileName);

                // Validate before Path.Combine / new FileInfo, which throw ArgumentException
                // ("Null character in path." / "Illegal characters in path.") on NUL bytes and
                // other invalid characters that survive JSON parsing. Without this we surface
                // as a generic "An unexpected error occurred" — see
                // https://github.com/microsoft/aspire/issues/17624.
                if (!IsValidConfiguredAppHostPath(configAppHostPath, configFilePath, fieldName: AspireConfigAppHostPathKey, silent: silent))
                {
                    return null;
                }

                var qualifiedPath = Path.IsPathRooted(configAppHostPath)
                    ? configAppHostPath
                    : Path.Combine(searchDirectory.FullName, configAppHostPath);
                qualifiedPath = PathNormalizer.NormalizePathForCurrentPlatform(qualifiedPath);
                var appHostFile = new FileInfo(qualifiedPath);

                if (appHostFile.Exists)
                {
                    logger.LogInformation("Found AppHost path '{AppHostPath}' from config file in {Directory}", configAppHostPath, searchDirectory.FullName);
                    return appHostFile;
                }
                else
                {
                    if (!silent)
                    {
                        interactionService.DisplayMessage(KnownEmojis.Warning, string.Format(CultureInfo.CurrentCulture, ErrorStrings.AppHostWasSpecifiedButDoesntExist, configFilePath, qualifiedPath));
                    }
                    return null;
                }
            }

            // TODO: Remove legacy .aspire/settings.json fallback once confident most users have migrated.
            // Tracked by https://github.com/microsoft/aspire/issues/15239
            // Fall back to .aspire/settings.json
            var settingsFile = new FileInfo(ConfigurationHelper.BuildPathToSettingsJsonFile(searchDirectory.FullName));

            if (settingsFile.Exists)
            {
                try
                {
                    using var stream = settingsFile.OpenRead();
                    using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

                    if (json.RootElement.ValueKind is not JsonValueKind.Object)
                    {
                        ReportInvalidConfigurationFileShape(settingsFile.FullName, silent);
                        return null;
                    }

                    if (json.RootElement.TryGetProperty(LegacySettingsAppHostPathKey, out var appHostPathProperty))
                    {
                        if (appHostPathProperty.ValueKind is not JsonValueKind.Null and not JsonValueKind.String)
                        {
                            ReportInvalidConfiguredAppHostPathType(settingsFile.FullName, LegacySettingsAppHostPathKey, silent);
                            return null;
                        }

                        if (appHostPathProperty.GetString() is { } appHostPath)
                        {
                            // Mirror the validation on the modern path above so the legacy branch also
                            // cannot reach Path.Combine with a NUL byte or other Path.GetInvalidPathChars
                            // value (https://github.com/microsoft/aspire/issues/17624).
                            if (!IsValidConfiguredAppHostPath(appHostPath, settingsFile.FullName, fieldName: LegacySettingsAppHostPathKey, silent: silent))
                            {
                                return null;
                            }

                            var qualifiedAppHostPath = Path.IsPathRooted(appHostPath) ? appHostPath : Path.Combine(settingsFile.Directory!.FullName, appHostPath);
                            qualifiedAppHostPath = PathNormalizer.NormalizePathForCurrentPlatform(qualifiedAppHostPath);
                            var appHostFile = new FileInfo(qualifiedAppHostPath);

                            if (appHostFile.Exists)
                            {
                                return appHostFile;
                            }
                            else
                            {
                                if (!silent)
                                {
                                    // Warn against the user-authored file (.aspire/settings.json), not the
                                    // never-authored aspire.config.json. Earlier versions reported
                                    // aspire.config.json because startup eagerly migrated the legacy
                                    // settings (PR #17234); see https://github.com/microsoft/aspire/issues/17620
                                    // for the user-facing impact of pointing users at a file they did
                                    // not create.
                                    interactionService.DisplayMessage(KnownEmojis.Warning, string.Format(CultureInfo.CurrentCulture, ErrorStrings.AppHostWasSpecifiedButDoesntExist, settingsFile.FullName, qualifiedAppHostPath));
                                }
                                return null;
                            }
                        }
                    }
                }
                catch (JsonException ex)
                {
                    var message = string.Format(CultureInfo.CurrentCulture, ErrorStrings.InvalidJsonInConfigFile, settingsFile.FullName, ex.Message);
                    ReportInvalidConfigurationFile(ex, message, silent);
                    return null;
                }
            }

            if (searchParentDirectories && searchDirectory.Parent is not null)
            {
                searchDirectory = searchDirectory.Parent;
            }
            else
            {
                return null;
            }
        }
    }

    private void ReportInvalidConfigurationFileShape(string configFilePath, bool silent)
    {
        var message = string.Format(CultureInfo.CurrentCulture, ErrorStrings.ConfigurationFileMustBeJsonObject, configFilePath);
        if (!silent)
        {
            interactionService.DisplayError(message);
        }
        else
        {
            logger.LogWarning("Ignoring AppHost settings in '{ConfigFilePath}' because the configuration root is not a JSON object.", configFilePath);
        }
    }

    private void ReportInvalidConfiguredAppHostPathType(string configFilePath, string fieldName, bool silent)
    {
        var message = string.Format(CultureInfo.CurrentCulture, ErrorStrings.ConfiguredAppHostPathMustBeString, configFilePath, fieldName);
        if (!silent)
        {
            interactionService.DisplayError(message);
        }
        else
        {
            logger.LogWarning("Ignoring configured AppHost path in '{ConfigFilePath}' ('{FieldName}') because it is not a JSON string.", configFilePath, fieldName);
        }
    }

    private void ReportInvalidConfigurationFile(JsonException ex, string message, bool silent)
    {
        if (!silent)
        {
            interactionService.DisplayError(message);
        }
        else
        {
            logger.LogWarning(ex, "Unable to load AppHost settings: {Message}", message);
        }
    }

    // Reject empty paths (Path.Combine("", base) collapses to the base directory and surfaces
    // a misleading "directory doesn't exist" warning downstream) and paths that contain
    // characters that would crash System.IO APIs. Path.GetInvalidPathChars() includes NUL on
    // every platform plus the platform-specific set of disallowed characters (e.g. < > | on
    // Windows). Plain Contains('\0') is included explicitly for readability even though it is
    // redundant with the IndexOfAny check.
    private bool IsValidConfiguredAppHostPath(string path, string configFilePath, string fieldName, bool silent)
    {
        if (path.Length == 0 || path.Contains('\0') || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            if (!silent)
            {
                interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, ErrorStrings.ConfiguredAppHostPathHasInvalidCharacters, configFilePath, fieldName));
            }
            else
            {
                logger.LogWarning("Ignoring configured AppHost path in '{ConfigFilePath}' ('{FieldName}') because it is empty or contains invalid characters.", configFilePath, fieldName);
            }
            return false;
        }

        return true;
    }

    public Task<AppHostProjectSearchResult> UseOrFindAppHostProjectFileAsync(FileInfo? projectFile, MultipleAppHostProjectsFoundBehavior multipleAppHostProjectsFoundBehavior, bool createSettingsFile, CancellationToken cancellationToken = default)
    {
        return UseOrFindAppHostProjectFileAsync(projectFile, multipleAppHostProjectsFoundBehavior, createSettingsFile, displayProgress: true, cancellationToken);
    }

    public async Task<AppHostProjectSearchResult> UseOrFindAppHostProjectFileAsync(FileInfo? projectFile, MultipleAppHostProjectsFoundBehavior multipleAppHostProjectsFoundBehavior, bool createSettingsFile, bool displayProgress, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Finding project file in {CurrentDirectory}", executionContext.WorkingDirectory);

        if (projectFile is not null)
        {
            // Check if the provided path is actually a directory
            if (Directory.Exists(projectFile.FullName))
            {
                logger.LogDebug("Provided path {Path} is a directory, searching for project files recursively", projectFile.FullName);
                var directory = new DirectoryInfo(projectFile.FullName);

                // The user explicitly pointed at this directory, so don't let gitignore
                // hide AppHosts under it. Still apply the built-in junk-directory skip
                // list for dependency/build-output folders.
                var searchResults = await FindAppHostProjectFilesAsync(
                    directory,
                    stopAfterMultipleBuildableAppHosts: multipleAppHostProjectsFoundBehavior is MultipleAppHostProjectsFoundBehavior.Throw,
                    displayProgress: displayProgress,
                    scope: AppHostDiscoveryScope.ExplicitDirectory,
                    maxDepth: null,
                    cancellationToken: cancellationToken);
                var appHostProjects = searchResults.BuildableAppHost.Select(c => c.AppHostFile).ToList();

                if (displayProgress)
                {
                    interactionService.DisplayEmptyLine();
                }

                if (appHostProjects.Count == 0)
                {
                    if (searchResults.HasUnsupportedProjects)
                    {
                        throw new ProjectLocatorException(ErrorStrings.NoProjectFileFound, ProjectLocatorFailureReason.UnsupportedProjects);
                    }

                    logger.LogError("No AppHost project files found in directory {Directory}", directory.FullName);
                    throw new ProjectLocatorException(ErrorStrings.ProjectFileDoesntExist, ProjectLocatorFailureReason.ProjectFileDoesntExist);
                }
                else if (appHostProjects.Count == 1)
                {
                    logger.LogDebug("Found single AppHost project file {ProjectFile} in directory {Directory}", appHostProjects[0].FullName, directory.FullName);
                    projectFile = appHostProjects[0];
                }
                else
                {
                    if (multipleAppHostProjectsFoundBehavior is MultipleAppHostProjectsFoundBehavior.Prompt)
                    {
                        logger.LogDebug("Multiple AppHost project files found in directory {Directory}, prompting user to select", directory.FullName);
                        projectFile = await interactionService.PromptForSelectionAsync(
                            InteractionServiceStrings.SelectAppHostToUse,
                            appHostProjects,
                            file => $"{file.Name.EscapeMarkup()} ({Path.GetRelativePath(executionContext.WorkingDirectory.FullName, file.FullName).EscapeMarkup()})",
                            cancellationToken: cancellationToken
                        );
                    }
                    else if (multipleAppHostProjectsFoundBehavior is MultipleAppHostProjectsFoundBehavior.None)
                    {
                        logger.LogDebug("Multiple AppHost project files found in directory {Directory}, selecting none", directory.FullName);
                        projectFile = null;
                    }
                    else if (multipleAppHostProjectsFoundBehavior is MultipleAppHostProjectsFoundBehavior.Throw)
                    {
                        logger.LogError("Multiple AppHost project files found in directory {Directory}, throwing exception", directory.FullName);
                        throw new ProjectLocatorException(ErrorStrings.MultipleProjectFilesFound, ProjectLocatorFailureReason.MultipleProjectFilesFound);
                    }
                }
            }
            else if (File.Exists(projectFile.FullName))
            {
                // A project file was directly specified.
                //
                // Resolve to the filesystem-canonical path so the path used for backchannel socket
                // hash computation matches.
                var resolvedProjectPath = PathNormalizer.ResolveToFilesystemPath(projectFile.FullName);

                if (!string.Equals(resolvedProjectPath, projectFile.FullName, StringComparison.Ordinal))
                {
                    logger.LogDebug(
                        "Canonicalized explicit AppHost path from '{OriginalPath}' to '{ResolvedPath}'.",
                        projectFile.FullName,
                        resolvedProjectPath);

                    projectFile = new FileInfo(resolvedProjectPath);
                }
            }

            if (projectFile is not null)
            {
                // If the project file is passed, validate it.
                if (!projectFile.Exists)
                {
                    logger.LogError("Project file {ProjectFile} does not exist.", projectFile.FullName);
                    throw new ProjectLocatorException(ErrorStrings.ProjectFileDoesntExist, ProjectLocatorFailureReason.ProjectFileDoesntExist);
                }

                // Check if any handler can handle this file
                var handler = projectFactory.TryGetProject(projectFile);
                if (handler is not null)
                {
                    // The handler still may have matched an invalid single file apphost, so validate it before accepting as the selected project file
                    var validationResult = await handler.ValidateAppHostAsync(projectFile, cancellationToken);
                    if (validationResult.IsValid)
                    {
                        logger.LogDebug("Using {Language} apphost {ProjectFile}", handler.DisplayName, projectFile.FullName);
                        if (createSettingsFile)
                        {
                            await CreateSettingsFileAsync(projectFile, cancellationToken);
                        }

                        return new AppHostProjectSearchResult(projectFile, [projectFile]);
                    }
                }

                // If no handler matched, for .cs files check if we should search the parent directory
                if (projectFile.Name.Equals("apphost.cs", StringComparison.OrdinalIgnoreCase) && projectFile.Directory is { } parentDirectory)
                {
                    // File exists but is not a valid single-file apphost. Search in the parent directory.
                    // Propagate displayProgress so callers that opted out of progress UI (e.g. the hidden
                    // `extension get-apphosts` flow) do not start emitting progress on this fallback path.
                    return await UseOrFindAppHostProjectFileAsync(new FileInfo(parentDirectory.FullName), multipleAppHostProjectsFoundBehavior, createSettingsFile, displayProgress, cancellationToken);
                }

                // No handler can process this file
                throw new ProjectLocatorException(ErrorStrings.ProjectFileDoesntExist, ProjectLocatorFailureReason.ProjectFileDoesntExist);
            }
        }

        var settingsAppHost = await GetValidatedAppHostProjectFileFromSettingsAsync(executionContext.WorkingDirectory, searchParentDirectories: true, cancellationToken);

        if (settingsAppHost is not null && multipleAppHostProjectsFoundBehavior is not MultipleAppHostProjectsFoundBehavior.None)
        {
            logger.LogDebug("Using AppHost path from settings without scanning: {AppHost}", settingsAppHost.FullName);

            if (createSettingsFile)
            {
                await CreateSettingsFileAsync(settingsAppHost, cancellationToken);
            }

            return new AppHostProjectSearchResult(settingsAppHost, [settingsAppHost]);
        }

        logger.LogDebug("No project file specified, searching for apphost projects in {CurrentDirectory}", executionContext.WorkingDirectory);
        // No --project was provided; this is ambient discovery from the working
        // directory, so use git-aware/default filters.
        var results = await FindAppHostProjectFilesAsync(
            executionContext.WorkingDirectory,
            stopAfterMultipleBuildableAppHosts: multipleAppHostProjectsFoundBehavior is MultipleAppHostProjectsFoundBehavior.Throw && settingsAppHost is null,
            displayProgress: displayProgress,
            scope: AppHostDiscoveryScope.DefaultFiltered,
            maxDepth: null,
            cancellationToken: cancellationToken);

        logger.LogDebug("Found {ProjectFileCount} project files.", results.BuildableAppHost.Count);

        FileInfo? selectedAppHost = null;

        if (results.BuildableAppHost.Count == 0 && results.UnbuildableSuspectedAppHostProjects.Count == 0)
        {
            if (settingsAppHost is not null)
            {
                selectedAppHost = settingsAppHost;
            }
            else if (results.HasUnsupportedProjects)
            {
                throw new ProjectLocatorException(ErrorStrings.NoProjectFileFound, ProjectLocatorFailureReason.UnsupportedProjects);
            }
            else
            {
                throw new ProjectLocatorException(ErrorStrings.NoProjectFileFound, ProjectLocatorFailureReason.NoProjectFileFound);
            }
        }
        else if (results.BuildableAppHost.Count == 0 && results.UnbuildableSuspectedAppHostProjects.Count > 0)
        {
            if (settingsAppHost is not null)
            {
                selectedAppHost = settingsAppHost;
            }
            else
            {
                throw new ProjectLocatorException(ErrorStrings.AppHostsMayNotBeBuildable, ProjectLocatorFailureReason.AppHostsMayNotBeBuildable);
            }
        }
        else if (results.BuildableAppHost.Count == 1)
        {
            selectedAppHost = settingsAppHost ?? results.BuildableAppHost[0].AppHostFile;
        }
        else if (results.BuildableAppHost.Count > 1)
        {
            // Check if a previously-selected apphost is cached in settings and
            // is still among the discovered candidates. If so, reuse it to avoid
            // prompting the user every time when nothing has changed.
            var pathComparison = environment.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            if (settingsAppHost is not null
                && results.BuildableAppHost.Any(c => string.Equals(c.AppHostFile.FullName, settingsAppHost.FullName, pathComparison)))
            {
                logger.LogDebug("Using previously-selected AppHost from settings: {AppHost}", settingsAppHost.FullName);
                selectedAppHost = settingsAppHost;
            }
            else
            {
                // No valid cached selection — prompt or error based on interactivity.
                selectedAppHost = multipleAppHostProjectsFoundBehavior switch
                {
                    MultipleAppHostProjectsFoundBehavior.Throw => throw new ProjectLocatorException(ErrorStrings.MultipleProjectFilesFound, ProjectLocatorFailureReason.MultipleProjectFilesFound),
                    MultipleAppHostProjectsFoundBehavior.Prompt => await interactionService.PromptForSelectionAsync(InteractionServiceStrings.SelectAppHostToUse, results.BuildableAppHost.Select(c => c.AppHostFile).ToList(), projectFile => $"{projectFile.Name.EscapeMarkup()} ({Path.GetRelativePath(executionContext.WorkingDirectory.FullName, projectFile.FullName).EscapeMarkup()})", cancellationToken: cancellationToken),
                    MultipleAppHostProjectsFoundBehavior.None => null,
                    _ => selectedAppHost
                };
            }
        }

        if (createSettingsFile)
        {
            await CreateSettingsFileAsync(selectedAppHost!, cancellationToken);
        }

        // Ensure the selected AppHost is always represented in the candidate list so callers
        // can rely on SelectedProjectFile being present in AllProjectFileCandidates. This
        // covers cases where the configured settings AppHost is selected but lives outside
        // the discovered candidate set (e.g. parent directory or excluded by enumeration).
        var allCandidates = results.BuildableAppHost.Select(c => c.AppHostFile).ToList();
        if (selectedAppHost is not null
            && !allCandidates.Any(f => string.Equals(f.FullName, selectedAppHost.FullName, environment.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
        {
            allCandidates = [.. allCandidates, selectedAppHost];
        }

        return new AppHostProjectSearchResult(selectedAppHost, allCandidates);
    }

    public async Task<FileInfo?> UseOrFindAppHostProjectFileAsync(FileInfo? projectFile, bool createSettingsFile, CancellationToken cancellationToken = default)
    {
        var result = await UseOrFindAppHostProjectFileAsync(projectFile, MultipleAppHostProjectsFoundBehavior.Prompt, createSettingsFile, cancellationToken);
        return result.SelectedProjectFile;
    }

    private async Task CreateSettingsFileAsync(FileInfo projectFile, CancellationToken cancellationToken)
    {
        // Checked here rather than at each call site so every command that resolves an AppHost
        // (run, publish, deploy, do, add, update) honors it. A VS Code launch configuration can
        // name any of those commands, and each one previously rewrote aspire.config.json to the
        // launched AppHost, so switching between per-AppHost launch configurations kept clobbering
        // the workspace default. See https://github.com/microsoft/aspire/issues/19080.
        var selectionOrigin = configuration[KnownConfigNames.CliAppHostSelectionOrigin];
        var isSessionScopedSelection = selectionOrigin is not null && s_sessionScopedSelectionOrigins.Contains(selectionOrigin);

        // Everything below reads the workspace config, can migrate a legacy layout onto disk,
        // decides whether the workspace already has a default to preserve, and then rewrites the
        // file -- a check-then-act plus a whole-file write. Two CLI processes are not hypothetical
        // here: a VS Code compound launch configuration starts every AppHost it lists at the same
        // moment (https://code.visualstudio.com/docs/debugtest/debugging#_compound-launch-configurations),
        // so without serialization both can observe "no default recorded" and both establish one,
        // and one whole-file write can land on top of the other's.
        using var configLock = await TryAcquireWorkspaceConfigLockAsync(projectFile, cancellationToken);

        if (ResolveWorkspaceConfigTarget(projectFile) is not { } configTarget)
        {
            // The workspace already records this AppHost, so there is nothing to write.
            return;
        }

        var settingsFile = configTarget.SettingsFile;
        var fileExisted = settingsFile.Exists;

        // A session-scoped selection names a target for one invocation; it is not a statement about
        // which AppHost the workspace defaults to. It may still establish the default when there is
        // nothing to preserve, so a single-AppHost repo keeps getting a config file from its first
        // launch, but it must never replace a default the user already has. The read happens under
        // the config lock taken above, so a launch that starts alongside the one establishing the
        // default observes that write rather than racing it.
        //
        // The recorded default comes from the same directory-scoped reader the SDK version
        // inheritance below uses, which covers both key spellings, both file layouts
        // (aspire.config.json and the legacy .aspire/settings.json) and, when the workspace records
        // nothing, the global settings that already take part in resolving the AppHost
        // (ConfigurationHelper.RegisterSettingsFiles). Only its presence matters: resolving the path
        // would mean calling Path.GetFullPath without the IsValidConfiguredAppHostPath guard the
        // canonical readers apply, which throws on NUL bytes that survive JSON parsing
        // (https://github.com/microsoft/aspire/issues/17624), and a recorded path has to count even
        // when the file it names is missing, because a branch switch or a sparse checkout is
        // indistinguishable from a deletion and would otherwise let the next launch permanently
        // re-point the default. The cost is that a stale default is healed only by a selection the
        // user actually made, from any other origin or `aspire config set`.
        if (isSessionScopedSelection)
        {
            var configRootDirectory = configTarget.ConfigRootDirectory;
            var recordedDefault = await configurationService.GetConfigurationFromDirectoryAsync(AspireConfigAppHostPathKey, configRootDirectory, cancellationToken: cancellationToken)
                ?? await configurationService.GetConfigurationFromDirectoryAsync(LegacySettingsAppHostPathKey, configRootDirectory, cancellationToken: cancellationToken);

            if (!string.IsNullOrEmpty(recordedDefault))
            {
                logger.LogDebug(
                    "Not replacing recorded AppHost default {RecordedAppHost} in {ConfigDirectory} with {AppHost} because the latter was selected by {SelectionOrigin}.",
                    recordedDefault,
                    configTarget.ConfigRootPath,
                    projectFile.FullName,
                    selectionOrigin);
                return;
            }
        }

        logger.LogDebug("Creating settings file at {SettingsFilePath}", settingsFile.FullName);

        var relativePathToProjectFile = Path.GetRelativePath(configTarget.ConfigRootPath, projectFile.FullName).Replace(Path.DirectorySeparatorChar, '/');

        // Use the configuration writer to set the AppHost path, which will merge with any existing settings.
        await ConfigurationService.SetConfigurationInFileAsync(settingsFile.FullName, AspireConfigAppHostPathKey, relativePathToProjectFile, cancellationToken);

        // For polyglot projects, also set language and inherit SDK version from parent/global config.
        var language = languageDiscovery.GetLanguageByFile(projectFile);
        if (language is not null && !language.LanguageId.Value.Equals(KnownLanguageId.CSharp, StringComparison.OrdinalIgnoreCase))
        {
            await ConfigurationService.SetConfigurationInFileAsync(settingsFile.FullName, "appHost.language", language.LanguageId.Value, cancellationToken);

            // Inherit SDK version from parent/global config if available.
            var inheritedSdkVersion = configTarget.AppHostDirectoryForScopedConfig is { } appHostDirForScopedConfig
                ? await configurationService.GetConfigurationFromDirectoryAsync("sdk.version", appHostDirForScopedConfig, continueSearchWhenKeyMissing: true, cancellationToken: cancellationToken)
                    ?? await configurationService.GetConfigurationFromDirectoryAsync("sdkVersion", appHostDirForScopedConfig, continueSearchWhenKeyMissing: true, cancellationToken: cancellationToken)
                : await configurationService.GetConfigurationAsync("sdk.version", cancellationToken)
                    ?? await configurationService.GetConfigurationAsync("sdkVersion", cancellationToken);

            if (!string.IsNullOrEmpty(inheritedSdkVersion))
            {
                await ConfigurationService.SetConfigurationInFileAsync(settingsFile.FullName, "sdk.version", inheritedSdkVersion, cancellationToken);
                logger.LogDebug("Set SDK version {Version} in settings file (inherited from parent config)", inheritedSdkVersion);
            }
        }

        var relativeSettingsFilePath = Path.GetRelativePath(executionContext.WorkingDirectory.FullName, settingsFile.FullName).Replace(Path.DirectorySeparatorChar, '/');
        var message = fileExisted ? InteractionServiceStrings.UpdatedSettingsFile : InteractionServiceStrings.CreatedSettingsFile;
        interactionService.DisplayMessage(KnownEmojis.FloppyDisk, string.Format(CultureInfo.CurrentCulture, message, $"[bold]'{relativeSettingsFilePath.EscapeMarkup()}'[/]"), allowMarkup: true);
    }

    /// <summary>
    /// Resolves the config file the selected AppHost should be recorded in, or
    /// <see langword="null"/> when that config already records <paramref name="projectFile"/> and
    /// there is nothing to write.
    /// </summary>
    /// <remarks>
    /// Call it only while the workspace config lock is held: a legacy layout is migrated onto disk
    /// as a side effect of being loaded here.
    /// </remarks>
    private WorkspaceConfigTarget? ResolveWorkspaceConfigTarget(FileInfo projectFile)
    {
        // Search from the apphost's directory upward for an existing config file.
        // This handles the case where "aspire new" created a project in a subdirectory
        // and the user runs "aspire run" from the parent without cd-ing first.
        if (projectFile.Directory is { } appHostDir && ConfigurationHelper.FindNearestConfigFilePath(appHostDir) is { } nearAppHost)
        {
            var configDir = Path.GetDirectoryName(nearAppHost)!;
            var targetSettingsFilePath = nearAppHost;
            AspireConfigFile? existingConfig;

            // For legacy .aspire/settings.json, the config root is the parent of .aspire/
            var trimmedConfigDir = configDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(Path.GetFileName(trimmedConfigDir), ".aspire", StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.GetParent(trimmedConfigDir) is { } parentDir)
                {
                    configDir = parentDir.FullName;
                }

                // Rebase onto aspire.config.json so the loaded config and the file that gets written
                // are the same file. LoadOrCreate persists the migration as a side effect, which is
                // why the two must be decided together here.
                targetSettingsFilePath = Path.Combine(configDir, AspireConfigFile.FileName);
                existingConfig = AspireConfigFile.LoadOrCreate(configDir);
            }
            else
            {
                existingConfig = AspireConfigFile.Load(configDir);
            }

            if (existingConfig?.AppHost?.Path is { } existingPath)
            {
                // Resolve the stored path relative to the config file's directory.
                var resolvedPath = Path.GetFullPath(
                    Path.IsPathRooted(existingPath) ? existingPath : Path.Combine(configDir, existingPath));

                // Only skip creation if the config already points to the discovered apphost.
                // If the path is stale/invalid, fall through so the config gets healed.
                if (string.Equals(resolvedPath, projectFile.FullName, StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogDebug(
                        "Config at {Path} already references apphost {AppHost}, skipping creation",
                        nearAppHost, projectFile.FullName);
                    return null;
                }
            }

            return new WorkspaceConfigTarget(new FileInfo(targetSettingsFilePath), appHostDir);
        }

        // Only use the working-directory config after checking the selected AppHost's tree.
        // GetOrCreateLocalAspireConfigFile can migrate legacy .aspire/settings.json into
        // aspire.config.json, so calling it earlier would recreate the split-config bug.
        return new WorkspaceConfigTarget(GetOrCreateLocalAspireConfigFile(), AppHostDirectoryForScopedConfig: null);
    }

    /// <summary>
    /// Acquires the cross-process lock that serializes reading, deciding and rewriting the workspace
    /// config for <paramref name="projectFile"/>, or returns <see langword="null"/> when the lock
    /// could not be taken.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="FileLock"/> is the CLI's existing cross-process primitive (see
    /// <c>BundleService</c> and <c>PrebuiltAppHostServer</c>): a file opened with
    /// <see cref="FileShare.None"/> and <see cref="FileOptions.DeleteOnClose"/>. Exclusion is
    /// enforced by the OS on every platform we ship -- a share-mode check on Windows, an advisory
    /// <c>flock(2)</c> on Linux and macOS -- and in both cases the OS drops it when the holding
    /// process exits, however it exits. There is therefore no stale lock to recover from: a crashed
    /// or killed CLI cannot block the next launch, and the worst it can leave behind is a zero-byte
    /// file in the cache directory that the next holder simply reopens.
    /// </para>
    /// <para>
    /// Recording the workspace default is bookkeeping around the command the user actually asked
    /// for, so failing to lock must not fail that command. Environments where the cache directory is
    /// unwritable, or which sit on a file system without working advisory locks, fall back to the
    /// unsynchronized behavior that shipped before this lock existed.
    /// </para>
    /// </remarks>
    private async Task<FileLock?> TryAcquireWorkspaceConfigLockAsync(FileInfo projectFile, CancellationToken cancellationToken)
    {
        var lockPath = GetWorkspaceConfigLockPath(projectFile);

        try
        {
            return await FileLock.AcquireAsync(lockPath, cancellationToken, s_workspaceConfigLockTimeout);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Proceeding without the workspace config lock at {LockPath}.", lockPath);
            return null;
        }
    }

    /// <summary>
    /// Returns the lock file path that identifies the workspace config
    /// <paramref name="projectFile"/> would be recorded in.
    /// </summary>
    private string GetWorkspaceConfigLockPath(FileInfo projectFile)
    {
        // Key the lock on the config root the write will land in, so two AppHosts that share a
        // config file serialize while two that do not never block each other. This mirrors the
        // search order in ResolveWorkspaceConfigTarget: the AppHost's own tree wins, and the working
        // directory is consulted only when that tree has no config. It is deliberately duplicated
        // rather than derived from the resolved target, because resolution has to run inside the
        // lock -- it is what performs the legacy migration write. Both helpers used here only read,
        // so computing the key cannot itself race.
        var configRoot = projectFile.Directory is { } appHostDirectory && ConfigurationHelper.FindNearestConfigFilePath(appHostDirectory) is not null
            ? ConfigurationHelper.GetConfigRootDirectory(appHostDirectory)
            : ConfigurationHelper.GetConfigRootDirectory(executionContext.WorkingDirectory);

        // Two processes only exclude each other when they derive the same file name, so fold away
        // the spellings that name one directory. Symlinks first: macOS resolves /tmp to
        // /private/tmp, and checkouts are routinely reached through links.
        var normalizedRoot = PathNormalizer.ResolveSymlinks(configRoot.FullName);

        // The lock lives in the CLI's cache directory rather than in the workspace. The workspace
        // can be read-only, and dropping even a transient file into it would show up in git status
        // the way an eagerly written config file once did
        // (https://github.com/microsoft/aspire/issues/17615). CacheDirectory is derived from the
        // user profile rather than the working directory, so every CLI process on the machine
        // computes the same path for a given config root.
        return Path.Combine(executionContext.CacheDirectory.FullName, "workspace-config-locks", GetWorkspaceConfigLockFileName(normalizedRoot));
    }

    /// <summary>
    /// Returns the lock file name that identifies the workspace config rooted at
    /// <paramref name="configRootPath"/>.
    /// </summary>
    internal static string GetWorkspaceConfigLockFileName(string configRootPath)
    {
        // Case-fold on every platform rather than only on Windows. macOS ships a case-insensitive
        // volume by default, so two launches can spell one config root differently and still land on
        // the same aspire.config.json, and resolving symlinks canonicalizes links but not casing.
        //
        // Folding on a genuinely case-sensitive volume can only make two distinct roots share one
        // lock, which briefly over-serializes a critical section measured in milliseconds. Not
        // folding lets two processes that share a config file miss each other entirely, which is the
        // failure this lock exists to prevent, so it has to fail toward blocking. Probing the volume
        // for case sensitivity would be more precise, but it adds IO to every launch and could
        // answer differently in two processes, which is the one thing a lock key must never do.
        //
        // ToLowerInvariant rather than the current culture for the same reason: two CLI processes in
        // different locales must derive the same name.
        return $"{Convert.ToHexString(XxHash3.Hash(Encoding.UTF8.GetBytes(configRootPath.ToLowerInvariant()))).ToLowerInvariant()}.lock";
    }

    private FileInfo GetOrCreateLocalAspireConfigFile()
    {
        var settingsFile = new FileInfo(configurationService.GetSettingsFilePath(isGlobal: false));

        if (string.Equals(settingsFile.Name, AspireConfigFile.FileName, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug("Using existing config file at {Path}", settingsFile.FullName);
            return settingsFile;
        }

        var legacySettingsRootDirectory = ConfigurationHelper.GetLegacySettingsRootDirectory(settingsFile);
        if (legacySettingsRootDirectory is null)
        {
            var newConfigPath = Path.Combine(executionContext.WorkingDirectory.FullName, AspireConfigFile.FileName);
            logger.LogDebug("No existing config found, will create new config at {Path}", newConfigPath);
            return new FileInfo(newConfigPath);
        }

        var aspireConfigFile = new FileInfo(Path.Combine(legacySettingsRootDirectory.FullName, AspireConfigFile.FileName));
        if (!aspireConfigFile.Exists)
        {
            logger.LogInformation("Migrating legacy settings from {LegacyDir} to {ConfigFile}", legacySettingsRootDirectory.FullName, aspireConfigFile.FullName);
            MigrateLegacySettings(legacySettingsRootDirectory);
        }

        return aspireConfigFile;
    }

    private void MigrateLegacySettings(DirectoryInfo settingsRootDirectory)
    {
        var configFilePath = Path.Combine(settingsRootDirectory.FullName, AspireConfigFile.FileName);
        logger.LogInformation("Migrating legacy settings to {SettingsFilePath}", configFilePath);

        // LoadOrCreate handles the legacy fallback and migration internally,
        // including saving the migrated config to disk.
        _ = AspireConfigFile.LoadOrCreate(settingsRootDirectory.FullName);
    }

    private string? GetNuGetPackagesCachePath()
    {
        var envPath = environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrEmpty(envPath))
        {
            return Path.GetFullPath(envPath);
        }

        var userProfile = executionContext.HomeDirectory.FullName;
        if (!string.IsNullOrEmpty(userProfile))
        {
            return Path.GetFullPath(Path.Combine(userProfile, ".nuget", "packages"));
        }

        return null;
    }

    /// <summary>
    /// The workspace config file the selected AppHost will be recorded in.
    /// </summary>
    /// <param name="SettingsFile">The file that will be written.</param>
    /// <param name="AppHostDirectoryForScopedConfig">
    /// The AppHost directory to inherit scoped settings such as the SDK version from, or
    /// <see langword="null"/> when the target came from the working directory instead of the
    /// AppHost's own tree, in which case the ambient config search applies.
    /// </param>
    /// <remarks>
    /// These travel together because every correctness question in this area is about whether they
    /// still agree: whether the config that decided "the workspace already has a default" is the one
    /// about to be overwritten, and whether the directory a relative AppHost path is resolved against
    /// is the one that path will be stored in. Deriving the config root from
    /// <see cref="SettingsFile"/> rather than tracking it separately makes disagreement
    /// unrepresentable, including across the legacy migration that rebases the config root onto the
    /// parent of <c>.aspire/</c>.
    /// </remarks>
    private sealed record WorkspaceConfigTarget(FileInfo SettingsFile, DirectoryInfo? AppHostDirectoryForScopedConfig)
    {
        /// <summary>
        /// The directory <see cref="SettingsFile"/> lives in, which is also the directory the
        /// recorded default is read from and the one relative AppHost paths are stored relative to.
        /// </summary>
        public DirectoryInfo ConfigRootDirectory => SettingsFile.Directory!;

        /// <inheritdoc cref="ConfigRootDirectory"/>
        public string ConfigRootPath => ConfigRootDirectory.FullName;
    }
}

internal class ProjectLocatorException(string message, ProjectLocatorFailureReason failureReason) : System.Exception(message)
{
    public ProjectLocatorFailureReason FailureReason { get; } = failureReason;
}

internal static class ProjectLocatorErrorHelper
{
    public static (int ExitCode, string ErrorMessage) GetExitCodeAndMessage(ProjectLocatorException ex, bool projectOptionSpecifiedAsDirectory = false)
    {
        ArgumentNullException.ThrowIfNull(ex);

        return ex.FailureReason switch
        {
            ProjectLocatorFailureReason.MultipleProjectFilesFound when projectOptionSpecifiedAsDirectory
                => (CliExitCodes.FailedToFindProject, InteractionServiceStrings.ProjectOptionSpecifiedDirectoryContainsMultipleAppHosts),
            ProjectLocatorFailureReason.ProjectFileDoesntExist or ProjectLocatorFailureReason.NoProjectFileFound when projectOptionSpecifiedAsDirectory
                => (CliExitCodes.FailedToFindProject, InteractionServiceStrings.ProjectOptionSpecifiedDirectoryContainsNoAppHosts),
            ProjectLocatorFailureReason.UnsupportedProjects
                => (CliExitCodes.SdkNotInstalled, InteractionServiceStrings.NoSupportedAppHostsFound),
            ProjectLocatorFailureReason.ProjectFileNotAppHostProject
                => (CliExitCodes.FailedToFindProject, InteractionServiceStrings.SpecifiedProjectFileNotAppHostProject),
            ProjectLocatorFailureReason.ProjectFileDoesntExist
                => (CliExitCodes.FailedToFindProject, InteractionServiceStrings.ProjectOptionDoesntExist),
            ProjectLocatorFailureReason.MultipleProjectFilesFound
                => (CliExitCodes.FailedToFindProject, InteractionServiceStrings.ProjectOptionNotSpecifiedMultipleAppHostsFound),
            ProjectLocatorFailureReason.NoProjectFileFound
                => (CliExitCodes.FailedToFindProject, InteractionServiceStrings.ProjectOptionNotSpecifiedNoCsprojFound),
            ProjectLocatorFailureReason.AppHostsMayNotBeBuildable
                => (CliExitCodes.FailedToFindProject, InteractionServiceStrings.UnbuildableAppHostsDetected),
            _ => (CliExitCodes.FailedToFindProject, string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.UnexpectedErrorOccurred, ex.Message))
        };
    }
}

internal enum ProjectLocatorFailureReason
{
    ProjectFileDoesntExist,
    ProjectFileNotAppHostProject,
    MultipleProjectFilesFound,
    NoProjectFileFound,
    AppHostsMayNotBeBuildable,
    UnsupportedProjects,
}

internal record AppHostProjectSearchResult(FileInfo? SelectedProjectFile, List<FileInfo> AllProjectFileCandidates);

internal enum MultipleAppHostProjectsFoundBehavior
{
    Prompt,
    Throw,
    None
}
