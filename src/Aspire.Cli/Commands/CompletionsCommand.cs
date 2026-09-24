// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Aspire.Cli.Completions;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Commands;

internal sealed class CompletionsCommand : ParentCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.ToolsAndConfiguration;

    public CompletionsCommand(IEnvironment environment, CommonCommandServices services)
        : base("completions", RootCommandStrings.CompletionsDescription, services)
    {
        Subcommands.Add(new ScriptCommand(environment, services));
    }

    private sealed class ScriptCommand : BaseCommand
    {
        private readonly Argument<string?> _shellArgument = new("shell")
        {
            Description = RootCommandStrings.CompletionShellDescription,
            Arity = ArgumentArity.ZeroOrOne
        };

        private readonly IEnvironment _environment;

        public ScriptCommand(IEnvironment environment, CommonCommandServices services)
            : base("script", RootCommandStrings.CompletionScriptDescription, services)
        {
            _environment = environment;
            _shellArgument.AcceptOnlyFromAmong(CompletionScripts.SupportedShells);
            Arguments.Add(_shellArgument);
        }

        protected override Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
        {
            var shell = parseResult.GetValue(_shellArgument) ?? CompletionScripts.DetectShell(_environment);
            if (shell is null)
            {
                return Task.FromResult(CommandResult.Failure(CliExitCodes.InvalidCommand, RootCommandStrings.CompletionShellRequired));
            }

            // A script is a machine-readable payload, not Spectre markup. Use the invocation's
            // writer so redirection does not introduce escaping, wrapping, colors, or banners.
            parseResult.InvocationConfiguration.Output.Write(CompletionScripts.Generate(shell));
            return Task.FromResult(CommandResult.Success());
        }
    }
}
