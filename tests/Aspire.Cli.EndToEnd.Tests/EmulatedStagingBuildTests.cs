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
/// nuget.org and drops no feed pin, a staging build resolves them from its SHA-specific darc feed and
/// therefore <em>must</em> drop a <c>NuGet.config</c> mapping <c>Aspire*</c> to
/// <c>darc-pub-microsoft-aspire-&lt;sha8&gt;</c>. Validating this locally — without producing a real
/// official build — is the core promise of the CLI identity sidecar.
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
}
