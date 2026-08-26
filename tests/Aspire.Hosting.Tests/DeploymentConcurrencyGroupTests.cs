// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMPUTE004 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

namespace Aspire.Hosting.Tests;

public class DeploymentConcurrencyGroupTests
{
    [Fact]
    public void ConstructorInitializesGroup()
    {
        var group = new DeploymentConcurrencyGroup(maxConcurrentDeployments: 2);

        Assert.Equal(2, group.MaxConcurrentDeployments);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ConstructorRejectsNonPositiveLimit(int maxConcurrentDeployments)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new DeploymentConcurrencyGroup(maxConcurrentDeployments));

        Assert.Equal(nameof(maxConcurrentDeployments), exception.ParamName);
    }

    [Fact]
    public void AnnotationConstructorInitializesGroup()
    {
        var group = new DeploymentConcurrencyGroup(maxConcurrentDeployments: 2);

        var annotation = new DeploymentConcurrencyGroupAnnotation(group);

        Assert.Same(group, annotation.Group);
    }

    [Fact]
    public void AnnotationConstructorRejectsNullGroup()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => new DeploymentConcurrencyGroupAnnotation(null!));

        Assert.Equal("group", exception.ParamName);
    }
}
