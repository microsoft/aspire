// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.Configuration;
using Aspire.Cli.Templating;
using Aspire.Cli.Tests.TestServices;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using RootCommand = Aspire.Cli.Commands.RootCommand;

namespace Aspire.Cli.Tests.Commands;

public partial class NewCommandLocalTemplateTests
{
    [Theory]
    [InlineData("local")]
    [InlineData("git")]
    [InlineData("catalog")]
    public async Task TemplateSourceCommands_AddAndRemoveGlobalObjects(string kind)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();
        var configuration = provider.GetRequiredService<IConfigurationService>();
        var globalPath = configuration.GetSettingsFilePath(true);
        await File.WriteAllTextAsync(globalPath, """{"unrelated":"preserved","templates":{"existing":{"href":"https://example.test/other"}}}""");
        var localPath = configuration.GetSettingsFilePath(false);
        var localBefore = File.Exists(localPath) ? await File.ReadAllTextAsync(localPath) : null;
        var options = kind switch
        {
            "local" => new[] { "--path", workspace.Path },
            "git" => ["--repository", "https://example.test/templates", "--ref", "release/17.0", "--path", "templates/api"],
            _ => ["--href", "https://example.test/templates?q={search}&cliVersion={cliVersion}"]
        };
        var root = provider.GetRequiredService<RootCommand>();

        Assert.Equal(CliExitCodes.Success, await root.Parse(["template", "source", "add", "company", .. options]).InvokeAsync().DefaultTimeout());
        var settings = JsonNode.Parse(await File.ReadAllTextAsync(globalPath))!;
        var source = Assert.IsType<JsonObject>(settings["templates"]!["company"]);
        Assert.Equal(options.Length / 2, source.Count);
        for (var index = 0; index < options.Length; index += 2)
        {
            Assert.Equal(options[index + 1], source[options[index][2..]]!.GetValue<string>());
        }
        Assert.Equal("preserved", settings["unrelated"]!.GetValue<string>());
        Assert.Equal(2, settings["templates"]!.AsObject().Count);
        Assert.Equal(localBefore, File.Exists(localPath) ? await File.ReadAllTextAsync(localPath) : null);

        Assert.Equal(CliExitCodes.Success, await root.Parse("template source remove COMPANY").InvokeAsync().DefaultTimeout());
        settings = JsonNode.Parse(await File.ReadAllTextAsync(globalPath))!;
        Assert.Equal(["existing"], settings["templates"]!.AsObject().Select(entry => entry.Key));
        Assert.Equal("preserved", settings["unrelated"]!.GetValue<string>());
        Assert.Empty(interaction.DisplayedErrors);
    }

    [Theory]
    [InlineData("company", new[] { "--path", "relative/path" })]
    [InlineData("company", new[] { "--ref", "main" })]
    [InlineData("company", new string[0])]
    [InlineData("company", new[] { "--href", "https://example.test/templates", "--path", "templates" })]
    [InlineData("company", new[] { "--href", "https://example.test/templates", "--repository", "https://example.test/repo" })]
    [InlineData("company", new[] { "--repository", "http://example.test/repo" })]
    [InlineData("company", new[] { "--repository", "https://example.test/repo", "--path", "../escape" })]
    [InlineData("company", new[] { "--href", "https://example.test/{search}" })]
    [InlineData("nested.name", new[] { "--href", "https://example.test/templates" })]
    [InlineData("nested:name", new[] { "--href", "https://example.test/templates" })]
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
        const string original = """{ /*keep*/ "templates": { "Company": { "href": "https://example.test/index" } } }""";
        await File.WriteAllTextAsync(path, original);
        var root = provider.GetRequiredService<RootCommand>();

        Assert.Equal(CliExitCodes.InvalidCommand, await root.Parse("template source add company --href https://example.test/replacement").InvokeAsync().DefaultTimeout());
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Equal(CliExitCodes.ConfigNotFound, await root.Parse("template source remove missing").InvokeAsync().DefaultTimeout());
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Equal(2, interaction.DisplayedErrors.Count);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("""{"templates":null}""")]
    [InlineData("""{"templates":42}""")]
    public async Task TemplateSourceCommands_DoNotOverwriteMalformedSettings(string original)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();
        var path = provider.GetRequiredService<IConfigurationService>().GetSettingsFilePath(true);
        await File.WriteAllTextAsync(path, original);
        var root = provider.GetRequiredService<RootCommand>();

        Assert.Equal(CliExitCodes.InvalidCommand, await root.Parse("template source add company --href https://example.test/index").InvokeAsync().DefaultTimeout());
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Equal(CliExitCodes.InvalidCommand, await root.Parse("template source remove company").InvokeAsync().DefaultTimeout());
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Equal(2, interaction.DisplayedErrors.Count);
    }

    [Theory]
    [InlineData("templates.company.href", "https://example.test/index")]
    [InlineData("templates.company.unknown", "value")]
    [InlineData("templates.company.path.child", "value")]
    [InlineData("templates.company.path", "../escape")]
    [InlineData("templates.company", "/some/path")]
    [InlineData("templates", "value")]
    public async Task TemplateSourceConfig_InvalidEditPreservesBytes(string key, string value)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();
        var path = provider.GetRequiredService<IConfigurationService>().GetSettingsFilePath(true);
        const string original = """{ /*keep*/ "templates": { "company": { "repository": "https://example.test/repo" } }, }""";
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
        var original = new JsonObject { ["Templates"] = new JsonObject { ["Company"] = new JsonObject { ["path"] = workspace.Path } } }.ToJsonString();
        await File.WriteAllTextAsync(path, original);
        var root = provider.GetRequiredService<RootCommand>();

        Assert.Equal(CliExitCodes.InvalidCommand, await root.Parse("template source add COMPANY --href https://example.test/duplicate").InvokeAsync().DefaultTimeout());
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Equal(CliExitCodes.Success, await root.Parse("config set templates.company.path templates/api --global").InvokeAsync().DefaultTimeout());
        Assert.Equal(CliExitCodes.Success, await root.Parse("config set TEMPLATES.COMPANY.repository https://example.test/repo --global").InvokeAsync().DefaultTimeout());
        Assert.Equal(CliExitCodes.Success, await root.Parse("config set templates.company.ref main --global").InvokeAsync().DefaultTimeout());
        Assert.True(await provider.GetRequiredService<IConfigurationService>().DeleteConfigurationAsync("TEMPLATES.COMPANY.ref", isGlobal: true));
        var settings = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        Assert.Equal(["Templates"], settings.Select(entry => entry.Key));
        Assert.Equal(["Company"], settings["Templates"]!.AsObject().Select(entry => entry.Key));
        var source = Assert.Single(await provider.GetRequiredService<ConfiguredTemplateSourceService>().GetSourcesAsync(default));
        Assert.Equal("Company", source.Key);
        Assert.Equal(new TemplateSource("templates/api", "https://example.test/repo", null), source.Value.Template);
        Assert.Equal("Company", Assert.Single(await provider.GetRequiredService<TemplateSourceStore>().ListAsync(default)).Key);

        Assert.Equal(CliExitCodes.Success, await root.Parse("template source add other --href https://example.test/catalog").InvokeAsync().DefaultTimeout());
        Assert.Equal(CliExitCodes.Success, await root.Parse("template source remove company").InvokeAsync().DefaultTimeout());
        Assert.Equal(["other"], JsonNode.Parse(await File.ReadAllTextAsync(path))!["Templates"]!.AsObject().Select(entry => entry.Key));
        Assert.Equal(CliExitCodes.Success, await root.Parse("template source remove OTHER").InvokeAsync().DefaultTimeout());
        Assert.Empty(JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"templates":{"company":{"ref":"main"}}}""")]
    [InlineData("""{"Templates":{"Company":{"ref":"main"}}}""")]
    public async Task TemplateSourceConfig_IncompleteSourceRejectsEscapingPathWithoutWriting(string original)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();
        var path = provider.GetRequiredService<IConfigurationService>().GetSettingsFilePath(true);
        await File.WriteAllTextAsync(path, original);

        var result = await provider.GetRequiredService<RootCommand>()
            .Parse("config set templates.company.path ../escape --global").InvokeAsync().DefaultTimeout();

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
        const string original = """{"templates":{"company":{"href":"https://example.test/index"}}}""";
        await File.WriteAllTextAsync(path, original);
        var cache = provider.GetRequiredService<CliExecutionContext>().CacheDirectory;
        cache.Create();
        await File.WriteAllTextAsync(Path.Combine(cache.FullName, "workspace-config-locks"), "Blocks lock directory creation.");

        var command = remove ? "template source remove company" : "template source add other --href https://example.test/other";
        var result = await provider.GetRequiredService<RootCommand>().Parse(command).InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, result);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Single(interaction.DisplayedErrors);
    }

    [Fact]
    public async Task TemplateSourceConfig_PartialGitObjectCanBeCompletedWithoutOtherBrokenSourceBlocking()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        using var provider = CreateServices(workspace, interaction, interactive: true).BuildServiceProvider();
        var path = provider.GetRequiredService<IConfigurationService>().GetSettingsFilePath(true);
        await File.WriteAllTextAsync(path, """{"templates":{"broken":false}}""");
        var root = provider.GetRequiredService<RootCommand>();

        foreach (var (field, value) in new[] { ("ref", "release/17.0"), ("path", "templates/api"), ("repository", "https://example.test/repo") })
        {
            Assert.Equal(CliExitCodes.Success, await root.Parse(["config", "set", $"templates.company.{field}", value, "--global"]).InvokeAsync().DefaultTimeout());
        }
        var source = Assert.Single(await provider.GetRequiredService<ConfiguredTemplateSourceService>().GetSourcesAsync(default));
        Assert.Equal("company", source.Key);
        Assert.Equal(new TemplateSource("templates/api", "https://example.test/repo", "release/17.0"), source.Value.Template);
        Assert.False(JsonNode.Parse(await File.ReadAllTextAsync(path))!["templates"]!["broken"]!.GetValue<bool>());
        Assert.Empty(interaction.DisplayedErrors);
    }
}
