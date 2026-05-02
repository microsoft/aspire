// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Aspire.TypeSystem;

namespace Aspire.Hosting.CodeGeneration.Go;

internal sealed class GoExportedValueTreeNode
{
    public Dictionary<string, GoExportedValueTreeNode> Children { get; } = new(StringComparer.Ordinal);

    public AtsExportedValueInfo? Value { get; set; }
}

/// <summary>
/// Generates a Go SDK using the ATS (Aspire Type System) capability-based API.
///
/// Architecture (mirrors the TypeScript generator):
///   - Public surface is interfaces; concrete impls are unexported structs.
///   - Add* / With* methods are non-blocking: they submit to the builder's
///     builderContext and return a lazy proxy immediately.
///   - Build() is the single flush point; it waits for all submitted goroutines
///     and aggregates errors via errors.Join.
///   - No init() functions; per-client wrapper / callback registries are
///     populated inside CreateBuilder via registerWrappers(c).
///   - Variadic options merge via the deepUpdate helper in base.go.
///   - Union parameters use Go 1.18+ type-set generics — no AspireUnion wrapper.
/// </summary>
internal sealed class AtsGoCodeGenerator : ICodeGenerator
{
    private static readonly HashSet<string> s_goKeywords = new(StringComparer.Ordinal)
    {
        "break", "case", "chan", "const", "continue", "default", "defer", "else",
        "fallthrough", "for", "func", "go", "goto", "if", "import", "interface",
        "map", "package", "range", "return", "select", "struct", "switch", "type", "var",
        "any", "true", "false", "nil", "iota",
    };

    private TextWriter _writer = null!;

    // typeId → exported Go interface name (e.g., "Aspire.Hosting/...IResource" → "Resource")
    private readonly Dictionary<string, string> _interfaceNames = new(StringComparer.Ordinal);

    // typeId → unexported Go struct name (concrete types only; e.g., "redisResource")
    private readonly Dictionary<string, string> _implNames = new(StringComparer.Ordinal);

    // typeId → bool: is this type an interface in metadata?
    private readonly Dictionary<string, bool> _isInterfaceType = new(StringComparer.Ordinal);

    // typeId → bool: is this concrete type a resource builder?
    private readonly Dictionary<string, bool> _isResourceBuilder = new(StringComparer.Ordinal);

    private readonly Dictionary<string, string> _dtoNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _enumNames = new(StringComparer.Ordinal);

    // capabilityId → resolved Options type name (qualified when simple name collides with a DTO or another capability's Options)
    private readonly Dictionary<string, string> _optionsTypeNames = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string Language => "Go";

    /// <inheritdoc />
    public Dictionary<string, string> GenerateDistributedApplication(AtsContext context)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["go.mod"] = """
                module apphost/modules/aspire

                go 1.23
                """,
            ["transport.go"] = GetEmbeddedResource("transport.go"),
            ["base.go"] = GetEmbeddedResource("base.go"),
            ["aspire.go"] = GenerateAspireSdk(context)
        };
    }

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

        BuildNameMaps(context);

        var capabilitiesByDirectTarget = GroupCapabilitiesByDirectTarget(context.Capabilities);
        var capabilitiesByConcreteImpl = GroupCapabilitiesByConcreteImpl(context.Capabilities);

        WriteHeader();
        GenerateEnumTypes(context.EnumTypes);
        GenerateDtoTypes(context.DtoTypes);
        GenerateExportedValues(context.ExportedValues, context.DtoTypes.ToDictionary(dto => dto.TypeId, StringComparer.Ordinal));
        GenerateMarkerInterfaces(capabilitiesByDirectTarget);
        GenerateConcreteHandleTypes(capabilitiesByConcreteImpl);
        GenerateOptionsStructs(context.Capabilities);
        GenerateRegisterWrappers();
        GenerateCreateBuilder(context);

        return stringWriter.ToString();
    }

    // ── Name resolution ──────────────────────────────────────────────────────

    private void BuildNameMaps(AtsContext context)
    {
        _interfaceNames.Clear();
        _implNames.Clear();
        _isInterfaceType.Clear();
        _isResourceBuilder.Clear();
        _dtoNames.Clear();
        _enumNames.Clear();

        foreach (var enumType in context.EnumTypes)
        {
            _enumNames[enumType.TypeId] = ReserveName(SanitizeIdentifier(enumType.Name), takenLowercase: false);
        }

        foreach (var dto in context.DtoTypes)
        {
            if (dto.TypeId == AtsConstants.ReferenceExpressionTypeId)
            {
                continue; // ReferenceExpression is provided by base.go
            }
            _dtoNames[dto.TypeId] = ReserveName(SanitizeIdentifier(dto.Name), takenLowercase: false);
        }

        // Walk metadata to discover all handle type IDs we need to emit. We
        // include both context.HandleTypes (the directly-discovered handles) and
        // any types referenced by capabilities (target / return / parameter /
        // expanded targets / callback parameters).
        var handleTypeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var handleType in context.HandleTypes)
        {
            if (handleType.AtsTypeId == AtsConstants.ReferenceExpressionTypeId
                || IsCancellationTokenTypeId(handleType.AtsTypeId))
            {
                continue;
            }
            handleTypeIds.Add(handleType.AtsTypeId);
            _isInterfaceType[handleType.AtsTypeId] = handleType.IsInterface;
            _isResourceBuilder[handleType.AtsTypeId] = handleType.IsResourceBuilder;
        }

        // Type IDs that appear in any ExpandedTargetTypes. Even if the
        // metadata classifies them as interfaces (e.g. IDistributedApplicationBuilder
        // whose ExpandedTargetTypes is just [itself]), they need a private impl
        // struct so capability methods have somewhere to live.
        var expansionTargetIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capability in context.Capabilities)
        {
            CollectHandleTypeIds(handleTypeIds, capability.TargetType);
            CollectHandleTypeIds(handleTypeIds, capability.ReturnType);
            foreach (var parameter in capability.Parameters)
            {
                CollectHandleTypeIds(handleTypeIds, parameter.Type);
                if (parameter.IsCallback && parameter.CallbackParameters is not null)
                {
                    foreach (var cbParam in parameter.CallbackParameters)
                    {
                        CollectHandleTypeIds(handleTypeIds, cbParam.Type);
                    }
                }
            }
            foreach (var expanded in capability.ExpandedTargetTypes)
            {
                CollectHandleTypeIds(handleTypeIds, expanded);
                if (!string.IsNullOrEmpty(expanded.TypeId))
                {
                    expansionTargetIds.Add(expanded.TypeId);
                }
            }
        }

        // Sort to get deterministic output. Reserve interface names first (they
        // get the bare name); concrete types fall back to suffix-based
        // disambiguation when a collision occurs.
        var ordered = handleTypeIds
            .OrderBy(id => _isInterfaceType.TryGetValue(id, out var isI) && isI ? 0 : 1)
            .ThenBy(id => id, StringComparer.Ordinal)
            .ToList();

        foreach (var typeId in ordered)
        {
            var isInterface = _isInterfaceType.TryGetValue(typeId, out var ii) && ii;
            var needsImpl = !isInterface || expansionTargetIds.Contains(typeId);
            var rawName = ExtractTypeName(typeId);
            var stripped = StripInterfacePrefix(rawName, isInterface);
            var sanitized = SanitizeIdentifier(stripped);

            string interfaceName;
            if (isInterface && !needsImpl)
            {
                interfaceName = ReserveName(sanitized, takenLowercase: false);
            }
            else
            {
                // Need both an interface and an impl: try bare, then
                // assembly-prefixed, then numeric.
                if (!IsNameTaken(sanitized))
                {
                    interfaceName = ReserveName(sanitized, takenLowercase: true);
                }
                else
                {
                    var assemblyPrefix = SanitizeIdentifier(typeId.Split('/')[0]);
                    var prefixed = $"{assemblyPrefix}{sanitized}";
                    interfaceName = !IsNameTaken(prefixed)
                        ? ReserveName(prefixed, takenLowercase: true)
                        : ReserveName(sanitized, takenLowercase: true);
                }
            }

            _interfaceNames[typeId] = interfaceName;
            if (needsImpl)
            {
                _implNames[typeId] = ToCamelCase(interfaceName);
            }
        }

        // Resolve Options struct names for all capabilities that have optional params.
        // Must run after DTOs, enums, and handle types are all reserved so collision
        // detection via IsNameTaken() is accurate.
        _optionsTypeNames.Clear();
        var optionsNamesInUse = new HashSet<string>(StringComparer.Ordinal);
        // Tracks the first capability assigned to each Options name so subsequent
        // capabilities can compare field signatures before sharing.
        var optionsNameFirstCapability = new Dictionary<string, AtsCapabilityInfo>(StringComparer.Ordinal);
        foreach (var capability in context.Capabilities)
        {
            var targetParamName = capability.TargetParameterName ?? "builder";
            var hasOptionalParams = capability.Parameters.Any(p =>
                !string.Equals(p.Name, targetParamName, StringComparison.Ordinal) &&
                (IsCancellationToken(p) || p.IsOptional));
            if (!hasOptionalParams)
            {
                continue;
            }

            var simpleName = $"{ToPascalCase(capability.MethodName)}Options";
            string resolvedName;

            if (!IsNameTaken(simpleName) && !optionsNamesInUse.Contains(simpleName))
            {
                // Name is free everywhere — use simple name and mark it used.
                resolvedName = simpleName;
                optionsNamesInUse.Add(simpleName);
                optionsNameFirstCapability[simpleName] = capability;
            }
            else if (optionsNamesInUse.Contains(simpleName) && !IsNameTaken(simpleName))
            {
                // Already claimed by a previous Options struct (same method name on a different
                // type). Reuse ONLY if the optional-param field signatures are compatible.
                // Incompatible signatures (e.g. RunAsEmulator on Storage vs EventHubs both have
                // configureContainer but with different emulator callback types) must get
                // separate qualified structs — sharing would produce a type mismatch in the
                // generated callback shim.
                if (optionsNameFirstCapability.TryGetValue(simpleName, out var firstCap)
                    && AreOptionsCompatible(capability, firstCap))
                {
                    resolvedName = simpleName; // same field shapes — safe to share
                }
                else
                {
                    // Incompatible callback types — qualify with target interface name.
                    var qualifiedName = GetOptionsQualifiedName(capability, simpleName);
                    resolvedName = qualifiedName;
                    optionsNamesInUse.Add(resolvedName);
                    optionsNameFirstCapability[resolvedName] = capability;
                }
            }
            else
            {
                // Collision with a DTO, enum, or handle type. Qualify with the target interface name.
                var qualifiedName = GetOptionsQualifiedName(capability, simpleName);
                resolvedName = qualifiedName;
                optionsNamesInUse.Add(resolvedName);
                optionsNameFirstCapability[resolvedName] = capability;
            }

            _optionsTypeNames[capability.CapabilityId] = resolvedName;
        }
    }

    private static void CollectHandleTypeIds(HashSet<string> set, AtsTypeRef? typeRef)
    {
        if (typeRef is null)
        {
            return;
        }
        if (typeRef.TypeId == AtsConstants.ReferenceExpressionTypeId
            || IsCancellationTokenTypeId(typeRef.TypeId))
        {
            return;
        }
        if (typeRef.Category == AtsTypeCategory.Handle)
        {
            set.Add(typeRef.TypeId);
        }
        if (typeRef.ElementType is not null)
        {
            CollectHandleTypeIds(set, typeRef.ElementType);
        }
        if (typeRef.KeyType is not null)
        {
            CollectHandleTypeIds(set, typeRef.KeyType);
        }
        if (typeRef.ValueType is not null)
        {
            CollectHandleTypeIds(set, typeRef.ValueType);
        }
        if (typeRef.UnionTypes is not null)
        {
            foreach (var u in typeRef.UnionTypes)
            {
                CollectHandleTypeIds(set, u);
            }
        }
    }

    private static string StripInterfacePrefix(string name, bool isInterface)
    {
        if (!isInterface || name.Length < 2 || name[0] != 'I')
        {
            return name;
        }
        if (!char.IsUpper(name[1]))
        {
            return name;
        }
        return name[1..];
    }

    private readonly HashSet<string> _reservedNames = new(StringComparer.Ordinal);

    private bool IsNameTaken(string name) => _reservedNames.Contains(name);

    private string ReserveName(string candidate, bool takenLowercase)
    {
        var name = candidate;
        var counter = 2;
        while (_reservedNames.Contains(name)
            || (takenLowercase && _reservedNames.Contains(ToCamelCase(name))))
        {
            name = $"{candidate}{counter}";
            counter++;
        }
        _reservedNames.Add(name);
        if (takenLowercase)
        {
            _reservedNames.Add(ToCamelCase(name));
        }
        return name;
    }

    // ── Capability grouping ───────────────────────────────────────────────────

    /// <summary>
    /// Groups capabilities by their direct target type ID (no expansion).
    /// Used to emit interface-metadata declarations: methods listed here go on
    /// the marker interface for the target.
    /// </summary>
    private static Dictionary<string, List<AtsCapabilityInfo>> GroupCapabilitiesByDirectTarget(
        IReadOnlyList<AtsCapabilityInfo> capabilities)
    {
        var result = new Dictionary<string, List<AtsCapabilityInfo>>(StringComparer.Ordinal);
        foreach (var capability in capabilities)
        {
            if (string.IsNullOrEmpty(capability.TargetTypeId))
            {
                continue;
            }
            if (!result.TryGetValue(capability.TargetTypeId, out var list))
            {
                list = new List<AtsCapabilityInfo>();
                result[capability.TargetTypeId] = list;
            }
            list.Add(capability);
        }
        return result;
    }

    /// <summary>
    /// Groups capabilities by every concrete type they end up applying to.
    /// Uses ExpandedTargetTypes when the target is an interface; falls back to
    /// TargetType otherwise. Used to emit method bodies on each concrete impl.
    /// </summary>
    private Dictionary<string, List<AtsCapabilityInfo>> GroupCapabilitiesByConcreteImpl(
        IReadOnlyList<AtsCapabilityInfo> capabilities)
    {
        var result = new Dictionary<string, List<AtsCapabilityInfo>>(StringComparer.Ordinal);

        foreach (var capability in capabilities)
        {
            if (string.IsNullOrEmpty(capability.TargetTypeId))
            {
                continue;
            }

            var targetTypes = capability.ExpandedTargetTypes.Count > 0
                ? capability.ExpandedTargetTypes
                : capability.TargetType is not null
                    ? [capability.TargetType]
                    : (IReadOnlyList<AtsTypeRef>)Array.Empty<AtsTypeRef>();

            foreach (var targetType in targetTypes)
            {
                if (string.IsNullOrEmpty(targetType.TypeId))
                {
                    continue;
                }
                // Emit on any type that has an impl struct — concrete types
                // and self-referencing interfaces (e.g. IDistributedApplicationBuilder).
                if (!_implNames.ContainsKey(targetType.TypeId))
                {
                    continue;
                }
                if (!result.TryGetValue(targetType.TypeId, out var list))
                {
                    list = new List<AtsCapabilityInfo>();
                    result[targetType.TypeId] = list;
                }
                list.Add(capability);
            }
        }
        return result;
    }

    // ── Header / imports ─────────────────────────────────────────────────────

    private void WriteHeader()
    {
        WriteLine("""
            // aspire.go - Capability-based Aspire SDK
            // This SDK uses the ATS (Aspire Type System) capability API.
            // Capabilities are endpoints like 'Aspire.Hosting/createBuilder'.
            //
            // GENERATED CODE - DO NOT EDIT

            package aspire

            import (
                "context"
                "fmt"
                "os"
                "time"
            )

            // Compile-time references to keep imports used in minimal SDKs.
            var _ = context.Background
            var _ = fmt.Errorf
            var _ = os.Getenv
            var _ = time.Second
            """);
        WriteLine();
    }

    // ── Enums ────────────────────────────────────────────────────────────────

    private void GenerateEnumTypes(IReadOnlyList<AtsEnumTypeInfo> enumTypes)
    {
        if (enumTypes.Count == 0)
        {
            return;
        }

        WriteLine("// ============================================================================");
        WriteLine("// Enums");
        WriteLine("// ============================================================================");
        WriteLine();

        foreach (var enumType in enumTypes)
        {
            if (enumType.ClrType is null)
            {
                continue;
            }

            var enumName = _enumNames[enumType.TypeId];
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

    // ── DTOs ─────────────────────────────────────────────────────────────────

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
                EmitEmptyToMap(dtoName);
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

            WriteLine($"// ToMap converts the DTO to a map for JSON serialization.");
            WriteLine($"func (d *{dtoName}) ToMap() map[string]any {{");
            WriteLine("\tm := map[string]any{}");
            foreach (var property in dto.Properties)
            {
                var propertyName = ToPascalCase(property.Name);
                var propertyType = MapTypeRefToGo(property.Type, property.IsOptional);
                if (IsNilableGoType(propertyType))
                {
                    WriteLine($"\tif d.{propertyName} != nil {{ m[\"{property.Name}\"] = serializeValue(d.{propertyName}) }}");
                }
                else
                {
                    WriteLine($"\tm[\"{property.Name}\"] = serializeValue(d.{propertyName})");
                }
            }
            WriteLine("\treturn m");
            WriteLine("}");
            WriteLine();
        }
    }

    private void GenerateExportedValues(
        IReadOnlyList<AtsExportedValueInfo> exportedValues,
        IReadOnlyDictionary<string, AtsDtoTypeInfo> dtoTypesById)
    {
        if (exportedValues.Count == 0)
        {
            return;
        }

        var root = BuildExportedValueTree(exportedValues);

        WriteLine("// ============================================================================");
        WriteLine("// Exported Values");
        WriteLine("// ============================================================================");
        WriteLine();

        foreach (var (name, node) in root.Children.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            WriteLine($"var {name} = {RenderGoExportedValueType(node, indentLevel: 0)}{RenderGoExportedValueValue(node, dtoTypesById, indentLevel: 0)}");
            WriteLine();
        }
    }

    private string RenderGoExportedValueType(GoExportedValueTreeNode node, int indentLevel)
    {
        var indent = new string('\t', indentLevel);
        var nestedIndent = new string('\t', indentLevel + 1);
        var sb = new StringBuilder();
        sb.AppendLine("struct {");

        foreach (var (name, child) in node.Children.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (child.Value is { } valueInfo)
            {
                sb.Append(nestedIndent);
                sb.Append(name);
                sb.Append(' ');
                sb.AppendLine(MapTypeRefToGo(valueInfo.Type, isOptional: false));
            }
            else
            {
                sb.Append(nestedIndent);
                sb.Append(name);
                sb.Append(' ');
                sb.Append(RenderGoExportedValueType(child, indentLevel + 1));
                sb.AppendLine();
            }
        }

        sb.Append(indent);
        sb.Append('}');
        return sb.ToString();
    }

    private string RenderGoExportedValueValue(
        GoExportedValueTreeNode node,
        IReadOnlyDictionary<string, AtsDtoTypeInfo> dtoTypesById,
        int indentLevel)
    {
        var indent = new string('\t', indentLevel);
        var nestedIndent = new string('\t', indentLevel + 1);
        var sb = new StringBuilder();
        sb.AppendLine("{");

        foreach (var (name, child) in node.Children.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            sb.Append(nestedIndent);
            sb.Append(name);
            sb.Append(": ");
            if (child.Value is { } valueInfo)
            {
                sb.Append(RenderGoExportedValue(valueInfo.Value, valueInfo.Type, dtoTypesById));
            }
            else
            {
                sb.Append(RenderGoExportedValueType(child, indentLevel + 1));
                sb.Append(RenderGoExportedValueValue(child, dtoTypesById, indentLevel + 1));
            }

            sb.AppendLine(",");
        }

        sb.Append(indent);
        sb.Append('}');
        return sb.ToString();
    }

    private string RenderGoExportedValue(
        JsonNode? value,
        AtsTypeRef typeRef,
        IReadOnlyDictionary<string, AtsDtoTypeInfo> dtoTypesById)
    {
        if (value is null)
        {
            return "nil";
        }

        return typeRef.Category switch
        {
            AtsTypeCategory.Primitive => value.ToRelaxedJsonString(),
            AtsTypeCategory.Enum => $"{MapTypeRefToGo(typeRef, isOptional: false)}({value.ToRelaxedJsonString()})",
            AtsTypeCategory.Dto when value is JsonObject obj && dtoTypesById.TryGetValue(typeRef.TypeId, out var dtoInfo)
                => RenderGoDtoValue(obj, dtoInfo, dtoTypesById),
            AtsTypeCategory.Array or AtsTypeCategory.List when value is JsonArray arr
                => $"[]{MapTypeRefToGo(typeRef.ElementType, isOptional: false)}{{{string.Join(", ", arr.Select(item => RenderGoExportedValue(item, typeRef.ElementType!, dtoTypesById)))}}}",
            AtsTypeCategory.Dict when value is JsonObject obj
                => $"map[{MapTypeRefToGo(typeRef.KeyType, isOptional: false)}]{MapTypeRefToGo(typeRef.ValueType, isOptional: false)}{{{string.Join(", ", obj.Select(pair => $"{AtsJsonCodeWriter.ToRelaxedJsonString(pair.Key)}: {RenderGoExportedValue(pair.Value, typeRef.ValueType!, dtoTypesById)}"))}}}",
            _ => value.ToRelaxedJsonString()
        };
    }

    private string RenderGoDtoValue(
        JsonObject value,
        AtsDtoTypeInfo dtoInfo,
        IReadOnlyDictionary<string, AtsDtoTypeInfo> dtoTypesById)
    {
        var sb = new StringBuilder();
        sb.Append('&');
        sb.Append(_dtoNames[dtoInfo.TypeId]);
        sb.Append('{');

        var members = new List<string>();
        foreach (var property in dtoInfo.Properties)
        {
            if (!value.TryGetPropertyValue(property.Name, out var propertyValue))
            {
                continue;
            }

            members.Add($"{ToPascalCase(property.Name)}: {RenderGoExportedValue(propertyValue, property.Type, dtoTypesById)}");
        }

        sb.Append(string.Join(", ", members));
        sb.Append('}');
        return sb.ToString();
    }

    private static GoExportedValueTreeNode BuildExportedValueTree(IReadOnlyList<AtsExportedValueInfo> exportedValues)
    {
        var root = new GoExportedValueTreeNode();
        foreach (var exportedValue in exportedValues)
        {
            var current = root;
            foreach (var segment in exportedValue.PathSegments)
            {
                if (!current.Children.TryGetValue(segment, out var child))
                {
                    child = new GoExportedValueTreeNode();
                    current.Children[segment] = child;
                }

                current = child;
            }

            current.Value = exportedValue;
        }

        return root;
    }

    private void EmitEmptyToMap(string dtoName)
    {
        WriteLine($"// ToMap converts the DTO to a map for JSON serialization.");
        WriteLine($"func (d *{dtoName}) ToMap() map[string]any {{ return map[string]any{{}} }}");
        WriteLine();
    }

    // ── Marker interfaces (interface-metadata types) ─────────────────────────
    //
    // Interface-metadata types are emitted as empty marker interfaces. They
    // don't declare methods because Go has no return-type covariance: if we
    // declared `WithEnvironment() ResourceWithEnvironment` here, no concrete
    // impl could satisfy it while also providing a fluent return of its own
    // concrete type. Concrete impls list every method inline (see
    // GenerateConcreteHandleTypes), and capability returns of an interface
    // type fall back to the marker interface, which any concrete satisfies
    // trivially.

    private void GenerateMarkerInterfaces(Dictionary<string, List<AtsCapabilityInfo>> capabilitiesByDirectTarget)
    {
        var interfaceTypeIds = _interfaceNames
            .Where(kv => _isInterfaceType.TryGetValue(kv.Key, out var isI) && isI && !_implNames.ContainsKey(kv.Key))
            .OrderBy(kv => kv.Value, StringComparer.Ordinal)
            .ToList();

        if (interfaceTypeIds.Count == 0)
        {
            return;
        }

        WriteLine("// ============================================================================");
        WriteLine("// Marker interfaces (from interface-metadata types)");
        WriteLine("// ============================================================================");
        WriteLine();

        foreach (var (typeId, name) in interfaceTypeIds)
        {
            var hasMethods = capabilitiesByDirectTarget.ContainsKey(typeId);
            var note = hasMethods
                ? "Methods are emitted on concrete impls; this interface is a marker for type assertions."
                : "Marker interface.";
            WriteLine($"// {name} marks types implementing {ExtractTypeName(typeId)}.");
            WriteLine($"// {note}");
            WriteLine($"type {name} interface {{");
            WriteLine("\thandleReference");
            WriteLine("}");
            WriteLine();
        }
    }

    // ── Concrete handle types: interface + impl + methods ─────────────────────

    private void GenerateConcreteHandleTypes(Dictionary<string, List<AtsCapabilityInfo>> capabilitiesByConcreteImpl)
    {
        var concreteTypeIds = _implNames.Keys.OrderBy(id => _interfaceNames[id], StringComparer.Ordinal).ToList();
        if (concreteTypeIds.Count == 0)
        {
            return;
        }

        WriteLine("// ============================================================================");
        WriteLine("// Handle wrappers");
        WriteLine("// ============================================================================");
        WriteLine();

        foreach (var typeId in concreteTypeIds)
        {
            var interfaceName = _interfaceNames[typeId];
            var implName = _implNames[typeId];
            var capabilities = capabilitiesByConcreteImpl.TryGetValue(typeId, out var caps)
                ? caps.Where(c => !IsSpecialCasedCapability(c.CapabilityId))
                      .OrderBy(c => c.MethodName, StringComparer.Ordinal)
                      .ToList()
                : new List<AtsCapabilityInfo>();

            var listDictGetters = CollectListDictGetters(capabilities);

            EmitConcreteInterface(typeId, interfaceName, capabilities, listDictGetters);
            EmitConcreteStruct(interfaceName, implName, listDictGetters);
            EmitConcreteConstructor(interfaceName, implName);

            foreach (var capability in capabilities)
            {
                EmitCapabilityMethod(typeId, interfaceName, implName, capability, listDictGetters);
            }
        }
    }

    private static List<(string Field, string MethodName, AtsTypeRef ReturnType, string CapabilityId, string? Description)>
        CollectListDictGetters(IReadOnlyList<AtsCapabilityInfo> capabilities)
    {
        var result = new List<(string, string, AtsTypeRef, string, string?)>();
        foreach (var capability in capabilities)
        {
            var targetParamName = capability.TargetParameterName ?? "builder";
            var nonTargetParams = capability.Parameters
                .Where(p => !string.Equals(p.Name, targetParamName, StringComparison.Ordinal))
                .ToList();
            if (nonTargetParams.Count != 0)
            {
                continue;
            }
            if (capability.ReturnType is null)
            {
                continue;
            }
            if (capability.ReturnType.Category != AtsTypeCategory.List
                && capability.ReturnType.Category != AtsTypeCategory.Dict)
            {
                continue;
            }
            if (capability.ReturnType.IsReadOnly)
            {
                continue;
            }
            var methodName = ToPascalCase(capability.MethodName);
            var fieldName = ToCamelCase(methodName);
            result.Add((fieldName, methodName, capability.ReturnType, capability.CapabilityId, capability.Description));
        }
        return result;
    }

    private void EmitConcreteInterface(
        string typeId,
        string interfaceName,
        IReadOnlyList<AtsCapabilityInfo> capabilities,
        List<(string Field, string MethodName, AtsTypeRef ReturnType, string CapabilityId, string? Description)> listDictGetters)
    {
        WriteLine($"// {interfaceName} is the public interface for handle type {interfaceName}.");
        WriteLine($"type {interfaceName} interface {{");
        WriteLine("\thandleReference");

        // Capability methods (skip list/dict getters — they're rendered as
        // distinct accessor methods below).
        var listDictCapIds = listDictGetters.Select(g => g.CapabilityId).ToHashSet(StringComparer.Ordinal);
        foreach (var capability in capabilities)
        {
            if (listDictCapIds.Contains(capability.CapabilityId))
            {
                continue;
            }
            WriteLine($"\t{RenderMethodSignature(typeId, interfaceName, capability)}");
        }

        // List/Dict accessor methods.
        foreach (var (_, methodName, returnType, _, _) in listDictGetters)
        {
            var (wrapperType, typeArgs) = ListDictTypeArgs(returnType);
            WriteLine($"\t{methodName}() *{wrapperType}{typeArgs}");
        }

        // The builder gets a Build() method synthesised by GenerateCreateBuilder;
        // declare it here so the public interface exposes it.
        if (string.Equals(typeId, AtsConstants.BuilderTypeId, StringComparison.Ordinal))
        {
            var appInterface = _interfaceNames.TryGetValue(AtsConstants.ApplicationTypeId, out var appName)
                ? appName
                : "DistributedApplication";
            WriteLine($"\tBuild() ({appInterface}, error)");
        }

        // Lifecycle escape hatches.
        WriteLine("\tErr() error");

        WriteLine("}");
        WriteLine();
    }

    private void EmitConcreteStruct(
        string interfaceName,
        string implName,
        List<(string Field, string MethodName, AtsTypeRef ReturnType, string CapabilityId, string? Description)> listDictGetters)
    {
        WriteLine($"// {implName} is the unexported impl of {interfaceName}.");
        WriteLine($"type {implName} struct {{");
        WriteLine("\t*resourceBuilderBase");
        foreach (var (field, _, returnType, _, _) in listDictGetters)
        {
            var (wrapperType, typeArgs) = ListDictTypeArgs(returnType);
            WriteLine($"\t{field} *{wrapperType}{typeArgs}");
        }
        WriteLine("}");
        WriteLine();
    }

    private void EmitConcreteConstructor(string interfaceName, string implName)
    {
        WriteLine($"// new{interfaceName}FromHandle wraps an existing handle as {interfaceName}.");
        WriteLine($"func new{interfaceName}FromHandle(h *handle, c *client) {interfaceName} {{");
        WriteLine($"\treturn &{implName}{{resourceBuilderBase: newResourceBuilderBase(h, c)}}");
        WriteLine("}");
        WriteLine();
    }

    // ── Capability method emission ────────────────────────────────────────────

    private string RenderMethodSignature(string currentTypeId, string interfaceName, AtsCapabilityInfo capability)
    {
        var methodName = ToPascalCase(capability.MethodName);
        var targetParamName = capability.TargetParameterName ?? "builder";
        var parameters = capability.Parameters
            .Where(p => !string.Equals(p.Name, targetParamName, StringComparison.Ordinal))
            .ToList();

        var (requiredParams, optionalParams) = SplitParameters(parameters);
        var paramSig = RenderParameterList(requiredParams, optionalParams, capability);
        var returnSig = RenderReturnSignature(currentTypeId, interfaceName, capability);
        return $"{methodName}({paramSig}){returnSig}";
    }

    private static (List<AtsParameterInfo> Required, List<AtsParameterInfo> Optional) SplitParameters(
        List<AtsParameterInfo> parameters)
    {
        var required = new List<AtsParameterInfo>();
        var optional = new List<AtsParameterInfo>();
        foreach (var p in parameters)
        {
            // CancellationToken is always treated as optional regardless of
            // metadata — we want app.Run() to be callable without an explicit
            // token. Optional callbacks also live in Options (matches TS).
            if (IsCancellationToken(p) || p.IsOptional)
            {
                optional.Add(p);
            }
            else
            {
                required.Add(p);
            }
        }
        return (required, optional);
    }

    private string RenderParameterList(
        List<AtsParameterInfo> requiredParams,
        List<AtsParameterInfo> optionalParams,
        AtsCapabilityInfo capability)
    {
        var sb = new StringBuilder();
        foreach (var p in requiredParams)
        {
            if (sb.Length > 0)
            {
                sb.Append(", ");
            }
            sb.Append(GetLocalIdentifier(p.Name));
            sb.Append(' ');
            sb.Append(RenderParameterType(p));
        }
        if (optionalParams.Count > 0)
        {
            if (sb.Length > 0)
            {
                sb.Append(", ");
            }
            sb.Append("options ...*");
            sb.Append(GetOptionsTypeName(capability));
        }
        return sb.ToString();
    }

    private string RenderParameterType(AtsParameterInfo p)
    {
        if (p.IsCallback)
        {
            return RenderCallbackType(p);
        }
        if (IsCancellationToken(p))
        {
            return "*CancellationToken";
        }
        return MapTypeRefToGo(p.Type, p.IsOptional);
    }

    /// <summary>
    /// Renders a Go function type from the callback metadata. Falls back to
    /// `func(...any) any` only if the metadata is empty.
    /// </summary>
    private string RenderCallbackType(AtsParameterInfo p)
    {
        var sb = new StringBuilder("func(");
        if (p.CallbackParameters is { Count: > 0 } cps)
        {
            for (var i = 0; i < cps.Count; i++)
            {
                if (i > 0) { sb.Append(", "); }
                sb.Append(GetLocalIdentifier(cps[i].Name));
                sb.Append(' ');
                sb.Append(MapTypeRefToGo(cps[i].Type, false));
            }
        }
        else
        {
            sb.Append("...any");
        }
        sb.Append(')');
        var hasReturn = p.CallbackReturnType is not null
            && p.CallbackReturnType.TypeId != AtsConstants.Void;
        if (hasReturn)
        {
            sb.Append(' ');
            sb.Append(MapTypeRefToGo(p.CallbackReturnType, false));
        }
        else if (p.CallbackParameters is null)
        {
            // Legacy fallback signature used to return any; keep that shape
            // so dynamic-arg callbacks with no metadata still compile.
            sb.Append(" any");
        }
        return sb.ToString();
    }

    private string RenderReturnSignature(string currentTypeId, string interfaceName, AtsCapabilityInfo capability)
    {
        var hasReturn = capability.ReturnType is not null && capability.ReturnType.TypeId != AtsConstants.Void;
        if (!hasReturn)
        {
            // Void capability → returns error (sequential model). Failures are
            // not deferred; the caller checks the returned error.
            return " error";
        }

        // Handle returns yield a typed wrapper. Fluent (return == receiver)
        // returns the receiver interface for chaining; the err lives on the
        // receiver and is read via .Err(). Value returns are synchronous and
        // return (T, error).
        var returnType = capability.ReturnType!;
        if (returnType.Category != AtsTypeCategory.Handle)
        {
            return $" ({MapTypeRefToGo(returnType, false)}, error)";
        }
        if (IsSameType(returnType.TypeId, capability, currentTypeId))
        {
            return $" {interfaceName}";
        }
        return $" {MapTypeRefToGo(returnType, false)}";
    }

    private static bool IsSameType(string? typeId, AtsCapabilityInfo capability, string? currentTypeId = null)
    {
        if (typeId is null)
        {
            return false;
        }
        // Fluent: return type matches the constraint type (e.g. generic `T` resolves to the constraint).
        if (string.Equals(typeId, capability.TargetTypeId, StringComparison.Ordinal))
        {
            return true;
        }
        // Fluent: return type matches the specific concrete type being emitted right now.
        // Do NOT check all ExpandedTargetTypes — that incorrectly flags a method as fluent
        // when its return type happens to be one of the other expanded types (e.g.
        // GetAzureContainerRegistry() returns AzureContainerRegistryResource which is itself
        // an expanded target; testing all expanded types would mark every emit as fluent).
        if (currentTypeId is not null && string.Equals(typeId, currentTypeId, StringComparison.Ordinal))
        {
            return true;
        }
        return false;
    }

    private void EmitCapabilityMethod(
        string typeId,
        string interfaceName,
        string implName,
        AtsCapabilityInfo capability,
        List<(string Field, string MethodName, AtsTypeRef ReturnType, string CapabilityId, string? Description)> listDictGetters)
    {
        // List/Dict property getters get a distinct emission path.
        var listDictMatch = listDictGetters.FirstOrDefault(g => g.CapabilityId == capability.CapabilityId);
        if (listDictMatch.CapabilityId is not null)
        {
            EmitListDictGetter(implName, listDictMatch);
            return;
        }

        // Union-param capabilities are emitted as plain methods with `any`
        // for the union parameters. Go's type-set generics can't express
        // unions containing interfaces-with-methods (which is the shape of
        // most of our handle types), so the design's generic-free-function
        // form would degenerate to `func Foo[T any](...)` — pure ceremony.
        // serializeValue handles dispatch by concrete type at runtime.

        var methodName = ToPascalCase(capability.MethodName);
        var targetParamName = capability.TargetParameterName ?? "builder";
        var parameters = capability.Parameters
            .Where(p => !string.Equals(p.Name, targetParamName, StringComparison.Ordinal))
            .ToList();
        var (requiredParams, optionalParams) = SplitParameters(parameters);

        var paramSig = RenderParameterList(requiredParams, optionalParams, capability);
        var returnSig = RenderReturnSignature(typeId, interfaceName, capability);

        if (!string.IsNullOrEmpty(capability.Description))
        {
            EmitDocComment(methodName, capability.Description);
        }
        EmitUnionAllowedTypesDoc(capability);

        WriteLine($"func (s *{implName}) {methodName}({paramSig}){returnSig} {{");
        EmitCapabilityBody(capability, requiredParams, optionalParams, targetParamName, typeId);

        WriteLine("}");
        WriteLine();
    }

    private void EmitDocComment(string methodName, string description)
    {
        var firstChar = char.ToLowerInvariant(description[0]);
        var rest = description.Length > 1 ? description[1..] : string.Empty;
        WriteLine($"// {methodName} {firstChar}{rest}");
    }

    /// <summary>
    /// Emits a body for a capability that returns a handle. Sequential model:
    /// fluent (return == receiver) → run RPC, store any error on s, return s.
    /// Otherwise → run RPC, return a fresh child wrapper (pre-errored if the
    /// parent already failed or the RPC failed).
    /// </summary>
    private void EmitHandleReturningBody(
        AtsCapabilityInfo capability,
        List<AtsParameterInfo> requiredParams,
        List<AtsParameterInfo> optionalParams,
        string targetParamName,
        string currentTypeId)
    {
        var returnType = capability.ReturnType!;
        var isFluent = IsSameType(returnType.TypeId, capability, currentTypeId);
        var methodName = ToPascalCase(capability.MethodName);

        if (isFluent)
        {
            WriteLine("\tif s.err != nil { return s }");
            EmitHandleParamErrorChecks("\t", capability, "s.setErr(err); return s");
            EmitUnionTypeChecks("\t", capability, methodName, new[] { "s.setErr(err); return s" });
            EmitArgsConstruction("\t", capability, requiredParams, optionalParams, targetParamName, "s.handle");
                WriteLine($"\tif _, err := s.client.invokeCapability(ctx, \"{capability.CapabilityId}\", reqArgs); err != nil {{ s.setErr(err) }}");
            WriteLine("\treturn s");
            return;
        }

        var childImplName = returnType.TypeId is not null && _implNames.TryGetValue(returnType.TypeId, out var implTarget)
            ? implTarget
            : null;

        if (childImplName is null)
        {
            // No registered impl for this typeId (e.g. *ReferenceExpression,
            // which is hand-written in base.go). Cast the wrapIfHandle result
            // directly to the declared return type. Errors land on s; we
            // return nil and the caller must consult s.Err().
            var returnGoType = MapTypeRefToGo(returnType, false);
            WriteLine("\tif s.err != nil { return nil }");
            EmitHandleParamErrorChecks("\t", capability, "s.setErr(err); return nil");
            EmitUnionTypeChecks("\t", capability, methodName, new[] { "s.setErr(err); return nil" });
            EmitArgsConstruction("\t", capability, requiredParams, optionalParams, targetParamName, "s.handle");
                WriteLine($"\tresult, err := s.client.invokeCapability(ctx, \"{capability.CapabilityId}\", reqArgs)");
            WriteLine("\tif err != nil { s.setErr(err); return nil }");
            WriteLine($"\ttyped, ok := result.({returnGoType})");
            WriteLine("\tif !ok {");
            WriteLine($"\t\ts.setErr(fmt.Errorf(\"aspire: {capability.CapabilityId} returned unexpected type %T\", result))");
            WriteLine("\t\treturn nil");
            WriteLine("\t}");
            WriteLine("\treturn typed");
            return;
        }

        // Pre-errored child path: parent failure short-circuits this call.
        WriteLine($"\tif s.err != nil {{ return &{childImplName}{{resourceBuilderBase: newErroredResourceBuilder(s.err, s.client)}} }}");
        EmitHandleParamErrorChecks("\t", capability,
            $"return &{childImplName}{{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}}");
        EmitUnionTypeChecks("\t", capability, methodName, new[]
        {
            $"return &{childImplName}{{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}}",
        });
        EmitArgsConstruction("\t", capability, requiredParams, optionalParams, targetParamName, "s.handle");
        WriteLine($"\tresult, err := s.client.invokeCapability(ctx, \"{capability.CapabilityId}\", reqArgs)");
        WriteLine("\tif err != nil {");
        WriteLine($"\t\treturn &{childImplName}{{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}}");
        WriteLine("\t}");
        WriteLine("\thref, ok := result.(handleReference)");
        WriteLine("\tif !ok {");
        WriteLine($"\t\terr := fmt.Errorf(\"aspire: {capability.CapabilityId} returned unexpected type %T\", result)");
        WriteLine($"\t\treturn &{childImplName}{{resourceBuilderBase: newErroredResourceBuilder(err, s.client)}}");
        WriteLine("\t}");
        WriteLine($"\treturn &{childImplName}{{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), s.client)}}");
    }

    /// <summary>
    /// Emits a body for a capability that returns a non-handle value. Sync RPC
    /// returning (T, error). A pending error on the receiver short-circuits.
    /// </summary>
    private void EmitValueReturningBody(
        AtsCapabilityInfo capability,
        List<AtsParameterInfo> requiredParams,
        List<AtsParameterInfo> optionalParams,
        string targetParamName)
    {
        var returnType = capability.ReturnType!;
        var returnGoType = MapTypeRefToGo(returnType, false);
        var methodName = ToPascalCase(capability.MethodName);

        WriteLine($"\tif s.err != nil {{ var zero {returnGoType}; return zero, s.err }}");
        EmitHandleParamErrorChecks("\t", capability, $"var zero {returnGoType}; return zero, err");
        EmitUnionTypeChecks("\t", capability, methodName, new[] { $"var zero {returnGoType}", "return zero, err" });
        EmitArgsConstruction("\t", capability, requiredParams, optionalParams, targetParamName, "s.handle");
        WriteLine($"\tresult, err := s.client.invokeCapability(ctx, \"{capability.CapabilityId}\", reqArgs)");
        WriteLine("\tif err != nil {");
        WriteLine($"\t\tvar zero {returnGoType}");
        WriteLine("\t\treturn zero, err");
        WriteLine("\t}");
        WriteLine($"\treturn decodeAs[{returnGoType}](result)");
    }

    /// <summary>
    /// Emits a body for a void capability: synchronous RPC returning error.
    /// </summary>
    private void EmitVoidBody(
        AtsCapabilityInfo capability,
        List<AtsParameterInfo> requiredParams,
        List<AtsParameterInfo> optionalParams,
        string targetParamName)
    {
        var methodName = ToPascalCase(capability.MethodName);
        WriteLine("\tif s.err != nil { return s.err }");
        EmitHandleParamErrorChecks("\t", capability, "return err");
        EmitUnionTypeChecks("\t", capability, methodName, new[] { "return err" });
        EmitArgsConstruction("\t", capability, requiredParams, optionalParams, targetParamName, "s.handle");
        WriteLine($"\t_, err := s.client.invokeCapability(ctx, \"{capability.CapabilityId}\", reqArgs)");
        WriteLine("\treturn err");
    }

    /// <summary>
    /// Emits short-circuit checks for handle-typed REQUIRED parameters: if
    /// any of them already carry an error, propagate it via the supplied
    /// errorAction (e.g. "s.setErr(err); return s" or "return err"). Optional
    /// handle parameters live inside the Options struct and are not in scope
    /// at this point.
    /// </summary>
    private void EmitHandleParamErrorChecks(string indent, AtsCapabilityInfo capability, string errorAction)
    {
        var targetParamName = capability.TargetParameterName ?? "builder";
        foreach (var p in capability.Parameters)
        {
            if (string.Equals(p.Name, targetParamName, StringComparison.Ordinal)) { continue; }
            if (p.IsOptional) { continue; } // optional → in Options struct, not a local
            if (p.IsCallback) { continue; }
            if (IsCancellationToken(p)) { continue; }
            if (p.Type?.Category != AtsTypeCategory.Handle) { continue; }
            var paramName = GetLocalIdentifier(p.Name);
            WriteLine($"{indent}if {paramName} != nil {{ if err := {paramName}.Err(); err != nil {{ {errorAction} }} }}");
        }
    }

    private void EmitArgsConstruction(
        string indent,
        AtsCapabilityInfo capability,
        List<AtsParameterInfo> requiredParams,
        List<AtsParameterInfo> optionalParams,
        string targetParamName,
        string targetHandleVar)
    {
        // ctx defaults to Background. If the caller provided a CancellationToken
        // via Options, we override ctx inside the options block below so the
        // local Go RPC respects the user's cancellation.
        WriteLine($"{indent}ctx := context.Background()");
        WriteLine($"{indent}reqArgs := map[string]any{{");
        WriteLine($"{indent}\t\"{targetParamName}\": {targetHandleVar}.ToJSON(),");
        WriteLine($"{indent}}}");

        foreach (var p in requiredParams)
        {
            var paramName = GetLocalIdentifier(p.Name);
            if (p.IsCallback)
            {
                EmitCallbackRegistration(indent, p, paramName);
                continue;
            }
            if (IsCancellationToken(p))
            {
                // Only register/send the cancellation token when the caller
                // explicitly provides one — matches TypeScript SDK behavior.
                // We deliberately do NOT substitute a default token here:
                // tying every server-side op to a wrapper-internal lifetime
                // would create unwanted cross-cancellation and leak token
                // state on the server for ops the user didn't ask to cancel.
                WriteLine($"{indent}if {paramName} != nil {{");
                WriteLine($"{indent}\tif id := s.client.registerCancellation({paramName}); id != \"\" {{");
                WriteLine($"{indent}\t\treqArgs[\"{p.Name}\"] = id");
                WriteLine($"{indent}\t}}");
                WriteLine($"{indent}}}");
                continue;
            }
            var typeStr = MapTypeRefToGo(p.Type, p.IsOptional);
            if (IsNilableGoType(typeStr))
            {
                WriteLine($"{indent}if {paramName} != nil {{ reqArgs[\"{p.Name}\"] = serializeValue({paramName}) }}");
            }
            else
            {
                WriteLine($"{indent}reqArgs[\"{p.Name}\"] = serializeValue({paramName})");
            }
        }

        if (optionalParams.Count > 0)
        {
            var optionsType = GetOptionsTypeName(capability);
            WriteLine($"{indent}if len(options) > 0 {{");
            WriteLine($"{indent}\tmerged := &{optionsType}{{}}");
            WriteLine($"{indent}\tfor _, opt := range options {{");
            WriteLine($"{indent}\t\tif opt != nil {{ merged = deepUpdate(merged, opt) }}");
            WriteLine($"{indent}\t}}");
            WriteLine($"{indent}\tfor k, v := range merged.ToMap() {{ reqArgs[k] = v }}");
            // Callbacks: wrap the typed user function in a positional `any`
            // shim that the transport's invokeCallback can call.
            foreach (var p in optionalParams.Where(o => o.IsCallback))
            {
                var fieldName = ToPascalCase(p.Name);
                EmitCallbackRegistration(indent + "\t", p, $"merged.{fieldName}");
            }
            // Cancellation tokens: register, inject the id, and use the
            // token's context for local Go-side cancellation.
            foreach (var p in optionalParams.Where(IsCancellationToken))
            {
                var fieldName = ToPascalCase(p.Name);
                WriteLine($"{indent}\tif merged.{fieldName} != nil {{");
                WriteLine($"{indent}\t\tctx = merged.{fieldName}.Context()");
                WriteLine($"{indent}\t\tif id := s.client.registerCancellation(merged.{fieldName}); id != \"\" {{");
                WriteLine($"{indent}\t\t\treqArgs[\"{p.Name}\"] = id");
                WriteLine($"{indent}\t\t}}");
                WriteLine($"{indent}\t}}");
            }
            WriteLine($"{indent}}}");
        }
    }

    /// <summary>
    /// Emits a callback-registration block for the supplied callback variable.
    /// Generates a typed→positional shim that decodes positional args back to
    /// the user-supplied callback's parameter types.
    /// </summary>
    private void EmitCallbackRegistration(string indent, AtsParameterInfo p, string callbackExpr)
    {
        var hasReturn = p.CallbackReturnType is not null
            && p.CallbackReturnType.TypeId != AtsConstants.Void;
        var callExpr = new StringBuilder();
        callExpr.Append("cb(");
        if (p.CallbackParameters is { Count: > 0 } cps)
        {
            for (var i = 0; i < cps.Count; i++)
            {
                if (i > 0) { callExpr.Append(", "); }
                var goType = MapTypeRefToGo(cps[i].Type, false);
                callExpr.Append(CultureInfo.InvariantCulture, $"callbackArg[{goType}](args, {i})");
            }
        }
        else
        {
            callExpr.Append("args...");
        }
        callExpr.Append(')');

        WriteLine($"{indent}if {callbackExpr} != nil {{");
        WriteLine($"{indent}\tcb := {callbackExpr}");
        WriteLine($"{indent}\tshim := func(args ...any) any {{");
        if (hasReturn)
        {
            WriteLine($"{indent}\t\treturn {callExpr}");
        }
        else if (p.CallbackParameters is null)
        {
            // Legacy untyped callback returning any — preserve return value.
            WriteLine($"{indent}\t\treturn {callExpr}");
        }
        else if (p.CallbackParameters is { Count: > 0 } callbackParameters && callbackParameters.Any(cp => cp.Type.Category == AtsTypeCategory.Dto))
        {
            var argNames = new List<string>(callbackParameters.Count);
            for (var i = 0; i < callbackParameters.Count; i++)
            {
                var argName = $"arg{i}";
                argNames.Add(argName);
                var goType = MapTypeRefToGo(callbackParameters[i].Type, false);
                WriteLine($"{indent}\t\t{argName} := callbackArg[{goType}](args, {i})");
            }

            WriteLine($"{indent}\t\tcb({string.Join(", ", argNames)})");
            WriteLine($"{indent}\t\treturn map[string]any{{");
            for (var i = 0; i < callbackParameters.Count; i++)
            {
                if (callbackParameters[i].Type.Category == AtsTypeCategory.Dto)
                {
                    WriteLine($"{indent}\t\t\t\"p{i}\": serializeValue({argNames[i]}),");
                }
            }
            WriteLine($"{indent}\t\t}}");
        }
        else
        {
            WriteLine($"{indent}\t\t{callExpr}");
            WriteLine($"{indent}\t\treturn nil");
        }
        WriteLine($"{indent}\t}}");
        WriteLine($"{indent}\treqArgs[\"{p.Name}\"] = s.client.registerCallback(shim)");
        WriteLine($"{indent}}}");
    }

    // ── List / Dict accessor methods ─────────────────────────────────────────

    private void EmitListDictGetter(
        string implName,
        (string Field, string MethodName, AtsTypeRef ReturnType, string CapabilityId, string? Description) g)
    {
        var (wrapperType, typeArgs) = ListDictTypeArgs(g.ReturnType);
        var factory = wrapperType == "Dict" ? "newDictWithGetter" : "newListWithGetter";

        if (!string.IsNullOrEmpty(g.Description))
        {
            EmitDocComment(g.MethodName, g.Description!);
        }
        WriteLine($"func (s *{implName}) {g.MethodName}() *{wrapperType}{typeArgs} {{");
        WriteLine($"\tif s.{g.Field} == nil {{");
        WriteLine($"\t\ts.{g.Field} = {factory}{typeArgs}(s.handleWrapperBase, \"{g.CapabilityId}\")");
        WriteLine("\t}");
        WriteLine($"\treturn s.{g.Field}");
        WriteLine("}");
        WriteLine();
    }

    private (string Wrapper, string TypeArgs) ListDictTypeArgs(AtsTypeRef returnType)
    {
        if (returnType.Category == AtsTypeCategory.Dict)
        {
            var keyType = MapTypeRefToGo(returnType.KeyType, false);
            var valueType = MapTypeRefToGo(returnType.ValueType, false);
            return ("Dict", $"[{keyType}, {valueType}]");
        }
        var elementType = MapTypeRefToGo(returnType.ElementType, false);
        return ("List", $"[{elementType}]");
    }

    // ── Options structs ──────────────────────────────────────────────────────

    private void GenerateOptionsStructs(IReadOnlyList<AtsCapabilityInfo> capabilities)
    {
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var seenForCapability = new Dictionary<string, string>(StringComparer.Ordinal);
        var anyEmitted = false;

        foreach (var capability in capabilities)
        {
            var targetParamName = capability.TargetParameterName ?? "builder";
            var optionalParams = capability.Parameters
                .Where(p => !string.Equals(p.Name, targetParamName, StringComparison.Ordinal))
                .Where(p => IsCancellationToken(p) || p.IsOptional)
                .ToList();
            if (optionalParams.Count == 0)
            {
                continue;
            }
            var name = GetOptionsTypeName(capability);
            seenForCapability[capability.CapabilityId] = name;
            if (!emitted.Add(name))
            {
                continue;
            }

            if (!anyEmitted)
            {
                WriteLine("// ============================================================================");
                WriteLine("// Options structs");
                WriteLine("// ============================================================================");
                WriteLine();
                anyEmitted = true;
            }

            WriteLine($"// {name} carries optional parameters for {ToPascalCase(capability.MethodName)}.");
            WriteLine($"type {name} struct {{");
            foreach (var p in optionalParams)
            {
                var fieldName = ToPascalCase(p.Name);
                var fieldType = RenderOptionsFieldType(p);
                // Callback fields and cancellation tokens are not JSON-marshaled
                // — they're handled via registerCallback / registerCancellation
                // in the body. Marking with json:"-" keeps them out of any
                // accidental Marshal calls.
                var jsonTag = (p.IsCallback || IsCancellationToken(p))
                    ? "`json:\"-\"`"
                    : $"`json:\"{p.Name},omitempty\"`";
                WriteLine($"\t{fieldName} {fieldType} {jsonTag}");
            }
            WriteLine("}");
            WriteLine();

            WriteLine($"func (o *{name}) ToMap() map[string]any {{");
            WriteLine("\tm := map[string]any{}");
            WriteLine("\tif o == nil { return m }");
            foreach (var p in optionalParams)
            {
                // Callbacks and CTs are registered separately by the calling
                // body — they cannot be plain-serialized.
                if (p.IsCallback || IsCancellationToken(p))
                {
                    continue;
                }
                var fieldName = ToPascalCase(p.Name);
                var fieldType = MapTypeRefToGo(p.Type, isOptional: true);
                if (IsNilableGoType(fieldType))
                {
                    WriteLine($"\tif o.{fieldName} != nil {{ m[\"{p.Name}\"] = serializeValue(o.{fieldName}) }}");
                }
                else
                {
                    WriteLine($"\tm[\"{p.Name}\"] = serializeValue(o.{fieldName})");
                }
            }
            WriteLine("\treturn m");
            WriteLine("}");
            WriteLine();
        }
    }

    /// <summary>
    /// Field type for an Options struct member. Callbacks render as their
    /// typed Go function signature; CT renders as *CancellationToken; other
    /// values use the standard MapTypeRefToGo with optional/nilable wrapping.
    /// </summary>
    private string RenderOptionsFieldType(AtsParameterInfo p)
    {
        if (p.IsCallback)
        {
            return RenderCallbackType(p);
        }
        if (IsCancellationToken(p))
        {
            return "*CancellationToken";
        }
        return MapTypeRefToGo(p.Type, isOptional: true);
    }

    private string GetOptionsTypeName(AtsCapabilityInfo capability)
    {
        return _optionsTypeNames.TryGetValue(capability.CapabilityId, out var name)
            ? name
            : $"{ToPascalCase(capability.MethodName)}Options";
    }

    private string GetOptionsQualifiedName(AtsCapabilityInfo capability, string simpleName)
    {
        var targetInterfaceName = capability.TargetTypeId is not null
            && _interfaceNames.TryGetValue(capability.TargetTypeId, out var ifName)
            ? ifName
            : capability.TargetTypeId ?? "Unknown";
        return $"{targetInterfaceName}{simpleName}";
    }

    /// <summary>
    /// Returns true when two capabilities have the same optional-param field
    /// signatures and can share a single Options struct. Compares params by
    /// name and rendered Go type so callbacks with different argument types
    /// (e.g. AzureStorageEmulatorResource vs AzureEventHubsEmulatorResource)
    /// are correctly detected as incompatible.
    /// </summary>
    private bool AreOptionsCompatible(AtsCapabilityInfo a, AtsCapabilityInfo b)
    {
        var aTarget = a.TargetParameterName ?? "builder";
        var bTarget = b.TargetParameterName ?? "builder";

        var aOpts = a.Parameters
            .Where(p => !string.Equals(p.Name, aTarget, StringComparison.Ordinal)
                        && (IsCancellationToken(p) || p.IsOptional))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();
        var bOpts = b.Parameters
            .Where(p => !string.Equals(p.Name, bTarget, StringComparison.Ordinal)
                        && (IsCancellationToken(p) || p.IsOptional))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

        if (aOpts.Count != bOpts.Count)
        {
            return false;
        }

        for (var i = 0; i < aOpts.Count; i++)
        {
            if (!string.Equals(aOpts[i].Name, bOpts[i].Name, StringComparison.Ordinal))
            {
                return false;
            }
            if (!string.Equals(RenderOptionsFieldType(aOpts[i]), RenderOptionsFieldType(bOpts[i]), StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    // ── Union parameter handling ─────────────────────────────────────────────
    //
    // Go's type-set generics cannot express unions containing
    // interfaces-with-methods, which is the shape of nearly every metadata
    // union (handle types are always interfaces-with-methods). The
    // design-spec generic-free-function form would degenerate to
    // `func Foo[T any](...)` — pure ceremony with no compile-time benefit.
    //
    // Instead union parameters are emitted as plain `any` and the generated
    // body type-checks them at runtime against the documented allowed-types
    // list. Wrong types fail fast with a clear error rather than silently
    // serialising garbage. The allowed types are also enumerated in the
    // method's doc comment so editors / godoc surface them.

    private static List<AtsParameterInfo> GetUnionParameters(AtsCapabilityInfo capability)
    {
        // Required positional union parameters only — optional unions live in
        // the Options struct as `any` fields and aren't bound to a local var.
        var targetParamName = capability.TargetParameterName ?? "builder";
        var result = new List<AtsParameterInfo>();
        foreach (var p in capability.Parameters)
        {
            if (string.Equals(p.Name, targetParamName, StringComparison.Ordinal)) { continue; }
            if (p.IsOptional) { continue; }
            if (p.IsCallback) { continue; }
            if (IsCancellationToken(p)) { continue; }
            if (p.Type?.Category == AtsTypeCategory.Union)
            {
                result.Add(p);
            }
        }
        return result;
    }

    /// <summary>
    /// Emits a runtime type guard for each union parameter. On mismatch the
    /// emitted code creates a descriptive error and runs the supplied
    /// errorAction lines (e.g. propagating to a child wrapper) before
    /// returning. Caller is responsible for choosing the right indentation
    /// and the right error action for the surrounding shape (sync vs goroutine).
    /// </summary>
    private void EmitUnionTypeChecks(
        string indent,
        AtsCapabilityInfo capability,
        string methodName,
        IReadOnlyList<string> errorActionLines)
    {
        foreach (var p in GetUnionParameters(capability))
        {
            var paramName = GetLocalIdentifier(p.Name);
            var allowed = GetUnionAllowedTypes(p.Type!);
            WriteLine($"{indent}switch {paramName}.(type) {{");
            WriteLine($"{indent}case {allowed}:");
            WriteLine($"{indent}default:");
            WriteLine($"{indent}\terr := fmt.Errorf(\"aspire: {methodName}: parameter %q must be one of [{allowed}], got %T\", \"{p.Name}\", {paramName})");
            foreach (var line in errorActionLines)
            {
                WriteLine($"{indent}\t{line}");
            }
            WriteLine($"{indent}}}");
        }
    }

    private void EmitUnionAllowedTypesDoc(AtsCapabilityInfo capability)
    {
        foreach (var p in GetUnionParameters(capability))
        {
            var allowed = GetUnionAllowedTypes(p.Type!);
            WriteLine($"// Allowed types for parameter {p.Name}: {allowed}.");
        }
    }

    private string GetUnionAllowedTypes(AtsTypeRef typeRef)
    {
        if (typeRef.UnionTypes is not { Count: > 0 } types)
        {
            return "any";
        }

        var allowedTypes = types
            .Select(type => MapTypeRefToGo(type, isOptional: false))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return allowedTypes.Length > 0 ? string.Join(", ", allowedTypes) : "any";
    }

    /// <summary>
    /// Routes to the appropriate body emission based on return shape.
    /// </summary>
    private void EmitCapabilityBody(
        AtsCapabilityInfo capability,
        List<AtsParameterInfo> requiredParams,
        List<AtsParameterInfo> optionalParams,
        string targetParamName,
        string currentTypeId)
    {
        var hasReturn = capability.ReturnType is not null && capability.ReturnType.TypeId != AtsConstants.Void;
        if (hasReturn && capability.ReturnType!.Category == AtsTypeCategory.Handle)
        {
            EmitHandleReturningBody(capability, requiredParams, optionalParams, targetParamName, currentTypeId);
        }
        else if (hasReturn)
        {
            EmitValueReturningBody(capability, requiredParams, optionalParams, targetParamName);
        }
        else
        {
            EmitVoidBody(capability, requiredParams, optionalParams, targetParamName);
        }
    }

    // ── Per-client wrapper registration ──────────────────────────────────────

    private void GenerateRegisterWrappers()
    {
        WriteLine("// ============================================================================");
        WriteLine("// Per-client handle wrapper registration");
        WriteLine("// ============================================================================");
        WriteLine();
        WriteLine("func registerWrappers(c *client) {");
        WriteLine("\tc.registerHandleWrapper(\"" + AtsConstants.ReferenceExpressionTypeId + "\", func(h *handle, c *client) any {");
        WriteLine("\t\treturn newHandleBackedReferenceExpression(h, c)");
        WriteLine("\t})");
        foreach (var (typeId, implName) in _implNames.OrderBy(kv => kv.Value, StringComparer.Ordinal))
        {
            var interfaceName = _interfaceNames[typeId];
            WriteLine($"\tc.registerHandleWrapper(\"{typeId}\", func(h *handle, c *client) any {{");
            WriteLine($"\t\treturn new{interfaceName}FromHandle(h, c)");
            WriteLine("\t})");
        }
        WriteLine("}");
        WriteLine();
    }

    // ── CreateBuilder / Build ────────────────────────────────────────────────

    private void GenerateCreateBuilder(AtsContext context)
    {
        var builderTypeId = AtsConstants.BuilderTypeId;
        var hasBuilderImpl = _implNames.TryGetValue(builderTypeId, out var existingBuilderImpl);
        var builderInterface = _interfaceNames.TryGetValue(builderTypeId, out var existingBuilderIface)
            ? existingBuilderIface
            : "DistributedApplicationBuilder";
        var builderImpl = hasBuilderImpl ? existingBuilderImpl! : ToCamelCase(builderInterface);

        var appTypeId = AtsConstants.ApplicationTypeId;
        var hasAppInterface = _interfaceNames.ContainsKey(appTypeId);
        var appInterface = hasAppInterface ? _interfaceNames[appTypeId] : "DistributedApplication";
        var hasAppImpl = _implNames.ContainsKey(appTypeId);

        WriteLine("// ============================================================================");
        WriteLine("// Builder construction & Build()");
        WriteLine("// ============================================================================");
        WriteLine();

        if (!hasBuilderImpl)
        {
            // Synthesize a private impl struct. The interface (and any
            // capability methods on it) was emitted by GenerateConcreteHandleTypes
            // already if hasBuilderImpl was true; otherwise we synthesize a
            // minimal interface here.
            if (!_interfaceNames.ContainsKey(builderTypeId))
            {
                WriteLine($"// {builderInterface} is the entry point to the Aspire SDK.");
                WriteLine($"type {builderInterface} interface {{");
                WriteLine("\thandleReference");
                WriteLine($"\tBuild() ({appInterface}, error)");
                WriteLine("}");
                WriteLine();
            }
            WriteLine($"type {builderImpl} struct {{ *resourceBuilderBase }}");
            WriteLine();
        }

        if (!hasAppInterface)
        {
            WriteLine($"// {appInterface} is returned by Build(); it represents the running application.");
            WriteLine($"type {appInterface} interface {{ handleReference }}");
            WriteLine();
        }
        if (!hasAppImpl)
        {
            // Synthesize an impl so wrapIfHandle can return a value satisfying
            // the interface even when the metadata doesn't expose the application
            // as a concrete handle type.
            WriteLine($"type {ToCamelCase(appInterface)} struct {{ *resourceBuilderBase }}");
            WriteLine();
        }

        // Build() method on the concrete impl. Sequential model: any prior
        // chain error short-circuits; otherwise invoke the build capability
        // and return the wrapped result.
        WriteLine($"// Build invokes the build capability and returns the running application.");
        WriteLine($"func (b *{builderImpl}) Build() ({appInterface}, error) {{");
        WriteLine("\tif b.err != nil { return nil, b.err }");
        WriteLine($"\tresult, err := b.client.invokeCapability(context.Background(), \"{AtsConstants.BuildCapability}\", map[string]any{{");
        WriteLine("\t\t\"context\": b.handle.ToJSON(),");
        WriteLine("\t})");
        WriteLine("\tif err != nil { return nil, err }");
        WriteLine($"\tapp, ok := result.({appInterface})");
        WriteLine($"\tif !ok {{ return nil, fmt.Errorf(\"aspire: build returned unexpected type %T\", result) }}");
        WriteLine("\treturn app, nil");
        WriteLine("}");
        WriteLine();

        // CreateBuilder factory.
        var createOptionsType = GetCreateBuilderOptionsType(context);
        WriteLine($"// CreateBuilder establishes a connection to the AppHost and returns a new builder.");
        if (createOptionsType is not null)
        {
            WriteLine($"func CreateBuilder(options ...*{createOptionsType}) ({builderInterface}, error) {{");
        }
        else
        {
            WriteLine($"func CreateBuilder() ({builderInterface}, error) {{");
        }
        WriteLine("\tsocketPath := os.Getenv(\"REMOTE_APP_HOST_SOCKET_PATH\")");
        WriteLine("\tif socketPath == \"\" {");
        WriteLine("\t\treturn nil, fmt.Errorf(\"REMOTE_APP_HOST_SOCKET_PATH environment variable not set. Run this application using `aspire run`\")");
        WriteLine("\t}");
        WriteLine("\tc := newClient(socketPath)");
        WriteLine("\tif err := c.connect(context.Background(), 5*time.Second); err != nil { return nil, err }");
        WriteLine("\tc.onDisconnect(func() { os.Exit(1) })");
        WriteLine("\tregisterWrappers(c)");
        WriteLine();
        WriteLine("\tresolved := map[string]any{}");
        if (createOptionsType is not null)
        {
            WriteLine("\tif len(options) > 0 {");
            WriteLine($"\t\tmerged := &{createOptionsType}{{}}");
            WriteLine("\t\tfor _, opt := range options {");
            WriteLine("\t\t\tif opt != nil { merged = deepUpdate(merged, opt) }");
            WriteLine("\t\t}");
            WriteLine("\t\tfor k, v := range merged.ToMap() { resolved[k] = v }");
            WriteLine("\t}");
        }
        WriteLine("\tif _, ok := resolved[\"Args\"]; !ok { resolved[\"Args\"] = os.Args[1:] }");
        WriteLine("\tif projectDirectory, ok := resolved[\"ProjectDirectory\"].(string); !ok || projectDirectory == \"\" {");
        WriteLine("\t\tif pwd, err := os.Getwd(); err == nil { resolved[\"ProjectDirectory\"] = pwd }");
        WriteLine("\t}");
        WriteLine("\tif appHostFilePath, ok := resolved[\"AppHostFilePath\"].(string); !ok || appHostFilePath == \"\" {");
        WriteLine("\t\tif appHostFilePath := os.Getenv(\"ASPIRE_APPHOST_FILEPATH\"); appHostFilePath != \"\" { resolved[\"AppHostFilePath\"] = appHostFilePath }");
        WriteLine("\t}");
        WriteLine("\tif dashboardApplicationName, ok := resolved[\"DashboardApplicationName\"].(string); ok && dashboardApplicationName == \"\" {");
        WriteLine("\t\tdelete(resolved, \"DashboardApplicationName\")");
        WriteLine("\t}");
        WriteLine();
        WriteLine($"\tresult, err := c.invokeCapability(context.Background(), \"{AtsConstants.CreateBuilderCapability}\", map[string]any{{\"argsOrOptions\": resolved}})");
        WriteLine("\tif err != nil { return nil, err }");
        WriteLine("\thref, ok := result.(handleReference)");
        WriteLine("\tif !ok { return nil, fmt.Errorf(\"aspire: createBuilder returned unexpected type %T\", result) }");
        WriteLine($"\treturn &{builderImpl}{{resourceBuilderBase: newResourceBuilderBase(href.getHandle(), c)}}, nil");
        WriteLine("}");
        WriteLine();
    }

    private string? GetCreateBuilderOptionsType(AtsContext context)
    {
        // The TypeScript generator uses a DTO named "CreateBuilderOptions" for
        // the createBuilder parameter; mirror that.
        foreach (var dto in context.DtoTypes)
        {
            if (dto.Name.Equals("CreateBuilderOptions", StringComparison.Ordinal)
                && _dtoNames.TryGetValue(dto.TypeId, out var goName))
            {
                return goName;
            }
        }
        return null;
    }

    // ── Type mapping ─────────────────────────────────────────────────────────

    private string MapTypeRefToGo(AtsTypeRef? typeRef, bool isOptional)
    {
        if (typeRef is null)
        {
            return "any";
        }

        if (typeRef.TypeId == AtsConstants.ReferenceExpressionTypeId)
        {
            return "*ReferenceExpression";
        }

        var baseType = typeRef.Category switch
        {
            AtsTypeCategory.Primitive => MapPrimitiveType(typeRef.TypeId),
            AtsTypeCategory.Enum => MapEnumType(typeRef.TypeId),
            AtsTypeCategory.Handle => MapHandleType(typeRef.TypeId),
            AtsTypeCategory.Dto => "*" + MapDtoType(typeRef.TypeId),
            AtsTypeCategory.Callback => "func(...any) any",
            AtsTypeCategory.Array => $"[]{MapTypeRefToGo(typeRef.ElementType, false)}",
            AtsTypeCategory.List => typeRef.IsReadOnly
                ? $"[]{MapTypeRefToGo(typeRef.ElementType, false)}"
                : $"*List[{MapTypeRefToGo(typeRef.ElementType, false)}]",
            AtsTypeCategory.Dict => typeRef.IsReadOnly
                ? $"map[{MapTypeRefToGo(typeRef.KeyType, false)}]{MapTypeRefToGo(typeRef.ValueType, false)}"
                : $"*Dict[{MapTypeRefToGo(typeRef.KeyType, false)}, {MapTypeRefToGo(typeRef.ValueType, false)}]",
            AtsTypeCategory.Union => "any",
            AtsTypeCategory.Unknown => "any",
            _ => "any"
        };

        if (isOptional && !IsNilableGoType(baseType))
        {
            return $"*{baseType}";
        }
        return baseType;
    }

    private static bool IsNilableGoType(string typeName) =>
        typeName.StartsWith("*", StringComparison.Ordinal) ||
        typeName.StartsWith("[]", StringComparison.Ordinal) ||
        typeName.StartsWith("map[", StringComparison.Ordinal) ||
        typeName == "any" ||
        typeName.StartsWith("func(", StringComparison.Ordinal);

    /// <summary>
    /// Handle types map to their Go interface name. Interfaces in Go are
    /// already reference types — no leading * required.
    /// </summary>
    private string MapHandleType(string typeId)
    {
        if (_interfaceNames.TryGetValue(typeId, out var name))
        {
            return name;
        }
        return "handleReference";
    }

    private string MapDtoType(string typeId) =>
        _dtoNames.TryGetValue(typeId, out var name) ? name : "map[string]any";

    private string MapEnumType(string typeId) =>
        _enumNames.TryGetValue(typeId, out var name) ? name : "string";

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
    /// Capabilities the generator emits with hand-written bodies. They must be
    /// skipped when iterating metadata-driven capability emission so the body
    /// is not duplicated.
    /// </summary>
    private static bool IsSpecialCasedCapability(string capabilityId) =>
        string.Equals(capabilityId, AtsConstants.BuildCapability, StringComparison.Ordinal)
        || string.Equals(capabilityId, AtsConstants.CreateBuilderCapability, StringComparison.Ordinal);

    private static bool IsCancellationToken(AtsParameterInfo parameter) =>
        IsCancellationTokenTypeId(parameter.Type?.TypeId);

    private static bool IsCancellationTokenTypeId(string? typeId) =>
        string.Equals(typeId, AtsConstants.CancellationToken, StringComparison.Ordinal)
        || (typeId?.EndsWith("/System.Threading.CancellationToken", StringComparison.Ordinal) ?? false);

    // ── Identifier helpers ────────────────────────────────────────────────────

    private static string ExtractTypeName(string typeId)
    {
        var slashIndex = typeId.IndexOf('/', StringComparison.Ordinal);
        var typeName = slashIndex >= 0 ? typeId[(slashIndex + 1)..] : typeId;
        var lastDot = typeName.LastIndexOf('.');
        var plusIndex = typeName.LastIndexOf('+');
        var delimiterIndex = Math.Max(lastDot, plusIndex);
        return delimiterIndex >= 0 ? typeName[(delimiterIndex + 1)..] : typeName;
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
        return s_goKeywords.Contains(sanitized) ? sanitized + "_" : sanitized;
    }

    private static string GetLocalIdentifier(string name) => SanitizeIdentifier(ToCamelCase(name));

    private static string ToPascalCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }
        if (char.IsUpper(name[0]))
        {
            return name;
        }
        return char.ToUpperInvariant(name[0]) + name[1..];
    }

    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }
        if (char.IsLower(name[0]))
        {
            return name;
        }
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private void WriteLine(string value = "")
    {
        _writer.WriteLine(value);
    }
}
