// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public sealed class CliE2EImageBuildTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("fallback-failure")]
    [InlineData("slow-success")]
    [InlineData("stall")]
    [InlineData("forced-kill")]
    [InlineData("cancelled")]
    [InlineData("external-cancel")]
    [InlineData("all-images")]
    [InlineData("dotnet-only")]
    [RequiresTools(["node", "bash", "timeout"])]
    public async Task ImageBuildPreservesFallbackAndCancellation(string scenario)
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "GNU timeout process-group behavior requires Linux.");
        using var command = new NodeCommand(output).WithTimeout(TimeSpan.FromMinutes(2));
        var result = await command.ExecuteScriptAsync(
            Path.Combine(RepoRoot.Path, "tests", "Infrastructure.Tests", "Pipelines", "cli-e2e-image-build.harness.ts"),
            RepoRoot.Path, scenario);

        result.EnsureSuccessful();
    }

    [Fact]
    [RequiresTools(["node", "docker"])]
    public async Task DerivedImageFallbackRestoresOriginalAptSources()
    {
        using var command = new NodeCommand(output).WithTimeout(TimeSpan.FromMinutes(2));
        var result = await command.ExecuteScriptAsync(
            Path.Combine(RepoRoot.Path, "tests", "Infrastructure.Tests", "Pipelines", "cli-e2e-image-build.harness.ts"),
            RepoRoot.Path, "inherited-sources");

        result.EnsureSuccessful();
    }
}
