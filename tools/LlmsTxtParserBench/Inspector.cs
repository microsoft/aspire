// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Documentation.Docs;

namespace LlmsTxtParserBench;

/// <summary>
/// Non-benchmark structural analysis of the parser output. Designed to be cheap so
/// it can be run alongside the BDN suite to compare A/B changes for memory
/// amplification, slug collisions, and other shape-level concerns.
/// </summary>
internal static class Inspector
{
    public static async Task RunAsync(RunOptions options, CancellationToken cancellationToken = default)
    {
        var path = await CorpusLoader.EnsureCorpusAsync(options, cancellationToken).ConfigureAwait(false);
        var raw = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var rawBytes = (long)raw.Length * sizeof(char);

        Console.WriteLine($"corpus path:           {path}");
        Console.WriteLine($"corpus chars:          {raw.Length:N0}");
        Console.WriteLine($"corpus bytes (UTF-16): {rawBytes:N0}");

        var documents = await LlmsTxtParser.ParseAsync(raw, cancellationToken).ConfigureAwait(false);

        var contentChars = documents.Sum(d => (long)d.Content.Length);
        var sectionChars = documents.Sum(d => d.Sections.Sum(s => (long)s.Content.Length));
        var summaryChars = documents.Sum(d => (long)(d.Summary?.Length ?? 0));
        var titleChars = documents.Sum(d => (long)d.Title.Length);
        var totalChars = contentChars + sectionChars + summaryChars + titleChars;

        Console.WriteLine();
        Console.WriteLine("=== shape ===");
        Console.WriteLine($"documents:             {documents.Count:N0}");
        Console.WriteLine($"sections (total):      {documents.Sum(d => d.Sections.Count):N0}");
        Console.WriteLine($"median sections/doc:   {Median(documents.Select(d => d.Sections.Count).ToList())}");
        Console.WriteLine($"max sections in a doc: {documents.Max(d => d.Sections.Count)}");

        Console.WriteLine();
        Console.WriteLine("=== string content (chars) ===");
        Console.WriteLine($"sum(doc.Content):        {contentChars,15:N0}");
        Console.WriteLine($"sum(section.Content):    {sectionChars,15:N0}");
        Console.WriteLine($"sum(doc.Summary):        {summaryChars,15:N0}");
        Console.WriteLine($"sum(doc.Title):          {titleChars,15:N0}");
        Console.WriteLine($"────────────────────────────────────────────");
        Console.WriteLine($"total chars retained:    {totalChars,15:N0}");
        Console.WriteLine($"vs corpus chars:         {(double)totalChars / raw.Length,15:F2}x");

        // JSON cache size estimate (matches index_*.json roughly)
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(
            documents.Select(d => new
            {
                d.Title,
                d.Slug,
                d.Summary,
                d.Content,
                Sections = d.Sections.Select(s => new { s.Heading, s.Level, s.Content }).ToArray(),
            }).ToArray());

        Console.WriteLine();
        Console.WriteLine("=== serialization ===");
        Console.WriteLine($"JSON UTF-8 bytes:      {jsonBytes.Length:N0}");
        Console.WriteLine($"vs corpus UTF-16:      {(double)jsonBytes.Length / rawBytes,15:F2}x");

        Console.WriteLine();
        Console.WriteLine("=== slug uniqueness ===");
        var slugGroups = documents.GroupBy(d => d.Slug).Where(g => g.Count() > 1).ToList();
        Console.WriteLine($"slug collisions:       {slugGroups.Count}");
        foreach (var group in slugGroups)
        {
            Console.WriteLine($"  {group.Key}");
            foreach (var doc in group)
            {
                Console.WriteLine($"    title: {doc.Title}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("=== top 5 largest docs by Content size ===");
        foreach (var doc in documents.OrderByDescending(d => d.Content.Length).Take(5))
        {
            var secBytes = doc.Sections.Sum(s => s.Content.Length);
            Console.WriteLine($"  {doc.Content.Length,8:N0} chars  +  {secBytes,8:N0} section chars  {doc.Slug}");
        }
    }

    private static double Median(List<int> values)
    {
        if (values.Count is 0)
        {
            return 0;
        }

        values.Sort();
        return values.Count % 2 is 1
            ? values[values.Count / 2]
            : (values[(values.Count / 2) - 1] + values[values.Count / 2]) / 2.0;
    }
}
