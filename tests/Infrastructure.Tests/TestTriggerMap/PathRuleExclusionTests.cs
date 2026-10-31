// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.SelectTests;
using Xunit;

namespace Infrastructure.Tests.TestTriggerMap;

public sealed class PathRuleExclusionTests(ITestOutputHelper output) : IDisposable
{
    private readonly TemporaryWorkspace _workspace = TemporaryWorkspace.Create(output);

    [Theory]
    [InlineData("analyzer", false, new[] { "Analyzer.Tests" })]
    [InlineData("general", false, new[] { "General.Tests" })]
    [InlineData("both", false, new[] { "Analyzer.Tests", "General.Tests" })]
    [InlineData("analyzer", true, new[] { "Analyzer.Tests", "General.Tests" })]
    [InlineData("case-sensitive", false, new[] { "General.Tests" })]
    public void ExclusionsArePerRuleAndDoNotRemoveOtherFilesOrLayer1Consumers(string files, bool affectedGeneral, string[] expected)
    {
        var path = Path.Combine(_workspace.Path, "map.yml");
        File.WriteAllText(
            path,
            """
            version: 1
            path_rules:
              - paths: [inputs/**]
                exclude_paths: [inputs/analyzer/**]
                targets: [test:General.Tests]
              - paths: [inputs/analyzer/**]
                targets: [test:Analyzer.Tests]
            """);
        var selector = Selector(path);
        string[] changed = files switch
        {
            "analyzer" => ["inputs/analyzer/file.json"],
            "general" => ["inputs/general/file.json"],
            "case-sensitive" => ["inputs/Analyzer/file.json"],
            _ => ["inputs/analyzer/file.json", "inputs/general/file.json"],
        };
        var result = selector.Select(changed, affectedGeneral ? ["General.Tests"] : [], new SelectorOptions());
        Assert.False(result.SelectsAll);
        Assert.Equal(expected.Order(StringComparer.Ordinal), result.TestProjects.Order(StringComparer.Ordinal));
        Assert.Empty(result.UnmatchedFiles);
    }

    [Fact]
    public void ExcludedInputsWithoutAnotherOwnerStillUseTheRunAllFallback()
    {
        var path = Path.Combine(_workspace.Path, "map.yml");
        File.WriteAllText(
            path,
            """
            version: 1
            path_rules:
              - paths: [inputs/**]
                exclude_paths: [inputs/analyzer/**]
                targets: [test:General.Tests]
            """);
        var result = Selector(path).Select(["inputs/analyzer/file.json"], [], new SelectorOptions());
        Assert.True(result.SelectsAll);
        Assert.Equal(["inputs/analyzer/file.json"], result.UnmatchedFiles);
        Assert.Equal(["Analyzer.Tests", "General.Tests"], result.TestProjects.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("['']")]
    [InlineData("['   ']")]
    public void InvalidExclusionCollectionsFailWithTheConfigurationFieldName(string value)
    {
        var path = Path.Combine(_workspace.Path, "map.yml");
        File.WriteAllText(
            path,
            $$"""
            version: 1
            path_rules:
              - paths: [inputs/**]
                exclude_paths: {{value}}
                targets: [test:General.Tests]
            """);
        var error = Assert.Throws<InvalidDataException>(() => Selector(path).Select(["inputs/file"], [], new SelectorOptions()));
        Assert.Contains("exclude_paths", error.Message, StringComparison.Ordinal);
    }

    private static TestSelector Selector(string path)
        => new(path, ["Analyzer.Tests", "General.Tests"], [], new HashSet<string>(["Analyzer.Tests", "General.Tests"], StringComparer.Ordinal));

    public void Dispose() => _workspace.Dispose();
}
