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

        var (sortOrder, knownProperty) = lookup.FindProperty(KnownProperties.Resource.State);

        Assert.NotEqual(int.MaxValue, sortOrder);
        Assert.NotNull(knownProperty);
        Assert.Equal(KnownProperties.Resource.State, knownProperty.Key);
    }

    [Theory]
    [InlineData(KnownProperties.Project.Path)]
    [InlineData(KnownProperties.Project.LaunchProfile)]
    [InlineData(KnownProperties.Executable.Path)]
    [InlineData(KnownProperties.Executable.WorkDir)]
    [InlineData(KnownProperties.Executable.Args)]
    [InlineData(KnownProperties.Executable.Pid)]
    [InlineData(KnownProperties.Container.Image)]
    [InlineData(KnownProperties.Container.Id)]
    [InlineData(KnownProperties.Container.Command)]
    [InlineData(KnownProperties.Container.Args)]
    [InlineData(KnownProperties.Container.Ports)]
    [InlineData(KnownProperties.Container.Lifetime)]
    [InlineData(KnownProperties.Parameter.Value)]
    public void FindProperty_ProducerSuppliedPropertyMetadata_ReturnsUnknownProperty(string propertyName)
    {
        var lookup = new KnownPropertyLookup();

        var (sortOrder, knownProperty) = lookup.FindProperty(propertyName);

        Assert.Equal(int.MaxValue, sortOrder);
        Assert.Null(knownProperty);
    }
}
