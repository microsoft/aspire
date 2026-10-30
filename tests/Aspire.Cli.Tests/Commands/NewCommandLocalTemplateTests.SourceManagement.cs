// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Commands;
using Aspire.Cli.Configuration;
using Aspire.Cli.Resources;
using Aspire.Cli.Templating;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using RootCommand = Aspire.Cli.Commands.RootCommand;

namespace Aspire.Cli.Tests.Commands;

public partial class NewCommandLocalTemplateTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(false, null)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(true, null)]
    public async Task TemplateSourceCommands_RequiresTrustBeforeAdding(bool repository, bool? trust)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        if (trust is { } response)
        {
            interaction.SetupBooleanResponse(response);
        }
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();
        var path = provider.GetRequiredService<IConfigurationService>().GetSettingsFilePath(true);
        const string original = """{ /*keep*/ "unrelated": "preserved", }""";
        await File.WriteAllTextAsync(path, original);
        var location = repository ? "https://example.test/templates" : workspace.Path;

        var result = await provider.GetRequiredService<RootCommand>()
            .Parse(["template", "source", "add", "company", repository ? "--repository" : "--path", location])
            .InvokeAsync().DefaultTimeout();

        var prompt = Assert.Single(interaction.BooleanPromptCalls);
        Assert.False(prompt.DefaultValue);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, NewCommandStrings.TemplateSourceTrustPrompt, "company", location),
            prompt.PromptText);
        if (trust == true)
        {
            Assert.Equal(CliExitCodes.Success, result);
            var source = Assert.Single(await provider.GetRequiredService<TemplateSourceStore>().ListAsync(default));
            Assert.Equal("company", source.Key);
            Assert.Equal(location, source.Value![repository ? "repository" : "path"]!.GetValue<string>());
            Assert.Single(interaction.DisplayedSuccess);
        }
        else
        {
            Assert.Equal(CliExitCodes.Cancelled, result);
            Assert.Equal(original, await File.ReadAllTextAsync(path));
            Assert.Empty(interaction.DisplayedSuccess);
            Assert.Single(interaction.DisplayedCancellations);
        }
        Assert.Empty(interaction.DisplayedErrors);
    }

    [Theory]
    [InlineData(true, "--yes")]
    [InlineData(true, "-y")]
    [InlineData(false, "--yes")]
    [InlineData(false, "-y")]
    public async Task TemplateSourceCommands_YesSkipsTrustPrompt(bool interactive, string yesOption)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive).BuildServiceProvider();
        var mode = interactive ? Array.Empty<string>() : ["--non-interactive"];

        var result = await provider.GetRequiredService<RootCommand>()
            .Parse(["template", "source", "add", "company", "--repository", "https://example.test/templates", yesOption, .. mode])
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, result);
        Assert.Empty(interaction.BooleanPromptCalls);
        Assert.Equal("company", Assert.Single(await provider.GetRequiredService<TemplateSourceStore>().ListAsync(default)).Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--yes false")]
    public void TemplateSourceCommands_NonInteractiveRequiresYes(string yesOption)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: false).BuildServiceProvider();

        var result = provider.GetRequiredService<RootCommand>()
            .Parse($"template source add company --repository https://example.test/templates --non-interactive {yesOption}");

        Assert.Contains("--yes", Assert.Single(result.Errors).Message);
        Assert.Empty(interaction.BooleanPromptCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TemplateSourceCommands_NoInteractiveInputRequiresYes(bool yes)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateNonInteractiveHostEnvironment();
        }).BuildServiceProvider();
        var path = provider.GetRequiredService<IConfigurationService>().GetSettingsFilePath(true);
        const string original = """{ /*keep*/ "unrelated": "preserved", }""";
        await File.WriteAllTextAsync(path, original);
        var arguments = yes ? new[] { "--yes" } : [];

        var result = await provider.GetRequiredService<RootCommand>()
            .Parse(["template", "source", "add", "company", "--repository", "https://example.test/templates", .. arguments])
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(yes ? CliExitCodes.Success : CliExitCodes.MissingRequiredArgument, result);
        if (yes)
        {
            Assert.Equal("company", Assert.Single(await provider.GetRequiredService<TemplateSourceStore>().ListAsync(default)).Key);
        }
        else
        {
            Assert.Equal(original, await File.ReadAllTextAsync(path));
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(6, true)]
    [InlineData(7, true)]
    public void TemplateSourceOptions_FromParseResultValidatesEveryFieldCombination(int fieldMask, bool valid)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CreateServices(workspace, new TestInteractionService(), interactive: true).BuildServiceProvider();
        var path = (fieldMask & 2) != 0 ? "templates/api" : workspace.Path;
        (int Mask, string Option, string Value)[] fields =
        [
            (1, "--path", path),
            (2, "--repository", "https://example.test/repo"),
            (4, "--ref", "main")
        ];
        var arguments = fields.Where(field => (fieldMask & field.Mask) != 0)
            .SelectMany(field => new[] { field.Option, field.Value }).ToArray();
        var result = provider.GetRequiredService<RootCommand>().Parse(["template", "source", "add", "company", .. arguments]);
        Assert.Empty(result.Errors);

        if (valid)
        {
            var options = TemplateManagementCommand.AddSourceOptions.FromParseResult(result);
            Assert.Equal("company", options.Name);
            Assert.Equal((fieldMask & 1) != 0 ? path : null, options.Path);
            Assert.Equal((fieldMask & 2) != 0 ? "https://example.test/repo" : null, options.Repository);
            Assert.Equal((fieldMask & 4) != 0 ? "main" : null, options.Ref);
        }
        else
        {
            Assert.Throws<TemplateSourceConfigurationException>(() => TemplateManagementCommand.AddSourceOptions.FromParseResult(result));
        }
    }

    [Theory]
    [InlineData("local")]
    [InlineData("git")]
    [InlineData("git-defaults")]
    [InlineData("git-path")]
    [InlineData("git-ref")]
    public async Task TemplateSourceCommands_AddAndRemoveGlobalObjects(string kind)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();
        var configuration = provider.GetRequiredService<IConfigurationService>();
        var globalPath = configuration.GetSettingsFilePath(true);
        await File.WriteAllTextAsync(globalPath, """{"unrelated":"preserved","templateSources":{"existing":{"repository":"https://example.test/other"}}}""");
        var localPath = configuration.GetSettingsFilePath(false);
        var localBefore = File.Exists(localPath) ? await File.ReadAllTextAsync(localPath) : null;
        var options = kind switch
        {
            "local" => new[] { "--path", workspace.Path },
            "git" => ["--repository", "https://example.test/templates", "--ref", "release/17.0", "--path", "templates/api"],
            "git-defaults" => ["--repository", "https://example.test/templates"],
            "git-path" => ["--repository", "https://example.test/templates", "--path", "templates/api"],
            "git-ref" => ["--repository", "https://example.test/templates", "--ref", "release/17.0"],
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var root = provider.GetRequiredService<RootCommand>();

        Assert.Equal(CliExitCodes.Success, await root.Parse(["template", "source", "add", "company", "--yes", .. options]).InvokeAsync().DefaultTimeout());
        var settings = JsonNode.Parse(await File.ReadAllTextAsync(globalPath))!;
        var source = Assert.IsType<JsonObject>(settings["templateSources"]!["company"]);
        Assert.Equal(options.Length / 2, source.Count);
        for (var index = 0; index < options.Length; index += 2)
        {
            Assert.Equal(options[index + 1], source[options[index][2..]]!.GetValue<string>());
        }
        Assert.Equal("preserved", settings["unrelated"]!.GetValue<string>());
        Assert.Equal(2, settings["templateSources"]!.AsObject().Count);
        Assert.Equal(localBefore, File.Exists(localPath) ? await File.ReadAllTextAsync(localPath) : null);

        Assert.Equal(CliExitCodes.Success, await root.Parse("template source remove COMPANY").InvokeAsync().DefaultTimeout());
        settings = JsonNode.Parse(await File.ReadAllTextAsync(globalPath))!;
        Assert.Equal(["existing"], settings["templateSources"]!.AsObject().Select(entry => entry.Key));
        Assert.Equal("preserved", settings["unrelated"]!.GetValue<string>());
        Assert.Empty(interaction.DisplayedErrors);
    }

    [Theory]
    [InlineData("company", new[] { "--path", "relative/path" })]
    [InlineData("company", new[] { "--ref", "main" })]
    [InlineData("company", new string[0])]
    [InlineData("company", new[] { "--repository", "http://example.test/repo" })]
    [InlineData("company", new[] { "--repository", "https://example.test/repo", "--path", "../escape" })]
    [InlineData("nested.name", new[] { "--repository", "https://example.test/templates" })]
    [InlineData("nested:name", new[] { "--repository", "https://example.test/templates" })]
    public async Task TemplateSourceCommands_InvalidAddDoesNotWrite(string name, string[] options)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();
        var path = provider.GetRequiredService<IConfigurationService>().GetSettingsFilePath(true);
        const string original = """{ /*keep*/ "unrelated": "preserved", }""";
        await File.WriteAllTextAsync(path, original);

        var result = await provider.GetRequiredService<RootCommand>()
            .Parse(["template", "source", "add", name, .. options]).InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, result);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Single(interaction.DisplayedErrors);
    }

    [Fact]
    public async Task TemplateSourceCommands_DuplicateAndMissingNamesDoNotWrite()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();
        var path = provider.GetRequiredService<IConfigurationService>().GetSettingsFilePath(true);
        const string original = """{ /*keep*/ "templateSources": { "Company": { "repository": "https://example.test/templates" } } }""";
        await File.WriteAllTextAsync(path, original);
        var root = provider.GetRequiredService<RootCommand>();

        Assert.Equal(CliExitCodes.InvalidCommand, await root.Parse("template source add company --repository https://example.test/replacement --yes").InvokeAsync().DefaultTimeout());
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Equal(CliExitCodes.ConfigNotFound, await root.Parse("template source remove missing").InvokeAsync().DefaultTimeout());
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Equal(2, interaction.DisplayedErrors.Count);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("""{"templateSources":null}""")]
    [InlineData("""{"templateSources":42}""")]
    public async Task TemplateSourceCommands_DoNotOverwriteMalformedSettings(string original)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();
        var path = provider.GetRequiredService<IConfigurationService>().GetSettingsFilePath(true);
        await File.WriteAllTextAsync(path, original);
        var root = provider.GetRequiredService<RootCommand>();

        Assert.Equal(CliExitCodes.InvalidCommand, await root.Parse("template source add company --repository https://example.test/templates --yes").InvokeAsync().DefaultTimeout());
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Equal(CliExitCodes.InvalidCommand, await root.Parse("template source remove company").InvokeAsync().DefaultTimeout());
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Equal(2, interaction.DisplayedErrors.Count);
    }

    [Theory]
    [InlineData("templateSources.company.unknown", "value")]
    [InlineData("templateSources.company.path.child", "value")]
    [InlineData("templateSources.company.path", "../escape")]
    [InlineData("templateSources.company", "/some/path")]
    [InlineData("templateSources", "value")]
    public async Task TemplateSourceConfig_InvalidEditPreservesBytes(string key, string value)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();
        var path = provider.GetRequiredService<IConfigurationService>().GetSettingsFilePath(true);
        const string original = """{ /*keep*/ "templateSources": { "company": { "repository": "https://example.test/repo" } }, }""";
        await File.WriteAllTextAsync(path, original);

        var result = await provider.GetRequiredService<RootCommand>()
            .Parse(["config", "set", key, value, "--global"]).InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, result);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Contains(key.Split('.')[0], Assert.Single(interaction.DisplayedErrors));
    }

    [Fact]
    public async Task TemplateSourceCommands_CaseInsensitiveRootAndSourceNamesPreserveExistingSpelling()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();
        var path = provider.GetRequiredService<IConfigurationService>().GetSettingsFilePath(true);
        var original = new JsonObject { ["TemplateSources"] = new JsonObject { ["Company"] = new JsonObject { ["path"] = workspace.Path } } }.ToJsonString();
        await File.WriteAllTextAsync(path, original);
        var root = provider.GetRequiredService<RootCommand>();

        Assert.Equal(CliExitCodes.InvalidCommand, await root.Parse("template source add COMPANY --repository https://example.test/duplicate --yes").InvokeAsync().DefaultTimeout());
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Equal(CliExitCodes.Success, await root.Parse("config set templateSources.company.path templates/api --global").InvokeAsync().DefaultTimeout());
        Assert.Equal(CliExitCodes.Success, await root.Parse("config set TEMPLATESOURCES.COMPANY.repository https://example.test/repo --global").InvokeAsync().DefaultTimeout());
        Assert.Equal(CliExitCodes.Success, await root.Parse("config set templateSources.company.ref main --global").InvokeAsync().DefaultTimeout());
        Assert.True(await provider.GetRequiredService<IConfigurationService>().DeleteConfigurationAsync("TEMPLATESOURCES.COMPANY.ref", isGlobal: true));
        var settings = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        Assert.Equal(["TemplateSources"], settings.Select(entry => entry.Key));
        Assert.Equal(["Company"], settings["TemplateSources"]!.AsObject().Select(entry => entry.Key));
        var source = Assert.Single(await provider.GetRequiredService<ConfiguredTemplateSourceService>().GetSourcesAsync(default));
        Assert.Equal("Company", source.Key);
        Assert.Equal(new TemplateSource("templates/api", "https://example.test/repo", null), source.Value.Template);
        Assert.Equal("Company", Assert.Single(await provider.GetRequiredService<TemplateSourceStore>().ListAsync(default)).Key);

        Assert.Equal(CliExitCodes.Success, await root.Parse("template source add other --repository https://example.test/other --yes").InvokeAsync().DefaultTimeout());
        Assert.Equal(CliExitCodes.Success, await root.Parse("template source remove company").InvokeAsync().DefaultTimeout());
        Assert.Equal(["other"], JsonNode.Parse(await File.ReadAllTextAsync(path))!["TemplateSources"]!.AsObject().Select(entry => entry.Key));
        Assert.Equal(CliExitCodes.Success, await root.Parse("template source remove OTHER").InvokeAsync().DefaultTimeout());
        Assert.Empty(JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject());
    }

    [Theory]
    [InlineData("repository", "ref", "path")]
    [InlineData("Repository", "Ref", "Path")]
    public async Task TemplateSourceConfig_EditsTypedSourcesPreservingUnknownFieldsAndExistingKeys(
        string repositoryField, string refField, string pathField)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CreateServices(workspace, new TestInteractionService(), interactive: true).BuildServiceProvider();
        var configuration = provider.GetRequiredService<IConfigurationService>();
        var path = configuration.GetSettingsFilePath(true);
        var settings = new JsonObject
        {
            ["templateSources"] = new JsonObject
            {
                ["company"] = new JsonObject
                {
                    [repositoryField] = "https://example.test/repo",
                    [refField] = null,
                    [pathField] = null,
                    ["futureFeature"] = new JsonObject { ["enabled"] = true }
                }
            }
        };
        await File.WriteAllTextAsync(path, settings.ToJsonString());
        var sources = provider.GetRequiredService<ConfiguredTemplateSourceService>();
        Assert.Equal(new TemplateSource(".", "https://example.test/repo", null),
            Assert.Single(await sources.GetSourcesAsync(default)).Value.Template);

        var result = await provider.GetRequiredService<RootCommand>()
            .Parse("config set templateSources.company.path templates/api --global").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, result);
        var edited = JsonNode.Parse(await File.ReadAllTextAsync(path))!["templateSources"]!["company"]!.AsObject();
        Assert.Equal([repositoryField, refField, pathField, "futureFeature"], edited.Select(property => property.Key));
        Assert.True(JsonNode.DeepEquals(settings["templateSources"]!["company"]!["futureFeature"], edited["futureFeature"]));
        Assert.Equal("templates/api", edited[pathField]!.GetValue<string>());
        Assert.Null(edited[refField]);
        Assert.Equal(new TemplateSource("templates/api", "https://example.test/repo", null),
            Assert.Single(await sources.GetSourcesAsync(default)).Value.Template);

        Assert.True(await configuration.DeleteConfigurationAsync("templateSources.company.PATH", isGlobal: true));
        var deleted = JsonNode.Parse(await File.ReadAllTextAsync(path))!["templateSources"]!["company"]!.AsObject();
        Assert.Equal([repositoryField, refField, "futureFeature"], deleted.Select(property => property.Key));
        Assert.True(JsonNode.DeepEquals(settings["templateSources"]!["company"]!["futureFeature"], deleted["futureFeature"]));
        Assert.Equal(new TemplateSource(".", "https://example.test/repo", null),
            Assert.Single(await sources.GetSourcesAsync(default)).Value.Template);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"templateSources":{"company":{"ref":"main"}}}""")]
    [InlineData("""{"TemplateSources":{"Company":{"ref":"main"}}}""")]
    public async Task TemplateSourceConfig_IncompleteSourceRejectsEscapingPathWithoutWriting(string original)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();
        var path = provider.GetRequiredService<IConfigurationService>().GetSettingsFilePath(true);
        await File.WriteAllTextAsync(path, original);

        var result = await provider.GetRequiredService<RootCommand>()
            .Parse("config set templateSources.company.path ../escape --global").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, result);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Single(interaction.DisplayedErrors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TemplateSourceCommands_UnavailableLockDoesNotWrite(bool remove)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();
        var path = provider.GetRequiredService<IConfigurationService>().GetSettingsFilePath(true);
        const string original = """{"templateSources":{"company":{"repository":"https://example.test/templates"}}}""";
        await File.WriteAllTextAsync(path, original);
        var cache = provider.GetRequiredService<CliExecutionContext>().CacheDirectory;
        cache.Create();
        await File.WriteAllTextAsync(Path.Combine(cache.FullName, "workspace-config-locks"), "Blocks lock directory creation.");

        var command = remove ? "template source remove company" : "template source add other --repository https://example.test/other --yes";
        var result = await provider.GetRequiredService<RootCommand>().Parse(command).InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, result);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Single(interaction.DisplayedErrors);
    }

    [Fact]
    public async Task TemplateSourceConfig_PartialGitObjectCanBeCompletedAndBrokenSourceRemoved()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();
        var path = provider.GetRequiredService<IConfigurationService>().GetSettingsFilePath(true);
        await File.WriteAllTextAsync(path, """{"templateSources":{"broken":false}}""");
        var root = provider.GetRequiredService<RootCommand>();

        foreach (var (field, value) in new[] { ("ref", "release/17.0"), ("path", "templates/api"), ("repository", "https://example.test/repo") })
        {
            Assert.Equal(CliExitCodes.Success, await root.Parse(["config", "set", $"templateSources.company.{field}", value, "--global"]).InvokeAsync().DefaultTimeout());
        }
        await Assert.ThrowsAsync<JsonException>(() =>
            provider.GetRequiredService<ConfiguredTemplateSourceService>().GetSourcesAsync(default));
        Assert.False(JsonNode.Parse(await File.ReadAllTextAsync(path))!["templateSources"]!["broken"]!.GetValue<bool>());

        Assert.Equal(CliExitCodes.Success, await root.Parse("template source remove broken").InvokeAsync().DefaultTimeout());
        var source = Assert.Single(await provider.GetRequiredService<ConfiguredTemplateSourceService>().GetSourcesAsync(default));
        Assert.Equal("company", source.Key);
        Assert.Equal(new TemplateSource("templates/api", "https://example.test/repo", "release/17.0"), source.Value.Template);
        Assert.Empty(interaction.DisplayedErrors);
    }
}
