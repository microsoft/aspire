// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using Aspire.Cli.Agents.Configuration;
using Aspire.Cli.Agents.Playwright;
using Aspire.Cli.Resources;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents;

/// <summary>
/// Installs CLI-managed tool skills at the selected clients' shared project and user locations.
/// </summary>
internal sealed class AgentSkillInstaller(
    PlaywrightCliInstaller playwrightInstaller,
    CliExecutionContext executionContext,
    AgentConfigurationPaths paths,
    ILogger<AgentSkillInstaller> logger) : IAgentSkillInstaller
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentTargetResult>> InstallAsync(AgentInitRequest request, CancellationToken cancellationToken)
    {
        if ((!request.Assets.Playwright && !request.Assets.DotnetInspect) || request.Clients.Count == 0)
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

        // This bootstrap intentionally has no AppHost/language dependency and never installs
        // the dotnet-inspect binary. The skill invokes the matching guide through dnx on demand.
        if (request.Assets.DotnetInspect)
        {
            AgentSkillFile[] files = [new("SKILL.md", Encoding.UTF8.GetBytes(CommonAgentApplicators.DotnetInspectSkillFileContent))];
            foreach (var target in targets.Where(static target => target.Asset is AgentAssetKind.DotnetInspect && target.Error is null))
            {
                results.Add(await InstallFilesAsync(target, files, cancellationToken));
            }
        }

        return results;
    }

    private IReadOnlyList<SkillTarget> ResolveTargets(AgentInitRequest request)
    {
        Dictionary<string, SkillTarget> targets = new(AgentConfigurationPath.Comparer);
        AgentAssetKind[] assets = [AgentAssetKind.Playwright, AgentAssetKind.DotnetInspect];
        AgentConfigurationScope[] scopes = [AgentConfigurationScope.Project, AgentConfigurationScope.User];

        foreach (var client in request.Clients.Distinct())
        {
            foreach (var scope in scopes)
            {
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
                        logicalPath = Path.GetFullPath(Path.Combine(GetSkillBaseDirectory(client, scope, root), GetSkillName(asset)));
                        physicalPath = AgentConfigurationPath.Resolve(logicalPath);
                    }
                    catch (Exception ex) when (ex is AgentConfigurationException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                    {
                        physicalPath = logicalPath;
                        error = FormatInstallationError(asset, logicalPath, ex.Message);
                    }

                    // Both scopes can resolve to the same directory, as can several clients
                    // (.agents/skills is deliberately shared). Report and write that target once.
                    var key = $"{asset}:{physicalPath}";
                    if (targets.TryGetValue(key, out var existing))
                    {
                        if (!existing.Clients.Contains(client))
                        {
                            existing.Clients.Add(client);
                        }

                        existing.Aliases.Add(logicalPath);
                        if (scope is AgentConfigurationScope.Project)
                        {
                            targets[key] = existing with { Scope = scope };
                        }
                    }
                    else
                    {
                        targets.Add(key, new SkillTarget(asset, [client], physicalPath, scope, [logicalPath], error));
                    }
                }
            }
        }

        return targets.Values.ToArray();
    }

    private string GetSkillBaseDirectory(AgentClientKind client, AgentConfigurationScope scope, string root)
    {
        // These clients natively discover both project and home .agents/skills directories.
        // COPILOT_HOME and OPENCODE_CONFIG_DIR relocate their own configuration, not this
        // common compatibility location. Do not create separate per-client mirrors.
        // https://docs.github.com/en/copilot/concepts/agents/about-agent-skills
        // https://code.visualstudio.com/docs/agent-customization/agent-skills
        // https://opencode.ai/docs/skills/
        if (client is AgentClientKind.CopilotCli or AgentClientKind.CopilotApp or AgentClientKind.VsCode or AgentClientKind.OpenCode)
        {
            return Path.Combine(root, ".agents", "skills");
        }

        if (client is not AgentClientKind.ClaudeCode)
        {
            throw new ArgumentOutOfRangeException(nameof(client), client, null);
        }

        // CLAUDE_CONFIG_DIR relocates personal .claude content, including skills, but
        // not the project's .claude directory: https://code.claude.com/docs/en/claude-directory
        return scope is AgentConfigurationScope.User
            ? Path.Combine(paths.ClaudeDirectory, "skills")
            : Path.Combine(root, ".claude", "skills");
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
                var existing = await ReadExistingAsync(path, cancellationToken);
                if (existing is not null && existing.AsSpan().SequenceEqual(file.Content))
                {
                    await ValidateBeforeCommitAsync(cancellationToken);
                    continue;
                }

                await AgentFileCommitter.CommitAsync(
                    path,
                    destinationExists: existing is not null,
                    (stream, token) => stream.WriteAsync(file.Content, token).AsTask(),
                    ValidateBeforeCommitAsync,
                    newFileMode: null,
                    cancellationToken);
                changed = true;

                async Task ValidateBeforeCommitAsync(CancellationToken token)
                {
                    ValidateTargetPath(target);
                    if (!AgentConfigurationPath.Comparer.Equals(path, ResolveSkillFile(target, file.RelativePath)))
                    {
                        throw new IOException(AgentConfigurationStrings.ConcurrentChange);
                    }

                    var current = await ReadExistingAsync(path, token);
                    if (existing is null ? current is not null : current is null || !existing.AsSpan().SequenceEqual(current))
                    {
                        throw new IOException(AgentConfigurationStrings.ConcurrentChange);
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
            if (!AgentConfigurationPath.Comparer.Equals(target.Path, AgentConfigurationPath.Resolve(alias)))
            {
                throw new IOException(AgentConfigurationStrings.ConcurrentChange);
            }
        }
    }

    private static string ResolveSkillFile(SkillTarget target, string relativePath)
    {
        var logicalPath = Path.Combine(target.Path, relativePath);
        var physicalPath = AgentConfigurationPath.Resolve(logicalPath);
        // A whole skill directory can be a shared, deduplicated target. A link within
        // it must not redirect a payload write into an unrelated skill or cache.
        var relativePhysicalPath = Path.GetRelativePath(target.Path, physicalPath);
        if (Path.IsPathRooted(relativePhysicalPath) || relativePhysicalPath == ".." ||
            relativePhysicalPath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new IOException(string.Format(
                CultureInfo.CurrentCulture, AgentSkillInstallerStrings.SkillFileOutsideTarget, logicalPath));
        }

        return physicalPath;
    }

    private static async Task<byte[]?> ReadExistingAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllBytesAsync(path, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static string GetSkillName(AgentAssetKind asset) => asset switch
    {
        AgentAssetKind.Playwright => PlaywrightCliInstaller.PlaywrightCliSkillName,
        AgentAssetKind.DotnetInspect => CommonAgentApplicators.DotnetInspectSkillName,
        _ => throw new ArgumentOutOfRangeException(nameof(asset), asset, null)
    };

    private static string FormatInstallationError(AgentAssetKind asset, string path, string message) =>
        string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_FailedToInstallSkill, GetSkillName(asset), path, message);

    private sealed record SkillTarget(
        AgentAssetKind Asset,
        List<AgentClientKind> Clients,
        string Path,
        AgentConfigurationScope Scope,
        HashSet<string> Aliases,
        string? Error)
    {
        public AgentTargetResult ToResult(AgentConfigurationStatus status, string? message) =>
            new(Asset, Clients.ToArray(), Path, Scope, status, message);
    }
}

/// <summary>
/// A file captured from a managed skill payload, including non-text supporting files.
/// </summary>
internal sealed record AgentSkillFile(string RelativePath, byte[] Content);
