// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests that validate TypeScript SDK generation for guest AppHosts,
/// including refreshing the generated SDK after adding integrations.
/// </summary>
public sealed class TypeScriptCodegenValidationTests(ITestOutputHelper output)
{
    public static TheoryData<string> AlternativeToolchains => new()
    {
        "bun",
        "yarn",
        "pnpm"
    };

    [Theory]
    [MemberData(nameof(AlternativeToolchains))]
    [CaptureWorkspaceOnFailure]
    public async Task RestoreGeneratesSdkFiles_WithConfiguredToolchain(string toolchain)
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect();
        var workspace = TemporaryWorkspace.Create(output);
        var localChannel = CliE2ETestHelpers.PrepareLocalChannel(repoRoot, strategy,
            ["Aspire.Hosting.CodeGeneration.TypeScript.", "Aspire.Hosting.Redis.", "Aspire.Hosting.SqlServer."]);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, variant: CliE2ETestHelpers.DockerfileVariant.DotNet, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliAsync(strategy, counter);

        output.WriteLine($"Testing TypeScript AppHost toolchain: {toolchain}");

        // Step 1: Create a TypeScript AppHost.
        await auto.TypeAsync("aspire init --language typescript --non-interactive");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Created apphost.ts", timeout: TimeSpan.FromMinutes(2));
        await auto.WaitForSuccessPromptAsync(counter);

        TypeScriptAppHostToolchainTestHelpers.SetPackageManager(workspace.WorkspaceRoot.FullName, toolchain, cleanInstallState: true);
        if (localChannel is not null)
        {
            CliE2ETestHelpers.WriteLocalChannelSettings(workspace.WorkspaceRoot.FullName, localChannel.SdkVersion);
        }

        // Step 2: Add two integrations.
        await auto.TypeAsync("aspire add Aspire.Hosting.Redis");
        await auto.EnterAsync();
        await auto.WaitForAspireAddSuccessAsync(counter, TimeSpan.FromMinutes(2));

        await auto.TypeAsync("aspire add Aspire.Hosting.SqlServer");
        await auto.EnterAsync();
        await auto.WaitForAspireAddSuccessAsync(counter, TimeSpan.FromMinutes(2));

        // Step 3: Run aspire restore and verify success.
        await auto.TypeAsync("aspire restore");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("SDK code restored successfully", timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 4: Verify generated SDK files exist.
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
    public async Task RestoreRefreshesGeneratedSdkAfterAddingIntegration()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect();
        var workspace = TemporaryWorkspace.Create(output);

        // Use the DotNet container variant even for the TypeScript AppHost so local LocalHive
        // repros still have dotnet available for package/add flows while remaining outside the repo,
        // which keeps the AppHost on the PrebuiltAppHostServer path instead of in-repo dev mode.
        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot,
            strategy,
            output,
            variant: CliE2ETestHelpers.DockerfileVariant.DotNet,
            mountDockerSocket: false,
            workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliAsync(strategy, counter);

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
        var strategy = CliInstallStrategy.Detect();
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot,
            strategy,
            output,
            variant: CliE2ETestHelpers.DockerfileVariant.DotNet,
            mountDockerSocket: true,
            workspace: workspace);
        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

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
