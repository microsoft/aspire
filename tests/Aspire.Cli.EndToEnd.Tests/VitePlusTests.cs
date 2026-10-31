// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

public sealed class VitePlusTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [CaptureWorkspaceOnFailure]
    public async Task CreateAndRunVitePlusApp(bool useHttps)
    {
        const string projectName = "VitePlusApp";
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        using var workspace = TemporaryWorkspace.Create(output);
        var localChannel = CliE2ETestHelpers.PrepareLocalChannel(repoRoot, strategy,
            ["Aspire.Hosting.JavaScript."]);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.AspireNewCSharpEmptyAppHostAsync(projectName, counter);
        await auto.RunCommandAsync($"cd {projectName}", counter);

        var projectDirectory = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
        if (localChannel is not null)
        {
            CliE2ETestHelpers.WriteLocalChannelSettings(projectDirectory, localChannel.SdkVersion);
        }

        await auto.TypeAsync("aspire add Aspire.Hosting.JavaScript --non-interactive");
        await auto.EnterAsync();
        await auto.WaitForAspireAddCompletionAsync(counter, TimeSpan.FromMinutes(2));

        WriteVitePlusApp(projectDirectory);

        var appHostPath = Path.Combine(projectDirectory, "apphost.cs");
        var appHost = File.ReadAllText(appHostPath);
        const string builderStatement = "var builder = DistributedApplication.CreateBuilder(args);";
        Assert.Contains(builderStatement, appHost);
        var httpsConfiguration = useHttps ? "\n    .WithHttpsDeveloperCertificate()" : string.Empty;
        File.WriteAllText(appHostPath, appHost.Replace(builderStatement, $$"""
            {{builderStatement}}
            #pragma warning disable ASPIRECERTIFICATES001
            builder.AddViteApp("frontend", "./frontend")
                .WithViteConfig("./vite.custom.config.ts"){{httpsConfiguration}};
            #pragma warning restore ASPIRECERTIFICATES001
            """, StringComparison.Ordinal));

        await auto.RunCommandAsync("unset ASPIRE_PLAYGROUND", counter);
        await auto.AspireStartAsync(counter);

        try
        {
            await auto.RunCommandAsync(
                $"bash verify-vite-plus.sh {(useHttps ? "https" : "http")}",
                counter,
                TimeSpan.FromMinutes(4));
        }
        catch (InvalidOperationException)
        {
            // A failed command leaves the counter on its error prompt. Advance it so
            // TerminalRun can still capture diagnostics from the next shell prompt.
            counter.Increment();
            throw;
        }
    }

    private static void WriteVitePlusApp(string projectDirectory)
    {
        const string vitePlusVersion = "1.4.0";
        var frontendDirectory = Path.Combine(projectDirectory, "frontend");
        Directory.CreateDirectory(frontendDirectory);

        // Use the project-local CLI, not a global vp installation. Aspire must install
        // the dependency and launch it through the package script as it does for Vite.
        File.WriteAllText(Path.Combine(frontendDirectory, "package.json"), $$"""
            {
              "name": "aspire-vite-plus-e2e",
              "private": true,
              "type": "module",
              "scripts": {
                "dev": "vp dev",
                "build": "vp build"
              },
              "devDependencies": {
                "vite-plus": "{{vitePlusVersion}}"
              },
              "overrides": {
                "vite": "npm:@voidzero-dev/vite-plus-core@{{vitePlusVersion}}"
              }
            }
            """);

        File.WriteAllText(Path.Combine(frontendDirectory, "vite.custom.config.ts"), """
            import { defineConfig } from 'vite-plus';

            export default defineConfig({
              server: {
                strictPort: true,
              },
              plugins: [{
                name: 'aspire-vite-plus-e2e',
                transformIndexHtml(html) {
                  return html.replace('__VITE_PLUS_MESSAGE__', 'Vite+ running under Aspire');
                },
              }],
            });
            """);

        File.WriteAllText(Path.Combine(frontendDirectory, "index.html"), """
            <!doctype html>
            <html>
              <head><title>Aspire Vite+ E2E</title></head>
              <body>
                <h1>__VITE_PLUS_MESSAGE__</h1>
                <script type="module" src="/main.ts"></script>
              </body>
            </html>
            """);

        File.WriteAllText(Path.Combine(frontendDirectory, "main.ts"), """
            const message: string = 'Vite+ TypeScript module';
            document.body.dataset.message = message;
            """);

        File.WriteAllText(Path.Combine(projectDirectory, "verify-vite-plus.sh"), """
            #!/usr/bin/env bash
            set -euo pipefail

            cleanup() {
                local exit_code=$?
                if [ "$exit_code" -ne 0 ]; then
                    aspire describe --format json || true
                    aspire logs frontend --non-interactive || true
                fi
                aspire stop --non-interactive || exit_code=1
                exit "$exit_code"
            }
            trap cleanup EXIT

            aspire wait frontend --status up --timeout 120
            aspire describe frontend --format json > frontend.json
            # Describe emits {"resources":[{"urls":[{"url":"http://localhost:12345",...}],...}]}.
            # Select the public proxy URL rather than the internal target endpoint.
            url=$(jq -er --arg scheme "$1://" \
                '[.resources[0].urls[] | select(.isInternal != true and (.url | startswith($scheme)))][0].url' \
                frontend.json)

            # The HTTPS case uses Aspire's development certificate, which is not trusted
            # by curl in this disposable container. The URL scheme still requires TLS.
            curl --insecure --fail --silent --show-error \
                --connect-timeout 2 --max-time 5 \
                --retry 30 --retry-delay 1 --retry-max-time 120 --retry-all-errors \
                "$url" -o frontend.html
            grep -Fq '<h1>Vite+ running under Aspire</h1>' frontend.html
            grep -Fq '/@vite/client' frontend.html

            curl --insecure --fail --silent --show-error \
                --connect-timeout 2 --max-time 10 "$url/main.ts" -o main.js
            grep -Fq 'const message = "Vite+ TypeScript module"' main.js
            """);
    }
}
