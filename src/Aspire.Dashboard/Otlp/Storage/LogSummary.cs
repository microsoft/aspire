// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Model;

namespace Aspire.Dashboard.Otlp.Storage;

/// <summary>
/// Contains the log information displayed on the structured logs page.
/// </summary>
public sealed class LogSummary
{
    public required long InternalId { get; init; }
    public required DateTime TimeStamp { get; init; }
    public required LogLevel Severity { get; init; }
    public required string Message { get; init; }
    public required string SpanId { get; init; }
    public required string TraceId { get; init; }
    public required OtlpResource Resource { get; init; }
    public required string? ExceptionText { get; init; }
    public required bool HasGenAI { get; init; }
    public bool IsError => Severity is LogLevel.Error or LogLevel.Critical;
    public bool IsWarning => Severity is LogLevel.Warning;
}