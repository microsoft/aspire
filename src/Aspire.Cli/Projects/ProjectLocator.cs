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
using Aspire.Shared;
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
    private const string ExplicitLaunchConfigurationSelectionOrigin = "explicit-launch-configuration";
    private const string ExplicitCliSelectionOrigin = "explicit-cli";
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

    private async Task<(List<AppHostProjectCandidate> BuildableAppHost, List<AppHostProjectCandidate> UnbuildableSuspectedAppHostProjects, List<FileInfo> UnsupportedProjects)> FindAppHostProjectFilesAsync(DirectoryInfo searchDirectory, bool stopAfterMultipleBuildableAppHosts, bool displayProgress, AppHostDiscoveryScope scope, int? maxDepth, ChannelWriter<AppHostProjectCandidate>? candidateWriter = null, Action<int>? onDirectoryEnumerated = null, CancellationToken cancellationToken = default)
    {
        using var activity = telemetry.StartDiagnosticActivity();

        async Task<(List<AppHostProjectCandidate> BuildableAppHost, List<AppHostProjectCandidate> UnbuildableSuspectedAppHostProjects, List<FileInfo> UnsupportedProjects)> FindAppHostsAsync()
        {
            var appHostProjects = new List<AppHostProjectCandidate>();
            var unbuildableSuspectedAppHostProjects = new List<AppHostProjectCandidate>();
            var unsupportedProjects = new List<FileInfo>();
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
                            unsupportedProjects.Add(candidateFile);
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

            // Explicit-directory callers asked to inspect only the named subtree. Importing an
            // AppHost from a parent aspire.config.json violates that boundary and can affect both
            // selection and shallow probes such as `aspire doctor`.
            if (scope is not AppHostDiscoveryScope.ExplicitDirectory)
            {
                await AddSettingsAppHostCandidateAsync().ConfigureAwait(false);
            }

            // This sort is done here to make results deterministic since we get all the app
            // host information in parallel and the order may vary.
            appHostProjects.Sort((x, y) => string.Compare(x.AppHostFile.FullName, y.AppHostFile.FullName, StringComparison.Ordinal));

            return (appHostProjects, unbuildableSuspectedAppHostProjects, unsupportedProjects);

            async Task AddSettingsAppHostCandidateAsync()
            {
                var settingsAppHost = await GetAppHostProjectFileFromSettingsAsync(searchDirectory, searchParentDirectories: true, silent: false, cancellationToken).ConfigureAwait(false);
                if (settingsAppHost is null)
                {
                    return;
                }

                // Canonicalize filesystem aliases before comparing so a settings-derived candidate
                // like /tmp/L5/x.cs does not produce a duplicate entry next to the
                // discovery-walked /private/tmp/L5/x.cs on macOS, where /tmp is a symlink
                // to /private/tmp. Filesystem casing is recovered as well so equivalent paths
                // converge on case-insensitive volumes without conflating paths on case-sensitive
                // APFS. See https://github.com/microsoft/aspire/issues/17626 and
                // https://github.com/microsoft/aspire/issues/17635.
                // Resolved paths are used as comparison keys only — the surfaced
                // AppHostProjectCandidate keeps the original FileInfo so display paths are
                // unchanged from what the user-authored settings file pointed at.
                //
                // Filesystem canonicalization performs IO per path segment, so we keep it
                // off the hot path: the exact-string compare below short-circuits before
                // the per-candidate resolve runs at all in the common case (no symlinks
                // involved). Pre-materializing canonical paths for every candidate would
                // force the resolve even when the cheap compare would have matched.
                var settingsCanonicalPath = PathNormalizer.ResolveToFilesystemPath(settingsAppHost.FullName);
                bool IsDuplicate(AppHostProjectCandidate candidate)
                {
                    if (string.Equals(candidate.AppHostFile.FullName, settingsAppHost.FullName, StringComparisons.FileSystemPath))
                    {
                        return true;
                    }

                    var candidateCanonicalPath = PathNormalizer.ResolveToFilesystemPath(candidate.AppHostFile.FullName);
                    return string.Equals(candidateCanonicalPath, settingsCanonicalPath, StringComparisons.FileSystemPath);
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
                    unsupportedProjects.Add(settingsAppHost);
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
                    unsupportedProjects.Add(settingsAppHost);
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

    /// <summary>
    /// The AppHost resolved from <c>aspire.config.json</c> (or migrated legacy settings), if any.
    /// </summary>
    /// <param name="AppHost">The configured AppHost, or <see langword="null"/> when none was usable.</param>
    /// <param name="IsUnverified">
    /// <see langword="true"/> when MSBuild could not evaluate the configured AppHost, so it could not be
    /// confirmed to be an AppHost. The selection is still honored, but it must never be persisted back to
    /// settings and callers are expected to surface the underlying build diagnostics.
    /// </param>
    private readonly record struct SettingsAppHostResult(FileInfo? AppHost, bool IsUnverified);

    /// <summary>
    /// Determines whether <paramref name="file"/> lives beneath <paramref name="directory"/>.
    /// </summary>
    /// <remarks>
    /// Windows paths are compared case-insensitively. Other platforms use case-sensitive comparison
    /// because macOS can use case-sensitive APFS volumes.
    /// </remarks>
    internal static bool IsUnderDirectory(FileInfo file, DirectoryInfo directory)
    {
        // Compare the raw paths first. The discovery walk can reach a candidate by descending through
        // a symlinked subdirectory, and canonicalizing that path would relocate it outside the
        // directory the user actually named.
        if (IsUnder(file.FullName, directory.FullName))
        {
            return true;
        }

        // Otherwise canonicalize both sides, because the same directory can be spelled two ways: on
        // macOS /tmp is a symlink to /private/tmp, so a candidate discovered as /private/tmp/x/App.csproj
        // would not textually start with /tmp/x. See https://github.com/microsoft/aspire/issues/17626.
        return IsUnder(
            PathNormalizer.ResolveToFilesystemPath(file.FullName),
            PathNormalizer.ResolveToFilesystemPath(directory.FullName));

        static bool IsUnder(string filePath, string directoryPath)
        {
            // The trailing separator keeps a sibling with a shared name prefix (".../Services2")
            // from matching ".../Services".
            var prefix = directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return filePath.StartsWith(prefix, StringComparisons.FileSystemPath);
        }
    }

    private async Task<SettingsAppHostResult> GetValidatedAppHostProjectFileFromSettingsAsync(DirectoryInfo searchDirectory, bool searchParentDirectories, CancellationToken cancellationToken)
    {
        // This is reached from UseOrFindAppHostProjectFileAsync. When the configured
        // legacy settings point at a missing file we still want the warning to surface,
        // but the discovery walk that runs afterwards (AddSettingsAppHostCandidateAsync)
        // will emit the same warning. Stay silent here to avoid a duplicate.
        var settingsAppHost = await GetAppHostProjectFileFromSettingsAsync(searchDirectory, searchParentDirectories, silent: true, cancellationToken);
        if (settingsAppHost is null)
        {
            return default;
        }

        var handler = projectFactory.TryGetProject(settingsAppHost);
        if (handler is null)
        {
            logger.LogWarning("Ignoring AppHost path '{AppHostPath}' from settings because no project handler can process it.", settingsAppHost.FullName);
            return default;
        }

        var validationResult = await handler.ValidateAppHostAsync(settingsAppHost, cancellationToken);
        if (validationResult.IsValid)
        {
            return new SettingsAppHostResult(settingsAppHost, IsUnverified: false);
        }

        var messageSuffix = validationResult.Message is { Length: > 0 } message ? $": {message}" : string.Empty;
        if (validationResult.IsUnsupported)
        {
            logger.LogWarning("Ignoring AppHost path '{AppHostPath}' from settings because it is not supported in the current environment{MessageSuffix}.", settingsAppHost.FullName, messageSuffix);
        }
        else if (validationResult.IsPossiblyUnbuildable)
        {
            // A configured AppHost is as deliberate a choice as --apphost, so keep it rather than
            // falling back to discovery. Discarding it reported "No AppHosts were found ..." for a path
            // the CLI had already resolved, and could silently run a different application that
            // discovery happened to find. See https://github.com/microsoft/aspire/issues/19035.
            logger.LogWarning("AppHost path '{AppHostPath}' from settings could not be evaluated by MSBuild and may not be buildable{MessageSuffix}.", settingsAppHost.FullName, messageSuffix);
            return new SettingsAppHostResult(settingsAppHost, IsUnverified: true);
        }
        else
        {
            logger.LogWarning("Ignoring AppHost path '{AppHostPath}' from settings because it is no longer a valid AppHost project{MessageSuffix}.", settingsAppHost.FullName, messageSuffix);
        }

        return default;
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
        var explicitSelectionWasPrompted = false;

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
                // Keep the requested directory as the selection boundary even if additional
                // explicit-discovery candidate sources are introduced later.
                var appHostProjects = searchResults.BuildableAppHost
                    .Where(c => IsUnderDirectory(c.AppHostFile, directory))
                    .Select(c => c.AppHostFile)
                    .ToList();

                if (displayProgress)
                {
                    interactionService.DisplayEmptyLine();
                }

                if (appHostProjects.Count == 0)
                {
                    var unbuildableInDirectory = searchResults.UnbuildableSuspectedAppHostProjects
                        .Where(c => IsUnderDirectory(c.AppHostFile, directory))
                        .ToList();

                    // The user pointed at this directory, and it holds exactly one candidate that only
                    // failed because MSBuild could not evaluate it. Selecting it is the same intent as
                    // naming the file, and it lets the caller's build surface the real MSBuild
                    // diagnostics instead of a resolution error that hides them. See
                    // https://github.com/microsoft/aspire/issues/19035.
                    if (unbuildableInDirectory.Count == 1)
                    {
                        var unbuildableAppHost = unbuildableInDirectory[0].AppHostFile;
                        logger.LogDebug(
                            "Selecting AppHost project file {ProjectFile} in directory {Directory} even though MSBuild could not evaluate it.",
                            unbuildableAppHost.FullName,
                            directory.FullName);

                        // Deliberately skip CreateSettingsFileAsync: this candidate was never confirmed to
                        // be an AppHost, so persisting it would make later ambient invocations silently
                        // reuse an unverified guess.
                        return new AppHostProjectSearchResult(unbuildableAppHost, [unbuildableAppHost]);
                    }

                    if (unbuildableInDirectory.Count > 1)
                    {
                        // Several broken candidates under one directory is a genuine ambiguity rather than
                        // a user selection, so this stays a project-resolution failure.
                        throw new ProjectLocatorException(ErrorStrings.AppHostsMayNotBeBuildable, ProjectLocatorFailureReason.AppHostsMayNotBeBuildable);
                    }

                    if (searchResults.UnsupportedProjects.Any(file => IsUnderDirectory(file, directory)))
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
                        explicitSelectionWasPrompted = true;
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
                        return new AppHostProjectSearchResult(null, appHostProjects);
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
                // Preserve symlinks because single-file AppHosts load apphost.run.json and
                // aspire.config.json beside the selected path. Backchannel and comparison call
                // sites canonicalize their own identity keys.
                var resolvedProjectPath = PathNormalizer.ResolvePathCasing(projectFile.FullName);

                if (!string.Equals(resolvedProjectPath, projectFile.FullName, StringComparison.Ordinal))
                {
                    logger.LogDebug(
                        "Normalized explicit AppHost path casing from '{OriginalPath}' to '{ResolvedPath}'.",
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
                            await CreateSettingsFileAsync(projectFile, preserveExistingDefault: !explicitSelectionWasPrompted, cancellationToken);
                        }

                        return new AppHostProjectSearchResult(projectFile, [projectFile])
                        {
                            WasExplicitDirectorySelectionPrompted = explicitSelectionWasPrompted
                        };
                    }

                    if (validationResult.IsPossiblyUnbuildable)
                    {
                        // The user named this exact file and it does exist. MSBuild simply could not
                        // evaluate it (unresolvable Aspire.AppHost.Sdk, malformed XML, ...), so keep it
                        // selected and let the caller's build print the real MSB4236/CS diagnostics.
                        // Reporting a resolution failure here produced the misleading "the --apphost
                        // option specified a project that does not exist" in
                        // https://github.com/microsoft/aspire/issues/19035.
                        logger.LogDebug(
                            "Selecting explicitly specified AppHost {ProjectFile} even though MSBuild could not evaluate it.",
                            projectFile.FullName);

                        // Deliberately skip CreateSettingsFileAsync: see the explicit-directory path above.
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

        var settingsResult = await GetValidatedAppHostProjectFileFromSettingsAsync(executionContext.WorkingDirectory, searchParentDirectories: true, cancellationToken);
        var settingsAppHost = settingsResult.AppHost;

        if (settingsAppHost is not null && multipleAppHostProjectsFoundBehavior is not MultipleAppHostProjectsFoundBehavior.None)
        {
            logger.LogDebug("Using AppHost path from settings without scanning: {AppHost}", settingsAppHost.FullName);

            // An unverified selection is never persisted: rewriting settings would turn a candidate that
            // was only kept because MSBuild failed into a confirmed choice.
            if (createSettingsFile && !settingsResult.IsUnverified)
            {
                await CreateSettingsFileAsync(settingsAppHost, preserveExistingDefault: false, cancellationToken);
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
            else if (results.UnsupportedProjects.Count > 0)
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
            var settingsCanonicalPath = settingsAppHost is null
                ? null
                : PathNormalizer.ResolveToFilesystemPath(settingsAppHost.FullName);

            if (settingsAppHost is not null
                && (settingsResult.IsUnverified
                    || results.BuildableAppHost.Any(c =>
                        string.Equals(c.AppHostFile.FullName, settingsAppHost.FullName, StringComparisons.FileSystemPath) ||
                        string.Equals(
                            PathNormalizer.ResolveToFilesystemPath(c.AppHostFile.FullName),
                            settingsCanonicalPath,
                            StringComparisons.FileSystemPath))))
            {
                // An unverified configured AppHost can never appear in BuildableAppHost by
                // construction, but it is still an explicit user choice. Honoring it here keeps this
                // branch consistent with the single-candidate branch above, which already prefers the
                // configured AppHost, and lets the caller's build surface the real MSBuild error
                // instead of prompting for (or silently running) a different application.
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

        // A selection that came from unverified settings must not be persisted (see the early-return
        // above); this path is reached when MultipleAppHostProjectsFoundBehavior.None skipped it.
        var selectionIsUnverifiedSettingsAppHost = settingsResult.IsUnverified
            && selectedAppHost is not null
            && settingsAppHost is not null
            && string.Equals(
                PathNormalizer.ResolveToFilesystemPath(selectedAppHost.FullName),
                PathNormalizer.ResolveToFilesystemPath(settingsAppHost.FullName),
                StringComparisons.FileSystemPath);

        if (createSettingsFile && !selectionIsUnverifiedSettingsAppHost)
        {
            await CreateSettingsFileAsync(selectedAppHost!, preserveExistingDefault: false, cancellationToken);
        }

        // Ensure the selected AppHost is always represented in the candidate list so callers
        // can rely on SelectedProjectFile being present in AllProjectFileCandidates. This
        // covers cases where the configured settings AppHost is selected but lives outside
        // the discovered candidate set (e.g. parent directory or excluded by enumeration).
        var allCandidates = results.BuildableAppHost.Select(c => c.AppHostFile).ToList();
        if (selectedAppHost is not null &&
            !allCandidates.Any(f => string.Equals(f.FullName, selectedAppHost.FullName, StringComparison.Ordinal)))
        {
            var selectedAppHostCanonicalPath = PathNormalizer.ResolveToFilesystemPath(selectedAppHost.FullName);
            var equivalentCandidateIndex = allCandidates.FindIndex(f =>
                string.Equals(
                    PathNormalizer.ResolveToFilesystemPath(f.FullName),
                    selectedAppHostCanonicalPath,
                    StringComparisons.FileSystemPath));

            if (equivalentCandidateIndex >= 0)
            {
                allCandidates[equivalentCandidateIndex] = selectedAppHost;
            }
            else
            {
                allCandidates.Add(selectedAppHost);
            }
        }

        return new AppHostProjectSearchResult(selectedAppHost, allCandidates);
    }

    public async Task<FileInfo?> UseOrFindAppHostProjectFileAsync(FileInfo? projectFile, bool createSettingsFile, CancellationToken cancellationToken = default)
    {
        var result = await UseOrFindAppHostProjectFileAsync(projectFile, MultipleAppHostProjectsFoundBehavior.Prompt, createSettingsFile, cancellationToken);
        return result.SelectedProjectFile;
    }

    /// <summary>
    /// Determines whether a persisted AppHost path identifies the selected project on the current platform.
    /// </summary>
    internal static bool IsSamePersistedAppHostPath(string persistedPath, string selectedPath)
    {
        var persistedCanonicalPath = PathNormalizer.ResolveToFilesystemPath(persistedPath);
        var selectedCanonicalPath = PathNormalizer.ResolveToFilesystemPath(selectedPath);

        return string.Equals(persistedCanonicalPath, selectedCanonicalPath, StringComparisons.FileSystemPath);
    }

    private async Task CreateSettingsFileAsync(FileInfo projectFile, bool preserveExistingDefault, CancellationToken cancellationToken)
    {
        var configuredSelectionOrigin = configuration[KnownConfigNames.CliAppHostSelectionOrigin];
        var isExplicitLaunchConfigurationSelection = string.Equals(
            configuredSelectionOrigin,
            ExplicitLaunchConfigurationSelectionOrigin,
            StringComparison.OrdinalIgnoreCase);
        var isExplicitCliSelection = string.Equals(
            configuredSelectionOrigin,
            ExplicitCliSelectionOrigin,
            StringComparison.OrdinalIgnoreCase);
        var hasConfiguredSelectionOrigin = !string.IsNullOrEmpty(configuredSelectionOrigin);
        var selectionOrigin = hasConfiguredSelectionOrigin ? configuredSelectionOrigin : "--apphost";
        var shouldPreserveExistingDefault =
            isExplicitLaunchConfigurationSelection ||
            (preserveExistingDefault && (isExplicitCliSelection || !hasConfiguredSelectionOrigin));

        var (settingsFile, appHostDirForScopedConfig) = ResolveWorkspaceConfigTarget(projectFile);

        // Compound launch configurations start multiple CLI processes together. The default check
        // and the whole-file writes must share one cross-process critical section so only the first
        // launch can establish a missing workspace default.
        using var configLock = await TryAcquireWorkspaceConfigLockAsync(settingsFile, cancellationToken);

        var existingConfig = LoadOrMigrateWorkspaceConfig(settingsFile);
        var fileExisted = settingsFile.Exists;

        if (existingConfig?.AppHost?.Path is { } existingPath &&
            IsValidConfiguredAppHostPath(existingPath, settingsFile.FullName, AspireConfigAppHostPathKey, silent: true))
        {
            var resolvedPath = PathNormalizer.NormalizePathForCurrentPlatform(
                Path.IsPathRooted(existingPath) ? existingPath : Path.Combine(settingsFile.Directory!.FullName, existingPath));

            if (IsSamePersistedAppHostPath(resolvedPath, projectFile.FullName))
            {
                logger.LogDebug(
                    "Config at {Path} already references apphost {AppHost}, skipping creation",
                    settingsFile.FullName,
                    projectFile.FullName);
                return;
            }

            // An explicit CLI target or launch configuration is for this invocation only. Preserve
            // an existing workspace default, but let the selected AppHost replace a deleted target.
            if (shouldPreserveExistingDefault && File.Exists(resolvedPath))
            {
                logger.LogDebug(
                    "Not replacing recorded AppHost default {RecordedAppHost} with {AppHost} because the latter was selected by {SelectionOrigin}.",
                    resolvedPath,
                    projectFile.FullName,
                    selectionOrigin);
                return;
            }
        }

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

    private (FileInfo SettingsFile, DirectoryInfo? AppHostDirectoryForScopedConfig) ResolveWorkspaceConfigTarget(FileInfo projectFile)
    {
        // Search from the AppHost's directory first so a config beside the AppHost wins over one
        // associated with the working directory.
        if (projectFile.Directory is { } appHostDirectory &&
            ConfigurationHelper.FindNearestConfigFilePath(appHostDirectory) is { } configPath)
        {
            var configDirectoryPath = Path.GetDirectoryName(configPath)!;
            var targetSettingsFilePath = configPath;

            // For legacy .aspire/settings.json, the config root is the parent of .aspire/.
            var trimmedConfigDirectoryPath = configDirectoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(Path.GetFileName(trimmedConfigDirectoryPath), ".aspire", StringComparison.OrdinalIgnoreCase) &&
                Directory.GetParent(trimmedConfigDirectoryPath) is { } parentDirectory)
            {
                targetSettingsFilePath = Path.Combine(parentDirectory.FullName, AspireConfigFile.FileName);
            }

            return (new FileInfo(targetSettingsFilePath), appHostDirectory);
        }

        var configuredSettingsFile = new FileInfo(configurationService.GetSettingsFilePath(isGlobal: false));

        if (string.Equals(configuredSettingsFile.Name, AspireConfigFile.FileName, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug("Using existing config file at {Path}", configuredSettingsFile.FullName);
            return (configuredSettingsFile, null);
        }

        var configRoot = ConfigurationHelper.GetLegacySettingsRootDirectory(configuredSettingsFile)
            ?? executionContext.WorkingDirectory;
        var newConfigPath = Path.Combine(configRoot.FullName, AspireConfigFile.FileName);
        logger.LogDebug("Will use workspace config at {Path}", newConfigPath);

        return (new FileInfo(newConfigPath), null);
    }

    private AspireConfigFile? LoadOrMigrateWorkspaceConfig(FileInfo settingsFile)
    {
        var configRoot = settingsFile.Directory!;
        if (settingsFile.Exists)
        {
            return AspireConfigFile.Load(configRoot.FullName);
        }

        var legacySettingsFile = new FileInfo(ConfigurationHelper.BuildPathToSettingsJsonFile(configRoot.FullName));
        if (!legacySettingsFile.Exists)
        {
            return null;
        }

        logger.LogInformation("Migrating legacy settings from {LegacyDir} to {ConfigFile}", configRoot.FullName, settingsFile.FullName);
        return AspireConfigFile.LoadOrCreate(configRoot.FullName);
    }

    private async Task<FileLock?> TryAcquireWorkspaceConfigLockAsync(FileInfo settingsFile, CancellationToken cancellationToken)
    {
        var lockPath = GetWorkspaceConfigLockPath(settingsFile);

        try
        {
            return await FileLock.AcquireAsync(lockPath, cancellationToken, s_workspaceConfigLockTimeout);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            // Persisting the workspace default is bookkeeping around the requested command. Preserve
            // the previous best-effort behavior if the cache directory cannot host the lock.
            logger.LogDebug(ex, "Proceeding without the workspace config lock at {LockPath}.", lockPath);
            return null;
        }
    }

    private string GetWorkspaceConfigLockPath(FileInfo settingsFile)
    {
        // Lock files live in the cache so read-only workspaces remain usable. Canonicalizing and
        // folding the config path makes aliases on symlinked or case-insensitive volumes contend;
        // on a case-sensitive volume this can only serialize two otherwise independent writes.
        var normalizedSettingsPath = PathNormalizer.ResolveSymlinks(settingsFile.FullName)
            .Normalize(NormalizationForm.FormC)
            .ToUpperInvariant();
        var lockFileName = Convert.ToHexString(XxHash3.Hash(Encoding.UTF8.GetBytes(normalizedSettingsPath))).ToLowerInvariant();

        return Path.Combine(executionContext.CacheDirectory.FullName, "workspace-config-locks", $"{lockFileName}.lock");
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
                => (CliExitCodes.FailedToFindProject, InteractionServiceStrings.ProjectOptionNotSpecifiedNoAppHostsFound),
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

internal record AppHostProjectSearchResult(FileInfo? SelectedProjectFile, List<FileInfo> AllProjectFileCandidates)
{
    internal bool WasExplicitDirectorySelectionPrompted { get; init; }
}

internal enum MultipleAppHostProjectsFoundBehavior
{
    Prompt,
    Throw,
    None
}
