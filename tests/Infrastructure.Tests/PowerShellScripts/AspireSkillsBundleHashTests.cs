// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Offline guards for the hashing helpers in <c>eng/scripts/aspire-skills-bundle.common.ps1</c> that
/// the embedded-bundle verification (<c>verify-aspire-skills-bundle.ps1</c>) relies on. Those helpers
/// hash the telemetry hook scripts over LF-normalized UTF-8 (no BOM) so the recorded hash is stable no
/// matter how git checked the file out — <c>track-telemetry.ps1</c> is <c>text=auto</c> and lands with
/// CRLF on Windows, while <c>track-telemetry.sh</c> is <c>eol=lf</c>.
/// </summary>
/// <remarks>
/// The hook-hash branch of the verify script only runs once a companion aspire-skills release records a
/// <c>hooks</c> metadata block, so CI does not exercise it on this repo today. That makes a normalization
/// regression invisible until the bundle update lands — an awkward place to discover it. These tests pin
/// the contract now, exercising only the offline helpers (no GitHub contents API fetch).
/// </remarks>
public sealed class AspireSkillsBundleHashTests : IDisposable
{
    // SHA-256 of the LF, UTF-8 (no BOM) bytes of CanonicalText, computed independently of the script
    // under test (so this is a real oracle, not a tautology). Every line-ending variant of the same
    // logical content must normalize to this one hash.
    private const string ExpectedSha256 = "83fd2d53ae2f0c5f2326321934026cf6f0c3397f17aa1ba0887178155c220931";

    // Canonical hook-like content using LF placeholders; each test rewrites the newlines per style.
    private const string CanonicalText = "#!/usr/bin/env bash\necho 'aspire'\n";

    private readonly TestTempDirectory _tempDir = new();
    private readonly string _commonScriptPath;
    private readonly ITestOutputHelper _output;

    public AspireSkillsBundleHashTests(ITestOutputHelper output)
    {
        _output = output;
        _commonScriptPath = Path.Combine(RepoRoot.Path, "eng", "scripts", "aspire-skills-bundle.common.ps1");
    }

    public void Dispose() => _tempDir.Dispose();

    [Theory]
    [RequiresTools(["pwsh"])]
    [InlineData(LineEndings.Lf)]
    [InlineData(LineEndings.Crlf)]
    [InlineData(LineEndings.Cr)]
    [InlineData(LineEndings.BomCrlf)]
    public async Task NormalizesEveryLineEndingVariantToTheSameHash(LineEndings lineEndings)
    {
        var inputPath = Path.Combine(_tempDir.Path, $"input-{lineEndings}.bin");
        File.WriteAllBytes(inputPath, BuildInput(lineEndings));

        var hash = await RunHashDriverAsync(inputPath);

        Assert.Equal(ExpectedSha256, hash);
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
            Write-Output (Get-AspireSkillsSha256Hex -Bytes $normalized)
            """);

        var result = await RunDriverAsync(
            driverPath,
            "-CommonScript", $"\"{_commonScriptPath}\"",
            "-InputFile", $"\"{inputPath}\"");

        var hash = ReadLines(result.Output)
            .FirstOrDefault(static l => l.Length == 64 && l.All(static c => char.IsAsciiHexDigitLower(c)));

        Assert.True(hash is not null, $"Expected a SHA-256 line in driver output:{Environment.NewLine}{result.Output}");
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
        var path = Path.Combine(_tempDir.Path, fileName);
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
