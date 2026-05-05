// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Hex1b.Input;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end test for the JavaScript workspace (npm/yarn/pnpm/bun monorepo) publish flow. Creates
/// a TypeScript AppHost in a workspace root that contains a single Node.js workspace member, calls
/// <c>withWorkspaceRoot</c>, and verifies that <c>aspire publish</c> generates a Dockerfile whose
/// build context is the workspace root and which copies workspace-level manifests before installing.
/// </summary>
public sealed class JavaScriptWorkspacePublishTests(ITestOutputHelper output)
{
    [Fact]
    public async Task PublishWithWorkspaceRoot_NpmMonorepo_GeneratesWorkspaceDockerfile()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        using var workspace = TemporaryWorkspace.Create(output);

        // Lay out a tiny npm workspace before launching the CLI:
        //
        //   <workspace>/
        //     package.json          (root, declares "packages/*" workspace)
        //     package-lock.json     (lockfile required by WithWorkspaceRoot)
        //     apphost/              (AppHost lives here; aspire init runs from this dir)
        //     packages/
        //       web/
        //         package.json      (declares name "@example/web")
        //         server.js
        var root = workspace.WorkspaceRoot.FullName;
        File.WriteAllText(
            Path.Combine(root, "package.json"),
            "{\"name\":\"workspace-root\",\"private\":true,\"workspaces\":[\"packages/*\"]}");
        File.WriteAllText(Path.Combine(root, "package-lock.json"), "{}");

        var webDir = Path.Combine(root, "packages", "web");
        Directory.CreateDirectory(webDir);
        File.WriteAllText(
            Path.Combine(webDir, "package.json"),
            "{\"name\":\"@example/web\",\"version\":\"1.0.0\",\"scripts\":{\"start\":\"node server.js\"}}");
        File.WriteAllText(
            Path.Combine(webDir, "server.js"),
            "require('http').createServer((_,r)=>r.end('ok')).listen(3000);");

        var appHostDir = Path.Combine(root, "apphost");
        Directory.CreateDirectory(appHostDir);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, variant: CliE2ETestHelpers.DockerfileVariant.DotNet, mountDockerSocket: false, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.EnablePolyglotSupportAsync(counter);

        // cd into the apphost subdir so aspire init only writes apphost files there, leaving the
        // workspace root cleanly intact for use as the docker build context.
        await auto.TypeAsync("cd apphost");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire init");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Which language would you like to use?", timeout: TimeSpan.FromSeconds(30));
        await auto.KeyAsync(Hex1bKey.DownArrow);
        await auto.WaitUntilTextAsync("> TypeScript (Node.js)", timeout: TimeSpan.FromSeconds(5));
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Created apphost.ts", timeout: TimeSpan.FromMinutes(2));
        await auto.DeclineAgentInitPromptAsync(counter);

        await auto.TypeAsync("aspire add Aspire.Hosting.JavaScript");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(180));

        await auto.TypeAsync("aspire add Aspire.Hosting.Docker");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(180));

        await auto.TypeAsync("aspire restore");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("SDK code restored successfully", timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        var appHostPath = Path.Combine(appHostDir, "apphost.ts");
        File.WriteAllText(appHostPath, """
            import { createBuilder } from './.modules/aspire.js';

            const builder = await createBuilder();
            await builder.addDockerComposeEnvironment('compose');

            await builder.addNodeApp('web', '../packages/web', 'server.js')
                .withWorkspaceRoot('..')
                .withExternalHttpEndpoints();

            await builder.build().run();
            """);

        await auto.TypeAsync("unset ASPIRE_PLAYGROUND");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire publish -o artifacts --non-interactive");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, timeout: TimeSpan.FromMinutes(5));

        // Locate the generated Dockerfile (its name is templated). Show its content first for
        // diagnostics, then assert the workspace-style COPY layout.
        await auto.TypeAsync("find artifacts -name 'web*.Dockerfile' -print");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("cat artifacts/web*.Dockerfile");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Workspace-root manifests must be copied into /app, not the app subdir. Sibling member
        // package.json must also be copied so npm install can resolve workspace:* refs.
        await auto.TypeAsync("grep -F 'COPY package.json ./package.json' artifacts/web*.Dockerfile");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("grep -F 'COPY package-lock.json ./package-lock.json' artifacts/web*.Dockerfile");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("grep -F 'COPY packages/web/package.json ./packages/web/package.json' artifacts/web*.Dockerfile");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("grep -F 'WORKDIR /app/packages/web' artifacts/web*.Dockerfile");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // The compose service must build from the workspace root (one level up from apphost), not
        // from the app subdirectory.
        await auto.TypeAsync("grep -F 'context: ../' artifacts/docker-compose.yaml");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();
        await pendingRun;
    }
}
