// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Aspire.Cli.Configuration;
using Aspire.Cli.Projects;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Caching;

/// <summary>
/// Content-keyed disk cache for the AppHost MSBuild project inspection.
/// </summary>
/// <remarks>
/// Reliable invalidation is achieved by making the inputs that drive the cached MSBuild
/// evaluation part of the cache key itself. Any change to a tracked input produces a different
/// key, which means a cache miss and a fresh re-evaluation. There is no need for a time-based
/// staleness window for correctness — only for janitorial cleanup of orphaned entries.
/// This mirrors the shape of the SDK's own MSBuild-output caches: incremental targets include
/// inputs such as <c>$(ProjectAssetsFile)</c>, <c>$(ProjectAssetsCacheFile)</c>, and
/// <c>$(MSBuildAllProjects)</c>. This cache sits before the MSBuild call it is trying to avoid,
/// so it fingerprints stable filesystem beacons that represent those same inputs instead of
/// asking MSBuild for another evaluation.
///
/// Tracked inputs (see <see cref="ComputeKeyAsync(FileInfo, IEnvironment, in DotNetAppHostProject.AppHostImportDependencies)"/>):
/// <list type="bullet">
///   <item>The .csproj absolute path and its last-write time.</item>
///   <item>The mtime of <c>obj/project.assets.json</c> next to the .csproj. NuGet writes this
///         file on every restore, so any package-graph change (Central Package Management
///         version bumps, transitive package updates, SDK changes that affect restore) advances
///         this timestamp.</item>
///   <item>The mtimes of <c>Directory.Build.props</c>, <c>Directory.Build.targets</c>,
///         <c>Directory.Packages.props</c>, and <c>Directory.Packages.targets</c> found by
///         walking up from the project directory to the filesystem root. This matches MSBuild's
///         own Directory.Build.* discovery range (which has no <c>.git</c> boundary) and keeps
///         the fingerprint in parity with <c>DotNetAppHostProject.IsLikelyAppHost</c>. This catches
///         transitive .props edits that the user has not yet restored against.</item>
///   <item>The mtime of <c>global.json</c> walking up the same path, to catch SDK pin
///         changes that do not trigger a restore.</item>
///   <item>The mtimes of the custom files that the AppHost prefilter's imports resolve to
///         (see <c>DotNetAppHostProject.CollectImportDependencies</c>): walk-up import targets such as
///         <c>$([MSBuild]::GetPathOfFileAbove('Aspire.Common.props'))</c>, statted at every ancestor
///         level, and exact static import targets such as
///         <c>$(MSBuildThisFileDirectory)../shared/Directory.Build.props</c>, statted by full path.
///         Those imports can set <c>IsAspireHost</c> without touching any conventional file above.</item>
///   <item>A schema version constant, bumped when the set of cached properties changes.</item>
/// </list>
///
/// When the prefilter honors an import whose target no filesystem walk can predict (an MSBuild expression
/// in place of the file name, a walk-up rooted outside the project's ancestor chain, an appended path that
/// does not name a single file in the resolved directory, or a wildcard), the disk cache is bypassed
/// entirely for that project rather than risking a permanently stale entry.
///
/// The cache only stores metadata from the AppHost inspection target, not build outputs or runtime
/// state, so a stale hit can only reuse stale answers such as the AppHost marker, Aspire.Hosting
/// version, CLI bundle opt-in, user-secrets ID, or AppHost launch metadata. The known stale-hit
/// cases are the inputs MSBuild can see but this pre-MSBuild fingerprint cannot reliably discover
/// without doing another evaluation:
/// <list type="bullet">
///   <item>Edits to <c>.targets</c> or <c>.props</c> files imported from OUTSIDE the project
///         directory tree (e.g. <c>&lt;Import Project="..\..\shared.targets"/&gt;</c>).</item>
///   <item>Custom imports whose path changes are not reflected by one of the conventional
///         walk-up files tracked above.</item>
///   <item>External manipulation of <c>project.assets.json</c> mtime, or a restore/package graph
///         change that does not update that file's timestamp.</item>
/// </list>
///
/// Users with such setups can recover by touching the .csproj, running <c>dotnet restore</c>,
/// running <c>aspire cache clear</c>, or running
/// <c>aspire config set dotnetAppHostInfoCacheDisabled true</c>.
/// </remarks>
internal sealed class AppHostInfoDiskCache : IAppHostInfoDiskCache
{
    // Bump this after shipping if the cached property set changes so old entries are ignored.
    private const string SchemaVersion = "v1";

    // Keep AppHost inspection entries isolated from other Aspire caches so `aspire cache clear`
    // can delete the whole subtree without needing to understand the file naming scheme.
    private const string SubDirectoryName = "apphost-info";

    // Escape hatch for users whose projects rely on imported files that are not represented in
    // the fingerprint. This intentionally goes through IConfigurationService instead of the
    // process-wide IConfiguration so only `aspire config set dotnetAppHostInfoCacheDisabled true`
    // participates; environment variables with the same name must not disable the cache.
    private const string DisableConfigKey = "dotnetAppHostInfoCacheDisabled";

    // We cannot rely only on MSBuildAllProjects here because cache hits happen before MSBuild
    // runs. The previous evaluation can tell us which files were imported then, but it cannot
    // reveal a newly added Directory.Build.* or Directory.Packages.* file that would change the
    // next evaluation unless we probe those conventional walk-up locations ourselves.
    private static readonly string[] s_trackedSiblingFiles =
    [
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Packages.props",
        "Directory.Packages.targets",
    ];

    private static JsonTypeInfo<AppHostInfoCacheEntry> EntryTypeInfo =>
        JsonSourceGenerationContext.Default.AppHostInfoCacheEntry;

    private readonly ILogger<AppHostInfoDiskCache> _logger;
    private readonly DirectoryInfo _cacheDirectory;
    private readonly IConfigurationService _configurationService;
    private readonly IEnvironment _environment;

    public AppHostInfoDiskCache(ILogger<AppHostInfoDiskCache> logger, CliExecutionContext executionContext, IConfigurationService configurationService, IEnvironment environment)
    {
        _logger = logger;
        _cacheDirectory = new DirectoryInfo(Path.Combine(executionContext.CacheDirectory.FullName, SubDirectoryName));
        _configurationService = configurationService;
        _environment = environment;
    }

    public async Task<AppHostInfoCacheEntry?> TryGetAsync(FileInfo projectFile, CancellationToken cancellationToken)
    {
        if (await IsDisabledAsync(projectFile, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        // Collect once and reuse for both the bypass decision and the key: the collector parses the project
        // and the ancestor Directory.Build.* files, which is the most expensive part of a cache hit.
        var importDependencies = DotNetAppHostProject.CollectImportDependencies(projectFile);
        if (HasUnfingerprintableImports(projectFile, importDependencies))
        {
            return null;
        }

        try
        {
            var key = ComputeKeyAsync(projectFile, _environment, importDependencies);
            var path = Path.Combine(_cacheDirectory.FullName, $"{key}.json");
            if (!File.Exists(path))
            {
                _logger.LogTrace("AppHost info cache miss for {Project} (key {Key})", projectFile.FullName, key);
                return null;
            }

            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var entry = JsonSerializer.Deserialize(json, EntryTypeInfo);
            if (entry is null || !string.Equals(entry.SchemaVersion, SchemaVersion, StringComparison.Ordinal))
            {
                // Schema mismatch — treat as miss, the new value will overwrite.
                _logger.LogTrace("AppHost info cache schema mismatch for {Project}", projectFile.FullName);
                return null;
            }

            if (entry.ExitCode == 0 && entry.IsAspireHost && string.IsNullOrWhiteSpace(entry.RunCommand))
            {
                // Treat entries that successfully inspected an AppHost but are missing RunCommand
                // as schema-incompatible misses, so the direct launch path can refresh the SDK run
                // command instead of permanently falling back. The schema marker stays stable
                // because adding RunCommand is backward compatible for non-AppHost entries.
                _logger.LogTrace("AppHost info cache entry for {Project} is missing RunCommand", projectFile.FullName);
                return null;
            }

            _logger.LogTrace("AppHost info cache hit for {Project} (key {Key})", projectFile.FullName, key);
            return entry;
        }
        catch (Exception ex)
        {
            // Any read or deserialization failure is non-fatal: just miss the cache.
            _logger.LogDebug(ex, "Failed to read AppHost info cache for {Project}", projectFile.FullName);
            return null;
        }
    }

    public async Task SetAsync(FileInfo projectFile, string expectedCacheKey, AppHostInfoCacheEntry entry, CancellationToken cancellationToken)
    {
        if (await IsDisabledAsync(projectFile, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var importDependencies = DotNetAppHostProject.CollectImportDependencies(projectFile);
        if (HasUnfingerprintableImports(projectFile, importDependencies))
        {
            return;
        }

        string? tempPath = null;

        try
        {
            if (!_cacheDirectory.Exists)
            {
                _cacheDirectory.Create();
            }

            var key = ComputeKeyAsync(projectFile, _environment, importDependencies);
            if (!string.Equals(key, expectedCacheKey, StringComparison.Ordinal))
            {
                // The key is captured before MSBuild runs and checked again before publishing.
                // Without this guard, a project/import/assets edit that lands during evaluation
                // could write stale metadata under the new input key.
                _logger.LogTrace(
                    "Skipping AppHost info cache write for {Project}; cache key changed from {ExpectedKey} to {CurrentKey}",
                    projectFile.FullName,
                    expectedCacheKey,
                    key);
                return;
            }

            var path = Path.Combine(_cacheDirectory.FullName, $"{key}.json");

            // Same pattern used by dotnet/sdk's SdkReleaseMetadataCache: write a complete
            // payload to a random file in the target directory, then atomically replace the
            // stable project/input-scoped file so readers never see partial JSON.
            // See https://github.com/dotnet/sdk/blob/main/src/Cli/dotnet/SdkVulnerability/SdkReleaseMetadataCache.cs
            tempPath = Path.Combine(_cacheDirectory.FullName, $"{Path.GetRandomFileName()}.tmp");
            var payload = JsonSerializer.Serialize(entry with { SchemaVersion = SchemaVersion }, EntryTypeInfo);
            await File.WriteAllTextAsync(tempPath, payload, cancellationToken).ConfigureAwait(false);

            // File.Move(..., overwrite: true) is atomic on the same volume on POSIX and on
            // Windows since .NET 5. If two CLIs race here the loser overwrites with identical
            // content (same key → same payload), so the result is consistent either way.
            File.Move(tempPath, path, overwrite: true);
            _logger.LogTrace("Stored AppHost info cache entry for {Project} (key {Key})", projectFile.FullName, key);
        }
        catch (Exception ex)
        {
            if (tempPath is not null)
            {
                TryDeleteTemporaryFile(tempPath, _logger);
            }

            _logger.LogDebug(ex, "Failed to write AppHost info cache for {Project}", projectFile.FullName);
        }
    }

    private static void TryDeleteTemporaryFile(string tempPath, ILogger logger)
    {
        try
        {
            File.Delete(tempPath);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to delete temporary AppHost info cache file {Path}", tempPath);
        }
    }

    /// <summary>
    /// Computes a stable, content-derived cache key for the supplied project file.
    /// The key is a hex-encoded XxHash3 of a delimited string of inputs; it is suitable for
    /// use as a filename on all platforms.
    /// </summary>
    public string GetCacheKey(FileInfo projectFile) => ComputeKeyAsync(projectFile, _environment);

    private async Task<bool> IsDisabledAsync(FileInfo projectFile, CancellationToken cancellationToken)
    {
        var startDirectory = projectFile.Directory ?? new DirectoryInfo(Environment.CurrentDirectory);
        var value = await _configurationService.GetConfigurationFromDirectoryAsync(DisableConfigKey, startDirectory, cancellationToken: cancellationToken).ConfigureAwait(false);
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private bool HasUnfingerprintableImports(FileInfo projectFile, in DotNetAppHostProject.AppHostImportDependencies importDependencies)
    {
        // The AppHost prefilter honors custom imports such as
        //   <Import Project="$([MSBuild]::GetPathOfFileAbove('Aspire.Common.props', '$(ExternalRoot)'))" />
        //   <Import Project="$(RepoRoot)Directory.Build.props" />
        // Most of them are fingerprinted in ComputeKeyAsync, but some resolve to a file no filesystem walk
        // can predict (an MSBuild expression where the file name belongs, a walk-up rooted outside the
        // project's ancestor chain, an appended path that does not name a single file in the resolved
        // directory, a wildcard). Editing such a file to set <IsAspireHost>true</IsAspireHost> would leave
        // every fingerprinted mtime untouched, so a cached "not an AppHost" answer would survive forever and
        // the AppHost would stay undiscoverable. Skip the disk cache entirely for those projects and let
        // MSBuild answer each time; the in-process cache in AppHostInfoResolver still de-duplicates work
        // inside a single CLI invocation.
        if (!importDependencies.HasUnfingerprintableImport)
        {
            return false;
        }

        _logger.LogTrace(
            "Skipping AppHost info disk cache for {Project}; it depends on an import that cannot be fingerprinted",
            projectFile.FullName);
        return true;
    }

    internal static string ComputeKeyAsync(FileInfo projectFile, IEnvironment environment)
        => ComputeKeyAsync(projectFile, environment, DotNetAppHostProject.CollectImportDependencies(projectFile));

    internal static string ComputeKeyAsync(FileInfo projectFile, IEnvironment environment, in DotNetAppHostProject.AppHostImportDependencies importDependencies)
    {
        // Raw fingerprint shape:
        //   v1|/repo/app/AppHost.csproj|csproj=638831006400000000|assets=638831006410000000|...
        // Each file input is represented by a stable tag and its UTC last-write timestamp ticks,
        // or '-' when the file is absent/inaccessible. The raw fingerprint intentionally includes full
        // paths so two projects with identical mtimes cannot collide, but that makes it too long
        // and path-sensitive for a portable filename. Hash it with XxHash3 so the cache file is
        // short, filename-safe, and non-cryptographic (this is only cache identity, not security).
        var sb = new StringBuilder(512);
        sb.Append(SchemaVersion);
        sb.Append('|');
        sb.Append(CliPathHelper.NormalizePathCasing(projectFile.FullName, environment));
        sb.Append('|');
        AppendMtime(sb, projectFile.FullName, "csproj");

        var projectDir = projectFile.Directory?.FullName;
        if (projectDir is not null)
        {
            // obj/project.assets.json is NuGet's resolved package graph for this project.
            // The SDK also treats it as an incremental build input:
            // https://github.com/dotnet/sdk/blob/main/src/Tasks/Microsoft.NET.Build.Tasks/targets/Microsoft.NET.Sdk.targets
            // The AppHost inspection reads PackageReference/PackageVersion-derived items,
            // so the cache must notice changes that come from restore inputs outside the
            // .csproj itself: Central Package Management edits, transitive updates, SDK
            // changes that affect restore, or a fresh restore after package graph changes.
            AppendMtime(sb, Path.Combine(projectDir, "obj", "project.assets.json"), "assets");

            // Beyond the conventional files, fingerprint the custom files that DotNetAppHostProject's
            // prefilter lets decide whether this project is an AppHost. Without this, setting
            // <IsAspireHost>true</IsAspireHost> in such a file leaves every tracked mtime unchanged and the
            // CLI keeps serving the stale "not an AppHost" entry.
            //
            // Walk-up imports (e.g. a shared Aspire.Common.props pulled in with
            // $([MSBuild]::GetPathOfFileAbove(...))) bind to the nearest file of that name at or above the
            // importing file, so each name is statted at every ancestor level inside the loop below. Static
            // imports (e.g. "$(MSBuildThisFileDirectory)../shared/Directory.Build.props") name exactly one
            // file, which may sit outside the ancestor chain entirely, so those are statted by full path.
            var customImportFileNames = importDependencies.AncestorSearchFileNames;

            foreach (var exactPath in importDependencies.ExactFilePaths)
            {
                // The path is part of the tag here — unlike the per-directory entries below, these are not
                // positionally tied to a walk step, so two different imported files must not be
                // interchangeable in the fingerprint.
                AppendMtime(sb, exactPath, CliPathHelper.NormalizePathCasing(exactPath, environment));
            }

            // Walk up to the filesystem root and stat any Directory.Build.* / Directory.Packages.* /
            // global.json we find along the way. This matches MSBuild's own Directory.Build.* discovery,
            // which has no .git boundary and walks to the root of the drive
            // (https://learn.microsoft.com/visualstudio/msbuild/customize-by-directory), and keeps this
            // fingerprint in parity with DotNetAppHostProject's IsLikelyAppHost ancestor walk. If the two
            // walks disagreed, a Directory.Build.props above a nested .git could promote a project in the
            // classifier while an edit to that same file failed to invalidate this cache — a stale hit.
            // Files higher up shadow files lower down in MSBuild, but for cache invalidation
            // we just need to detect ANY change. AppendMtime records each entry as
            // "tag=ticks" (or "tag=-" when absent); the path itself is used only to stat the
            // file, not appended to the string. That is sufficient here because (a) the
            // project's absolute path is already at the head of the fingerprint, and (b) the
            // positional order of these per-directory entries in the walkup sequence
            // implicitly identifies which directory each tick belongs to.
            var dir = projectFile.Directory;
            while (dir is not null)
            {
                foreach (var siblingName in s_trackedSiblingFiles)
                {
                    AppendMtime(sb, Path.Combine(dir.FullName, siblingName), siblingName);
                }
                AppendMtime(sb, Path.Combine(dir.FullName, "global.json"), "globaljson");

                // Ordinal-sorted by the collector, so the same import set always produces the same key.
                foreach (var customName in customImportFileNames)
                {
                    AppendMtime(sb, Path.Combine(dir.FullName, customName), customName);
                }

                dir = dir.Parent;
            }
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var hash = XxHash3.Hash(bytes);
        return Convert.ToHexString(hash);
    }

    // "mtime" is shorthand for modification time: FileInfo.LastWriteTimeUtc converted to
    // DateTime ticks. We use UTC ticks instead of formatted timestamps so the fingerprint is
    // culture-invariant and stable across processes.
    private static void AppendMtime(StringBuilder sb, string path, string tag)
    {
        sb.Append('|');
        sb.Append(tag);
        sb.Append('=');
        try
        {
            var info = new FileInfo(path);
            if (info.Exists)
            {
                sb.Append(info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append('-');
            }
        }
        catch
        {
            // A stat failure (permission denied, transient IO) collapses to the "missing"
            // marker. Worst case we get a cache miss until the situation resolves.
            sb.Append('-');
        }
    }
}

internal interface IAppHostInfoDiskCache
{
    string GetCacheKey(FileInfo projectFile);
    Task<AppHostInfoCacheEntry?> TryGetAsync(FileInfo projectFile, CancellationToken cancellationToken);
    Task SetAsync(FileInfo projectFile, string expectedCacheKey, AppHostInfoCacheEntry entry, CancellationToken cancellationToken);
}

internal sealed record AppHostInfoCacheEntry
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "v1";

    [JsonPropertyName("exitCode")]
    public int ExitCode { get; init; }

    [JsonPropertyName("isAspireHost")]
    public bool IsAspireHost { get; init; }

    [JsonPropertyName("aspireHostingVersion")]
    public string? AspireHostingVersion { get; init; }

    [JsonPropertyName("isUsingCliBundle")]
    public bool IsUsingCliBundle { get; init; }

    [JsonPropertyName("userSecretsId")]
    public string? UserSecretsId { get; init; }

    [JsonPropertyName("runCommand")]
    public string? RunCommand { get; init; }

    [JsonPropertyName("targetPath")]
    public string? TargetPath { get; init; }

    [JsonPropertyName("runWorkingDirectory")]
    public string? RunWorkingDirectory { get; init; }

    [JsonPropertyName("runArguments")]
    public string? RunArguments { get; init; }

    [JsonPropertyName("targetFramework")]
    public string? TargetFramework { get; init; }

    [JsonPropertyName("targetFrameworks")]
    public string? TargetFrameworks { get; init; }
}
