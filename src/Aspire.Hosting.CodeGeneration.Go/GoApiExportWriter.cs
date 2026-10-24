// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aspire.Hosting.CodeGeneration.Go;

/// <summary>
/// Serializes a <see cref="GoApiModel"/> into the canonical schema version 1 export document.
/// </summary>
/// <remarks>
/// The document is written by hand rather than through reflection-based serialization because the
/// shape is a published contract that documentation sites bind to. Writing it explicitly makes the
/// contract reviewable in one place, keeps property order stable so exports diff cleanly between SDK
/// versions, and drops empty collections and null strings so a version bump only shows real API
/// changes. The envelope (<c>schemaVersion</c>, <c>generator</c>, <c>package</c>, <c>modules</c>,
/// <c>declarations</c>) matches the TypeScript export so a consumer can read both.
/// </remarks>
internal static class GoApiExportWriter
{
    public static JsonObject Write(GoApiModel model)
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
            ["generator"] = new JsonObject
            {
                ["name"] = model.Generator.Name,
                ["version"] = model.Generator.Version
            },
            ["package"] = new JsonObject
            {
                ["name"] = model.Package.Name,
                ["version"] = model.Package.Version,
                ["importPath"] = model.Package.ImportPath
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
    public static string WriteToJson(GoApiModel model, bool indented = false)
        => Write(model).ToJsonString(new JsonSerializerOptions { WriteIndented = indented });

    private static JsonObject WriteModule(GoApiModule module)
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

    private static JsonObject WriteItem(GoApiItem item)
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
        AddIfTrue(json, "marker", item.IsMarker);
        AddIfPresent(json, "implements", item.Implements);
        AddIfPresent(json, "usedBy", item.UsedBy);

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

    private static JsonObject WriteMember(GoApiMember member)
    {
        var json = new JsonObject
        {
            ["id"] = member.Id,
            ["kind"] = ToKindString(member.Kind),
            ["name"] = member.Name,
            ["declaration"] = member.Declaration
        };

        AddIfPresent(json, "capabilityId", member.CapabilityId);
        AddIfPresent(json, "targetTypeId", member.TargetTypeId);
        AddIfPresent(json, "capabilityKind", member.CapabilityKind);
        AddIfPresent(json, "owningAssembly", member.OwningAssemblyName);
        AddIfPresent(json, "returnType", member.ReturnType);
        if (member.ErrorModel is { } errorModel)
        {
            json["errorModel"] = ToErrorModelString(errorModel);
        }

        AddIfPresent(json, "summary", member.Summary);
        AddIfPresent(json, "remarks", member.Remarks);
        AddIfPresent(json, "returns", member.Returns);
        AddIfPresent(json, "jsonName", member.JsonName);
        AddIfPresent(json, "value", member.Value);
        AddIfTrue(json, "returnsReceiver", member.ReturnsReceiver);
        AddIfTrue(json, "returnsBuilder", member.ReturnsBuilder);
        AddIfTrue(json, "synthesized", member.IsSynthesized);
        AddIfTrue(json, "experimental", member.IsExperimental);
        if (member.DeprecationMessage is not null)
        {
            json["deprecated"] = member.DeprecationMessage;
        }

        if (member.Parameters.Count > 0)
        {
            json["parameters"] = WriteParameters(member.Parameters);
        }

        if (member.Options is { } options)
        {
            var optionsJson = new JsonObject { ["typeName"] = options.TypeName };
            AddIfTrue(optionsJson, "direct", options.IsDirect);
            if (options.Fields.Count > 0)
            {
                optionsJson["fields"] = WriteParameters(options.Fields);
            }

            json["options"] = optionsJson;
        }

        return json;
    }

    private static JsonArray WriteParameters(IReadOnlyList<GoApiParameter> parameters)
    {
        var array = new JsonArray();
        foreach (var parameter in parameters)
        {
            var json = new JsonObject
            {
                ["name"] = parameter.Name,
                ["type"] = parameter.DeclaredType,
                ["optional"] = parameter.IsOptional
            };

            AddIfTrue(json, "callback", parameter.IsCallback);
            AddIfPresent(json, "jsonName", parameter.JsonName);
            AddIfPresent(json, "summary", parameter.Summary);
            AddIfPresent(json, "allowedTypes", parameter.AllowedTypes);
            array.Add((JsonNode)json);
        }

        return array;
    }

    private static string ToKindString(GoApiItemKind kind) => kind switch
    {
        GoApiItemKind.Interface => "interface",
        GoApiItemKind.Augmentation => "augmentation",
        GoApiItemKind.Dto => "dto",
        GoApiItemKind.Options => "options",
        GoApiItemKind.Enum => "enum",
        GoApiItemKind.Variable => "variable",
        GoApiItemKind.Function => "function",
        GoApiItemKind.Type => "type",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown API item kind.")
    };

    private static string ToKindString(GoApiMemberKind kind) => kind switch
    {
        GoApiMemberKind.Method => "method",
        GoApiMemberKind.Field => "field",
        GoApiMemberKind.Constant => "constant",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown API member kind.")
    };

    private static string ToErrorModelString(GoApiErrorModel errorModel) => errorModel switch
    {
        GoApiErrorModel.None => "none",
        GoApiErrorModel.Returned => "returned",
        GoApiErrorModel.Deferred => "deferred",
        _ => throw new ArgumentOutOfRangeException(nameof(errorModel), errorModel, "Unknown error model.")
    };

    private static void AddIfPresent(JsonObject json, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            json[name] = value;
        }
    }

    private static void AddIfTrue(JsonObject json, string name, bool value)
    {
        if (value)
        {
            json[name] = true;
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
