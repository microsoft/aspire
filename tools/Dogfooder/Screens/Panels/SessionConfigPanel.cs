// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dogfooder.Services;
using Aspire.Dogfooder.State;
using Hex1b;
using Hex1b.Widgets;

namespace Aspire.Dogfooder.Screens.Panels;

/// <summary>
/// Right-pane editor for a draft <see cref="DogfoodSessionConfig"/>. All
/// mutations go through <see cref="AppState.UpdateDraftConfig"/> so the form
/// is a pure render of state — no field-level mutable hooks live here.
/// </summary>
internal static class SessionConfigPanel
{
    public static Hex1bWidget Build(
        WidgetContext<VStackWidget> ctx,
        AppState state,
        IDogfoodSessionPreparer preparer,
        IPrCatalog prCatalog)
    {
        _ = prCatalog; // Phase 5 will use this to render a PR picker.

        return ctx.Border(b =>
        [
            b.Text($"Configure session: {state.DraftSessionName}"),
            b.Separator(),
            b.Form(f =>
            [
                f.TextField("Name")
                    .InitialValue(state.DraftSessionName)
                    .OnTextChanged(e => state.UpdateDraftName(e.NewText)),
                f.TextField("Channel (stable | staging | daily | pr-<N> | local)")
                    .InitialValue(ChannelToString(state.DraftConfig)),
                f.TextField("ASPIRE_CLI_VERSION (optional)")
                    .InitialValue(state.DraftConfig.VersionOverride ?? ""),
                f.TextField("ASPIRE_CLI_COMMIT (optional)")
                    .InitialValue(state.DraftConfig.CommitOverride ?? ""),
                f.TextField("ASPIRE_CLI_NUGET_SERVICE_INDEX (optional)")
                    .InitialValue(state.DraftConfig.NuGetServiceIndexOverride ?? ""),
            ]),
            b.Separator(),
            b.Button("Create →").OnClick(_ =>
            {
                // For Phase 1 we honor whatever is currently in DraftConfig
                // (channel text-edits aren't yet wired into the state — that
                // arrives with the richer picker widget in a follow-up); the
                // user can still drive end-to-end by editing DraftConfig from
                // a future automation hook.
                var session = state.Sessions.Add(state.DraftSessionName, state.DraftConfig);
                session.Plan = preparer.BuildPlan(session.Config);
                state.SwitchToTerminal(session);
            }),
        ]);
    }

    private static string ChannelToString(DogfoodSessionConfig config) =>
        config.Channel switch
        {
            ChannelKind.Pr when config.PrNumber is int n => $"pr-{n}",
            _ => config.Channel.ToString().ToLowerInvariant(),
        };
}
