// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dogfooder.Services;
using Aspire.Dogfooder.State;
using Hex1b;
using Hex1b.Widgets;

namespace Aspire.Dogfooder.Screens.Workspace;

/// <summary>
/// Builds the window-body widget for a session in its <em>configuration</em>
/// state: a form for the channel/version/commit/NuGet-index overrides plus a
/// Continue button that commits the plan and triggers a content swap to the
/// terminal body on the next render pass.
/// </summary>
internal static class SessionConfigContent
{
    // Display order for the channel toggle. The numeric position is what
    // ToggleSwitch reports back via SelectedIndex, so this array is the
    // single source of truth for both rendering and round-tripping into a
    // ChannelKind enum value.
    private static readonly (string Label, ChannelKind Kind)[] s_channels =
    [
        ("Stable", ChannelKind.Stable),
        ("Staging", ChannelKind.Staging),
        ("Daily", ChannelKind.Daily),
        ("PR", ChannelKind.Pr),
        ("Local", ChannelKind.Local),
    ];

    public static Hex1bWidget Build(
        WindowContentContext<Hex1bWidget> ctx,
        DogfoodSession session,
        AppState state,
        IDogfoodSessionPreparer preparer)
    {
        var channelLabels = s_channels.Select(c => c.Label).ToArray();
        var initialChannelIndex = IndexOf(session.Config.Channel);

        return ctx.VStack(v =>
        [
            v.Text(""),
            v.Text("  Configure dogfooding identity overrides:"),
            v.Text(""),
            v.Form(form =>
            [
                form.TextField("Name")
                    .InitialValue(session.Name)
                    // OnTextChanged mutates the session in place. We do NOT
                    // notify on every keystroke — Hex1b drives the text field
                    // re-render itself, and we don't want to thrash the rest
                    // of the workspace on every character.
                    .OnTextChanged(e => session.Name = e.NewText),

                // Channel + PR-number row. The toggle owns the channel kind;
                // the PR-number FormTextField sits beside it and is enabled
                // only when the toggle is on "PR". We use the FormContext
                // (closed over from the outer form lambda) to register the
                // PR-number field so it participates in the form's field
                // registry and EnableWhen re-evaluation pipeline; the HStack
                // is just visual grouping.
                form.HStack(h =>
                [
                    h.Text(" Channel  "),
                    h.ToggleSwitch(channelLabels, initialChannelIndex)
                        .OnSelectionChanged(e =>
                        {
                            var picked = s_channels[e.SelectedIndex].Kind;
                            // Clear PrNumber when leaving the Pr branch so a
                            // stale number from a prior selection can't leak
                            // into the env-var when the user clicks Continue
                            // without re-entering the PR field.
                            session.Config = picked == ChannelKind.Pr
                                ? session.Config with { Channel = ChannelKind.Pr }
                                : session.Config with { Channel = picked, PrNumber = null };
                            // Tickle the render loop so EnableWhen on the PR
                            // field re-evaluates and lights/dims it.
                            state.Notifier.Notify();
                        }),
                    h.Text("   PR # "),
                    form.TextField("")
                        .InitialValue(session.Config.PrNumber?.ToString(CultureInfo.InvariantCulture) ?? "")
                        .MinWidth(8)
                        .EnableWhen(() => session.Config.Channel == ChannelKind.Pr)
                        .OnTextChanged(e =>
                        {
                            session.Config = session.Config with
                            {
                                PrNumber = int.TryParse(e.NewText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                                    ? n
                                    : null,
                            };
                        }),
                ]),

                form.TextField("ASPIRE_CLI_VERSION (optional)")
                    .InitialValue(session.Config.VersionOverride ?? "")
                    .OnTextChanged(e => session.Config = session.Config with
                    {
                        VersionOverride = NullIfEmpty(e.NewText),
                    }),
                form.TextField("ASPIRE_CLI_COMMIT (optional)")
                    .InitialValue(session.Config.CommitOverride ?? "")
                    .OnTextChanged(e => session.Config = session.Config with
                    {
                        CommitOverride = NullIfEmpty(e.NewText),
                    }),
                form.TextField("ASPIRE_CLI_NUGET_SERVICE_INDEX (optional)")
                    .InitialValue(session.Config.NuGetServiceIndexOverride ?? "")
                    .OnTextChanged(e => session.Config = session.Config with
                    {
                        NuGetServiceIndexOverride = NullIfEmpty(e.NewText),
                    }),
            ]),
            v.Text(""),
            v.HStack(h =>
            [
                h.Text("  "),
                h.Button("Continue →").OnClick(_ =>
                {
                    // Stamping the Plan is the trigger for the window's
                    // content lambda to switch from this form to the
                    // terminal body. The Notifier wakes Hex1b's render loop;
                    // the window then re-evaluates its content callback and
                    // picks up the new branch.
                    session.Plan = preparer.BuildPlan(session.Config);
                    state.SetStatus($"Launching terminal for '{session.Name}' …");
                }),
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

    private static int IndexOf(ChannelKind kind)
    {
        for (var i = 0; i < s_channels.Length; i++)
        {
            if (s_channels[i].Kind == kind)
            {
                return i;
            }
        }
        return 0;
    }

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
