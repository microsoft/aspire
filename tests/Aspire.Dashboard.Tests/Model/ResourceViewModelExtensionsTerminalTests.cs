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

    [Fact]
    public void TryGetTerminalReplicaInfo_ReturnsParsedValues()
    {
        var resource = ModelTestHelpers.CreateResource(
            properties: new Dictionary<string, ResourcePropertyViewModel>
            {
                [KnownProperties.Terminal.ReplicaIndex] = StringProperty(KnownProperties.Terminal.ReplicaIndex, "2"),
                [KnownProperties.Terminal.ReplicaCount] = StringProperty(KnownProperties.Terminal.ReplicaCount, "5"),
            });

        Assert.True(resource.TryGetTerminalReplicaInfo(out var index, out var count));
        Assert.Equal(2, index);
        Assert.Equal(5, count);
    }

    [Fact]
    public void TryGetTerminalReplicaInfo_FalseWhenIndexMissing()
    {
        var resource = ModelTestHelpers.CreateResource(
            properties: new Dictionary<string, ResourcePropertyViewModel>
            {
                [KnownProperties.Terminal.ReplicaCount] = StringProperty(KnownProperties.Terminal.ReplicaCount, "1"),
            });

        Assert.False(resource.TryGetTerminalReplicaInfo(out _, out _));
    }

    [Fact]
    public void TryGetTerminalReplicaInfo_FalseWhenIndexUnparseable()
    {
        var resource = ModelTestHelpers.CreateResource(
            properties: new Dictionary<string, ResourcePropertyViewModel>
            {
                [KnownProperties.Terminal.ReplicaIndex] = StringProperty(KnownProperties.Terminal.ReplicaIndex, "not-a-number"),
                [KnownProperties.Terminal.ReplicaCount] = StringProperty(KnownProperties.Terminal.ReplicaCount, "1"),
            });

        Assert.False(resource.TryGetTerminalReplicaInfo(out _, out _));
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
            priority: 0);
    }
}
