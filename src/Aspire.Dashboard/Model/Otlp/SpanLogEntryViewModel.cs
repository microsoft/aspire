// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Dashboard.Otlp.Storage;

namespace Aspire.Dashboard.Model.Otlp;

[DebuggerDisplay("Index = {Index}, LogEntry = {LogEntry.InternalId}, LeftOffset = {LeftOffset}")]
public sealed class SpanLogEntryViewModel
{
    public required int Index { get; init; }
    public required LogSummary LogEntry { get; init; }
    public required double LeftOffset { get; init; }
}
