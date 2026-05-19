// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Resources;

#nullable enable

namespace Aspire.Hosting.Browsers.Resources;

internal static class BrowserCommandStrings
{
    private static readonly ResourceManager s_resourceManager = new("Aspire.Hosting.Browsers.Resources.BrowserCommandStrings", typeof(BrowserCommandStrings).Assembly);

    internal static CultureInfo? Culture { get; set; }

    internal static string OpenTrackedBrowserDescription => GetString(nameof(OpenTrackedBrowserDescription));
    internal static string OpenTrackedBrowserName => GetString(nameof(OpenTrackedBrowserName));
    internal static string ConfigureTrackedBrowserDescription => GetString(nameof(ConfigureTrackedBrowserDescription));
    internal static string ConfigureTrackedBrowserName => GetString(nameof(ConfigureTrackedBrowserName));
    internal static string ConfigureTrackedBrowserPromptMessage => GetString(nameof(ConfigureTrackedBrowserPromptMessage));
    internal static string ConfigureTrackedBrowserSaveButton => GetString(nameof(ConfigureTrackedBrowserSaveButton));
    internal static string ConfigureTrackedBrowserScopeLabel => GetString(nameof(ConfigureTrackedBrowserScopeLabel));
    internal static string ConfigureTrackedBrowserResourceScopeOption => GetString(nameof(ConfigureTrackedBrowserResourceScopeOption));
    internal static string ConfigureTrackedBrowserGlobalScopeOption => GetString(nameof(ConfigureTrackedBrowserGlobalScopeOption));
    internal static string ConfigureTrackedBrowserGlobalScopeResult => GetString(nameof(ConfigureTrackedBrowserGlobalScopeResult));
    internal static string ConfigureTrackedBrowserBrowserLabel => GetString(nameof(ConfigureTrackedBrowserBrowserLabel));
    internal static string ConfigureTrackedBrowserBrowserDescription => GetString(nameof(ConfigureTrackedBrowserBrowserDescription));
    internal static string ConfigureTrackedBrowserEdgeOption => GetString(nameof(ConfigureTrackedBrowserEdgeOption));
    internal static string ConfigureTrackedBrowserChromeOption => GetString(nameof(ConfigureTrackedBrowserChromeOption));
    internal static string ConfigureTrackedBrowserChromiumOption => GetString(nameof(ConfigureTrackedBrowserChromiumOption));
    internal static string ConfigureTrackedBrowserUserDataModeLabel => GetString(nameof(ConfigureTrackedBrowserUserDataModeLabel));
    internal static string ConfigureTrackedBrowserProfileLabel => GetString(nameof(ConfigureTrackedBrowserProfileLabel));
    internal static string ConfigureTrackedBrowserProfileDescription => GetString(nameof(ConfigureTrackedBrowserProfileDescription));
    internal static string ConfigureTrackedBrowserDefaultProfileOption => GetString(nameof(ConfigureTrackedBrowserDefaultProfileOption));
    internal static string ConfigureTrackedBrowserProfileOptionWithDisplayName => GetString(nameof(ConfigureTrackedBrowserProfileOptionWithDisplayName));
    internal static string ConfigureTrackedBrowserBrowserRequired => GetString(nameof(ConfigureTrackedBrowserBrowserRequired));
    internal static string ConfigureTrackedBrowserUserDataModeRequired => GetString(nameof(ConfigureTrackedBrowserUserDataModeRequired));
    internal static string ConfigureTrackedBrowserProfileRequiresShared => GetString(nameof(ConfigureTrackedBrowserProfileRequiresShared));
    internal static string ConfigureTrackedBrowserSaveToUserSecretsLabel => GetString(nameof(ConfigureTrackedBrowserSaveToUserSecretsLabel));
    internal static string ConfigureTrackedBrowserSaveToUserSecretsDescriptionConfigured => GetString(nameof(ConfigureTrackedBrowserSaveToUserSecretsDescriptionConfigured));
    internal static string ConfigureTrackedBrowserSaveToUserSecretsDescriptionNotConfigured => GetString(nameof(ConfigureTrackedBrowserSaveToUserSecretsDescriptionNotConfigured));
    internal static string ConfigureTrackedBrowserInteractionUnavailable => GetString(nameof(ConfigureTrackedBrowserInteractionUnavailable));
    internal static string ConfigureTrackedBrowserUserSecretsUnavailable => GetString(nameof(ConfigureTrackedBrowserUserSecretsUnavailable));
    internal static string ConfigureTrackedBrowserApplied => GetString(nameof(ConfigureTrackedBrowserApplied));
    internal static string ConfigureTrackedBrowserSaved => GetString(nameof(ConfigureTrackedBrowserSaved));
    internal static string ConfigureTrackedBrowserSaveFailed => GetString(nameof(ConfigureTrackedBrowserSaveFailed));
    internal static string CaptureScreenshotDescription => GetString(nameof(CaptureScreenshotDescription));
    internal static string CaptureScreenshotName => GetString(nameof(CaptureScreenshotName));

    private static string GetString(string name) => s_resourceManager.GetString(name, Culture)!;
}
