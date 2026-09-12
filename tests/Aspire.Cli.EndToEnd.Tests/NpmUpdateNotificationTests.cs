// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Compression;
using System.Xml.Linq;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end coverage for npm-installed CLI update notifications.
/// </summary>
public sealed class NpmUpdateNotificationTests(ITestOutputHelper output)
{
    private const string NpmLatestVersion = "999.0.0";
    private const string NuGetOnlyVersion = "999.1.0";

    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task UpdateNotificationUsesNpmLatestVersionInsteadOfNewerNuGetFeedVersion()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        RequireOfflineArchiveOrSkip(strategy);

        var workspace = TemporaryWorkspace.Create(output);
        var fakeNpmCompletedMarkerPath = Path.Combine(workspace.WorkspaceRoot.FullName, "npm-latest-version-complete");
        var fakeNpmPath = CreateFakeNpmScript(workspace, fakeNpmCompletedMarkerPath);
        var fakeFeedPath = CreateFakeCliFeed(workspace);
        WriteWorkspaceNuGetConfig(workspace, CliE2ETestHelpers.ToContainerPath(fakeFeedPath, workspace));

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        var containerFakeBinPath = CliE2ETestHelpers.ToContainerPath(Path.GetDirectoryName(fakeNpmPath)!, workspace);
        var containerFeedPath = CliE2ETestHelpers.ToContainerPath(fakeFeedPath, workspace);

        // Prove the workspace NuGet feed really offers a higher Aspire.Cli version than the npm dist-tag.
        // If the production command path accidentally consults NuGet instead of npm, the banner would show
        // 999.1.0 and this test would fail below.
        await auto.RunCommandAsync(
            $"dotnet package search Aspire.Cli --exact-match --source {AspireCliShellCommandHelpers.QuoteBashArg(containerFeedPath)} --prerelease --format json > ./cli-search.json && grep -q {AspireCliShellCommandHelpers.QuoteBashArg(NuGetOnlyVersion)} ./cli-search.json",
            counter,
            TimeSpan.FromSeconds(60));

        await auto.ClearScreenAsync(counter);

        await auto.RunCommandAsync(
            $"export PATH={AspireCliShellCommandHelpers.QuoteBashArg(containerFakeBinPath)}:$PATH ASPIRE_NPM_PACKAGE=@microsoft/aspire-cli ASPIRE_NPM_PACKAGE_VERSION=0.0.0-test ASPIRE_NPM_PACKAGE_RID=linux-x64",
            counter);

        // `aspire init` is update-notification enabled and pauses on interactive prompts, which gives
        // the hosted prefetch path time to populate the shared update cache before the command exits.
        await auto.TypeAsync("aspire init");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(
            snapshot => new CellPatternSearcher().Find("> C#").Search(snapshot).Count > 0,
            timeout: TimeSpan.FromSeconds(30),
            description: "language selection prompt with default '> C#'");
        await WaitForFileAsync(
            fakeNpmCompletedMarkerPath,
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Created aspire.config.json", timeout: TimeSpan.FromMinutes(2));
        await auto.DeclineAgentInitPromptAsync(counter);

        await auto.WaitUntilAsync(snapshot =>
        {
            if (snapshot.ContainsText($"A new version of Aspire is available: {NuGetOnlyVersion}"))
            {
                throw new InvalidOperationException("Update notification preferred the newer NuGet feed version instead of npm latest.");
            }

            return snapshot.ContainsText($"A new version of Aspire is available: {NpmLatestVersion}") &&
                   snapshot.ContainsText("To update, run: npm install -g @microsoft/aspire-cli@latest");
        }, timeout: TimeSpan.FromSeconds(30), description: "npm update notification using npm latest version");
    }

    private static void RequireOfflineArchiveOrSkip(CliInstallStrategy strategy)
    {
        Assert.SkipUnless(
            strategy.Mode is CliInstallMode.LocalArchive or CliInstallMode.LocalHive,
            $"This test requires an offline CLI archive so the E2E run stays deterministic. Current mode: {strategy.Mode}. " +
            "Run with ASPIRE_E2E_ARCHIVE (LocalHive) or ASPIRE_E2E_CLI_ARCHIVE_DIR (LocalArchive).");
    }

    private static string CreateFakeNpmScript(TemporaryWorkspace workspace, string completionMarkerPath)
    {
        var fakeBinDir = workspace.CreateDirectory("fake-bin");
        var fakeNpmPath = Path.Combine(fakeBinDir.FullName, "npm");
        var completionMarkerFileName = Path.GetFileName(completionMarkerPath);

        File.WriteAllText(fakeNpmPath, $$"""
            #!/bin/sh
            if [ "$#" -ge 3 ] && [ "$1" = "view" ] && [ "$2" = "@microsoft/aspire-cli@latest" ] && [ "$3" = "version" ]; then
              marker_path="$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)/{{completionMarkerFileName}}"
              : > "$marker_path"
              printf '%s\n' "{{NpmLatestVersion}}"
              exit 0
            fi

            printf 'unexpected fake npm args: %s\n' "$*" >&2
            exit 64
            """);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                fakeNpmPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        return fakeNpmPath;
    }

    private static string CreateFakeCliFeed(TemporaryWorkspace workspace)
    {
        var feedDir = workspace.CreateDirectory("local-cli-feed");
        var packagePath = Path.Combine(feedDir.FullName, $"Aspire.Cli.{NuGetOnlyVersion}.nupkg");

        using var package = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        using var packageMetadataStream = package.CreateEntry("Aspire.Cli.nuspec").Open();
        var nuspec = new XDocument(
            new XElement("package",
                new XElement("metadata",
                    new XElement("id", "Aspire.Cli"),
                    new XElement("version", NuGetOnlyVersion),
                    new XElement("authors", "Aspire"),
                    new XElement("description", "Fake Aspire CLI package used by npm update notification E2E coverage."))));
        nuspec.Save(packageMetadataStream);

        return feedDir.FullName;
    }

    private static void WriteWorkspaceNuGetConfig(TemporaryWorkspace workspace, string containerFeedPath)
    {
        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.config");
        var config = new XDocument(
            new XElement("configuration",
                new XElement("packageSources",
                    new XElement("add",
                        new XAttribute("key", "local-cli-feed"),
                        new XAttribute("value", containerFeedPath)))));
        config.Save(configPath);
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            return;
        }

        var directory = Path.GetDirectoryName(path);
        if (directory is null)
        {
            throw new InvalidOperationException($"Could not determine parent directory for '{path}'.");
        }

        var fileName = Path.GetFileName(path);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };

        void CompleteIfPresent()
        {
            if (File.Exists(path))
            {
                tcs.TrySetResult();
            }
        }

        FileSystemEventHandler createdOrChanged = (_, _) => CompleteIfPresent();
        RenamedEventHandler renamed = (_, _) => CompleteIfPresent();
        watcher.Created += createdOrChanged;
        watcher.Changed += createdOrChanged;
        watcher.Renamed += renamed;

        CompleteIfPresent();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        using var registration = timeoutCts.Token.Register(() => tcs.TrySetCanceled(timeoutCts.Token));
        await tcs.Task.ConfigureAwait(false);
    }
}
