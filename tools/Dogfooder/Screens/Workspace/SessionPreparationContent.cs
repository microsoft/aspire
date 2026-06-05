// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dogfooder.State;
using Hex1b;
using Hex1b.Widgets;

namespace Aspire.Dogfooder.Screens.Workspace;

/// <summary>
/// Window-body widget for a session in its <em>Preparing</em> state. Renders
/// a streaming view of the build log + NuGet server startup output so the
/// user can watch the multi-minute build progress rather than staring at a
/// blank window. Swapped out for <see cref="SessionTerminalContent"/> by the
/// window opener's content lambda once preparation completes.
/// </summary>
internal static class SessionPreparationContent
{
    public static Hex1bWidget Build<TParent>(
        WidgetContext<TParent> ctx,
        DogfoodSession session,
        AppState state)
        where TParent : Hex1bWidget
    {
        _ = ctx;
        _ = state;
        var prep = session.Preparation;
        if (prep is null)
        {
            return new TextBlockWidget("(preparing…)");
        }

        var phaseLabel = prep.CurrentPhase switch
        {
            SessionPreparationState.Phase.Pending => "Pending…",
            SessionPreparationState.Phase.Building => "Building packages…",
            SessionPreparationState.Phase.StartingProxy => "Starting NuGet proxy…",
            SessionPreparationState.Phase.Complete => "Complete",
            SessionPreparationState.Phase.Failed => $"FAILED: {prep.FailureReason}",
            _ => "?",
        };

        // Once the build has reached a terminal phase, hand the entire log
        // over to a read-only Editor widget so the user can scroll back,
        // select, and copy. While the build is still running we keep the
        // lightweight streaming TextBlock view — the periodic invalidation
        // loop in SessionScreen.RunPreparationAsync pushes re-renders even
        // when there's no input.
        var isTerminal = prep.CurrentPhase is SessionPreparationState.Phase.Complete or SessionPreparationState.Phase.Failed;
        if (isTerminal)
        {
            var editor = prep.GetOrCreateLogEditor();
            return ctx.VStack(v =>
            [
                v.Text(""),
                v.Text($"  Phase: {phaseLabel}  ({prep.Log.Count} lines)"),
                v.Text(""),
                v.Editor(editor).LineNumbers().Fill(),
            ]);
        }

        // We render the last ~200 log lines. Hex1b's text rendering tolerates
        // any line count, but trimming keeps the layout pass cheap on long
        // builds (an Arcade --pack can emit thousands of lines).
        var log = prep.Log;
        var visibleStart = Math.Max(0, log.Count - 200);

        return ctx.VStack(v =>
        {
            var rows = new List<Hex1bWidget>
            {
                v.Text(""),
                v.Text($"  Phase: {phaseLabel}"),
                v.Text(""),
            };
            for (var i = visibleStart; i < log.Count; i++)
            {
                rows.Add(v.Text(log[i]));
            }
            return rows.ToArray();
        });
    }
}
