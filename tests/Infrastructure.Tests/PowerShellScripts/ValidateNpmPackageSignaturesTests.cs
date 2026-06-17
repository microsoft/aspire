// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Behavioral coverage for <c>eng/scripts/validate-npm-package-signatures.ps1</c>.
/// Drives the script with a temp Shipping directory and asserts on exit code +
/// stderr/stdout, instead of grepping the YAML that previously inlined the same
/// logic. This catches a regression in the signature heuristics (which the prior
/// content-assertion test couldn't), while staying decoupled from how/where the
/// script is wired into the pipeline.
/// </summary>
public sealed class ValidateNpmPackageSignaturesTests : IDisposable
{
    private readonly TestTempDirectory _tempDir = new();
    private readonly string _scriptPath;
    private readonly ITestOutputHelper _output;

    public ValidateNpmPackageSignaturesTests(ITestOutputHelper output)
    {
        _output = output;
        _scriptPath = Path.Combine(RepoRoot.Path, "eng", "scripts", "validate-npm-package-signatures.ps1");
    }

    public void Dispose() => _tempDir.Dispose();

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task FailsWhenShippingDirectoryDoesNotExist()
    {
        var missing = Path.Combine(_tempDir.Path, "does-not-exist");

        var result = await RunScript(missing);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Shipping packages directory not found", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task FailsWhenShippingDirectoryIsAFile()
    {
        // Pointing -ShippingDir at a file used to slip past Test-Path and fail
        // later inside Get-ChildItem with a less actionable message. The script
        // now rejects non-container paths up front (Test-Path -PathType
        // Container) and reports the same "not found (or not a directory)"
        // error so callers get one clear failure mode.
        var filePath = Path.Combine(_tempDir.Path, "not-a-directory.txt");
        File.WriteAllText(filePath, "hi");

        var result = await RunScript(filePath);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Shipping packages directory not found", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task FailsWhenNoNpmTarballsFound()
    {
        var shipping = CreateShipping();
        // Drop in a totally unrelated file so the directory isn't empty —
        // the failure must be specific ("no aspire CLI npm packages"), not
        // a generic "empty directory" message.
        File.WriteAllText(Path.Combine(shipping, "Aspire.Hosting.13.4.0.nupkg"), "decoy");

        var result = await RunScript(shipping);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("No Aspire CLI npm packages were found", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task FailsWhenSignatureSidecarIsMissing()
    {
        var shipping = CreateShipping();
        var tgz = Path.Combine(shipping, "microsoft-aspire-cli-linux-x64-13.4.0.tgz");
        File.WriteAllBytes(tgz, [0x1f, 0x8b]); // anything; content not inspected
        // No .sig file alongside it.

        var result = await RunScript(shipping);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Missing detached signature sidecar", result.Output);
        Assert.Contains("microsoft-aspire-cli-linux-x64-13.4.0.tgz.sig", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task FailsWhenSignatureIsShorterThanMinimumLength()
    {
        var shipping = CreateShipping();
        var tgz = Path.Combine(shipping, "microsoft-aspire-cli-linux-x64-13.4.0.tgz");
        File.WriteAllBytes(tgz, [0x1f, 0x8b]);
        // 32 bytes — under the 64-byte minimum the script enforces. Avoids the
        // false-positive case where an empty-but-padded sidecar slips through.
        File.WriteAllBytes($"{tgz}.sig", Enumerable.Repeat((byte)0xC2, 32).ToArray());

        var result = await RunScript(shipping);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("content sanity check", result.Output);
        Assert.Contains("only 32 bytes", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task FailsWhenSignatureHasNoPgpMarker()
    {
        var shipping = CreateShipping();
        var tgz = Path.Combine(shipping, "microsoft-aspire-cli-linux-x64-13.4.0.tgz");
        File.WriteAllBytes(tgz, [0x1f, 0x8b]);
        // 128 bytes of garbage starting with 0x00 — neither armored ("-----BEGIN")
        // nor an OpenPGP packet tag in the accepted ranges (0x88..0x8B old
        // format, 0xC2 new format; see RFC 9580 §4.3 / §5.2).
        File.WriteAllBytes($"{tgz}.sig", new byte[128]);

        var result = await RunScript(shipping);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("content sanity check", result.Output);
        Assert.Contains("no PGP signature marker", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task SucceedsForArmoredPgpSignature()
    {
        var shipping = CreateShipping();
        var tgz = Path.Combine(shipping, "microsoft-aspire-cli-linux-x64-13.4.0.tgz");
        File.WriteAllBytes(tgz, [0x1f, 0x8b]);
        var armored = Encoding.ASCII.GetBytes(
            "-----BEGIN PGP SIGNATURE-----\n" +
            "Version: GnuPG v2\n\n" +
            new string('A', 256) + "\n" +
            "-----END PGP SIGNATURE-----\n");
        File.WriteAllBytes($"{tgz}.sig", armored);

        var result = await RunScript(shipping);

        result.EnsureSuccessful();
        Assert.Contains("Validated 1 Aspire CLI npm package signature sidecar", result.Output);
    }

    [Theory]
    [InlineData(0x88)]
    [InlineData(0x89)]
    [InlineData(0x8A)]
    [InlineData(0x8B)]
    [InlineData(0xC2)]
    [RequiresTools(["pwsh"])]
    public async Task SucceedsForBinaryOpenPgpSignaturePacket(int firstByteInt)
    {
        var firstByte = (byte)firstByteInt;
        var shipping = CreateShipping();
        var tgz = Path.Combine(shipping, "microsoft-aspire-cli-linux-x64-13.4.0.tgz");
        File.WriteAllBytes(tgz, [0x1f, 0x8b]);
        // Binary OpenPGP signature packet — old-format tags 0x88..0x8B (signature
        // packet, tag 2) and new-format 0xC2 are the marker bytes the script
        // accepts. Pad to 128 bytes; the script only reads the first 64.
        var sig = new byte[128];
        sig[0] = firstByte;
        File.WriteAllBytes($"{tgz}.sig", sig);

        var result = await RunScript(shipping);

        result.EnsureSuccessful();
        Assert.Contains("Validated 1 Aspire CLI npm package signature sidecar", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task ReportsAllFailuresInOnePass()
    {
        // Two tarballs — one missing a sidecar, one with a wrong-prefix sidecar.
        // The script must surface BOTH failure categories in a single run so
        // operators diagnosing a real signing outage see every problem on one
        // CI run instead of fixing one and rediscovering the next.
        var shipping = CreateShipping();
        var tgzMissing = Path.Combine(shipping, "microsoft-aspire-cli-linux-x64-13.4.0.tgz");
        var tgzBadPrefix = Path.Combine(shipping, "microsoft-aspire-cli-win-x64-13.4.0.tgz");
        File.WriteAllBytes(tgzMissing, [0x1f, 0x8b]);
        File.WriteAllBytes(tgzBadPrefix, [0x1f, 0x8b]);
        File.WriteAllBytes($"{tgzBadPrefix}.sig", new byte[128]); // bad prefix

        var result = await RunScript(shipping);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Missing detached signature sidecar", result.Output);
        Assert.Contains("microsoft-aspire-cli-linux-x64-13.4.0.tgz.sig", result.Output);
        Assert.Contains("content sanity check", result.Output);
        Assert.Contains("microsoft-aspire-cli-win-x64-13.4.0.tgz.sig", result.Output);
    }

    private async Task<CommandResult> RunScript(string shippingDir)
    {
        using var cmd = new PowerShellCommand(_scriptPath, _output)
            .WithTimeout(TimeSpan.FromMinutes(1));

        return await cmd.ExecuteAsync(
            "-ShippingDir", $"\"{shippingDir}\"");
    }

    private string CreateShipping()
    {
        var unique = Path.GetRandomFileName();
        var path = Path.Combine(_tempDir.Path, unique, "Shipping");
        Directory.CreateDirectory(path);
        return path;
    }
}
