// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// Verifies that <c>aspire new</c> does or does not create a project-level <c>nuget.config</c>
/// depending on the selected channel.
/// </summary>
/// <remarks>
/// <para>
/// The stable channel maps only to nuget.org (the default source), so a project-level
/// <c>nuget.config</c> would be redundant and is suppressed. Other explicit channels
/// (daily, local, PR hives) require a <c>nuget.config</c> to pin Aspire packages to
/// their non-default feed.
/// </para>
/// <para>
/// See: https://github.com/microsoft/aspire/issues/18124
/// </para>
/// </remarks>
public sealed class NewChannelNuGetConfigTests(ITestOutputHelper output)
{
    [CaptureWorkspaceOnFailure]
    [Theory]
    [InlineData("stable", false)]
    [InlineData(null, true)]
    public async Task AspireNew_CreatesNuGetConfig_BasedOnChannel(string? channel, bool expectNuGetConfig)
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);

        // This test needs an explicit (non-stable) channel for the "creates nuget.config" case.
        // When no channel is passed, the CLI uses its baked-in channel. For LocalHive that's
        // the "local" channel; for PR/dev/staging install strategies it's their respective
        // explicit channel. Only a bare InstallScript (latest GA) would default to stable and
        // make the null-channel case indistinguishable from the stable case — skip in that scenario.
        if (channel is null && strategy.Mode == CliInstallMode.InstallScript && strategy.Quality is null && strategy.Version is null)
        {
            Assert.Skip(
                "This test needs a non-stable baked channel for the null-channel case. " +
                "Run with ASPIRE_E2E_ARCHIVE (LocalHive), in CI (dev/staging quality), or " +
                "against a specific non-stable build.");
        }

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot, strategy, output, mountDockerSocket: false, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        const string projectName = "NuGetConfigTest";
        await auto.AspireNewCSharpEmptyAppHostAsync(projectName, counter, channel: channel);

        var nugetConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, projectName, "nuget.config");

        if (expectNuGetConfig)
        {
            Assert.True(File.Exists(nugetConfigPath),
                $"Expected nuget.config to be created for channel '{channel ?? "(default)"}' at: {nugetConfigPath}");
        }
        else
        {
            Assert.False(File.Exists(nugetConfigPath),
                $"Expected no nuget.config for channel '{channel}' but found one at: {nugetConfigPath}");
        }
    }
}
