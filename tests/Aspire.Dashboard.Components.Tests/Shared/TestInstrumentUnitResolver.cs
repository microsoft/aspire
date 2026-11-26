// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;

namespace Aspire.Dashboard.Components.Tests.Shared;

public sealed class TestInstrumentUnitResolver : IInstrumentUnitResolver
{
    public string ResolveDisplayedUnit(OtlpInstrumentSummary instrument, bool capitalize, bool pluralize)
    {
        return instrument.Unit;
    }
}
