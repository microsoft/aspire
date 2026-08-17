// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;

namespace Aspire.Templates.Tests;

[RequiresFeature(TestFeature.SSLCertificate)]
public class StarterTemplateRunTests_Net11 : StarterTemplateRunTestsBase<StarterTemplateFixture_Net11>
{
    public StarterTemplateRunTests_Net11(StarterTemplateFixture_Net11 fixture, ITestOutputHelper testOutput)
        : base(fixture, testOutput)
    {
    }
}
