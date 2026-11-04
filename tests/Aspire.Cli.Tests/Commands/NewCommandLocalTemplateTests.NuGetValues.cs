// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Resources;
using Aspire.Cli.Templating;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Versioning;
using RootCommand = Aspire.Cli.Commands.RootCommand;

namespace Aspire.Cli.Tests.Commands;

public partial class NewCommandLocalTemplateTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LocalTemplate_ComputesPackageVersionFromTemplatePolicy(bool prerelease, bool interactive)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var feed = workspace.CreateDirectory("feed");
        foreach (var version in new[] { "13.6.2", "13.6.3-preview.2", "13.6.3-preview.10", "13.7.0" })
        {
            NuGetTestHelper.CreatePackage(feed, "Aspire.Hosting.Dotnet", version, new Dictionary<string, string>());
        }
        await CreateComputedVersionTemplateAsync(workspace, $$"""
            {
              "kind": "nugetPackageVersion",
              "packageId": "aspire.hosting.dotnet",
              "versionRange": "[13.6.0-preview.0,13.7.0)",
              "allowPrerelease": {{prerelease.ToString().ToLowerInvariant()}}
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "template", "NuGet.Config"), """
            <configuration>
              <packageSources><clear /><add key="template-feed" value="../feed" /></packageSources>
              <packageSourceMapping>
                <clear /><packageSource key="template-feed"><package pattern="Aspire.Hosting.Dotnet" /></packageSource>
              </packageSourceMapping>
            </configuration>
            """);
        var interaction = new TestInteractionService();
        var services = CreateServices(workspace, interaction, interactive);
        services.AddSingleton(workspace.CreateExecutionContext(identityChannel: "unavailable", identityVersion: "13.6.3"));
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.True(exitCode == CliExitCodes.Success, string.Join(Environment.NewLine, interaction.DisplayedErrors));
        Assert.Empty(interaction.StringPromptCalls);
        Assert.Empty(Directory.GetFileSystemEntries(workspace.Path, ".aspire-template-*"));
        await Verify(ReadOutput(workspace)).UseParameters(prerelease, interactive);
    }

    [Theory]
    [InlineData("ancestor")]
    [InlineData("nested")]
    [InlineData("channel")]
    [InlineData("embedded")]
    public async Task LocalTemplate_ComputedVersionUsesEffectiveNuGetConfiguration(string policy)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var feed = workspace.CreateDirectory("feed");
        NuGetTestHelper.CreatePackage(feed, "Aspire.Hosting.Dotnet", "13.6.1-preview.3", new Dictionary<string, string>());
        await CreateComputedVersionTemplateAsync(workspace, $$"""
            {
              "kind": "nugetPackageVersion", "packageId": "Aspire.Hosting.Dotnet",
              "versionRange": "[13.6.0-preview.0,13.7.0)", "allowPrerelease": true,
              "workingDirectory": "{{(policy == "nested" ? "AppHost" : ".")}}"
            }
            """);
        var config = """
            <configuration>
              <packageSources><clear /><add key="chosen" value="feed" /></packageSources>
              <packageSourceMapping><clear /><packageSource key="chosen"><package pattern="*" /></packageSource></packageSourceMapping>
            </configuration>
            """;
        if (policy == "ancestor")
        {
            await File.WriteAllTextAsync(Path.Combine(workspace.Path, "NuGet.Config"), config);
        }
        else if (policy == "nested")
        {
            workspace.CreateDirectory("template/AppHost");
            await File.WriteAllTextAsync(Path.Combine(workspace.Path, "template", "AppHost", "nuget.config"),
                config.Replace("value=\"feed\"", "value=\"../../feed\"", StringComparison.Ordinal));
            await File.WriteAllTextAsync(Path.Combine(workspace.Path, "template", "nuget.config"),
                "<configuration><packageSources><clear /></packageSources><packageSourceMapping><clear /></packageSourceMapping></configuration>");
        }
        else if (policy == "embedded")
        {
            var embedded = workspace.CreateDirectory("template/feed");
            File.Copy(Path.Combine(feed.FullName, "Aspire.Hosting.Dotnet.13.6.1-preview.3.nupkg"),
                Path.Combine(embedded.FullName, "Aspire.Hosting.Dotnet.13.6.1-preview.3.nupkg"));
            await File.WriteAllTextAsync(Path.Combine(workspace.Path, "template", "nuget.config"), config);
        }
        else
        {
            await File.WriteAllTextAsync(Path.Combine(workspace.Path, "NuGet.Config"),
                "<configuration><packageSources><clear /></packageSources></configuration>");
            var host = Path.Combine(workspace.Path, "template", ".template.config", "aspirecli.host.json");
            var metadata = JsonNode.Parse(await File.ReadAllTextAsync(host))!;
            metadata["createNuGetConfig"] = true;
            await File.WriteAllTextAsync(host, metadata.ToJsonString());
        }
        var interaction = new TestInteractionService();
        var services = CreateServices(workspace, interaction, interactive: false);
        services.AddSingleton(workspace.CreateExecutionContext(identityChannel: "daily", identityVersion: "13.6.3"));
        services.AddSingleton<IPackagingService>(new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>(
            [
                PackageChannel.CreateExplicitChannel("daily", PackageChannelQuality.Prerelease,
                    [new PackageMapping("Aspire*", feed.FullName)],
                    new FakeNuGetPackageCache(), new TestFeatures(), NullLogger.Instance)
            ])
        });
        using var provider = services.BuildServiceProvider();
        workspace.CreateDirectory("out");

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.True(exitCode == CliExitCodes.Success, string.Join(Environment.NewLine, interaction.DisplayedErrors));
        var output = Path.Combine(workspace.Path, "out");
        Assert.Equal(
            [policy == "embedded" ? Path.Combine(output, "feed") : feed.FullName],
            NuGetTestHelper.GetEligiblePackageSources(policy == "nested" ? Path.Combine(output, "AppHost") : output, "Aspire.Hosting.Dotnet"));
        await Verify(await File.ReadAllTextAsync(Path.Combine(output, "versions.json"))).UseParameters(policy);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"kind":"unknown","packageId":"Example","versionRange":"*"}""")]
    [InlineData("""{"kind":"nugetPackageVersion","packageId":"bad id","versionRange":"*"}""")]
    [InlineData("""{"kind":"nugetPackageVersion","packageId":"Example","versionRange":"invalid"}""")]
    [InlineData("""{"kind":"nugetPackageVersion","packageId":"Example","versionRange":"*","allowPrerelease":"true"}""")]
    [InlineData("""{"kind":"nugetPackageVersion","packageId":"Example","versionRange":"*","workingDirectory":null}""")]
    public async Task LocalTemplate_RejectsInvalidComputedValueMetadata(string valueSource)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateComputedVersionTemplateAsync(workspace, valueSource);
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: false).BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.NotEmpty(interaction.DisplayedErrors);
        Assert.False(Directory.Exists(Path.Combine(workspace.Path, "out")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LocalTemplate_ComputedLookupFailureDoesNotPublishOrUseDefault(bool queryFails, bool existingOutput)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateComputedVersionTemplateAsync(workspace, """
            {"kind":"nugetPackageVersion","packageId":"Aspire.Hosting.Dotnet","versionRange":"[13.6.0,13.7.0)"}
            """);
        var interaction = new TestInteractionService();
        var services = CreateServices(workspace, interaction, interactive: false);
        services.AddSingleton<INuGetClient>(new FakeNuGetClient
        {
            GetPackageVersionsCallback = (_, _, _) => queryFails
                ? throw new NuGetOperationException("Lookup failed.")
                : Task.FromResult<IReadOnlyList<NuGetVersion>>([NuGetVersion.Parse("13.6.1-preview.1"), NuGetVersion.Parse("13.7.0")])
        });
        using var provider = services.BuildServiceProvider();
        if (existingOutput)
        {
            workspace.CreateDirectory("out");
        }

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToCreateNewProject, exitCode);
        Assert.Single(interaction.DisplayedErrors);
        Assert.Empty(interaction.DisplayedSuccess);
        Assert.Equal(existingOutput, Directory.Exists(Path.Combine(workspace.Path, "out")));
        if (existingOutput)
        {
            Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(workspace.Path, "out")));
        }
        Assert.Empty(Directory.GetFileSystemEntries(workspace.Path, ".aspire-template-*"));
    }

    [Theory]
    [InlineData("../feed")]
    [InlineData("missing")]
    [InlineData("/")]
    public async Task LocalTemplate_ComputedWorkingDirectoryCannotEscapeOrFallBack(string directory)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        workspace.CreateDirectory("feed");
        await CreateComputedVersionTemplateAsync(workspace, $$"""
            {"kind":"nugetPackageVersion","packageId":"Example","versionRange":"*","workingDirectory":"{{directory}}"}
            """);
        var interaction = new TestInteractionService();
        var services = CreateServices(workspace, interaction, interactive: false);
        services.AddSingleton<INuGetClient>(new FakeNuGetClient
        {
            GetPackageVersionsCallback = (_, _, _) => throw new InvalidOperationException("Invalid directories must not query feeds.")
        });
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.False(Directory.Exists(Path.Combine(workspace.Path, "out")));
        Assert.Empty(Directory.GetFileSystemEntries(workspace.Path, ".aspire-template-*"));
    }

    [Fact]
    public async Task LocalTemplate_ComputedValuesCannotChangeTheirNuGetPolicy()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateComputedVersionTemplateAsync(workspace, """
            {"kind":"nugetPackageVersion","packageId":"Example","versionRange":"[1.0.0,2.0.0)"}
            """);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "template", "nuget.config"), """
            <configuration><packageSources><clear /><add key="self" value="../feed/IntegrationVersionValue" /></packageSources></configuration>
            """);
        var interaction = new TestInteractionService();
        var services = CreateServices(workspace, interaction, interactive: false);
        services.AddSingleton<INuGetClient>(new FakeNuGetClient
        {
            GetPackageVersionsCallback = (_, _, _) => Task.FromResult<IReadOnlyList<NuGetVersion>>([NuGetVersion.Parse("1.5.0")])
        });
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToCreateNewProject, exitCode);
        Assert.Equal(TemplatingStrings.TemplateNuGetConfigurationChanged, Assert.Single(interaction.DisplayedErrors));
        Assert.False(Directory.Exists(Path.Combine(workspace.Path, "out")));
        Assert.Empty(Directory.GetFileSystemEntries(workspace.Path, ".aspire-template-*"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalTemplate_ComputedValuesCannotControlPrompting(bool interactive)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateComputedVersionTemplateAsync(workspace, """
            {"kind":"nugetPackageVersion","packageId":"Example","versionRange":"*"}
            """);
        var path = Path.Combine(workspace.Path, "template", ".template.config", "template.json");
        var template = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        template["symbols"]!["Dependent"] = new JsonObject
        {
            ["type"] = "parameter", ["datatype"] = "string", ["isEnabled"] = "(IntegrationVersion == \"1.0.0\")"
        };
        await File.WriteAllTextAsync(path, template.ToJsonString());
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive).BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToCreateNewProject, exitCode);
        Assert.False(Directory.Exists(Path.Combine(workspace.Path, "out")));
        Assert.Empty(interaction.StringPromptCalls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LocalTemplate_OnlyQueriesEnabledComputedParameters(bool enabled, bool interactive)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateComputedVersionTemplateAsync(workspace, """
            {"kind":"nugetPackageVersion","packageId":"Example","versionRange":"*"}
            """);
        var path = Path.Combine(workspace.Path, "template", ".template.config", "template.json");
        var template = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        template["symbols"]!["IncludeIntegration"] = new JsonObject
        {
            ["type"] = "parameter", ["datatype"] = "bool", ["defaultValue"] = enabled ? "true" : "false"
        };
        template["symbols"]!["IntegrationVersion"]!["isEnabled"] = "(IncludeIntegration)";
        template["sources"] = JsonNode.Parse("""
            [{"modifiers":[{"condition":"(!IncludeIntegration)","exclude":["versions.json"]}]}]
            """);
        await File.WriteAllTextAsync(path, template.ToJsonString());
        var calls = 0;
        var interaction = new TestInteractionService();
        interaction.SetupBooleanResponse(enabled);
        var services = CreateServices(workspace, interaction, interactive);
        services.AddSingleton<INuGetClient>(new FakeNuGetClient
        {
            GetPackageVersionsCallback = (_, _, _) =>
            {
                calls++;
                return Task.FromResult<IReadOnlyList<NuGetVersion>>([NuGetVersion.Parse("1.2.3")]);
            }
        });
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.True(exitCode == CliExitCodes.Success, string.Join(Environment.NewLine, interaction.DisplayedErrors));
        Assert.Equal(enabled ? 1 : 0, calls);
        Assert.Empty(interaction.StringPromptCalls);
        Assert.Equal(enabled, File.Exists(Path.Combine(workspace.Path, "out", "versions.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalTemplate_ComputedValuesUseOnlySelectedHostMetadata(bool emptyPrimary)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateComputedVersionTemplateAsync(workspace, """
            {"kind":"nugetPackageVersion","packageId":"Example","versionRange":"*"}
            """);
        var config = Path.Combine(workspace.Path, "template", ".template.config");
        File.Move(Path.Combine(config, "aspirecli.host.json"), Path.Combine(config, "dotnetcli.host.json"));
        var definitionPath = Path.Combine(config, "template.json");
        var definition = JsonNode.Parse(await File.ReadAllTextAsync(definitionPath))!;
        definition["symbols"]!["IntegrationVersion"]!["isRequired"] = false;
        await File.WriteAllTextAsync(definitionPath, definition.ToJsonString());
        if (emptyPrimary)
        {
            await File.WriteAllTextAsync(Path.Combine(config, "aspirecli.host.json"), "{}");
        }
        var calls = 0;
        var interaction = new TestInteractionService();
        var services = CreateServices(workspace, interaction, interactive: false);
        services.AddSingleton(workspace.CreateExecutionContext(identityVersion: "13.6.3"));
        services.AddSingleton<INuGetClient>(new FakeNuGetClient
        {
            GetPackageVersionsCallback = (_, _, _) =>
            {
                calls++;
                return Task.FromResult<IReadOnlyList<NuGetVersion>>([NuGetVersion.Parse("1.2.3")]);
            }
        });
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.True(exitCode == CliExitCodes.Success, string.Join(Environment.NewLine, interaction.DisplayedErrors));
        Assert.Equal(emptyPrimary ? 0 : 1, calls);
        await Verify(ReadOutput(workspace)).UseParameters(emptyPrimary);
    }

    [Theory]
    [InlineData("Missing", "string")]
    [InlineData("AspireVersion", "string")]
    [InlineData("IntegrationVersion", "bool")]
    public async Task LocalTemplate_ComputedMetadataRejectsInvalidTargets(string target, string datatype)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateComputedVersionTemplateAsync(workspace, """
            {"kind":"nugetPackageVersion","packageId":"Example","versionRange":"*"}
            """);
        var config = Path.Combine(workspace.Path, "template", ".template.config");
        var hostPath = Path.Combine(config, "aspirecli.host.json");
        var host = JsonNode.Parse(await File.ReadAllTextAsync(hostPath))!;
        var source = host["symbolInfo"]!["IntegrationVersion"]!.DeepClone();
        host["symbolInfo"] = new JsonObject { [target] = source };
        await File.WriteAllTextAsync(hostPath, host.ToJsonString());
        var templatePath = Path.Combine(config, "template.json");
        var template = JsonNode.Parse(await File.ReadAllTextAsync(templatePath))!;
        template["symbols"]!["IntegrationVersion"]!["datatype"] = datatype;
        await File.WriteAllTextAsync(templatePath, template.ToJsonString());
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: false).BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.NotEmpty(interaction.DisplayedErrors);
        Assert.False(Directory.Exists(Path.Combine(workspace.Path, "out")));
    }

    [Fact]
    public async Task LocalTemplate_CancelingComputedLookupRemovesStaging()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var cancellation = new CancellationTokenSource();
        await CreateComputedVersionTemplateAsync(workspace, """
            {"kind":"nugetPackageVersion","packageId":"Example","versionRange":"*"}
            """);
        var services = CreateServices(workspace, new TestInteractionService(), interactive: false);
        services.AddSingleton<INuGetClient>(new FakeNuGetClient
        {
            GetPackageVersionsCallback = (_, _, token) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<IReadOnlyList<NuGetVersion>>(token);
            }
        });
        using var provider = services.BuildServiceProvider();
        var templates = await provider.GetRequiredService<ITemplateProvider>().GetTemplatesAsync(new TemplateSource("template", null, null));
        var parseResult = provider.GetRequiredService<RootCommand>().Parse("new --template-path template");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Assert.Single(templates).ApplyTemplateAsync(
            new TemplateInputs { Name = "Demo", Output = "out" }, parseResult, cancellation.Token));

        Assert.False(Directory.Exists(Path.Combine(workspace.Path, "out")));
        Assert.Empty(Directory.GetFileSystemEntries(workspace.Path, ".aspire-template-*"));
    }

    [Fact]
    public async Task LocalTemplate_ComputedGenerationProtectsConcurrentOutputWrites()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateComputedVersionTemplateAsync(workspace, """
            {"kind":"nugetPackageVersion","packageId":"Example","versionRange":"*"}
            """);
        var output = workspace.CreateDirectory("out");
        var interaction = new TestInteractionService();
        var services = CreateServices(workspace, interaction, interactive: false);
        services.AddSingleton<INuGetClient>(new FakeNuGetClient
        {
            GetPackageVersionsCallback = async (_, _, token) =>
            {
                await File.WriteAllTextAsync(Path.Combine(output.FullName, "keep.txt"), "Concurrent user content", token);
                return [NuGetVersion.Parse("1.2.3")];
            }
        });
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToCreateNewProject, exitCode);
        Assert.Empty(interaction.DisplayedSuccess);
        Assert.Empty(Directory.GetFileSystemEntries(workspace.Path, ".aspire-template-*"));
        await Verify(ReadOutput(workspace));
    }

    [Fact]
    public async Task LocalTemplate_ComputesIndependentPackageVersionsAndDiscardsPreliminaryFiles()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateComputedVersionTemplateAsync(workspace, """
            {"kind":"nugetPackageVersion","packageId":"Aspire.Hosting.Dotnet","versionRange":"*","allowPrerelease":true}
            """);
        var config = Path.Combine(workspace.Path, "template", ".template.config");
        var path = Path.Combine(config, "template.json");
        var template = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        template["symbols"]!["KubernetesVersion"] = new JsonObject
        {
            ["type"] = "parameter", ["datatype"] = "string", ["replaces"] = "KubernetesVersionValue"
        };
        template["sources"] = JsonNode.Parse("""
            [{"modifiers":[{"condition":"(IntegrationVersion != \"0.0.0\")","exclude":["preliminary-only.txt"]}]}]
            """);
        await File.WriteAllTextAsync(path, template.ToJsonString());
        path = Path.Combine(config, "aspirecli.host.json");
        var host = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        host["symbolInfo"]!["KubernetesVersion"] = JsonNode.Parse("""
            {"valueSource":{"kind":"nugetPackageVersion","packageId":"Aspire.Hosting.Kubernetes","versionRange":"[13.6.0-preview.0,13.7.0)","allowPrerelease":true}}
            """);
        await File.WriteAllTextAsync(path, host.ToJsonString());
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "template", "preliminary-only.txt"), "Placeholder render only");
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "template", "versions.json"),
            """{"sdk":"AspireVersionValue","dotnet":"IntegrationVersionValue","kubernetes":"KubernetesVersionValue"}""");
        var interaction = new TestInteractionService();
        var services = CreateServices(workspace, interaction, interactive: false);
        services.AddSingleton(workspace.CreateExecutionContext(identityChannel: "stable", identityVersion: "13.6.3"));
        services.AddSingleton<INuGetClient>(new FakeNuGetClient
        {
            GetPackageVersionsCallback = (id, _, _) => Task.FromResult<IReadOnlyList<NuGetVersion>>(
                [NuGetVersion.Parse(id == "Aspire.Hosting.Dotnet" ? "13.6.0" : "13.6.1-preview.2")])
        });
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.True(exitCode == CliExitCodes.Success, string.Join(Environment.NewLine, interaction.DisplayedErrors));
        await Verify(ReadOutput(workspace));
    }

    private static async Task CreateComputedVersionTemplateAsync(TemporaryWorkspace workspace, string valueSource)
    {
        var config = workspace.CreateDirectory("template/.template.config");
        await File.WriteAllTextAsync(Path.Combine(config.FullName, "template.json"), """
            {
              "identity": "Aspire.Test.ComputedVersion", "name": "Computed version", "shortName": "computed",
              "symbols": {
                "AspireVersion": {"type":"parameter","datatype":"string","replaces":"AspireVersionValue"},
                "IntegrationVersion": {
                  "type":"parameter","datatype":"string","defaultValue":"99.0.0","isRequired":true,
                  "replaces":"IntegrationVersionValue"
                }
              }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(config.FullName, "aspirecli.host.json"), $$"""
            {
              "symbolInfo": {
                "IntegrationVersion": {
                  "valueSource": {{valueSource}}
                }
              }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "template", "versions.json"),
            """{"sdk":"AspireVersionValue","integration":"IntegrationVersionValue"}""");
    }
}
