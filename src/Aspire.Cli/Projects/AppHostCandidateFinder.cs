// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Aspire.Cli.Git;
using Aspire.Cli.Telemetry;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Projects;

/// <summary>
/// Finds candidate project files before AppHost validation.
/// </summary>
internal interface IAppHostCandidateFinder
{
    /// <summary>
    /// Finds files matching AppHost detection patterns using the requested discovery scope.
    /// </summary>
    /// <param name="searchDirectory">The directory to search recursively.</param>
    /// <param name="patterns">The detection patterns to match.</param>
    /// <param name="nugetCachePath">The NuGet package cache path to exclude, if known.</param>
    /// <param name="scope">Controls which files are considered. See <see cref="AppHostDiscoveryScope"/>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="maxDepth">The maximum subdirectory depth to search, where 0 only considers files in <paramref name="searchDirectory"/>.</param>
    /// <param name="onDirectoryEnumerated">
    /// Optional callback invoked with the running total of directories examined during discovery.
    /// In the git-aware path the callback is invoked once with the count of distinct directories; in the
    /// filesystem fallback it is invoked synchronously on the walker thread as each directory is
    /// enumerated. Callers must keep the callback cheap and thread-safe.
    /// </param>
    /// <returns>A search result with matching files and one count entry for every requested pattern.</returns>
    Task<AppHostCandidateFileSearchResult> FindCandidateFilesAsync(
        DirectoryInfo searchDirectory,
        IReadOnlyList<string> patterns,
        string? nugetCachePath,
        AppHostDiscoveryScope scope,
        CancellationToken cancellationToken,
        int? maxDepth = null,
        Action<int>? onDirectoryEnumerated = null);
}

/// <summary>
/// Contains matched candidate files and per-pattern match counts.
/// </summary>
internal sealed record AppHostCandidateFileSearchResult(FileInfo[] Files, Dictionary<string, int> CountsByPattern);

/// <summary>
/// Finds AppHost candidate files using git-aware and filesystem discovery.
/// </summary>
internal sealed class AppHostCandidateFinder(
    IGitRepository gitRepository,
    IEnvironment environment,
    ProfilingTelemetry profilingTelemetry,
    ILogger<AppHostCandidateFinder> logger) : IAppHostCandidateFinder
{
    // Directory names (and path-scoped subpaths) excluded from AppHost discovery by default. These
    // are common build outputs, package caches, and tooling directories that should never contain a
    // user's AppHost. The list is intentionally conservative - it omits names like "vendor" (Go/PHP
    // first-party), "build" (some repos use as source), and "packages" (pnpm/Lerna workspace root)
    // because those are sometimes legitimate. Matching is case-insensitive on every platform; on
    // Linux that means a directory named "Bin" coincidentally collides with "bin" and gets skipped,
    // which is the right tradeoff given these are conventional names.
    private static readonly ImmutableArray<string> s_defaultExcludedDirectoryNames =
    [
        // Build outputs / artifacts
        "bin", "obj", "dist", "out", "target", "artifacts", "coverage",

        // VCS / IDE
        ".git", ".vs", ".idea",

        // Agent skill roots ship apphost.* files as code samples, not runnable AppHosts (see
        // SkillLocation). .agents and .claude are tool-owned, so the whole directory is excluded;
        // .github and .opencode hold non-skill content too, so only the skills subpath is excluded.
        ".agents", ".claude", Path.Combine(".github", "skills"), Path.Combine(".opencode", "skill"),

        // JavaScript / TypeScript ecosystems
        "node_modules", ".next", ".nuxt", ".cache", ".turbo", ".svelte-kit",
        ".parcel-cache", ".yarn", ".pnpm-store", "bower_components",

        // Python
        "__pycache__", ".venv", "venv", ".tox",
        ".pytest_cache", ".mypy_cache", ".ruff_cache",

        // JVM / other
        ".gradle",
    ];

    // Segment view of the exclusion list, computed once. A bare name like "bin" becomes a
    // one-segment sequence and a path-scoped entry like ".github/skills" becomes a multi-segment
    // sequence; both are matched as a contiguous subsequence of a candidate's directory segments.
    private static readonly ImmutableArray<ImmutableArray<string>> s_excludedDirectorySegments =
    [
        .. s_defaultExcludedDirectoryNames.Select(static name =>
            name.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
                .ToImmutableArray()),
    ];

    public async Task<AppHostCandidateFileSearchResult> FindCandidateFilesAsync(
        DirectoryInfo searchDirectory,
        IReadOnlyList<string> patterns,
        string? nugetCachePath,
        AppHostDiscoveryScope scope,
        CancellationToken cancellationToken,
        int? maxDepth = null,
        Action<int>? onDirectoryEnumerated = null)
    {
        using var discoveryActivity = profilingTelemetry.StartAppHostCandidateDiscovery(searchDirectory, scope, patterns.Count, nugetCachePath is not null);

        // This method often starts from the Ctrl+C path in `aspire ls`. Check before doing any
        // discovery work so a cancellation observed after telemetry setup does not continue into
        // git or filesystem enumeration.
        cancellationToken.ThrowIfCancellationRequested();

        if (patterns.Count == 0)
        {
            discoveryActivity.SetAppHostDiscoverySource(ProfilingTelemetry.Values.AppHostDiscoverySourceNone);
            discoveryActivity.SetAppHostCandidateCount(0);
            return new([], new Dictionary<string, int>(StringComparer.Ordinal));
        }

        // In ambient discovery mode, prefer git as the source of candidate paths so the
        // user's .gitignore is honored. The git-included path returns null when git is
        // unavailable, the directory is not in a working tree, or the command fails - in
        // which case we fall back to the filesystem walk below.
        if (scope == AppHostDiscoveryScope.DefaultFiltered)
        {
            var includedPaths = await gitRepository.GetIncludedFilesAsync(searchDirectory, cancellationToken);
            if (includedPaths is not null)
            {
                logger.LogDebug("Using git-included file list ({Count} entries) as discovery source.", includedPaths.Count);
                using var matchActivity = profilingTelemetry.StartAppHostCandidateGitMatch(includedPaths.Count, patterns.Count);
                var result = MatchFromIncludedPaths(searchDirectory, patterns, includedPaths, nugetCachePath, maxDepth, cancellationToken);
                matchActivity.SetAppHostCandidateCount(result.Files.Length);
                discoveryActivity.SetAppHostDiscoverySource(ProfilingTelemetry.Values.AppHostDiscoverySourceGit);
                discoveryActivity.SetAppHostDiscoveryIncludedFileCount(includedPaths.Count);
                discoveryActivity.SetAppHostCandidateCount(result.Files.Length);

                // Surface a single aggregate directory count so progress UI (e.g. `aspire ls`) can show that
                // work happened. Git enumeration is a single shot over files, so we approximate the directory
                // walk by counting unique directories that contain git-included files.
                onDirectoryEnumerated?.Invoke(CountIncludedDirectories(includedPaths));

                return result;
            }

            logger.LogDebug("Git enumeration unavailable for {SearchDirectory}; falling back to filesystem walk.", searchDirectory.FullName);
        }

        // If cancellation happened while the external git command was failing or being torn down,
        // don't immediately start the slower fallback filesystem walk over a large repository.
        cancellationToken.ThrowIfCancellationRequested();

        // Directory exclusions only apply to the filtered scope; AllFiles opts in to everything.
        var applyDirectoryExclusions = scope != AppHostDiscoveryScope.AllFiles;
        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true
        };

        using var walkActivity = profilingTelemetry.StartAppHostCandidateFilesystemWalk(searchDirectory, patterns.Count, applyDirectoryExclusions, nugetCachePath is not null);
        var searchResult = FindMatchingFiles(searchDirectory, patterns, enumerationOptions, nugetCachePath, applyDirectoryExclusions, maxDepth, walkActivity, onDirectoryEnumerated, cancellationToken);
        walkActivity.SetAppHostCandidateCount(searchResult.Files.Length);
        discoveryActivity.SetAppHostDiscoverySource(ProfilingTelemetry.Values.AppHostDiscoverySourceFilesystem);
        discoveryActivity.SetAppHostCandidateCount(searchResult.Files.Length);
        return searchResult;
    }

    private AppHostCandidateFileSearchResult MatchFromIncludedPaths(
        DirectoryInfo searchDirectory,
        IReadOnlyList<string> patterns,
        IReadOnlySet<string> includedPaths,
        string? nugetCachePath,
        int? maxDepth,
        CancellationToken cancellationToken)
    {
        var pathComparison = GetPathComparison();
        var pathComparer = GetPathComparer();

        var rootFullName = searchDirectory.FullName;

        // Git gives us absolute paths, but the globbing library matches relative paths.
        // Example: if the search root is "/repo" and git returns
        // "/repo/src/MyApp.AppHost/MyApp.AppHost.csproj", we match
        // "src/MyApp.AppHost/MyApp.AppHost.csproj" against patterns like
        // "**/*.csproj".
        var matcher = CreateMatcher(patterns);

        // Keep a combined matcher for the actual candidate list, then run one matcher
        // per original pattern to preserve the debug counts ProjectLocator logs.
        // Example: a file can match both "*.csproj" and "src/**/AppHost.csproj";
        // it should appear once in Files but increment both pattern counts.
        var matchersByPattern = patterns.Select(p => (Pattern: p, Matcher: CreateMatcher(p))).ToArray();
        var countsByPattern = patterns.ToDictionary(p => p, _ => 0, StringComparer.Ordinal);

        var matchedFiles = new List<FileInfo>();
        var matchedSet = new HashSet<string>(pathComparer);

        foreach (var absolutePath in includedPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The git list may include tracked-but-deleted files; skip anything that no
            // longer exists on disk so callers don't end up validating phantom files.
            if (!File.Exists(absolutePath))
            {
                continue;
            }

            // Apply the NuGet cache exclusion uniformly across both code paths.
            if (nugetCachePath is not null && IsUnderPath(absolutePath, nugetCachePath, pathComparison))
            {
                continue;
            }

            // Belt-and-suspenders: even when git is the source of truth, prune anything
            // under a hardcoded skip-listed directory so a repo that forgot to gitignore
            // node_modules/ still gets cleaned up.
            if (HasSkipListedDirectorySegment(rootFullName, absolutePath))
            {
                continue;
            }

            var relativeNative = Path.GetRelativePath(rootFullName, absolutePath);
            if (!IsWithinDepth(relativeNative, maxDepth))
            {
                continue;
            }

            // Microsoft.Extensions.FileSystemGlobbing matchers operate on forward-slash
            // paths even on Windows. Convert "src\\AppHost\\AppHost.csproj" to
            // "src/AppHost/AppHost.csproj" before matching.
            var relativeForwardSlash = Path.DirectorySeparatorChar == '/'
                ? relativeNative
                : relativeNative.Replace(Path.DirectorySeparatorChar, '/');

            if (!matcher.Match(relativeForwardSlash).HasMatches)
            {
                continue;
            }

            if (matchedSet.Add(absolutePath))
            {
                matchedFiles.Add(new FileInfo(absolutePath));
            }

            foreach (var (pattern, perPatternMatcher) in matchersByPattern)
            {
                if (perPatternMatcher.Match(relativeForwardSlash).HasMatches)
                {
                    countsByPattern[pattern]++;
                }
            }
        }

        return new(matchedFiles.ToArray(), countsByPattern);
    }

    private static bool IsUnderPath(string candidatePath, string ancestorPath, StringComparison pathComparison)
    {
        var candidate = Path.GetFullPath(candidatePath);
        var ancestor = Path.GetFullPath(ancestorPath);
        return candidate.Equals(ancestor, pathComparison)
            || candidate.StartsWith(ancestor + Path.DirectorySeparatorChar, pathComparison);
    }

    private int CountIncludedDirectories(IReadOnlySet<string> includedPaths)
    {
        var directories = new HashSet<string>(GetPathComparer());
        foreach (var includedPath in includedPaths)
        {
            if (Path.GetDirectoryName(includedPath) is { } directory)
            {
                directories.Add(Path.GetFullPath(directory));
            }
        }

        return directories.Count;
    }

    private static bool HasSkipListedDirectorySegment(string rootFullName, string absolutePath)
    {
        if (Path.GetDirectoryName(absolutePath) is not { Length: > 0 } absoluteDirectory)
        {
            return false;
        }

        // Match against the directory path relative to the search root. Only segments below the root
        // are considered, so the root's own name (and any ancestor above it) never participates -- a
        // repo checked out under a folder named like an excluded directory (e.g. C:\obj\repo or a
        // root literally named "obj") keeps its real AppHosts.
        var relative = Path.GetRelativePath(rootFullName, absoluteDirectory);

        // GetRelativePath returns ".." when absoluteDirectory is outside the root; nothing to match
        // in that case (shouldn't happen with well-formed git output rooted under the search root).
        if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal))
        {
            return false;
        }

        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        return ContainsExcludedSegmentSequence(segments);
    }

    // True when any excluded sequence appears as a contiguous run of segments. Single-name entries
    // are length-1 sequences, so this also covers the plain directory-name exclusions.
    private static bool ContainsExcludedSegmentSequence(ReadOnlySpan<string> segments)
    {
        foreach (var sequence in s_excludedDirectorySegments)
        {
            if (ContainsContiguousSubsequence(segments, sequence.AsSpan()))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsContiguousSubsequence(ReadOnlySpan<string> segments, ReadOnlySpan<string> sequence)
    {
        if (sequence.Length == 0 || sequence.Length > segments.Length)
        {
            return false;
        }

        for (var start = 0; start <= segments.Length - sequence.Length; start++)
        {
            var matched = true;
            for (var offset = 0; offset < sequence.Length; offset++)
            {
                if (!segments[start + offset].Equals(sequence[offset], StringComparison.OrdinalIgnoreCase))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    // Filesystem-walk counterpart: prunes at the directory that IS an excluded root by matching the
    // directory and its ancestors against an excluded sequence's trailing segments. The walk is
    // bounded by the sequence length, so ancestors above the matched root are never inspected.
    private static bool IsExcludedDirectory(DirectoryInfo directory)
    {
        foreach (var sequence in s_excludedDirectorySegments)
        {
            if (MatchesTrailingSegments(directory, sequence.AsSpan()))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesTrailingSegments(DirectoryInfo directory, ReadOnlySpan<string> sequence)
    {
        DirectoryInfo? current = directory;
        for (var i = sequence.Length - 1; i >= 0; i--)
        {
            if (current is null || !current.Name.Equals(sequence[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            current = current.Parent;
        }

        return true;
    }

    private AppHostCandidateFileSearchResult FindMatchingFiles(
        DirectoryInfo searchDirectory,
        IReadOnlyList<string> patterns,
        EnumerationOptions options,
        string? excludePath,
        bool applyDirectoryExclusions,
        int? maxDepth,
        ProfilingTelemetry.ActivityScope walkActivity,
        Action<int>? onDirectoryEnumerated,
        CancellationToken cancellationToken)
    {
        if (patterns.Count == 0)
        {
            return new([], new Dictionary<string, int>(StringComparer.Ordinal));
        }

        var pathComparison = GetPathComparison();

        var matcher = CreateMatcher(patterns);

        // The MatcherDirectoryInfo adapter lets Microsoft.Extensions.FileSystemGlobbing
        // drive a single recursive traversal while still honoring our fallback filters.
        // This is important for patterns like "*.csproj": we normalize them to
        // "**/*.csproj" and let the matcher walk once instead of enumerating once per
        // language detection pattern.
        var counters = new DiscoveryCounters();
        var directory = new MatcherDirectoryInfo(searchDirectory, options, excludePath, applyDirectoryExclusions, pathComparison, counters, onDirectoryEnumerated, depth: 0, maxDepth, cancellationToken);
        var matchedFilePaths = matcher.Execute(directory).Files.Select(match => match.Path).ToArray();
        walkActivity.SetAppHostDiscoveryWalkCounts(counters.FilesEnumerated, counters.DirectoriesEnumerated, counters.DirectoriesSkipped);

        var matchedFiles = matchedFilePaths
            .Select(path => new FileInfo(Path.Combine(searchDirectory.FullName, path.Replace('/', Path.DirectorySeparatorChar))))
            .ToArray();

        var countsByPattern = patterns.ToDictionary(pattern => pattern, _ => 0, StringComparer.Ordinal);

        // The combined matcher above gives the union of all matches. Re-run the already
        // matched relative paths through each original pattern so callers can report the
        // same per-pattern counts without paying for another filesystem traversal.
        // Example: if "**/*.csproj" and "**/*.fsproj" are configured, both counts are
        // derived from matchedFilePaths, not from two separate directory walks.
        var matchersByPattern = patterns.Select(pattern => (Pattern: pattern, Matcher: CreateMatcher(pattern))).ToArray();
        foreach (var matchedFilePath in matchedFilePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var (pattern, patternMatcher) in matchersByPattern)
            {
                if (patternMatcher.Match(matchedFilePath).HasMatches)
                {
                    countsByPattern[pattern]++;
                }
            }
        }

        return new(matchedFiles, countsByPattern);
    }

    private static string ToRecursiveGlobPattern(string pattern)
    {
        var normalizedPattern = pattern.Replace(Path.DirectorySeparatorChar, '/');
        if (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar)
        {
            normalizedPattern = normalizedPattern.Replace(Path.AltDirectorySeparatorChar, '/');
        }

        // Language detection patterns are usually file names ("*.csproj"). Without the
        // recursive prefix, FileSystemGlobbing only matches them at the search root.
        // Convert "*.csproj" to "**/*.csproj" so nested AppHosts are discovered, while
        // leaving directory-aware patterns such as "src/**/AppHost.csproj" unchanged.
        return normalizedPattern.Contains('/', StringComparison.Ordinal)
            ? normalizedPattern
            : $"**/{normalizedPattern}";
    }

    private static bool IsWithinDepth(string relativePath, int? maxDepth)
    {
        if (maxDepth is null)
        {
            return true;
        }

        var relativeDirectory = Path.GetDirectoryName(relativePath);
        if (string.IsNullOrEmpty(relativeDirectory))
        {
            return true;
        }

        var depth = relativeDirectory.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries).Length;
        return depth <= maxDepth;
    }

    private static Matcher CreateMatcher(IEnumerable<string> patterns)
    {
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddIncludePatterns(patterns.Select(ToRecursiveGlobPattern));
        return matcher;
    }

    private static Matcher CreateMatcher(string pattern)
    {
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(ToRecursiveGlobPattern(pattern));
        return matcher;
    }

    private StringComparison GetPathComparison()
    {
        return environment.IsWindows() || environment.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }

    private StringComparer GetPathComparer()
    {
        return environment.IsWindows() || environment.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
    }

    private sealed class DiscoveryCounters
    {
        // Matcher.Execute drives DirectoryInfoBase enumeration synchronously on a
        // single thread. If this adapter is ever used from parallel enumeration,
        // these counters need Interlocked updates or equivalent synchronization.
        public int FilesEnumerated { get; set; }

        public int DirectoriesEnumerated { get; set; }

        public int DirectoriesSkipped { get; set; }
    }

    private sealed class MatcherDirectoryInfo(DirectoryInfo directory, EnumerationOptions options, string? excludePath, bool applyDirectoryExclusions, StringComparison pathComparison, DiscoveryCounters counters, Action<int>? onDirectoryEnumerated, int depth, int? maxDepth, CancellationToken cancellationToken) : DirectoryInfoBase
    {
        private readonly DirectoryInfo _directory = directory;
        private readonly EnumerationOptions _options = options;
        private readonly string? _excludePath = excludePath;
        private readonly bool _applyDirectoryExclusions = applyDirectoryExclusions;
        private readonly StringComparison _pathComparison = pathComparison;
        private readonly DiscoveryCounters _counters = counters;
        private readonly Action<int>? _onDirectoryEnumerated = onDirectoryEnumerated;
        private readonly int _depth = depth;
        private readonly int? _maxDepth = maxDepth;
        private readonly CancellationToken _cancellationToken = cancellationToken;

        public override string Name => _directory.Name;

        public override string FullName => _directory.FullName;

        public override DirectoryInfoBase ParentDirectory => _directory.Parent is { } parent
            ? new MatcherDirectoryInfo(parent, _options, _excludePath, _applyDirectoryExclusions, _pathComparison, _counters, _onDirectoryEnumerated, _depth - 1, _maxDepth, _cancellationToken)
            : null!;

        public override IEnumerable<FileSystemInfoBase> EnumerateFileSystemInfos()
        {
            foreach (var entry in _directory.EnumerateFileSystemInfos("*", CreateTopDirectoryOnlyOptions(_options)))
            {
                _cancellationToken.ThrowIfCancellationRequested();

                if (entry is DirectoryInfo childDirectory)
                {
                    _counters.DirectoriesEnumerated++;
                    // Report the running count so progress UI (e.g. `aspire ls`) can give the user a visible
                    // signal that work is happening even before the first AppHost candidate is found.
                    _onDirectoryEnumerated?.Invoke(_counters.DirectoriesEnumerated);
                    if (CanRecurse && !ShouldExcludeDirectory(childDirectory))
                    {
                        yield return new MatcherDirectoryInfo(childDirectory, _options, _excludePath, _applyDirectoryExclusions, _pathComparison, _counters, _onDirectoryEnumerated, _depth + 1, _maxDepth, _cancellationToken);
                    }
                    else
                    {
                        _counters.DirectoriesSkipped++;
                    }
                }
                else if (entry is FileInfo childFile)
                {
                    _counters.FilesEnumerated++;
                    if (CanIncludeFiles)
                    {
                        yield return new MatcherFileInfo(childFile, _options, _excludePath, _applyDirectoryExclusions, _pathComparison, _counters, _onDirectoryEnumerated, _depth, _maxDepth, _cancellationToken);
                    }
                }
            }
        }

        public override DirectoryInfoBase GetDirectory(string path)
        {
            return new MatcherDirectoryInfo(new DirectoryInfo(Path.Combine(_directory.FullName, path)), _options, _excludePath, _applyDirectoryExclusions, _pathComparison, _counters, _onDirectoryEnumerated, _depth + 1, _maxDepth, _cancellationToken);
        }

        public override FileInfoBase GetFile(string path)
        {
            return new MatcherFileInfo(new FileInfo(Path.Combine(_directory.FullName, path)), _options, _excludePath, _applyDirectoryExclusions, _pathComparison, _counters, _onDirectoryEnumerated, _depth, _maxDepth, _cancellationToken);
        }

        private bool ShouldExcludeDirectory(DirectoryInfo directory)
        {
            // Exclusions are disabled under the AllFiles scope (applyDirectoryExclusions is false).
            if (_applyDirectoryExclusions && IsExcludedDirectory(directory))
            {
                return true;
            }

            if (_excludePath is null)
            {
                return false;
            }

            var directoryPath = Path.GetFullPath(directory.FullName);
            return directoryPath.Equals(_excludePath, _pathComparison)
                || directoryPath.StartsWith(_excludePath + Path.DirectorySeparatorChar, _pathComparison);
        }

        private bool CanRecurse => _maxDepth is null || _depth < _maxDepth;

        private bool CanIncludeFiles => _maxDepth is null || _depth <= _maxDepth;
    }

    private sealed class MatcherFileInfo(FileInfo file, EnumerationOptions options, string? excludePath, bool applyDirectoryExclusions, StringComparison pathComparison, DiscoveryCounters counters, Action<int>? onDirectoryEnumerated, int depth, int? maxDepth, CancellationToken cancellationToken) : FileInfoBase
    {
        private readonly FileInfo _file = file;
        private readonly EnumerationOptions _options = options;
        private readonly string? _excludePath = excludePath;
        private readonly bool _applyDirectoryExclusions = applyDirectoryExclusions;
        private readonly StringComparison _pathComparison = pathComparison;
        private readonly DiscoveryCounters _counters = counters;
        private readonly Action<int>? _onDirectoryEnumerated = onDirectoryEnumerated;
        private readonly int _depth = depth;
        private readonly int? _maxDepth = maxDepth;
        private readonly CancellationToken _cancellationToken = cancellationToken;

        public override string Name => _file.Name;

        public override string FullName => _file.FullName;

        public override DirectoryInfoBase ParentDirectory => _file.Directory is { } parent
            ? new MatcherDirectoryInfo(parent, _options, _excludePath, _applyDirectoryExclusions, _pathComparison, _counters, _onDirectoryEnumerated, _depth, _maxDepth, _cancellationToken)
            : null!;
    }

    private static EnumerationOptions CreateTopDirectoryOnlyOptions(EnumerationOptions options)
    {
        return new EnumerationOptions
        {
            AttributesToSkip = options.AttributesToSkip,
            BufferSize = options.BufferSize,
            IgnoreInaccessible = options.IgnoreInaccessible,
            MatchCasing = options.MatchCasing,
            MatchType = options.MatchType,
            MaxRecursionDepth = options.MaxRecursionDepth,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = options.ReturnSpecialDirectories
        };
    }
}
