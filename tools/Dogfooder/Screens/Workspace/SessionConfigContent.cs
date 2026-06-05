// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dogfooder.Scenarios;
using Aspire.Dogfooder.State;
using Hex1b;
using Hex1b.Widgets;

namespace Aspire.Dogfooder.Screens.Workspace;

/// <summary>
/// Body content for the single-session screen when no plan has been
/// committed yet: a scenario picker plus the dynamic per-scenario input
/// fields. The <c>onContinue</c> callback is the screen's hook to transition
/// the app state and kick off the preparer.
/// </summary>
internal static class SessionConfigContent
{
    public static Hex1bWidget Build<TParent>(
        WidgetContext<TParent> ctx,
        DogfoodSession session,
        AppState state,
        DogfoodScenarioRegistry scenarioRegistry,
        Action onContinue)
        where TParent : Hex1bWidget
    {
        var scenarios = scenarioRegistry.Scenarios;
        var currentScenario = scenarioRegistry.GetOrDefault(session.Config.ScenarioId);
        var currentIndex = IndexOf(scenarios, currentScenario.Id);

        return ctx.VStack(v =>
        {
            var fields = new List<Hex1bWidget>
            {
                v.Text(""),
                v.Text("  Pick a scenario, fill in any required inputs, and press Continue."),
                v.Text(""),
            };

            fields.Add(v.Form(form =>
            {
                var rows = new List<Hex1bWidget>
                {
                    form.Text("  Scenario:"),
                };
                for (var i = 0; i < scenarios.Count; i++)
                {
                    var scenario = scenarios[i];
                    var selected = i == currentIndex;
                    var prefix = selected ? "  (*) " : "  ( ) ";
                    rows.Add(form.HStack(h =>
                    [
                        h.Button($"{prefix}{scenario.DisplayName}")
                            .OnClick(_ =>
                            {
                                // Reset inputs when the scenario changes —
                                // input keys are per-scenario, so a stale
                                // PR number from a prior selection would be
                                // dead weight.
                                session.Config = new DogfoodSessionConfig(
                                    scenario.Id,
                                    new Dictionary<string, string?>(StringComparer.Ordinal));
                                state.Notifier.Notify();
                            }),
                    ]));
                }

                rows.Add(form.Text(""));
                rows.Add(form.Text("  " + currentScenario.Description));

                if (currentScenario.Inputs.Count > 0)
                {
                    rows.Add(form.Text(""));
                    rows.Add(form.Text("  Inputs:"));
                    foreach (var spec in currentScenario.Inputs)
                    {
                        var capturedKey = spec.Key;
                        var existing = session.Config.Inputs.TryGetValue(capturedKey, out var v0) ? v0 ?? "" : "";
                        var label = spec.Required ? $"{spec.Label} (required)" : spec.Label;
                        // FormTextFieldWidget has no placeholder API; bake the
                        // placeholder hint into the label so it's still visible.
                        if (!string.IsNullOrEmpty(spec.Placeholder))
                        {
                            label = $"{label} [{spec.Placeholder}]";
                        }
                        rows.Add(form.TextField(label)
                            .InitialValue(existing)
                            .OnTextChanged(e =>
                            {
                                // Copy-and-swap: builds a fresh dictionary so
                                // the record's Inputs reference is stable
                                // per-render.
                                var next = new Dictionary<string, string?>(session.Config.Inputs, StringComparer.Ordinal)
                                {
                                    [capturedKey] = string.IsNullOrEmpty(e.NewText) ? null : e.NewText,
                                };
                                session.Config = session.Config with { Inputs = next };
                            }));
                    }
                }

                return rows.ToArray();
            }));

            fields.Add(v.Text(""));
            fields.Add(v.HStack(h =>
            [
                h.Text("  "),
                h.Button("Continue →").OnClick(_ => onContinue()),
            ]));

            return fields.ToArray();
        });
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
