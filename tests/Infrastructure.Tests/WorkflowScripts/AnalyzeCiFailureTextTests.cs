// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Xunit;

namespace Infrastructure.Tests;

public sealed class AnalyzeCiFailureTextTests
{
    [Theory]
    [InlineData(500, 500, true)]
    [InlineData(501, 500, false)]
    [InlineData(238, 238, true)]
    [InlineData(239, 238, false)]
    public void TextLimitsCountUnicodeCodePoints(int count, int limit, bool expected)
    {
        var text = string.Concat(Enumerable.Repeat("\U0001f600", count));

        Assert.Equal(expected, AnalyzeCiFailure.Json.Safe(JsonValue.Create(text), limit));
    }

    [Theory]
    [InlineData("\u0001")]
    [InlineData("\u200b")]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    [InlineData("\ufe00")]
    [InlineData("\U000e0041")]
    [InlineData("\U000e0100")]
    [InlineData("\U000e01ef")]
    public void RawTextRuleRejectsInvisibleCodePoints(string character)
    {
        // Entry sanitization strips these characters. Exercise the independent rule before
        // sanitization to catch supplementary-plane format/variation-selector regressions.
        var text = JsonValue.Create("before" + character + "after");

        Assert.False(AnalyzeCiFailure.Json.Safe(text, 500));
        Assert.False(AnalyzeCiFailure.Json.Safe(text, 500, multiline: true));
    }

    [Theory]
    [InlineData("\t")]
    [InlineData("\n")]
    public void OnlyMultilineFieldsAllowTabAndNewline(string character)
    {
        var text = JsonValue.Create("before" + character + "after");

        Assert.False(AnalyzeCiFailure.Json.Safe(text, 500));
        Assert.True(AnalyzeCiFailure.Json.Safe(text, 500, multiline: true));
    }
}
