// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.Pipelines;

namespace Aspire.Hosting.Tests.Pipelines;

[Trait("Partition", "4")]
public class PipelineSummaryTests
{
    [Fact]
    public void Add_WithValidKeyAndValue_AddsItemToCollection()
    {
        // Arrange
        var summary = new PipelineSummary();

        // Act
        summary.Add("Key1", "Value1");

        // Assert
        Assert.Single(summary.Items);
        Assert.Equal("Key1", summary.Items[0].Key);
        Assert.Equal("Value1", summary.Items[0].Value);
        Assert.False(summary.Items[0].EnableMarkdown);
    }

    [Fact]
    public void Add_MultipleItems_PreservesInsertionOrder()
    {
        // Arrange
        var summary = new PipelineSummary();

        // Act
        summary.Add("First", "1");
        summary.Add("Second", "2");
        summary.Add("Third", "3");

        // Assert
        Assert.Equal(3, summary.Items.Count);
        Assert.Equal("First", summary.Items[0].Key);
        Assert.Equal("Second", summary.Items[1].Key);
        Assert.Equal("Third", summary.Items[2].Key);
    }

    [Fact]
    public void Add_DuplicateKeys_AllowsBothInItems()
    {
        // Arrange
        var summary = new PipelineSummary();

        // Act
        summary.Add("Key", "Value1");
        summary.Add("Key", "Value2");

        // Assert
        Assert.Equal(2, summary.Items.Count);
        Assert.Equal("Value1", summary.Items[0].Value);
        Assert.Equal("Value2", summary.Items[1].Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Add_WithNullOrWhitespaceKey_ThrowsArgumentException(string? key)
    {
        // Arrange
        var summary = new PipelineSummary();

        // Act & Assert
        Assert.ThrowsAny<ArgumentException>(() => summary.Add(key!, "value"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Add_WithNullOrWhitespaceValue_ThrowsArgumentException(string? value)
    {
        // Arrange
        var summary = new PipelineSummary();

        // Act & Assert
        Assert.ThrowsAny<ArgumentException>(() => summary.Add("key", value!));
    }

    [Fact]
    public void Items_ReturnsReadOnlyCollection()
    {
        // Arrange
        var summary = new PipelineSummary();
        summary.Add("Key", "Value");

        // Act
        var items = summary.Items;

        // Assert
        Assert.IsType<System.Collections.ObjectModel.ReadOnlyCollection<PipelineSummaryItem>>(items);
    }

    [Fact]
    public void Items_WhenEmpty_ReturnsEmptyCollection()
    {
        // Arrange
        var summary = new PipelineSummary();

        // Act & Assert
        Assert.Empty(summary.Items);
    }

    [Fact]
    public void Items_WithItems_ReturnsItemsInInsertionOrder()
    {
        // Arrange
        var summary = new PipelineSummary();
        summary.Add("Key1", "Value1");
        summary.Add("Key2", "Value2");

        // Act & Assert
        Assert.Equal(2, summary.Items.Count);
        Assert.Equal("Key1", summary.Items[0].Key);
        Assert.Equal("Value1", summary.Items[0].Value);
        Assert.Equal("Key2", summary.Items[1].Key);
        Assert.Equal("Value2", summary.Items[1].Value);
    }

    [Fact]
    public void Items_WithDuplicateKeys_PreservesAllEntries()
    {
        // Arrange
        var summary = new PipelineSummary();
        summary.Add("Key", "FirstValue");
        summary.Add("Key", "LastValue");

        // Act & Assert
        Assert.Equal(2, summary.Items.Count);
        Assert.Equal("FirstValue", summary.Items[0].Value);
        Assert.Equal("LastValue", summary.Items[1].Value);
    }

    [Fact]
    public void Add_WithUnicodeCharactersInKey_Succeeds()
    {
        // Arrange
        var summary = new PipelineSummary();

        // Act
        summary.Add("☁️ Target", "Azure");
        summary.Add("📦 Resource Group", "rg-test");

        // Assert
        Assert.Equal(2, summary.Items.Count);
        Assert.Equal("☁️ Target", summary.Items[0].Key);
        Assert.Equal("📦 Resource Group", summary.Items[1].Key);
    }

    [Fact]
    public void Add_WithMarkdownString_SetsEnableMarkdownTrue()
    {
        // Arrange
        var summary = new PipelineSummary();

        // Act
        summary.Add("📦 Resource Group", new MarkdownString("[rg-test](https://portal.azure.com)"));

        // Assert
        Assert.Single(summary.Items);
        Assert.Equal("📦 Resource Group", summary.Items[0].Key);
        Assert.Equal("[rg-test](https://portal.azure.com)", summary.Items[0].Value);
        Assert.True(summary.Items[0].EnableMarkdown);
    }

    [Fact]
    public void Add_WithPlainString_SetsEnableMarkdownFalse()
    {
        // Arrange
        var summary = new PipelineSummary();

        // Act
        summary.Add("☁️ Target", "Azure");

        // Assert
        Assert.Single(summary.Items);
        Assert.False(summary.Items[0].EnableMarkdown);
    }

    [Fact]
    public void Add_MixedPlainAndMarkdown_PreservesFlags()
    {
        // Arrange
        var summary = new PipelineSummary();

        // Act
        summary.Add("☁️ Target", "Azure");
        summary.Add("📦 Resource Group", new MarkdownString("[rg-test](https://portal.azure.com)"));
        summary.Add("🌐 Location", "eastus");

        // Assert
        Assert.Equal(3, summary.Items.Count);
        Assert.False(summary.Items[0].EnableMarkdown);
        Assert.True(summary.Items[1].EnableMarkdown);
        Assert.False(summary.Items[2].EnableMarkdown);
    }
}
