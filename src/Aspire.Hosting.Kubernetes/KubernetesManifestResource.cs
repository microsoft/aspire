// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.Kubernetes.Resources;

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// Represents an arbitrary Kubernetes manifest that is emitted with a Kubernetes service.
/// </summary>
[AspireExport]
internal sealed class KubernetesManifestResource : BaseKubernetesResource
{
    internal KubernetesManifestResource(string apiVersion, string kind, string name)
        : base(apiVersion, kind)
    {
        Metadata.Name = name;
    }

    internal Dictionary<string, object?> Fields { get; } = [];

    /// <summary>
    /// Sets the namespace for this manifest.
    /// </summary>
    /// <param name="namespace">The Kubernetes namespace.</param>
    /// <returns>This manifest resource.</returns>
    [AspireExport(Description = "Sets the Kubernetes namespace for this manifest")]
    public KubernetesManifestResource WithNamespace(string @namespace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(@namespace);

        Metadata.Namespace = @namespace;

        return this;
    }

    /// <summary>
    /// Adds or updates a Kubernetes label on this manifest.
    /// </summary>
    /// <param name="key">The label key.</param>
    /// <param name="value">The label value.</param>
    /// <returns>This manifest resource.</returns>
    [AspireExport(Description = "Adds or updates a Kubernetes label on this manifest")]
    public KubernetesManifestResource WithLabel(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        Metadata.Labels[key] = value;

        return this;
    }

    /// <summary>
    /// Adds or updates a Kubernetes annotation on this manifest.
    /// </summary>
    /// <param name="key">The annotation key.</param>
    /// <param name="value">The annotation value.</param>
    /// <returns>This manifest resource.</returns>
    [AspireExport(Description = "Adds or updates a Kubernetes annotation on this manifest")]
    public KubernetesManifestResource WithAnnotation(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        Metadata.Annotations[key] = value;

        return this;
    }

    /// <summary>
    /// Adds or updates a manifest field using a dot-separated path.
    /// </summary>
    /// <remarks>
    /// Polyglot SDKs currently support scalar field values only. Use dot-separated paths to build nested objects with
    /// scalar leaf values; array values are not supported by this method.
    /// </remarks>
    /// <param name="path">The dot-separated manifest field path, for example <c>spec.replicas</c> or <c>data.username</c>.</param>
    /// <param name="value">The field value.</param>
    /// <returns>This manifest resource.</returns>
    [AspireExport(Description = "Adds or updates a manifest field using a dot-separated path")]
    public KubernetesManifestResource WithField(
        string path,
        [AspireUnion(typeof(string), typeof(double), typeof(bool))] object value)
    {
        var segments = ParseFieldPath(path);

        SetField(Fields, segments, path, NormalizeManifestValue(value));

        return this;
    }

    private static string[] ParseFieldPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            throw new ArgumentException("Manifest field path must contain at least one segment.", nameof(path));
        }

        if (segments[0] is "apiVersion" or "kind" or "metadata")
        {
            throw new ArgumentException("Use the dedicated manifest API to configure apiVersion, kind, and metadata fields.", nameof(path));
        }

        return segments;
    }

    private static void SetField(Dictionary<string, object?> fields, ReadOnlySpan<string> segments, string path, object? value)
    {
        var current = fields;

        for (var i = 0; i < segments.Length - 1; i++)
        {
            var segment = segments[i];
            if (current.TryGetValue(segment, out var child))
            {
                if (child is not Dictionary<string, object?> childFields)
                {
                    throw new ArgumentException($"Cannot set nested manifest field '{path}' because '{segment}' already has a scalar value.", nameof(path));
                }

                current = childFields;
            }
            else
            {
                var childFields = new Dictionary<string, object?>(StringComparer.Ordinal);
                current[segment] = childFields;
                current = childFields;
            }
        }

        current[segments[^1]] = value;
    }

    internal static object? NormalizeManifestValue(object? value)
    {
        return value switch
        {
            null => null,
            string => value,
            bool => value,
            byte or sbyte or short or ushort or int or uint or long or ulong => value,
            float floatValue => NormalizeFloatingPointValue(floatValue),
            double doubleValue => NormalizeFloatingPointValue(doubleValue),
            decimal decimalValue => NormalizeDecimalValue(decimalValue),
            JsonElement element => NormalizeJsonElement(element),
            JsonNode node => NormalizeJsonNode(node),
            IDictionary dictionary => NormalizeDictionary(dictionary),
            IEnumerable enumerable when value is not string => NormalizeEnumerable(enumerable),
            _ => throw new ArgumentException($"Manifest field values must be JSON-compatible primitives, dictionaries, or arrays. Type '{value.GetType()}' is not supported.", nameof(value))
        };
    }

    private const double MaxLongExclusiveAsDouble = 9_223_372_036_854_775_808d;

    private static object NormalizeFloatingPointValue(float value)
    {
        if (!float.IsFinite(value))
        {
            throw new ArgumentException("Manifest field numeric values must be finite.", nameof(value));
        }

        if (MathF.Truncate(value) == value)
        {
            return ConvertWholeNumberToLong(value);
        }

        return value;
    }

    private static object NormalizeFloatingPointValue(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentException("Manifest field numeric values must be finite.", nameof(value));
        }

        if (Math.Truncate(value) == value)
        {
            return ConvertWholeNumberToLong(value);
        }

        return value;
    }

    private static object NormalizeDecimalValue(decimal value)
    {
        if (decimal.Truncate(value) == value && value >= long.MinValue && value <= long.MaxValue)
        {
            return decimal.ToInt64(value);
        }

        return value;
    }

    private static long ConvertWholeNumberToLong(double value)
    {
        if (value < long.MinValue || value >= MaxLongExclusiveAsDouble)
        {
            throw new ArgumentException($"Whole-number manifest field values must be between {long.MinValue} and {long.MaxValue}.", nameof(value));
        }

        return (long)value;
    }

    private static object? NormalizeJsonNode(JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());

        return NormalizeJsonElement(document.RootElement);
    }

    private static object? NormalizeJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.Array => element.EnumerateArray().Select(NormalizeJsonElement).ToList(),
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(prop => prop.Name, prop => NormalizeJsonElement(prop.Value), StringComparer.Ordinal),
            _ => throw new ArgumentException($"Unsupported JSON value kind '{element.ValueKind}'.", nameof(element))
        };
    }

    private static Dictionary<string, object?> NormalizeDictionary(IDictionary dictionary)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (DictionaryEntry entry in dictionary)
        {
            if (entry.Key is not string key)
            {
                throw new ArgumentException("Manifest field dictionaries must use string keys.");
            }

            result[key] = NormalizeManifestValue(entry.Value);
        }

        return result;
    }

    private static List<object?> NormalizeEnumerable(IEnumerable enumerable)
    {
        var result = new List<object?>();

        foreach (var item in enumerable)
        {
            result.Add(NormalizeManifestValue(item));
        }

        return result;
    }
}
