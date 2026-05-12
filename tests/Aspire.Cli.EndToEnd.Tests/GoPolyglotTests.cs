// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end smoke test for the Go polyglot AppHost SDK with the Redis integration.
/// Mirrors the coverage previously provided by
/// <c>.github/workflows/polyglot-validation/test-go.sh</c>:
/// scaffold a Go AppHost, add <c>Aspire.Hosting.Redis</c>, replace <c>apphost.go</c>
/// with a Redis-enabled version, run the AppHost, and verify a Redis container
/// actually materialized via the host Docker socket.
/// </summary>
public sealed class GoPolyglotTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task CreateGoAppHostWithRedisIntegration()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot,
            strategy,
            output,
            variant: CliE2ETestHelpers.DockerfileVariant.PolyglotGo,
            mountDockerSocket: true,
            workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.EnableExperimentalGoSupportAsync(counter);

        await auto.TypeAsync("aspire init --language go --non-interactive");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Created apphost.go", timeout: TimeSpan.FromMinutes(2));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire add Aspire.Hosting.Redis");
        await auto.EnterAsync();
        await auto.WaitForAspireAddSuccessAsync(counter, TimeSpan.FromMinutes(2));

        // Overwrite apphost.go with a version that registers the Redis cache. The
        // current Go scaffold uses tab indentation; the Go formatter is lenient about
        // mixed indentation for `go build`, so we emit the modified file inline.
        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.go");
        File.WriteAllText(appHostPath, """
            // Aspire Go AppHost
            // For more information, see: https://aspire.dev

            package main

            import (
            	"log"
            	"apphost/modules/aspire"
            )

            func main() {
            	builder, err := aspire.CreateBuilder()
            	if err != nil {
            		log.Fatal(aspire.FormatError(err))
            	}

            	_ = builder.AddRedis("cache").WithImageRegistry("netaspireci.azurecr.io")
            	if err := builder.Err(); err != nil {
            		log.Fatal(aspire.FormatError(err))
            	}

            	app, err := builder.Build()
            	if err != nil {
            		log.Fatal(aspire.FormatError(err))
            	}
            	if err := app.Run(); err != nil {
            		log.Fatal(aspire.FormatError(err))
            	}
            }
            """);

        await PolyglotRedisAssertions.RunAndAssertRedisContainerAsync(auto, counter, workspace);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
