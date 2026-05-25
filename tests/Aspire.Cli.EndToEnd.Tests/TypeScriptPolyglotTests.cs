// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Hex1b.Input;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for Aspire CLI with TypeScript polyglot AppHost.
/// Tests creating a TypeScript-based AppHost and adding a Vite application.
/// </summary>
public sealed class TypeScriptPolyglotTests(ITestOutputHelper output)
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
    public async Task CreateTypeScriptAppHostWithViteApp_UsesConfiguredToolchain(string toolchain)
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);
        var localChannel = CliE2ETestHelpers.PrepareLocalChannel(repoRoot, strategy,
            ["Aspire.Hosting.CodeGeneration.TypeScript.", "Aspire.Hosting.JavaScript."]);

        // LocalHive strategy only: PrepareLocalChannel returned a real channel,
        // so pass --channel local explicitly to aspire init. Other strategies
        // (script-installed CLI, pre-existing CLI) return null and rely on the
        // CLI's baked channel + ambient NuGet feeds.
        var channelArgument = localChannel is not null ? " --channel local" : string.Empty;

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, variant: CliE2ETestHelpers.DockerfileVariant.Polyglot, mountDockerSocket: true, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliAsync(strategy, counter);

        output.WriteLine($"Testing TypeScript AppHost toolchain: {toolchain}");

        // Step 1: Create TypeScript AppHost
        await auto.TypeAsync($"aspire init --language typescript --non-interactive{channelArgument}");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Created apphost.mts", timeout: TimeSpan.FromMinutes(2));
        await auto.DeclineAgentInitPromptAsync(counter);

        TypeScriptAppHostToolchainTestHelpers.SetPackageManager(workspace.WorkspaceRoot.FullName, toolchain, cleanInstallState: true);

        // LocalHive strategy only: PrepareLocalChannel returned a real channel,
        // so write the per-project aspire.config.json to point at the in-repo
        // nupkg hive. Other strategies (script-installed CLI, pre-existing CLI)
        // return null and rely on the CLI's baked channel + ambient NuGet feeds.
        if (localChannel is not null)
        {
            CliE2ETestHelpers.WriteLocalChannelSettings(workspace.WorkspaceRoot.FullName, localChannel.SdkVersion);
        }

        // Step 2: Create a Vite app using npm create vite
        // Using --template vanilla-ts for a minimal TypeScript Vite app
        // Use -y to skip npm prompts and -- to pass args to create-vite
        // Use --no-interactive to skip vite's interactive prompts (rolldown, install now, etc.)
        await auto.TypeAsync("npm create -y vite@latest viteapp -- --template vanilla-ts --no-interactive");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

        var viteProjectRoot = Path.Combine(workspace.WorkspaceRoot.FullName, "viteapp");
        TypeScriptAppHostToolchainTestHelpers.SetPackageManager(viteProjectRoot, toolchain, cleanInstallState: true);

        // Step 3: Install Vite app dependencies
        await auto.TypeAsync($"cd viteapp && {TypeScriptAppHostToolchainTestHelpers.GetInstallCommand(toolchain)} && cd ..");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

        // Step 4: Add Aspire.Hosting.JavaScript package
        // When channel is set (CI) and there's only one channel with one version,
        // the version is auto-selected without prompting.
        await auto.TypeAsync("aspire add Aspire.Hosting.JavaScript");
        await auto.EnterAsync();
        await auto.WaitForAspireAddSuccessAsync(counter, TimeSpan.FromMinutes(2));

        // Step 5: Modify apphost.mts to add the Vite app
        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.mts");
        var newContent = """
            // Aspire TypeScript AppHost
            // For more information, see: https://aspire.dev

            import { createBuilder } from './.aspire/modules/aspire.mjs';

            const builder = await createBuilder();

            // Add the Vite frontend application
            const viteApp = await builder.addViteApp("viteapp", "./viteapp");

            await builder.build().run();
            """;

        File.WriteAllText(appHostPath, newContent);

        // Step 6: Restore and type-check with the configured package manager before running.
        await auto.TypeAsync("aspire restore");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("SDK code restored successfully", timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        var appHostLockFilePath = Path.Combine(
            workspace.WorkspaceRoot.FullName,
            TypeScriptAppHostToolchainTestHelpers.GetLockFileName(toolchain));
        Assert.True(
            File.Exists(appHostLockFilePath),
            $"Expected {TypeScriptAppHostToolchainTestHelpers.GetDisplayName(toolchain)} restore to create '{appHostLockFilePath}'.");

        var viteLockFilePath = Path.Combine(
            viteProjectRoot,
            TypeScriptAppHostToolchainTestHelpers.GetLockFileName(toolchain));
        Assert.True(
            File.Exists(viteLockFilePath),
            $"Expected {TypeScriptAppHostToolchainTestHelpers.GetDisplayName(toolchain)} install to create '{viteLockFilePath}'.");

        await auto.TypeAsync(TypeScriptAppHostToolchainTestHelpers.GetTypeCheckCommand(toolchain, "tsconfig.apphost.json"));
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptFailFastAsync(counter, TimeSpan.FromMinutes(2));

        // Step 7: Run the apphost
        await auto.TypeAsync("aspire run");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(s =>
        {
            if (s.ContainsText("Select an AppHost to use:"))
            {
                throw new InvalidOperationException(
                    "Unexpected apphost selection prompt detected! " +
                    "This indicates multiple apphosts were incorrectly detected.");
            }

            return s.ContainsText("Press CTRL+C to stop the AppHost and exit.");
        }, timeout: TimeSpan.FromMinutes(3), description: "Press CTRL+C message (aspire run started)");

        // Step 8: Stop the apphost
        await auto.Ctrl().KeyAsync(Hex1bKey.C);
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    [Theory]
    [MemberData(nameof(SupportedToolchains))]
    [CaptureWorkspaceOnFailure]
    public async Task GeneratedAspireDevScript_StartsWatchMode_WithConfiguredToolchain(string toolchain)
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);
        var localChannel = CliE2ETestHelpers.PrepareLocalChannel(repoRoot, strategy,
            ["Aspire.Hosting.CodeGeneration.TypeScript."]);

        var channelArgument = localChannel is not null ? " --channel local" : string.Empty;

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        await auto.TypeAsync($"aspire init --language typescript --non-interactive{channelArgument}");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Created apphost.mts", timeout: TimeSpan.FromMinutes(2));
        await auto.DeclineAgentInitPromptAsync(counter);

        TypeScriptAppHostToolchainTestHelpers.SetPackageManager(workspace.WorkspaceRoot.FullName, toolchain, cleanInstallState: true);

        if (localChannel is not null)
        {
            CliE2ETestHelpers.WriteLocalChannelSettings(workspace.WorkspaceRoot.FullName, localChannel.SdkVersion);
        }

        await auto.TypeAsync(TypeScriptAppHostToolchainTestHelpers.GetInstallCommand(toolchain));
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

        var lockFilePath = Path.Combine(
            workspace.WorkspaceRoot.FullName,
            TypeScriptAppHostToolchainTestHelpers.GetLockFileName(toolchain));
        Assert.True(
            File.Exists(lockFilePath),
            $"Expected {TypeScriptAppHostToolchainTestHelpers.GetDisplayName(toolchain)} install to create '{lockFilePath}'.");

        await auto.TypeAsync(TypeScriptAppHostToolchainTestHelpers.GetRunScriptCommand(toolchain, "aspire:dev"));
        await auto.EnterAsync();
        await auto.WaitUntilAsync(
            s => s.ContainsText("Watching for file changes."),
            timeout: TimeSpan.FromMinutes(2),
            description: $"{toolchain} watch mode to start");

        await auto.Ctrl().KeyAsync(Hex1bKey.C);
        await auto.WaitForAnyPromptAsync(counter);
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    [Fact]
    public async Task InitTypeScriptAppHost_AugmentsExistingViteRepoAtRoot()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);
        var localChannel = CliE2ETestHelpers.PrepareLocalChannel(repoRoot, strategy,
            ["Aspire.Hosting.CodeGeneration.TypeScript.", "Aspire.Hosting.JavaScript."]);

        // LocalHive strategy only: PrepareLocalChannel returned a real channel,
        // so pass --channel local explicitly to aspire init. Other strategies
        // (script-installed CLI, pre-existing CLI) return null and rely on the
        // CLI's baked channel + ambient NuGet feeds.
        var channelArgument = localChannel is not null ? " --channel local" : string.Empty;

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, variant: CliE2ETestHelpers.DockerfileVariant.DotNet, mountDockerSocket: true, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        string? originalDevScript = null;
        string? originalBuildScript = null;
        string? originalPreviewScript = null;
        string? originalPackageType = null;
        string? originalTsConfig = null;

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.EnablePolyglotSupportAsync(counter);

        // Create brownfield Vite project
        await auto.TypeAsync("mkdir brownfield && cd brownfield");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("npm create -y vite@latest . -- --template vanilla-ts --no-interactive");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

        // Capture original package.json scripts and tsconfig before aspire init
        var projectRoot = Path.Combine(workspace.WorkspaceRoot.FullName, "brownfield");
        var packageJson = JsonNode.Parse(File.ReadAllText(Path.Combine(projectRoot, "package.json")))!.AsObject();
        var scripts = packageJson["scripts"]!.AsObject();
        originalDevScript = scripts["dev"]?.GetValue<string>();
        originalBuildScript = scripts["build"]?.GetValue<string>();
        originalPreviewScript = scripts["preview"]?.GetValue<string>();
        originalPackageType = packageJson["type"]?.GetValue<string>();
        originalTsConfig = File.ReadAllText(Path.Combine(projectRoot, "tsconfig.json"));

        // LocalHive strategy only: PrepareLocalChannel returned a real channel,
        // so write the per-project aspire.config.json to point at the in-repo
        // nupkg hive. Other strategies (script-installed CLI, pre-existing CLI)
        // return null and rely on the CLI's baked channel + ambient NuGet feeds.
        if (localChannel is not null)
        {
            CliE2ETestHelpers.WriteLocalChannelSettings(projectRoot, localChannel.SdkVersion);
        }

        // Run aspire init in brownfield mode
        await auto.TypeAsync($"aspire init --language typescript --non-interactive{channelArgument}");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Created aspire-apphost/apphost.mts", timeout: TimeSpan.FromMinutes(2));
        await auto.DeclineAgentInitPromptAsync(counter);

        // Verify brownfield augmentation preserved existing config
        Assert.NotNull(originalDevScript);
        Assert.NotNull(originalBuildScript);
        Assert.NotNull(originalPreviewScript);
        Assert.NotNull(originalTsConfig);

        packageJson = JsonNode.Parse(File.ReadAllText(Path.Combine(projectRoot, "package.json")))!.AsObject();
        scripts = packageJson["scripts"]!.AsObject();

        Assert.Equal(originalDevScript, scripts["dev"]?.GetValue<string>());
        Assert.Equal(originalBuildScript, scripts["build"]?.GetValue<string>());
        Assert.Equal(originalPreviewScript, scripts["preview"]?.GetValue<string>());
        Assert.Equal("npm --prefix aspire-apphost run aspire:start", scripts["aspire:start"]?.GetValue<string>());
        Assert.Equal("npm --prefix aspire-apphost run aspire:build", scripts["aspire:build"]?.GetValue<string>());
        Assert.Equal("npm --prefix aspire-apphost run aspire:dev", scripts["aspire:dev"]?.GetValue<string>());
        Assert.Equal(originalPackageType, packageJson["type"]?.GetValue<string>());
        Assert.False(scripts.ContainsKey("start"));
        var rootDependencies = packageJson["dependencies"]?.AsObject();
        var rootDevDependencies = packageJson["devDependencies"]?.AsObject();
        Assert.Null(rootDependencies?["vscode-jsonrpc"]);
        Assert.Null(rootDevDependencies?["vscode-jsonrpc"]);
        Assert.Null(rootDevDependencies?["nodemon"]);
        Assert.Null(rootDevDependencies?["tsx"]);
        Assert.Equal(originalTsConfig, File.ReadAllText(Path.Combine(projectRoot, "tsconfig.json")));
        Assert.False(File.Exists(Path.Combine(projectRoot, "tsconfig.apphost.json")));
        Assert.False(File.Exists(Path.Combine(projectRoot, "apphost.mts")));

        var appHostDirectory = Path.Combine(projectRoot, "aspire-apphost");
        var appHostPath = Path.Combine(appHostDirectory, "apphost.mts");
        Assert.True(File.Exists(appHostPath));

        var appHostPackageJson = JsonNode.Parse(File.ReadAllText(Path.Combine(appHostDirectory, "package.json")))!.AsObject();
        var appHostScripts = appHostPackageJson["scripts"]!.AsObject();
        var appHostDependencies = appHostPackageJson["dependencies"]!.AsObject();
        var appHostDevDependencies = appHostPackageJson["devDependencies"]!.AsObject();
        Assert.Equal("module", appHostPackageJson["type"]?.GetValue<string>());
        Assert.Equal("aspire-apphost", appHostPackageJson["name"]?.GetValue<string>());
        Assert.Equal("aspire run", appHostScripts["aspire:start"]?.GetValue<string>());
        Assert.NotNull(appHostDependencies["vscode-jsonrpc"]);
        Assert.NotNull(appHostDevDependencies["@types/node"]);
        Assert.NotNull(appHostDevDependencies["nodemon"]);
        Assert.NotNull(appHostDevDependencies["tsx"]);
        Assert.NotNull(appHostDevDependencies["typescript"]);
        Assert.True(File.Exists(Path.Combine(appHostDirectory, "tsconfig.apphost.json")));

        // Verify Aspire.Hosting.JavaScript was pre-added in config
        var configPath = Path.Combine(projectRoot, "aspire.config.json");
        var config = JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
        var appHost = config["appHost"]!.AsObject();
        Assert.Equal("aspire-apphost/apphost.mts", appHost["path"]?.GetValue<string>());
        var packagesNode = config["packages"];
        Assert.NotNull(packagesNode);
        var packages = packagesNode!.AsObject();
        Assert.NotNull(packages["Aspire.Hosting.JavaScript"]);

        // Modify apphost.mts to add the Vite app before running
        var newContent = """
            // Aspire TypeScript AppHost
            // For more information, see: https://aspire.dev

            import { createBuilder } from './.aspire/modules/aspire.mjs';

            const builder = await createBuilder();

            await builder.addViteApp("brownfield", "..");

            await builder.build().run();
            """;

        File.WriteAllText(appHostPath, newContent);

        // Run the apphost to verify it works
        await auto.TypeAsync("aspire run");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(s =>
        {
            if (s.ContainsText("Select an AppHost to use:"))
            {
                throw new InvalidOperationException(
                    "Unexpected apphost selection prompt detected! " +
                    "This indicates multiple apphosts were incorrectly detected.");
            }

            return s.ContainsText("Press CTRL+C to stop the AppHost and exit.");
        }, timeout: TimeSpan.FromMinutes(3), description: "Press CTRL+C message (aspire run started)");

        await auto.Ctrl().KeyAsync(Hex1bKey.C);
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
