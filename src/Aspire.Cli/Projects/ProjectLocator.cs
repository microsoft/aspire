// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Aspire.Cli.Configuration;
using Aspire.Cli.DotNet;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Aspire.Hosting.Utils;
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
    /// Streams candidate AppHost projects as validation completes.
    /// </summary>
    /// <param name="searchDirectory">The directory to search recursively.</param>
    /// <param name="scope">Controls which files are considered. See <see cref="AppHostDiscoveryScope"/>.</param>
    /// <param name="onDirectoryEnumerated">
    /// Optional callback invoked synchronously on the discovery thread with the running total of directories
    /// enumerated so callers can render progress before validation completes. See
    /// <see cref="IAppHostCandidateFinder.FindCandidateFilesAsync"/> for caller obligations.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An async stream of candidate AppHost projects in validation-completion order.</returns>
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
    IInteractionService interactionService,
    IConfigurationService configurationService,
    IAppHostProjectFactory projectFactory,
    ILanguageDiscovery languageDiscovery,
    IDotNetSdkInstaller sdkInstaller,
    IAppHostCandidateFinder appHostCandidateFinder,
    AspireCliTelemetry telemetry) : IProjectLocator
{
    private const string AspireConfigAppHostPathKey = "appHost.path";
    private const string LegacySettingsAppHostPathKey = "appHostPath";

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
        var allCandidates = await FindAppHostProjectFilesAsync(searchDirectory, stopAfterMultipleBuildableAppHosts: false, displayProgress: false, scope, maxDepth, cancellationToken);
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
            await FindAppHostProjectFilesAsync(searchDirectory, stopAfterMultipleBuildableAppHosts: false, displayProgress: false, scope, maxDepth: null, cancellationToken, candidateWriter, onDirectoryEnumerated).ConfigureAwait(false);
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

    private async Task<(List<AppHostProjectCandidate> BuildableAppHost, List<AppHostProjectCandidate> UnbuildableSuspectedAppHostProjects, bool HasUnsupportedProjects)> FindAppHostProjectFilesAsync(DirectoryInfo searchDirectory, bool stopAfterMultipleBuildableAppHosts, AppHostDiscoveryScope scope, CancellationToken cancellationToken)
    {
        return await FindAppHostProjectFilesAsync(searchDirectory, stopAfterMultipleBuildableAppHosts, displayProgress: true, scope, maxDepth: null, cancellationToken);
    }

    private async Task<(List<AppHostProjectCandidate> BuildableAppHost, List<AppHostProjectCandidate> UnbuildableSuspectedAppHostProjects, bool HasUnsupportedProjects)> FindAppHostProjectFilesAsync(DirectoryInfo searchDirectory, bool stopAfterMultipleBuildableAppHosts, bool displayProgress, AppHostDiscoveryScope scope, int? maxDepth, CancellationToken cancellationToken, ChannelWriter<AppHostProjectCandidate>? candidateWriter = null, Action<int>? onDirectoryEnumerated = null)
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
                var pathComparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
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

    public async Task<AppHostProjectSearchResult> UseOrFindAppHostProjectFileAsync(FileInfo? projectFile, MultipleAppHostProjectsFoundBehavior multipleAppHostProjectsFoundBehavior, bool createSettingsFile, CancellationToken cancellationToken = default)
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
                    AppHostDiscoveryScope.ExplicitDirectory,
                    cancellationToken);
                var appHostProjects = searchResults.BuildableAppHost.Select(c => c.AppHostFile).ToList();

                interactionService.DisplayEmptyLine();

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
                    // File exists but is not a valid single-file apphost. Search in the parent directory
                    return await UseOrFindAppHostProjectFileAsync(new FileInfo(parentDirectory.FullName), multipleAppHostProjectsFoundBehavior, createSettingsFile, cancellationToken);
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
            AppHostDiscoveryScope.DefaultFiltered,
            cancellationToken);

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
            var pathComparison = OperatingSystem.IsWindows()
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
            && !allCandidates.Any(f => string.Equals(f.FullName, selectedAppHost.FullName, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
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
        FileInfo? settingsFile = null;
        DirectoryInfo? appHostDirForScopedConfig = null;

        // Search from the apphost's directory upward for an existing config file.
        // This handles the case where "aspire new" created a project in a subdirectory
        // and the user runs "aspire run" from the parent without cd-ing first.
        if (projectFile.Directory is { } appHostDir)
        {
            var nearAppHost = ConfigurationHelper.FindNearestConfigFilePath(appHostDir);
            if (nearAppHost is not null)
            {
                var configDir = Path.GetDirectoryName(nearAppHost)!;
                var targetSettingsFilePath = nearAppHost;
                AspireConfigFile? existingConfig;

                // For legacy .aspire/settings.json, the config root is the parent of .aspire/
                var trimmedConfigDir = configDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(Path.GetFileName(trimmedConfigDir), ".aspire", StringComparison.OrdinalIgnoreCase))
                {
                    var parentDir = Directory.GetParent(trimmedConfigDir);
                    if (parentDir is not null)
                    {
                        configDir = parentDir.FullName;
                    }

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
                        return;
                    }
                }

                settingsFile = new FileInfo(targetSettingsFilePath);
                appHostDirForScopedConfig = appHostDir;
            }
        }

        // Only use the working-directory config after checking the selected AppHost's tree.
        // GetOrCreateLocalAspireConfigFile can migrate legacy .aspire/settings.json into
        // aspire.config.json, so calling it earlier would recreate the split-config bug.
        settingsFile ??= GetOrCreateLocalAspireConfigFile();
        var fileExisted = settingsFile.Exists;

        logger.LogDebug("Creating settings file at {SettingsFilePath}", settingsFile.FullName);

        var relativePathToProjectFile = Path.GetRelativePath(settingsFile.Directory!.FullName, projectFile.FullName).Replace(Path.DirectorySeparatorChar, '/');

        // Use the configuration writer to set the AppHost path, which will merge with any existing settings.
        await ConfigurationService.SetConfigurationInFileAsync(settingsFile.FullName, AspireConfigAppHostPathKey, relativePathToProjectFile, cancellationToken);

        // For polyglot projects, also set language and inherit SDK version from parent/global config.
        var language = languageDiscovery.GetLanguageByFile(projectFile);
        if (language is not null && !language.LanguageId.Value.Equals(KnownLanguageId.CSharp, StringComparison.OrdinalIgnoreCase))
        {
            await ConfigurationService.SetConfigurationInFileAsync(settingsFile.FullName, "appHost.language", language.LanguageId.Value, cancellationToken);

            // Inherit SDK version from parent/global config if available.
            var inheritedSdkVersion = appHostDirForScopedConfig is not null
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
        var envPath = executionContext.GetEnvironmentVariable("NUGET_PACKAGES");
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
