// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents.Copilot;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Agents;

/// <summary>
/// Resolves the extensions supported by Copilot App without a CLI-defined fallback.
/// </summary>
internal sealed class ExtensionCatalog : IAgentAssetCatalog
{
    private readonly IAgentAssetSource _assetSource;

    /// <summary>
    /// Project-level <c>.github/extensions/</c> location.
    /// </summary>
    public static readonly AgentAssetLocation ProjectExtensions = new(
        "project",
        AgentCommandStrings.ExtensionLocation_ProjectName,
        AgentCommandStrings.ExtensionLocation_ProjectDescription,
        Path.Combine(".github", "extensions"),
        isDefault: true,
        scopes: AgentAssetLocationScope.Workspace);

    /// <summary>
    /// User-level extension location resolved through <see cref="CopilotPaths"/> to honor <c>COPILOT_HOME</c>.
    /// </summary>
    public static readonly AgentAssetLocation UserExtensions = new(
        "user",
        AgentCommandStrings.ExtensionLocation_UserName,
        AgentCommandStrings.ExtensionLocation_UserDescription,
        Path.Combine(".copilot", "extensions"),
        isDefault: false,
        scopes: AgentAssetLocationScope.User);

    public static IReadOnlyList<AgentAssetLocation> KnownLocations { get; } =
        [ProjectExtensions, UserExtensions];

    public ExtensionCatalog(IAgentAssetSource assetSource)
    {
        _assetSource = assetSource;
    }

    public string Name => "extensions";

    public IReadOnlyList<AgentClient> SupportedClients { get; } = [AgentClient.CopilotApp];

    public IReadOnlyList<AgentAssetLocation> Locations => KnownLocations;

    public AgentAssetFileInstaller FileInstaller => AgentAssetFileInstaller.ManagedDirectory;

    public async Task<AgentAssetCatalogResult> ResolveAsync(string? requestedAssets, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.Equals(requestedAssets, ConsoleInteractionService.NoneChoice, StringComparison.OrdinalIgnoreCase))
        {
            return new([], DiagnosticMessage: null, IsFailure: false);
        }

        var result = await _assetSource.GetAssetsAsync(cancellationToken);
        if (!result.IsAvailable)
        {
            return new(
                [],
                result.Message,
                IsFailure: true);
        }

        return new(result.Assets, DiagnosticMessage: null, IsFailure: false);
    }

    public IEnumerable<AgentAssetInstallTarget> ResolveInstallTargets(
        AgentAssetLocation location,
        DirectoryInfo workspaceDirectory,
        DirectoryInfo homeDirectory,
        IEnvironment environment)
    {
        if (location == UserExtensions)
        {
            yield return ResolveCopilotExtensionsInstallTarget(homeDirectory, environment);
            yield break;
        }

        foreach (var target in location.ResolveInstallTargets(workspaceDirectory, homeDirectory))
        {
            yield return target;
        }
    }

    private static AgentAssetInstallTarget ResolveCopilotExtensionsInstallTarget(DirectoryInfo homeDirectory, IEnvironment environment)
    {
        const string relativeExtensionsDirectory = "extensions";

        var configDirectory = new DirectoryInfo(CopilotPaths.GetConfigDirectory(homeDirectory, environment));
        var defaultConfigDirectory = Path.Combine(homeDirectory.FullName, ".copilot");
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        // Only the default location can be abbreviated as "~/.copilot/extensions"; a COPILOT_HOME
        // override points somewhere else entirely, so show the resolved path instead.
        var displayDirectory = string.Equals(configDirectory.FullName, defaultConfigDirectory, pathComparison)
            ? "~/.copilot/extensions"
            : Path.Combine(configDirectory.FullName, relativeExtensionsDirectory);

        return new(configDirectory, relativeExtensionsDirectory, displayDirectory);
    }
}
