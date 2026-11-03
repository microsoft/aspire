// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Hosting.CodeGeneration.TypeScript;
using Aspire.TypeSystem;

namespace Aspire.Managed;

/// <summary>Merges loaded provider contracts and emits SDK files using the ATS generator.</summary>
internal static class AppHostServerV2CodeGeneration
{
    internal static async Task<int> GenerateAsync(string requestPath)
    {
        var input = await AppHostServerV2CodeGenerationInput.ReadAsync(requestPath).ConfigureAwait(false);
        var files = Generate(input);
        Directory.CreateDirectory(input.Output);
        foreach (var (name, content) in files)
        {
            await File.WriteAllTextAsync(Path.Combine(input.Output, name), content).ConfigureAwait(false);
        }

        return 0;
    }

    internal static IReadOnlyDictionary<string, string> Generate(AppHostServerV2CodeGenerationInput input)
    {
        var core = input.Core;
        var capabilities = core.Capabilities.ToList();
        var handles = core.HandleTypes.ToDictionary(type => type.AtsTypeId, StringComparer.Ordinal);
        var ids = capabilities.Select(capability => capability.CapabilityId).ToHashSet(StringComparer.Ordinal);
        var sdkFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var provider in input.Providers)
        {
            foreach (var export in provider.Capabilities)
            {
                if (export.LifecyclePhase is not null)
                {
                    continue;
                }
                var capability = export.Capability;
                if (!capability.CapabilityId.StartsWith(provider.PackageName + "/", StringComparison.Ordinal) ||
                    !ids.Add(capability.CapabilityId))
                {
                    throw new InvalidDataException($"Provider capability '{capability.CapabilityId}' is duplicate or outside its package.");
                }
                capabilities.Add(capability);
                foreach (var type in capability.Parameters.Select(parameter => parameter.Type)
                    .Append(capability.ReturnType).Where(type => type is not null))
                {
                    AddHandle(type!);
                }
                foreach (var parameter in capability.Parameters.Where(parameter => parameter.IsCallback))
                {
                    foreach (var callbackParameter in parameter.CallbackParameters ?? [])
                    {
                        AddHandle(callbackParameter.Type);
                    }
                    if (parameter.CallbackReturnType is { } callbackReturn)
                    {
                        AddHandle(callbackReturn);
                    }
                }
            }
            foreach (var (name, content) in provider.SdkFiles)
            {
                if (!sdkFiles.TryAdd(name, content))
                {
                    throw new InvalidDataException($"Provider SDK file '{name}' is duplicated.");
                }
            }
        }
        var context = new AtsContext
        {
            Capabilities = capabilities,
            HandleTypes = handles.Values.ToArray(),
            DtoTypes = core.DtoTypes,
            EnumTypes = core.EnumTypes,
            ExportedValues = core.ExportedValues
        };
        var files = new AtsTypeScriptCodeGenerator().GenerateDistributedApplication(context, includeManagedBootstrap: false);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, content) in files)
        {
            if (sdkFiles.ContainsKey(name))
            {
                throw new InvalidDataException($"Provider SDK file '{name}' replaces a core SDK file.");
            }
            var projected = content;
            if (name == "aspire.mts")
            {
                projected += "\nexport { createBuilder, DistributedApplicationBuilder, ResourceBuilder, Resource, ContainerResource, ExecutableResource, ParameterResource, ComputeEnvironmentResource, ReferenceExpression, EndpointReference, ExecutableCommand, NativeBuildContext, NativeApplication } from './native-builder.mjs';\n";
                projected += "export type { ParameterOptions, ParameterGenerationOptions, PublishExpression, PublishResource, PublishModel, PublishingContext, DeploymentTarget } from './native-builder.mjs';\n";
                projected += "export { NativePipeline, WellKnownPipelineSteps, WellKnownPipelineTags } from './native-pipeline.mjs';\nexport type { NativePipelineStepContext, NativePipelineConfigurationContext, NativePipelineStepOptions, NativePipelineAction } from './native-pipeline.mjs';\n";
                foreach (var provider in input.Providers)
                {
                    foreach (var module in provider.Exports)
                    {
                        projected += "export * from " + JsonSerializer.Serialize("./" + module[..^4] + ".mjs") + ";\n";
                    }
                }
            }
            result.Add(name, projected);
        }
        foreach (var (name, content) in sdkFiles)
        {
            result.Add(name, content);
        }

        return result;

        void AddHandle(AtsTypeRef type)
        {
            if (type.Category == AtsTypeCategory.Handle)
            {
                handles.TryAdd(type.TypeId, new AtsTypeInfo { AtsTypeId = type.TypeId });
            }
            if (type.ElementType is { } element)
            {
                AddHandle(element);
            }
        }
    }
}
