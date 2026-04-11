// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Aspire.TypeSystem;

namespace Aspire.Hosting.RemoteHost;

/// <summary>
/// Filters ATS contexts to a set of exporting assemblies.
/// </summary>
internal static class AtsContextFilter
{
    /// <summary>
    /// Filters the given ATS context to include only capabilities and types exported by the specified assemblies.
    /// </summary>
    /// <param name="context">The ATS context to filter.</param>
    /// <param name="assemblyNames">The names of the assemblies to include.</param>
    /// <returns>A filtered ATS context.</returns>
    public static AtsContext FilterByExportingAssemblies(
        AtsContext context,
        IReadOnlyCollection<string> assemblyNames)
        => FilterByExportingAssemblies(context, assemblyNames, includeReferencedTypes: false);

    /// <summary>
    /// Filters the given ATS context to include only capabilities and types exported by the specified assemblies, including all transitively referenced types.
    /// </summary>
    /// <param name="context">The ATS context to filter.</param>
    /// <param name="assemblyNames">The names of the assemblies to include.</param>
    /// <returns>A filtered ATS context.</returns>
    public static AtsContext FilterByExportingAssembliesWithReferences(
        AtsContext context,
        IReadOnlyCollection<string> assemblyNames)
        => FilterByExportingAssemblies(context, assemblyNames, includeReferencedTypes: true);

    private static AtsContext FilterByExportingAssemblies(
        AtsContext context,
        IReadOnlyCollection<string> assemblyNames,
        bool includeReferencedTypes)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(assemblyNames);

        var normalizedAssemblyNames = new HashSet<string>(assemblyNames.Where(static name => !string.IsNullOrWhiteSpace(name)), StringComparer.OrdinalIgnoreCase);
        if (normalizedAssemblyNames.Count == 0)
        {
            return context;
        }

        var filteredCapabilities = context.Capabilities
            .Where(capability => IsCapabilityOwnedBySelectedAssembly(context, capability, normalizedAssemblyNames))
            .ToList();

        var handleTypesById = context.HandleTypes.ToDictionary(type => type.AtsTypeId, StringComparer.Ordinal);
        var dtoTypesById = context.DtoTypes.ToDictionary(type => type.TypeId, StringComparer.Ordinal);
        var enumTypesById = context.EnumTypes.ToDictionary(type => type.TypeId, StringComparer.Ordinal);

        var includedHandleTypeIds = new HashSet<string>(
            context.HandleTypes
                .Where(type => IsOwnedBySelectedAssembly(type.ClrType?.Assembly, type.AtsTypeId, normalizedAssemblyNames))
                .Select(type => type.AtsTypeId),
            StringComparer.Ordinal);

        var includedDtoTypeIds = new HashSet<string>(
            context.DtoTypes
                .Where(type => IsOwnedBySelectedAssembly(type.ClrType?.Assembly, type.TypeId, normalizedAssemblyNames))
                .Select(type => type.TypeId),
            StringComparer.Ordinal);

        var includedEnumTypeIds = new HashSet<string>(
            context.EnumTypes
                .Where(type => IsOwnedBySelectedAssembly(type.ClrType?.Assembly, type.TypeId, normalizedAssemblyNames))
                .Select(type => type.TypeId),
            StringComparer.Ordinal);

        if (includeReferencedTypes)
        {
            foreach (var capability in filteredCapabilities)
            {
                CollectReferencedType(capability.TargetType, handleTypesById, dtoTypesById, enumTypesById, includedHandleTypeIds, includedDtoTypeIds, includedEnumTypeIds);
                CollectReferencedType(capability.ReturnType, handleTypesById, dtoTypesById, enumTypesById, includedHandleTypeIds, includedDtoTypeIds, includedEnumTypeIds);

                foreach (var expandedTargetType in capability.ExpandedTargetTypes)
                {
                    CollectReferencedType(expandedTargetType, handleTypesById, dtoTypesById, enumTypesById, includedHandleTypeIds, includedDtoTypeIds, includedEnumTypeIds);
                }

                foreach (var parameter in capability.Parameters)
                {
                    CollectReferencedType(parameter.Type, handleTypesById, dtoTypesById, enumTypesById, includedHandleTypeIds, includedDtoTypeIds, includedEnumTypeIds);

                    if (parameter.CallbackParameters is not null)
                    {
                        foreach (var callbackParameter in parameter.CallbackParameters)
                        {
                            CollectReferencedType(callbackParameter.Type, handleTypesById, dtoTypesById, enumTypesById, includedHandleTypeIds, includedDtoTypeIds, includedEnumTypeIds);
                        }
                    }

                    CollectReferencedType(parameter.CallbackReturnType, handleTypesById, dtoTypesById, enumTypesById, includedHandleTypeIds, includedDtoTypeIds, includedEnumTypeIds);
                }
            }
        }

        var filteredContext = new AtsContext
        {
            Capabilities = filteredCapabilities,
            HandleTypes = context.HandleTypes.Where(type => includedHandleTypeIds.Contains(type.AtsTypeId)).ToList(),
            DtoTypes = context.DtoTypes.Where(type => includedDtoTypeIds.Contains(type.TypeId)).ToList(),
            EnumTypes = context.EnumTypes.Where(type => includedEnumTypeIds.Contains(type.TypeId)).ToList(),
            Diagnostics = context.Diagnostics
        };

        foreach (var capability in filteredCapabilities)
        {
            if (context.Methods.TryGetValue(capability.CapabilityId, out var method))
            {
                filteredContext.Methods[capability.CapabilityId] = method;
            }

            if (context.Properties.TryGetValue(capability.CapabilityId, out var property))
            {
                filteredContext.Properties[capability.CapabilityId] = property;
            }
        }

        return filteredContext;
    }

    private static void CollectReferencedType(
        AtsTypeRef? typeRef,
        IReadOnlyDictionary<string, AtsTypeInfo> handleTypesById,
        IReadOnlyDictionary<string, AtsDtoTypeInfo> dtoTypesById,
        IReadOnlyDictionary<string, AtsEnumTypeInfo> enumTypesById,
        HashSet<string> includedHandleTypeIds,
        HashSet<string> includedDtoTypeIds,
        HashSet<string> includedEnumTypeIds)
    {
        if (typeRef is null)
        {
            return;
        }

        if (handleTypesById.TryGetValue(typeRef.TypeId, out var handleType) && includedHandleTypeIds.Add(handleType.AtsTypeId))
        {
            foreach (var implementedInterface in handleType.ImplementedInterfaces)
            {
                CollectReferencedType(implementedInterface, handleTypesById, dtoTypesById, enumTypesById, includedHandleTypeIds, includedDtoTypeIds, includedEnumTypeIds);
            }

            foreach (var baseType in handleType.BaseTypeHierarchy)
            {
                CollectReferencedType(baseType, handleTypesById, dtoTypesById, enumTypesById, includedHandleTypeIds, includedDtoTypeIds, includedEnumTypeIds);
            }
        }

        if (dtoTypesById.TryGetValue(typeRef.TypeId, out var dtoType) && includedDtoTypeIds.Add(dtoType.TypeId))
        {
            foreach (var property in dtoType.Properties)
            {
                CollectReferencedType(property.Type, handleTypesById, dtoTypesById, enumTypesById, includedHandleTypeIds, includedDtoTypeIds, includedEnumTypeIds);
            }
        }

        if (enumTypesById.ContainsKey(typeRef.TypeId))
        {
            includedEnumTypeIds.Add(typeRef.TypeId);
        }

        CollectReferencedType(typeRef.ElementType, handleTypesById, dtoTypesById, enumTypesById, includedHandleTypeIds, includedDtoTypeIds, includedEnumTypeIds);
        CollectReferencedType(typeRef.KeyType, handleTypesById, dtoTypesById, enumTypesById, includedHandleTypeIds, includedDtoTypeIds, includedEnumTypeIds);
        CollectReferencedType(typeRef.ValueType, handleTypesById, dtoTypesById, enumTypesById, includedHandleTypeIds, includedDtoTypeIds, includedEnumTypeIds);

        if (typeRef.UnionTypes is not null)
        {
            foreach (var unionType in typeRef.UnionTypes)
            {
                CollectReferencedType(unionType, handleTypesById, dtoTypesById, enumTypesById, includedHandleTypeIds, includedDtoTypeIds, includedEnumTypeIds);
            }
        }
    }

    private static bool IsCapabilityOwnedBySelectedAssembly(
        AtsContext context,
        AtsCapabilityInfo capability,
        HashSet<string> assemblyNames)
    {
        if (context.Methods.TryGetValue(capability.CapabilityId, out var method))
        {
            return IsSelectedAssembly(method.DeclaringType?.Assembly, assemblyNames);
        }

        if (context.Properties.TryGetValue(capability.CapabilityId, out var property))
        {
            return IsSelectedAssembly(property.DeclaringType?.Assembly, assemblyNames);
        }

        if (capability.TargetType?.ClrType is not null)
        {
            return IsSelectedAssembly(capability.TargetType.ClrType.Assembly, assemblyNames);
        }

        return TryGetAssemblyNameFromId(capability.CapabilityId, out var assemblyName)
            && assemblyNames.Contains(assemblyName);
    }

    private static bool IsOwnedBySelectedAssembly(Assembly? assembly, string typeId, HashSet<string> assemblyNames)
    {
        if (IsSelectedAssembly(assembly, assemblyNames))
        {
            return true;
        }

        return TryGetAssemblyNameFromId(typeId, out var assemblyName)
            && assemblyNames.Contains(assemblyName);
    }

    private static bool IsSelectedAssembly(Assembly? assembly, HashSet<string> assemblyNames)
    {
        var assemblyName = assembly?.GetName().Name;
        return assemblyName is not null && assemblyNames.Contains(assemblyName);
    }

    private static bool TryGetAssemblyNameFromId(string id, out string assemblyName)
    {
        assemblyName = string.Empty;

        var separatorIndex = id.IndexOf('/');
        if (separatorIndex <= 0)
        {
            return false;
        }

        assemblyName = id[..separatorIndex];
        return true;
    }
}