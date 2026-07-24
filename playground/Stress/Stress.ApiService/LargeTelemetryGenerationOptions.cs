// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Stress.ApiService;

public sealed class LargeTelemetryGenerationOptions
{
    public bool GenerateTraces { get; init; } = true;
    public int TraceCount { get; init; } = LargeTelemetryGenerator.TraceCount;
    public int SpansPerTrace { get; init; } = LargeTelemetryGenerator.SpansPerTrace;
    public bool GenerateLargeTrace { get; init; } = true;
    public int LargeTraceSpanCount { get; init; } = LargeTelemetryGenerator.LargeTraceSpanCount;
    public bool GenerateStructuredLogs { get; init; } = true;
    public int StructuredLogCount { get; init; } = LargeTelemetryGenerator.StructuredLogCount;
    public bool GenerateConsoleLogs { get; init; } = true;
    public int ConsoleLogCount { get; init; } = LargeTelemetryGenerator.ConsoleLogCount;
    public bool GenerateMetrics { get; init; } = true;
    public int MetricDurationHours { get; init; } = LargeTelemetryGenerator.MetricDurationHours;
    public int MetricDimensionCount { get; init; } = LargeTelemetryGenerator.MetricDimensionCount;

    public string? GetValidationError()
    {
        if (!GenerateTraces && !GenerateLargeTrace && !GenerateStructuredLogs && !GenerateConsoleLogs && !GenerateMetrics)
        {
            return "At least one telemetry kind must be enabled.";
        }
        if (GenerateTraces && TraceCount <= 0)
        {
            return "TraceCount must be positive when trace generation is enabled.";
        }
        if (GenerateTraces && SpansPerTrace <= 0)
        {
            return "SpansPerTrace must be positive when trace generation is enabled.";
        }
        if (GenerateLargeTrace && LargeTraceSpanCount <= 0)
        {
            return "LargeTraceSpanCount must be positive when large trace generation is enabled.";
        }
        if (GenerateStructuredLogs && StructuredLogCount <= 0)
        {
            return "StructuredLogCount must be positive when structured log generation is enabled.";
        }
        if (GenerateConsoleLogs && ConsoleLogCount <= 0)
        {
            return "ConsoleLogCount must be positive when console log generation is enabled.";
        }
        if (GenerateMetrics && MetricDurationHours <= 0)
        {
            return "MetricDurationHours must be positive when metric generation is enabled.";
        }
        if (GenerateMetrics && MetricDimensionCount <= 0)
        {
            return "MetricDimensionCount must be positive when metric generation is enabled.";
        }

        return null;
    }
}