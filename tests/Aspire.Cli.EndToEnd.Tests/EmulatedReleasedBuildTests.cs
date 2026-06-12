// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests that install the real CLI build and then use the <c>ASPIRE_CLI_*</c> identity
/// override environment variables to make the locally built CLI <em>emulate the latest released
/// (stable) build</em>. They validate that the identity-override mechanism faithfully flips the
/// version- and channel-sensitive behavior the CLI uses for <c>aspire new</c> and <c>aspire add</c>,
/// so a shipped release (or staging build) can be reproduced locally without rebuilding or
/// reinstalling — the core promise of the CLI identity sidecar.
///
/// This class is the <b>stable</b> row of the AppHost-language × channel-emulation matrix, with one
/// test per AppHost language (C# and TypeScript). The <b>staging</b> row lives in
/// <see cref="EmulatedStagingBuildTests"/>. We deliberately keep a separate test per language rather
/// than collapsing them: C# and TypeScript AppHosts scaffold through different code paths and have
/// diverged in behavior before, so each cell of the matrix must be exercised independently.
///
/// Each test class runs as a separate CI job for parallelization.
/// </summary>
public sealed class EmulatedReleasedBuildTests(ITestOutputHelper output)
{
    /// <summary>
    /// SCENARIO: a shipped <b>stable / GA release</b> build of the CLI, scaffolding the C#
    /// <c>aspire-starter</c> template.
    ///
    /// A stable build resolves all <c>Aspire.*</c> packages from nuget.org (the ambient default
    /// source), so it must <b>not</b> drop a per-project <c>NuGet.config</c> feed pin — doing so would
    /// be redundant and would wipe a consumer's ambient feeds. (This guards the 13.4 regression where a
    /// stable build accidentally started dropping a nuget.org-only config; see PR #17120.) This test
    /// emulates the latest released identity, scaffolds the C# starter, and asserts that no
    /// <c>NuGet.config</c> is dropped and the AppHost SDK is pinned to the emulated stable version.
    /// Contrast with <see cref="EmulatedStagingBuildTests"/>, where a staging build <b>does</b> drop a
    /// darc-feed config.
    /// </summary>
    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task EmulatedStableIdentityScaffoldsCSharpStarterWithoutNuGetConfig()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);

        var stableVersion = await CliE2ETestHelpers.TryGetLatestStableAspireVersionAsync(output.WriteLine, TestContext.Current.CancellationToken);
        Assert.SkipWhen(stableVersion is null, "Could not determine the latest stable Aspire version from nuget.org (network unavailable?).");
        output.WriteLine($"Emulating latest stable Aspire identity: {stableVersion}");

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        await ApplyEmulatedStableIdentityAsync(auto, counter, stableVersion!);

        const string projectName = "EmulatedStarter";
        await auto.AspireNewAsync(projectName, counter, template: AspireTemplate.Starter, useRedisCache: false);

        var projectDir = new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, projectName));

        // A stable build's packages all live on nuget.org (the ambient default source), so the CLI
        // must not pin a custom feed via NuGet.config. A non-stable channel (daily/staging/pr) would.
        AssertNoNuGetConfig(projectDir);

        // The exact-version match against the emulated identity pins the AppHost SDK to that version.
        var appHostCsproj = Path.Combine(projectDir.FullName, $"{projectName}.AppHost", $"{projectName}.AppHost.csproj");
        var sdkVersion = GetAppHostSdkVersionFromCsproj(appHostCsproj);
        output.WriteLine($"Generated AppHost SDK version: {sdkVersion}");
        Assert.Equal(stableVersion, sdkVersion);

        // `aspire add` must resolve a restorable version (no custom feed available) and complete.
        await auto.RunCommandAsync($"cd {projectName}/{projectName}.AppHost", counter);
        await AddIntegrationInteractivelyAsync(auto, counter, "redis");
    }

    /// <summary>
    /// SCENARIO: a shipped <b>stable / GA release</b> build of the CLI, scaffolding the TypeScript
    /// <c>aspire-ts-starter</c> (Express/React) template.
    ///
    /// This is the TypeScript half of the stable row of the language × channel matrix; it mirrors the
    /// C# stable test (<see cref="EmulatedStableIdentityScaffoldsCSharpStarterWithoutNuGetConfig"/>)
    /// because the C# and TypeScript AppHosts go through <em>different</em> scaffolding code paths and
    /// have historically diverged. Like the C# case it asserts that no <c>NuGet.config</c> feed pin is
    /// dropped (a stable build's packages live on nuget.org). It additionally asserts that the SDK
    /// version recorded in <c>aspire.config.json</c> (where the TS template stores it, rather than a
    /// csproj) is <b>stable-shaped</b> — proving the emulated stable <em>channel</em>, not the local
    /// build's own pre-release stamp, drove template resolution. The exact version is not asserted
    /// because the stable channel can resolve a later patch than the discovered template version.
    /// </summary>
    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task EmulatedStableIdentityScaffoldsTypeScriptStarterWithoutNuGetConfig()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);

        var stableVersion = await CliE2ETestHelpers.TryGetLatestStableAspireVersionAsync(output.WriteLine, TestContext.Current.CancellationToken);
        Assert.SkipWhen(stableVersion is null, "Could not determine the latest stable Aspire version from nuget.org (network unavailable?).");
        output.WriteLine($"Emulating latest stable Aspire identity: {stableVersion}");

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        await ApplyEmulatedStableIdentityAsync(auto, counter, stableVersion!);

        const string projectName = "EmulatedTsStarter";
        await auto.AspireNewAsync(projectName, counter, template: AspireTemplate.ExpressReact);

        var projectDir = new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, projectName));
        var configPath = Path.Combine(projectDir.FullName, "aspire.config.json");

        // Like the C# stable case, a stable build's packages all live on nuget.org, so the CLI must
        // not pin a custom feed via NuGet.config. Asserting this for the TS path too is the point of
        // the matrix: C# and TypeScript scaffold through different code and have diverged before.
        AssertNoNuGetConfig(projectDir);

        // The TypeScript starter records its SDK version in aspire.config.json. Under stable
        // emulation it must be stable-shaped — without the override it would inherit the build's
        // own pre-release version. The exact value is not asserted because the stable channel can
        // resolve a later patch than the discovered template version.
        var sdkVersion = GetSdkVersionFromAspireConfig(configPath);
        output.WriteLine($"Generated TypeScript SDK version: {sdkVersion}");
        AssertStableShaped(sdkVersion, configPath);

        await auto.RunCommandAsync($"cd {projectName}", counter);
        await AddIntegrationInteractivelyAsync(auto, counter, "redis");
    }

    /// <summary>
    /// Exports the identity-override environment variables that make the just-installed CLI report
    /// and behave as the latest released (stable) build, then proves the override is live before any
    /// scaffolding runs. The overrides take effect for every subsequent <c>aspire</c> invocation in
    /// the shell; the CLI strips them before spawning child processes, so only the CLI's own identity
    /// decisions change. This is the exact mechanism the test validates.
    /// </summary>
    private static async Task ApplyEmulatedStableIdentityAsync(Hex1bTerminalAutomator auto, SequenceCounter counter, string stableVersion)
    {
        // Environment-variable names are the public ASPIRE_CLI_* identity contract read by
        // IdentityResolver. They are deliberately written as literals here to document the contract
        // the test depends on. The discovered version is a clean numeric version string, so it needs
        // no shell quoting.
        await auto.RunCommandAsync("export ASPIRE_CLI_CHANNEL=stable", counter);
        await auto.RunCommandAsync($"export ASPIRE_CLI_VERSION={stableVersion}", counter);

        // `aspire --version` reports the resolved identity version (honoring ASPIRE_CLI_VERSION), and
        // the emulation notice is written to stderr for every non-machine-readable invocation while an
        // override is active. Seeing both confirms the override path — not the physical build — is in
        // effect before we depend on it for `aspire new`/`aspire add`.
        await auto.TypeAsync("aspire --version");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(
            s => s.ContainsText(stableVersion) && s.ContainsText("emulating identity"),
            timeout: TimeSpan.FromSeconds(60),
            description: $"aspire --version reporting emulated stable identity '{stableVersion}' with override notice");
        await auto.WaitForSuccessPromptAsync(counter);
    }

    /// <summary>
    /// Runs <c>aspire add</c> interactively, filters the integration list by <paramref name="filter"/>,
    /// accepts the default version if a version picker appears, and waits for the success message.
    /// Mirrors the proven interactive add flow used elsewhere in the E2E suite.
    /// </summary>
    private static async Task AddIntegrationInteractivelyAsync(Hex1bTerminalAutomator auto, SequenceCounter counter, string filter)
    {
        await auto.TypeAsync("aspire add");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync(AddCommandStrings.SelectAnIntegrationToAdd, timeout: TimeSpan.FromMinutes(1));
        await auto.TypeAsync(filter);
        await auto.EnterAsync();

        var waitingForVersionSelection = false;
        await auto.WaitUntilAsync(snapshot =>
        {
            waitingForVersionSelection = snapshot.ContainsText("Select a version of");
            return waitingForVersionSelection || snapshot.ContainsText("was added successfully.");
        }, timeout: TimeSpan.FromMinutes(2), description: "version prompt or add success");

        if (waitingForVersionSelection)
        {
            await auto.EnterAsync();
        }

        await auto.WaitUntilTextAsync("was added successfully.", timeout: TimeSpan.FromMinutes(2));
        await auto.WaitForSuccessPromptAsync(counter);
    }

    private static void AssertNoNuGetConfig(DirectoryInfo projectDir)
    {
        // Match by file name with a case-insensitive comparison rather than relying on the glob
        // matcher, whose case sensitivity differs across host operating systems (the workspace is
        // read from the host: case-insensitive on macOS/Windows, case-sensitive on Linux CI).
        var nuGetConfigs = projectDir
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(f => string.Equals(f.Name, "NuGet.config", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.FullName)
            .ToList();

        Assert.True(
            nuGetConfigs.Count == 0,
            $"Emulating a stable build, 'aspire new' must not drop a NuGet.config (stable packages live on nuget.org). Found: {string.Join(", ", nuGetConfigs)}");
    }

    private static string GetAppHostSdkVersionFromCsproj(string csprojPath)
    {
        if (!File.Exists(csprojPath))
        {
            throw new FileNotFoundException($"Expected AppHost project to exist: {csprojPath}", csprojPath);
        }

        // The generated AppHost csproj opens with: <Project Sdk="Aspire.AppHost.Sdk/13.4.3">
        var content = File.ReadAllText(csprojPath);
        var match = Regex.Match(content, "Sdk=\"Aspire\\.AppHost\\.Sdk/(?<version>[^\"]+)\"");
        return match.Success
            ? match.Groups["version"].Value
            : throw new InvalidOperationException($"Could not find an Aspire.AppHost.Sdk reference in {csprojPath}.");
    }

    private static string GetSdkVersionFromAspireConfig(string configPath)
    {
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException($"Expected aspire.config.json to exist: {configPath}", configPath);
        }

        using var document = JsonDocument.Parse(File.ReadAllText(configPath));
        if (document.RootElement.TryGetProperty("sdk", out var sdk) &&
            sdk.ValueKind == JsonValueKind.Object &&
            sdk.TryGetProperty("version", out var version) &&
            version.ValueKind == JsonValueKind.String &&
            version.GetString() is { Length: > 0 } sdkVersion)
        {
            return sdkVersion;
        }

        throw new InvalidOperationException($"Could not find sdk.version in {configPath}.");
    }

    private static void AssertStableShaped(string version, string sourcePath)
    {
        Assert.False(
            version.Contains('-', StringComparison.Ordinal) || version.Contains('+', StringComparison.Ordinal),
            $"Expected a stable (non-prerelease) Aspire SDK version in {sourcePath}, got '{version}'.");
    }
}
