// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit.Sdk;

namespace Aspire.Templates.Tests;

public sealed class StarterTemplateFixture_PreviousTFM : TemplateAppFixture
{
    public StarterTemplateFixture_PreviousTFM(IMessageSink diagnosticMessageSink)
        : base(diagnosticMessageSink, "aspire-starter", tfm: TestTargetFramework.Net8)
    {
    }
}
