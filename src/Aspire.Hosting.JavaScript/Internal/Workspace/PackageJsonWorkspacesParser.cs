// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Parses the "workspaces" field from a package.json document. This is the
// declaration shared by npm, Yarn, and bun workspaces.
//
// Spec / reference implementations:
// - npm RFC 0026 (workspaces):
//   https://github.com/npm/rfcs/blob/main/accepted/0026-workspaces.md
// - npm CLI reference impl:
//   https://github.com/npm/cli/blob/latest/lib/utils/get-workspaces.js
// - Yarn classic workspaces:
//   https://classic.yarnpkg.com/lang/en/docs/workspaces/
// - Yarn Berry workspaces:
//   https://yarnpkg.com/features/workspaces
// - bun workspaces:
//   https://bun.sh/docs/install/workspaces
//
// Supported package.json declaration shapes:
//   {
//     "name": "workspace-root",
//     "private": true,
//     "workspaces": ["packages/*", "apps/web"]   // string array (npm/yarn classic/bun)
//   }
//   {
//     "name": "workspace-root",
//     "private": true,
//     "workspaces": {
//       "packages": ["packages/*", "apps/*"]      // object form (yarn classic)
//     }
//   }
//
// All glob semantics (recursive, negated, non-trailing star) are handled by
// callers (see WorkspacePatternValidator) — this
// parser just extracts the strings.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Hosting.JavaScript.Internal.Workspace;

internal static class PackageJsonWorkspacesParser
{
    /// <summary>
    /// Reads workspace patterns from a package.json document.
    /// </summary>
    /// <param name="packageJsonContent">The textual content of a package.json file.</param>
    /// <returns>
    /// The list of pattern strings declared under <c>workspaces</c>, in declaration order.
    /// Returns an empty list when the document is invalid JSON, has no <c>workspaces</c>
    /// field, or has a workspace declaration shape that does not match the documented npm/Yarn forms.
    /// </returns>
    public static IReadOnlyList<string> Parse(string packageJsonContent)
    {
        ArgumentNullException.ThrowIfNull(packageJsonContent);

        var arrayDeclaration = TryDeserialize<PackageJsonWorkspaceArrayDeclaration>(packageJsonContent);
        if (arrayDeclaration?.Workspaces is not null)
        {
            return FilterPatterns(arrayDeclaration.Workspaces);
        }

        var objectDeclaration = TryDeserialize<PackageJsonWorkspaceObjectDeclaration>(packageJsonContent);
        if (objectDeclaration?.Workspaces?.Packages is not null)
        {
            return FilterPatterns(objectDeclaration.Workspaces.Packages);
        }

        return [];
    }

    private static T? TryDeserialize<T>(string packageJsonContent)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(packageJsonContent);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static IReadOnlyList<string> FilterPatterns(IEnumerable<string?> patterns) =>
        patterns.Where(static p => !string.IsNullOrEmpty(p)).Select(static p => p!).ToArray();

    private sealed class PackageJsonWorkspaceArrayDeclaration
    {
        [JsonPropertyName("workspaces")]
        public List<string>? Workspaces { get; set; }
    }

    private sealed class PackageJsonWorkspaceObjectDeclaration
    {
        [JsonPropertyName("workspaces")]
        public PackageJsonWorkspaceObject? Workspaces { get; set; }
    }

    private sealed class PackageJsonWorkspaceObject
    {
        [JsonPropertyName("packages")]
        public List<string>? Packages { get; set; }
    }
}
