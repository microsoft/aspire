// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit.Sdk;

namespace Aspire.Templates.Tests;

public sealed class StarterTemplateWithRedisCacheFixture_Net11 : TemplateAppFixture
{
    public StarterTemplateWithRedisCacheFixture_Net11(IMessageSink diagnosticMessageSink)
        : base(diagnosticMessageSink, "aspire-starter", "--use-redis-cache", tfm: TestTargetFramework.Net11)
    {
    }
}
