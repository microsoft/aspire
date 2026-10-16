// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Backchannel;
using Aspire.Hosting.Dcp.Process;
using Aspire.Hosting.Diagnostics;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Go.Tests;

public class GoRunDebuggingTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task GoFlagsProbeDoesNotResolveUnrelatedApplicationValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var runner = new TestProcessRunner();
        runner.EnqueueResult(output: [JsonSerializer.Serialize(new { GOFLAGS = "" })]);
        builder.Services.AddSingleton<IProcessRunner>(runner);
        var go = builder.AddGoApp("api", builder.AppHostDirectory);
        var unrelated = builder.AddParameter("unrelated", () => throw new InvalidOperationException("Unrelated value was resolved."));

        await using var app = builder.Build();
        var context = new EnvironmentCallbackContext(builder.ExecutionContext, go.Resource);
        context.EnvironmentVariables["UNRELATED"] = unrelated.Resource;

        await GoRunDebugging.ConfigureEnvironmentAsync(context);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task GoFlagsProbeUsesConfiguredCommandAndPreservesItsFlags(string? resourceFlags)
    {
        const string flags = "-tags=wrapper -ldflags=-X=main.version=wrapper";
        using var builder = TestDistributedApplicationBuilder.Create();
        var command = Path.Combine(builder.AppHostDirectory, "custom go");
        var runner = new TestProcessRunner();
        runner.EnqueueResult(output: [JsonSerializer.Serialize(new { GOFLAGS = flags })]);
        builder.Services.AddSingleton<IProcessRunner>(runner);
        var go = builder.AddGoApp("api", builder.AppHostDirectory)
            .WithCommand(command);
        if (resourceFlags is not null)
        {
            go.WithEnvironment("GOFLAGS", resourceFlags);
        }

        var environment = await GetEnvironmentAsync(builder, go.Resource);

        var probe = Assert.Single(runner.ProcessSpecs);
        Assert.Equal(command, probe.ExecutablePath);
        Assert.Equal(["env", "-json", "GOFLAGS"], probe.ArgumentList);
        Assert.Equal(
            "\"-ldflags=-s=false -w=false\" -tags=wrapper -ldflags=-X=main.version=wrapper \"-ldflags=-X=main.version=wrapper -s=false -w=false\"",
            environment["GOFLAGS"]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ResourceGoFlagsAreMergedAfterModelConfigurationAndPreserveSensitivity(
        bool useEnvironmentCallback, bool secret)
    {
        const string flags = "-ldflags=-X=main.version=1.2.3";
        const string expectedFlags = "\"-ldflags=-s=false -w=false\" -ldflags=-X=main.version=1.2.3 \"-ldflags=-X=main.version=1.2.3 -s=false -w=false\"";
        using var builder = TestDistributedApplicationBuilder.Create();
        var runner = new TestProcessRunner();
        builder.Services.AddSingleton<IProcessRunner>(runner);
        var parameter = builder.AddParameter("linker-flags", () => flags, secret: secret);
        var go = builder.AddGoApp("api", builder.AppHostDirectory)
            .WithEnvironment("PUBLIC_FLAGS", "-tags=netgo");
        if (useEnvironmentCallback)
        {
            go.WithEnvironment(context => context.EnvironmentVariables["GOFLAGS"] = parameter);
        }
        else
        {
            go.WithEnvironment("GOFLAGS", parameter);
        }

        await using var app = builder.Build();
        await builder.Eventing.PublishAsync(new BeforeStartEvent(
            app.Services, app.Services.GetRequiredService<DistributedApplicationModel>()));
        var configuration = await ExecutionConfigurationBuilder.Create(go.Resource)
            .WithEnvironmentVariablesConfig()
            .BuildAsync(builder.ExecutionContext);

        Assert.Null(configuration.Exception);
        Assert.Equal(expectedFlags, Assert.Single(configuration.EnvironmentVariables, variable => variable.Key == "GOFLAGS").Value);

        await app.Services.GetRequiredService<ResourceNotificationService>().PublishUpdateAsync(go.Resource, snapshot => snapshot with
        {
            EnvironmentVariables = [.. configuration.EnvironmentVariables
                .Select(variable => new EnvironmentVariableSnapshot(variable.Key, variable.Value, true))]
        });
        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);
        var snapshot = Assert.Single(await target.GetResourceSnapshotsAsync(), resource => resource.DisplayName == "api");

        Assert.Equal(secret ? null : expectedFlags, Assert.Single(snapshot.EnvironmentVariables, variable => variable.Name == "GOFLAGS").Value);
        Assert.Equal("-tags=netgo", Assert.Single(snapshot.EnvironmentVariables, variable => variable.Name == "PUBLIC_FLAGS").Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitLinkerOptionsAndDelveDoNotReceiveRunDefaults(bool delve)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var runner = new TestProcessRunner();
        builder.Services.AddSingleton<IProcessRunner>(runner);
        var go = builder.AddGoApp("api", builder.AppHostDirectory, ldFlags: delve ? null : "-s -w");
        if (delve)
        {
            go.WithDelveServer();
        }

        var environment = await GetEnvironmentAsync(builder, go.Resource);

        Assert.False(environment.ContainsKey("GOFLAGS"));
        Assert.Empty(runner.ProcessSpecs);
    }

    [Theory]
    [InlineData("\"-ldflags=-w")]
    [InlineData("-ldflags=-X=main.version=a'\"b")]
    public async Task UnsupportedGoFlagsAreRejected(string flags)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var go = builder.AddGoApp("api", builder.AppHostDirectory)
            .WithEnvironment("GOFLAGS", flags);

        await Assert.ThrowsAsync<FormatException>(() => GetEnvironmentAsync(builder, go.Resource));
    }

    [Theory]
    [InlineData(false, "", "unset", false)]
    [InlineData(false, "-ldflags=-X=main.version=1.2.3", "1.2.3", false)]
    [InlineData(false, "'-ldflags=-X \"main.version=hello world\"'", "hello world", false)]
    [InlineData(false, "-ldflags=all=-X=main.version=1.2.3", "1.2.3", false)]
    [InlineData(true, "-ldflags=-X=main.version=1.2.3", "1.2.3", false)]
    [InlineData(false, "-tags=stamp -ldflags=-X=main.version=1.2.3", "1.2.3", true)]
    [InlineData(false, "-ldflags=all=-X=main.version=all -ldflags=example.com/debugflags=-X=main.version=specific", "specific", false)]
    [InlineData(false, "-ldflags=example.com/debugflags=-X=main.version=specific -ldflags=all=-X=main.version=all", "all", false)]
    [InlineData(false, "-ldflags=-s", "unset", false)]
    [InlineData(false, "-ldflags=", "unset", false)]
    [RequiresTools(["go"])]
    public async Task GoRunPreservesVersionStampAndDebugSymbols(
        bool persisted, string flags, string expectedVersion, bool expectedTagged)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, "go.mod"), "module example.com/debugflags\n\ngo 1.18\n");
        File.WriteAllText(Path.Combine(workspace.Path, "main.go"), """
            package main

            import (
                "debug/dwarf"
                "debug/elf"
                "debug/macho"
                "debug/pe"
                "encoding/json"
                "os"
                "runtime"
            )

            var version = "unset"
            var tagged = false

            type executable interface {
                DWARF() (*dwarf.Data, error)
                Close() error
            }

            func main() {
                path, err := os.Executable()
                if err != nil { panic(err) }
                var image executable
                var hasSymbols bool
                switch runtime.GOOS {
                case "windows":
                    file, err := pe.Open(path)
                    if err != nil { panic(err) }
                    image = file
                    hasSymbols = len(file.Symbols) > 0
                case "darwin":
                    file, err := macho.Open(path)
                    if err != nil { panic(err) }
                    image = file
                    hasSymbols = file.Symtab != nil && len(file.Symtab.Syms) > 0
                default:
                    file, err := elf.Open(path)
                    if err != nil { panic(err) }
                    image = file
                    symbols, symbolErr := file.Symbols()
                    hasSymbols = symbolErr == nil && len(symbols) > 0
                }
                defer image.Close()
                data, err := image.DWARF()
                if err != nil { panic(err) }
                entry, err := data.Reader().Next()
                if err != nil { panic(err) }
                if err := json.NewEncoder(os.Stdout).Encode(map[string]any{
                    "version": version,
                    "dwarf": entry != nil,
                    "symbols": hasSymbols,
                    "tagged": tagged,
                }); err != nil { panic(err) }
            }
            """);
        File.WriteAllText(Path.Combine(workspace.Path, "tag.go"), """
            //go:build stamp

            package main

            func init() { tagged = true }
            """);

        using var builder = TestDistributedApplicationBuilder.Create();
        var go = builder.AddGoApp("api", workspace.Path)
            .WithEnvironment("GOFLAGS", persisted ? "" : flags)
            .WithEnvironment("GOENV", "off");
        if (persisted)
        {
            var goEnvironment = Path.Combine(workspace.Path, "go.env");
            File.WriteAllText(goEnvironment, $"GOFLAGS={flags}\n");
            go.WithEnvironment("GOENV", goEnvironment);
        }

        await using var app = builder.Build();
        await builder.Eventing.PublishAsync(new BeforeStartEvent(
            app.Services, app.Services.GetRequiredService<DistributedApplicationModel>()));
        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(go.Resource, serviceProvider: app.Services);
        var arguments = await ArgumentEvaluator.GetArgumentListAsync(go.Resource, app.Services);
        var output = new StringBuilder();
        var (resultTask, process) = app.Services.GetRequiredService<IProcessRunner>().Run(new ProcessSpec(go.Resource.Command)
        {
            WorkingDirectory = go.Resource.WorkingDirectory,
            ArgumentList = arguments,
            EnvironmentVariables = environment,
            ResolveExecutablePath = true,
            OnOutputData = line => output.AppendLine(line)
        });

        await using (process)
        {
            var result = await resultTask.WaitAsync(TimeSpan.FromMinutes(2));
            Assert.Equal(0, result.ExitCode);
        }

        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal(expectedVersion, document.RootElement.GetProperty("version").GetString());
        Assert.True(document.RootElement.GetProperty("dwarf").GetBoolean());
        Assert.True(document.RootElement.GetProperty("symbols").GetBoolean());
        Assert.Equal(expectedTagged, document.RootElement.GetProperty("tagged").GetBoolean());
    }

    private static async Task<Dictionary<string, string>> GetEnvironmentAsync(
        IDistributedApplicationBuilder builder, GoAppResource resource)
    {
        await using var app = builder.Build();
        await builder.Eventing.PublishAsync(new BeforeStartEvent(
            app.Services, app.Services.GetRequiredService<DistributedApplicationModel>()));

        return await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(resource, serviceProvider: app.Services);
    }
}
