// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Hosting.JavaScript.Internal.Workspace;

namespace Aspire.Hosting.JavaScript;

/// <summary>
/// Thin facade over the workspace reader components in
/// <see cref="Internal.Workspace"/>. Existing call sites in
/// <see cref="JavaScriptWorkspaceExtensions"/> use this entry point; new code
/// should prefer the more focused helpers under <c>Internal.Workspace</c>.
/// </summary>
internal static class JavaScriptWorkspaceReader
{
    /// <summary>
    /// Reads the <c>name</c> field from <c>&lt;directory&gt;/package.json</c>, e.g.
    /// <c>{ "name": "@example/web" }</c>.
    /// </summary>
    /// <returns>
    /// The package name, or <see langword="null"/> when the file does not exist or has no
    /// <c>name</c> field.
    /// </returns>
    public static string? TryReadPackageName(string directory)
    {
        var packageJsonPath = Path.Combine(directory, "package.json");
        if (!File.Exists(packageJsonPath))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(packageJsonPath);
            var packageJson = JsonSerializer.Deserialize<PackageJsonNameInfo>(stream);
            if (packageJson?.Name is { Length: > 0 } name)
            {
                return name;
            }
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }

        return null;
    }

    /// <summary>
    /// Reads workspace member glob patterns declared at the given root by combining
    /// <c>package.json#workspaces</c> (string array form, or <c>{ "packages": [...] }</c>
    /// form) and <c>pnpm-workspace.yaml#packages</c> (for example,
    /// <c>packages: ["packages/*", "apps/web"]</c>).
    /// </summary>
    /// <returns>
    /// The list of glob patterns in declaration order. Negated and otherwise
    /// unsupported patterns are returned for caller validation; see
    /// <see cref="WorkspacePatternValidator"/>.
    /// </returns>
    public static IReadOnlyList<string> ReadWorkspacePatterns(string rootPath)
    {
        var patterns = new List<string>();

        var packageJsonPath = Path.Combine(rootPath, "package.json");
        if (File.Exists(packageJsonPath))
        {
            try
            {
                var content = File.ReadAllText(packageJsonPath);
                patterns.AddRange(PackageJsonWorkspacesParser.Parse(content));
            }
            catch (IOException)
            {
            }
        }

        var pnpmYamlPath = Path.Combine(rootPath, "pnpm-workspace.yaml");
        if (File.Exists(pnpmYamlPath))
        {
            try
            {
                var content = File.ReadAllText(pnpmYamlPath);
                patterns.AddRange(PnpmWorkspaceYamlParser.Parse(content));
            }
            catch (IOException)
            {
            }
        }

        return patterns;
    }

    /// <summary>
    /// Expands workspace glob patterns to actual member directories under
    /// <paramref name="rootPath"/>.
    /// </summary>
    public static IReadOnlyList<string> ExpandWorkspacePatterns(string rootPath, IEnumerable<string> patterns)
        => WorkspacePatternExpander.Expand(rootPath, patterns);

    private sealed class PackageJsonNameInfo
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
