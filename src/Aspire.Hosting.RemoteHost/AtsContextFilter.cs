// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Aspire.TypeSystem;

namespace Aspire.Hosting.RemoteHost;

/// <summary>
/// Filters ATS contexts to a set of exporting assemblies.
/// </summary>
internal static class AtsContextFilter
{
    /// <summary>
    /// Resolves <paramref name="requestedName"/> to the spelling the assembly that carries it
    /// actually uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A NuGet package id is case-insensitive
    /// (<see href="https://learn.microsoft.com/nuget/consume-packages/finding-and-choosing-packages#package-identifiers"/>),
    /// so a caller can name a package in any casing, but an API export records the id verbatim as
    /// the identity consumers key on. Every filter here treats the package id as an assembly name,
    /// so the assemblies this context was scanned from are the authority on how it is spelled.
    /// </para>
    /// <para>
    /// Failing to match is worth reporting rather than absorbing. The candidates below are a
    /// superset of everything <see cref="FilterByExportingAssemblies(AtsContext, IReadOnlyCollection{string})"/>
    /// can match on, so a name that matches nothing here is a name the export would filter to
    /// nothing — a package that restored but whose assembly is named something else. Continuing
    /// under the requested spelling would publish an empty document that claims to describe it.
    /// </para>
    /// </remarks>
    /// <param name="context">The unfiltered ATS context.</param>
    /// <param name="requestedName">The assembly or package name as the caller spelled it.</param>
    /// <param name="canonicalName">The canonical spelling, when a loaded assembly matches.</param>
    /// <returns><see langword="true"/> when a loaded assembly matches; otherwise <see langword="false"/>.</returns>
    public static bool TryResolveCanonicalAssemblyName(
        AtsContext context,
        string requestedName,
        [NotNullWhen(true)] out string? canonicalName)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedName);

        var candidates = GetKnownAssemblyNames(
            context,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        return candidates.TryGetValue(requestedName, out canonicalName);
    }

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

    /// <summary>
    /// Filters an ATS context for API export while retaining enough capability metadata to resolve
    /// the generated wrapper shape of referenced handle types.
    /// </summary>
    /// <remarks>
    /// A package can return a handle owned by another assembly. The generated SDK still exposes that
    /// handle through its wrapper when the referenced type has chainable members, so the exporter
    /// needs to see those member kinds even though it must not republish the members themselves.
    /// Supporting capabilities retain their target, member kind, and referenced handle types. Their
    /// callable shape is otherwise removed so foreign API and options interfaces cannot leak into the
    /// package export while wrapper unions still match full source generation.
    /// </remarks>
    /// <param name="context">The ATS context to filter.</param>
    /// <param name="assemblyNames">The names of the assemblies whose API is being exported.</param>
    /// <returns>The filtered API export context.</returns>
    internal static AtsContext FilterForApiExport(
        AtsContext context,
        IReadOnlyCollection<string> assemblyNames)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(assemblyNames);

        var filteredContext = FilterByExportingAssemblies(context, assemblyNames, includeReferencedTypes: true);
        var normalizedAssemblyNames = new HashSet<string>(
            assemblyNames.Where(static name => !string.IsNullOrWhiteSpace(name)),
            StringComparer.OrdinalIgnoreCase);

        if (normalizedAssemblyNames.Count == 0)
        {
            return filteredContext;
        }

        var capabilityTargetTypeIds = filteredContext.Capabilities
            .SelectMany(GetCapabilityTargetTypeIds)
            .ToHashSet(StringComparer.Ordinal);
        var supportingHandleTypes = filteredContext.HandleTypes
            .Where(type =>
                !capabilityTargetTypeIds.Contains(type.AtsTypeId) &&
                !IsOwnedBySelectedAssembly(type.ClrType?.Assembly, type.AtsTypeId, normalizedAssemblyNames))
            .ToDictionary(type => type.AtsTypeId, StringComparer.Ordinal);
        if (supportingHandleTypes.Count == 0)
        {
            return filteredContext;
        }

        var includedCapabilityIds = filteredContext.Capabilities
            .Select(capability => capability.CapabilityId)
            .ToHashSet(StringComparer.Ordinal);
        var supportingCapabilities = context.Capabilities
            .Where(capability => !includedCapabilityIds.Contains(capability.CapabilityId))
            .SelectMany(capability => CreateApiExportSupportCapabilities(capability, supportingHandleTypes))
            .ToList();

        if (supportingCapabilities.Count == 0)
        {
            return filteredContext;
        }

        var capabilities = filteredContext.Capabilities.Concat(supportingCapabilities).ToList();
        var apiExportContext = new AtsContext
        {
            Capabilities = capabilities,
            HandleTypes = filteredContext.HandleTypes,
            DtoTypes = filteredContext.DtoTypes,
            EnumTypes = filteredContext.EnumTypes,
            ExportedValues = filteredContext.ExportedValues,
            Diagnostics = filteredContext.Diagnostics
        };

        foreach (var capability in capabilities)
        {
            // Instance capability IDs can be namespace-qualified rather than assembly-qualified.
            // Keep the reflection registries so the exporter attributes each retained capability to
            // the assembly that actually declares it instead of guessing from the ID prefix.
            if (context.Methods.TryGetValue(capability.CapabilityId, out var method))
            {
                apiExportContext.Methods[capability.CapabilityId] = method;
            }

            if (context.Properties.TryGetValue(capability.CapabilityId, out var property))
            {
                apiExportContext.Properties[capability.CapabilityId] = property;
            }

        }

        return apiExportContext;
    }

    private static IEnumerable<string> GetCapabilityTargetTypeIds(AtsCapabilityInfo capability)
    {
        if (capability.TargetTypeId is { } targetTypeId)
        {
            yield return targetTypeId;
        }

        if (capability.TargetType is { } targetType)
        {
            yield return targetType.TypeId;
        }

        foreach (var expandedTargetType in capability.ExpandedTargetTypes)
        {
            yield return expandedTargetType.TypeId;
        }
    }

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

        var filteredExportedValues = context.ExportedValues
            .Where(value => normalizedAssemblyNames.Contains(value.OwningAssemblyName))
            .ToList();
        var knownAssemblyNames = GetKnownAssemblyNames(context, normalizedAssemblyNames);

        if (includeReferencedTypes)
        {
            // Types owned by the selected assemblies were seeded into the included sets directly,
            // which means CollectReferencedType's "was this newly added?" guard will refuse to walk
            // their own members if a capability later references them. Expand the seeds explicitly so
            // an owned DTO's property types survive the filter. Without this, a DTO owned by
            // Aspire.Hosting that exposes an enum declared in a non-Aspire dependency (for example
            // HealthStatus from Microsoft.Extensions.Diagnostics.HealthChecks) is retained while the
            // enum it references is dropped, and code generation then fails on the dangling type.
            foreach (var handleType in context.HandleTypes.Where(type => includedHandleTypeIds.Contains(type.AtsTypeId)).ToList())
            {
                CollectHandleTypeMembers(handleType, handleTypesById, dtoTypesById, enumTypesById, includedHandleTypeIds, includedDtoTypeIds, includedEnumTypeIds);
            }

            foreach (var dtoType in context.DtoTypes.Where(type => includedDtoTypeIds.Contains(type.TypeId)).ToList())
            {
                CollectDtoTypeMembers(dtoType, handleTypesById, dtoTypesById, enumTypesById, includedHandleTypeIds, includedDtoTypeIds, includedEnumTypeIds);
            }

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

            foreach (var exportedValue in filteredExportedValues)
            {
                CollectReferencedType(exportedValue.Type, handleTypesById, dtoTypesById, enumTypesById, includedHandleTypeIds, includedDtoTypeIds, includedEnumTypeIds);
            }
        }

        var filteredContext = new AtsContext
        {
            Capabilities = filteredCapabilities,
            HandleTypes = context.HandleTypes.Where(type => includedHandleTypeIds.Contains(type.AtsTypeId)).ToList(),
            DtoTypes = context.DtoTypes.Where(type => includedDtoTypeIds.Contains(type.TypeId)).ToList(),
            EnumTypes = context.EnumTypes.Where(type => includedEnumTypeIds.Contains(type.TypeId)).ToList(),
            ExportedValues = filteredExportedValues,
            Diagnostics = context.Diagnostics
                .Where(diagnostic => IsDiagnosticOwnedBySelectedAssembly(context, diagnostic, normalizedAssemblyNames, knownAssemblyNames))
                .ToList()
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

    private static IEnumerable<AtsCapabilityInfo> CreateApiExportSupportCapabilities(
        AtsCapabilityInfo capability,
        IReadOnlyDictionary<string, AtsTypeInfo> supportingHandleTypes)
    {
        if (!GetCapabilityTargetTypeIds(capability).Any(supportingHandleTypes.ContainsKey))
        {
            yield break;
        }

        var targetType = capability.TargetType;
        if (targetType is null &&
            capability.TargetTypeId is { } targetTypeId &&
            supportingHandleTypes.TryGetValue(targetTypeId, out var handleType))
        {
            targetType = new AtsTypeRef
            {
                TypeId = targetTypeId,
                ClrType = handleType.ClrType,
                Category = AtsTypeCategory.Handle,
                IsInterface = handleType.IsInterface,
                ImplementedInterfaces = handleType.ImplementedInterfaces
            };
        }

        yield return new AtsCapabilityInfo
        {
            CapabilityId = capability.CapabilityId,
            MethodName = capability.MethodName,
            OwningTypeName = capability.OwningTypeName,
            // The canonical exporter needs the same handle universe as full source generation.
            // Preserve foreign handle references as required synthetic parameters so wrapper
            // unions stay identical without importing the foreign member's options interface.
            Parameters = CreateApiExportSupportParameters(capability),
            ReturnType = new AtsTypeRef
            {
                TypeId = AtsConstants.Void,
                Category = AtsTypeCategory.Primitive
            },
            TargetTypeId = capability.TargetTypeId,
            TargetType = targetType,
            TargetParameterName = capability.TargetParameterName,
            // Keep the complete expansion. Full source generation applies the member to every
            // implementer, and those wrappers participate in interface-parameter unions even when
            // only one implementer was directly referenced by the exporting package.
            ExpandedTargetTypes = capability.ExpandedTargetTypes,
            ReturnsBuilder = false,
            CapabilityKind = capability.CapabilityKind
        };
    }

    private static IReadOnlyList<AtsParameterInfo> CreateApiExportSupportParameters(AtsCapabilityInfo capability)
    {
        var referencedHandleTypes = new Dictionary<string, AtsTypeRef>(StringComparer.Ordinal);

        CollectHandleTypes(capability.ReturnType);
        foreach (var parameter in capability.Parameters)
        {
            CollectHandleTypes(parameter.Type);
            if (parameter.CallbackParameters is { } callbackParameters)
            {
                foreach (var callbackParameter in callbackParameters)
                {
                    CollectHandleTypes(callbackParameter.Type);
                }
            }

            CollectHandleTypes(parameter.CallbackReturnType);
        }

        return referencedHandleTypes
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static (pair, index) => new AtsParameterInfo
            {
                Name = $"__apiExportSupportType{index}",
                Type = pair.Value
            })
            .ToList();

        void CollectHandleTypes(AtsTypeRef? typeRef)
        {
            if (typeRef is null)
            {
                return;
            }

            if (typeRef.Category == AtsTypeCategory.Handle && !string.IsNullOrEmpty(typeRef.TypeId))
            {
                referencedHandleTypes.TryAdd(typeRef.TypeId, typeRef);
            }

            CollectHandleTypes(typeRef.ElementType);
            CollectHandleTypes(typeRef.KeyType);
            CollectHandleTypes(typeRef.ValueType);
            if (typeRef.UnionTypes is { } unionTypes)
            {
                foreach (var unionType in unionTypes)
                {
                    CollectHandleTypes(unionType);
                }
            }
        }
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
            CollectHandleTypeMembers(handleType, handleTypesById, dtoTypesById, enumTypesById, includedHandleTypeIds, includedDtoTypeIds, includedEnumTypeIds);
        }

        if (dtoTypesById.TryGetValue(typeRef.TypeId, out var dtoType) && includedDtoTypeIds.Add(dtoType.TypeId))
        {
            CollectDtoTypeMembers(dtoType, handleTypesById, dtoTypesById, enumTypesById, includedHandleTypeIds, includedDtoTypeIds, includedEnumTypeIds);
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

    private static void CollectHandleTypeMembers(
        AtsTypeInfo handleType,
        IReadOnlyDictionary<string, AtsTypeInfo> handleTypesById,
        IReadOnlyDictionary<string, AtsDtoTypeInfo> dtoTypesById,
        IReadOnlyDictionary<string, AtsEnumTypeInfo> enumTypesById,
        HashSet<string> includedHandleTypeIds,
        HashSet<string> includedDtoTypeIds,
        HashSet<string> includedEnumTypeIds)
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

    private static void CollectDtoTypeMembers(
        AtsDtoTypeInfo dtoType,
        IReadOnlyDictionary<string, AtsTypeInfo> handleTypesById,
        IReadOnlyDictionary<string, AtsDtoTypeInfo> dtoTypesById,
        IReadOnlyDictionary<string, AtsEnumTypeInfo> enumTypesById,
        HashSet<string> includedHandleTypeIds,
        HashSet<string> includedDtoTypeIds,
        HashSet<string> includedEnumTypeIds)
    {
        foreach (var property in dtoType.Properties)
        {
            CollectReferencedType(property.Type, handleTypesById, dtoTypesById, enumTypesById, includedHandleTypeIds, includedDtoTypeIds, includedEnumTypeIds);

            // Callback properties are emitted as function signatures, so their parameter and return
            // types are just as load-bearing as the declared property type.
            if (property.CallbackParameters is not null)
            {
                foreach (var callbackParameter in property.CallbackParameters)
                {
                    CollectReferencedType(callbackParameter.Type, handleTypesById, dtoTypesById, enumTypesById, includedHandleTypeIds, includedDtoTypeIds, includedEnumTypeIds);
                }
            }

            CollectReferencedType(property.CallbackReturnType, handleTypesById, dtoTypesById, enumTypesById, includedHandleTypeIds, includedDtoTypeIds, includedEnumTypeIds);
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

    private static HashSet<string> GetKnownAssemblyNames(AtsContext context, HashSet<string> assemblyNames)
    {
        var knownAssemblyNames = new HashSet<string>(assemblyNames, StringComparer.OrdinalIgnoreCase);
        var capabilityIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var capability in context.Capabilities)
        {
            capabilityIds.Add(capability.CapabilityId);
            AddAssemblyNameFromId(knownAssemblyNames, capability.CapabilityId);
        }

        foreach (var type in context.HandleTypes)
        {
            AddAssemblyName(knownAssemblyNames, type.ClrType?.Assembly);
            AddAssemblyNameFromId(knownAssemblyNames, type.AtsTypeId);
        }

        foreach (var type in context.DtoTypes)
        {
            AddAssemblyName(knownAssemblyNames, type.ClrType?.Assembly);
            AddAssemblyNameFromId(knownAssemblyNames, type.TypeId);
        }

        foreach (var type in context.EnumTypes)
        {
            AddAssemblyName(knownAssemblyNames, type.ClrType?.Assembly);
        }

        foreach (var exportedValue in context.ExportedValues)
        {
            AddAssemblyName(knownAssemblyNames, exportedValue.OwningAssemblyName);
        }

        foreach (var (capabilityId, method) in context.Methods)
        {
            if (capabilityIds.Contains(capabilityId))
            {
                AddAssemblyName(knownAssemblyNames, method.DeclaringType?.Assembly);
            }
        }

        foreach (var (capabilityId, property) in context.Properties)
        {
            if (capabilityIds.Contains(capabilityId))
            {
                AddAssemblyName(knownAssemblyNames, property.DeclaringType?.Assembly);
            }
        }

        return knownAssemblyNames;
    }

    private static void AddAssemblyName(HashSet<string> assemblyNames, Assembly? assembly)
    {
        AddAssemblyName(assemblyNames, assembly?.GetName().Name);
    }

    private static void AddAssemblyName(HashSet<string> assemblyNames, string? assemblyName)
    {
        if (!string.IsNullOrWhiteSpace(assemblyName))
        {
            assemblyNames.Add(assemblyName);
        }
    }

    private static void AddAssemblyNameFromId(HashSet<string> assemblyNames, string id)
    {
        if (TryGetAssemblyNameFromId(id, out var assemblyName))
        {
            assemblyNames.Add(assemblyName);
        }
    }

    private static bool IsDiagnosticOwnedBySelectedAssembly(
        AtsContext context,
        AtsDiagnostic diagnostic,
        HashSet<string> assemblyNames,
        HashSet<string> knownAssemblyNames)
    {
        if (string.IsNullOrWhiteSpace(diagnostic.Location))
        {
            return true;
        }

        if (TryGetAssemblyNameFromDiagnosticLocation(context, diagnostic.Location, knownAssemblyNames, out var assemblyName))
        {
            return assemblyNames.Contains(assemblyName);
        }

        return false;
    }

    private static bool TryGetAssemblyNameFromDiagnosticLocation(
        AtsContext context,
        string location,
        HashSet<string> knownAssemblyNames,
        out string assemblyName)
    {
        if (TryGetAssemblyNameFromId(location, out assemblyName))
        {
            return true;
        }

        foreach (var capability in context.Capabilities)
        {
            if (!string.Equals(capability.SourceLocation, location, StringComparison.Ordinal))
            {
                continue;
            }

            if (context.Methods.TryGetValue(capability.CapabilityId, out var method))
            {
                assemblyName = method.DeclaringType?.Assembly.GetName().Name ?? string.Empty;
                return assemblyName.Length > 0;
            }

            if (context.Properties.TryGetValue(capability.CapabilityId, out var property))
            {
                assemblyName = property.DeclaringType?.Assembly.GetName().Name ?? string.Empty;
                return assemblyName.Length > 0;
            }
        }

        return TryGetMostSpecificDottedAssemblyName(location, knownAssemblyNames, out assemblyName);
    }

    private static bool TryGetMostSpecificDottedAssemblyName(string location, HashSet<string> knownAssemblyNames, out string assemblyName)
    {
        assemblyName = string.Empty;

        foreach (var knownAssemblyName in knownAssemblyNames)
        {
            if (location.Length <= knownAssemblyName.Length ||
                location[knownAssemblyName.Length] != '.' ||
                !location.StartsWith(knownAssemblyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (knownAssemblyName.Length > assemblyName.Length)
            {
                assemblyName = knownAssemblyName;
            }
        }

        return assemblyName.Length > 0;
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
