// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for Aspire CLI with Empty AppHost template.
/// Each test class runs as a separate CI job for parallelization.
/// </summary>
public sealed class EmptyAppHostTemplateTests(ITestOutputHelper output)
{
    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task CreateAndRunEmptyAppHostProject()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        await auto.AspireNewAsync("AspireEmptyApp", counter, template: AspireTemplate.EmptyAppHost);

        // Start the empty AppHost to verify the scaffolded project works
        await auto.TypeAsync("cd AspireEmptyApp");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.AspireStartAsync(counter);
        await auto.AspireStopAsync(counter);
    }

    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task CreateDotNetTemplateWithSourceOverrideDoesNotContactOtherSources()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        await auto.RunCommandAsync("mkdir source-feed && cp \"$HOME\"/.aspire/hives/*/packages/Aspire.ProjectTemplates.*.nupkg source-feed/", counter);
        await auto.RunCommandAsync("aspire --version > /tmp/aspire-source-version", counter);

        // CLI update checks are independent from template source selection. Disable them so the
        // TCP tripwire isolates template discovery and installation traffic from the update notifier.
        await auto.RunCommandAsync("aspire config set features.updateNotificationsEnabled false -g", counter);
        await auto.RunCommandAsync("aspire config set features.showAllTemplates true -g", counter);
        await auto.RunCommandAsync(
            "export ASPIRE_CLI_CHANNEL=staging ASPIRE_CLI_VERSION=\"$(cat /tmp/aspire-source-version)\" " +
            "ASPIRE_CLI_COMMIT=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            counter);

        // The selected .NET template restores after scaffolding. Keep that independent post-action
        // on the local feed so the tripwire remains scoped to template discovery and installation.
        await auto.RunCommandAsync("dotnet nuget disable source nuget.org", counter);
        await auto.RunCommandAsync("dotnet nuget add source \"$PWD/source-feed\" --name source-override-e2e", counter);

        // Package search can suppress source failures, so use a TCP tripwire to detect connection attempts independently of logs.
        await auto.RunCommandAsync(
            "printf '127.0.0.1 api.nuget.org azuresearch-usnc.nuget.org azuresearch-ussc.nuget.org pkgs.dev.azure.com\\n' >> /etc/hosts",
            counter);
        await auto.RunCommandAsync(
            "rm -f /tmp/unexpected-nuget-source-contacted /tmp/nuget-source-listener-ready && " +
            "python3 -c 'import pathlib,socket; s=socket.socket(); s.setsockopt(socket.SOL_SOCKET,socket.SO_REUSEADDR,1); " +
            "s.bind((\"0.0.0.0\",443)); s.listen(); pathlib.Path(\"/tmp/nuget-source-listener-ready\").touch(); " +
            "c,_=s.accept(); pathlib.Path(\"/tmp/unexpected-nuget-source-contacted\").touch(); c.close()' >/tmp/nuget-source-listener.log 2>&1 & " +
            "for i in $(seq 1 100); do [ -f /tmp/nuget-source-listener-ready ] && break; sleep 0.1; done; " +
            "if [ ! -f /tmp/nuget-source-listener-ready ]; then cat /tmp/nuget-source-listener.log; (exit 1); fi",
            counter);
        await auto.RunCommandAsync("rm -rf \"$HOME/.aspire/logs\" && mkdir -p \"$HOME/.aspire/logs\"", counter);

        await auto.RunCommandAsync(
            "aspire new aspire-servicedefaults --name SourceOverrideServiceDefaults --output SourceOverrideServiceDefaults " +
            "--channel staging --source source-feed --non-interactive --suppress-agent-init --log-level Debug",
            counter,
            TimeSpan.FromMinutes(2));

        await auto.RunCommandAsync("test -f SourceOverrideServiceDefaults/SourceOverrideServiceDefaults.csproj", counter);

        // A local `--source` directory is enumerated directly rather than queried through a feed, so
        // the observable evidence is the install itself: it runs from a source-scoped temporary
        // NuGet.config directory and points `dotnet new install` at the .nupkg copied into
        // source-feed. Logged lines look like:
        //   Running dotnet in /tmp/aspire-nuget-configABC123 with args: new install /workspace/.../source-feed/Aspire.ProjectTemplates.13.0.0-preview.1.nupkg
        // Match the argument list instead of the randomly-suffixed temp directory.
        //
        // The negative guard has to name the search path this CLI would actually take. Shipped (and
        // CI-installed) CLIs are bundles, so a feed-backed template lookup goes through
        // BundleNuGetPackageCache -> `aspire-managed nuget search`, which logs:
        //   Running NuGet search via aspire-managed: Aspire.ProjectTemplates
        //   NuGet search args: nuget search --query Aspire.ProjectTemplates --take 1000 --format json ...
        // Those two Debug lines are the only trace of it, because the helper process itself is spawned
        // with SuppressLogging (as is the SDK-backed `dotnet package search` fallback), so asserting on
        // a logged `package search` command line can never fail and would not catch a regression.
        // Because the positive assertion above matches a Debug line from the same log, an empty or
        // level-filtered log cannot make these negative assertions pass vacuously.
        await auto.RunCommandAsync(
            "test ! -e /tmp/unexpected-nuget-source-contacted && " +
            "find \"$HOME/.aspire/logs\" -type f -name '*.log' -print -quit | grep -q . && " +
            "grep -R -F \"Resolved 'staging' channel\" \"$HOME/.aspire/logs\" && " +
            "grep -R -E 'Running dotnet in .*aspire-nuget-config.* with args: new install [^ ]*/source-feed/Aspire\\.ProjectTemplates\\.[^ ]*\\.nupkg' \"$HOME/.aspire/logs\" && " +
            "! grep -R -E -- 'Running NuGet search via aspire-managed: Aspire\\.ProjectTemplates|NuGet search args: nuget search --query Aspire\\.ProjectTemplates' \"$HOME/.aspire/logs\" && " +
            "! grep -R -F -- '--nuget-source' \"$HOME/.aspire/logs\" && " +
            "! grep -R -F 'api.nuget.org' \"$HOME/.aspire/logs\"",
            counter);
    }
}
