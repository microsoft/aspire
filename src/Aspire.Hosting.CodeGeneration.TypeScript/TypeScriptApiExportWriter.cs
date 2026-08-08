// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aspire.Hosting.CodeGeneration.TypeScript;

/// <summary>
/// Serializes a <see cref="TypeScriptApiModel"/> into the canonical schema version 1 export document.
/// </summary>
/// <remarks>
/// The document is written by hand rather than through reflection-based serialization because the
/// shape is a published contract that documentation sites bind to. Writing it explicitly makes the
/// contract reviewable in one place, keeps property order stable so exports diff cleanly between SDK
/// versions, and drops empty collections and null strings so a version bump only shows real API
/// changes.
/// </remarks>
internal static class TypeScriptApiExportWriter
{
    /// <summary>The schema version emitted by this writer.</summary>
    public const int SchemaVersion = TypeScriptApiProjector.ExportSchemaVersion;

    public static JsonObject Write(TypeScriptApiModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var modules = new JsonArray();
        foreach (var module in model.Modules)
        {
            modules.Add((JsonNode)WriteModule(module));
        }

        var declarations = new JsonArray();
        foreach (var declaration in model.Declarations)
        {
            declarations.Add((JsonNode)new JsonObject
            {
                ["id"] = declaration.Id,
                ["owningAssembly"] = declaration.OwningAssemblyName,
                ["content"] = declaration.Content
            });
        }

        return new JsonObject
        {
            ["schemaVersion"] = model.SchemaVersion,
            ["language"] = model.Language,
            ["package"] = new JsonObject
            {
                ["name"] = model.Package.Name,
                ["version"] = model.Package.Version
            },
            ["modules"] = modules,
            ["declarations"] = declarations
        };
    }

    /// <summary>
    /// Serializes the export document to UTF-8 JSON text.
    /// </summary>
    /// <param name="model">The model to serialize.</param>
    /// <param name="indented">
    /// When <see langword="true"/>, writes human-readable JSON. Machine consumers use the compact
    /// form so the document is a single line on stdout.
    /// </param>
    public static string WriteToJson(TypeScriptApiModel model, bool indented = false)
        => Write(model).ToJsonString(new JsonSerializerOptions { WriteIndented = indented });

    private static JsonObject WriteModule(TypeScriptApiModule module)
    {
        var items = new JsonArray();
        foreach (var item in module.Items)
        {
            items.Add((JsonNode)WriteItem(item));
        }

        var json = new JsonObject { ["name"] = module.Name };
        AddIfPresent(json, "summary", module.Summary);
        json["items"] = items;
        return json;
    }

    private static JsonObject WriteItem(TypeScriptApiItem item)
    {
        var json = new JsonObject
        {
            ["id"] = item.Id,
            ["kind"] = ToKindString(item.Kind),
            ["name"] = item.Name,
            ["typeId"] = item.TypeId,
            ["owningAssembly"] = item.OwningAssemblyName,
            ["declaration"] = item.Declaration
        };

        AddIfPresent(json, "summary", item.Summary);
        AddIfPresent(json, "remarks", item.Remarks);
        AddIfPresent(json, "examples", item.Examples);
        AddIfPresent(json, "extends", item.Extends);

        if (item.Members.Count > 0)
        {
            var members = new JsonArray();
            foreach (var member in item.Members)
            {
                members.Add((JsonNode)WriteMember(member));
            }

            json["members"] = members;
        }

        return json;
    }

    private static JsonObject WriteMember(TypeScriptApiMember member)
    {
        var json = new JsonObject
        {
            ["id"] = member.Id,
            ["kind"] = ToKindString(member.Kind),
            ["name"] = member.Name,
            ["declaration"] = member.Declaration
        };

        AddIfPresent(json, "capabilityId", member.CapabilityId);
        AddIfPresent(json, "returnType", member.ReturnType);
        AddIfPresent(json, "summary", member.Summary);
        AddIfPresent(json, "remarks", member.Remarks);
        AddIfPresent(json, "examples", member.Examples);
        AddIfPresent(json, "deprecated", member.DeprecationMessage);

        if (member.Parameters.Count > 0)
        {
            var parameters = new JsonArray();
            foreach (var parameter in member.Parameters)
            {
                var parameterJson = new JsonObject
                {
                    ["name"] = parameter.Name,
                    ["type"] = parameter.DeclaredType,
                    ["optional"] = parameter.IsOptional
                };

                AddIfPresent(parameterJson, "summary", parameter.Summary);
                parameters.Add((JsonNode)parameterJson);
            }

            json["parameters"] = parameters;
        }

        return json;
    }

    private static string ToKindString(TypeScriptApiItemKind kind) => kind switch
    {
        TypeScriptApiItemKind.Interface => "interface",
        TypeScriptApiItemKind.Enum => "enum",
        TypeScriptApiItemKind.Dto => "dto",
        TypeScriptApiItemKind.Options => "options",
        TypeScriptApiItemKind.Augmentation => "augmentation",
        TypeScriptApiItemKind.Method => "method",
        TypeScriptApiItemKind.Property => "property",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown API item kind.")
    };

    private static void AddIfPresent(JsonObject json, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            json[name] = value;
        }
    }

    private static void AddIfPresent(JsonObject json, string name, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add((JsonNode)JsonValue.Create(value));
        }

        json[name] = array;
    }
}
