// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using AnalyzeCiFailure;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>Checks exact positive GitHub identifier decoding without coercion or precision loss.</summary>
public sealed class AnalyzeCiFailureGitHubJsonTests
{
    [Theory]
    [InlineData("1", 1L)]
    [InlineData("9007199254740993", 9007199254740993L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    public void UpstreamIdsPreserveExactPositiveIntegers(string json, long expected)
    {
        var node = JsonNode.Parse(json);
        using var document = JsonDocument.Parse(json);

        Assert.True(GitHubJson.TryReadId(node, out var id));
        Assert.Equal(expected, id);
        Assert.Equal(expected, GitHubJson.ReadRequiredId(node));
        Assert.Equal(expected, GitHubJson.ReadRequiredId(document.RootElement));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("false")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("\"123\"")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.0")]
    [InlineData("1e1")]
    [InlineData("1.5")]
    [InlineData("9223372036854775808")]
    public void UpstreamIdsRejectCoercionAndOverflow(string json)
    {
        var node = JsonNode.Parse(json);
        using var document = JsonDocument.Parse(json);

        Assert.False(GitHubJson.TryReadId(node, out _));
        Assert.Throws<ValidationException>(() => GitHubJson.ReadRequiredId(node));
        Assert.Throws<ValidationException>(() => GitHubJson.ReadRequiredId(document.RootElement));
    }
}
