// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Net;
using Aspire.Cli.Git;
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
    private const string CatalogJson = """
        {"templates":[
          {"name":"Contoso API","description":"An API with PostgreSQL","repository":"https://example.test/api","ref":"main","path":"templates/api"},
          {"name":"Worker","description":"Background processing","repository":"https://example.test/worker","ref":"release/17.0","path":"."}
        ]}
        """;

    [Theory]
    [InlineData("", 2)]
    [InlineData("CONTOSO", 1)]
    [InlineData("postgresql", 1)]
    [InlineData("not-found", 0)]
    public async Task TemplateCatalog_StaticCatalogAlwaysFiltersLocallyWithoutGit(string query, int expected)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        var git = new TestGitRunner();
        using var handler = new MockHttpMessageHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(CatalogJson) });
        var services = CreateServices(workspace, interaction, interactive: true);
        services.AddSingleton<IHttpClientFactory>(new MockHttpClientFactory(handler));
        services.AddSingleton<IGitRunner>(git);
        using var provider = services.BuildServiceProvider();
        await WriteConfiguredSourcesAsync(provider, new() { ["catalog"] = new { href = "https://example.test/templates.json" } });

        var results = await provider.GetRequiredService<ConfiguredTemplateSourceService>().SearchAsync(query, default);

        Assert.Equal(expected, results.Count);
        Assert.All(results, result => Assert.Equal("catalog", result.SourceName));
        Assert.Empty(git.ResolveCalls);
        Assert.Empty(git.CloneCalls);
        Assert.Empty(interaction.DisplayedMessages);
    }

    [Fact]
    public async Task TemplateCatalog_EscapesQueryAndPassesCliIdentityVersion()
    {
        const string query = "API &redirect=https://other.test/ +#?";
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        Uri? requested = null;
        using var handler = new MockHttpMessageHandler(request =>
        {
            requested = request.RequestUri;
            return new(HttpStatusCode.OK) { Content = new StringContent(CatalogJson) };
        });
        var services = CreateServices(workspace, interaction, interactive: true);
        services.AddSingleton<IHttpClientFactory>(new MockHttpClientFactory(handler));
        using var provider = services.BuildServiceProvider();
        var version = provider.GetRequiredService<CliExecutionContext>().IdentityVersion;

        await provider.GetRequiredService<TemplateCatalogService>().SearchAsync(
            "https://example.test/index?q={search}&cliVersion={cliVersion}", "templates.catalog", query, default);

        Assert.Equal($"https://example.test/index?q={Uri.EscapeDataString(query)}&cliVersion={Uri.EscapeDataString(version)}",
            requested!.AbsoluteUri);
    }

    [Fact]
    public async Task TemplateCatalog_SelectionAcquiresOnlyChosenRepositoryAndPromptsActualParameters()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        interaction.SetupStringPromptResponse("");
        interaction.SetupStringPromptResponse("Welcome");
        var git = new TestGitRunner
        {
            CloneAsyncCallback = (_, destination, _, _) => CreateClonedSearchTemplateAsync(workspace, destination, "Catalog")
        };
        var selectionCount = 0;
        interaction.PromptForSelectionCallback = (_, choices, formatter, _) =>
        {
            var options = choices.Cast<object>().ToArray();
            Assert.Empty(git.ResolveCalls);
            Assert.Empty(git.CloneCalls);
            return ++selectionCount == 1 ? options[^1] : options[1];
        };
        using var handler = new MockHttpMessageHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(CatalogJson) });
        var services = CreateServices(workspace, interaction, interactive: true);
        services.AddSingleton<IHttpClientFactory>(new MockHttpClientFactory(handler));
        services.AddSingleton<IGitRunner>(git);
        using var provider = services.BuildServiceProvider();
        await WriteConfiguredSourcesAsync(provider, new() { ["catalog"] = new { href = "https://example.test/index" } });

        var result = await provider.GetRequiredService<RootCommand>()
            .Parse("new --name Demo --output out --suppress-agent-init").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, result);
        Assert.Equal(("https://example.test/worker", (string?)"release/17.0"), Assert.Single(git.ResolveCalls));
        Assert.Single(git.CloneCalls);
        Assert.Equal([NewCommandStrings.TemplateSearchPrompt, "Greeting text"], interaction.StringPromptCalls.Select(call => call.PromptText));
        Assert.Empty(interaction.DisplayedErrors);
        await Verify(ReadOutput(workspace));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task TemplateCatalog_SelectionRejectsPathsWithoutExactlyOneTemplate(int count)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        var git = new TestGitRunner
        {
            CloneAsyncCallback = async (_, destination, _, _) =>
            {
                destination.CreateSubdirectory(".git");
                for (var index = 0; index < count; index++)
                {
                    await CreateSearchTemplateAsync(workspace,
                        Path.GetRelativePath(workspace.Path, Path.Combine(destination.FullName, $"template{index}")),
                        $"template{index}", $"Template {index}", "Unused");
                }
            }
        };
        using var handler = new MockHttpMessageHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(CatalogJson) });
        var services = CreateServices(workspace, interaction, interactive: true);
        services.AddSingleton<IHttpClientFactory>(new MockHttpClientFactory(handler));
        services.AddSingleton<IGitRunner>(git);
        using var provider = services.BuildServiceProvider();
        var template = Assert.Single(await provider.GetRequiredService<TemplateCatalogService>()
            .SearchAsync("https://example.test/index", "templates.catalog", "Worker", default));
        Assert.Empty(git.ResolveCalls);
        var parseResult = provider.GetRequiredService<RootCommand>().Parse("new --name Demo --output out --suppress-agent-init");

        var exception = await Assert.ThrowsAsync<TemplateDiscoveryException>(() =>
            template.ApplyTemplateAsync(new TemplateInputs { Name = "Demo", Output = "out" }, parseResult, default));

        if (count == 2)
        {
            Assert.Equal(string.Format(CultureInfo.CurrentCulture, NewCommandStrings.TemplateCatalogRequiresSingleTemplate, "Worker"), exception.Message);
        }
        Assert.Single(git.ResolveCalls);
        Assert.Single(git.CloneCalls);
        Assert.False(Directory.Exists(Path.Combine(workspace.Path, "out")));
        Assert.Empty(interaction.StringPromptCalls);
    }

    [Theory]
    [InlineData("new --help")]
    [InlineData("template --help")]
    [InlineData("template source add --help")]
    public async Task TemplateCatalog_HelpNeverContactsConfiguredSources(string command)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        var requests = 0;
        using var handler = new MockHttpMessageHandler(_ =>
        {
            requests++;
            return new(HttpStatusCode.OK) { Content = new StringContent(CatalogJson) };
        });
        var services = CreateServices(workspace, interaction, interactive: true);
        services.AddSingleton<IHttpClientFactory>(new MockHttpClientFactory(handler));
        using var provider = services.BuildServiceProvider();
        await WriteConfiguredSourcesAsync(provider, new() { ["catalog"] = new { href = "https://example.test/index" } });

        Assert.Equal(CliExitCodes.Success, await provider.GetRequiredService<RootCommand>().Parse(command).InvokeAsync().DefaultTimeout());
        Assert.Equal(0, requests);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"templates":{}}""")]
    [InlineData("""{"templates":null}""")]
    public async Task TemplateCatalog_InvalidResponseDoesNotHideLocalResults(string json)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateSearchTemplateAsync(workspace, "local", "sample", "Sample", "Local");
        var interaction = new TestInteractionService();
        using var handler = new MockHttpMessageHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(json) });
        var services = CreateServices(workspace, interaction, interactive: true);
        services.AddSingleton<IHttpClientFactory>(new MockHttpClientFactory(handler));
        using var provider = services.BuildServiceProvider();
        await WriteConfiguredSourcesAsync(provider, new()
        {
            ["catalog"] = new { href = "https://example.test/index" },
            ["local"] = Path.Combine(workspace.Path, "local")
        });

        var result = Assert.Single(await provider.GetRequiredService<ConfiguredTemplateSourceService>().SearchAsync("", default));

        Assert.Equal("local", result.SourceName);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, NewCommandStrings.TemplateSourceSearchFailed,
            "catalog", NewCommandStrings.TemplateCatalogInvalidResponse), Assert.Single(interaction.DisplayedMessages).Message);
    }

    [Theory]
    [InlineData("null", "templates.catalog.templates[0]")]
    [InlineData("""{"description":"Description","repository":"https://example.test/repo","ref":"main","path":"."}""", "templates.catalog.templates[0].name")]
    [InlineData("""{"name":"Name","description":"Description","repository":"http://example.test/repo","ref":"main","path":"."}""", "templates.catalog.templates[0].repository")]
    [InlineData("""{"name":"Name","description":"Description","repository":"https://example.test/repo","path":"."}""", "templates.catalog.templates[0].ref")]
    public async Task TemplateCatalog_InvalidEntriesDoNotHideValidEntries(string entry, string settingPath)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        var json = $$"""{"templates":[{{entry}},{"name":"Valid","description":"Valid template","repository":"https://example.test/repo","ref":"main","path":"."}]}""";
        using var handler = new MockHttpMessageHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(json) });
        var services = CreateServices(workspace, interaction, interactive: true);
        services.AddSingleton<IHttpClientFactory>(new MockHttpClientFactory(handler));
        using var provider = services.BuildServiceProvider();

        var result = Assert.Single(await provider.GetRequiredService<TemplateCatalogService>()
            .SearchAsync("https://example.test/index", "templates.catalog", "", default));

        Assert.Equal("Valid", result.Name);
        Assert.Contains($"'{settingPath}'", Assert.Single(interaction.DisplayedMessages).Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task TemplateCatalog_HttpFailureDoesNotExposeUrlOrBody(HttpStatusCode status)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var handler = new MockHttpMessageHandler(_ => new(status) { Content = new StringContent("private server diagnostics") });
        var services = CreateServices(workspace, new TestInteractionService(), interactive: true);
        services.AddSingleton<IHttpClientFactory>(new MockHttpClientFactory(handler));
        using var provider = services.BuildServiceProvider();

        var exception = await Assert.ThrowsAsync<TemplateDiscoveryException>(() => provider.GetRequiredService<TemplateCatalogService>()
            .SearchAsync("https://example.test/private?q=secret", "templates.catalog", "secret", default));

        Assert.Equal(string.Format(CultureInfo.CurrentCulture, NewCommandStrings.TemplateCatalogHttpFailed, (int)status), exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TemplateCatalog_TransportAndTimeoutFailuresAreSanitized(bool timeout)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var handler = new MockHttpMessageHandler(timeout
            ? new TaskCanceledException("private URL and search")
            : new HttpRequestException("private URL and search"));
        var services = CreateServices(workspace, new TestInteractionService(), interactive: true);
        services.AddSingleton<IHttpClientFactory>(new MockHttpClientFactory(handler));
        using var provider = services.BuildServiceProvider();

        var exception = await Assert.ThrowsAsync<TemplateDiscoveryException>(() => provider.GetRequiredService<TemplateCatalogService>()
            .SearchAsync("https://example.test/private", "templates.catalog", "", default));

        Assert.Equal(timeout ? NewCommandStrings.TemplateCatalogTimedOut : NewCommandStrings.TemplateCatalogDownloadFailed, exception.Message);
    }

    [Fact]
    public async Task TemplateCatalog_CallerCancellationPropagates()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var cancellation = new CancellationTokenSource();
        using var handler = new MockHttpMessageHandler(async (_, token) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(HttpStatusCode.OK);
        });
        var services = CreateServices(workspace, new TestInteractionService(), interactive: true);
        services.AddSingleton<IHttpClientFactory>(new MockHttpClientFactory(handler));
        using var provider = services.BuildServiceProvider();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetRequiredService<TemplateCatalogService>()
            .SearchAsync("https://example.test/index", "templates.catalog", "", cancellation.Token));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task TemplateCatalog_EnforcesResponseByteLimit(int excess, bool accepted)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var json = """{"templates":[]}""";
        var content = new string(' ', TemplateCatalogService.MaximumResponseBytes - json.Length + excess) + json;
        using var handler = new MockHttpMessageHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(content) });
        var services = CreateServices(workspace, new TestInteractionService(), interactive: true);
        services.AddSingleton<IHttpClientFactory>(new MockHttpClientFactory(handler));
        using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<TemplateCatalogService>();

        if (accepted)
        {
            Assert.Empty(await catalog.SearchAsync("https://example.test/index", "templates.catalog", "", default));
        }
        else
        {
            var exception = await Assert.ThrowsAsync<TemplateDiscoveryException>(() =>
                catalog.SearchAsync("https://example.test/index", "templates.catalog", "", default));
            Assert.Equal(NewCommandStrings.TemplateCatalogDownloadFailed, exception.Message);
        }
    }
}
