// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Packaging;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using RootCommand = Aspire.Cli.Commands.RootCommand;

namespace Aspire.Cli.Tests.Commands;

public partial class NewCommandLocalTemplateTests
{
    [Theory]
    [InlineData(0, null, true, true)]
    [InlineData(0, "true", false, true)]
    [InlineData(0, "\"true\"", false, true)]
    [InlineData(0, "false", true, true)]
    [InlineData(0, "\"false\"", true, true)]
    [InlineData(1, "true", true, true)]
    [InlineData(1, "invalid-json", true, true)]
    [InlineData(2, "true", true, false)]
    public async Task LocalTemplate_HostConfigurationSelectsOneFile(
        int aspireConfiguration, string? dotnetHidden, bool promptGreeting, bool promptMode)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateTemplateAsync(workspace, "template");
        var config = Path.Combine(workspace.Path, "template", ".template.config");
        if (dotnetHidden is not null)
        {
            await File.WriteAllTextAsync(Path.Combine(config, "dotnetcli.host.json"), $$"""
                { "symbolInfo": { "Greeting": { "isHidden": {{dotnetHidden}} } } }
                """);
        }
        if (aspireConfiguration != 0)
        {
            await File.WriteAllTextAsync(Path.Combine(config, "aspirecli.host.json"), aspireConfiguration == 1 ? "{}" : """
                {
                  "symbolInfo": {
                    "Greeting": { "displayName": "Message" },
                    "Mode": { "isHidden": true }
                  }
                }
                """);
        }

        var interaction = new TestInteractionService();
        if (promptGreeting)
        {
            interaction.SetupStringPromptResponse("Welcome");
        }
        interaction.SetupBooleanResponse(true);
        var modePrompts = 0;
        interaction.PromptForSelectionCallback = (_, _, _, _) =>
        {
            modePrompts++;
            return "api";
        };
        interaction.PromptForSelectionsCallback = (_, _, _, _) => ["web"];
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.True(exitCode == CliExitCodes.Success, string.Join(Environment.NewLine, interaction.DisplayedErrors));
        Assert.Empty(interaction.DisplayedErrors);
        Assert.Equal(promptMode ? 1 : 0, modePrompts);
        if (promptGreeting)
        {
            Assert.Equal(aspireConfiguration == 2 ? "Message" : "Greeting text",
                Assert.Single(interaction.StringPromptCalls).PromptText);
        }
        else
        {
            Assert.Empty(interaction.StringPromptCalls);
        }
        await Verify(ReadOutput(workspace)).ScrubInlineGuids().UseParameters(aspireConfiguration, dotnetHidden);
    }

    [Fact]
    public async Task LocalTemplate_HiddenParametersRetainEngineDefaultsForEveryPromptType()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateTemplateAsync(workspace, "template");
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "template", ".template.config", "aspirecli.host.json"), """
            {
              "symbolInfo": {
                "Greeting": { "isHidden": true },
                "IncludeMessage": { "isHidden": true },
                "Mode": { "isHidden": true },
                "Features": { "isHidden": true }
              }
            }
            """);
        var interaction = new TestInteractionService();
        interaction.PromptForSelectionCallback = (_, _, _, _) => throw new InvalidOperationException("Hidden choices must not prompt.");
        interaction.PromptForSelectionsCallback = (_, _, _, _) => throw new InvalidOperationException("Hidden choices must not prompt.");
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(interaction.DisplayedErrors);
        Assert.Empty(interaction.StringPromptCalls);
        Assert.Empty(interaction.BooleanPromptCalls);
        await Verify(ReadOutput(workspace)).ScrubInlineGuids();
    }

    [Theory]
    [InlineData("aspirecli.host.json", true)]
    [InlineData("aspirecli.host.json", false)]
    [InlineData("dotnetcli.host.json", true)]
    [InlineData("dotnetcli.host.json", false)]
    public async Task LocalTemplate_HiddenPortsUseGeneratedFallbacksAndHiddenControllersUseDefaults(
        string hostFile, bool interactive)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var config = workspace.CreateDirectory("template/.template.config");
        await File.WriteAllTextAsync(Path.Combine(config.FullName, "template.json"), """
            {
              "identity": "Aspire.Local.HiddenPort",
              "name": "Hidden port",
              "shortName": "hidden-port",
              "symbols": {
                "Detail": {
                  "type": "parameter", "datatype": "string", "defaultValue": "Engine default",
                  "isEnabled": "(IncludeDetails)", "replaces": "DetailValue"
                },
                "IncludeDetails": { "type": "parameter", "datatype": "bool", "defaultValue": "true" },
                "Port": { "type": "parameter", "datatype": "integer" },
                "GeneratedPort": {
                  "type": "generated", "generator": "port", "parameters": { "low": 15000, "high": 15300 }
                },
                "EffectivePort": {
                  "type": "generated", "generator": "coalesce", "replaces": "15000",
                  "parameters": { "sourceVariableName": "Port", "fallbackVariableName": "GeneratedPort" }
                }
              }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(config.FullName, hostFile), """
            {
              "symbolInfo": {
                "Port": { "isHidden": true },
                "IncludeDetails": { "isHidden": true }
              }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "template", "settings.json"),
            """{ "port": 15000, "detail": "DetailValue" }""");
        var interaction = new TestInteractionService();
        interaction.SetupStringPromptResponse("Updated");
        using var provider = CreateServices(workspace, interaction, interactive).BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(interaction.DisplayedErrors);
        Assert.Empty(interaction.BooleanPromptCalls);
        Assert.Equal(interactive ? 1 : 0, interaction.StringPromptCalls.Count);
        using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace.Path, "out", "settings.json")));
        var port = settings.RootElement.GetProperty("port").GetInt32();
        Assert.InRange(port, 15000, 15300);
        Assert.Equal(interactive ? "Updated" : "Engine default", settings.RootElement.GetProperty("detail").GetString());
        await Verify(ReadOutput(workspace))
            .AddScrubber(text => text.Replace(port.ToString(CultureInfo.InvariantCulture), "{generated-port}"))
            .UseParameters(hostFile, interactive);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LocalTemplate_HiddenRequiredParametersAreStillEngineValidated(bool interactive)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateTemplateAsync(workspace, "template", requiredGreeting: true);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "template", ".template.config", "aspirecli.host.json"), """
            { "symbolInfo": { "Greeting": { "isHidden": true } } }
            """);
        var interaction = new TestInteractionService();
        interaction.SetupBooleanResponse(true);
        interaction.PromptForSelectionCallback = (_, _, _, _) => "api";
        interaction.PromptForSelectionsCallback = (_, _, _, _) => ["web"];
        using var provider = CreateServices(workspace, interaction, interactive).BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.NotEqual(CliExitCodes.Success, exitCode);
        Assert.NotEmpty(interaction.DisplayedErrors);
        Assert.Empty(interaction.StringPromptCalls);
        Assert.False(Directory.Exists(Path.Combine(workspace.Path, "out")));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("""{ "symbolInfo": [] }""")]
    [InlineData("""{ "symbolInfo": { "Greeting": true } }""")]
    [InlineData("""{ "symbolInfo": { "Greeting": { "isHidden": 1 } } }""")]
    [InlineData("""{ "symbolInfo": { "Greeting": { "isHidden": "yes" } } }""")]
    [InlineData("""{ "symbolInfo": { "Greeting": { "displayName": false } } }""")]
    [InlineData("""{ "createNuGetConfig": "true" }""")]
    [InlineData("""{ "createNuGetConfig": null }""")]
    [InlineData("""{ "createNuGetConfig": 1 }""")]
    [InlineData("""{ "createNuGetConfig": [] }""")]
    [InlineData("""{ "createNuGetConfig": {} }""")]
    public async Task LocalTemplate_InvalidSelectedHostConfigurationDoesNotFallBack(string contents)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateTemplateAsync(workspace, "template");
        var config = Path.Combine(workspace.Path, "template", ".template.config");
        await File.WriteAllTextAsync(Path.Combine(config, "aspirecli.host.json"), contents);
        await File.WriteAllTextAsync(Path.Combine(config, "dotnetcli.host.json"), "{}");
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: false).BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.NotEqual(CliExitCodes.Success, exitCode);
        Assert.NotEmpty(interaction.DisplayedErrors);
        Assert.False(Directory.Exists(Path.Combine(workspace.Path, "out")));
    }

    [Fact]
    public async Task LocalTemplate_HiddenVersionStillUsesCliIdentity()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateVersionedTemplateAsync(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "template", ".template.config", "aspirecli.host.json"), """
            { "symbolInfo": { "AspireVersion": { "isHidden": true } } }
            """);
        var interaction = new TestInteractionService();
        var services = CreateServices(workspace, interaction, interactive: true);
        services.AddSingleton(workspace.CreateExecutionContext(identityChannel: "stable", identityVersion: "17.0.0"));
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(interaction.DisplayedErrors);
        Assert.Empty(interaction.StringPromptCalls);
        await Verify(ReadOutput(workspace));
    }

    [Fact]
    public async Task LocalTemplate_HiddenNuGetSourceStillUsesCliIdentity()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateNuGetSourceTemplateAsync(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "template", ".template.config", "aspirecli.host.json"), """
            {
              "symbolInfo": {
                "AspireVersion": { "isHidden": true },
                "AspireNuGetSource": { "isHidden": true }
              }
            }
            """);
        var interaction = new TestInteractionService();
        var services = CreateServices(workspace, interaction, interactive: true);
        services.AddSingleton(workspace.CreateExecutionContext(identityChannel: "stable", identityVersion: "17.0.0"));
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(interaction.DisplayedErrors);
        Assert.Empty(interaction.StringPromptCalls);
        await Verify(ReadOutput(workspace));
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("empty")]
    [InlineData("false")]
    public async Task LocalTemplate_PolyglotVersionDoesNotRequestNuGetPolicy(string hostConfiguration)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateVersionedTemplateAsync(workspace);
        var hostFile = Path.Combine(workspace.Path, "template", ".template.config", "aspirecli.host.json");
        if (hostConfiguration == "absent")
        {
            File.Delete(hostFile);
        }
        else
        {
            await File.WriteAllTextAsync(hostFile,
                hostConfiguration == "empty" ? "{}" : """{ "createNuGetConfig": false }""");
        }
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "template", "aspire.config.json"), """
            {
              "appHost": { "path": "apphost.mts", "language": "typescript/nodejs" },
              "sdk": { "version": "AspireVersionValue" },
              "packages": { "Aspire.Hosting.JavaScript": "AspireVersionValue" }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "template", "apphost.mts"),
            """import { createBuilder } from "./.aspire/sdk/aspire.js";""");
        var interaction = new TestInteractionService();
        var services = CreateServices(workspace, interaction, interactive: false);
        services.AddSingleton(workspace.CreateExecutionContext(identityChannel: "pr-missing", identityVersion: "17.0.0-pr.12345"));
        services.AddSingleton<IPackagingService>(new TestPackagingService());
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(interaction.DisplayedErrors);
        await Verify(ReadOutput(workspace)).UseParameters(hostConfiguration);
    }

    [Theory]
    [InlineData(null, true, true)]
    [InlineData("{}", true, false)]
    [InlineData("""{ "createNuGetConfig": false }""", true, false)]
    [InlineData("""{ "createNuGetConfig": true }""", false, true)]
    public async Task LocalTemplate_NuGetConfigOptInUsesOneHostFileWithoutRequiringVersionParameter(
        string? aspireConfiguration, bool dotnetOptIn, bool expectedCreate)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateVersionedTemplateAsync(workspace);
        var config = Path.Combine(workspace.Path, "template", ".template.config");
        await File.WriteAllTextAsync(Path.Combine(config, "template.json"), """
            {
              "identity": "Aspire.Local.NuGetOptIn",
              "name": "NuGet opt-in template",
              "shortName": "nuget-opt-in"
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "template", "aspire.config.json"), "{}");
        var hostFile = Path.Combine(config, "aspirecli.host.json");
        if (aspireConfiguration is null)
        {
            File.Delete(hostFile);
        }
        else
        {
            await File.WriteAllTextAsync(hostFile, aspireConfiguration);
        }
        await File.WriteAllTextAsync(Path.Combine(config, "dotnetcli.host.json"),
            JsonSerializer.Serialize(new { createNuGetConfig = dotnetOptIn }));
        var interaction = new TestInteractionService();
        var services = CreateServices(workspace, interaction, interactive: false);
        services.AddSingleton(workspace.CreateExecutionContext(identityChannel: "daily", identityVersion: "17.0.0-preview.1"));
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(interaction.DisplayedErrors);
        var output = Path.Combine(workspace.Path, "out");
        Assert.Equal(expectedCreate, File.Exists(Path.Combine(output, "nuget.config")));
        Assert.Equal(
            expectedCreate
                ? new[] { "https://api.nuget.org/v3/index.json", "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet9/nuget/v3/index.json" }
                : ["https://api.nuget.org/v3/index.json"],
            NuGetTestHelper.GetEligiblePackageSources(output, "Aspire.Hosting.Redis"));
    }

    public static TheoryData<string, string, string[], string[], string?, string> BuiltInTemplatePrompts => new()
    {
        { "aspire-apphost", "None", [], [], "17.0", "17.0.0" },
        { "aspire-apphost-singlefile", "None", [], ["Use .dev.localhost URLs"], "17.0", "17.0.0" },
        { "aspire-empty", "None", [], [], "17.0", "17.0.0" },
        { "aspire-empty", "None", [], [], "17.0", "17.0.0-preview.1" },
        { "aspire-empty", "None", [], [], "17.0", "17.0.0-dev" },
        { "aspire-empty", "None", [], [], null, "17.0.0" },
        { "aspire-empty", "None", [], [], null, "17.0.0-preview.1" },
        { "aspire-empty", "None", [], [], null, "17.0.0-dev" },
        { "aspire-starter", "None", ["Test framework"], ["Use Redis for caching", "Use .dev.localhost URLs"], "17.0", "17.0.0" },
        { "aspire-starter", "xUnit.net", ["Test framework", "xUnit.net version"], ["Use Redis for caching", "Use .dev.localhost URLs"], "17.0", "17.0.0" },
        { "aspire-ts-cs-starter", "None", [], ["Use Redis for caching", "Use .dev.localhost URLs"], "17.0", "17.0.0" }
    };

    [Theory]
    [MemberData(nameof(BuiltInTemplatePrompts))]
    public async Task LocalTemplate_BuiltInTemplatesHaveCuratedPromptsAndGeneratedPorts(
        string templateName, string testFramework, string[] expectedChoices, string[] expectedBooleans,
        string? packagedVersion, string identityVersion)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "src", "Aspire.ProjectTemplates", "templates", templateName));
        var template = workspace.CreateDirectory("template");
        FileSystemHelper.CopyDirectory(source, template.FullName);
        if (packagedVersion is not null)
        {
            var configurationPath = Path.Combine(template.FullName, ".template.config", "template.json");
            // Packaging replaces this metadata with major.minor, not the full SDK version.
            // Keep the unmodified source template covered as well.
            await File.WriteAllTextAsync(configurationPath,
                (await File.ReadAllTextAsync(configurationPath))
                    .Replace("!!REPLACE_WITH_LATEST_MAJOR_MINOR_VERSION!!", packagedVersion, StringComparison.Ordinal));
        }
        var interaction = new TestInteractionService();
        foreach (var _ in expectedBooleans)
        {
            interaction.SetupBooleanResponse(false);
        }
        var choicePrompts = new List<string>();
        interaction.PromptForSelectionCallback = (prompt, choices, _, _) =>
        {
            choicePrompts.Add(prompt);
            return prompt == "Test framework" ? testFramework : choices.Cast<string>().First();
        };
        var services = CreateServices(workspace, interaction, interactive: true);
        services.AddSingleton(workspace.CreateExecutionContext(identityChannel: "stable", identityVersion: identityVersion));
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.True(exitCode == CliExitCodes.Success, string.Join(Environment.NewLine, interaction.DisplayedErrors));
        Assert.Empty(interaction.DisplayedErrors);
        Assert.Empty(interaction.StringPromptCalls);
        Assert.Equal(expectedChoices, choicePrompts);
        Assert.Equal(expectedBooleans, interaction.BooleanPromptCalls.Select(call => call.PromptText));
        var output = Path.Combine(workspace.Path, "out");
        var profiles = Directory.GetFiles(output, "launchSettings.json", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(output, "apphost.run.json", SearchOption.AllDirectories)).ToArray();
        Assert.Equal(templateName switch
        {
            "aspire-starter" => 3,
            "aspire-ts-cs-starter" => 2,
            "aspire-apphost" or "aspire-apphost-singlefile" or "aspire-empty" => 1,
            _ => 0
        }, profiles.Length);
        foreach (var path in profiles)
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            var launchProfiles = document.RootElement.GetProperty("profiles");
            Assert.Equal(["http", "https"], launchProfiles.EnumerateObject().Select(profile => profile.Name).Order(StringComparer.Ordinal));
            foreach (var profile in launchProfiles.EnumerateObject())
            {
                foreach (var url in profile.Value.GetProperty("applicationUrl").GetString()!.Split(';'))
                {
                    Assert.InRange(new Uri(url).Port, 1024, 65535);
                }
                foreach (var variable in profile.Value.GetProperty("environmentVariables").EnumerateObject())
                {
                    if (variable.Name.EndsWith("_URL", StringComparison.Ordinal))
                    {
                        Assert.InRange(new Uri(variable.Value.GetString()!).Port, 1024, 65535);
                    }
                }
            }
        }
    }
}
