// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils.Markdown;
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
    private TimeSpan? _summaryElapsedOverride;

    // No raw ANSI escape codes; rely on Spectre.Console markup tokens.

    private const string SuccessSymbol = "✓";
    private const string FailureSymbol = "✗";
    // The warning symbol is intentionally not ⚠ because that character can be displayed as an emoji in some terminals, causing rendering issues.
    private const string WarningSymbol = "△";

    private const string InProgressSymbol = "→";
    private const string InfoSymbol = "i";
    private const int SummaryTimelineWidth = 28;
    private const int SummaryTimelineTicks = 4;

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
        WriteLine(taskKey, InProgressSymbol, startingMessage ?? ConsoleActivityLoggerStrings.ActivityStarting, ActivityState.InProgress);
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
        WriteLine(taskKey, InProgressSymbol, startingMessage ?? string.Format(CultureInfo.CurrentCulture, ConsoleActivityLoggerStrings.ActivityStartingWithName, displayName), ActivityState.InProgress);
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

    public void Info(string taskKey, string message, bool dim = false)
    {
        WriteLine(taskKey, InfoSymbol, message, ActivityState.Info, dim);
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
            var totalDuration = _summaryElapsedOverride ?? _stopwatch.Elapsed;
            var totalSeconds = totalDuration.TotalSeconds;
            var line = new string('-', 60);
            _console.MarkupLine(line);
            var totalSteps = _stepStates.Count;
            // Derive per-step outcome counts from _stepStates (not task-level counters) for accurate X/Y display.
            var succeededSteps = _stepStates.Values.Count(v => v == ActivityState.Success);
            var warningSteps = _stepStates.Values.Count(v => v == ActivityState.Warning);
            var failedSteps = _stepStates.Values.Count(v => v == ActivityState.Failure);
            var summaryParts = new List<string>();
            var succeededSegment = totalSteps > 0
                ? string.Format(CultureInfo.CurrentCulture, ConsoleActivityLoggerStrings.SummaryStepsSucceededWithTotal, succeededSteps, totalSteps)
                : string.Format(CultureInfo.CurrentCulture, ConsoleActivityLoggerStrings.SummaryStepsSucceeded, succeededSteps);
            if (_enableColor)
            {
                summaryParts.Add($"[green]{ConsoleHelpers.FormatEmojiPrefix(KnownEmojis.CheckMarkButton, _console, suppressColor: true)}{succeededSegment}[/]");
                if (warningSteps > 0)
                {
                    var warningText = warningSteps == 1
                        ? string.Format(CultureInfo.CurrentCulture, ConsoleActivityLoggerStrings.SummaryWarningsSingular, warningSteps)
                        : string.Format(CultureInfo.CurrentCulture, ConsoleActivityLoggerStrings.SummaryWarningsPlural, warningSteps);
                    summaryParts.Add($"[yellow]{ConsoleHelpers.FormatEmojiPrefix(KnownEmojis.Warning, _console, suppressColor: true)}{warningText}[/]");
                }
                if (failedSteps > 0)
                {
                    summaryParts.Add($"[red]{ConsoleHelpers.FormatEmojiPrefix(KnownEmojis.CrossMark, _console, suppressColor: true)}{string.Format(CultureInfo.CurrentCulture, ConsoleActivityLoggerStrings.SummaryFailed, failedSteps)}[/]");
                }
            }
            else
            {
                summaryParts.Add($"{SuccessSymbol} {succeededSegment}");
                if (warningSteps > 0)
                {
                    var warningText = warningSteps == 1
                        ? string.Format(CultureInfo.CurrentCulture, ConsoleActivityLoggerStrings.SummaryWarningsSingular, warningSteps)
                        : string.Format(CultureInfo.CurrentCulture, ConsoleActivityLoggerStrings.SummaryWarningsPlural, warningSteps);
                    summaryParts.Add($"{WarningSymbol} {warningText}");
                }
                if (failedSteps > 0)
                {
                    summaryParts.Add($"{FailureSymbol} {string.Format(CultureInfo.CurrentCulture, ConsoleActivityLoggerStrings.SummaryFailed, failedSteps)}");
                }
            }
            summaryParts.Add(string.Format(CultureInfo.CurrentCulture, ConsoleActivityLoggerStrings.SummaryTotalTime, DurationFormatter.FormatDuration(TimeSpan.FromSeconds(totalSeconds), CultureInfo.InvariantCulture, DecimalDurationDisplay.Fixed)));
            _console.MarkupLine(string.Join(" • ", summaryParts));

            if (_durationRecords is { Count: > 0 })
            {
                WriteStepDurationsSummary(_durationRecords);
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
                        ? $"[dim]{ConsoleActivityLoggerStrings.SummaryLogLevelHelp}[/]"
                        : ConsoleActivityLoggerStrings.SummaryLogLevelHelp;
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
                ? $"[green]{ConsoleHelpers.FormatEmojiPrefix(KnownEmojis.CheckMarkButton, _console, suppressColor: true)}{ConsoleActivityLoggerStrings.PipelineSucceeded}[/]"
                : $"{SuccessSymbol} {ConsoleActivityLoggerStrings.PipelineSucceeded}";
        }
        else
        {
            _finalStatusHeader = _enableColor
                ? $"[red]{ConsoleHelpers.FormatEmojiPrefix(KnownEmojis.CrossMark, _console, suppressColor: true)}{ConsoleActivityLoggerStrings.PipelineFailed}[/]"
                : $"{FailureSymbol} {ConsoleActivityLoggerStrings.PipelineFailed}";
        }
    }

    /// <summary>
    /// Provides per-step duration data for inclusion in the summary.
    /// </summary>
    public void SetStepDurations(IEnumerable<StepDurationRecord> records)
    {
        _durationRecords = records.ToList();
    }

    internal void SeedSummaryState(IEnumerable<StepDurationRecord> records)
    {
        var recordList = records.ToList();

        lock (_lock)
        {
            _stepStates.Clear();
            _displayNames.Clear();

            foreach (var record in recordList)
            {
                _stepStates[record.Key] = record.State;
                _displayNames[record.Key] = record.DisplayName;
            }

            _summaryElapsedOverride = recordList.Count > 0
                ? recordList.Max(r => r.EndOffset > TimeSpan.Zero ? r.EndOffset : r.Duration)
                : TimeSpan.Zero;
        }
    }

    public readonly record struct StepDurationRecord(
        string Key,
        string DisplayName,
        ActivityState State,
        TimeSpan Duration,
        string? FailureReason,
        string? ParentKey = null,
        int Level = 0,
        int Sequence = 0,
        TimeSpan StartOffset = default,
        TimeSpan EndOffset = default);

    private void WriteStepDurationsSummary(IReadOnlyList<StepDurationRecord> records)
    {
        var orderedRecords = OrderStepDurationsHierarchically(records);
        if (orderedRecords.Count == 0)
        {
            return;
        }

        var summaryTitle = SharedCommandStrings.PipelineStepsSummaryTitle;
        var timelineLabel = SharedCommandStrings.PipelineStepTimelineLabel;
        var totalTimeline = orderedRecords.Max(r => r.EndOffset > TimeSpan.Zero ? r.EndOffset : r.Duration);
        var durationWidth = Math.Max(10, orderedRecords.Max(r => FormatSummaryDuration(r.Duration, totalTimeline).Length));
        var nameWidth = Math.Max(timelineLabel.Length, orderedRecords.Max(r => GetIndentedDisplayName(r).RemoveMarkup().Length));
        var renderTimeline = ShouldRenderTimeline(durationWidth, nameWidth, totalTimeline);
        var timelinePrefix = $"  {new string(' ', durationWidth)}    {new string(' ', nameWidth)}  ";
        var timelineLabelPrefix = $"  {new string(' ', durationWidth)}    {timelineLabel.PadRight(nameWidth)}  ";

        _console.WriteLine();
        _console.MarkupLine(summaryTitle);

        if (renderTimeline)
        {
            _console.MarkupLine($"{timelineLabelPrefix}[dim]{BuildTimelineLabels(totalTimeline, SummaryTimelineWidth).EscapeMarkup()}[/]");
            _console.MarkupLine($"{timelinePrefix}[dim]{BuildTimelineScale(SummaryTimelineWidth).EscapeMarkup()}[/]");
        }

        foreach (var rec in orderedRecords)
        {
            var durStr = FormatSummaryDuration(rec.Duration, totalTimeline).PadLeft(durationWidth);
            var stateSymbol = rec.State switch
            {
                ActivityState.Success => SuccessSymbol,
                ActivityState.Warning => WarningSymbol,
                ActivityState.Failure => FailureSymbol,
                ActivityState.Info => InfoSymbol,
                _ => InProgressSymbol
            };
            var symbol = _enableColor ? $"[{GetStateColor(rec.State)}]{stateSymbol}[/]" : stateSymbol;
            var displayName = GetIndentedDisplayName(rec);
            var plainDisplayName = displayName.RemoveMarkup();
            // Pad based on visible (plain-text) width, then re-append the markup name so tags render correctly.
            var padding = renderTimeline ? Math.Max(0, nameWidth - plainDisplayName.Length) : 0;
            var name = displayName + new string(' ', padding);

            // FailureReason is already Spectre-safe (pre-processed through ConvertTextWithMarkdownFlag which escapes or converts markdown).
            var reason = rec.State == ActivityState.Failure && !string.IsNullOrEmpty(rec.FailureReason)
                ? (_enableColor ? $" [red]— {HighlightMessage(rec.FailureReason!)}[/]" : $" — {rec.FailureReason!}")
                : string.Empty;

            var lineSb = new StringBuilder();
            lineSb.Append("  ")
                .Append(durStr).Append("  ")
                .Append(symbol).Append(' ')
                .Append("[dim]").Append(name).Append("[/]");

            if (renderTimeline)
            {
                var timelineBar = ColorizeSummaryBar(BuildTimelineBar(rec, totalTimeline, SummaryTimelineWidth), rec.State);
                lineSb.Append("  ").Append(timelineBar);
            }

            lineSb.Append(reason);
            _console.MarkupLine(lineSb.ToString());
        }

        _console.WriteLine();
    }

    private static List<StepDurationRecord> OrderStepDurationsHierarchically(IReadOnlyList<StepDurationRecord> records)
    {
        var orderedRecords = records
            .OrderBy(r => r.Sequence)
            .ThenBy(r => r.DisplayName, StringComparers.CommandName)
            .ToList();
        var recordsByKey = orderedRecords.ToDictionary(r => r.Key, StringComparers.CommandName);
        var childrenByParent = new Dictionary<string, List<StepDurationRecord>>(StringComparers.CommandName);

        foreach (var record in orderedRecords)
        {
            if (record.ParentKey is { Length: > 0 } parentKey &&
                !string.Equals(parentKey, record.Key, StringComparisons.CommandName) &&
                recordsByKey.ContainsKey(parentKey))
            {
                if (!childrenByParent.TryGetValue(parentKey, out var children))
                {
                    children = [];
                    childrenByParent[parentKey] = children;
                }

                children.Add(record);
            }
        }

        foreach (var children in childrenByParent.Values)
        {
            children.Sort(static (left, right) =>
            {
                var sequenceComparison = left.Sequence.CompareTo(right.Sequence);
                return sequenceComparison != 0
                    ? sequenceComparison
                    : StringComparers.CommandName.Compare(left.DisplayName, right.DisplayName);
            });
        }

        var result = new List<StepDurationRecord>(orderedRecords.Count);
        var visited = new HashSet<string>(StringComparers.CommandName);

        foreach (var root in orderedRecords.Where(r => r.ParentKey is null || !recordsByKey.ContainsKey(r.ParentKey)))
        {
            VisitRecord(root, childrenByParent, visited, result);
        }

        foreach (var record in orderedRecords)
        {
            if (visited.Add(record.Key))
            {
                result.Add(record);
            }
        }

        return result;
    }

    private static void VisitRecord(
        StepDurationRecord record,
        IReadOnlyDictionary<string, List<StepDurationRecord>> childrenByParent,
        ISet<string> visited,
        ICollection<StepDurationRecord> result)
    {
        if (!visited.Add(record.Key))
        {
            return;
        }

        result.Add(record);

        if (!childrenByParent.TryGetValue(record.Key, out var children))
        {
            return;
        }

        foreach (var child in children)
        {
            VisitRecord(child, childrenByParent, visited, result);
        }
    }

    private static string GetIndentedDisplayName(StepDurationRecord record)
    {
        var level = Math.Max(record.Level, 0);
        return level == 0
            ? record.DisplayName
            : $"{new string(' ', level * 2)}{record.DisplayName}";
    }

    private static string BuildTimelineScale(int width)
    {
        if (width <= 0)
        {
            return "││";
        }

        var chars = Enumerable.Repeat('─', width).ToArray();
        for (var tick = 1; tick < SummaryTimelineTicks; tick++)
        {
            var position = (int)Math.Round((double)tick * (width - 1) / SummaryTimelineTicks);
            if (position >= 0 && position < chars.Length)
            {
                chars[position] = '┬';
            }
        }

        return $"│{new string(chars)}│";
    }

    private static string BuildTimelineLabels(TimeSpan totalTimeline, int width)
    {
        // Match the zero label to the unit family used by the end label so short timelines don't mix `0s`
        // with millisecond- or microsecond-based durations.
        var startText = BuildTimelineStartLabel(totalTimeline);
        var endText = DurationFormatter.FormatDuration(totalTimeline, CultureInfo.InvariantCulture, DecimalDurationDisplay.Fixed);
        var labelWidth = Math.Max(width + 2, startText.Length + 1 + endText.Length);
        var spacing = Math.Max(1, labelWidth - startText.Length - endText.Length);

        return $"{startText}{new string(' ', spacing)}{endText}";
    }

    private static string BuildTimelineStartLabel(TimeSpan totalTimeline)
    {
        var unit = totalTimeline > TimeSpan.Zero ? DurationFormatter.GetUnit(totalTimeline) : "ms";
        return $"0{unit}";
    }

    private static string FormatSummaryDuration(TimeSpan duration, TimeSpan totalTimeline)
    {
        return duration == TimeSpan.Zero
            ? BuildTimelineStartLabel(totalTimeline)
            : DurationFormatter.FormatDuration(duration, CultureInfo.InvariantCulture, DecimalDurationDisplay.Fixed);
    }

    private bool ShouldRenderTimeline(int durationWidth, int nameWidth, TimeSpan totalTimeline)
    {
        var consoleWidth = _console.Profile.Width;
        if (consoleWidth <= 0 || consoleWidth == int.MaxValue)
        {
            return true;
        }

        // If the shared padded name column plus the chart would overflow the console, prefer keeping
        // the hierarchical step names readable and omit the chart for the whole summary.
        var timelineWidth = Math.Max(
            BuildTimelineLabels(totalTimeline, SummaryTimelineWidth).Length,
            BuildTimelineScale(SummaryTimelineWidth).Length);

        return 2 + durationWidth + 2 + 1 + 1 + nameWidth + 2 + timelineWidth <= consoleWidth;
    }

    private static string BuildTimelineBar(StepDurationRecord record, TimeSpan totalTimeline, int width)
    {
        if (width <= 0)
        {
            return "││";
        }

        var chars = Enumerable.Repeat(' ', width).ToArray();
        var start = record.StartOffset;
        var end = record.EndOffset > start ? record.EndOffset : start + record.Duration;
        double startPosition;
        double endPosition;

        if (totalTimeline <= TimeSpan.Zero)
        {
            startPosition = 0;
            endPosition = 0;
        }
        else
        {
            startPosition = start.TotalMilliseconds / totalTimeline.TotalMilliseconds * (width - 1);
            endPosition = end.TotalMilliseconds / totalTimeline.TotalMilliseconds * (width - 1);
        }

        // When a span is smaller than a single character cell it would disappear if we only rendered bar caps.
        // Show a point marker instead so very short durations remain visible in the summary.
        if (endPosition - startPosition < 1)
        {
            var pointIndex = Math.Clamp((int)Math.Round((startPosition + endPosition) / 2, MidpointRounding.AwayFromZero), 0, width - 1);
            chars[pointIndex] = '╴';
        }
        else
        {
            var startIndex = Math.Clamp((int)Math.Floor(startPosition), 0, width - 1);
            var endIndex = Math.Clamp((int)Math.Ceiling(endPosition), startIndex, width - 1);
            chars[startIndex] = '╶';
            chars[endIndex] = '╴';

            for (var i = startIndex + 1; i < endIndex; i++)
            {
                chars[i] = '─';
            }
        }

        return $"│{new string(chars)}│";
    }

    private string ColorizeSummaryBar(string bar, ActivityState state)
    {
        var escapedBar = bar.EscapeMarkup();

        if (!_enableColor)
        {
            return escapedBar;
        }

        return $"[{GetStateColor(state)}]{escapedBar}[/]";
    }

    private void WriteCompletion(string taskKey, string symbol, string message, ActivityState state, double? seconds)
    {
        var text = seconds.HasValue ? $"{message} ({DurationFormatter.FormatDuration(TimeSpan.FromSeconds(seconds.Value), CultureInfo.InvariantCulture, DecimalDurationDisplay.Fixed)})" : message;
        WriteLine(taskKey, symbol, text, state);
    }

    private void WriteLine(string taskKey, string symbol, string message, ActivityState state, bool dim = false)
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

                // Apply dim formatting per-line so that [dim]...[/] tags don't span across
                // split lines or conflict with Spectre markup already present in the message.
                if (dim && _enableColor)
                {
                    highlightedLine = $"[dim]{highlightedLine}[/]";
                }

                var markup = new StringBuilder();
                markup.Append("[dim]").Append(time).Append("[/] ");
                markup.Append('[').Append(stepColor).Append(']').Append('(').Append(displayKey).Append(")[/] ");
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
                var markupString = markup.ToString();
                try
                {
                    _console.MarkupLine(markupString);
                }
                catch (InvalidOperationException ex)
                {
                    throw new InvalidOperationException(
                        $"Spectre markup rendering failed for line: \"{markupString}\". Original message: \"{line}\"", ex);
                }
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

    private static string GetStateColor(ActivityState state) => state switch
    {
        ActivityState.Success => "green",
        ActivityState.Warning => "yellow",
        ActivityState.Failure => "red",
        ActivityState.Info => "blue",
        _ => "cyan"
    };

    private static string ColorizeSymbol(string symbol, ActivityState state) =>
        $"[{GetStateColor(state)}]{symbol}[/]";

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
