// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Parses the top-level "packages" sequence from a pnpm-workspace.yaml document.
//
// Spec / reference implementation:
// - pnpm workspaces docs: https://pnpm.io/workspaces
// - pnpm-workspace.yaml schema:
//   https://pnpm.io/pnpm-workspace_yaml#packages
// - pnpm CLI reference impl (find-workspace-packages):
//   https://github.com/pnpm/pnpm/tree/main/workspace/find-workspace-packages
//
// Supported pnpm-workspace.yaml shape:
//   packages:
//     - "packages/*"
//     - "apps/web"
//
// Flow-style YAML parses to the same sequence:
//   packages: ["packages/*", "apps/web"]
//
// We deserialize only the fields Aspire needs instead of modeling pnpm's whole
// workspace schema. Real pnpm-workspace.yaml files often contain unrelated
// settings such as catalog, overrides, onlyBuiltDependencies, trustPolicy, etc.;
// those must remain tolerated because pnpm, not Aspire, owns their semantics.
//
// All glob semantics (recursive, negated, non-trailing star) are handled by
// callers (see WorkspacePatternValidator and WorkspacePatternMatcher).

using YamlDotNet.Serialization;

namespace Aspire.Hosting.JavaScript.Internal.Workspace;

internal static class PnpmWorkspaceYamlParser
{
    private static readonly IDeserializer s_deserializer = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Reads workspace patterns from the <c>packages:</c> field of a
    /// pnpm-workspace.yaml document.
    /// </summary>
    /// <param name="yamlContent">The textual content of a pnpm-workspace.yaml file.</param>
    /// <returns>
    /// The list of pattern strings declared under <c>packages</c>, in declaration order.
    /// Returns an empty list when the document is invalid YAML, the root is not a mapping,
    /// the <c>packages</c> key is missing, or its value is not a sequence.
    /// </returns>
    public static IReadOnlyList<string> Parse(string yamlContent)
    {
        ArgumentNullException.ThrowIfNull(yamlContent);

        var workspace = TryDeserialize(yamlContent);
        return workspace?.Packages?.Where(static p => !string.IsNullOrEmpty(p)).ToArray() ?? [];
    }

    /// <summary>
    /// Reads the optional <c>injectWorkspacePackages</c> setting used by pnpm 10's non-legacy
    /// <c>pnpm deploy</c> implementation.
    /// </summary>
    /// <param name="yamlContent">The textual content of a pnpm-workspace.yaml file.</param>
    /// <returns>
    /// <see langword="true"/> or <see langword="false"/> when the setting is present as a
    /// boolean scalar; otherwise <see langword="null"/>.
    /// </returns>
    public static bool? ParseInjectWorkspacePackages(string yamlContent)
    {
        ArgumentNullException.ThrowIfNull(yamlContent);

        var workspace = TryDeserialize(yamlContent);
        return workspace?.InjectWorkspacePackages ?? workspace?.InjectWorkspacePackagesKebab;
    }

    private static PnpmWorkspaceYaml? TryDeserialize(string yamlContent)
    {
        if (yamlContent.Length == 0)
        {
            return null;
        }

        try
        {
            using var reader = new StringReader(yamlContent);
            return s_deserializer.Deserialize<PnpmWorkspaceYaml>(reader);
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return null;
        }
    }

    private sealed class PnpmWorkspaceYaml
    {
        [YamlMember(Alias = "packages")]
        public List<string>? Packages { get; set; }

        [YamlMember(Alias = "injectWorkspacePackages")]
        public bool? InjectWorkspacePackages { get; set; }

        [YamlMember(Alias = "inject-workspace-packages")]
        public bool? InjectWorkspacePackagesKebab { get; set; }
    }
}
