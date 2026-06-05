// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Dogfooder.State;

/// <summary>
/// Per-session scratch directory that owns every file the dogfood run
/// produces: built NuGet packages, build/terminal log files, the
/// session-local NuGet global-packages cache, and a <c>dogfood.json</c>
/// manifest describing the chosen scenario + inputs. The terminal is
/// launched with this directory as its current working directory so any
/// <c>aspire new</c> output also lands here.
/// </summary>
/// <remarks>
/// <para>
/// Created up-front by <c>DogfoodSessionPreparer</c> before any build step
/// so the build log can be written into <see cref="LogsDir"/> immediately,
/// and the <c>NUGET_PACKAGES</c> environment variable can be pointed at
/// <see cref="NuGetCacheDir"/> for the spawned shell — which prevents the
/// CLI's restore code path from satisfying requests from the user's
/// machine-wide cache (which would silently bypass the local proxy and
/// defeat the whole point of the dogfood run).
/// </para>
/// <para>
/// The workspace lives under <c>Directory.CreateTempSubdirectory("aspire-dogfood-")</c>
/// rather than inside the repo tree so concurrent dogfooder runs in
/// different worktrees don't collide and the OS cleans the directory up
/// when its temp-retention policy fires. We deliberately do NOT delete the
/// directory on session shutdown — the user often wants to inspect the
/// log files after the TUI exits.
/// </para>
/// </remarks>
internal sealed class SessionWorkspace
{
    public SessionWorkspace(string root, string logsDir, string nugetCacheDir, string nugetHttpCacheDir, string packagesDir, string dogfoodJsonPath)
    {
        Root = root;
        LogsDir = logsDir;
        NuGetCacheDir = nugetCacheDir;
        NuGetHttpCacheDir = nugetHttpCacheDir;
        PackagesDir = packagesDir;
        DogfoodJsonPath = dogfoodJsonPath;
    }

    public string Root { get; }
    public string LogsDir { get; }
    public string NuGetCacheDir { get; }

    /// <summary>
    /// Per-session NuGet v3 HTTP cache directory (env: <c>NUGET_HTTP_CACHE_PATH</c>).
    /// Distinct from <see cref="NuGetCacheDir"/> (the global-packages folder
    /// — extracted package contents) because NuGet keeps two independent
    /// caches and setting only one leaves the other warm from previous
    /// sessions. Without isolating the HTTP cache, <c>dotnet package search</c>
    /// and the v3 service-index/registration lookups can silently return
    /// nuget.org responses that were cached during a non-dogfood run,
    /// bypassing the local proxy entirely.
    /// See https://learn.microsoft.com/nuget/consume-packages/managing-the-global-packages-and-cache-folders
    /// </summary>
    public string NuGetHttpCacheDir { get; }
    public string PackagesDir { get; }
    public string DogfoodJsonPath { get; }

    /// <summary>
    /// Creates the workspace and all required subdirectories. The returned
    /// instance is safe to use immediately. Throws on filesystem failure
    /// (out of space, permissions, etc.) — callers should let the failure
    /// surface in the preparation log rather than swallowing it because a
    /// missing workspace makes downstream phases meaningless.
    /// </summary>
    public static SessionWorkspace Create()
    {
        // CreateTempSubdirectory generates a cryptographically-random
        // suffix so two concurrent dogfooder processes (e.g. on different
        // worktrees) get different directories without us having to
        // coordinate.
        var root = Directory.CreateTempSubdirectory("aspire-dogfood-");
        var logsDir = Path.Combine(root.FullName, "logs");
        var nugetCacheDir = Path.Combine(root.FullName, "nugetcache");
        var nugetHttpCacheDir = Path.Combine(root.FullName, "nugethttpcache");
        var packagesDir = Path.Combine(root.FullName, "packages");
        Directory.CreateDirectory(logsDir);
        Directory.CreateDirectory(nugetCacheDir);
        Directory.CreateDirectory(nugetHttpCacheDir);
        Directory.CreateDirectory(packagesDir);

        var dogfoodJson = Path.Combine(root.FullName, "dogfood.json");
        return new SessionWorkspace(root.FullName, logsDir, nugetCacheDir, nugetHttpCacheDir, packagesDir, dogfoodJson);
    }

    /// <summary>
    /// Serialises <paramref name="manifest"/> into <see cref="DogfoodJsonPath"/>.
    /// Indented JSON so the file is greppable and diff-friendly when the
    /// user opens it after the session ends. Failure to write is non-fatal
    /// — the manifest is documentation, not a hard input to anything
    /// downstream.
    /// </summary>
    public void WriteManifest(DogfoodManifest manifest)
    {
        try
        {
            var json = JsonSerializer.Serialize(manifest, s_manifestJsonOptions);
            File.WriteAllText(DogfoodJsonPath, json);
        }
        catch
        {
            // Best-effort; if disk is full or permissions blow up the
            // session is more important than the manifest file.
        }
    }

    private static readonly JsonSerializerOptions s_manifestJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>
/// On-disk manifest written to <c>dogfood.json</c> at the root of every
/// session workspace. Records the scenario picked, the user inputs that
/// drove its plan, and the resolved version stamping so the user (or a
/// follow-up bug report) can reproduce the exact run later.
/// </summary>
internal sealed record DogfoodManifest(
    DateTimeOffset CreatedAt,
    string ScenarioId,
    string ScenarioDisplayName,
    IReadOnlyDictionary<string, string?> Inputs,
    string? ResolvedVCurrentVersion,
    string? PackageVersionStamp,
    string WorkspaceRoot,
    string LogsDir,
    string NuGetCacheDir,
    string NuGetHttpCacheDir,
    string PackagesDir);
