// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Agents.Configuration;

/// <summary>
/// Deduplicates physical destinations and entries before any configuration is written.
/// </summary>
internal sealed class AgentConfigurationPlanner(IEnumerable<IAgentConfigurationHandler> handlers)
{
    public IEnumerable<AgentConfigurationTarget> GetTargets(AgentInitRequest request)
        => handlers.SelectMany(handler => handler.GetTargets(request));

    public static AgentConfigurationPlan Group(IEnumerable<AgentConfigurationTarget> targets)
    {
        var files = new Dictionary<string, List<AgentConfigurationTarget>>(AgentConfigurationPath.Comparer);
        var results = new List<AgentTargetResult>();
        foreach (var target in targets)
        {
            try
            {
                var path = AgentConfigurationPath.Resolve(target.Path);
                if (!files.TryGetValue(path, out var entries))
                {
                    entries = [];
                    files.Add(path, entries);
                }

                entries.Add(target with { Path = Path.GetFullPath(target.Path) });
            }
            catch (AgentConfigurationException ex)
            {
                results.Add(ToResult(target, AgentConfigurationStatus.Blocked, ex.Message));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                results.Add(ToResult(target, AgentConfigurationStatus.Failed,
                    string.Format(CultureInfo.CurrentCulture, AgentConfigurationStrings.ReadWriteFailed, ex.Message)));
            }
        }

        var grouped = files.Select(file => new AgentConfigurationFile(
            file.Key,
            file.Value.Select(target => target.Path).Distinct(AgentConfigurationPath.Comparer).ToArray(),
            file.Value.GroupBy(target => (target.Asset, target.Entry))
                .Select(group => group.First() with
                {
                    Scope = group.Any(target => target.Scope is AgentConfigurationScope.User) ? AgentConfigurationScope.User : AgentConfigurationScope.Project,
                    Clients = group.SelectMany(target => target.Clients).Distinct().ToArray(),
                    ApplyAsync = async (root, context, cancellationToken) =>
                    {
                        // Sharing a physical MCP entry must not bypass either client's policy
                        // checks. All contributions validate; the merged file is still saved once.
                        AgentConfigurationEdit? edit = null;
                        AgentConfigurationEdit? skipped = null;
                        foreach (var target in group)
                        {
                            edit = await target.ApplyAsync(root, context, cancellationToken);
                            if (edit.Status is AgentConfigurationStatus.Blocked or AgentConfigurationStatus.Failed)
                            {
                                return edit;
                            }

                            if (edit.Status is AgentConfigurationStatus.Skipped)
                            {
                                skipped = edit;
                            }
                        }

                        return skipped ?? edit!;
                    }
                })
                .OrderBy(target => target.Asset is AgentAssetKind.TelemetryHooks)
                .ToArray()))
            // User hook files go last. Claude's user plugin settings and hook share a file:
            // earlier native results plus that file's pending edits determine hook eligibility.
            .OrderBy(file => file.Targets.Any(target => target.Asset is AgentAssetKind.TelemetryHooks))
            .ThenBy(file => file.Targets.All(target => target.Asset is AgentAssetKind.TelemetryHooks))
            .ToArray();

        return new AgentConfigurationPlan(grouped, results);
    }

    internal static AgentTargetResult ToResult(AgentConfigurationTarget target, AgentConfigurationStatus status, string message)
        => new(target.Asset, target.Clients, target.Path, target.Scope, status, message);
}

internal sealed record AgentConfigurationFile(string Path, IReadOnlyList<string> Aliases, IReadOnlyList<AgentConfigurationTarget> Targets);

internal sealed record AgentConfigurationPlan(IReadOnlyList<AgentConfigurationFile> Files, IReadOnlyList<AgentTargetResult> Results);
