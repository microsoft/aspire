// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model.Markdown;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Microsoft.FluentUI.AspNetCore.Components;

using DialogsLoc = Aspire.Dashboard.Resources.Dialogs;

namespace Aspire.Dashboard.Components.Dialogs;

public partial class AIAgentsDialog : IDialogContentComponent
{
    private MarkdownProcessor? _markdownProcessor;

    [Inject]
    public required IStringLocalizer<DialogsLoc> Loc { get; init; }

    [Inject]
    public required IStringLocalizer<Resources.ControlsStrings> ControlsStringsLoc { get; init; }

    [Inject]
    public required IDashboardClient DashboardClient { get; init; }

    [Inject]
    public required IOptionsMonitor<DashboardOptions> Options { get; init; }

    private const string AppHostLearnMoreUrl = "https://aspire.dev/get-started/ai-coding-agents/";
    private const string StandaloneLearnMoreUrl = "https://aspire.dev/dashboard/ai-coding-agents/";

    private string Description => DashboardClient.IsEnabled
        ? string.Format(CultureInfo.CurrentCulture, Loc[nameof(DialogsLoc.AIAgentsDialogAppHostDescription)], AppHostLearnMoreUrl)
        : string.Format(CultureInfo.CurrentCulture, Loc[nameof(DialogsLoc.AIAgentsDialogStandaloneDescription)], GetDashboardUrl(), StandaloneLearnMoreUrl);

    private string GetDashboardUrl()
    {
        var options = Options.CurrentValue;
        var endpointAddresses = options.Frontend.GetEndpointAddresses();
        var baseUrl = options.Frontend.PublicUrl
            ?? (endpointAddresses.Count > 0 ? endpointAddresses[0].ToString() : null);

        if (baseUrl is null)
        {
            return "http://localhost:18888";
        }

        // Trim trailing slash for consistent URL building.
        baseUrl = baseUrl.TrimEnd('/');

        if (options.Api.AuthMode is ApiAuthMode.ApiKey &&
            options.Frontend.AuthMode is FrontendAuthMode.BrowserToken &&
            options.Frontend.BrowserToken is { } token)
        {
            return $"{baseUrl}/login?t={token}";
        }

        return baseUrl;
    }

    private MarkdownProcessor GetMarkdownProcessor()
    {
        return _markdownProcessor ??= new MarkdownProcessor(ControlsStringsLoc, safeUrlSchemes: MarkdownHelpers.SafeUrlSchemes, extensions: []);
    }
}
