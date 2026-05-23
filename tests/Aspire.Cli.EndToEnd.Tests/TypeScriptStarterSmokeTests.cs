// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end smoke tests for the TypeScript Express/React starter template (aspire-ts-starter).
/// Validates that <c>aspire new</c> creates a working Express API + React frontend project,
/// the resulting <c>aspire.config.json</c> agrees with the CLI's identity channel, and
/// <c>aspire run</c> starts it successfully.
/// <para>
/// Class name ends in <c>SmokeTests</c> so the daily smoke workflow
/// (<c>.github/workflows/tests-daily-smoke.yml</c>) picks it up via its
/// <c>--filter-class "*SmokeTests"</c> filter — which exercises the CLI against the actual
/// daily-channel build from <c>https://aka.ms/dotnet/9/aspire/daily</c>. This is the
/// regression coverage that catches identity-channel resolution bugs invisible on PR builds
/// (PR CLIs bake <c>pr-&lt;N&gt;</c> as their identity, which always took the well-tested
/// local-build branch even before the identity-channel selection was widened).
/// </para>
/// </summary>
public sealed class TypeScriptStarterSmokeTests(ITestOutputHelper output)
{
    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task CreateAndRunTypeScriptStarterProject()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        // Step 1: Create project using aspire new, selecting the Express/React template.
        // Deliberately no `--channel` arg — exercises the implicit-channel path where the
        // resolved channel falls out of CliExecutionContext.IdentityChannel.
        await auto.AspireNewAsync("TsStarterApp", counter, template: AspireTemplate.ExpressReact);

        // Step 1.5: Verify starter creation also restored the generated TypeScript SDK.
        var projectRoot = Path.Combine(workspace.WorkspaceRoot.FullName, "TsStarterApp");
        GitIgnoreAssertions.AssertContainsEntry(projectRoot, ".aspire/");
        var modulesDir = Path.Combine(projectRoot, ".aspire/modules");

        if (!Directory.Exists(modulesDir))
        {
            throw new InvalidOperationException($".aspire/modules directory was not created at {modulesDir}");
        }

        var aspireModulePath = Path.Combine(modulesDir, "aspire.mts");
        if (!File.Exists(aspireModulePath))
        {
            throw new InvalidOperationException($"Expected generated file not found: {aspireModulePath}");
        }

        // Step 1.6: Regression guard for the daily-channel restore failure. The bug class
        // we're locking out: a CLI whose IdentityChannel is daily/staging would resolve
        // `Aspire.ProjectTemplates` from the Implicit (nuget.org) channel — yielding a
        // stable SDK version — but the template factories still pinned `channel` to the
        // CLI's identity channel. The pinned-channel PSM then routed Aspire.* to a feed
        // that has no stable, and restore failed with "Unable to find a stable package".
        //
        // After the fix, the channel persisted into `aspire.config.json` MUST agree with
        // the SDK version: if a non-stable channel is pinned, the SDK version must be a
        // prerelease (since channel-specific feeds only host prereleases). If channel is
        // unset or `stable`, the SDK version must be a stable release. Restore succeeding
        // above proves the two are mutually satisfiable, but explicitly asserting the
        // invariant here surfaces the mismatch directly if anything reseeds an
        // inconsistent pair in the future.
        AssertAspireConfigChannelConsistentWithSdkVersion(Path.Combine(projectRoot, "aspire.config.json"));

        // Step 2: Navigate into the project and start it.
        await auto.TypeAsync("cd TsStarterApp");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.RunCommandFailFastAsync("npm run build", counter, TimeSpan.FromMinutes(2));

        await auto.AspireStartAsync(counter);
        await auto.AspireStopAsync(counter);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    /// <summary>
    /// Reads <c>aspire.config.json</c> and asserts the SDK version is consistent with the
    /// channel pin. Non-stable channel ⇒ prerelease SDK; absent/stable channel ⇒ stable
    /// SDK. The invariant is the contract restore relies on: any consumer reading the
    /// per-project channel for PSM/feed selection must be able to find the requested SDK
    /// version through that channel.
    /// </summary>
    private static void AssertAspireConfigChannelConsistentWithSdkVersion(string configPath)
    {
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException($"Expected aspire.config.json to exist at {configPath}.", configPath);
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
        var root = doc.RootElement;

        if (!root.TryGetProperty("sdk", out var sdk) ||
            !sdk.TryGetProperty("version", out var versionProp) ||
            versionProp.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"aspire.config.json is missing 'sdk.version': {configPath}");
        }

        var sdkVersion = versionProp.GetString()!;
        var sdkIsPrerelease = sdkVersion.Contains('-', StringComparison.Ordinal);

        var channel = root.TryGetProperty("channel", out var channelProp) && channelProp.ValueKind == JsonValueKind.String
            ? channelProp.GetString()
            : null;

        // The staging channel's quality is configurable (Stable / Prerelease / Both) — its
        // SHA-pinned feed can legitimately host either stable or prerelease packages, so
        // we can't enforce the prerelease ↔ channel invariant from the channel name alone.
        // Restore feasibility is still validated by the surrounding aspire-run flow.
        if (string.Equals(channel, "staging", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Stable channel (or no channel pin) → SDK must be stable.
        // Any non-stable channel (daily / pr-* / local) → SDK must be prerelease.
        var channelExpectsPrerelease = !string.IsNullOrEmpty(channel) &&
            !string.Equals(channel, "stable", StringComparison.OrdinalIgnoreCase);

        if (channelExpectsPrerelease && !sdkIsPrerelease)
        {
            throw new InvalidOperationException(
                $"aspire.config.json pinned channel '{channel}' but SDK version '{sdkVersion}' is stable. " +
                "These cannot be mutually satisfied by restore (the channel feed only hosts prereleases). " +
                $"See {configPath}.");
        }

        if (!channelExpectsPrerelease && sdkIsPrerelease)
        {
            throw new InvalidOperationException(
                $"aspire.config.json has no non-stable channel pin (channel='{channel ?? "<unset>"}') but SDK version '{sdkVersion}' is a prerelease. " +
                "The Implicit/stable channel won't surface this version. " +
                $"See {configPath}.");
        }
    }
}
