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

internal static class TemplatePromptDefaults
{
    public static IReadOnlyDictionary<string, string> Load(
        IEngineEnvironmentSettings settings, ITemplateInfo template, TemplateHostConfiguration hostConfiguration,
        string name, CancellationToken cancellationToken)
    {
        var defaults = new Dictionary<string, string>(StringComparer.Ordinal);
        if (hostConfiguration.DefaultValueSources.Count == 0)
        {
            return defaults;
        }

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
        var symbolNames = symbols.Keys.ToArray();
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
                !symbols.TryGetValue(sourceName, out var source) || source is not GeneratedSymbol { Generator: "coalesce" } coalesce ||
                !coalesce.Parameters.TryGetValue("sourceVariableName", out var sourceVariable))
            {
                throw InvalidDefault(parameterName);
            }

            // GeneratedSymbol.Parameters contains JSON-serialized values, e.g.
            // sourceVariableName -> "\"displayName\"", not an expression to interpolate.
            using var document = JsonDocument.Parse(sourceVariable);
            if (document.RootElement.ValueKind != JsonValueKind.String || document.RootElement.GetString() != parameterName)
            {
                throw InvalidDefault(parameterName);
            }

            // Only project-name-derived values are previewed. Evaluating random/bound symbols
            // twice can change the accepted default, and other parameters may contain secrets.
            var variables = new VariableCollection { ["name"] = name };
            var visiting = new HashSet<string>(StringComparer.Ordinal);
            var evaluated = new HashSet<string>(StringComparer.Ordinal);
            Evaluate(sourceName);
            if (!variables.TryGetValue(sourceName, out var value) || value is not string defaultValue)
            {
                throw InvalidDefault(parameterName);
            }

            defaults.Add(parameterName, defaultValue);

            void Evaluate(string symbolName)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Native coalesce falls back when an override was not supplied:
                // https://github.com/dotnet/templating/wiki/Available-Symbols-Generators#coalesce
                // Omit it here too, rather than feeding its static default to the macro.
                if (symbolName == parameterName || symbolName == "name" || evaluated.Contains(symbolName))
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

                var config = macro.CreateConfig(settings, generated);
                if (config is not IMacroConfigDependency dependencies)
                {
                    throw InvalidDefault(parameterName);
                }

                dependencies.ResolveSymbolDependencies(symbolNames);
                foreach (var dependency in dependencies.Dependencies)
                {
                    Evaluate(dependency);
                }

                // Use the engine's own implementations and dependency metadata, rather than
                // interpreting regex steps or coalesce expressions in the CLI.
                macro.EvaluateConfig(settings, variables, config);
                visiting.Remove(symbolName);
                evaluated.Add(symbolName);
            }
        }

        return defaults;
    }

    private static TemplateAuthoringException InvalidDefault(string parameterName) =>
        new(string.Format(CultureInfo.CurrentCulture, TemplatingStrings.InvalidTemplatePromptDefault, parameterName), parameterName);
}
