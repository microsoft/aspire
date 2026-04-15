// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Aspire.TestUtilities;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end test for JavaScript publish methods. Uses checked-in fixture apps for all four
/// publish patterns (StaticWebsite, NodeServer, NpmScript, AddNextJsApp), deploys them in a
/// single apphost via aspire deploy, and verifies all Docker images build successfully.
/// </summary>
public sealed class JavaScriptPublishTests(ITestOutputHelper output)
{
    private static readonly string s_fixturesDir = Path.Combine(
        CliE2ETestHelpers.GetRepoRoot(),
        "tests", "Aspire.Cli.EndToEnd.Tests", "Fixtures", "JsPublish");

    [Fact]
    [CaptureWorkspaceOnFailure]
    [QuarantinedTest("https://github.com/microsoft/aspire/issues/16188")]
    public async Task AllPublishMethodsBuildDockerImages()
    {
        using var workspace = TemporaryWorkspace.Create(output);

        var prNumber = CliE2ETestHelpers.GetRequiredPrNumber();
        var commitSha = CliE2ETestHelpers.GetRequiredCommitSha();
        var isCI = CliE2ETestHelpers.IsRunningInCI;
        using var terminal = CliE2ETestHelpers.CreateTestTerminal();

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareEnvironmentAsync(workspace, counter);

        if (isCI)
        {
            await auto.InstallAspireCliFromPullRequestAsync(prNumber, counter);
            await auto.SourceAspireCliEnvironmentAsync(counter);
            await auto.VerifyAspireCliVersionAsync(commitSha, counter);
        }

        // Create TS AppHost and add packages
        await auto.TypeAsync("aspire init");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Which language would you like to use?", timeout: TimeSpan.FromSeconds(30));
        await auto.DownAsync();
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

        // Copy checked-in fixture apps and write the apphost
        CopyFixtures(workspace);
        WriteAppHost(workspace);

        // Deploy
        await auto.TypeAsync("unset ASPIRE_PLAYGROUND");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire deploy --non-interactive");
        await auto.EnterAsync();
        await auto.WaitForPipelineSuccessAsync(timeout: TimeSpan.FromMinutes(5));
        await auto.WaitForSuccessPromptAsync(counter);

        // Wait for services and verify — verify.sh captures diagnostics first, then asserts
        await auto.TypeAsync("bash verify.sh");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("ALL_OK", timeout: TimeSpan.FromSeconds(90));
        await auto.WaitForSuccessPromptAsync(counter);

        // Clean up
        await auto.TypeAsync("docker ps -q --filter label=com.docker.compose.project | xargs -r docker rm -f 2>/dev/null || true");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

        await auto.TypeAsync("exit");
        await auto.EnterAsync();
        await pendingRun;
    }

    private static void WriteAppHost(TemporaryWorkspace workspace)
    {
        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts");
        File.WriteAllText(appHostPath, """
            import { createBuilder } from './.modules/aspire.js';

            const builder = await createBuilder();
            await builder.addDockerComposeEnvironment('compose');

            const api = await builder.addNodeApp('api', './api', 'server.js')
                .withHttpEndpoint({ port: 3001, env: 'PORT' })
                .withExternalHttpEndpoints();

            await builder.addViteApp('staticsite', './staticsite')
                .publishAsStaticWebsite({ apiPath: '/api', apiTarget: api })
                .withExternalHttpEndpoints();

            await builder.addViteApp('nodeserver', './nodeserver')
                .publishAsNodeServer('build/server.js', { outputPath: 'build' })
                .withExternalHttpEndpoints();

            await builder.addViteApp('npmscript', './npmscript')
                .publishAsNpmScript({ startScriptName: 'start' })
                .withExternalHttpEndpoints();

            await builder.addNextJsApp('nextjs', './nextjs')
                .withExternalHttpEndpoints();

            await builder.build().run();
            """);
    }

    private static void CopyFixtures(TemporaryWorkspace workspace)
    {
        // Copy root-level files (e.g. verify.sh)
        foreach (var file in Directory.GetFiles(s_fixturesDir))
        {
            File.Copy(file, Path.Combine(workspace.WorkspaceRoot.FullName, Path.GetFileName(file)));
        }

        // Copy subdirectories (app fixtures)
        foreach (var fixtureDir in Directory.GetDirectories(s_fixturesDir))
        {
            var targetDir = Path.Combine(workspace.WorkspaceRoot.FullName, Path.GetFileName(fixtureDir));
            CopyDirectory(fixtureDir, targetDir);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var dir in Directory.GetDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }
}
