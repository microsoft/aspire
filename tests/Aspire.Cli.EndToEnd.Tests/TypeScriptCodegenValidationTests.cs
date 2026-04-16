// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests that validate TypeScript SDK generation for guest AppHosts,
/// including refreshing the generated SDK after adding integrations.
/// </summary>
public sealed class TypeScriptCodegenValidationTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task RestoreRefreshesGeneratedSdkAfterAddingIntegration()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var workspace = TemporaryWorkspace.Create(output);

        // Use the DotNet container variant even for the TypeScript AppHost so local LocalHive
        // repros still have dotnet available for package/add flows while remaining outside the repo,
        // which keeps the AppHost on the PrebuiltAppHostServer path instead of in-repo dev mode.
        using var terminal = CreateDockerTestTerminal(
            repoRoot,
            workspace,
            variant: CliE2ETestHelpers.DockerfileVariant.DotNet,
            mountDockerSocket: false,
            out var strategy,
            out var installMode);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await InstallAspireCliAsync(auto, counter, strategy, installMode);

        // Step 1: Create a TypeScript AppHost, restore it, and verify the baseline generated SDK.
        await auto.TypeAsync("aspire init --language typescript --non-interactive");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Created apphost.ts", timeout: TimeSpan.FromMinutes(2));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire restore");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("SDK code restored successfully", timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        var modulesDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".modules");
        if (!Directory.Exists(modulesDir))
        {
            throw new InvalidOperationException($".modules directory was not created at {modulesDir}");
        }

        var aspireModulePath = Path.Combine(modulesDir, "aspire.ts");
        if (!File.Exists(aspireModulePath))
        {
            throw new InvalidOperationException($"Expected generated file not found: {aspireModulePath}");
        }

        var initialAspireTs = File.ReadAllText(aspireModulePath);
        if (!initialAspireTs.Contains("createBuilder"))
        {
            throw new InvalidOperationException("aspire.ts did not contain the expected base exports before adding a new integration.");
        }

        if (Regex.IsMatch(initialAspireTs, @"(?m)^\s*(?![*/])\S.*\baddRedis\s*\("))
        {
            throw new InvalidOperationException("Baseline SDK already exposes an addRedis API; test cannot verify refresh behavior after adding Redis.");
        }

        var codegenHashPath = Path.Combine(modulesDir, ".codegen-hash");
        var initialCodegenHash = File.Exists(codegenHashPath) ? File.ReadAllText(codegenHashPath) : null;

        // Step 2: Add a new integration that is not present in the initial generated SDK.
        await auto.TypeAsync("aspire add Aspire.Hosting.Redis");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("The package Aspire.Hosting.", timeout: TimeSpan.FromMinutes(2));
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 3: Run aspire restore and verify the generated SDK picks up the new exports.
        await auto.TypeAsync("aspire restore");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("SDK code restored successfully", timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 4: Verify generated SDK files exist and were refreshed.
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

        var restoredAspireTs = File.ReadAllText(aspireModulePath);
        if (!restoredAspireTs.Contains("createBuilder"))
        {
            throw new InvalidOperationException("aspire.ts no longer contains the original base exports after restore.");
        }

        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.config.json");
        var configJson = File.ReadAllText(configPath);
        if (!configJson.Contains("\"Aspire.Hosting.Redis\""))
        {
            throw new InvalidOperationException("aspire.config.json did not record Aspire.Hosting.Redis after aspire add.");
        }

        var restoredCodegenHash = File.Exists(codegenHashPath) ? File.ReadAllText(codegenHashPath) : null;
        if (initialCodegenHash == restoredCodegenHash)
        {
            throw new InvalidOperationException(".modules/.codegen-hash did not change after adding Aspire.Hosting.Redis and running aspire restore.");
        }

        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts");
        File.WriteAllText(appHostPath, """
            import { createBuilder } from './.modules/aspire.js';

            const builder = await createBuilder();
            await builder.addRedis("cache");
            await builder.build().run();
            """);

        await auto.TypeAsync("npx tsc --noEmit --project tsconfig.apphost.json");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptFailFastAsync(counter, TimeSpan.FromMinutes(2));

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task UnAwaitedChainsCompileWithAutoResolvePromises()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CreateDockerTestTerminal(
            repoRoot,
            workspace,
            variant: CliE2ETestHelpers.DockerfileVariant.DotNet,
            mountDockerSocket: true,
            out var strategy,
            out var installMode);
        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await InstallAspireCliAsync(auto, counter, strategy, installMode);

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

    private Hex1bTerminal CreateDockerTestTerminal(
        string repoRoot,
        TemporaryWorkspace workspace,
        CliE2ETestHelpers.DockerfileVariant variant,
        bool mountDockerSocket,
        out CliInstallStrategy? strategy,
        out CliE2ETestHelpers.DockerInstallMode? installMode,
        [CallerMemberName] string testName = "")
    {
        if (ShouldUseCliInstallStrategy())
        {
            strategy = CliInstallStrategy.Detect();
            installMode = null;

            return CliE2ETestHelpers.CreateDockerTestTerminal(
                repoRoot,
                strategy,
                output,
                variant: variant,
                mountDockerSocket: mountDockerSocket,
                workspace: workspace,
                testName: testName);
        }

        strategy = null;
        installMode = CliE2ETestHelpers.DetectDockerInstallMode(repoRoot);

        return CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot,
            installMode.Value,
            output,
            variant: variant,
            mountDockerSocket: mountDockerSocket,
            workspace: workspace,
            testName: testName);
    }

    private static async Task InstallAspireCliAsync(
        Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        CliInstallStrategy? strategy,
        CliE2ETestHelpers.DockerInstallMode? installMode)
    {
        if (strategy is not null)
        {
            await auto.InstallAspireCliAsync(strategy, counter);
            return;
        }

        if (installMode is not null)
        {
            await auto.InstallAspireCliInDockerAsync(installMode.Value, counter);
            return;
        }

        throw new InvalidOperationException("Either a CLI install strategy or Docker install mode must be provided.");
    }

    private static bool ShouldUseCliInstallStrategy()
    {
        return CliE2ETestHelpers.IsRunningInCI ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPIRE_E2E_ARCHIVE")) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPIRE_E2E_QUALITY")) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPIRE_E2E_VERSION"));
    }
}
