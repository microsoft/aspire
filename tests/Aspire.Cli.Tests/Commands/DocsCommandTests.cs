// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Mcp.Docs;
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
        var provider = services.BuildServiceProvider();

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
        var provider = services.BuildServiceProvider();

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
        var provider = services.BuildServiceProvider();

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
        var provider = services.BuildServiceProvider();

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
        var provider = services.BuildServiceProvider();

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
        var provider = services.BuildServiceProvider();

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
        var provider = services.BuildServiceProvider();

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
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("docs get redis-integration --section \"Getting Started\"");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task DocsGetCommand_WithInvalidSlug_ReturnsError()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.DocsIndexServiceFactory = _ => new TestDocsIndexService();
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("docs get nonexistent-page");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.NotEqual(0, exitCode);
    }
}

internal sealed class TestDocsIndexService : IDocsIndexService
{
    public bool IsIndexed => true;

    public ValueTask EnsureIndexedAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<DocsListItem>> ListDocumentsAsync(CancellationToken cancellationToken = default)
    {
        var docs = new List<DocsListItem>
        {
            new() { Title = "Redis Integration", Slug = "redis-integration", Summary = "Learn how to use Redis" },
            new() { Title = "PostgreSQL Integration", Slug = "postgresql-integration", Summary = "Learn how to use PostgreSQL" },
            new() { Title = "Getting Started", Slug = "getting-started", Summary = "Get started with Aspire" }
        };
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
        if (slug == "redis-integration")
        {
            return ValueTask.FromResult<DocsContent?>(new DocsContent
            {
                Title = "Redis Integration",
                Slug = "redis-integration",
                Summary = "Learn how to use Redis",
                Content = "# Redis Integration\n\nThis is the Redis integration documentation.",
                Sections = new[] { "Getting Started", "Hosting integration", "Client integration" }
            });
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
