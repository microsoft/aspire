// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Aspire.TypeSystem;

namespace Aspire.Hosting.CodeGeneration.Python;

/// <summary>
/// Generates a Python SDK using the ATS (Aspire Type System) capability-based API.
/// Produces wrapper classes that proxy capabilities via JSON-RPC.
/// </summary>
public sealed class AtsPythonCodeGenerator : ICodeGenerator
{
    private static readonly HashSet<string> s_pythonKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "false", "none", "true", "and", "as", "assert", "async", "await", "break",
        "class", "continue", "def", "del", "elif", "else", "except", "finally",
        "for", "from", "global", "if", "import", "in", "is", "lambda", "nonlocal",
        "not", "or", "pass", "raise", "return", "try", "while", "with", "yield",
        "match", "case"
    };

    private TextWriter _writer = null!;
    private readonly Dictionary<string, string> _classNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _dtoNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _enumNames = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string Language => "Python";

    /// <inheritdoc />
    public Dictionary<string, string> GenerateDistributedApplication(AtsContext context)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["transport.py"] = GetEmbeddedResource("transport.py"),
            ["base.py"] = GetEmbeddedResource("base.py"),
            ["aspire.py"] = GenerateAspireSdk(context)
        };
    }

    private static string GetEmbeddedResource(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"Aspire.Hosting.CodeGeneration.Python.Resources.{name}";

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
        GenerateHandleTypes(handleTypes, capabilitiesByTarget);
        GenerateHandleWrapperRegistrations(handleTypes, collectionTypes);
        GenerateConnectionHelpers();

        return stringWriter.ToString();
    }

    private void WriteHeader()
    {
        WriteLine("# aspire.py - Capability-based Aspire SDK");
        WriteLine("# GENERATED CODE - DO NOT EDIT");
        WriteLine();
        WriteLine("from __future__ import annotations");
        WriteLine();
        WriteLine("import os");
        WriteLine("import sys");
        WriteLine("from dataclasses import dataclass");
        WriteLine("from enum import Enum");
        WriteLine("from typing import Any, Callable, Dict, List");
        WriteLine();
        WriteLine("from transport import AspireClient, Handle, CapabilityError, register_callback, register_handle_wrapper, register_cancellation");
        WriteLine("from base import AspireDict, AspireList, ReferenceExpression, ref_expr, HandleWrapperBase, ResourceBuilderBase, serialize_value");
        WriteLine();
    }

    private void GenerateEnumTypes(IReadOnlyList<AtsEnumTypeInfo> enumTypes)
    {
        if (enumTypes.Count == 0)
        {
            return;
        }

        WriteLine("# ============================================================================");
        WriteLine("# Enums");
        WriteLine("# ============================================================================");
        WriteLine();

        foreach (var enumType in enumTypes)
        {
            if (enumType.ClrType is null)
            {
                continue;
            }

            var enumName = _enumNames[enumType.TypeId];
            WriteLine($"class {enumName}(str, Enum):");
            foreach (var member in Enum.GetNames(enumType.ClrType))
            {
                // Convert enum member names to UPPER_SNAKE_CASE for idiomatic Python
                var memberName = ToUpperSnakeCase(member);
                WriteLine($"    {memberName} = \"{member}\"");
            }
            WriteLine();
        }
    }

    private void GenerateDtoTypes(IReadOnlyList<AtsDtoTypeInfo> dtoTypes)
    {
        if (dtoTypes.Count == 0)
        {
            return;
        }

        WriteLine("# ============================================================================");
        WriteLine("# DTOs");
        WriteLine("# ============================================================================");
        WriteLine();

        foreach (var dto in dtoTypes)
        {
            var dtoName = _dtoNames[dto.TypeId];
            WriteLine("@dataclass");
            WriteLine($"class {dtoName}:");
            if (dto.Properties.Count == 0)
            {
                WriteLine("    pass");
                WriteLine();
                continue;
            }

            foreach (var property in dto.Properties)
            {
                // Convert property name to snake_case for idiomatic Python
                var propertyName = ToSnakeCase(property.Name);
                var propertyType = MapTypeRefToPython(property.Type);
                var optionalSuffix = property.IsOptional ? " | None" : string.Empty;
                var defaultValue = property.IsOptional ? " = None" : string.Empty;
                WriteLine($"    {propertyName}: {propertyType}{optionalSuffix}{defaultValue}");
            }

            WriteLine();
            WriteLine("    def to_dict(self) -> Dict[str, Any]:");
            WriteLine("        return {");
            foreach (var property in dto.Properties)
            {
                // Use snake_case in Python code, but original name for JSON serialization
                var propertyName = ToSnakeCase(property.Name);
                WriteLine($"            \"{property.Name}\": serialize_value(self.{propertyName}),");
            }
            WriteLine("        }");
            WriteLine();
        }
    }

    private void GenerateHandleTypes(
        IReadOnlyList<PythonHandleType> handleTypes,
        Dictionary<string, List<AtsCapabilityInfo>> capabilitiesByTarget)
    {
        if (handleTypes.Count == 0)
        {
            return;
        }

        WriteLine("# ============================================================================");
        WriteLine("# Handle Wrappers");
        WriteLine("# ============================================================================");
        WriteLine();

        foreach (var handleType in handleTypes.OrderBy(t => t.ClassName, StringComparer.Ordinal))
        {
            var baseClass = handleType.IsResourceBuilder ? "ResourceBuilderBase" : "HandleWrapperBase";
            WriteLine($"class {handleType.ClassName}({baseClass}):");
            WriteLine("    def __init__(self, handle: Handle, client: AspireClient):");
            WriteLine("        super().__init__(handle, client)");
            WriteLine();

            if (capabilitiesByTarget.TryGetValue(handleType.TypeId, out var methods))
            {
                foreach (var method in methods)
                {
                    GenerateCapabilityMethod(method);
                }
            }
            else
            {
                WriteLine("    pass");
            }

            WriteLine();
        }
    }

    private void GenerateCapabilityMethod(AtsCapabilityInfo capability)
    {
        var targetParamName = capability.TargetParameterName ?? "builder";
        // Convert method name to snake_case for idiomatic Python
        var methodName = ToSnakeCase(capability.MethodName);
        var parameters = capability.Parameters
            .Where(p => !string.Equals(p.Name, targetParamName, StringComparison.Ordinal))
            .ToList();

        // Check if this is a List/Dict property getter (no parameters, returns List/Dict)
        if (parameters.Count == 0 && IsListOrDictPropertyGetter(capability.ReturnType))
        {
            GenerateListOrDictProperty(capability, methodName);
            return;
        }

        var parameterList = BuildParameterList(parameters);
        var returnType = MapTypeRefToPython(capability.ReturnType);

        var signature = string.IsNullOrEmpty(parameterList)
            ? "self"
            : $"self, {parameterList}";
        WriteLine($"    def {methodName}({signature}) -> {returnType}:");
        if (!string.IsNullOrEmpty(capability.Description))
        {
            WriteLine($"        \"\"\"{capability.Description}\"\"\"");
        }

        // Use serialize_value for the handle to convert it to JSON format
        WriteLine($"        args: Dict[str, Any] = {{ \"{targetParamName}\": serialize_value(self._handle) }}");

        foreach (var parameter in parameters)
        {
            // Convert parameter name to snake_case for idiomatic Python
            var parameterName = ToSnakeCase(parameter.Name);
            if (parameter.IsCallback)
            {
                WriteLine($"        {parameterName}_id = register_callback({parameterName}) if {parameterName} is not None else None");
                WriteLine($"        if {parameterName}_id is not None:");
                WriteLine($"            args[\"{parameter.Name}\"] = {parameterName}_id");
                continue;
            }

            if (IsCancellationToken(parameter))
            {
                WriteLine($"        {parameterName}_id = register_cancellation({parameterName}, self._client) if {parameterName} is not None else None");
                WriteLine($"        if {parameterName}_id is not None:");
                WriteLine($"            args[\"{parameter.Name}\"] = {parameterName}_id");
                continue;
            }

            if (parameter.IsOptional && parameter.DefaultValue is null)
            {
                WriteLine($"        if {parameterName} is not None:");
                WriteLine($"            args[\"{parameter.Name}\"] = serialize_value({parameterName})");
            }
            else
            {
                WriteLine($"        args[\"{parameter.Name}\"] = serialize_value({parameterName})");
            }
        }

        if (capability.ReturnType.TypeId == AtsConstants.Void)
        {
            WriteLine($"        self._client.invoke_capability(\"{capability.CapabilityId}\", args)");
            WriteLine("        return None");
        }
        else
        {
            WriteLine($"        return self._client.invoke_capability(\"{capability.CapabilityId}\", args)");
        }
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

    private void GenerateListOrDictProperty(AtsCapabilityInfo capability, string methodName)
    {
        var returnType = capability.ReturnType!;
        var isDict = returnType.Category == AtsTypeCategory.Dict;
        var wrapperType = isDict ? "AspireDict" : "AspireList";

        // Determine element type for type hints
        string typeHint;
        if (isDict)
        {
            var keyType = MapTypeRefToPython(returnType.KeyType);
            var valueType = MapTypeRefToPython(returnType.ValueType);
            typeHint = $"{wrapperType}[{keyType}, {valueType}]";
        }
        else
        {
            var elementType = MapTypeRefToPython(returnType.ElementType);
            typeHint = $"{wrapperType}[{elementType}]";
        }

        // Generate cached property with lazy initialization
        WriteLine($"    @property");
        WriteLine($"    def {methodName}(self) -> {typeHint}:");
        if (!string.IsNullOrEmpty(capability.Description))
        {
            WriteLine($"        \"\"\"{capability.Description}\"\"\"");
        }
        WriteLine($"        if not hasattr(self, '_{methodName}'):");
        WriteLine($"            self._{methodName} = {wrapperType}(");
        WriteLine($"                self._handle,");
        WriteLine($"                self._client,");
        WriteLine($"                \"{capability.CapabilityId}\"");
        WriteLine($"            )");
        WriteLine($"        return self._{methodName}");
        WriteLine();
    }

    private void GenerateHandleWrapperRegistrations(
        IReadOnlyList<PythonHandleType> handleTypes,
        Dictionary<string, bool> collectionTypes)
    {
        WriteLine("# ============================================================================");
        WriteLine("# Handle wrapper registrations");
        WriteLine("# ============================================================================");
        WriteLine();

        foreach (var handleType in handleTypes)
        {
            WriteLine($"register_handle_wrapper(\"{handleType.TypeId}\", lambda handle, client: {handleType.ClassName}(handle, client))");
        }

        foreach (var (typeId, isDict) in collectionTypes)
        {
            var wrapperType = isDict ? "AspireDict" : "AspireList";
            WriteLine($"register_handle_wrapper(\"{typeId}\", lambda handle, client: {wrapperType}(handle, client))");
        }

        WriteLine();
    }

    private void GenerateConnectionHelpers()
    {
        var builderClassName = _classNames.TryGetValue(AtsConstants.BuilderTypeId, out var name)
            ? name
            : "DistributedApplicationBuilder";

        WriteLine("# ============================================================================");
        WriteLine("# Connection Helpers");
        WriteLine("# ============================================================================");
        WriteLine();
        WriteLine("def connect() -> AspireClient:");
        WriteLine("    socket_path = os.environ.get(\"REMOTE_APP_HOST_SOCKET_PATH\")");
        WriteLine("    if not socket_path:");
        WriteLine("        raise RuntimeError(\"REMOTE_APP_HOST_SOCKET_PATH environment variable not set. Run this application using `aspire run`.\")");
        WriteLine("    client = AspireClient(socket_path)");
        WriteLine("    client.connect()");
        WriteLine("    client.on_disconnect(lambda: sys.exit(1))");
        WriteLine("    return client");
        WriteLine();
        WriteLine($"def create_builder(options: Any | None = None) -> {builderClassName}:");
        WriteLine("    client = connect()");
        WriteLine("    resolved_options: Dict[str, Any] = {}");
        WriteLine("    if options is not None:");
        WriteLine("        if hasattr(options, \"to_dict\"):");
        WriteLine("            resolved_options.update(options.to_dict())");
        WriteLine("        elif isinstance(options, dict):");
        WriteLine("            resolved_options.update(options)");
        WriteLine("    if resolved_options.get(\"Args\") is None:");
        WriteLine("        resolved_options[\"Args\"] = sys.argv[1:]");
        WriteLine("    if resolved_options.get(\"ProjectDirectory\") is None:");
        WriteLine("        resolved_options[\"ProjectDirectory\"] = os.environ.get(\"ASPIRE_PROJECT_DIRECTORY\", os.getcwd())");
        WriteLine("    apphost_file_path = os.environ.get(\"ASPIRE_APPHOST_FILEPATH\")");
        WriteLine("    if apphost_file_path:");
        WriteLine("        if resolved_options.get(\"AppHostFilePath\") is None:");
        WriteLine("            resolved_options[\"AppHostFilePath\"] = apphost_file_path");
        WriteLine("    result = client.invoke_capability(\"Aspire.Hosting/createBuilderWithOptions\", {\"options\": resolved_options})");
        WriteLine("    return result");
        WriteLine();
        WriteLine("# Re-export commonly used types");
        WriteLine("CapabilityError = CapabilityError");
        WriteLine("Handle = Handle");
        WriteLine("ReferenceExpression = ReferenceExpression");
        WriteLine("ref_expr = ref_expr");
        WriteLine();
    }

    private IReadOnlyList<PythonHandleType> BuildHandleTypes(AtsContext context)
    {
        var handleTypeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var handleType in context.HandleTypes)
        {
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
        foreach (var typeId in handleTypeIds)
        {
            _classNames[typeId] = CreateClassName(typeId);
        }

        var handleTypeMap = context.HandleTypes.ToDictionary(t => t.AtsTypeId, StringComparer.Ordinal);
        var results = new List<PythonHandleType>();
        foreach (var typeId in handleTypeIds)
        {
            var isResourceBuilder = false;
            if (handleTypeMap.TryGetValue(typeId, out var typeInfo))
            {
                isResourceBuilder = typeInfo.IsResourceBuilder;
            }

            results.Add(new PythonHandleType(typeId, _classNames[typeId], isResourceBuilder));
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

    private string BuildParameterList(List<AtsParameterInfo> parameters)
    {
        if (parameters.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        for (var index = 0; index < parameters.Count; index++)
        {
            var parameter = parameters[index];
            if (index > 0)
            {
                builder.Append(", ");
            }

            // Convert parameter name to snake_case for idiomatic Python
            var parameterName = ToSnakeCase(parameter.Name);
            var parameterType = parameter.IsCallback
                ? MapCallbackTypeSignature(parameter.CallbackParameters, parameter.CallbackReturnType)
                : IsCancellationToken(parameter)
                    ? "CancellationToken"
                    : MapTypeRefToPython(parameter.Type);
            var defaultValue = parameter.IsOptional
                ? GetDefaultValue(parameter)
                : null;

            if (parameter.IsOptional && defaultValue is null)
            {
                parameterType += " | None";
                defaultValue = "None";
            }

            builder.Append(parameterName);
            builder.Append(": ");
            builder.Append(parameterType);
            if (defaultValue is not null)
            {
                builder.Append(" = ");
                builder.Append(defaultValue);
            }
        }

        return builder.ToString();
    }

    private string MapTypeRefToPython(AtsTypeRef? typeRef)
    {
        if (typeRef is null)
        {
            return "Any";
        }

        if (typeRef.TypeId == AtsConstants.ReferenceExpressionTypeId)
        {
            return "ReferenceExpression";
        }

        return typeRef.Category switch
        {
            AtsTypeCategory.Primitive => MapPrimitiveType(typeRef.TypeId),
            AtsTypeCategory.Enum => MapEnumType(typeRef.TypeId),
            AtsTypeCategory.Handle => MapHandleType(typeRef.TypeId),
            AtsTypeCategory.Dto => MapDtoType(typeRef.TypeId),
            AtsTypeCategory.Callback => "Callable[..., Any]",
            AtsTypeCategory.Array => $"list[{MapTypeRefToPython(typeRef.ElementType)}]",
            AtsTypeCategory.List => typeRef.IsReadOnly
                ? $"list[{MapTypeRefToPython(typeRef.ElementType)}]"
                : $"AspireList[{MapTypeRefToPython(typeRef.ElementType)}]",
            AtsTypeCategory.Dict => typeRef.IsReadOnly
                ? $"dict[{MapTypeRefToPython(typeRef.KeyType)}, {MapTypeRefToPython(typeRef.ValueType)}]"
                : $"AspireDict[{MapTypeRefToPython(typeRef.KeyType)}, {MapTypeRefToPython(typeRef.ValueType)}]",
            AtsTypeCategory.Union => MapUnionType(typeRef),
            AtsTypeCategory.Unknown => "Any",
            _ => "Any"
        };
    }

    private string MapUnionType(AtsTypeRef typeRef)
    {
        if (typeRef.UnionTypes is null || typeRef.UnionTypes.Count == 0)
        {
            return "Any";
        }

        var unionTypes = typeRef.UnionTypes.Select(MapTypeRefToPython);
        return string.Join(" | ", unionTypes);
    }

    private string MapHandleType(string typeId) =>
        _classNames.TryGetValue(typeId, out var name) ? name : "Handle";

    private string MapDtoType(string typeId) =>
        _dtoNames.TryGetValue(typeId, out var name) ? name : "dict[str, Any]";

    private string MapEnumType(string typeId) =>
        _enumNames.TryGetValue(typeId, out var name) ? name : "str";

    private static string MapPrimitiveType(string typeId) => typeId switch
    {
        AtsConstants.String or AtsConstants.Char => "str",
        AtsConstants.Number => "float",
        AtsConstants.Boolean => "bool",
        AtsConstants.Void => "None",
        AtsConstants.Any => "Any",
        AtsConstants.DateTime or AtsConstants.DateTimeOffset or
        AtsConstants.DateOnly or AtsConstants.TimeOnly => "str",
        AtsConstants.TimeSpan => "float",
        AtsConstants.Guid or AtsConstants.Uri => "str",
        AtsConstants.CancellationToken => "CancellationToken",
        _ => "Any"
    };

    private string MapCallbackTypeSignature(
        IReadOnlyList<AtsCallbackParameterInfo>? parameters,
        AtsTypeRef? returnType)
    {
        var returnTypeName = MapTypeRefToPython(returnType);
        if (parameters is null || parameters.Count == 0)
        {
            return $"Callable[[], {returnTypeName}]";
        }

        var paramTypes = string.Join(", ", parameters.Select(p => MapTypeRefToPython(p.Type)));
        return $"Callable[[{paramTypes}], {returnTypeName}]";
    }

    private static bool IsCancellationToken(AtsParameterInfo parameter) =>
        parameter.Type?.TypeId == AtsConstants.CancellationToken;

    private static void AddHandleTypeIfNeeded(HashSet<string> handleTypeIds, AtsTypeRef? typeRef)
    {
        if (typeRef is null)
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

    private static string? GetDefaultValue(AtsParameterInfo parameter)
    {
        if (parameter.DefaultValue is null)
        {
            return null;
        }

        return parameter.DefaultValue switch
        {
            bool boolValue => boolValue ? "True" : "False",
            string stringValue => $"\"{stringValue.Replace("\"", "\\\"", StringComparison.Ordinal)}\"",
            char charValue => $"\"{charValue}\"",
            int intValue => intValue.ToString(CultureInfo.InvariantCulture),
            long longValue => longValue.ToString(CultureInfo.InvariantCulture),
            float floatValue => floatValue.ToString(CultureInfo.InvariantCulture),
            double doubleValue => doubleValue.ToString(CultureInfo.InvariantCulture),
            decimal decimalValue => decimalValue.ToString(CultureInfo.InvariantCulture),
            _ => "None"
        };
    }

    private string CreateClassName(string typeId)
    {
        var baseName = ExtractTypeName(typeId);
        var name = SanitizeIdentifier(baseName);
        if (_classNames.Values.Contains(name, StringComparer.Ordinal))
        {
            var assemblyName = typeId.Split('/')[0];
            var assemblyPrefix = SanitizeIdentifier(assemblyName);
            name = $"{assemblyPrefix}{name}";
        }

        var counter = 1;
        var candidate = name;
        while (_classNames.Values.Contains(candidate, StringComparer.Ordinal))
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
        return s_pythonKeywords.Contains(sanitized) ? sanitized + "_" : sanitized;
    }

    /// <summary>
    /// Converts a camelCase or PascalCase identifier to snake_case for Python.
    /// </summary>
    private static string ToSnakeCase(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "_";
        }

        var snakeCase = JsonNamingPolicy.SnakeCaseLower.ConvertName(name);
        return s_pythonKeywords.Contains(snakeCase) ? snakeCase + "_" : snakeCase;
    }

    /// <summary>
    /// Converts a camelCase or PascalCase identifier to UPPER_SNAKE_CASE for Python enum members.
    /// </summary>
    private static string ToUpperSnakeCase(string name) => ToSnakeCase(name).ToUpperInvariant();

    private void WriteLine(string value = "")
    {
        _writer.WriteLine(value);
    }

    private sealed record PythonHandleType(string TypeId, string ClassName, bool IsResourceBuilder);
}
