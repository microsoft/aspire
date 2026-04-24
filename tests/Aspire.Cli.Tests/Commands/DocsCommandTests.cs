// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Documentation.Docs;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests.Commands;

public class DocsCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task DocsCommand_WithNoSubcommand_ShowsHelp()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.DocsIndexServiceFactory = _ => new TestDocsIndexService();
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("docs");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        // Returns InvalidCommand exit code when no subcommand is provided (shows help)
        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
    }

    [Fact]
    public async Task DocsListCommand_ReturnsDocuments()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.DocsIndexServiceFactory = _ => new TestDocsIndexService();
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("docs list");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task DocsListCommand_WithJsonFormat_ReturnsJson()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.DocsIndexServiceFactory = _ => new TestDocsIndexService();
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("docs list --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task DocsSearchCommand_WithQuery_ReturnsResults()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.DocsIndexServiceFactory = _ => new TestDocsIndexService();
            options.DocsSearchServiceFactory = _ => new TestDocsSearchService();
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("docs search redis");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task DocsSearchCommand_WithLimit_RespectsLimit()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.DocsIndexServiceFactory = _ => new TestDocsIndexService();
            options.DocsSearchServiceFactory = _ => new TestDocsSearchService();
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("docs search redis -n 3");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task DocsSearchCommand_WithJsonFormat_ReturnsJson()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.DocsIndexServiceFactory = _ => new TestDocsIndexService();
            options.DocsSearchServiceFactory = _ => new TestDocsSearchService();
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("docs search redis --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task DocsGetCommand_WithValidSlug_ReturnsContent()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.DocsIndexServiceFactory = _ => new TestDocsIndexService();
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("docs get redis-integration");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task DocsGetCommand_WithSection_ReturnsSection()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.DocsIndexServiceFactory = _ => new TestDocsIndexService();
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("docs get redis-integration --section \"Getting Started\"");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task DocsGetCommand_WithRichMarkdown_PreservesReadableBlockOrder()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var document = new DocsContent
        {
            Title = "Docs Smoke Test",
            Slug = "docs-smoke-test",
            Summary = "Interactive rendering sample",
            Content = """
                # Docs Smoke Test
                > Learn how to configure HTTPS endpoints with the [Aspire CLI](https://example.com/install) and `aspire run`.

                ## Steps

                1. First item

                   Continued explanation.

                   * Nested item

                ## Commands

                ```bash
                aspire docs get docs-smoke-test
                ```

                ## Settings

                | Setting | Environment variable | Purpose |
                | :------ | :------------------- | ------: |
                | `Azure:SubscriptionId` | `Azure__SubscriptionId` | Target Azure subscription |
                """,
            Sections = ["Steps", "Commands", "Settings"]
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = outputWriter;
            options.DisableAnsi = true;
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateNonInteractiveHostEnvironment();
            options.DocsIndexServiceFactory = _ => new TestDocsIndexService(new Dictionary<string, DocsContent>
            {
                [document.Slug] = document
            });
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("docs get docs-smoke-test");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(0, exitCode);

        var output = string.Join("\n", outputWriter.Logs);

        Assert.Contains("Docs Smoke Test", output);
        Assert.Contains("Aspire CLI (https://example.com/install)", output);
        Assert.Contains("aspire run", output);
        Assert.DoesNotContain("[Aspire CLI](", output);
        Assert.DoesNotContain("`aspire run`", output);
        Assert.Contains("1. First item", output);
        Assert.Contains("Continued explanation.", output);
        Assert.Contains("Nested item", output);
        Assert.Contains("aspire docs get docs-smoke-test", output);
        Assert.Contains("Setting", output);
        Assert.Contains("Azure:SubscriptionId", output);
        Assert.Contains("Azure__SubscriptionId", output);
        Assert.Contains("Target Azure subscription", output);

        var headingIndex = FindLogIndex(outputWriter.Logs, "Docs Smoke Test");
        var listIndex = FindLogIndex(outputWriter.Logs, "1. First item");
        var codeIndex = FindLogIndex(outputWriter.Logs, "aspire docs get docs-smoke-test");
        var tableIndex = FindLogIndex(outputWriter.Logs, "Azure:SubscriptionId");

        Assert.True(headingIndex < listIndex);
        Assert.True(listIndex < codeIndex);
        Assert.True(codeIndex < tableIndex);
    }

    [Fact]
    public void WrapMarkdownForConsole_PreservesMarkdownStructure()
    {
        var markdown = """
            # Certificate configuration

            > Learn how to configure HTTPS endpoints with the [Aspire CLI](https://aspire.dev/get-started/install-cli/) and `aspire run`.

            ### Using the Aspire CLI (recommended)

            * First item with [a link](https://example.com/docs)
            * Second item

            ```bash
            aspire docs get certificate-configuration
            ```
            """;

        var wrapped = Aspire.Cli.Commands.DocsGetCommand.WrapMarkdownForConsole(markdown, width: 60);

        Assert.Contains("# Certificate configuration", wrapped);
        Assert.Contains("\n\n### Using the Aspire CLI (recommended)\n\n", wrapped);
        Assert.Contains("[Aspire CLI](https://aspire.dev/get-started/install-cli/)", wrapped);
        Assert.Contains("`aspire run`", wrapped);
        Assert.Contains("```bash\naspire docs get certificate-configuration\n```", wrapped.Replace("\r\n", "\n"));
    }

    [Fact]
    public void WrapMarkdownForConsole_DoesNotWrapTableRows()
    {
        var markdown = """
            | Setting | Environment variable | Purpose |
            | ---------------------- | ----------------------- | ---------------------------------------------- |
            | `Azure:SubscriptionId` | `Azure__SubscriptionId` | Target Azure subscription |
            """;

        var wrapped = Aspire.Cli.Commands.DocsGetCommand.WrapMarkdownForConsole(markdown, width: 40);

        Assert.Contains("| Setting | Environment variable | Purpose |", wrapped);
        Assert.Contains("| ---------------------- | ----------------------- | ---------------------------------------------- |", wrapped);
        Assert.Contains("| `Azure:SubscriptionId` | `Azure__SubscriptionId` | Target Azure subscription |", wrapped);
    }

    [Fact]
    public async Task DocsGetCommand_WithInvalidSlug_ReturnsError()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.DocsIndexServiceFactory = _ => new TestDocsIndexService();
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("docs get nonexistent-page");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.NotEqual(0, exitCode);
    }

    private static int FindLogIndex(IReadOnlyList<string> logs, string text)
    {
        var index = -1;

        for (var i = 0; i < logs.Count; i++)
        {
            if (logs[i].Contains(text, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }

        Assert.True(index >= 0, $"Could not find '{text}' in output.");
        return index;
    }
}

internal sealed class TestDocsIndexService : IDocsIndexService
{
    private readonly IReadOnlyDictionary<string, DocsContent> _documents;

    public TestDocsIndexService(IReadOnlyDictionary<string, DocsContent>? documents = null)
    {
        _documents = documents ?? new Dictionary<string, DocsContent>
        {
            ["redis-integration"] = new()
            {
                Title = "Redis Integration",
                Slug = "redis-integration",
                Summary = "Learn how to use Redis",
                Content = "# Redis Integration\n\nThis is the Redis integration documentation.",
                Sections = ["Getting Started", "Hosting integration", "Client integration"]
            }
        };
    }

    public bool IsIndexed => true;

    public ValueTask EnsureIndexedAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<DocsListItem>> ListDocumentsAsync(CancellationToken cancellationToken = default)
    {
        var docs = _documents.Values
            .Select(doc => new DocsListItem { Title = doc.Title, Slug = doc.Slug, Summary = doc.Summary })
            .ToList();

        docs.Add(new DocsListItem { Title = "PostgreSQL Integration", Slug = "postgresql-integration", Summary = "Learn how to use PostgreSQL" });
        docs.Add(new DocsListItem { Title = "Getting Started", Slug = "getting-started", Summary = "Get started with Aspire" });

        return ValueTask.FromResult<IReadOnlyList<DocsListItem>>(docs);
    }

    public ValueTask<IReadOnlyList<DocsSearchResult>> SearchAsync(string query, int topK = 10, CancellationToken cancellationToken = default)
    {
        var results = new List<DocsSearchResult>
        {
            new() { Title = "Redis Integration", Slug = "redis-integration", Summary = "Learn how to use Redis", Score = 100.0f, MatchedSection = "Hosting integration" },
            new() { Title = "Azure Cache for Redis", Slug = "azure-cache-redis", Summary = "Azure Redis integration", Score = 80.0f, MatchedSection = "Client integration" }
        };
        return ValueTask.FromResult<IReadOnlyList<DocsSearchResult>>(results.Take(topK).ToList() as IReadOnlyList<DocsSearchResult>);
    }

    public ValueTask<DocsContent?> GetDocumentAsync(string slug, string? section = null, CancellationToken cancellationToken = default)
    {
        if (_documents.TryGetValue(slug, out var document))
        {
            return ValueTask.FromResult<DocsContent?>(document);
        }

        return ValueTask.FromResult<DocsContent?>(null);
    }
}

internal sealed class TestDocsSearchService : IDocsSearchService
{
    public Task<DocsSearchResponse?> SearchAsync(string query, int topK = 5, CancellationToken cancellationToken = default)
    {
        var results = new List<SearchResult>
        {
            new() { Title = "Redis Integration", Slug = "redis-integration", Content = "Learn how to use Redis", Score = 100.0f, Section = "Hosting integration" },
            new() { Title = "Azure Cache for Redis", Slug = "azure-cache-redis", Content = "Azure Redis integration", Score = 80.0f, Section = "Client integration" }
        };

        return Task.FromResult<DocsSearchResponse?>(new DocsSearchResponse
        {
            Query = query,
            Results = results.Take(topK).ToList()
        });
    }
}
