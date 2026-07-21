// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using System.Text.Json;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.AspireSkills;

namespace Aspire.Cli.Tests.TestServices;

/// <summary>
/// A fake implementation of <see cref="IAspireSkillsInstaller"/> for testing.
/// </summary>
internal sealed class FakeAspireSkillsInstaller : IAspireSkillsInstaller
{
    internal const string AspireInitSkillName = "aspire-init";
    internal const string AspireMonitoringSkillName = "aspire-monitoring";
    internal const string AspireOrchestrationSkillName = "aspire-orchestration";
    internal const string AspireDoctorExtensionName = "aspire-doctor";

    private static readonly IAspireSkillsBundleProvider s_bundleProvider = new AspireSkillsBundleProvider();

    private readonly DirectoryInfo _bundleDirectory;
    private readonly AspireSkillsInstallResult? _result;

    public FakeAspireSkillsInstaller(CliExecutionContext executionContext)
        : this(executionContext, result: null)
    {
    }

    public FakeAspireSkillsInstaller(CliExecutionContext executionContext, AspireSkillsInstallResult? result)
    {
        _bundleDirectory = new DirectoryInfo(Path.Combine(executionContext.WorkingDirectory.FullName, ".fake-aspire-skills-bundle"));
        _result = result;
    }

    public async Task<AspireSkillsInstallResult> InstallAsync(AgentAssetKind assetKind, CancellationToken cancellationToken)
    {
        if (_result is not null)
        {
            return _result;
        }

        await EnsureBundleAsync(assetKind, cancellationToken);
        var bundle = await s_bundleProvider.LoadAsync(_bundleDirectory, assetKind, cancellationToken);
        return AspireSkillsInstallResult.Installed(bundle);
    }

    private async Task EnsureBundleAsync(AgentAssetKind assetKind, CancellationToken cancellationToken)
    {
        if (assetKind is AgentAssetKind.Skill)
        {
            await EnsureSkillsBundleAsync(cancellationToken);
            return;
        }

        if (assetKind is AgentAssetKind.Extension)
        {
            await EnsureExtensionsBundleAsync(cancellationToken);
            return;
        }

        throw new InvalidOperationException($"Unsupported fake Aspire skills asset kind '{assetKind}'.");
    }

    private async Task EnsureSkillsBundleAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(Path.Combine(_bundleDirectory.FullName, "skill-manifest.json")))
        {
            return;
        }

        var files = new Dictionary<(string SkillName, string RelativePath), string>
        {
            [(CommonAgentApplicators.AspireSkillName, "SKILL.md")] =
                """
                ---
                name: aspire
                description: "Aspire CLI commands and workflows for distributed apps"
                ---

                # Aspire Skill
                """,
            [(CommonAgentApplicators.AspireSkillName, Path.Combine("references", "app-commands.md"))] = "# App commands",
            [(CommonAgentApplicators.AspireSkillName, Path.Combine("evals", "evals.json"))] = "{}",
            [(CommonAgentApplicators.AspireifySkillName, "SKILL.md")] =
                """
                ---
                name: aspireify
                description: "One-time setup: wire up AppHost with discovered projects"
                ---

                # Aspireify
                """,
            [(CommonAgentApplicators.AspireDeploymentSkillName, "SKILL.md")] =
                """
                ---
                name: aspire-deployment
                description: "Aspire deployment target selection, preflight, publish, and deploy workflows"
                ---

                # Aspire Deployment
                """,
            [(CommonAgentApplicators.AspireDeploymentSkillName, Path.Combine("references", "preflight.md"))] = "# Preflight",
            [(AspireInitSkillName, "SKILL.md")] =
                """
                ---
                name: aspire-init
                description: "First-run flow for adding Aspire to a repo"
                ---

                # Aspire Init
                """,
            [(AspireMonitoringSkillName, "SKILL.md")] =
                """
                ---
                name: aspire-monitoring
                description: "Observe Aspire apps with logs, traces, metrics, and resource state"
                ---

                # Aspire Monitoring
                """,
            [(AspireOrchestrationSkillName, "SKILL.md")] =
                """
                ---
                name: aspire-orchestration
                description: "Manage Aspire AppHost lifecycle and resource commands"
                ---

                # Aspire Orchestration
                """
        };

        foreach (var ((skillName, relativePath), content) in files)
        {
            var path = Path.Combine(_bundleDirectory.FullName, "skills", skillName, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content, cancellationToken);
        }

        var manifest = new SkillBundleManifest
        {
            Version = AspireSkillsInstaller.Version,
            Supports = new SkillBundleSupports
            {
                AspireCli = ">=0.0.0 <999.0.0",
                AspireSdk = ">=0.0.0 <999.0.0"
            },
            Assets =
            [
                CreateAsset(CommonAgentApplicators.AspireSkillName, ["evals"], files),
                CreateAsset(CommonAgentApplicators.AspireifySkillName, ["evals"], files),
                CreateAsset(CommonAgentApplicators.AspireDeploymentSkillName, ["evals"], files),
                CreateAsset(AspireInitSkillName, ["evals"], files),
                CreateAsset(AspireMonitoringSkillName, ["evals"], files),
                CreateAsset(AspireOrchestrationSkillName, ["evals"], files)
            ]
        };

        var manifestJson = JsonSerializer.Serialize(new SkillBundleManifestJson
        {
            Version = manifest.Version,
            Supports = manifest.Supports,
            Skills = manifest.Assets
        }, AspireSkillsJsonSerializerContext.Default.SkillBundleManifestJson);

        await File.WriteAllTextAsync(Path.Combine(_bundleDirectory.FullName, "skill-manifest.json"), manifestJson, cancellationToken);
    }

    private async Task EnsureExtensionsBundleAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(Path.Combine(_bundleDirectory.FullName, "extension-manifest.json")))
        {
            return;
        }

        var files = new Dictionary<(string ExtensionName, string RelativePath), string>
        {
            [(AspireDoctorExtensionName, "extension.mjs")] =
                """
                export default {
                  name: "aspire-doctor"
                };
                """
        };

        foreach (var ((extensionName, relativePath), content) in files)
        {
            var path = Path.Combine(_bundleDirectory.FullName, "extensions", extensionName, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content, cancellationToken);
        }

        var manifest = new SkillBundleManifest
        {
            Version = AspireSkillsInstaller.Version,
            Supports = new SkillBundleSupports
            {
                AspireCli = ">=0.0.0 <999.0.0",
                AspireSdk = ">=0.0.0 <999.0.0"
            },
            Assets =
            [
                CreateAsset(AspireDoctorExtensionName, [], files, "extensions")
            ]
        };

        var manifestJson = JsonSerializer.Serialize(new SkillBundleManifestJson
        {
            Version = manifest.Version,
            Supports = manifest.Supports,
            Extensions = manifest.Assets
        }, AspireSkillsJsonSerializerContext.Default.SkillBundleManifestJson);

        await File.WriteAllTextAsync(Path.Combine(_bundleDirectory.FullName, "extension-manifest.json"), manifestJson, cancellationToken);
    }

    private SkillBundleAsset CreateAsset(string assetName, string[] installExcludedRelativePaths, Dictionary<(string AssetName, string RelativePath), string> files, string relativeDirectoryName = "skills")
    {
        var assetDescription = relativeDirectoryName == "extensions" ? $"{assetName} extension" : $"{assetName} skill";

        return new SkillBundleAsset
        {
            Name = assetName,
            Description = assetDescription,
            InstallExcludedRelativePaths = installExcludedRelativePaths,
            Files = files

                .Where(entry => string.Equals(entry.Key.AssetName, assetName, StringComparison.Ordinal))
                .Select(entry => new SkillBundleFile
                {
                    RelativePath = entry.Key.RelativePath,
                    Sha256 = ComputeSha256(Path.Combine(_bundleDirectory.FullName, relativeDirectoryName, assetName, entry.Key.RelativePath))
                })
                .ToArray()
        };
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
