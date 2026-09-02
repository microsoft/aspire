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
    internal const string AspireInitSkillName = "aspire-init";
    internal const string AspireMonitoringSkillName = "aspire-monitoring";
    internal const string AspireOrchestrationSkillName = "aspire-orchestration";

    private readonly DirectoryInfo _bundleDirectory;
    private readonly AspireSkillsInstallResult? _skillResult;
    private readonly List<AgentAssetKind> _requestedAssetKinds = [];

    public FakeAspireSkillsInstaller(CliExecutionContext executionContext)
        : this(executionContext, result: null)
    {
    }

    public FakeAspireSkillsInstaller(CliExecutionContext executionContext, AspireSkillsInstallResult? result)
    {
        _bundleDirectory = new DirectoryInfo(Path.Combine(executionContext.WorkingDirectory.FullName, ".fake-aspire-skills-bundle"));
        _skillResult = result;
    }

    public IReadOnlyList<AgentAssetKind> RequestedAssetKinds => _requestedAssetKinds;

    public AspireSkillsInstallResult? ExtensionResult { get; init; }

    public async Task<AspireSkillsInstallResult> InstallAsync(
        AgentAssetKind assetKind,
        CancellationToken cancellationToken)
    {
        _requestedAssetKinds.Add(assetKind);
        var configuredResult = assetKind switch
        {
            AgentAssetKind.Skill => _skillResult,
            AgentAssetKind.Extension => ExtensionResult,
            _ => AspireSkillsInstallResult.Unavailable,
        };
        if (configuredResult is not null)
        {
            return configuredResult;
        }

        AspireSkillsBundleProvider provider = assetKind switch
        {
            AgentAssetKind.Skill => new SkillBundleProvider(),
            AgentAssetKind.Extension => new ExtensionBundleProvider(),
            _ => throw new ArgumentOutOfRangeException(nameof(assetKind), assetKind, null),
        };
        var bundleDirectory = new DirectoryInfo(Path.Combine(_bundleDirectory.FullName, provider.AssetKindName));
        await EnsureBundleAsync(provider, bundleDirectory, cancellationToken);
        var bundle = await provider.LoadAsync(bundleDirectory, cancellationToken);
        return AspireSkillsInstallResult.Installed(bundle);
    }

    private static async Task EnsureBundleAsync(
        AspireSkillsBundleProvider provider,
        DirectoryInfo bundleDirectory,
        CancellationToken cancellationToken)
    {
        if (bundleDirectory.Exists)
        {
            return;
        }

        if (provider.AssetKind is AgentAssetKind.Extension)
        {
            const string extensionName = "aspire-doctor";
            const string extensionContent = "export default {};";
            var extensionDirectory = Path.Combine(bundleDirectory.FullName, "extensions", extensionName);
            Directory.CreateDirectory(extensionDirectory);
            var extensionPath = Path.Combine(extensionDirectory, "extension.mjs");
            await File.WriteAllTextAsync(extensionPath, extensionContent, cancellationToken);
            var binaryPath = Path.Combine(extensionDirectory, "ui", "icon.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(binaryPath)!);
            await File.WriteAllBytesAsync(binaryPath, [0x00, 0xff, 0x80, 0x0a], cancellationToken);

            var extensionManifest = new SkillBundleManifest
            {
                Version = AspireSkillsInstaller.Version,
                Supports = new SkillBundleSupports
                {
                    AspireCli = ">=0.0.0 <999.0.0",
                    AspireSdk = ">=0.0.0 <999.0.0"
                },
                Assets =
                [
                    new SkillBundleAsset
                    {
                        Name = extensionName,
                        Description = "Runs Aspire doctor in a canvas",
                        Files =
                        [
                            new SkillBundleFile
                            {
                                RelativePath = "extension.mjs",
                                Sha512 = ComputeSha512(extensionPath)
                            },
                            new SkillBundleFile
                            {
                                RelativePath = "ui/icon.bin",
                                Sha512 = ComputeSha512(binaryPath)
                            }
                        ]
                    }
                ]
            };
            var extensionManifestJson = JsonSerializer.Serialize(
                extensionManifest,
                provider.CreateManifestTypeInfo());
            await File.WriteAllTextAsync(
                Path.Combine(bundleDirectory.FullName, provider.ManifestFileName),
                extensionManifestJson,
                cancellationToken);
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

        foreach (var ((assetName, relativePath), content) in files)
        {
            var path = Path.Combine(bundleDirectory.FullName, "skills", assetName, relativePath);
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
                CreateAgentAsset(bundleDirectory, CommonAgentApplicators.AspireSkillName, ["evals"], files),
                CreateAgentAsset(bundleDirectory, CommonAgentApplicators.AspireifySkillName, ["evals"], files),
                CreateAgentAsset(bundleDirectory, CommonAgentApplicators.AspireDeploymentSkillName, ["evals"], files),
                CreateAgentAsset(bundleDirectory, AspireInitSkillName, ["evals"], files),
                CreateAgentAsset(bundleDirectory, AspireMonitoringSkillName, ["evals"], files),
                CreateAgentAsset(bundleDirectory, AspireOrchestrationSkillName, ["evals"], files)
            ]
        };

        var manifestJson = JsonSerializer.Serialize(
            manifest,
            provider.CreateManifestTypeInfo());
        await File.WriteAllTextAsync(Path.Combine(bundleDirectory.FullName, "skill-manifest.json"), manifestJson, cancellationToken);
    }

    private static SkillBundleAsset CreateAgentAsset(
        DirectoryInfo bundleDirectory,
        string assetName,
        string[] installExcludedRelativePaths,
        Dictionary<(string AssetName, string RelativePath), string> files)
    {
        return new SkillBundleAsset
        {
            Name = assetName,
            Description = $"{assetName} skill",
            InstallExcludedRelativePaths = installExcludedRelativePaths,
            Files = files
                .Where(entry => string.Equals(entry.Key.AssetName, assetName, StringComparison.Ordinal))
                .Select(entry => new SkillBundleFile
                {
                    RelativePath = entry.Key.RelativePath,
                    Sha512 = ComputeSha512(Path.Combine(bundleDirectory.FullName, "skills", assetName, entry.Key.RelativePath))
                })
                .ToArray()
        };
    }

    private static string ComputeSha512(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA512.HashData(stream)).ToLowerInvariant();
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
