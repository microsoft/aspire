// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Storage;
using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components;

public partial class LogMessageColumnDisplay
{
    [Parameter, EditorRequired]
    public required LogSummary LogEntry { get; set; }

    [Parameter, EditorRequired]
    public required string FilterText { get; set; }

    [Parameter, EditorRequired]
    public required EventCallback<LogSummary> LaunchGenAIVisualizerCallback { get; set; }

    [Parameter, EditorRequired]
    public required Func<LogSummary, bool> IsGenAILogCallback { get; set; }

    private Task OnLaunchGenAIVisualizerAsync() => LaunchGenAIVisualizerCallback.InvokeAsync(LogEntry);
}
