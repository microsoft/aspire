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
    public Task<ProvenanceVerificationResult> VerifyProvenanceAsync(string packageName, string version, string expectedSourceRepository, string expectedWorkflowPath, string expectedBuildType, Func<WorkflowRefInfo, bool>? validateWorkflowRef, CancellationToken cancellationToken, string? sriIntegrity = null)
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

    public async Task<AspireSkillsInstallResult> InstallAsync(CancellationToken cancellationToken)
    {
        if (_result is not null)
        {
            return _result;
        }

        await EnsureBundleAsync(cancellationToken);
        var bundle = await AspireSkillsBundle.LoadAsync(_bundleDirectory, cancellationToken);
        return AspireSkillsInstallResult.Installed(bundle);
    }

    private async Task EnsureBundleAsync(CancellationToken cancellationToken)
    {
        if (_bundleDirectory.Exists)
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
            Skills =
            [
                CreateSkill(CommonAgentApplicators.AspireSkillName, ["evals"], files),
                CreateSkill(CommonAgentApplicators.AspireifySkillName, ["evals"], files),
                CreateSkill(CommonAgentApplicators.AspireDeploymentSkillName, ["evals"], files),
                CreateSkill(AspireInitSkillName, ["evals"], files),
                CreateSkill(AspireMonitoringSkillName, ["evals"], files),
                CreateSkill(AspireOrchestrationSkillName, ["evals"], files)
            ]
        };

        var manifestJson = JsonSerializer.Serialize(manifest, AspireSkillsJsonSerializerContext.Default.SkillBundleManifest);
        await File.WriteAllTextAsync(Path.Combine(_bundleDirectory.FullName, "skill-manifest.json"), manifestJson, cancellationToken);
    }

    private SkillBundleSkill CreateSkill(string skillName, string[] installExcludedRelativePaths, Dictionary<(string SkillName, string RelativePath), string> files)
    {
        return new SkillBundleSkill
        {
            Name = skillName,
            Description = $"{skillName} skill",
            InstallExcludedRelativePaths = installExcludedRelativePaths,
            Files = files
                .Where(entry => string.Equals(entry.Key.SkillName, skillName, StringComparison.Ordinal))
                .Select(entry => new SkillBundleFile
                {
                    RelativePath = entry.Key.RelativePath,
                    Sha256 = ComputeSha256(Path.Combine(_bundleDirectory.FullName, "skills", skillName, entry.Key.RelativePath))
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
