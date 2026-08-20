// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class IconResolverTests
{
    private static IconResolver CreateIconResolver(ILogger<IconResolver>? logger = null)
    {
        return new IconResolver(logger ?? NullLogger<IconResolver>.Instance);
    }

    [Theory]
    [InlineData("Database", IconSize.Size16, IconVariant.Regular)]
    [InlineData("database", IconSize.Size20, IconVariant.Regular)]
    [InlineData("DATABASE", IconSize.Size24, IconVariant.Regular)]
    [InlineData("Database", IconSize.Size16, IconVariant.Filled)]
    [InlineData("database", IconSize.Size20, IconVariant.Filled)]
    [InlineData("DATABASE", IconSize.Size24, IconVariant.Filled)]
    public void ResolveIconName_ValidIcon_ReturnsIcon(string name, IconSize size, IconVariant variant)
    {
        // Arrange
        var iconResolver = CreateIconResolver();

        // Act
        var icon = iconResolver.ResolveIconName(name, size, variant);

        // Assert
        Assert.NotNull(icon);
        Assert.Equal("Database", icon.Name);
        Assert.Equal(size, icon.Size);
        Assert.Equal(variant, icon.Variant);
    }

    [Fact]
    public void ResolveIconName_InvalidIcon_ReturnsNull()
    {
        // Arrange
        var iconResolver = CreateIconResolver();

        // Act
        var icon = iconResolver.ResolveIconName("NonExistentIcon", IconSize.Size20, IconVariant.Filled);

        // Assert
        Assert.Null(icon);
    }

    [Fact]
    public void ResolveIconName_InvalidIcon_LogsWarning()
    {
        // Arrange
        var testSink = new TestSink();
        var factory = LoggerFactory.Create(b => b.AddProvider(new TestLoggerProvider(testSink)));
        var logger = factory.CreateLogger<IconResolver>();
        var iconResolver = CreateIconResolver(logger);

        // Act
        var icon = iconResolver.ResolveIconName("NonExistentIcon", IconSize.Size20, IconVariant.Filled);

        // Assert
        Assert.Null(icon);
        var write = Assert.Single(testSink.Writes);
        Assert.Equal(LogLevel.Warning, write.LogLevel);
        Assert.Contains("NonExistentIcon", write.Message);
        Assert.Contains("could not be resolved", write.Message);
    }

    [Fact]
    public void ResolveIconName_CachesResults()
    {
        // Arrange
        var iconResolver = CreateIconResolver();

        // Act
        var icon1 = iconResolver.ResolveIconName("Database", IconSize.Size20, IconVariant.Filled);
        var icon2 = iconResolver.ResolveIconName("Database", IconSize.Size20, IconVariant.Filled);

        // Assert
        Assert.NotNull(icon1);
        Assert.NotNull(icon2);
        Assert.Same(icon1, icon2); // Should be the same cached instance
    }

    [Fact]
    public void ResolveIconName_InvalidIcon_CachesNullResult()
    {
        // Arrange
        var iconResolver = CreateIconResolver();

        // Act - call twice with same invalid icon
        var icon1 = iconResolver.ResolveIconName("NonExistentIcon", IconSize.Size20, IconVariant.Filled);
        var icon2 = iconResolver.ResolveIconName("NonExistentIcon", IconSize.Size20, IconVariant.Filled);

        // Assert
        Assert.Null(icon1);
        Assert.Null(icon2);
        // Both calls should return the same cached null result
    }

    [Fact]
    public void ResolveIconName_DifferentSizes_CreatesSeparateCacheEntries()
    {
        // Arrange
        var iconResolver = CreateIconResolver();

        // Act
        var icon16 = iconResolver.ResolveIconName("Database", IconSize.Size16, IconVariant.Filled);
        var icon20 = iconResolver.ResolveIconName("Database", IconSize.Size20, IconVariant.Filled);

        // Assert
        Assert.NotNull(icon16);
        Assert.NotNull(icon20);
        // Different sizes should have different instances
        Assert.NotSame(icon16, icon20);
    }

    [Fact]
    public void ResolveIconName_DifferentVariants_CreatesSeparateCacheEntries()
    {
        // Arrange
        var iconResolver = CreateIconResolver();

        // Act
        var iconFilled = iconResolver.ResolveIconName("Database", IconSize.Size20, IconVariant.Filled);
        var iconRegular = iconResolver.ResolveIconName("Database", IconSize.Size20, IconVariant.Regular);

        // Assert
        Assert.NotNull(iconFilled);
        Assert.NotNull(iconRegular);
        // Different variants should have different instances
        Assert.NotSame(iconFilled, iconRegular);
    }

    [Theory]
    [InlineData(IconSize.Size16)]
    [InlineData(IconSize.Size20)]
    [InlineData(IconSize.Size24)]
    public void ResolveIconName_WithVariousSizes_ReturnsIcon(IconSize size)
    {
        // Arrange
        var iconResolver = CreateIconResolver();

        // Act
        var icon = iconResolver.ResolveIconName("Database", size, IconVariant.Filled);

        // Assert
        Assert.NotNull(icon);
        Assert.Equal(size, icon.Size);
    }

    [Fact]
    public void ResolveIconName_SameCacheKeyMultipleTimes_ReturnsSameInstance()
    {
        // Arrange
        var iconResolver = CreateIconResolver();

        // Act
        var icon1 = iconResolver.ResolveIconName("Database", IconSize.Size20, IconVariant.Filled);
        var icon2 = iconResolver.ResolveIconName("Database", IconSize.Size20, IconVariant.Filled);
        var icon3 = iconResolver.ResolveIconName("Database", IconSize.Size20, IconVariant.Filled);

        // Assert
        Assert.NotNull(icon1);
        Assert.Same(icon1, icon2);
        Assert.Same(icon2, icon3);
        // Multiple calls with same parameters should return same cached instance
    }

    [Fact]
    public void ResolveIconName_FallbackToSmallerSizeIfUnavailable_ReturnsIcon()
    {
        // Arrange
        var iconResolver = CreateIconResolver();

        // Act - CodePyRectangle only has size 16, but we request size 24
        var icon = iconResolver.ResolveIconName("CodePyRectangle", IconSize.Size24, IconVariant.Filled);

        // Assert
        Assert.NotNull(icon);
        Assert.Equal(IconSize.Size16, icon.Size);
    }
}
