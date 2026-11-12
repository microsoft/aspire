// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Hex1b.Input;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// Regression tests for channel-update NuGet policy and restore ordering
/// (https://github.com/microsoft/aspire/issues/15891).
/// </summary>
/// <remarks>
/// <para>
/// Reproduces the user-visible failure where <c>aspire update --channel daily</c> on a
/// file-based AppHost referencing several stable Aspire integration packages crashed
/// with <c>NU1103</c>. The CLI rewrote <c>nuget.config</c> to add the daily feed plus a
/// <c>&lt;packageSourceMapping&gt;</c> pinning <c>Aspire*</c> to that feed, then ran each
/// per-package <c>dotnet package add</c> with restore enabled. Restore on the first add
/// saw the still-stable references for the not-yet-bumped packages, but the new mapping
/// blocked the nuget.org fallback for them, so NuGet emitted <c>NU1103</c> and the
/// update aborted halfway through.
/// </para>
/// <para>
/// The fix in <c>ProjectUpdater</c> passes <c>--no-restore</c> on every per-package add
/// and validates the complete graph only when <c>--dry-run</c> is requested,
/// so the packageSourceMapping never sees a half-updated reference graph. These tests
/// exercises that invariant end-to-end against the LocalHive channel — the bug fires
/// for any <c>Explicit</c> channel (daily, staging, PR hives, local), and LocalHive is
/// the only channel a source-build E2E run can deterministically control.
/// </para>
/// <para>
/// Configured-channel cases also run against PR archives and verify feed-only updates,
/// including preview approval and rejection without an explicit channel option.
/// </para>
/// <para>
/// This lives in its own test class (separate from <see cref="ChannelUpdateWorkflowTests"/>)
/// so CI's per-class job split isolates NuGet policy regressions from unrelated channel tests.
/// </para>
/// </remarks>
public sealed class UpdateChannelNuGetConfigOrderingTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task AspireUpdate_DryRunRestoresSourcesAndRejectsWarmCacheOnlyDependencies()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        Assert.SkipUnless(strategy.Mode is CliInstallMode.LocalHive or CliInstallMode.LocalArchive,
            "Channel validation requires packages from a current CLI archive.");
        var channel = CliE2ETestHelpers.PrepareLocalChannel(
            repoRoot, strategy, ["Aspire.AppHost.Sdk.", "Aspire.Hosting.Redis."]);
        var sdkVersion = channel?.SdkVersion ?? strategy.ExpectedVersion;
        Assert.NotNull(sdkVersion);
        var hiveLabel = strategy.Mode == CliInstallMode.LocalHive ? "local" : strategy.LocalArchiveHiveLabel;

        using var workspace = TemporaryWorkspace.Create(output);
        var root = workspace.WorkspaceRoot.FullName;
        const string dependencyId = "Contoso.UpdateValidation.Dependency";
        const string dependencyVersion = "1.0.0";
        var feed = workspace.CreateDirectory("dependency-feed");
        var packagePath = Path.Combine(feed.FullName, $"{dependencyId}.{dependencyVersion}.nupkg");
        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            await using (var writer = new StreamWriter(archive.CreateEntry($"{dependencyId}.nuspec").Open()))
            {
                await writer.WriteAsync($"""
                    <?xml version="1.0"?>
                    <package>
                      <metadata>
                        <id>{dependencyId}</id>
                        <version>{dependencyVersion}</version>
                        <authors>Aspire Tests</authors>
                        <description>Detects validation that reuses the ambient package cache.</description>
                      </metadata>
                    </package>
                    """);
            }
            archive.CreateEntry("lib/net10.0/_._");
        }

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot, strategy, output, mountDockerSocket: false, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);
        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.RunCommandAsync("export NUGET_PACKAGES=\"$ASPIRE_E2E_WORKSPACE/.nuget-packages\"", counter);

        var projectPath = Path.Combine(root, "AppHost.csproj");
        // The exact version range resolves successfully but still needs a source edit to become
        // the selected version. Both successful and failed validation must roll that edit back.
        await File.WriteAllTextAsync(projectPath, $$"""
            <Project Sdk="Aspire.AppHost.Sdk/{{sdkVersion}}">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <NuGetAudit>false</NuGetAudit>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Aspire.Hosting.Redis" Version="[{{sdkVersion}}]" />
                <PackageReference Include="{{dependencyId}}" Version="{{dependencyVersion}}" />
              </ItemGroup>
            </Project>
            """);
        var configPath = Path.Combine(root, "aspire.config.json");
        await File.WriteAllTextAsync(configPath, $$"""
            {
              "appHost": { "path": "AppHost.csproj" },
              "channel": "stable",
              "sdk": { "version": "{{sdkVersion}}" },
              "configSource": { "optOut": true },
              "features": { "updateNotificationsEnabled": false }
            }
            """);
        var nugetPath = Path.Combine(root, "nuget.config");
        new XDocument(new XElement("configuration",
            new XElement("packageSources",
                new XElement("clear"),
                new XElement("add", new XAttribute("key", "dependency"),
                    new XAttribute("value", CliE2ETestHelpers.ToContainerPath(feed.FullName, workspace))),
                new XElement("add", new XAttribute("key", "nuget.org"),
                    new XAttribute("value", "https://api.nuget.org/v3/index.json")))))
            .Save(nugetPath);
        await auto.RunCommandAsync(
            $"dotnet nuget add source \"$HOME/.aspire/hives/{hiveLabel}/packages\" --name seed-local --configfile nuget.config",
            counter);
        await auto.RunCommandAsync("dotnet restore AppHost.csproj --force --no-http-cache", counter, TimeSpan.FromMinutes(3));
        // Channel selection must supply the hive for candidate validation and persist it only
        // during apply. The original SDK and application closure remain warm in the user cache.
        await auto.RunCommandAsync("dotnet nuget remove source seed-local --configfile nuget.config", counter);
        var originals = new Dictionary<string, byte[]>();
        foreach (var path in new[] { projectPath, configPath, nugetPath })
        {
            originals.Add(path, await File.ReadAllBytesAsync(path));
        }

        var dryRunCommand = $"aspire update --apphost AppHost.csproj --channel {hiveLabel} --dry-run";
        await auto.RunCommandAsync(dryRunCommand + " --non-interactive", counter, TimeSpan.FromMinutes(3));
        await AssertOriginalsAsync();
        Assert.Empty(Directory.GetFiles(root, "*.aspire-backup*"));

        var backupPath = projectPath + ".aspire-backup";
        const string previousBackup = "Previous recovery data must not be overwritten without consent.";
        await File.WriteAllTextAsync(backupPath, previousBackup);
        await auto.RunCommandAsync(dryRunCommand + " --yes --non-interactive; printf '%s' \"$?\" > dry-run-exit.txt",
            counter, TimeSpan.FromMinutes(3));
        await AssertValidationFailedAsync();
        await AssertOriginalsAsync();
        Assert.Equal(previousBackup, await File.ReadAllTextAsync(backupPath));

        await auto.TypeAsync(dryRunCommand + " --yes; printf '%s' \"$?\" > dry-run-exit.txt");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Replace the existing backups with the current file contents?", timeout: TimeSpan.FromMinutes(3));
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
        await AssertValidationFailedAsync();
        await AssertOriginalsAsync();
        Assert.Equal(previousBackup, await File.ReadAllTextAsync(backupPath));

        await auto.TypeAsync(dryRunCommand);
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Replace the existing backups with the current file contents?", timeout: TimeSpan.FromMinutes(3));
        await auto.KeyAsync(Hex1bKey.Y);
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(3));
        await AssertOriginalsAsync();
        Assert.Empty(Directory.GetFiles(root, "*.aspire-backup*"));

        // Remove the only feed copy while retaining the extracted package. An ordinary warm
        // restore still succeeds, but validation must download into its own empty package folder.
        File.Delete(packagePath);
        await auto.RunCommandAsync("dotnet restore AppHost.csproj --force --no-http-cache", counter, TimeSpan.FromMinutes(3));
        var cachedDependency = Path.Combine(root, ".nuget-packages", dependencyId.ToLowerInvariant(), dependencyVersion);
        Assert.True(File.Exists(Path.Combine(cachedDependency, ".nupkg.metadata")));
        await auto.RunCommandAsync(
            dryRunCommand + " --non-interactive --log-level debug > dry-run-output.txt 2>&1; printf '%s' \"$?\" > dry-run-exit.txt",
            counter, TimeSpan.FromMinutes(3));
        await AssertValidationFailedAsync();
        // The CLI does not forward native restore output. Its assets file records diagnostics as:
        // { "code": "NU1101", "level": "Error", "libraryId": "Contoso.UpdateValidation.Dependency", ... }
        var restoreDiagnostics = JsonNode.Parse(
            await File.ReadAllTextAsync(Path.Combine(root, "obj", "project.assets.json")))!["logs"]!.AsArray();
        Assert.Single(restoreDiagnostics, diagnostic => diagnostic is { } error
            && error["code"]?.GetValue<string>() == "NU1101"
            && error["level"]?.GetValue<string>() == "Error"
            && error["libraryId"]?.GetValue<string>() == dependencyId);
        await AssertOriginalsAsync();
        Assert.Empty(Directory.GetFiles(root, "*.aspire-backup*"));
        Assert.True(File.Exists(Path.Combine(cachedDependency, ".nupkg.metadata")));

        // With neither a source nor a cache entry, an accidental application restore would
        // fail. Applying the explicit channel update must still persist its confirmed edits.
        await auto.RunCommandAsync(
            $"mv .nuget-packages/{dependencyId.ToLowerInvariant()}/{dependencyVersion} unavailable-dependency-cache", counter);
        await auto.RunCommandAsync(
            $"aspire update --apphost AppHost.csproj --channel {hiveLabel} --yes --non-interactive",
            counter, TimeSpan.FromMinutes(3));
        var updatedProject = XDocument.Load(projectPath);
        Assert.Equal(sdkVersion, updatedProject.Descendants("PackageReference")
            .Single(reference => (string?)reference.Attribute("Include") == "Aspire.Hosting.Redis").Attribute("Version")!.Value);
        var updatedConfig = JsonNode.Parse(await File.ReadAllTextAsync(configPath))!;
        Assert.Equal(hiveLabel, updatedConfig["channel"]!.GetValue<string>());
        Assert.Equal(sdkVersion, updatedConfig["sdk"]!["version"]!.GetValue<string>());
        Assert.Single(XDocument.Load(nugetPath).Descendants("packageSources").Elements("add"),
            source => ((string?)source.Attribute("value"))?.EndsWith($"/hives/{hiveLabel}/packages", StringComparison.Ordinal) == true);
        Assert.Empty(Directory.GetFiles(root, "*.aspire-backup*"));

        async Task AssertOriginalsAsync()
        {
            foreach (var (path, original) in originals)
            {
                Assert.Equal(original, await File.ReadAllBytesAsync(path));
            }
        }

        async Task AssertValidationFailedAsync()
        {
            Assert.Equal(CliExitCodes.FailedToUpgradeProject.ToString(CultureInfo.InvariantCulture),
                await File.ReadAllTextAsync(Path.Combine(root, "dry-run-exit.txt")));
        }
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task AspireUpdate_AppliesAllPackageEdits_OnSourceMapping()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);

        // The bug only fires when the channel switch causes the CLI to rewrite nuget.config
        // and add a packageSourceMapping. That requires an Explicit channel (daily, staging,
        // PR hive, or local) with a controlled set of packages. Only LocalHive gives a
        // source-build E2E run that level of control; for other strategies we skip rather
        // than try to reproduce against a moving public daily feed.
        if (strategy.Mode != CliInstallMode.LocalHive)
        {
            Assert.Skip(
                "Issue #15891 requires an Explicit channel with controlled package versions. " +
                "Run with ASPIRE_E2E_ARCHIVE pointing at a locally-built archive (LocalHive).");
        }

        // Pre-stage the locally-built nupkgs for the integration packages we'll reference.
        // Three is enough to make the bug surface deterministically: the per-package adds run
        // sequentially, so the first one's restore (without the fix) sees the other two still
        // pinned to the stable version that doesn't exist on the local feed.
        var localChannel = CliE2ETestHelpers.PrepareLocalChannel(
            repoRoot,
            strategy,
            ["Aspire.Hosting.Redis.", "Aspire.Hosting.PostgreSQL.", "Aspire.Hosting.Kafka."]);

        Assert.NotNull(localChannel);

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot, strategy, output, mountDockerSocket: false, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        // Suppress the post-update CLI self-update prompt so it doesn't block the test waiting
        // for "Update successful!". Same pattern as CentralPackageManagementTests.
        await auto.TypeAsync("aspire config set features.updateNotificationsEnabled false -g");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Run aspire init non-interactively to scaffold a single-file C# AppHost. We'll
        // overwrite the generated apphost.cs and nuget.config below to set up the bug
        // pre-condition (stable Aspire packages + mapped nuget.org source).
        await auto.TypeAsync("aspire init --language csharp");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Created aspire.config.json", timeout: TimeSpan.FromMinutes(2));
        await auto.DeclineAgentInitPromptAsync(counter);

        var appHostCsPath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs");
        var nugetConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, "nuget.config");
        var aspireConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.config.json");

        Assert.True(File.Exists(appHostCsPath), $"Expected apphost.cs at: {appHostCsPath}");
        Assert.True(File.Exists(aspireConfigPath), $"Expected aspire.config.json at: {aspireConfigPath}");

        // Set up the bug pre-condition from the host side — the workspace is bind-mounted
        // into the container so these writes are visible to the in-container CLI.

        // 1. apphost.cs: reference three stable Aspire integration packages from nuget.org.
        //    13.2.1 is the same release David Fowler used in the issue's repro and is real
        //    on nuget.org for all three packages, so the initial state matches the bug
        //    report exactly: stable references that nuget.org can resolve before the
        //    channel switch.
        const string StablePackageVersion = "13.2.1";
        await File.WriteAllTextAsync(appHostCsPath,
            $$"""
            #:sdk Aspire.AppHost.Sdk@{{StablePackageVersion}}
            #:package Aspire.Hosting.AppHost@{{StablePackageVersion}}
            #:package Aspire.Hosting.Redis@{{StablePackageVersion}}
            #:package Aspire.Hosting.PostgreSQL@{{StablePackageVersion}}
            #:package Aspire.Hosting.Kafka@{{StablePackageVersion}}

            var builder = DistributedApplication.CreateBuilder(args);
            builder.Build().Run();
            """);

        // 2. Start with mapping enabled: the CLI only extends existing mapping policy.
        //    Adding the local Aspire* mapping must not trigger restore while other Aspire
        //    references still require their old nuget.org versions.
        await File.WriteAllTextAsync(nugetConfigPath,
            """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="nuget.org"><package pattern="*" /></packageSource>
              </packageSourceMapping>
            </configuration>
            """);

        // 3. aspire.config.json: point at the locally-built channel/SDK so `aspire update`
        //    has a concrete Explicit target to compute updates against. Preserves the
        //    appHost/profiles sections that `aspire init` wrote.
        CliE2ETestHelpers.WriteLocalChannelSettings(workspace.WorkspaceRoot.FullName, localChannel.SdkVersion);

        // Run the update. Pass --channel local explicitly to make the CLI's channel
        // resolution deterministic regardless of any global channel state.
        await auto.TypeAsync("aspire update --channel local");
        await auto.EnterAsync();

        await auto.WaitUntilTextAsync("Perform updates?", timeout: TimeSpan.FromMinutes(2));
        await auto.EnterAsync();

        // The pre-existing nuget.config doesn't already contain the local feed, so the CLI
        // will prompt to apply the merged changes. Accept the default (Yes).
        await auto.WaitUntilTextAsync("Apply these changes to NuGet.config?", timeout: TimeSpan.FromMinutes(1));
        await auto.EnterAsync();

        // Without the fix this is where the test would fail: NuGet would emit NU1103 on
        // the first per-package restore and "Update successful!" would never appear before
        // the timeout. All package edits must land without implicitly restoring the graph.
        await auto.WaitUntilTextAsync("Update successful!", timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        // Host-side assertions on the post-update files.
        var updatedAppHostCs = await File.ReadAllTextAsync(appHostCsPath);
        var updatedNuGetConfig = await File.ReadAllTextAsync(nugetConfigPath);

        // Pre-condition was actually exercised: nuget.config gained an Aspire* mapping.
        // If this assertion fails it means the rewrite never happened and the
        // test isn't actually guarding the bug.
        Assert.Contains("packageSourceMapping", updatedNuGetConfig, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Aspire", updatedNuGetConfig, StringComparison.Ordinal);

        // Every Aspire integration directive was bumped — proves the loop ran to completion
        // rather than aborting after the first add (the user-visible symptom of the bug).
        foreach (var packageId in new[]
                 {
                     "Aspire.Hosting.AppHost",
                     "Aspire.Hosting.Redis",
                     "Aspire.Hosting.PostgreSQL",
                     "Aspire.Hosting.Kafka",
                 })
        {
            Assert.Contains($"#:package {packageId}@{localChannel.SdkVersion}", updatedAppHostCs, StringComparison.Ordinal);
            Assert.DoesNotContain($"#:package {packageId}@{StablePackageVersion}", updatedAppHostCs, StringComparison.Ordinal);
        }

        // The SDK directive is bumped on the same code path; assert it too so a future
        // regression that only updates packages but not the SDK is also caught.
        Assert.Contains($"#:sdk Aspire.AppHost.Sdk@{localChannel.SdkVersion}", updatedAppHostCs, StringComparison.Ordinal);

        // Best-effort cleanup of the global feature flag we toggled above so other tests in
        // the same shared home aren't affected. Bound the wait so cleanup never burns the
        // automator's default timeout, and accept either OK or ERR — we only care that the
        // shell returned to a prompt.
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [CaptureWorkspaceOnFailure]
    public async Task AspireUpdate_ConfiguredChannel_PreviewsNuGetPolicyBeforeApplying(bool accept)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        if (strategy.Mode is not (CliInstallMode.LocalHive or CliInstallMode.PullRequest or CliInstallMode.LocalArchive))
        {
            Assert.Skip("Configured channel updates require a current source build. Run with ASPIRE_E2E_ARCHIVE or in pull request CI.");
        }

        using var workspace = TemporaryWorkspace.Create(output);
        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot, strategy, output, mountDockerSocket: false, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, cancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.RunCommandAsync("aspire config set features.updateNotificationsEnabled false -g", counter);

        const string channelName = "configured-update";
        // A separate hive name distinguishes the configured channel from the running CLI's identity.
        // Reuse the installed packages so PR archive runs need no locally packed repository output.
        await auto.RunCommandAsync(
            "package_path=$(find \"$HOME/.aspire/hives\" -type f -name 'Aspire.Hosting.*.nupkg' -print -quit) && " +
            "test -n \"$package_path\" && " +
            $"mkdir -p \"$HOME/.aspire/hives/{channelName}\" && " +
            $"ln -s \"$(dirname \"$package_path\")\" \"$HOME/.aspire/hives/{channelName}/packages\"",
            counter);

        const string projectName = "ConfiguredChannelApp";
        await auto.AspireNewCSharpEmptyAppHostAsync(projectName, counter);

        var projectPath = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
        var appHostPath = Path.Combine(projectPath, "apphost.cs");
        var configPath = Path.Combine(projectPath, "aspire.config.json");
        var nugetConfigPath = Path.Combine(projectPath, "nuget.config");
        var restoreMarkerPath = Path.Combine(projectPath, "native-restore.txt");
        await File.WriteAllTextAsync(Path.Combine(projectPath, "Directory.Build.targets"), """
            <Project>
              <Target Name="RecordConfiguredChannelRestore" AfterTargets="Restore">
                <WriteLinesToFile File="$(MSBuildThisFileDirectory)native-restore.txt"
                                  Lines="restored" Overwrite="true" />
              </Target>
            </Project>
            """, cancellationToken);
        var originalAppHost = await File.ReadAllBytesAsync(appHostPath, cancellationToken);
        var appHostVersion = CliE2ETestHelpers.GetAppHostVersion(
            await File.ReadAllTextAsync(appHostPath, cancellationToken));
        var configuration = JsonNode.Parse(await File.ReadAllTextAsync(configPath, cancellationToken))!.AsObject();
        configuration["channel"] = channelName;
        configuration["sdk"] ??= new JsonObject();
        configuration["sdk"]!["version"] = appHostVersion;
        await File.WriteAllTextAsync(configPath, configuration.ToJsonString(), cancellationToken);

        // Keep package versions and SDK metadata current: changing only feed policy must still
        // trigger preview and persistence without a validation restore rather than reporting "up to date".
        await File.WriteAllTextAsync(nugetConfigPath, """
            <configuration>
              <packageSources>
                <clear />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
              <packageSourceMapping>
                <clear />
                <packageSource key="nuget.org"><package pattern="*" /></packageSource>
              </packageSourceMapping>
            </configuration>
            """, cancellationToken);
        var originalConfig = await File.ReadAllBytesAsync(configPath, cancellationToken);
        var originalNuGetConfig = await File.ReadAllBytesAsync(nugetConfigPath, cancellationToken);

        await auto.RunCommandAsync($"cd {projectName}", counter);
        await auto.ClearScreenAsync(counter);
        await auto.TypeAsync("aspire update --nuget-config-dir .");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync(
            $"Using channel '{channelName}' from Aspire configuration. Pass --channel to override.",
            timeout: TimeSpan.FromMinutes(2));
        await auto.WaitUntilTextAsync("Perform updates?", timeout: TimeSpan.FromMinutes(2));
        await auto.TypeAsync("y");

        await auto.WaitUntilAsync(
            snapshot => snapshot.ContainsText("Apply these changes to NuGet.config?") &&
                snapshot.ContainsText($"/hives/{channelName}/packages") &&
                snapshot.ContainsText("Aspire*"),
            timeout: TimeSpan.FromMinutes(1),
            description: "configured-channel feed and mapping preview");
        Assert.Equal(originalAppHost, await File.ReadAllBytesAsync(appHostPath, cancellationToken));
        Assert.Equal(originalConfig, await File.ReadAllBytesAsync(configPath, cancellationToken));
        Assert.Equal(originalNuGetConfig, await File.ReadAllBytesAsync(nugetConfigPath, cancellationToken));
        Assert.False(File.Exists(restoreMarkerPath));

        // Spectre confirmation consumes one character; an extra Enter can reach the shell.
        await auto.TypeAsync(accept ? "y" : "n");
        if (accept)
        {
            await auto.WaitUntilTextAsync("Update successful!", timeout: TimeSpan.FromMinutes(3));
        }
        await auto.WaitForSuccessPromptAsync(counter);

        Assert.Equal(originalAppHost, await File.ReadAllBytesAsync(appHostPath, cancellationToken));
        Assert.Equal(originalConfig, await File.ReadAllBytesAsync(configPath, cancellationToken));
        if (!accept)
        {
            Assert.Equal(originalNuGetConfig, await File.ReadAllBytesAsync(nugetConfigPath, cancellationToken));
            Assert.False(File.Exists(restoreMarkerPath));
            return;
        }

        Assert.False(File.Exists(restoreMarkerPath));
        var nugetConfig = XDocument.Load(nugetConfigPath);
        var sources = nugetConfig.Root!.Element("packageSources")!.Elements("add").ToArray();
        Assert.Equal(2, sources.Length);
        var channelSource = Assert.Single(sources,
            source => source.Attribute("value")!.Value.EndsWith($"/hives/{channelName}/packages", StringComparison.Ordinal));
        var publicSource = Assert.Single(sources, source => source.Attribute("key")!.Value == "nuget.org");
        Assert.Equal("https://api.nuget.org/v3/index.json", publicSource.Attribute("value")!.Value);

        var mappings = nugetConfig.Root.Element("packageSourceMapping")!.Elements("packageSource").ToArray();
        Assert.Equal(2, mappings.Length);
        var channelMapping = Assert.Single(mappings,
            mapping => mapping.Attribute("key")!.Value == channelSource.Attribute("key")!.Value);
        Assert.Equal(["Aspire*"], channelMapping.Elements("package").Select(package => package.Attribute("pattern")!.Value));
        var publicMapping = Assert.Single(mappings, mapping => mapping.Attribute("key")!.Value == "nuget.org");
        Assert.Equal(["*"], publicMapping.Elements("package").Select(package => package.Attribute("pattern")!.Value));

        // Restore again without the CLI's temporary preview to validate the persisted policy.
        await auto.RunCommandAsync("dotnet restore apphost.cs --force", counter, timeout: TimeSpan.FromMinutes(3));
    }
}
