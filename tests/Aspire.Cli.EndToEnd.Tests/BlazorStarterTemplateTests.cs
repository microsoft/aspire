// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for the default Blazor starter template created by <c>aspire new</c>.
/// Validates the full CLI workflow from project creation through detached startup,
/// frontend/API verification, and dashboard telemetry (traces, structured logs, metrics).
/// </summary>
public sealed class BlazorStarterTemplateTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task CreateStartAndVerifyBlazorStarterProject()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var installMode = CliE2ETestHelpers.DetectDockerInstallMode(repoRoot);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, installMode, output, mountDockerSocket: true, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliInDockerAsync(installMode, counter);

        await auto.AspireNewAsync("AspireBlazorStarterApp", counter, useRedisCache: false);

        await auto.TypeAsync("cd AspireBlazorStarterApp");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.AspireStartAsync(counter);

        await auto.TypeAsync("aspire wait webfrontend --status up --timeout 300");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("is up (running).", timeout: TimeSpan.FromMinutes(6));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire wait apiservice --status up --timeout 300");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("is up (running).", timeout: TimeSpan.FromMinutes(6));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire describe webfrontend --format json > webfrontend.json");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("WEB_URL=$(grep -oE 'https?://localhost:[0-9]+' webfrontend.json | head -1); echo \"WEB_URL=$WEB_URL\"");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("WEB_URL=http", timeout: TimeSpan.FromSeconds(15));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("curl -ksSL -o webfrontend.html -w 'webfrontend-http-%{http_code}' \"$WEB_URL\" || echo 'webfrontend-http-failed'");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("webfrontend-http-200", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("curl -ksSL -o weather.html -w 'weather-http-%{http_code}' \"$WEB_URL/weather\" || echo 'weather-http-failed'");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("weather-http-200", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("grep -q 'Weather' webfrontend.html && grep -q 'Date' weather.html && echo 'webfrontend-content-ok'");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("webfrontend-content-ok", timeout: TimeSpan.FromSeconds(15));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("API_URL=$(aspire describe apiservice --format json | grep -oE 'https?://localhost:[0-9]+' | head -1); echo \"API_URL=$API_URL\"");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("API_URL=http", timeout: TimeSpan.FromSeconds(15));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("curl -ksSL \"$API_URL/weatherforecast\" | grep -q 'temperatureC' && echo 'apiservice-weather-ok'");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("apiservice-weather-ok", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        // Verify dashboard telemetry: traces should show webfrontend and apiservice
        // Use --format json and grep for resource names in the OTLP trace output
        await auto.TypeAsync("aspire otel traces --format json > traces.json 2>&1 && echo 'otel-traces-ok' || echo 'otel-traces-failed'");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("otel-traces-ok", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("grep -q 'webfrontend' traces.json && echo 'traces-webfrontend-ok' || echo 'traces-webfrontend-missing'");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("traces-webfrontend-ok", timeout: TimeSpan.FromSeconds(15));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("grep -q 'apiservice' traces.json && echo 'traces-apiservice-ok' || echo 'traces-apiservice-missing'");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("traces-apiservice-ok", timeout: TimeSpan.FromSeconds(15));
        await auto.WaitForSuccessPromptAsync(counter);

        // Verify structured logs exist for webfrontend
        await auto.TypeAsync("aspire otel logs webfrontend --format json > logs.json 2>&1 && echo 'otel-logs-ok' || echo 'otel-logs-failed'");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("otel-logs-ok", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("grep -q 'webfrontend' logs.json && echo 'logs-webfrontend-ok' || echo 'logs-webfrontend-missing'");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("logs-webfrontend-ok", timeout: TimeSpan.FromSeconds(15));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.AspireStopAsync(counter);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
