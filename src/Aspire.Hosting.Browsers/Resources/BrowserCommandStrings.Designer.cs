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
    internal static string FocusBrowserElementDescription => GetString(nameof(FocusBrowserElementDescription));
    internal static string FocusBrowserElementName => GetString(nameof(FocusBrowserElementName));
    internal static string FocusBrowserElementSucceeded => GetString(nameof(FocusBrowserElementSucceeded));
    internal static string TypeBrowserTextDescription => GetString(nameof(TypeBrowserTextDescription));
    internal static string TypeBrowserTextName => GetString(nameof(TypeBrowserTextName));
    internal static string TypeBrowserTextSucceeded => GetString(nameof(TypeBrowserTextSucceeded));
    internal static string PressBrowserKeyDescription => GetString(nameof(PressBrowserKeyDescription));
    internal static string PressBrowserKeyName => GetString(nameof(PressBrowserKeyName));
    internal static string PressBrowserKeySucceeded => GetString(nameof(PressBrowserKeySucceeded));
    internal static string HoverBrowserElementDescription => GetString(nameof(HoverBrowserElementDescription));
    internal static string HoverBrowserElementName => GetString(nameof(HoverBrowserElementName));
    internal static string HoverBrowserElementSucceeded => GetString(nameof(HoverBrowserElementSucceeded));
    internal static string SelectBrowserOptionDescription => GetString(nameof(SelectBrowserOptionDescription));
    internal static string SelectBrowserOptionName => GetString(nameof(SelectBrowserOptionName));
    internal static string SelectBrowserOptionSucceeded => GetString(nameof(SelectBrowserOptionSucceeded));
    internal static string ScrollBrowserDescription => GetString(nameof(ScrollBrowserDescription));
    internal static string ScrollBrowserName => GetString(nameof(ScrollBrowserName));
    internal static string ScrollBrowserSucceeded => GetString(nameof(ScrollBrowserSucceeded));
    internal static string WaitForBrowserDescription => GetString(nameof(WaitForBrowserDescription));
    internal static string WaitForBrowserName => GetString(nameof(WaitForBrowserName));
    internal static string WaitForBrowserSucceeded => GetString(nameof(WaitForBrowserSucceeded));
    internal static string WaitBrowserDescription => GetString(nameof(WaitBrowserDescription));
    internal static string WaitBrowserName => GetString(nameof(WaitBrowserName));
    internal static string WaitBrowserSucceeded => GetString(nameof(WaitBrowserSucceeded));
    internal static string WaitForBrowserUrlDescription => GetString(nameof(WaitForBrowserUrlDescription));
    internal static string WaitForBrowserUrlName => GetString(nameof(WaitForBrowserUrlName));
    internal static string WaitForBrowserUrlSucceeded => GetString(nameof(WaitForBrowserUrlSucceeded));
    internal static string WaitForBrowserLoadStateDescription => GetString(nameof(WaitForBrowserLoadStateDescription));
    internal static string WaitForBrowserLoadStateName => GetString(nameof(WaitForBrowserLoadStateName));
    internal static string WaitForBrowserLoadStateSucceeded => GetString(nameof(WaitForBrowserLoadStateSucceeded));
    internal static string WaitForBrowserElementStateDescription => GetString(nameof(WaitForBrowserElementStateDescription));
    internal static string WaitForBrowserElementStateName => GetString(nameof(WaitForBrowserElementStateName));
    internal static string WaitForBrowserElementStateSucceeded => GetString(nameof(WaitForBrowserElementStateSucceeded));
    internal static string CloseTrackedBrowserDescription => GetString(nameof(CloseTrackedBrowserDescription));
    internal static string CloseTrackedBrowserName => GetString(nameof(CloseTrackedBrowserName));
    internal static string CloseTrackedBrowserSucceeded => GetString(nameof(CloseTrackedBrowserSucceeded));
    internal static string SelectorArgumentLabel => GetString(nameof(SelectorArgumentLabel));
    internal static string SelectorArgumentDescription => GetString(nameof(SelectorArgumentDescription));
    internal static string ValueArgumentLabel => GetString(nameof(ValueArgumentLabel));
    internal static string ValueArgumentDescription => GetString(nameof(ValueArgumentDescription));
    internal static string TypeTextArgumentDescription => GetString(nameof(TypeTextArgumentDescription));
    internal static string SelectValueArgumentDescription => GetString(nameof(SelectValueArgumentDescription));
    internal static string KeyArgumentLabel => GetString(nameof(KeyArgumentLabel));
    internal static string KeyArgumentDescription => GetString(nameof(KeyArgumentDescription));
    internal static string TextArgumentLabel => GetString(nameof(TextArgumentLabel));
    internal static string TextArgumentDescription => GetString(nameof(TextArgumentDescription));
    internal static string UrlContainsArgumentLabel => GetString(nameof(UrlContainsArgumentLabel));
    internal static string UrlContainsArgumentDescription => GetString(nameof(UrlContainsArgumentDescription));
    internal static string DeltaYArgumentLabel => GetString(nameof(DeltaYArgumentLabel));
    internal static string DeltaYArgumentDescription => GetString(nameof(DeltaYArgumentDescription));
    internal static string DeltaXArgumentLabel => GetString(nameof(DeltaXArgumentLabel));
    internal static string DeltaXArgumentDescription => GetString(nameof(DeltaXArgumentDescription));
    internal static string UrlArgumentLabel => GetString(nameof(UrlArgumentLabel));
    internal static string UrlArgumentDescription => GetString(nameof(UrlArgumentDescription));
    internal static string WaitUrlArgumentDescription => GetString(nameof(WaitUrlArgumentDescription));
    internal static string MatchArgumentLabel => GetString(nameof(MatchArgumentLabel));
    internal static string MatchArgumentDescription => GetString(nameof(MatchArgumentDescription));
    internal static string StateArgumentLabel => GetString(nameof(StateArgumentLabel));
    internal static string LoadStateArgumentLabel => GetString(nameof(LoadStateArgumentLabel));
    internal static string LoadStateArgumentDescription => GetString(nameof(LoadStateArgumentDescription));
    internal static string ElementStateArgumentLabel => GetString(nameof(ElementStateArgumentLabel));
    internal static string ElementStateArgumentDescription => GetString(nameof(ElementStateArgumentDescription));
    internal static string FunctionArgumentLabel => GetString(nameof(FunctionArgumentLabel));
    internal static string FunctionArgumentDescription => GetString(nameof(FunctionArgumentDescription));
    internal static string TimeoutMillisecondsArgumentLabel => GetString(nameof(TimeoutMillisecondsArgumentLabel));
    internal static string TimeoutMillisecondsArgumentDescription => GetString(nameof(TimeoutMillisecondsArgumentDescription));
    internal static string MaxElementsArgumentLabel => GetString(nameof(MaxElementsArgumentLabel));
    internal static string MaxElementsArgumentDescription => GetString(nameof(MaxElementsArgumentDescription));
    internal static string MaxTextLengthArgumentLabel => GetString(nameof(MaxTextLengthArgumentLabel));
    internal static string MaxTextLengthArgumentDescription => GetString(nameof(MaxTextLengthArgumentDescription));

    private static string GetString(string name) => s_resourceManager.GetString(name, Culture)!;
}
