// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// The original code was borrowed from https://github.com/xunit/samples.xunit/blob/main/v3/UseCultureExample/UseCultureAttributeTests.cs
// Licensed under http://www.apache.org/licenses/LICENSE-2.0.

using System.Globalization;

namespace Aspire.TestUtilities.Tests;

[Trait("Partition", "4")]
public class UseCultureAttributeTests
{
    [Fact]
    public void GuardClauses()
    {
        Assert.Throws<ArgumentNullException>(() => new UseCultureAttribute(null!).Culture);
        Assert.Throws<ArgumentNullException>(() => new UseCultureAttribute("en-US", null!).UICulture);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("da-DK")]
    [InlineData("de-DE")]
    public void CreatingWithCultureSetsCorrectCultureProperty(string culture)
    {
        var attr = new UseCultureAttribute(culture);

        Assert.Equal(culture, attr.Culture.Name);
    }

    [Theory]
    [InlineData("nl-BE")]
    [InlineData("fi-FI")]
    [InlineData("fr-CA")]
    public void CreatingWithCultureAndUICultureSetsCorrectCulturePropery(string culture)
    {
        var attr = new UseCultureAttribute(culture, "fr");

        Assert.Equal(culture, attr.Culture.Name);
    }

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("es-ES")]
    [InlineData("zh-HK")]
    public void CreatingWithCultureSetsSameUICulture(string culture)
    {
        var attr = new UseCultureAttribute(culture);

        Assert.Equal(culture, attr.UICulture.Name);
    }

    [Theory]
    [InlineData("nl-NL")]
    [InlineData("de-AT")]
    [InlineData("en-GB")]
    public void CreatingWithCultureAndUICultureSetsCorrectUICulturePropery(string uiCulture)
    {
        var attr = new UseCultureAttribute("el-GR", uiCulture);

        Assert.Equal(uiCulture, attr.UICulture.Name);
    }

    [Theory]
    [InlineData("it-IT")]
    [InlineData("ja-JP")]
    [InlineData("nb-NO")]
    public void CultureIsChangedWithinTest(string culture)
    {
        var originalCulture = Thread.CurrentThread.CurrentCulture;
        var attr = new UseCultureAttribute(culture);

        attr.Before(null!, null!);

        Assert.Equal(attr.Culture, Thread.CurrentThread.CurrentCulture);

        attr.After(null!, null!);

        Assert.Equal(originalCulture, Thread.CurrentThread.CurrentCulture);
    }

    [Theory]
    [InlineData("pt-BR")]
    [InlineData("pa-IN")]
    [InlineData("rm-CH")]
    public void UICultureIsChangedWithinTest(string uiCulture)
    {
        var originalUICulture = Thread.CurrentThread.CurrentUICulture;
        var attr = new UseCultureAttribute("ru-RU", uiCulture);

        attr.Before(null!, null!);

        Assert.Equal(attr.UICulture, Thread.CurrentThread.CurrentUICulture);

        attr.After(null!, null!);

        Assert.Equal(originalUICulture, Thread.CurrentThread.CurrentUICulture);
    }

    [Fact, UseCulture("sv-SE")]
    public void AttributeChangesCultureToSwedishInTestMethod()
    {
        Assert.Equal("sv-SE", Thread.CurrentThread.CurrentCulture.Name);
    }

    [Fact, UseCulture("th-TH", "es-CL")]
    public void AttributeChangesUICultureToChileanSpanishInTestMethod()
    {
        Assert.Equal("es-CL", Thread.CurrentThread.CurrentUICulture.Name);
    }

    [Theory]
    [InlineData("it-IT", "IT")]
    [InlineData("ja-JP", "JP")]
    [InlineData("nb-NO", "NO")]
    public void RefreshCachedCultureRelatedInformationWithinTest(string culture, string region)
    {
        var originalCulture = Thread.CurrentThread.CurrentCulture;
        var attr = new UseCultureAttribute(culture);

        attr.Before(null!, null!);

        var ri = new RegionInfo(Thread.CurrentThread.CurrentCulture.LCID);

        Assert.Equal(attr.Culture, Thread.CurrentThread.CurrentCulture);
        Assert.Equal(ri.TwoLetterISORegionName, region);

        attr.After(null!, null!);

        Assert.Equal(originalCulture, Thread.CurrentThread.CurrentCulture);
    }
}
