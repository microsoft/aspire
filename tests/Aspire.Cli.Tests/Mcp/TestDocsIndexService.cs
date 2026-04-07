// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Mcp.Docs;

namespace Aspire.Cli.Tests.Mcp;

/// <summary>
/// Test implementation of IDocsIndexService that returns canned data without making network calls.
/// </summary>
internal sealed class TestDocsIndexService : IDocsIndexService
{
    private static readonly List<DocsListItem> s_defaultDocuments =
    [
        new DocsListItem { Slug = "getting-started", Title = "Getting Started", Summary = "Learn how to get started with Aspire" },
        new DocsListItem { Slug = "fundamentals/app-host", Title = "App Host", Summary = "Learn about the Aspire app host" },
        new DocsListItem { Slug = "deployment/azure", Title = "Deploy to Azure", Summary = "Deploy your Aspire app to Azure" },
    ];

    private readonly List<DocsListItem> _documents;
    private bool _isIndexed;

    /// <summary>
    /// Creates a new instance with default documents and already indexed.
    /// </summary>
    public TestDocsIndexService() : this(s_defaultDocuments, isIndexed: true)
    {
    }

    /// <summary>
    /// Creates a new instance with specified documents and indexing state.
    /// </summary>
    /// <param name="documents">The documents to return. If null, uses default documents.</param>
    /// <param name="isIndexed">Whether the service starts in an indexed state.</param>
    public TestDocsIndexService(IEnumerable<DocsListItem>? documents, bool isIndexed = true)
    {
        _documents = documents?.ToList() ?? [.. s_defaultDocuments];
        _isIndexed = isIndexed;
    }

    public bool IsIndexed => _isIndexed;

    public ValueTask EnsureIndexedAsync(CancellationToken cancellationToken = default)
    {
        _isIndexed = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask<DocsContent?> GetDocumentAsync(string slug, string? section = null, CancellationToken cancellationToken = default)
    {
        var doc = _documents.FirstOrDefault(d => d.Slug == slug);
        if (doc is null)
        {
            return ValueTask.FromResult<DocsContent?>(null);
        }

        var content = $"# {doc.Title}\n\n{doc.Summary}\n\nThis is test content for the document.";
        return ValueTask.FromResult<DocsContent?>(new DocsContent
        {
            Slug = doc.Slug,
            Title = doc.Title,
            Summary = doc.Summary,
            Content = content,
            Sections = []
        });
    }

    public ValueTask<IReadOnlyList<DocsListItem>> ListDocumentsAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult<IReadOnlyList<DocsListItem>>(_documents);
    }

    public ValueTask<IReadOnlyList<DocsSearchResult>> SearchAsync(string query, int topK = 10, CancellationToken cancellationToken = default)
    {
        var results = _documents
            .Where(d => (d.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (d.Summary?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            .Take(topK)
            .Select(d => new DocsSearchResult { Slug = d.Slug, Title = d.Title, Summary = d.Summary, Score = 1.0f })
            .ToList();

        return ValueTask.FromResult<IReadOnlyList<DocsSearchResult>>(results);
    }
}

