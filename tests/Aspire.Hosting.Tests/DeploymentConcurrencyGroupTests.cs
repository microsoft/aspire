// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMPUTE004 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

namespace Aspire.Hosting.Tests;

public class DeploymentConcurrencyGroupTests
{
    [Fact]
    public void AnnotationConstructorInitializesGroup()
    {
        var group = new DeploymentConcurrencyGroup();

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
