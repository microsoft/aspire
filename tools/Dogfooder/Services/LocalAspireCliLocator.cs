// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Runtime.InteropServices;

namespace Aspire.Dogfooder.Services;

/// <summary>
/// Resolves the path to a locally-built Aspire CLI executable inside the
/// current repo's <c>artifacts/bin/Aspire.Cli/</c> output tree, so the
/// embedded dogfooding shell can <c>which aspire</c> against the freshly
/// built bits rather than the globally-installed tool.
/// </summary>
internal interface ILocalAspireCliLocator
{
    /// <summary>
    /// The directory containing the locally-built <c>aspire</c> executable,
    /// or null if no build output was found.
    /// </summary>
    string? CliDirectory { get; }

    /// <summary>Full path to the built executable, or null if not found.</summary>
    string? CliExecutablePath { get; }
}

internal sealed class LocalAspireCliLocator : ILocalAspireCliLocator
{
    public LocalAspireCliLocator()
    {
        var repoRoot = FindRepoRoot();
        if (repoRoot is null)
        {
            return;
        }

        // The Arcade-driven build emits per-configuration per-TFM directories
        // under artifacts/bin/Aspire.Cli/{Configuration}/{TFM}/. Pick the most
        // recently built executable so a developer who built with `--rebuild`
        // in Release picks that up automatically.
        var cliRoot = Path.Combine(repoRoot, "artifacts", "bin", "Aspire.Cli");
        if (!Directory.Exists(cliRoot))
        {
            return;
        }

        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "aspire.exe" : "aspire";

        // Enumerate Debug/<tfm>/aspire and Release/<tfm>/aspire; sort by mtime.
        var candidates = Directory
            .EnumerateFiles(cliRoot, exeName, SearchOption.AllDirectories)
            .Select(p => new FileInfo(p))
            .Where(fi => fi.Exists)
            .OrderByDescending(fi => fi.LastWriteTimeUtc)
            .ToList();

        if (candidates.Count == 0)
        {
            return;
        }

        CliExecutablePath = candidates[0].FullName;
        CliDirectory = candidates[0].DirectoryName;
    }

    public string? CliDirectory { get; }
    public string? CliExecutablePath { get; }

    private static string? FindRepoRoot()
    {
        // Walk up from the assembly directory until we hit a folder containing
        // global.json. We don't use .git because worktrees place .git as a
        // file pointing elsewhere, which still resolves but is less reliable
        // across enlistment shapes; global.json is universally present at the
        // repo root in this codebase.
        var startDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        var dir = startDir is null ? null : new DirectoryInfo(startDir);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "global.json")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
