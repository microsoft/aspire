// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;

namespace Aspire.Acquisition.Tests.Scripts;

/// <summary>
/// End-to-end coverage for <see cref="ScriptPaths.VerifyCliArchivePowerShell"/> against synthetic
/// per-RID CLI archives. Keeps the signed-build verifier contract under test in GitHub CI without
/// needing real signed artifacts or network access. The fake archive's `aspire` mock is a bash
/// script, so these tests are Unix-only.
/// </summary>
[RequiresTools(["pwsh"])]
public class VerifyCliArchivePowerShellTests(ITestOutputHelper testOutput)
{
    private readonly ITestOutputHelper _testOutput = testOutput;

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "The synthetic linux archive uses a bash executable stub.")]
    public async Task VerifyCliArchive_AcceptsCleanPerRidArchive()
    {
        using var env = new TestEnvironment();

        var archive = await FakeArchiveHelper.CreateFakeVerifyArchiveAsync(env.TempDirectory);

        using var cmd = new ScriptToolCommand(ScriptPaths.VerifyCliArchivePowerShell, env, _testOutput);
        cmd.WithTimeout(TimeSpan.FromSeconds(60));

        var result = await cmd.ExecuteAsync("-ArchivePath", archive.ArchivePath);

        result.EnsureSuccessful();
        Assert.Contains("aspire mock v1.0", result.Output);
        Assert.Contains("'aspire new aspire-starter' created project successfully", result.Output);
        Assert.Contains("linux-* archive correctly omits the install-route sidecar", result.Output);
        Assert.Contains("All verification checks passed", result.Output);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "The synthetic linux archive uses a bash executable stub.")]
    public async Task VerifyCliArchive_RejectsArchiveWithStraySidecar()
    {
        using var env = new TestEnvironment();

        var archive = await FakeArchiveHelper.CreateFakeVerifyArchiveAsync(env.TempDirectory, includeStraySidecar: true);

        using var cmd = new ScriptToolCommand(ScriptPaths.VerifyCliArchivePowerShell, env, _testOutput);
        cmd.WithTimeout(TimeSpan.FromSeconds(60));

        var result = await cmd.ExecuteAsync("-ArchivePath", archive.ArchivePath);

        Assert.NotEqual(0, result.ExitCode);
        // The exact error wording is part of the verifier's user-facing contract; see
        // eng/scripts/verify-cli-archive.ps1 Test-ArchiveSidecar.
        Assert.Contains(".aspire-install.json", result.Output);
        Assert.Contains("per-RID archives are shared across install routes", result.Output);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "The synthetic linux archive uses a bash executable stub.")]
    public async Task VerifyCliArchive_RejectsStraySidecarWhenBinaryIsNestedUnderSubdirectory()
    {
        // Regression guard: Get-ArchiveRoot returns a single subdirectory when the
        // binary is nested, and the sidecar scan must inspect the true archive root
        // (the extraction directory), not whichever subdirectory holds the binary.
        // Without this guard the verifier would silently accept a per-RID archive
        // that ships .aspire-install.json alongside a nested payload directory.
        using var env = new TestEnvironment();

        var archive = await FakeArchiveHelper.CreateFakeVerifyArchiveAsync(
            env.TempDirectory,
            includeStraySidecar: true,
            nestAspireUnderSubdir: true);

        using var cmd = new ScriptToolCommand(ScriptPaths.VerifyCliArchivePowerShell, env, _testOutput);
        cmd.WithTimeout(TimeSpan.FromSeconds(60));

        var result = await cmd.ExecuteAsync("-ArchivePath", archive.ArchivePath);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(".aspire-install.json", result.Output);
        Assert.Contains("per-RID archives are shared across install routes", result.Output);
    }
}
