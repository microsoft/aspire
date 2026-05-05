// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Aspire.Shared.Json;
using Aspire.TypeSystem;

namespace Aspire.Hosting.CodeGeneration.Go;

internal sealed class GoExportedValueTreeNode
{
    public Dictionary<string, GoExportedValueTreeNode> Children { get; } = new(StringComparer.Ordinal);

    public AtsExportedValueInfo? Value { get; set; }
}

/// <summary>
/// Generates a Go SDK using the ATS (Aspire Type System) capability-based API.
/// Produces wrapper structs that proxy capabilities via JSON-RPC.
/// </summary>
internal sealed class AtsGoCodeGenerator : ICodeGenerator
{
    private static readonly HashSet<string> s_goKeywords = new(StringComparer.Ordinal)
    {
        "break", "case", "chan", "const", "continue", "default", "defer", "else",
        "fallthrough", "for", "func", "go", "goto", "if", "import", "interface",
        "map", "package", "range", "return", "select", "struct", "switch", "type", "var"
    };

    private TextWriter _writer = null!;
    private readonly Dictionary<string, string> _structNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _dtoNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _enumNames = new(StringComparer.Ordinal);

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

        var capabilities = context.Capabilities;
        var dtoTypes = context.DtoTypes;
        var enumTypes = context.EnumTypes;
        var exportedValues = context.ExportedValues;

        _enumNames.Clear();
        foreach (var enumType in enumTypes)
        {
            _enumNames[enumType.TypeId] = SanitizeIdentifier(enumType.Name);
        }

        _dtoNames.Clear();
        foreach (var dto in dtoTypes)
        {
            _dtoNames[dto.TypeId] = SanitizeIdentifier(dto.Name);
        }

        var handleTypes = BuildHandleTypes(context);
        var capabilitiesByTarget = GroupCapabilitiesByTarget(capabilities);
        var collectionTypes = CollectListAndDictTypeIds(capabilities);

        WriteHeader();
        GenerateEnumTypes(enumTypes);
        GenerateDtoTypes(dtoTypes);
        GenerateExportedValues(exportedValues, dtoTypes.ToDictionary(dto => dto.TypeId, StringComparer.Ordinal));
        GenerateHandleTypes(handleTypes, capabilitiesByTarget);
        GenerateHandleWrapperRegistrations(handleTypes, collectionTypes);
        GenerateConnectionHelpers();

        return stringWriter.ToString();
    }

    private void WriteHeader()
    {
        WriteLine("// aspire.go - Capability-based Aspire SDK");
        WriteLine("// GENERATED CODE - DO NOT EDIT");
        WriteLine();
        WriteLine("package aspire");
        WriteLine();
        WriteLine("import (");
        WriteLine("\t\"fmt\"");
        WriteLine("\t\"os\"");
        WriteLine(")");
        WriteLine();
    }

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

    private void GenerateHandleTypes(
        IReadOnlyList<GoHandleType> handleTypes,
        Dictionary<string, List<AtsCapabilityInfo>> capabilitiesByTarget)
    {
        if (handleTypes.Count == 0)
        {
            return;
        }

        WriteLine("// ============================================================================");
        WriteLine("// Handle Wrappers");
        WriteLine("// ============================================================================");
        WriteLine();

        foreach (var handleType in handleTypes.OrderBy(t => t.StructName, StringComparer.Ordinal))
        {
            var baseStruct = handleType.IsResourceBuilder ? "ResourceBuilderBase" : "HandleWrapperBase";

            // Collect list/dict property fields
            var listDictFields = new List<(string fieldName, string fieldType)>();
            if (capabilitiesByTarget.TryGetValue(handleType.TypeId, out var methods))
            {
                foreach (var method in methods)
                {
                    var parameters = method.Parameters
                        .Where(p => !string.Equals(p.Name, method.TargetParameterName ?? "builder", StringComparison.Ordinal))
                        .ToList();

                    if (parameters.Count == 0 && IsListOrDictPropertyGetter(method.ReturnType))
                    {
                        var returnType = method.ReturnType!;
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
            }

            WriteLine($"// {handleType.StructName} wraps a handle for {handleType.TypeId}.");
            WriteLine($"type {handleType.StructName} struct {{");
            WriteLine($"\t{baseStruct}");
            foreach (var (fieldName, fieldType) in listDictFields)
            {
                WriteLine($"\t{fieldName} {fieldType}");
            }
            WriteLine("}");
            WriteLine();

            // Constructor
            WriteLine($"// New{handleType.StructName} creates a new {handleType.StructName}.");
            WriteLine($"func New{handleType.StructName}(handle *Handle, client *AspireClient) *{handleType.StructName} {{");
            WriteLine($"\treturn &{handleType.StructName}{{");
            WriteLine($"\t\t{baseStruct}: New{baseStruct}(handle, client),");
            WriteLine("\t}");
            WriteLine("}");
            WriteLine();

            if (capabilitiesByTarget.TryGetValue(handleType.TypeId, out var allMethods))
            {
                foreach (var method in allMethods)
                {
                    GenerateCapabilityMethod(handleType.StructName, method);
                }
            }
        }
    }

    private void GenerateCapabilityMethod(string structName, AtsCapabilityInfo capability)
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

        var returnType = MapTypeRefToGo(capability.ReturnType, false);
        var hasReturn = capability.ReturnType.TypeId != AtsConstants.Void;
        // Don't add extra * if return type already starts with *
        var returnSignature = hasReturn
            ? returnType.StartsWith("*", StringComparison.Ordinal) || returnType == "any"
                ? $"({returnType}, error)"
                : $"(*{returnType}, error)"
            : "error";

        // Build parameter list
        var paramList = new StringBuilder();
        foreach (var parameter in parameters)
        {
            if (paramList.Length > 0)
            {
                paramList.Append(", ");
            }
            var paramName = GetLocalIdentifier(parameter.Name);
            var paramType = parameter.IsCallback
                ? "func(...any) any"
                : IsCancellationToken(parameter)
                    ? "*CancellationToken"
                    : MapTypeRefToGo(parameter.Type, parameter.IsOptional);
            paramList.Append(CultureInfo.InvariantCulture, $"{paramName} {paramType}");
        }

        // Generate comment
        if (!string.IsNullOrEmpty(capability.Description))
        {
            WriteLine($"// {methodName} {char.ToLowerInvariant(capability.Description[0])}{capability.Description[1..]}");
        }

        // Use 'reqArgs' as local variable name to avoid conflict with parameters named 'args'
        WriteLine($"func (s *{structName}) {methodName}({paramList}) {returnSignature} {{");
        WriteLine("\treqArgs := map[string]any{");
        WriteLine($"\t\t\"{targetParamName}\": SerializeValue(s.Handle()),");
        WriteLine("\t}");

        foreach (var parameter in parameters)
        {
            var paramName = GetLocalIdentifier(parameter.Name);
            if (parameter.IsCallback)
            {
                WriteLine($"\tif {paramName} != nil {{");
                WriteLine($"\t\treqArgs[\"{parameter.Name}\"] = RegisterCallback({paramName})");
                WriteLine("\t}");
                continue;
            }

            if (IsCancellationToken(parameter))
            {
                WriteLine($"\tif {paramName} != nil {{");
                WriteLine($"\t\treqArgs[\"{parameter.Name}\"] = RegisterCancellation({paramName}, s.Client())");
                WriteLine("\t}");
                continue;
            }

            var paramTypeStr = MapTypeRefToGo(parameter.Type, parameter.IsOptional);
            var isNilableType = IsNilableGoType(paramTypeStr);

            if (parameter.IsOptional && isNilableType)
            {
                WriteLine($"\tif {paramName} != nil {{");
                WriteLine($"\t\treqArgs[\"{parameter.Name}\"] = SerializeValue({paramName})");
                WriteLine("\t}");
            }
            else
            {
                WriteLine($"\treqArgs[\"{parameter.Name}\"] = SerializeValue({paramName})");
            }
        }

        if (hasReturn)
        {
            WriteLine($"\tresult, err := s.Client().InvokeCapability(\"{capability.CapabilityId}\", reqArgs)");
            WriteLine("\tif err != nil {");
            WriteLine("\t\treturn nil, err");
            WriteLine("\t}");
            // Cast appropriately based on whether return type is already a pointer
            if (returnType.StartsWith("*", StringComparison.Ordinal))
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
            WriteLine($"\t_, err := s.Client().InvokeCapability(\"{capability.CapabilityId}\", reqArgs)");
            WriteLine("\treturn err");
        }

        WriteLine("}");
        WriteLine();
    }

    private static bool IsListOrDictPropertyGetter(AtsTypeRef? returnType)
    {
        if (returnType is null)
        {
            return false;
        }

        return returnType.Category == AtsTypeCategory.List || returnType.Category == AtsTypeCategory.Dict;
    }

    private void GenerateListOrDictProperty(string structName, AtsCapabilityInfo capability, string methodName)
    {
        var returnType = capability.ReturnType!;
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

    private void GenerateHandleWrapperRegistrations(
        IReadOnlyList<GoHandleType> handleTypes,
        Dictionary<string, bool> collectionTypes)
    {
        WriteLine("// ============================================================================");
        WriteLine("// Handle wrapper registrations");
        WriteLine("// ============================================================================");
        WriteLine();
        WriteLine("func init() {");

        foreach (var handleType in handleTypes)
        {
            WriteLine($"\tRegisterHandleWrapper(\"{handleType.TypeId}\", func(h *Handle, c *AspireClient) any {{");
            WriteLine($"\t\treturn New{handleType.StructName}(h, c)");
            WriteLine("\t})");
        }

        foreach (var (typeId, isDict) in collectionTypes)
        {
            var wrapperType = isDict ? "AspireDict" : "AspireList";
            var typeArgs = isDict ? "[any, any]" : "[any]";
            WriteLine($"\tRegisterHandleWrapper(\"{typeId}\", func(h *Handle, c *AspireClient) any {{");
            WriteLine($"\t\treturn &{wrapperType}{typeArgs}{{HandleWrapperBase: NewHandleWrapperBase(h, c)}}");
            WriteLine("\t})");
        }

        WriteLine("}");
        WriteLine();
    }

    private void GenerateConnectionHelpers()
    {
        var builderStructName = _structNames.TryGetValue(AtsConstants.BuilderTypeId, out var name)
            ? name
            : "DistributedApplicationBuilder";

        WriteLine("// ============================================================================");
        WriteLine("// Connection Helpers");
        WriteLine("// ============================================================================");
        WriteLine();
        WriteLine("// Connect establishes a connection to the AppHost server.");
        WriteLine("func Connect() (*AspireClient, error) {");
        WriteLine("\tsocketPath := os.Getenv(\"REMOTE_APP_HOST_SOCKET_PATH\")");
        WriteLine("\tif socketPath == \"\" {");
        WriteLine("\t\treturn nil, fmt.Errorf(\"REMOTE_APP_HOST_SOCKET_PATH environment variable not set. Run this application using `aspire run`\")");
        WriteLine("\t}");
        WriteLine("\tclient := NewAspireClient(socketPath)");
        WriteLine("\tif err := client.Connect(); err != nil {");
        WriteLine("\t\treturn nil, err");
        WriteLine("\t}");
        WriteLine("\tauthToken := os.Getenv(\"ASPIRE_REMOTE_APPHOST_TOKEN\")");
        WriteLine("\tif authToken == \"\" {");
        WriteLine("\t\treturn nil, fmt.Errorf(\"ASPIRE_REMOTE_APPHOST_TOKEN environment variable not set. Run this application using `aspire run`\")");
        WriteLine("\t}");
        WriteLine("\tif err := client.Authenticate(authToken); err != nil {");
        WriteLine("\t\treturn nil, err");
        WriteLine("\t}");
        WriteLine("\tclient.OnDisconnect(func() { os.Exit(1) })");
        WriteLine("\treturn client, nil");
        WriteLine("}");
        WriteLine();
        WriteLine($"// CreateBuilder creates a new distributed application builder.");
        WriteLine($"func CreateBuilder(options *CreateBuilderOptions) (*{builderStructName}, error) {{");
        WriteLine("\tclient, err := Connect()");
        WriteLine("\tif err != nil {");
        WriteLine("\t\treturn nil, err");
        WriteLine("\t}");
        WriteLine("\tresolvedOptions := make(map[string]any)");
        WriteLine("\tif options != nil {");
        WriteLine("\t\tfor k, v := range options.ToMap() {");
        WriteLine("\t\t\tresolvedOptions[k] = v");
        WriteLine("\t\t}");
        WriteLine("\t}");
        WriteLine("\tif _, ok := resolvedOptions[\"Args\"]; !ok {");
        WriteLine("\t\tresolvedOptions[\"Args\"] = os.Args[1:]");
        WriteLine("\t}");
        WriteLine("\tif _, ok := resolvedOptions[\"ProjectDirectory\"]; !ok {");
        WriteLine("\t\tif pwd, err := os.Getwd(); err == nil {");
        WriteLine("\t\t\tresolvedOptions[\"ProjectDirectory\"] = pwd");
        WriteLine("\t\t}");
        WriteLine("\t}");
        WriteLine("\tresult, err := client.InvokeCapability(\"Aspire.Hosting/createBuilder\", map[string]any{\"argsOrOptions\": resolvedOptions})");
        WriteLine("\tif err != nil {");
        WriteLine("\t\treturn nil, err");
        WriteLine("\t}");
        WriteLine($"\treturn result.(*{builderStructName}), nil");
        WriteLine("}");
        WriteLine();
    }

    private IReadOnlyList<GoHandleType> BuildHandleTypes(AtsContext context)
    {
        var handleTypeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var handleType in context.HandleTypes)
        {
            // Skip ReferenceExpression and CancellationToken - they're defined in base.go/transport.go
            if (handleType.AtsTypeId == AtsConstants.ReferenceExpressionTypeId
                || IsCancellationTokenTypeId(handleType.AtsTypeId))
            {
                continue;
            }
            handleTypeIds.Add(handleType.AtsTypeId);
        }

        foreach (var capability in context.Capabilities)
        {
            AddHandleTypeIfNeeded(handleTypeIds, capability.TargetType);
            AddHandleTypeIfNeeded(handleTypeIds, capability.ReturnType);
            foreach (var parameter in capability.Parameters)
            {
                AddHandleTypeIfNeeded(handleTypeIds, parameter.Type);
                if (parameter.IsCallback && parameter.CallbackParameters is not null)
                {
                    foreach (var callbackParam in parameter.CallbackParameters)
                    {
                        AddHandleTypeIfNeeded(handleTypeIds, callbackParam.Type);
                    }
                }
            }
            // Also include expanded target types (concrete types discovered via interface expansion)
            foreach (var expandedType in capability.ExpandedTargetTypes)
            {
                AddHandleTypeIfNeeded(handleTypeIds, expandedType);
            }
        }

        _structNames.Clear();
        foreach (var typeId in handleTypeIds)
        {
            _structNames[typeId] = CreateStructName(typeId);
        }

        var handleTypeMap = context.HandleTypes
            .GroupBy(t => t.AtsTypeId, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Any(t => t.IsResourceBuilder),
                StringComparer.Ordinal);
        var results = new List<GoHandleType>();
        foreach (var typeId in handleTypeIds)
        {
            var isResourceBuilder = false;
            if (handleTypeMap.TryGetValue(typeId, out var typeInfo))
            {
                isResourceBuilder = typeInfo;
            }

            results.Add(new GoHandleType(typeId, _structNames[typeId], isResourceBuilder));
        }

        return results;
    }

    private static Dictionary<string, List<AtsCapabilityInfo>> GroupCapabilitiesByTarget(
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
                    : [];

            foreach (var targetType in targetTypes)
            {
                if (targetType.TypeId is null)
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
                if (parameter.IsCallback && parameter.CallbackParameters is not null)
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

        var baseType = typeRef.Category switch
        {
            AtsTypeCategory.Primitive => MapPrimitiveType(typeRef.TypeId),
            AtsTypeCategory.Enum => MapEnumType(typeRef.TypeId),
            AtsTypeCategory.Handle => "*" + MapHandleType(typeRef.TypeId),
            AtsTypeCategory.Dto => "*" + MapDtoType(typeRef.TypeId),
            AtsTypeCategory.Callback => "func(...any) any",
            AtsTypeCategory.Array => $"[]{MapTypeRefToGo(typeRef.ElementType, false)}",
            AtsTypeCategory.List => typeRef.IsReadOnly
                ? $"[]{MapTypeRefToGo(typeRef.ElementType, false)}"
                : $"*AspireList[{MapTypeRefToGo(typeRef.ElementType, false)}]",
            AtsTypeCategory.Dict => typeRef.IsReadOnly
                ? $"map[{MapTypeRefToGo(typeRef.KeyType, false)}]{MapTypeRefToGo(typeRef.ValueType, false)}"
                : $"*AspireDict[{MapTypeRefToGo(typeRef.KeyType, false)}, {MapTypeRefToGo(typeRef.ValueType, false)}]",
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

    private string MapHandleType(string typeId) =>
        _structNames.TryGetValue(typeId, out var name) ? name : "Handle";

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

    private static bool IsCancellationToken(AtsParameterInfo parameter) =>
        IsCancellationTokenTypeId(parameter.Type?.TypeId);

    private static bool IsCancellationTokenTypeId(string? typeId) =>
        string.Equals(typeId, AtsConstants.CancellationToken, StringComparison.Ordinal)
        || (typeId?.EndsWith("/System.Threading.CancellationToken", StringComparison.Ordinal) ?? false);

    private static void AddHandleTypeIfNeeded(HashSet<string> handleTypeIds, AtsTypeRef? typeRef)
    {
        if (typeRef is null)
        {
            return;
        }

        // Skip ReferenceExpression and CancellationToken - they're defined in base.go/transport.go
        if (typeRef.TypeId == AtsConstants.ReferenceExpressionTypeId
            || IsCancellationTokenTypeId(typeRef.TypeId))
        {
            return;
        }

        if (typeRef.Category == AtsTypeCategory.Handle)
        {
            handleTypeIds.Add(typeRef.TypeId);
        }
    }

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
        else if (typeRef.Category == AtsTypeCategory.Dict)
        {
            if (!typeRef.IsReadOnly)
            {
                typeIds[typeRef.TypeId] = true; // true = Dict
            }
        }
    }

    private string CreateStructName(string typeId)
    {
        var baseName = ExtractTypeName(typeId);
        var name = SanitizeIdentifier(baseName);
        if (_structNames.Values.Contains(name, StringComparer.Ordinal))
        {
            var assemblyName = typeId.Split('/')[0];
            var assemblyPrefix = SanitizeIdentifier(assemblyName);
            name = $"{assemblyPrefix}{name}";
        }

        var counter = 1;
        var candidate = name;
        while (_structNames.Values.Contains(candidate, StringComparer.Ordinal))
        {
            counter++;
            candidate = $"{name}{counter}";
        }

        return candidate;
    }

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

    /// <summary>
    /// Converts a name to PascalCase for Go exported identifiers.
    /// </summary>
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

    /// <summary>
    /// Converts a name to camelCase for Go unexported identifiers.
    /// </summary>
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

    private sealed record GoHandleType(string TypeId, string StructName, bool IsResourceBuilder);
}
