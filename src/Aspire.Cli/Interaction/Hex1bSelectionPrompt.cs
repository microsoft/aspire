// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;
using Hex1b;
using Hex1b.Composition;
using Hex1b.Flow;
using Hex1b.Input;
using Hex1b.Theming;
using Hex1b.Widgets;

namespace Aspire.Cli.Interaction;

/// <summary>
/// Spike replacement for Spectre.Console's <c>SelectionPrompt&lt;T&gt;.EnableSearch()</c>.
/// Renders a single-line text input over a <see cref="ListWidget"/> whose visible items
/// are filtered (not just highlighted) by the typed text using the same fuzzy scorer
/// that <see cref="Commands.IntegrationPackageSearchService"/> uses when an integration
/// argument is supplied on the command line. The first time the user types, the list
/// collapses to the matches in score-descending order; clearing the box restores the
/// original order. Enter activates the highlighted item; Ctrl+C cancels.
/// </summary>
internal static class Hex1bSelectionPrompt
{
    // Same threshold as Commands.IntegrationPackageSearchService.FuzzyMatchThreshold.
    // Keeping the value local avoids exposing that internal constant just for the spike.
    private const double FuzzyMatchThreshold = 0.3;

    // Page-size analogue of Spectre's SelectionPrompt.PageSize(10): cap the visible
    // list height so very long catalogues (the integration list is hundreds of items)
    // don't push the rest of the screen off when nothing is filtered yet.
    private const int MaxVisibleRows = 10;

    public static async Task<T> RunAsync<T>(
        string promptText,
        IReadOnlyList<T> choices,
        Func<T, string> choiceFormatter,
        CancellationToken cancellationToken) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(promptText);
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentNullException.ThrowIfNull(choiceFormatter);

        if (choices.Count == 0)
        {
            // Caller is expected to guard against this, but mirror Spectre's behaviour
            // so we don't render an empty prompt that can never complete.
            throw new InvalidOperationException("Hex1bSelectionPrompt requires at least one choice.");
        }

        // Pre-compute the formatted, markup-stripped string for each choice once.
        // The displayed label keeps any Spectre markup (the surrounding console still
        // renders Spectre markup elsewhere — preserving it keeps visual parity with
        // the rest of the CLI output). The score key is the stripped form so e.g.
        // "[bold]Redis[/] (Aspire.Hosting.Redis)" scores naturally on "redis".
        var rows = new ChoiceRow[choices.Count];
        for (var i = 0; i < choices.Count; i++)
        {
            var label = choiceFormatter(choices[i]);
            rows[i] = new ChoiceRow(i, label, StripMarkup(label));
        }

        var result = new ResultBox<T>();
        var cancelled = false;

        var cursorRow = SafeGetCursorRow();

        // Note: not `await using` — disposing the terminal emits the alternate-screen
        // exit sequence which would clear the inline prompt area. Letting it go out
        // of scope keeps any post-prompt scrollback (the echoSelected line) sitting
        // naturally beneath the completed step. This mirrors the pattern used in
        // FancyFlowDemo's orchestrator.
        var terminal = Hex1bTerminal.CreateBuilder()
            .WithScrollback()
            .WithHex1bFlow(async flow =>
            {
                var step = flow.Step(
                    flowCtx =>
                    {
                        // Capture the live FlowStep handle so the body widget's
                        // event handlers (text-changed, item-activated, ctrl+C)
                        // can invalidate/complete without depending on a separate
                        // context. The body widget itself is rebuilt every render
                        // pass; we want one stable Step reference threaded through.
                        return new SelectionPromptBody<T>(
                            promptText,
                            choices,
                            rows,
                            flowCtx.Step,
                            OnChosen: chosen => result.Set(chosen),
                            OnCancelled: () => cancelled = true);
                    },
                    opts => opts.MaxHeight = MaxVisibleRows + 4);
                await step.WaitForCompletionAsync().ConfigureAwait(false);
            },
            options =>
            {
                options.InitialCursorRow = cursorRow;
                options.UseSoftWrapTombstones = true;
            })
            .Build();

        await terminal.RunAsync(cancellationToken).ConfigureAwait(false);

        if (cancelled)
        {
            throw new OperationCanceledException(cancellationToken.IsCancellationRequested ? cancellationToken : CancellationToken.None);
        }

        if (!result.HasValue)
        {
            // Defensive: the flow exited without a selection and without our cancel
            // path. Treat it as a cancellation rather than returning default(T).
            throw new OperationCanceledException();
        }

        return result.Value;
    }

    private static Hex1bWidget BuildPromptBody<T>(
        CompositionContext ctx,
        string promptText,
        IReadOnlyList<T> choices,
        IReadOnlyList<ChoiceRow> rows,
        FlowStep step,
        Action<T> onChosen,
        Action onCancelled) where T : notnull
    {
        var state = ctx.UseState(() => new FilterState());

        // Recompute the visible-row → original-choice index map every render. Cheap
        // for the integration list (hundreds of items) and keeps the widget purely
        // a function of FilterState.CurrentText, which is the invariant we want.
        var visibleIndices = ComputeVisibleIndices(rows, state.CurrentText);
        var labels = new string[visibleIndices.Count];
        for (var i = 0; i < visibleIndices.Count; i++)
        {
            labels[i] = rows[visibleIndices[i]].DisplayLabel;
        }

        var textBox = ctx.TextBox(state.CurrentText)
            .OnTextChanged(e =>
            {
                state.CurrentText = e.NewText;
                step.Invalidate();
            })
            .OnSubmit(_ =>
            {
                // Enter from the textbox always selects the current top match —
                // mirrors Spectre.Console's SelectionPrompt where typing then Enter
                // commits without first tabbing into the list. With no matches we
                // do nothing so the user can correct the filter in place.
                var visible = ComputeVisibleIndices(rows, state.CurrentText);
                if (visible.Count == 0)
                {
                    return;
                }
                onChosen(choices[visible[0]]);
                step.Complete();
            })
            .FillWidth();

        Hex1bWidget body = visibleIndices.Count == 0
            ? ctx.ThemePanel(
                t => t.Set(GlobalTheme.ForegroundColor, Hex1bColor.FromRgb(140, 130, 160)),
                ctx.Text($"No matches for '{state.CurrentText}'."))
            : ctx.List(labels)
                .OnItemActivated(e =>
                {
                    var originalIndex = visibleIndices[e.ActivatedIndex];
                    onChosen(choices[originalIndex]);
                    step.Complete();
                })
                .FixedHeight(Math.Min(labels.Length, MaxVisibleRows) + 1);

        var content = ctx.VStack(v =>
        [
            v.Text(promptText),
            textBox,
            body,
            v.Text("(type to filter, ↑/↓ to move, Enter to select, Ctrl+C to cancel)"),
        ]);

        return content.InputBindings(b =>
        {
            b.Ctrl().Key(Hex1bKey.C).Action(_ =>
            {
                onCancelled();
                step.Complete();
            }, "Cancel");
        });
    }

    /// <summary>
    /// Returns the original-choice indices that survive the filter, in display order:
    /// empty filter ⇒ original order; non-empty ⇒ score-descending then original-order
    /// tie-break, with a minimum score equal to <see cref="FuzzyMatchThreshold"/> (the
    /// same threshold used by <c>IntegrationPackageSearchService</c>).
    /// </summary>
    private static IReadOnlyList<int> ComputeVisibleIndices(IReadOnlyList<ChoiceRow> rows, string filterText)
    {
        if (string.IsNullOrEmpty(filterText))
        {
            var all = new int[rows.Count];
            for (var i = 0; i < rows.Count; i++)
            {
                all[i] = i;
            }
            return all;
        }

        var scored = new List<(int Index, double Score)>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var score = StringUtils.CalculateFuzzyScore(filterText, rows[i].ScoreKey);
            if (score > FuzzyMatchThreshold)
            {
                scored.Add((i, score));
            }
        }

        // Stable sort by score descending; List<T>.Sort is not stable so use OrderBy.
        return scored
            .OrderByDescending(p => p.Score)
            .ThenBy(p => p.Index)
            .Select(p => p.Index)
            .ToArray();
    }

    /// <summary>
    /// Stripped form used for fuzzy scoring only — the displayed label keeps its
    /// Spectre markup so the visible prompt looks identical to today's UX.
    /// </summary>
    private static string StripMarkup(string label) => label.RemoveSpectreFormatting();

    private static int SafeGetCursorRow()
    {
        try
        {
            return Console.GetCursorPosition().Top;
        }
        catch
        {
            // Some environments (redirected stdout, certain CI shells) throw when
            // querying cursor position. The flow runner handles a missing initial
            // row gracefully; returning 0 keeps the spike resilient there.
            return 0;
        }
    }

    private sealed class FilterState
    {
        public string CurrentText { get; set; } = string.Empty;
    }

    /// <summary>
    /// Composite widget that owns the filter state and rebuilds the textbox + list
    /// on every keystroke. Lives as a <see cref="Hex1bWidget"/> rather than a static
    /// builder so we can reach <see cref="CompositionContext.UseState{T}"/> — the
    /// outer flow step builder only receives a <see cref="FlowStepContext"/> which
    /// is a <see cref="RootContext"/> and doesn't expose state hooks.
    /// </summary>
    private sealed record SelectionPromptBody<T>(
        string PromptText,
        IReadOnlyList<T> Choices,
        IReadOnlyList<ChoiceRow> Rows,
        FlowStep Step,
        Action<T> OnChosen,
        Action OnCancelled) : Hex1bWidget where T : notnull
    {
        protected override Hex1bWidget Build(CompositionContext ctx) =>
            BuildPromptBody(ctx, PromptText, Choices, Rows, Step, OnChosen, OnCancelled);
    }

    private sealed class ResultBox<T>
    {
        public T Value { get; private set; } = default!;
        public bool HasValue { get; private set; }

        public void Set(T value)
        {
            Value = value;
            HasValue = true;
        }
    }

    private readonly record struct ChoiceRow(int OriginalIndex, string DisplayLabel, string ScoreKey);
}
