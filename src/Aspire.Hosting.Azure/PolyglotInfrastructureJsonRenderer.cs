// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json.Nodes;

namespace Aspire.Hosting.Azure;

internal static class PolyglotInfrastructureJsonRenderer
{
    public static string RenderBicepTemplate(string infrastructureJson)
    {
        ArgumentException.ThrowIfNullOrEmpty(infrastructureJson);

        var root = JsonNode.Parse(infrastructureJson)?.AsObject()
            ?? throw new InvalidOperationException("The infrastructure JSON document is empty.");
        var infras = root["infras"]?.AsArray()
            ?? throw new InvalidOperationException("The infrastructure JSON document must contain an 'infras' array.");

        if (infras.Count != 1)
        {
            throw new InvalidOperationException($"Expected exactly one infrastructure entry, but found {infras.Count}.");
        }

        var infrastructure = infras[0]?.AsObject()
            ?? throw new InvalidOperationException("The infrastructure entry must be a JSON object.");

        var sections = new List<string>();

        if (TryGetString(infrastructure, "targetScope", out var targetScope))
        {
            sections.Add($"targetScope = {RenderString(targetScope)}");
        }

        AddStatements(sections, infrastructure["parameters"] as JsonObject, RenderParameter);
        AddStatements(sections, infrastructure["variables"] as JsonObject, RenderVariable);
        AddStatements(sections, infrastructure["resources"] as JsonObject, RenderResource);
        AddStatements(sections, infrastructure["outputs"] as JsonObject, RenderOutput);

        return string.Join(Environment.NewLine + Environment.NewLine, sections) + Environment.NewLine;
    }

    private static void AddStatements(List<string> sections, JsonObject? statements, Func<JsonObject, string> render)
    {
        if (statements is null)
        {
            return;
        }

        foreach (var (_, value) in statements)
        {
            if (value is JsonObject statement)
            {
                sections.Add(render(statement));
            }
        }
    }

    private static string RenderParameter(JsonObject parameter)
    {
        var builder = new StringBuilder();

        if (parameter["decorators"] is JsonObject decorators)
        {
            RenderDecorators(builder, decorators);
        }

        var identifier = GetRequiredString(parameter, "bicepIdentifier");
        var parameterType = RenderType(parameter["valueType"]?.AsObject()
            ?? throw new InvalidOperationException($"Parameter '{identifier}' is missing 'valueType'."));

        builder.Append("param ");
        builder.Append(identifier);
        builder.Append(' ');
        builder.Append(parameterType);

        if (parameter["defaultValue"] is JsonObject defaultValue)
        {
            builder.Append(" = ");
            builder.Append(RenderExpression(defaultValue));
        }

        return builder.ToString();
    }

    private static string RenderVariable(JsonObject variable)
    {
        var identifier = GetRequiredString(variable, "bicepIdentifier");
        var value = variable["value"]?.AsObject()
            ?? throw new InvalidOperationException($"Variable '{identifier}' is missing 'value'.");

        return $"var {identifier} = {RenderExpression(value)}";
    }

    private static string RenderResource(JsonObject resource)
    {
        var identifier = GetRequiredString(resource, "bicepIdentifier");
        var type = GetRequiredString(resource, "type");
        var apiVersion = GetRequiredString(resource, "apiVersion");
        var existing = resource["existing"]?.GetValue<bool>() ?? false;
        var value = resource["value"]?.AsObject()
            ?? throw new InvalidOperationException($"Resource '{identifier}' is missing 'value'.");

        return $"resource {identifier} '{type}@{apiVersion}'{(existing ? " existing" : "")} = {RenderExpression(value)}";
    }

    private static string RenderOutput(JsonObject output)
    {
        var identifier = GetRequiredString(output, "bicepIdentifier");
        var outputType = RenderType(output["valueType"]?.AsObject()
            ?? throw new InvalidOperationException($"Output '{identifier}' is missing 'valueType'."));
        var value = output["value"]?.AsObject()
            ?? throw new InvalidOperationException($"Output '{identifier}' is missing 'value'.");

        return $"output {identifier} {outputType} = {RenderExpression(value)}";
    }

    private static void RenderDecorators(StringBuilder builder, JsonObject decorators)
    {
        foreach (var (name, value) in decorators)
        {
            switch (name)
            {
                case "description" when value is not null:
                    builder.Append("@description(");
                    builder.Append(RenderString(value.GetValue<string>()));
                    builder.AppendLine(")");
                    break;
                case "secure" when value?.GetValue<bool>() == true:
                    builder.AppendLine("@secure()");
                    break;
                default:
                    throw new InvalidOperationException($"Decorator '{name}' is not supported by the polyglot JSON renderer.");
            }
        }
    }

    private static string RenderType(JsonObject type)
    {
        var kind = GetRequiredString(type, "kind");
        return kind switch
        {
            "primitive-type" => GetRequiredString(type, "name"),
            _ => throw new InvalidOperationException($"Type kind '{kind}' is not supported by the polyglot JSON renderer.")
        };
    }

    private static string RenderExpression(JsonObject expression, int indent = 0)
    {
        var kind = GetRequiredString(expression, "kind");

        return kind switch
        {
            "array" => RenderArrayExpression(expression, indent),
            "boolean" => expression["value"]?.GetValue<bool>() == true ? "true" : "false",
            "contextual-variable" => $"{GetRequiredString(expression, "context")}().{GetRequiredString(expression, "property")}",
            "function-call" => RenderFunctionCall(expression),
            "identifier" => GetRequiredString(expression, "id"),
            "integer" => GetRequiredString(expression, "value"),
            "interpolated-string" => RenderInterpolatedString(expression),
            "object" => RenderObjectExpression(expression, indent),
            "property-access" => $"{RenderExpression(expression["base"]?.AsObject() ?? throw new InvalidOperationException("Property access is missing 'base'."), indent)}.{GetRequiredString(expression, "property")}",
            "string" => RenderString(GetRequiredString(expression, "value")),
            _ => throw new InvalidOperationException($"Expression kind '{kind}' is not supported by the polyglot JSON renderer.")
        };
    }

    private static string RenderFunctionCall(JsonObject expression)
    {
        var target = GetRequiredString(expression, "target");
        var arguments = expression["args"]?.AsArray() ?? [];

        return $"{target}({string.Join(", ", arguments.Select(argument => RenderExpression(argument?.AsObject() ?? throw new InvalidOperationException($"Function '{target}' contains an invalid argument."))))})";
    }

    private static string RenderInterpolatedString(JsonObject expression)
    {
        var segments = expression["segments"]?.AsArray()
            ?? throw new InvalidOperationException("Interpolated string is missing 'segments'.");
        var builder = new StringBuilder();
        builder.Append('\'');

        foreach (var segmentNode in segments)
        {
            var segment = segmentNode?.AsObject()
                ?? throw new InvalidOperationException("Interpolated string contains an invalid segment.");

            if (GetRequiredString(segment, "kind") is "string")
            {
                builder.Append(EscapeString(GetRequiredString(segment, "value")));
            }
            else
            {
                builder.Append("${");
                builder.Append(RenderExpression(segment));
                builder.Append('}');
            }
        }

        builder.Append('\'');
        return builder.ToString();
    }

    private static string RenderObjectExpression(JsonObject expression, int indent)
    {
        var value = expression["value"]?.AsObject()
            ?? throw new InvalidOperationException("Object expression is missing 'value'.");
        var builder = new StringBuilder();
        builder.AppendLine("{");

        foreach (var (name, propertyValue) in value)
        {
            var propertyExpression = propertyValue?.AsObject()
                ?? throw new InvalidOperationException($"Object property '{name}' must be a JSON object.");

            builder.Append(Indent(indent + 1));
            builder.Append(name);
            builder.Append(": ");
            builder.Append(RenderExpression(propertyExpression, indent + 1));
            builder.AppendLine();
        }

        builder.Append(Indent(indent));
        builder.Append('}');
        return builder.ToString();
    }

    private static string RenderArrayExpression(JsonObject expression, int indent)
    {
        var items = expression["value"]?.AsArray()
            ?? throw new InvalidOperationException("Array expression is missing 'value'.");

        if (items.Count == 0)
        {
            return "[]";
        }

        var builder = new StringBuilder();
        builder.AppendLine("[");

        foreach (var itemNode in items)
        {
            var item = itemNode?.AsObject()
                ?? throw new InvalidOperationException("Array expression contains an invalid item.");
            builder.Append(Indent(indent + 1));
            builder.Append(RenderExpression(item, indent + 1));
            builder.AppendLine();
        }

        builder.Append(Indent(indent));
        builder.Append(']');
        return builder.ToString();
    }

    private static string RenderString(string value) => $"'{EscapeString(value)}'";

    private static string EscapeString(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string Indent(int depth) => new(' ', depth * 2);

    private static string GetRequiredString(JsonObject jsonObject, string name) =>
        TryGetString(jsonObject, name, out var value)
            ? value
            : throw new InvalidOperationException($"The required property '{name}' is missing.");

    private static bool TryGetString(JsonObject jsonObject, string name, out string value)
    {
        if (jsonObject[name] is JsonValue jsonValue && jsonValue.TryGetValue<string>(out value!))
        {
            return true;
        }

        value = string.Empty;
        return false;
    }
}
