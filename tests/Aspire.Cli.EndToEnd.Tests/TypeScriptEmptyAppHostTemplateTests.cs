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
/// End-to-end tests for the TypeScript Empty AppHost template (aspire-ts-empty).
/// Validates that aspire new creates a working TypeScript AppHost project
/// and that aspire start runs it successfully.
/// </summary>
public sealed class TypeScriptEmptyAppHostTemplateTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task CreateAndRunTypeScriptEmptyAppHostProject()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        await auto.AspireNewAsync("TsEmptyApp", counter, template: AspireTemplate.TypeScriptEmptyAppHost);

        GitIgnoreAssertions.AssertContainsEntry(
            Path.Combine(workspace.WorkspaceRoot.FullName, "TsEmptyApp"),
            ".aspire/");

        // Start the empty TypeScript AppHost to verify the scaffolded project works
        await auto.TypeAsync("cd TsEmptyApp");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.RunCommandAsync("npm run build", counter, TimeSpan.FromMinutes(2));

        await auto.AspireStartAsync(counter);
        await auto.AspireStopAsync(counter);
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task TypeScriptAppHostRunDoesNotDeadlockWhenLazyOptionsInvokeAsyncCallback()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace, enableDcpDiagnostics: true);
        await auto.InstallAspireCliAsync(strategy, counter);

        await auto.AspireNewTypeScriptEmptyAppHostAsync("TsDeadlockRepro", counter);

        var appDirectory = Path.Combine(workspace.WorkspaceRoot.FullName, "TsDeadlockRepro");
        WriteDeadlockReproFiles(appDirectory);

        await auto.RunCommandAsync("cd TsDeadlockRepro", counter);
        await auto.RunCommandAsync("aspire restore --non-interactive", counter, TimeSpan.FromMinutes(3));
        await auto.RunCommandAsync("npm run build", counter, TimeSpan.FromMinutes(2));

        await auto.AspireStartAsync(counter, startTimeout: TimeSpan.FromMinutes(2));
        await auto.AspireStopAsync(counter);
    }

    private static void WriteDeadlockReproFiles(string appDirectory)
    {
        var sdkVersion = GetSdkVersion(appDirectory);
        var extensionDirectory = Directory.CreateDirectory(Path.Combine(appDirectory, "DeadlockExtension"));

        File.WriteAllText(Path.Combine(extensionDirectory.FullName, "DeadlockExtension.csproj"), $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <NoWarn>$(NoWarn);ASPIREATS001</NoWarn>
              </PropertyGroup>

              <ItemGroup>
                <PackageReference Include="Aspire.Hosting" Version="{{sdkVersion}}" />
              </ItemGroup>
            </Project>
            """);

        File.WriteAllText(Path.Combine(extensionDirectory.FullName, "DeadlockExtensions.cs"), """
            using Aspire.Hosting;
            using Aspire.Hosting.ApplicationModel;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Options;

            namespace DeadlockExtension;

            public static class DeadlockExtensions
            {
                [AspireExport(RunSyncOnBackgroundThread = true)]
                public static IDistributedApplicationBuilder AddLazyOptionsDeadlockRepro(
                    this IDistributedApplicationBuilder builder,
                    Action<DeadlockOptions>? configure = null)
                {
                    builder.Services.AddOptions<DeadlockOptions>()
                        .Configure(options => configure?.Invoke(options));

                    builder.Eventing.Subscribe<BeforeStartEvent>((@event, _) =>
                    {
                        var options = @event.Services.GetRequiredService<IOptions<DeadlockOptions>>().Value;
                        if (options.SomeProperty is not "value-from-typescript")
                        {
                            throw new InvalidOperationException($"Expected TypeScript callback to set SomeProperty, but got '{options.SomeProperty ?? "<null>"}'.");
                        }

                        return Task.CompletedTask;
                    });

                    return builder;
                }
            }

            [AspireExport(ExposeProperties = true)]
            public sealed class DeadlockOptions
            {
                public string? SomeProperty { get; set; }
            }
            """);

        File.WriteAllText(Path.Combine(appDirectory, "apphost.mts"), """
            import { createBuilder } from './.aspire/modules/aspire.mjs';

            const builder = await createBuilder();

            await builder.addLazyOptionsDeadlockRepro({
                configure: async (options) => {
                    await options.someProperty.set("value-from-typescript");
                }
            });

            await builder.build().run();
            """);

        var configPath = Path.Combine(appDirectory, "aspire.config.json");
        var config = JsonNode.Parse(File.ReadAllText(configPath))?.AsObject()
            ?? throw new InvalidOperationException($"Unable to read {configPath}.");
        config["packages"] = new JsonObject
        {
            ["DeadlockExtension"] = "DeadlockExtension/DeadlockExtension.csproj"
        };
        File.WriteAllText(configPath, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string GetSdkVersion(string appDirectory)
    {
        var configPath = Path.Combine(appDirectory, "aspire.config.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
        return doc.RootElement.GetProperty("sdk").GetProperty("version").GetString()
            ?? throw new InvalidOperationException("Expected aspire.config.json to contain sdk.version.");
    }
}
