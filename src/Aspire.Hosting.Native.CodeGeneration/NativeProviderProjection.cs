// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Globalization;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.TypeSystem;

namespace Aspire.Hosting.Native.CodeGeneration;

/// <summary>Generates provider dispatch and consumer projection offline; no reflection is needed in the native runtime.</summary>
internal static class NativeProviderProjection
{
    public static async Task GenerateAsync(string assemblyPath, string packageName, string output, AtsContext core)
    {
        assemblyPath = Path.GetFullPath(assemblyPath);
        AssemblyLoadContext.Default.Resolving += Resolve;
        try
        {
            var assembly = Assembly.LoadFrom(assemblyPath);
            var methods = assembly.GetTypes().SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                .Select(method => (Method: method, Attribute: method.CustomAttributes.SingleOrDefault(attribute =>
                    attribute.AttributeType.FullName == "Aspire.Hosting.AspireExportAttribute")))
                .Where(pair => pair.Attribute is not null).Select(pair => (pair.Method,
                    Name: pair.Attribute!.ConstructorArguments.Count > 0 ? (string)pair.Attribute.ConstructorArguments[0].Value! :
                        char.ToLowerInvariant(pair.Method.Name[0]) + pair.Method.Name[1..])).ToArray();
            if (methods.Length == 0)
            {
                throw new InvalidOperationException("The provider assembly has no native exports.");
            }
            var coreTypes = core.HandleTypes.ToDictionary(type => type.ClrType!.Name, type => type.AtsTypeId);
            var resources = assembly.GetTypes().Where(IsResource).ToArray();
            var manifest = new JsonObject { ["protocolVersion"] = 2, ["capabilities"] = new JsonArray() };
            var capabilities = manifest["capabilities"]!.AsArray();
            var code = new StringBuilder("""
                // Generated offline from integration exports. Do not edit.
                #nullable enable
                #pragma warning disable CS1591 // Offline-generated provider infrastructure and consumer proxies.
                using System.Text.Json;
                using System.Text.Json.Serialization;
                using Aspire.Hosting.Native.Client;
                namespace NativeIntegration.Generated;

                """);
            foreach (var (method, name) in methods)
            {
                var lifecycle = method.CustomAttributes.SingleOrDefault(attribute =>
                    attribute.AttributeType.FullName == "Aspire.Hosting.Native.Client.NativeResourceLifecycleAttribute");
                var parameters = new JsonArray();
                if (!method.IsStatic)
                {
                    parameters.Add(Parameter("context", method.DeclaringType!));
                }
                foreach (var parameter in method.GetParameters())
                {
                    if (parameter.IsOptional)
                    {
                        throw new NotSupportedException("Provider projection requires explicit arguments.");
                    }
                    if (parameter.ParameterType != typeof(CancellationToken))
                    {
                        parameters.Add(Parameter(parameter.Name!, parameter.ParameterType));
                    }
                }
                capabilities.Add(new JsonObject
                {
                    ["id"] = packageName + "/" + name, ["method"] = name, ["description"] = $"Integration-owned {method.Name}.",
                    ["parameters"] = parameters, ["returnType"] = TypeRef(ReturnType(method)),
                    ["targetTypeId"] = method.IsStatic ? null : TypeId(method.DeclaringType!),
                    ["targetParameterName"] = method.IsStatic ? null : "context"
                });
                if (lifecycle is not null)
                {
                    capabilities[^1]!["lifecyclePhase"] = (string)lifecycle.ConstructorArguments[0].Value!;
                }
            }
            var metadata = manifest.ToJsonString();
            code.AppendLine("public static class ProviderContract");
            code.AppendLine("{");
            code.AppendLine(CultureInfo.InvariantCulture, $"    public static NativeProviderDefinition Create() => new(JsonDocument.Parse({Quote(metadata)}).RootElement.Clone(), DispatchAsync);");
            code.AppendLine("    private static async Task<JsonElement> DispatchAsync(NativeProviderContext context, string capability, JsonElement args)");
            code.AppendLine("    {");
            code.AppendLine("        switch (capability)");
            code.AppendLine("        {");
            foreach (var (method, name) in methods)
            {
                var receiver = method.IsStatic ? $"global::{method.DeclaringType!.FullName}" :
                    $"context.ReadResource<global::{method.DeclaringType!.FullName}>(args.GetProperty(\"context\"))";
                var invocation = receiver + "." + method.Name + "(" + string.Join(", ", method.GetParameters().Select(parameter =>
                    Decode(parameter.ParameterType, $"args.GetProperty({Quote(parameter.Name!)})"))) + ")";
                var resultType = ReturnType(method);
                var awaitPrefix = typeof(Task).IsAssignableFrom(method.ReturnType) ? "await " : "";
                if (awaitPrefix.Length > 0)
                {
                    invocation += ".ConfigureAwait(false)";
                }
                code.AppendLine(CultureInfo.InvariantCulture, $"            case {Quote(packageName + "/" + name)}:");
                code.AppendLine("            {");
                if (resultType == typeof(void))
                {
                    code.AppendLine(CultureInfo.InvariantCulture, $"                {awaitPrefix}{invocation};");
                    code.AppendLine("                return JsonSerializer.SerializeToElement((string?)null, ProviderProjectionJsonContext.Default.String);");
                }
                else
                {
                    code.AppendLine(CultureInfo.InvariantCulture, $"                var result = {awaitPrefix}{invocation};");
                    code.AppendLine(CultureInfo.InvariantCulture, $"                return {Encode(resultType, "result", "context.Client", provider: true)};");
                }
                code.AppendLine("            }");
            }
            code.AppendLine("            default: throw new InvalidOperationException(\"Unknown generated provider capability.\");");
            code.AppendLine("        }");
            code.AppendLine("    }");
            code.AppendLine("}");
            code.AppendLine("public static class IntegrationExtensions");
            code.AppendLine("{");
            foreach (var (method, name) in methods.Where(pair => pair.Method.IsStatic))
            {
                AppendClientMethod(method, name, isStatic: true);
            }
            code.AppendLine("}");
            foreach (var resource in resources)
            {
                code.AppendLine(CultureInfo.InvariantCulture, $"public sealed class {resource.Name}Client : NativeRemoteObject");
                code.AppendLine("{");
                code.AppendLine(CultureInfo.InvariantCulture, $"    internal {resource.Name}Client(NativeClient client, JsonElement handle) : base(client, handle, {Quote(TypeId(resource))}) {{ }}");
                foreach (var (method, name) in methods.Where(pair => pair.Method.DeclaringType == resource && !pair.Method.IsStatic &&
                    !pair.Method.CustomAttributes.Any(attribute => attribute.AttributeType.FullName == "Aspire.Hosting.Native.Client.NativeResourceLifecycleAttribute")))
                {
                    AppendClientMethod(method, name, isStatic: false);
                }
                code.AppendLine("}");
            }
            code.AppendLine("""
                [JsonSerializable(typeof(string))]
                [JsonSerializable(typeof(bool))]
                [JsonSerializable(typeof(int))]
                [JsonSerializable(typeof(long))]
                internal sealed partial class ProviderProjectionJsonContext : JsonSerializerContext;
                """);
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, "ProviderContract.g.cs"), code.ToString()).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(output, "provider-contract.json"), metadata).ConfigureAwait(false);
            return;

            void AppendClientMethod(MethodInfo method, string name, bool isStatic)
            {
                // Cancellation is invocation context, not a wire argument. Ordinary
                // authoring adapters need the same cancellation as lifecycle handlers.
                var inputs = method.GetParameters().Where(parameter => parameter.ParameterType != typeof(CancellationToken)).ToArray();
                if (isStatic && (inputs.Length == 0 || !coreTypes.ContainsKey(inputs[0].ParameterType.Name)))
                {
                    throw new NotSupportedException("Static provider exports require a typed native receiver.");
                }
                var resultType = ReturnType(method);
                var returnType = resultType == typeof(void) ? "Task" : $"Task<{ClientType(resultType)}>";
                var signature = string.Join(", ", inputs.Select((parameter, index) =>
                    $"{(isStatic && index == 0 ? "this " : "")}{ClientType(parameter.ParameterType)} {parameter.Name}"));
                code.AppendLine(CultureInfo.InvariantCulture, $"    public {(isStatic ? "static " : "")}async {returnType} {method.Name}Async({signature}{(inputs.Length > 0 ? ", " : "")}CancellationToken cancellationToken = default)");
                code.AppendLine("    {");
                code.AppendLine(CultureInfo.InvariantCulture, $"        var client = {(isStatic ? inputs[0].Name + ".Client" : "Client")};");
                code.AppendLine("        var arguments = new Dictionary<string, JsonElement>();");
                if (!isStatic)
                {
                    code.AppendLine("        arguments[\"context\"] = client.GetHandle(this);");
                }
                foreach (var parameter in inputs)
                {
                    code.AppendLine(CultureInfo.InvariantCulture, $"        arguments[{Quote(parameter.Name!)}] = {Encode(parameter.ParameterType, parameter.Name!, "client", provider: false)};");
                }
                code.AppendLine(CultureInfo.InvariantCulture, $"        var result = await client.InvokeAsync({Quote(packageName + "/" + name)}, arguments, cancellationToken).ConfigureAwait(false);");
                if (resultType != typeof(void))
                {
                    code.AppendLine(CultureInfo.InvariantCulture, $"        return {DecodeClient(resultType, "result")};");
                }
                code.AppendLine("    }");
            }

            JsonObject Parameter(string name, Type type) => new() { ["name"] = name, ["type"] = TypeRef(type) };
            JsonObject TypeRef(Type type) => new() { ["typeId"] = TypeId(type), ["category"] = IsHandle(type) ? "Handle" : "Primitive" };
            bool IsHandle(Type type) => IsResource(type) || coreTypes.ContainsKey(type.Name) && type.Namespace == "Aspire.Hosting.Native.Client";
            string TypeId(Type type) => IsResource(type) ? packageName + "/" + type.FullName :
                IsHandle(type) ? coreTypes[type.Name] : type == typeof(void) ? "void" :
                type == typeof(string) ? "string" : type == typeof(bool) ? "bool" : type == typeof(int) ? "int" :
                type == typeof(long) ? "long" : throw new NotSupportedException($"Unsupported native provider projection type {type}.");
            string ClientType(Type type) => IsResource(type) ? type.Name + "Client" : "global::" + type.FullName;
            string Decode(Type type, string expression) => type == typeof(CancellationToken) ? "context.CancellationToken" :
                IsResource(type) ? $"context.ReadResource<global::{type.FullName}>({expression})" :
                IsHandle(type) ? $"new global::{type.FullName}(context.Client, {expression})" :
                type == typeof(string) ? expression + ".GetString()!" : type == typeof(bool) ? expression + ".GetBoolean()" :
                type == typeof(int) ? expression + ".GetInt32()" : type == typeof(long) ? expression + ".GetInt64()" :
                throw new NotSupportedException();
            string DecodeClient(Type type, string expression) => IsHandle(type) ? $"new {ClientType(type)}(client, {expression})" :
                type == typeof(string) ? expression + ".GetString()!" : type == typeof(bool) ? expression + ".GetBoolean()" :
                type == typeof(int) ? expression + ".GetInt32()" : expression + ".GetInt64()";
            string Encode(Type type, string expression, string client, bool provider) =>
                IsResource(type) && provider ? $"context.PublishResource({expression}, {Quote(TypeId(type))})" :
                IsHandle(type) ? $"{client}.GetHandle({expression})" :
                $"JsonSerializer.SerializeToElement({expression}, ProviderProjectionJsonContext.Default.{type.Name})";
        }
        finally
        {
            AssemblyLoadContext.Default.Resolving -= Resolve;
        }

        Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
        {
            var path = Path.Combine(Path.GetDirectoryName(assemblyPath)!, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        }
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value);
    private static Type ReturnType(MethodInfo method) => method.ReturnType == typeof(Task) ? typeof(void) :
        method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)
            ? method.ReturnType.GetGenericArguments()[0] : method.ReturnType;
    private static bool IsResource(Type type) => type.BaseType is { } parent &&
        (parent.FullName == "Aspire.Hosting.Native.Client.NativeIntegrationResource" || IsResource(parent));
}
