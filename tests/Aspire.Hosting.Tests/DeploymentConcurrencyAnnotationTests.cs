// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMPUTE004 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

namespace Aspire.Hosting.Tests;

public class DeploymentConcurrencyAnnotationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ConstructorRejectsNonPositiveLimit(int maxConcurrentDeployments)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new DeploymentConcurrencyAnnotation(maxConcurrentDeployments));

        Assert.Equal(nameof(maxConcurrentDeployments), exception.ParamName);
    }
}
