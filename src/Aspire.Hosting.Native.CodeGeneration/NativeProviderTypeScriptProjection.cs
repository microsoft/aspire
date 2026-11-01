// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json;
using Aspire.Hosting.Native.Tooling;
using Aspire.TypeSystem;

namespace Aspire.Hosting.Native.CodeGeneration;

/// <summary>Emits standalone primitive and handle wrappers from parsed provider contracts.</summary>
internal static class NativeProviderTypeScriptProjection
{
    public static async Task GenerateAsync(string manifestPath, string output, IReadOnlyList<AtsTypeInfo> coreHandles)
    {
        var exports = NativeProviderContractReader.Read(await File.ReadAllTextAsync(manifestPath).ConfigureAwait(false));
        var code = Generate(exports, coreHandles);
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "provider-client.mts"), code).ConfigureAwait(false);
    }

    internal static string Generate(IReadOnlyList<NativeProviderExport> exports, IReadOnlyList<AtsTypeInfo> coreHandles)
    {
        var methods = exports.Where(export => export.LifecyclePhase is null).Select(export => export.Capability).ToArray();
        var types = methods.SelectMany(method => method.Parameters.Select(parameter => parameter.Type)
            .Append(method.ReturnType)).OfType<AtsTypeRef>().Where(type => type.Category == AtsTypeCategory.Handle)
            .Select(type => type.TypeId).Distinct(StringComparer.Ordinal).ToArray();
        var coreIds = coreHandles.Select(type => type.AtsTypeId).ToHashSet(StringComparer.Ordinal);
        var coreTypes = types.Where(coreIds.Contains).ToArray();
        var ownTypes = types.Except(coreTypes, StringComparer.Ordinal).ToArray();
        var code = new StringBuilder("""
            // Generated offline from portable provider metadata. Do not edit.
            import { type AspireClientRpc, type MarshalledHandle, registerHandleWrapper, wrapIfHandle } from './transport.mjs';

            """);
        if (coreTypes.Length > 0)
        {
            code.AppendLine("import type { " + string.Join(", ", coreTypes.Select(Name)) + " } from './aspire.mjs';");
        }
        code.AppendLine("export class IntegrationClient {");
        code.AppendLine("    constructor(private readonly client: AspireClientRpc) {}");
        foreach (var method in methods.Where(method => method.TargetTypeId is null))
        {
            AppendMethod(method, instance: false);
        }
        code.AppendLine("}");
        foreach (var type in ownTypes)
        {
            var name = Name(type) + "Client";
            code.AppendLine("export class " + name + " {");
            code.AppendLine("    constructor(private readonly handle: MarshalledHandle, private readonly client: AspireClientRpc) {}");
            code.AppendLine("    toJSON(): MarshalledHandle { return this.handle; }");
            foreach (var method in methods.Where(method => method.TargetTypeId == type))
            {
                AppendMethod(method, instance: true);
            }
            code.AppendLine("}");
            code.AppendLine("registerHandleWrapper(" + Quote(type) + ", (handle, client) => new " + name + "(handle, client));");
        }
        return code.ToString();

        void AppendMethod(AtsCapabilityInfo method, bool instance)
        {
            var target = method.TargetParameterName;
            var parameters = method.Parameters.Where(parameter => !instance || parameter.Name != target).ToArray();
            var signature = string.Join(", ", parameters.Select(parameter => parameter.Name + ": " +
                TypeName(parameter.Type ?? throw new InvalidDataException("Provider parameters require a type."))));
            var returnType = TypeName(method.ReturnType);
            code.AppendLine("    async " + method.MethodName + "(" + signature + "): Promise<" + returnType + "> {");
            code.AppendLine("        const result = await this.client.invokeCapability(" + Quote(method.CapabilityId) + ", {");
            if (instance)
            {
                code.AppendLine("            " + target + ": this.handle,");
            }
            foreach (var parameter in parameters)
            {
                code.AppendLine("            " + parameter.Name + ",");
            }
            code.AppendLine("        });");
            if (returnType != "void")
            {
                code.AppendLine("        return wrapIfHandle(result, this.client) as " + returnType + ";");
            }
            code.AppendLine("    }");
        }

        string TypeName(AtsTypeRef type)
        {
            var id = type.TypeId;
            return type.Category switch
            {
                AtsTypeCategory.Handle => Name(id) + (ownTypes.Contains(id) ? "Client" : ""),
                AtsTypeCategory.Primitive => id switch
                {
                    "string" => "string",
                    "bool" => "boolean",
                    "int" or "long" => "number",
                    "void" => "void",
                    _ => throw new NotSupportedException($"Unsupported provider primitive '{id}'.")
                },
                _ => throw new NotSupportedException($"Unsupported provider type category '{type.Category}'.")
            };
        }
    }

    private static string Name(string id) => id[(id.LastIndexOf('.') + 1)..].Split('/').Last();
    private static string Quote(string value) => JsonSerializer.Serialize(value);
}
