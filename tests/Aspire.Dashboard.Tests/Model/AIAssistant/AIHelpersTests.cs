// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model.Assistant;
using Aspire.Shared.ConsoleLogs;
using Xunit;

namespace Aspire.Dashboard.Tests.Model.AIAssistant;

public class AIHelpersTests
{
    [Fact]
    public void TryGetSingleResult_NoMatches_ReturnsFalse()
    {
        // Arrange
        var items = new List<int> { 1, 2, 3, 4, 5 };

        // Act
        var result = AIHelpers.TryGetSingleResult(items, x => x > 10, out var value);

        // Assert
        Assert.False(result);
        Assert.Equal(0, value);
    }

    [Fact]
    public void TryGetSingleResult_SingleMatch_ReturnsTrueWithValue()
    {
        // Arrange
        var items = new List<int> { 1, 2, 3, 4, 5 };

        // Act
        var result = AIHelpers.TryGetSingleResult(items, x => x == 3, out var value);

        // Assert
        Assert.True(result);
        Assert.Equal(3, value);
    }

    [Fact]
    public void TryGetSingleResult_MultipleMatches_ReturnsFalse()
    {
        // Arrange
        var items = new List<int> { 1, 2, 3, 4, 5 };

        // Act
        var result = AIHelpers.TryGetSingleResult(items, x => x > 2, out var value);

        // Assert
        Assert.False(result);
        Assert.Equal(0, value);
    }

    [Fact]
    public void TryGetSingleResult_ReferenceType_SingleMatch_ReturnsTrueWithValue()
    {
        // Arrange
        var items = new List<string> { "one", "two", "three" };

        // Act
        var result = AIHelpers.TryGetSingleResult(items, x => x == "two", out var value);

        // Assert
        Assert.True(result);
        Assert.Equal("two", value);
    }

    [Fact]
    public void TryGetSingleResult_ReferenceType_MultipleMatches_ReturnsFalse()
    {
        // Arrange
        var items = new List<string> { "one", "two", "three" };

        // Act
        var result = AIHelpers.TryGetSingleResult(items, x => x.Length == 3, out var value);

        // Assert
        Assert.False(result);
        Assert.Null(value);
    }

    [Fact]
    public void LimitLength_UnderLimit_ReturnFullValue()
    {
        var value = SharedAIHelpers.LimitLength("How now brown cow?");
        Assert.Equal("How now brown cow?", value);
    }

    [Fact]
    public void LimitLength_OverLimit_ReturnTrimmedValue()
    {
        var value = SharedAIHelpers.LimitLength(new string('!', 10_000));
        Assert.Equal($"{new string('!', SharedAIHelpers.MaximumStringLength)}...[TRUNCATED]", value);
    }

    [Fact]
    public void GetLimitFromEndWithSummary_UnderLimits_ReturnAll()
    {
        // Arrange
        var values = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            values.Add(new string((char)('a' + i), 16));
        }

        // Act
        var (items, message) = SharedAIHelpers.GetLimitFromEndWithSummary(values, totalValues: values.Count, limit: 20, "test item", "test items", s => s, s => ((string)s).Length);

        // Assert
        Assert.Equal(10, items.Count);
        Assert.Equal("Returned 10 test items.", message);
    }

    [Fact]
    public void GetLimitFromEndWithSummary_UnderTotal_ReturnPassedIn()
    {
        // Arrange
        var values = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            values.Add(new string((char)('a' + i), 16));
        }

        // Act
        var (items, message) = SharedAIHelpers.GetLimitFromEndWithSummary(values, totalValues: 100, limit: 20, "test item", "test items", s => s, s => ((string)s).Length);

        // Assert
        Assert.Equal(10, items.Count);
        Assert.Equal("Returned latest 10 test items. Earlier 90 test items not returned because of size limits.", message);
    }

    [Fact]
    public void GetLimitFromEndWithSummary_ExceedCountLimit_ReturnMostRecentItems()
    {
        // Arrange
        var values = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            values.Add(new string((char)('a' + i), 2));
        }

        // Act
        var (items, message) = SharedAIHelpers.GetLimitFromEndWithSummary(values, totalValues: 100, limit: 5, "test item", "test items", s => s, s => ((string)s).Length);

        // Assert
        Assert.Collection(items,
            s => Assert.Equal("ff", s),
            s => Assert.Equal("gg", s),
            s => Assert.Equal("hh", s),
            s => Assert.Equal("ii", s),
            s => Assert.Equal("jj", s));
        Assert.Equal("Returned latest 5 test items. Earlier 95 test items not returned because of size limits.", message);
    }

    [Fact]
    public void GetLimitFromEndWithSummary_ExceedTokenLimit_ReturnMostRecentItems()
    {
        const int textLength = 1024 * 2;

        // Arrange
        var values = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            values.Add(new string((char)('a' + i), textLength));
        }

        // Act
        var (items, message) = SharedAIHelpers.GetLimitFromEndWithSummary(values, limit: 10, "test item", "test items", s => s, s => ((string)s).Length);

        // Assert
        Assert.Collection(items,
            s => Assert.Equal(new string('g', textLength), s),
            s => Assert.Equal(new string('h', textLength), s),
            s => Assert.Equal(new string('i', textLength), s),
            s => Assert.Equal(new string('j', textLength), s));
        Assert.Equal("Returned latest 4 test items. Earlier 6 test items not returned because of size limits.", message);
    }

    [Fact]
    public void GetDashboardUrl_PublicUrl()
    {
        // Arrange
        var options = new DashboardOptions();
        options.Frontend.EndpointUrls = "http://localhost:5000;https://localhost:1234";
        options.Frontend.PublicUrl = "https://localhost:1234";
        Assert.True(options.Frontend.TryParseOptions(out _));

        // Act
        var url = AIHelpers.GetDashboardUrl(options);

        // Assert
        Assert.Equal("https://localhost:1234", url);
    }

    [Fact]
    public void GetDashboardUrl_HttpsAndHttpEndpointUrls()
    {
        // Arrange
        var options = new DashboardOptions();
        options.Frontend.EndpointUrls = "http://localhost:5000;https://localhost:1234";
        Assert.True(options.Frontend.TryParseOptions(out _));

        // Act
        var url = AIHelpers.GetDashboardUrl(options);

        // Assert
        Assert.Equal("https://localhost:1234", url);
    }

    [Fact]
    public void GetDashboardUrl_HttpEndpointUrl()
    {
        // Arrange
        var options = new DashboardOptions();
        options.Frontend.EndpointUrls = "http://localhost:5000;";
        Assert.True(options.Frontend.TryParseOptions(out _));

        // Act
        var url = AIHelpers.GetDashboardUrl(options);

        // Assert
        Assert.Equal("http://localhost:5000", url);
    }
}
