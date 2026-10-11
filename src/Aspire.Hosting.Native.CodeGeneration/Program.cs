// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Aspire.Hosting.CodeGeneration.TypeScript;
using Aspire.Hosting.Native.CodeGeneration;
using Aspire.Hosting.RemoteHost;
using Aspire.TypeSystem;

var output = Path.GetFullPath(args.Single());
Directory.CreateDirectory(output);
var contractAssembly = NativeContractCompilation.Compile(output);
var scan = AtsCapabilityScanner.ScanAssemblies([contractAssembly]);
var context = scan.ToAtsContext();
if (context.Diagnostics.Any(diagnostic => diagnostic.Severity != AtsDiagnosticSeverity.Info))
{
    throw new InvalidOperationException(string.Join(Environment.NewLine,
        context.Diagnostics.Select(diagnostic => $"{diagnostic.Severity}: {diagnostic.Message}")));
}
if (context.Properties.Count != 0 || context.EnumTypes.Count != 0)
{
    throw new NotSupportedException("Native dispatch generation does not yet support exported properties or enums.");
}

var handles = context.HandleTypes.Select(handle => handle.AtsTypeId).ToHashSet(StringComparer.Ordinal);
var dtoTypes = new HashSet<Type>();
var code = new StringBuilder("""
    // Generated from ATS declarations. Do not edit.
    #nullable enable
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using System.Text.Json.Serialization;
    using Aspire.Hosting.Native.Api;
    namespace Aspire.Hosting.Native.Rpc;
    internal static class NativeDispatch
    {
        public static async Task<JsonNode?> InvokeAsync(NativeHandles handles, string capability, JsonObject arguments)
        {
            switch (capability)
            {

    """);

foreach (var capability in context.Capabilities.OrderBy(capability => capability.CapabilityId, StringComparer.Ordinal))
{
    var method = context.Methods[capability.CapabilityId];
    var asynchronous = method.ReturnType == typeof(Task) ||
        method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>);
    var returnType = method.ReturnType == typeof(Task) ? typeof(void) :
        asynchronous ? method.ReturnType.GetGenericArguments()[0] : method.ReturnType;
    if (method.IsGenericMethod || method.GetParameters().Any(parameter => parameter.IsOptional))
    {
        throw new NotSupportedException($"Unsupported generic or optional native export: {capability.CapabilityId}.");
    }
    var receiver = method.IsStatic ? [] : new[] { capability.TargetParameterName! };
    var names = receiver.Concat(method.GetParameters().Select(parameter => parameter.Name!)).ToArray();
    code.AppendLine(CultureInfo.InvariantCulture, $"            case {Literal(capability.CapabilityId)}:");
    code.AppendLine("            {");
    code.AppendLine(CultureInfo.InvariantCulture, $"                RpcArguments.Validate(arguments, [{string.Join(", ", names.Select(Literal))}]);");
    if (!method.IsStatic)
    {
        code.AppendLine(CultureInfo.InvariantCulture,
            $"                var target = handles.Get<{method.DeclaringType!.Name}>(arguments[{Literal(capability.TargetParameterName!)}], {Literal(capability.TargetTypeId!)});");
    }
    if (handles.Contains(capability.ReturnType.TypeId))
    {
        if (asynchronous)
        {
            throw new NotSupportedException($"Asynchronous native exports cannot publish handles after connection retirement: {capability.CapabilityId}.");
        }
        code.AppendLine("                handles.EnsureCapacity();");
    }
    var parameters = method.GetParameters().Select(parameter => Read(parameter.ParameterType, parameter.Name!));
    var invocation = $"{(method.IsStatic ? method.DeclaringType!.Name : "target")}.{method.Name}({string.Join(", ", parameters)})";
    if (returnType == typeof(void))
    {
        code.AppendLine(CultureInfo.InvariantCulture,
            $"                {(asynchronous ? "await " : "")}{invocation}{(asynchronous ? ".ConfigureAwait(false)" : "")};");
        code.AppendLine("                return null;");
    }
    else
    {
        code.AppendLine(CultureInfo.InvariantCulture,
            $"                var result = {(asynchronous ? "await " : "")}{invocation}{(asynchronous ? ".ConfigureAwait(false)" : "")};");
        if (handles.Contains(capability.ReturnType.TypeId))
        {
            code.AppendLine(CultureInfo.InvariantCulture, $"                return handles.Add(result, {Literal(capability.ReturnType.TypeId)});");
        }
        else if (context.DtoTypes.Any(dto => dto.TypeId == capability.ReturnType.TypeId))
        {
            dtoTypes.Add(returnType);
            code.AppendLine(CultureInfo.InvariantCulture,
                $"                return JsonSerializer.SerializeToNode(result, NativeRpcJsonContext.Default.{returnType.Name});");
        }
        else if (returnType == typeof(string))
        {
            code.AppendLine("                return JsonValue.Create(result);");
        }
        else
        {
            throw new NotSupportedException($"Unsupported native return type: {method.ReturnType}.");
        }
    }
    code.AppendLine("            }");
}
code.AppendLine("""
                default: throw new RpcFault("CAPABILITY_NOT_FOUND", "The capability is not implemented.");
            }
        }
    }
    """);
foreach (var type in dtoTypes.OrderBy(type => type.FullName, StringComparer.Ordinal))
{
    code.AppendLine(CultureInfo.InvariantCulture, $"[JsonSerializable(typeof({type.Name}))]");
}
code.AppendLine("""
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectNullableAnnotations = true)]
    internal sealed partial class NativeRpcJsonContext : JsonSerializerContext;
    """);
await File.WriteAllTextAsync(Path.Combine(output, "NativeDispatch.g.cs"), code.ToString()).ConfigureAwait(false);

// The bundle retains ATS data but omits scanner MethodInfo/PropertyInfo registries.
// Those reflection objects belong to this tool, never to the native process.
var resolver = new DefaultJsonTypeInfoResolver();
resolver.Modifiers.Add(typeInfo =>
{
    foreach (var property in typeInfo.Properties.Where(property =>
        typeof(MemberInfo).IsAssignableFrom(property.PropertyType)).ToArray())
    {
        typeInfo.Properties.Remove(property);
    }
});
var bundle = JsonSerializer.Serialize(new
{
    Capabilities = context.Capabilities.OrderBy(capability => capability.CapabilityId, StringComparer.Ordinal),
    HandleTypes = context.HandleTypes.OrderBy(handle => handle.AtsTypeId, StringComparer.Ordinal),
    context.DtoTypes,
    context.EnumTypes,
    context.ExportedValues
}, new JsonSerializerOptions { TypeInfoResolver = resolver, WriteIndented = true });
await File.WriteAllTextAsync(Path.Combine(output, "contract.json"), bundle).ConfigureAwait(false);
var sdk = new AtsTypeScriptCodeGenerator().GenerateDistributedApplication(context, includeManagedBootstrap: false);
// Language commands belong to the existing language provider. Serialize them
// during the build so the native executable needs neither that provider nor ATS
// reflection at runtime. Unsupported modes are omitted rather than simulated.
var languageSupportType = typeof(AtsTypeScriptCodeGenerator).Assembly.GetType(
    "Aspire.Hosting.CodeGeneration.TypeScript.TypeScriptLanguageSupport", throwOnError: true)!;
var languageSupport = (ILanguageSupport)Activator.CreateInstance(languageSupportType, nonPublic: true)!;
var runtimeSpec = JsonSerializer.SerializeToNode(languageSupport.GetRuntimeSpec(),
    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!.AsObject();
runtimeSpec.Remove("watchExecute");
await File.WriteAllTextAsync(Path.Combine(output, "languages.json"), new JsonArray(new JsonObject
{
    ["runtimeSpec"] = runtimeSpec, ["sdkResourcePrefix"] = "NativeSdk."
}).ToJsonString(new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
foreach (var (name, content) in sdk)
{
    await File.WriteAllTextAsync(Path.Combine(output, name), content).ConfigureAwait(false);
}
var rootTypeId = $"{contractAssembly.GetName().Name}/Aspire.Hosting.Native.Api.NativeApplicationServer";
if (!handles.Contains(rootTypeId))
{
    throw new InvalidOperationException("The native application server entry point is not in the ATS contract.");
}
await File.WriteAllTextAsync(Path.Combine(output, "native-client.mts"), $$"""
    // Generated native server bootstrap from the ATS entry-point declaration.
    import { createConnection } from 'node:net';
    import * as rpc from 'vscode-jsonrpc/node.js';
    import './aspire.mjs';
    import type { NativeApplicationServer } from './aspire.mjs';
    import { AspireClient, isMarshalledHandle, wrapIfHandle } from './transport.mjs';

    export interface NativeConnectionOptions {
        endpoint: string | { host: '127.0.0.1'; port: number };
        authenticationToken: string;
        authenticationTimeoutMilliseconds: number;
    }

    export async function connectNativeAppHost(options: NativeConnectionOptions):
        Promise<{ client: AspireClient; server: NativeApplicationServer }> {
        if (!Number.isSafeInteger(options.authenticationTimeoutMilliseconds) ||
            options.authenticationTimeoutMilliseconds < 1 || options.authenticationTimeoutMilliseconds > 2147483647) {
            throw new RangeError('An explicit bounded authentication timeout is required.');
        }
        const socket = typeof options.endpoint === 'string'
            ? createConnection(options.endpoint)
            : createConnection(options.endpoint);
        const connection = rpc.createMessageConnection(new rpc.StreamMessageReader(socket), new rpc.StreamMessageWriter(socket));
        const timeout = setTimeout(() => socket.destroy(new Error('Native server authentication timed out.')),
            options.authenticationTimeoutMilliseconds);
        try {
            await new Promise<void>((resolve, reject) => {
                socket.once('connect', resolve);
                socket.once('error', reject);
            });
            connection.listen();
            if (await connection.sendRequest('authenticate', options.authenticationToken) !== true) {
                throw new Error('Native server authentication was rejected.');
            }
            const root = await connection.sendRequest('getApplicationServer');
            if (!isMarshalledHandle(root) || root.$type !== {{Literal(rootTypeId)}}) {
                throw new Error('The native server entry-point contract is not applicable.');
            }
            const client = AspireClient.fromConnection(connection, socket, message => {
                if (process.env.ASPIRE_NATIVE_TRACE_RPC === '1') console.error(message);
            });
            return { client, server: wrapIfHandle(root, client) as NativeApplicationServer };
        } catch (error) {
            connection.dispose();
            socket.destroy();
            throw error;
        } finally {
            clearTimeout(timeout);
        }
    }
    """).ConfigureAwait(false);
Console.WriteLine($"Generated {context.Capabilities.Count} native ATS capabilities.");

string Read(Type type, string name)
{
    var node = $"arguments[{Literal(name)}]";
    if (type == typeof(string))
    {
        return $"RpcArguments.String({node})";
    }
    if (type == typeof(bool) || type == typeof(int) || type == typeof(long))
    {
        return $"RpcArguments.{(type == typeof(bool) ? "Boolean" : type == typeof(int) ? "Int32" : "Int64")}({node})";
    }
    if (context.DtoTypes.Any(dto => dto.TypeId == $"{type.Assembly.GetName().Name}/{type.FullName}"))
    {
        dtoTypes.Add(type);

        return $"RpcArguments.Dto({node}, NativeRpcJsonContext.Default.{type.Name})";
    }
    var typeId = context.HandleTypes.SingleOrDefault(handle => handle.AtsTypeId == $"{type.Assembly.GetName().Name}/{type.FullName}");
    if (typeId is not null)
    {
        return $"handles.Get<{type.Name}>({node}, {Literal(typeId.AtsTypeId)})";
    }

    throw new NotSupportedException($"Unsupported native input type: {type}.");
}

static string Literal(string value) => JsonSerializer.Serialize(value);
