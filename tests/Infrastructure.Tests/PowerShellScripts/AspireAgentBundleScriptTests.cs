// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Exercises skills and extension bundle coordination in the maintenance scripts,
/// substituting only the external GitHub CLI command to keep the scenarios offline.
/// </summary>
public sealed class AspireAgentBundleScriptTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace;
    private readonly string _commonScriptPath;
    private readonly ITestOutputHelper _output;

    public AspireAgentBundleScriptTests(ITestOutputHelper output)
    {
        _output = output;
        _workspace = TemporaryWorkspace.Create(output);
        _commonScriptPath = Path.Combine(RepoRoot.Path, "eng", "scripts", "aspire-skills-bundle.common.ps1");
    }

    public void Dispose() => _workspace.Dispose();

    [Theory]
    [RequiresTools(["pwsh"])]
    [InlineData("aspire-skills", 0)]
    [InlineData("aspire-skills", 1)]
    [InlineData("aspire-extensions", 0)]
    [InlineData("aspire-extensions", 1)]
    public async Task EveryBundleRequiresItsOwnAttestation(string assetPrefix, int attestationExitCode)
    {
        var driverPath = WriteDriver(
            "attestation-driver.ps1",
            """
            [CmdletBinding()]
            param(
                [Parameter(Mandatory = $true)][string]$CommonScript,
                [Parameter(Mandatory = $true)][string]$EmbeddedDirectory,
                [Parameter(Mandatory = $true)][string]$AssetPrefix,
                [Parameter(Mandatory = $true)][int]$AttestationExitCode
            )

            $ErrorActionPreference = 'Stop'
            . $CommonScript

            # Replace only the external command; exercise metadata/hash checks and the exit-code gate.
            function gh {
                Write-Host ("GH_ARGUMENTS=" + ($args -join '|'))
                $global:LASTEXITCODE = $AttestationExitCode
            }

            $definitions = @(Get-AspireSkillsBundleDefinitions)
            Write-Output ("BUNDLES=" + ($definitions.AssetPrefix -join '|'))
            $definition = $definitions | Where-Object { $_.AssetPrefix -eq $AssetPrefix }
            $metadata = Get-Content -Raw (Join-Path $EmbeddedDirectory $definition.MetadataFileName) | ConvertFrom-Json
            $archivePath = Join-Path $EmbeddedDirectory $metadata.assetName
            $identity = "https://github.com/$($metadata.repository)/.github/workflows/publish.yml@refs/tags/$($metadata.tag)"
            Write-Output "EXPECTED_ARGUMENTS=attestation|verify|$archivePath|--repo|$($metadata.repository)|--cert-identity|$identity|--cert-oidc-issuer|https://token.actions.githubusercontent.com"
            try {
                $null = Get-AspireSkillsVerifiedBundleMetadata -Repository 'microsoft/aspire-skills' -EmbeddedDirectory $EmbeddedDirectory -Definition $definition
                Write-Output 'VERIFICATION=passed'
            }
            catch {
                if ($_.Exception.Message -notlike 'GitHub artifact attestation verification failed*') {
                    throw
                }
                Write-Output 'VERIFICATION=rejected'
            }
            """);

        var embeddedDirectory = Path.Combine(RepoRoot.Path, "src", "Aspire.Cli", "Agents", "AspireSkills", "Embedded");
        var result = await RunDriverAsync(
            driverPath,
            "-CommonScript", $"\"{_commonScriptPath}\"",
            "-EmbeddedDirectory", $"\"{embeddedDirectory}\"",
            "-AssetPrefix", assetPrefix,
            "-AttestationExitCode", attestationExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var lines = ReadLines(result.Output).ToArray();
        Assert.Equal("BUNDLES=aspire-skills|aspire-extensions", Assert.Single(lines, static l => l.StartsWith("BUNDLES=", StringComparison.Ordinal)));
        var arguments = Assert.Single(lines, static l => l.StartsWith("GH_ARGUMENTS=", StringComparison.Ordinal));
        var expectedArguments = Assert.Single(lines, static l => l.StartsWith("EXPECTED_ARGUMENTS=", StringComparison.Ordinal));
        Assert.Equal(expectedArguments["EXPECTED_ARGUMENTS=".Length..], arguments["GH_ARGUMENTS=".Length..]);
        Assert.Equal(
            attestationExitCode == 0 ? "VERIFICATION=passed" : "VERIFICATION=rejected",
            Assert.Single(lines, static l => l.StartsWith("VERIFICATION=", StringComparison.Ordinal)));
    }

    [Theory]
    [RequiresTools(["pwsh"])]
    [InlineData("aspire-skills", "aspire-extensions")]
    [InlineData("aspire-extensions", "aspire-skills")]
    public async Task SiblingBundleCannotSatisfyVerification(string assetPrefix, string siblingPrefix)
    {
        var driverPath = WriteDriver(
            "sibling-driver.ps1",
            """
            [CmdletBinding()]
            param(
                [Parameter(Mandatory = $true)][string]$CommonScript,
                [Parameter(Mandatory = $true)][string]$EmbeddedDirectory,
                [Parameter(Mandatory = $true)][string]$AssetPrefix,
                [Parameter(Mandatory = $true)][string]$SiblingPrefix
            )

            $ErrorActionPreference = 'Stop'
            . $CommonScript

            function gh {
                throw 'Attestation must not run for the wrong bundle kind.'
            }

            $definition = Get-AspireSkillsBundleDefinitions | Where-Object { $_.AssetPrefix -eq $AssetPrefix }
            $definition.MetadataFileName = "$SiblingPrefix.metadata.json"
            try {
                $null = Get-AspireSkillsVerifiedBundleMetadata -Repository 'microsoft/aspire-skills' -EmbeddedDirectory $EmbeddedDirectory -Definition $definition
                throw 'Verification accepted the wrong bundle kind.'
            }
            catch {
                if ($_.Exception.Message -notlike '*does not match its bundle kind*') {
                    throw
                }
                Write-Output 'VERIFICATION=rejected'
            }
            """);

        var embeddedDirectory = Path.Combine(RepoRoot.Path, "src", "Aspire.Cli", "Agents", "AspireSkills", "Embedded");
        var result = await RunDriverAsync(
            driverPath,
            "-CommonScript", $"\"{_commonScriptPath}\"",
            "-EmbeddedDirectory", $"\"{embeddedDirectory}\"",
            "-AssetPrefix", assetPrefix,
            "-SiblingPrefix", siblingPrefix);

        Assert.Equal("VERIFICATION=rejected", result.Output.Trim());
    }

    [Theory]
    [RequiresTools(["pwsh"])]
    [InlineData("0.0.1", true)]
    [InlineData("0.0.2", false)]
    public async Task VerificationRequiresMatchingSiblingVersions(string extensionVersion, bool versionsMatch)
    {
        var root = CreateBundleScriptFixture("0.0.1", extensionVersion);
        var driverPath = WriteDriver(
            "verify-driver.ps1",
            """
            param([string]$Root)
            $ErrorActionPreference = 'Stop'
            function gh {
                Write-Host 'ATTESTATION=passed'
                $global:LASTEXITCODE = 0
            }
            try {
                & (Join-Path $Root 'eng\scripts\verify-aspire-skills-bundle.ps1')
                Write-Output 'VERIFICATION=passed'
            }
            catch {
                if ($_.Exception.Message -notlike 'Embedded Aspire bundle metadata versions must match*') {
                    throw
                }
                Write-Output 'VERIFICATION=rejected'
            }
            """);

        var result = await RunDriverAsync(driverPath, "-Root", $"\"{root}\"");
        var lines = ReadLines(result.Output).ToArray();

        Assert.Equal(2, lines.Count(static line => line == "ATTESTATION=passed"));
        Assert.Equal(
            versionsMatch ? "VERIFICATION=passed" : "VERIFICATION=rejected",
            Assert.Single(lines, static line => line.StartsWith("VERIFICATION=", StringComparison.Ordinal)));
    }

    [Theory]
    [RequiresTools(["pwsh"])]
    [InlineData("success")]
    [InlineData("missing-sibling")]
    [InlineData("attestation-failure")]
    public async Task UpdateRequiresBothVerifiedBundles(string scenario)
    {
        var root = CreateBundleScriptFixture("0.0.1", "0.0.1");
        var embeddedDirectory = Path.Combine(root, "src", "Aspire.Cli", "Agents", "AspireSkills", "Embedded");
        var installerPath = Path.Combine(root, "src", "Aspire.Cli", "Agents", "AspireSkills", "AspireSkillsInstaller.cs");
        var projectPath = Path.Combine(root, "src", "Aspire.Cli", "Aspire.Cli.csproj");
        var originalProject = File.ReadAllText(projectPath);
        var originalInstaller = File.ReadAllText(installerPath);
        var driverPath = WriteDriver(
            "update-driver.ps1",
            """
            param([string]$Root, [string]$Scenario)
            $ErrorActionPreference = 'Stop'
            function gh {
                $global:LASTEXITCODE = 0
                if ($args[0] -eq 'release' -and $args[1] -eq 'view') {
                    $assets = @(@{ name = 'aspire-skills-v2.0.0.tgz' })
                    if ($Scenario -ne 'missing-sibling') {
                        $assets += @{ name = 'aspire-extensions-v2.0.0.tgz' }
                    }
                    return @{ tagName = 'v2.0.0'; assets = $assets } | ConvertTo-Json -Depth 4
                }
                if ($args[0] -eq 'release' -and $args[1] -eq 'download') {
                    $assetName = $args[[array]::IndexOf($args, '--pattern') + 1]
                    $directory = $args[[array]::IndexOf($args, '--dir') + 1]
                    [System.IO.File]::WriteAllText((Join-Path $directory $assetName), "updated $assetName")
                    return
                }
                if ($args[0] -eq 'attestation') {
                    if ($Scenario -eq 'attestation-failure' -and $args[2] -like '*aspire-extensions-*') {
                        $global:LASTEXITCODE = 1
                    }
                    return
                }
                if ($args[0] -eq 'api') {
                    throw 'HTTP 404: Not Found'
                }
                throw "Unexpected gh arguments: $args"
            }
            try {
                & (Join-Path $Root 'eng\scripts\update-aspire-skills-bundle.ps1') -Version '2.0.0'
                Write-Output 'UPDATE=completed'
            }
            catch {
                $message = $_.Exception.Message
                switch ($Scenario) {
                    'missing-sibling' {
                        if ($message -notlike '*does not contain a supported Aspire extensions archive asset*') { throw }
                    }
                    'attestation-failure' {
                        if ($message -notlike 'gh attestation verify *failed with exit code 1.') { throw }
                    }
                    default { throw }
                }
                Write-Output 'UPDATE=rejected'
            }
            """);

        var result = await RunDriverAsync(driverPath, "-Root", $"\"{root}\"", "-Scenario", scenario);

        var updated = scenario == "success";
        Assert.Equal(
            updated ? "UPDATE=completed" : "UPDATE=rejected",
            Assert.Single(ReadLines(result.Output), static line => line.StartsWith("UPDATE=", StringComparison.Ordinal)));
        var archiveSuffix = updated ? "v2.0.0" : "v0.0.1";
        Assert.Equal(
            [$"aspire-extensions-{archiveSuffix}.tgz", $"aspire-skills-{archiveSuffix}.tgz"],
            Directory.GetFiles(embeddedDirectory, "*.tgz").Select(Path.GetFileName).Order(StringComparer.Ordinal));

        foreach (var prefix in new[] { "aspire-skills", "aspire-extensions" })
        {
            var archiveName = $"{prefix}-{archiveSuffix}.tgz";
            Assert.Equal(
                updated ? $"updated {archiveName}" : $"original {prefix}",
                File.ReadAllText(Path.Combine(embeddedDirectory, archiveName)));
            using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(embeddedDirectory, $"{prefix}.metadata.json")));
            Assert.Equal(updated ? "2.0.0" : "0.0.1", metadata.RootElement.GetProperty("version").GetString());
            Assert.Equal(archiveName, metadata.RootElement.GetProperty("assetName").GetString());
        }

        Assert.Equal(
            updated ? originalInstaller.Replace("0.0.1", "2.0.0", StringComparison.Ordinal) : originalInstaller,
            File.ReadAllText(installerPath).Trim());
        Assert.Equal(
            updated ? originalProject.Replace("0.0.1", "2.0.0", StringComparison.Ordinal) : originalProject,
            File.ReadAllText(projectPath).Trim());
    }

    private string CreateBundleScriptFixture(string skillsVersion, string extensionVersion)
    {
        var root = Path.Combine(_workspace.Path, "repo");
        var scriptsDirectory = Path.Combine(root, "eng", "scripts");
        var embeddedDirectory = Path.Combine(root, "src", "Aspire.Cli", "Agents", "AspireSkills", "Embedded");
        Directory.CreateDirectory(scriptsDirectory);
        Directory.CreateDirectory(embeddedDirectory);
        foreach (var scriptName in new[] { "aspire-skills-bundle.common.ps1", "verify-aspire-skills-bundle.ps1", "update-aspire-skills-bundle.ps1" })
        {
            File.Copy(Path.Combine(RepoRoot.Path, "eng", "scripts", scriptName), Path.Combine(scriptsDirectory, scriptName));
        }

        foreach (var (prefix, version) in new[] { ("aspire-skills", skillsVersion), ("aspire-extensions", extensionVersion) })
        {
            // Maintenance scripts hash archives rather than parsing them.
            var assetName = $"{prefix}-v{version}.tgz";
            var bytes = Encoding.UTF8.GetBytes($"original {prefix}");
            File.WriteAllBytes(Path.Combine(embeddedDirectory, assetName), bytes);
            File.WriteAllText(
                Path.Combine(embeddedDirectory, $"{prefix}.metadata.json"),
                JsonSerializer.Serialize(new
                {
                    version,
                    repository = "microsoft/aspire-skills",
                    tag = $"v{version}",
                    assetName,
                    sha512 = Convert.ToHexStringLower(SHA512.HashData(bytes))
                }));
        }

        File.WriteAllText(
            Path.Combine(root, "src", "Aspire.Cli", "Agents", "AspireSkills", "AspireSkillsInstaller.cs"),
            $"""internal const string Version = "{skillsVersion}";""");
        File.WriteAllText(
            Path.Combine(root, "src", "Aspire.Cli", "Aspire.Cli.csproj"),
            $"""<Project><ItemGroup><EmbeddedResource Include="Agents\AspireSkills\Embedded\aspire-skills-v{skillsVersion}.tgz" /><EmbeddedResource Include="Agents\AspireSkills\Embedded\aspire-extensions-v{extensionVersion}.tgz" /></ItemGroup></Project>""");

        return root;
    }

    private async Task<CommandResult> RunDriverAsync(string driverPath, params string[] args)
    {
        using var cmd = new PowerShellCommand(driverPath, _output).WithTimeout(TimeSpan.FromMinutes(1));
        var result = await cmd.ExecuteAsync(args);
        result.EnsureSuccessful();
        return result;
    }

    private string WriteDriver(string fileName, string content)
    {
        var path = Path.Combine(_workspace.Path, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private static IEnumerable<string> ReadLines(string output)
        => output.Split('\n').Select(static l => l.Trim('\r', ' '));
}