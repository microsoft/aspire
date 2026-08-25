// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMPUTE004 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

namespace Aspire.Hosting.Tests;

public class DeploymentConcurrencyGroupTests
{
    [Fact]
    public void ConstructorInitializesGroup()
    {
        var group = new DeploymentConcurrencyGroup("group", maxConcurrentDeployments: 2);

        Assert.Equal("group", group.Name);
        Assert.Equal(2, group.MaxConcurrentDeployments);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ConstructorRejectsNonPositiveLimit(int maxConcurrentDeployments)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new DeploymentConcurrencyGroup("group", maxConcurrentDeployments));

        Assert.Equal(nameof(maxConcurrentDeployments), exception.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void ConstructorRejectsEmptyName(string name)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new DeploymentConcurrencyGroup(name, maxConcurrentDeployments: 1));

        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void ConstructorRejectsNullName()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => new DeploymentConcurrencyGroup(null!, maxConcurrentDeployments: 1));

        Assert.Equal("name", exception.ParamName);
    }
}
