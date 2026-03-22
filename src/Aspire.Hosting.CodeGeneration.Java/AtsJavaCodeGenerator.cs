// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Reflection;
using System.Text;
using Aspire.TypeSystem;

namespace Aspire.Hosting.CodeGeneration.Java;

/// <summary>
/// Generates a Java SDK using the ATS (Aspire Type System) capability-based API.
/// Produces wrapper classes that proxy capabilities via JSON-RPC.
/// </summary>
public sealed class AtsJavaCodeGenerator : ICodeGenerator
{
    private static readonly HashSet<string> s_javaKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "assert", "boolean", "break", "byte", "case", "catch", "char",
        "class", "const", "continue", "default", "do", "double", "else", "enum",
        "extends", "final", "finally", "float", "for", "goto", "if", "implements",
        "import", "instanceof", "int", "interface", "long", "native", "new", "package",
        "private", "protected", "public", "return", "short", "static", "strictfp",
        "super", "switch", "synchronized", "this", "throw", "throws", "transient",
        "try", "void", "volatile", "while", "true", "false", "null"
    };

    private TextWriter _writer = null!;
    private readonly Dictionary<string, string> _classNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _dtoNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _enumNames = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string Language => "Java";

    /// <inheritdoc />
    public Dictionary<string, string> GenerateDistributedApplication(AtsContext context)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Transport.java"] = GetEmbeddedResource("Transport.java"),
            ["Base.java"] = GetEmbeddedResource("Base.java"),
            ["Aspire.java"] = GenerateAspireSdk(context)
        };
    }

    private static string GetEmbeddedResource(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"Aspire.Hosting.CodeGeneration.Java.Resources.{name}";

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
        WriteFooter();

        return stringWriter.ToString();
    }

    private void WriteHeader()
    {
        WriteLine("// Aspire.java - Capability-based Aspire SDK");
        WriteLine("// GENERATED CODE - DO NOT EDIT");
        WriteLine();
        WriteLine("package aspire;");
        WriteLine();
        WriteLine("import java.util.*;");
        WriteLine("import java.util.function.*;");
        WriteLine();
    }

    private static void WriteFooter()
    {
        // Close the package-level class if needed
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
            WriteLine($"/** {enumType.Name} enum. */");
            WriteLine($"enum {enumName} {{");
            var members = Enum.GetNames(enumType.ClrType);
            for (var i = 0; i < members.Length; i++)
            {
                var member = members[i];
                var memberName = ToUpperSnakeCase(member);
                var suffix = i < members.Length - 1 ? "," : ";";
                WriteLine($"    {memberName}(\"{member}\"){suffix}");
            }
            WriteLine();
            WriteLine("    private final String value;");
            WriteLine();
            WriteLine($"    {enumName}(String value) {{");
            WriteLine("        this.value = value;");
            WriteLine("    }");
            WriteLine();
            WriteLine("    public String getValue() { return value; }");
            WriteLine();
            WriteLine($"    public static {enumName} fromValue(String value) {{");
            WriteLine($"        for ({enumName} e : values()) {{");
            WriteLine("            if (e.value.equals(value)) return e;");
            WriteLine("        }");
            WriteLine("        throw new IllegalArgumentException(\"Unknown value: \" + value);");
            WriteLine("    }");
            WriteLine("}");
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
            // Skip ReferenceExpression - it's defined in Base.java
            if (dto.TypeId == AtsConstants.ReferenceExpressionTypeId)
            {
                continue;
            }

            var dtoName = _dtoNames[dto.TypeId];
            WriteLine($"/** {dto.Name} DTO. */");
            WriteLine($"class {dtoName} {{");
            
            // Fields
            foreach (var property in dto.Properties)
            {
                var fieldName = ToCamelCase(property.Name);
                var fieldType = MapTypeRefToJava(property.Type, property.IsOptional);
                WriteLine($"    private {fieldType} {fieldName};");
            }
            WriteLine();

            // Getters and setters
            foreach (var property in dto.Properties)
            {
                var fieldName = ToCamelCase(property.Name);
                var methodName = ToPascalCase(property.Name);
                var fieldType = MapTypeRefToJava(property.Type, property.IsOptional);
                
                WriteLine($"    public {fieldType} get{methodName}() {{ return {fieldName}; }}");
                WriteLine($"    public void set{methodName}({fieldType} value) {{ this.{fieldName} = value; }}");
            }
            WriteLine();

            // toMap method for serialization
            WriteLine("    public Map<String, Object> toMap() {");
            WriteLine("        Map<String, Object> map = new HashMap<>();");
            foreach (var property in dto.Properties)
            {
                var fieldName = ToCamelCase(property.Name);
                WriteLine($"        map.put(\"{property.Name}\", AspireClient.serializeValue({fieldName}));");
            }
            WriteLine("        return map;");
            WriteLine("    }");

            WriteLine("}");
            WriteLine();
        }
    }

    private void GenerateHandleTypes(
        IReadOnlyList<JavaHandleType> handleTypes,
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

        foreach (var handleType in handleTypes.OrderBy(t => t.ClassName, StringComparer.Ordinal))
        {
            var baseClass = handleType.IsResourceBuilder ? "ResourceBuilderBase" : "HandleWrapperBase";
            WriteLine($"/** Wrapper for {handleType.TypeId}. */");
            WriteLine($"class {handleType.ClassName} extends {baseClass} {{");
            WriteLine($"    {handleType.ClassName}(Handle handle, AspireClient client) {{");
            WriteLine("        super(handle, client);");
            WriteLine("    }");
            WriteLine();

            if (capabilitiesByTarget.TryGetValue(handleType.TypeId, out var methods))
            {
                foreach (var method in methods)
                {
                    GenerateCapabilityMethod(method);
                }
            }

            WriteLine("}");
            WriteLine();
        }
    }

    private void GenerateCapabilityMethod(AtsCapabilityInfo capability)
    {
        var targetParamName = capability.TargetParameterName ?? "builder";
        var methodName = ToCamelCase(capability.MethodName);
        var parameters = capability.Parameters
            .Where(p => !string.Equals(p.Name, targetParamName, StringComparison.Ordinal))
            .ToList();

        // Check if this is a List/Dict property getter (no parameters, returns List/Dict)
        if (parameters.Count == 0 && IsListOrDictPropertyGetter(capability.ReturnType))
        {
            GenerateListOrDictProperty(capability, methodName);
            return;
        }

        var returnType = MapTypeRefToJava(capability.ReturnType, false);
        var hasReturn = capability.ReturnType.TypeId != AtsConstants.Void;

        // Build parameter list
        var paramList = new StringBuilder();
        foreach (var parameter in parameters)
        {
            if (paramList.Length > 0)
            {
                paramList.Append(", ");
            }
            var paramName = ToCamelCase(parameter.Name);
            var paramType = parameter.IsCallback
                ? "Function<Object[], Object>"
                : IsCancellationToken(parameter)
                    ? "CancellationToken"
                    : MapTypeRefToJava(parameter.Type, parameter.IsOptional);
            paramList.Append(CultureInfo.InvariantCulture, $"{paramType} {paramName}");
        }

        // Generate Javadoc
        if (!string.IsNullOrEmpty(capability.Description))
        {
            WriteLine($"    /** {capability.Description} */");
        }

        WriteLine($"    public {returnType} {methodName}({paramList}) {{");
        WriteLine("        Map<String, Object> reqArgs = new HashMap<>();");
        WriteLine($"        reqArgs.put(\"{targetParamName}\", AspireClient.serializeValue(getHandle()));");

        foreach (var parameter in parameters)
        {
            var paramName = ToCamelCase(parameter.Name);
            if (parameter.IsCallback)
            {
                WriteLine($"        if ({paramName} != null) {{");
                WriteLine($"            reqArgs.put(\"{parameter.Name}\", getClient().registerCallback({paramName}));");
                WriteLine("        }");
                continue;
            }

            if (IsCancellationToken(parameter))
            {
                WriteLine($"        if ({paramName} != null) {{");
                WriteLine($"            reqArgs.put(\"{parameter.Name}\", getClient().registerCancellation({paramName}));");
                WriteLine("        }");
                continue;
            }

            if (parameter.IsOptional)
            {
                WriteLine($"        if ({paramName} != null) {{");
                WriteLine($"            reqArgs.put(\"{parameter.Name}\", AspireClient.serializeValue({paramName}));");
                WriteLine("        }");
            }
            else
            {
                WriteLine($"        reqArgs.put(\"{parameter.Name}\", AspireClient.serializeValue({paramName}));");
            }
        }

        if (hasReturn)
        {
            WriteLine($"        return ({returnType}) getClient().invokeCapability(\"{capability.CapabilityId}\", reqArgs);");
        }
        else
        {
            WriteLine($"        getClient().invokeCapability(\"{capability.CapabilityId}\", reqArgs);");
        }

        WriteLine("    }");
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

        // Determine type arguments
        string typeArgs;
        if (isDict)
        {
            var keyType = MapTypeRefToJava(returnType.KeyType, false);
            var valueType = MapTypeRefToJava(returnType.ValueType, false);
            // Use boxed types for generics
            keyType = BoxPrimitiveType(keyType);
            valueType = BoxPrimitiveType(valueType);
            typeArgs = $"<{keyType}, {valueType}>";
        }
        else
        {
            var elementType = MapTypeRefToJava(returnType.ElementType, false);
            // Use boxed types for generics
            elementType = BoxPrimitiveType(elementType);
            typeArgs = $"<{elementType}>";
        }

        var fullType = $"{wrapperType}{typeArgs}";
        var fieldName = methodName + "Field";

        // Generate Javadoc
        if (!string.IsNullOrEmpty(capability.Description))
        {
            WriteLine($"    /** {capability.Description} */");
        }

        // Generate private field and getter
        WriteLine($"    private {fullType} {fieldName};");
        WriteLine($"    public {fullType} {methodName}() {{");
        WriteLine($"        if ({fieldName} == null) {{");
        WriteLine($"            {fieldName} = new {wrapperType}<>(getHandle(), getClient(), \"{capability.CapabilityId}\");");
        WriteLine("        }");
        WriteLine($"        return {fieldName};");
        WriteLine("    }");
        WriteLine();
    }

    private static string BoxPrimitiveType(string type)
    {
        return type switch
        {
            "int" => "Integer",
            "long" => "Long",
            "double" => "Double",
            "float" => "Float",
            "boolean" => "Boolean",
            "char" => "Character",
            "byte" => "Byte",
            "short" => "Short",
            _ => type
        };
    }

    private void GenerateHandleWrapperRegistrations(
        IReadOnlyList<JavaHandleType> handleTypes,
        Dictionary<string, bool> collectionTypes)
    {
        WriteLine("// ============================================================================");
        WriteLine("// Handle wrapper registrations");
        WriteLine("// ============================================================================");
        WriteLine();
        WriteLine("/** Static initializer to register handle wrappers. */");
        WriteLine("class AspireRegistrations {");
        WriteLine("    static {");

        foreach (var handleType in handleTypes)
        {
            WriteLine($"        AspireClient.registerHandleWrapper(\"{handleType.TypeId}\", (h, c) -> new {handleType.ClassName}(h, c));");
        }

        foreach (var (typeId, isDict) in collectionTypes)
        {
            var wrapperType = isDict ? "AspireDict" : "AspireList";
            WriteLine($"        AspireClient.registerHandleWrapper(\"{typeId}\", (h, c) -> new {wrapperType}(h, c));");
        }

        WriteLine("    }");
        WriteLine();
        WriteLine("    static void ensureRegistered() {");
        WriteLine("        // Called to trigger static initializer");
        WriteLine("    }");
        WriteLine("}");
        WriteLine();
    }

    private void GenerateConnectionHelpers()
    {
        var builderClassName = _classNames.TryGetValue(AtsConstants.BuilderTypeId, out var name)
            ? name
            : "DistributedApplicationBuilder";

        WriteLine("// ============================================================================");
        WriteLine("// Connection Helpers");
        WriteLine("// ============================================================================");
        WriteLine();
        WriteLine("/** Main entry point for Aspire SDK. */");
        WriteLine("public class Aspire {");
        WriteLine("    /** Connect to the AppHost server. */");
        WriteLine("    public static AspireClient connect() throws Exception {");
        WriteLine("        AspireRegistrations.ensureRegistered();");
        WriteLine("        String socketPath = System.getenv(\"REMOTE_APP_HOST_SOCKET_PATH\");");
        WriteLine("        if (socketPath == null || socketPath.isEmpty()) {");
        WriteLine("            throw new RuntimeException(\"REMOTE_APP_HOST_SOCKET_PATH environment variable not set. Run this application using `aspire run`.\");");
        WriteLine("        }");
        WriteLine("        AspireClient client = new AspireClient(socketPath);");
        WriteLine("        client.connect();");
        WriteLine("        client.onDisconnect(() -> System.exit(1));");
        WriteLine("        return client;");
        WriteLine("    }");
        WriteLine();
        WriteLine($"    /** Create a new distributed application builder. */");
        WriteLine($"    public static {builderClassName} createBuilder(CreateBuilderOptions options) throws Exception {{");
        WriteLine("        AspireClient client = connect();");
        WriteLine("        Map<String, Object> resolvedOptions = new HashMap<>();");
        WriteLine("        if (options != null) {");
        WriteLine("            resolvedOptions.putAll(options.toMap());");
        WriteLine("        }");
        WriteLine("        if (!resolvedOptions.containsKey(\"Args\")) {");
        WriteLine("            // Note: Java doesn't have easy access to command line args from here");
        WriteLine("            resolvedOptions.put(\"Args\", new String[0]);");
        WriteLine("        }");
        WriteLine("        if (!resolvedOptions.containsKey(\"ProjectDirectory\")) {");
        WriteLine("            resolvedOptions.put(\"ProjectDirectory\", System.getProperty(\"user.dir\"));");
        WriteLine("        }");
        WriteLine("        Map<String, Object> args = new HashMap<>();");
        WriteLine("        args.put(\"options\", resolvedOptions);");
        WriteLine($"        return ({builderClassName}) client.invokeCapability(\"Aspire.Hosting/createBuilderWithOptions\", args);");
        WriteLine("    }");
        WriteLine("}");
        WriteLine();
    }

    private IReadOnlyList<JavaHandleType> BuildHandleTypes(AtsContext context)
    {
        var handleTypeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var handleType in context.HandleTypes)
        {
            // Skip ReferenceExpression and CancellationToken - they're defined in Base.java/Transport.java
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

        _classNames.Clear();
        foreach (var typeId in handleTypeIds)
        {
            _classNames[typeId] = CreateClassName(typeId);
        }

        var handleTypeMap = context.HandleTypes.ToDictionary(t => t.AtsTypeId, StringComparer.Ordinal);
        var results = new List<JavaHandleType>();
        foreach (var typeId in handleTypeIds)
        {
            var isResourceBuilder = false;
            if (handleTypeMap.TryGetValue(typeId, out var typeInfo))
            {
                isResourceBuilder = typeInfo.IsResourceBuilder;
            }

            results.Add(new JavaHandleType(typeId, _classNames[typeId], isResourceBuilder));
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

    private string MapTypeRefToJava(AtsTypeRef? typeRef, bool isOptional)
    {
        if (typeRef is null)
        {
            return "Object";
        }

        if (typeRef.TypeId == AtsConstants.ReferenceExpressionTypeId)
        {
            return "ReferenceExpression";
        }

        var baseType = typeRef.Category switch
        {
            AtsTypeCategory.Primitive => MapPrimitiveType(typeRef.TypeId, isOptional),
            AtsTypeCategory.Enum => MapEnumType(typeRef.TypeId),
            AtsTypeCategory.Handle => MapHandleType(typeRef.TypeId),
            AtsTypeCategory.Dto => MapDtoType(typeRef.TypeId),
            AtsTypeCategory.Callback => "Function<Object[], Object>",
            AtsTypeCategory.Array => $"{MapTypeRefToJava(typeRef.ElementType, false)}[]",
            AtsTypeCategory.List => typeRef.IsReadOnly
                ? $"List<{MapTypeRefToJava(typeRef.ElementType, false)}>"
                : $"AspireList<{MapTypeRefToJava(typeRef.ElementType, false)}>",
            AtsTypeCategory.Dict => typeRef.IsReadOnly
                ? $"Map<{MapTypeRefToJava(typeRef.KeyType, false)}, {MapTypeRefToJava(typeRef.ValueType, false)}>"
                : $"AspireDict<{MapTypeRefToJava(typeRef.KeyType, false)}, {MapTypeRefToJava(typeRef.ValueType, false)}>",
            AtsTypeCategory.Union => "Object",
            AtsTypeCategory.Unknown => "Object",
            _ => "Object"
        };

        return baseType;
    }

    private string MapHandleType(string typeId) =>
        _classNames.TryGetValue(typeId, out var name) ? name : "Handle";

    private string MapDtoType(string typeId) =>
        _dtoNames.TryGetValue(typeId, out var name) ? name : "Map<String, Object>";

    private string MapEnumType(string typeId) =>
        _enumNames.TryGetValue(typeId, out var name) ? name : "String";

    private static string MapPrimitiveType(string typeId, bool isOptional) => typeId switch
    {
        AtsConstants.String or AtsConstants.Char => "String",
        AtsConstants.Number => isOptional ? "Double" : "double",
        AtsConstants.Boolean => isOptional ? "Boolean" : "boolean",
        AtsConstants.Void => "void",
        AtsConstants.Any => "Object",
        AtsConstants.DateTime or AtsConstants.DateTimeOffset or
        AtsConstants.DateOnly or AtsConstants.TimeOnly => "String",
        AtsConstants.TimeSpan => isOptional ? "Double" : "double",
        AtsConstants.Guid or AtsConstants.Uri => "String",
        AtsConstants.CancellationToken => "CancellationToken",
        _ => "Object"
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

        // Skip ReferenceExpression and CancellationToken - they're defined in Base.java/Transport.java
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
        return s_javaKeywords.Contains(sanitized) ? sanitized + "_" : sanitized;
    }

    /// <summary>
    /// Converts a name to PascalCase for Java class/method names.
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
    /// Converts a name to camelCase for Java field/variable names.
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

    /// <summary>
    /// Converts a name to UPPER_SNAKE_CASE for Java enum constants.
    /// </summary>
    private static string ToUpperSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var result = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1]))
            {
                result.Append('_');
            }
            result.Append(char.ToUpperInvariant(c));
        }
        return result.ToString();
    }

    private void WriteLine(string value = "")
    {
        _writer.WriteLine(value);
    }

    private sealed record JavaHandleType(string TypeId, string ClassName, bool IsResourceBuilder);
}
