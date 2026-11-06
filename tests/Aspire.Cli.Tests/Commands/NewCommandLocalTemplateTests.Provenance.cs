// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Commands;
using Aspire.Cli.Configuration;
using Aspire.Cli.Git;
using Aspire.Cli.NuGet;
using Aspire.Cli.Templating;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Utils;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using NuGet.Versioning;
using RootCommand = Aspire.Cli.Commands.RootCommand;

namespace Aspire.Cli.Tests.Commands;

public partial class NewCommandLocalTemplateTests
{
    [Theory]
    [InlineData(".", "HEAD")]
    [InlineData("templates", "release/17.0")]
    [InlineData("templates/web", "refs/tags/v17.0")]
    [InlineData("templates/web", TestGitCommandRunner.DefaultCommitSha)]
    public async Task RepositoryTemplate_ProvenanceRecordsSelectedTemplateAndResolvedCommit(string searchPath, string reference)
    {
        const string repository = "https://example.test/templates.git";
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        var git = TestGitCommandRunner.CreateForTemplateCheckout(async (_, destination, _, token) =>
        {
            destination.CreateSubdirectory(".git");
            var root = Path.GetRelativePath(workspace.Path, destination.FullName);
            await CreateSearchTemplateAsync(workspace, $"{root}/templates/web", "web", "Web template", "Rendered");
            await File.WriteAllTextAsync(Path.Combine(destination.FullName, "templates/web/aspire.config.json"), """
                {
                  "appHost": { "path": "Demo.csproj", "custom": true },
                  "sdk": { "version": "17.0.0" },
                  "packages": { "Aspire.Hosting.Redis": "17.0.0" },
                  "profiles": { "dev": { "environmentVariables": { "CUSTOM": "retained" } } },
                  "custom": { "nested": [1, true, null] },
                  "provenance": { "custom": "retained", "template": { "custom": true, "sha": "old" } }
                }
                """, token);
        });
        var services = CreateServices(workspace, interaction, interactive: false);
        services.AddSingleton<IGitCommandRunner>(git);
        using var provider = services.BuildServiceProvider();
        var template = Assert.Single(await provider.GetServices<ITemplateFactory>().OfType<TemplateEngineFactory>().Single().GetTemplatesAsync(
            new TemplateSource(searchPath, repository, reference), TestContext.Current.CancellationToken));
        var commandsBeforeGeneration = git.Commands.Count;
        var parse = provider.GetRequiredService<RootCommand>().Parse("new --name Demo --output out --suppress-agent-init");

        var result = await template.ApplyTemplateAsync(new TemplateInputs { Name = "Demo", Output = "out" }, parse, TestContext.Current.CancellationToken);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal(commandsBeforeGeneration, git.Commands.Count);
        var config = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(workspace.Path, "out/aspire.config.json")))!;
        Assert.Equal(repository, config["provenance"]!["template"]!["repository"]!.GetValue<string>());
        Assert.Equal(TestGitCommandRunner.DefaultCommitSha, config["provenance"]!["template"]!["sha"]!.GetValue<string>());
        Assert.Equal("templates/web", config["provenance"]!["template"]!["path"]!.GetValue<string>());
        var typed = AspireConfigFile.Load(Path.Combine(workspace.Path, "out"))!;
        Assert.Equal(TestGitCommandRunner.DefaultCommitSha, typed.Provenance!.Template!.Sha);
        typed.Save(Path.Combine(workspace.Path, "out"));
        var saved = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(workspace.Path, "out/aspire.config.json")))!;
        Assert.Equal("retained", saved["provenance"]!["custom"]!.GetValue<string>());
        Assert.True(saved["provenance"]!["template"]!["custom"]!.GetValue<bool>());
        await Verify(config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), "json").UseParameters(searchPath, reference);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepositoryTemplate_ProvenanceIsNotWrittenForRejectedOutput(bool interactive)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateSearchTemplateAsync(workspace, "template", "sample", "Sample", "Rendered");
        workspace.CreateDirectory("out");
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "out/existing.txt"), "preserved");
        var interaction = new TestInteractionService();
        var git = TestGitCommandRunner.CreateForTemplateCheckout((_, destination, _, _) =>
            CreateClonedSearchTemplateAsync(workspace, destination, "Remote"));
        var services = CreateServices(workspace, interaction, interactive);
        services.AddSingleton<IGitCommandRunner>(git);
        using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<RootCommand>().Parse(
            "new --template-repository https://example.test/templates.git --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.NotEqual(CliExitCodes.Success, result);
        Assert.Equal(["existing.txt"], Directory.GetFiles(Path.Combine(workspace.Path, "out")).Select(Path.GetFileName));
        Assert.Equal("preserved", await File.ReadAllTextAsync(Path.Combine(workspace.Path, "out/existing.txt")));
    }

    [Theory]
    [InlineData("https://username:password@example.test/templates.git?token=secret#fragment", "https://example.test/templates.git")]
    [InlineData("ssh://git:password@example.test:2222/templates.git", "ssh://git@example.test:2222/templates.git")]
    [InlineData("file:///templates.git?token=secret#fragment", "file:///templates.git")]
    [InlineData("git@example.test:templates.git", "git@example.test:templates.git")]
    [InlineData("repository", null)]
    public async Task RepositoryTemplate_ProvenanceDoesNotPersistRepositoryCredentials(string repository, string? expectedRepository)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        var git = TestGitCommandRunner.CreateForTemplateCheckout((_, destination, _, _) =>
            CreateClonedSearchTemplateAsync(workspace, destination, "Remote"));
        var services = CreateServices(workspace, interaction, interactive: false);
        services.AddSingleton<IGitCommandRunner>(git);
        using var provider = services.BuildServiceProvider();
        var template = Assert.Single(await provider.GetServices<ITemplateFactory>().OfType<TemplateEngineFactory>().Single().GetTemplatesAsync(
            new TemplateSource(".", repository, TestGitCommandRunner.DefaultCommitSha), TestContext.Current.CancellationToken));
        var command = provider.GetRequiredService<RootCommand>().Parse("new --name Demo --output out --suppress-agent-init");

        var result = await template.ApplyTemplateAsync(new TemplateInputs { Name = "Demo", Output = "out" }, command, TestContext.Current.CancellationToken);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        var provenance = AspireConfigFile.Load(Path.Combine(workspace.Path, "out"))!.Provenance!.Template!;
        Assert.Equal(expectedRepository ?? PathNormalizer.NormalizePathForStorage(Path.Combine(workspace.Path, "repository")), provenance.Repository);
        Assert.Equal(".", provenance.Path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepositoryTemplate_ProvenanceNormalizesLocalRepositoryPaths(bool absolute)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var repositoryPath = workspace.CreateDirectory("repository with spaces/nested").FullName;
        var repository = absolute ? repositoryPath : Path.GetRelativePath(workspace.Path, repositoryPath);
        var interaction = new TestInteractionService();
        var git = TestGitCommandRunner.CreateForTemplateCheckout((_, destination, _, _) =>
            CreateClonedSearchTemplateAsync(workspace, destination, "Remote"));
        var services = CreateServices(workspace, interaction, interactive: false);
        services.AddSingleton<IGitCommandRunner>(git);
        using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<RootCommand>().Parse(
            ["new", "--template-repository", repository, "--name", "Demo", "--output", "out", "--suppress-agent-init"])
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, result);
        var provenance = AspireConfigFile.Load(Path.Combine(workspace.Path, "out"))!.Provenance!.Template!;
        Assert.Equal(PathNormalizer.NormalizePathForStorage(repositoryPath), provenance.Repository);
        Assert.Equal(".", provenance.Path);
    }

    [Fact]
    public async Task LocalTemplate_ProvenanceIsNotInventedAndConfigIsUnchanged()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateSearchTemplateAsync(workspace, "template", "sample", "Sample", "Rendered");
        const string content = """{"custom":{"retained":true},"sdk":{"version":"17.0.0"}}""";
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "template/aspire.config.json"), content);
        var git = new TestGitCommandRunner();
        var services = CreateServices(workspace, new TestInteractionService(), interactive: false);
        services.AddSingleton<IGitCommandRunner>(git);
        using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<RootCommand>().Parse(
            "new --template-path template --name Demo --output out --suppress-agent-init").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, result);
        Assert.Equal(content, await File.ReadAllTextAsync(Path.Combine(workspace.Path, "out/aspire.config.json")));
        Assert.Null(AspireConfigFile.Load(Path.Combine(workspace.Path, "out"))!.Provenance);
        Assert.Empty(git.Commands);
    }

    [Fact]
    public async Task RepositoryTemplate_CancelledSelectionDoesNotWriteProvenance()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        var git = TestGitCommandRunner.CreateForTemplateCheckout((_, destination, _, _) =>
            CreateClonedSearchTemplateAsync(workspace, destination, "Remote"));
        var services = CreateServices(workspace, interaction, interactive: true);
        services.AddSingleton<IGitCommandRunner>(git);
        using var provider = services.BuildServiceProvider();
        await WriteConfiguredSourcesAsync(provider, new() { ["company"] = "https://example.test/templates.git" });
        using var cancellation = new CancellationTokenSource();
        interaction.PromptForSelectionCallback = (_, _, _, _) =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        };

        await provider.GetRequiredService<RootCommand>().Parse("new --name Demo --output out --suppress-agent-init")
            .InvokeAsync(cancellationToken: cancellation.Token).DefaultTimeout();

        Assert.False(Directory.Exists(Path.Combine(workspace.Path, "out")));
    }

    [Fact]
    public async Task RepositoryTemplate_FailedEngineValidationDoesNotWriteProvenance()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        var git = TestGitCommandRunner.CreateForTemplateCheckout(async (_, destination, _, _) =>
        {
            destination.CreateSubdirectory(".git");
            await CreateTemplateAsync(workspace, Path.GetRelativePath(workspace.Path, destination.FullName), requiredGreeting: true);
        });
        var services = CreateServices(workspace, interaction, interactive: false);
        services.AddSingleton<IGitCommandRunner>(git);
        using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<RootCommand>().Parse(
            "new --template-repository https://example.test/templates.git --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToCreateNewProject, result);
        Assert.False(Directory.Exists(Path.Combine(workspace.Path, "out")));
        Assert.NotEmpty(interaction.DisplayedErrors);
        Assert.Empty(interaction.DisplayedSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepositoryTemplate_ComputedGenerationWritesProvenanceOnlyAfterSuccessfulResolution(bool failResolution)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateComputedVersionTemplateAsync(workspace, """
            {"kind":"nugetPackageVersion","packageId":"Example","versionRange":"[1.0.0,2.0.0)"}
            """);
        var interaction = new TestInteractionService();
        var git = TestGitCommandRunner.CreateForTemplateCheckout((_, destination, _, _) =>
        {
            destination.CreateSubdirectory(".git");
            FileSystemHelper.CopyDirectory(Path.Combine(workspace.Path, "template"), destination.FullName);
            return Task.CompletedTask;
        });
        var services = CreateServices(workspace, interaction, interactive: false);
        services.AddSingleton<IGitCommandRunner>(git);
        services.AddSingleton<INuGetClient>(new FakeNuGetClient
        {
            GetPackageVersionsCallback = (_, workingDirectory, _) =>
            {
                Assert.False(Directory.Exists(Path.Combine(workspace.Path, "out")));
                Assert.False(File.Exists(Path.Combine(workingDirectory, "aspire.config.json")));
                return Task.FromResult<IReadOnlyList<NuGetVersion>>(failResolution ? [] : [NuGetVersion.Parse("1.5.0")]);
            }
        });
        using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<RootCommand>().Parse(
            "new --template-repository https://example.test/templates.git --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(failResolution ? CliExitCodes.FailedToCreateNewProject : CliExitCodes.Success, result);
        if (failResolution)
        {
            Assert.False(Directory.Exists(Path.Combine(workspace.Path, "out")));
        }
        else
        {
            Assert.Equal(TestGitCommandRunner.DefaultCommitSha,
                AspireConfigFile.Load(Path.Combine(workspace.Path, "out"))!.Provenance!.Template!.Sha);
        }
    }

    [Theory]
    [InlineData("""{ "provenance": false }""")]
    [InlineData("""{ "provenance": { "template": "invalid" } }""")]
    [InlineData("""{ broken json }""")]
    public async Task RepositoryTemplate_InvalidConfigIsPreservedAndReportsFailure(string content)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        var git = TestGitCommandRunner.CreateForTemplateCheckout(async (_, destination, _, token) =>
        {
            await CreateClonedSearchTemplateAsync(workspace, destination, "Remote");
            await File.WriteAllTextAsync(Path.Combine(destination.FullName, "aspire.config.json"), content, token);
        });
        var services = CreateServices(workspace, interaction, interactive: false);
        services.AddSingleton<IGitCommandRunner>(git);
        using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<RootCommand>().Parse(
            "new --template-repository https://example.test/templates.git --name Demo --output out --suppress-agent-init")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToCreateNewProject, result);
        Assert.Equal(content, await File.ReadAllTextAsync(Path.Combine(workspace.Path, "out/aspire.config.json")));
        Assert.NotEmpty(interaction.DisplayedErrors);
        Assert.Empty(interaction.DisplayedSuccess);
    }
}
