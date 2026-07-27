// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Telemetry;

public static class TelemetryComponentIds
{
    // Pages
    public const string Resources = nameof(Resources);
    public const string Login = nameof(Login);
    public const string Traces = nameof(Traces);
    public const string StructuredLogs = nameof(StructuredLogs);
    public const string NotFound = nameof(NotFound);
    public const string Metrics = nameof(Metrics);
    public const string Error = nameof(Error);
    public const string ConsoleLogs = nameof(ConsoleLogs);

    // Controls
    public const string TraceDetail = nameof(TraceDetail);
    public const string StructuredLogDetails = nameof(StructuredLogDetails);
    public const string SpanDetails = nameof(SpanDetails);
    public const string ResourceDetails = nameof(ResourceDetails);
    public const string InteractionMessageBox = nameof(InteractionMessageBox);
    public const string InteractionMessageBar = nameof(InteractionMessageBar);
    public const string InteractionInputsDialog = nameof(InteractionInputsDialog);
    public const string GenAIVisualizerDialog = nameof(GenAIVisualizerDialog);
}
