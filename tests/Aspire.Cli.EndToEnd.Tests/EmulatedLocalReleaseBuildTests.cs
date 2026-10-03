// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Resources;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for the <b>all-local future release</b> workflow: a developer builds a
/// <em>stable-shaped</em> archive (CLI + packages + templates) for a version that does not exist
/// anywhere yet — e.g. a future <c>13.5.0</c> — entirely from local source via
/// <c>localhive --version 13.5.0</c>, then drives <c>aspire new</c> and <c>aspire add</c> so that
/// every template and package is resolved from that local build instead of nuget.org.
///
/// This differs from <see cref="EmulatedReleasedBuildTests"/> (which emulates the <em>already
/// shipped</em> latest stable version, resolving from nuget.org) in that the emulated version is
/// only present in the local hive. Because the version does not exist on nuget.org, a successful
/// resolve against it is itself proof that the CLI consulted the local packages — exercising the
/// <c>ASPIRE_CLI_PACKAGES</c> hive override under a <c>stable</c> identity (the product fix that
/// makes the override honored under any emulated channel name, not just <c>local</c>).
///
/// This class contains the <b>all-local stable</b> row of the AppHost-language × channel matrix, with
/// one scaffolding test per AppHost language (C# and TypeScript), plus focused TypeScript coverage for
/// archive-backed identity emulation and package-scoped source composition. The TypeScript-focused
/// tests also run against the PR archive used by normal GitHub CI, so those regression guards do not
/// depend on the opt-in stable archive. We keep the language tests separate because C# and TypeScript
/// AppHosts scaffold through different code paths and have diverged in behavior before.
///
/// <para>
/// <b>Gating / cost:</b> the two scaffolding tests only run when the CLI was installed from a local
/// hive archive (<see cref="CliInstallMode.LocalHive"/>) and that build produced a stable-shaped
/// package (no pre-release suffix). The archive-backed identity and package-scoped source-composition
/// tests additionally run with the <see cref="CliInstallMode.LocalArchive"/> supplied by normal PR CI.
/// The stable-release matrix activates in the deliberate local workflow:
/// <code>
/// ./localhive.sh --version 13.5.0 -o /tmp/aspire-localrelease -r linux-arm64 --archive
/// ASPIRE_E2E_ARCHIVE=/tmp/aspire-localrelease.tar.gz \
///   dotnet test tests/Aspire.Cli.EndToEnd.Tests/Aspire.Cli.EndToEnd.Tests.csproj \
///   -- --filter-class "*.EmulatedLocalReleaseBuildTests"
/// </code>
/// </para>
///
/// Each test class runs as a separate CI job for parallelization.
/// </summary>
public sealed class EmulatedLocalReleaseBuildTests(ITestOutputHelper output)
{
    /// <summary>
    /// SCENARIO: a developer-built <b>future stable release</b> (e.g. <c>13.5.0</c>) of the CLI,
    /// scaffolding the C# <c>aspire-starter</c> template entirely from the local build.
    ///
    /// Validates the "everything local" promise for C#: <c>aspire new</c> resolves the template from
    /// the local hive (not nuget.org), pins the AppHost SDK to the local stable version, and — because
    /// a stable build's packages live on the ambient default source — drops <b>no</b> per-project
    /// <c>NuGet.config</c>. Then <c>aspire add redis</c> resolves <c>Aspire.Hosting.Redis</c> at the
    /// local stable version; since that version exists only in the local hive, resolving it proves the
    /// CLI consulted <c>ASPIRE_CLI_PACKAGES</c> rather than nuget.org.
    /// </summary>
    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task EmulatedLocalReleaseScaffoldsCSharpStarterFromLocalPackages()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var localStableVersion = RequireLocalStableArchiveOrSkip(repoRoot, strategy);

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await InstallLocalHiveWithoutLocalChannelAsync(auto, counter);
        await ApplyEmulatedLocalReleaseIdentityAsync(auto, counter, localStableVersion);

        const string projectName = "EmulatedLocalStarter";
        await auto.AspireNewAsync(projectName, counter, template: AspireTemplate.Starter, useRedisCache: false);

        var projectDir = new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, projectName));

        // A stable build's packages all live on the ambient default source, so the CLI must not pin a
        // custom feed via NuGet.config — even though resolution here is actually local. The hive is
        // surfaced through ASPIRE_CLI_PACKAGES, not a per-project feed pin.
        AssertNoNuGetConfig(projectDir);

        // The local stable version exists only in the hive, so an exact match pins the AppHost SDK to
        // it — proving `aspire new` resolved the template from the local build rather than nuget.org.
        var appHostCsproj = Path.Combine(projectDir.FullName, $"{projectName}.AppHost", $"{projectName}.AppHost.csproj");
        var sdkVersion = GetAppHostSdkVersionFromCsproj(appHostCsproj);
        output.WriteLine($"Generated AppHost SDK version: {sdkVersion}");
        Assert.Equal(localStableVersion, sdkVersion);

        // Register the local hive as an ambient NuGet source so the apphost's `Aspire.AppHost.Sdk`
        // (resolved by MSBuild's NuGet-based project-SDK resolver, which only reads nuget.config
        // sources — never ASPIRE_CLI_PACKAGES) can restore during `aspire add`.
        await RegisterLocalHiveAsAmbientNuGetSourceAsync(auto, counter);

        await auto.RunCommandAsync($"cd {projectName}/{projectName}.AppHost", counter);
        await AddIntegrationInteractivelyAsync(auto, counter, "redis");

        // The added Redis integration must be the local stable version. That version is absent from
        // nuget.org, so resolving it confirms `aspire add` consulted the local hive. We match whichever
        // Aspire Redis integration the interactive picker selected (e.g. Aspire.Hosting.Redis or
        // Aspire.Hosting.Azure.Redis) rather than hard-coding one, since the exact selection is not the
        // point of this test — proving the version came from the local build is.
        var (addedRedisPackage, addedRedisVersion) = GetAddedAspireRedisPackageReferenceFromCsproj(appHostCsproj);
        output.WriteLine($"Added Redis integration: {addedRedisPackage} {addedRedisVersion}");
        Assert.Equal(localStableVersion, addedRedisVersion);
    }

    /// <summary>
    /// SCENARIO: a developer-built <b>future stable release</b> (e.g. <c>13.5.0</c>) of the CLI,
    /// scaffolding the TypeScript <c>aspire-ts-starter</c> (Express/React) template entirely from the
    /// local build.
    ///
    /// This is the TypeScript half of the all-local stable row; it mirrors the C# test because the two
    /// AppHosts scaffold through different code paths and have historically diverged. The TS starter
    /// records its SDK version in <c>aspire.config.json</c> (not a csproj) and drops no
    /// <c>NuGet.config</c>; instead the polyglot path discovers <c>Aspire.*</c> packages through the
    /// hive surfaced by <c>ASPIRE_CLI_PACKAGES</c>. The test asserts the recorded SDK version equals
    /// the local stable version (present only in the hive) and that <c>aspire add redis</c> resolves
    /// the same local stable version — proving local resolution for the TypeScript path too.
    /// </summary>
    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task EmulatedLocalReleaseScaffoldsTypeScriptStarterFromLocalPackages()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var localStableVersion = RequireLocalStableArchiveOrSkip(repoRoot, strategy);

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await InstallLocalHiveWithoutLocalChannelAsync(auto, counter);
        await ApplyEmulatedLocalReleaseIdentityAsync(auto, counter, localStableVersion);

        const string projectName = "EmulatedLocalTsStarter";
        await auto.AspireNewAsync(projectName, counter, template: AspireTemplate.ExpressReact);

        var projectDir = new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, projectName));
        var configPath = Path.Combine(projectDir.FullName, "aspire.config.json");

        // The TypeScript polyglot path does not drop a project NuGet.config (a stable build's packages
        // live on the ambient default source); it instead relies on the hive via ASPIRE_CLI_PACKAGES.
        // Asserting this for TS too is the point of the matrix — C# and TS diverge here.
        AssertNoNuGetConfig(projectDir);

        // The TS starter records its SDK version in aspire.config.json. The local stable version exists
        // only in the hive, so an exact match proves `aspire new` resolved from the local build.
        var sdkVersion = GetSdkVersionFromAspireConfig(configPath);
        output.WriteLine($"Generated TypeScript SDK version: {sdkVersion}");
        Assert.Equal(localStableVersion, sdkVersion);

        await RegisterLocalHiveAsAmbientNuGetSourceAsync(auto, counter);

        await auto.RunCommandAsync($"cd {projectName}", counter);
        await AddIntegrationInteractivelyAsync(auto, counter, "redis");

        // The polyglot path records added integrations in aspire.config.json's `packages` map. The
        // resolved Redis version must be the local stable version (absent from nuget.org), confirming
        // `aspire add` consulted the local hive for the TypeScript path. We match whichever Aspire Redis
        // integration the picker selected rather than hard-coding one.
        var (addedRedisPackage, addedRedisVersion) = GetAddedAspireRedisPackageFromAspireConfig(configPath);
        output.WriteLine($"Added Redis integration: {addedRedisPackage} {addedRedisVersion}");
        Assert.Equal(localStableVersion, addedRedisVersion);
    }

    /// <summary>
    /// Verifies that the package hive shipped with a PR CI archive can back each channel identity
    /// used to validate a build before publication. The identity version remains the archive's exact
    /// package version because PR archives do not contain stable-shaped package aliases. The
    /// TypeScript AppHost deliberately has no persisted channel and the hive is not registered as an
    /// ambient NuGet source, so both initialization and the subsequent cold <c>aspire add</c> must
    /// resolve through <c>ASPIRE_CLI_PACKAGES</c>.
    /// </summary>
    [CaptureWorkspaceOnFailure]
    [Theory]
    [InlineData("local")]
    [InlineData("daily")]
    [InlineData("staging")]
    [InlineData("stable")]
    public async Task ArchivePackagesBackEmulatedTypeScriptIdentity(string identityChannel)
    {
        const string packageId = "Aspire.Hosting.Redis";

        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var archiveContext = RequireArchivePackagesOrSkip(repoRoot, strategy);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot,
            strategy,
            output,
            workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(
            terminal,
            workspace,
            auto,
            counter,
            output,
            TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await InstallArchiveWithoutLocalChannelAsync(auto, counter, strategy);
        await ApplyArchiveBackedIdentityAsync(
            auto,
            counter,
            identityChannel,
            archiveContext.PackageVersion,
            archiveContext.HiveLabel);

        // Each theory row gets an empty package cache. The archive hive remains invocation-scoped so
        // success proves the emulated identity honored ASPIRE_CLI_PACKAGES rather than an ambient feed.
        await auto.RunCommandAsync("export NUGET_PACKAGES=\"$PWD/.nuget-packages\"", counter);
        await auto.RunCommandAsync(
            $"if dotnet nuget list source | grep -F \"$HOME/.aspire/hives/{archiveContext.HiveLabel}/packages\"; then " +
            "echo 'The archive hive must not be registered as an ambient source.' >&2; exit 1; fi",
            counter);

        await auto.TypeAsync("aspire init --language typescript --non-interactive --suppress-agent-init");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Created apphost.mts", timeout: TimeSpan.FromMinutes(2));
        await auto.WaitForSuccessPromptAsync(counter);

        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.config.json");
        Assert.Equal(archiveContext.PackageVersion, GetSdkVersionFromAspireConfig(configPath));

        // Remove the channel persisted by initialization so `add` must resolve from the running CLI
        // identity. Clearing the cache then makes this the no-`--source` control for the scoped-source
        // test below: only the archive-backed identity source can satisfy the cold restore.
        RemoveChannelFromAspireConfig(configPath);
        await auto.RunCommandAsync(
            "rm -rf \"$NUGET_PACKAGES\" && mkdir -p \"$NUGET_PACKAGES\"",
            counter);

        await auto.TypeAsync(
            $"aspire add {packageId} --version {archiveContext.PackageVersion} --non-interactive");
        await auto.EnterAsync();
        await auto.WaitForAspireAddSuccessAsync(counter, TimeSpan.FromMinutes(3));

        Assert.Equal(
            archiveContext.PackageVersion,
            GetPackageVersionFromAspireConfig(configPath, packageId));

        var redisMetadata =
            $".nuget-packages/aspire.hosting.redis/{archiveContext.PackageVersion}/.nupkg.metadata";
        var hostingMetadata =
            $".nuget-packages/aspire.hosting/{archiveContext.PackageVersion}/.nupkg.metadata";
        await auto.RunCommandAsync(
            $"test -f {redisMetadata} && test -f {hostingMetadata} && " +
            $"python3 -c 'import json,os; " +
            $"redis=json.load(open(\"{redisMetadata}\")); hosting=json.load(open(\"{hostingMetadata}\")); " +
            $"expected=os.path.realpath(os.path.expandvars(\"$HOME/.aspire/hives/{archiveContext.HiveLabel}/packages\")); " +
            "assert os.path.realpath(redis[\"source\"]) == expected; " +
            "assert os.path.realpath(hosting[\"source\"]) == expected'",
            counter);
    }

    /// <summary>
    /// Verifies that a package-scoped <c>aspire add --source</c> appends the selected feed instead of
    /// replacing the matching identity feed. The explicit source contains only a third-party
    /// integration package; its exact <c>Aspire.Hosting</c> dependency exists only in the installed
    /// identity hive. The hive is deliberately not registered in ambient NuGet configuration, so a
    /// cold restore can succeed only when the CLI composes both invocation-scoped sources.
    /// </summary>
    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task PackageScopedSourceAppendsIdentitySourceForTypeScriptRestore()
    {
        const string packageId = "CommunityToolkit.Aspire.Hosting.SourceAppendProbe";
        const string packageVersion = "1.0.0";

        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var restoreContext = RequirePackageSourceAppendArchiveOrSkip(repoRoot, strategy);
        var workspace = TemporaryWorkspace.Create(output);
        CreateThirdPartyIntegrationPackage(
            Path.Combine(workspace.WorkspaceRoot.FullName, "explicit-feed"),
            packageId,
            packageVersion,
            restoreContext.PackageVersion);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot,
            strategy,
            output,
            workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(
            terminal,
            workspace,
            auto,
            counter,
            output,
            TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await InstallArchiveWithoutLocalChannelAsync(auto, counter, strategy);
        if (strategy.Mode == CliInstallMode.LocalHive)
        {
            await ApplyEmulatedLocalReleaseIdentityAsync(
                auto,
                counter,
                restoreContext.PackageVersion);
        }

        // Use an empty per-test package cache so a globally cached Aspire.Hosting cannot conceal
        // loss of the identity-local source. The identity hive must remain invocation-scoped: unlike
        // the C# AppHost tests above, this polyglot path does not need it registered with NuGet.
        await auto.RunCommandAsync("export NUGET_PACKAGES=\"$PWD/.nuget-packages\"", counter);
        await auto.RunCommandAsync(
            $"if dotnet nuget list source | grep -F \"$HOME/.aspire/hives/{restoreContext.HiveLabel}/packages\"; then " +
            "echo 'The identity hive must not be registered as an ambient source.' >&2; exit 1; fi",
            counter);

        await auto.TypeAsync("aspire init --language typescript --non-interactive --suppress-agent-init");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Created apphost.mts", timeout: TimeSpan.FromMinutes(2));
        await auto.WaitForSuccessPromptAsync(counter);

        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.config.json");
        Assert.Equal(restoreContext.PackageVersion, GetSdkVersionFromAspireConfig(configPath));

        // PR archive creation persists its channel in aspire.config.json. Remove that project-level
        // selection so the add operation must rediscover the matching source from the running CLI
        // identity, reproducing the path that package-scoped --source previously short-circuited.
        RemoveChannelFromAspireConfig(configPath);

        // Initialization restores the polyglot SDK and therefore warms NUGET_PACKAGES. Remove those
        // assets while leaving the workspace manifest in place so add must reject stale paths and
        // perform a genuinely cold restore from the composed explicit and identity sources.
        await auto.RunCommandAsync(
            "rm -rf \"$NUGET_PACKAGES\" && mkdir -p \"$NUGET_PACKAGES\"",
            counter);

        await auto.TypeAsync(
            $"aspire add {packageId} --version {packageVersion} --source explicit-feed --non-interactive");
        await auto.EnterAsync();
        await auto.WaitForAspireAddSuccessAsync(counter, TimeSpan.FromMinutes(3));

        Assert.Equal(packageVersion, GetPackageVersionFromAspireConfig(configPath, packageId));

        var packageCacheId = packageId.ToLowerInvariant();
        var sourceMetadata = $".nuget-packages/{packageCacheId}/{packageVersion}/.nupkg.metadata";
        var hostingMetadata = $".nuget-packages/aspire.hosting/{restoreContext.PackageVersion}/.nupkg.metadata";
        await auto.RunCommandAsync(
            $"test \"$(find explicit-feed -maxdepth 1 -name '*.nupkg' | wc -l)\" -eq 1 && " +
            $"test -f {sourceMetadata} && test -f {hostingMetadata} && " +
            $"python3 -c 'import json,os; " +
            $"selected=json.load(open(\"{sourceMetadata}\")); hosting=json.load(open(\"{hostingMetadata}\")); " +
            "assert os.path.realpath(selected[\"source\"]) == os.path.realpath(\"explicit-feed\"); " +
            $"assert os.path.realpath(hosting[\"source\"]) == os.path.realpath(os.path.expandvars(\"$HOME/.aspire/hives/{restoreContext.HiveLabel}/packages\"))'",
            counter);
    }

    private static ArchivePackageContext RequireArchivePackagesOrSkip(
        string repoRoot,
        CliInstallStrategy strategy)
    {
        Assert.SkipUnless(
            strategy.Mode is CliInstallMode.LocalHive or CliInstallMode.LocalArchive,
            $"Archive-backed identity tests require a local hive or PR archive; current mode is {strategy.Mode}.");

        if (strategy.Mode == CliInstallMode.LocalArchive)
        {
            Assert.SkipWhen(
                strategy.ExpectedVersion is null,
                "The PR archive did not contain an Aspire.Cli pointer package from which to determine the identity version.");

            return new(strategy.ExpectedVersion!, strategy.LocalArchiveHiveLabel);
        }

        var localChannel = CliE2ETestHelpers.PrepareLocalChannel(
            repoRoot,
            strategy,
            ["Aspire.Hosting.Redis."]);
        Assert.SkipWhen(
            localChannel is null,
            "The local hive archive did not contain the packages required by the archive-backed identity tests.");

        return new(localChannel!.SdkVersion, "local");
    }

    private static ArchivePackageContext RequirePackageSourceAppendArchiveOrSkip(
        string repoRoot,
        CliInstallStrategy strategy)
    {
        if (strategy.Mode == CliInstallMode.LocalArchive)
        {
            return RequireArchivePackagesOrSkip(repoRoot, strategy);
        }

        Assert.SkipUnless(
            strategy.Mode == CliInstallMode.LocalHive,
            $"Package-scoped source composition requires a local hive or PR archive; current mode is {strategy.Mode}.");

        var localStableVersion = RequireLocalStableArchiveOrSkip(repoRoot, strategy);
        return new(localStableVersion, "local");
    }

    /// <summary>
    /// Asserts the preconditions for an all-local emulated-release run and returns the local stable
    /// version, or skips the test. The run requires (1) the CLI installed from a local hive archive and
    /// (2) that archive built with a stable-shaped version (via <c>localhive --version X.Y.Z</c>). In
    /// every other configuration there is no future-release build to emulate, so the test skips.
    /// </summary>
    private static string RequireLocalStableArchiveOrSkip(string repoRoot, CliInstallStrategy strategy)
    {
        Assert.SkipUnless(
            strategy.Mode == CliInstallMode.LocalHive,
            $"All-local emulated-release tests require a local hive archive (CliInstallMode.LocalHive); current mode is {strategy.Mode}. " +
            "Build one with `localhive --version <X.Y.Z> --archive` and point ASPIRE_E2E_ARCHIVE at it.");

        var localStableVersion = CliE2ETestHelpers.TryGetLocalStableAspireVersion(repoRoot);
        Assert.SkipWhen(
            localStableVersion is null,
            "No stable-shaped Aspire packages were found under artifacts/packages/**/Shipping. " +
            "Build a stable-shaped archive with `localhive --version <X.Y.Z> --archive` to run this test.");

        return localStableVersion!;
    }

    /// <summary>
    /// Extracts the local hive archive and sources the CLI environment, but deliberately does NOT run
    /// the local-hive configuration step. That step pins <c>channel local</c> globally, which would
    /// defeat the whole point of this test: we want the CLI to run under an emulated <c>stable</c>
    /// identity while still resolving packages from the local hive (via <c>ASPIRE_CLI_PACKAGES</c>),
    /// exactly as a future shipped release would behave once its packages are public.
    /// </summary>
    private static async Task InstallLocalHiveWithoutLocalChannelAsync(Hex1bTerminalAutomator auto, SequenceCounter counter)
    {
        // The host archive is mounted read-only at this fixed path for LocalHive mode (see
        // CliInstallStrategy.ConfigureContainer). Extracting it lays the CLI down under ~/.aspire/bin
        // and the packages under ~/.aspire/hives/local/packages.
        await auto.ExtractLocalHiveArchiveAsync("/tmp/aspire-localhive.tar.gz", counter);
        await auto.SourceAspireCliEnvironmentAsync(counter);
    }

    private static Task InstallArchiveWithoutLocalChannelAsync(
        Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        CliInstallStrategy strategy)
    {
        return strategy.Mode == CliInstallMode.LocalHive
            ? InstallLocalHiveWithoutLocalChannelAsync(auto, counter)
            : auto.InstallAspireCliAsync(strategy, counter);
    }

    private static async Task ApplyArchiveBackedIdentityAsync(
        Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        string identityChannel,
        string identityVersion,
        string hiveLabel)
    {
        await auto.RunCommandAsync(
            $"export ASPIRE_CLI_CHANNEL={identityChannel} " +
            $"ASPIRE_CLI_VERSION={identityVersion} " +
            $"ASPIRE_CLI_PACKAGES=\"$HOME/.aspire/hives/{hiveLabel}/packages\"; " +
            (string.Equals(identityChannel, "staging", StringComparison.Ordinal)
                ? "export ASPIRE_CLI_COMMIT=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                : "unset ASPIRE_CLI_COMMIT"),
            counter);

        await auto.TypeAsync("aspire --version");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(
            s => s.ContainsText(identityVersion) && s.ContainsText("emulating identity"),
            timeout: TimeSpan.FromSeconds(60),
            description: $"aspire --version reporting emulated {identityChannel} identity '{identityVersion}' with archive packages");
        await auto.WaitForSuccessPromptAsync(counter);
    }

    /// <summary>
    /// Exports the identity-override environment variables that make the just-installed CLI report and
    /// behave as a future stable release whose packages live only in the local hive, then proves the
    /// override is live before any scaffolding runs.
    /// </summary>
    private static async Task ApplyEmulatedLocalReleaseIdentityAsync(Hex1bTerminalAutomator auto, SequenceCounter counter, string localStableVersion)
    {
        // ASPIRE_CLI_CHANNEL=stable + ASPIRE_CLI_VERSION coerce the identity to a shipped-release shape.
        // ASPIRE_CLI_PACKAGES points the Aspire* feed at the extracted hive's flat package directory so
        // template and integration resolution come from the local build. Together these emulate a
        // future release that has not been published anywhere yet. (The product fix being validated:
        // ASPIRE_CLI_PACKAGES is honored under the `stable` channel, not just `local`.)
        await auto.RunCommandAsync("export ASPIRE_CLI_CHANNEL=stable", counter);
        await auto.RunCommandAsync($"export ASPIRE_CLI_VERSION={localStableVersion}", counter);
        await auto.RunCommandAsync("export ASPIRE_CLI_PACKAGES=$HOME/.aspire/hives/local/packages", counter);

        // `aspire --version` reports the resolved identity version (honoring ASPIRE_CLI_VERSION) and the
        // emulation notice fires on stderr while an override is active. Seeing both confirms the override
        // path — not the physical build — is in effect before we depend on it.
        await auto.TypeAsync("aspire --version");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(
            s => s.ContainsText(localStableVersion) && s.ContainsText("emulating identity"),
            timeout: TimeSpan.FromSeconds(60),
            description: $"aspire --version reporting emulated local-release identity '{localStableVersion}' with override notice");
        await auto.WaitForSuccessPromptAsync(counter);
    }

    /// <summary>
    /// Registers the extracted local hive's flat package directory as an ambient NuGet source. This is
    /// required for buildability of <c>aspire add</c>/<c>aspire run</c> in the all-local stable case:
    /// MSBuild resolves the apphost's <c>Aspire.AppHost.Sdk</c> project SDK <em>before</em> restore via
    /// the NuGet-based SDK resolver, which reads only nuget.config sources and ignores
    /// <c>ASPIRE_CLI_PACKAGES</c>. Without the published packages on nuget.org, the local hive must be a
    /// real NuGet source for the SDK (and the integration's transitive packages) to restore.
    /// </summary>
    private static async Task RegisterLocalHiveAsAmbientNuGetSourceAsync(Hex1bTerminalAutomator auto, SequenceCounter counter)
    {
        // Writes to the user-level NuGet.config (~/.nuget/NuGet.config), NOT the project directory, so
        // the per-project "no NuGet.config" assertions above remain valid.
        await auto.RunCommandAsync(
            "dotnet nuget add source \"$HOME/.aspire/hives/local/packages\" --name local-aspire-release",
            counter,
            TimeSpan.FromSeconds(30));
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

    private static void CreateThirdPartyIntegrationPackage(
        string sourceDirectory,
        string packageId,
        string packageVersion,
        string hostingVersion)
    {
        Directory.CreateDirectory(sourceDirectory);
        var packagePath = Path.Combine(sourceDirectory, $"{packageId}.{packageVersion}.nupkg");
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        var nuspec = archive.CreateEntry($"{packageId}.nuspec");
        using (var writer = new StreamWriter(nuspec.Open()))
        {
            writer.Write($"""
                <?xml version="1.0"?>
                <package>
                  <metadata>
                    <id>{packageId}</id>
                    <version>{packageVersion}</version>
                    <authors>Aspire</authors>
                    <description>Verifies package-scoped source composition.</description>
                    <tags>aspire integration hosting polyglot</tags>
                    <dependencies>
                      <group targetFramework="net10.0">
                        <dependency id="Aspire.Hosting" version="[{hostingVersion}]" />
                      </group>
                    </dependencies>
                  </metadata>
                </package>
                """);
        }

        archive.CreateEntry("lib/net10.0/_._");
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
            $"Emulating an all-local stable build, 'aspire new' must not drop a NuGet.config (the hive is surfaced via ASPIRE_CLI_PACKAGES, not a per-project feed pin). Found: {string.Join(", ", nuGetConfigs)}");
    }

    private static string GetAppHostSdkVersionFromCsproj(string csprojPath)
    {
        if (!File.Exists(csprojPath))
        {
            throw new FileNotFoundException($"Expected AppHost project to exist: {csprojPath}", csprojPath);
        }

        // The generated AppHost csproj opens with: <Project Sdk="Aspire.AppHost.Sdk/13.5.0">
        var content = File.ReadAllText(csprojPath);
        var match = Regex.Match(content, "Sdk=\"Aspire\\.AppHost\\.Sdk/(?<version>[^\"]+)\"");
        return match.Success
            ? match.Groups["version"].Value
            : throw new InvalidOperationException($"Could not find an Aspire.AppHost.Sdk reference in {csprojPath}.");
    }

    private static (string Package, string Version) GetAddedAspireRedisPackageReferenceFromCsproj(string csprojPath)
    {
        if (!File.Exists(csprojPath))
        {
            throw new FileNotFoundException($"Expected AppHost project to exist: {csprojPath}", csprojPath);
        }

        // `aspire add` writes a PackageReference such as:
        //   <PackageReference Include="Aspire.Hosting.Redis" Version="13.5.0" />
        //   <PackageReference Include="Aspire.Hosting.Azure.Redis" Version="13.5.0" />
        // Match any Aspire.* Redis integration regardless of which one the interactive picker selected.
        // The Include and Version attributes can appear in either order, so handle both layouts.
        var content = File.ReadAllText(csprojPath);
        var match = Regex.Match(content, "Include=\"(?<id>Aspire\\.[^\"]*Redis[^\"]*)\"\\s+Version=\"(?<version>[^\"]+)\"");
        if (!match.Success)
        {
            match = Regex.Match(content, "Version=\"(?<version>[^\"]+)\"\\s+Include=\"(?<id>Aspire\\.[^\"]*Redis[^\"]*)\"");
        }

        return match.Success
            ? (match.Groups["id"].Value, match.Groups["version"].Value)
            : throw new InvalidOperationException($"Could not find an Aspire.* Redis PackageReference in {csprojPath}.");
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

    private static (string Package, string Version) GetAddedAspireRedisPackageFromAspireConfig(string configPath)
    {
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException($"Expected aspire.config.json to exist: {configPath}", configPath);
        }

        // The polyglot path records added integrations as:
        //   { "packages": { "Aspire.Hosting.Redis": "13.5.0", ... } }
        // Match any Aspire.* Redis integration regardless of which one the interactive picker selected.
        using var document = JsonDocument.Parse(File.ReadAllText(configPath));
        if (document.RootElement.TryGetProperty("packages", out var packages) &&
            packages.ValueKind == JsonValueKind.Object)
        {
            foreach (var package in packages.EnumerateObject())
            {
                if (package.Name.StartsWith("Aspire.", StringComparison.OrdinalIgnoreCase) &&
                    package.Name.Contains("Redis", StringComparison.OrdinalIgnoreCase) &&
                    package.Value.ValueKind == JsonValueKind.String &&
                    package.Value.GetString() is { Length: > 0 } version)
                {
                    return (package.Name, version);
                }
            }
        }

        throw new InvalidOperationException($"Could not find an Aspire.* Redis entry in packages of {configPath}.");
    }

    private static string GetPackageVersionFromAspireConfig(string configPath, string packageId)
    {
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException($"Expected aspire.config.json to exist: {configPath}", configPath);
        }

        using var document = JsonDocument.Parse(File.ReadAllText(configPath));
        if (document.RootElement.TryGetProperty("packages", out var packages) &&
            packages.ValueKind == JsonValueKind.Object &&
            packages.TryGetProperty(packageId, out var packageVersion) &&
            packageVersion.ValueKind == JsonValueKind.String &&
            packageVersion.GetString() is { Length: > 0 } version)
        {
            return version;
        }

        throw new InvalidOperationException($"Could not find package '{packageId}' in {configPath}.");
    }

    private static void RemoveChannelFromAspireConfig(string configPath)
    {
        var config = JsonNode.Parse(File.ReadAllText(configPath))?.AsObject()
            ?? throw new InvalidOperationException($"Expected {configPath} to contain a JSON object.");

        config.Remove("channel");
        File.WriteAllText(configPath, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed record ArchivePackageContext(string PackageVersion, string HiveLabel);
}
