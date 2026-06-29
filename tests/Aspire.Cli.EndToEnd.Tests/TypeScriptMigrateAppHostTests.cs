// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end coverage for <c>aspire update --migrate</c>, which upgrades a legacy
/// TypeScript AppHost (<c>apphost.ts</c> importing the generated SDK from
/// <c>./.modules/aspire.js</c>) to the modern <c>apphost.mts</c> layout (importing from
/// <c>./.aspire/modules/aspire.mjs</c>). After migration the command must:
/// <list type="bullet">
///   <item><description>Write a new <c>apphost.mts</c> with rewritten SDK imports and delete <c>apphost.ts</c>.</description></item>
///   <item><description>Point <c>aspire.config.json</c> and <c>tsconfig.apphost.json</c> at the modern files.</description></item>
///   <item><description>Regenerate the SDK into <c>./.aspire/modules/</c> (the legacy <c>./.modules/</c> is removed).</description></item>
///   <item><description>Leave the project runnable via <c>aspire start</c> against the migrated <c>apphost.mts</c>.</description></item>
/// </list>
/// See <c>UpdateCommand</c> (the <c>--migrate</c> flag), <c>TypeScriptAppHostMigration</c>, and
/// https://github.com/microsoft/aspire/issues/17842.
/// </summary>
public sealed class TypeScriptMigrateAppHostTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task AspireMigrateUpgradesLegacyAppHostTsToMts()
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

        // Step 1: Bootstrap a modern TypeScript AppHost so all scaffolded files and the installed
        // Node toolchain are real, then convert it in place to the legacy layout to reproduce a
        // project that an earlier CLI version originally created.
        await auto.TypeAsync("aspire init --language typescript --non-interactive");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Created apphost.mts", timeout: TimeSpan.FromMinutes(2));
        await auto.WaitForSuccessPromptAsync(counter);

        ConvertScaffoldToLegacyAppHostTs(workspace.WorkspaceRoot.FullName);

        // Step 2: Register Redis so the regenerated SDK exposes addRedis. `aspire add` persists the
        // package to aspire.config.json against the legacy apphost.ts.
        await auto.TypeAsync("aspire add Aspire.Hosting.Redis");
        await auto.EnterAsync();
        await auto.WaitForAspireAddSuccessAsync(counter, TimeSpan.FromMinutes(2));

        // Step 3: Run the migration via `aspire update --migrate`. This updates packages first,
        // then rewrites apphost.ts -> apphost.mts, updates config and tsconfig, removes .modules/,
        // and regenerates the SDK into .aspire/modules/.
        await auto.TypeAsync("aspire update --migrate --yes");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Migrated 'apphost.ts' to 'apphost.mts'", timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(3));

        AssertModernModulesLayout(workspace.WorkspaceRoot.FullName);

        // Step 4: Type-check the migrated apphost.mts against its tsconfig to prove the rewritten
        // import specifiers resolve against the modern .aspire/modules/ folder.
        await auto.TypeAsync("npx --no-install tsc --noEmit -p tsconfig.apphost.json");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

        // Step 5: `aspire start` exercises the migrated apphost.mts at runtime and proves addRedis
        // (added in step 2 and carried through migration) materializes as a real resource.
        await auto.AspireStartAsync(counter);
        await auto.AssertResourcesExistAsync(counter, "cache");
        await auto.AspireStopAsync(counter);
    }

    /// <summary>
    /// Mutates a freshly-scaffolded modern TypeScript AppHost into the legacy layout
    /// (<c>apphost.ts</c> + <c>./.modules/aspire.js</c> imports) that pre-<c>apphost.mts</c> CLI
    /// versions produced, so the migration has a realistic legacy project to upgrade.
    /// </summary>
    private static void ConvertScaffoldToLegacyAppHostTs(string projectRoot)
    {
        // The legacy layout requires apphost.ts AND no apphost.mts on disk, so drop the modern
        // apphost.mts and any pre-generated modern SDK files first.
        var mtsPath = Path.Combine(projectRoot, "apphost.mts");
        if (File.Exists(mtsPath))
        {
            File.Delete(mtsPath);
        }

        var modernModulesDir = Path.Combine(projectRoot, ".aspire", "modules");
        if (Directory.Exists(modernModulesDir))
        {
            Directory.Delete(modernModulesDir, recursive: true);
        }

        // The legacy apphost.ts imports the generated SDK from ./.modules/aspire.js — the exact
        // import shape that pre-apphost.mts scaffolding produced.
        File.WriteAllText(Path.Combine(projectRoot, "apphost.ts"), """
            import { createBuilder } from './.modules/aspire.js';

            const builder = await createBuilder();
            await builder.addRedis("cache");
            await builder.build().run();
            """);

        // `aspire run/start` reads appHost.path explicitly from aspire.config.json — without this
        // update the CLI would still look for apphost.mts.
        var configPath = Path.Combine(projectRoot, "aspire.config.json");
        var configJson = JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
        var appHost = configJson["appHost"]!.AsObject();
        appHost["path"] = "apphost.ts";
        File.WriteAllText(
            configPath,
            configJson.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        File.WriteAllText(Path.Combine(projectRoot, "tsconfig.apphost.json"), """
            {
              "compilerOptions": {
                "target": "ES2022",
                "module": "NodeNext",
                "moduleResolution": "NodeNext",
                "esModuleInterop": true,
                "forceConsistentCasingInFileNames": true,
                "strict": true,
                "skipLibCheck": true,
                "outDir": "./dist/apphost",
                "rootDir": "."
              },
              "include": [
                "apphost.ts",
                ".modules/aspire.ts",
                ".modules/base.ts",
                ".modules/transport.ts"
              ],
              "exclude": ["node_modules"]
            }
            """);
    }

    private static void AssertModernModulesLayout(string projectRoot)
    {
        // apphost.ts must be gone and apphost.mts must exist with rewritten imports.
        var legacyAppHost = Path.Combine(projectRoot, "apphost.ts");
        if (File.Exists(legacyAppHost))
        {
            throw new InvalidOperationException(
                $"Legacy 'apphost.ts' still exists at {legacyAppHost} after migration. " +
                "The migration should have deleted it after writing apphost.mts.");
        }

        var modernAppHost = Path.Combine(projectRoot, "apphost.mts");
        if (!File.Exists(modernAppHost))
        {
            throw new InvalidOperationException(
                $"Modern 'apphost.mts' was not created at {modernAppHost} after migration.");
        }

        var appHostContent = File.ReadAllText(modernAppHost);
        if (!appHostContent.Contains(".aspire/modules/aspire.mjs", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Migrated 'apphost.mts' does not import the SDK from './.aspire/modules/aspire.mjs'. " +
                $"Content: {appHostContent}");
        }

        // The legacy .modules/ folder must be removed and the modern .aspire/modules/ regenerated.
        var legacyModulesDir = Path.Combine(projectRoot, ".modules");
        if (Directory.Exists(legacyModulesDir))
        {
            throw new InvalidOperationException(
                $"Legacy '.modules' directory still exists at {legacyModulesDir} after migration. " +
                "The migration should have removed it.");
        }

        var modernModulesDir = Path.Combine(projectRoot, ".aspire", "modules");
        if (!Directory.Exists(modernModulesDir))
        {
            throw new InvalidOperationException(
                $"Modern '.aspire/modules' directory was not regenerated at {modernModulesDir} after migration.");
        }

        foreach (var expectedFile in new[] { "aspire.mts", "base.mts", "transport.mts" })
        {
            var filePath = Path.Combine(modernModulesDir, expectedFile);
            if (!File.Exists(filePath))
            {
                throw new InvalidOperationException($"Expected regenerated file not found: {filePath}");
            }
        }

        // The regenerated SDK must surface the Redis API that aspire add registered.
        var aspireMts = File.ReadAllText(Path.Combine(modernModulesDir, "aspire.mts"));
        if (!aspireMts.Contains("addRedis", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Regenerated '.aspire/modules/aspire.mts' does not expose addRedis after migration. " +
                $"File path: {Path.Combine(modernModulesDir, "aspire.mts")}.");
        }

        // tsconfig.apphost.json include entries must have been rewritten to the modern files.
        var tsconfig = JsonNode.Parse(File.ReadAllText(Path.Combine(projectRoot, "tsconfig.apphost.json")))!.AsObject();
        var includes = tsconfig["include"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        var expectedIncludes = new[] { "apphost.mts", ".aspire/modules/aspire.mts", ".aspire/modules/base.mts", ".aspire/modules/transport.mts" };
        if (!includes.SequenceEqual(expectedIncludes))
        {
            throw new InvalidOperationException(
                $"tsconfig.apphost.json include was not rewritten to the modern layout. " +
                $"Expected [{string.Join(", ", expectedIncludes)}] but found [{string.Join(", ", includes)}].");
        }
    }
}
