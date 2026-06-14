// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public class KnownPropertyLookupTests
{
    [Fact]
    public void FindProperty_GenericResourceProperty_ReturnsKnownProperty()
    {
        var lookup = new KnownPropertyLookup();

        var (priority, knownProperty) = lookup.FindProperty(KnownResourceTypes.Container, KnownProperties.Resource.State);

        Assert.NotEqual(int.MaxValue, priority);
        Assert.NotNull(knownProperty);
        Assert.Equal(KnownProperties.Resource.State, knownProperty.Key);
    }

    [Theory]
    [InlineData(KnownResourceTypes.Container, KnownProperties.Container.Image)]
    [InlineData(KnownResourceTypes.Executable, KnownProperties.Executable.Path)]
    [InlineData(KnownResourceTypes.Project, KnownProperties.Project.Path)]
    [InlineData(KnownResourceTypes.Parameter, KnownProperties.Parameter.Value)]
    public void FindProperty_ProducerSuppliedPropertyMetadata_ReturnsUnknownProperty(string resourceType, string propertyName)
    {
        var lookup = new KnownPropertyLookup();

        var (priority, knownProperty) = lookup.FindProperty(resourceType, propertyName);

        Assert.Equal(int.MaxValue, priority);
        Assert.Null(knownProperty);
    }
}
