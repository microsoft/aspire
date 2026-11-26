// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Model;

namespace Aspire.Dashboard.Model;

public interface IInstrumentUnitResolver
{
    string ResolveDisplayedUnit(OtlpInstrumentSummary instrument, bool capitalize, bool pluralize);
}
