// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dogfooder.Scenarios;
using Aspire.Dogfooder.Services;
using Aspire.Dogfooder.State;
using Hex1b;
using Hex1b.Widgets;

namespace Aspire.Dogfooder.Screens.Workspace;

/// <summary>
/// Builds the window-body widget for a session in its <em>configuration</em>
/// state: pick a scenario from the catalog, fill in whichever inputs that
/// scenario declares, then Continue to kick off async preparation. The
/// content lambda in <see cref="SessionWindowOpener"/> swaps to the
/// preparation log on the next render pass.
/// </summary>
internal static class SessionConfigContent
{
    public static Hex1bWidget Build(
        WindowContentContext<Hex1bWidget> ctx,
        DogfoodSession session,
        AppState state,
        IDogfoodSessionPreparer preparer,
        DogfoodingNuGetServerRegistry nuGetRegistry,
        DogfoodScenarioRegistry scenarioRegistry)
    {
        var scenarios = scenarioRegistry.Scenarios;
        var currentScenario = scenarioRegistry.GetOrDefault(session.Config.ScenarioId);
        var currentIndex = IndexOf(scenarios, currentScenario.Id);

        return ctx.VStack(v =>
        [
            v.Text(""),
            v.Text("  Pick a scenario, fill in any required inputs, and Continue."),
            v.Text(""),
            v.Form(form =>
            {
                var fields = new List<Hex1bWidget>
                {
                    form.TextField("Name")
                        .InitialValue(session.Name)
                        .OnTextChanged(e => session.Name = e.NewText),

                    // Scenario picker. We render scenarios as a vertical menu
                    // (one row per scenario) rather than a toggle because the
                    // 7+ scenarios won't all fit on a single line at common
                    // window widths.
                    form.Text(""),
                    form.Text("  Scenario:"),
                };
                for (var i = 0; i < scenarios.Count; i++)
                {
                    var scenario = scenarios[i];
                    var selected = i == currentIndex;
                    var prefix = selected ? "  (*) " : "  ( ) ";
                    fields.Add(form.HStack(h =>
                    [
                        h.Button($"{prefix}{scenario.DisplayName}")
                            .OnClick(_ =>
                            {
                                // Replace the scenario id and reset inputs —
                                // input keys are per-scenario, so a stale
                                // PR number from a prior selection would just
                                // be dead weight.
                                session.Config = new DogfoodSessionConfig(
                                    scenario.Id,
                                    new Dictionary<string, string?>(StringComparer.Ordinal));
                                state.Notifier.Notify();
                            }),
                    ]));
                }

                // Description for the selected scenario. TextBlockWidget
                // doesn't word-wrap, so the description is expected to fit
                // on a single line at typical workspace widths; longer prose
                // belongs in tooltips/help, not the form.
                fields.Add(form.Text(""));
                fields.Add(form.Text("  " + currentScenario.Description));

                // Per-scenario inputs. Each input gets a TextField keyed by
                // ScenarioInputSpec.Key; OnTextChanged writes back into the
                // session config's input dictionary as a fresh dict (the
                // record is intentionally immutable on the surface so swaps
                // are atomic).
                if (currentScenario.Inputs.Count > 0)
                {
                    fields.Add(form.Text(""));
                    fields.Add(form.Text("  Inputs:"));
                    foreach (var spec in currentScenario.Inputs)
                    {
                        var capturedKey = spec.Key;
                        var existing = session.Config.Inputs.TryGetValue(capturedKey, out var v0) ? v0 ?? "" : "";
                        var label = spec.Required ? $"{spec.Label} (required)" : spec.Label;
                        var placeholder = spec.Placeholder;
                        // FormTextFieldWidget has no placeholder API; bake
                        // the hint into the label so it's still visible.
                        if (!string.IsNullOrEmpty(placeholder))
                        {
                            label = $"{label} [{placeholder}]";
                        }
                        var field = form.TextField(label).InitialValue(existing);
                        field = field.OnTextChanged(e =>
                        {
                            // Copy-and-swap: builds a fresh dictionary so the
                            // record's `Inputs` reference is stable per-render
                            // (any consumer comparing by reference sees a
                            // change).
                            var next = new Dictionary<string, string?>(session.Config.Inputs, StringComparer.Ordinal)
                            {
                                [capturedKey] = string.IsNullOrEmpty(e.NewText) ? null : e.NewText,
                            };
                            session.Config = session.Config with { Inputs = next };
                        });
                        fields.Add(field);
                    }
                }

                return fields.ToArray();
            }),
            v.Text(""),
            v.HStack(h =>
            [
                h.Text("  "),
                h.Button("Continue →").OnClick((Action<Hex1b.Events.ButtonClickedEventArgs>)(ev =>
                {
                    // Kick off async preparation. The window's content lambda
                    // observes session.Status / session.Plan to decide which
                    // body to render on the next frame.
                    _ = ev;
                    session.Status = SessionStatus.Preparing;
                    state.SetStatus($"Preparing '{session.Name}' …");
                    _ = RunPreparationAsync(session, state, preparer, nuGetRegistry);
                })),
                h.Text("  "),
                h.Button("Cancel").OnClick(ev =>
                {
                    // Cancel discards the session entirely — equivalent to
                    // closing the window. We Close via the window handle so
                    // the OnClose callback fires and the registry/PTY are
                    // cleaned up uniformly.
                    state.Sessions.Remove(session);
                    ev.Windows.Close(ctx.Window);
                }),
            ]),
        ]);
    }

    private static async Task RunPreparationAsync(
        DogfoodSession session,
        AppState state,
        IDogfoodSessionPreparer preparer,
        DogfoodingNuGetServerRegistry nuGetRegistry)
    {
        try
        {
            var result = await preparer.PrepareAsync(session, CancellationToken.None).ConfigureAwait(false);
            if (!result.Success)
            {
                // Leave the session in Preparing with the failure message
                // visible in the preparation log so the user can act on it.
                state.SetStatus($"Preparation failed: {session.Name}");
                state.Notifier.Notify();
                return;
            }

            if (result.NuGetServer is not null)
            {
                nuGetRegistry.Register(session.Id, result.NuGetServer);
            }

            // Plan is already stamped onto the session by the preparer; clear
            // Preparing so the window swaps to the terminal body on the next
            // render pass.
            session.Status = SessionStatus.Idle;
            state.SetStatus($"Launching terminal for '{session.Name}' …");
            state.Notifier.Notify();
        }
        catch (Exception ex)
        {
            session.Preparation?.SetPhase(SessionPreparationState.Phase.Failed, ex.Message);
            state.SetStatus($"Preparation crashed: {session.Name} — {ex.Message}");
            state.Notifier.Notify();
        }
    }

    private static int IndexOf(IReadOnlyList<IDogfoodScenario> scenarios, string id)
    {
        for (var i = 0; i < scenarios.Count; i++)
        {
            if (string.Equals(scenarios[i].Id, id, StringComparison.Ordinal))
            {
                return i;
            }
        }
        return 0;
    }
}
