// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dogfooder.State;
using Hex1b;
using Hex1b.Widgets;

namespace Aspire.Dogfooder.Screens.Workspace;

/// <summary>
/// Builds the footer status bar — a simple key/value <c>InfoBar</c> showing
/// the last status message and the number of session windows currently open.
/// </summary>
internal static class StatusBarBuilder
{
    public static Hex1bWidget Build(
        WidgetContext<VStackWidget> ctx,
        AppState state,
        SessionWindowRegistry windowRegistry)
    {
        return ctx.InfoBar(new[]
        {
            "Status", state.StatusMessage,
            "Sessions", state.Sessions.Sessions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "Windows", $"{windowRegistry.OpenCount} open",
        });
    }
}
