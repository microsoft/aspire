// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for reusable TypeScript helper packages that consume generated Aspire SDK types.
/// </summary>
public sealed class TypeScriptReusablePackageTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RestoreSupportsConfigOnlyHelperPackageAndCrossPackageTypes()
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

        var appDirectory = Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, "app"));
        var helperDirectory = Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, "packages", "aspire-commands"));
        var helperSourceDirectory = helperDirectory.CreateSubdirectory("src");

        await auto.TypeAsync($"cd {CliE2ETestHelpers.ToContainerPath(appDirectory.FullName, workspace)}");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // The main app is a normal TypeScript AppHost created by the CLI.
        await auto.TypeAsync("aspire init --language typescript --non-interactive");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        var appAppHostPath = Path.Combine(appDirectory.FullName, "apphost.ts");
        Assert.True(File.Exists(appAppHostPath), $"Expected the CLI-created app to contain {appAppHostPath}.");

        await auto.TypeAsync("aspire add Aspire.Hosting.Redis");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("The package Aspire.Hosting.", timeout: TimeSpan.FromMinutes(2));
        await auto.WaitForSuccessPromptAsync(counter);

        var sdkVersion = GetSdkVersion(appDirectory);
        WriteHelperPackageFiles(helperDirectory, helperSourceDirectory, sdkVersion);
        RewriteAppHostFiles(appDirectory);

        // The helper package is intentionally config-only: it has aspire.config.json but no apphost.ts.
        var helperAppHostPath = Path.Combine(helperDirectory.FullName, "apphost.ts");
        Assert.False(File.Exists(helperAppHostPath), $"Config-only helper package unexpectedly contained {helperAppHostPath}.");

        await auto.TypeAsync($"cd {CliE2ETestHelpers.ToContainerPath(helperDirectory.FullName, workspace)}");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire restore");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("SDK code restored successfully", timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        var helperModulesDirectory = Path.Combine(helperDirectory.FullName, ".modules");
        Assert.True(Directory.Exists(helperModulesDirectory), $".modules directory was not created for helper package at {helperModulesDirectory}");
        Assert.Contains("addRedis", File.ReadAllText(Path.Combine(helperModulesDirectory, "aspire.ts")));

        await auto.TypeAsync("npx tsc --noEmit");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptFailFastAsync(counter, TimeSpan.FromMinutes(2));

        await auto.TypeAsync($"cd {CliE2ETestHelpers.ToContainerPath(appDirectory.FullName, workspace)}");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire restore");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("SDK code restored successfully", timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("npx tsc --noEmit");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptFailFastAsync(counter, TimeSpan.FromMinutes(2));

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    private static string GetSdkVersion(DirectoryInfo appDirectory)
    {
        var configPath = Path.Combine(appDirectory.FullName, "aspire.config.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
        return doc.RootElement.GetProperty("sdk").GetProperty("version").GetString()
            ?? throw new InvalidOperationException("Expected aspire.config.json to contain sdk.version.");
    }

    private static void RewriteAppHostFiles(DirectoryInfo appDirectory)
    {
        File.WriteAllText(Path.Combine(appDirectory.FullName, "apphost.ts"), """
            import { createBuilder, refExpr } from './.modules/aspire.js';
            import { configureRedis } from '../packages/aspire-commands/src/index.js';

            const builder = await createBuilder();
            const redis = await builder.addRedis("cache");

            await configureRedis(redis, refExpr`redis://${redis}`);

            await builder.build().run();
            """);

        File.WriteAllText(Path.Combine(appDirectory.FullName, "tsconfig.json"), """
            {
              "compilerOptions": {
                "target": "ES2022",
                "module": "NodeNext",
                "moduleResolution": "NodeNext",
                "esModuleInterop": true,
                "forceConsistentCasingInFileNames": true,
                "strict": true,
                "skipLibCheck": true,
                "outDir": "./dist",
                "rootDir": ".."
              },
              "include": [
                "apphost.ts",
                ".modules/**/*.ts",
                "../packages/aspire-commands/src/**/*.ts",
                "../packages/aspire-commands/.modules/**/*.ts"
              ],
              "exclude": [
                "node_modules"
              ]
            }
            """);
    }

    private static void WriteHelperPackageFiles(DirectoryInfo helperDirectory, DirectoryInfo helperSourceDirectory, string sdkVersion)
    {
        File.WriteAllText(Path.Combine(helperDirectory.FullName, "aspire.config.json"), $$"""
            {
              "appHost": {
                "language": "typescript/nodejs"
              },
              "sdk": {
                "version": "{{sdkVersion}}"
              },
              "packages": {
                "Aspire.Hosting.Redis": ""
              }
            }
            """);

        File.WriteAllText(Path.Combine(helperDirectory.FullName, "package.json"), """
            {
              "name": "@issue15507/aspire-commands",
              "private": true,
              "type": "module",
              "dependencies": {
                "vscode-jsonrpc": "^8.2.0"
              },
              "devDependencies": {
                "@types/node": "^20.0.0",
                "tsx": "^4.19.0",
                "typescript": "^5.3.0"
              }
            }
            """);

        File.WriteAllText(Path.Combine(helperDirectory.FullName, "tsconfig.json"), """
            {
              "compilerOptions": {
                "target": "ES2022",
                "module": "NodeNext",
                "moduleResolution": "NodeNext",
                "esModuleInterop": true,
                "forceConsistentCasingInFileNames": true,
                "strict": true,
                "skipLibCheck": true,
                "outDir": "./dist",
                "rootDir": "."
              },
              "include": [
                "src/**/*.ts",
                ".modules/**/*.ts"
              ],
              "exclude": [
                "node_modules"
              ]
            }
            """);

        File.WriteAllText(Path.Combine(helperSourceDirectory.FullName, "index.ts"), """
            import type {
                ExecuteCommandContext,
                ExecuteCommandResult,
                RedisResource,
                ReferenceExpression
            } from '../.modules/aspire.js';

            export async function configureRedis(
                redis: RedisResource,
                prefix: ReferenceExpression
            ): Promise<RedisResource> {
                await redis.withConnectionProperty("prefix", prefix);

                return await redis.withCommand(
                    "flush",
                    "Flush Redis cache",
                    async (context: ExecuteCommandContext): Promise<ExecuteCommandResult> => ({
                        success: (await context.resourceName.get()).length > 0
                    }));
            }
            """);
    }
}
