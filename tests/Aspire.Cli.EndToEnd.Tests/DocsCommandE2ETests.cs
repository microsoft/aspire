// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

public sealed class DocsCommandE2ETests(ITestOutputHelper output)
{
    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task DocsCommand_RendersInteractiveMarkdownFromLocalSource()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        using var workspace = TemporaryWorkspace.Create(output);
        var docsFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, "llms-small.txt");

        File.WriteAllText(docsFilePath, """
            # Docs Smoke Test
            > Learn how to configure HTTPS endpoints with the [Aspire CLI](https://example.com/install) and `aspire run`.

            ## Steps

            1. First item

               * Nested item

            ## Commands

            ```bash
            aspire docs get docs-smoke-test
            ```

            ## Settings

            | Setting | Environment variable | Purpose |
            | :------ | :------------------- | ------: |
            | `Azure:SubscriptionId` | `Azure__SubscriptionId` | Target Azure subscription |
            """);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);
        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        await auto.TypeAsync("python3 -m http.server 18080 --bind 127.0.0.1 >/tmp/docs-server.log 2>&1 &");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("until curl -sf http://127.0.0.1:18080/llms-small.txt >/dev/null; do sleep 1; done");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("export DOCS__LLMSTXTURL=http://127.0.0.1:18080/llms-small.txt");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.ClearScreenAsync(counter);
        await auto.TypeAsync("aspire --nologo docs get docs-smoke-test");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(snapshot =>
        {
            if (snapshot.ContainsText("https://example.com/install"))
            {
                throw new InvalidOperationException("Interactive docs rendering fell back to plain-text link output.");
            }

            if (snapshot.ContainsText("[Aspire CLI](") || snapshot.ContainsText("`aspire run`"))
            {
                throw new InvalidOperationException("Interactive docs rendering showed raw markdown syntax.");
            }

            return snapshot.ContainsText("Docs Smoke Test")
                && snapshot.ContainsText("Aspire CLI")
                && snapshot.ContainsText("aspire run")
                && snapshot.ContainsText("First item")
                && snapshot.ContainsText("Nested item")
                && snapshot.ContainsText("Setting")
                && snapshot.ContainsText("Environment variable")
                && snapshot.ContainsText("Azure:SubscriptionId")
                && snapshot.ContainsText("Azure__SubscriptionId")
                && snapshot.ContainsText("Target Azure subscription");
        }, timeout: TimeSpan.FromSeconds(60), description: "waiting for docs get rendered output");
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
