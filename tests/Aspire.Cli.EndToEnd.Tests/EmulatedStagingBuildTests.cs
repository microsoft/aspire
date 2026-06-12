// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using System.Xml.Linq;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests that install the real CLI build and then use the <c>ASPIRE_CLI_*</c> identity
/// override environment variables to make the locally built CLI <em>emulate the latest staging
/// ("rc/daily") build</em>. They are the counterpart to <see cref="EmulatedReleasedBuildTests"/> and
/// prove the stable-vs-staging differentiation: whereas a stable build resolves <c>Aspire.*</c> from
/// nuget.org and drops no feed pin, a staging build resolves them from its SHA-specific darc feed.
///
/// This class is the <b>staging</b> row of the AppHost-language × channel-emulation matrix, with one
/// test per AppHost language. The two languages behave <em>differently</em> here, which is exactly why
/// each is exercised independently:
/// <list type="bullet">
/// <item><b>C#</b>: the AppHost is a real csproj restored by MSBuild, which needs the feed configured
/// locally, so <c>aspire new</c> <b>must</b> drop a <c>NuGet.config</c> mapping <c>Aspire*</c> to
/// <c>darc-pub-microsoft-aspire-&lt;sha8&gt;</c>.</item>
/// <item><b>TypeScript</b>: the AppHost is scaffolded by the CLI itself (which resolves
/// <c>Aspire.*</c> from the channel feed when it generates <c>.aspire/modules</c>), so <b>no</b>
/// <c>NuGet.config</c> is dropped — the same as the stable case. This asymmetry has bitten us before,
/// so the test pins it.</item>
/// </list>
/// Validating this locally — without producing a real official build — is the core promise of the CLI
/// identity sidecar.
///
/// Each test class runs as a separate CI job for parallelization.
/// </summary>
public sealed class EmulatedStagingBuildTests(ITestOutputHelper output)
{
    /// <summary>
    /// SCENARIO: a published <b>staging ("rc/daily")</b> build of the CLI, scaffolding the C#
    /// <c>aspire-starter</c> template.
    ///
    /// A staging build resolves <c>Aspire.*</c> packages from its commit-specific darc feed
    /// (<c>darc-pub-microsoft-aspire-&lt;sha8&gt;</c>) — those packages are not yet on nuget.org — so it
    /// <b>must</b> drop a per-project <c>NuGet.config</c> that maps the <c>Aspire*</c> pattern to that
    /// feed. This is the exact inverse of the stable scenario in
    /// <see cref="EmulatedReleasedBuildTests"/>, and the pair together pins the stable-vs-staging
    /// differentiation. This test discovers the latest real staging build (version + source commit),
    /// emulates it, scaffolds the C# starter, and asserts: (1) a <c>NuGet.config</c> is dropped mapping
    /// <c>Aspire*</c> to the commit-derived darc feed, (2) the AppHost SDK is pinned to the emulated
    /// staging version, and (3) <c>aspire add</c> actually restores from that darc feed (proving the
    /// pin is functional, not just well-formed).
    /// </summary>
    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task EmulatedStagingIdentityScaffoldsCSharpStarterWithDarcFeedNuGetConfig()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);

        var staging = await CliE2ETestHelpers.TryGetLatestStagingBuildAsync(output.WriteLine, TestContext.Current.CancellationToken);
        Assert.SkipWhen(staging is null, "Could not discover the latest staging Aspire build (network unavailable, GitHub rate-limited, or no recent darc feed). Pin via ASPIRE_E2E_STAGING_VERSION/ASPIRE_E2E_STAGING_COMMIT to force.");
        output.WriteLine($"Emulating latest staging Aspire identity: version={staging!.Version}, commit={staging.Commit} (feed darc-pub-microsoft-aspire-{staging.ShortCommit}).");

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        await ApplyEmulatedStagingIdentityAsync(auto, counter, staging);

        const string projectName = "EmulatedStagingStarter";
        await auto.AspireNewAsync(projectName, counter, template: AspireTemplate.Starter, useRedisCache: false);

        var projectDir = new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, projectName));

        // A staging build's Aspire packages live on its SHA-specific darc feed, not nuget.org, so the
        // CLI MUST pin that feed via a dropped NuGet.config (the opposite of the stable case). This is
        // the stable-vs-staging behavioral difference the identity sidecar must preserve.
        AssertNuGetConfigPinsStagingFeed(projectDir, staging.ShortCommit);

        // The exact-version match against the emulated identity pins the AppHost SDK to that version.
        var appHostCsproj = Path.Combine(projectDir.FullName, $"{projectName}.AppHost", $"{projectName}.AppHost.csproj");
        var sdkVersion = GetAppHostSdkVersionFromCsproj(appHostCsproj);
        output.WriteLine($"Generated AppHost SDK version: {sdkVersion}");
        Assert.Equal(staging.Version, sdkVersion);

        // `aspire add` must resolve and restore from the pinned darc feed and complete successfully,
        // proving the dropped feed pin is functional (not just well-formed).
        await auto.RunCommandAsync($"cd {projectName}/{projectName}.AppHost", counter);
        await AddIntegrationInteractivelyAsync(auto, counter, "redis");
    }

    /// <summary>
    /// SCENARIO: a published <b>staging ("rc/daily")</b> build of the CLI, scaffolding the TypeScript
    /// <c>aspire-ts-starter</c> (Express/React) template.
    ///
    /// This is the TypeScript half of the staging row and the cell that proves the C#/TypeScript
    /// divergence: unlike the C# staging case
    /// (<see cref="EmulatedStagingIdentityScaffoldsCSharpStarterWithDarcFeedNuGetConfig"/>), the TS
    /// AppHost is scaffolded by the CLI itself rather than restored by MSBuild, so it does <b>not</b>
    /// get a project-local <c>NuGet.config</c> even on a custom-feed channel. This test emulates the
    /// latest staging build, scaffolds the TS starter, and asserts: (1) <b>no</b> <c>NuGet.config</c>
    /// is dropped, (2) the SDK version recorded in <c>aspire.config.json</c> equals the emulated
    /// staging version (the darc feed carries exactly that version), and (3) <c>aspire add</c> still
    /// resolves the integration from the staging darc feed <em>without</em> a project-local config —
    /// proving the CLI's own channel-feed resolution, not a dropped file, drives TS package access.
    /// </summary>
    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task EmulatedStagingIdentityScaffoldsTypeScriptStarterWithoutNuGetConfig()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);

        var staging = await CliE2ETestHelpers.TryGetLatestStagingBuildAsync(output.WriteLine, TestContext.Current.CancellationToken);
        Assert.SkipWhen(staging is null, "Could not discover the latest staging Aspire build (network unavailable, GitHub rate-limited, or no recent darc feed). Pin via ASPIRE_E2E_STAGING_VERSION/ASPIRE_E2E_STAGING_COMMIT to force.");
        output.WriteLine($"Emulating latest staging Aspire identity: version={staging!.Version}, commit={staging.Commit} (feed darc-pub-microsoft-aspire-{staging.ShortCommit}).");

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        await ApplyEmulatedStagingIdentityAsync(auto, counter, staging);

        const string projectName = "EmulatedStagingTsStarter";
        await auto.AspireNewAsync(projectName, counter, template: AspireTemplate.ExpressReact);

        var projectDir = new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, projectName));
        var configPath = Path.Combine(projectDir.FullName, "aspire.config.json");

        // The C#/TS divergence: the TS template path never drops a NuGet.config (the CLI resolves
        // Aspire.* from the channel feed itself when generating .aspire/modules), so even on the
        // custom-feed staging channel there must be no project-local feed pin.
        AssertNoNuGetConfig(projectDir);

        // The staging darc feed carries exactly one version, so the SDK version recorded in
        // aspire.config.json must equal the emulated staging version exactly (unlike the stable case,
        // where a later patch could be resolved).
        var sdkVersion = GetSdkVersionFromAspireConfig(configPath);
        output.WriteLine($"Generated TypeScript SDK version: {sdkVersion}");
        Assert.Equal(staging.Version, sdkVersion);

        // `aspire add` must resolve the integration from the staging darc feed and complete, proving
        // the CLI's internal channel-feed resolution works for TS without a project-local NuGet.config.
        // Use `valkey` rather than `redis`: the interactive picker filters by substring and selects the
        // first match, and "redis" matches `azure-redis` (Aspire.Hosting.Azure.Redis) ahead of
        // `redis` — whereas "valkey" uniquely matches Aspire.Hosting.Valkey, so the assertion below
        // targets a deterministic package id.
        await auto.RunCommandAsync($"cd {projectName}", counter);
        await AddIntegrationInteractivelyAsync(auto, counter, "valkey");

        // REGRESSION GUARD: "added successfully" alone is insufficient here — the stable Valkey package
        // (13.4.3) also resolves fine from nuget.org, so a regression that ignored the pinned staging
        // channel would still report success while silently adding the wrong version. The polyglot path
        // has no project-local NuGet.config to constrain resolution; it relies entirely on the
        // configured channel (aspire.config.json `channel: staging`) being honored by `aspire add`. The
        // added version is recorded under `packages.<id>` in aspire.config.json, so assert it equals the
        // emulated staging version (the darc feed carries exactly that version) and is NOT the stable
        // nuget.org version. See the polyglot manifestation tracked by
        // https://github.com/microsoft/aspire/issues/18114.
        var addedValkeyVersion = GetPackageVersionFromAspireConfig(configPath, "Aspire.Hosting.Valkey");
        output.WriteLine($"aspire add valkey resolved version: {addedValkeyVersion}");
        Assert.Equal(staging.Version, addedValkeyVersion);
    }

    /// <summary>
    /// Exports the identity-override environment variables that make the just-installed CLI report and
    /// behave as the latest staging build, then proves the override is live before any scaffolding
    /// runs. Unlike the stable case, staging requires <c>ASPIRE_CLI_COMMIT</c> as well: the CLI derives
    /// its <c>darc-pub-microsoft-aspire-&lt;sha8&gt;</c> feed from the commit, so an emulated staging
    /// build with the wrong (or missing) commit would resolve the wrong feed.
    /// </summary>
    private static async Task ApplyEmulatedStagingIdentityAsync(Hex1bTerminalAutomator auto, SequenceCounter counter, StagingBuildIdentity staging)
    {
        // Environment-variable names are the public ASPIRE_CLI_* identity contract read by
        // IdentityResolver. They are written as literals here to document the contract the test depends
        // on. The discovered version/commit are clean tokens (numeric-dotted version, 40-hex commit),
        // so they need no shell quoting.
        await auto.RunCommandAsync("export ASPIRE_CLI_CHANNEL=staging", counter);
        await auto.RunCommandAsync($"export ASPIRE_CLI_VERSION={staging.Version}", counter);
        await auto.RunCommandAsync($"export ASPIRE_CLI_COMMIT={staging.Commit}", counter);

        // `aspire --version` reports the resolved identity version (honoring ASPIRE_CLI_VERSION), and
        // the emulation notice is written to stderr for every non-machine-readable invocation while an
        // override is active. Seeing both confirms the override path — not the physical build — is in
        // effect before we depend on it for `aspire new`/`aspire add`.
        await auto.TypeAsync("aspire --version");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(
            s => s.ContainsText(staging.Version) && s.ContainsText("emulating identity"),
            timeout: TimeSpan.FromSeconds(60),
            description: $"aspire --version reporting emulated staging identity '{staging.Version}' with override notice");
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

    /// <summary>
    /// Asserts that <c>aspire new</c> dropped exactly one <c>NuGet.config</c> and that it pins the
    /// staging build's SHA-specific darc feed: a <c>packageSource</c> whose URL contains
    /// <c>darc-pub-microsoft-aspire-&lt;sha8&gt;</c> and a <c>packageSourceMapping</c> routing the
    /// <c>Aspire*</c> pattern to that source.
    /// </summary>
    private static void AssertNuGetConfigPinsStagingFeed(DirectoryInfo projectDir, string shortCommit)
    {
        // Match by file name with a case-insensitive comparison rather than relying on the glob
        // matcher, whose case sensitivity differs across host operating systems (the workspace is read
        // from the host: case-insensitive on macOS/Windows, case-sensitive on Linux CI).
        var nuGetConfigs = projectDir
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(f => string.Equals(f.Name, "NuGet.config", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(
            nuGetConfigs.Count == 1,
            $"Emulating a staging build, 'aspire new' must drop exactly one NuGet.config pinning the darc feed. Found: {nuGetConfigs.Count} ({string.Join(", ", nuGetConfigs.Select(f => f.FullName))}).");

        var configPath = nuGetConfigs[0].FullName;
        var doc = XDocument.Load(configPath);
        var feedFragment = $"darc-pub-microsoft-aspire-{shortCommit}";

        // <packageSources><add key="<url>" value="<url>" /></packageSources>
        var stagingSourceKey = doc
            .Descendants("packageSources")
            .Elements("add")
            .Select(e => (string?)e.Attribute("value") ?? (string?)e.Attribute("key"))
            .FirstOrDefault(v => v is not null && v.Contains(feedFragment, StringComparison.OrdinalIgnoreCase));

        Assert.True(
            stagingSourceKey is not null,
            $"Dropped NuGet.config ({configPath}) does not contain a package source for the staging feed '{feedFragment}'. Content:\n{File.ReadAllText(configPath)}");

        // <packageSourceMapping><packageSource key="<feed>"><package pattern="Aspire*" /></packageSource>
        var aspireMappedToStagingFeed = doc
            .Descendants("packageSourceMapping")
            .Elements("packageSource")
            .Where(ps => ((string?)ps.Attribute("key"))?.Contains(feedFragment, StringComparison.OrdinalIgnoreCase) == true)
            .SelectMany(ps => ps.Elements("package"))
            .Any(p => string.Equals((string?)p.Attribute("pattern"), "Aspire*", StringComparison.OrdinalIgnoreCase));

        Assert.True(
            aspireMappedToStagingFeed,
            $"Dropped NuGet.config ({configPath}) does not map the 'Aspire*' pattern to the staging feed '{feedFragment}'. Content:\n{File.ReadAllText(configPath)}");
    }

    private static string GetAppHostSdkVersionFromCsproj(string csprojPath)
    {
        if (!File.Exists(csprojPath))
        {
            throw new FileNotFoundException($"Expected AppHost project to exist: {csprojPath}", csprojPath);
        }

        // The generated AppHost csproj opens with: <Project Sdk="Aspire.AppHost.Sdk/13.4.4">
        var content = File.ReadAllText(csprojPath);
        var match = Regex.Match(content, "Sdk=\"Aspire\\.AppHost\\.Sdk/(?<version>[^\"]+)\"");
        return match.Success
            ? match.Groups["version"].Value
            : throw new InvalidOperationException($"Could not find an Aspire.AppHost.Sdk reference in {csprojPath}.");
    }

    /// <summary>
    /// Reads the <c>sdk.version</c> recorded in a TypeScript starter's <c>aspire.config.json</c> (the
    /// TS AppHost stores its SDK version there rather than in a csproj).
    /// </summary>
    private static string GetSdkVersionFromAspireConfig(string configPath)
    {
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException($"Expected aspire.config.json to exist: {configPath}", configPath);
        }

        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(configPath));
        if (document.RootElement.TryGetProperty("sdk", out var sdk) &&
            sdk.ValueKind == System.Text.Json.JsonValueKind.Object &&
            sdk.TryGetProperty("version", out var version) &&
            version.ValueKind == System.Text.Json.JsonValueKind.String &&
            version.GetString() is { Length: > 0 } sdkVersion)
        {
            return sdkVersion;
        }

        throw new InvalidOperationException($"Could not find sdk.version in {configPath}.");
    }

    /// <summary>
    /// Reads the version recorded for <paramref name="packageId"/> under the <c>packages</c> map of a
    /// TypeScript starter's <c>aspire.config.json</c>. The polyglot AppHost persists each added
    /// integration's resolved version there (e.g. <c>"Aspire.Hosting.Redis": "13.4.4"</c>), which lets
    /// the staging test assert that <c>aspire add</c> honored the pinned channel's feed rather than
    /// silently resolving the stable nuget.org version.
    /// </summary>
    private static string GetPackageVersionFromAspireConfig(string configPath, string packageId)
    {
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException($"Expected aspire.config.json to exist: {configPath}", configPath);
        }

        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(configPath));
        if (document.RootElement.TryGetProperty("packages", out var packages) &&
            packages.ValueKind == System.Text.Json.JsonValueKind.Object &&
            packages.TryGetProperty(packageId, out var version) &&
            version.ValueKind == System.Text.Json.JsonValueKind.String &&
            version.GetString() is { Length: > 0 } packageVersion)
        {
            return packageVersion;
        }

        throw new InvalidOperationException($"Could not find packages.{packageId} in {configPath}.");
    }

    /// <summary>
    /// Asserts that <c>aspire new</c> dropped no <c>NuGet.config</c> anywhere under the project. Used by
    /// the TypeScript staging case, where — unlike C# — the CLI never writes a project-local feed pin.
    /// </summary>
    private static void AssertNoNuGetConfig(DirectoryInfo projectDir)
    {
        // Match by file name with a case-insensitive comparison rather than relying on the glob
        // matcher, whose case sensitivity differs across host operating systems (the workspace is read
        // from the host: case-insensitive on macOS/Windows, case-sensitive on Linux CI).
        var nuGetConfigs = projectDir
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(f => string.Equals(f.Name, "NuGet.config", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.FullName)
            .ToList();

        Assert.True(
            nuGetConfigs.Count == 0,
            $"The TypeScript template must not drop a NuGet.config (the CLI resolves Aspire.* from the channel feed itself). Found: {string.Join(", ", nuGetConfigs)}");
    }
}
