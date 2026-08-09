// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;

namespace Aspire.Shared;

internal static class LocaleHelpers
{
    private const int MaxCultureParentDepth = 5;

    // our localization list comes from https://github.com/dotnet/arcade/blob/89008f339a79931cc49c739e9dbc1a27c608b379/src/Microsoft.DotNet.XliffTasks/build/Microsoft.DotNet.XliffTasks.props#L22
    public static readonly string[] SupportedLocales = ["en", "cs", "de", "es", "fr", "it", "ja", "ko", "pl", "pt-BR", "ru", "tr", "zh-Hans", "zh-Hant"];

    public static SetLocaleResult TrySetLocaleOverride(string localeOverride)
    {
        // Explicitly check if this is a known culture.
        // Linux/macOS don't thrown CultureNotFoundException so this check provides a consistent experience.
        if (!IsKnownCulture(localeOverride))
        {
            return SetLocaleResult.InvalidLocale;
        }

        try
        {
            var cultureInfo = new CultureInfo(localeOverride);
            if (IsSupportedCulture(cultureInfo))
            {
                CultureInfo.CurrentUICulture = cultureInfo;
                CultureInfo.CurrentCulture = cultureInfo;
                CultureInfo.DefaultThreadCurrentCulture = cultureInfo;
                CultureInfo.DefaultThreadCurrentUICulture = cultureInfo;
                return SetLocaleResult.Success;
            }

            return SetLocaleResult.UnsupportedLocale;
        }
        catch (CultureNotFoundException)
        {
            return SetLocaleResult.InvalidLocale;
        }
    }

    private static bool IsSupportedCulture(CultureInfo cultureInfo)
    {
        // Check exact name and two-letter ISO language name first.
        if (SupportedLocales.Contains(cultureInfo.Name) ||
            SupportedLocales.Contains(cultureInfo.TwoLetterISOLanguageName))
        {
            return true;
        }

        // Walk the parent chain to find a supported culture.
        // For example, zh-CN's parent is zh-Hans which is supported.
        var current = cultureInfo.Parent;
        var depth = 0;
        while (current != CultureInfo.InvariantCulture && current != current.Parent && depth < MaxCultureParentDepth)
        {
            if (SupportedLocales.Contains(current.Name))
            {
                return true;
            }
            current = current.Parent;
            depth++;
        }

        return false;
    }

    private static bool IsKnownCulture(string cultureName)
    {
        return CultureInfo
            .GetCultures(CultureTypes.AllCultures)
            .Any(c => string.Equals(c.Name, cultureName, StringComparison.OrdinalIgnoreCase));
    }

    public static string? GetLocaleOverride(IConfiguration configuration)
    {
        var localeOverride = configuration[KnownConfigNames.LocaleOverride];
        if (string.IsNullOrEmpty(localeOverride))
        {
            // also support DOTNET_CLI_UI_LANGUAGE as it's a common dotnet environment variable
            localeOverride = configuration[KnownConfigNames.DotnetCliUiLanguage];
        }

        return localeOverride;
    }
}

internal enum SetLocaleResult
{
    Success,
    InvalidLocale,
    UnsupportedLocale
}
