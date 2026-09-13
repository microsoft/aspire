// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Offline guards for the hashing helpers in <c>eng/scripts/aspire-skills-bundle.common.ps1</c> that
/// the embedded-bundle verification (<c>verify-aspire-skills-bundle.ps1</c>) relies on. Those helpers
/// hash the telemetry hook scripts over LF-normalized UTF-8 (no BOM) so the recorded hash is stable no
/// matter how git checked the file out — <c>track-telemetry.ps1</c> is <c>text=auto</c> and lands with
/// CRLF on Windows, while <c>track-telemetry.sh</c> is <c>eol=lf</c>.
/// Also verifies that each sibling bundle requires its own matching asset and successful attestation,
/// substituting only the external GitHub CLI command to keep these checks offline.
/// </summary>
/// <remarks>
/// The hook-hash branch of the verify script only runs once a companion aspire-skills release records a
/// <c>hooks</c> metadata block, so CI does not exercise it on this repo today. That makes a normalization
/// regression invisible until the bundle update lands — an awkward place to discover it. These tests pin
/// the contract now, exercising only the offline helpers (no GitHub contents API fetch).
/// </remarks>
public sealed class AspireSkillsBundleHashTests : IDisposable
{
    // SHA-512 of the LF, UTF-8 (no BOM) bytes of CanonicalText, computed independently of the script
    // under test (so this is a real oracle, not a tautology). Every line-ending variant of the same
    // logical content must normalize to this one hash.
    private const string ExpectedSha512 = "00957ac0d67fb6cb43c6dc38038de8dc96f75375d278c907484686d38faf812eeaec1153a0e523507b4afb49cf8f39f9075afb20ab13e71e753a455d3abb78b0";

    // Canonical hook-like content using LF placeholders; each test rewrites the newlines per style.
    private const string CanonicalText = "#!/usr/bin/env bash\necho 'aspire'\n";

    private readonly TemporaryWorkspace _workspace;
    private readonly string _commonScriptPath;
    private readonly ITestOutputHelper _output;

    public AspireSkillsBundleHashTests(ITestOutputHelper output)
    {
        _output = output;
        _workspace = TemporaryWorkspace.Create(output);
        _commonScriptPath = Path.Combine(RepoRoot.Path, "eng", "scripts", "aspire-skills-bundle.common.ps1");
    }

    public void Dispose() => _workspace.Dispose();

    [Theory]
    [RequiresTools(["pwsh"])]
    [InlineData(LineEndings.Lf)]
    [InlineData(LineEndings.Crlf)]
    [InlineData(LineEndings.Cr)]
    [InlineData(LineEndings.BomCrlf)]
    public async Task NormalizesEveryLineEndingVariantToTheSameHash(LineEndings lineEndings)
    {
        var inputPath = Path.Combine(_workspace.Path, $"input-{lineEndings}.bin");
        File.WriteAllBytes(inputPath, BuildInput(lineEndings));

        var hash = await RunHashDriverAsync(inputPath);

        Assert.Equal(ExpectedSha512, hash);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task HookFileNamesMatchTheEmbeddedHookScripts()
    {
        var names = await RunHookNamesDriverAsync();

        // The verify loop iterates Get-AspireSkillsHookFileNames and requires a recorded hash for each,
        // so the list must stay in lockstep with the hook scripts actually shipped. A rename on disk that
        // is not mirrored in the array would leave the renamed script unverified with no failure signal.
        var hooksDir = Path.Combine(RepoRoot.Path, "src", "Aspire.Cli", "Agents", "Hooks");
        var onDisk = Directory.EnumerateFiles(hooksDir, "track-telemetry.*")
            .Select(Path.GetFileName)
            .OrderBy(static n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(onDisk, names.OrderBy(static n => n, StringComparer.Ordinal).ToArray());
    }

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
    public async Task VerificationChecksSiblingVersionsWithoutRequiringInstallerSource(string extensionVersion, bool versionsMatch)
    {
        var root = CreateBundleScriptFixture("0.0.1", extensionVersion);
        File.Delete(Path.Combine(root, "src", "Aspire.Cli", "Agents", "AspireSkills", "AspireSkillsInstaller.cs"));
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
    [InlineData("hook-failure")]
    public async Task UpdatePreservesSiblingPreparationAndExistingHookFailureOrder(string scenario)
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
                    if ($Scenario -eq 'hook-failure') {
                        throw 'HTTP 401: Unauthorized'
                    }
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
                    'hook-failure' {
                        if ($message -notlike '*HTTP 401*') { throw }
                    }
                    default { throw }
                }
                Write-Output 'UPDATE=rejected'
            }
            """);

        var result = await RunDriverAsync(driverPath, "-Root", $"\"{root}\"", "-Scenario", scenario);

        Assert.Equal(
            scenario == "success" ? "UPDATE=completed" : "UPDATE=rejected",
            Assert.Single(ReadLines(result.Output), static line => line.StartsWith("UPDATE=", StringComparison.Ordinal)));
        var archivesUpdated = scenario is "success" or "hook-failure";
        var archiveSuffix = archivesUpdated ? "v2.0.0" : "legacy";
        Assert.Equal(
            [$"aspire-extensions-{archiveSuffix}.tgz", $"aspire-skills-{archiveSuffix}.tgz"],
            Directory.GetFiles(embeddedDirectory, "*.tgz").Select(Path.GetFileName).Order(StringComparer.Ordinal));

        foreach (var prefix in new[] { "aspire-skills", "aspire-extensions" })
        {
            var archiveName = $"{prefix}-{archiveSuffix}.tgz";
            Assert.Equal(
                archivesUpdated ? $"updated {archiveName}" : $"original {prefix}",
                File.ReadAllText(Path.Combine(embeddedDirectory, archiveName)));
            using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(embeddedDirectory, $"{prefix}.metadata.json")));
            Assert.Equal(scenario == "success" ? "2.0.0" : "0.0.1", metadata.RootElement.GetProperty("version").GetString());
            Assert.Equal(
                scenario == "success" ? $"{prefix}-v2.0.0.tgz" : $"{prefix}-legacy.tgz",
                metadata.RootElement.GetProperty("assetName").GetString());
        }

        Assert.Equal(
            scenario == "success" ? originalInstaller.Replace("0.0.1", "2.0.0", StringComparison.Ordinal) : originalInstaller,
            File.ReadAllText(installerPath).Trim());
        Assert.Equal(
            scenario == "success" ? originalProject.Replace("legacy", "v2.0.0", StringComparison.Ordinal) : originalProject,
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
            // Maintenance scripts hash archives rather than parsing them. Legacy names also
            // ensure sibling identity checks do not introduce metadata-to-filename version checks.
            var assetName = $"{prefix}-legacy.tgz";
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
            """internal const string Version = "0.0.1";""");
        File.WriteAllText(
            Path.Combine(root, "src", "Aspire.Cli", "Aspire.Cli.csproj"),
            """<Project><ItemGroup><EmbeddedResource Include="Agents\AspireSkills\Embedded\aspire-skills-legacy.tgz" /><EmbeddedResource Include="Agents\AspireSkills\Embedded\aspire-extensions-legacy.tgz" /></ItemGroup></Project>""");

        return root;
    }

    private async Task<string> RunHashDriverAsync(string inputPath)
    {
        // Dot-source the library and run only its offline helpers; the C# side owns the exact input bytes
        // (including CRLF/CR/BOM) so the script under test is what decides the resulting hash.
        var driverPath = WriteDriver(
            "hash-driver.ps1",
            """
            [CmdletBinding()]
            param(
                [Parameter(Mandatory = $true)][string]$CommonScript,
                [Parameter(Mandatory = $true)][string]$InputFile
            )

            Set-StrictMode -Version Latest
            $ErrorActionPreference = 'Stop'

            . $CommonScript

            $bytes = [System.IO.File]::ReadAllBytes($InputFile)
            $normalized = ConvertTo-LfUtf8Bytes -Bytes $bytes
            Write-Output (Get-AspireSkillsSha512Hex -Bytes $normalized)
            """);

        var result = await RunDriverAsync(
            driverPath,
            "-CommonScript", $"\"{_commonScriptPath}\"",
            "-InputFile", $"\"{inputPath}\"");

        var hash = ReadLines(result.Output)
            .FirstOrDefault(static l => l.Length == 128 && l.All(static c => char.IsAsciiHexDigitLower(c)));

        Assert.True(hash is not null, $"Expected a SHA-512 line in driver output:{Environment.NewLine}{result.Output}");
        return hash!;
    }

    private async Task<string[]> RunHookNamesDriverAsync()
    {
        var driverPath = WriteDriver(
            "names-driver.ps1",
            """
            [CmdletBinding()]
            param([Parameter(Mandatory = $true)][string]$CommonScript)

            Set-StrictMode -Version Latest
            $ErrorActionPreference = 'Stop'

            . $CommonScript

            Write-Output ("NAMES=" + ((Get-AspireSkillsHookFileNames) -join ';'))
            """);

        var result = await RunDriverAsync(driverPath, "-CommonScript", $"\"{_commonScriptPath}\"");

        var line = ReadLines(result.Output)
            .FirstOrDefault(static l => l.StartsWith("NAMES=", StringComparison.Ordinal));

        Assert.NotNull(line);
        return line!["NAMES=".Length..].Split(';', StringSplitOptions.RemoveEmptyEntries);
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

    private static byte[] BuildInput(LineEndings lineEndings)
    {
        var newline = lineEndings switch
        {
            LineEndings.Lf => "\n",
            LineEndings.Crlf or LineEndings.BomCrlf => "\r\n",
            LineEndings.Cr => "\r",
            _ => throw new ArgumentOutOfRangeException(nameof(lineEndings))
        };

        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var body = utf8.GetBytes(CanonicalText.Replace("\n", newline));

        if (lineEndings != LineEndings.BomCrlf)
        {
            return body;
        }

        // Prepend a UTF-8 BOM (EF BB BF) so the helper's BOM-stripping path is exercised too.
        ReadOnlySpan<byte> bom = [0xEF, 0xBB, 0xBF];
        var withBom = new byte[bom.Length + body.Length];
        bom.CopyTo(withBom);
        body.CopyTo(withBom.AsSpan(bom.Length));
        return withBom;
    }

    public enum LineEndings
    {
        Lf,
        Crlf,
        Cr,
        BomCrlf
    }
}