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
    public static TheoryData<string> SupportedToolchains => new()
    {
        "npm",
        "bun",
        "yarn",
        "pnpm"
    };

    [Theory]
    [MemberData(nameof(SupportedToolchains))]
    [CaptureWorkspaceOnFailure]
    public async Task RestoreGeneratesSdkFiles_WithConfiguredToolchain(string toolchain)
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);
        var localChannel = CliE2ETestHelpers.PrepareLocalChannel(repoRoot, strategy,
            ["Aspire.Hosting.CodeGeneration.TypeScript.", "Aspire.Hosting.Redis.", "Aspire.Hosting.SqlServer."]);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, variant: CliE2ETestHelpers.DockerfileVariant.DotNet, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliAsync(strategy, counter);

        output.WriteLine($"Testing TypeScript AppHost toolchain: {toolchain}");

        // Step 1: Create a TypeScript AppHost.
        await auto.TypeAsync("aspire init --language typescript --non-interactive");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Created apphost.mts", timeout: TimeSpan.FromMinutes(2));
        await auto.WaitForSuccessPromptAsync(counter);

        TypeScriptAppHostToolchainTestHelpers.SetPackageManager(workspace.WorkspaceRoot.FullName, toolchain, cleanInstallState: true);

        // LocalHive strategy only: PrepareLocalChannel returned a real channel,
        // so write the per-project aspire.config.json to point at the in-repo
        // nupkg hive. Other strategies (script-installed CLI, pre-existing CLI)
        // return null and rely on the CLI's baked channel + ambient NuGet feeds.
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

        var lockFilePath = Path.Combine(
            workspace.WorkspaceRoot.FullName,
            TypeScriptAppHostToolchainTestHelpers.GetLockFileName(toolchain));
        if (!File.Exists(lockFilePath))
        {
            throw new InvalidOperationException(
                $"Expected {TypeScriptAppHostToolchainTestHelpers.GetDisplayName(toolchain)} restore to create '{lockFilePath}'.");
        }

        await auto.TypeAsync(TypeScriptAppHostToolchainTestHelpers.GetTypeCheckCommand(toolchain, "tsconfig.apphost.json"));
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

        // Step 4: Verify generated SDK files exist.
        var modulesDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "modules");
        if (!Directory.Exists(modulesDir))
        {
            throw new InvalidOperationException($".aspire/modules directory was not created at {modulesDir}");
        }

        var expectedFiles = new[] { "aspire.mts", "base.mts", "transport.mts" };
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

        var aspireTs = File.ReadAllText(Path.Combine(modulesDir, "aspire.mts"));
        if (!aspireTs.Contains("addRedis"))
        {
            throw new InvalidOperationException("aspire.mts does not contain addRedis from Aspire.Hosting.Redis");
        }

        if (!aspireTs.Contains("addSqlServer"))
        {
            throw new InvalidOperationException("aspire.mts does not contain addSqlServer from Aspire.Hosting.SqlServer");
        }
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task RestoreRefreshesGeneratedSdkAfterAddingIntegration()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
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
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliAsync(strategy, counter);

        // Step 1: Create a TypeScript AppHost, restore it, and verify the baseline generated SDK.
        await auto.TypeAsync("aspire init --language typescript --non-interactive");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Created apphost.mts", timeout: TimeSpan.FromMinutes(2));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire restore");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("SDK code restored successfully", timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        var modulesDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "modules");
        if (!Directory.Exists(modulesDir))
        {
            throw new InvalidOperationException($".aspire/modules directory was not created at {modulesDir}");
        }

        var aspireModulePath = Path.Combine(modulesDir, "aspire.mts");
        if (!File.Exists(aspireModulePath))
        {
            throw new InvalidOperationException($"Expected generated file not found: {aspireModulePath}");
        }

        var initialAspireTs = File.ReadAllText(aspireModulePath);
        if (!initialAspireTs.Contains("createBuilder"))
        {
            throw new InvalidOperationException("aspire.mts did not contain the expected base exports before adding a new integration.");
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
        var expectedFiles = new[] { "aspire.mts", "base.mts", "transport.mts" };
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
            throw new InvalidOperationException("aspire.mts no longer contains the original base exports after restore.");
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
            throw new InvalidOperationException(".aspire/modules/.codegen-hash did not change after adding Aspire.Hosting.Redis and running aspire restore.");
        }

        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.mts");
        File.WriteAllText(appHostPath, """
            import { createBuilder } from './.aspire/modules/aspire.mjs';

            const builder = await createBuilder();
            await builder.addRedis("cache");
            await builder.build().run();
            """);

        await auto.TypeAsync("npx tsc --noEmit --project tsconfig.apphost.json");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task UnAwaitedChainsCompileWithAutoResolvePromises()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot,
            strategy,
            output,
            variant: CliE2ETestHelpers.DockerfileVariant.DotNet,
            mountDockerSocket: true,
            workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        await auto.TypeAsync("aspire init --language typescript --non-interactive");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Created apphost.mts", timeout: TimeSpan.FromMinutes(2));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire add Aspire.Hosting.PostgreSQL");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("The package Aspire.Hosting.", timeout: TimeSpan.FromMinutes(2));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire restore");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("SDK code restored successfully", timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.mts");
        var newContent = """
            import { createBuilder } from './.aspire/modules/aspire.mjs';

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
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

        // Validate runtime behavior: aspire start launches the apphost, which calls
        // build() and triggers flushPendingPromises(). If the flush deadlocks (e.g. the
        // build promise is re-awaited in a while loop), the process hangs and this test
        // times out. A successful start proves un-awaited chains execute their RPC calls.
        await auto.AspireStartAsync(counter);

        // Verify the un-awaited resources were actually materialized — not silently dropped.
        // If flushPendingPromises skipped the pending chains, these resources wouldn't exist.
        await auto.AssertResourcesExistAsync(counter, "postgres", "db", "consumer");

        await auto.AspireStopAsync(counter);
    }
}
