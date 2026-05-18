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

    internal static string BrowserDefaultProfileName => GetString(nameof(BrowserDefaultProfileName));
    internal static string BrowserEmptyBrowserConfiguration => GetString(nameof(BrowserEmptyBrowserConfiguration));
    internal static string BrowserEmptyProfileConfiguration => GetString(nameof(BrowserEmptyProfileConfiguration));
    internal static string BrowserProfileRequiresSharedUserDataMode => GetString(nameof(BrowserProfileRequiresSharedUserDataMode));
    internal static string BrowserInvalidUserDataModeConfiguration => GetString(nameof(BrowserInvalidUserDataModeConfiguration));
    internal static string BrowserUnableToLocateBrowser => GetString(nameof(BrowserUnableToLocateBrowser));
    internal static string BrowserAppHostPathShaNotAvailable => GetString(nameof(BrowserAppHostPathShaNotAvailable));
    internal static string BrowserUserDataDirectoryNotFound => GetString(nameof(BrowserUserDataDirectoryNotFound));
    internal static string BrowserTrackedBrowserProfileConflict => GetString(nameof(BrowserTrackedBrowserProfileConflict));
    internal static string BrowserUnableToReadProfileMetadata => GetString(nameof(BrowserUnableToReadProfileMetadata));
    internal static string BrowserInvalidProfileMetadata => GetString(nameof(BrowserInvalidProfileMetadata));
    internal static string BrowserProfileNotFound => GetString(nameof(BrowserProfileNotFound));
    internal static string BrowserAmbiguousProfile => GetString(nameof(BrowserAmbiguousProfile));
    internal static string BrowserResourceMissingHttpEndpoint => GetString(nameof(BrowserResourceMissingHttpEndpoint));
    internal static string BrowserEndpointNotAllocated => GetString(nameof(BrowserEndpointNotAllocated));

    private static string GetString(string name) => s_resourceManager.GetString(name, Culture)!;
}
