// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Model;
using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components;

public partial class LogMessageColumnDisplay
{
    [Parameter, EditorRequired]
    public required OtlpLogEntry LogEntry { get; set; }

    [Parameter, EditorRequired]
    public required string FilterText { get; set; }

    [Parameter, EditorRequired]
    public required EventCallback<OtlpLogEntry> LaunchGenAIVisualizerCallback { get; set; }

    [Parameter, EditorRequired]
    public required Func<OtlpLogEntry, bool> IsGenAILogCallback { get; set; }

    private string? _exceptionText;

    protected override void OnInitialized()
    {
        _exceptionText = OtlpLogEntry.GetExceptionText(LogEntry);
    }

    private Task OnLaunchGenAIVisualizerAsync() => LaunchGenAIVisualizerCallback.InvokeAsync(LogEntry);
}
