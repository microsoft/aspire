// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
    public static Hex1bWidget Build(
        WindowContentContext<Hex1bWidget> ctx,
        DogfoodSession session,
        AppState state,
        IDogfoodSessionPreparer preparer)
    {
        return ctx.VStack(v =>
        [
            v.Text(""),
            v.Text("  Configure dogfooding identity overrides:"),
            v.Text(""),
            v.Form(f =>
            [
                f.TextField("Name")
                    .InitialValue(session.Name)
                    // OnTextChanged mutates the session in place. We do NOT
                    // notify on every keystroke — Hex1b drives the text field
                    // re-render itself, and we don't want to thrash the rest
                    // of the workspace on every character.
                    .OnTextChanged(e => session.Name = e.NewText),
                f.TextField("Channel (stable | staging | daily | pr-<N> | local)")
                    .InitialValue(ChannelToString(session.Config))
                    .OnTextChanged(e => session.Config = ParseChannel(e.NewText, session.Config)),
                f.TextField("ASPIRE_CLI_VERSION (optional)")
                    .InitialValue(session.Config.VersionOverride ?? "")
                    .OnTextChanged(e => session.Config = session.Config with
                    {
                        VersionOverride = NullIfEmpty(e.NewText),
                    }),
                f.TextField("ASPIRE_CLI_COMMIT (optional)")
                    .InitialValue(session.Config.CommitOverride ?? "")
                    .OnTextChanged(e => session.Config = session.Config with
                    {
                        CommitOverride = NullIfEmpty(e.NewText),
                    }),
                f.TextField("ASPIRE_CLI_NUGET_SERVICE_INDEX (optional)")
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

    private static string ChannelToString(DogfoodSessionConfig config) =>
        config.Channel switch
        {
            ChannelKind.Pr when config.PrNumber is int n => $"pr-{n}",
            _ => config.Channel.ToString().ToLowerInvariant(),
        };

    private static DogfoodSessionConfig ParseChannel(string text, DogfoodSessionConfig previous)
    {
        var trimmed = (text ?? "").Trim();

        // `pr-<number>` is the only multi-token channel literal we accept.
        // Anything else maps directly to a ChannelKind name (case-insensitive)
        // and falls back to the previous value when unrecognised so a
        // mid-typing keystroke doesn't clobber the user's intent with Local.
        if (trimmed.StartsWith("pr-", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(trimmed.AsSpan(3), out var pr))
        {
            return previous with { Channel = ChannelKind.Pr, PrNumber = pr };
        }

        return trimmed.ToLowerInvariant() switch
        {
            "stable" => previous with { Channel = ChannelKind.Stable, PrNumber = null },
            "staging" => previous with { Channel = ChannelKind.Staging, PrNumber = null },
            "daily" => previous with { Channel = ChannelKind.Daily, PrNumber = null },
            "local" => previous with { Channel = ChannelKind.Local, PrNumber = null },
            _ => previous,
        };
    }

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
