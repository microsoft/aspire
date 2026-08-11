// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;

namespace Aspire.Cli.Commands;

internal sealed record ResourceCommandHelpRequest(string ResourceName, string CommandName, FileInfo? AppHostProjectFile);

internal static class ResourceCommandHelpParser
{
    public static ResourceCommandHelpRequest? Parse(
        ParseResult parseResult,
        Argument<string> resourceArgument,
        Argument<string> commandArgument,
        OptionWithLegacy<FileInfo?> appHostOption)
    {
        // Command-specific help is only available when both the resource and command were parsed:
        //   aspire resource web wait-for-browser --help
        //   aspire resource web wait-for-browser --apphost ./AppHost.csproj --help
        //   aspire resource web wait-for-browser --project ./AppHost.csproj --help
        //
        // Generic help is intentionally ignored here and falls back to System.CommandLine:
        //   aspire resource --help
        //   aspire resource web --help
        var resourceName = GetArgumentValue(parseResult, resourceArgument);
        var commandName = GetArgumentValue(parseResult, commandArgument);
        var appHostOptionValue = GetOptionTokenValue(parseResult, appHostOption.InnerOption) ?? GetOptionTokenValue(parseResult, appHostOption.LegacyOption);

        // Only intercept help when the resource and command tokens are real positional arguments. Resource-only help,
        // option tokens, and option values that System.CommandLine bound into the command argument should fall back to
        // the default/resource-scoped help paths instead of being treated as command-specific help.
        // Examples:
        //   aspire resource web --help
        //   aspire resource web --apphost ./AppHost.csproj --help
        //   aspire resource web -- --message hi --help
        if (string.IsNullOrEmpty(resourceName) ||
            IsOptionLikeToken(resourceName) ||
            string.IsNullOrEmpty(commandName) ||
            IsOptionLikeToken(commandName) ||
            string.Equals(commandName, appHostOptionValue, StringComparison.Ordinal))
        {
            return null;
        }

        return new ResourceCommandHelpRequest(
            resourceName,
            commandName,
            appHostOptionValue is null ? null : new FileInfo(appHostOptionValue));
    }

    private static string? GetArgumentValue(ParseResult parseResult, Argument<string> argument)
    {
        var result = parseResult.GetResult(argument);
        return result?.Tokens.Count > 0 ? result.Tokens[0].Value : null;
    }

    private static string? GetOptionTokenValue(ParseResult parseResult, Option<FileInfo?> option)
    {
        var result = parseResult.GetResult(option);
        return result?.Tokens.Count > 0 ? result.Tokens[0].Value : null;
    }

    private static bool IsOptionLikeToken(string value)
    {
        return value.StartsWith("-", StringComparison.Ordinal);
    }
}
