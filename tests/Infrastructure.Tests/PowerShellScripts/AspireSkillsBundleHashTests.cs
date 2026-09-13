// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Aspire.TestUtilities;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Offline guards for the hashing helpers in <c>eng/scripts/aspire-skills-bundle.common.ps1</c> that
/// the embedded-bundle verification (<c>verify-aspire-skills-bundle.ps1</c>) relies on. Those helpers
/// hash the telemetry hook scripts over LF-normalized UTF-8 (no BOM) so the recorded hash is stable no
/// matter how git checked the file out — <c>track-telemetry.ps1</c> is <c>text=auto</c> and lands with
/// CRLF on Windows, while <c>track-telemetry.sh</c> is <c>eol=lf</c>.
/// Also exercises the maintenance scripts in an isolated repository fixture, replacing only the
/// external GitHub CLI to cover version consistency and preparation failures without network access.
/// </summary>
/// <remarks>
/// The hook-hash branch of the verify script only runs once a companion aspire-skills release records a
/// <c>hooks</c> metadata block, so CI does not exercise it on this repo today. That makes a normalization
/// regression invisible until the bundle update lands — an awkward place to discover it. These tests pin
/// the contract now without a live GitHub contents API fetch.
/// </remarks>
public sealed class AspireSkillsBundleHashTests : IDisposable
{
    // SHA-512 of the LF, UTF-8 (no BOM) bytes of CanonicalText, computed independently of the script
    // under test (so this is a real oracle, not a tautology). Every line-ending variant of the same
    // logical content must normalize to this one hash.
    private const string ExpectedSha512 = "00957ac0d67fb6cb43c6dc38038de8dc96f75375d278c907484686d38faf812eeaec1153a0e523507b4afb49cf8f39f9075afb20ab13e71e753a455d3abb78b0";

    // Canonical hook-like content using LF placeholders; each test rewrites the newlines per style.
    private const string CanonicalText = "#!/usr/bin/env bash\necho 'aspire'\n";
    private const string PowerShellHookText = "Write-Host 'aspire'\n";
    private const string ReleaseArchiveText = "updated skills archive\n";
    private const string ReleaseCommitSha = "0123456789abcdef0123456789abcdef01234567";
    private const string OriginalAssetName = "aspire-skills-v0.0.1.tgz";
    private const string VerifyScriptName = "verify-aspire-skills-bundle.ps1";
    private const string UpdateScriptName = "update-aspire-skills-bundle.ps1";

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
    [InlineData("0.0.1", "aspire-skills-v0.0.1.zip")]
    [InlineData("0.0.1", "aspire-skills-0.0.1.zip")]
    [InlineData("0.0.1", "aspire-skills-v0.0.1.tar.gz")]
    [InlineData("0.0.1", "aspire-skills-0.0.1.tar.gz")]
    [InlineData("0.0.1", "aspire-skills-v0.0.1.tgz")]
    [InlineData("0.0.1", "aspire-skills-0.0.1.tgz")]
    [InlineData("v0.0.1", "aspire-skills-0.0.1.zip")]
    [InlineData("V0.0.1", "aspire-skills-V0.0.1.TGZ")]
    [InlineData("0.0.1-preview.2+build.3", "aspire-skills-v0.0.1-preview.2+build.3.tgz")]
    public async Task VerificationAcceptsSupportedAssetNames(string version, string assetName)
    {
        CreateBundleFixture(version, assetName);

        var result = await RunMaintenanceScriptAsync(VerifyScriptName);

        result.EnsureSuccessful();
        AssertAttestationArguments(result, assetName, ReadMetadata()["tag"]!.GetValue<string>());
    }

    [Theory]
    [RequiresTools(["pwsh"])]
    [InlineData(false, null, "Embedded Aspire skills metadata must specify a version.")]
    [InlineData(true, null, "Embedded Aspire skills metadata must specify a version.")]
    [InlineData(true, "", "Embedded Aspire skills metadata must specify a version.")]
    [InlineData(true, " \t ", "Embedded Aspire skills metadata must specify a version.")]
    [InlineData(true, "v", "A version is required.")]
    [InlineData(true, "V", "A version is required.")]
    [InlineData(true, "v  ", "A version is required.")]
    public async Task VerificationRequiresMeaningfulMetadataVersion(bool includeVersion, string? version, string expectedError)
    {
        CreateBundleFixture();
        var metadata = ReadMetadata();
        if (includeVersion)
        {
            metadata["version"] = version;
        }
        else
        {
            metadata.Remove("version");
        }
        File.WriteAllText(MetadataPath, metadata.ToJsonString());

        var result = await RunMaintenanceScriptAsync(VerifyScriptName);

        Assert.Equal(expectedError, GetScriptError(result));
        Assert.Empty(GetGhCalls(result));
    }

    [Theory]
    [RequiresTools(["pwsh"])]
    [InlineData("0.0.2", OriginalAssetName)]
    [InlineData("0.0.1", "aspire-skills-v0.0.2.tgz")]
    [InlineData("0.0.1", "aspire-skills-0x0x1.zip")]
    [InlineData("0.0.1", "aspire-skills-v0.0.1.tar")]
    [InlineData("0.0.1", "aspire-skills-v0.0.1.tgz.sha512")]
    public async Task VerificationRequiresAssetForMetadataVersion(string version, string assetName)
    {
        // The fixture gives the named file a matching hash and installer version, so the asset-name
        // check itself must reject a different version or an unsupported archive format.
        CreateBundleFixture(version, assetName);

        var result = await RunMaintenanceScriptAsync(VerifyScriptName);

        Assert.Contains("does not match metadata version", GetScriptError(result), StringComparison.Ordinal);
        Assert.Empty(GetGhCalls(result));
    }

    [Theory]
    [RequiresTools(["pwsh"])]
    [InlineData(VerifyScriptName, 0)]
    [InlineData(VerifyScriptName, 2)]
    [InlineData(UpdateScriptName, 0)]
    [InlineData(UpdateScriptName, 2)]
    public async Task MaintenanceRequiresExactlyOneInstallerVersion(string scriptName, int declarationCount)
    {
        CreateBundleFixture();
        WriteInstallerVersions(Enumerable.Range(0, declarationCount).Select(i => $"0.0.{i + 1}").ToArray());
        var originalFiles = ReadTrackedFiles();

        var result = await RunMaintenanceScriptAsync(
            scriptName,
            scriptName == UpdateScriptName ? ["-Version", "0.0.2"] : []);

        var error = GetScriptError(result);
        Assert.StartsWith("Expected exactly one AspireSkillsInstaller.Version constant", error, StringComparison.Ordinal);
        Assert.EndsWith($"but found {declarationCount}.", error, StringComparison.Ordinal);
        Assert.Empty(GetGhCalls(result));
        AssertTrackedFilesUnchanged(originalFiles);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task VerificationRequiresMatchingInstallerVersion()
    {
        CreateBundleFixture();
        WriteInstallerVersions("0.0.2");

        var result = await RunMaintenanceScriptAsync(VerifyScriptName);

        Assert.Equal(
            "Embedded Aspire skills metadata version '0.0.1' must match AspireSkillsInstaller.Version '0.0.2'.",
            GetScriptError(result));
        Assert.Empty(GetGhCalls(result));
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task VerificationAcceptsEquivalentVersionPrefixes()
    {
        CreateBundleFixture("v0.0.1");
        WriteInstallerVersions("0.0.1");

        var result = await RunMaintenanceScriptAsync(VerifyScriptName);

        result.EnsureSuccessful();
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task VerificationStillRejectsArchiveHashMismatch()
    {
        CreateBundleFixture();
        File.AppendAllText(Path.Combine(EmbeddedDirectory, OriginalAssetName), "tampered");

        var result = await RunMaintenanceScriptAsync(VerifyScriptName);

        Assert.StartsWith("Embedded bundle SHA-512 mismatch.", GetScriptError(result), StringComparison.Ordinal);
        Assert.Empty(GetGhCalls(result));
    }

    [Theory]
    [RequiresTools(["pwsh"])]
    [InlineData(VerifyScriptName)]
    [InlineData(UpdateScriptName)]
    public async Task MaintenanceStillRequiresSuccessfulAttestation(string scriptName)
    {
        CreateBundleFixture();
        var originalFiles = ReadTrackedFiles();
        string[] versionArgs = scriptName == UpdateScriptName ? ["-Version", "0.0.2"] : [];

        var result = await RunMaintenanceScriptAsync(
            scriptName,
            ["-AttestationExitCode", "1", .. versionArgs]);

        Assert.Contains("exit code 1", GetScriptError(result), StringComparison.Ordinal);
        AssertAttestationArguments(
            result,
            scriptName == UpdateScriptName ? "aspire-skills-v0.0.2.tgz" : OriginalAssetName,
            scriptName == UpdateScriptName ? "v0.0.2" : "v0.0.1");
        AssertTrackedFilesUnchanged(originalFiles);
    }

    [Theory]
    [RequiresTools(["pwsh"])]
    [InlineData("0.0.1", "track-telemetry.ps1", 403)]
    [InlineData("0.0.2", "track-telemetry.ps1", 500)]
    [InlineData("0.0.2", "track-telemetry.sh", 403)]
    public async Task HookFetchFailureLeavesTrackedBundleUntouched(string version, string failedHook, int statusCode)
    {
        CreateBundleFixture();
        var originalFiles = ReadTrackedFiles();

        var result = await RunMaintenanceScriptAsync(
            UpdateScriptName,
            "-Version", version,
            "-FakeReleaseTag", $"v{version}",
            "-FakeAssetName", $"aspire-skills-v{version}.tgz",
            "-HookFailureFile", failedHook,
            "-HookStatusCode", statusCode.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.Contains($"HTTP {statusCode}", GetScriptError(result), StringComparison.Ordinal);
        AssertTrackedFilesUnchanged(originalFiles);
        var hookCalls = GetHookCalls(result);
        Assert.Equal(
            failedHook == "track-telemetry.ps1" ? ["track-telemetry.sh", "track-telemetry.ps1"] : ["track-telemetry.sh"],
            hookCalls);
        AssertAttestationArguments(result, $"aspire-skills-v{version}.tgz", $"v{version}");
    }

    [Theory]
    [RequiresTools(["pwsh"])]
    [InlineData("0.0.2", "v0.0.2", "aspire-skills-v0.0.2.zip")]
    [InlineData("v0.0.2", "0.0.2", "aspire-skills-0.0.2.zip")]
    [InlineData("V0.0.2", "v0.0.2", "aspire-skills-v0.0.2.tar.gz")]
    [InlineData("0.0.2", "0.0.2", "aspire-skills-0.0.2.tar.gz")]
    [InlineData("v0.0.2", "v0.0.2", "aspire-skills-v0.0.2.tgz")]
    [InlineData("0.0.2", "0.0.2", "aspire-skills-0.0.2.tgz")]
    public async Task UpdatesSupportedReleaseArchives(string version, string tag, string assetName)
    {
        CreateBundleFixture();

        var result = await RunMaintenanceScriptAsync(
            UpdateScriptName,
            "-Version", version,
            "-FakeReleaseTag", tag,
            "-FakeAssetName", assetName);

        result.EnsureSuccessful();
        AssertUpdatedBundle("0.0.2", tag, assetName, expectHooks: true);
        AssertAttestationArguments(result, assetName, tag);
        Assert.Equal(["track-telemetry.sh", "track-telemetry.ps1"], GetHookCalls(result));

        var verification = await RunMaintenanceScriptAsync(VerifyScriptName);
        verification.EnsureSuccessful();
        AssertAttestationArguments(verification, assetName, tag);
        Assert.Equal(["track-telemetry.sh", "track-telemetry.ps1"], GetHookCalls(verification));
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task UpdateWithoutVersionUsesCurrentMetadataVersion()
    {
        CreateBundleFixture("v0.0.1");

        var result = await RunMaintenanceScriptAsync(
            UpdateScriptName,
            "-FakeReleaseTag", "v0.0.1",
            "-FakeAssetName", OriginalAssetName);

        result.EnsureSuccessful();
        AssertUpdatedBundle("0.0.1", "v0.0.1", OriginalAssetName, expectHooks: true);
    }

    [Theory]
    [RequiresTools(["pwsh"])]
    [InlineData("track-telemetry.sh")]
    [InlineData("track-telemetry.ps1")]
    public async Task MissingHooksStillAllowUpdateWithoutChangingEitherHook(string missingHook)
    {
        CreateBundleFixture();
        var originalHooks = Directory.GetFiles(HooksDirectory)
            .ToDictionary(static path => Path.GetFileName(path), File.ReadAllBytes, StringComparer.Ordinal);

        var result = await RunMaintenanceScriptAsync(
            UpdateScriptName,
            "-Version", "0.0.2",
            "-HookFailureFile", missingHook,
            "-HookStatusCode", "404");

        result.EnsureSuccessful();
        Assert.Contains("Skipping telemetry hook sync", result.Output, StringComparison.Ordinal);
        AssertUpdatedBundle("0.0.2", "v0.0.2", "aspire-skills-v0.0.2.tgz", expectHooks: false);
        foreach (var (fileName, content) in originalHooks)
        {
            Assert.Equal(content, File.ReadAllBytes(Path.Combine(HooksDirectory, fileName)));
        }

        var verification = await RunMaintenanceScriptAsync(VerifyScriptName);
        verification.EnsureSuccessful();
        Assert.Single(GetGhCalls(verification));
        AssertAttestationArguments(verification, "aspire-skills-v0.0.2.tgz", "v0.0.2");
    }

    private string CliDirectory => Path.Combine(_workspace.Path, "src", "Aspire.Cli");
    private string SkillsDirectory => Path.Combine(CliDirectory, "Agents", "AspireSkills");
    private string EmbeddedDirectory => Path.Combine(SkillsDirectory, "Embedded");
    private string MetadataPath => Path.Combine(EmbeddedDirectory, "aspire-skills.metadata.json");
    private string InstallerPath => Path.Combine(SkillsDirectory, "AspireSkillsInstaller.cs");
    private string ProjectPath => Path.Combine(CliDirectory, "Aspire.Cli.csproj");
    private string HooksDirectory => Path.Combine(CliDirectory, "Agents", "Hooks");

    private void CreateBundleFixture(string version = "0.0.1", string assetName = OriginalAssetName)
    {
        var scriptsDirectory = _workspace.CreateDirectory(Path.Combine("eng", "scripts")).FullName;
        foreach (var scriptName in new[] { Path.GetFileName(_commonScriptPath), VerifyScriptName, UpdateScriptName })
        {
            File.Copy(Path.Combine(RepoRoot.Path, "eng", "scripts", scriptName), Path.Combine(scriptsDirectory, scriptName));
        }

        Directory.CreateDirectory(EmbeddedDirectory);
        Directory.CreateDirectory(HooksDirectory);
        var archiveBytes = Encoding.UTF8.GetBytes("original skills archive\n");
        File.WriteAllBytes(Path.Combine(EmbeddedDirectory, assetName), archiveBytes);
        var metadata = new JsonObject
        {
            ["version"] = version,
            ["repository"] = "microsoft/aspire-skills",
            ["tag"] = version.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? version : $"v{version}",
            ["assetName"] = assetName,
            ["sha512"] = Convert.ToHexStringLower(SHA512.HashData(archiveBytes))
        };
        File.WriteAllText(MetadataPath, metadata.ToJsonString());
        WriteInstallerVersions(version);
        new XDocument(
            new XElement("Project",
                new XElement("ItemGroup",
                    new XElement("EmbeddedResource",
                        new XAttribute("Include", $@"Agents\AspireSkills\Embedded\{assetName}")))))
            .Save(ProjectPath);

        File.WriteAllText(Path.Combine(HooksDirectory, "track-telemetry.sh"), "original shell hook\n");
        File.WriteAllText(Path.Combine(HooksDirectory, "track-telemetry.ps1"), "original PowerShell hook\r\n");
        File.WriteAllText(Path.Combine(_workspace.Path, "release-archive.bin"), ReleaseArchiveText);
        File.WriteAllBytes(Path.Combine(_workspace.Path, "release-track-telemetry.sh"), BuildInput(LineEndings.BomCrlf));
        File.WriteAllText(
            Path.Combine(_workspace.Path, "release-track-telemetry.ps1"),
            PowerShellHookText.Replace("\n", "\r\n"),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private void WriteInstallerVersions(params string[] versions)
    {
        File.WriteAllLines(
            InstallerPath,
            [
                "internal sealed class AspireSkillsInstaller",
                "{",
                .. versions.Select(static version => $"    internal const string Version = \"{version}\";"),
                "}"
            ]);
    }

    private JsonObject ReadMetadata() => JsonNode.Parse(File.ReadAllText(MetadataPath))!.AsObject();

    private Dictionary<string, byte[]> ReadTrackedFiles()
        => Directory.GetFiles(CliDirectory, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(CliDirectory, path), File.ReadAllBytes, StringComparer.Ordinal);

    private void AssertTrackedFilesUnchanged(Dictionary<string, byte[]> expected)
    {
        var actual = ReadTrackedFiles();
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, bytes) in expected)
        {
            Assert.Equal(bytes, actual[path]);
        }
    }

    private void AssertUpdatedBundle(string version, string tag, string assetName, bool expectHooks)
    {
        Assert.Equal(
            new[] { "aspire-skills.metadata.json", assetName }.Order(StringComparer.Ordinal),
            Directory.GetFiles(EmbeddedDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        var archiveBytes = Encoding.UTF8.GetBytes(ReleaseArchiveText);
        Assert.Equal(archiveBytes, File.ReadAllBytes(Path.Combine(EmbeddedDirectory, assetName)));

        var metadata = ReadMetadata();
        Assert.Equal(version, metadata["version"]!.GetValue<string>());
        Assert.Equal("microsoft/aspire-skills", metadata["repository"]!.GetValue<string>());
        Assert.Equal(tag, metadata["tag"]!.GetValue<string>());
        Assert.Equal(assetName, metadata["assetName"]!.GetValue<string>());
        Assert.Equal(Convert.ToHexStringLower(SHA512.HashData(archiveBytes)), metadata["sha512"]!.GetValue<string>());
        Assert.Equal(expectHooks, metadata.ContainsKey("hooks"));

        var installer = CSharpSyntaxTree.ParseText(File.ReadAllText(InstallerPath)).GetRoot();
        var versionDeclaration = Assert.Single(
            installer.DescendantNodes().OfType<VariableDeclaratorSyntax>(),
            static declaration => declaration.Identifier.ValueText == "Version");
        Assert.Equal(version, Assert.IsType<LiteralExpressionSyntax>(versionDeclaration.Initializer!.Value).Token.ValueText);
        var resource = Assert.Single(XDocument.Load(ProjectPath).Descendants("EmbeddedResource"));
        Assert.Equal($@"Agents\AspireSkills\Embedded\{assetName}", resource.Attribute("Include")!.Value);

        if (expectHooks)
        {
            var hooks = metadata["hooks"]!.AsObject();
            Assert.Equal(ReleaseCommitSha, hooks["commitSha"]!.GetValue<string>());
            var hashes = hooks["files"]!.AsObject();
            Assert.Equal(["track-telemetry.sh", "track-telemetry.ps1"], hashes.Select(static entry => entry.Key));
            foreach (var (fileName, text) in new[]
            {
                ("track-telemetry.sh", CanonicalText),
                ("track-telemetry.ps1", PowerShellHookText)
            })
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(HooksDirectory, fileName)));
                Assert.Equal(Convert.ToHexStringLower(SHA512.HashData(bytes)), hashes[fileName]!.GetValue<string>());
            }
        }
    }

    private static string GetScriptError(CommandResult result)
    {
        Assert.NotEqual(0, result.ExitCode);
        var line = Assert.Single(ReadLines(result.Output), static line => line.StartsWith("SCRIPT_ERROR=", StringComparison.Ordinal));
        return line["SCRIPT_ERROR=".Length..];
    }

    private static string[][] GetGhCalls(CommandResult result)
        => ReadLines(result.Output)
            .Where(static line => line.StartsWith("GH_ARGUMENTS=", StringComparison.Ordinal))
            .Select(static line => line["GH_ARGUMENTS=".Length..].Split('|'))
            .ToArray();

    private static string[] GetHookCalls(CommandResult result)
    {
        const string prefix = "repos/microsoft/aspire-skills/contents/hooks/scripts/";
        return GetGhCalls(result)
            .Where(static args => args[0] == "api" && args[1].StartsWith(prefix, StringComparison.Ordinal))
            .Select(static args =>
            {
                // For example: repos/.../contents/hooks/scripts/track-telemetry.sh?ref=<commit>.
                // A tag in place of the immutable commit must not satisfy the hook fetch assertions.
                var parts = args[1][prefix.Length..].Split("?ref=", StringSplitOptions.None);
                Assert.Equal(2, parts.Length);
                Assert.Equal(ReleaseCommitSha, parts[1]);
                return parts[0];
            })
            .ToArray();
    }

    private static void AssertAttestationArguments(CommandResult result, string assetName, string tag)
    {
        var arguments = Assert.Single(GetGhCalls(result), static args => args[0] == "attestation");
        Assert.Equal("verify", arguments[1]);
        Assert.Equal(assetName, Path.GetFileName(arguments[2]));
        Assert.Equal(
            [
                "--repo", "microsoft/aspire-skills",
                "--cert-identity", $"https://github.com/microsoft/aspire-skills/.github/workflows/publish.yml@refs/tags/{tag}",
                "--cert-oidc-issuer", "https://token.actions.githubusercontent.com"
            ],
            arguments[3..]);
    }

    private async Task<CommandResult> RunMaintenanceScriptAsync(string scriptName, params string[] args)
    {
        var driverPath = WriteDriver(
            "maintenance-driver.ps1",
            """
            [CmdletBinding()]
            param(
                [Parameter(Mandatory = $true)][string]$ScriptPath,
                [string]$Version,
                [string]$FakeReleaseTag = 'v0.0.2',
                [string]$FakeAssetName = 'aspire-skills-v0.0.2.tgz',
                [string]$HookFailureFile,
                [int]$HookStatusCode = 200,
                [int]$AttestationExitCode = 0
            )

            Set-StrictMode -Version Latest
            $ErrorActionPreference = 'Stop'
            $fakeCommitSha = '0123456789abcdef0123456789abcdef01234567'

            # Replace only the external CLI. The copied maintenance scripts perform all metadata,
            # version, hashing, normalization, provenance, and filesystem operations themselves.
            function gh {
                $global:LASTEXITCODE = 0
                Write-Host ("GH_ARGUMENTS=" + ($args -join '|'))
                switch ("$($args[0]) $($args[1])") {
                    'release view' {
                        if ($args[2] -ne $FakeReleaseTag) {
                            $global:LASTEXITCODE = 1
                            return
                        }
                        return (@{
                            tagName = $FakeReleaseTag
                            assets = @(@{ name = $FakeAssetName })
                        } | ConvertTo-Json -Depth 5)
                    }
                    'release download' {
                        $downloadDirectory = $args[[array]::IndexOf($args, '--dir') + 1]
                        Copy-Item -Path (Join-Path $PSScriptRoot 'release-archive.bin') -Destination (Join-Path $downloadDirectory $FakeAssetName)
                        return
                    }
                    'attestation verify' {
                        $global:LASTEXITCODE = $AttestationExitCode
                        return
                    }
                }

                if ($args[0] -eq 'api') {
                    if ($args[1] -eq "repos/microsoft/aspire-skills/commits/$FakeReleaseTag") {
                        return (@{ sha = $fakeCommitSha } | ConvertTo-Json)
                    }
                    foreach ($fileName in @('track-telemetry.sh', 'track-telemetry.ps1')) {
                        if ($args[1] -eq "repos/microsoft/aspire-skills/contents/hooks/scripts/${fileName}?ref=$fakeCommitSha") {
                            if ($fileName -eq $HookFailureFile) {
                                $global:LASTEXITCODE = 1
                                # Emit an ErrorRecord as a native stderr line would be seen by the
                                # common helper's 2>&1 pipeline: gh: Not Found (HTTP 404), for example.
                                Write-Error "gh: Fixture request failed (HTTP $HookStatusCode)" -ErrorAction Continue
                                return
                            }
                            $bytes = [System.IO.File]::ReadAllBytes((Join-Path $PSScriptRoot "release-$fileName"))
                            return (@{
                                type = 'file'
                                name = $fileName
                                content = [Convert]::ToBase64String($bytes)
                            } | ConvertTo-Json)
                        }
                    }
                }
                throw "Unexpected gh arguments: $($args -join ' ')"
            }

            $parameters = @{}
            if ($PSBoundParameters.ContainsKey('Version')) {
                $parameters.Version = $Version
            }
            try {
                & $ScriptPath @parameters
            }
            catch {
                Write-Host ("SCRIPT_ERROR=" + $_.Exception.Message)
                exit 1
            }
            """);

        using var command = new PowerShellCommand(driverPath, _output).WithTimeout(TimeSpan.FromMinutes(1));
        var scriptPath = Path.Combine(_workspace.Path, "eng", "scripts", scriptName);
        return await command.ExecuteAsync(["-ScriptPath", $"\"{scriptPath}\"", .. args]);
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