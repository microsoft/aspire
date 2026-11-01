// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json.Nodes;
using Aspire.Cli.Agents.AspireSkills;
using Aspire.Cli.Agents.Hooks;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Agents.OpenCode;

internal sealed class OpenCodeAgentClient(CliExecutionContext context, IEnvironment environment)
    : IAgentClient
{
    public const string ClientId = "opencode";

    private static readonly McpConfigFileHelper.Configuration s_mcp = new()
    {
        ServersPropertyName = "mcp",
        JsonDocumentOptions = ConfigurationHelper.ParseOptions,
        Schema = "https://opencode.ai/config.json",
        ServerConfiguration = new JsonObject
        {
            ["type"] = "local",
            ["command"] = new JsonArray("aspire", "agent", "mcp"),
            ["enabled"] = true
        }
    };

    private static readonly McpConfigFileHelper.Configuration s_nativeMcp = s_mcp with
    {
        ServersPropertyName = "servers",
        Schema = null,
        ServerConfiguration = new JsonObject
        {
            ["type"] = "local",
            ["command"] = new JsonArray("aspire", "agent", "mcp"),
            ["disabled"] = false
        }
    };

    public string Id => ClientId;
    public string DisplayName { get; } = AgentCommandStrings.Environment_OpenCode;
    public AgentEnvironment Environments => AgentEnvironment.OpenCode;

    public string GetSkillBaseDirectory(AgentConfigurationScope scope, DirectoryInfo workspaceRoot)
        => Path.Combine(scope is AgentConfigurationScope.Project ? workspaceRoot.FullName : context.HomeDirectory.FullName,
            AgentPath.UniversalSkillsDirectory);

    public IEnumerable<string> GetLocalSkillDirectories(AgentConfigurationScope scope, DirectoryInfo workspaceRoot)
    {
        // https://opencode.ai/docs/skills/
        var root = scope is AgentConfigurationScope.Project ? workspaceRoot.FullName : context.HomeDirectory.FullName;
        yield return GetSkillBaseDirectory(scope, workspaceRoot);
        yield return Path.Combine(root, ".claude", "skills");
        var openCodeRoot = scope is AgentConfigurationScope.Project ? Path.Combine(root, ".opencode")
            : OpenCodePaths.GetConfigDirectory(context, environment);
        yield return Path.Combine(openCodeRoot, "skill");
        yield return Path.Combine(openCodeRoot, "skills");
        if (scope is AgentConfigurationScope.Global &&
            OpenCodePaths.GetOverride(OpenCodePaths.ConfigDirectoryEnvironmentVariable, context, environment) is { } custom)
        {
            yield return Path.Combine(custom, "skill");
            yield return Path.Combine(custom, "skills");
        }

        var settingsPaths = scope is AgentConfigurationScope.Project
            ? OpenCodePaths.GetProjectConfigPaths(workspaceRoot)
            : GlobalCatalogSettings();
        foreach (var path in settingsPaths)
        {
            var config = AgentConfiguration.ReadObject(path, CancellationToken.None);
            if (config is null)
            {
                continue;
            }

            foreach (var directory in GetConfiguredSkillDirectories(config))
            {
                yield return directory;
            }
        }

        if (scope is AgentConfigurationScope.Global &&
            environment.GetEnvironmentVariable(OpenCodePaths.ConfigContentEnvironmentVariable) is { Length: > 0 } inline)
        {
            foreach (var directory in GetConfiguredSkillDirectories(AgentConfiguration.ParseObject(inline)))
            {
                yield return directory;
            }
        }
    }

    private IEnumerable<string> GetConfiguredSkillDirectories(JsonObject config)
    {
        if (!config.TryGetPropertyValue("skills", out var skills))
        {
            yield break;
        }

        ArgumentNullException.ThrowIfNull(skills);
        // V1 {"skills":{"paths":["./team-skills"]}} and V2 {"skills":["./team-skills"]}
        // resolve local paths from the active working directory, not the config file.
        // HTTP catalogs are not local copies. https://opencode.ai/docs/skills/#sources
        var paths = skills is JsonObject v1
            ? AspireSkillsRemoteConfiguration.OptionalStrings(v1, "paths")
            : AspireSkillsRemoteConfiguration.Strings(skills.AsArray());
        foreach (var node in paths ?? [])
        {
            var value = node!.GetValue<string>();
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                continue;
            }

            var expanded = value.StartsWith("~/", StringComparison.Ordinal)
                ? Path.Combine(context.HomeDirectory.FullName, value[2..])
                : value;
            yield return Path.GetFullPath(expanded.Length == 0 ? "." : expanded, context.WorkingDirectory.FullName);
        }
    }

    public string GetMcpConfigPath(AgentConfigurationScope scope, DirectoryInfo workspaceRoot)
        => scope is AgentConfigurationScope.Project ? OpenCodePaths.GetProjectConfigPath(workspaceRoot)
            : OpenCodePaths.GetGlobalMcpConfigPath(context, environment);

    public IEnumerable<(string Path, bool ConfigureServer)> GetMcpConfigPaths(
        AgentConfigurationScope scope, DirectoryInfo workspaceRoot)
        => [(GetMcpConfigPath(scope, workspaceRoot), true)];

    public async Task<(JsonObject Config, string Path)> ReadMcpConfigAsync(
        string configFilePath, DirectoryInfo workspaceRoot, AgentConfigurationScope scope,
        AgentConfiguration edit, CancellationToken cancellationToken)
    {
        var config = await McpConfigFileHelper.ReadConfigAsync(configFilePath, s_mcp.JsonDocumentOptions, edit, cancellationToken);
        // JSONC {"mcp":{"aspire":{"enabled":true}}} inherits its command from a
        // lower-precedence document. Repair that owner without shadowing pins or
        // overrides in JSONC or .opencode settings. Explicit OPENCODE_CONFIG files
        // never enable sibling discovery. https://opencode.ai/docs/config/#locations
        var candidates = scope is AgentConfigurationScope.Project
            ? OpenCodePaths.GetProjectConfigPaths(workspaceRoot)
            : environment.GetEnvironmentVariable(OpenCodePaths.ConfigEnvironmentVariable) is not { Length: > 0 }
                ? OpenCodePaths.GetConfigPaths(Path.GetDirectoryName(configFilePath)!)
                : [configFilePath];
        var result = (Config: config, Path: configFilePath);
        var foundAspire = false;
        foreach (var candidate in candidates.Reverse())
        {
            var candidateConfig = StringComparers.FileSystemPath.Equals(candidate, configFilePath)
                ? config
                : await McpConfigFileHelper.ReadConfigAsync(candidate, s_mcp.JsonDocumentOptions, edit, cancellationToken);
            var (servers, native) = GetMcpServerContainer(candidateConfig, candidate);
            if (servers is not null && servers.TryGetPropertyValue("aspire", out var server))
            {
                if (!foundAspire)
                {
                    result = (candidateConfig, candidate);
                    foundAspire = true;
                }

                // Remote servers use their URL instead of an inherited local command.
                if (native ||
                    server is not JsonObject aspire ||
                    aspire.ContainsKey("command") ||
                    (aspire["type"] is JsonValue type &&
                        type.TryGetValue<string>(out var connectionType) &&
                        connectionType == "remote"))
                {
                    return (candidateConfig, candidate);
                }
            }
        }

        return result;
    }

    public (bool Repaired, bool Added) UpdateMcpConfig(
        JsonObject config, string configFilePath, bool configureServer, AgentConfigurationScope scope)
    {
        var (_, native) = GetMcpServerContainer(config, configFilePath);
        var root = native ? config["mcp"]!.AsObject() : config;
        var configuration = native ? s_nativeMcp : s_mcp;
        var repaired = McpConfigFileHelper.UpdateDeprecatedCommand(root, configuration.ServersPropertyName);
        var added = configureServer && McpConfigFileHelper.ConfigureMcpServer(root, configuration, scope, configFilePath);
        if (added && native && !config.ContainsKey("$schema"))
        {
            config["$schema"] = s_mcp.Schema;
        }

        return (repaired, added);
    }

    // OpenCode does not yet have a verified telemetry hook schema.
    public Task<TelemetryHookConfigurationResult> ConfigureTelemetryHooksAsync(
        AgentEnvironment detectedEnvironments, CancellationToken cancellationToken)
        => Task.FromResult(new TelemetryHookConfigurationResult(AgentEnvironment.None, []));

    public override string ToString() => Id;

    private static (JsonObject? Servers, bool Native) GetMcpServerContainer(JsonObject root, string path)
    {
        var mcp = McpConfigFileHelper.GetServerContainer(root, s_mcp.ServersPropertyName, path);
        if (mcp is not null && mcp.TryGetPropertyValue("servers", out var servers))
        {
            // {"mcp":{"servers":{"type":"local","command":[...]}}} names a V1 server.
            // Otherwise V2's mcp.servers.aspire wins over mcp.aspire within this file;
            // file precedence is handled separately by ReadMcpConfigAsync.
            // https://github.com/anomalyco/opencode/blob/52a10f573f1da5103c85990da9341e3645a39365/packages/core/src/config/normalize.ts
            var legacyServer = servers is JsonObject server &&
                (AspireSkillsRemoteConfiguration.String(server["type"]) is "local" or "remote" ||
                    server["enabled"] is JsonValue enabled && enabled.TryGetValue<bool>(out _));
            if (!legacyServer)
            {
                if (servers is not JsonObject native)
                {
                    throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture,
                        AgentCommandStrings.McpServerContainerMustBeJsonObject, "mcp.servers", path));
                }
                if (native.ContainsKey("aspire") || !mcp.ContainsKey("aspire"))
                {
                    return (native, true);
                }
            }
        }

        return (mcp, false);
    }

    public string GetIntegrationConfigPath(AgentConfigurationScope scope, DirectoryInfo workspaceRoot)
        => scope is AgentConfigurationScope.Project
            ? OpenCodePaths.GetProjectConfigPath(workspaceRoot)
            : OpenCodePaths.GetGlobalMcpConfigPath(context, environment);

    public bool HasIntegrationRegistration(
        DirectoryInfo workspaceRoot, AgentConfigurationScope scope, CancellationToken cancellationToken)
    {
        // Inline settings can activate skills, but are not a writable registration target.
        if (environment.GetEnvironmentVariable(OpenCodePaths.ConfigContentEnvironmentVariable) is { Length: > 0 } inline &&
            AspireSkillsRemoteConfiguration.HasOpenCodeCatalog(AgentConfiguration.ParseObject(inline)))
        {
            return true;
        }

        foreach (var path in scope is AgentConfigurationScope.Project ? CatalogSettings(workspaceRoot) : GlobalCatalogSettings())
        {
            if (AgentConfiguration.ReadObject(path, cancellationToken) is { } config && AspireSkillsRemoteConfiguration.HasOpenCodeCatalog(config))
            {
                return true;
            }
        }

        return false;
    }

    public async Task UpdateIntegrationConfigAsync(
        JsonObject config, DirectoryInfo workspaceRoot, AgentConfigurationScope scope,
        AgentConfiguration edit, CancellationToken cancellationToken)
    {
        var configs = await AspireSkillsRemoteConfiguration.ReadSettingsAsync(
            scope is AgentConfigurationScope.Project ? CatalogSettings(workspaceRoot) : GlobalCatalogSettings(),
            GetIntegrationConfigPath(scope, workspaceRoot), edit, cancellationToken);
        if (environment.GetEnvironmentVariable(OpenCodePaths.ConfigContentEnvironmentVariable) is { Length: > 0 } inline)
        {
            var inlineConfig = AgentConfiguration.ParseObject(inline);
            if (inlineConfig.ContainsKey("skills"))
            {
                throw AspireSkillsRemoteConfiguration.ExistingConfiguration($"{OpenCodePaths.ConfigContentEnvironmentVariable}.skills");
            }
        }

        AspireSkillsRemoteConfiguration.ApplyOpenCodeCatalog(config, configs.Append(config));
    }

    private IEnumerable<string> CatalogSettings(DirectoryInfo workspaceRoot)
        => AgentPath.ProjectDirectories(context.WorkingDirectory, workspaceRoot).Reverse()
            .SelectMany(OpenCodePaths.GetProjectConfigPaths)
            .Concat(GlobalCatalogSettings()).Distinct(StringComparers.FileSystemPath);

    private IEnumerable<string> GlobalCatalogSettings()
    {
        var defaultPaths = OpenCodePaths.GetConfigPaths(OpenCodePaths.GetConfigDirectory(context, environment));
        var customDirectory = OpenCodePaths.GetOverride(OpenCodePaths.ConfigDirectoryEnvironmentVariable, context, environment);
        IEnumerable<string> customPaths = customDirectory is null ? [] : OpenCodePaths.GetConfigPaths(customDirectory);
        var globalPaths = environment.GetEnvironmentVariable(OpenCodePaths.ConfigEnvironmentVariable) is { Length: > 0 }
            ? new[] { OpenCodePaths.GetGlobalMcpConfigPath(context, environment) }.Concat(customPaths).Concat(defaultPaths)
            : defaultPaths.Concat(customPaths);

        return globalPaths
            .Concat(OpenCodePaths.GetConfigPaths(OpenCodePaths.GetManagedSettingsDirectory(context, environment))).Distinct(StringComparers.FileSystemPath);
    }
}
