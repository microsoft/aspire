// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Aspire.Shared;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aspire.Dashboard.Utils;

internal static class GlobalizationHelpers
{
    private const string DefaultHtmlLanguageName = "en";
    private const int MaxCultureParentDepth = 5;

    public static List<CultureInfo> OrderedLocalizedCultures { get; }

    public static List<CultureInfo> AllCultures { get; }

    public static Dictionary<string, List<CultureInfo>> ExpandedLocalizedCultures { get; }

    static GlobalizationHelpers()
    {
        var localizedCultureInfos = LocaleHelpers.SupportedLocales.Select(CultureInfo.GetCultureInfo).ToList();

        AllCultures = GetAllCultures();

        ExpandedLocalizedCultures = GetExpandedLocalizedCultures(localizedCultureInfos, AllCultures);

        // Order cultures for display in the UI with invariant culture. This prevents the order of languages changing when the culture changes.
        OrderedLocalizedCultures = localizedCultureInfos.OrderBy(c => c.NativeName, StringComparer.InvariantCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Returns the language name used by the dashboard's HTML document.
    /// </summary>
    public static string GetHtmlLanguageName(CultureInfo culture)
    {
        // Invariant culture uses the English base resources, but its Name is empty.
        return culture.Name is { Length: > 0 } languageName ? languageName : DefaultHtmlLanguageName;
    }

    private static Dictionary<string, List<CultureInfo>> GetExpandedLocalizedCultures(List<CultureInfo> localizedCultures, List<CultureInfo> allCultures)
    {
        var dict = new Dictionary<string, List<CultureInfo>>(StringComparers.CultureName);
        foreach (var localizedCulture in localizedCultures)
        {
            var selfAndChildren = new List<CultureInfo>();
            dict[localizedCulture.Name] = selfAndChildren;

            foreach (var culture in allCultures)
            {
                var current = culture;
                var parentCount = 0;

                // The top-level parent of all cultures is invariant culture.
                while (current != CultureInfo.InvariantCulture)
                {
                    if (current.Equals(localizedCulture))
                    {
                        selfAndChildren.Add(culture);
                        break;
                    }
                    if (parentCount >= MaxCultureParentDepth)
                    {
                        // A recursion limit ensures there is no chance of an infinite loop from a circular parent chain.
                        break;
                    }
                    parentCount++;
                    current = current.Parent;
                }
            }
        }

        return dict;
    }

    private static List<CultureInfo> GetAllCultures()
    {
        var allCultures = CultureInfo.GetCultures(CultureTypes.AllCultures).ToList();

        // "zh-CN" is a non-standard culture but it is the default in many Chinese browsers.
        // Ensuring zh-CN is present allows OS culture customization to flow through the dashboard.
        if (!allCultures.Any(c => c.Name == "zh-CN"))
        {
            var simplifiedChinese = CultureInfo.GetCultureInfo("zh-CN");
            if (simplifiedChinese != null)
            {
                allCultures.Add(simplifiedChinese);
            }
        }

        return allCultures;
    }

    public static bool TryGetKnownParentCulture(CultureInfo culture, [NotNullWhen(true)] out CultureInfo? matchedCulture)
    {
        return TryGetKnownParentCulture(OrderedLocalizedCultures, culture, out matchedCulture);
    }

    public static bool TryGetKnownParentCulture(List<CultureInfo> knownCultures, CultureInfo culture, [NotNullWhen(true)] out CultureInfo? matchedCulture)
    {
        if (knownCultures.Contains(culture))
        {
            matchedCulture = culture;
            return true;
        }

        var count = 0;
        var current = culture;

        // The top-level parent of all cultures is invariant culture.
        while (current != CultureInfo.InvariantCulture)
        {
            // ensure we don't get stuck in an infinite loop by limiting the number of parent levels we check
            if (count >= MaxCultureParentDepth)
            {
                matchedCulture = null;
                return false;
            }

            if (knownCultures.Contains(current))
            {
                matchedCulture = current;
                return true;
            }

            count++;
            current = current.Parent;
        }

        matchedCulture = null;
        return false;
    }

    // Temp culture that will be set if the request culture cannot be resolved.
    private static readonly RequestCulture s_fallbackRequestCulture = new RequestCulture(CultureInfo.InvariantCulture, CultureInfo.InvariantCulture);

    internal static async Task<RequestCulture?> ResolveSetCultureToAcceptedCultureAsync(string acceptLanguage, List<CultureInfo> availableCultures)
    {
        var tempHttpContext = new DefaultHttpContext();
        tempHttpContext.Request.Headers["Accept-Language"] = acceptLanguage;

        // Use the RequestLocalizationMiddleware to resolve the culture.
        // This is hacky and not efficent to create and run middleware, but this is only called once when setting the language.
        // Reusing the middleware avoids us duplicating the culture matching logic.
        var middleware = new RequestLocalizationMiddleware(c => Task.CompletedTask, Options.Create(new RequestLocalizationOptions
        {
            SupportedCultures = availableCultures,
            SupportedUICultures = availableCultures,
            RequestCultureProviders = new List<IRequestCultureProvider>
                {
                    new AcceptLanguageHeaderRequestCultureProvider()
                },
            DefaultRequestCulture = s_fallbackRequestCulture
        }), NullLoggerFactory.Instance);

        await middleware.Invoke(tempHttpContext).ConfigureAwait(false);

        var result = tempHttpContext.Features.Get<IRequestCultureFeature>()?.RequestCulture;
        if (result == null || result == s_fallbackRequestCulture)
        {
            // No result was set or the result is the fallback culture.
            // The Accept-Language values are not compatible with the set language.
            return null;
        }

        return result;
    }
}
