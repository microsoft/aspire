// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.Cli.EndToEnd.Tests.Helpers;

public class LocalDeploymentTestHelpersTests
{
    [Fact]
    public void GetLabeledResourceListArguments_ContainerIncludesStoppedContainers()
    {
        Assert.Equal(
            ["container", "ls", "--all", "-q", "--filter", "label=owner=test"],
            LocalDeploymentTestHelpers.GetLabeledResourceListArguments("container", "owner=test"));
    }

    [Theory]
    [InlineData("volume")]
    [InlineData("network")]
    public void GetLabeledResourceListArguments_NonContainerIsUnchanged(string kind)
    {
        Assert.Equal(
            [kind, "ls", "-q", "--filter", "label=owner=test"],
            LocalDeploymentTestHelpers.GetLabeledResourceListArguments(kind, "owner=test"));
    }
}
