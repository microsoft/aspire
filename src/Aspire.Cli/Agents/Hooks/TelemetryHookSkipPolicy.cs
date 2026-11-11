// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Agents.ClaudeCode;

namespace Aspire.Cli.Agents.Hooks;

/// <summary>
/// Decides whether a telemetry hook invocation should defer to another installed hook that records the same tool call.
/// </summary>
/// <remarks>
/// <para>
/// Agents run every configured hook for a tool call without deduplicating them. GitHub Copilot runs hooks from
/// <c>~/.copilot/hooks</c>, the repository's <c>.github/hooks</c>, and the repository's <c>.claude/settings.json</c>
/// once each, all with the same <c>toolCallId</c>, so a trusted repository with project-scope hooks would otherwise
/// record one Copilot tool call three times. Copilot never reads <c>~/.claude/settings.json</c>.
/// https://docs.github.com/en/copilot/reference/hooks-reference#hooks-locations
/// </para>
/// <para>
/// Each hook command carries <c>--source</c> (the <see cref="IAgentClient.Id"/> that installed it) and <c>--scope</c>.
/// A Claude Code hook defers inside Copilot because Copilot's own hook records the event, and a user-scope hook
/// defers when its client reports a project-scope hook in the enclosing repository. Copilot runs repository hooks
/// only after the user trusts the folder, and then the project hook records the event instead.
/// </para>
/// </remarks>
internal sealed class TelemetryHookSkipPolicy(IEnumerable<IAgentClient> clients, CliExecutionContext executionContext, IEnvironment environment)
{
    /// <summary>
    /// Determines whether the hook identified by <paramref name="source"/> and <paramref name="scope"/> should not record.
    /// </summary>
    /// <remarks>
    /// Hooks installed before <c>--source</c> and <c>--scope</c> existed pass neither, and always record.
    /// Unreadable project settings also record, so a broken file never drops the only hook that would.
    /// </remarks>
    public bool ShouldSkip(string? source, string? scope)
    {
        if (source is null || scope is null)
        {
            return false;
        }

        if (source == ClaudeCodeAgentClient.ClientId && AgentTelemetryHook.GetCopilotRuntimeName(environment) is not null)
        {
            return true;
        }

        if (scope != AgentTelemetryProtocol.UserScope
            || clients.FirstOrDefault(client => client.Id == source) is not { } client
            || FindRepositoryRoot(executionContext.WorkingDirectory) is not { } repositoryRoot)
        {
            return false;
        }

        try
        {
            return client.HasProjectTelemetryHook(repositoryRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    // Walks the file system instead of running git because this runs on every recorded tool call.
    // A worktree or submodule has a .git file rather than a directory.
    private static DirectoryInfo? FindRepositoryRoot(DirectoryInfo workingDirectory)
    {
        for (var directory = workingDirectory; directory is not null; directory = directory.Parent)
        {
            var git = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
            {
                return directory;
            }
        }

        return null;
    }
}
