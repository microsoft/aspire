// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using System.Text.Json;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.AspireSkills;
using Aspire.Cli.Agents.Playwright;
using Aspire.Cli.Npm;
using Semver;

namespace Aspire.Cli.Tests.TestServices;

/// <summary>
/// A fake implementation of <see cref="INpmRunner"/> for testing.
/// </summary>
internal sealed class FakeNpmRunner : INpmRunner
{
    public bool IsAvailable => true;

    public Task<NpmPackageInfo?> ResolvePackageAsync(string packageName, string versionRange, CancellationToken cancellationToken)
        => Task.FromResult<NpmPackageInfo?>(null);

    public Task<string?> PackAsync(string packageName, string version, string outputDirectory, CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);

    public Task<bool> AuditSignaturesAsync(string packageName, string version, CancellationToken cancellationToken)
        => Task.FromResult(true);

    public Task<bool> InstallGlobalAsync(string tarballPath, CancellationToken cancellationToken)
        => Task.FromResult(true);
}

/// <summary>
/// A fake implementation of <see cref="INpmProvenanceChecker"/> for testing.
/// </summary>
internal sealed class FakeNpmProvenanceChecker : INpmProvenanceChecker
{
    public Task<ProvenanceVerificationResult> VerifyProvenanceAsync(string packageName, string version, string expectedSourceRepository, string expectedWorkflowPath, string expectedBuildType, Func<WorkflowRefInfo, bool>? validateWorkflowRef, string? sriIntegrity, CancellationToken cancellationToken)
        => Task.FromResult(new ProvenanceVerificationResult
        {
            Outcome = ProvenanceVerificationOutcome.Verified,
            Provenance = new NpmProvenanceData { SourceRepository = expectedSourceRepository }
        });
}

/// <summary>
/// A fake implementation of <see cref="IAspireSkillsInstaller"/> for testing.
/// </summary>
internal sealed class FakeAspireSkillsInstaller : IAspireSkillsInstaller
{
    internal const string AspireCanvasExtensionName = "aspire-canvas";
    internal const string AspireInitSkillName = "aspire-init";
    internal const string AspireMonitoringSkillName = "aspire-monitoring";
    internal const string AspireOrchestrationSkillName = "aspire-orchestration";

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

        var bundleDirectory = assetKind is AgentAssetKind.Skill
            ? _bundleDirectory
            : new DirectoryInfo($"{_bundleDirectory.FullName}-extensions");
        await EnsureBundleAsync(assetKind, bundleDirectory, cancellationToken);
        var bundle = await new AspireSkillsBundleProvider().LoadAsync(
            assetKind,
            bundleDirectory,
            AspireSkillsInstaller.Version,
            AspireSkillsInstaller.Version,
            skipCompatibilityCheck: false,
            cancellationToken);
        return AspireSkillsInstallResult.Installed(bundle);
    }

    private static async Task EnsureBundleAsync(AgentAssetKind assetKind, DirectoryInfo bundleDirectory, CancellationToken cancellationToken)
    {
        if (bundleDirectory.Exists)
        {
            return;
        }

        if (assetKind is AgentAssetKind.Extension)
        {
            await EnsureExtensionBundleAsync(bundleDirectory, cancellationToken);
            return;
        }

        var files = new Dictionary<(string AssetName, string RelativePath), string>
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
            var path = Path.Combine(bundleDirectory.FullName, "skills", skillName, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content, cancellationToken);
        }

        var manifest = new BundleManifest
        {
            Version = AspireSkillsInstaller.Version,
            Supports = new BundleSupports
            {
                AspireCli = ">=0.0.0 <999.0.0",
                AspireSdk = ">=0.0.0 <999.0.0"
            },
            Assets =
            [
                CreateSkill(bundleDirectory, CommonAgentApplicators.AspireSkillName, ["evals"], files),
                CreateSkill(bundleDirectory, CommonAgentApplicators.AspireifySkillName, ["evals"], files),
                CreateSkill(bundleDirectory, CommonAgentApplicators.AspireDeploymentSkillName, ["evals"], files),
                CreateSkill(bundleDirectory, AspireInitSkillName, ["evals"], files),
                CreateSkill(bundleDirectory, AspireMonitoringSkillName, ["evals"], files),
                CreateSkill(bundleDirectory, AspireOrchestrationSkillName, ["evals"], files)
            ]
        };

        var manifestJson = JsonSerializer.Serialize(new
        {
            version = manifest.Version,
            supports = manifest.Supports,
            skills = manifest.Assets
        });
        await File.WriteAllTextAsync(Path.Combine(bundleDirectory.FullName, "skill-manifest.json"), manifestJson, cancellationToken);
    }

    private static async Task EnsureExtensionBundleAsync(DirectoryInfo bundleDirectory, CancellationToken cancellationToken)
    {
        var extensionDirectory = Path.Combine(bundleDirectory.FullName, "extensions", AspireCanvasExtensionName);
        Directory.CreateDirectory(extensionDirectory);
        var extensionPath = Path.Combine(extensionDirectory, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, "export default {};", cancellationToken);

        var manifest = new BundleManifest
        {
            Version = AspireSkillsInstaller.Version,
            Supports = new BundleSupports
            {
                AspireCli = ">=0.0.0 <999.0.0",
                AspireSdk = ">=0.0.0 <999.0.0"
            },
            Assets =
            [
                new BundleAsset
                {
                    Name = AspireCanvasExtensionName,
                    Description = "Aspire canvas extension",
                    Files =
                    [
                        new BundleFile
                        {
                            RelativePath = "extension.mjs",
                            Sha256 = ComputeSha256(extensionPath)
                        }
                    ]
                }
            ]
        };
        var manifestJson = JsonSerializer.Serialize(new
        {
            version = manifest.Version,
            supports = manifest.Supports,
            extensions = manifest.Assets
        });
        await File.WriteAllTextAsync(Path.Combine(bundleDirectory.FullName, "extension-manifest.json"), manifestJson, cancellationToken);
    }

    private static BundleAsset CreateSkill(DirectoryInfo bundleDirectory, string skillName, string[] installExcludedRelativePaths, Dictionary<(string SkillName, string RelativePath), string> files)
    {
        return new BundleAsset
        {
            Name = skillName,
            Description = $"{skillName} skill",
            InstallExcludedRelativePaths = installExcludedRelativePaths,
            Files = files
                .Where(entry => string.Equals(entry.Key.SkillName, skillName, StringComparison.Ordinal))
                .Select(entry => new BundleFile
                {
                    RelativePath = entry.Key.RelativePath,
                    Sha256 = ComputeSha256(Path.Combine(bundleDirectory.FullName, "skills", skillName, entry.Key.RelativePath))
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

/// <summary>
/// A fake implementation of <see cref="IPlaywrightCliRunner"/> for testing.
/// </summary>
internal sealed class FakePlaywrightCliRunner : IPlaywrightCliRunner
{
    public Task<SemVersion?> GetVersionAsync(CancellationToken cancellationToken)
        => Task.FromResult<SemVersion?>(null);

    public Task<bool> InstallSkillsAsync(string workingDirectory, CancellationToken cancellationToken)
        => Task.FromResult(true);
}
