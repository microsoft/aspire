// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json.Nodes;
using Aspire.Cli.Agents.Copilot;
using Aspire.Cli.Resources;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents.VsCode;

/// <summary>
/// Discovers VS Code and configures its native MCP and plugin-marketplace settings.
/// </summary>
internal sealed class VsCodeAgentEnvironmentScanner : IAgentEnvironmentScanner
{
    internal const string ClientId = "vscode";

    private readonly IVsCodeCliRunner _vsCodeCliRunner;
    private readonly CliExecutionContext _executionContext;
    private readonly IEnvironment _environment;
    private readonly ILogger<VsCodeAgentEnvironmentScanner> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="VsCodeAgentEnvironmentScanner"/>.
    /// </summary>
    /// <param name="vsCodeCliRunner">The VS Code CLI runner for checking if VS Code is installed.</param>
    /// <param name="executionContext">The CLI execution context for resolving workspace and user configuration paths.</param>
    /// <param name="environment">The environment abstraction for reading environment variables.</param>
    /// <param name="logger">The logger for diagnostic output.</param>
    public VsCodeAgentEnvironmentScanner(
        IVsCodeCliRunner vsCodeCliRunner,
        CliExecutionContext executionContext,
        IEnvironment environment,
        ILogger<VsCodeAgentEnvironmentScanner> logger)
    {
        ArgumentNullException.ThrowIfNull(vsCodeCliRunner);
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logger);
        _vsCodeCliRunner = vsCodeCliRunner;
        _executionContext = executionContext;
        _environment = environment;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Id => ClientId;

    public string DisplayName => AgentCommandStrings.Environment_VsCode;

    public override string ToString() => Id;

    /// <inheritdoc />
    public async Task ScanAsync(AgentEnvironmentScanContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogDebug("Starting VS Code environment scan in directory: {WorkingDirectory}", context.WorkingDirectory.FullName);

        var hasProjectConfiguration = HasProjectConfiguration(context.WorkingDirectory, context.WorkspaceRoot);
        var isVsCodeTerminal = _environment.GetEnvironmentVariable("TERM_PROGRAM") == "vscode";
        if (hasProjectConfiguration || isVsCodeTerminal)
        {
            var version = isVsCodeTerminal ? _environment.GetEnvironmentVariable("TERM_PROGRAM_VERSION")?.Trim() : null;
            if (string.IsNullOrEmpty(version))
            {
                version = null;
            }

            // VS Code exposes e.g. "1.110.0" or "1.111.0-insider" in TERM_PROGRAM_VERSION.
            // Retain that evidence for native user paths even when a project marker avoids CLI probes.
            context.AddDetection(new(AgentClientKind.VsCode, version, IsInsiders: version?.Contains("-insider", StringComparison.OrdinalIgnoreCase) == true));
            return;
        }

        var vsCodeVersion = await _vsCodeCliRunner.GetVersionAsync(new VsCodeRunOptions { UseInsiders = false }, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (vsCodeVersion is not null)
        {
            _logger.LogDebug("Found VS Code stable version: {Version}", vsCodeVersion);
            context.AddDetection(new(AgentClientKind.VsCode, vsCodeVersion.ToString(), IsInsiders: false));
            return;
        }

        var vsCodeInsidersVersion = await _vsCodeCliRunner.GetVersionAsync(new VsCodeRunOptions { UseInsiders = true }, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (vsCodeInsidersVersion is not null)
        {
            _logger.LogDebug("Found VS Code Insiders version: {Version}", vsCodeInsidersVersion);
            context.AddDetection(new(AgentClientKind.VsCode, vsCodeInsidersVersion.ToString(), IsInsiders: true));
        }
    }

    /// <summary>
    /// Checks for .vscode within the workspace boundary, excluding the home directory used for extensions.
    /// </summary>
    /// <param name="startDirectory">The directory to start searching from.</param>
    /// <param name="repositoryRoot">The workspace root to use as the boundary for searches.</param>
    private bool HasProjectConfiguration(DirectoryInfo startDirectory, DirectoryInfo repositoryRoot)
        => AgentPath.ProjectDirectories(startDirectory, repositoryRoot).Any(directory =>
            Path.GetRelativePath(_executionContext.HomeDirectory.FullName, directory.FullName) != "." &&
            Directory.Exists(Path.Combine(directory.FullName, ".vscode")));

    /// <inheritdoc />
    public IEnumerable<AgentConfigurationTarget> GetTargets(AgentInitRequest request)
    {
        var editions = request.Detections.Where(detection => detection.Client is AgentClientKind.VsCode)
            .Select(detection => detection.IsInsiders).Distinct().DefaultIfEmpty(false).ToArray();

        if (!request.Assets.AspireSkills && !request.Assets.Mcp)
        {
            yield break;
        }

        if (request.Assets.Mcp)
        {
            yield return Target(Path.Combine(request.WorkspaceRoot.FullName, ".vscode", "mcp.json"), AgentConfigurationScope.Project);
        }

        foreach (var insiders in editions)
        {
            var userDirectory = GetUserDirectory(insiders, _executionContext, _environment);
            foreach (var target in UserTargets(userDirectory))
            {
                yield return target;
            }

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
                yield return new AgentConfigurationTarget(profileDirectory, AgentConfigurationScope.User,
                    request.Assets.AspireSkills ? AgentAssetKind.AspireSkills : AgentAssetKind.Mcp,
                    [this], "profiles:unavailable",
                    (_, _, _) => Task.FromResult(new AgentConfigurationEdit(AgentConfigurationStatus.Failed, error)));
            }

            foreach (var profile in profiles.Order(AgentPath.Comparer))
            {
                // The reserved system-profile directory is not a user-selected profile.
                if (Path.GetFileName(profile) != "builtin" &&
                    (File.Exists(Path.Combine(profile, "settings.json")) || File.Exists(Path.Combine(profile, "mcp.json"))))
                {
                    foreach (var target in UserTargets(profile))
                    {
                        yield return target;
                    }
                }
            }
        }

        IEnumerable<AgentConfigurationTarget> UserTargets(string directory)
        {
            if (request.Assets.AspireSkills)
            {
                yield return PluginTarget(Path.Combine(directory, "settings.json"));
            }
            if (request.Assets.Mcp)
            {
                yield return Target(Path.Combine(directory, "mcp.json"), AgentConfigurationScope.User);
            }
        }

        AgentConfigurationTarget PluginTarget(string path)
            => new(path, AgentConfigurationScope.User, AgentAssetKind.AspireSkills, [this], "marketplaces:aspire",
                async (root, context, cancellationToken) =>
                {
                    // VS Code reads these workspace recommendations. Preserve their pins and
                    // disabled choices, but leave writes to the Copilot/Claude environment.
                    // https://code.visualstudio.com/docs/agent-customization/agent-plugins
                    var workspace = request.WorkspaceRoot.FullName;
                    var recommendations = await AgentConfigurationJson.ReadSettingsAsync(
                        context, CopilotPaths.ProjectSettings(request.WorkspaceRoot), cancellationToken);
                    var settingsPaths = editions.Select(edition => Path.Combine(GetUserDirectory(edition, _executionContext, _environment), "settings.json"))
                        .Append(Path.Combine(workspace, ".vscode", "settings.json"))
                        .Append(path);
                    var settings = await AgentConfigurationJson.ReadSettingsAsync(context, settingsPaths, cancellationToken);
                    if (CheckPluginSettings(settings) is { } blocked)
                    {
                        return blocked;
                    }

                    var nativePin = settings.SelectMany(settingsFile =>
                            AgentConfigurationJson.OptionalStrings(settingsFile, "chat.plugins.marketplaces") ?? [])
                        .Select(AgentConfigurationJson.String)
                        .Any(source => source is not null && IsAspireMarketplace(source) && source.Contains('#'));
                    var recommendation = new JsonObject();
                    var registration = AspireSkillsPluginConfiguration.Apply(recommendation, recommendations);
                    if (registration.Status is not AgentConfigurationStatus.Configured)
                    {
                        return registration;
                    }

                    var marketplaces = AgentConfigurationJson.OptionalStrings(root, "chat.plugins.marketplaces");
                    if (marketplaces?.Any(value => IsAspireMarketplace(AgentConfigurationJson.String(value)!)) is true)
                    {
                        return registration;
                    }
                    var source = recommendation["extraKnownMarketplaces"]![AspireSkillsPluginConfiguration.MarketplaceName]!["source"]!.AsObject();
                    if (nativePin || source.ContainsKey("ref") || source.ContainsKey("sha"))
                    {
                        return AgentConfigurationEdit.Skipped(AgentCommandStrings.Configuration_ExistingPluginPin);
                    }

                    if (marketplaces is null)
                    {
                        marketplaces = new JsonArray();
                        root["chat.plugins.marketplaces"] = marketplaces;
                    }
                    marketplaces.Add((JsonNode)AspireSkillsPluginConfiguration.Repository);
                    return registration;
                });

        AgentConfigurationTarget Target(string path, AgentConfigurationScope scope)
            => new(path, scope, AgentAssetKind.Mcp, [this], "servers:aspire",
                (root, _, _) =>
                {
                    var edit = AspireMcpConfiguration.Apply(root, "servers", commandArray: false, "stdio");
                    return Task.FromResult(scope is AgentConfigurationScope.User && edit.Status is AgentConfigurationStatus.Configured
                        ? edit with { Message = AgentCommandStrings.Configuration_ProfileLimitations }
                        : edit);
                });
    }

    private static AgentConfigurationEdit? CheckPluginSettings(IEnumerable<JsonObject> settings)
    {
        foreach (var settingsFile in settings)
        {
            if (AgentConfigurationJson.Boolean(settingsFile, "chat.plugins.enabled") is false)
            {
                return AgentConfigurationEdit.Skipped(AgentCommandStrings.Configuration_Disabled);
            }
            // These settings represent policy, not plugin installation/activation state.
            // Never enable plugins or relax strict marketplaces on the user's behalf.
            // https://code.visualstudio.com/docs/enterprise/ai-settings#manage-agent-plugins-and-marketplaces
            if (AgentConfigurationJson.Boolean(settingsFile, "chat.plugins.strictMarketplaces") is true ||
                (AgentConfigurationJson.OptionalObject(settingsFile, "chat.plugins.enabledPlugins") is { } allowed &&
                 AgentConfigurationJson.Boolean(allowed, AspireSkillsPluginConfiguration.PluginName) is not true))
            {
                return AgentConfigurationEdit.Blocked(AgentCommandStrings.Configuration_PolicyBlocked);
            }
        }

        return null;
    }

    private static bool IsAspireMarketplace(string value)
    {
        // Native marketplaces accept owner/repo, HTTPS and SCP-style git remotes.
        // Preserve an existing ref fragment, e.g. "microsoft/aspire-skills#v0.0.2".
        var source = value.Split('#', 2)[0].TrimEnd('/');
        return source.Equals(AspireSkillsPluginConfiguration.Repository, StringComparison.OrdinalIgnoreCase) ||
            source.Equals($"https://github.com/{AspireSkillsPluginConfiguration.Repository}", StringComparison.OrdinalIgnoreCase) ||
            source.Equals($"https://github.com/{AspireSkillsPluginConfiguration.Repository}.git", StringComparison.OrdinalIgnoreCase) ||
            source.Equals($"git@github.com:{AspireSkillsPluginConfiguration.Repository}.git", StringComparison.OrdinalIgnoreCase);
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
