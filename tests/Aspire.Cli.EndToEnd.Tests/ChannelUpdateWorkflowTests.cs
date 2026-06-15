// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for the channel-update workflow.
///
/// The first test exercises the deep TypeScript path:
/// <list type="number">
///   <item><description>Create a TypeScript empty AppHost on the CLI's baked channel.</description></item>
///   <item><description><c>aspire add</c> a package and verify the recorded version is non-stable.</description></item>
///   <item><description><c>aspire start</c>, assert a wired resource exists, then <c>aspire stop</c>.</description></item>
///   <item><description><c>aspire update --channel stable</c> and verify that it previews
///     stable package updates without enqueuing an <c>aspire.config.json#channel</c> rewrite.</description></item>
///   <item><description>Decline the previewed updates, then verify the existing non-stable
///     channel and package versions are preserved.</description></item>
///   <item><description><c>aspire add</c> a second package and verify it still resolves to a non-stable version
///     because the project's configured channel was intentionally left alone.</description></item>
///   <item><description><c>aspire start</c> / <c>aspire stop</c> again to confirm the project still runs after the declined update.</description></item>
/// </list>
///
/// The remaining tests are lean regression guards for
/// <see href="https://github.com/microsoft/aspire/issues/17295"/>: each scaffolds an AppHost via one of
/// the four supported create paths (<c>aspire init</c> C#, <c>aspire new aspire-empty</c> C#,
/// <c>aspire init --language typescript</c>, plus the TypeScript <c>aspire new</c> case already
/// covered by the deep test above) and asserts that <c>aspire update --channel stable</c> does not
/// rewrite <c>aspire.config.json#channel</c>. Package-version assertions are intentionally scoped to
/// the polyglot deep test only: for C# projects, <c>aspire add</c> on PR/CI hives prefers the package
/// version that matches the running CLI build (the <c>VersionHelper.TryGetCurrentCliVersionMatch</c>
/// branch in <c>AddCommand</c>), which deliberately overrides the channel choice for build coherence
/// and would make a stable-version assertion flaky on C#.
/// </summary>
public sealed class ChannelUpdateWorkflowTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task UpdateToStable_TypeScript_PreviewsStablePkgsAndKeepsChannel()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);

        // The workflow exercises switching FROM a non-stable channel TO stable. If the running CLI
        // was acquired as latest-GA (the default local-developer fallback in CliInstallStrategy.Detect),
        // the initial channel is already stable and `aspire update --channel stable` is a no-op, so
        // the prompts the test drives would never appear. Skip with a clear message instead of hanging.
        if (strategy.Mode == CliInstallMode.InstallScript && strategy.Quality is null && strategy.Version is null)
        {
            Assert.Skip(
                "This test exercises 'aspire update --channel stable' from a non-stable channel. " +
                "Run with ASPIRE_E2E_ARCHIVE (LocalHive), in CI (dev/staging quality), or against a " +
                "specific non-stable build so the initial channel is non-stable.");
        }

        var workspace = TemporaryWorkspace.Create(output);

        // Pre-stage the locally-built nupkgs (LocalHive runs) so the in-container `aspire add` /
        // `aspire update` resolve the integration packages from the local channel. For non-LocalHive
        // strategies this is a no-op and the CLI uses its baked channel + NuGet feeds.
        var localChannel = CliE2ETestHelpers.PrepareLocalChannel(repoRoot, strategy,
            ["Aspire.Hosting.CodeGeneration.TypeScript.", "Aspire.Hosting.Redis.", "Aspire.Hosting.PostgreSQL."]);

        // Polyglot variant + docker socket so the `cache` Redis container can come up during `aspire start`
        // and `aspire describe cache` has something to inspect.
        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot, strategy, output,
            variant: CliE2ETestHelpers.DockerfileVariant.Polyglot,
            mountDockerSocket: true,
            workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        const string projectName = "ChannelUpdateTsApp";
        var projectPath = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
        var aspireConfigPath = Path.Combine(projectPath, "aspire.config.json");

        // Step 1: Create the empty TypeScript AppHost. No --channel is passed so the CLI uses its
        // baked channel (local / pr-N / dev / staging depending on the install strategy).
        await auto.AspireNewTypeScriptEmptyAppHostAsync(projectName, counter);

        // LocalHive strategy only: PrepareLocalChannel returned a real channel,
        // so write the per-project aspire.config.json to point at the in-repo
        // nupkg hive. Other strategies (script-installed CLI, pre-existing CLI)
        // return null and rely on the CLI's baked channel + ambient NuGet feeds.
        if (localChannel is not null)
        {
            CliE2ETestHelpers.WriteLocalChannelSettings(projectPath, localChannel.SdkVersion);
        }

        // Step 2: Capture the initial channel. Defense in depth — if the strategy gate above didn't
        // catch the "already stable" case (e.g. a hand-rolled build with channel baked to "stable"),
        // skip here so the test doesn't try to assert prerelease versions that won't exist.
        var initialChannel = ReadAspireConfigChannel(aspireConfigPath);
        if (string.IsNullOrEmpty(initialChannel) ||
            string.Equals(initialChannel, "stable", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Skip(
                $"Initial aspire.config.json#channel was '{initialChannel ?? "<null>"}'. " +
                "This test requires a non-stable initial channel so 'aspire update --channel stable' can preview stable-channel updates.");
        }

        output.WriteLine($"Initial channel: {initialChannel}");
        var appHostRelativePath = ReadAspireConfigAppHostPath(aspireConfigPath);
        var appHostPath = Path.GetFullPath(Path.Combine(projectPath, appHostRelativePath));
        var generatedModuleImport = appHostRelativePath.EndsWith(".mts", StringComparison.OrdinalIgnoreCase)
            ? "./.aspire/modules/aspire.mjs"
            : "./.aspire/modules/aspire.js";

        await auto.RunCommandAsync($"cd {projectName}", counter);

        // Step 3: Add the first package on the non-stable channel. Don't pass --non-interactive — the
        // helper handles both direct success and the "based on NuGet.config" version picker that
        // can appear when more than one version is reachable.
        await auto.TypeAsync("aspire add Aspire.Hosting.Redis");
        await auto.EnterAsync();
        await auto.WaitForAspireAddSuccessAsync(counter, TimeSpan.FromMinutes(2));

        // Step 4: Verify the recorded version is non-stable. For polyglot, package versions live in
        // aspire.config.json#packages, keyed by the exact package ID (not the friendly name).
        var redisVersionBefore = ReadAspireConfigPackageVersion(aspireConfigPath, "Aspire.Hosting.Redis");
        output.WriteLine($"Redis version before update: {redisVersionBefore}");
        Assert.False(
            IsStableVersion(redisVersionBefore),
            $"Expected a non-stable Aspire.Hosting.Redis version before 'aspire update --channel stable', got '{redisVersionBefore}'.");

        // Step 5: Wire the cache resource into the scaffolded AppHost so the project has an actual resource to
        // start and describe. Without this, the empty AppHost has no resources and `aspire describe`
        // would have nothing to assert against.
        await File.WriteAllTextAsync(appHostPath,
            $$"""
            import { createBuilder } from '{{generatedModuleImport}}';

            const builder = await createBuilder();
            await builder.addRedis("cache");
            await builder.build().run();
            """);

        // Step 6: Sanity-check the project boots and the wired resource is visible.
        await auto.AspireStartAsync(counter);
        await auto.AssertResourcesExistAsync(counter, "cache");
        await auto.AspireStopAsync(counter);

        // Step 7: Suppress the post-update CLI self-update prompt (UpdateCommand.cs:243-265) so it
        // doesn't block waiting on "Update successful!". Pattern borrowed from CentralPackageManagementTests.
        await auto.TypeAsync("aspire config set features.updateNotificationsEnabled false -g");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        try
        {
            // Step 8: Preview a stable-channel update. The stable channel is intentionally not
            // persisted, so decline the package updates here: applying stable package versions while
            // preserving the existing PR/local channel would leave the TypeScript SDK restore with
            // package versions that are not available from the preserved channel's source mapping.
            await PreviewStableUpdateAndDeclineAsync(auto, counter, expectedPackageInPlan: "Aspire.Hosting.Redis");

            // Step 9: Verify the existing channel and Redis package version were preserved.
            var channelAfter = ReadAspireConfigChannel(aspireConfigPath);
            Assert.Equal(initialChannel, channelAfter);

            var redisVersionAfter = ReadAspireConfigPackageVersion(aspireConfigPath, "Aspire.Hosting.Redis");
            output.WriteLine($"Redis version after declined update: {redisVersionAfter}");
            Assert.Equal(redisVersionBefore, redisVersionAfter);

            // Step 10: Add a second package after the declined update. Because the project channel is
            // preserved, the resolved version should still come from the non-stable channel. Note the
            // canonical capitalization: Aspire.Hosting.PostgreSQL.
            await auto.TypeAsync("aspire add Aspire.Hosting.PostgreSQL");
            await auto.EnterAsync();
            await auto.WaitForAspireAddSuccessAsync(counter, TimeSpan.FromMinutes(2));

            var postgresVersion = ReadAspireConfigPackageVersion(aspireConfigPath, "Aspire.Hosting.PostgreSQL");
            output.WriteLine($"PostgreSQL version (post-update add): {postgresVersion}");
            Assert.False(
                IsStableVersion(postgresVersion),
                $"Expected the post-update 'aspire add' to preserve the non-stable Aspire.Hosting.PostgreSQL channel, got '{postgresVersion}'.");

            // Step 11: Boot once more to confirm the project still runs after the declined update.
            // The AppHost still wires only the cache resource — PostgreSQL was added to config but not
            // wired in the AppHost, which is sufficient for the channel-preservation assertion.
            await auto.AspireStartAsync(counter);
            await auto.AssertResourcesExistAsync(counter, "cache");
            await auto.AspireStopAsync(counter);
        }
        finally
        {
            // Best-effort cleanup of the global feature flag we toggled above so other tests in the
            // same shared home aren't affected. Bound the wait so cleanup never burns the automator's
            // default timeout if the terminal is in an unexpected state, and accept either an OK or
            // an ERR prompt — we only care that the shell returned to a prompt.
            try
            {
                await auto.TypeAsync("aspire config delete features.updateNotificationsEnabled -g");
                await auto.EnterAsync();
                await auto.WaitForAnyPromptAsync(counter, TimeSpan.FromSeconds(30));
            }
            catch
            {
            }
        }
    }

    // ----------------------------------------------------------------------------------
    // Stable-channel persistence regression guards for https://github.com/microsoft/aspire/issues/17295
    //
    // These four cells cover the create-paths × language matrix:
    //   - C# `aspire init`             (single-file apphost.cs)        -> see test below
    //   - C# `aspire new aspire-empty` (project-mode .csproj)          -> see test below
    //   - TS `aspire init --language typescript --non-interactive`     -> see test below
    //   - TS `aspire new aspire-ts-empty` (project-mode)               -> already covered by the
    //                                                                     deep test above.
    //
    // Each test asserts the stable-channel invariant: `aspire update --channel stable` should not
    // enqueue an aspire.config.json#channel rewrite, so the existing channel value is preserved.
    // Package version assertions are intentionally not duplicated here — see the class docstring.
    // ----------------------------------------------------------------------------------

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task UpdateToStable_CSharpSingleFileInit_KeepsConfigChannel()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        if (ShouldSkipForStableInitialChannel(strategy, out var skipReason))
        {
            Assert.Skip(skipReason);
        }

        var workspace = TemporaryWorkspace.Create(output);
        var localChannel = CliE2ETestHelpers.PrepareLocalChannel(repoRoot, strategy,
            ["Aspire.Hosting.AppHost."]);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot, strategy, output,
            variant: CliE2ETestHelpers.DockerfileVariant.DotNet,
            workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        // `aspire init` scaffolds into the current working directory — give it a fresh subfolder so
        // the workspace root stays clean and the assertions target a well-known file path.
        const string projectName = "ChannelUpdateCsharpInitApp";
        var projectPath = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
        Directory.CreateDirectory(projectPath);
        await auto.RunCommandAsync($"cd {projectName}", counter);

        await auto.AspireInitAsync(counter);

        if (localChannel is not null)
        {
            CliE2ETestHelpers.WriteLocalChannelSettings(projectPath, localChannel.SdkVersion);
        }

        await RunStableChannelUpdateAndAssertChannelPreservedAsync(auto, counter, Path.Combine(projectPath, "aspire.config.json"));
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task UpdateToStable_CSharpEmptyAppHost_KeepsConfigChannel()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        if (ShouldSkipForStableInitialChannel(strategy, out var skipReason))
        {
            Assert.Skip(skipReason);
        }

        var workspace = TemporaryWorkspace.Create(output);
        var localChannel = CliE2ETestHelpers.PrepareLocalChannel(repoRoot, strategy,
            ["Aspire.Hosting.AppHost."]);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot, strategy, output,
            variant: CliE2ETestHelpers.DockerfileVariant.DotNet,
            workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        const string projectName = "ChannelUpdateCsharpEmptyApp";
        var projectPath = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);

        await auto.AspireNewCSharpEmptyAppHostAsync(projectName, counter);

        if (localChannel is not null)
        {
            CliE2ETestHelpers.WriteLocalChannelSettings(projectPath, localChannel.SdkVersion);
        }

        await auto.RunCommandAsync($"cd {projectName}", counter);
        await RunStableChannelUpdateAndAssertChannelPreservedAsync(auto, counter, Path.Combine(projectPath, "aspire.config.json"));
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task UpdateToStable_TypeScriptSingleFileInit_KeepsConfigChannel()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        if (ShouldSkipForStableInitialChannel(strategy, out var skipReason))
        {
            Assert.Skip(skipReason);
        }

        var workspace = TemporaryWorkspace.Create(output);
        var localChannel = CliE2ETestHelpers.PrepareLocalChannel(repoRoot, strategy,
            ["Aspire.Hosting.CodeGeneration.TypeScript."]);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot, strategy, output,
            variant: CliE2ETestHelpers.DockerfileVariant.Polyglot,
            workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        // `aspire init` for TS scaffolds into the current directory — match the existing
        // TypeScriptCodegenValidationTests pattern so a fresh subfolder is the working dir.
        const string projectName = "ChannelUpdateTsInitApp";
        var projectPath = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
        Directory.CreateDirectory(projectPath);
        await auto.RunCommandAsync($"cd {projectName}", counter);

        await auto.TypeAsync("aspire init --language typescript --non-interactive");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Created apphost.mts", timeout: TimeSpan.FromMinutes(2));
        await auto.WaitForSuccessPromptAsync(counter);

        if (localChannel is not null)
        {
            CliE2ETestHelpers.WriteLocalChannelSettings(projectPath, localChannel.SdkVersion);
        }

        await RunStableChannelUpdateAndAssertChannelPreservedAsync(auto, counter, Path.Combine(projectPath, "aspire.config.json"));
    }

    /// <summary>
    /// Shared skip-gate for the channel-preservation regression tests: if the running CLI's baked channel
    /// is already <c>stable</c> (e.g. a local install-script run with no quality/version override),
    /// <c>aspire update --channel stable</c> would be a no-op and the prompts the test drives would
    /// never appear. Returns <c>true</c> together with a human-readable reason in that case.
    /// </summary>
    private static bool ShouldSkipForStableInitialChannel(CliInstallStrategy strategy, out string reason)
    {
        if (strategy.Mode == CliInstallMode.InstallScript && strategy.Quality is null && strategy.Version is null)
        {
            reason =
                "This test exercises 'aspire update --channel stable' from a non-stable channel. " +
                "Run with ASPIRE_E2E_ARCHIVE (LocalHive), in CI (dev/staging quality), or against a " +
                "specific non-stable build so the initial channel is non-stable.";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    /// <summary>
    /// Shared body for the channel-preservation regression tests: snapshot the initial channel,
    /// suppress the post-update CLI self-update prompt, preview <c>aspire update --channel stable</c>,
    /// then assert that <c>aspire.config.json#channel</c> was left unchanged.
    /// Assumes the automator is positioned in the project directory (containing aspire.config.json).
    /// </summary>
    private static async Task RunStableChannelUpdateAndAssertChannelPreservedAsync(
        Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        string aspireConfigPath)
    {
        var initialChannel = ReadAspireConfigChannel(aspireConfigPath);
        if (string.IsNullOrEmpty(initialChannel) ||
            string.Equals(initialChannel, "stable", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Skip(
                $"Initial aspire.config.json#channel was '{initialChannel ?? "<null>"}'. " +
                "This test requires a non-stable initial channel so 'aspire update --channel stable' can preview stable-channel updates.");
        }

        // Suppress the post-update CLI self-update prompt so it doesn't block waiting on
        // "Update successful!". No cleanup needed — each test runs in its own Docker container.
        await auto.TypeAsync("aspire config set features.updateNotificationsEnabled false -g");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await PreviewStableUpdateAndDeclineAsync(auto, counter);

        var channelAfter = ReadAspireConfigChannel(aspireConfigPath);
        Assert.Equal(initialChannel, channelAfter);
    }

    private static async Task PreviewStableUpdateAndDeclineAsync(
        Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        string? expectedPackageInPlan = null)
    {
        var updatePrompt = new CellPatternSearcher().Find("Perform updates?");
        var upToDateMessage = new CellPatternSearcher().Find("Project is up to date! (no updates necessary)");
        var channelUpdateLine = new CellPatternSearcher().Find("aspire.config.json#channel");
        var cliUpdatePrompt = new CellPatternSearcher().Find("Update the Aspire CLI now and re-run");
        var expectedPackageLine = expectedPackageInPlan is not null
            ? new CellPatternSearcher().Find(expectedPackageInPlan)
            : null;

        var sawChannelUpdateLine = false;
        var sawExpectedPackageLine = expectedPackageLine is null;
        var sawUpdatePrompt = false;
        var sawUpToDateMessage = false;
        var sawCliUpdatePrompt = false;

        await auto.TypeAsync("aspire update --channel stable --nuget-config-dir .");
        await auto.EnterAsync();

        async Task WaitForStableUpdatePreviewAsync(bool allowCliUpdatePrompt)
        {
            await auto.WaitUntilAsync(snapshot =>
            {
                sawChannelUpdateLine |= channelUpdateLine.Search(snapshot).Count > 0;
                if (expectedPackageLine is not null && expectedPackageLine.Search(snapshot).Count > 0)
                {
                    sawExpectedPackageLine = true;
                }
                sawUpdatePrompt |= updatePrompt.Search(snapshot).Count > 0;
                sawUpToDateMessage |= upToDateMessage.Search(snapshot).Count > 0;
                var foundCliUpdatePrompt = cliUpdatePrompt.Search(snapshot).Count > 0;
                sawCliUpdatePrompt |= foundCliUpdatePrompt;

                return sawUpdatePrompt || sawUpToDateMessage || (allowCliUpdatePrompt && foundCliUpdatePrompt);
            }, TimeSpan.FromMinutes(3), description: "waiting for stable update preview");
        }

        await WaitForStableUpdatePreviewAsync(allowCliUpdatePrompt: true);

        if (sawCliUpdatePrompt && !sawUpdatePrompt && !sawUpToDateMessage)
        {
            // Stable release versions sort higher than same-base PR prerelease versions
            // (for example, 13.4.3 > 13.4.3-pr.18093.g...). Decline the CLI self-update
            // prompt so the project update preview can continue. The prompt remains in the
            // terminal snapshot after the key is accepted, so the second wait must ignore it.
            await auto.TypeAsync("n");
            await WaitForStableUpdatePreviewAsync(allowCliUpdatePrompt: false);
        }

        Assert.False(sawChannelUpdateLine, "Stable channel updates should not enqueue an aspire.config.json#channel rewrite.");
        Assert.True(sawExpectedPackageLine, $"Expected the stable update preview to include '{expectedPackageInPlan}'.");

        if (sawUpdatePrompt)
        {
            // Type "n" to decline. Do NOT send Enter — the Spectre.Console [Y/n] confirmation
            // prompt accepts a single character. Sending Enter risks a race: if aspire update
            // returns from its line-reader on the "n" keystroke and tears down before the Enter
            // is dequeued, bash receives the Enter and executes a phantom blank command,
            // advancing CMDCOUNT and desyncing the test counter from the shell counter.
            // See .agents/skills/cli-e2e-testing/troubleshooting.md for the full failure pattern.
            await auto.TypeAsync("n");
        }

        await auto.WaitForSuccessPromptAsync(counter);
    }

    /// <summary>
    /// A NuGet semver is "stable" iff it carries no prerelease label (no <c>-</c>) and no build
    /// metadata (no <c>+</c>). Same heuristic used by <c>SmokeTests.LatestCliCanStartStableChannelAppHost</c>.
    /// </summary>
    private static bool IsStableVersion(string version) =>
        !version.Contains('-', StringComparison.Ordinal) &&
        !version.Contains('+', StringComparison.Ordinal);

    private static string? ReadAspireConfigChannel(string aspireConfigPath)
    {
        if (!File.Exists(aspireConfigPath))
        {
            throw new FileNotFoundException($"Expected aspire.config.json at: {aspireConfigPath}", aspireConfigPath);
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(aspireConfigPath));
        return doc.RootElement.TryGetProperty("channel", out var channelElement) &&
               channelElement.ValueKind == JsonValueKind.String
            ? channelElement.GetString()
            : null;
    }

    private static string ReadAspireConfigAppHostPath(string aspireConfigPath)
    {
        if (!File.Exists(aspireConfigPath))
        {
            throw new FileNotFoundException($"Expected aspire.config.json at: {aspireConfigPath}", aspireConfigPath);
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(aspireConfigPath));
        if (!doc.RootElement.TryGetProperty("appHost", out var appHost) ||
            appHost.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"aspire.config.json has no 'appHost' object. Path: {aspireConfigPath}");
        }

        if (!appHost.TryGetProperty("path", out var pathElement) ||
            pathElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"aspire.config.json#appHost does not contain a string 'path' entry. Path: {aspireConfigPath}");
        }

        return pathElement.GetString()
            ?? throw new InvalidOperationException($"aspire.config.json#appHost.path was null. Path: {aspireConfigPath}");
    }

    private static string ReadAspireConfigPackageVersion(string aspireConfigPath, string packageId)
    {
        if (!File.Exists(aspireConfigPath))
        {
            throw new FileNotFoundException($"Expected aspire.config.json at: {aspireConfigPath}", aspireConfigPath);
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(aspireConfigPath));
        if (!doc.RootElement.TryGetProperty("packages", out var packages) ||
            packages.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"aspire.config.json has no 'packages' object — was '{packageId}' actually added? Path: {aspireConfigPath}");
        }

        if (!packages.TryGetProperty(packageId, out var versionElement) ||
            versionElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(
                $"aspire.config.json#packages does not contain a string entry for '{packageId}'. Path: {aspireConfigPath}");
        }

        return versionElement.GetString()
            ?? throw new InvalidOperationException(
                $"aspire.config.json#packages.{packageId} was null. Path: {aspireConfigPath}");
    }
}
