// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Aspire.Cli.Backchannel;
using Aspire.Shared;
using Spectre.Console;

namespace Aspire.Cli.Utils;

/// <summary>
/// Lightweight, spec-aligned console logger for aligned colored task output without
/// rewriting the entire existing publishing pipeline. Integrates by mapping publish
/// step/task events to Start/Progress/Success/Warning/Failure calls.
/// </summary>
internal sealed class ConsoleActivityLogger
{
    private readonly IAnsiConsole _console;
    private readonly bool _enableColor;
    private readonly ICliHostEnvironment _hostEnvironment;
    private readonly bool _isDebugOrTraceLoggingEnabled;
    private readonly object _lock = new();
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly Dictionary<string, string> _stepColors = new();
    private readonly Dictionary<string, ActivityState> _stepStates = new(); // Track final state per step for summary
    private readonly Dictionary<string, string> _displayNames = new(); // Optional friendly display names for step keys
    private List<StepDurationRecord>? _durationRecords; // Optional per-step duration breakdown
    private readonly string[] _availableColors = ["blue", "cyan", "yellow", "magenta", "purple", "orange3"];
    private int _colorIndex;

    private int _successCount;
    private int _warningCount;
    private int _failureCount;
    private volatile bool _spinning;
    private Task? _spinnerTask;
    private readonly char[] _spinnerChars = ['|', '/', '-', '\\'];
    private int _spinnerIndex;

    private string? _finalStatusHeader;
    private bool _pipelineSucceeded;
    private IReadOnlyList<BackchannelPipelineSummaryItem>? _pipelineSummary;

    // No raw ANSI escape codes; rely on Spectre.Console markup tokens.

    private const string SuccessSymbol = "✓";
    private const string FailureSymbol = "✗";
    private const string WarningSymbol = "⚠";
    private const string InProgressSymbol = "→";
    private const string InfoSymbol = "i";

    public ConsoleActivityLogger(IAnsiConsole console, ICliHostEnvironment hostEnvironment, bool isDebugOrTraceLoggingEnabled = false, bool? forceColor = null)
    {
        _console = console;
        _hostEnvironment = hostEnvironment;
        _enableColor = forceColor ?? _hostEnvironment.SupportsAnsi;
        _isDebugOrTraceLoggingEnabled = isDebugOrTraceLoggingEnabled;

        // Disable spinner in non-interactive environments
        if (!_hostEnvironment.SupportsInteractiveOutput)
        {
            _spinning = false;
        }
    }

    public enum ActivityState
    {
        InProgress,
        Success,
        Warning,
        Failure,
        Info
    }

    public void StartTask(string taskKey, string? startingMessage = null)
    {
        lock (_lock)
        {
            // Initialize step state as InProgress if first time seen
            if (!_stepStates.ContainsKey(taskKey))
            {
                _stepStates[taskKey] = ActivityState.InProgress;
            }
        }
        WriteLine(taskKey, InProgressSymbol, startingMessage ?? "Starting...", ActivityState.InProgress);
    }

    public void StartTask(string taskKey, string displayName, string? startingMessage = null)
    {
        lock (_lock)
        {
            if (!_stepStates.ContainsKey(taskKey))
            {
                _stepStates[taskKey] = ActivityState.InProgress;
            }
            _displayNames[taskKey] = displayName;
        }
        WriteLine(taskKey, InProgressSymbol, startingMessage ?? ($"Starting {displayName}..."), ActivityState.InProgress);
    }

    public void StartSpinner()
    {
        // Skip spinner in non-interactive environments
        if (!_hostEnvironment.SupportsInteractiveOutput || _spinning)
        {
            return;
        }
        _spinning = true;
        _spinnerTask = Task.Run(async () =>
        {
            _console.Cursor.Hide();

            try
            {
                while (_spinning)
                {
                    var spinChar = _spinnerChars[_spinnerIndex % _spinnerChars.Length];

                    // Write then move back so nothing can write between these events (hopefully)
                    _console.Write(spinChar.ToString());
                    _console.Cursor.MoveLeft();

                    _spinnerIndex++;
                    await Task.Delay(120).ConfigureAwait(false);
                }
            }
            finally
            {
                // Clear spinner character
                _console.Write(" ");
                _console.Cursor.MoveLeft();
                _console.Cursor.Show();
            }
        });
    }

    public async Task StopSpinnerAsync()
    {
        _spinning = false;
        if (_spinnerTask is not null)
        {
            await _spinnerTask.ConfigureAwait(false);
            _spinnerTask = null;
        }
    }

    public void Progress(string taskKey, string message)
    {
        WriteLine(taskKey, InProgressSymbol, message, ActivityState.InProgress);
    }

    public void Success(string taskKey, string message, double? seconds = null)
    {
        lock (_lock)
        {
            _successCount++;
            _stepStates[taskKey] = ActivityState.Success;
        }
        WriteCompletion(taskKey, SuccessSymbol, message, ActivityState.Success, seconds);
    }

    public void Warning(string taskKey, string message, double? seconds = null)
    {
        lock (_lock)
        {
            _warningCount++;
            _stepStates[taskKey] = ActivityState.Warning;
        }
        WriteCompletion(taskKey, WarningSymbol, message, ActivityState.Warning, seconds);
    }

    public void Failure(string taskKey, string message, double? seconds = null)
    {
        lock (_lock)
        {
            _failureCount++;
            _stepStates[taskKey] = ActivityState.Failure;
        }
        WriteCompletion(taskKey, FailureSymbol, message, ActivityState.Failure, seconds);
    }

    public void Info(string taskKey, string message)
    {
        WriteLine(taskKey, InfoSymbol, message, ActivityState.Info);
    }

    public void Continuation(string message)
    {
        lock (_lock)
        {
            // Continuation lines: indent with two spaces relative to the symbol column for readability
            const string continuationPrefix = "  ";
            foreach (var line in SplitLinesPreserve(message))
            {
                _console.Write(continuationPrefix);
                _console.WriteLine(line);
            }
        }
    }

    public void WriteSummary()
    {
        lock (_lock)
        {
            var totalSeconds = _stopwatch.Elapsed.TotalSeconds;
            var line = new string('-', 60);
            _console.MarkupLine(line);
            var totalSteps = _stepStates.Count;
            // Derive per-step outcome counts from _stepStates (not task-level counters) for accurate X/Y display.
            var succeededSteps = _stepStates.Values.Count(v => v == ActivityState.Success);
            var warningSteps = _stepStates.Values.Count(v => v == ActivityState.Warning);
            var failedSteps = _stepStates.Values.Count(v => v == ActivityState.Failure);
            var summaryParts = new List<string>();
            var succeededSegment = totalSteps > 0 ? $"{succeededSteps}/{totalSteps} steps succeeded" : $"{succeededSteps} steps succeeded";
            if (_enableColor)
            {
                summaryParts.Add($"[green]{SuccessSymbol} {succeededSegment}[/]");
                if (warningSteps > 0)
                {
                    summaryParts.Add($"[yellow]{WarningSymbol} {warningSteps} warning{(warningSteps == 1 ? string.Empty : "s")}[/]");
                }
                if (failedSteps > 0)
                {
                    summaryParts.Add($"[red]{FailureSymbol} {failedSteps} failed[/]");
                }
            }
            else
            {
                summaryParts.Add($"{SuccessSymbol} {succeededSegment}");
                if (warningSteps > 0)
                {
                    summaryParts.Add($"{WarningSymbol} {warningSteps} warning{(warningSteps == 1 ? string.Empty : "s")}");
                }
                if (failedSteps > 0)
                {
                    summaryParts.Add($"{FailureSymbol} {failedSteps} failed");
                }
            }
            summaryParts.Add($"Total time: {DurationFormatter.FormatDuration(TimeSpan.FromSeconds(totalSeconds), CultureInfo.InvariantCulture, DecimalDurationDisplay.Fixed)}");
            _console.MarkupLine(string.Join(" • ", summaryParts));

            if (_durationRecords is { Count: > 0 })
            {
                _console.WriteLine();
                _console.MarkupLine("Steps Summary:");
                foreach (var rec in _durationRecords)
                {
                    // PadLeft(10) accommodates split units like "2h 30m", decimal units like "1.5s", and very long durations like "999d 23h"
                    var durStr = DurationFormatter.FormatDuration(rec.Duration, CultureInfo.InvariantCulture, DecimalDurationDisplay.Fixed).PadLeft(10);
                    var symbol = rec.State switch
                    {
                        ActivityState.Success => _enableColor ? "[green]" + SuccessSymbol + "[/]" : SuccessSymbol,
                        ActivityState.Warning => _enableColor ? "[yellow]" + WarningSymbol + "[/]" : WarningSymbol,
                        ActivityState.Failure => _enableColor ? "[red]" + FailureSymbol + "[/]" : FailureSymbol,
                        _ => _enableColor ? "[cyan]" + InProgressSymbol + "[/]" : InProgressSymbol
                    };
                    var name = rec.DisplayName.EscapeMarkup();
                    var reason = rec.State == ActivityState.Failure && !string.IsNullOrEmpty(rec.FailureReason)
                        ? ( _enableColor ? $" [red]— {HighlightMessage(rec.FailureReason!.EscapeMarkup())}[/]" : $" — {rec.FailureReason!.EscapeMarkup()}" )
                        : string.Empty;
                    var lineSb = new StringBuilder();
                    lineSb.Append("  ")
                        .Append(durStr).Append("  ")
                        .Append(symbol).Append(' ')
                        .Append("[dim]").Append(name).Append("[/]")
                        .Append(reason);
                    _console.MarkupLine(lineSb.ToString());
                }
                _console.WriteLine();
            }

            // If a caller provided a final status line via SetFinalResult, print it now
            if (!string.IsNullOrEmpty(_finalStatusHeader))
            {
                _console.MarkupLine(_finalStatusHeader!);
                
                // Display pipeline summary if available (for successful deployments)
                // Store in local variable to avoid potential threading issues
                var pipelineSummary = _pipelineSummary;
                if (_pipelineSucceeded && pipelineSummary is { Count: > 0 })
                {
                    _console.WriteLine();
                    foreach (var item in pipelineSummary)
                    {
                        var formattedLine = FormatPipelineSummaryItem(item);
                        _console.MarkupLine(formattedLine);
                    }
                }
                
                // If pipeline failed and not already in debug/trace mode, show help message about using --log-level debug
                if (!_pipelineSucceeded && !_isDebugOrTraceLoggingEnabled)
                {
                    var helpMessage = _enableColor
                        ? "[dim]For more details, add --log-level debug/trace to the command.[/]"
                        : "For more details, add --log-level debug/trace to the command.";
                    _console.MarkupLine(helpMessage);
                }
            }
            _console.MarkupLine(line);
            _console.WriteLine(); // Ensure final newline after deployment summary
        }
    }

    /// <summary>
    /// Formats a pipeline summary item for display.
    /// Values with Markdown enabled are converted to Spectre markup; plain-text values are escaped.
    /// </summary>
    private string FormatPipelineSummaryItem(BackchannelPipelineSummaryItem item)
    {
        if (_enableColor)
        {
            var escapedKey = item.Key.EscapeMarkup();
            var convertedValue = item.EnableMarkdown
                ? MarkdownToSpectreConverter.ConvertToSpectre(item.Value)
                : item.Value.EscapeMarkup();
            convertedValue = HighlightMessage(convertedValue);
            return $"  [blue]{escapedKey}[/]: {convertedValue}";
        }
        else
        {
            var plainKey = item.Key.EscapeMarkup();
            var plainValue = item.EnableMarkdown
                ? MarkdownToSpectreConverter.ConvertLinksToPlainText(item.Value).EscapeMarkup()
                : item.Value.EscapeMarkup();
            return $"  {plainKey}: {plainValue}";
        }
    }

    /// <summary>
    /// Sets the final pipeline result lines to be displayed in the summary (e.g., PIPELINE FAILED ...).
    /// Optional usage so existing callers remain compatible.
    /// </summary>
    /// <param name="succeeded">Whether the pipeline succeeded.</param>
    /// <param name="pipelineSummary">Optional pipeline summary as key-value pairs to display after the result. The list preserves insertion order.</param>
    public void SetFinalResult(bool succeeded, IReadOnlyList<BackchannelPipelineSummaryItem>? pipelineSummary = null)
    {
        _pipelineSucceeded = succeeded;
        _pipelineSummary = pipelineSummary;
        // Always show only a single final header line with symbol; no per-step duplication.
        if (succeeded)
        {
            _finalStatusHeader = _enableColor
                ? $"[green]{SuccessSymbol} PIPELINE SUCCEEDED[/]"
                : $"{SuccessSymbol} PIPELINE SUCCEEDED";
        }
        else
        {
            _finalStatusHeader = _enableColor
                ? $"[red]{FailureSymbol} PIPELINE FAILED[/]"
                : $"{FailureSymbol} PIPELINE FAILED";
        }
    }

    /// <summary>
    /// Provides per-step duration data (already sorted) for inclusion in the summary.
    /// </summary>
    public void SetStepDurations(IEnumerable<StepDurationRecord> records)
    {
        _durationRecords = records.ToList();
    }

    public readonly record struct StepDurationRecord(string Key, string DisplayName, ActivityState State, TimeSpan Duration, string? FailureReason);

    private void WriteCompletion(string taskKey, string symbol, string message, ActivityState state, double? seconds)
    {
        var text = seconds.HasValue ? $"{message} ({DurationFormatter.FormatDuration(TimeSpan.FromSeconds(seconds.Value), CultureInfo.InvariantCulture, DecimalDurationDisplay.Fixed)})" : message;
        WriteLine(taskKey, symbol, text, state);
    }

    private void WriteLine(string taskKey, string symbol, string message, ActivityState state)
    {
        lock (_lock)
        {
            var time = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            var stepColor = GetOrAssignStepColor(taskKey);
            var displayKey = _displayNames.TryGetValue(taskKey, out var dn) ? dn : taskKey;
            var coloredSymbol = _enableColor ? ColorizeSymbol(symbol, state) : symbol;

            foreach (var line in SplitLinesPreserve(message))
            {
                // Format: dim timestamp, colored step tag, symbol, message with Spectre markup
                var highlightedLine = HighlightMessage(line);
                var escapedTask = displayKey.EscapeMarkup();
                var markup = new StringBuilder();
                markup.Append("[dim]").Append(time).Append("[/] ");
                markup.Append('[').Append(stepColor).Append(']').Append('(').Append(escapedTask).Append(')').Append("[/] ");
                if (_enableColor)
                {
                    if (state == ActivityState.Failure)
                    {
                        // Make the entire failure segment (symbol + message) red, not just the symbol
                        markup.Append("[red]").Append(symbol).Append(' ').Append(highlightedLine).Append("[/]");
                    }
                    else if (state == ActivityState.Warning)
                    {
                        // Optionally color whole warning message (improves scanability)
                        markup.Append("[yellow]").Append(symbol).Append(' ').Append(highlightedLine).Append("[/]");
                    }
                    else
                    {
                        markup.Append(coloredSymbol).Append(' ').Append(highlightedLine);
                    }
                }
                else
                {
                    markup.Append(symbol).Append(' ').Append(highlightedLine);
                }
                _console.MarkupLine(markup.ToString());
            }
        }
    }

    private string GetOrAssignStepColor(string taskKey)
    {
        if (!_stepColors.TryGetValue(taskKey, out var color))
        {
            color = _availableColors[_colorIndex % _availableColors.Length];
            _stepColors[taskKey] = color;
            _colorIndex++;
        }
        return color;
    }

    private static IEnumerable<string> SplitLinesPreserve(string message)
    {
        if (message.IndexOf('\n') < 0)
        {
            yield return message;
            yield break;
        }
        var lines = message.Replace("\r\n", "\n").Split('\n');
        foreach (var l in lines)
        {
            yield return l;
        }
    }

    private static string ColorizeSymbol(string symbol, ActivityState state) => state switch
    {
        ActivityState.Success => $"[green]{symbol}[/]",
        ActivityState.Warning => $"[yellow]{symbol}[/]",
        ActivityState.Failure => $"[red]{symbol}[/]",
        ActivityState.InProgress => $"[cyan]{symbol}[/]",
        ActivityState.Info => $"[dim]{symbol}[/]",
        _ => symbol
    };

    // Messages are already converted from Markdown to Spectre markup in PipelineCommandBase.
    // When interactive output is not supported, we need to convert Spectre link markup
    // back to plain text since clickable links won't work. Show the URL for accessibility.
    private string HighlightMessage(string message)
    {
        if (!_hostEnvironment.SupportsInteractiveOutput)
        {
            // Convert Spectre link markup [cyan][link=url]text[/][/] to show URL
            // Pattern matches: [cyan][link=URL]TEXT[/][/] and replaces with URL
            return Regex.Replace(
                message,
                @"\[cyan\]\[link=([^\]]+)\]([^\[]+)\[/\]\[/\]",
                "$1");
        }

        return message;
    }

    // Note: DetectColorSupport is no longer needed as we use _hostEnvironment.SupportsAnsi directly
}
