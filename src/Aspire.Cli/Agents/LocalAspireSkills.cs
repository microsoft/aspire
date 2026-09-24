// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Agents.ClaudeCode;
using Aspire.Cli.Agents.Copilot;
using Aspire.Cli.Agents.OpenCode;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Agents;

/// <summary>
/// Finds local Aspire skill entry points without assuming who installed them or changing their content.
/// </summary>
internal static class LocalAspireSkills
{
    private static readonly string[] s_skillNames =
    [
        "aspire", "aspire-init", "aspireify", "aspire-deployment",
        "aspire-monitoring", "aspire-orchestration", "aspire-project-v2-migration"
    ];

    public static async Task<LocalAspireSkillScan> FindAsync(
        DirectoryInfo workspaceRoot,
        IEnumerable<IAgentEnvironmentScanner> agents,
        CliExecutionContext executionContext,
        IEnvironment environment,
        CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, LocalAspireSkill>(AgentPath.Comparer);
        var errors = new HashSet<string>(StringComparer.Ordinal);
        var inspected = new HashSet<string>(AgentPath.Comparer);
        foreach (var agent in agents)
        {
            foreach (var scope in new[] { AgentConfigurationScope.Project, AgentConfigurationScope.User })
            {
                cancellationToken.ThrowIfCancellationRequested();
                string[] directories;
                try
                {
                    directories = GetDirectories(agent.Id, scope, workspaceRoot, executionContext, environment).ToArray();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    errors.Add(string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.LocalSkills_ReadFailed, agent.DisplayName, ex.Message));
                    continue;
                }

                foreach (var directory in directories)
                {
                    foreach (var name in s_skillNames)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var path = Path.Combine(directory, name, "SKILL.md");
                        if (!inspected.Add(path))
                        {
                            continue;
                        }

                        try
                        {
                            var physicalPath = AgentPath.Resolve(path);
                            // Older CLI versions copied Markdown verbatim, without an ownership
                            // stamp or installed version. Names identify possible collisions only;
                            // even byte-identical direct installs cannot be classified as obsolete.
                            if (await AgentFileWriter.ReadExistingAsync(physicalPath, cancellationToken) is not null)
                            {
                                files.TryAdd(physicalPath, new LocalAspireSkill(path, scope));
                            }
                        }
                        catch (Exception ex) when (ex is AgentConfigurationException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                        {
                            errors.Add(string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.LocalSkills_ReadFailed, path, ex.Message));
                        }
                    }
                }
            }
        }

        return new(files.Values.OrderBy(file => file.Path, AgentPath.Comparer).ToArray(), errors.Order(StringComparer.Ordinal).ToArray());
    }

    private static IEnumerable<string> GetDirectories(
        string agent,
        AgentConfigurationScope scope,
        DirectoryInfo workspaceRoot,
        CliExecutionContext executionContext,
        IEnvironment environment)
    {
        var root = scope is AgentConfigurationScope.Project ? workspaceRoot.FullName : executionContext.HomeDirectory.FullName;
        if (agent is CopilotAgentEnvironmentScanner.ClientId or OpenCodeAgentEnvironmentScanner.ClientId)
        {
            yield return Path.Combine(root, ".agents", "skills");
        }

        // Native clients also discover compatibility locations, independently of where this
        // CLI would install a new skill. Never search unrelated repositories recursively.
        // https://docs.github.com/en/copilot/concepts/agents/about-agent-skills
        // https://opencode.ai/docs/skills/
        if (agent is ClaudeCodeAgentEnvironmentScanner.ClientId && scope is AgentConfigurationScope.User)
        {
            yield return Path.Combine(ClaudeCodeAgentEnvironmentScanner.GetConfigDirectory(executionContext, environment), "skills");
        }
        else
        {
            yield return Path.Combine(root, ".claude", "skills");
        }

        if (agent is CopilotAgentEnvironmentScanner.ClientId)
        {
            yield return scope is AgentConfigurationScope.Project
                ? Path.Combine(root, ".github", "skills")
                : Path.Combine(CopilotPaths.GetConfigDirectory(executionContext, environment), "skills");
        }

        if (agent is OpenCodeAgentEnvironmentScanner.ClientId)
        {
            var openCodeRoot = scope is AgentConfigurationScope.Project
                ? Path.Combine(root, ".opencode")
                : OpenCodeAgentEnvironmentScanner.GetConfigDirectory(executionContext, environment);
            yield return Path.Combine(openCodeRoot, "skill");
            yield return Path.Combine(openCodeRoot, "skills");
            if (scope is AgentConfigurationScope.User &&
                AgentPath.GetOverride("OPENCODE_CONFIG_DIR", executionContext, environment) is { } custom)
            {
                yield return Path.Combine(custom, "skill");
                yield return Path.Combine(custom, "skills");
            }
        }
    }
}

internal sealed record LocalAspireSkill(string Path, AgentConfigurationScope Scope);

internal sealed record LocalAspireSkillScan(IReadOnlyList<LocalAspireSkill> Files, IReadOnlyList<string> Errors);
