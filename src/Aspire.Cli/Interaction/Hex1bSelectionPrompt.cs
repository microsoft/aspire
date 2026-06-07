// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;
using Hex1b;
using Hex1b.Composition;
using Hex1b.Flow;
using Hex1b.Input;
using Hex1b.Widgets;

namespace Aspire.Cli.Interaction;

/// <summary>
/// Hex1b-backed replacement for Spectre.Console's
/// <c>SelectionPrompt&lt;T&gt;.EnableSearch()</c>. Renders a single-line text input
/// over a <see cref="ListWidget{T}"/> whose visible items are filtered (not just
/// highlighted) by the typed text using the same fuzzy scorer that
/// <see cref="Commands.IntegrationPackageSearchService"/> uses when an integration
/// argument is supplied on the command line. The first time the user types, the
/// list collapses to the matches in score-descending order; clearing the box
/// restores the original order. Enter activates the highlighted item; Ctrl+C
/// cancels.
/// </summary>
internal static class Hex1bSelectionPrompt
{
    // Same threshold as Commands.IntegrationPackageSearchService.FuzzyMatchThreshold.
    // Keeping the value local avoids exposing that internal constant just for the
    // prompt.
    private const double FuzzyMatchThreshold = 0.3;

    // Page-size analogue of Spectre's SelectionPrompt.PageSize(10): the list pane
    // is sized to this many rows so the prompt frame stays a constant height even
    // as the filter narrows the visible list.
    private const int VisibleRows = 10;

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
            // Caller is expected to guard against this — mirror Spectre's behaviour
            // so we never render an empty prompt that can't be completed.
            throw new InvalidOperationException("Hex1bSelectionPrompt requires at least one choice.");
        }

        // Pre-compute a (value, display) pair per choice once. The display text
        // is the formatter output with any Spectre markup ([bold]…[/]) stripped:
        // Hex1b's text widget renders bracketed runs literally so we need a clean
        // string for the visible row, and using the same string as the fuzzy
        // score key keeps "what you see is what you score" semantics.
        var allChoices = new Choice<T>[choices.Count];
        for (var i = 0; i < choices.Count; i++)
        {
            allChoices[i] = new Choice<T>(choices[i], choiceFormatter(choices[i]).RemoveSpectreFormatting());
        }

        var result = new ResultBox<T>();
        var cancelled = false;

        var cursorRow = SafeGetCursorRow();

        // Note: not `await using` — disposing the terminal emits the alternate-screen
        // exit sequence which would clear the inline prompt area. Letting it go out
        // of scope keeps any post-prompt scrollback (the echoSelected line) sitting
        // naturally beneath the completed step. This mirrors the FancyFlowDemo
        // orchestrator pattern.
        var terminal = Hex1bTerminal.CreateBuilder()
            .WithScrollback()
            .WithHex1bFlow(async flow =>
            {
                var step = flow.Step(
                    flowCtx => new SelectionPromptBody<T>(
                        promptText,
                        allChoices,
                        // Capture the live FlowStep handle here so the body widget's
                        // event handlers can invalidate / complete without us having
                        // to thread FlowStepContext through every layer (the body
                        // widget is rebuilt on every render pass; the Step is stable).
                        flowCtx.Step,
                        OnChosen: chosen => result.Set(chosen),
                        OnCancelled: () => cancelled = true),
                    opts => opts.MaxHeight = VisibleRows + 4);
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
        IReadOnlyList<Choice<T>> allChoices,
        FlowStep step,
        Action<T> onChosen,
        Action onCancelled) where T : notnull
    {
        var state = ctx.UseState(() => new FilterState());

        // Recomputed every render — cheap for a few hundred items and keeps the
        // widget a pure function of state.CurrentText.
        var visible = FilterChoices(allChoices, state.CurrentText);

        var textBox = ctx.TextBox(state.CurrentText)
            .OnTextChanged(e =>
            {
                state.CurrentText = e.NewText;
                step.Invalidate();
            })
            .OnSubmit(_ =>
            {
                // Enter from the textbox commits the current top match — mirrors
                // Spectre's SelectionPrompt where typing then Enter selects without
                // first tabbing into the list. With no matches we do nothing so the
                // user can correct the filter in place.
                var current = FilterChoices(allChoices, state.CurrentText);
                if (current.Count == 0)
                {
                    return;
                }
                onChosen(current[0].Value);
                step.Complete();
            })
            .FillWidth();

        // Typed ListWidget<Choice<T>>: ItemKey gives each row stable identity so
        // the cursor follows the same logical item as the filter narrows or
        // expands; ItemTemplate owns the per-row chrome (focus marker) and the
        // Empty builder handles the no-matches state without a sibling
        // conditional panel.
        var list = ctx.List(visible)
            .ItemKey(c => c.Display)
            .ItemTemplate(itemCtx =>
            {
                var marker = itemCtx.IsFocused ? "▸ " : "  ";
                return itemCtx.Text(marker + itemCtx.Item.Display);
            })
            .OnItemActivated(e =>
            {
                onChosen(e.ActivatedItem.Value);
                step.Complete();
            })
            .Empty(emptyCtx => emptyCtx.Text($"  No matches for '{state.CurrentText}'."))
            .FixedHeight(VisibleRows);

        var content = ctx.VStack(v =>
        [
            v.Text(promptText),
            textBox,
            list,
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
    /// Returns the choices that survive the filter, in display order:
    /// empty filter ⇒ original order; non-empty ⇒ score-descending then
    /// original-order tie-break, with a minimum score equal to
    /// <see cref="FuzzyMatchThreshold"/> (the same threshold used by
    /// <c>IntegrationPackageSearchService</c>).
    /// </summary>
    private static IReadOnlyList<Choice<T>> FilterChoices<T>(IReadOnlyList<Choice<T>> choices, string filterText)
        where T : notnull
    {
        if (string.IsNullOrEmpty(filterText))
        {
            return choices;
        }

        var scored = new List<(int Index, Choice<T> Choice, double Score)>(choices.Count);
        for (var i = 0; i < choices.Count; i++)
        {
            var score = StringUtils.CalculateFuzzyScore(filterText, choices[i].Display);
            if (score > FuzzyMatchThreshold)
            {
                scored.Add((i, choices[i], score));
            }
        }

        // List<T>.Sort is not stable, so OrderBy is used to preserve the original
        // order for score ties (matches the behaviour of GetIntegrationSearchMatches).
        return scored
            .OrderByDescending(p => p.Score)
            .ThenBy(p => p.Index)
            .Select(p => p.Choice)
            .ToArray();
    }

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
            // row gracefully; returning 0 keeps the prompt resilient there.
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
        IReadOnlyList<Choice<T>> AllChoices,
        FlowStep Step,
        Action<T> OnChosen,
        Action OnCancelled) : Hex1bWidget where T : notnull
    {
        protected override Hex1bWidget Build(CompositionContext ctx) =>
            BuildPromptBody(ctx, PromptText, AllChoices, Step, OnChosen, OnCancelled);
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

    /// <summary>
    /// Bound to the typed <see cref="ListWidget{T}"/> as its item type so the
    /// widget carries the original choice value alongside its pre-stripped
    /// display string — no index-map indirection on activation.
    /// </summary>
    private readonly record struct Choice<T>(T Value, string Display) where T : notnull;
}
