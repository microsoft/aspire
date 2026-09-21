// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents.Copilot;

namespace Aspire.Cli.Agents.Configuration;

/// <summary>
/// Documented native user locations and read-only policy/override inputs.
/// </summary>
internal sealed class AgentConfigurationPaths(CliExecutionContext executionContext, IEnvironment environment)
{
    public string Home => executionContext.HomeDirectory.FullName;

    public string CopilotDirectory => Absolute(CopilotPaths.GetConfigDirectory(executionContext.HomeDirectory, environment));

    public string ClaudeDirectory => Override("CLAUDE_CONFIG_DIR") ?? Path.Combine(Home, ".claude");

    // CLAUDE_CONFIG_DIR relocates the state file into the custom directory, unlike the
    // default ~/.claude.json beside ~/.claude. See the supported override in
    // https://code.claude.com/docs/en/env-vars and upstream confirmation in
    // https://github.com/anthropics/claude-code/issues/79275.
    public string ClaudeMcpFile => Override("CLAUDE_CONFIG_DIR") is { } directory
        ? Path.Combine(directory, ".claude.json")
        : Path.Combine(Home, ".claude.json");

    public string OpenCodeDirectory => Path.Combine(Override("XDG_CONFIG_HOME") ?? Path.Combine(Home, ".config"), "opencode");

    public string? Override(string variable)
    {
        if (environment.GetEnvironmentVariable(variable) is not { Length: > 0 } value)
        {
            return null;
        }

        var baseDirectory = executionContext.WorkingDirectory.FullName;
        if (variable is "VSCODE_PORTABLE" or "VSCODE_APPDATA" &&
            environment.GetEnvironmentVariable("VSCODE_CWD") is { Length: > 0 } originalDirectory)
        {
            baseDirectory = Absolute(originalDirectory);
        }

        return Absolute(value, baseDirectory);
    }

    // Full normalization/validation belongs to the writer's per-target error boundary.
    // A malformed override must not abort planning for every other selected client.
    public string Absolute(string path) => Absolute(path, executionContext.WorkingDirectory.FullName);

    private string Absolute(string path, string baseDirectory)
    {
        // Configuration-directory overrides accept "~", "~/claude-work", and
        // "~\claude-work" on every platform. Expand against the injected home, never
        // the process environment or the current project, and leave "~other-user" alone.
        if (path == "~")
        {
            return Home;
        }

        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith(@"~\", StringComparison.Ordinal))
        {
            var relative = path[2..].Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            return Path.Join(Home, relative);
        }

        return Path.IsPathFullyQualified(path) ? path : Path.Combine(baseDirectory, path);
    }

    public IEnumerable<string> PluginSettings(AgentInitRequest request, bool copilot)
    {
        if (copilot)
        {
            // Copilot reads Claude's project settings for its shared cross-tool subset, but
            // never ~/.claude/settings.json. Do not let a new overlay undo a disable or pin.
            // https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-config-dir-reference
            yield return Path.Combine(CopilotDirectory, "settings.json");
            yield return Path.Combine(request.WorkspaceRoot.FullName, ".claude", "settings.json");
            yield return Path.Combine(request.WorkspaceRoot.FullName, ".claude", "settings.local.json");
            yield return Path.Combine(request.WorkspaceRoot.FullName, ".github", "copilot", "settings.json");
            yield return Path.Combine(request.WorkspaceRoot.FullName, ".github", "copilot", "settings.local.json");
        }
        else
        {
            yield return Path.Combine(ClaudeDirectory, "settings.json");
            yield return Path.Combine(request.WorkspaceRoot.FullName, ".claude", "settings.json");
            yield return Path.Combine(request.WorkspaceRoot.FullName, ".claude", "settings.local.json");
        }

        foreach (var path in ManagedSettings(copilot))
        {
            yield return path;
        }
    }

    public string ManagedDirectory(bool copilot)
    {
        // These file-based policy paths are documented by both clients. Platform MDM,
        // server policy and command-line overrides remain client-owned; our success
        // messages explicitly do not claim that effective activation was verified.
        // https://code.claude.com/docs/en/managed-settings
        // https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-config-dir-reference#mdm-managed-settings
        var name = copilot ? "GitHubCopilot" : "ClaudeCode";
        if (environment.IsWindows())
        {
            return Path.Combine(Override("ProgramFiles") ?? Path.Combine(Path.GetPathRoot(Home)!, "Program Files"), name);
        }

        return environment.IsMacOS()
            ? Path.Combine(Path.DirectorySeparatorChar.ToString(), "Library", "Application Support", name)
            : Path.Combine(Path.DirectorySeparatorChar.ToString(), "etc", copilot ? "github-copilot" : "claude-code");
    }

    public IEnumerable<string> ManagedSettings(bool copilot)
    {
        var directory = ManagedDirectory(copilot);
        yield return Path.Combine(directory, "managed-settings.json");
        if (!copilot)
        {
            var fragments = Path.Combine(directory, "managed-settings.d");
            if (Directory.Exists(fragments))
            {
                foreach (var file in Directory.EnumerateFiles(fragments, "*.json").Order(StringComparer.Ordinal))
                {
                    if (!Path.GetFileName(file).StartsWith('.'))
                    {
                        yield return file;
                    }
                }
            }
        }
    }

    public string VsCodeUserDirectory(bool insiders)
    {
        // Follow VS Code's own resolution, including portable and VSCODE_APPDATA:
        // https://github.com/microsoft/vscode/blob/main/src/vs/platform/environment/node/userDataPath.ts
        if (Override("VSCODE_PORTABLE") is { } portable)
        {
            return Path.Combine(portable, "user-data", "User");
        }

        var appData = Override("VSCODE_APPDATA");
        if (appData is null)
        {
            appData = environment.IsWindows()
                ? Override("APPDATA") ?? Path.Combine(Home, "AppData", "Roaming")
                : environment.IsMacOS()
                    ? Path.Combine(Home, "Library", "Application Support")
                    : Override("XDG_CONFIG_HOME") ?? Path.Combine(Home, ".config");
        }

        var product = environment.GetEnvironmentVariable("VSCODE_DEV") is { Length: > 0 } ? "code-oss-dev"
            : insiders ? "Code - Insiders" : "Code";

        return Path.Combine(appData, product, "User");
    }
}
