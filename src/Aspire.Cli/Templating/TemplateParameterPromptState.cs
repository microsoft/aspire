// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Resources;
using Microsoft.Extensions.Logging;
using Microsoft.TemplateEngine.Abstractions;
using Microsoft.TemplateEngine.Core;
using Microsoft.TemplateEngine.Core.Expressions.Cpp2;
using Microsoft.TemplateEngine.Utils;

namespace Aspire.Cli.Templating;

internal sealed class TemplateParameterPromptState
{
    private readonly IEngineEnvironmentSettings _settings;
    private readonly IGenerator _generator;
    private readonly ILogger _logger;
    private readonly Dictionary<string, object> _values = new(StringComparer.Ordinal);
    private readonly IReadOnlySet<string> _computedParameters;

    public TemplateParameterPromptState(
        IEngineEnvironmentSettings settings, ITemplateInfo template, string name, ILogger logger,
        IReadOnlySet<string> computedParameters)
    {
        _settings = settings;
        _logger = logger;
        _computedParameters = computedParameters;
        if (!settings.Components.TryGetComponent(template.GeneratorId, out IGenerator? generator) || generator is null)
        {
            throw new InvalidOperationException($"Template generator '{template.GeneratorId}' is not registered.");
        }
        _generator = generator;
        Parameters = OrderParameters(template.ParameterDefinitions.ToArray());
        foreach (var parameter in Parameters)
        {
            if (parameter.Precedence.PrecedenceDefinition == PrecedenceDefinition.Disabled)
            {
                continue;
            }

            var value = parameter.IsName ? name
                : settings.Host.TryGetHostParamDefault(parameter.Name, out var hostDefault) ? hostDefault
                : parameter.Precedence.CanBeRequired ? null : parameter.DefaultValue;
            if (value is not null)
            {
                // TemplateCreator validates invalid defaults; an interactive answer can still replace them.
                TrySetValue(parameter, value);
            }
        }
    }

    public IEnumerable<ITemplateParameter> Parameters { get; }

    public bool IsEnabled(ITemplateParameter parameter)
    {
        if (EvaluateCondition(parameter, parameter.Precedence.IsEnabledCondition, defaultValue: true))
        {
            return true;
        }

        // The engine removes disabled values before evaluating dependent parameters' conditions.
        _values.Remove(parameter.Name);
        return false;
    }

    public bool IsRequired(ITemplateParameter parameter) =>
        parameter.Precedence.IsRequired || EvaluateCondition(parameter, parameter.Precedence.IsRequiredCondition, defaultValue: false);

    public bool TrySetValue(ITemplateParameter parameter, string value)
    {
        var converted = _generator.ConvertParameterValueToType(_settings, parameter, value, out var error);
        if (error || converted is null)
        {
            return false;
        }

        _values[parameter.Name] = converted;
        return true;
    }

    private bool EvaluateCondition(ITemplateParameter parameter, string? condition, bool defaultValue)
    {
        if (string.IsNullOrEmpty(condition))
        {
            return defaultValue;
        }

        if (!_generator.TryEvaluateFromString(_logger, condition, _values, out var result, out var error, null))
        {
            throw new TemplateAuthoringException($"{parameter.Name}: {error}", parameter.Name);
        }

        return result;
    }

    private IReadOnlyList<ITemplateParameter> OrderParameters(ITemplateParameter[] parameters)
    {
        // Parse with every symbol in scope to find references even when its value is not collected yet.
        // The engine parser respects quoted literals and captures both sides of Boolean expressions.
        var symbols = new VariableCollection(null, parameters.ToDictionary(parameter => parameter.Name, _ => (object)string.Empty));
        var dependencies = new Dictionary<ITemplateParameter, HashSet<string>>();
        foreach (var parameter in parameters)
        {
            var references = new HashSet<string>(StringComparer.Ordinal);
            foreach (var condition in new[] { parameter.Precedence.IsEnabledCondition, parameter.Precedence.IsRequiredCondition })
            {
                if (string.IsNullOrEmpty(condition))
                {
                    continue;
                }

                if (Cpp2StyleEvaluatorDefinition.GetEvaluableExpression(_logger, condition, symbols, out var error, references) is null ||
                    !string.IsNullOrEmpty(error))
                {
                    throw new TemplateAuthoringException($"{parameter.Name}: {error ?? condition}", parameter.Name);
                }
            }
            if (references.Overlaps(_computedParameters))
            {
                // NuGet policy can depend on answers, so computed values are resolved after
                // prompting. They cannot also control that earlier prompting phase.
                throw new TemplateAuthoringException(
                    string.Format(CultureInfo.CurrentCulture, TemplatingStrings.ComputedTemplateParameterCondition, parameter.Name),
                    parameter.Name);
            }
            references.Remove(parameter.Name);
            dependencies[parameter] = references;
        }

        var remaining = parameters.ToList();
        var ordered = new List<ITemplateParameter>(parameters.Length);
        while (remaining.Count > 0)
        {
            var next = remaining.FindIndex(parameter => !remaining.Any(other => dependencies[parameter].Contains(other.Name)));
            if (next < 0)
            {
                // Cyclic conditions have no dependency-first prompt order. Keep declaration order;
                // TemplateCreator validates whether the final values form a valid configuration.
                _logger.LogWarning("Template parameter conditions are cyclic; using declaration order for {Parameters}.",
                    string.Join(", ", remaining.Select(parameter => parameter.Name)));
                ordered.AddRange(remaining);
                break;
            }

            ordered.Add(remaining[next]);
            remaining.RemoveAt(next);
        }

        return ordered;
    }
}
