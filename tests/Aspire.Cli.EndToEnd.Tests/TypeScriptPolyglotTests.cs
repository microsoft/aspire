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
    [Fact]
    public async Task CreateTypeScriptAppHostWithViteApp()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var installMode = CliE2ETestHelpers.DetectDockerInstallMode(repoRoot);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, installMode, output, variant: CliE2ETestHelpers.DockerfileVariant.Polyglot, mountDockerSocket: true, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliInDockerAsync(installMode, counter);

        // Step 1: Create TypeScript AppHost using aspire init with interactive language selection
        await auto.TypeAsync("aspire init");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Which language would you like to use?", timeout: TimeSpan.FromSeconds(30));
        // Navigate down to "TypeScript (Node.js)" which is the 2nd option
        await auto.DownAsync();
        await auto.WaitUntilTextAsync("> TypeScript (Node.js)", timeout: TimeSpan.FromSeconds(5));
        await auto.EnterAsync(); // select TypeScript
        await auto.WaitUntilTextAsync("Created apphost.ts", timeout: TimeSpan.FromMinutes(2));
        await auto.DeclineAgentInitPromptAsync(counter);

        // Step 2: Create a Vite app using npm create vite
        // Using --template vanilla-ts for a minimal TypeScript Vite app
        // Use -y to skip npm prompts and -- to pass args to create-vite
        // Use --no-interactive to skip vite's interactive prompts (rolldown, install now, etc.)
        await auto.TypeAsync("npm create -y vite@latest viteapp -- --template vanilla-ts --no-interactive");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

        // Step 3: Install Vite app dependencies
        await auto.TypeAsync("cd viteapp && npm install && cd ..");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

        // Step 4: Add Aspire.Hosting.JavaScript package
        // When channel is set (CI) and there's only one channel with one version,
        // the version is auto-selected without prompting.
        await auto.TypeAsync("aspire add Aspire.Hosting.JavaScript");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("The package Aspire.Hosting.", timeout: TimeSpan.FromMinutes(2));
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 5: Modify apphost.ts to add the Vite app
        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts");
        var newContent = """
            // Aspire TypeScript AppHost
            // For more information, see: https://aspire.dev

            import { createBuilder } from './.modules/aspire.js';

            const builder = await createBuilder();

            // Add the Vite frontend application
            const viteApp = await builder.addViteApp("viteapp", "./viteapp");

            await builder.build().run();
            """;

        File.WriteAllText(appHostPath, newContent);

        // Step 6: Run the apphost
        await auto.TypeAsync("aspire run");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Press CTRL+C to stop the apphost and exit.", timeout: TimeSpan.FromMinutes(3));

        // Step 7: Stop the apphost
        await auto.Ctrl().KeyAsync(Hex1bKey.C);
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    [Fact]
    public async Task InitTypeScriptAppHost_AugmentsExistingViteRepoAtRoot()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var installMode = CliE2ETestHelpers.DetectDockerInstallMode(repoRoot);
        var workspace = TemporaryWorkspace.Create(output);
        var localChannel = PrepareLocalChannel(repoRoot, workspace, installMode);
        var channelArgument = localChannel is not null ? " --channel local" : string.Empty;

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, installMode, output, variant: CliE2ETestHelpers.DockerfileVariant.DotNet, mountDockerSocket: true, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        string? originalDevScript = null;
        string? originalBuildScript = null;
        string? originalPreviewScript = null;
        string? originalTsConfig = null;

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliInDockerAsync(installMode, counter);

        await auto.EnablePolyglotSupportAsync(counter);

        if (localChannel is not null)
        {
            var containerLocalChannelPackagesPath = CliE2ETestHelpers.ToContainerPath(localChannel.PackagesPath, workspace);
            await auto.TypeAsync($"mkdir -p ~/.aspire/hives/local && rm -rf ~/.aspire/hives/local/packages && ln -s '{containerLocalChannelPackagesPath}' ~/.aspire/hives/local/packages");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);
        }

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
        originalTsConfig = File.ReadAllText(Path.Combine(projectRoot, "tsconfig.json"));

        if (localChannel is not null)
        {
            WriteLocalChannelSettings(projectRoot, localChannel.SdkVersion);
        }

        // Run aspire init in brownfield mode
        await auto.TypeAsync($"aspire init --language typescript --non-interactive{channelArgument}");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Created apphost.ts", timeout: TimeSpan.FromMinutes(2));
        await auto.DeclineAgentInitPromptAsync(counter);

        // Verify brownfield augmentation preserved existing config
        Assert.NotNull(originalDevScript);
        Assert.NotNull(originalBuildScript);
        Assert.NotNull(originalPreviewScript);
        Assert.NotNull(originalTsConfig);

        packageJson = JsonNode.Parse(File.ReadAllText(Path.Combine(projectRoot, "package.json")))!.AsObject();
        scripts = packageJson["scripts"]!.AsObject();
        var dependencies = packageJson["dependencies"]!.AsObject();
        var devDependencies = packageJson["devDependencies"]!.AsObject();

        Assert.Equal(originalDevScript, scripts["dev"]?.GetValue<string>());
        Assert.Equal(originalBuildScript, scripts["build"]?.GetValue<string>());
        Assert.Equal(originalPreviewScript, scripts["preview"]?.GetValue<string>());
        Assert.Equal("aspire run", scripts["aspire:start"]?.GetValue<string>());
        Assert.Equal("tsc -p tsconfig.apphost.json", scripts["aspire:build"]?.GetValue<string>());
        Assert.Equal("tsc --watch -p tsconfig.apphost.json", scripts["aspire:dev"]?.GetValue<string>());
        Assert.False(scripts.ContainsKey("start"));

        Assert.Equal("module", packageJson["type"]?.GetValue<string>());
        Assert.NotNull(dependencies["vscode-jsonrpc"]);
        Assert.NotNull(devDependencies["@types/node"]);
        Assert.NotNull(devDependencies["nodemon"]);
        Assert.NotNull(devDependencies["tsx"]);
        Assert.NotNull(devDependencies["typescript"]);

        Assert.Equal(originalTsConfig, File.ReadAllText(Path.Combine(projectRoot, "tsconfig.json")));
        Assert.True(File.Exists(Path.Combine(projectRoot, "tsconfig.apphost.json")));

        // Verify Aspire.Hosting.JavaScript was pre-added in config
        var configPath = Path.Combine(projectRoot, "aspire.config.json");
        var config = JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
        var packagesNode = config["packages"];
        Assert.NotNull(packagesNode);
        var packages = packagesNode!.AsObject();
        Assert.NotNull(packages["Aspire.Hosting.JavaScript"]);

        // Modify apphost.ts to add the Vite app before running
        var appHostPath = Path.Combine(projectRoot, "apphost.ts");
        var newContent = """
            // Aspire TypeScript AppHost
            // For more information, see: https://aspire.dev

            import { createBuilder } from './.modules/aspire.js';

            const builder = await createBuilder();

            await builder.addViteApp("brownfield", ".");

            await builder.build().run();
            """;

        File.WriteAllText(appHostPath, newContent);

        // Run the apphost to verify it works
        await auto.TypeAsync("aspire run");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Press CTRL+C to stop the apphost and exit.", timeout: TimeSpan.FromMinutes(3));

        await auto.Ctrl().KeyAsync(Hex1bKey.C);
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    private static void WriteLocalChannelSettings(string projectRoot, string sdkVersion)
    {
        var configPath = Path.Combine(projectRoot, "aspire.config.json");

        var config = new JsonObject
        {
            ["channel"] = "local",
            ["sdk"] = new JsonObject { ["version"] = sdkVersion }
        };

        File.WriteAllText(configPath, config.ToJsonString());
    }

    private static LocalChannelInfo? PrepareLocalChannel(
        string repoRoot,
        TemporaryWorkspace workspace,
        CliE2ETestHelpers.DockerInstallMode installMode)
    {
        if (installMode != CliE2ETestHelpers.DockerInstallMode.SourceBuild)
        {
            return null;
        }

        var shippingPackagesDirectory = Path.Combine(repoRoot, "artifacts", "packages", "Debug", "Shipping");
        if (!Directory.Exists(shippingPackagesDirectory))
        {
            throw new InvalidOperationException("Local source-built TypeScript E2E tests require packed Aspire packages. Run './build.sh --bundle --pack' first.");
        }

        var packageFiles = Directory.EnumerateFiles(shippingPackagesDirectory, "Aspire*.nupkg", SearchOption.TopDirectoryOnly)
            .Where(file => !file.EndsWith(".symbols.nupkg", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (!packageFiles.Any(file => Path.GetFileName(file).StartsWith("Aspire.Hosting.CodeGeneration.TypeScript.", StringComparison.OrdinalIgnoreCase)) ||
            !packageFiles.Any(file => Path.GetFileName(file).StartsWith("Aspire.Hosting.JavaScript.", StringComparison.OrdinalIgnoreCase)) ||
            !packageFiles.Any(file => Path.GetFileName(file).StartsWith("Aspire.Hosting.", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Local source-built TypeScript E2E tests require packed Aspire.Hosting, Aspire.Hosting.JavaScript, and Aspire.Hosting.CodeGeneration.TypeScript packages. Run './build.sh --bundle --pack' first.");
        }

        var localChannelPackagesPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire-local", "packages");
        Directory.CreateDirectory(localChannelPackagesPath);

        foreach (var packageFile in packageFiles)
        {
            File.Copy(packageFile, Path.Combine(localChannelPackagesPath, Path.GetFileName(packageFile)), overwrite: true);
        }

        var sdkVersion = packageFiles
            .Select(Path.GetFileName)
            .FirstOrDefault(fileName => fileName is not null && System.Text.RegularExpressions.Regex.IsMatch(fileName, @"^Aspire\.Hosting\.\d+\.\d+\.\d+.*\.nupkg$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            ?.Replace("Aspire.Hosting.", string.Empty, StringComparison.OrdinalIgnoreCase)
            ?.Replace(".nupkg", string.Empty, StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(sdkVersion))
        {
            throw new InvalidOperationException("Local source-built TypeScript E2E tests could not determine the Aspire SDK version from packed packages.");
        }

        return new LocalChannelInfo(localChannelPackagesPath, sdkVersion);
    }

    private sealed record LocalChannelInfo(string PackagesPath, string SdkVersion);
}
