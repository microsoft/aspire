// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Documentation.Docs;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;

namespace Aspire.Cli.Benchmarks;

[MemoryDiagnoser]
[ThreadingDiagnoser]
[Config(typeof(Config))]
public class ParserBenchmarks
{
    private string _corpus = "";

    [GlobalSetup]
    public async Task Setup()
    {
        var path = await CorpusLoader.EnsureCorpusAsync(new RunOptions(null, Refresh: false)).ConfigureAwait(false);
        _corpus = await File.ReadAllTextAsync(path).ConfigureAwait(false);
    }

    /// <summary>
    /// End-to-end parse of the full corpus. This is the headline metric:
    /// it covers boundary scanning, code-fence detection, per-document slicing,
    /// section parsing, summary extraction, slug generation, and string
    /// materialization for Content + Sections[*].Content.
    /// </summary>
    [Benchmark(Description = "ParseAsync(llms-full.txt)")]
    public async Task<int> ParseAsync_FullCorpus()
    {
        var docs = await LlmsTxtParser.ParseAsync(_corpus).ConfigureAwait(false);
        return docs.Count;
    }

    private sealed class Config : ManualConfig
    {
        public Config()
        {
            AddJob(Job.Default
                .WithToolchain(InProcessNoEmitToolchain.Instance)
                .WithWarmupCount(2)
                .WithIterationCount(5)
                .WithInvocationCount(16)
                .WithUnrollFactor(1));

            AddDiagnoser(MemoryDiagnoser.Default);
        }
    }
}
