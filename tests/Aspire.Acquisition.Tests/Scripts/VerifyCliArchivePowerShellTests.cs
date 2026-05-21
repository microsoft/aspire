// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;

namespace Aspire.Acquisition.Tests.Scripts;

/// <summary>
/// End-to-end coverage for <see cref="ScriptPaths.VerifyCliArchivePowerShell"/> against a synthetic
/// CLI archive that simulates embedded bundle extraction and TypeScript starter outputs.
/// This keeps the shipped-archive validation contract under test without depending on real signed artifacts.
/// </summary>
[RequiresTools(["pwsh"])]
public class VerifyCliArchivePowerShellTests(ITestOutputHelper testOutput)
{
    private readonly ITestOutputHelper _testOutput = testOutput;

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "The synthetic linux archive uses bash executable stubs.")]
    public async Task VerifyCliArchivePowerShellScript_ValidatesBundleLayoutAndTypeScriptStarter()
    {
        using var env = new TestEnvironment();

        var archive = await FakeArchiveHelper.CreateFakeBundleArchiveAsync(env.TempDirectory);

        using var cmd = new ScriptToolCommand(ScriptPaths.VerifyCliArchivePowerShell, env, _testOutput);
        cmd.WithTimeout(TimeSpan.FromSeconds(120));

        var result = await cmd.ExecuteAsync("-ArchivePath", archive.ArchivePath);

        result.EnsureSuccessful();
        Assert.Contains("Extracted bundle layout contains AppHost server assets", result.Output);
        Assert.Contains("aspire new aspire-ts-starter", result.Output);
        Assert.Contains("restore/codegen artifacts", result.Output);
    }
}
