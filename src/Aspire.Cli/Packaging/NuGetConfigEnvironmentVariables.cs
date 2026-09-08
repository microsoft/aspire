// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;

namespace Aspire.Cli.Packaging;

/// <summary>
/// Finds environment variables referenced by NuGet configuration values.
/// </summary>
internal static partial class NuGetConfigEnvironmentVariables
{
    internal static string[] FindReferencedNames(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        // NuGet config values commonly reference environment variables as:
        //   %USERPROFILE%\.nuget\packages
        //   $HOME/.nuget/packages
        //   ${HOME}/.nuget/packages
        // See https://learn.microsoft.com/nuget/reference/nuget-config-file#using-environment-variables.
        var environmentVariableComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        return [.. EnvironmentVariablePattern()
            .Matches(content)
            .Select(static match => match.Groups[1].Success
                ? match.Groups[1].Value
                : match.Groups[2].Success
                    ? match.Groups[2].Value
                    : match.Groups[3].Value)
            .Distinct(environmentVariableComparer)
            .OrderBy(static name => name, environmentVariableComparer)];
    }

    [GeneratedRegex(
        @"%([^%]+)%|\$\{([A-Za-z_][A-Za-z0-9_]*)\}|\$([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentVariablePattern();
}
