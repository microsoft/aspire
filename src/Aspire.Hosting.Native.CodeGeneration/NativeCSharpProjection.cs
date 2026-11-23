// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Aspire.TypeSystem;

namespace Aspire.Hosting.Native.CodeGeneration;

/// <summary>Projects the same scanned ATS declarations as native dispatch into a standalone .NET client.</summary>
internal static class NativeCSharpProjection
{
    public static string Generate(AtsContext context)
    {
        if (context.Properties.Count != 0 || context.EnumTypes.Count != 0 || context.ExportedValues.Count != 0)
        {
            throw new NotSupportedException("The native C# projection supports methods, DTOs and handles only.");
        }
        var handles = context.HandleTypes.ToDictionary(type => type.AtsTypeId, type => type.ClrType!.Name);
        var dtos = context.DtoTypes.ToDictionary(type => type.TypeId, type => type.Name);
        if (handles.Values.Concat(dtos.Values).Distinct(StringComparer.Ordinal).Count() != handles.Count + dtos.Count)
        {
            throw new NotSupportedException("The native C# projection requires unique simple type names.");
        }
        var code = new StringBuilder("""
            // Generated from the native ATS contract. Do not edit.
            #nullable enable
            using System.Text.Json;
            using System.Text.Json.Serialization;
            namespace Aspire.Hosting.Native.Client;

            """);
        var scalarTypes = new HashSet<string>(StringComparer.Ordinal) { "string", "bool", "int", "long" };
        foreach (var dto in context.DtoTypes.OrderBy(type => type.Name, StringComparer.Ordinal))
        {
            code.AppendLine(CultureInfo.InvariantCulture, $"/// <summary>Copied ATS data for {dto.Name}; contains no execution authority.</summary>");
            code.AppendLine(CultureInfo.InvariantCulture, $"public sealed class {dto.Name}");
            code.AppendLine("{");
            foreach (var property in dto.Properties)
            {
                if (property.IsCallback || ContainsHandle(property.Type))
                {
                    throw new NotSupportedException("Native DTOs cannot contain callbacks or live capability handles.");
                }
                code.AppendLine(CultureInfo.InvariantCulture, $"    /// <summary>Gets the {property.Name} contract field.</summary>");
                code.AppendLine(CultureInfo.InvariantCulture,
                    $"    public required {TypeName(property.Type)} {property.Name} {{ get; init; }}");
            }
            code.AppendLine("}");
        }
        foreach (var handle in context.HandleTypes.OrderBy(type => type.AtsTypeId, StringComparer.Ordinal))
        {
            var name = handles[handle.AtsTypeId];
            code.AppendLine(CultureInfo.InvariantCulture, $"/// <summary>Connection-scoped ATS capability for {name}.</summary>");
            code.AppendLine(CultureInfo.InvariantCulture, $"public sealed class {name} : NativeRemoteObject");
            code.AppendLine("{");
            code.AppendLine("    /// <summary>Wraps an authoritative wire identity for generated provider projections.</summary>");
            code.AppendLine(CultureInfo.InvariantCulture, $"    public {name}(NativeClient client, JsonElement handle) : base(client, handle, {Literal(handle.AtsTypeId)}) {{ }}");
            foreach (var capability in context.Capabilities.Where(capability => capability.TargetTypeId == handle.AtsTypeId)
                .OrderBy(capability => capability.CapabilityId, StringComparer.Ordinal))
            {
                AppendMethod(capability, isStatic: false);
            }
            code.AppendLine("}");
        }
        var staticCapabilities = context.Capabilities.Where(capability => context.Methods[capability.CapabilityId].IsStatic).ToArray();
        if (staticCapabilities.Length != 0)
        {
            code.AppendLine("""
                /// <summary>Static factories exported by the native ATS contract.</summary>
                public static class NativeFactories
                {
                """);
            foreach (var capability in staticCapabilities.OrderBy(capability => capability.CapabilityId, StringComparer.Ordinal))
            {
                AppendMethod(capability, isStatic: true);
            }
            code.AppendLine("}");
        }
        foreach (var type in dtos.Values.Concat(scalarTypes).Order(StringComparer.Ordinal))
        {
            code.AppendLine(CultureInfo.InvariantCulture, $"[JsonSerializable(typeof({type}))]");
        }
        code.AppendLine("""
            [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectNullableAnnotations = true)]
            internal sealed partial class NativeClientJsonContext : JsonSerializerContext;
            """);

        return code.ToString();

        void AppendMethod(AtsCapabilityInfo capability, bool isStatic)
        {
            var method = context.Methods[capability.CapabilityId];
            // ATS includes the implicit instance receiver as a "context" parameter.
            // The C# projection binds it to this wrapper, never a caller argument.
            var inputs = capability.Parameters.Where(parameter => isStatic || parameter.Name != capability.TargetParameterName).ToArray();
            if (method.IsGenericMethod || method.GetParameters().Any(parameter => parameter.IsOptional) ||
                inputs.Any(parameter => parameter.Type is null || parameter.Type.IsNullable == true))
            {
                throw new NotSupportedException($"Unsupported native C# signature: {capability.CapabilityId}.");
            }
            var resultType = TypeName(capability.ReturnType);
            var task = resultType == "void" ? "Task" : $"Task<{resultType}>";
            var reservedNames = inputs.Select(parameter => parameter.Name).ToHashSet(StringComparer.Ordinal);
            var connectionName = ReserveName("clientConnection");
            var argumentsName = ReserveName("arguments");
            var resultName = ReserveName("result");
            var clientName = ReserveName("client");
            var cancellationName = ReserveName("cancellationToken");
            var callbacks = inputs.Where(parameter => parameter.IsCallback).ToArray();
            foreach (var callback in callbacks)
            {
                if (callback.CallbackParameters is not [{ Type.Category: AtsTypeCategory.Handle }] ||
                    callback.CallbackReturnType?.TypeId != "void")
                {
                    throw new NotSupportedException($"Unsupported native callback: {capability.CapabilityId}.");
                }
            }
            var callbackNames = callbacks.ToDictionary(parameter => parameter.Name,
                parameter => ReserveName(parameter.Name + "Id"), StringComparer.Ordinal);
            var parameters = inputs.Select(parameter => $"{(parameter.IsCallback ? $"Func<{TypeName(parameter.CallbackParameters![0].Type)}, Task>" : TypeName(parameter.Type!))} @{parameter.Name}");
            if (isStatic)
            {
                parameters = new[] { $"NativeClient {clientName}" }.Concat(parameters);
            }
            parameters = parameters.Append($"CancellationToken {cancellationName} = default");
            code.AppendLine(CultureInfo.InvariantCulture, $"    /// <summary>Invokes the native {method.Name} capability.</summary>");
            foreach (var parameter in inputs)
            {
                code.AppendLine(CultureInfo.InvariantCulture, $"    /// <param name=\"{parameter.Name}\">The {parameter.Name} contract argument.</param>");
            }
            if (isStatic)
            {
                code.AppendLine(CultureInfo.InvariantCulture, $"    /// <param name=\"{clientName}\">The authenticated connection owning the result.</param>");
            }
            code.AppendLine(CultureInfo.InvariantCulture, $"    /// <param name=\"{cancellationName}\">Cancels the caller's wait; it does not roll back server-side execution.</param>");
            code.AppendLine("    /// <returns>The copied result or connection-scoped capability.</returns>");
            code.AppendLine(CultureInfo.InvariantCulture,
                $"    public {(isStatic ? "static " : "")}async {task} {method.Name}Async({string.Join(", ", parameters)})");
            code.AppendLine("    {");
            code.AppendLine(CultureInfo.InvariantCulture, $"        var {connectionName} = {(isStatic ? clientName : "this.Client")};");
            code.AppendLine(CultureInfo.InvariantCulture, $"        var {argumentsName} = new Dictionary<string, JsonElement>(StringComparer.Ordinal);");
            if (!isStatic)
            {
                code.AppendLine(CultureInfo.InvariantCulture, $"        {argumentsName}.Add({Literal(capability.TargetParameterName!)}, {connectionName}.GetHandle(this));");
            }
            foreach (var parameter in inputs)
            {
                if (parameter.IsCallback)
                {
                    continue;
                }
                var parameterType = TypeName(parameter.Type!);
                if (parameterType == "string[]")
                {
                    scalarTypes.Add(parameterType);
                }
                var value = parameter.Type!.Category == AtsTypeCategory.Union
                    ? $"@{parameter.Name} switch {{ {string.Join(", ", parameter.Type.UnionTypes!.Select(member =>
                        member.TypeId == "string"
                            ? $"string text => JsonSerializer.SerializeToElement(text, NativeClientJsonContext.Default.String)"
                            : $"{TypeName(member)} remote => {connectionName}.GetHandle(remote)"))}, _ => throw new ArgumentException(\"Unsupported symbolic value.\", nameof(@{parameter.Name})) }}"
                    : parameter.Type.Category == AtsTypeCategory.Handle
                    ? $"{connectionName}.GetHandle(@{parameter.Name})"
                    : $"JsonSerializer.SerializeToElement(@{parameter.Name}, NativeClientJsonContext.Default.{JsonContextName(parameterType)})";
                code.AppendLine(CultureInfo.InvariantCulture, $"        {argumentsName}.Add({Literal(parameter.Name)}, {value});");
            }
            foreach (var callback in callbacks)
            {
                var callbackType = TypeName(callback.CallbackParameters![0].Type);
                var callbackName = callbackNames[callback.Name];
                code.AppendLine(CultureInfo.InvariantCulture, $"        ArgumentNullException.ThrowIfNull(@{callback.Name});");
                code.AppendLine(CultureInfo.InvariantCulture, $"        var {callbackName} = Guid.NewGuid().ToString(\"N\");");
                code.AppendLine(CultureInfo.InvariantCulture, $"        {connectionName}.RegisterCallback({callbackName}, async callbackArguments =>");
                code.AppendLine("        {");
                code.AppendLine(CultureInfo.InvariantCulture,
                    $"            await @{callback.Name}(new {callbackType}({connectionName}, callbackArguments.GetProperty(\"p0\"))).ConfigureAwait(false);");
                code.AppendLine("            return JsonSerializer.SerializeToElement((string?)null, NativeClientJsonContext.Default.String);");
                code.AppendLine("        });");
                code.AppendLine(CultureInfo.InvariantCulture,
                    $"        {argumentsName}.Add({Literal(callback.Name)}, JsonSerializer.SerializeToElement({callbackName}, NativeClientJsonContext.Default.String));");
            }
            if (callbacks.Length != 0)
            {
                code.AppendLine("        try");
                code.AppendLine("        {");
            }
            var invocationIndent = callbacks.Length != 0 ? "            " : "        ";
            code.AppendLine(CultureInfo.InvariantCulture,
                $"{invocationIndent}var {resultName} = await {connectionName}.InvokeAsync({Literal(capability.CapabilityId)}, {argumentsName}, {cancellationName}).ConfigureAwait(false);");
            if (capability.ReturnType.Category == AtsTypeCategory.Handle)
            {
                code.AppendLine(CultureInfo.InvariantCulture, $"{invocationIndent}return new {resultType}({connectionName}, {resultName});");
            }
            else if (resultType == "void")
            {
                code.AppendLine(CultureInfo.InvariantCulture, $"{invocationIndent}if ({resultName}.ValueKind != JsonValueKind.Null) throw new InvalidDataException(\"The capability returned an unexpected result.\");");
            }
            else
            {
                var missingResult = resultType is "bool" or "int" or "long" || capability.ReturnType.IsNullable == true ? "" :
                    " ?? throw new InvalidDataException(\"The capability returned no result.\")";
                code.AppendLine(CultureInfo.InvariantCulture,
                    $"{invocationIndent}return JsonSerializer.Deserialize({resultName}, NativeClientJsonContext.Default.{JsonContextName(resultType)}){missingResult};");
            }
            if (callbacks.Length != 0)
            {
                code.AppendLine("        }");
                code.AppendLine("        finally");
                code.AppendLine("        {");
                foreach (var callbackName in callbackNames.Values)
                {
                    code.AppendLine(CultureInfo.InvariantCulture, $"            {connectionName}.UnregisterCallback({callbackName});");
                }
                code.AppendLine("        }");
            }
            code.AppendLine("    }");

            string ReserveName(string preferred)
            {
                var name = preferred;
                while (!reservedNames.Add(name))
                {
                    name += "_";
                }

                return name;
            }
        }

        string TypeName(AtsTypeRef type)
        {
            var name = type.Category switch
            {
                AtsTypeCategory.Handle when handles.TryGetValue(type.TypeId, out var handle) => handle,
                AtsTypeCategory.Dto when dtos.TryGetValue(type.TypeId, out var dto) => dto,
                AtsTypeCategory.Array => TypeName(type.ElementType!) + "[]",
                AtsTypeCategory.Union => "object",
                AtsTypeCategory.Callback => "Delegate",
                AtsTypeCategory.Primitive => type.TypeId switch
                {
                    "string" => "string",
                    "bool" or "boolean" => "bool",
                    "int" or "integer" or "number" when type.ClrType == typeof(int) => "int",
                    "long" or "number" when type.ClrType == typeof(long) => "long",
                    "void" => "void",
                    _ => throw new NotSupportedException($"Unsupported native primitive: {type.TypeId} ({type.ClrType}).")
                },
                _ => throw new NotSupportedException($"Unsupported native C# type: {type.TypeId} ({type.Category}).")
            };

            return type.IsNullable == true ? name + "?" : name;
        }
    }

    private static bool ContainsHandle(AtsTypeRef type) => type.Category == AtsTypeCategory.Handle ||
        type.ElementType is { } element && ContainsHandle(element) ||
        type.ValueType is { } value && ContainsHandle(value);

    private static string JsonContextName(string type) => type.TrimEnd('?') switch
    {
        "string" => "String",
        "bool" => "Boolean",
        "int" => "Int32",
        "long" => "Int64",
        "string[]" => "StringArray",
        _ => type.TrimEnd('?')
    };

    private static string Literal(string value) => JsonSerializer.Serialize(value);
}
