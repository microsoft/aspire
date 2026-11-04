// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Configuration;

internal static class SourceNameValidator
{
    internal const int MaximumLength = 64;
    internal const string Description = "Source names must be 1-64 ASCII letters, digits, dots, hyphens, or underscores, starting with a letter or digit.";

    internal static bool IsValid(string name)
    {
        if (name.Length is 0 or > MaximumLength || !char.IsAsciiLetterOrDigit(name[0]))
        {
            return false;
        }

        foreach (var character in name)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    internal static void Validate(string name, string settingPath)
    {
        if (!IsValid(name))
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.CurrentCulture, ErrorStrings.InvalidSourceName, settingPath));
        }
    }
}
