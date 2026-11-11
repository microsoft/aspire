// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

public sealed class GoStarterReadinessTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [CaptureWorkspaceOnFailure]
    public async Task StarterShipsTidyApiAndServesRequests(bool useRedisCache, bool prebuildApplications)
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        using var workspace = TemporaryWorkspace.Create(output);

        // DCP publishes Redis on the Docker host. The API must reach that host's loopback ports.
        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot, strategy, output,
            variant: CliE2ETestHelpers.DockerfileVariant.PolyglotGo,
            mountDockerSocket: useRedisCache, workspace: workspace, network: "host");
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        if (useRedisCache)
        {
            await auto.AspireNewAsync("GoReady", counter, template: AspireTemplate.GoStarter);
        }
        else
        {
            await auto.RunCommandAsync(
                "aspire new aspire-go-starter --name GoReady --output GoReady --use-redis-cache false --localhost-tld false --non-interactive --suppress-agent-init",
                counter,
                TimeSpan.FromMinutes(3));
        }

        var projectRoot = Path.Combine(workspace.WorkspaceRoot.FullName, "GoReady");
        GitIgnoreAssertions.AssertContainsEntry(projectRoot, ".aspire/");
        Assert.True(File.Exists(Path.Combine(projectRoot, "api", "go.sum")));

        await auto.RunCommandAsync("cd GoReady", counter);
        if (prebuildApplications)
        {
            // Prove both generated applications build without Aspire repairing dependencies.
            await auto.RunCommandAsync("(cd api && go build -mod=readonly -o /tmp/go-ready-api . && go mod tidy -diff)", counter, TimeSpan.FromMinutes(3));
            await auto.RunCommandAsync("npm --prefix frontend ci && npm --prefix frontend run build", counter, TimeSpan.FromMinutes(3));
        }
        try
        {
            await auto.AspireStartAsync(counter, startTimeout: TimeSpan.FromMinutes(4));
            await auto.RunCommandAsync("aspire wait app --status up --timeout 120", counter, TimeSpan.FromMinutes(3));
            await auto.RunCommandAsync("aspire wait frontend --status up --timeout 120", counter, TimeSpan.FromMinutes(3));
            if (useRedisCache)
            {
                await auto.RunCommandAsync("aspire wait cache --status up --timeout 120", counter, TimeSpan.FromMinutes(3));
            }
            await auto.RunCommandAsync("aspire describe frontend --format json > frontend.json", counter);
            // Describe emits { resources: [{ urls: [{ url: \"http://...\" }] }] }. Use Node,
            // already provided by the polyglot image, rather than assuming jq is installed.
            await auto.RunCommandAsync(
                """FRONTEND_URL=$(node -e 'const fs = require("node:fs"); const data = JSON.parse(fs.readFileSync("frontend.json", "utf8")); const url = data.resources[0].urls.find(u => u.url.startsWith("http://")); if (!url) throw new Error("Missing HTTP frontend URL"); process.stdout.write(url.url.replace(/\/$/, ""));') && curl -fsS --retry 10 --retry-all-errors --retry-delay 1 "$FRONTEND_URL/" > index.html && grep -q '<div id="root"></div>' index.html && curl -fsS "$FRONTEND_URL/api/weatherforecast" > weather.json && node -e 'const assert = require("node:assert/strict"); const weather = require("./weather.json"); assert.equal(weather.length, 5); for (const forecast of weather) { assert.equal(typeof forecast.temperatureC, "number"); assert.equal(typeof forecast.summary, "string"); }'""",
                counter,
                TimeSpan.FromMinutes(2));
            await auto.RunCommandAsync(
                """found=0; for i in $(seq 1 20); do aspire otel spans --format json --limit 100 > spans.json && aspire otel logs --format json --limit 100 > otel-logs.json && node -e 'const assert = require("node:assert/strict"); const spans = require("./spans.json"); const logs = require("./otel-logs.json"); assert.ok(spans.some(span => span.source === "app" && span.attributes?.["url.path"] === "/api/weatherforecast")); assert.ok(logs.some(log => log.resourceName === "app" && log.message.includes("api listening")));' && found=1 && break; sleep 1; done; test "$found" = 1""",
                counter,
                TimeSpan.FromMinutes(2));
            if (useRedisCache)
            {
                await auto.RunCommandAsync(
                    """found=0; for i in $(seq 1 20); do aspire otel spans --format json --limit 100 > spans.json && node -e 'const assert = require("node:assert/strict"); const spans = require("./spans.json"); assert.ok(spans.some(span => span.source === "app" && span.attributes?.["db.system"] === "redis"));' && found=1 && break; sleep 1; done; test "$found" = 1""",
                    counter,
                    TimeSpan.FromMinutes(2));
            }
        }
        finally
        {
            await auto.AspireStopAsync(counter);
        }
    }
}
