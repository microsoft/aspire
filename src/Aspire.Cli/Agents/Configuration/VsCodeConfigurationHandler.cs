// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Agents.Configuration;

/// <summary>
/// Native VS Code workspace and user/profile MCP configuration, distinct from Copilot plugin settings.
/// </summary>
internal sealed class VsCodeConfigurationHandler(AgentConfigurationPaths paths) : IAgentConfigurationHandler
{
    public IEnumerable<AgentConfigurationTarget> GetTargets(AgentInitRequest request)
    {
        if (!request.Assets.Mcp || !request.Clients.Contains(AgentClientKind.VsCode))
        {
            yield break;
        }

        yield return Target(Path.Combine(request.WorkspaceRoot.FullName, ".vscode", "mcp.json"), AgentConfigurationScope.Project);

        var editions = request.Detections.Where(detection => detection.Client is AgentClientKind.VsCode)
            .Select(detection => detection.IsInsiders).Distinct().DefaultIfEmpty(false);
        foreach (var insiders in editions)
        {
            var userDirectory = paths.VsCodeUserDirectory(insiders);
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

            foreach (var profile in profiles.Order(AgentConfigurationPath.Comparer))
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
                    var edit = McpConfiguration.Apply(root, "servers", commandArray: false, "stdio", copilot: false);
                    return Task.FromResult(scope is AgentConfigurationScope.User && edit.Status is AgentConfigurationStatus.Configured
                        ? edit with { Message = AgentConfigurationStrings.ProfileLimitations }
                        : edit);
                });
    }
}
