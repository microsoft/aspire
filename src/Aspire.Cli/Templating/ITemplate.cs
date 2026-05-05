// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;

namespace Aspire.Cli.Templating;

/// <summary>
/// Defines a template that can be applied by the CLI.
/// </summary>
internal interface ITemplate
{
    /// <summary>
    /// Gets the template name.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the template description shown in prompts and help text.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Gets a value indicating whether this is an empty/scaffold-only template.
    /// </summary>
    bool IsEmpty { get; }

    /// <summary>
    /// Gets a value indicating whether this template should be shown in the interactive template picker.
    /// </summary>
    bool ShowInPrompt { get; }

    /// <summary>
    /// Gets the runtime model used to execute this template.
    /// </summary>
    TemplateRuntime Runtime { get; }

    /// <summary>
    /// Gets a function that derives the output path from a project name.
    /// </summary>
    Func<string, string> PathDeriver { get; }

    /// <summary>
    /// Gets the AppHost language identifier associated with this template.
    /// </summary>
    string? LanguageId { get; }

    /// <summary>
    /// Determines whether this template is available for the selected language.
    /// </summary>
    /// <param name="languageId">The selected language identifier.</param>
    /// <returns><see langword="true"/> if the template is available; otherwise <see langword="false"/>.</returns>
    bool SupportsLanguage(string languageId);

    /// <summary>
    /// Gets the AppHost languages that this template can prompt for.
    /// </summary>
    IReadOnlyList<string> SelectableAppHostLanguages { get; }

    /// <summary>
    /// Applies template-specific command options.
    /// </summary>
    /// <param name="command">The command to configure.</param>
    void ApplyOptions(Command command);

    /// <summary>
    /// Applies the template using the provided inputs.
    /// </summary>
    /// <param name="inputs">The template inputs.</param>
    /// <param name="parseResult">The parsed command-line result.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The template execution result.</returns>
    Task<TemplateResult> ApplyTemplateAsync(TemplateInputs inputs, ParseResult parseResult, CancellationToken cancellationToken);
}

internal sealed record TemplateResult(int ExitCode, string? OutputPath = null);
