// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Agents.ClaudeCode;
using Aspire.Cli.Agents.Playwright;
using Aspire.Cli.Resources;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents;

/// <summary>
/// Installs only the CLI-managed Playwright and dotnet-inspect skill payloads.
/// </summary>
internal interface IAgentSkillInstaller
{
    Task<IReadOnlyList<AgentTargetResult>> InstallAsync(AgentInitRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Installs CLI-managed tool skills at the selected agents' shared locations in one scope.
/// </summary>
internal sealed class AgentSkillInstaller(
    PlaywrightCliInstaller playwrightInstaller,
    CliExecutionContext executionContext,
    IEnvironment environment,
    ILogger<AgentSkillInstaller> logger) : IAgentSkillInstaller
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentTargetResult>> InstallAsync(AgentInitRequest request, CancellationToken cancellationToken)
    {
        if ((!request.Assets.Playwright && !request.Assets.DotnetInspect) || request.Environments.Count == 0)
        {
            return [];
        }

        cancellationToken.ThrowIfCancellationRequested();
        var targets = ResolveTargets(request);
        List<AgentTargetResult> results = [];

        foreach (var target in targets.Where(static target => target.Error is not null))
        {
            results.Add(target.ToResult(AgentConfigurationStatus.Failed, target.Error));
        }

        var playwrightTargets = targets.Where(static target => target.Asset is AgentAssetKind.Playwright && target.Error is null).ToArray();
        if (playwrightTargets.Length > 0)
        {
            var installation = await playwrightInstaller.InstallAsync(cancellationToken);
            foreach (var target in playwrightTargets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(installation.Status switch
                {
                    PlaywrightInstallStatus.Installed => await InstallFilesAsync(target, installation.Files, cancellationToken),
                    PlaywrightInstallStatus.Skipped => target.ToResult(AgentConfigurationStatus.Blocked, installation.Message),
                    _ => target.ToResult(AgentConfigurationStatus.Failed, installation.Message)
                });
            }
        }

        if (request.Assets.DotnetInspect)
        {
            var files = DotnetInspectSkill.CreateFiles();
            foreach (var target in targets.Where(static target => target.Asset is AgentAssetKind.DotnetInspect && target.Error is null))
            {
                results.Add(await InstallFilesAsync(target, files, cancellationToken));
            }
        }

        return results;
    }

    private IReadOnlyList<SkillTarget> ResolveTargets(AgentInitRequest request)
    {
        Dictionary<string, SkillTarget> targets = new(AgentPath.Comparer);
        AgentAssetKind[] assets = [AgentAssetKind.Playwright, AgentAssetKind.DotnetInspect];
        foreach (var client in request.Environments.Distinct())
        {
            var scope = request.Scope;
            foreach (var asset in assets)
            {
                if ((asset is AgentAssetKind.Playwright && !request.Assets.Playwright) ||
                    (asset is AgentAssetKind.DotnetInspect && !request.Assets.DotnetInspect))
                {
                    continue;
                }

                var root = scope is AgentConfigurationScope.Project
                    ? request.WorkspaceRoot.FullName
                    : executionContext.HomeDirectory.FullName;
                var logicalPath = root;
                string physicalPath;
                string? error = null;
                try
                {
                    logicalPath = Path.GetFullPath(Path.Combine(GetSkillBaseDirectory(client, scope, request.WorkspaceRoot, executionContext, environment), GetSkillName(asset)));
                    physicalPath = AgentPath.Resolve(logicalPath);
                }
                catch (Exception ex) when (ex is AgentConfigurationException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    physicalPath = logicalPath;
                    error = FormatInstallationError(asset, logicalPath, ex.Message);
                }

                // Several agents can share .agents/skills or resolve to the same
                // physical directory. Publish and report that target only once.
                var key = $"{asset}:{physicalPath}";
                if (targets.TryGetValue(key, out var existing))
                {
                    if (!existing.Environments.Contains(client))
                    {
                        existing.Environments.Add(client);
                    }

                    existing.Aliases.Add(logicalPath);
                }
                else
                {
                    targets.Add(key, new SkillTarget(asset, [client], physicalPath, scope, [logicalPath], error));
                }
            }
        }

        return targets.Values.ToArray();
    }

    internal static string GetSkillBaseDirectory(IAgentEnvironmentScanner client, AgentConfigurationScope scope, DirectoryInfo workspaceRoot, CliExecutionContext executionContext, IEnvironment environment)
    {
        // These clients natively discover both project and home .agents/skills directories.
        // COPILOT_HOME and OPENCODE_CONFIG_DIR relocate their own configuration, not this
        // common compatibility location. Do not create separate per-client mirrors.
        // https://docs.github.com/en/copilot/concepts/agents/about-agent-skills
        // https://code.visualstudio.com/docs/agent-customization/agent-skills
        // https://opencode.ai/docs/skills/
        if (client.Id is Copilot.CopilotAgentEnvironmentScanner.ClientId or OpenCode.OpenCodeAgentEnvironmentScanner.ClientId)
        {
            var root = scope is AgentConfigurationScope.Project ? workspaceRoot.FullName : executionContext.HomeDirectory.FullName;
            return Path.Combine(root, ".agents", "skills");
        }

        if (client.Id != ClaudeCodeAgentEnvironmentScanner.ClientId)
        {
            throw new ArgumentOutOfRangeException(nameof(client), client, null);
        }

        return ClaudeCodeAgentEnvironmentScanner.GetSkillDirectory(workspaceRoot, scope, executionContext, environment);
    }

    private async Task<AgentTargetResult> InstallFilesAsync(SkillTarget target, IReadOnlyList<AgentSkillFile> files, CancellationToken cancellationToken)
    {
        try
        {
            var changed = false;
            // Supporting files precede SKILL.md so a failed reference copy does not publish
            // a new entry point. Do not prune files: extras can belong to the user.
            foreach (var file in files.OrderBy(static file => file.RelativePath == "SKILL.md"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateTargetPath(target);
                var path = ResolveSkillFile(target, file.RelativePath);
                var existing = await AgentFileWriter.ReadExistingAsync(path, cancellationToken);
                if (existing is not null && existing.AsSpan().SequenceEqual(file.Content))
                {
                    await ValidateBeforePublishAsync(cancellationToken);
                    continue;
                }

                await AgentFileWriter.WriteAsync(
                    path,
                    destinationExists: existing is not null,
                    (stream, token) => stream.WriteAsync(file.Content, token).AsTask(),
                    ValidateBeforePublishAsync,
                    newFileMode: null,
                    cancellationToken);
                changed = true;

                async Task ValidateBeforePublishAsync(CancellationToken token)
                {
                    ValidateTargetPath(target);
                    if (!AgentPath.Comparer.Equals(path, ResolveSkillFile(target, file.RelativePath)))
                    {
                        throw new IOException(AgentCommandStrings.Configuration_ConcurrentChange);
                    }

                    var current = await AgentFileWriter.ReadExistingAsync(path, token);
                    if (existing is null ? current is not null : current is null || !existing.AsSpan().SequenceEqual(current))
                    {
                        throw new IOException(AgentCommandStrings.Configuration_ConcurrentChange);
                    }
                }
            }

            return target.ToResult(changed ? AgentConfigurationStatus.Configured : AgentConfigurationStatus.Unchanged, null);
        }
        catch (Exception ex) when (ex is AgentConfigurationException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Failed to install {Asset} skill at {Target}.", target.Asset, target.Path);
            return target.ToResult(AgentConfigurationStatus.Failed, FormatInstallationError(target.Asset, target.Path, ex.Message));
        }
    }

    private static void ValidateTargetPath(SkillTarget target)
    {
        // Resolving a shared target once loses the evidence that a selected client's link
        // was repointed while npm/generation or staging ran. Revalidate every logical alias.
        foreach (var alias in target.Aliases)
        {
            if (!AgentPath.Comparer.Equals(target.Path, AgentPath.Resolve(alias)))
            {
                throw new IOException(AgentCommandStrings.Configuration_ConcurrentChange);
            }
        }
    }

    private static string ResolveSkillFile(SkillTarget target, string relativePath)
    {
        var logicalPath = Path.Combine(target.Path, relativePath);
        var physicalPath = AgentPath.Resolve(logicalPath);
        // A whole skill directory can be a shared, deduplicated target. A link within
        // it must not redirect a payload write into an unrelated skill or cache.
        var relativePhysicalPath = Path.GetRelativePath(target.Path, physicalPath);
        if (Path.IsPathRooted(relativePhysicalPath) || relativePhysicalPath == ".." ||
            relativePhysicalPath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new IOException(string.Format(
                CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_SkillFileOutsideTarget, logicalPath));
        }

        return physicalPath;
    }

    internal static string GetSkillName(AgentAssetKind asset) => asset switch
    {
        AgentAssetKind.Playwright => PlaywrightCliInstaller.PlaywrightCliSkillName,
        AgentAssetKind.DotnetInspect => DotnetInspectSkill.Name,
        _ => throw new ArgumentOutOfRangeException(nameof(asset), asset, null)
    };

    private static string FormatInstallationError(AgentAssetKind asset, string path, string message) =>
        string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_FailedToInstallSkill, GetSkillName(asset), path, message);

    private sealed record SkillTarget(
        AgentAssetKind Asset,
        List<IAgentEnvironmentScanner> Environments,
        string Path,
        AgentConfigurationScope Scope,
        HashSet<string> Aliases,
        string? Error)
    {
        public AgentTargetResult ToResult(AgentConfigurationStatus status, string? message) =>
            new(Asset, Environments.ToArray(), Path, Scope, status, message);
    }
}

/// <summary>
/// A file captured from a managed skill payload, including non-text supporting files.
/// </summary>
internal sealed record AgentSkillFile(string RelativePath, byte[] Content);
