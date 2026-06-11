// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Text.Json.Nodes;
using Aspire.TypeSystem;

namespace Aspire.Hosting.RemoteHost.Ats;

/// <summary>
/// Reference to a <c>ReferenceExpression</c> in the ATS protocol.
/// Used when passing reference expressions as arguments.
/// </summary>
/// <remarks>
/// <para>
/// Reference expressions are serialized in JSON using the <c>$expr</c> marker in two shapes:
/// </para>
/// <para><b>Value mode</b> — a format string with optional value-provider placeholders:</para>
/// <code>
/// {
///   "$expr": {
///     "format": "redis://{0}:{1}",
///     "valueProviders": [
///       { "$handle": "Aspire.Hosting.ApplicationModel/EndpointReference:1" },
///       "6379"
///     ]
///   }
/// }
/// </code>
/// <para><b>Conditional mode</b> — a ternary expression selecting between two branch expressions:</para>
/// <code>
/// {
///   "$expr": {
///     "condition": { "$handle": "Aspire.Hosting.ApplicationModel/EndpointReferenceExpression:1" },
///     "matchValue": "true",
///     "whenTrue": { "$expr": { "format": ",ssl=true" } },
///     "whenFalse": { "$expr": { "format": "" } }
///   }
/// }
/// </code>
/// <para>
/// The presence of a <c>condition</c> property inside the <c>$expr</c> object distinguishes
/// conditional mode from value mode.
/// </para>
/// </remarks>
internal sealed class ReferenceExpressionRef
{
    // Value mode fields
    public string? Format { get; init; }
    public JsonNode?[]? ValueProviders { get; init; }

    // Conditional mode fields
    public JsonNode? Condition { get; init; }
    public JsonNode? WhenTrue { get; init; }
    public JsonNode? WhenFalse { get; init; }
    public string? MatchValue { get; init; }

    /// <summary>
    /// Gets a value indicating whether this reference represents a conditional expression.
    /// </summary>
    public bool IsConditional => Condition is not null;

    /// <summary>
    /// Creates a ReferenceExpressionRef from a JSON node if it contains a $expr property.
    /// Handles both value mode (format + valueProviders) and conditional mode (condition + whenTrue + whenFalse).
    /// </summary>
    /// <param name="node">The JSON node to parse.</param>
    /// <returns>A ReferenceExpressionRef if the node represents an expression, otherwise null.</returns>
    public static ReferenceExpressionRef? FromJsonNode(JsonNode? node)
    {
        if (node is not JsonObject obj || !obj.TryGetPropertyValue("$expr", out var exprNode))
        {
            return null;
        }

        if (exprNode is not JsonObject exprObj)
        {
            return null;
        }

        // Check for conditional mode: presence of "condition" property
        if (exprObj.TryGetPropertyValue("condition", out var conditionNode))
        {
            exprObj.TryGetPropertyValue("whenTrue", out var whenTrueNode);
            exprObj.TryGetPropertyValue("whenFalse", out var whenFalseNode);

            string? matchValue = null;
            if (exprObj.TryGetPropertyValue("matchValue", out var matchValueNode) &&
                matchValueNode is JsonValue matchValueJsonValue &&
                matchValueJsonValue.TryGetValue<string>(out var mv))
            {
                matchValue = mv;
            }

            return new ReferenceExpressionRef
            {
                Condition = conditionNode,
                WhenTrue = whenTrueNode,
                WhenFalse = whenFalseNode,
                MatchValue = matchValue
            };
        }

        // Value mode: format + optional valueProviders
        if (!exprObj.TryGetPropertyValue("format", out var formatNode) ||
            formatNode is not JsonValue formatValue ||
            !formatValue.TryGetValue<string>(out var format))
        {
            return null;
        }

        // Get value providers (optional)
        JsonNode?[]? valueProviders = null;
        if (exprObj.TryGetPropertyValue("valueProviders", out var providersNode) &&
            providersNode is JsonArray providersArray)
        {
            valueProviders = new JsonNode?[providersArray.Count];
            for (int i = 0; i < providersArray.Count; i++)
            {
                valueProviders[i] = providersArray[i];
            }
        }

        return new ReferenceExpressionRef
        {
            Format = format,
            ValueProviders = valueProviders
        };
    }

    /// <summary>
    /// Checks if a JSON node is a reference expression.
    /// </summary>
    /// <param name="node">The JSON node to check.</param>
    /// <returns>True if the node contains a $expr property.</returns>
    public static bool IsReferenceExpressionRef(JsonNode? node)
    {
        return node is JsonObject obj && obj.ContainsKey("$expr");
    }

    /// <summary>
    /// Creates a <c>ReferenceExpression</c> from this reference by resolving handles.
    /// Handles both value mode and conditional mode.
    /// </summary>
    /// <param name="handles">The handle registry to resolve handles from.</param>
    /// <param name="capabilityId">The capability ID for error messages.</param>
    /// <param name="paramName">The parameter name for error messages.</param>
    /// <returns>A constructed <c>ReferenceExpression</c>.</returns>
    /// <exception cref="CapabilityException">Thrown if handles cannot be resolved or are invalid types.</exception>
    public object ToReferenceExpression(
        HandleRegistry handles,
        string capabilityId,
        string paramName)
    {
        if (IsConditional)
        {
            return ToConditionalReferenceExpression(handles, capabilityId, paramName);
        }

        return ToValueReferenceExpression(handles, capabilityId, paramName);
    }

    private object ToConditionalReferenceExpression(
        HandleRegistry handles,
        string capabilityId,
        string paramName)
    {
        // Resolve the condition handle to an IValueProvider
        var conditionHandleRef = HandleRef.FromJsonNode(Condition)
            ?? throw CapabilityException.InvalidArgument(capabilityId, $"{paramName}.condition",
                "Condition must be a handle reference ({ $handle: \"...\" })");

        if (!handles.TryGet(conditionHandleRef.HandleId, out var conditionObj, out _))
        {
            throw CapabilityException.HandleNotFound(conditionHandleRef.HandleId, capabilityId);
        }

        var valueProviderType = GetRequiredHostingType(HostingTypeNames.ValueProviderInterface, conditionObj);
        if (conditionObj is null || !valueProviderType.IsAssignableFrom(conditionObj.GetType()))
        {
            throw CapabilityException.InvalidArgument(capabilityId, $"{paramName}.condition",
                $"Condition handle must resolve to an IValueProvider, got {conditionObj?.GetType().Name ?? "null"}");
        }

        // Resolve whenTrue as a ReferenceExpression
        var whenTrueExprRef = FromJsonNode(WhenTrue)
            ?? throw CapabilityException.InvalidArgument(capabilityId, $"{paramName}.whenTrue",
                "whenTrue must be a reference expression ({ $expr: { ... } })");
        var whenTrue = whenTrueExprRef.ToReferenceExpression(handles, capabilityId, $"{paramName}.whenTrue");

        // Resolve whenFalse as a ReferenceExpression
        var whenFalseExprRef = FromJsonNode(WhenFalse)
            ?? throw CapabilityException.InvalidArgument(capabilityId, $"{paramName}.whenFalse",
                "whenFalse must be a reference expression ({ $expr: { ... } })");
        var whenFalse = whenFalseExprRef.ToReferenceExpression(handles, capabilityId, $"{paramName}.whenFalse");

        return CreateConditionalReferenceExpression(conditionObj, MatchValue ?? bool.TrueString, whenTrue, whenFalse);
    }

    private object ToValueReferenceExpression(
        HandleRegistry handles,
        string capabilityId,
        string paramName)
    {
        var builder = CreateReferenceExpressionBuilder();

        if (ValueProviders == null || ValueProviders.Length == 0)
        {
            // No value providers - just a literal string
            AppendLiteral(builder, Format!);
        }
        else
        {
            // Resolve each value provider - can be handles or literal strings
            var valueProviders = new object?[ValueProviders.Length];
            for (int i = 0; i < ValueProviders.Length; i++)
            {
                var providerNode = ValueProviders[i];

                // Try to parse as a handle reference
                var handleRef = HandleRef.FromJsonNode(providerNode);
                if (handleRef != null)
                {
                    if (!handles.TryGet(handleRef.HandleId, out var obj, out _))
                    {
                        throw CapabilityException.HandleNotFound(handleRef.HandleId, capabilityId);
                    }
                    valueProviders[i] = obj;
                }
                else if (providerNode is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var stringValue))
                {
                    // Literal string value - will be appended directly to the expression
                    valueProviders[i] = stringValue;
                }
                else
                {
                    throw CapabilityException.InvalidArgument(capabilityId, $"{paramName}.valueProviders[{i}]",
                        "Value provider must be a handle reference ({ $handle: \"...\" }) or a string literal");
                }
            }

            // Parse the format string and interleave with value providers
            var parts = SplitFormatString(Format!);
            foreach (var part in parts)
            {
                if (part.StartsWith("{") && part.EndsWith("}") &&
                    int.TryParse(part[1..^1], out var index) &&
                    index >= 0 && index < valueProviders.Length)
                {
                    var provider = valueProviders[index];
                    if (provider is string literalString)
                    {
                        // String value providers are treated as literals
                        AppendLiteral(builder, literalString);
                    }
                    else if (provider != null)
                    {
                        // Object value providers (handles) are appended as value providers
                        AppendValueProvider(builder, provider);
                    }
                }
                else
                {
                    AppendLiteral(builder, part);
                }
            }
        }

        return BuildReferenceExpression(builder);
    }

    /// <summary>
    /// Splits a format string into literal parts and placeholders.
    /// </summary>
    /// <remarks>
    /// Given "redis://{0}:{1}", returns ["redis://", "{0}", ":", "{1}"].
    /// </remarks>
    private static string[] SplitFormatString(string format)
    {
        var parts = new List<string>();
        var current = 0;

        while (current < format.Length)
        {
            var start = format.IndexOf('{', current);
            if (start < 0)
            {
                // No more placeholders - add the rest as a literal
                if (current < format.Length)
                {
                    parts.Add(format[current..]);
                }
                break;
            }

            // Add the literal part before the placeholder (if any)
            if (start > current)
            {
                parts.Add(format[current..start]);
            }

            // Find the end of the placeholder
            var end = format.IndexOf('}', start);
            if (end < 0)
            {
                // No closing brace - treat the rest as a literal
                parts.Add(format[start..]);
                break;
            }

            // Add the placeholder
            parts.Add(format[start..(end + 1)]);
            current = end + 1;
        }

        return [.. parts];
    }

    private static object CreateReferenceExpressionBuilder()
    {
        var builderType = GetRequiredHostingType(HostingTypeNames.ReferenceExpressionBuilder);
        return Activator.CreateInstance(builderType)
            ?? throw new InvalidOperationException($"Failed to create '{builderType.FullName}'.");
    }

    private static void AppendLiteral(object builder, string value)
    {
        var appendLiteralMethod = builder.GetType().GetMethod(
            "AppendLiteral",
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            [typeof(string)],
            modifiers: null)
            ?? throw new InvalidOperationException($"'{builder.GetType().FullName}' is missing AppendLiteral(string).");

        appendLiteralMethod.Invoke(builder, [value]);
    }

    private static void AppendValueProvider(object builder, object valueProvider)
    {
        var appendValueProviderMethod = builder.GetType().GetMethod(
            "AppendValueProvider",
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            [typeof(object), typeof(string)],
            modifiers: null)
            ?? throw new InvalidOperationException($"'{builder.GetType().FullName}' is missing AppendValueProvider(object, string).");

        appendValueProviderMethod.Invoke(builder, [valueProvider, null]);
    }

    private static object BuildReferenceExpression(object builder)
    {
        var buildMethod = builder.GetType().GetMethod("Build", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException($"'{builder.GetType().FullName}' is missing Build().");

        return buildMethod.Invoke(builder, null)
            ?? throw new InvalidOperationException($"'{builder.GetType().FullName}.Build()' returned null.");
    }

    private static object CreateConditionalReferenceExpression(
        object condition,
        string matchValue,
        object whenTrue,
        object whenFalse)
    {
        var referenceExpressionType = GetRequiredHostingType(HostingTypeNames.ReferenceExpression, condition);
        var valueProviderType = GetRequiredHostingType(HostingTypeNames.ValueProviderInterface, condition);

        var createConditionalMethod = referenceExpressionType.GetMethod(
            "CreateConditional",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            [valueProviderType, typeof(string), referenceExpressionType, referenceExpressionType],
            modifiers: null)
            ?? throw new InvalidOperationException($"'{referenceExpressionType.FullName}' is missing CreateConditional(...).");

        return createConditionalMethod.Invoke(null, [condition, matchValue, whenTrue, whenFalse])
            ?? throw new InvalidOperationException($"'{referenceExpressionType.FullName}.CreateConditional(...)' returned null.");
    }

    private static Type GetRequiredHostingType(string fullName, object? anchor = null) =>
        FindHostingType(fullName, anchor) ??
        throw new InvalidOperationException($"Could not resolve runtime type '{fullName}'.");

    private static Type? FindHostingType(string fullName, object? anchor = null)
    {
        if (anchor is not null)
        {
            var anchoredType = anchor.GetType().Assembly.GetType(fullName, throwOnError: false);
            if (anchoredType is not null)
            {
                return anchoredType;
            }
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = assembly.GetType(fullName, throwOnError: false);
            if (type is not null)
            {
                return type;
            }
        }

        return null;
    }
}
