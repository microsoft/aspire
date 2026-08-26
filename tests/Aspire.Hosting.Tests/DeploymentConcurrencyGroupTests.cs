// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMPUTE004 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

namespace Aspire.Hosting.Tests;

public class DeploymentConcurrencyGroupTests
{
    [Fact]
    public void ConstructorInitializesName()
    {
        var group = new DeploymentConcurrencyGroup("shared");

        Assert.Equal("shared", group.Name);
    }

    [Fact]
    public void ConstructorRejectsNullName()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => new DeploymentConcurrencyGroup(null!));

        Assert.Equal("name", exception.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void ConstructorRejectsInvalidName(string name)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new DeploymentConcurrencyGroup(name));

        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void AnnotationConstructorInitializesGroup()
    {
        var group = new DeploymentConcurrencyGroup("shared");

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
