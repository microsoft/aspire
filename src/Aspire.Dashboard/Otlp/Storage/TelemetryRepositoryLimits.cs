// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Otlp.Storage;

internal static class TelemetryRepositoryLimits
{
    public const int MaxResourceViewCount = 10_000;
    public const int MaxInstrumentCount = 10_000;
    public const int MaxScopeCount = 10_000;
    public const int MaxDimensionCount = 10_000;
    public const int MaxKnownAttributeValueCount = 10_000;
    public const int MaxKnownAttributeValuesPerKey = 10_000;
}
