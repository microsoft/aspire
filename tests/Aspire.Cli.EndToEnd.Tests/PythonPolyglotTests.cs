// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end smoke test for the Python polyglot AppHost SDK with the Redis integration.
/// Mirrors the coverage previously provided by
/// <c>.github/workflows/polyglot-validation/test-python.sh</c>:
/// scaffold a Python AppHost, add <c>Aspire.Hosting.Redis</c>, edit <c>apphost.py</c>
/// to launch a Redis cache, run the AppHost, and verify a Redis container actually
/// materialized via the host Docker socket.
/// </summary>
public sealed class PythonPolyglotTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task CreatePythonAppHostWithRedisIntegration()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot,
            strategy,
            output,
            variant: CliE2ETestHelpers.DockerfileVariant.PolyglotPython,
            mountDockerSocket: true,
            workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.EnableExperimentalPythonSupportAsync(counter);

        await auto.TypeAsync("aspire init --language python --non-interactive");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Created apphost.py", timeout: TimeSpan.FromMinutes(2));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire add Aspire.Hosting.Redis");
        await auto.EnterAsync();
        await auto.WaitForAspireAddSuccessAsync(counter, TimeSpan.FromMinutes(2));

        // Overwrite apphost.py with a version that registers the Redis cache before run().
        // We write the entire file rather than sed-editing the template so the test stays
        // robust against future template formatting changes.
        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.py");
        File.WriteAllText(appHostPath, """
            # Aspire Python AppHost
            # For more information, see: https://aspire.dev

            from aspire_app import create_builder

            with create_builder() as builder:
                redis = builder.add_redis("cache")
                builder.run()
            """);

        await PolyglotRedisAssertions.RunAndAssertRedisContainerAsync(auto, counter, workspace);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
