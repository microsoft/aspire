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

var csharpOnly = args is [_, "--csharp"];
var providerOnly = args is ["--provider", _, _, _];
var providerTypeScriptOnly = args is ["--provider-ts", _, _];
var output = Path.GetFullPath(args switch
{
    ["--provider-ts", _, var directory] => directory,
    ["--provider", _, _, var directory] => directory,
    [var directory, "--csharp"] => directory,
    [var directory] => directory,
    _ => throw new ArgumentException("Specify an output directory or a provider projection command.")
});
Directory.CreateDirectory(output);
var contractAssembly = NativeContractCompilation.Compile(output);
var scan = AtsCapabilityScanner.ScanAssemblies([contractAssembly]);
var context = scan.ToAtsContext();
if (providerTypeScriptOnly)
{
    await NativeProviderTypeScriptProjection.GenerateAsync(args[1], output, context.HandleTypes).ConfigureAwait(false);
    return;
}
if (providerOnly)
{
    await NativeProviderProjection.GenerateAsync(args[1], args[2], output, context).ConfigureAwait(false);
    return;
}
if (context.Diagnostics.Any(diagnostic => diagnostic.Severity != AtsDiagnosticSeverity.Info))
{
    throw new InvalidOperationException(string.Join(Environment.NewLine,
        context.Diagnostics.Select(diagnostic => $"{diagnostic.Severity}: {diagnostic.Message}")));
}
if (context.Properties.Count != 0 || context.EnumTypes.Count != 0)
{
    throw new NotSupportedException("Native dispatch generation does not yet support exported properties or enums.");
}
if (csharpOnly)
{
    await File.WriteAllTextAsync(Path.Combine(output, "NativeClient.g.cs"),
        NativeCSharpProjection.Generate(context)).ConfigureAwait(false);
    Console.WriteLine($"Generated C# projection for {context.Capabilities.Count} native ATS capabilities.");
    return;
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
    using Aspire.Hosting.Native.Authoring;
    using Aspire.Hosting.Native.Model;
    namespace Aspire.Hosting.Native.Rpc;
    internal static class NativeDispatch
    {
        public static async Task<JsonNode?> InvokeAsync(NativeApplicationServer server, NativeHandles handles, NativeRpcConnection connection, NativeRpcConnection callbackConnection, string capability, JsonObject arguments)
        {
            try
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
        if (asynchronous && (method.IsStatic || returnType != method.DeclaringType))
        {
            throw new NotSupportedException($"Asynchronous native factories cannot publish new handle ownership: {capability.CapabilityId}.");
        }
        if (!asynchronous)
        {
            code.AppendLine("                handles.EnsureCapacity();");
        }
    }
    var parameters = method.GetParameters().Select(parameter => Read(parameter.ParameterType, parameter.Name!,
        capability.Parameters.Single(input => input.Name == parameter.Name)));
    var invocation = $"{(method.IsStatic ? method.DeclaringType!.Name : "target")}.{method.Name}({string.Join(", ", parameters)})";
    if (capability.CapabilityId == $"{contractAssembly.GetName().Name}/createSession")
    {
        // Keep the legacy parameterless wire factory, but give its session the
        // current server's executor and clock rather than isolated System defaults.
        invocation = "server.OpenApplication()";
    }
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
            code.AppendLine(CultureInfo.InvariantCulture,
                $"                return {(asynchronous ? "connection.PublishCoreHandle" : "handles.Add")}(result, {Literal(capability.ReturnType.TypeId)});");
        }
        else if (context.DtoTypes.Any(dto => dto.TypeId == capability.ReturnType.TypeId))
        {
            dtoTypes.Add(returnType);
            code.AppendLine(CultureInfo.InvariantCulture,
                $"                return JsonSerializer.SerializeToNode(result, NativeRpcJsonContext.Default.{returnType.Name});");
        }
        else if (returnType == typeof(string[]))
        {
            code.AppendLine("                return new JsonArray(result.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());");
        }
        else if (returnType == typeof(string) || returnType == typeof(bool) || returnType == typeof(int) || returnType == typeof(long))
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
                default: throw RpcFault.CapabilityNotFound("The capability is not implemented.");
            }
            }
            catch (SymbolicValueException exception)
            {
                throw new RpcFault(exception.Code, exception.Message);
            }
            catch (Aspire.Hosting.Native.Runtime.ExecutionPlanException exception)
            {
                // This diagnostic contains only validated resource names and
                // milestone names, never callback exceptions or launch inputs.
                throw new RpcFault("EXECUTION_PREREQUISITE_CYCLE", exception.Message);
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
var sdk = new AtsTypeScriptCodeGenerator().GenerateDistributedApplication(context, includeManagedBootstrap: false,
    invocationScopedCallbacks: true);
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
    var projectedContent = name == "aspire.mts"
        ? content + "\nexport { createBuilder, DistributedApplicationBuilder, ResourceBuilder, Resource, ContainerResource, ExecutableResource, ParameterResource, ComputeEnvironmentResource, ReferenceExpression, EndpointReference, ExecutableCommand, NativeBuildContext, NativeApplication } from './native-builder.mjs';\nexport type { ParameterOptions, ParameterGenerationOptions, PublishExpression, PublishResource, PublishModel, PublishingContext, DeploymentTarget } from './native-builder.mjs';\nexport { NativePipeline, WellKnownPipelineSteps, WellKnownPipelineTags } from './native-pipeline.mjs';\nexport type { NativePipelineStepContext, NativePipelineConfigurationContext, NativePipelineStepOptions, NativePipelineAction } from './native-pipeline.mjs';\n"
        : content;
    await File.WriteAllTextAsync(Path.Combine(output, name), projectedContent).ConfigureAwait(false);
}
using (var builderSource = typeof(NativeProviderProjection).Assembly.GetManifestResourceStream(
    "Aspire.Hosting.Native.CodeGeneration.Resources.native-builder.mts")
    ?? throw new InvalidOperationException("The native builder authoring projection is missing."))
using (var reader = new StreamReader(builderSource))
{
    await File.WriteAllTextAsync(Path.Combine(output, "native-builder.mts"), await reader.ReadToEndAsync().ConfigureAwait(false)).ConfigureAwait(false);
}
using (var pipelineSource = typeof(NativeProviderProjection).Assembly.GetManifestResourceStream(
    "Aspire.Hosting.Native.CodeGeneration.Resources.native-pipeline.mts")
    ?? throw new InvalidOperationException("The native pipeline authoring projection is missing."))
using (var reader = new StreamReader(pipelineSource))
{
    await File.WriteAllTextAsync(Path.Combine(output, "native-pipeline.mts"), await reader.ReadToEndAsync().ConfigureAwait(false)).ConfigureAwait(false);
}
await File.WriteAllTextAsync(Path.Combine(output, "native-types.mts"),
    "export const nativeTypes = " + new JsonObject(context.HandleTypes.Select(handle => KeyValuePair.Create(
        handle.ClrType!.Name, (JsonNode?)new JsonObject { ["typeId"] = handle.AtsTypeId, ["category"] = "Handle" }))).ToJsonString() +
    " as const;\n").ConfigureAwait(false);
var rootTypeId = $"{contractAssembly.GetName().Name}/Aspire.Hosting.Native.Api.NativeApplicationServer";
if (!handles.Contains(rootTypeId))
{
    throw new InvalidOperationException("The native application server entry point is not in the ATS contract.");
}
await File.WriteAllTextAsync(Path.Combine(output, "native-client.mts"), $$"""
    // Generated native server bootstrap from the ATS entry-point declaration.
    import { createConnection } from 'node:net';
    import { spawn } from 'node:child_process';
    import * as rpc from 'vscode-jsonrpc/node.js';
    import './aspire.mjs';
    import type { NativeApplicationServer } from './aspire.mjs';
    import { AspireClient, isMarshalledHandle, wrapIfHandle } from './transport.mjs';

    export interface NativeConnectionOptions {
        endpoint: string | { host: '127.0.0.1'; port: number };
        authenticationToken: string;
        authenticationTimeoutMilliseconds: number;
        providerCleanupTimeoutMilliseconds?: number;
        providerShutdownTimeoutMilliseconds?: number;
    }

    export async function connectNativeAppHost(options: NativeConnectionOptions):
        Promise<{ client: AspireClient; server: NativeApplicationServer;
            providerConfiguration(): Promise<{ packageName: string; executable: string; arguments: string[] }[]>;
            activateProvider(packageName: string, executable: string, args: readonly string[]): Promise<{ close(): Promise<void> }> }> {
        const providerCleanupTimeout = options.providerCleanupTimeoutMilliseconds ?? 15000;
        const providerShutdownTimeout = options.providerShutdownTimeoutMilliseconds ?? 5000;
        for (const duration of [options.authenticationTimeoutMilliseconds, providerCleanupTimeout, providerShutdownTimeout]) {
            if (!Number.isSafeInteger(duration) || duration < 1 || duration > 2147483647) {
                throw new RangeError('Connection and provider cleanup deadlines must be positive bounded durations.');
            }
        }
        const socket = typeof options.endpoint === 'string'
            ? createConnection(process.platform === 'win32' ? `\\\\.\\pipe\\${options.endpoint}` : options.endpoint)
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
            return {
                client,
                server: wrapIfHandle(root, client) as NativeApplicationServer,
                async providerConfiguration() {
                    const value = await connection.sendRequest<unknown>('getAppHostServerConfiguration');
                    if (!Array.isArray(value) || value.some(provider =>
                        !provider || typeof provider.packageName !== 'string' || typeof provider.executable !== 'string' ||
                        !Array.isArray(provider.arguments) || provider.arguments.some((argument: unknown) => typeof argument !== 'string'))) {
                        throw new Error('The AppHost Server v2 provider configuration is invalid.');
                    }
                    return value;
                },
                async activateProvider(packageName: string, executable: string, args: readonly string[]) {
                    const endpoint = options.endpoint;
                    const authority = await connection.sendRequest<string>('expectIntegrationHost', packageName);
                    const abandon = async (): Promise<void> => {
                        const cancellation = new rpc.CancellationTokenSource();
                        let timer: ReturnType<typeof setTimeout> | undefined;
                        try {
                            // Cancellation requests remote cooperation; race an independent
                            // deadline as well so a stalled peer cannot prevent child cleanup.
                            await Promise.race([
                                connection.sendRequest('abandonIntegrationHostRegistration', authority, cancellation.token),
                                new Promise<never>((_, reject) => {
                                    timer = setTimeout(() => {
                                        cancellation.cancel();
                                        reject(new Error('Provider activation cleanup timed out.'));
                                    }, providerCleanupTimeout);
                                })
                            ]);
                        } finally {
                            clearTimeout(timer);
                            cancellation.dispose();
                        }
                    };
                    const process = await (async () => {
                        try {
                            return spawn(executable, [...args], {
                                env: { ...globalThis.process.env,
                                    ASPIRE_NATIVE_RPC_ENDPOINT: typeof endpoint === 'string' ? undefined : `${endpoint.host}:${endpoint.port}`,
                                    ASPIRE_NATIVE_RPC_SOCKET_PATH: typeof endpoint === 'string' ? endpoint : undefined,
                                    ASPIRE_NATIVE_RPC_TOKEN: options.authenticationToken,
                                    ASPIRE_INTEGRATION_HOST_REGISTRATION_ID: authority },
                                stdio: ['ignore', 'pipe', 'pipe']
                            });
                        } catch (error) {
                            try {
                                await abandon();
                            } catch (cleanupError) {
                                throw new AggregateError([error, cleanupError], 'Provider activation and cleanup failed.');
                            }
                            throw error;
                        }
                    })();
                    // Drain both pipes without logging untrusted provider payloads.
                    process.stdout.resume();
                    process.stderr.resume();
                    // 'close' follows exit and stdio release, unlike 'exit' alone.
                    // https://nodejs.org/api/child_process.html#event-close
                    const released = new Promise<void>((resolve, reject) => {
                        process.once('error', reject);
                        process.once('close', () => resolve());
                    });
                    // A spawn/kill error can precede registration completion.
                    // Observe it now; close still reports the original failure.
                    void released.catch(() => {});
                    const waitForRelease = async (): Promise<boolean> => {
                        let timer: ReturnType<typeof setTimeout> | undefined;
                        try {
                            return await Promise.race([
                                released.then(() => true),
                                new Promise<boolean>(resolve => { timer = setTimeout(() => resolve(false), providerShutdownTimeout); })
                            ]);
                        } finally {
                            clearTimeout(timer);
                        }
                    };
                    let closing: Promise<void> | undefined;
                    const close = (): Promise<void> => closing ??= (async () => {
                        if (process.exitCode === null && process.signalCode === null) {
                            process.kill('SIGTERM');
                        }
                        if (!await waitForRelease()) {
                            if (process.exitCode === null && process.signalCode === null && !process.kill('SIGKILL')) {
                                throw new Error('The provider could not be forcibly stopped.');
                            }
                            if (!await waitForRelease()) {
                                throw new Error('The provider did not release its process and streams after forced shutdown.');
                            }
                        }
                    })();
                    try {
                        // A failed spawn must not wait for the server activation deadline.
                        await Promise.race([
                            connection.sendRequest('waitIntegrationHostRegistration', authority),
                            released.then(() => { throw new Error('The provider disconnected during activation.'); })
                        ]);
                        if (process.exitCode !== null || process.signalCode !== null) {
                            throw new Error('The provider disconnected during activation.');
                        }
                        return { close };
                    } catch (error) {
                        const failures: unknown[] = [error];
                        try {
                            await abandon();
                        } catch (cleanupError) {
                            failures.push(cleanupError);
                        }
                        try {
                            await close();
                        } catch (cleanupError) {
                            failures.push(cleanupError);
                        }
                        if (failures.length > 1) {
                            throw new AggregateError(failures, 'Provider activation and cleanup failed.');
                        }
                        throw error;
                    }
                }
            };
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

string Read(Type type, string name, AtsParameterInfo parameter)
{
    var node = $"arguments[{Literal(name)}]";
    if (parameter.IsCallback && parameter.CallbackParameters is [{ Type.Category: AtsTypeCategory.Handle } callbackParameter] &&
        parameter.CallbackReturnType?.TypeId == "void" && type.IsGenericType &&
        type.GetGenericTypeDefinition() == typeof(Func<,>) && type.GetGenericArguments()[1] == typeof(Task))
    {
        return $"connection.CreateAuthoringCallback<{type.GetGenericArguments()[0].Name}>(callbackConnection, RpcArguments.String({node}), {Literal(callbackParameter.Type.TypeId)})";
    }
    if (parameter.Type is { Category: AtsTypeCategory.Union, UnionTypes: { } members } &&
        members.Any(member => member.TypeId == "string") &&
        members.All(member => member.TypeId == "string" || member.Category == AtsTypeCategory.Handle))
    {
        return $"RpcArguments.Union({node}, handles, [{string.Join(", ", members.Where(member => member.Category == AtsTypeCategory.Handle).Select(member => Literal(member.TypeId)))}])";
    }
    if (type == typeof(string[]))
    {
        return $"RpcArguments.Strings({node})";
    }
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
