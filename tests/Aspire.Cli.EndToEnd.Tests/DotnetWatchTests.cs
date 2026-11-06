// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

public sealed class DotnetWatchTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task TypeScriptAppHostWatchesManagedServiceWithoutRestartingIt() => RunAsync("typescript");

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task CSharpAppHostReplacesItsGenerationAfterModelChanges() => RunAsync("csharp");

    private async Task RunAsync(string language)
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        if (strategy.Mode == CliInstallMode.InstallScript && strategy.Quality is null && strategy.Version is null)
        {
            Assert.Skip("Experimental .NET watch requires current repository bits, not the latest released CLI.");
        }
        using var workspace = TemporaryWorkspace.Create(output);
        var localChannel = CliE2ETestHelpers.PrepareLocalChannel(
            repoRoot, strategy, ["Aspire.Hosting.CodeGeneration.TypeScript.", "Aspire.Hosting.Dotnet."]);
        var preinstalled = strategy.Mode == CliInstallMode.Preinstalled;
        using var terminal = preinstalled
            ? CliE2ETestHelpers.CreateTestTerminal()
            : CliE2ETestHelpers.CreateDockerTestTerminal(
                repoRoot, strategy, output, mountDockerSocket: false, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(
            terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);
        if (preinstalled)
        {
            await auto.PrepareEnvironmentAsync(workspace, counter);
            // MTP inherits the repository SDK paths; the fixture must select its own SDK.
            await auto.RunCommandAsync("unset MSBuildSDKsPath MSBuildExtensionsPath DOTNET_HOST_PATH", counter);
            await auto.InstallAspireCliInShellAsync(strategy, counter);
        }
        else
        {
            await auto.PrepareDockerEnvironmentAsync(counter, workspace);
            await auto.InstallAspireCliAsync(strategy, counter);
        }
        if (language == "csharp")
        {
            await File.WriteAllTextAsync(Path.Combine(workspace.Path, "Watch.slnx"), "<Solution />");
        }
        await auto.RunCommandAsync(
            $"aspire init --language {language} --non-interactive --suppress-agent-init", counter);
        if (localChannel is not null)
        {
            CliE2ETestHelpers.WriteLocalChannelSettings(workspace.Path, localChannel.SdkVersion);
        }
        if (language == "typescript")
        {
            // Hosting.Dotnet is a managed integration, not a native protocol-v2 provider.
            var path = Path.Combine(workspace.Path, "aspire.config.json");
            var configuration = Assert.IsType<JsonObject>(JsonNode.Parse(await File.ReadAllTextAsync(path)));
            configuration["features"] ??= new JsonObject();
            Assert.IsType<JsonObject>(configuration["features"])["appHostServerV2"] = false;
            await File.WriteAllTextAsync(path, configuration.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            var path = Path.Combine(workspace.Path, "Watch.AppHost", "Watch.AppHost.csproj");
            var project = XDocument.Load(path);
            Assert.Single(project.Descendants("TargetFramework")).Value = "net10.0";
            project.Save(path);
        }
        await auto.RunCommandAsync("aspire add Aspire.Hosting.Dotnet --non-interactive", counter);
        var hostingVersion = language == "csharp"
            ? CliE2ETestHelpers.GetAppHostVersion(await File.ReadAllTextAsync(
                Path.Combine(workspace.Path, "Watch.AppHost", "Watch.AppHost.csproj")))
            : (Assert.IsType<JsonObject>(JsonNode.Parse(await File.ReadAllTextAsync(
                Path.Combine(workspace.Path, "aspire.config.json"))))["sdk"]?["version"]?.GetValue<string>()
                ?? throw new InvalidOperationException("The TypeScript fixture must identify its Hosting package version."));
        var shared = Directory.CreateDirectory(Path.Combine(workspace.Path, "Shared"));
        await File.WriteAllTextAsync(Path.Combine(shared.FullName, "Shared.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(shared.FullName, "SharedValue.cs"), """
            namespace WatchShared;
            public static class SharedValue
            {
                public static string Read() => "shared-before";
            }
            """);
        foreach (var name in new[] { "Api", "Worker" })
        {
            var directory = Directory.CreateDirectory(Path.Combine(workspace.Path, name));
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, name + ".csproj"), """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup><ProjectReference Include="../Shared/Shared.csproj" /></ItemGroup>
            </Project>
            """);
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "Program.cs"), """
            var builder = WebApplication.CreateBuilder(args);
            var app = builder.Build();
            app.MapGet("/health", () => "ok");
            app.MapGet("/", () => new
            {
                value = ValueSource.Read(),
                shared = WatchShared.SharedValue.Read(),
                pid = Environment.ProcessId,
                configured = Environment.GetEnvironmentVariable("WATCH_MODEL_VALUE")
            });
            app.Run();

            public class ValueSource
            {
                public static string Read() => "before";
            }

            public class ReplacementBase
            {
            }
            """);
        }
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "ordinary.cs"), $$"""
            #:sdk Microsoft.NET.Sdk.Web
            #:property PublishAot=true
            #:package Aspire.Hosting@{{hostingVersion}}

            using Aspire.Hosting;
            using System.Text.Json.Serialization;

            using var hostingProbe = DistributedApplication.CreateBuilder(
                new DistributedApplicationOptions { DisableDashboard = true }).Build();
            var builder = WebApplication.CreateBuilder(args);
            var app = builder.Build();
            app.MapGet("/health", () => "ok");
            app.MapGet("/", () => Results.Json(new OrdinaryProbe(
                Environment.ProcessId, true,
                Environment.GetEnvironmentVariable("ASPIRE_DOTNET_WATCH_LAUNCH_OWNER"),
                Environment.GetEnvironmentVariable("AppHost__Run__WatchEnabled"),
                Environment.GetEnvironmentVariable("ASPIRE_DOTNET_WATCH_HOST_CONTROL"),
                Environment.GetEnvironmentVariable("ASPIRE_DOTNET_WATCH_HOST_SESSION")),
                OrdinaryJsonContext.Default.OrdinaryProbe));
            app.Run();

            record OrdinaryProbe(int Pid, bool HostingLoaded, string? WatchOwner, string? WatchEnabled, string? HostControl, string? HostSession);
            [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
            [JsonSerializable(typeof(OrdinaryProbe))]
            partial class OrdinaryJsonContext : JsonSerializerContext;
            """);
        if (language == "typescript")
        {
            await File.WriteAllTextAsync(Path.Combine(workspace.Path, "apphost.mts"), """
                import { createBuilder } from './.aspire/modules/aspire.mjs';
                const builder = await createBuilder();
                await builder.addDotnetProject('api', './Api/Api.csproj')
                    .withHttpEndpoint({ env: 'ASPNETCORE_HTTP_PORTS' })
                    .withHttpHealthCheck({ path: '/health' })
                    .withEnvironment('WATCH_MODEL_VALUE', 'one');
                await builder.addDotnetProject('worker', './Worker/Worker.csproj')
                    .withHttpEndpoint({ env: 'ASPNETCORE_HTTP_PORTS' })
                    .withHttpHealthCheck({ path: '/health' });
                await builder.addDotnetProject('ordinary', './ordinary.cs')
                    .withHttpEndpoint({ env: 'ASPNETCORE_HTTP_PORTS' })
                    .withHttpHealthCheck({ path: '/health' });
                await builder.build().run();
                """);
        }
        else
        {
            var path = Path.Combine(workspace.Path, "Watch.AppHost", "AppHost.cs");
            await File.WriteAllTextAsync(path, """
                if (Environment.GetEnvironmentVariable("WATCH_STARTUP_GATE") is { } gate)
                {
                    File.WriteAllText(gate + ".entered", Environment.ProcessId.ToString());
                    while (!File.Exists(gate + ".release"))
                    {
                        await Task.Delay(25);
                    }
                }
                var builder = DistributedApplication.CreateBuilder(args);
                builder.AddDotnetProject("api", "../Api/Api.csproj")
                    .WithHttpEndpoint(env: "ASPNETCORE_HTTP_PORTS")
                    .WithHttpHealthCheck("/health")
                    .WithEnvironment("WATCH_MODEL_VALUE", "one");
                builder.AddDotnetProject("worker", "../Worker/Worker.csproj")
                    .WithHttpEndpoint(env: "ASPNETCORE_HTTP_PORTS")
                    .WithHttpHealthCheck("/health");
                builder.AddDotnetProject("ordinary", "../ordinary.cs")
                    .WithHttpEndpoint(env: "ASPNETCORE_HTTP_PORTS")
                    .WithHttpHealthCheck("/health");
                if (Environment.GetEnvironmentVariable("WATCH_VERIFY_WORKLOAD") == "true")
                {
                    builder.AddContainer("watch-owned-workload", "mcr.microsoft.com/dotnet/sdk", "10.0")
                        .WithEntrypoint("/bin/sh")
                        .WithArgs("-c", "sleep 600");
                }
                builder.Build().Run();
                """);
        }
        using var asset = Assembly.GetExecutingAssembly().GetManifestResourceStream("Aspire.Cli.EndToEnd.Tests.verify-watch.py")
            ?? throw new InvalidOperationException("The watch verification asset was not embedded.");
        using var reader = new StreamReader(asset);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "verify-watch.py"), await reader.ReadToEndAsync());
        try
        {
            await auto.RunCommandAsync($"python3 verify-watch.py {language}", counter, TimeSpan.FromMinutes(12));
        }
        catch (InvalidOperationException)
        {
            counter.Increment();
            throw;
        }
    }
}
