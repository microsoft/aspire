// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Deck;
using Aspire.Dashboard.Model;
using Aspire.Tests.Shared.DashboardModel;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class ResourceIconHelpersTests
{
    [Theory]
    [InlineData("Database", DeckIconName.Database)]
    [InlineData("CloudArrowUp", DeckIconName.External)]
    [InlineData("CodeCsRectangle", DeckIconName.Executable)]
    [InlineData("PlugConnectedSettings", DeckIconName.Link)]
    public void GetDeckIconForResource_WithMappedCustomIcon_ReturnsMappedIcon(string iconName, DeckIconName expected)
    {
        var resource = ModelTestHelpers.CreateResource(iconName: iconName);

        var icon = ResourceIconHelpers.GetDeckIconForResource(resource);

        Assert.Equal(expected, icon);
    }

    [Fact]
    public void GetDeckIconForResource_WithUnknownCustomIcon_FallsBackToResourceType()
    {
        var resource = ModelTestHelpers.CreateResource(resourceType: KnownResourceTypes.Container, iconName: "NonExistentIcon");

        var icon = ResourceIconHelpers.GetDeckIconForResource(resource);

        Assert.Equal(DeckIconName.Container, icon);
    }

    [Theory]
    [InlineData(KnownResourceTypes.Executable, DeckIconName.Executable)]
    [InlineData(KnownResourceTypes.Project, DeckIconName.Project)]
    [InlineData(KnownResourceTypes.Container, DeckIconName.Container)]
    [InlineData(KnownResourceTypes.Parameter, DeckIconName.Parameters)]
    [InlineData(KnownResourceTypes.ConnectionString, DeckIconName.Link)]
    [InlineData(KnownResourceTypes.ExternalService, DeckIconName.External)]
    [InlineData("postgres-database", DeckIconName.Database)]
    [InlineData("custom-resource", DeckIconName.Resources)]
    public void GetDeckIconForResource_WithResourceType_ReturnsExpectedIcon(string resourceType, DeckIconName expected)
    {
        var resource = ModelTestHelpers.CreateResource(resourceType: resourceType);

        var icon = ResourceIconHelpers.GetDeckIconForResource(resource);

        Assert.Equal(expected, icon);
    }

    [Theory]
    [InlineData("DATABASE", DeckIconName.Database)]
    [InlineData("AgentsAdd", DeckIconName.Sparkle)]
    [InlineData("GlobeDesktop", DeckIconName.External)]
    public void TryGetDeckIcon_IsCaseInsensitive(string iconName, DeckIconName expected)
    {
        var resolved = ResourceIconHelpers.TryGetDeckIcon(iconName, out var icon);

        Assert.True(resolved);
        Assert.Equal(expected, icon);
    }

    [Fact]
    public void TryGetDeckIcon_WithUnknownIcon_ReturnsFalse()
    {
        var resolved = ResourceIconHelpers.TryGetDeckIcon("NonExistentIcon", out _);

        Assert.False(resolved);
    }
}
