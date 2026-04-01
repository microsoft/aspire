// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;

namespace Aspire.Cli.Templating;

internal class CallbackTemplate(
    string name,
    string description,
    Func<string, string> pathDeriverCallback,
    Action<Command> applyOptionsCallback,
    Func<CallbackTemplate, TemplateInputs, ParseResult, CancellationToken, Task<TemplateResult>> applyTemplateCallback,
    TemplateRuntime runtime = TemplateRuntime.DotNet,
    string? languageId = null,
    Func<string, bool>? supportsLanguageCallback = null,
    IReadOnlyList<string>? selectableAppHostLanguages = null,
    bool isEmpty = false) : ITemplate
{
    public string Name => name;

    public string Description => description;

    public bool IsEmpty => isEmpty;

    public TemplateRuntime Runtime => runtime;

    public Func<string, string> PathDeriver => pathDeriverCallback;

    public string? LanguageId => languageId;

    public IReadOnlyList<string> SelectableAppHostLanguages { get; } = selectableAppHostLanguages ?? [];

    public bool SupportsLanguage(string languageId)
    {
        if (supportsLanguageCallback is not null)
        {
            return supportsLanguageCallback(languageId);
        }

        return LanguageId is null || LanguageId.Equals(languageId, StringComparison.OrdinalIgnoreCase);
    }

    public void ApplyOptions(Command command)
    {
        applyOptionsCallback?.Invoke(command);
    }

    public Task<TemplateResult> ApplyTemplateAsync(TemplateInputs inputs, ParseResult parseResult, CancellationToken cancellationToken)
    {
        return applyTemplateCallback(this, inputs, parseResult, cancellationToken);
    }
}
