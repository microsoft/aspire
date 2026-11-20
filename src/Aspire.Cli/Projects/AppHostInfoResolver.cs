// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Cli.Caching;
using Aspire.Cli.DotNet;
using Semver;

namespace Aspire.Cli.Projects;

internal interface IAppHostInfoResolver
{
    Task<AppHostProjectInfo> GetAppHostInfoAsync(FileInfo projectFile, CancellationToken cancellationToken);
}

internal sealed class AppHostInfoResolver(IDotNetCliRunner runner, IAppHostInfoDiskCache diskCache) : IAppHostInfoResolver
{
    private readonly ConcurrentDictionary<string, Task<AppHostProjectInfo>> _cache = new();

    public async Task<AppHostProjectInfo> GetAppHostInfoAsync(FileInfo projectFile, CancellationToken cancellationToken)
    {
        // Refresh so FileInfo reflects the on-disk mtime even when callers pass a long-lived instance.
        projectFile.Refresh();
        var key = diskCache.GetCacheKey(projectFile);
        var task = GetOrAddSharedFetch(key, projectFile);

        try
        {
            // The MSBuild probe is shared by all callers for this project snapshot, so it cannot
            // use any one caller's token. Apply cancellation to each waiter instead; otherwise one
            // cancelled validation could cancel the shared probe for unrelated callers.
            var result = await task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                // Transient MSBuild failures should not poison long-lived CLI processes.
                _cache.TryRemove(key, out _);
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !task.IsCompleted)
        {
            throw;
        }
        catch
        {
            // A cancelled or faulted in-flight probe must not become the answer for later runs.
            _cache.TryRemove(key, out _);
            throw;
        }
    }

    private Task<AppHostProjectInfo> GetOrAddSharedFetch(string key, FileInfo projectFile)
    {
        while (true)
        {
            if (_cache.TryGetValue(key, out var existingTask))
            {
                return existingTask;
            }

            // ConcurrentDictionary.GetOrAdd can run value factories more than once under contention.
            // Add a TaskCompletionSource placeholder first, then run the MSBuild probe only if this
            // caller won the TryAdd race. Losers loop back and await the winner's task.
            var completion = new TaskCompletionSource<AppHostProjectInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_cache.TryAdd(key, completion.Task))
            {
                _ = CompleteSharedFetchAsync(key, projectFile, completion);
                return completion.Task;
            }
        }
    }

    private async Task CompleteSharedFetchAsync(string key, FileInfo projectFile, TaskCompletionSource<AppHostProjectInfo> completion)
    {
        try
        {
            var result = await FetchAppHostInfoCoreAsync(projectFile, CancellationToken.None).ConfigureAwait(false);
            completion.TrySetResult(result);
        }
        catch (Exception ex)
        {
            // Match the retry behavior in GetAppHostInfoAsync: transient MSBuild/cache failures
            // should complete all current waiters, then allow the next caller to try again.
            completion.TrySetException(ex);
            _cache.TryRemove(key, out _);
        }
    }

    private async Task<AppHostProjectInfo> FetchAppHostInfoCoreAsync(FileInfo projectFile, CancellationToken cancellationToken)
    {
        // First, see if a previous CLI invocation already cached the answer on disk. The
        // cache key includes mtimes of the tracked inputs that affect this metadata query, so a
        // hit means those inputs have not changed since that previous evaluation. This is not a
        // build-output cache; normal build/no-build semantics still decide whether .cs changes are
        // compiled before the AppHost runs.
        var diskEntry = await diskCache.TryGetAsync(projectFile, cancellationToken).ConfigureAwait(false);
        if (diskEntry is not null)
        {
            return new AppHostProjectInfo(
                ExitCode: diskEntry.ExitCode,
                IsAspireHost: diskEntry.IsAspireHost,
                AspireHostingVersion: diskEntry.AspireHostingVersion,
                IsUsingCliBundle: diskEntry.IsUsingCliBundle,
                UserSecretsId: diskEntry.UserSecretsId,
                RunCommand: diskEntry.RunCommand,
                TargetPath: diskEntry.TargetPath,
                RunWorkingDirectory: diskEntry.RunWorkingDirectory,
                RunArguments: diskEntry.RunArguments,
                TargetFramework: diskEntry.TargetFramework,
                TargetFrameworks: diskEntry.TargetFrameworks);
        }

        // Capture the input fingerprint before evaluating MSBuild. If any tracked input changes
        // while MSBuild is running, SetAsync will skip writing this now-stale result.
        var expectedCacheKey = diskCache.GetCacheKey(projectFile);

        // Mirror the property/item shape used by DotNetCliRunner.GetAppHostInformationAsync and
        // additionally request AspireUseCliBundle, UserSecretsId, and run metadata so the CLI bundle
        // handoff, --isolated user-secrets clone, and post-build AppHost launch path do not require
        // their own MSBuild evaluations.
        // Adding extra -getProperty names is an evaluation-only cost.
        //
        // The Run* properties (RunCommand, RunArguments, RunWorkingDirectory) are only
        // populated for the direct-launch contract after ComputeRunArguments has run, so
        // request that target here. This matches the property values `dotnet run` would
        // resolve before launching the AppHost. TargetFrameworks is also cached so
        // DotNetAppHostProject can detect multi-targeted AppHosts and fall back to
        // `dotnet run`, which already handles target framework selection.
        //
        // In the .NET SDK, ComputeRunArguments is intentionally a placeholder target with no
        // DependsOnTargets; it exists so projects can override RunCommand/RunArguments after
        // evaluation. Requesting it preserves that extension point without causing the default
        // probe to build the AppHost. See:
        // https://github.com/dotnet/sdk/blob/main/src/Tasks/Microsoft.NET.Build.Tasks/targets/Microsoft.NET.Sdk.targets
        var (exitCode, jsonDocument) = await runner.GetProjectItemsAndPropertiesAsync(
            projectFile,
            items: ["PackageReference", "AspireProjectOrPackageReference", "PackageVersion"],
            properties: ["IsAspireHost", "AspireHostingSDKVersion", "_AspireAppHostPackageVersion", "ProjectAssetsFile", "AspireUseCliBundle", "UserSecretsId", "RunCommand", "TargetPath", "RunWorkingDirectory", "RunArguments", "TargetFramework", "TargetFrameworks"],
            targets: ["ComputeRunArguments"],
            new ProcessInvocationOptions(),
            cancellationToken).ConfigureAwait(false);

        if (exitCode != 0 || jsonDocument is null)
        {
            // Do not persist failure responses to memory or disk — a transient MSBuild error should
            // not sit in a cache and short-circuit later successful evaluations.
            // DotNetCliRunner already logged the failing MSBuild stdout/stderr before returning
            // null here; keep the non-zero exit code so callers can surface the normal
            // "not buildable AppHost" project-resolution behavior instead of treating this as a
            // negative cache entry.
            return new AppHostProjectInfo(ExitCode: exitCode, IsAspireHost: false, AspireHostingVersion: null, IsUsingCliBundle: false, UserSecretsId: null, RunCommand: null, TargetPath: null, RunWorkingDirectory: null, RunArguments: null, TargetFramework: null, TargetFrameworks: null);
        }

        AppHostProjectInfo info;
        using (jsonDocument)
        {
            var msbuildOutput = JsonSerializer.Deserialize(
                jsonDocument.RootElement,
                JsonSourceGenerationContext.Default.AppHostProjectInspectionOutput);

            info = ParseAppHostInfo(msbuildOutput, exitCode);
        }

        // Persist successful evaluations so the next CLI process can short-circuit MSBuild
        // entirely when the inputs are unchanged. Failures inside the cache write are
        // swallowed; cache misses never break a run.
        await diskCache.SetAsync(projectFile, expectedCacheKey, new AppHostInfoCacheEntry
        {
            ExitCode = info.ExitCode,
            IsAspireHost = info.IsAspireHost,
            AspireHostingVersion = info.AspireHostingVersion,
            IsUsingCliBundle = info.IsUsingCliBundle,
            UserSecretsId = info.UserSecretsId,
            RunCommand = info.RunCommand,
            TargetPath = info.TargetPath,
            RunWorkingDirectory = info.RunWorkingDirectory,
            RunArguments = info.RunArguments,
            TargetFramework = info.TargetFramework,
            TargetFrameworks = info.TargetFrameworks,
        }, cancellationToken).ConfigureAwait(false);

        return info;
    }

    internal static AppHostProjectInfo ParseAppHostInfo(AppHostProjectInspectionOutput? msbuildOutput, int exitCode)
    {
        var properties = msbuildOutput?.Properties;
        if (properties is null)
        {
            return new AppHostProjectInfo(ExitCode: exitCode, IsAspireHost: false, AspireHostingVersion: null, IsUsingCliBundle: false, UserSecretsId: null, RunCommand: null, TargetPath: null, RunWorkingDirectory: null, RunArguments: null, TargetFramework: null, TargetFrameworks: null);
        }

        var isUsingCliBundle = string.Equals(properties.AspireUseCliBundle, "true", StringComparison.OrdinalIgnoreCase);

        var userSecretsId = string.IsNullOrWhiteSpace(properties.UserSecretsId) ? null : properties.UserSecretsId;
        var runCommand = string.IsNullOrWhiteSpace(properties.RunCommand) ? null : properties.RunCommand;
        var targetPath = string.IsNullOrWhiteSpace(properties.TargetPath) ? null : properties.TargetPath;
        var runWorkingDirectory = string.IsNullOrWhiteSpace(properties.RunWorkingDirectory) ? null : properties.RunWorkingDirectory;
        var runArguments = string.IsNullOrWhiteSpace(properties.RunArguments) ? null : properties.RunArguments;
        var targetFramework = string.IsNullOrWhiteSpace(properties.TargetFramework) ? null : properties.TargetFramework;
        var targetFrameworks = string.IsNullOrWhiteSpace(properties.TargetFrameworks) ? null : properties.TargetFrameworks;

        var items = msbuildOutput?.Items;
        // Package props are not imported until the first restore. Respect an explicit opt-out.
        var isAspireHost = string.Equals(properties.IsAspireHost, "true", StringComparison.OrdinalIgnoreCase)
            || (string.IsNullOrWhiteSpace(properties.IsAspireHost)
                && items?.PackageReference?.Any(item => string.Equals(item.Identity, "Aspire.Hosting.AppHost", StringComparison.OrdinalIgnoreCase)) == true);

        if (!isAspireHost)
        {
            return new AppHostProjectInfo(ExitCode: exitCode, IsAspireHost: false, AspireHostingVersion: null, IsUsingCliBundle: isUsingCliBundle, UserSecretsId: userSecretsId, RunCommand: runCommand, TargetPath: targetPath, RunWorkingDirectory: runWorkingDirectory, RunArguments: runArguments, TargetFramework: targetFramework, TargetFrameworks: targetFrameworks);
        }

        // project.assets.json is the authoritative NuGet result for a restored project. The
        // generated package property is the next-best source because it comes from the imported
        // Aspire.Hosting.AppHost build targets. Both are empty before the first restore.
        var aspireHostingVersion = GetResolvedPackageVersion(properties.ProjectAssetsFile, "Aspire.Hosting")
            ?? GetConcreteVersion(properties.AspireAppHostPackageVersion);

        if (aspireHostingVersion is null && items is not null)
        {
            // Preserve the effective-reference order used by NuGet: a direct Hosting reference
            // wins over the AppHost metapackage, and Central Package Management contributes only
            // to references that are actually present. Orphan PackageVersion entries must not
            // override a different project reference or the SDK default.
            aspireHostingVersion = GetEffectivePackageVersion(items.PackageReference, items.PackageVersion, "Aspire.Hosting")
                ?? GetEffectivePackageVersion(items.PackageReference, items.PackageVersion, "Aspire.Hosting.AppHost")
                ?? GetEffectivePackageVersion(items.AspireProjectOrPackageReference, items.PackageVersion, "Aspire.Hosting")
                ?? GetEffectivePackageVersion(items.AspireProjectOrPackageReference, items.PackageVersion, "Aspire.Hosting.AppHost");
        }

        aspireHostingVersion ??= string.IsNullOrWhiteSpace(properties.AspireHostingSDKVersion)
            ? null
            : properties.AspireHostingSDKVersion;

        return new AppHostProjectInfo(ExitCode: exitCode, IsAspireHost: true, AspireHostingVersion: aspireHostingVersion, IsUsingCliBundle: isUsingCliBundle, UserSecretsId: userSecretsId, RunCommand: runCommand, TargetPath: targetPath, RunWorkingDirectory: runWorkingDirectory, RunArguments: runArguments, TargetFramework: targetFramework, TargetFrameworks: targetFrameworks);
    }

    private static string? GetEffectivePackageVersion(
        IReadOnlyList<AppHostProjectInspectionItem>? references,
        IReadOnlyList<AppHostProjectInspectionItem>? centralVersions,
        string packageId)
    {
        if (references is null)
        {
            return null;
        }

        foreach (var reference in references)
        {
            if (!string.Equals(reference.Identity, packageId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return !string.IsNullOrWhiteSpace(reference.Version)
                ? reference.Version
                : GetPackageVersionFromItems(centralVersions, packageId);
        }

        return null;
    }

    private static string? GetPackageVersionFromItems(IReadOnlyList<AppHostProjectInspectionItem>? items, string packageId)
    {
        return items?.FirstOrDefault(item =>
            string.Equals(item.Identity, packageId, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(item.Version))?.Version;
    }

    private static string? GetConcreteVersion(string? version)
        => SemVersion.TryParse(version, SemVersionStyles.Any, out _) ? version : null;

    private static string? GetResolvedPackageVersion(string? assetsFilePath, string packageId)
    {
        if (string.IsNullOrWhiteSpace(assetsFilePath) || !File.Exists(assetsFilePath))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(assetsFilePath);
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("libraries", out var libraries))
            {
                return null;
            }

            // project.assets.json identifies resolved packages as keys such as
            //   "Aspire.Hosting/17.1.0-preview.1"
            // and may contain project references with the same version-shaped suffix.
            var prefix = $"{packageId}/";
            foreach (var library in libraries.EnumerateObject())
            {
                if (library.Value.TryGetProperty("type", out var type) &&
                    string.Equals(type.GetString(), "package", StringComparison.OrdinalIgnoreCase) &&
                    library.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return GetConcreteVersion(library.Name[prefix.Length..]);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A missing, transiently-written, or malformed assets file is equivalent to an
            // unrestored project for this best-effort fallback path.
        }

        return null;
    }
}

internal sealed record AppHostProjectInfo(
    int ExitCode,
    bool IsAspireHost,
    string? AspireHostingVersion,
    bool IsUsingCliBundle,
    string? UserSecretsId,
    string? RunCommand,
    string? TargetPath,
    string? RunWorkingDirectory,
    string? RunArguments,
    string? TargetFramework,
    string? TargetFrameworks);

internal sealed record AppHostProjectInspectionOutput
{
    [JsonPropertyName("Properties")]
    public AppHostProjectInspectionProperties? Properties { get; init; }

    [JsonPropertyName("Items")]
    public AppHostProjectInspectionItems? Items { get; init; }
}

internal sealed record AppHostProjectInspectionProperties
{
    [JsonPropertyName("IsAspireHost")]
    public string? IsAspireHost { get; init; }

    [JsonPropertyName("AspireHostingSDKVersion")]
    public string? AspireHostingSDKVersion { get; init; }

    [JsonPropertyName("_AspireAppHostPackageVersion")]
    public string? AspireAppHostPackageVersion { get; init; }

    [JsonPropertyName("ProjectAssetsFile")]
    public string? ProjectAssetsFile { get; init; }

    [JsonPropertyName("AspireUseCliBundle")]
    public string? AspireUseCliBundle { get; init; }

    [JsonPropertyName("UserSecretsId")]
    public string? UserSecretsId { get; init; }

    [JsonPropertyName("RunCommand")]
    public string? RunCommand { get; init; }

    [JsonPropertyName("TargetPath")]
    public string? TargetPath { get; init; }

    [JsonPropertyName("RunWorkingDirectory")]
    public string? RunWorkingDirectory { get; init; }

    [JsonPropertyName("RunArguments")]
    public string? RunArguments { get; init; }

    [JsonPropertyName("TargetFramework")]
    public string? TargetFramework { get; init; }

    [JsonPropertyName("TargetFrameworks")]
    public string? TargetFrameworks { get; init; }
}

internal sealed record AppHostProjectInspectionItems
{
    [JsonPropertyName("PackageReference")]
    public IReadOnlyList<AppHostProjectInspectionItem>? PackageReference { get; init; }

    [JsonPropertyName("AspireProjectOrPackageReference")]
    public IReadOnlyList<AppHostProjectInspectionItem>? AspireProjectOrPackageReference { get; init; }

    [JsonPropertyName("PackageVersion")]
    public IReadOnlyList<AppHostProjectInspectionItem>? PackageVersion { get; init; }
}

internal sealed record AppHostProjectInspectionItem
{
    [JsonPropertyName("Identity")]
    public string? Identity { get; init; }

    [JsonPropertyName("Version")]
    public string? Version { get; init; }
}
