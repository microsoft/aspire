// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents.ClaudeCode;

namespace Aspire.Cli.Agents.Copilot;

/// <summary>
/// Resolves user-level GitHub Copilot configuration paths.
/// </summary>
internal static class CopilotPaths
{
    private const string CopilotHomeEnvironmentVariable = "COPILOT_HOME";
    private const string DefaultCopilotDirectoryName = ".copilot";

    /// <summary>
    /// Gets the user-level GitHub Copilot configuration directory.
    /// </summary>
    /// <param name="homeDirectory">The user's home directory.</param>
    /// <param name="environment">The environment abstraction used to resolve <c>COPILOT_HOME</c>.</param>
    /// <returns>The configured Copilot home, or <c>~/.copilot</c> when no override is set.</returns>
    public static string GetConfigDirectory(DirectoryInfo homeDirectory, IEnvironment environment)
    {
        var configuredHome = environment.GetEnvironmentVariable(CopilotHomeEnvironmentVariable);
        return !string.IsNullOrEmpty(configuredHome)
            ? configuredHome
            : Path.Combine(homeDirectory.FullName, DefaultCopilotDirectoryName);
    }

    public static string GetConfigDirectory(CliExecutionContext executionContext, IEnvironment environment)
        => AgentPath.Expand(GetConfigDirectory(executionContext.HomeDirectory, environment), executionContext);

    public static IEnumerable<string> PluginSettings(AgentInitRequest request, CliExecutionContext executionContext, IEnvironment environment)
    {
        // Copilot reads Claude's project settings, never Claude's user settings.
        // https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-config-dir-reference
        yield return Path.Combine(GetConfigDirectory(executionContext, environment), "settings.json");
        foreach (var path in ClaudeCodeAgentEnvironmentScanner.ProjectSettings(request.WorkspaceRoot))
        {
            yield return path;
        }

        yield return Path.Combine(request.WorkspaceRoot.FullName, ".github", "copilot", "settings.json");
        yield return Path.Combine(request.WorkspaceRoot.FullName, ".github", "copilot", "settings.local.json");
        foreach (var path in ManagedSettings(executionContext, environment))
        {
            yield return path;
        }
    }

    public static IEnumerable<string> ManagedSettings(CliExecutionContext executionContext, IEnvironment environment)
    {
        // Platform MDM and runtime overrides remain client-owned.
        // https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-config-dir-reference#mdm-managed-settings
        yield return Path.Combine(AgentPath.GetManagedDirectory(executionContext, environment, "GitHubCopilot", "github-copilot"), "managed-settings.json");
    }

    public static IEnumerable<string> ExistingHookSettings(AgentInitRequest request, CliExecutionContext executionContext, IEnvironment environment)
    {
        // Cross-tool repository hooks must not be duplicated by a new user hook.
        // https://docs.github.com/en/copilot/reference/hooks-reference
        foreach (var path in ClaudeCodeAgentEnvironmentScanner.ProjectSettings(request.WorkspaceRoot))
        {
            yield return path;
        }

        yield return Path.Combine(request.WorkspaceRoot.FullName, ".github", "copilot", "settings.json");
        yield return Path.Combine(request.WorkspaceRoot.FullName, ".github", "copilot", "settings.local.json");
        yield return Path.Combine(request.WorkspaceRoot.FullName, ".github", "hooks", "aspire-telemetry.json");
        yield return Path.Combine(GetConfigDirectory(executionContext, environment), "settings.json");
    }
}
