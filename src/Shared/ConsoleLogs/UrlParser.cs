// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Aspire.Shared.ConsoleLogs;

public static partial class UrlParser
{
    private static readonly Regex s_urlRegEx = GenerateUrlRegEx();

    public static bool TryParse(string? text, Func<string, string>? nonMatchFragmentCallback, [NotNullWhen(true)] out string? modifiedText)
    {
        if (text is not null)
        {
            var urlMatch = s_urlRegEx.Match(text);

            StringBuilder? builder = null;

            var nextCharIndex = 0;
            while (urlMatch.Success)
            {
                builder ??= new StringBuilder(text.Length * 2);

                if (urlMatch.Index > 0)
                {
                    AppendNonMatchFragment(builder, nonMatchFragmentCallback, text[(nextCharIndex)..urlMatch.Index]);
                }

                var urlStart = urlMatch.Index;
                nextCharIndex = urlMatch.Index + urlMatch.Length;
                var url = text[urlStart..nextCharIndex];

                builder.Append(CultureInfo.InvariantCulture, $"<a target=\"_blank\" href=\"{url}\" rel=\"noopener noreferrer nofollow\">{WebUtility.HtmlEncode(url)}</a>");
                urlMatch = urlMatch.NextMatch();
            }

            if (builder?.Length > 0)
            {
                if (nextCharIndex < text.Length)
                {
                    AppendNonMatchFragment(builder, nonMatchFragmentCallback, text[(nextCharIndex)..]);
                }

                modifiedText = builder.ToString();
                return true;
            }
        }

        modifiedText = null;
        return false;

        static void AppendNonMatchFragment(StringBuilder stringBuilder, Func<string, string>? nonMatchFragmentCallback, string text)
        {
            if (nonMatchFragmentCallback != null)
            {
                text = nonMatchFragmentCallback(text);
            }

            stringBuilder.Append(text);
        }
    }

    // Regular expression that detects http/https URLs in a log entry
    // Based on the RegEx used by GitHub to detect links in content.
    [GeneratedRegex(
        @"((?<!\+)https?:\/\/(?:www\.)?(?:[-\p{L}.]+?[.@][a-zA-Z\d]{2,}|localhost)(?:[-\w\p{L}.:%+~#*$!?&/=@]*(?:,(?!\s))*?)*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
    public static partial Regex GenerateUrlRegEx();
}
