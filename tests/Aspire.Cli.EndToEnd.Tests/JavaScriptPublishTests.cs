// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
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
    public async Task AllPublishMethodsBuildDockerImages()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        using var workspace = TemporaryWorkspace.Create(output);
        var localChannel = CliE2ETestHelpers.PrepareLocalChannel(repoRoot, strategy,
            ["Aspire.Hosting.CodeGeneration.TypeScript.", "Aspire.Hosting.JavaScript.", "Aspire.Hosting.Docker."]);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        // Create TS AppHost and add packages
        await auto.TypeAsync("aspire init");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Which language would you like to use?", timeout: TimeSpan.FromSeconds(30));
        await auto.DownAsync();
        await auto.WaitUntilTextAsync("> TypeScript (Node.js)", timeout: TimeSpan.FromSeconds(5));
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Created apphost.ts", timeout: TimeSpan.FromMinutes(2));
        await auto.DeclineAgentInitPromptAsync(counter);

        if (localChannel is not null)
        {
            CliE2ETestHelpers.WriteLocalChannelSettings(workspace.WorkspaceRoot.FullName, localChannel.SdkVersion);
        }

        await auto.TypeAsync("aspire add Aspire.Hosting.JavaScript");
        await auto.EnterAsync();
        await auto.WaitForAspireAddCompletionAsync(counter, TimeSpan.FromSeconds(180));

        await auto.TypeAsync("aspire add Aspire.Hosting.Docker");
        await auto.EnterAsync();
        await auto.WaitForAspireAddCompletionAsync(counter, TimeSpan.FromSeconds(180));

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

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task JavaScriptHostingApisRunFromTypeScriptAppHost()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        using var workspace = TemporaryWorkspace.Create(output);
        var localChannel = CliE2ETestHelpers.PrepareLocalChannel(repoRoot, strategy,
            ["Aspire.Hosting.CodeGeneration.TypeScript.", "Aspire.Hosting.JavaScript."]);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, variant: CliE2ETestHelpers.DockerfileVariant.Polyglot, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        var testBodyFailed = false;

        try
        {
            await auto.PrepareDockerEnvironmentAsync(counter, workspace);
            await auto.InstallAspireCliAsync(strategy, counter);

            await auto.RunCommandFailFastAsync("aspire init --language typescript --non-interactive", counter, TimeSpan.FromMinutes(2));

            if (localChannel is not null)
            {
                CliE2ETestHelpers.WriteLocalChannelSettings(workspace.WorkspaceRoot.FullName, localChannel.SdkVersion);
            }

            await auto.TypeAsync("aspire add Aspire.Hosting.JavaScript");
            await auto.EnterAsync();
            await auto.WaitForAspireAddCompletionAsync(counter, TimeSpan.FromMinutes(2));

            WriteRuntimeFixtures(workspace);
            WriteRuntimeAppHost(workspace);
            WriteRuntimeVerificationScript(workspace);

            await auto.RunCommandFailFastAsync("unset ASPIRE_PLAYGROUND", counter);

            await auto.RunCommandFailFastAsync("aspire run > aspire-run.log 2>&1 & echo $! > aspire-run.pid", counter);
            await auto.RunCommandFailFastAsync("bash verify-runtime.sh", counter, TimeSpan.FromMinutes(2));
        }
        catch
        {
            testBodyFailed = true;
            throw;
        }
        finally
        {
            try
            {
                await auto.RunCommandAsync("if [ -f aspire-run.pid ]; then kill \"$(cat aspire-run.pid)\" 2>/dev/null || true; wait \"$(cat aspire-run.pid)\" 2>/dev/null || true; fi", counter, TimeSpan.FromMinutes(1));
            }
            catch
            {
                // Best effort. A failure before aspire run writes its PID leaves no process to stop.
            }

            try
            {
                await auto.CaptureAspireDiagnosticsAsync(counter, workspace);
            }
            catch
            {
                // Best effort diagnostics capture.
            }

            try
            {
                await auto.TypeAsync("exit");
                await auto.EnterAsync();
                await pendingRun;
            }
            catch
            {
                if (!testBodyFailed)
                {
                    throw;
                }
            }
        }
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
                .withHttpEndpoint({ name: 'http', targetPort: 5000 })
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

    private static void WriteRuntimeAppHost(TemporaryWorkspace workspace)
    {
        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts");
        File.WriteAllText(appHostPath, $$"""
            import { createBuilder } from './.modules/aspire.js';

            const builder = await createBuilder();

            const nodeNpm = await builder.addNodeApp('node-npm', './node-npm', 'server.js');
            await nodeNpm.withNpm({ install: false });
            await nodeNpm.withRunScript('start');
            await nodeNpm.withHttpEndpoint({ name: 'http', env: 'PORT' });

            const javaScriptPnpm = await builder.addJavaScriptApp('javascript-pnpm', './javascript-pnpm', { runScriptName: 'dev' });
            await javaScriptPnpm.withPnpm({ install: false });

            const viteYarn = await builder.addViteApp('vite-yarn', './vite-yarn', { runScriptName: 'dev' });
            await viteYarn.withYarn({ install: false });

            const nextBun = await builder.addNextJsApp('next-bun', './next-bun', { runScriptName: 'dev' });
            await nextBun.disableBuildValidation();
            await nextBun.withBun({ install: false });

            await builder.build().run();
            """);
    }

    private static void WriteRuntimeFixtures(TemporaryWorkspace workspace)
    {
        WriteRuntimeApp(workspace, "node-npm", "start");
        WriteRuntimeApp(workspace, "javascript-pnpm", "dev");
        WriteRuntimeApp(workspace, "vite-yarn", "dev", packageManager: "yarn@4.14.1");
        WriteRuntimeApp(workspace, "next-bun", "dev");
    }

    private static void WriteRuntimeApp(TemporaryWorkspace workspace, string appName, string scriptName, string? packageManager = null)
    {
        var appDir = Path.Combine(workspace.WorkspaceRoot.FullName, appName);
        Directory.CreateDirectory(appDir);

        var packageManagerProperty = packageManager is not null ? $"""
              "packageManager": "{packageManager}",
            """ : string.Empty;

        File.WriteAllText(Path.Combine(appDir, "package.json"), $$"""
            {
              "name": "{{appName}}",
              "private": true,
            {{packageManagerProperty}}
              "scripts": {
                "{{scriptName}}": "node server.js"
              }
            }
            """);

        if (packageManager?.StartsWith("yarn@", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Yarn 2+ requires a project-local lockfile with the workspace package entry before
            // it will run scripts, even when Aspire is configured not to run package install.
            File.WriteAllText(Path.Combine(appDir, "yarn.lock"), $$"""
                # This file is generated by running "yarn install" inside your project.
                # Manual changes might be lost - proceed with caution!

                __metadata:
                  version: 9
                  cacheKey: 10c0

                "{{appName}}@workspace:.":
                  version: 0.0.0-use.local
                  resolution: "{{appName}}@workspace:."
                  languageName: unknown
                  linkType: soft

                """);
        }

        File.WriteAllText(Path.Combine(appDir, "server.js"), $$"""
            const http = require('http');
            const fs = require('fs');

            const portArgumentIndex = process.argv.findIndex(arg => arg === '--port' || arg === '-p');
            const fallbackPorts = {
              'node-npm': 3001,
              'javascript-pnpm': 3002,
              'vite-yarn': 3003,
              'next-bun': 3004,
            };
            const port = process.env.PORT || (portArgumentIndex >= 0 ? process.argv[portArgumentIndex + 1] : undefined) || fallbackPorts['{{appName}}'];

            http.createServer((req, res) => {
              res.writeHead(200, { 'Content-Type': 'application/json' });
              res.end(JSON.stringify({ app: '{{appName}}', ok: true }));
            }).listen(port, '0.0.0.0', () => {
              fs.writeFileSync('ready-port', port.toString());
              console.log('{{appName}} listening on ' + port);
            });
            """);
    }

    private static void WriteRuntimeVerificationScript(TemporaryWorkspace workspace)
    {
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "verify-runtime.sh"), $$"""
            #!/usr/bin/env bash
            set -euo pipefail

            check_endpoint() {
                local name="$1"
                local port_file="${name}/ready-port"

                for i in $(seq 1 30); do
                    if [ ! -f "${port_file}" ]; then
                        sleep 1
                        continue
                    fi

                    local port
                    port="$(cat "${port_file}")"
                    if curl -sf --max-time 5 "http://localhost:${port}/" | grep -q "\"app\":\"${name}\""; then
                        echo "${name}_OK"
                        return 0
                    fi

                    sleep 1
                done

                echo "${name}_FAIL"
                echo "===== aspire-run.log ====="
                cat aspire-run.log || true
                echo "===== end aspire-run.log ====="
                echo "===== runtime fixture files ====="
                for file in package.json node-npm/package.json node-npm/ready-port javascript-pnpm/package.json javascript-pnpm/ready-port vite-yarn/package.json vite-yarn/yarn.lock vite-yarn/ready-port next-bun/package.json next-bun/ready-port; do
                    if [ -f "$file" ]; then
                        echo "--- $file"
                        cat "$file"
                    fi
                done
                echo "===== end runtime fixture files ====="
                return 1
            }

            check_endpoint "node-npm"
            check_endpoint "javascript-pnpm"
            check_endpoint "vite-yarn"
            check_endpoint "next-bun"

            echo "RUNTIME_ALL_OK"
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
            TestDirectoryHelpers.CopyDirectory(fixtureDir, targetDir);
        }
    }
}
