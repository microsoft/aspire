// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end test for the channel-update workflow on the polyglot/TypeScript path:
/// <list type="number">
///   <item><description>Create a TypeScript empty AppHost on the CLI's baked channel.</description></item>
///   <item><description><c>aspire add</c> a package and verify the recorded version is non-stable.</description></item>
///   <item><description><c>aspire start</c>, assert a wired resource exists, then <c>aspire stop</c>.</description></item>
///   <item><description><c>aspire update --channel stable</c> and verify that
///     <c>aspire.config.json#channel</c> flips to <c>"stable"</c> and the previously
///     added package version is now stable.</description></item>
///   <item><description><c>aspire add</c> a second package and verify it resolves to a stable version
///     (because <c>aspire.config.json#channel</c> is now <c>"stable"</c> and the polyglot
///     <c>aspire add</c> honors the project's configured channel).</description></item>
///   <item><description><c>aspire start</c> / <c>aspire stop</c> again to confirm the project still runs after the channel switch.</description></item>
/// </list>
/// The C# AppHost variant is intentionally not covered here: for C# projects, <c>aspire update</c>
/// does not rewrite <c>aspire.config.json#channel</c>, and <c>aspire add</c> on PR/CI hives prefers
/// the package version that matches the running CLI build (the
/// <c>VersionHelper.TryGetCurrentCliVersionMatch</c> branch in <c>AddCommand</c>), which deliberately
/// overrides the channel choice for build coherence.
/// </summary>
public sealed class ChannelUpdateWorkflowTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task UpdateProjectChannelToStable_TypeScript_PicksUpStablePackages()
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

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

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
                "This test requires a non-stable initial channel so 'aspire update --channel stable' has something to change.");
        }

        output.WriteLine($"Initial channel: {initialChannel}");
        var appHostRelativePath = ReadAspireConfigAppHostPath(aspireConfigPath);
        var appHostPath = Path.GetFullPath(Path.Combine(projectPath, appHostRelativePath));
        var generatedModuleImport = appHostRelativePath.EndsWith(".mts", StringComparison.OrdinalIgnoreCase)
            ? "./.aspire/modules/aspire.mjs"
            : "./.aspire/modules/aspire.js";

        await auto.RunCommandFailFastAsync($"cd {projectName}", counter);

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
            // Step 8: Switch the project to the stable channel. The polyglot update path
            // (GuestAppHostProject.UpdatePackagesInternalAsync) only prompts "Perform updates?" — there
            // is no NuGet.config because polyglot apphosts persist channel + packages in aspire.config.json.
            await auto.TypeAsync("aspire update --channel stable");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("Perform updates?", timeout: TimeSpan.FromMinutes(2));
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("Update successful!", timeout: TimeSpan.FromMinutes(3));
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 9: Verify the channel was rewritten and Redis is now on a stable version.
            var channelAfter = ReadAspireConfigChannel(aspireConfigPath);
            Assert.Equal("stable", channelAfter);

            var redisVersionAfter = ReadAspireConfigPackageVersion(aspireConfigPath, "Aspire.Hosting.Redis");
            output.WriteLine($"Redis version after update: {redisVersionAfter}");
            Assert.True(
                IsStableVersion(redisVersionAfter),
                $"Expected a stable Aspire.Hosting.Redis version after 'aspire update --channel stable', got '{redisVersionAfter}'.");

            // Step 10: Add a second package now that the project is on stable. For polyglot the
            // configured channel ("stable") is honored by aspire add, so the resolved version must be
            // stable too. Note the canonical capitalization: Aspire.Hosting.PostgreSQL.
            await auto.TypeAsync("aspire add Aspire.Hosting.PostgreSQL");
            await auto.EnterAsync();
            await auto.WaitForAspireAddSuccessAsync(counter, TimeSpan.FromMinutes(2));

            var postgresVersion = ReadAspireConfigPackageVersion(aspireConfigPath, "Aspire.Hosting.PostgreSQL");
            output.WriteLine($"PostgreSQL version (post-update add): {postgresVersion}");
            Assert.True(
                IsStableVersion(postgresVersion),
                $"Expected the post-update 'aspire add' to resolve a stable Aspire.Hosting.PostgreSQL version, got '{postgresVersion}'.");

            // Step 11: Boot once more to confirm the project still runs after the channel switch.
            // The AppHost still wires only the cache resource — PostgreSQL was added to config but not
            // wired in the AppHost, which is sufficient for the user-facing "package can be added from
            // stable channel" assertion.
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

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
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
