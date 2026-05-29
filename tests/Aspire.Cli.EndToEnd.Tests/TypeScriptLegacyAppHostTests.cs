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
/// End-to-end coverage for the legacy TypeScript AppHost compatibility path. TypeScript
/// projects scaffolded before the move to <c>apphost.mts</c> still ship an
/// <c>apphost.ts</c> that imports the generated SDK from <c>./.modules/aspire.js</c>.
/// When such a project is detected, the CLI must:
/// <list type="bullet">
///   <item><description>Write generated SDK files to the legacy <c>./.modules/</c> folder (not <c>./.aspire/modules/</c>).</description></item>
///   <item><description>Convert <c>.mts</c>/<c>.mjs</c> output back to <c>.ts</c>/<c>.js</c> so the existing import specifiers resolve.</description></item>
///   <item><description>Continue to run successfully via <c>aspire start</c>.</description></item>
/// </list>
/// See <c>GuestAppHostProject.ShouldEmitLegacyTypeScriptGeneratedFiles</c> and
/// <c>ConvertGeneratedFilesForLegacyTypeScriptAppHost</c>.
/// </summary>
public sealed class TypeScriptLegacyAppHostTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task AspireAddAndStartWorkAgainstLegacyAppHostTs()
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

        // Step 1: Bootstrap a modern TypeScript AppHost so all scaffolded files
        // (package.json, tsconfig.apphost.json, aspire.config.json) and the installed
        // Node toolchain are real. We then convert it in place to the legacy layout to
        // reproduce a project that an earlier CLI version originally created.
        await auto.TypeAsync("aspire init --language typescript --non-interactive");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Created apphost.mts", timeout: TimeSpan.FromMinutes(2));
        await auto.WaitForSuccessPromptAsync(counter);

        ConvertScaffoldToLegacyAppHostTs(workspace.WorkspaceRoot.FullName);

        // Step 2: Add a new integration through `aspire add`. The CLI must persist the
        // package to aspire.config.json and then route the subsequent code generation
        // through the legacy compatibility path without disturbing the existing apphost.ts.
        await auto.TypeAsync("aspire add Aspire.Hosting.Redis");
        await auto.EnterAsync();
        await auto.WaitForAspireAddSuccessAsync(counter, TimeSpan.FromMinutes(2));

        // Step 3: Restore triggers the explicit code-generation step. For a legacy
        // apphost.ts (apphost.mts absent), generated files MUST land in `.modules/`
        // rather than `.aspire/modules/`. RestoreCommand calls BuildAndGenerateSdkAsync
        // with appHostFile: null, so ShouldEmitLegacyTypeScriptGeneratedFiles takes the
        // disk-scan branch and selects the legacy layout.
        await auto.TypeAsync("aspire restore");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("SDK code restored successfully", timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        AssertLegacyModulesLayout(workspace.WorkspaceRoot.FullName);

        // Step 4: Type-check apphost.ts against its tsconfig. This proves the rewritten
        // `.js` import specifiers in the generated files resolve correctly against the
        // legacy `.modules/` folder — the contract the conversion enforces.
        await auto.TypeAsync("npx --no-install tsc --noEmit -p tsconfig.apphost.json");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

        // Step 5: `aspire start` exercises apphost.ts at runtime — proving the generated
        // SDK is dynamically importable AND that addRedis (added via aspire add in step 2)
        // materializes as a real resource. RunAsync passes the explicit apphost.ts
        // FileInfo, so legacy detection takes the file-name branch this time.
        await auto.AspireStartAsync(counter);
        await auto.AssertResourcesExistAsync(counter, "cache");
        await auto.AspireStopAsync(counter);
    }

    /// <summary>
    /// Mutates a freshly-scaffolded modern TypeScript AppHost into the legacy layout
    /// (<c>apphost.ts</c> + <c>./.modules/aspire.js</c> imports) that pre-13.4 CLI
    /// versions produced. This is intentionally surgical — we only touch the files
    /// that the legacy detection in
    /// <c>GuestAppHostProject.ShouldEmitLegacyTypeScriptGeneratedFiles</c> reads, plus
    /// the tsconfig include set so <c>tsc --noEmit</c> can type-check the legacy layout.
    /// </summary>
    private static void ConvertScaffoldToLegacyAppHostTs(string projectRoot)
    {
        // Drop the modern apphost.mts and any pre-generated SDK files. Their presence
        // would defeat the legacy detection, which requires apphost.ts AND no apphost.mts
        // on disk (see ShouldEmitLegacyTypeScriptGeneratedFiles' file-existence check).
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

        // The legacy apphost.ts imports the generated SDK from ./.modules/aspire.js —
        // the exact import shape that pre-13.4 scaffolding produced and that this test
        // is validating continues to work end-to-end.
        File.WriteAllText(Path.Combine(projectRoot, "apphost.ts"), """
            import { createBuilder } from './.modules/aspire.js';

            const builder = await createBuilder();
            await builder.addRedis("cache");
            await builder.build().run();
            """);

        // Point aspire.config.json at apphost.ts. apphost.ts also matches the TypeScript
        // detection patterns, but `aspire run/start` reads appHost.path explicitly from
        // aspire.config.json — without this update the CLI would still look for
        // apphost.mts.
        var configPath = Path.Combine(projectRoot, "aspire.config.json");
        var configJson = JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
        var appHost = configJson["appHost"]!.AsObject();
        appHost["path"] = "apphost.ts";
        File.WriteAllText(
            configPath,
            configJson.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        // The CLI's PreExecute step runs `tsc --noEmit -p tsconfig.apphost.json` against
        // whatever files the tsconfig includes. Replace the modern .mts entries with the
        // legacy .ts equivalents so the type-check actually sees the rewritten SDK.
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

    private static void AssertLegacyModulesLayout(string projectRoot)
    {
        var legacyModulesDir = Path.Combine(projectRoot, ".modules");
        if (!Directory.Exists(legacyModulesDir))
        {
            throw new InvalidOperationException(
                $"Legacy '.modules' directory was not created at {legacyModulesDir}. " +
                "The CLI is supposed to route generated SDK files to '.modules/' (not '.aspire/modules/') " +
                "when an apphost.ts (without an apphost.mts) is detected — see ShouldEmitLegacyTypeScriptGeneratedFiles.");
        }

        var modernModulesDir = Path.Combine(projectRoot, ".aspire", "modules");
        if (Directory.Exists(modernModulesDir))
        {
            throw new InvalidOperationException(
                $"Modern '.aspire/modules' directory was unexpectedly created at {modernModulesDir} for a legacy apphost.ts project. " +
                "This would silently leave the project's `./.modules/aspire.js` import unresolved.");
        }

        foreach (var expectedFile in new[] { "aspire.ts", "base.ts", "transport.ts" })
        {
            var filePath = Path.Combine(legacyModulesDir, expectedFile);
            if (!File.Exists(filePath))
            {
                throw new InvalidOperationException($"Expected generated file not found: {filePath}");
            }

            var content = File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new InvalidOperationException($"Generated file is empty: {filePath}");
            }

            // The legacy conversion rewrites .mjs import specifiers to .js so the
            // existing apphost.ts `./.modules/aspire.js` import (and the inter-module
            // imports inside the generated files) all resolve at runtime.
            if (content.Contains(".mjs", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Generated file '{filePath}' still contains a '.mjs' import specifier — the legacy conversion " +
                    "should have rewritten these to '.js' to match how Node resolves the SDK from apphost.ts.");
            }
        }

        // None of the generated files should be emitted with .mts extensions in the
        // legacy folder — the conversion renames aspire/base/transport to .ts.
        foreach (var unexpectedFile in new[] { "aspire.mts", "base.mts", "transport.mts" })
        {
            var filePath = Path.Combine(legacyModulesDir, unexpectedFile);
            if (File.Exists(filePath))
            {
                throw new InvalidOperationException(
                    $"Unexpected modern file '{filePath}' was emitted into the legacy '.modules/' folder. " +
                    "The conversion path should have written the .ts equivalents only.");
            }
        }

        // The generated SDK must actually surface the Redis API that aspire add registered.
        // If addRedis is missing the apphost.ts call to builder.addRedis("cache") would
        // fail at runtime — and `aspire start` would never reach the dashboard URL.
        var aspireTs = File.ReadAllText(Path.Combine(legacyModulesDir, "aspire.ts"));
        if (!aspireTs.Contains("addRedis", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Generated '.modules/aspire.ts' does not expose addRedis after `aspire add Aspire.Hosting.Redis`. " +
                $"File path: {Path.Combine(legacyModulesDir, "aspire.ts")}.");
        }
    }
}
