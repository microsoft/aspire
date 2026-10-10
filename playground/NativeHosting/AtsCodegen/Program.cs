// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.RemoteHost;
using Aspire.TypeSystem;
using NativeHosting;

var root = Path.GetFullPath(args.Single());
var context = AtsCapabilityScanner.ScanAssemblies([typeof(NativeBuilder).Assembly]).ToAtsContext();
foreach (var diagnostic in context.Diagnostics)
{
    Console.Error.WriteLine($"{diagnostic.Severity}: {diagnostic.Message}");
}

if (context.Diagnostics.Any(diagnostic => diagnostic.Severity != AtsDiagnosticSeverity.Info))
{
    throw new InvalidOperationException("ATS contract must scan without warnings or errors.");
}

// The shipped generator is internal. Reflection is deliberately limited to this
// offline development tool; neither scanner nor generator ships in the AOT core.
var assembly = Assembly.Load("Aspire.Hosting.CodeGeneration.TypeScript");
var generator = (ICodeGenerator)Activator.CreateInstance(assembly.GetType(
    "Aspire.Hosting.CodeGeneration.TypeScript.AtsTypeScriptCodeGenerator", throwOnError: true)!, nonPublic: true)!;
var output = Path.Combine(root, "generated");
Directory.CreateDirectory(output);
foreach (var (name, content) in generator.GenerateDistributedApplication(context))
{
    var generated = content;
    if (name == "aspire.mts")
    {
        // The current generator unconditionally emits a managed Hosting-specific
        // createBuilder helper, even when that contract is absent. Keep its real
        // connect/transport and every capability wrapper; remove only that helper.
        // This is an explicit codegen coupling exposed by the native exploration.
        var start = generated.IndexOf("/**\n * Creates a new distributed application builder.", StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException("The generator's managed bootstrap shape changed.");
        }

        var end = generated.IndexOf("// Re-export commonly used types", start, StringComparison.Ordinal);
        if (end < start)
        {
            throw new InvalidOperationException("The generator's managed bootstrap end marker changed.");
        }

        generated = generated.Remove(start, end - start);
    }

    await File.WriteAllTextAsync(Path.Combine(output, name), generated);
}

var native = new StringBuilder("""
    // GENERATED from the real ATS scanner. Do not edit.
    #nullable enable
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using System.Text.Json.Serialization;
    namespace NativeHosting;
    internal static class NativeDispatch
    {
        public static async Task<JsonNode?> InvokeAsync(AtsSession session, string capability, JsonObject args, CancellationToken token)
        {
            switch (capability)
            {

    """);
var dtoTypes = new HashSet<Type>();
foreach (var capability in context.Capabilities.OrderBy(capability => capability.CapabilityId, StringComparer.Ordinal))
{
    if (context.Methods[capability.CapabilityId].DeclaringType == typeof(IntegrationContracts))
    {
        continue;
    }

    var method = context.Methods[capability.CapabilityId];
    native.AppendLine(CultureInfo.InvariantCulture, $"            case {JsonSerializer.Serialize(capability.CapabilityId)}:");
    native.AppendLine("            {");
    if (!method.IsStatic)
    {
        native.AppendLine(CultureInfo.InvariantCulture, $"                var target = session.Get<{method.DeclaringType!.Name}>(args[{JsonSerializer.Serialize(capability.TargetParameterName)}]);");
    }

    foreach (var parameter in method.GetParameters().Where(parameter => typeof(Delegate).IsAssignableFrom(parameter.ParameterType)))
    {
        native.AppendLine(CultureInfo.InvariantCulture,
            $"                session.TrackCallback(AtsSession.String(args[{JsonSerializer.Serialize(parameter.Name)}], \"callback\"));");
    }

    if (method.Name == nameof(NativeResource.UpdateCustom))
    {
        native.AppendLine("                session.EnsureController(target);");
    }

    var parameters = method.GetParameters().Select(parameter =>
        Read(parameter.ParameterType, $"args[{JsonSerializer.Serialize(parameter.Name)}]", parameter.Name!)).ToArray();
    var invocation = $"{(method.IsStatic ? method.DeclaringType!.Name : "target")}.{method.Name}({string.Join(", ", parameters)})";
    var returnType = method.ReturnType;
    var async = returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>);
    if (async)
    {
        returnType = returnType.GetGenericArguments()[0];
    }

    native.AppendLine(CultureInfo.InvariantCulture, $"                var result = {(async ? "await " : "")}{invocation};");
    if (method.Name == nameof(NativeResource.WithControl))
    {
        native.AppendLine("                session.ClaimController(target);");
    }
    native.AppendLine(CultureInfo.InvariantCulture, $"                return {Write(returnType, "result")};");
    native.AppendLine("            }");
}

native.AppendLine("""
                default: throw new AtsFault("CAPABILITY_NOT_FOUND", $"Unknown ATS capability '{capability}'.");
            }
        }
    }
    """);
native.AppendLine("internal static class ExternalDispatch");
native.AppendLine("{");
native.Append("    public static bool Contains(string capability) => capability is ");
native.Append(string.Join(" or ", context.Capabilities.Where(capability => context.Methods[capability.CapabilityId].DeclaringType == typeof(IntegrationContracts))
    .Select(capability => JsonSerializer.Serialize(capability.CapabilityId))));
native.AppendLine(";");
native.AppendLine("}");
foreach (var type in dtoTypes.OrderBy(type => type.Name, StringComparer.Ordinal))
{
    native.AppendLine(CultureInfo.InvariantCulture, $"[JsonSerializable(typeof({type.Name}))]");
}

native.AppendLine("""
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    internal sealed partial class DispatchJsonContext : JsonSerializerContext;
    """);
await File.WriteAllTextAsync(Path.Combine(root, "AtsServer", "NativeDispatch.g.cs"), native.ToString());

var projections = new JsonObject();
foreach (var capability in context.Capabilities.Where(capability => context.Methods[capability.CapabilityId].DeclaringType == typeof(IntegrationContracts)))
{
    projections[capability.MethodName] = new JsonObject
    {
        ["id"] = capability.CapabilityId, ["method"] = capability.MethodName,
        ["description"] = capability.Documentation?.Summary ?? capability.MethodName,
        ["projection"] = new JsonObject
        {
            ["capabilityKind"] = capability.CapabilityKind.ToString(),
            ["returnType"] = TypeRef(capability.ReturnType),
            ["targetTypeId"] = capability.TargetTypeId,
            ["targetType"] = TypeRef(capability.TargetType!),
            ["targetParameterName"] = capability.TargetParameterName,
            ["parameters"] = new JsonArray(capability.Parameters.Select(parameter => (JsonNode)new JsonObject
            {
                ["name"] = parameter.Name, ["type"] = TypeRef(parameter.Type!),
                ["isOptional"] = parameter.IsOptional, ["isNullable"] = parameter.IsNullable
            }).ToArray())
        }
    };
}

await File.WriteAllTextAsync(Path.Combine(output, "ports-projection.mts"),
    "import type { AspireExportMetadata } from './base.mjs';\nexport const projections = " +
    projections.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) +
    " as const satisfies Record<string, AspireExportMetadata>;\n");
Console.WriteLine($"Generated real ATS SDK: {context.Capabilities.Count} capabilities, {context.HandleTypes.Count} handles, {context.DtoTypes.Count} DTOs.");

string Read(Type type, string node, string name)
{
    if (type == typeof(string))
    {
        return $"AtsSession.String({node}, {JsonSerializer.Serialize(name)})";
    }

    if (type == typeof(bool) || type == typeof(long))
    {
        return $"{node}!.GetValue<{(type == typeof(bool) ? "bool" : "long")}>()";
    }

    if (type == typeof(CancellationToken))
    {
        return $"session.Token({node}, token)";
    }

    if (type == typeof(object))
    {
        return $"session.Union({node})";
    }

    if (type.IsArray)
    {
        return $"{node}!.AsArray().Select(item => {Read(type.GetElementType()!, "item", name)}).ToArray()";
    }

    if (typeof(Delegate).IsAssignableFrom(type))
    {
        var invoke = type.GetMethod("Invoke")!;
        var callbackParameters = invoke.GetParameters().Select((parameter, index) => $"p{index}").ToArray();
        var payload = invoke.GetParameters().Where(parameter => parameter.ParameterType != typeof(CancellationToken))
            .Select((parameter, index) => $"[\"p{index}\"] = {Write(parameter.ParameterType, $"p{index}")}");
        return $"({string.Join(", ", callbackParameters)}) => session.CallbackAsync(AtsSession.String({node}, \"callback\"), " +
            $"new JsonObject {{ {string.Join(", ", payload)} }}, {callbackParameters.Last()})";
    }

    if (context.DtoTypes.Any(dto => dto.ClrType == type))
    {
        dtoTypes.Add(type);
        return $"{node}!.Deserialize(DispatchJsonContext.Default.{type.Name})!";
    }

    return $"session.Get<{type.Name}>({node})";
}

string Write(Type type, string value)
{
    if (type == typeof(bool) || type == typeof(string))
    {
        return $"JsonValue.Create({value})";
    }

    if (context.DtoTypes.Any(dto => dto.ClrType == type))
    {
        dtoTypes.Add(type);
        return $"JsonSerializer.SerializeToNode({value}, DispatchJsonContext.Default.{type.Name})";
    }

    var handle = context.HandleTypes.Single(handle => handle.ClrType == type);
    return $"session.Marshal({value}, {JsonSerializer.Serialize(handle.AtsTypeId)})";
}

static JsonObject TypeRef(AtsTypeRef type)
{
    var result = new JsonObject { ["typeId"] = type.TypeId, ["category"] = type.Category.ToString() };
    if (type.ElementType is not null)
    {
        result["elementType"] = TypeRef(type.ElementType);
    }

    return result;
}
