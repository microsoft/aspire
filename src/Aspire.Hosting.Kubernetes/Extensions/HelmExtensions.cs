// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using YamlDotNet.Core;

namespace Aspire.Hosting.Kubernetes.Extensions;
internal static partial class HelmExtensions
{
    private const string DeploymentKey = "deployment";
    private const string StatefulSetKey = "statefulset";
    private const string ServiceKey = "service";
    private const string PvcKey = "pvc";
    private const string PvKey = "pv";
    private const string ValuesSegment = ".Values";
    public const string ParametersKey = "parameters";
    public const string SecretsKey = "secrets";
    public const string ConfigKey = "config";
    public const string TemplateFileSeparator = "---";

    public const string StartDelimiter = "{{";
    public const string EndDelimiter = "}}";
    public const string PipelineDelimiter = "|";

    /// <summary>
    /// Converts the specified resource name into a Helm configuration section name.
    /// </summary>
    /// <remarks>
    /// The section names in Helm values files can not contain hyphens ('-').
    /// <see href="link">https://helm.sh/docs/chart_best_practices/values/</see>
    /// </remarks>
    public static string ToHelmValuesSectionName(this string resourceName)
        => $"{resourceName.Replace("-", "_")}";

    public static string ToHelmExpression(this string expression)
        => $"{StartDelimiter} {expression} {EndDelimiter}";

    public static string ToHelmParameterExpression(this string parameterName, string resourceName)
        => $"{ValuesSegment}.{ParametersKey}.{resourceName}.{parameterName}".ToHelmValuesSectionName().ToHelmExpression();

    public static string ToHelmSecretExpression(this string parameterName, string resourceName)
        => $"{ValuesSegment}.{SecretsKey}.{resourceName}.{parameterName}".ToHelmValuesSectionName().ToHelmExpression();

    public static string ToHelmConfigExpression(this string parameterName, string resourceName)
        => $"{ValuesSegment}.{ConfigKey}.{resourceName}.{parameterName}".ToHelmValuesSectionName().ToHelmExpression();

    public static string ToHelmChartName(this string applicationName)
        => applicationName.ToLower().Replace("_", "-").Replace(".", "-");

    /// <summary>
    /// Converts the specified resource name into a Kubernetes resource name.
    /// </summary>
    /// <remarks>
    /// Kubernetes resource object names can only contain lowercase alphanumeric characters, '-', and '.'.
    /// <see href="link">https://kubernetes.io/docs/concepts/overview/working-with-objects/names/</see>
    /// </remarks>
    public static string ToKubernetesResourceName(this string resourceName)
        => $"{resourceName.ToLowerInvariant()}";

    public static string ToConfigMapName(this string resourceName)
        => $"{resourceName.ToKubernetesResourceName()}-{ConfigKey}";

    public static string ToSecretName(this string resourceName)
        => $"{resourceName.ToKubernetesResourceName()}-{SecretsKey}";

    public static string ToDeploymentName(this string resourceName)
        => $"{resourceName.ToKubernetesResourceName()}-{DeploymentKey}";

    public static string ToStatefulSetName(this string resourceName)
        => $"{resourceName.ToKubernetesResourceName()}-{StatefulSetKey}";

    public static string ToServiceName(this string resourceName)
        => $"{resourceName.ToKubernetesResourceName()}-{ServiceKey}";

    public static string ToPvcName(this string resourceName, string volumeName)
        => $"{resourceName.ToKubernetesResourceName()}-{volumeName}-{PvcKey}";

    public static string ToPvName(this string resourceName, string volumeName)
        => $"{resourceName.ToKubernetesResourceName()}-{volumeName}-{PvKey}";

    public static bool ContainsHelmExpression(this string value)
        => ExpressionPattern().IsMatch(value);

    public static bool ContainsHelmValuesExpression(this string value)
        => ExpressionPattern().IsMatch(value)
        && value.Contains($"{ValuesSegment}.", StringComparison.Ordinal);

    public static bool ContainsHelmValuesSecretExpression(this string value)
        => ExpressionPattern().IsMatch(value)
        && value.Contains($"{ValuesSegment}.{SecretsKey}.", StringComparison.Ordinal);

    public static (bool, ScalarStyle?) ShouldDoubleQuoteString(string value)
    {
        // Flow control expressions (if/else) must be rendered as plain YAML so Helm
        // can process them as template expressions without YAML escaping. This check
        // runs first because if/else blocks contain multiple {{ }} pairs and won't
        // match ScalarExpressionPattern.
        if (HelmFlowControlPattern().IsMatch(value))
        {
            return (false, ScalarStyle.ForcePlain);
        }

        if (!ScalarExpressionPattern().IsMatch(value))
        {
            return (true, null);
        }

        // Scalar Helm expressions that contain type conversions (| int, | float64, etc.)
        // must be rendered as plain (unquoted) YAML so that Helm can process them as
        // template expressions without YAML escaping interference.
        if (EndWithNonStringTypePattern().IsMatch(value))
        {
            return (false, ScalarStyle.ForcePlain);
        }

        return (true, null);
    }

    [GeneratedRegex(@"^\{\{\s*if\b")]
    internal static partial Regex HelmFlowControlPattern();

    [GeneratedRegex(@"\{\{[^}]*\|\s*(int|int64|float64)\s*\}\}")]
    private static partial Regex EndWithNonStringTypePattern();

    [GeneratedRegex(@"((?<=^\{\{\s*)(?:[^{}]+?)(?=(?:\}\}$)))")]
    internal static partial Regex ScalarExpressionPattern();

    [GeneratedRegex(@"((?<=\{\{\s*)(?:[^{}]+?)(?=(?:\}\})))")]
    internal static partial Regex ExpressionPattern();

}
