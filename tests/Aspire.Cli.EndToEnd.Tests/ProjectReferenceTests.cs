// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end test for polyglot project reference support.
/// Creates a .NET hosting integration project and a TypeScript AppHost that references it
/// via <c>aspire.config.json</c>, then verifies the integration is discovered, code-generated, and functional.
/// </summary>
public sealed class ProjectReferenceTests(ITestOutputHelper output)
{
    [Fact]
    [ActiveIssue("https://github.com/microsoft/aspire/issues/15831")]
    public async Task TypeScriptAppHostWithProjectReferenceIntegration()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliAsync(strategy, counter);

        // Step 1: Create a TypeScript AppHost (so we get the SDK version in aspire.config.json)
        await auto.TypeAsync("aspire init --language typescript --non-interactive");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Created apphost.ts", timeout: TimeSpan.FromMinutes(2));
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 2: Create the integration project, update aspire.config.json, and modify apphost.ts
        {
            var workDir = workspace.WorkspaceRoot.FullName;

            // Read the SDK version from the aspire.config.json that aspire init created.
            var configPath = Path.Combine(workDir, "aspire.config.json");
            var configJson = File.ReadAllText(configPath);
            using var doc = JsonDocument.Parse(configJson);
            var sdkVersion = doc.RootElement.GetProperty("sdk").GetProperty("version").GetString()!;

            // Create the .NET hosting integration project
            var integrationDir = Path.Combine(workDir, "MyIntegration");
            Directory.CreateDirectory(integrationDir);

            File.WriteAllText(Path.Combine(integrationDir, "MyIntegration.csproj"), $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <NoWarn>$(NoWarn);ASPIREATS001</NoWarn>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Aspire.Hosting" Version="{{sdkVersion}}" />
                  </ItemGroup>
                </Project>
                """);

            // Write a nuget.config in the workspace root so MyIntegration.csproj can resolve
            // Aspire.Hosting from the configured channel's package source (hive or feed).
            // Without this, NuGet walks up from MyIntegration/ and finds no config pointing
            // to the hive, falling back to nuget.org which doesn't have prerelease versions.
            var aspireHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aspire");
            var hivesDir = Path.Combine(aspireHome, "hives");
            if (Directory.Exists(hivesDir))
            {
                var hiveDirs = Directory.GetDirectories(hivesDir);
                var sourceLines = new List<string> { """<add key="nuget.org" value="https://api.nuget.org/v3/index.json" />""" };
                foreach (var hiveDir in hiveDirs)
                {
                    var packagesDir = Path.Combine(hiveDir, "packages");
                    if (Directory.Exists(packagesDir))
                    {
                        var hiveName = Path.GetFileName(hiveDir);
                        sourceLines.Insert(0, $"""<add key="hive-{hiveName}" value="{packagesDir}" />""");
                    }
                }
                var nugetConfig = $"""
                    <?xml version="1.0" encoding="utf-8"?>
                    <configuration>
                      <packageSources>
                        <clear />
                        {string.Join("\n        ", sourceLines)}
                      </packageSources>
                    </configuration>
                    """;
                File.WriteAllText(Path.Combine(workDir, "nuget.config"), nugetConfig);
            }

            File.WriteAllText(Path.Combine(integrationDir, "MyIntegrationExtensions.cs"), """
                using Aspire.Hosting;
                using Aspire.Hosting.ApplicationModel;

                namespace Aspire.Hosting;

                public static class MyIntegrationExtensions
                {
                    [AspireExport]
                    public static IResourceBuilder<ContainerResource> AddMyService(
                        this IDistributedApplicationBuilder builder, string name)
                        => builder.AddContainer(name, "redis", "latest");
                }
                """);

            // Update aspire.config.json to add the project reference.
            var config = JsonNode.Parse(configJson)?.AsObject()
                ?? throw new InvalidOperationException("Expected aspire.config.json to contain a JSON object.");
            var packages = config["packages"] as JsonObject ?? new JsonObject();
            packages["MyIntegration"] = "./MyIntegration/MyIntegration.csproj";
            config["packages"] = packages;

            var updatedJson = config.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, updatedJson);

            // Delete the generated .modules folder to force re-codegen with the new integration
            var modulesDir = Path.Combine(workDir, ".modules");
            if (Directory.Exists(modulesDir))
            {
                Directory.Delete(modulesDir, recursive: true);
            }

            // Update apphost.ts to use the custom integration
            File.WriteAllText(Path.Combine(workDir, "apphost.ts"), """
                import { createBuilder } from './.modules/aspire.js';

                const builder = await createBuilder();
                await builder.addMyService("my-svc");
                await builder.build().run();
                """);
        }

        // Step 3: Start the AppHost (triggers project ref build + codegen)
        await auto.TypeAsync("aspire start --non-interactive 2>&1 | tee /tmp/aspire-start-output.txt");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(s =>
        {
            if (s.ContainsText(RunCommandStrings.AppHostFailedToBuild))
            {
                // Dump child logs before failing
                return true;
            }
            return s.ContainsText(RunCommandStrings.AppHostStartedSuccessfully);
        }, timeout: TimeSpan.FromMinutes(2), description: "waiting for apphost start success or failure");
        await auto.WaitForSuccessPromptAsync(counter);

        // If start failed, dump the child log for debugging before the test fails
        await auto.TypeAsync("CHILD_LOG=$(ls -t ~/.aspire/logs/cli_*detach*.log 2>/dev/null | head -1) && if [ -n \"$CHILD_LOG\" ]; then echo '=== CHILD LOG ==='; cat \"$CHILD_LOG\"; echo '=== END CHILD LOG ==='; fi");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(10));

        // Step 4: Verify the custom integration was code-generated
        await auto.TypeAsync("grep addMyService .modules/aspire.ts");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("addMyService", timeout: TimeSpan.FromSeconds(5));
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 5: Wait for the custom resource to be up
        await auto.TypeAsync("aspire wait my-svc --timeout 60");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(90));

        // Step 6: Verify the resource appears in describe
        await auto.TypeAsync("aspire describe my-svc --format json > /tmp/my-svc-describe.json");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(15));
        await auto.TypeAsync("cat /tmp/my-svc-describe.json");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("my-svc", timeout: TimeSpan.FromSeconds(5));
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 7: Clean up
        await auto.TypeAsync("aspire stop");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
