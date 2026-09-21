// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Agents.Copilot;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Agents.VsCode;

/// <summary>
/// Native VS Code workspace and user/profile MCP configuration, distinct from Copilot plugin settings.
/// </summary>
internal static class VsCodeAgentConfiguration
{
    public static AgentClientDescriptor Client { get; } = new(AgentClientKind.VsCode, "vscode", "VS Code", GetTargets);

    private static IEnumerable<AgentConfigurationTarget> GetTargets(
        AgentInitRequest request,
        CliExecutionContext executionContext,
        IEnvironment environment)
    {
        // The supported Copilot-backed runtime shares plugin registration, not MCP files.
        // A selected Copilot frontend already contributes the shared plugin targets.
        if (!request.Clients.Any(client => client is AgentClientKind.CopilotCli or AgentClientKind.CopilotApp))
        {
            foreach (var target in CopilotAgentConfiguration.GetPluginTargets(request, executionContext, environment))
            {
                yield return target;
            }
        }

        if (!request.Assets.Mcp)
        {
            yield break;
        }

        yield return Target(Path.Combine(request.WorkspaceRoot.FullName, ".vscode", "mcp.json"), AgentConfigurationScope.Project);

        var editions = request.Detections.Where(detection => detection.Client is AgentClientKind.VsCode)
            .Select(detection => detection.IsInsiders).Distinct().DefaultIfEmpty(false);
        foreach (var insiders in editions)
        {
            var userDirectory = GetUserDirectory(insiders, executionContext, environment);
            yield return Target(Path.Combine(userDirectory, "mcp.json"), AgentConfigurationScope.User);

            // Existing profiles have independent mcp.json resources. Never invent a profile
            // or inspect client-owned storage to guess a --profile/--user-data-dir session.
            // https://code.visualstudio.com/docs/agent-customization/mcp-servers
            // https://github.com/microsoft/vscode/blob/main/src/vs/platform/userDataProfile/common/userDataProfile.ts
            var profileDirectory = Path.Combine(userDirectory, "profiles");
            string[] profiles = [];
            string? error = null;
            try
            {
                if (Directory.Exists(profileDirectory))
                {
                    profiles = Directory.GetDirectories(profileDirectory);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = string.Format(CultureInfo.CurrentCulture, AgentConfigurationStrings.ReadWriteFailed, ex.Message);
            }

            if (error is not null)
            {
                yield return new AgentConfigurationTarget(profileDirectory, AgentConfigurationScope.User, AgentAssetKind.Mcp,
                    [AgentClientKind.VsCode], "profiles:unavailable",
                    (_, _, _) => Task.FromResult(new AgentConfigurationEdit(AgentConfigurationStatus.Failed, error)));
            }

            foreach (var profile in profiles.Order(AgentPath.Comparer))
            {
                // The reserved system-profile directory is not a user-selected profile.
                if (Path.GetFileName(profile) != "builtin" &&
                    (File.Exists(Path.Combine(profile, "settings.json")) || File.Exists(Path.Combine(profile, "mcp.json"))))
                {
                    yield return Target(Path.Combine(profile, "mcp.json"), AgentConfigurationScope.User);
                }
            }
        }

        static AgentConfigurationTarget Target(string path, AgentConfigurationScope scope)
            => new(path, scope, AgentAssetKind.Mcp, [AgentClientKind.VsCode], "servers:aspire",
                (root, _, _) =>
                {
                    var edit = McpConfiguration.Apply(root, "servers", commandArray: false, "stdio");
                    return Task.FromResult(scope is AgentConfigurationScope.User && edit.Status is AgentConfigurationStatus.Configured
                        ? edit with { Message = AgentConfigurationStrings.ProfileLimitations }
                        : edit);
                });
    }

    public static string GetUserDirectory(bool insiders, CliExecutionContext executionContext, IEnvironment environment)
    {
        // Follow VS Code's portable, appdata, and original-working-directory overrides.
        // https://github.com/microsoft/vscode/blob/main/src/vs/platform/environment/node/userDataPath.ts
        if (Override("VSCODE_PORTABLE") is { } portable)
        {
            return Path.Combine(portable, "user-data", "User");
        }

        var home = executionContext.HomeDirectory.FullName;
        var appData = Override("VSCODE_APPDATA");
        if (appData is null)
        {
            appData = environment.IsWindows()
                ? AgentPath.GetOverride("APPDATA", executionContext, environment) ?? Path.Combine(home, "AppData", "Roaming")
                : environment.IsMacOS()
                    ? Path.Combine(home, "Library", "Application Support")
                    : AgentPath.GetOverride("XDG_CONFIG_HOME", executionContext, environment) ?? Path.Combine(home, ".config");
        }

        var product = environment.GetEnvironmentVariable("VSCODE_DEV") is { Length: > 0 } ? "code-oss-dev"
            : insiders ? "Code - Insiders" : "Code";

        return Path.Combine(appData, product, "User");

        string? Override(string variable)
        {
            if (environment.GetEnvironmentVariable(variable) is not { Length: > 0 } value)
            {
                return null;
            }

            var workingDirectory = AgentPath.GetOverride("VSCODE_CWD", executionContext, environment) ?? executionContext.WorkingDirectory.FullName;
            return AgentPath.Expand(value, executionContext.HomeDirectory.FullName, workingDirectory);
        }
    }
}
