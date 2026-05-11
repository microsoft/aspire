// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Aspire.TestUtilities;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end smoke test for the Rust polyglot AppHost SDK with the Redis integration.
/// Mirrors the coverage previously provided by
/// <c>.github/workflows/polyglot-validation/test-rust.sh</c>:
/// scaffold a Rust AppHost, add <c>Aspire.Hosting.Redis</c>, replace <c>src/main.rs</c>
/// with a Redis-enabled version, run the AppHost, and verify a Redis container
/// actually materialized via the host Docker socket.
/// </summary>
/// <remarks>
/// Quarantined to preserve the existing <c>continue-on-error: true</c> semantics from
/// the legacy <c>polyglot-validation.yml</c> Rust SDK Validation job: Rust polyglot
/// support is experimental and was non-blocking on PRs. The test runs in the scheduled
/// quarantine workflow so regressions are still tracked.
/// </remarks>
[QuarantinedTest("https://github.com/microsoft/aspire/issues/16701")]
public sealed class RustPolyglotTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task CreateRustAppHostWithRedisIntegration()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot,
            strategy,
            output,
            variant: CliE2ETestHelpers.DockerfileVariant.PolyglotRust,
            mountDockerSocket: true,
            workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        // Rust builds the AppHost from source on `aspire run`, which is significantly slower
        // than Python/Go's interpreted/precompiled paths, so the per-step timeout is bumped.
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(900));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.EnableExperimentalRustSupportAsync(counter);

        await auto.TypeAsync("aspire init --language rust --non-interactive");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Created apphost.rs", timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire add Aspire.Hosting.Redis");
        await auto.EnterAsync();
        await auto.WaitForAspireAddSuccessAsync(counter, TimeSpan.FromMinutes(3));

        // Rust AppHost entry point is src/main.rs; apphost.rs is just a detection marker.
        var mainRsPath = Path.Combine(workspace.WorkspaceRoot.FullName, "src", "main.rs");
        File.WriteAllText(mainRsPath, """
            // Aspire Rust AppHost
            // For more information, see: https://aspire.dev

            #[path = "../.modules/mod.rs"]
            mod aspire;

            use aspire::*;

            fn main() -> Result<(), Box<dyn std::error::Error>> {
                let builder = create_builder(None)?;

                let _redis = builder.add_redis("cache")?;

                let app = builder.build()?;
                app.run(None)?;
                Ok(())
            }
            """);

        await PolyglotRedisAssertions.RunAndAssertRedisContainerAsync(
            auto,
            counter,
            workspace,
            // Rust must `cargo build` the AppHost before DCP can launch resources.
            aspireRunStartupTimeout: TimeSpan.FromMinutes(5));

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
