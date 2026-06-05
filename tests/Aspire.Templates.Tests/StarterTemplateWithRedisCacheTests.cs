// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;

namespace Aspire.Templates.Tests;

[RequiresFeature(TestFeature.Docker)]
[RequiresFeature(TestFeature.SSLCertificate)]
public class StarterTemplateWithRedisCacheTests : StarterTemplateRunTestsBase<StarterTemplateWithRedisCacheFixture>
{
    protected override int DashboardResourcesWaitTimeoutSecs => 300;

    public StarterTemplateWithRedisCacheTests(StarterTemplateWithRedisCacheFixture fixture, ITestOutputHelper testOutput)
        : base(fixture, testOutput)
    {
        HasRedisCache = true;
    }
}
