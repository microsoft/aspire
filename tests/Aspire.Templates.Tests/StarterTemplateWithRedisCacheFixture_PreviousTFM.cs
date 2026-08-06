// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit.Sdk;

namespace Aspire.Templates.Tests;

public sealed class StarterTemplateWithRedisCacheFixture_PreviousTFM : TemplateAppFixture
{
    public StarterTemplateWithRedisCacheFixture_PreviousTFM(IMessageSink diagnosticMessageSink)
        : base(diagnosticMessageSink, "aspire-starter", "--use-redis-cache", tfm: TestTargetFramework.Net8)
    {
    }
}
