// <copyright file="ChaosMeshScope.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

namespace Aspire.Hosting.Chaos;

/// <summary>
/// An explicit <b>include</b> allowlist of the exact edges a chaos mesh should proxy, identified by
/// <c>{client}-&gt;{target}</c> resource-name pairs. When a scope is supplied to
/// <see cref="ChaosProxyMeshExtensions.AddChaosProxyMesh"/>, only edges in this set are meshed; every
/// other eligible edge keeps its DIRECT reference (the already-supported no-mesh topology) with its
/// authored <c>WaitFor</c> dependencies intact.
/// </summary>
/// <remarks>
/// <para>
/// This exists to cut cold-start cost: the full mesh inserts one locally-built proxy container per
/// eligible edge (~19 in the Uber AppHost), yet a targeted run typically injects on a single edge, so
/// the other proxies are pure startup tax. Scoping the mesh to the edges a run actually references
/// removes that tax without changing the wiring of the untouched edges.
/// </para>
/// <para>
/// <b>Fail-closed:</b> a scope is a promise that every requested edge WILL be meshed. After the mesh
/// is built, <see cref="ChaosProxyMesh.Seal"/> reconciles the requested edges against what was
/// actually meshed and <b>throws</b> if any requested edge does not exist in the graph or could not be
/// meshed. A requested-but-unmeshable edge means the requested chaos policy physically cannot be
/// injected, so a hard failure is the correct outcome — never a silent full-mesh fallback and never a
/// silent drop.
/// </para>
/// </remarks>
public sealed class ChaosMeshScope
{
    /// <summary>
    /// The environment variable the Uber AppHost reads to scope the mesh (comma-separated
    /// <c>{client}-&gt;{target}</c> pairs). Unset / empty means "no scope" — the full mesh, byte-identical
    /// to the pre-scope behaviour.
    /// </summary>
    public const string EnvironmentVariableName = "CHAOS_MESH_SCOPE";

    private const string EdgeSeparator = "->";

    private readonly HashSet<string> edgeKeys;

    private ChaosMeshScope(IReadOnlyList<(string Client, string Target)> edges, HashSet<string> edgeKeys)
    {
        this.Edges = edges;
        this.edgeKeys = edgeKeys;
    }

    /// <summary>Gets the requested edges, in the order they were specified.</summary>
    public IReadOnlyList<(string Client, string Target)> Edges { get; }

    /// <summary>
    /// Parses the <see cref="EnvironmentVariableName"/> value into a scope. Returns <see langword="null"/>
    /// when <paramref name="raw"/> is <see langword="null"/> or whitespace (meaning "no scope" — full mesh).
    /// </summary>
    /// <param name="raw">The raw environment-variable value: comma-separated <c>{client}-&gt;{target}</c> pairs
    /// (e.g. <c>armgatewayservice-api-&gt;cosmos, workspace-service-&gt;cosmos</c>).</param>
    /// <returns>A parsed scope, or <see langword="null"/> for the unset/full-mesh case.</returns>
    /// <exception cref="FormatException">The value is non-empty but malformed (a token is missing the
    /// <c>-&gt;</c> separator, has an empty client or target, or the value contains no valid edge). Parsing
    /// fails closed rather than silently ignoring a bad token.</exception>
    public static ChaosMeshScope? FromEnvironmentValue(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var edges = new List<(string Client, string Target)>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = token.Split(EdgeSeparator, StringSplitOptions.None);
            if (parts.Length != 2)
            {
                throw new FormatException(
                    $"{EnvironmentVariableName} edge '{token}' is malformed; expected exactly one '{EdgeSeparator}' separator in the form 'client{EdgeSeparator}target'.");
            }

            var client = parts[0].Trim();
            var target = parts[1].Trim();
            if (client.Length == 0 || target.Length == 0)
            {
                throw new FormatException(
                    $"{EnvironmentVariableName} edge '{token}' is malformed; both client and target must be non-empty (form 'client{EdgeSeparator}target').");
            }

            if (keys.Add(EdgeKey(client, target)))
            {
                edges.Add((client, target));
            }
        }

        if (edges.Count == 0)
        {
            throw new FormatException(
                $"{EnvironmentVariableName} was set but contained no valid 'client{EdgeSeparator}target' edges.");
        }

        return new ChaosMeshScope(edges, keys);
    }

    /// <summary>
    /// Returns whether the edge <c>{clientName}-&gt;{targetName}</c> is in this scope (case-insensitive,
    /// matching Aspire resource-name comparison).
    /// </summary>
    public bool Contains(string clientName, string targetName)
        => this.edgeKeys.Contains(EdgeKey(clientName, targetName));

    /// <summary>
    /// Fail-closed completeness gate: every requested edge must have produced a proxy. Throws
    /// <see cref="InvalidOperationException"/> listing every requested edge that was not meshed —
    /// either because no such edge exists in the graph, or because the edge was skipped for a
    /// structural reason (surfaced from <paramref name="reports"/>).
    /// </summary>
    /// <param name="realizedProxyNames">The proxy resource names the mesh actually created.</param>
    /// <param name="reports">The per-edge disposition summary, used to explain unmet edges.</param>
    internal void Validate(IReadOnlyCollection<string> realizedProxyNames, IReadOnlyList<ChaosMeshEdgeReport> reports)
    {
        var realized = new HashSet<string>(realizedProxyNames, StringComparer.OrdinalIgnoreCase);
        var unmet = new List<string>();

        foreach (var (client, target) in this.Edges)
        {
            var proxyName = $"mesh-{client}-to-{target}";
            if (realized.Contains(proxyName))
            {
                continue;
            }

            var report = reports.FirstOrDefault(r =>
                string.Equals(r.ClientName, client, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.TargetName, target, StringComparison.OrdinalIgnoreCase));

            var reason = report is null
                ? "no such edge exists in the application graph (check the client and target resource names)"
                : report.SkipReason ?? "edge was considered but not meshed";

            unmet.Add($"'{client}{EdgeSeparator}{target}' ({reason})");
        }

        if (unmet.Count > 0)
        {
            throw new InvalidOperationException(
                $"{EnvironmentVariableName} requested {this.Edges.Count} edge(s) but {unmet.Count} could not be meshed: " +
                $"{string.Join("; ", unmet)}. The mesh scope fails closed rather than injecting a partial or unintended fault topology; " +
                "fix the requested edges (or clear the scope for a full mesh).");
        }
    }

    private static string EdgeKey(string client, string target) => $"{client}{EdgeSeparator}{target}";
}
