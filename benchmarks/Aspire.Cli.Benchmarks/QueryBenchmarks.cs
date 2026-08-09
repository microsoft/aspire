// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Documentation.Docs;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Perfolizer.Horology;

namespace Aspire.Cli.Benchmarks;

/// <summary>
/// Benchmarks the hot-path that runs on EVERY `aspire docs ...` invocation after the
/// initial index is built: SearchAsync, GetDocumentAsync, ListDocumentsAsync.
/// </summary>
/// <remarks>
/// We construct a real <see cref="DocsIndexService"/> with stub IO, drive
/// EnsureIndexedAsync once in <see cref="Setup"/>, then time the query methods.
/// This is the closest measurement we can take to the user-perceived latency of
/// `aspire docs get` / `aspire docs search` minus CLI process startup, since the
/// CLI hits the on-disk cache (which deserializes into the same IndexedDocument
/// list this service is holding in memory by the time benchmarks run).
/// </remarks>
[MemoryDiagnoser]
[Config(typeof(Config))]
public class QueryBenchmarks
{
    private DocsIndexService _service = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        var path = await CorpusLoader.EnsureCorpusAsync(new RunOptions(null, Refresh: false)).ConfigureAwait(false);
        var content = await File.ReadAllTextAsync(path).ConfigureAwait(false);

        var fetcher = new StubFetcher(content);
        var cache = new StubCache();
        var configuration = new ConfigurationBuilder().Build();

        _service = new DocsIndexService(fetcher, cache, configuration, NullLogger<DocsIndexService>.Instance);
        await _service.EnsureIndexedAsync().ConfigureAwait(false);
    }

    [Benchmark(Description = "Search('redis') - single common token")]
    public async ValueTask<int> Search_SingleToken()
    {
        var results = await _service.SearchAsync("redis").ConfigureAwait(false);
        return results.Count;
    }

    [Benchmark(Description = "Search('service discovery') - two tokens")]
    public async ValueTask<int> Search_TwoTokens()
    {
        var results = await _service.SearchAsync("service discovery").ConfigureAwait(false);
        return results.Count;
    }

    [Benchmark(Description = "Search('configuration') - hits many docs")]
    public async ValueTask<int> Search_HighRecall()
    {
        var results = await _service.SearchAsync("configuration").ConfigureAwait(false);
        return results.Count;
    }

    [Benchmark(Description = "Search('kafka') - rare token (1%)")]
    public async ValueTask<int> Search_RareToken()
    {
        var results = await _service.SearchAsync("kafka").ConfigureAwait(false);
        return results.Count;
    }

    [Benchmark(Description = "Search('aspire dashboard') - common phrase")]
    public async ValueTask<int> Search_CommonPhrase()
    {
        var results = await _service.SearchAsync("aspire dashboard").ConfigureAwait(false);
        return results.Count;
    }

    [Benchmark(Description = "GetDocument('whats-new-in-aspire-133')")]
    public async ValueTask<int> GetDocument()
    {
        var result = await _service.GetDocumentAsync("whats-new-in-aspire-133").ConfigureAwait(false);
        return result?.Content.Length ?? 0;
    }

    [Benchmark(Description = "ListDocuments - all docs")]
    public async ValueTask<int> ListDocuments()
    {
        var items = await _service.ListDocumentsAsync().ConfigureAwait(false);
        return items.Count;
    }

    /// <summary>
    /// Returns the in-memory corpus as if it came from the network.
    /// Returning the exact same string instance every call lets EnsureIndexedAsync hit
    /// the no-change path on the second call without us having to wire up a real cache.
    /// </summary>
    private sealed class StubFetcher(string content) : IDocsFetcher
    {
        public Task<string?> FetchDocsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(content);
    }

    /// <summary>
    /// In-memory cache that never returns a hit on first run (we want to force a parse
    /// path through EnsureIndexedAsync) but still satisfies the interface contract.
    /// </summary>
    private sealed class StubCache : IDocsCache
    {
        public Task<LlmsDocument[]?> GetIndexAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<LlmsDocument[]?>(null);

        public Task SetIndexAsync(LlmsDocument[] documents, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<string?> GetIndexSourceFingerprintAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task SetIndexSourceFingerprintAsync(string fingerprint, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task SetAsync(string key, string content, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<string?> GetETagAsync(string url, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task SetETagAsync(string url, string? etag, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task InvalidateAsync(string key, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class Config : ManualConfig
    {
        public Config()
        {
            AddJob(Job.Default
                .WithToolchain(InProcessNoEmitToolchain.Instance)
                .WithWarmupCount(2)
                .WithIterationCount(5)
                .WithMinIterationTime(TimeInterval.FromMilliseconds(150))
                .WithUnrollFactor(1));

            AddDiagnoser(MemoryDiagnoser.Default);
        }
    }
}
