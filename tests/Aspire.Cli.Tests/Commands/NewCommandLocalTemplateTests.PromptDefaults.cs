// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.Tests.TestServices;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using RootCommand = Aspire.Cli.Commands.RootCommand;

namespace Aspire.Cli.Tests.Commands;

public partial class NewCommandLocalTemplateTests
{
    [Theory]
    [InlineData("Demo", "Demo")]
    [InlineData("Demo.App", "Demo-App")]
    [InlineData("Demo App", "Demo-App")]
    [InlineData("...___", "service")]
    [InlineData(".Demo. App-", "Demo-App")]
    public async Task LocalTemplate_ProjectDerivedDefaultsAreDisplayedAndAccepted(string name, string shortName)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreatePromptDefaultsTemplateAsync(workspace);
        // An empty response queue accepts the binding's default, as Enter does in the terminal.
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse($"new --template-path template --name \"{name}\" --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(interaction.DisplayedErrors);
        Assert.Collection(interaction.StringPromptCalls,
            prompt =>
            {
                Assert.Equal("Display name", prompt.PromptText);
                Assert.Equal(name, prompt.DefaultValue);
                Assert.False(prompt.IsSecret);
            },
            prompt =>
            {
                Assert.Equal("Short name", prompt.PromptText);
                Assert.Equal(shortName, prompt.DefaultValue);
            });
        await Verify(ReadOutput(workspace)).UseParameters(name, shortName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LocalTemplate_ProjectDerivedDefaultsPreserveOverridesAndNonInteractiveFallbacks(bool interactive)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreatePromptDefaultsTemplateAsync(workspace);
        var interaction = new TestInteractionService();
        interaction.SetupStringPromptResponse("Custom display");
        interaction.SetupStringPromptResponse("custom-short");
        using var provider = CreateServices(workspace, interaction, interactive).BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo.App --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(interaction.DisplayedErrors);
        Assert.Equal(interactive ? ["Demo.App", "Demo-App"] : [], interaction.StringPromptCalls.Select(prompt => prompt.DefaultValue));
        await Verify(ReadOutput(workspace)).UseParameters(interactive);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LocalTemplate_ProjectDerivedDefaultsUsePromptedNameAndPreserveOmission(bool typeDefaults)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreatePromptDefaultsTemplateAsync(workspace);
        var interaction = new TestInteractionService();
        interaction.SetupStringPromptResponse("Typed.Project Name");
        if (typeDefaults)
        {
            interaction.SetupStringPromptResponse("Typed.Project Name");
            interaction.SetupStringPromptResponse("Typed-Project-Name");
        }
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(interaction.DisplayedErrors);
        Assert.Equal(3, interaction.StringPromptCalls.Count);
        Assert.Equal("Typed.Project Name", interaction.StringPromptCalls[1].DefaultValue);
        Assert.Equal("Typed-Project-Name", interaction.StringPromptCalls[2].DefaultValue);
        await Verify(ReadOutput(workspace)).UseParameters(typeDefaults);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("random")]
    [InlineData("bound")]
    [InlineData("other-parameter")]
    [InlineData("cycle")]
    [InlineData("required")]
    [InlineData("static-default")]
    [InlineData("wrong-source")]
    [InlineData("wrong-type")]
    [InlineData("invalid-metadata")]
    [InlineData("missing-dependency")]
    [InlineData("no-coalesce")]
    [InlineData("invalid-regex")]
    [InlineData("value-source")]
    [InlineData("nested-missing-source")]
    [InlineData("nested-missing-fallback")]
    [InlineData("nested-name-source")]
    [InlineData("regex-omitted-override")]
    [InlineData("coalesce-omitted-fallback")]
    [InlineData("redefined-name-constant")]
    [InlineData("redefined-name-random")]
    [InlineData("redefined-name-parameter")]
    public async Task LocalTemplate_InvalidProjectDerivedDefaultsFailBeforePromptingOrWriting(string scenario)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreatePromptDefaultsTemplateAsync(workspace);
        var templatePath = Path.Combine(workspace.Path, "template", ".template.config", "template.json");
        var template = JsonNode.Parse(await File.ReadAllTextAsync(templatePath))!;
        var symbols = template["symbols"]!;
        var hostPath = Path.Combine(workspace.Path, "template", ".template.config", "aspirecli.host.json");
        var host = JsonNode.Parse(await File.ReadAllTextAsync(hostPath))!;
        switch (scenario)
        {
            case "unknown":
                host["symbolInfo"]!["DisplayName"]!["defaultValueSource"] = "missing";
                break;
            case "random":
                symbols["normalizedName"] = JsonNode.Parse("""{"type":"generated","generator":"random","parameters":{"low":1,"high":10}}""");
                break;
            case "bound":
                symbols["normalizedName"] = JsonNode.Parse("""{"type":"bind","binding":"env:HOME"}""");
                break;
            case "other-parameter":
                symbols["normalizedName"]!["parameters"]!["source"] = "DisplayName";
                break;
            case "cycle":
                symbols["normalizedName"]!["parameters"]!["source"] = "shortNameValue";
                break;
            case "required":
                symbols["DisplayName"]!["isRequired"] = true;
                break;
            case "static-default":
                symbols["DisplayName"]!["defaultValue"] = "Static";
                break;
            case "wrong-source":
                symbols["displayNameValue"]!["parameters"]!["sourceVariableName"] = "ShortName";
                break;
            case "wrong-type":
                symbols["DisplayName"]!["datatype"] = "bool";
                break;
            case "invalid-metadata":
                host["symbolInfo"]!["DisplayName"]!["defaultValueSource"] = true;
                break;
            case "missing-dependency":
                symbols["normalizedName"]!["parameters"]!["source"] = "missing";
                break;
            case "no-coalesce":
                host["symbolInfo"]!["ShortName"]!["defaultValueSource"] = "normalizedName";
                break;
            case "invalid-regex":
                symbols["normalizedName"]!["parameters"]!["steps"]![0]!["regex"] = "[";
                break;
            case "value-source":
                host["symbolInfo"]!["DisplayName"]!["valueSource"] = JsonNode.Parse(
                    """{"kind":"nugetPackageVersion","packageId":"Example.Package","versionRange":"[1.0,2.0)"}""");
                break;
            case "nested-missing-source":
                symbols["nestedName"] = JsonNode.Parse(
                    """{"type":"generated","generator":"coalesce","parameters":{"sourceVariableName":"missing","fallbackVariableName":"name"}}""");
                symbols["shortNameValue"]!["parameters"]!["fallbackVariableName"] = "nestedName";
                break;
            case "nested-missing-fallback":
                symbols["nestedName"] = JsonNode.Parse(
                    """{"type":"generated","generator":"coalesce","parameters":{"sourceVariableName":"normalizedName","fallbackVariableName":"missing"}}""");
                symbols["shortNameValue"]!["parameters"]!["fallbackVariableName"] = "nestedName";
                break;
            case "nested-name-source":
                symbols["nestedName"] = JsonNode.Parse(
                    """{"type":"generated","generator":"coalesce","parameters":{"sourceVariableName":"name","fallbackVariableName":"normalizedName"}}""");
                symbols["shortNameValue"]!["parameters"]!["fallbackVariableName"] = "nestedName";
                break;
            case "regex-omitted-override":
                symbols["normalizedName"]!["parameters"]!["source"] = "ShortName";
                break;
            case "coalesce-omitted-fallback":
                symbols["nestedName"] = JsonNode.Parse(
                    """{"type":"generated","generator":"coalesce","parameters":{"sourceVariableName":"normalizedName","fallbackVariableName":"ShortName"}}""");
                symbols["shortNameValue"]!["parameters"]!["fallbackVariableName"] = "nestedName";
                break;
            case "redefined-name-constant":
                symbols["name"] = JsonNode.Parse(
                    """{"type":"generated","generator":"constant","parameters":{"value":"DifferentName"}}""");
                break;
            case "redefined-name-random":
                symbols["name"] = JsonNode.Parse(
                    """{"type":"generated","generator":"random","parameters":{"low":1,"high":10}}""");
                break;
            case "redefined-name-parameter":
                symbols["name"] = JsonNode.Parse(
                    """{"type":"parameter","datatype":"string","defaultValue":"DifferentName"}""");
                break;
        }
        await File.WriteAllTextAsync(templatePath, template.ToJsonString());
        await File.WriteAllTextAsync(hostPath, host.ToJsonString());
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.NotEqual(CliExitCodes.Success, exitCode);
        Assert.NotEmpty(interaction.DisplayedErrors);
        Assert.Empty(interaction.StringPromptCalls);
        Assert.False(Directory.Exists(Path.Combine(workspace.Path, "out")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LocalTemplate_InvalidProjectDerivedDefaultsFailBeforeProjectPrompts(bool supplyName, bool supplyOutput)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreatePromptDefaultsTemplateAsync(workspace);
        var hostPath = Path.Combine(workspace.Path, "template", ".template.config", "aspirecli.host.json");
        await File.WriteAllTextAsync(hostPath, """
            { "symbolInfo": { "ShortName": { "defaultValueSource": "missing" } } }
            """);
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();
        var options = $"{(supplyName ? "--name Demo" : "")} {(supplyOutput ? "--output out" : "")}";

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse($"new --template-path template {options} --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.NotEqual(CliExitCodes.Success, exitCode);
        Assert.NotEmpty(interaction.DisplayedErrors);
        Assert.Empty(interaction.StringPromptCalls);
        Assert.False(Directory.Exists(Path.Combine(workspace.Path, "out")));
    }

    [Fact]
    public async Task LocalTemplate_ProjectDerivedDefaultsSupportNestedCoalesce()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreatePromptDefaultsTemplateAsync(workspace);
        var templatePath = Path.Combine(workspace.Path, "template", ".template.config", "template.json");
        var template = JsonNode.Parse(await File.ReadAllTextAsync(templatePath))!;
        var symbols = template["symbols"]!;
        symbols["nestedName"] = JsonNode.Parse(
            """{"type":"generated","generator":"coalesce","parameters":{"sourceVariableName":"normalizedName","fallbackVariableName":"name"}}""");
        symbols["shortNameValue"]!["parameters"]!["fallbackVariableName"] = "nestedName";
        await File.WriteAllTextAsync(templatePath, template.ToJsonString());
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("new --template-path template --name Demo.App --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(interaction.DisplayedErrors);
        Assert.Equal(["Demo.App", "Demo-App"], interaction.StringPromptCalls.Select(prompt => prompt.DefaultValue));
        await Verify(ReadOutput(workspace));
    }

    private static async Task CreatePromptDefaultsTemplateAsync(TemporaryWorkspace workspace)
    {
        var config = workspace.CreateDirectory("template/.template.config");
        await File.WriteAllTextAsync(Path.Combine(config.FullName, "template.json"), """
            {
              "identity": "Aspire.Local.PromptDefaults", "name": "Prompt defaults", "shortName": "prompt-defaults",
              "symbols": {
                "DisplayName": {
                  "type": "parameter", "datatype": "string", "defaultValue": "",
                  "displayName": "Display name", "replaces": "RAW_DISPLAY"
                },
                "ShortName": {
                  "type": "parameter", "datatype": "string", "defaultValue": "",
                  "displayName": "Short name", "replaces": "RAW_SHORT"
                },
                "displayNameValue": {
                  "type": "generated", "generator": "coalesce",
                  "parameters": { "sourceVariableName": "DisplayName", "fallbackVariableName": "name" },
                  "replaces": "DISPLAY_VALUE"
                },
                "normalizedName": {
                  "type": "generated", "generator": "regex",
                  "parameters": {
                    "source": "name",
                    "steps": [
                      { "regex": "[^A-Za-z0-9]+", "replacement": "-" },
                      { "regex": "^-+|-+$", "replacement": "" },
                      { "regex": "^$", "replacement": "service" }
                    ]
                  }
                },
                "shortNameValue": {
                  "type": "generated", "generator": "coalesce",
                  "parameters": { "sourceVariableName": "ShortName", "fallbackVariableName": "normalizedName" },
                  "replaces": "SHORT_VALUE"
                }
              }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(config.FullName, "aspirecli.host.json"), """
            {
              "symbolInfo": {
                "DisplayName": { "defaultValueSource": "displayNameValue" },
                "ShortName": { "defaultValueSource": "shortNameValue" }
              }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "template", "names.txt"),
            "Display=DISPLAY_VALUE\nShort=SHORT_VALUE\nRawDisplay=RAW_DISPLAY\nRawShort=RAW_SHORT\n");
    }
}
