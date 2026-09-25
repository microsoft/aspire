// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model.Otlp;

namespace Aspire.Dashboard.Model;

public sealed class FilterDialogViewModel
{
    public required FieldTelemetryFilter? Filter { get; init; }
    public required List<string> KnownKeys { get; init; }
    public required Func<CancellationToken, Task<List<string>>> GetPropertyKeysAsync { get; init; }
    public required Func<string, CancellationToken, Task<Dictionary<string, int>>> GetFieldValuesAsync { get; init; }
}
