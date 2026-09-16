// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.CommandLine.Completions;
using System.CommandLine.Parsing;
using System.Globalization;
using Aspire.Cli.Resources;
using RootCommand = Aspire.Cli.Commands.RootCommand;

namespace Aspire.Cli.Completions;

internal static class CompletionInvocation
{
    internal static bool IsSuggestionRequest(string[] args)
        => args.Length > 0 && args[0].StartsWith("[suggest", StringComparison.Ordinal);

    internal static bool Matches(string[] args)
    {
        if (IsSuggestionRequest(args))
        {
            return true;
        }

        if (!args.Contains("completions", StringComparer.Ordinal))
        {
            return false;
        }

        // Recognize the command before startup, including global options placed before it.
        // Use the actual option metadata so values such as "--log-file completions" are
        // not mistaken for a request to generate a script.
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            var separator = token.IndexOfAny(['=', ':']);
            var name = separator < 0 ? token : token[..separator];
            if (name is CommonOptionNames.Version or CommonOptionNames.VersionShort or
                CommonOptionNames.Help or CommonOptionNames.HelpShort or CommonOptionNames.HelpAlt or
                CommonOptionNames.HelpSlash or CommonOptionNames.HelpAltSlash)
            {
                continue;
            }

            if (!token.StartsWith('-'))
            {
                return token == "completions";
            }

            // This option is normally registered only by extension hosts, but it must
            // still select the completion path before any extension connection is made.
            var option = RootCommand.GlobalOptions.Append(RootCommand.StartDebugSessionOption)
                .FirstOrDefault(o => o.Name == name || o.Aliases.Contains(name));
            if (option is null)
            {
                return false;
            }

            if (separator < 0 && option.Arity.MaximumNumberOfValues > 0 &&
                (option is not Option<bool> || i + 1 < args.Length && bool.TryParse(args[i + 1], out _)))
            {
                i++;
            }
        }

        return false;
    }

    internal static int WriteSuggestions(System.CommandLine.RootCommand command, string[] args, TextWriter output, TextWriter error)
    {
        if (args is ["[suggest:tokens]", .. var tokens])
        {
            // Native shell adapters send argv directly, e.g. ["run", "--project",
            // "My App.csproj", ""]. Preserve spaces, literal quotes, and a final empty
            // token (the start of a new word) instead of rebuilding a raw command line.
            return WriteCompletions(GetTokenCompletions(command.Parse(tokens)), output);
        }

        // Shell hooks send: [suggest] "aspire run --apph", or [suggest:17] "aspire run --apph".
        // The line is data, never an action to invoke (even if it includes --help, --banner,
        // a debugger option, another directive, or a complete executable command).
        var position = args.Length == 2 ? args[1].Length : 0;
        if (args.Length != 2 ||
            (args[0] != "[suggest]" &&
                !(args[0].StartsWith("[suggest:", StringComparison.Ordinal) &&
                  args[0].EndsWith(']') &&
                  int.TryParse(args[0].AsSpan(9, args[0].Length - 10), NumberStyles.None, CultureInfo.InvariantCulture, out position))) ||
            position < 0 || position > args[1].Length)
        {
            error.WriteLine(RootCommandStrings.InvalidCompletionRequest);
            return CliExitCodes.InvalidCommand;
        }

        return WriteCompletions(command.Parse(args[1]).GetCompletions(position), output);
    }

    private static IEnumerable<CompletionItem> GetTokenCompletions(ParseResult parseResult)
    {
        // System.CommandLine 2.0's argv completion path stops suggesting option values once
        // their arity is filled, even when that final token is still being completed.
        // Resolve the owning option explicitly (including recursive options on ancestors).
        // https://github.com/dotnet/command-line-api/blob/v2.0.8/src/System.CommandLine/ParseResult.cs
        if (parseResult.Tokens.Count > 0 && parseResult.Tokens[^1] is { Type: TokenType.Argument } lastToken)
        {
            for (var commandResult = parseResult.CommandResult; commandResult is not null; commandResult = commandResult.Parent as System.CommandLine.Parsing.CommandResult)
            {
                foreach (var optionResult in commandResult.Children.OfType<OptionResult>())
                {
                    if (optionResult.Tokens.Contains(lastToken))
                    {
                        return optionResult.Option.GetCompletions(parseResult.GetCompletionContext());
                    }
                }
            }
        }

        return parseResult.GetCompletions();
    }

    private static int WriteCompletions(IEnumerable<CompletionItem> completions, TextWriter output)
    {
        foreach (var completion in completions)
        {
            // Git Bash also queries the Windows binary; CRLF would leave a literal '\r'
            // in each candidate when Bash reads the newline-delimited protocol.
            output.Write(completion.Label);
            output.Write('\n');
        }

        return CliExitCodes.Success;
    }
}
