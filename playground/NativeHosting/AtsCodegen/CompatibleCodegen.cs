// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting;
using Aspire.Hosting.RemoteHost;
using Aspire.TypeSystem;

namespace NativeHosting;

// This development tool deliberately loads managed Hosting only outside the AOT
// process. The executable wire contract and client SDK come from production ATS.
internal static class CompatibleCodegen
{
    private static readonly Dictionary<string, string> s_routes = new(StringComparer.Ordinal)
    {
        ["Aspire.Hosting/createBuilder"] = "create",
        ["Aspire.Hosting/build"] = "build",
        ["Aspire.Hosting/run"] = "run",
        ["Aspire.Hosting/waitFor"] = "wait",
        ["Aspire.Hosting/withEnvironment"] = "environment",
        ["Aspire.Hosting/withReference"] = "reference",
        ["Aspire.Hosting/withHttpEndpoint"] = "endpoint",
        ["Aspire.Hosting/getConnectionProperty"] = "property",
        ["Aspire.Hosting.Redis/addRedis"] = "external",
        ["Aspire.Hosting.PostgreSQL/addPostgres"] = "external",
        ["Aspire.Hosting.PostgreSQL/addDatabase"] = "external",
        ["Aspire.Hosting.JavaScript/addJavaScriptApp"] = "external",
        ["Aspire.Hosting.DevTunnels/addDevTunnel"] = "external",
        ["Aspire.Hosting.DevTunnels/withReferenceResourceAnonymous"] = "external"
    };

    public static async Task RunAsync(string root, bool rpc)
    {
        var assemblies = new[]
        {
            typeof(DistributedApplication).Assembly, typeof(RedisBuilderExtensions).Assembly,
            typeof(PostgresBuilderExtensions).Assembly, typeof(DevTunnelsResourceBuilderExtensions).Assembly,
            typeof(JavaScriptHostingExtensions).Assembly
        };
        var scanned = AtsCapabilityScanner.ScanAssemblies(assemblies).ToAtsContext();
        var capabilities = s_routes.Keys.Select(id => scanned.Capabilities.Single(capability => capability.CapabilityId == id)).ToArray();
        var context = SelectContext(scanned, capabilities);
        var contract = new JsonObject();
        foreach (var capability in capabilities)
        {
            contract[capability.CapabilityId] = new JsonObject
            {
                ["route"] = s_routes[capability.CapabilityId],
                ["target"] = capability.TargetParameterName,
                ["targetType"] = capability.TargetTypeId,
                ["returnType"] = capability.ReturnType.TypeId,
                ["returnsBuilder"] = capability.ReturnsBuilder,
                ["targets"] = new JsonArray(capability.ExpandedTargetTypes.Select(type => (JsonNode)JsonValue.Create(type.TypeId)!)
                    .Append(JsonValue.Create(capability.TargetTypeId)!).ToArray()),
                ["parameters"] = new JsonArray(capability.Parameters.Select(parameter => (JsonNode)new JsonObject
                {
                    ["name"] = parameter.Name, ["optional"] = parameter.IsOptional,
                    ["nullable"] = parameter.IsNullable, ["type"] = parameter.Type?.TypeId
                }).ToArray())
            };
        }

        if (!rpc)
        {
            var projections = new JsonObject();
            foreach (var capability in capabilities.Where(capability => s_routes[capability.CapabilityId] == "external"))
            {
                // The external implementation receives primitive model handles.
                // Its production receiver/return views are checked by the boundary.
                var nativeTarget = capability.TargetTypeId?.EndsWith(".IDistributedApplicationBuilder", StringComparison.Ordinal) == true
                    ? "NativeHosting.Ats/NativeHosting.NativeBuilder" : "NativeHosting.Ats/NativeHosting.NativeResource";
                projections[capability.CapabilityId] = new JsonObject
                {
                    ["id"] = capability.CapabilityId, ["method"] = capability.MethodName,
                    ["description"] = capability.Description ?? capability.MethodName,
                    ["projection"] = new JsonObject
                    {
                        ["capabilityKind"] = "Method", ["targetParameterName"] = capability.TargetParameterName,
                        ["targetTypeId"] = nativeTarget, ["targetType"] = Handle(nativeTarget),
                        ["returnType"] = Handle("NativeHosting.Ats/NativeHosting.NativeResource"),
                        ["parameters"] = new JsonArray(capability.Parameters.Select(parameter => (JsonNode)new JsonObject
                        {
                            ["name"] = parameter.Name, ["isOptional"] = parameter.IsOptional, ["isNullable"] = parameter.IsNullable,
                            ["type"] = parameter.Type?.Category == AtsTypeCategory.Handle
                                ? Handle("NativeHosting.Ats/NativeHosting.NativeResource")
                                : new JsonObject { ["typeId"] = parameter.Type?.TypeId, ["category"] = parameter.Type?.Category.ToString() }
                        }).ToArray())
                    }
                };
            }

            await File.WriteAllTextAsync(Path.Combine(root, "compatible-projection.mts"),
                "import type { AspireExportMetadata } from './generated/base.mjs';\nexport const compatibleProjections = " +
                projections.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) +
                " as const satisfies Record<string, AspireExportMetadata>;\n");
            await File.WriteAllTextAsync(Path.Combine(root, "compatible-projection.mjs"),
                "export const compatibleProjections = " + projections.ToJsonString() + ";\n");
        }

        if (!rpc)
        {
            await File.WriteAllTextAsync(Path.Combine(root, "AtsServer", "CompatibleContract.json"), contract.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            // One bounded codegen request, e.g. {"language":"TypeScript",
            // "assemblyNames":["Aspire.Hosting.Redis"]}. No model data or secrets.
            var request = JsonNode.Parse(await Console.In.ReadLineAsync() ?? throw new ArgumentException("Missing codegen request."))!.AsObject();
            if (!string.Equals(request["language"]?.GetValue<string>(), "TypeScript", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException("Only the TypeScript generator is available.");
            }

            if (request["assemblyNames"] is JsonArray names && names.Count > 0)
            {
                var selected = names.Select(name => name!.GetValue<string>()).ToArray();
                if (selected.Any(name => !assemblies.Any(assembly => assembly.GetName().Name == name)))
                {
                    throw new NotSupportedException("An assembly filter names an unavailable integration.");
                }

                context = SelectContext(scanned, capabilities.Where(capability => selected.Any(name => capability.CapabilityId.StartsWith(name + "/", StringComparison.Ordinal)) ||
                    capability.CapabilityId.StartsWith("Aspire.Hosting/", StringComparison.Ordinal)).ToArray());
            }
        }

        var generatorType = Assembly.Load("Aspire.Hosting.CodeGeneration.TypeScript").GetType(
            "Aspire.Hosting.CodeGeneration.TypeScript.AtsTypeScriptCodeGenerator", throwOnError: true)!;
        var generator = (ICodeGenerator)Activator.CreateInstance(generatorType, nonPublic: true)!;
        var temporary = Directory.CreateTempSubdirectory("compatible-codegen-");
        try
        {
            var files = generator.GenerateDistributedApplication(context);
            foreach (var (name, content) in files)
            {
                await File.WriteAllTextAsync(Path.Combine(temporary.FullName, name), content);
            }

            var nodeModules = Path.GetFullPath(Path.Combine(root, "..", "NuxtApp", "node_modules"));
            Directory.CreateSymbolicLink(Path.Combine(temporary.FullName, "node_modules"), nodeModules);
            var start = new ProcessStartInfo("node") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { Path.Combine(nodeModules, "typescript", "bin", "tsc"), "--module", "NodeNext", "--moduleResolution", "NodeNext",
                "--target", "ES2023", "--strict", "--skipLibCheck", "--types", "node", "--typeRoots", Path.Combine(nodeModules, "@types") }
                .Concat(Directory.EnumerateFiles(temporary.FullName, "*.mts")))
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start TypeScript compilation.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var diagnostics = await stdout + await stderr;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Production-generated SDK did not compile: {diagnostics}");
            }

            var result = new Dictionary<string, string>();
            foreach (var path in Directory.EnumerateFiles(temporary.FullName).Where(path => path.EndsWith(".mts", StringComparison.Ordinal) || path.EndsWith(".mjs", StringComparison.Ordinal)))
            {
                result[Path.GetFileName(path)] = await File.ReadAllTextAsync(path);
            }

            if (rpc)
            {
                Console.WriteLine(JsonSerializer.Serialize(result));
            }
            else
            {
                var output = Directory.CreateDirectory(Path.Combine(root, "compatible-generated"));
                foreach (var (name, content) in result)
                {
                    await File.WriteAllTextAsync(Path.Combine(output.FullName, name), content);
                }

                Console.WriteLine($"Generated unchanged production SDK: {capabilities.Length} capabilities.");
            }
        }
        finally
        {
            temporary.Delete(recursive: true);
        }
    }

    public static async Task ValidateAppHostAsync(string root)
    {
        var assemblies = new[]
        {
            typeof(DistributedApplication).Assembly, typeof(RedisBuilderExtensions).Assembly,
            typeof(PostgresBuilderExtensions).Assembly, typeof(DevTunnelsResourceBuilderExtensions).Assembly,
            typeof(JavaScriptHostingExtensions).Assembly
        };
        var context = AtsCapabilityScanner.ScanAssemblies(assemblies).ToAtsContext();
        var generatorType = Assembly.Load("Aspire.Hosting.CodeGeneration.TypeScript").GetType(
            "Aspire.Hosting.CodeGeneration.TypeScript.AtsTypeScriptCodeGenerator", throwOnError: true)!;
        var generator = (ICodeGenerator)Activator.CreateInstance(generatorType, nonPublic: true)!;
        var temporary = Directory.CreateTempSubdirectory("managed-apphost-validation-");
        try
        {
            var modules = Directory.CreateDirectory(Path.Combine(temporary.FullName, ".aspire", "modules"));
            foreach (var (name, content) in generator.GenerateDistributedApplication(context))
            {
                await File.WriteAllTextAsync(Path.Combine(modules.FullName, name), content);
            }

            var source = Path.Combine(root, "CompatibleAppHost", "apphost.mts");
            var guest = Path.Combine(temporary.FullName, "apphost.mts");
            File.Copy(source, guest);
            var nodeModules = Path.GetFullPath(Path.Combine(root, "..", "NuxtApp", "node_modules"));
            Directory.CreateSymbolicLink(Path.Combine(temporary.FullName, "node_modules"), nodeModules);
            var start = new ProcessStartInfo("node") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { Path.Combine(nodeModules, "typescript", "bin", "tsc"),
                "--noEmit", "--module", "NodeNext", "--moduleResolution", "NodeNext", "--target", "ES2023",
                "--strict", "--skipLibCheck", "--types", "node", "--typeRoots", Path.Combine(nodeModules, "@types"), guest })
            {
                start.ArgumentList.Add(argument);
            }
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not compile the ordinary AppHost.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var diagnostics = await stdout + await stderr;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"AppHost did not compile against the full managed ATS SDK: {diagnostics}");
            }

            if (await File.ReadAllTextAsync(source) != await File.ReadAllTextAsync(guest))
            {
                throw new InvalidOperationException("AppHost source changed during validation.");
            }
            Console.WriteLine($"Unchanged AppHost compiles against the full managed ATS SDK ({context.Capabilities.Count} capabilities).");
        }
        finally
        {
            temporary.Delete(recursive: true);
        }
    }

    private static JsonObject Handle(string type) => new() { ["typeId"] = type, ["category"] = "Handle" };

    private static AtsContext SelectContext(AtsContext scanned, IReadOnlyList<AtsCapabilityInfo> capabilities)
    {
        var types = new HashSet<string>(StringComparer.Ordinal);
        void Visit(AtsTypeRef? type)
        {
            if (type is null || !types.Add(type.TypeId))
            {
                return;
            }

            Visit(type.ElementType);
            Visit(type.KeyType);
            Visit(type.ValueType);
            foreach (var member in type.UnionTypes ?? [])
            {
                Visit(member);
            }

            if (scanned.DtoTypes.FirstOrDefault(dto => dto.TypeId == type.TypeId) is { } dto)
            {
                foreach (var property in dto.Properties)
                {
                    Visit(property.Type);
                }
            }
        }

        foreach (var capability in capabilities)
        {
            Visit(capability.ReturnType);
            Visit(capability.TargetType);
            foreach (var parameter in capability.Parameters)
            {
                Visit(parameter.Type);
            }
        }

        // The production connection helper uses this DTO independently of the
        // scanned createBuilder signature.
        Visit(new AtsTypeRef { TypeId = "Aspire.Hosting/Aspire.Hosting.Ats.CreateBuilderOptions", Category = AtsTypeCategory.Dto });
        return new AtsContext
        {
            Capabilities = capabilities, HandleTypes = scanned.HandleTypes,
            DtoTypes = scanned.DtoTypes.Where(dto => types.Contains(dto.TypeId)).ToArray(),
            EnumTypes = scanned.EnumTypes.Where(type => types.Contains(type.TypeId)).ToArray()
        };
    }
}
