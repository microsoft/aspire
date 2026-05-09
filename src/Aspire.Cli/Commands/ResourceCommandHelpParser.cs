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
        if (string.IsNullOrEmpty(resourceName) ||
            IsOptionLikeToken(resourceName) ||
            string.IsNullOrEmpty(commandName) ||
            IsOptionLikeToken(commandName))
        {
            return null;
        }

        return new ResourceCommandHelpRequest(
            resourceName,
            commandName,
            GetOptionValue(parseResult, appHostOption.InnerOption) ?? GetOptionValue(parseResult, appHostOption.LegacyOption));
    }

    private static string? GetArgumentValue(ParseResult parseResult, Argument<string> argument)
    {
        var result = parseResult.GetResult(argument);
        return result?.Tokens.Count > 0 ? result.Tokens[0].Value : null;
    }

    private static FileInfo? GetOptionValue(ParseResult parseResult, Option<FileInfo?> option)
    {
        var result = parseResult.GetResult(option);
        return result?.Tokens.Count > 0 ? new FileInfo(result.Tokens[0].Value) : null;
    }

    private static bool IsOptionLikeToken(string value)
    {
        return value.StartsWith("-", StringComparison.Ordinal);
    }
}
