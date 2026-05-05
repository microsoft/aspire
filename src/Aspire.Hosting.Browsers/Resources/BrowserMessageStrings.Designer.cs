// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Resources;

#nullable enable

namespace Aspire.Hosting.Browsers.Resources;

internal static class BrowserMessageStrings
{
    private static readonly ResourceManager s_resourceManager = new("Aspire.Hosting.Browsers.Resources.BrowserMessageStrings", typeof(BrowserMessageStrings).Assembly);

    internal static CultureInfo? Culture { get; set; }

    internal static string BrowserLogsDefaultProfileName => GetString(nameof(BrowserLogsDefaultProfileName));
    internal static string BrowserLogsEmptyBrowserConfiguration => GetString(nameof(BrowserLogsEmptyBrowserConfiguration));
    internal static string BrowserLogsEmptyProfileConfiguration => GetString(nameof(BrowserLogsEmptyProfileConfiguration));
    internal static string BrowserLogsProfileRequiresSharedUserDataMode => GetString(nameof(BrowserLogsProfileRequiresSharedUserDataMode));
    internal static string BrowserLogsInvalidUserDataModeConfiguration => GetString(nameof(BrowserLogsInvalidUserDataModeConfiguration));
    internal static string BrowserLogsUnableToLocateBrowser => GetString(nameof(BrowserLogsUnableToLocateBrowser));
    internal static string BrowserLogsAppHostPathShaNotAvailable => GetString(nameof(BrowserLogsAppHostPathShaNotAvailable));
    internal static string BrowserLogsUserDataDirectoryNotFound => GetString(nameof(BrowserLogsUserDataDirectoryNotFound));
    internal static string BrowserLogsTrackedBrowserProfileConflict => GetString(nameof(BrowserLogsTrackedBrowserProfileConflict));
    internal static string BrowserLogsUnableToReadProfileMetadata => GetString(nameof(BrowserLogsUnableToReadProfileMetadata));
    internal static string BrowserLogsInvalidProfileMetadata => GetString(nameof(BrowserLogsInvalidProfileMetadata));
    internal static string BrowserLogsProfileNotFound => GetString(nameof(BrowserLogsProfileNotFound));
    internal static string BrowserLogsAmbiguousProfile => GetString(nameof(BrowserLogsAmbiguousProfile));
    internal static string BrowserAutomationResourceMissingHttpEndpoint => GetString(nameof(BrowserAutomationResourceMissingHttpEndpoint));
    internal static string BrowserLogsEndpointNotAllocated => GetString(nameof(BrowserLogsEndpointNotAllocated));

    private static string GetString(string name) => s_resourceManager.GetString(name, Culture)!;
}
