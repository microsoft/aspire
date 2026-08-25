// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Aspire.Hosting.Radius.Publishing;

/// <summary>
/// Resolves the Kubernetes context of the active <c>rad</c> workspace.
/// </summary>
/// <remarks>
/// Several deploy-time steps must talk to the same cluster <c>rad deploy</c> will target, and the
/// ambient <c>kubectl</c> current-context is not a reliable stand-in: a user can switch kubectl
/// contexts without switching rad workspaces. Both <see cref="SealedSecretApplyStep"/> (which
/// applies a <c>SealedSecret</c>) and the control plane version gate in
/// <see cref="RadiusDeploymentPipelineStep"/> (which reads the installed control plane version)
/// resolve the context through this type so they cannot disagree about which cluster is being
/// inspected.
/// </remarks>
internal static class RadiusWorkspaceKubeContext
{
    internal const string OverrideEnvironmentVariable = "ASPIRE_RADIUS_KUBE_CONTEXT";

    internal static string GetWorkspaceConfigPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".rad", "config.yaml");
    }

    /// <summary>
    /// Returns the explicit override when set, otherwise the active workspace's context, otherwise
    /// <see langword="null"/> when neither can be determined.
    /// </summary>
    internal static async Task<string?> TryResolveAsync(CancellationToken cancellationToken)
    {
        var overrideContext = Environment.GetEnvironmentVariable(OverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overrideContext))
        {
            return overrideContext.Trim();
        }

        var parsed = await ResolveWorkspaceContextAsync(GetWorkspaceConfigPath(), cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(parsed) ? null : parsed.Trim();
    }

    internal static async Task<string?> ResolveWorkspaceContextAsync(string configPath, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(configPath))
            {
                return null;
            }

            var text = await File.ReadAllTextAsync(configPath, cancellationToken).ConfigureAwait(false);
            return ParseActiveWorkspaceContext(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Selects the kubecontext of the *default* (active) rad workspace, not merely the first
    // `context:` in the file — a machine with several workspaces would otherwise pick the wrong
    // cluster. The rad config (~/.rad/config.yaml) is nested YAML shaped like:
    //   workspaces:
    //     default: kind-radius
    //     items:
    //       kind-radius:
    //         connection:
    //           kind: kubernetes
    //           context: kind-radius
    //       other:
    //         connection:
    //           context: other-ctx
    // We read workspaces.default, then workspaces.items.<default>.connection.context. If the
    // default selector is absent (older/single-workspace configs), we fall back to the single
    // `context:` value only when the file resolves to exactly one distinct context; multiple
    // contexts fail closed (null). Parsed with YamlDotNet so real YAML (inline comments, quoted keys,
    // flow-style mappings) is honored rather than a line-oriented approximation; any miss or malformed
    // document returns null and the caller decides how to handle an unresolved context.
    internal static string? ParseActiveWorkspaceContext(string text)
    {
        YamlMappingNode? root;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(text));
            root = stream.Documents.Count > 0 ? stream.Documents[0].RootNode as YamlMappingNode : null;
        }
        catch (YamlException)
        {
            return null;
        }

        if (root is null)
        {
            return null;
        }

        if (TryGetChild(root, "workspaces", out var workspacesNode) && workspacesNode is YamlMappingNode workspaces)
        {
            var defaultWorkspace = GetScalar(workspaces, "default");
            if (!string.IsNullOrEmpty(defaultWorkspace))
            {
                if (TryGetChild(workspaces, "items", out var itemsNode) && itemsNode is YamlMappingNode items &&
                    TryGetChild(items, defaultWorkspace, out var wsNode) && wsNode is YamlMappingNode workspace &&
                    TryGetChild(workspace, "connection", out var connNode) && connNode is YamlMappingNode connection)
                {
                    var context = GetScalar(connection, "context");
                    if (!string.IsNullOrEmpty(context))
                    {
                        return context;
                    }
                }

                // Once rad names an active workspace, guessing from another workspace would fail open
                // to the wrong cluster. Return null so the caller requires an explicit override.
                return null;
            }
        }

        // Fallback for older/single-workspace configs without a `workspaces.default` selector: only
        // accept a context when the file resolves to exactly one distinct value. With multiple
        // contexts there is no evidence which one is active, so fail closed (return null) and let the
        // caller require an explicit override — applying to the wrong cluster is worse than failing.
        var contexts = new HashSet<string>(StringComparer.Ordinal);
        CollectContextValues(root, contexts);
        return contexts.Count == 1 ? contexts.First() : null;
    }

    private static bool TryGetChild(YamlMappingNode mapping, string key, out YamlNode node)
    {
        foreach (var (candidateKey, value) in mapping.Children)
        {
            if (candidateKey is YamlScalarNode scalarKey &&
                string.Equals(scalarKey.Value, key, StringComparison.Ordinal))
            {
                node = value;
                return true;
            }
        }

        node = null!;
        return false;
    }

    private static string? GetScalar(YamlMappingNode mapping, string key) =>
        TryGetChild(mapping, key, out var node) && node is YamlScalarNode { Value: { Length: > 0 } value } ? value : null;

    // Recursively collects every scalar `context:` value in the document so the single-workspace
    // fallback can require exactly one distinct value.
    private static void CollectContextValues(YamlNode node, ISet<string> contexts)
    {
        switch (node)
        {
            case YamlMappingNode mapping:
                foreach (var (key, value) in mapping.Children)
                {
                    if (key is YamlScalarNode { Value: "context" } &&
                        value is YamlScalarNode { Value: { Length: > 0 } contextValue })
                    {
                        contexts.Add(contextValue);
                    }

                    CollectContextValues(value, contexts);
                }
                break;

            case YamlSequenceNode sequence:
                foreach (var child in sequence.Children)
                {
                    CollectContextValues(child, contexts);
                }
                break;
        }
    }
}
