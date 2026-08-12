// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Tests.Utils;

public class SearchTextParserTests
{
    #region ParseSearch - Basic

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseSearch_EmptyOrNull_ReturnsEmpty(string? search)
    {
        var filter = SearchTextParser.ParseSearch(search);
        Assert.True(filter.IsEmpty);
    }

    [Fact]
    public void ParseSearch_SingleWord_ReturnsTextFragment()
    {
        var filter = SearchTextParser.ParseSearch("hello");
        Assert.Single(filter.TextFragments, "hello");
        Assert.Empty(filter.Qualifiers);
        Assert.Empty(filter.NegatedQualifiers);
    }

    [Fact]
    public void ParseSearch_MultipleWords_SplitsIntoFragments()
    {
        var filter = SearchTextParser.ParseSearch("hello world foo");
        Assert.Equal(["hello", "world", "foo"], filter.TextFragments);
    }

    [Fact]
    public void ParseSearch_QuotedText_TreatedAsSingleFragment()
    {
        var filter = SearchTextParser.ParseSearch("\"hello world\"");
        Assert.Single(filter.TextFragments, "hello world");
    }

    [Fact]
    public void ParseSearch_MixedQuotedAndUnquoted()
    {
        var filter = SearchTextParser.ParseSearch("error \"connection failed\" timeout");
        Assert.Equal(["error", "connection failed", "timeout"], filter.TextFragments);
    }

    #endregion

    #region ParseSearch - Qualifiers

    [Fact]
    public void ParseSearch_SimpleQualifier()
    {
        var filter = SearchTextParser.ParseSearch("severity:error");
        Assert.Empty(filter.TextFragments);
        Assert.Single(filter.Qualifiers);
        Assert.Equal("severity", filter.Qualifiers[0].Key);
        Assert.Equal("error", filter.Qualifiers[0].Value);
        Assert.Equal(ComparisonOperator.Contains, filter.Qualifiers[0].Operator);
    }

    [Fact]
    public void ParseSearch_QualifierKeyIsLowercased()
    {
        var filter = SearchTextParser.ParseSearch("Severity:Error");
        Assert.Equal("severity", filter.Qualifiers[0].Key);
        Assert.Equal("Error", filter.Qualifiers[0].Value);
    }

    [Fact]
    public void ParseSearch_QualifierWithQuotedValue()
    {
        var filter = SearchTextParser.ParseSearch("message:\"connection timed out\"");
        Assert.Single(filter.Qualifiers);
        Assert.Equal("message", filter.Qualifiers[0].Key);
        Assert.Equal("connection timed out", filter.Qualifiers[0].Value);
    }

    [Fact]
    public void ParseSearch_DottedKey()
    {
        var filter = SearchTextParser.ParseSearch("http.method:GET");
        Assert.Single(filter.Qualifiers);
        Assert.Equal("http.method", filter.Qualifiers[0].Key);
        Assert.Equal("GET", filter.Qualifiers[0].Value);
    }

    [Fact]
    public void ParseSearch_QuotedQualifierSyntax_TreatedAsTextFragment()
    {
        var filter = SearchTextParser.ParseSearch("\"http.method:GET\"");
        Assert.Single(filter.TextFragments, "http.method:GET");
        Assert.Empty(filter.Qualifiers);
    }

    [Fact]
    public void ParseSearch_MultipleQualifiers()
    {
        var filter = SearchTextParser.ParseSearch("severity:error resource:api");
        Assert.Equal(2, filter.Qualifiers.Length);
        Assert.Equal("severity", filter.Qualifiers[0].Key);
        Assert.Equal("error", filter.Qualifiers[0].Value);
        Assert.Equal("resource", filter.Qualifiers[1].Key);
        Assert.Equal("api", filter.Qualifiers[1].Value);
    }

    [Fact]
    public void ParseSearch_QualifierWithEmptyValue_TreatedAsText()
    {
        var filter = SearchTextParser.ParseSearch("key:");
        Assert.Single(filter.TextFragments, "key:");
        Assert.Empty(filter.Qualifiers);
    }

    [Fact]
    public void ParseSearch_QualifierWithEmptyQuotedValue_TreatedAsText()
    {
        var filter = SearchTextParser.ParseSearch("key:\"\"");
        Assert.Single(filter.TextFragments, "key:\"\"");
        Assert.Empty(filter.Qualifiers);
    }

    #endregion

    #region ParseSearch - Negation

    [Fact]
    public void ParseSearch_NegatedQualifier()
    {
        var filter = SearchTextParser.ParseSearch("-severity:debug");
        Assert.Empty(filter.TextFragments);
        Assert.Empty(filter.Qualifiers);
        Assert.Single(filter.NegatedQualifiers);
        Assert.Equal("severity", filter.NegatedQualifiers[0].Key);
        Assert.Equal("debug", filter.NegatedQualifiers[0].Value);
    }

    [Fact]
    public void ParseSearch_NegatedQualifierWithQuotedValue()
    {
        var filter = SearchTextParser.ParseSearch("-message:\"not important\"");
        Assert.Single(filter.NegatedQualifiers);
        Assert.Equal("message", filter.NegatedQualifiers[0].Key);
        Assert.Equal("not important", filter.NegatedQualifiers[0].Value);
    }

    [Fact]
    public void ParseSearch_MixedPositiveAndNegated()
    {
        var filter = SearchTextParser.ParseSearch("severity:error -resource:debug hello");
        Assert.Single(filter.TextFragments, "hello");
        Assert.Single(filter.Qualifiers);
        Assert.Equal("severity", filter.Qualifiers[0].Key);
        Assert.Single(filter.NegatedQualifiers);
        Assert.Equal("resource", filter.NegatedQualifiers[0].Key);
    }

    #endregion

    #region ParseSearch - Attribute Prefix (@)

    [Fact]
    public void ParseSearch_AttributeQualifier()
    {
        var filter = SearchTextParser.ParseSearch("@http.method:GET");
        Assert.Single(filter.Qualifiers);
        Assert.Equal("http.method", filter.Qualifiers[0].Key);
        Assert.Equal("GET", filter.Qualifiers[0].Value);
        Assert.True(filter.Qualifiers[0].IsAttribute);
    }

    [Fact]
    public void ParseSearch_NegatedAttributeQualifier()
    {
        var filter = SearchTextParser.ParseSearch("-@db.system:redis");
        Assert.Single(filter.NegatedQualifiers);
        Assert.Equal("db.system", filter.NegatedQualifiers[0].Key);
        Assert.Equal("redis", filter.NegatedQualifiers[0].Value);
        Assert.True(filter.NegatedQualifiers[0].IsAttribute);
    }

    [Fact]
    public void ParseSearch_BareQualifierIsNotAttribute()
    {
        var filter = SearchTextParser.ParseSearch("status:error");
        Assert.Single(filter.Qualifiers);
        Assert.False(filter.Qualifiers[0].IsAttribute);
    }

    [Fact]
    public void ParseSearch_AttributeWithQuotedValue()
    {
        var filter = SearchTextParser.ParseSearch("@user.name:\"John Doe\"");
        Assert.Single(filter.Qualifiers);
        Assert.Equal("user.name", filter.Qualifiers[0].Key);
        Assert.Equal("John Doe", filter.Qualifiers[0].Value);
        Assert.True(filter.Qualifiers[0].IsAttribute);
    }

    [Fact]
    public void ParseSearch_AttributeWithComparisonOperator()
    {
        var filter = SearchTextParser.ParseSearch("@response.time:>500");
        Assert.Single(filter.Qualifiers);
        Assert.Equal("response.time", filter.Qualifiers[0].Key);
        Assert.Equal("500", filter.Qualifiers[0].Value);
        Assert.Equal(ComparisonOperator.GreaterThan, filter.Qualifiers[0].Operator);
        Assert.True(filter.Qualifiers[0].IsAttribute);
    }

    #endregion

    #region ParseSearch - Comparison Operators

    [Fact]
    public void ParseSearch_GreaterThan()
    {
        var filter = SearchTextParser.ParseSearch("duration:>100");
        Assert.Single(filter.Qualifiers);
        Assert.Equal("duration", filter.Qualifiers[0].Key);
        Assert.Equal("100", filter.Qualifiers[0].Value);
        Assert.Equal(ComparisonOperator.GreaterThan, filter.Qualifiers[0].Operator);
    }

    [Fact]
    public void ParseSearch_GreaterThanOrEqual()
    {
        var filter = SearchTextParser.ParseSearch("duration:>=500");
        Assert.Single(filter.Qualifiers);
        Assert.Equal("duration", filter.Qualifiers[0].Key);
        Assert.Equal("500", filter.Qualifiers[0].Value);
        Assert.Equal(ComparisonOperator.GreaterThanOrEqual, filter.Qualifiers[0].Operator);
    }

    [Fact]
    public void ParseSearch_LessThan()
    {
        var filter = SearchTextParser.ParseSearch("duration:<50");
        Assert.Single(filter.Qualifiers);
        Assert.Equal("duration", filter.Qualifiers[0].Key);
        Assert.Equal("50", filter.Qualifiers[0].Value);
        Assert.Equal(ComparisonOperator.LessThan, filter.Qualifiers[0].Operator);
    }

    [Fact]
    public void ParseSearch_LessThanOrEqual()
    {
        var filter = SearchTextParser.ParseSearch("duration:<=200");
        Assert.Single(filter.Qualifiers);
        Assert.Equal("duration", filter.Qualifiers[0].Key);
        Assert.Equal("200", filter.Qualifiers[0].Value);
        Assert.Equal(ComparisonOperator.LessThanOrEqual, filter.Qualifiers[0].Operator);
    }

    [Fact]
    public void ParseSearch_NegatedComparisonOperator()
    {
        var filter = SearchTextParser.ParseSearch("-duration:>1000");
        Assert.Single(filter.NegatedQualifiers);
        Assert.Equal("duration", filter.NegatedQualifiers[0].Key);
        Assert.Equal("1000", filter.NegatedQualifiers[0].Value);
        Assert.Equal(ComparisonOperator.GreaterThan, filter.NegatedQualifiers[0].Operator);
    }

    #endregion

    #region ParseSearch - Complex / Mixed

    [Fact]
    public void ParseSearch_ComplexMixed()
    {
        var filter = SearchTextParser.ParseSearch("error severity:warning -resource:test duration:>100 \"connection reset\"");
        Assert.Equal(["error", "connection reset"], filter.TextFragments);
        Assert.Equal(2, filter.Qualifiers.Length);
        Assert.Equal("severity", filter.Qualifiers[0].Key);
        Assert.Equal("warning", filter.Qualifiers[0].Value);
        Assert.Equal("duration", filter.Qualifiers[1].Key);
        Assert.Equal("100", filter.Qualifiers[1].Value);
        Assert.Equal(ComparisonOperator.GreaterThan, filter.Qualifiers[1].Operator);
        Assert.Single(filter.NegatedQualifiers);
        Assert.Equal("resource", filter.NegatedQualifiers[0].Key);
    }

    #endregion

    #region ParseFragments - Backward Compatibility

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseFragments_EmptyOrNull_ReturnsEmpty(string? search)
    {
        var result = SearchTextParser.ParseFragments(search);
        Assert.Empty(result);
    }

    [Fact]
    public void ParseFragments_TextOnly()
    {
        var result = SearchTextParser.ParseFragments("hello world");
        Assert.Equal(["hello", "world"], result);
    }

    [Fact]
    public void ParseFragments_IncludesQualifierValues()
    {
        var result = SearchTextParser.ParseFragments("severity:error hello");
        Assert.Contains("hello", result);
        Assert.Contains("error", result);
    }

    [Fact]
    public void ParseFragments_IncludesNegatedQualifierValues()
    {
        var result = SearchTextParser.ParseFragments("-severity:debug");
        Assert.Contains("debug", result);
    }

    #endregion

    #region MatchesAllFragments

    [Fact]
    public void MatchesAllFragments_EmptyFragments_ReturnsTrue()
    {
        Assert.True(SearchTextParser.MatchesAllFragments([], "anything", static (state, fragment) =>
            state.Contains(fragment, StringComparisons.FullTextSearch)));
    }

    [Fact]
    public void MatchesAllFragments_SingleMatch()
    {
        Assert.True(SearchTextParser.MatchesAllFragments(["hello"], "hello world", static (state, fragment) =>
            state.Contains(fragment, StringComparisons.FullTextSearch)));
    }

    [Fact]
    public void MatchesAllFragments_CaseInsensitive()
    {
        Assert.True(SearchTextParser.MatchesAllFragments(["HELLO"], "hello world", static (state, fragment) =>
            state.Contains(fragment, StringComparisons.FullTextSearch)));
    }

    [Fact]
    public void MatchesAllFragments_AllMustMatch()
    {
        Assert.False(SearchTextParser.MatchesAllFragments(["hello", "missing"], "hello world", static (state, fragment) =>
            state.Contains(fragment, StringComparisons.FullTextSearch)));
    }

    [Fact]
    public void MatchesAllFragments_DifferentCandidates()
    {
        Assert.True(SearchTextParser.MatchesAllFragments(["hello", "world"], ("hello", "world"), static (state, fragment) =>
            state.Item1.Contains(fragment, StringComparisons.FullTextSearch) ||
            state.Item2.Contains(fragment, StringComparisons.FullTextSearch)));
    }

    #endregion
}
