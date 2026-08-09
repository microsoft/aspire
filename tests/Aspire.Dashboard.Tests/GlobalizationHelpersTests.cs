// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Utils;
using Humanizer;
using Xunit;

namespace Aspire.Dashboard.Tests;

public class GlobalizationHelpersTests
{
    [Fact]
    public void ExpandedLocalizedCultures_IncludesPopularCultures()
    {
        // Act
        var supportedCultures = GlobalizationHelpers.ExpandedLocalizedCultures
            .SelectMany(kvp => kvp.Value)
            .Select(c => c.Name)
            .ToList();

        // Assert
        foreach (var localizedCulture in GlobalizationHelpers.OrderedLocalizedCultures)
        {
            Assert.Contains(localizedCulture.Name, supportedCultures);
        }

        // A few cultures we expect to be available
        Assert.Contains("en-GB", supportedCultures);
        Assert.Contains("fr-CA", supportedCultures);
        Assert.Contains("zh-CN", supportedCultures);
    }

    [Fact]
    public void OrderedLocalizedCultures_HasUniqueDisplayNames()
    {
        var displayNames = GlobalizationHelpers.OrderedLocalizedCultures
            .Select(c => c.NativeName.Humanize())
            .ToList();

        Assert.Equal(displayNames.Count, displayNames.Distinct().Count());
    }

    [Theory]
    [InlineData("en", true, "en")]
    [InlineData("en-US", true, "en")]
    [InlineData("fr", true, "fr")]
    [InlineData("zh-Hans", true, "zh-Hans")]
    [InlineData("zh-Hant", true, "zh-Hant")]
    [InlineData("zh-CN", true, "zh-Hans")]
    [InlineData("es", false, null)]
    [InlineData("aa-bb", false, null)]
    public void TryGetKnownParentCulture_VariousCultures_ReturnsExpectedResult(string cultureName, bool expectedResult, string? expectedMatchedCultureName)
    {
        // Arrange
        var cultureOptions = new List<CultureInfo>
        {
            new("en"),
            new("fr"),
            new("zh-Hans"),
            new("zh-Hant")
        };
        var culture = new CultureInfo(cultureName);

        // Act
        var result = GlobalizationHelpers.TryGetKnownParentCulture(cultureOptions, culture, out var matchedCulture);

        // Assert
        Assert.Equal(expectedResult, result);
        if (expectedMatchedCultureName is null)
        {
            Assert.Null(matchedCulture);
        }
        else
        {
            Assert.Equal(new CultureInfo(expectedMatchedCultureName), matchedCulture);
        }
    }

    [Theory]
    [InlineData("en", "en-US", "en-US")]
    [InlineData("en", "en-XX", "en")]
    [InlineData("de", "en-US", null)]
    [InlineData("zh-Hans", "en-US,en;q=0.9,zh-CN;q=0.8,zh;q=0.7", "zh-CN")]
    [InlineData("zh-Hans", "zh-CN", "zh-CN")]
    [InlineData("zh-Hans", "zh-CN,zh;q=0.9", "zh-CN")]
    public async Task ResolveSetCultureToAcceptedCultureAsync_MatchRequestToResult(string requestedLanguage, string acceptLanguage, string? result)
    {
        // Arrange
        var englishCultures = GlobalizationHelpers.ExpandedLocalizedCultures[requestedLanguage];

        // Act
        var requestCulture = await GlobalizationHelpers.ResolveSetCultureToAcceptedCultureAsync(acceptLanguage, englishCultures);

        // Assert
        if (result != null)
        {
            Assert.NotNull(requestCulture);
            Assert.Equal(result, requestCulture.Culture.Name);
            Assert.Equal(result, requestCulture.UICulture.Name);
        }
        else
        {
            Assert.Null(requestCulture);
        }
    }
}
