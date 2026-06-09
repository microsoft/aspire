// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Hosting.JavaScript.Internal.Workspace;

// The single member-discovery orchestrator for JS workspaces. It ties together the
// otherwise-independent pure parsers and the (filesystem-touching) pattern expander
// into one entry point that produces a fully populated WorkspaceInfo.
//
// This is the single documented filesystem boundary for member discovery: only the
// expander (Directory.Enumerate*), WorkspaceManifestDiscovery (File.Exists), and the
// per-member package.json name reads below touch disk. The JSON/YAML parsers and the
// validator remain pure.
//
// WHY this returns dir->name pairs: package managers select a workspace member by its
// package NAME (pnpm --filter <name>, npm --workspace=<name>, yarn workspace <name>,
// bun --filter <name>) and member-typo validation also compares against the declared
// NAME. But the expander resolves member DIRECTORIES (it walks the glob to dirs that
// contain a package.json). The user supplies the name, so discovery reads each member
// directory's package.json#name to bridge the two — otherwise validation would compare
// a name against a directory and silently mismatch.
internal static class WorkspaceMemberDiscovery
{
    /// <summary>
    /// Discovers the members of a JS workspace rooted at <paramref name="workspaceRootPath"/>.
    /// </summary>
    /// <param name="workspaceRootPath">The absolute path of the workspace root directory.</param>
    /// <param name="packageManagerExecutable">
    /// The package-manager executable name ("npm", "yarn", "pnpm", or "bun"). This selects
    /// where the workspace declaration is read from: pnpm reads pnpm-workspace.yaml while
    /// npm/yarn/bun read the "workspaces" field of the root package.json.
    /// </param>
    /// <returns>
    /// A populated <see cref="WorkspaceInfo"/> carrying the resolved member directories,
    /// the dir-&gt;name member pairs, the root manifest files/dirs, and the root app name.
    /// </returns>
    public static WorkspaceInfo Discover(string workspaceRootPath, string packageManagerExecutable)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspaceRootPath);
        ArgumentException.ThrowIfNullOrEmpty(packageManagerExecutable);

        var patterns = ReadDeclaredPatterns(workspaceRootPath, packageManagerExecutable);

        var memberDirs = WorkspacePatternExpander.Expand(workspaceRootPath, patterns);

        var members = new List<WorkspaceMember>(memberDirs.Count);
        foreach (var dir in memberDirs)
        {
            members.Add(new WorkspaceMember(dir, ReadMemberPackageName(workspaceRootPath, dir)));
        }

        var rootManifests = WorkspaceManifestDiscovery.Discover(workspaceRootPath);
        var appName = TryReadPackageName(Path.Combine(workspaceRootPath, "package.json")) ?? string.Empty;

        return new WorkspaceInfo(
            rootManifests.RootFiles,
            rootManifests.RootDirs,
            memberDirs,
            members,
            appName);
    }

    private static IReadOnlyList<string> ReadDeclaredPatterns(string workspaceRootPath, string packageManagerExecutable)
    {
        // pnpm declares members in pnpm-workspace.yaml; npm, yarn (classic + Berry), and bun
        // all share the root package.json "workspaces" field.
        if (string.Equals(packageManagerExecutable, "pnpm", StringComparison.Ordinal))
        {
            var yamlPath = Path.Combine(workspaceRootPath, "pnpm-workspace.yaml");
            return File.Exists(yamlPath)
                ? PnpmWorkspaceYamlParser.Parse(File.ReadAllText(yamlPath))
                : [];
        }

        var packageJsonPath = Path.Combine(workspaceRootPath, "package.json");
        return File.Exists(packageJsonPath)
            ? PackageJsonWorkspacesParser.Parse(File.ReadAllText(packageJsonPath))
            : [];
    }

    private static string ReadMemberPackageName(string workspaceRootPath, string relativeDir)
    {
        var packageJsonPath = Path.Combine(
            workspaceRootPath,
            relativeDir.Replace('/', Path.DirectorySeparatorChar),
            "package.json");

        // A member without a "name" field is uncommon but valid; fall back to the directory
        // name so the member still has a stable identifier (matches how tooling labels them).
        return TryReadPackageName(packageJsonPath)
            ?? relativeDir[(relativeDir.LastIndexOf('/') + 1)..];
    }

    /// <summary>
    /// Reads the "name" field from a package.json file, or <see langword="null"/> when the
    /// file is missing, unreadable, invalid JSON, or has no "name".
    /// </summary>
    internal static string? TryReadPackageName(string packageJsonPath)
    {
        if (!File.Exists(packageJsonPath))
        {
            return null;
        }
        try
        {
            using var stream = File.OpenRead(packageJsonPath);
            var info = JsonSerializer.Deserialize<PackageJsonNameInfo>(stream);
            return string.IsNullOrEmpty(info?.Name) ? null : info.Name;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private sealed class PackageJsonNameInfo
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
