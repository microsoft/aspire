// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests.Utils;

public class StringUtilsTests
{
    [Theory]
    [InlineData("", "", 0)]
    [InlineData("abc", "", 3)]
    [InlineData("", "abc", 3)]
    [InlineData("abc", "abc", 0)]
    [InlineData("abc", "def", 3)]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("Saturday", "Sunday", 3)]
    [InlineData("redis", "Redis", 1)] // Case-sensitive: one substitution
    [InlineData("postgres", "postgresql", 2)] // Two additions: 'q' and 'l'
    public void GetLevenshteinDistance_CalculatesCorrectDistance(string source, string target, int expectedDistance)
    {
        var distance = StringUtils.GetLevenshteinDistance(source, target);
        Assert.Equal(expectedDistance, distance);
    }

    [Theory]
    [InlineData("redis", "redis", 1.0)] // Exact match
    [InlineData("redis", "Redis", 1.0)] // Case-insensitive exact match
    [InlineData("redis", "REDIS", 1.0)] // Case-insensitive exact match
    [InlineData("redis", "redis-cache", 0.95)] // Starts with
    [InlineData("redis", "aspire-redis-cache", 0.85)] // Contains
    [InlineData("redis", "Aspire.Hosting.Redis", 0.85)] // Contains
    [InlineData("postgres", "postgresql", 0.75)] // Fuzzy match
    [InlineData("postgre", "postgres", 0.5)] // Common typo - lower fuzzy score
    [InlineData("pg", "postgres", 0.15)] // Very different strings return low score
    [InlineData("", "redis", 0.0)] // Empty search term
    [InlineData("redis", "", 0.0)] // Empty target
    public void CalculateFuzzyScore_ReturnsExpectedScores(string searchTerm, string target, double minExpectedScore)
    {
        var score = StringUtils.CalculateFuzzyScore(searchTerm, target);
        Assert.True(score >= minExpectedScore, $"Expected score >= {minExpectedScore} but got {score}");
    }
}
