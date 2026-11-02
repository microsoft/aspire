// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Resources;
using Microsoft.TemplateEngine.Abstractions;
using Microsoft.TemplateEngine.Core;
using Microsoft.TemplateEngine.Orchestrator.RunnableProjects.Abstractions;
using Microsoft.TemplateEngine.Orchestrator.RunnableProjects.ConfigModel;
using Microsoft.TemplateEngine.Utils;

namespace Aspire.Cli.Templating;

internal sealed class TemplatePromptDefaults
{
    private readonly Dictionary<string, List<(IGeneratedSymbolMacro Macro, IMacroConfig Config)>> _projections = new(StringComparer.Ordinal);

    public static async Task<TemplatePromptDefaults> LoadAsync(
        IEngineEnvironmentSettings settings, ITemplateInfo template, TemplateHostConfiguration hostConfiguration,
        CancellationToken cancellationToken)
    {
        var defaults = new TemplatePromptDefaults();
        if (hostConfiguration.DefaultValueSources.Count == 0)
        {
            return defaults;
        }

        if (!settings.Components.TryGetComponent(template.GeneratorId, out IGenerator? generator) || generator is null)
        {
            throw new InvalidOperationException($"Template generator '{template.GeneratorId}' is not registered.");
        }

        using var loadedTemplate = await generator.LoadTemplateAsync(settings, template, cancellationToken: cancellationToken)
            ?? throw new FileNotFoundException(null, template.ConfigPlace);

        if (!settings.TryGetMountPoint(template.MountPointUri, out var mount) || mount is null)
        {
            throw new FileNotFoundException(null, template.MountPointUri);
        }

        TemplateConfigModel model;
        using (mount)
        {
            var file = mount.FileInfo(template.ConfigPlace)
                ?? throw new FileNotFoundException(null, template.ConfigPlace);
            using var stream = file.OpenRead();
            model = TemplateConfigModel.FromStream(stream);
        }

        var symbols = model.Symbols.ToDictionary(symbol => symbol.Name, StringComparer.Ordinal);
        var macros = settings.Components.OfType<IGeneratedSymbolMacro>().ToDictionary(macro => macro.Type, StringComparer.Ordinal);
        foreach (var (parameterName, sourceName) in hostConfiguration.DefaultValueSources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parameter = template.ParameterDefinitions.FirstOrDefault(parameter => parameter.Name == parameterName);
            if (parameter is null || parameter.IsName || parameter.Type != "parameter" ||
                parameter.Precedence.CanBeRequired || !string.IsNullOrEmpty(parameter.DefaultValue) ||
                !parameter.DataType.Equals("string", StringComparison.OrdinalIgnoreCase) ||
                parameterName.Equals("AspireVersion", StringComparison.OrdinalIgnoreCase) ||
                parameterName.Equals("AspireNuGetSource", StringComparison.OrdinalIgnoreCase) ||
                !symbols.TryGetValue(sourceName, out var source) || source is not GeneratedSymbol { Generator: "coalesce" } coalesce)
            {
                throw InvalidDefault(parameterName);
            }

            if (ReadReference(coalesce, "sourceVariableName") != parameterName)
            {
                throw InvalidDefault(parameterName);
            }

            var visiting = new HashSet<string>(StringComparer.Ordinal);
            var validated = new HashSet<string>(StringComparer.Ordinal);
            var projection = new List<(IGeneratedSymbolMacro, IMacroConfig)>();
            Validate(sourceName);
            defaults._projections.Add(parameterName, projection);

            void Validate(string symbolName)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (symbolName == "name")
                {
                    // Cached discovery metadata does not preserve IsName; use runtime definitions.
                    if (!loadedTemplate.ParameterDefinitions.Any(parameter => parameter.Name == symbolName && parameter.IsName))
                    {
                        throw InvalidDefault(parameterName);
                    }

                    return;
                }

                if (validated.Contains(symbolName))
                {
                    return;
                }

                if (!visiting.Add(symbolName) ||
                    !symbols.TryGetValue(symbolName, out var symbol) ||
                    symbol is not GeneratedSymbol { Generator: "regex" or "coalesce" } generated ||
                    !macros.TryGetValue(generated.Generator, out var macro))
                {
                    throw InvalidDefault(parameterName);
                }

                // Only project-name-derived values are previewed. Evaluating random/bound
                // symbols twice can change the fallback; other parameters may contain secrets.
                // Validate raw references, including unused coalesce branches: the engine's
                // dependency discovery filters out unknown symbol names.
                var config = macro.CreateConfig(settings, generated);
                if (generated.Generator == "regex")
                {
                    var regexSource = ReadReference(generated, "source");
                    if (regexSource == parameterName)
                    {
                        // Regex can observe an omitted parameter's empty default, whereas
                        // coalesce also observes suppliedness. A plain collection cannot
                        // represent both; do not preview graphs requiring parameter state.
                        throw InvalidDefault(parameterName);
                    }
                    Validate(regexSource);
                }
                else
                {
                    var coalesceSource = ReadReference(generated, "sourceVariableName");
                    if (coalesceSource == "name")
                    {
                        // The engine marks name as NameParameter, not User input, so it
                        // falls back here even when name is nonempty. Its parameter-aware
                        // collection is internal; reject this shape rather than mispreview it.
                        // https://github.com/dotnet/templating/wiki/Available-Symbols-Generators#coalesce
                        throw InvalidDefault(parameterName);
                    }
                    if (coalesceSource != parameterName)
                    {
                        Validate(coalesceSource);
                    }
                    Validate(ReadReference(generated, "fallbackVariableName"));
                }

                visiting.Remove(symbolName);
                validated.Add(symbolName);
                projection.Add((macro, config));
            }

            string ReadReference(GeneratedSymbol symbol, string key)
            {
                if (!symbol.Parameters.TryGetValue(key, out var reference))
                {
                    throw InvalidDefault(parameterName);
                }

                // GeneratedSymbol.Parameters contains JSON-serialized values, e.g.
                // sourceVariableName -> "\"displayName\"", not an expression to interpolate.
                using var document = JsonDocument.Parse(reference);
                if (document.RootElement.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(document.RootElement.GetString()))
                {
                    throw InvalidDefault(parameterName);
                }

                return document.RootElement.GetString()!;
            }
        }

        return defaults;
    }

    public IReadOnlyDictionary<string, string> Evaluate(IEngineEnvironmentSettings settings, string name, CancellationToken cancellationToken)
    {
        var defaults = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (parameterName, projection) in _projections)
        {
            // Omit the override so native coalesce selects its fallback. Validation excludes
            // graphs where a plain collection differs from the engine's parameter-aware one.
            var variables = new VariableCollection { ["name"] = name };
            foreach (var (macro, config) in projection)
            {
                cancellationToken.ThrowIfCancellationRequested();
                macro.EvaluateConfig(settings, variables, config);
            }
            if (!variables.TryGetValue(projection[^1].Config.VariableName, out var value) || value is not string defaultValue)
            {
                throw InvalidDefault(parameterName);
            }
            defaults.Add(parameterName, defaultValue);
        }

        return defaults;
    }

    private static TemplateAuthoringException InvalidDefault(string parameterName) =>
        new(string.Format(CultureInfo.CurrentCulture, TemplatingStrings.InvalidTemplatePromptDefault, parameterName), parameterName);
}
