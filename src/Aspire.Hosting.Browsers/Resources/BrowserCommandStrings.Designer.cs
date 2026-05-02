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
    internal static string InspectBrowserDescription => GetString(nameof(InspectBrowserDescription));
    internal static string InspectBrowserName => GetString(nameof(InspectBrowserName));
    internal static string InspectBrowserSucceeded => GetString(nameof(InspectBrowserSucceeded));
    internal static string NavigateBrowserDescription => GetString(nameof(NavigateBrowserDescription));
    internal static string NavigateBrowserName => GetString(nameof(NavigateBrowserName));
    internal static string NavigateBrowserSucceeded => GetString(nameof(NavigateBrowserSucceeded));
    internal static string ClickBrowserDescription => GetString(nameof(ClickBrowserDescription));
    internal static string ClickBrowserName => GetString(nameof(ClickBrowserName));
    internal static string ClickBrowserSucceeded => GetString(nameof(ClickBrowserSucceeded));
    internal static string FillBrowserDescription => GetString(nameof(FillBrowserDescription));
    internal static string FillBrowserName => GetString(nameof(FillBrowserName));
    internal static string FillBrowserSucceeded => GetString(nameof(FillBrowserSucceeded));
    internal static string PressBrowserKeyDescription => GetString(nameof(PressBrowserKeyDescription));
    internal static string PressBrowserKeyName => GetString(nameof(PressBrowserKeyName));
    internal static string PressBrowserKeySucceeded => GetString(nameof(PressBrowserKeySucceeded));
    internal static string SelectBrowserOptionDescription => GetString(nameof(SelectBrowserOptionDescription));
    internal static string SelectBrowserOptionName => GetString(nameof(SelectBrowserOptionName));
    internal static string SelectBrowserOptionSucceeded => GetString(nameof(SelectBrowserOptionSucceeded));
    internal static string WaitForBrowserDescription => GetString(nameof(WaitForBrowserDescription));
    internal static string WaitForBrowserName => GetString(nameof(WaitForBrowserName));
    internal static string WaitForBrowserSucceeded => GetString(nameof(WaitForBrowserSucceeded));
    internal static string CloseTrackedBrowserDescription => GetString(nameof(CloseTrackedBrowserDescription));
    internal static string CloseTrackedBrowserName => GetString(nameof(CloseTrackedBrowserName));
    internal static string CloseTrackedBrowserSucceeded => GetString(nameof(CloseTrackedBrowserSucceeded));
    internal static string SelectorArgumentLabel => GetString(nameof(SelectorArgumentLabel));
    internal static string SelectorArgumentDescription => GetString(nameof(SelectorArgumentDescription));
    internal static string ValueArgumentLabel => GetString(nameof(ValueArgumentLabel));
    internal static string ValueArgumentDescription => GetString(nameof(ValueArgumentDescription));
    internal static string SelectValueArgumentDescription => GetString(nameof(SelectValueArgumentDescription));
    internal static string KeyArgumentLabel => GetString(nameof(KeyArgumentLabel));
    internal static string KeyArgumentDescription => GetString(nameof(KeyArgumentDescription));
    internal static string TextArgumentLabel => GetString(nameof(TextArgumentLabel));
    internal static string TextArgumentDescription => GetString(nameof(TextArgumentDescription));
    internal static string UrlArgumentLabel => GetString(nameof(UrlArgumentLabel));
    internal static string UrlArgumentDescription => GetString(nameof(UrlArgumentDescription));
    internal static string TimeoutMillisecondsArgumentLabel => GetString(nameof(TimeoutMillisecondsArgumentLabel));
    internal static string TimeoutMillisecondsArgumentDescription => GetString(nameof(TimeoutMillisecondsArgumentDescription));
    internal static string MaxElementsArgumentLabel => GetString(nameof(MaxElementsArgumentLabel));
    internal static string MaxElementsArgumentDescription => GetString(nameof(MaxElementsArgumentDescription));
    internal static string MaxTextLengthArgumentLabel => GetString(nameof(MaxTextLengthArgumentLabel));
    internal static string MaxTextLengthArgumentDescription => GetString(nameof(MaxTextLengthArgumentDescription));

    private static string GetString(string name) => s_resourceManager.GetString(name, Culture)!;
}
