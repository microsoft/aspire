// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;

namespace Aspire.Templates.Tests;

[RequiresFeature(TestFeature.ContainerRuntime)]
[RequiresFeature(TestFeature.SSLCertificate)]
public class StarterTemplateWithRedisCacheTests_Net11 : StarterTemplateRunTestsBase<StarterTemplateWithRedisCacheFixture_Net11>
{
    protected override int DashboardResourcesWaitTimeoutSecs => 300;

    public StarterTemplateWithRedisCacheTests_Net11(StarterTemplateWithRedisCacheFixture_Net11 fixture, ITestOutputHelper testOutput)
        : base(fixture, testOutput)
    {
        HasRedisCache = true;
    }
}
