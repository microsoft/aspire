// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Tests.Shared.DashboardModel;
using Xunit;
using Value = Google.Protobuf.WellKnownTypes.Value;

namespace Aspire.Dashboard.Tests.Model;

public class ResourceViewModelExtensionsTerminalTests
{
    [Fact]
    public void HasTerminal_TrueWhenEnabledMarkerPresent()
    {
        var resource = ModelTestHelpers.CreateResource(
            properties: new Dictionary<string, ResourcePropertyViewModel>
            {
                [KnownProperties.Terminal.Enabled] = StringProperty(KnownProperties.Terminal.Enabled, "true"),
            });

        Assert.True(resource.HasTerminal());
    }

    [Fact]
    public void HasTerminal_FalseWhenEnabledMarkerAbsent()
    {
        var resource = ModelTestHelpers.CreateResource();

        Assert.False(resource.HasTerminal());
    }

    [Theory]
    [InlineData(true, null, null)]
    [InlineData(true, null, "1")]
    [InlineData(true, "not-a-number", "1")]
    [InlineData(true, "2", "not-a-number")]
    [InlineData(true, "2", "5")]
    [InlineData(false, null, null)]
    [InlineData(false, "0", "1")]
    public void HasUsableTerminal_DependsOnlyOnEnabledMarker(bool enabled, string? replicaIndex, string? replicaCount)
    {
        var properties = new Dictionary<string, ResourcePropertyViewModel>();
        if (enabled)
        {
            properties[KnownProperties.Terminal.Enabled] = StringProperty(KnownProperties.Terminal.Enabled, "true");
        }
        if (replicaIndex is not null)
        {
            properties[KnownProperties.Terminal.ReplicaIndex] = StringProperty(KnownProperties.Terminal.ReplicaIndex, replicaIndex);
        }
        if (replicaCount is not null)
        {
            properties[KnownProperties.Terminal.ReplicaCount] = StringProperty(KnownProperties.Terminal.ReplicaCount, replicaCount);
        }
        var resource = ModelTestHelpers.CreateResource(properties: properties);

        Assert.Equal(enabled, ResourceSelectHelpers.HasUsableTerminal(resource));
    }

    [Fact]
    public void TryGetTerminalConsumerUdsPath_ReturnsValueWhenPresent()
    {
        const string path = "/tmp/aspire-term/svc-r0.sock";
        var resource = ModelTestHelpers.CreateResource(
            properties: new Dictionary<string, ResourcePropertyViewModel>
            {
                [KnownProperties.Terminal.ConsumerUdsPath] = StringProperty(KnownProperties.Terminal.ConsumerUdsPath, path),
            });

        Assert.True(resource.TryGetTerminalConsumerUdsPath(out var actual));
        Assert.Equal(path, actual);
    }

    [Fact]
    public void TryGetTerminalConsumerUdsPath_FalseWhenAbsent()
    {
        var resource = ModelTestHelpers.CreateResource();

        Assert.False(resource.TryGetTerminalConsumerUdsPath(out var actual));
        Assert.Null(actual);
    }

    private static ResourcePropertyViewModel StringProperty(string name, string value)
    {
        return new ResourcePropertyViewModel(
            name,
            new Value { StringValue = value },
            isValueSensitive: false,
            knownProperty: null,
            sortOrder: 0,
            displayName: null,
            isHighlighted: false);
    }
}
