// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end test that validates the <c>aspire restore</c> command by creating a
/// TypeScript AppHost with two integrations and verifying the generated SDK files
/// are produced in the <c>.modules/</c> directory.
/// </summary>
public sealed class TypeScriptCodegenValidationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RestoreGeneratesSdkFiles()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var installMode = CliE2ETestHelpers.DetectDockerInstallMode(repoRoot);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, installMode, output, variant: CliE2ETestHelpers.DockerfileVariant.Polyglot, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliInDockerAsync(installMode, counter);

        // Step 1: Create a TypeScript AppHost
        await auto.TypeAsync("aspire init --language typescript --non-interactive");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Created apphost.ts", timeout: TimeSpan.FromMinutes(2));
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 2: Add two integrations
        await auto.TypeAsync("aspire add Aspire.Hosting.Redis");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("The package Aspire.Hosting.", timeout: TimeSpan.FromMinutes(2));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire add Aspire.Hosting.SqlServer");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("The package Aspire.Hosting.", timeout: TimeSpan.FromMinutes(2));
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 3: Run aspire restore and verify success
        await auto.TypeAsync("aspire restore");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("SDK code restored successfully", timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 4: Verify generated SDK files exist
        var modulesDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".modules");
        if (!Directory.Exists(modulesDir))
        {
            throw new InvalidOperationException($".modules directory was not created at {modulesDir}");
        }

        var expectedFiles = new[] { "aspire.ts", "base.ts", "transport.ts" };
        foreach (var file in expectedFiles)
        {
            var filePath = Path.Combine(modulesDir, file);
            if (!File.Exists(filePath))
            {
                throw new InvalidOperationException($"Expected generated file not found: {filePath}");
            }

            var content = File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new InvalidOperationException($"Generated file is empty: {filePath}");
            }
        }

        // Verify aspire.ts contains symbols from both integrations
        var aspireTs = File.ReadAllText(Path.Combine(modulesDir, "aspire.ts"));
        if (!aspireTs.Contains("addRedis"))
        {
            throw new InvalidOperationException("aspire.ts does not contain addRedis from Aspire.Hosting.Redis");
        }
        if (!aspireTs.Contains("addSqlServer"))
        {
            throw new InvalidOperationException("aspire.ts does not contain addSqlServer from Aspire.Hosting.SqlServer");
        }

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task UnAwaitedChainsCompileWithAutoResolvePromises()
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

        await auto.TypeAsync("aspire init --language typescript --non-interactive");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Created apphost.ts", timeout: TimeSpan.FromMinutes(2));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire add Aspire.Hosting.PostgreSQL");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("The package Aspire.Hosting.", timeout: TimeSpan.FromMinutes(2));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire restore");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("SDK code restored successfully", timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts");
        var newContent = """
            import { createBuilder } from './.modules/aspire.js';

            const builder = await createBuilder();

            // None of these are awaited — they return PromiseLike wrappers whose
            // underlying RPC calls are tracked by trackPromise().
            const postgres = builder.addPostgres("postgres");
            const db = postgres.addDatabase("db");

            // This chain is also NOT awaited. The withReference(db) call accepts
            // db as a PromiseLike<T> and resolves it internally, but the outer
            // addContainer().withReference() promise itself stays un-awaited.
            // This is the key scenario: build() must call flushPendingPromises()
            // to ensure this chain's RPC calls actually execute.
            builder.addContainer("consumer", "nginx")
                .withReference(db);

            // build() flushes all pending promises before proceeding. If the
            // flush implementation deadlocks (e.g. re-awaiting a promise tracked
            // after flush starts), this line hangs and the test times out.
            await builder.build().run();
            """;

        File.WriteAllText(appHostPath, newContent);

        // Validate that un-awaited chains compile without type errors.
        // withReference(db) should accept PromiseLike<T> from the un-awaited addDatabase().
        await auto.TypeAsync("npx tsc --noEmit --project tsconfig.apphost.json");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptFailFastAsync(counter, TimeSpan.FromMinutes(2));

        // Validate runtime behavior: aspire start launches the apphost, which calls
        // build() and triggers flushPendingPromises(). If the flush deadlocks (e.g. the
        // build promise is re-awaited in a while loop), the process hangs and this test
        // times out. A successful start proves un-awaited chains execute their RPC calls.
        await auto.AspireStartAsync(counter);

        // Verify the un-awaited resources were actually materialized — not silently dropped.
        // If flushPendingPromises skipped the pending chains, these resources wouldn't exist.
        await auto.AssertResourcesExistAsync(counter, "postgres", "db", "consumer");

        await auto.AspireStopAsync(counter);

        await auto.CaptureAspireDiagnosticsAsync(counter, workspace);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
