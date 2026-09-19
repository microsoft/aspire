// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents.Playwright;
using Aspire.Cli.Npm;
using Semver;

namespace Aspire.Cli.Tests.TestServices;

/// <summary>
/// A fake implementation of <see cref="INpmRunner"/> for testing.
/// </summary>
internal sealed class FakeNpmRunner : INpmRunner
{
    public bool IsAvailable { get; set; } = true;
    public NpmPackageInfo? ResolveResult { get; set; }
    public string? PackResult { get; set; } = string.Empty;
    public byte[] TarballContent { get; set; } = [1, 2, 3];
    public bool InstallGlobalResult { get; set; } = true;
    public int ResolveCallCount { get; private set; }
    public int PackCallCount { get; private set; }
    public int InstallGlobalCallCount { get; private set; }
    public string? ResolvedPackageName { get; private set; }
    public string? ResolvedVersionRange { get; private set; }
    public string? PackOutputDirectory { get; private set; }
    public string? PackedTarballPath { get; private set; }
    public string? InstalledTarballPath { get; private set; }
    public Action<CancellationToken>? OnResolvePackage { get; set; }

    public Task<NpmPackageInfo?> ResolvePackageAsync(string packageName, string versionRange, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ResolveCallCount++;
        ResolvedPackageName = packageName;
        ResolvedVersionRange = versionRange;
        OnResolvePackage?.Invoke(cancellationToken);

        return Task.FromResult(ResolveResult);
    }

    public async Task<string?> PackAsync(string packageName, string version, string outputDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PackCallCount++;
        PackOutputDirectory = outputDirectory;
        PackedTarballPath = PackResult;
        if (PackResult == string.Empty)
        {
            PackedTarballPath = Path.Combine(outputDirectory, "package.tgz");
            await File.WriteAllBytesAsync(PackedTarballPath, TarballContent, cancellationToken);
        }

        return PackedTarballPath;
    }

    public Task<bool> InstallGlobalAsync(string tarballPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InstallGlobalCallCount++;
        InstalledTarballPath = tarballPath;

        return Task.FromResult(InstallGlobalResult);
    }
}

/// <summary>
/// A fake implementation of <see cref="INpmProvenanceChecker"/> for testing.
/// </summary>
internal sealed class FakeNpmProvenanceChecker : INpmProvenanceChecker
{
    public ProvenanceVerificationOutcome ProvenanceOutcome { get; set; } = ProvenanceVerificationOutcome.Verified;
    public int CallCount { get; private set; }
    public string? CapturedPackageName { get; private set; }
    public string? CapturedVersion { get; private set; }
    public string? CapturedExpectedSourceRepository { get; private set; }
    public string? CapturedExpectedWorkflowPath { get; private set; }
    public string? CapturedExpectedBuildType { get; private set; }
    public Func<WorkflowRefInfo, bool>? CapturedValidateWorkflowRef { get; private set; }
    public string? CapturedSriIntegrity { get; private set; }

    public Task<ProvenanceVerificationResult> VerifyProvenanceAsync(string packageName, string version, string expectedSourceRepository, string expectedWorkflowPath, string expectedBuildType, Func<WorkflowRefInfo, bool>? validateWorkflowRef, string? sriIntegrity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CallCount++;
        CapturedPackageName = packageName;
        CapturedVersion = version;
        CapturedExpectedSourceRepository = expectedSourceRepository;
        CapturedExpectedWorkflowPath = expectedWorkflowPath;
        CapturedExpectedBuildType = expectedBuildType;
        CapturedValidateWorkflowRef = validateWorkflowRef;
        CapturedSriIntegrity = sriIntegrity;

        return Task.FromResult(new ProvenanceVerificationResult
        {
            Outcome = ProvenanceOutcome,
            Provenance = new NpmProvenanceData { SourceRepository = expectedSourceRepository }
        });
    }
}

/// <summary>
/// A fake implementation of <see cref="IPlaywrightCliRunner"/> for testing.
/// </summary>
internal sealed class FakePlaywrightCliRunner : IPlaywrightCliRunner
{
    public SemVersion? InstalledVersion { get; set; }
    public bool InstallSkillsResult { get; set; } = true;
    public int GetVersionCallCount { get; private set; }
    public int InstallSkillsCallCount { get; private set; }
    public string? InstallSkillsWorkingDirectory { get; private set; }
    public Action<string>? OnInstallSkills { get; set; }
    public Dictionary<string, byte[]> SkillFiles { get; } = new(StringComparer.Ordinal)
    {
        ["SKILL.md"] = """
            ---
            name: playwright-cli
            description: Browser automation with Playwright CLI.
            ---

            # Playwright CLI

            See [commands](references/commands.md).
            """u8.ToArray(),
        [Path.Combine("references", "commands.md")] = "# Playwright CLI commands"u8.ToArray()
    };

    public Task<SemVersion?> GetVersionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GetVersionCallCount++;

        return Task.FromResult(InstalledVersion);
    }

    public async Task<bool> InstallSkillsAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InstallSkillsCallCount++;
        InstallSkillsWorkingDirectory = workingDirectory;
        OnInstallSkills?.Invoke(workingDirectory);

        if (InstallSkillsResult)
        {
            foreach (var (relativePath, content) in SkillFiles)
            {
                var path = Path.Combine(
                    workingDirectory, PlaywrightCliInstaller.s_primarySkillBaseDirectory, PlaywrightCliInstaller.PlaywrightCliSkillName, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, content, cancellationToken);
            }
        }

        return InstallSkillsResult;
    }
}
