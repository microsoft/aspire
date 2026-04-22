// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Reflection;
using System.Text;
using Aspire.TypeSystem;

namespace Aspire.Hosting.CodeGeneration.Go;

internal static class GoLang
{
    public static readonly HashSet<string> ReservedKeywords = new(StringComparer.Ordinal)
    {
        "break", "case", "chan", "const", "continue", "default", "defer", "else",
        "fallthrough", "for", "func", "go", "goto", "if", "import", "interface",
        "map", "package", "range", "return", "select", "struct", "switch", "type", "var"
    };
}

/// <summary>
/// Represents a builder/wrapper type to be generated with its capabilities.
/// Internal type replacing BuilderModel - used only within the generator.
/// </summary>
internal sealed class BuilderModel
{
    public required string TypeId { get; init; }
    public required string BuilderStructName { get; init; }
    public required List<AtsCapabilityInfo> Capabilities { get; init; }
    public bool IsInterface { get; init; }
    public AtsTypeRef? TargetType { get; init; }
    public bool IsResourceBuilder => TargetType?.IsResourceBuilder ?? false;
}

internal sealed class OptionsStructInfo
{
    public required string StructName { get; init; }
    public required List<AtsParameterInfo> Params { get; init; }
}

/// <summary>
/// Generates a Go SDK using the ATS (Aspire Type System) capability-based API.
/// Produces typed builder structs with fluent methods that use invokeCapability().
/// </summary>
/// <remarks>
/// <para>
/// <b>ATS to Go Type Mapping</b>
/// </para>
/// <para>
/// The generator maps ATS types to Go types according to the following rules:
/// </para>
/// <para>
/// <b>Primitive Types:</b>
/// <list type="table">
///   <listheader>
///     <term>ATS Type</term>
///     <description>Go Type</description>
///   </listheader>
///   <item><term><c>string</c></term><description><c>string</c></description></item>
///   <item><term><c>number</c></term><description><c>float64</c></description></item>
///   <item><term><c>boolean</c></term><description><c>bool</c></description></item>
///   <item><term><c>any</c></term><description><c>any</c> (i.e., <c>interface{}</c>)</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Handle Types:</b>
/// Type IDs use the format <c>{AssemblyName}/{TypeName}</c>.
/// <list type="table">
///   <listheader>
///     <term>ATS Type ID</term>
///     <description>Go Type</description>
///   </listheader>
///   <item><term><c>Aspire.Hosting/IDistributedApplicationBuilder</c></term><description><c>IDistributedApplicationBuilder</c></description></item>
///   <item><term><c>Aspire.Hosting/DistributedApplication</c></term><description><c>DistributedApplication</c></description></item>
///   <item><term><c>Aspire.Hosting/DistributedApplicationExecutionContext</c></term><description><c>DistributedApplicationExecutionContext</c></description></item>
///   <item><term><c>Aspire.Hosting.Redis/RedisResource</c></term><description><c>RedisResource</c></description></item>
///   <item><term><c>Aspire.Hosting/ContainerResource</c></term><description><c>ContainerResource</c></description></item>
///   <item><term><c>Aspire.Hosting.ApplicationModel/IResource</c></term><description><c>IResourceHandle</c></description></item>
/// </list>
/// </para>
/// <para>
/// <b>Handle Type Naming Rules:</b>
/// <list type="bullet">
///   <item><description>Core types: Use type name + "Handle"</description></item>
///   <item><description>Interface types: Use interface name + "Handle" (keep the "I" prefix)</description></item>
///   <item><description>Resource types: Use type name + "BuilderHandle"</description></item>
/// </list>
/// </para>
/// </remarks>
internal sealed class AtsGoCodeGenerator : ICodeGenerator
{
    private TextWriter _writer = null!;

    // Mapping of typeId -> wrapper struct name for all generated struct types
    // Used to resolve parameter types to wrapper structs instead of handle types
    private readonly Dictionary<string, string> _wrapperStructNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _resourceBuilderStructNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _dtoNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _enumTypeNames = new(StringComparer.Ordinal);

    // Options tracking — mirrors TypeScript generator exactly
    private readonly HashSet<string> _generatedOptionsInterfaces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OptionsStructInfo> _optionsInterfacesToGenerate = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _capabilityOptionsInterfaceMap = new(StringComparer.Ordinal);

    private readonly Dictionary<string, string> _implStructNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _implToInterface = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _unionNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AtsTypeRef> _unionsToGenerate = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string Language => "Go";

    /// <inheritdoc />
    public Dictionary<string, string> GenerateDistributedApplication(AtsContext context) => new(StringComparer.Ordinal)
    {
        ["go.mod"] = """
                     module apphost/modules/aspire

                     go 1.26
                     """,
        ["transport.go"] = GetEmbeddedResource("transport.go"),
        ["base.go"] = GetEmbeddedResource("base.go"),
        ["aspire.go"] = GenerateAspireSdk(context)
    };

    private static string GetEmbeddedResource(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"Aspire.Hosting.CodeGeneration.Go.Resources.{name}";

        using var stream = assembly.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException($"Embedded resource '{name}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private string GenerateAspireSdk(AtsContext context)
    {
        using var stringWriter = new StringWriter(CultureInfo.InvariantCulture);
        _writer = stringWriter;

        // Header
        WriteLine("""
            // aspire.go - Capability-based Aspire SDK
            // This SDK uses the ATS (Aspire Type System) capability API.
            // Capabilities are endpoints like 'Aspire.Hosting/createBuilder'.
            //
            // GENERATED CODE - DO NOT EDIT

            package aspire

            """);
        WriteLine();

        var capabilities = context.Capabilities;
        var dtoTypes = context.DtoTypes;
        var enumTypes = context.EnumTypes;

        // Get builder models (flattened - each builder has all its applicable capabilities)
        var allBuilders = CreateBuilderModels(capabilities);
        var entryPoints = GetEntryPointCapabilities(capabilities);

        // Collect all unique type IDs for handle type aliases
        // Exclude DTO types - they have their own interfaces, not handle aliases
        var dtoTypeIds = new HashSet<string>(dtoTypes.Select(d => d.TypeId));
        var typeIds = new HashSet<string>();
        foreach (var cap in capabilities)
        {
            if (!string.IsNullOrEmpty(cap.TargetTypeId) && !dtoTypeIds.Contains(cap.TargetTypeId))
            {
                typeIds.Add(cap.TargetTypeId);
            }
            if (IsHandleType(cap.ReturnType) && !dtoTypeIds.Contains(cap.ReturnType.TypeId))
            {
                typeIds.Add(GetReturnTypeId(cap));
            }
            // Add parameter type IDs (for types like IResourceBuilder<IResource>)
            foreach (var param in cap.Parameters)
            {
                if (IsHandleType(param.Type) && !dtoTypeIds.Contains(param.Type!.TypeId))
                {
                    typeIds.Add(param.Type!.TypeId);
                }
                // Also collect callback parameter types
                if (param is not { IsCallback: true, CallbackParameters: not null })
                {
                    continue;
                }

                foreach (var cbParam in param.CallbackParameters)
                {
                    if (IsHandleType(cbParam.Type) && !dtoTypeIds.Contains(cbParam.Type.TypeId))
                    {
                        typeIds.Add(cbParam.Type.TypeId);
                    }
                }
            }
        }

        // Ensure all builder type IDs have handle type wrappers.
        // CreateBuilderModels discovers additional resource types via CollectAllReferencedTypes
        // (e.g. types that appear only in return types or parameters but aren't direct capability targets).
        // Without this, the builder class references a handle type that was never declared.
        foreach (var builder in allBuilders.Where(builder => !dtoTypeIds.Contains(builder.TypeId)))
        {
            typeIds.Add(builder.TypeId);
        }

        // ── Phase 1: Clear all mutable state (idempotency across multiple calls) ──────────────
        _wrapperStructNames.Clear();
        _dtoNames.Clear();
        _enumTypeNames.Clear();
        _implStructNames.Clear();
        _implToInterface.Clear();
        _resourceBuilderStructNames.Clear();
        _generatedOptionsInterfaces.Clear();
        _optionsInterfacesToGenerate.Clear();
        _capabilityOptionsInterfaceMap.Clear();
        _unionNames.Clear();
        _unionsToGenerate.Clear();

        // ── Phase 2: Populate DTO and enum name tables ───────────────────────────────────────
        foreach (var dto in dtoTypes)
        {
            if (dto.TypeId == AtsConstants.ReferenceExpressionTypeId)
            {
                continue;
            }

            var dtoName = ExtractSimpleTypeName(dto.TypeId);
            _dtoNames[dto.TypeId] = dtoName;
            _generatedOptionsInterfaces.Add(dtoName); // reserve the name from options structs
        }

        foreach (var enumType in enumTypes)
        {
            var enumName = ExtractSimpleTypeName(enumType.TypeId);
            _enumTypeNames[enumType.TypeId] = enumName;
            _generatedOptionsInterfaces.Add(enumName); // reserve the name from options structs
        }

        // ── Phase 3: Populate wrapper/impl name tables from builder models ───────────────────
        foreach (var builder in allBuilders)
        {
            _wrapperStructNames[builder.TypeId] = builder.BuilderStructName;

            if (builder.IsInterface)
            {
                // Unexported impl struct: e.g. "DistributedApplicationBuilder" → "distributedApplicationBuilderImpl"
                var implName = ToCamelCase(builder.BuilderStructName) + "Impl";
                _implStructNames[builder.TypeId] = implName;
                _implToInterface[implName] = builder.BuilderStructName;

                if (builder.IsResourceBuilder)
                {
                    // The impl struct also counts as a resource builder (satisfies the interface).
                    _resourceBuilderStructNames.Add(implName);
                }
            }
            else if (builder.IsResourceBuilder)
            {
                _resourceBuilderStructNames.Add(builder.BuilderStructName);
            }
        }

        // ── Phase 4: Pre-scan capabilities to register options structs ────────────────────────
        // Builder capabilities (methods on resource/wrapper types)
        foreach (var builder in allBuilders)
        {
            foreach (var cap in builder.Capabilities)
            {
                var optionalParams = cap.Parameters
                    .Where(p => p is { IsOptional: true, IsCallback: false } && !IsCancellationToken(p))
                    .ToList();
                if (optionalParams.Count > 0)
                {
                    RegisterOptionsInterface(cap.CapabilityId, ToPascalCase(cap.MethodName), optionalParams);
                }
            }
        }

        // Entry-point capabilities (e.g. createBuilderWithOptions → CreateBuilderOptions)
        foreach (var cap in entryPoints)
        {
            var optionalParams = cap.Parameters
                .Where(p => p is { IsOptional: true, IsCallback: false } && !IsCancellationToken(p))
                .ToList();
            if (optionalParams.Count > 0)
            {
                RegisterOptionsInterface(cap.CapabilityId, ToPascalCase(cap.MethodName), optionalParams);
            }
        }

        // ── Phase 5: Collect union types ─────────────────────────────────────────────────────
        CollectUnionTypes(context);

        // ── Phase 6: Emit code sections in order ─────────────────────────────────────────────
        // Collect collection types first so GenerateHandleTypeWrappers can exclude them
        // (Dict/List TypeIds like "Aspire.Hosting/Dict<string,any>" are handled specially
        // in GenerateHandleWrapperRegistrations via *AspireDict/*AspireList — generating
        // stub wrappers for them would produce invalid Go identifiers with angle brackets).
        var collectionTypes = CollectListAndDictTypeIds(capabilities);

        GenerateHandleTypeWrappers(typeIds, collectionTypes);
        GenerateEnumTypes(enumTypes);
        GenerateDtoTypes(dtoTypes);
        GenerateUnionTypes();
        GenerateOptionsInterfaces();
        GenerateBuilderStructs(allBuilders);
        GenerateConnectionHelper();
        GenerateEntryPointFunctions(entryPoints);
        GenerateHandleWrapperRegistrations(allBuilders, collectionTypes);

        return stringWriter.ToString();
    }

    #region Aspire SDK Generator Helpers
    /// <summary>
    /// Groups capabilities by ExpandedTargetTypes to create builder models.
    /// Uses expansion to map interface targets to their concrete implementations.
    /// Also creates builders for interface types (for use as return type wrappers).
    /// </summary>
    private static List<BuilderModel> CreateBuilderModels(IReadOnlyList<AtsCapabilityInfo> capabilities)
    {
        // Group capabilities by expanded target type IDs
        // A capability targeting IResource with ExpandedTargetTypes = [RedisResource]
        // will be assigned to Aspire.Hosting.Redis/RedisResource (the concrete type)
        var capabilitiesByTypeId = new Dictionary<string, List<AtsCapabilityInfo>>();

        // Track the AtsTypeRef for each typeId (from ExpandedTargetTypes or TargetType metadata)
        var typeRefsByTypeId = new Dictionary<string, AtsTypeRef>();

        // Also track interface types and their capabilities (for interface wrapper structs)
        var interfaceCapabilities = new Dictionary<string, List<AtsCapabilityInfo>>();

        foreach (var cap in capabilities)
        {
            var targetTypeRef = cap.TargetType;
            var targetTypeId = cap.TargetTypeId;
            if (targetTypeRef == null || string.IsNullOrEmpty(targetTypeId))
            {
                // Entry point methods - handled separately
                continue;
            }

            // Use category-based check instead of string parsing
            if (targetTypeRef.Category != AtsTypeCategory.Handle)
            {
                continue;
            }

            // ReferenceExpression is implemented manually in base.go, including its handle wrapper
            // registration, so it must not also generate a duplicate wrapper class in aspire.go.
            if (targetTypeId == AtsConstants.ReferenceExpressionTypeId)
            {
                continue;
            }

            // Use expanded types if available, otherwise fall back to the original target
            var expandedTypes = cap.ExpandedTargetTypes;
            if (expandedTypes is { Count: > 0 })
            {
                // Flatten to concrete types
                foreach (var expandedType in expandedTypes)
                {
                    if (!capabilitiesByTypeId.TryGetValue(expandedType.TypeId, out var list))
                    {
                        list = [];
                        capabilitiesByTypeId[expandedType.TypeId] = list;
                        // Store the type ref for this expanded type
                        typeRefsByTypeId[expandedType.TypeId] = expandedType;
                    }
                    list.Add(cap);
                }

                // Also track the original interface type for wrapper class generation
                if (targetTypeRef.IsInterface)
                {
                    if (!interfaceCapabilities.TryGetValue(targetTypeId, out var interfaceList))
                    {
                        interfaceList = [];
                        interfaceCapabilities[targetTypeId] = interfaceList;
                        // Store the type ref for the interface
                        typeRefsByTypeId[targetTypeId] = targetTypeRef;
                    }
                    interfaceList.Add(cap);
                }
            }
            else
            {
                // No expansion - use original target (concrete type)
                if (!capabilitiesByTypeId.TryGetValue(targetTypeId, out var list))
                {
                    list = [];
                    capabilitiesByTypeId[targetTypeId] = list;
                    // Store the type ref for this target type
                    typeRefsByTypeId[targetTypeId] = targetTypeRef;
                }
                list.Add(cap);
            }
        }

        // Create a builder for each concrete type with its specific capabilities
        var builders = new List<BuilderModel>();
        foreach (var (typeId, typeCapabilities) in capabilitiesByTypeId)
        {
            var builderStructName = CreateStructName(typeId);

            // Get the type ref from tracked metadata (based on target type, not return type)
            var typeRef = typeRefsByTypeId.GetValueOrDefault(typeId);

            // Deduplicate capabilities by CapabilityId to avoid duplicate methods
            var uniqueCapabilities = typeCapabilities
                .GroupBy(c => c.CapabilityId)
                .Select(g => g.First())
                .ToList();

            var builder = new BuilderModel
            {
                TypeId = typeId,
                BuilderStructName = builderStructName,
                Capabilities = uniqueCapabilities,
                IsInterface = typeRef?.IsInterface ?? false,
                TargetType = typeRef
            };

            builders.Add(builder);
        }

        // Also create builders for interface types (for use as return type wrappers)
        // These are needed when methods return interface types like IResourceWithConnectionString
        foreach (var (interfaceTypeId, caps) in interfaceCapabilities)
        {
            // Skip if already added (shouldn't happen, but be safe)
            if (capabilitiesByTypeId.ContainsKey(interfaceTypeId))
            {
                continue;
            }

            var builderStructName = CreateStructName(interfaceTypeId);

            // Get the type ref from tracked metadata
            var typeRef = typeRefsByTypeId.GetValueOrDefault(interfaceTypeId);

            // Deduplicate capabilities
            var uniqueCapabilities = caps
                .GroupBy(c => c.CapabilityId)
                .Select(g => g.First())
                .ToList();

            var builder = new BuilderModel
            {
                TypeId = interfaceTypeId,
                BuilderStructName = builderStructName,
                Capabilities = uniqueCapabilities,
                IsInterface = true,
                TargetType = typeRef
            };

            builders.Add(builder);
        }

        // Also create builders for resource types referenced anywhere in capabilities
        // This handles types like RedisCommanderResource that appear in callback signatures,
        // return types, or parameter types but aren't capability targets
        var allReferencedTypeRefs = CollectAllReferencedTypes(capabilities);

        // Track all types we already have builders for (concrete + interface)
        var existingBuilderTypeIds = new HashSet<string>(capabilitiesByTypeId.Keys);
        foreach (var (interfaceTypeId, _) in interfaceCapabilities)
        {
            existingBuilderTypeIds.Add(interfaceTypeId);
        }

        foreach (var (typeId, typeRef) in allReferencedTypeRefs)
        {
            // Skip types we already have builders for (from concrete or interface lists)
            if (existingBuilderTypeIds.Contains(typeId))
            {
                continue;
            }

            // Only create builders for resource types (using metadata instead of string parsing)
            if (!typeRef.IsResourceBuilder)
            {
                continue;
            }

            var builderStructName = CreateStructName(typeId);
            var builder = new BuilderModel
            {
                TypeId = typeId,
                BuilderStructName = builderStructName,
                Capabilities = [],  // No specific capabilities - uses base type methods
                IsInterface = typeRef.IsInterface,
                TargetType = typeRef
            };
            builders.Add(builder);
        }

        // Deduplicate builders by class name, preferring concrete types over interfaces.
        // This handles cases where both a concrete type (e.g. AzureKeyVaultResource) and
        // its interface (IAzureKeyVaultResource → AzureKeyVaultResource) produce the same class name.
        // Sort: concrete types first, then interfaces
        return builders
            .OrderBy(b => b.IsInterface)
            .ThenBy(b => b.BuilderStructName)
            .GroupBy(b => b.BuilderStructName)
            .Select(g => g.First())
            .ToList();
    }

    #region SDK Generator Helpers
    private void GenerateHandleTypeWrappers(HashSet<string> typeIds, Dictionary<string, bool> collectionTypes)
    {
        // Emit minimal wrapper structs for types referenced in capabilities but not covered
        // by any builder model (those types were already registered in _wrapperStructNames
        // during the builder model population step in GenerateAspireSdk).
        // Skip collection types (Dict/List) — they have TypeIds like "Aspire.Hosting/Dict<string,any>"
        // which produce invalid Go identifiers; they are registered in GenerateHandleWrapperRegistrations
        // as *AspireDict/*AspireList instead.
        var stubTypeIds = typeIds
            .Where(id => !_wrapperStructNames.ContainsKey(id))
            .Where(id => !collectionTypes.ContainsKey(id))
            .Where(id => id != AtsConstants.ReferenceExpressionTypeId)
            .Where(id => !IsCancellationTokenTypeId(id))
            .Where(id => !id.EndsWith("[]", StringComparison.Ordinal))
            .OrderBy(id => id)
            .ToList();

        if (stubTypeIds.Count == 0)
        {
            return;
        }

        WriteLine("// ============================================================================");
        WriteLine("// Handle Type Wrappers");
        WriteLine("// ============================================================================");
        WriteLine();

        foreach (var typeId in stubTypeIds)
        {
            var structName = CreateStructName(typeId);
            // Register so subsequent generators (MapWrapperType, GenerateHandleWrapperRegistrations)
            // resolve this type correctly.
            _wrapperStructNames[typeId] = structName;

            WriteLine($"// {structName} wraps a handle for {typeId}.");
            WriteLine($"type {structName} struct {{");
            WriteLine("\tHandleWrapperBase");
            WriteLine("}");
            WriteLine();
            WriteLine($"// New{structName} creates a new {structName}.");
            WriteLine($"func New{structName}(handle *Handle, client *AspireClient) *{structName} {{");
            WriteLine($"\treturn &{structName}{{HandleWrapperBase: NewHandleWrapperBase(handle, client, nil)}}");
            WriteLine("}");
            WriteLine();
        }
    }

    /// <summary>
    /// Generates Go constant types from discovered enum types.
    /// </summary>
    private void GenerateEnumTypes(IReadOnlyList<AtsEnumTypeInfo> enumTypes)
    {
        if (enumTypes.Count == 0)
        {
            return;
        }

        WriteLine("// ============================================================================");
        WriteLine("// Constants");
        WriteLine("// ============================================================================");
        WriteLine();

        foreach (var enumType in enumTypes)
        {
            if (enumType.ClrType is null)
            {
                continue;
            }

            var enumName = _enumTypeNames[enumType.TypeId];
            WriteLine($"// {enumName} represents {enumType.Name}.");
            WriteLine($"type {enumName} string");
            WriteLine();
            WriteLine("const (");
            foreach (var member in Enum.GetNames(enumType.ClrType))
            {
                var memberName = $"{enumName}{ToPascalCase(member)}";
                WriteLine($"\t{memberName} {enumName} = \"{member}\"");
            }
            WriteLine(")");
            WriteLine();
        }
    }

    private void GenerateDtoTypes(IReadOnlyList<AtsDtoTypeInfo> dtoTypes)
    {
        if (dtoTypes.Count == 0)
        {
            return;
        }

        WriteLine("// ============================================================================");
        WriteLine("// DTOs");
        WriteLine("// ============================================================================");
        WriteLine();

        foreach (var dto in dtoTypes)
        {
            // Skip ReferenceExpression - it's defined in base.go
            if (dto.TypeId == AtsConstants.ReferenceExpressionTypeId)
            {
                continue;
            }

            var dtoName = _dtoNames[dto.TypeId];
            WriteLine($"// {dtoName} represents {dto.Name}.");
            WriteLine($"type {dtoName} struct {{");
            if (dto.Properties.Count == 0)
            {
                WriteLine("}");
                WriteLine();
                continue;
            }

            foreach (var property in dto.Properties)
            {
                var propertyName = ToPascalCase(property.Name);
                var propertyType = MapTypeRefToGo(property.Type, property.IsOptional);
                var jsonTag = $"`json:\"{property.Name},omitempty\"`";
                WriteLine($"\t{propertyName} {propertyType} {jsonTag}");
            }
            WriteLine("}");
            WriteLine();

            // Generate ToMap method for serialization
            WriteLine($"// ToMap converts the DTO to a map for JSON serialization.");
            WriteLine($"func (d *{dtoName}) ToMap() map[string]any {{");
            WriteLine("\treturn map[string]any{");
            foreach (var property in dto.Properties)
            {
                var propertyName = ToPascalCase(property.Name);
                WriteLine($"\t\t\"{property.Name}\": SerializeValue(d.{propertyName}),");
            }
            WriteLine("\t}");
            WriteLine("}");
            WriteLine();
        }
    }

    private void GenerateUnionTypes()
    {
        if (_unionsToGenerate.Count == 0)
        {
            return;
        }

        WriteLine("// ============================================================================");
        WriteLine("// Union Types");
        WriteLine("// ============================================================================");
        WriteLine();

        foreach (var (unionName, typeRef) in _unionsToGenerate.OrderBy(kvp => kvp.Key))
        {
            var memberTypes = typeRef.UnionTypes!;

            // ── Struct definition (embeds AspireUnion to inherit MarshalJSON) ─────────────
            var typeDescriptions = string.Join(" or ", memberTypes.Select(mt => MapTypeRefToGo(mt, false)));
            WriteLine($"// {unionName} is a union of {typeDescriptions}.");
            WriteLine($"// Use the New* constructors to create values; use As* methods to extract them.");
            WriteLine($"type {unionName} struct {{ AspireUnion }}");
            WriteLine();

            // ── Typed constructors ─────────────────────────────────────────────────────────
            foreach (var memberType in memberTypes)
            {
                var goType = MapTypeRefToGo(memberType, false);
                var goTypeNameForFunc = GoTypeNameForFunc(goType);
                WriteLine($"// New{unionName}From{goTypeNameForFunc} creates a new {unionName} holding a {goType} value.");
                WriteLine($"func New{unionName}From{goTypeNameForFunc}(v {goType}) *{unionName} {{");
                WriteLine($"\treturn &{unionName}{{AspireUnion: AspireUnion{{Value: v}}}}");
                WriteLine("}");
                WriteLine();
            }

            // ── Typed accessors ────────────────────────────────────────────────────────────
            foreach (var memberType in memberTypes)
            {
                var goType = MapTypeRefToGo(memberType, false);
                var goTypeNameForFunc = GoTypeNameForFunc(goType);
                WriteLine($"// As{goTypeNameForFunc} returns the {goType} value if the union holds it.");
                WriteLine($"func (u *{unionName}) As{goTypeNameForFunc}() ({goType}, bool) {{");
                WriteLine($"\tv, ok := u.Value.({goType})");
                WriteLine("\treturn v, ok");
                WriteLine("}");
                WriteLine();
            }

            // NOTE: MarshalJSON is promoted from the embedded AspireUnion — no additional code needed.
        }
    }

    private void GenerateOptionsInterfaces()
    {
        if (_optionsInterfacesToGenerate.Count == 0)
        {
            return;
        }

        WriteLine("// ============================================================================");
        WriteLine("// Options Structs");
        WriteLine("// ============================================================================");
        WriteLine();

        foreach (var (_, info) in _optionsInterfacesToGenerate)
        {
            WriteLine($"// {info.StructName} holds optional parameters.");
            WriteLine($"type {info.StructName} struct {{");
            foreach (var p in info.Params)
            {
                var fieldName = ToPascalCase(p.Name);
                var fieldType = MapTypeRefToGo(p.Type, p.IsOptional);
                var jsonTag = $"`json:\"{p.Name},omitempty\"`";
                WriteLine($"\t{fieldName} {fieldType} {jsonTag}");
            }

            WriteLine("}");
            WriteLine();

            WriteLine($"func (o *{info.StructName}) ToMap() map[string]any {{");
            WriteLine("\tm := map[string]any{}");
            foreach (var p in info.Params)
            {
                var fieldName = ToPascalCase(p.Name);
                var baseType = MapTypeRefToGo(p.Type, false);
                // If the base type is NOT nilable (e.g. string, bool, float64, enum), then the
                // optional form adds a *, so we must dereference when reading the field value.
                var valueExpr = !IsNilableGoType(baseType) ? $"*o.{fieldName}" : $"o.{fieldName}";
                WriteLine($"\tif o.{fieldName} != nil {{");
                WriteLine($"\t\tm[\"{p.Name}\"] = SerializeValue({valueExpr})");
                WriteLine("\t}");
            }

            WriteLine("\treturn m");
            WriteLine("}");
            WriteLine();
        }
    }

    private void GenerateBuilderStructs(IReadOnlyList<BuilderModel> builderModels)
    {
        if (builderModels.Count == 0)
        {
            return;
        }

        WriteLine("// ============================================================================");
        WriteLine("// Builder and Wrapper Structs");
        WriteLine("// ============================================================================");
        WriteLine();

        foreach (var builderModel in builderModels.OrderBy(t => t.BuilderStructName, StringComparer.Ordinal))
        {
            // NOTE: Unlike the TypeScript generator which uses 'extends' for inheritance, the Go generator
            // "flattens" all capabilities from base types and interfaces directly onto the concrete
            // struct. This is a more idiomatic Go approach that favors composition and avoids the
            // complexities of mimicking classical inheritance with struct embedding. The public API
            // of the generated struct is complete, containing all methods from its hierarchy.
            var baseStruct = builderModel.IsResourceBuilder ? "ResourceBuilderBase" : "HandleWrapperBase";
            var allMethods = builderModel.Capabilities.Count > 0 ? builderModel.Capabilities : null;

            if (builderModel.IsInterface && _implStructNames.TryGetValue(builderModel.TypeId, out var implName))
            {
                // ── Go interface (public API contract mirroring the C# interface) ──────────────────
                // The C# interface name (e.g. IDistributedApplicationBuilder) is mapped to a Go interface
                // name without the 'I' prefix (e.g. DistributedApplicationBuilder).
                WriteLine($"// {builderModel.BuilderStructName} is the Go interface for the C# interface type {builderModel.TypeId}.");
                WriteLine($"type {builderModel.BuilderStructName} interface {{");
                // Every interface type satisfies HandleReference via Handle().
                WriteLine("\tHandle() *Handle");
                if (allMethods != null)
                {
                    foreach (var method in allMethods)
                    {
                        EmitInterfaceMethodSignature(builderModel.BuilderStructName, builderModel.IsResourceBuilder, method);
                    }
                }
                WriteLine("}");
                WriteLine();

                // ── Unexported concrete implementation struct ─────────────────────────────────────
                WriteLine($"// {implName} is the concrete implementation of {builderModel.BuilderStructName}.");
                WriteLine($"type {implName} struct {{");
                WriteLine($"\t{baseStruct}");
                // Interface types don't have list/dict fields (those belong to the concrete impl only if needed)
                WriteLine("}");
                WriteLine();

                // Unexported constructor (used by CreateBuilder / handle-wrapper factories).
                // Prefix with "new" to avoid a Go name collision: a type and a function cannot share
                // the same identifier in the same package (e.g. "type azureImpl struct" + "func azureImpl(...)" collide).
                var ctorName = "new" + ToPascalCase(implName);
                WriteLine($"func {ctorName}(handle *Handle, client *AspireClient, bctx *builderContext) *{implName} {{");
                WriteLine($"\treturn &{implName}{{");
                WriteLine($"\t\t{baseStruct}: New{baseStruct}(handle, client, bctx),");
                WriteLine("\t}");
                WriteLine("}");
                WriteLine();

                // Methods on impl struct
                if (allMethods != null)
                {
                    foreach (var method in allMethods)
                    {
                        GenerateCapabilityMethod(implName, builderModel.IsResourceBuilder, method);
                    }
                }
            }
            else
            {
                // ── Concrete struct ───────────────────────────────────────────────────────────────

                // Collect list/dict property fields
                var listDictFields = new List<(string fieldName, string fieldType)>();
                if (allMethods != null)
                {
                    foreach (var method in allMethods)
                    {
                        var parameters = method.Parameters
                            .Where(p => !string.Equals(p.Name, method.TargetParameterName ?? "builder", StringComparison.Ordinal))
                            .ToList();

                        if (parameters.Count != 0 || !IsListOrDictPropertyGetter(method.ReturnType))
                        {
                            continue;
                        }

                        var returnType = method.ReturnType;
                        var isDict = returnType.Category == AtsTypeCategory.Dict;
                        var wrapperType = isDict ? "AspireDict" : "AspireList";

                        string typeArgs;
                        if (isDict)
                        {
                            var keyType = MapTypeRefToGo(returnType.KeyType, false);
                            var valueType = MapTypeRefToGo(returnType.ValueType, false);
                            typeArgs = $"[{keyType}, {valueType}]";
                        }
                        else
                        {
                            var elementType = MapTypeRefToGo(returnType.ElementType, false);
                            typeArgs = $"[{elementType}]";
                        }

                        var fieldName = ToCamelCase(ToPascalCase(method.MethodName));
                        listDictFields.Add((fieldName, $"*{wrapperType}{typeArgs}"));
                    }
                }

                WriteLine($"// {builderModel.BuilderStructName} wraps a handle for {builderModel.TypeId}.");
                WriteLine($"type {builderModel.BuilderStructName} struct {{");
                WriteLine($"\t{baseStruct}");
                foreach (var (fieldName, fieldType) in listDictFields)
                {
                    WriteLine($"\t{fieldName} {fieldType}");
                }
                WriteLine("}");
                WriteLine();

                // Public constructor (bctx=nil: used by factory deserialization only;
                // Add* goroutines set bctx explicitly via newLazyResourceBuilder).
                WriteLine($"// New{builderModel.BuilderStructName} creates a new {builderModel.BuilderStructName}.");
                WriteLine($"func New{builderModel.BuilderStructName}(handle *Handle, client *AspireClient) *{builderModel.BuilderStructName} {{");
                WriteLine($"\treturn &{builderModel.BuilderStructName}{{");
                WriteLine($"\t\t{baseStruct}: New{baseStruct}(handle, client, nil),");
                WriteLine("\t}");
                WriteLine("}");
                WriteLine();

                if (allMethods != null)
                {
                    foreach (var method in allMethods)
                    {
                        GenerateCapabilityMethod(builderModel.BuilderStructName, builderModel.IsResourceBuilder, method);
                    }
                }
            }
        }
    }

    private void GenerateCapabilityMethod(string structName, bool isResourceBuilder, AtsCapabilityInfo capability)
    {
        var targetParamName = capability.TargetParameterName ?? "builder";
        var methodName = ToPascalCase(capability.MethodName);
        var parameters = capability.Parameters
            .Where(p => !string.Equals(p.Name, targetParamName, StringComparison.Ordinal))
            .ToList();

        // Check if this is a List/Dict property getter (no parameters, returns List/Dict)
        if (parameters.Count == 0 && IsListOrDictPropertyGetter(capability.ReturnType))
        {
            GenerateListOrDictProperty(structName, capability, methodName);
            return;
        }

        // Categorize parameters into non-overlapping buckets.
        var requiredParams = parameters
            .Where(p => p is { IsOptional: false, IsCallback: false } && !IsCancellationToken(p))
            .ToList();
        var callbackParams = parameters.Where(p => p.IsCallback).ToList();
        var cancellationParams = parameters.Where(IsCancellationToken).ToList();

        _capabilityOptionsInterfaceMap.TryGetValue(capability.CapabilityId, out var optionsStructName);
        _optionsInterfacesToGenerate.TryGetValue(optionsStructName ?? "", out var optionsInfo);

        if (optionsInfo != null && requiredParams.Count == 0)
        {
            // Zero required params + optional params → emit a single variadic method (no "WithOptions"
            // suffix needed). This mirrors TypeScript's "method(options?: OptionsType)" pattern.
            // Callers can write: resource.Method() or resource.Method(&MethodOptions{...})
            EmitCapabilityMethod(structName, isResourceBuilder, capability, methodName,
                requiredParams, callbackParams, cancellationParams, optionsInfo, variadicOptions: true);
            return;
        }

        // Simple version — required + callbacks + CTs only (optional non-cb params omitted).
        EmitCapabilityMethod(structName, isResourceBuilder, capability, methodName,
            requiredParams, callbackParams, cancellationParams, optionsInfo: null);

        // WithOptions version — emitted only when there are optional non-callback params alongside
        // required params. Suffix changed from "WithOpts" to "WithOptions" for readability
        // (e.g. AddDockerfileWithOptions, not AddDockerfileWithOpts).
        if (optionsInfo != null)
        {
            EmitCapabilityMethod(structName, isResourceBuilder, capability, methodName + "WithOptions",
                requiredParams, callbackParams, cancellationParams, optionsInfo);
        }
    }

    private void GenerateListOrDictProperty(string structName, AtsCapabilityInfo capability, string methodName)
    {
        var returnType = capability.ReturnType;
        var isDict = returnType.Category == AtsTypeCategory.Dict;

        // Determine type arguments
        string typeArgs;
        if (isDict)
        {
            var keyType = MapTypeRefToGo(returnType.KeyType, false);
            var valueType = MapTypeRefToGo(returnType.ValueType, false);
            typeArgs = $"[{keyType}, {valueType}]";
        }
        else
        {
            var elementType = MapTypeRefToGo(returnType.ElementType, false);
            typeArgs = $"[{elementType}]";
        }

        var wrapperType = isDict ? "AspireDict" : "AspireList";
        var factoryFunc = isDict ? "NewAspireDictWithGetter" : "NewAspireListWithGetter";

        // Generate comment
        if (!string.IsNullOrEmpty(capability.Description))
        {
            WriteLine($"// {methodName} {char.ToLowerInvariant(capability.Description[0])}{capability.Description[1..]}");
        }

        // Generate getter method with lazy initialization
        var fieldName = ToCamelCase(methodName);
        WriteLine($"func (s *{structName}) {methodName}() *{wrapperType}{typeArgs} {{");
        WriteLine($"\tif s.{fieldName} == nil {{");
        WriteLine($"\t\ts.{fieldName} = {factoryFunc}{typeArgs}(s.Handle(), s.Client(), \"{capability.CapabilityId}\")");
        WriteLine("\t}");
        WriteLine($"\treturn s.{fieldName}");
        WriteLine("}");
        WriteLine();
    }

    private void GenerateConnectionHelper()
    {
        WriteLine("""
            // ============================================================================
            // Connection Helpers
            // ============================================================================

            // ResolveHandle converts a Handle (from a union type) into a fully-typed wrapper object.
            func (c *AspireClient) ResolveHandle(v any) (any, error) {
            	if h, ok := v.(*Handle); ok {
            		handleWrapperMu.RLock()
            		factory, factoryOk := handleWrapperRegistry[h.TypeID]
            		handleWrapperMu.RUnlock()
            		if factoryOk {
            			return factory(h, c), nil
            		}
            		return h, nil
            	}
            	return v, nil
            }

            """);

        // CreateBuilder is the primary connection entry point — always generated regardless of
        // whether createBuilderWithOptions appears in the scanned capabilities (it may have a
        // non-empty TargetTypeId depending on how the scanner maps CreateBuilderOptions).
        GenerateCreateBuilderFunction();
    }

    /// <summary>
    /// Generates exported entry-point functions for capabilities with no target type.
    /// Mirrors TypeScript's <c>GenerateAspireClient</c> → <c>GenerateEntryPointFunction</c> pattern.
    /// </summary>
    private void GenerateEntryPointFunctions(IReadOnlyList<AtsCapabilityInfo> entryPoints)
    {
        if (entryPoints.Count == 0)
        {
            return;
        }

        WriteLine("// ============================================================================");
        WriteLine("// Entry Point Functions");
        WriteLine("// ============================================================================");
        WriteLine();

        foreach (var cap in entryPoints)
        {
            GenerateEntryPointFunction(cap);
        }
    }

    private void GenerateEntryPointFunction(AtsCapabilityInfo cap)
    {
        // createBuilderWithOptions is the well-known builder entry point — special-cased for
        // bctx setup and Args/ProjectDirectory auto-injection.
        if (cap.CapabilityId == "Aspire.Hosting/createBuilderWithOptions")
        {
            GenerateCreateBuilderFunction();
            return;
        }

        // Generic path: simple Connect → InvokeCapability → return typed result.
        var funcName = ToPascalCase(cap.MethodName);
        var returnTypeId = GetReturnTypeId(cap);
        var returnStructName = _wrapperStructNames.GetValueOrDefault(returnTypeId)
            ?? ExtractSimpleTypeName(returnTypeId);

        var requiredParams = cap.Parameters
            .Where(p => p is { IsOptional: false, IsCallback: false } && !IsCancellationToken(p))
            .ToList();
        var optionalParams = cap.Parameters
            .Where(p => p is { IsOptional: true, IsCallback: false } && !IsCancellationToken(p))
            .ToList();

        var paramParts = requiredParams.Select(p => $"{p.Name} {MapTypeRefToGo(p.Type, p.IsOptional)}").ToList();
        if (optionalParams.Count > 0 && _capabilityOptionsInterfaceMap.TryGetValue(cap.CapabilityId, out var optStructName))
        {
            paramParts.Add($"options *{optStructName}");
        }

        WriteLine($"// {funcName} invokes the {cap.CapabilityId} capability.");
        WriteLine($"func {funcName}({string.Join(", ", paramParts)}) (*{returnStructName}, error) {{");
        WriteLine("\tclient, err := Connect()");
        WriteLine("\tif err != nil {");
        WriteLine("\t\treturn nil, err");
        WriteLine("\t}");
        WriteLine("\trpcArgs := map[string]any{}");
        foreach (var p in requiredParams)
        {
            WriteLine($"\trpcArgs[\"{p.Name}\"] = SerializeValue({p.Name})");
        }
        if (optionalParams.Count > 0)
        {
            WriteLine("\tif options != nil {");
            WriteLine("\t\tfor k, v := range options.ToMap() {");
            WriteLine("\t\t\trpcArgs[k] = v");
            WriteLine("\t\t}");
            WriteLine("\t}");
        }
        WriteLine($"\tresult, err := client.InvokeCapability(\"{cap.CapabilityId}\", rpcArgs)");
        WriteLine("\tif err != nil {");
        WriteLine("\t\treturn nil, err");
        WriteLine("\t}");
        WriteLine($"\treturn result.(*{returnStructName}), nil");
        WriteLine("}");
        WriteLine();
    }

    private void GenerateCreateBuilderFunction()
    {
        var builderStructName = _wrapperStructNames.GetValueOrDefault(AtsConstants.BuilderTypeId, "DistributedApplicationBuilder");
        _implStructNames.TryGetValue(AtsConstants.BuilderTypeId, out var builderImplName);
        var builderIsInterface = builderImplName is not null;

        // Return type: interface name (no *) if builder is an interface; *ConcreteStruct otherwise.
        var createBuilderReturnType = builderIsInterface ? builderStructName : $"*{builderStructName}";

        WriteLine($"// CreateBuilder creates a new distributed application builder.");
        WriteLine($"func CreateBuilder(options *CreateBuilderOptions) ({createBuilderReturnType}, error) {{");
        WriteLine("""
            	client, err := Connect()
            	if err != nil {
            		return nil, err
            	}
            	resolvedOptions := make(map[string]any)
            	if options != nil {
            		for k, v := range options.ToMap() {
            			resolvedOptions[k] = v
            		}
            	}
            	if _, ok := resolvedOptions["Args"]; !ok {
            		resolvedOptions["Args"] = os.Args[1:]
            	}
            	if _, ok := resolvedOptions["ProjectDirectory"]; !ok {
            		if pwd, err := os.Getwd(); err == nil {
            			resolvedOptions["ProjectDirectory"] = pwd
            		}
            	}
            	result, err := client.InvokeCapability("Aspire.Hosting/createBuilderWithOptions", map[string]any{"options": resolvedOptions})
            	if err != nil {
            		return nil, err
            	}
            """);
        if (builderIsInterface)
        {
            // Extract the handle from the factory-created wrapper, then build a properly-bctx'd impl.
            // The factory wrapper has bctx=nil (it was registered without one); we replace it here.
            WriteLine("\tbctx := &builderContext{}");
            WriteLine("\tref := result.(HandleReference)");
            WriteLine($"\treturn new{ToPascalCase(builderImplName!)}(ref.Handle(), client, bctx), nil");
        }
        else
        {
            WriteLine($"\treturn result.(*{builderStructName}), nil");
        }
        WriteLine("}");
        WriteLine();
    }

    private void GenerateHandleWrapperRegistrations(
        IReadOnlyList<BuilderModel> builderModels,
        Dictionary<string, bool> collectionTypes)
    {
        WriteLine("// ============================================================================");
        WriteLine("// Wrapper Registrations");
        WriteLine("// ============================================================================");
        WriteLine();
        WriteLine("func init() {");

        foreach (var builderModel in builderModels)
        {
            WriteLine($"\tRegisterHandleWrapper(\"{builderModel.TypeId}\", func(h *Handle, c *AspireClient) any {{");
            if (builderModel.IsInterface && _implStructNames.TryGetValue(builderModel.TypeId, out var regImplName))
            {
                // Interface type: use the unexported impl constructor (3-arg: handle, client, bctx=nil).
                // CreateBuilder will replace bctx with a real one before returning.
                WriteLine($"\t\treturn new{ToPascalCase(regImplName)}(h, c, nil)");
            }
            else
            {
                WriteLine($"\t\treturn New{builderModel.BuilderStructName}(h, c)");
            }
            WriteLine("\t})");
        }

        foreach (var (typeId, isDict) in collectionTypes)
        {
            var wrapperType = isDict ? "AspireDict" : "AspireList";
            var typeArgs = isDict ? "[any, any]" : "[any]";
            WriteLine($"\tRegisterHandleWrapper(\"{typeId}\", func(h *Handle, c *AspireClient) any {{");
            WriteLine($"\t\treturn &{wrapperType}{typeArgs}{{HandleWrapperBase: NewHandleWrapperBase(h, c, nil)}}");
            WriteLine("\t})");
        }

        WriteLine("}");
        WriteLine();
    }

    /// <summary>
    /// Records that <paramref name="capabilityId"/> uses an options struct for
    /// <paramref name="optionalParams"/> and assigns (or creates) a struct name.
    /// Merges into an existing compatible struct when possible; creates a numbered
    /// variant when the base name is taken by an incompatible struct or a reserved type.
    /// Mirrors TypeScript's <c>RegisterOptionsInterface</c>.
    /// </summary>
    private void RegisterOptionsInterface(string capabilityId, string methodName, List<AtsParameterInfo> optionalParams)
    {
        var baseStructName = GetOptionsInterfaceName(methodName);

        if (_optionsInterfacesToGenerate.TryGetValue(baseStructName, out var existing))
        {
            if (AreOptionsCompatible(existing.Params, optionalParams))
            {
                // Merge: add any new params from optionalParams not already in existing.
                var existingNames = existing.Params.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
                foreach (var p in optionalParams)
                {
                    if (!existingNames.Contains(p.Name))
                    {
                        existing.Params.Add(p);
                    }
                }

                _capabilityOptionsInterfaceMap[capabilityId] = baseStructName;
                return;
            }

            // Incompatible — try numbered variants: methodName + N + "Options"
            var counter = 1;
            while (true)
            {
                var numberedName = methodName + counter + "Options";

                if (!_optionsInterfacesToGenerate.TryGetValue(numberedName, out var numberedExisting))
                {
                    // Free slot — create.
                    var info = new OptionsStructInfo { StructName = numberedName, Params = [.. optionalParams] };
                    _optionsInterfacesToGenerate[numberedName] = info;
                    _capabilityOptionsInterfaceMap[capabilityId] = numberedName;
                    return;
                }

                if (AreOptionsCompatible(numberedExisting.Params, optionalParams))
                {
                    var existingNames = numberedExisting.Params.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
                    foreach (var p in optionalParams)
                    {
                        if (!existingNames.Contains(p.Name))
                        {
                            numberedExisting.Params.Add(p);
                        }
                    }

                    _capabilityOptionsInterfaceMap[capabilityId] = numberedName;
                    return;
                }

                counter++;
            }
        }
        else if (_generatedOptionsInterfaces.Contains(baseStructName))
        {
            // Reserved by a DTO/enum/handle — find a free numbered variant.
            var counter = 1;
            while (true)
            {
                var numberedName = methodName + counter + "Options";
                if (!_optionsInterfacesToGenerate.ContainsKey(numberedName)
                    && !_generatedOptionsInterfaces.Contains(numberedName))
                {
                    var info = new OptionsStructInfo { StructName = numberedName, Params = [.. optionalParams] };
                    _optionsInterfacesToGenerate[numberedName] = info;
                    _capabilityOptionsInterfaceMap[capabilityId] = numberedName;
                    return;
                }

                counter++;
            }
        }
        else
        {
            // Brand-new name — register it.
            var info = new OptionsStructInfo { StructName = baseStructName, Params = [.. optionalParams] };
            _optionsInterfacesToGenerate[baseStructName] = info;
            _capabilityOptionsInterfaceMap[capabilityId] = baseStructName;
        }
    }

    private void EmitCallbackRegistration(string paramName, AtsParameterInfo cb, string baseIndent = "\t")
    {
        if (cb.CallbackParameters is { Count: > 0 })
        {
            // Typed callback — emit an inline RegisterCallback adapter that handles all params
            var cbParams = cb.CallbackParameters;
            var i1 = baseIndent;
            var i2 = baseIndent + "\t";
            var i3 = baseIndent + "\t\t";
            var i4 = baseIndent + "\t\t\t";
            WriteLine($"{i1}if {paramName} != nil {{");
            WriteLine($"{i2}{paramName}Fn := {paramName}");
            WriteLine($"{i2}reqArgs[\"{cb.Name}\"] = RegisterCallback(func(args ...any) any {{");
            WriteLine($"{i3}if len(args) >= {cbParams.Count} {{");

            // For each callback parameter:
            //   * Union-typed: wrap the raw arg directly (no type assertion needed —
            //     the server always sends raw values, not *UnionFoo instances).
            //   * Non-union: type-assert, opening a nested if block.
            var indent = i4;
            var nestedIfCount = 0;
            for (var i = 0; i < cbParams.Count; i++)
            {
                var cp = cbParams[i];
                var argType = MapTypeRefToGo(cp.Type, false);
                var argLocal = GetLocalIdentifier(cp.Name);
                var isUnion = cp.Type.Category == AtsTypeCategory.Union;

                if (isUnion)
                {
                    var unionTypeName = argType.TrimStart('*');
                    WriteLine($"{indent}{argLocal} := &{unionTypeName}{{AspireUnion: AspireUnion{{Value: args[{i}]}}}}");
                }
                else
                {
                    WriteLine($"{indent}if {argLocal}, ok := args[{i}].({argType}); ok {{");
                    indent += "\t";
                    nestedIfCount++;
                }
            }

            var callArgs = string.Join(", ", cbParams.Select(cp => GetLocalIdentifier(cp.Name)));
            WriteLine($"{indent}{paramName}Fn({callArgs})");
            for (var i = 0; i < nestedIfCount; i++)
            {
                indent = indent[..^1];
                WriteLine($"{indent}}}");
            }
            WriteLine($"{i3}}}");
            WriteLine($"{i3}return nil");
            WriteLine($"{i2}}})");
            WriteLine($"{i1}}}");
        }
        else
        {
            // Untyped callback — current behaviour
            WriteLine($"{baseIndent}if {paramName} != nil {{");
            WriteLine($"{baseIndent}\treqArgs[\"{cb.Name}\"] = RegisterCallback({paramName})");
            WriteLine($"{baseIndent}}}");
        }
    }

    /// <summary>
    /// Emits a method signature line inside a Go interface block (no body, no receiver).
    /// Mirrors the parameter/return-type logic of <see cref="EmitCapabilityMethod"/> exactly so that
    /// the concrete impl struct satisfies the interface.
    /// </summary>
    private void EmitInterfaceMethodSignature(string interfaceName, bool isResourceBuilder, AtsCapabilityInfo capability)
    {
        var targetParamName = capability.TargetParameterName ?? "builder";
        var methodName = ToPascalCase(capability.MethodName);
        var parameters = capability.Parameters
            .Where(p => !string.Equals(p.Name, targetParamName, StringComparison.Ordinal))
            .ToList();

        // List/Dict property getters are emitted as method signatures too.
        if (parameters.Count == 0 && IsListOrDictPropertyGetter(capability.ReturnType))
        {
            var rt = capability.ReturnType;
            var isDict = rt.Category == AtsTypeCategory.Dict;
            var wrapperType = isDict ? "AspireDict" : "AspireList";
            var typeArgs = isDict ? $"[{MapTypeRefToGo(rt.KeyType, false)}, {MapTypeRefToGo(rt.ValueType, false)}]"
                : $"[{MapTypeRefToGo(rt.ElementType, false)}]";
            WriteLine($"\t{methodName}() *{wrapperType}{typeArgs}");
            return;
        }

        var requiredParams = parameters.Where(p => p is { IsOptional: false, IsCallback: false } && !IsCancellationToken(p)).ToList();
        var callbackParams = parameters.Where(p => p.IsCallback).ToList();
        var cancellationParams = parameters.Where(IsCancellationToken).ToList();
        _capabilityOptionsInterfaceMap.TryGetValue(capability.CapabilityId, out var optionsStructName);
        _optionsInterfacesToGenerate.TryGetValue(optionsStructName ?? "", out var optionsInfo);

        if (optionsInfo != null && requiredParams.Count == 0)
        {
            EmitOneInterfaceSig(interfaceName, isResourceBuilder, capability, methodName,
                requiredParams, callbackParams, cancellationParams, optionsInfo, variadicOptions: true);
            return;
        }

        EmitOneInterfaceSig(interfaceName, isResourceBuilder, capability, methodName,
            requiredParams, callbackParams, cancellationParams, optionsInfo: null);

        if (optionsInfo != null)
        {
            EmitOneInterfaceSig(interfaceName, isResourceBuilder, capability, methodName + "WithOptions",
                requiredParams, callbackParams, cancellationParams, optionsInfo);
        }
    }

    private void EmitOneInterfaceSig(
        string interfaceName, bool isResourceBuilder, AtsCapabilityInfo capability, string methodName,
        List<AtsParameterInfo> requiredParams, List<AtsParameterInfo> callbackParams,
        List<AtsParameterInfo> cancellationParams, OptionsStructInfo? optionsInfo, bool variadicOptions = false)
    {
        var returnTypeId = capability.ReturnType.TypeId;
        var isVoid = returnTypeId == AtsConstants.Void;
        var hasReturn = !isVoid;
        var returnsSameType = _wrapperStructNames.TryGetValue(returnTypeId, out var retStructName)
            && retStructName == interfaceName;

        var isFluent = isResourceBuilder && (isVoid || returnsSameType);
        var returnType = MapTypeRefToGo(capability.ReturnType, false);
        var returnChildStructName = retStructName ?? returnType.TrimStart('*');
        var returnChildIsResourceBuilder = _resourceBuilderStructNames.Contains(returnChildStructName);
        var returnsChildBuilder = capability.ReturnsBuilder && !returnsSameType && returnChildIsResourceBuilder;
        var originalTargetStructName = capability.TargetTypeId is not null
            && _wrapperStructNames.TryGetValue(capability.TargetTypeId, out var ots) ? ots : interfaceName;
        var isExpandedFluent = returnsChildBuilder && isResourceBuilder && (returnChildStructName == originalTargetStructName);

        string returnSig;
        if (isFluent || isExpandedFluent)
        {
            returnSig = interfaceName;  // return the interface type (impl *T satisfies it)
        }
        else if (returnsChildBuilder)
        {
            returnSig = returnType.StartsWith("*", StringComparison.Ordinal) ? returnType : $"*{returnType}";
        }
        else if (hasReturn)
        {
            returnSig = returnType.StartsWith("*", StringComparison.Ordinal) || returnType == "any"
                ? $"({returnType}, error)" : $"(*{returnType}, error)";
        }
        else
        {
            returnSig = "error";
        }

        var paramList = new StringBuilder();
        foreach (var p in requiredParams)
        {
            if (paramList.Length > 0)
            {
                paramList.Append(", ");
            }

            paramList.Append(CultureInfo.InvariantCulture,
                $"{GetLocalIdentifier(p.Name)} {MapTypeRefToGo(p.Type, p.IsOptional)}");
        }
        foreach (var cb in callbackParams)
        {
            if (paramList.Length > 0)
            {
                paramList.Append(", ");
            }

            paramList.Append(CultureInfo.InvariantCulture,
                $"{GetLocalIdentifier(cb.Name)} {GetCallbackParamType(cb)}");
        }
        foreach (var ct in cancellationParams)
        {
            if (paramList.Length > 0)
            {
                paramList.Append(", ");
            }

            paramList.Append(CultureInfo.InvariantCulture, $"{GetLocalIdentifier(ct.Name)} *CancellationToken");
        }
        // Options come LAST — in Go, variadic parameters (...T) must be the final parameter.
        // Non-variadic options (*T) are placed last too for consistent API style.
        if (optionsInfo != null)
        {
            if (paramList.Length > 0)
            {
                paramList.Append(", ");
            }

            paramList.Append(variadicOptions
                ? $"options ...{optionsInfo.StructName}"
                : $"options *{optionsInfo.StructName}");
        }

        WriteLine($"\t{methodName}({paramList}) {returnSig}");
    }

    /// <summary>
    /// Emits a single Go function for a capability.
    /// When <paramref name="optionsInfo"/> is null this is the simple version (no optional params).
    /// When non-null this is the <c>WithOpts</c> version that accepts an options struct.
    /// </summary>
    private void EmitCapabilityMethod(
        string structName,
        bool isResourceBuilder,
        AtsCapabilityInfo capability,
        string methodName,
        List<AtsParameterInfo> requiredParams,
        List<AtsParameterInfo> callbackParams,
        List<AtsParameterInfo> cancellationParams,
        OptionsStructInfo? optionsInfo,
        bool variadicOptions = false)
    {
        var targetParamName = capability.TargetParameterName ?? "builder";
        var returnTypeId = capability.ReturnType.TypeId;
        var isVoid = returnTypeId == AtsConstants.Void;
        var hasReturn = !isVoid;

        // returnsSameType: true when the method returns the same handle type as the receiver.
        // For interface impl structs (e.g. distributedApplicationBuilderImpl), also check whether the
        // return type's impl name matches structName — the C# interface may return itself (IFoo → IFoo).
        _wrapperStructNames.TryGetValue(returnTypeId, out var retStructName);
        var returnsSameType = retStructName == structName
            || (_implStructNames.TryGetValue(returnTypeId, out var retImplName) && retImplName == structName);

        // A method is "fluent" if it's on a resource builder and returns void or itself.
        var isFluent = isResourceBuilder && (isVoid || returnsSameType);

        var returnType = MapTypeRefToGo(capability.ReturnType, false);

        // For interface return types, use the impl struct name so we can create a lazy child builder.
        var returnChildStructName = _implStructNames.TryGetValue(returnTypeId, out var childImplName)
            ? childImplName
            : (retStructName ?? returnType.TrimStart('*'));

        // A method is "child-fluent" if it returns a DIFFERENT resource builder.
        // Checks both concrete struct names and impl struct names (for interface return types).
        var returnChildIsResourceBuilder = _resourceBuilderStructNames.Contains(returnChildStructName)
            || _resourceBuilderStructNames.Contains(retStructName ?? "");
        var returnsChildBuilder = capability.ReturnsBuilder && !returnsSameType && returnChildIsResourceBuilder;

        // When a capability from a parent/interface type is expanded onto a concrete subtype,
        // the return type still refers to the original type. Detect via original capability target.
        var originalTargetStructName = capability.TargetTypeId is not null
            && _wrapperStructNames.TryGetValue(capability.TargetTypeId, out var ots)
            ? ots
            : structName;
        // Also check the impl name for the original target (interface case).
        var originalTargetImplName = capability.TargetTypeId is not null
            && _implStructNames.TryGetValue(capability.TargetTypeId, out var oti)
            ? oti
            : null;
        var returnsOriginalTargetType = returnChildStructName == originalTargetStructName
            || (originalTargetImplName != null && returnChildStructName == originalTargetImplName);

        var isExpandedFluent = returnsChildBuilder && isResourceBuilder && returnsOriginalTargetType;

        // For fluent methods on an interface impl, the return type is the Go interface name
        // (not *implName) so that the method satisfies the interface contract.
        var fluentReturnType = _implToInterface.TryGetValue(structName, out var ifaceNameForImpl)
            ? ifaceNameForImpl   // interface type (no *): *implName satisfies IFoo
            : $"*{structName}";  // concrete type: return pointer to self

        // For child-builder methods, determine whether the return type is an interface.
        var childReturnIsInterface = capability.ReturnType.IsInterface;

        // Decide dispatch strategy up front — needed for both signature and body generation.
        // Resource builders use goroutine dispatch (mirrors TypeScript's eager async model):
        //   • Fluent/void methods submit a goroutine and immediately return self.
        //   • Child-builder methods create a lazy handle and submit a goroutine.
        // All other methods (non-resource-builder or value-returning) are synchronous.
        var useGoroutine = isResourceBuilder && (isFluent || isExpandedFluent || returnsChildBuilder);

        string returnSignature;
        if (isFluent || isExpandedFluent)
        {
            returnSignature = fluentReturnType;
        }
        else if (returnsChildBuilder)
        {
            if (useGoroutine)
            {
                // Goroutine path: single return — child builder manages its own error state via setHandleErr.
                returnSignature = childReturnIsInterface
                    ? returnType  // e.g. "IRedisResourceBuilder" (no *)
                    : (returnType.StartsWith("*", StringComparison.Ordinal) ? returnType : $"*{returnType}");
            }
            else if (childReturnIsInterface)
            {
                // Synchronous + interface return: must be a tuple — can't embed error in an interface.
                returnSignature = $"({returnType}, error)";
            }
            else
            {
                // Synchronous + concrete return: single return — error embedded in the returned struct.
                returnSignature = returnType.StartsWith("*", StringComparison.Ordinal)
                    ? returnType
                    : $"*{returnType}";
            }
        }
        else if (hasReturn)
        {
            returnSignature = returnType.StartsWith("*", StringComparison.Ordinal) || returnType == "any"
                ? $"({returnType}, error)"
                : $"(*{returnType}, error)";
        }
        else
        {
            returnSignature = "error";
        }

        // Build parameter list
        var paramList = new StringBuilder();

        foreach (var p in requiredParams)
        {
            if (paramList.Length > 0)
            {
                paramList.Append(", ");
            }

            paramList.Append(CultureInfo.InvariantCulture,
                $"{GetLocalIdentifier(p.Name)} {MapTypeRefToGo(p.Type, p.IsOptional)}");
        }

        foreach (var cb in callbackParams)
        {
            if (paramList.Length > 0)
            {
                paramList.Append(", ");
            }

            paramList.Append(CultureInfo.InvariantCulture,
                $"{GetLocalIdentifier(cb.Name)} {GetCallbackParamType(cb)}");
        }

        foreach (var ct in cancellationParams)
        {
            if (paramList.Length > 0)
            {
                paramList.Append(", ");
            }

            paramList.Append(CultureInfo.InvariantCulture, $"{GetLocalIdentifier(ct.Name)} *CancellationToken");
        }

        // Options come LAST — in Go, variadic parameters (...T) must be the final parameter.
        // Non-variadic options (*T) are placed last too for consistent API style.
        if (optionsInfo != null)
        {
            if (paramList.Length > 0)
            {
                paramList.Append(", ");
            }

            paramList.Append(variadicOptions
                ? $"options ...{optionsInfo.StructName}"
                : $"options *{optionsInfo.StructName}");
        }

        if (!string.IsNullOrEmpty(capability.Description))
        {
            WriteLine($"// {methodName} {char.ToLowerInvariant(capability.Description[0])}{capability.Description[1..]}");
        }

        // Build() on the builder must drain all pending goroutines first.
        _implStructNames.TryGetValue(AtsConstants.BuilderTypeId, out var builderImplName);
        _wrapperStructNames.TryGetValue(AtsConstants.BuilderTypeId, out var builderStructNameVal);
        var isBuilderBuild = string.Equals(methodName, "Build", StringComparison.Ordinal)
            && hasReturn
            && (structName == builderImplName || structName == builderStructNameVal);

        // First required param name for error context (usually "name" on Add* methods).
        var firstRequiredParamName = requiredParams.Count > 0 ? GetLocalIdentifier(requiredParams[0].Name) : null;

        WriteLine($"func (s *{structName}) {methodName}({paramList}) {returnSignature} {{");

        if (useGoroutine && (isFluent || isExpandedFluent))
        {
            // ── Goroutine: fluent/void ────────────────────────────────────────────────────────
            WriteLine($$"""
                	s.bctx.submit(func() error {
                		handle, err := s.awaitHandle()
                		if err != nil {
                			return fmt.Errorf("{{methodName}}: %w", err)
                		}
                """);
            EmitReqArgsBlock(targetParamName, "handle", requiredParams, optionsInfo, variadicOptions, callbackParams, cancellationParams, "\t\t");
            WriteLine($$"""
                		if _, err = s.Client().InvokeCapability("{{capability.CapabilityId}}", reqArgs); err != nil {
                			return fmt.Errorf("{{methodName}}: %w", err)
                		}
                		return nil
                	})
                	return s
                """);
        }
        else if (useGoroutine && returnsChildBuilder)
        {
            // ── Goroutine: child builder ──────────────────────────────────────────────────────
            // Create the lazy child before submitting the goroutine; the goroutine resolves its handle.
            WriteLine($"\tr := &{returnChildStructName}{{ResourceBuilderBase: newLazyResourceBuilder(s.Client(), s.bctx)}}");
            WriteLine("""
                	s.bctx.submit(func() error {
                		handle, err := s.awaitHandle()
                		if err != nil {
                """);
            WriteLine(firstRequiredParamName != null
                ? $"\t\t\tr.setHandleErr(fmt.Errorf(\"{methodName}(%q): %w\", {firstRequiredParamName}, err))"
                : $"\t\t\tr.setHandleErr(fmt.Errorf(\"{methodName}: %w\", err))");
            WriteLine("""
                      			return err
                      		}
                      """);
            EmitReqArgsBlock(targetParamName, "handle", requiredParams, optionsInfo, variadicOptions, callbackParams, cancellationParams, "\t\t");
            WriteLine($"\t\tresult, err := s.Client().InvokeCapability(\"{capability.CapabilityId}\", reqArgs)");
            WriteLine("\t\tif err != nil {");
            WriteLine(firstRequiredParamName != null
                ? $"\t\t\tr.setHandleErr(fmt.Errorf(\"{methodName}(%q): %w\", {firstRequiredParamName}, err))"
                : $"\t\t\tr.setHandleErr(fmt.Errorf(\"{methodName}: %w\", err))");
            WriteLine($$"""
                        			return err
                        		}
                        		if ref, ok := result.(HandleReference); ok {
                        			r.setHandle(ref.Handle())
                        		} else {
                        			e := fmt.Errorf("{{methodName}}: unexpected result type %T", result)
                        			r.setHandleErr(e)
                        			return e
                        		}
                        		return nil
                        	})
                        	return r
                        """);
        }
        else
        {
            // ── Synchronous dispatch ──────────────────────────────────────────────────────────
            // (value-returning methods; non-resource-builder methods; Build() on the builder)
            if (isBuilderBuild)
            {
                // Drain all pending goroutines before firing the build RPC.
                WriteLine("""
                    	if err := s.bctx.wait(); err != nil {
                    		return nil, err
                    	}
                    """);
            }

            WriteLine("\thandle, err := s.awaitHandle()");
            if (returnsChildBuilder && !childReturnIsInterface && !useGoroutine)
            {
                // Single-return concrete child builder: embed error in the returned struct.
                WriteLine($$"""
                    	if err != nil {
                    		return &{{returnChildStructName}}{ResourceBuilderBase: NewErroredResourceBuilder(err)}
                    	}
                    """);
            }
            else
            {
                WriteLine(hasReturn
                    ? """
                      	if err != nil {
                      		return nil, err
                      	}
                      """
                    : """
                      	if err != nil {
                      		return err
                      	}
                      """);
            }

            EmitReqArgsBlock(targetParamName, "handle", requiredParams, optionsInfo, variadicOptions, callbackParams, cancellationParams, "\t");

            if (returnsChildBuilder)
            {
                // Synchronous child-builder path (non-resource-builder context).
                WriteLine($"\tresult, err := s.Client().InvokeCapability(\"{capability.CapabilityId}\", reqArgs)");
                WriteLine("\tif err != nil {");
                if (childReturnIsInterface)
                {
                    // Tuple return for interface types — caller captures error.
                    WriteLine("\t\treturn nil, err");
                }
                else
                {
                    // Single return for concrete types — embed error in the returned struct.
                    WriteLine($"\t\treturn &{returnChildStructName}{{ResourceBuilderBase: NewErroredResourceBuilder(err)}}");
                }
                WriteLine("\t}");
                if (childReturnIsInterface)
                {
                    WriteLine($"\treturn result.({returnType}), nil");
                }
                else
                {
                    WriteLine($"\treturn result.(*{returnChildStructName})");
                }
            }
            else if (hasReturn)
            {
                WriteLine($"\tresult, err := s.Client().InvokeCapability(\"{capability.CapabilityId}\", reqArgs)");
                WriteLine("""
                    	if err != nil {
                    		return nil, err
                    	}
                    """);

                var isUnionReturn = capability.ReturnType.Category == AtsTypeCategory.Union;
                if (isUnionReturn)
                {
                    // Union return types: wrap the raw result directly (mirrors Java's AspireUnion.of(result)).
                    // The server sends a raw value; we wrap it in the union struct rather than type-asserting.
                    var unionTypeName = returnType.TrimStart('*');
                    WriteLine($"\treturn &{unionTypeName}{{AspireUnion: AspireUnion{{Value: result}}}}, nil");
                }
                else if (returnType.StartsWith("*", StringComparison.Ordinal))
                {
                    WriteLine($"\treturn result.({returnType}), nil");
                }
                else if (returnType == "any")
                {
                    WriteLine("\treturn result, nil");
                }
                else
                {
                    WriteLine($"\treturn result.(*{returnType}), nil");
                }
            }
            else
            {
                WriteLine($"\t_, err = s.Client().InvokeCapability(\"{capability.CapabilityId}\", reqArgs)");
                WriteLine("\treturn err");
            }
        }

        WriteLine("}");
        WriteLine();
    }

    // Emits the reqArgs map construction, options merge, callback registration, and cancellation
    // token registration into the current method body at the given indent level.
    // Used by both goroutine and synchronous dispatch paths.
    private void EmitReqArgsBlock(
        string targetParamName,
        string handleVar,
        List<AtsParameterInfo> requiredParams,
        OptionsStructInfo? optionsInfo,
        bool variadicOptions,
        List<AtsParameterInfo> callbackParams,
        List<AtsParameterInfo> cancellationParams,
        string indent)
    {
        WriteLine($"{indent}reqArgs := map[string]any{{");
        WriteLine($"{indent}\t\"{targetParamName}\": {handleVar}.ToJSON(),");
        WriteLine($"{indent}}}");

        foreach (var p in requiredParams)
        {
            var paramName = GetLocalIdentifier(p.Name);
            var paramTypeStr = MapTypeRefToGo(p.Type, p.IsOptional);
            if (p.IsOptional && IsNilableGoType(paramTypeStr))
            {
                WriteLine($"{indent}if {paramName} != nil {{");
                WriteLine($"{indent}\treqArgs[\"{p.Name}\"] = SerializeValue({paramName})");
                WriteLine($"{indent}}}");
            }
            else
            {
                WriteLine($"{indent}reqArgs[\"{p.Name}\"] = SerializeValue({paramName})");
            }
        }

        if (optionsInfo != null)
        {
            if (variadicOptions)
            {
                WriteLine($"{indent}if len(options) > 0 && options[0] != nil {{");
                WriteLine($"{indent}\tfor k, v := range options[0].ToMap() {{");
                WriteLine($"{indent}\t\treqArgs[k] = v");
                WriteLine($"{indent}\t}}");
                WriteLine($"{indent}}}");
            }
            else
            {
                WriteLine($"{indent}if options != nil {{");
                WriteLine($"{indent}\tfor k, v := range options.ToMap() {{");
                WriteLine($"{indent}\t\treqArgs[k] = v");
                WriteLine($"{indent}\t}}");
                WriteLine($"{indent}}}");
            }
        }

        foreach (var cb in callbackParams)
        {
            EmitCallbackRegistration(GetLocalIdentifier(cb.Name), cb, indent);
        }

        foreach (var ct in cancellationParams)
        {
            var paramName = GetLocalIdentifier(ct.Name);
            WriteLine($"{indent}if {paramName} != nil {{");
            WriteLine($"{indent}\treqArgs[\"{ct.Name}\"] = RegisterCancellation({paramName}, s.Client())");
            WriteLine($"{indent}}}");
        }
    }

    /// <summary>
    /// Converts a Go type name to a safe identifier suffix for function names.
    /// E.g. "*ParameterResource" → "ParameterResource", "string" → "String".
    /// </summary>
    private static string GoTypeNameForFunc(string goType) =>
        ToPascalCase(goType.Replace("*", "").Replace(".", "").Replace("[", "").Replace("]", "").Replace(",", "").Replace(" ", ""));

    /// <summary>
    /// Returns the base options struct name for a method: methodName + "Options".
    /// E.g. "AddRedis" → "AddRedisOptions".
    /// </summary>
    private static string GetOptionsInterfaceName(string methodName) => methodName + "Options";

    private string GetCallbackParamType(AtsParameterInfo cb)
    {
        if (cb.CallbackParameters is { Count: > 0 })
        {
            var parts = cb.CallbackParameters
                .Select(cp => $"{GetLocalIdentifier(cp.Name)} {MapTypeRefToGo(cp.Type, false)}")
                .ToList();
            return $"func({string.Join(", ", parts)})";
        }

        return "func(...any) any";
    }

    /// <summary>
    /// Gets entry point capabilities (those without TargetTypeId).
    /// </summary>
    private static List<AtsCapabilityInfo> GetEntryPointCapabilities(IReadOnlyList<AtsCapabilityInfo> capabilities) =>
        [.. capabilities.Where(c => string.IsNullOrEmpty(c.TargetTypeId))];

    /// <summary>
    /// Gets the TypeId from a capability's return type.
    /// </summary>
    private static string GetReturnTypeId(AtsCapabilityInfo capability) => capability.ReturnType.TypeId;

    /// <summary>
    /// True if <paramref name="candidate"/> is compatible with <paramref name="existing"/>:
    /// for every param in <paramref name="candidate"/> that also exists (by name) in
    /// <paramref name="existing"/>, their types must be equal.
    /// New params (not in <paramref name="existing"/>) are always compatible.
    /// Mirrors TypeScript's <c>AreOptionsCompatible</c>.
    /// </summary>
    private static bool AreOptionsCompatible(List<AtsParameterInfo> existing, List<AtsParameterInfo> candidate)
    {
        var existingByName = existing.ToDictionary(p => p.Name, StringComparer.Ordinal);
        foreach (var param in candidate)
        {
            if (existingByName.TryGetValue(param.Name, out var existingParam)
                && !AreParameterTypesEqual(existingParam, param))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True if the two parameters have the same type (including callback param types and return type).
    /// Mirrors TypeScript's <c>AreParameterTypesEqual</c>.
    /// </summary>
    private static bool AreParameterTypesEqual(AtsParameterInfo a, AtsParameterInfo b)
    {
        if (a.IsCallback != b.IsCallback)
        {
            return false;
        }

        if (!a.IsCallback)
        {
            return string.Equals(a.Type?.TypeId, b.Type?.TypeId, StringComparison.Ordinal);
        }

        var aCb = a.CallbackParameters ?? [];
        var bCb = b.CallbackParameters ?? [];
        if (aCb.Count != bCb.Count)
        {
            return false;
        }

        return !aCb.Where((t, i) => !string.Equals(t.Type.TypeId, bCb[i].Type.TypeId, StringComparison.Ordinal)).Any()
               && string.Equals(a.CallbackReturnType?.TypeId, b.CallbackReturnType?.TypeId, StringComparison.Ordinal);
    }

    /// <summary>
    /// Collects all type refs referenced in capabilities (return types, parameter types, callback types, etc.)
    /// Returns a dictionary mapping typeId to AtsTypeRef for use in builder creation.
    /// </summary>
    private static Dictionary<string, AtsTypeRef> CollectAllReferencedTypes(IReadOnlyList<AtsCapabilityInfo> capabilities)
    {
        var typeRefs = new Dictionary<string, AtsTypeRef>();

        void CollectFromTypeRef(AtsTypeRef? typeRef)
        {
            if (typeRef == null)
            {
                return;
            }

            if (!string.IsNullOrEmpty(typeRef.TypeId) && typeRef.Category == AtsTypeCategory.Handle)
            {
                typeRefs.TryAdd(typeRef.TypeId, typeRef);
            }

            // Also check nested types (generics, arrays, etc.)
            CollectFromTypeRef(typeRef.ElementType);
            CollectFromTypeRef(typeRef.KeyType);
            CollectFromTypeRef(typeRef.ValueType);
            if (typeRef.UnionTypes != null)
            {
                foreach (var unionType in typeRef.UnionTypes)
                {
                    CollectFromTypeRef(unionType);
                }
            }
        }

        foreach (var cap in capabilities)
        {
            // Check return type
            CollectFromTypeRef(cap.ReturnType);

            // Check parameter types
            foreach (var param in cap.Parameters)
            {
                CollectFromTypeRef(param.Type);

                // Check callback parameter types and return type
                if (param.IsCallback)
                {
                    if (param.CallbackParameters != null)
                    {
                        foreach (var cbParam in param.CallbackParameters)
                        {
                            CollectFromTypeRef(cbParam.Type);
                        }
                    }
                    CollectFromTypeRef(param.CallbackReturnType);
                }
            }
        }

        return typeRefs;
    }

    private static Dictionary<string, bool> CollectListAndDictTypeIds(IReadOnlyList<AtsCapabilityInfo> capabilities)
    {
        // Maps typeId -> isDict (true for Dict, false for List)
        var typeIds = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var capability in capabilities)
        {
            AddListOrDictTypeIfNeeded(typeIds, capability.TargetType);
            AddListOrDictTypeIfNeeded(typeIds, capability.ReturnType);
            foreach (var parameter in capability.Parameters)
            {
                AddListOrDictTypeIfNeeded(typeIds, parameter.Type);
                if (parameter is { IsCallback: true, CallbackParameters: not null })
                {
                    foreach (var callbackParam in parameter.CallbackParameters)
                    {
                        AddListOrDictTypeIfNeeded(typeIds, callbackParam.Type);
                    }
                }
            }
        }

        return typeIds;
    }

    private void CollectUnionTypes(AtsContext context)
    {
        var uniqueUnions = new Dictionary<string, AtsTypeRef>(StringComparer.Ordinal);

        foreach (var capability in context.Capabilities)
        {
            AddUnionTypeIfNeeded(capability.ReturnType);
            foreach (var parameter in capability.Parameters)
            {
                AddUnionTypeIfNeeded(parameter.Type);
            }
        }
        foreach (var dto in context.DtoTypes)
        {
            foreach (var prop in dto.Properties)
            {
                AddUnionTypeIfNeeded(prop.Type);
            }
        }

        foreach (var (key, typeRef) in uniqueUnions)
        {
            var unionTypes = typeRef.UnionTypes!;
            _unionNames[key] = $"Union{string.Join("", unionTypes.Select(t => ToPascalCase(ExtractSimpleTypeName(t.TypeId))))}";
            _unionsToGenerate[_unionNames[key]] = typeRef;
        }

        return;

        void AddUnionTypeIfNeeded(AtsTypeRef? typeRef)
        {
            while (true)
            {
                if (typeRef is null)
                {
                    return;
                }

                if (typeRef is { Category: AtsTypeCategory.Union, UnionTypes.Count: > 0 })
                {
                    var canonicalKey = string.Join("|", typeRef.UnionTypes.Select(t => t.TypeId).OrderBy(id => id));
                    uniqueUnions.TryAdd(canonicalKey, typeRef);
                }

                // Recurse into nested types
                AddUnionTypeIfNeeded(typeRef.ElementType);
                AddUnionTypeIfNeeded(typeRef.KeyType);
                typeRef = typeRef.ValueType;
            }
        }
    }

#pragma warning disable IDE0060 // Remove unused parameter - keeping for API consistency with Python generator
    private string MapTypeRefToGo(AtsTypeRef? typeRef, bool isOptional)
#pragma warning restore IDE0060
    {
        if (typeRef is null)
        {
            return "any";
        }

        if (typeRef.TypeId == AtsConstants.ReferenceExpressionTypeId)
        {
            return "*ReferenceExpression";
        }

        if (typeRef is { Category: AtsTypeCategory.Union, UnionTypes.Count: > 0 })
        {
            var canonicalKey = string.Join("|", typeRef.UnionTypes.Select(t => t.TypeId).OrderBy(id => id));
            return _unionNames.TryGetValue(canonicalKey, out var unionName) ? $"*{unionName}" : "any";
        }

        var baseType = typeRef.Category switch
        {
            AtsTypeCategory.Primitive => MapPrimitiveType(typeRef.TypeId),
            AtsTypeCategory.Enum => MapEnumType(typeRef.TypeId),
            AtsTypeCategory.Handle => typeRef.IsInterface
                ? MapWrapperType(typeRef.TypeId)        // Go interface value — no pointer needed
                : "*" + MapWrapperType(typeRef.TypeId), // pointer to concrete struct
            AtsTypeCategory.Dto => "*" + MapDtoType(typeRef.TypeId),
            AtsTypeCategory.Callback => "func(...any) any",
            AtsTypeCategory.Array => $"[]{MapTypeRefToGo(typeRef.ElementType, false)}",
            AtsTypeCategory.List => typeRef.IsReadOnly
                ? $"[]{MapTypeRefToGo(typeRef.ElementType, false)}"
                : $"*AspireList[{MapTypeRefToGo(typeRef.ElementType, false)}]",
            AtsTypeCategory.Dict => typeRef.IsReadOnly
                ? $"map[{MapTypeRefToGo(typeRef.KeyType, false)}]{MapTypeRefToGo(typeRef.ValueType, false)}"
                : $"*AspireDict[{MapTypeRefToGo(typeRef.KeyType, false)}, {MapTypeRefToGo(typeRef.ValueType, false)}]",
            AtsTypeCategory.Union => "any", // Should be handled above, but fallback just in case.
            AtsTypeCategory.Unknown => "any",
            _ => "any"
        };

        // Interface types (no *) are nilable in Go — never wrap with *.
        var isInterfaceHandle = typeRef is { Category: AtsTypeCategory.Handle, IsInterface: true };
        if (isOptional && !IsNilableGoType(baseType) && !isInterfaceHandle)
        {
            return $"*{baseType}";
        }

        return baseType;
    }

    private string MapWrapperType(string typeId) => _wrapperStructNames.GetValueOrDefault(typeId, "Handle");

    private string MapDtoType(string typeId) => _dtoNames.GetValueOrDefault(typeId, "map[string]any");

    private string MapEnumType(string typeId) => _enumTypeNames.GetValueOrDefault(typeId, "string");

    private static string MapPrimitiveType(string typeId) => typeId switch
    {
        AtsConstants.String or AtsConstants.Char => "string",
        AtsConstants.Number => "float64",
        AtsConstants.Boolean => "bool",
        AtsConstants.Void => "",
        AtsConstants.Any => "any",
        AtsConstants.DateTime or AtsConstants.DateTimeOffset or
        AtsConstants.DateOnly or AtsConstants.TimeOnly => "string",
        AtsConstants.TimeSpan => "float64",
        AtsConstants.Guid or AtsConstants.Uri => "string",
        AtsConstants.CancellationToken => "*CancellationToken",
        _ => "any"
    };

    /// <summary>
    /// Checks if an AtsTypeRef represents a handle type.
    /// </summary>
    private static bool IsHandleType(AtsTypeRef? typeRef) => typeRef is { Category: AtsTypeCategory.Handle };

    private static bool IsNilableGoType(string typeName) =>
        typeName.StartsWith('*') ||
        typeName.StartsWith("[]", StringComparison.Ordinal) ||
        typeName.StartsWith("map[", StringComparison.Ordinal) ||
        typeName == "any" ||
        typeName.StartsWith("func(", StringComparison.Ordinal);

    private static bool IsListOrDictPropertyGetter(AtsTypeRef? returnType)
    {
        if (returnType is null)
        {
            return false;
        }

        return returnType.Category == AtsTypeCategory.List || returnType.Category == AtsTypeCategory.Dict;
    }

    private static bool IsCancellationToken(AtsParameterInfo parameter) =>
        IsCancellationTokenTypeId(parameter.Type?.TypeId);

    private static bool IsCancellationTokenTypeId(string? typeId) =>
        string.Equals(typeId, AtsConstants.CancellationToken, StringComparison.Ordinal)
        || (typeId?.EndsWith("/System.Threading.CancellationToken", StringComparison.Ordinal) ?? false);

    private static void AddListOrDictTypeIfNeeded(Dictionary<string, bool> typeIds, AtsTypeRef? typeRef)
    {
        if (typeRef is null)
        {
            return;
        }

        if (typeRef.Category == AtsTypeCategory.List)
        {
            if (!typeRef.IsReadOnly)
            {
                typeIds[typeRef.TypeId] = false; // false = List
            }
        }
        else if (typeRef is { Category: AtsTypeCategory.Dict, IsReadOnly: false })
        {
            typeIds[typeRef.TypeId] = true; // true = Dict
        }
    }

    private static string SanitizeIdentifier(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "_";
        }

        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        }

        if (!char.IsLetter(builder[0]) && builder[0] != '_')
        {
            builder.Insert(0, '_');
        }

        var sanitized = builder.ToString();
        return GoLang.ReservedKeywords.Contains(sanitized) ? sanitized + "_" : sanitized;
    }

    private static string GetLocalIdentifier(string name) => SanitizeIdentifier(ToCamelCase(name));

    /// <summary>
    /// Converts a name to PascalCase for Go exported identifiers.
    /// </summary>
    private static string ToPascalCase(string name)
    {
        if (string.IsNullOrEmpty(name) || char.IsUpper(name[0]))
        {
            return name;
        }

        return char.ToUpperInvariant(name[0]) + name[1..];
    }

    /// <summary>
    /// Converts a name to camelCase for Go unexported identifiers.
    /// </summary>
    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name) || char.IsLower(name[0]))
        {
            return name;
        }

        return char.ToLowerInvariant(name[0]) + name[1..];
    }
    #endregion

    /// <summary>
    /// Extracts the simple type name from a type ID.
    /// </summary>
    /// <example>
    /// "Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResource" → "IResource"
    /// "Aspire.Hosting/Aspire.Hosting.DistributedApplication" → "DistributedApplication"
    /// </example>
    private static string ExtractSimpleTypeName(string typeId)
    {
        var slashIndex = typeId.LastIndexOf('/');
        var fullTypeName = slashIndex >= 0 ? typeId[(slashIndex + 1)..] : typeId;

        var dotIndex = fullTypeName.LastIndexOf('.');
        return dotIndex >= 0 ? fullTypeName[(dotIndex + 1)..] : fullTypeName;
    }

    private static string CreateStructName(string typeId)
    {
        var typeName = ExtractSimpleTypeName(typeId);

        // Strip leading 'I' from interface types
        if (typeName.StartsWith('I') && typeName.Length > 1 && char.IsUpper(typeName[1]))
        {
            typeName = typeName[1..];
        }

        return SanitizeIdentifier(typeName);
    }

    private void WriteLine(string? text = null)
    {
        if (text != null)
        {
            _writer.WriteLine(text);
        }
        else
        {
            _writer.WriteLine();
        }
    }
    #endregion
}
