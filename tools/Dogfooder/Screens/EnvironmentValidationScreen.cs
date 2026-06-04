// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dogfooder.State;
using Hex1b;
using Hex1b.Widgets;

namespace Aspire.Dogfooder.Screens;

/// <summary>
/// Builder for the environment-validation screen. Pure rendering of
/// <see cref="EnvironmentValidationState"/> plus a "Continue" button that
/// transitions the app to the main screen once every probe is green.
/// </summary>
internal static class EnvironmentValidationScreen
{
    public static Hex1bWidget Build(RootContext ctx, AppState state)
    {
        var v = state.Validation;

        return ctx.Border(b =>
        [
            b.Text("Aspire Dogfooder — environment validation"),
            b.Separator(),
            b.Text(FormatProbe(v.DotnetProbe)),
            b.Text(FormatProbe(v.GhAuthProbe)),
            b.Text(FormatProbe(v.GhTokenProbe)),
            b.Text(FormatProbe(v.LocalCliProbe)),
            b.Separator(),
            v.AllProbesOk
                ? b.Button("Continue →").OnClick(_ => state.EnterMainScreen())
                : b.Text("Resolve the failures above to continue."),
        ]);
    }

    private static string FormatProbe(EnvironmentProbeResult result)
    {
        var marker = result.Status switch
        {
            EnvironmentProbeStatus.Ok => "[ OK ]",
            EnvironmentProbeStatus.Failed => "[FAIL]",
            EnvironmentProbeStatus.Running => "[ .. ]",
            _ => "[    ]",
        };
        return $"{marker} {result.Name,-12}  {result.Detail}";
    }
}
