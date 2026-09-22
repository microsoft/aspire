// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Agents.Copilot;
using Aspire.Cli.Resources;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents.VsCode;

/// <summary>
/// Discovers VS Code and configures native workspace/user MCP and shared Copilot plugin settings.
/// </summary>
internal sealed class VsCodeAgentEnvironmentScanner(
    IVsCodeCliRunner vsCodeCliRunner,
    CliExecutionContext executionContext,
    IEnvironment environment,
    ILogger<VsCodeAgentEnvironmentScanner> logger) : IAgentClientEnvironment
{
    internal const string ClientId = "vscode";

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentClientDetection>> ScanAsync(IReadOnlyList<AgentClient> clients, DirectoryInfo workingDirectory, DirectoryInfo workspaceRoot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var client = clients.Single(client => client.Id == ClientId);
        logger.LogDebug("Starting VS Code environment scan in directory: {WorkingDirectory}", workingDirectory.FullName);

        var hasProjectConfiguration = HasProjectConfiguration(workingDirectory, workspaceRoot);
        var isVsCodeTerminal = environment.GetEnvironmentVariable("TERM_PROGRAM") == "vscode";
        if (hasProjectConfiguration || isVsCodeTerminal)
        {
            var version = isVsCodeTerminal ? environment.GetEnvironmentVariable("TERM_PROGRAM_VERSION")?.Trim() : null;
            if (string.IsNullOrEmpty(version))
            {
                version = null;
            }

            // VS Code exposes e.g. "1.110.0" or "1.111.0-insider" in TERM_PROGRAM_VERSION.
            // Retain that evidence for native user paths even when a project marker avoids CLI probes.
            return Array.AsReadOnly<AgentClientDetection>(
            [
                new(client, version, IsInsiders: version?.Contains("-insider", StringComparison.OrdinalIgnoreCase) == true)
            ]);
        }

        var vsCodeVersion = await vsCodeCliRunner.GetVersionAsync(new VsCodeRunOptions { UseInsiders = false }, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (vsCodeVersion is not null)
        {
            logger.LogDebug("Found VS Code stable version: {Version}", vsCodeVersion);
            return Array.AsReadOnly<AgentClientDetection>([new(client, vsCodeVersion.ToString(), IsInsiders: false)]);
        }

        var vsCodeInsidersVersion = await vsCodeCliRunner.GetVersionAsync(new VsCodeRunOptions { UseInsiders = true }, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (vsCodeInsidersVersion is not null)
        {
            logger.LogDebug("Found VS Code Insiders version: {Version}", vsCodeInsidersVersion);
            return Array.AsReadOnly<AgentClientDetection>([new(client, vsCodeInsidersVersion.ToString(), IsInsiders: true)]);
        }

        return Array.AsReadOnly<AgentClientDetection>([]);
    }

    private bool HasProjectConfiguration(DirectoryInfo startDirectory, DirectoryInfo repositoryRoot)
    {
        var relativePath = Path.GetRelativePath(repositoryRoot.FullName, startDirectory.FullName);
        if (Path.IsPathRooted(relativePath) || relativePath == ".." ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            // An explicit --workspace-root can be outside the current working directory.
            startDirectory = repositoryRoot;
        }

        for (var currentDirectory = startDirectory; currentDirectory is not null; currentDirectory = currentDirectory.Parent)
        {
            // The home .vscode directory holds user extensions rather than workspace settings.
            if (Path.GetRelativePath(executionContext.HomeDirectory.FullName, currentDirectory.FullName) != "." &&
                Directory.Exists(Path.Combine(currentDirectory.FullName, ".vscode")))
            {
                return true;
            }

            if (Path.GetRelativePath(repositoryRoot.FullName, currentDirectory.FullName) == ".")
            {
                break;
            }
        }

        return false;
    }

    public IEnumerable<AgentConfigurationTarget> GetTargets(AgentInitRequest request)
    {
        var client = request.Clients.Single(client => client.Environment == this);
        // The supported Copilot-backed runtime shares plugin registration, not MCP files.
        // A selected Copilot frontend already contributes the shared plugin targets.
        if (!request.Clients.Any(client => client.Environment is CopilotAgentEnvironmentScanner))
        {
            foreach (var target in CopilotAgentEnvironmentScanner.GetPluginTargets(request, executionContext, environment))
            {
                yield return target;
            }
        }

        if (!request.Assets.Mcp)
        {
            yield break;
        }

        yield return Target(Path.Combine(request.WorkspaceRoot.FullName, ".vscode", "mcp.json"), AgentConfigurationScope.Project);

        var editions = request.Detections.Where(detection => detection.Client.Environment is VsCodeAgentEnvironmentScanner)
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
                error = string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.Configuration_ReadWriteFailed, ex.Message);
            }

            if (error is not null)
            {
                yield return new AgentConfigurationTarget(profileDirectory, AgentConfigurationScope.User, AgentAssetKind.Mcp,
                    [client], "profiles:unavailable",
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

        AgentConfigurationTarget Target(string path, AgentConfigurationScope scope)
            => new(path, scope, AgentAssetKind.Mcp, [client], "servers:aspire",
                (root, _, _) =>
                {
                    var edit = McpConfiguration.Apply(root, "servers", commandArray: false, "stdio");
                    return Task.FromResult(scope is AgentConfigurationScope.User && edit.Status is AgentConfigurationStatus.Configured
                        ? edit with { Message = AgentCommandStrings.Configuration_ProfileLimitations }
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
