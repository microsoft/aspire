// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Documentation.ApiDocs;

/// <summary>
/// Common language identifiers for API reference entries.
/// </summary>
internal static class ApiReferenceLanguages
{
    /// <summary>
    /// The C# API language identifier.
    /// </summary>
    public const string CSharp = "csharp";

    /// <summary>
    /// The TypeScript API language identifier.
    /// </summary>
    public const string TypeScript = "typescript";

    /// <summary>
    /// Gets the supported API language identifiers.
    /// </summary>
    public static IReadOnlyList<string> All { get; } =
    [
        CSharp,
        TypeScript
    ];

    /// <summary>
    /// Determines whether the specified language is supported.
    /// </summary>
    /// <param name="language">The language identifier to test.</param>
    /// <returns><c>true</c> if the language is supported; otherwise, <c>false</c>.</returns>
    public static bool IsSupported(string language) => All.Contains(language, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Common kinds for API reference entries.
/// </summary>
internal static class ApiReferenceKinds
{
    /// <summary>
    /// The package kind.
    /// </summary>
    public const string Package = "package";

    /// <summary>
    /// The module kind.
    /// </summary>
    public const string Module = "module";

    /// <summary>
    /// The type kind.
    /// </summary>
    public const string Type = "type";

    /// <summary>
    /// The symbol kind.
    /// </summary>
    public const string Symbol = "symbol";

    /// <summary>
    /// The member kind.
    /// </summary>
    public const string Member = "member";

    /// <summary>
    /// The C# member-group page kind.
    /// </summary>
    public const string MemberGroup = "member-group";
}

/// <summary>
/// Represents a normalized API reference item in the cached index.
/// </summary>
internal sealed class ApiReferenceItem
{
    /// <summary>
    /// Gets the stable identifier for the API item.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets or sets the display name for the API item.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Gets the language for the API item.
    /// </summary>
    public required string Language { get; init; }

    /// <summary>
    /// Gets or sets the kind for the API item.
    /// </summary>
    public required string Kind { get; set; }

    /// <summary>
    /// Gets the canonical page URL for the API item.
    /// </summary>
    public required string PageUrl { get; init; }

    /// <summary>
    /// Gets the parent identifier, if any.
    /// </summary>
    public string? ParentId { get; init; }

    /// <summary>
    /// Gets the member group for the API item, if any.
    /// </summary>
    public string? MemberGroup { get; init; }

    /// <summary>
    /// Gets or sets the summary for the API item.
    /// </summary>
    public string? Summary { get; set; }
}

/// <summary>
/// Represents a listable API child item.
/// </summary>
internal sealed class ApiListItem
{
    /// <summary>
    /// Gets the stable identifier for the list item.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the display name for the list item.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the language for the list item.
    /// </summary>
    public required string Language { get; init; }

    /// <summary>
    /// Gets the kind for the list item.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Gets the parent identifier, if any.
    /// </summary>
    public string? ParentId { get; init; }

    /// <summary>
    /// Gets the member group, if any.
    /// </summary>
    public string? MemberGroup { get; init; }
}

/// <summary>
/// Represents an API search result.
/// </summary>
internal sealed class ApiSearchResult
{
    /// <summary>
    /// Gets the stable identifier for the search result.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the display name for the search result.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the language for the search result.
    /// </summary>
    public required string Language { get; init; }

    /// <summary>
    /// Gets the kind for the search result.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Gets the parent identifier, if any.
    /// </summary>
    public string? ParentId { get; init; }

    /// <summary>
    /// Gets the member group, if any.
    /// </summary>
    public string? MemberGroup { get; init; }

    /// <summary>
    /// Gets the summary for the search result, if any.
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>
    /// Gets the search score.
    /// </summary>
    public required float Score { get; init; }
}

/// <summary>
/// Represents the content returned by <c>aspire docs api get</c>.
/// </summary>
internal sealed class ApiContent
{
    /// <summary>
    /// Gets the stable identifier for the content item.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the display name for the content item.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the language for the content item.
    /// </summary>
    public required string Language { get; init; }

    /// <summary>
    /// Gets the kind for the content item.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Gets the canonical URL for the content item.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Gets the parent identifier, if any.
    /// </summary>
    public string? ParentId { get; init; }

    /// <summary>
    /// Gets the member group, if any.
    /// </summary>
    public string? MemberGroup { get; init; }

    /// <summary>
    /// Gets the markdown content for the item.
    /// </summary>
    public required string Content { get; init; }
}

/// <summary>
/// Provides indexing, browsing, searching, and retrieval for Aspire API reference documentation.
/// </summary>
internal interface IApiDocsIndexService
{
    /// <summary>
    /// Gets a value indicating whether the API index is available in memory.
    /// </summary>
    bool IsIndexed { get; }

    /// <summary>
    /// Ensures that the API index has been loaded.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    ValueTask EnsureIndexedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists child API items under the specified scope.
    /// </summary>
    /// <param name="scope">The parent scope to browse.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The child items under the specified scope.</returns>
    ValueTask<IReadOnlyList<ApiListItem>> ListAsync(string scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches the API index.
    /// </summary>
    /// <param name="query">The search query.</param>
    /// <param name="language">An optional language filter.</param>
    /// <param name="topK">The maximum number of results to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The matching API items.</returns>
    ValueTask<IReadOnlyList<ApiSearchResult>> SearchAsync(string query, string? language = null, int topK = 10, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the content for the specified API item.
    /// </summary>
    /// <param name="id">The API item identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The API content, or <c>null</c> if the item is not found.</returns>
    ValueTask<ApiContent?> GetAsync(string id, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default implementation of <see cref="IApiDocsIndexService"/>.
/// </summary>
internal sealed partial class ApiDocsIndexService(IApiDocsFetcher fetcher, IApiDocsCache cache, IConfiguration configuration, ILogger<ApiDocsIndexService> logger) : IApiDocsIndexService
{
    private const string RootCSharpScope = ApiReferenceLanguages.CSharp;
    private const string RootTypeScriptScope = ApiReferenceLanguages.TypeScript;
    private const float ExactIdMatchBonus = 60.0f;
    private const float ExactNameMatchBonus = 45.0f;
    private const float PathSegmentMatchBonus = 20.0f;
    private const float NamePrefixMatchBonus = 32.0f;
    private const float PathPrefixMatchBonus = 18.0f;
    private const int PrefixTightnessMaxBonus = 16;
    private const float IdWeight = 8.0f;
    private const float NameWeight = 10.0f;
    private const float SummaryWeight = 4.0f;
    private const float ParentWeight = 2.5f;
    private const float MemberGroupWeight = 3.0f;
    private const int MinTokenLength = 2;
    private const int MemberSearchBatchSize = 8;
    private const string MemberIndexFingerprintVersion = "v2";

    private readonly IApiDocsFetcher _fetcher = fetcher;
    private readonly IApiDocsCache _cache = cache;
    private readonly string _sitemapUrl = ApiDocsSourceConfiguration.GetSitemapUrl(configuration);
    private readonly ILogger<ApiDocsIndexService> _logger = logger;
    private volatile List<IndexedApiReferenceItem>? _indexedItems;
    private volatile Dictionary<string, ApiReferenceItem>? _itemsById;
    private volatile string? _indexSourceFingerprint;
    private volatile bool _memberCacheLoaded;
    private readonly HashSet<string> _indexedMemberContainerIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private readonly SemaphoreSlim _memberIndexLock = new(1, 1);

    /// <summary>
    /// Gets a value indicating whether the API index is available in memory.
    /// </summary>
    public bool IsIndexed => _indexedItems is not null;

    /// <summary>
    /// Ensures that the API index has been loaded from cache or rebuilt from source content.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    public async ValueTask EnsureIndexedAsync(CancellationToken cancellationToken = default)
    {
        if (_indexedItems is not null && _itemsById is not null)
        {
            return;
        }

        await _indexLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_indexedItems is not null && _itemsById is not null)
            {
                return;
            }

            var startTimestamp = Stopwatch.GetTimestamp();
            _logger.LogDebug("Loading Aspire API documentation index");

            var cachedItems = await _cache.GetIndexAsync(cancellationToken).ConfigureAwait(false);
            var cachedFingerprint = await _cache.GetIndexSourceFingerprintAsync(cancellationToken).ConfigureAwait(false);
            var sitemapContent = await _fetcher.FetchSitemapAsync(cancellationToken).ConfigureAwait(false);
            if (sitemapContent is null)
            {
                if (cachedItems is not null)
                {
                    SetIndex(cachedItems);
                    _indexSourceFingerprint = cachedFingerprint;
                    _logger.LogWarning("Failed to refresh Aspire API sitemap. Using cached index with {Count} items.", cachedItems.Length);
                    return;
                }

                _logger.LogWarning("Failed to fetch Aspire API sitemap");
                return;
            }

            var currentFingerprint = SourceContentFingerprint.Compute(sitemapContent);
            if (cachedItems is not null && string.Equals(cachedFingerprint, currentFingerprint, StringComparison.Ordinal))
            {
                SetIndex(cachedItems);
                _indexSourceFingerprint = currentFingerprint;
                _logger.LogInformation("Loaded {Count} API reference items from cache in {ElapsedTime:ss\\.fff} seconds.", cachedItems.Length, Stopwatch.GetElapsedTime(startTimestamp));
                return;
            }

            var sitemapEntries = ApiSitemapParser.Parse(sitemapContent);
            var items = BuildBaseItems(sitemapEntries, _sitemapUrl);

            var itemArray = items
                .OrderBy(static item => item.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            SetIndex(itemArray);
            _indexSourceFingerprint = currentFingerprint;
            await _cache.SetIndexAsync(itemArray, cancellationToken).ConfigureAwait(false);
            await _cache.SetIndexSourceFingerprintAsync(currentFingerprint, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Indexed {Count} API reference items in {ElapsedTime:ss\\.fff} seconds.", itemArray.Length, Stopwatch.GetElapsedTime(startTimestamp));
        }
        finally
        {
            _indexLock.Release();
        }
    }

    /// <summary>
    /// Lists child API items under the specified scope.
    /// </summary>
    /// <param name="scope">The parent scope to browse.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The child items under the specified scope.</returns>
    public async ValueTask<IReadOnlyList<ApiListItem>> ListAsync(string scope, CancellationToken cancellationToken = default)
    {
        await EnsureIndexedAsync(cancellationToken).ConfigureAwait(false);

        if (_indexedItems is null || string.IsNullOrWhiteSpace(scope))
        {
            return [];
        }

        var normalizedScope = NormalizeId(scope);
        var memberContainerId = GetMemberContainerIdForScope(normalizedScope);
        if (memberContainerId is not null)
        {
            await EnsureMemberContainerIndexedAsync(memberContainerId, cancellationToken).ConfigureAwait(false);
        }

        return
        [
            .. _indexedItems
                .Where(item => string.Equals(item.Source.ParentId, normalizedScope, StringComparison.OrdinalIgnoreCase))
                .OrderBy(static item => GetKindSortOrder(item.Source.Kind))
                .ThenBy(static item => item.Source.Name, StringComparer.OrdinalIgnoreCase)
                .Select(static item => new ApiListItem
                {
                    Id = item.Source.Id,
                    Name = item.Source.Name,
                    Language = item.Source.Language,
                    Kind = item.Source.Kind,
                    ParentId = item.Source.ParentId,
                    MemberGroup = item.Source.MemberGroup
                })
        ];
    }

    /// <summary>
    /// Searches the API index.
    /// </summary>
    /// <param name="query">The search query.</param>
    /// <param name="language">An optional language filter.</param>
    /// <param name="topK">The maximum number of results to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The matching API items.</returns>
    public async ValueTask<IReadOnlyList<ApiSearchResult>> SearchAsync(string query, string? language = null, int topK = 10, CancellationToken cancellationToken = default)
    {
        await EnsureIndexedAsync(cancellationToken).ConfigureAwait(false);

        if (_indexedItems is null || string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var normalizedLanguage = NormalizeLanguage(language);
        if (language is not null && normalizedLanguage is null)
        {
            return [];
        }

        var queryTokens = Tokenize(query);
        if (queryTokens.Length is 0)
        {
            return [];
        }

        var normalizedQuery = NormalizeId(query).ToLowerInvariant();
        var routeResults = SearchIndexedItems(queryTokens, normalizedQuery, normalizedLanguage, topK);
        var results = routeResults;
        if (ShouldExpandSearchWithMemberIndex(results, normalizedQuery, normalizedLanguage))
        {
            await EnsureCachedMemberItemsLoadedAsync(cancellationToken).ConfigureAwait(false);
            results = SearchIndexedItems(queryTokens, normalizedQuery, normalizedLanguage, topK);

            if (ShouldExpandSearchWithMemberIndex(results, normalizedQuery, normalizedLanguage))
            {
                foreach (var candidateContainerIds in GetCandidateMemberContainerIds(routeResults, queryTokens, normalizedLanguage).Chunk(MemberSearchBatchSize))
                {
                    await EnsureMemberContainersIndexedAsync(candidateContainerIds, cancellationToken).ConfigureAwait(false);
                    results = SearchIndexedItems(queryTokens, normalizedQuery, normalizedLanguage, topK);
                    if (results.Count >= topK)
                    {
                        break;
                    }
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Gets the content for the specified API item.
    /// </summary>
    /// <param name="id">The API item identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The API content, or <c>null</c> if the item is not found.</returns>
    public async ValueTask<ApiContent?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureIndexedAsync(cancellationToken).ConfigureAwait(false);

        if (_itemsById is null)
        {
            return null;
        }

        var normalizedId = NormalizeId(id);
        if (!_itemsById.TryGetValue(normalizedId, out var item))
        {
            var memberContainerId = GetMemberContainerIdForId(normalizedId);
            if (memberContainerId is not null)
            {
                await EnsureMemberContainerIndexedAsync(memberContainerId, cancellationToken).ConfigureAwait(false);
            }

            if (_itemsById is null || !_itemsById.TryGetValue(normalizedId, out item))
            {
                return null;
            }
        }

        var content = await _fetcher.FetchPageAsync(item.PageUrl, cancellationToken).ConfigureAwait(false);
        if (content is null)
        {
            return null;
        }

        content = ApiDocsSourceConfiguration.RewriteMarkdownLinks(content, item.PageUrl, _sitemapUrl);

        return new ApiContent
        {
            Id = item.Id,
            Name = item.Name,
            Language = item.Language,
            Kind = item.Kind,
            Url = item.PageUrl,
            ParentId = item.ParentId,
            MemberGroup = item.MemberGroup,
            Content = content
        };
    }

    private void SetIndex(ApiReferenceItem[] items)
    {
        _indexedItems = [.. items.Select(static item => new IndexedApiReferenceItem(item))];
        _itemsById = items.ToDictionary(static item => item.Id, StringComparer.OrdinalIgnoreCase);
        _indexedMemberContainerIds.Clear();
        _memberCacheLoaded = false;
    }

    private async ValueTask EnsureCachedMemberItemsLoadedAsync(CancellationToken cancellationToken)
    {
        await EnsureIndexedAsync(cancellationToken).ConfigureAwait(false);

        if (_memberCacheLoaded || _indexedItems is null || _itemsById is null)
        {
            return;
        }

        await _memberIndexLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_memberCacheLoaded || _indexedItems is null || _itemsById is null)
            {
                return;
            }

            var currentFingerprint = _indexSourceFingerprint ?? await _cache.GetIndexSourceFingerprintAsync(cancellationToken).ConfigureAwait(false);
            var cachedMemberItems = await _cache.GetMemberIndexAsync(cancellationToken).ConfigureAwait(false);
            var cachedMemberFingerprint = await _cache.GetMemberIndexSourceFingerprintAsync(cancellationToken).ConfigureAwait(false);

            if (cachedMemberItems is not null &&
                currentFingerprint is not null &&
                string.Equals(cachedMemberFingerprint, GetMemberIndexFingerprint(currentFingerprint), StringComparison.Ordinal))
            {
                MergeIndexItems(cachedMemberItems);
                _indexedMemberContainerIds.Clear();
                foreach (var containerId in await _cache.GetIndexedMemberContainerIdsAsync(cancellationToken).ConfigureAwait(false) ?? [])
                {
                    _indexedMemberContainerIds.Add(containerId);
                }
            }
            _memberCacheLoaded = true;
        }
        finally
        {
            _memberIndexLock.Release();
        }
    }

    private async ValueTask EnsureMemberContainerIndexedAsync(string memberContainerId, CancellationToken cancellationToken)
        => await EnsureMemberContainersIndexedAsync([memberContainerId], cancellationToken).ConfigureAwait(false);

    private async ValueTask EnsureMemberContainersIndexedAsync(IReadOnlyList<string> memberContainerIds, CancellationToken cancellationToken)
    {
        await EnsureCachedMemberItemsLoadedAsync(cancellationToken).ConfigureAwait(false);

        if (_itemsById is null || memberContainerIds.Count is 0)
        {
            return;
        }

        var containersToLoad = memberContainerIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(containerId => !_indexedMemberContainerIds.Contains(containerId))
            .Select(containerId => _itemsById.TryGetValue(containerId, out var containerItem) ? containerItem : null)
            .OfType<ApiReferenceItem>()
            .ToArray();

        if (containersToLoad.Length is 0)
        {
            return;
        }

        var memberGroupsByPageUrl = GetMemberGroupsByPageUrl();
        var loadedContainers = new ConcurrentBag<LoadedMemberContainer>();

        await Parallel.ForEachAsync(
            containersToLoad,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = MemberSearchBatchSize
            },
            async (containerItem, ct) =>
            {
                var content = await _fetcher.FetchPageAsync(containerItem.PageUrl, ct).ConfigureAwait(false);
                if (content is null)
                {
                    return;
                }

                var memberItems = Deduplicate([.. ApiMemberMarkdownParser.Parse(containerItem, content, _sitemapUrl, memberGroupsByPageUrl)])
                    .OrderBy(static item => item.Id, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                loadedContainers.Add(new LoadedMemberContainer(containerItem.Id, memberItems));
            }).ConfigureAwait(false);

        if (loadedContainers.IsEmpty)
        {
            return;
        }

        await _memberIndexLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_itemsById is null)
            {
                return;
            }

            var didChange = false;
            foreach (var loadedContainer in loadedContainers.OrderBy(static container => container.ContainerId, StringComparer.OrdinalIgnoreCase))
            {
                if (!_indexedMemberContainerIds.Add(loadedContainer.ContainerId))
                {
                    continue;
                }

                MergeIndexItems(loadedContainer.MemberItems);
                didChange = true;
            }

            if (didChange)
            {
                await PersistIndexedMemberItemsAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _memberIndexLock.Release();
        }
    }

    private void MergeIndexItems(ApiReferenceItem[] items)
    {
        if (_indexedItems is null || _itemsById is null || items.Length is 0)
        {
            return;
        }

        var mergedItems = new Dictionary<string, ApiReferenceItem>(_itemsById, StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            mergedItems[item.Id] = item;
        }

        _itemsById = mergedItems;
        _indexedItems = [.. mergedItems.Values.Select(static item => new IndexedApiReferenceItem(item))];
    }

    private IReadOnlyList<ApiSearchResult> SearchIndexedItems(string[] queryTokens, string normalizedQuery, string? normalizedLanguage, int topK)
    {
        if (_indexedItems is null)
        {
            return [];
        }

        var results = new List<ApiSearchResult>();

        foreach (var item in _indexedItems)
        {
            if (normalizedLanguage is not null && !item.Source.Language.Equals(normalizedLanguage, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var score = ScoreItem(item, queryTokens, normalizedQuery);
            if (score <= 0)
            {
                continue;
            }

            results.Add(new ApiSearchResult
            {
                Id = item.Source.Id,
                Name = item.Source.Name,
                Language = item.Source.Language,
                Kind = item.Source.Kind,
                ParentId = item.Source.ParentId,
                MemberGroup = item.Source.MemberGroup,
                Summary = item.Source.Summary,
                Score = score
            });
        }

        return
        [
            .. results
                .OrderByDescending(static result => result.Score)
                .ThenBy(static result => result.Id, StringComparer.OrdinalIgnoreCase)
                .Take(topK)
        ];
    }

    private string? GetMemberContainerIdForScope(string normalizedScope)
        => _itemsById is not null &&
            _itemsById.TryGetValue(normalizedScope, out var scopeItem) &&
            string.Equals(scopeItem.Kind, ApiReferenceKinds.MemberGroup, StringComparison.OrdinalIgnoreCase)
            ? scopeItem.ParentId
            : null;

    private string? GetMemberContainerIdForId(string normalizedId)
    {
        var fragmentSeparatorIndex = normalizedId.IndexOf('#');
        return fragmentSeparatorIndex <= 0
            ? null
            : GetMemberContainerIdForScope(normalizedId[..fragmentSeparatorIndex]);
    }

    private bool ShouldExpandSearchWithMemberIndex(IReadOnlyList<ApiSearchResult> results, string normalizedQuery, string? normalizedLanguage)
        => HasGroupedMemberPages(normalizedLanguage) &&
            (results.Count is 0 ||
             (!ContainsExactSearchMatch(results, normalizedQuery) &&
              !results.Any(static result => string.Equals(result.Kind, ApiReferenceKinds.Member, StringComparison.OrdinalIgnoreCase))));

    private bool HasGroupedMemberPages(string? normalizedLanguage)
        => _itemsById is not null &&
            _itemsById.Values.Any(item =>
                string.Equals(item.Kind, ApiReferenceKinds.MemberGroup, StringComparison.OrdinalIgnoreCase) &&
                (normalizedLanguage is null || string.Equals(item.Language, normalizedLanguage, StringComparison.OrdinalIgnoreCase)));

    private static bool ContainsExactSearchMatch(IReadOnlyList<ApiSearchResult> results, string normalizedQuery)
        => results.Any(result =>
            string.Equals(NormalizeId(result.Name), normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(NormalizeId(result.Id), normalizedQuery, StringComparison.OrdinalIgnoreCase));

    private IReadOnlyList<string> GetCandidateMemberContainerIds(IReadOnlyList<ApiSearchResult> routeResults, string[] queryTokens, string? normalizedLanguage)
    {
        if (_itemsById is null)
        {
            return [];
        }

        var preferredTopLevelScopes = routeResults
            .Select(static result => GetTopLevelScopeId(result.Id))
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return
        [
            .. _itemsById.Values
                .Where(item =>
                    item.ParentId is not null &&
                    string.Equals(item.Kind, ApiReferenceKinds.MemberGroup, StringComparison.OrdinalIgnoreCase) &&
                    (normalizedLanguage is null || string.Equals(item.Language, normalizedLanguage, StringComparison.OrdinalIgnoreCase)))
                .Select(static item => item.ParentId!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(containerId => !_indexedMemberContainerIds.Contains(containerId))
                .OrderBy(containerId => preferredTopLevelScopes.Contains(GetTopLevelScopeId(containerId) ?? string.Empty) ? 0 : 1)
                .ThenByDescending(containerId => CountTokenMatches(containerId, queryTokens))
                .ThenBy(static containerId => containerId, StringComparer.OrdinalIgnoreCase)
        ];
    }

    private IReadOnlyDictionary<string, ApiReferenceItem> GetMemberGroupsByPageUrl()
        => _itemsById is null
            ? new Dictionary<string, ApiReferenceItem>(StringComparer.OrdinalIgnoreCase)
            : _itemsById.Values
                .Where(static item => string.Equals(item.Kind, ApiReferenceKinds.MemberGroup, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(static item => item.PageUrl, StringComparer.OrdinalIgnoreCase);

    private async Task PersistIndexedMemberItemsAsync(CancellationToken cancellationToken)
    {
        if (_itemsById is null)
        {
            return;
        }

        var currentFingerprint = _indexSourceFingerprint ?? await _cache.GetIndexSourceFingerprintAsync(cancellationToken).ConfigureAwait(false);
        if (currentFingerprint is null)
        {
            return;
        }

        var cachedMemberItems = _itemsById.Values
            .Where(static item =>
                string.Equals(item.Kind, ApiReferenceKinds.Member, StringComparison.OrdinalIgnoreCase) &&
                item.PageUrl.Contains('#', StringComparison.Ordinal))
            .OrderBy(static item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        await _cache.SetMemberIndexAsync(cachedMemberItems, cancellationToken).ConfigureAwait(false);
        await _cache.SetIndexedMemberContainerIdsAsync([.. _indexedMemberContainerIds], cancellationToken).ConfigureAwait(false);
        await _cache.SetMemberIndexSourceFingerprintAsync(GetMemberIndexFingerprint(currentFingerprint), cancellationToken).ConfigureAwait(false);
    }

    private static string? GetTopLevelScopeId(string id)
    {
        var segments = NormalizeId(id).Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Length >= 2
            ? $"{segments[0]}/{segments[1]}"
            : null;
    }

    private static int CountTokenMatches(string text, string[] queryTokens)
    {
        var lowerText = text.ToLowerInvariant();
        return queryTokens.Count(token => lowerText.Contains(token, StringComparison.Ordinal));
    }

    private static string GetMemberIndexFingerprint(string currentFingerprint)
        => $"{MemberIndexFingerprintVersion}:{currentFingerprint}";

    private sealed record LoadedMemberContainer(string ContainerId, ApiReferenceItem[] MemberItems);

    private static List<ApiReferenceItem> BuildBaseItems(IReadOnlyList<ApiSitemapEntry> entries, string sitemapUrl)
    {
        var items = new List<ApiReferenceItem>(entries.Count);
        var typeScriptContainerIds = entries
            .Where(static entry => entry.Language == ApiReferenceLanguages.TypeScript && entry.Segments.Length == 3)
            .Select(static entry => $"{ApiReferenceLanguages.TypeScript}/{entry.Segments[0]}/{entry.Segments[1]}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            switch (entry.Language)
            {
                case ApiReferenceLanguages.CSharp:
                    BuildCSharpItems(items, entry, sitemapUrl);
                    break;
                case ApiReferenceLanguages.TypeScript:
                    BuildTypeScriptItems(items, entry, typeScriptContainerIds, sitemapUrl);
                    break;
            }
        }

        return Deduplicate(items);
    }

    private static void BuildCSharpItems(List<ApiReferenceItem> items, ApiSitemapEntry entry, string sitemapUrl)
    {
        if (entry.Segments.Length == 1)
        {
            items.Add(new ApiReferenceItem
            {
                Id = $"{ApiReferenceLanguages.CSharp}/{entry.Segments[0]}",
                Name = entry.Segments[0],
                Language = ApiReferenceLanguages.CSharp,
                Kind = ApiReferenceKinds.Package,
                ParentId = RootCSharpScope,
                PageUrl = ApiDocsSourceConfiguration.RebasePageUrl(entry.Url, sitemapUrl)
            });
            return;
        }

        if (entry.Segments.Length == 2)
        {
            items.Add(new ApiReferenceItem
            {
                Id = $"{ApiReferenceLanguages.CSharp}/{entry.Segments[0]}/{entry.Segments[1]}",
                Name = entry.Segments[1],
                Language = ApiReferenceLanguages.CSharp,
                Kind = ApiReferenceKinds.Type,
                ParentId = $"{ApiReferenceLanguages.CSharp}/{entry.Segments[0]}",
                PageUrl = ApiDocsSourceConfiguration.RebasePageUrl(entry.Url, sitemapUrl)
            });
            return;
        }

        if (entry.Segments.Length == 3)
        {
            items.Add(new ApiReferenceItem
            {
                Id = $"{ApiReferenceLanguages.CSharp}/{entry.Segments[0]}/{entry.Segments[1]}/{entry.Segments[2]}",
                Name = entry.Segments[2],
                Language = ApiReferenceLanguages.CSharp,
                Kind = ApiReferenceKinds.MemberGroup,
                ParentId = $"{ApiReferenceLanguages.CSharp}/{entry.Segments[0]}/{entry.Segments[1]}",
                PageUrl = ApiDocsSourceConfiguration.RebasePageUrl(entry.Url, sitemapUrl),
                MemberGroup = entry.Segments[2]
            });
        }
    }

    private static void BuildTypeScriptItems(List<ApiReferenceItem> items, ApiSitemapEntry entry, HashSet<string> typeScriptContainerIds, string sitemapUrl)
    {
        if (entry.Segments.Length == 1)
        {
            items.Add(new ApiReferenceItem
            {
                Id = $"{ApiReferenceLanguages.TypeScript}/{entry.Segments[0]}",
                Name = entry.Segments[0],
                Language = ApiReferenceLanguages.TypeScript,
                Kind = ApiReferenceKinds.Module,
                ParentId = RootTypeScriptScope,
                PageUrl = ApiDocsSourceConfiguration.RebasePageUrl(entry.Url, sitemapUrl)
            });
            return;
        }

        if (entry.Segments.Length == 2)
        {
            var id = $"{ApiReferenceLanguages.TypeScript}/{entry.Segments[0]}/{entry.Segments[1]}";
            items.Add(new ApiReferenceItem
            {
                Id = id,
                Name = entry.Segments[1],
                Language = ApiReferenceLanguages.TypeScript,
                Kind = typeScriptContainerIds.Contains(id) ? ApiReferenceKinds.Symbol : ApiReferenceKinds.Member,
                ParentId = $"{ApiReferenceLanguages.TypeScript}/{entry.Segments[0]}",
                PageUrl = ApiDocsSourceConfiguration.RebasePageUrl(entry.Url, sitemapUrl)
            });
            return;
        }

        if (entry.Segments.Length == 3)
        {
            items.Add(new ApiReferenceItem
            {
                Id = $"{ApiReferenceLanguages.TypeScript}/{entry.Segments[0]}/{entry.Segments[1]}/{entry.Segments[2]}",
                Name = entry.Segments[2],
                Language = ApiReferenceLanguages.TypeScript,
                Kind = ApiReferenceKinds.Member,
                ParentId = $"{ApiReferenceLanguages.TypeScript}/{entry.Segments[0]}/{entry.Segments[1]}",
                PageUrl = ApiDocsSourceConfiguration.RebasePageUrl(entry.Url, sitemapUrl)
            });
        }
    }

    private static List<ApiReferenceItem> Deduplicate(List<ApiReferenceItem> items)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduplicated = new List<ApiReferenceItem>(items.Count);

        foreach (var item in items)
        {
            if (seen.Add(item.Id))
            {
                deduplicated.Add(item);
            }
        }

        return deduplicated;
    }

    private static int GetKindSortOrder(string kind) => kind switch
    {
        ApiReferenceKinds.Package => 0,
        ApiReferenceKinds.Module => 0,
        ApiReferenceKinds.Type => 1,
        ApiReferenceKinds.Symbol => 2,
        ApiReferenceKinds.MemberGroup => 3,
        ApiReferenceKinds.Member => 3,
        _ => 4
    };

    private static string NormalizeId(string value) => value.Trim().Trim('/');

    private static string? NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        var normalized = language.Trim().ToLowerInvariant();
        return ApiReferenceLanguages.IsSupported(normalized)
            ? normalized
            : null;
    }

    private static float ScoreItem(IndexedApiReferenceItem item, string[] queryTokens, string normalizedQuery)
    {
        var score = 0.0f;
        if (item.IdLower == normalizedQuery)
        {
            score += ExactIdMatchBonus;
        }

        if (item.NameLower == normalizedQuery)
        {
            score += ExactNameMatchBonus;
        }

        score += ScoreField(item.IdLower, queryTokens) * IdWeight;
        score += ScoreField(item.NameLower, queryTokens) * NameWeight;

        if (item.SummaryLower is not null)
        {
            score += ScoreField(item.SummaryLower, queryTokens) * SummaryWeight;
        }

        if (item.ParentIdLower is not null)
        {
            score += ScoreField(item.ParentIdLower, queryTokens) * ParentWeight;
        }

        if (item.MemberGroupLower is not null)
        {
            score += ScoreField(item.MemberGroupLower, queryTokens) * MemberGroupWeight;
        }

        foreach (var token in queryTokens)
        {
            if (item.PathSegments.Contains(token, StringComparer.Ordinal))
            {
                score += PathSegmentMatchBonus;
            }
        }

        if (TryGetBroadQueryToken(normalizedQuery, queryTokens, out var broadQueryToken))
        {
            var prefixBonus = GetPrefixMatchBonus(item, broadQueryToken);
            if (prefixBonus > 0)
            {
                score += prefixBonus;
                score += GetBroadQueryKindBonus(item.Source.Kind);
            }
        }

        return score;
    }

    private static bool TryGetBroadQueryToken(string normalizedQuery, string[] queryTokens, [NotNullWhen(true)] out string? broadQueryToken)
    {
        broadQueryToken = null;

        if (queryTokens.Length is not 1)
        {
            return false;
        }

        var token = queryTokens[0];
        if (token.Length < 5 ||
            !string.Equals(normalizedQuery, token, StringComparison.Ordinal) ||
            normalizedQuery.IndexOfAny(['/', '#', '.', '(', ')']) >= 0)
        {
            return false;
        }

        broadQueryToken = token;
        return true;
    }

    private static float GetPrefixMatchBonus(IndexedApiReferenceItem item, string queryToken)
    {
        var score = 0.0f;
        if (item.NameLower.StartsWith(queryToken, StringComparison.Ordinal))
        {
            score += NamePrefixMatchBonus;
            score += GetPrefixTightnessBonus(item.NameLower.Length - queryToken.Length);
        }

        var bestPathSegmentLength = item.PathSegments
            .Where(segment => segment.StartsWith(queryToken, StringComparison.Ordinal))
            .Select(static segment => segment.Length)
            .DefaultIfEmpty(int.MaxValue)
            .Min();
        if (bestPathSegmentLength != int.MaxValue)
        {
            score += PathPrefixMatchBonus;
            score += GetPrefixTightnessBonus(bestPathSegmentLength - queryToken.Length);
        }

        return score;
    }

    private static float GetPrefixTightnessBonus(int extraLength)
        => extraLength switch
        {
            < 0 => 0.0f,
            >= PrefixTightnessMaxBonus => 0.0f,
            _ => PrefixTightnessMaxBonus - extraLength
        };

    private static float GetBroadQueryKindBonus(string kind) => kind switch
    {
        ApiReferenceKinds.Type => 28.0f,
        ApiReferenceKinds.Symbol => 28.0f,
        ApiReferenceKinds.Package => 16.0f,
        ApiReferenceKinds.Module => 16.0f,
        ApiReferenceKinds.MemberGroup => 8.0f,
        _ => 0.0f
    };

    private static string[] Tokenize(string text)
        => LexicalScoring.Tokenize(text, TokenSplitRegex(), MinTokenLength);

    private static float ScoreField(string lowerText, string[] queryTokens)
        => LexicalScoring.ScoreField(
            lowerText,
            queryTokens,
            wordCharacterMode: LexicalWordCharacterMode.IdentifierWithHyphen);

    [GeneratedRegex(@"[^\p{L}\p{N}\.\-_/]+")]
    private static partial Regex TokenSplitRegex();

    private sealed class IndexedApiReferenceItem(ApiReferenceItem source)
    {
        public ApiReferenceItem Source { get; } = source;

        public string IdLower { get; } = source.Id.ToLowerInvariant();

        public string NameLower { get; } = source.Name.ToLowerInvariant();

        public string? SummaryLower { get; } = source.Summary?.ToLowerInvariant();

        public string? ParentIdLower { get; } = source.ParentId?.ToLowerInvariant();

        public string? MemberGroupLower { get; } = source.MemberGroup?.ToLowerInvariant();

        public string[] PathSegments { get; } = [.. source.Id.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(static segment => segment.ToLowerInvariant())];
    }
}
