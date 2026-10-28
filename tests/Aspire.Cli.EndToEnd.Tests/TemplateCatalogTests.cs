// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Hex1b.Input;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

public sealed class TemplateCatalogTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task CatalogFetchingEnforcesPaginationAndTransportBoundaries()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);
        TestDirectoryHelpers.CopyDirectory(
            Path.Combine(repoRoot, "tests", "Aspire.Cli.EndToEnd.Tests", "Fixtures", "CustomTemplate"),
            Path.Combine(workspace.Path, "template"));
        await using var server = await TemplateCatalogServer.StartAsync(workspace.Path, TestContext.Current.CancellationToken);
        // Host networking reaches the fixture on Linux; Desktop uses its host gateway.
        // Trust the generated CA only inside this container, preserving certificate validation.
        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot, strategy, output, variant: CliE2ETestHelpers.DockerfileVariant.Polyglot,
            workspace: workspace, network: "host");
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(
            terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);
        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.RunCommandAsync("cat /etc/ssl/certs/ca-certificates.crt catalog-ca.pem > catalog-trust.pem && export SSL_CERT_FILE=\"$PWD/catalog-trust.pem\"", counter);
        await auto.RunCommandAsync($"curl --fail --silent --cacert catalog-trust.pem '{server.Url}/health' > /dev/null", counter);
        await auto.RunCommandAsync("aspire template source add local --path \"$PWD/template\" --nologo", counter);

        var cases = new (string Path, string Expected, int Requests)[]
        {
            ("/pages", "custom-last", 3),
            ("/unsafe", "invalid or repeated", 1),
            ("/loop", "invalid or repeated", 2),
            ("/limit", "limit of 10 pages", 10),
            ("/redirect", "HTTP 302", 1),
            ("/error", "HTTP 503", 1),
            ("/malformed", "must return a JSON object", 1),
            ("/large", "Could not download the template catalog", 1),
            ("/chunked", "Could not download the template catalog", 1),
            ("/aggregate", "Could not download the template catalog", 2),
            ("/slow-headers", "request timed out", 1),
            ("/slow-body", "request timed out", 1),
            ("/slow-pages", "request timed out", 2)
        };
        foreach (var scenario in cases)
        {
            await auto.RunCommandAsync(
                $"aspire template source add catalog --href '{server.Url}{scenario.Path}?q={{search}}&cliVersion={{cliVersion}}' --nologo", counter);
            await auto.ClearScreenAsync(counter);
            var start = server.Requests.Length;
            await StartSearchAsync(auto);
            var elapsed = Stopwatch.StartNew();
            try
            {
                await auto.WaitUntilTextAsync(scenario.Expected, timeout: TimeSpan.FromSeconds(60));
                await auto.WaitUntilTextAsync("custom-template-fixture", timeout: TimeSpan.FromSeconds(30));
                if (scenario.Path.StartsWith("/slow-", StringComparison.Ordinal))
                {
                    // A reset per-page timeout would take 45s for slow-pages, not the shared 30s.
                    Assert.InRange(elapsed.Elapsed, TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(40));
                }
            }
            finally
            {
                await auto.Ctrl().KeyAsync(Hex1bKey.C);
                await auto.WaitForAnyPromptAsync(counter, timeout: TimeSpan.FromSeconds(15));
            }

            var requests = server.Requests.Skip(start).ToArray();
            Assert.Equal(scenario.Requests, requests.Length);
            Assert.All(requests, request => Assert.Equal("", request.Cookie));
            if (scenario.Path == "/pages")
            {
                Assert.Equal(["/pages", "/pages", "/last"], requests.Select(request => request.Path));
                Assert.Matches(@"^\?q=cUsToM&cliVersion=.+$", requests[0].Query);
                Assert.Equal("?cursor=a%2Bb%26c%3D", requests[1].Query);
                Assert.Equal("?cursor=%7Bsearch%7D", requests[2].Query);
            }
            await auto.RunCommandAsync("aspire template source remove catalog --nologo", counter);
        }

        await auto.RunCommandAsync($"aspire template source add catalog --href '{server.Url}/slow-headers?q={{search}}' --nologo", counter);
        await auto.ClearScreenAsync(counter);
        var requestCount = server.Requests.Length;
        var cancelledCount = server.CancelledRequests.Length;
        await StartSearchAsync(auto);
        await auto.WaitUntilAsync(_ => server.Requests.Length > requestCount, TimeSpan.FromSeconds(15), description: "waiting for cancellable catalog request");
        await auto.Ctrl().KeyAsync(Hex1bKey.C);
        await auto.WaitForAnyPromptAsync(counter, timeout: TimeSpan.FromSeconds(15));
        await auto.WaitUntilAsync(_ => server.CancelledRequests.Length > cancelledCount, TimeSpan.FromSeconds(15), description: "waiting for HTTP request cancellation");
        Assert.Equal("/slow-headers", server.CancelledRequests.Last());
        // Only the exported test logs should be readable by the non-root Linux host runner.
        await auto.RunCommandAsync("cp -R \"$HOME/.aspire/logs\" catalog-logs && chmod -R a+rX catalog-logs", counter);
        var logs = Directory.GetFiles(Path.Combine(workspace.Path, "catalog-logs"), "*.log", SearchOption.AllDirectories);
        Assert.NotEmpty(logs);
        Assert.All(logs, path => Assert.False(File.ReadAllText(path).Contains("cUsToM", StringComparison.Ordinal)));
        Assert.False(Directory.Exists(Path.Combine(workspace.Path, "out")));
    }

    private static async Task StartSearchAsync(Hex1bTerminalAutomator auto)
    {
        await auto.TypeAsync("aspire new --name CatalogDemo --output out --suppress-agent-init --nologo");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Search for more templates", timeout: TimeSpan.FromSeconds(60));
        await auto.KeyAsync(Hex1bKey.End);
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Search templates (leave empty to list all)", timeout: TimeSpan.FromSeconds(30));
        await auto.TypeAsync("cUsToM");
        await auto.EnterAsync();
    }
}
