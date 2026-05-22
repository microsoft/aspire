// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Aspire.Cli.Commands;
using Aspire.Cli.Interaction;
using Aspire.Cli.Templating;
using Spectre.Console;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestNewCommandPrompter(IInteractionService interactionService) : NewCommandPrompter(interactionService)
{
    public Func<ITemplate[], ITemplate>? PromptForTemplateCallback { get; set; }
    public Func<string, string>? PromptForProjectNameCallback { get; set; }
    public Func<string, string>? PromptForOutputPathCallback { get; set; }
    public Func<string, Func<string, ValidationResult>?, string>? PromptForOutputPathWithValidatorCallback { get; set; }

    public override Task<ITemplate> PromptForTemplateAsync(ITemplate[] validTemplates, CancellationToken cancellationToken)
    {
        return PromptForTemplateCallback switch
        {
            { } callback => Task.FromResult(callback(validTemplates)),
            _ => Task.FromResult(validTemplates[0]) // If no callback is provided just accept the first template.
        };
    }

    public override Task<string> PromptForProjectNameAsync(string defaultName, ParseResult parseResult, CancellationToken cancellationToken)
    {
        return PromptForProjectNameCallback switch
        {
            { } callback => Task.FromResult(callback(defaultName)),
            _ => Task.FromResult(defaultName) // If no callback is provided just accept the default.
        };
    }

    public override Task<string> PromptForOutputPath(string path, ParseResult parseResult, Func<string, ValidationResult>? validator = null, CancellationToken cancellationToken = default, Func<string, string>? outputPathResolver = null)
    {
        var resolvedValidator = validator;
        if (validator is not null && outputPathResolver is not null)
        {
            resolvedValidator = candidatePath => validator(outputPathResolver(candidatePath));
        }

        var outputPath = PromptForOutputPathWithValidatorCallback switch
        {
            { } callback => callback(path, resolvedValidator),
            _ => PromptForOutputPathCallback switch
            {
                { } callback => callback(path),
                _ => path // If no callback is provided just accept the default.
            }
        };

        return Task.FromResult(outputPathResolver?.Invoke(outputPath) ?? outputPath);
    }
}
