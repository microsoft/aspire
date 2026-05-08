// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Aspire.Shared.Json;
using Aspire.TypeSystem;

namespace Aspire.Hosting.CodeGeneration.Java;

internal sealed class JavaExportedValueTreeNode
{
    public Dictionary<string, JavaExportedValueTreeNode> Children { get; } = new(StringComparer.Ordinal);

    public AtsExportedValueInfo? Value { get; set; }
}

/// <summary>
/// Generates a Java SDK using the ATS (Aspire Type System) capability-based API.
/// Produces wrapper classes that proxy capabilities via JSON-RPC.
/// </summary>
internal sealed class AtsJavaCodeGenerator : ICodeGenerator
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
    private readonly Dictionary<string, List<AtsParameterInfo>> _optionsClassesToGenerate = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _capabilityOptionsClassMap = new(StringComparer.Ordinal);
    private readonly HashSet<string> _resourceBuilderHandleClasses = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string Language => "Java";

    /// <inheritdoc />
    public Dictionary<string, string> GenerateDistributedApplication(AtsContext context)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);

        AddSplitJavaSourceFiles(files, GetEmbeddedResource("Transport.java"));
        AddSplitJavaSourceFiles(files, GetEmbeddedResource("Base.java"));
        AddSplitJavaSourceFiles(files, GenerateAspireSdk(context));

        files["sources.txt"] = string.Join(
            '\n',
            files.Keys
                .Where(static key => key.EndsWith(".java", StringComparison.Ordinal))
                .OrderBy(static key => key, StringComparer.Ordinal)
                .Select(static key => $".modules/{key}")) + '\n';

        return files;
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

    private static void AddSplitJavaSourceFiles(Dictionary<string, string> files, string source)
    {
        foreach (var (fileName, content) in SplitJavaSourceFiles(source))
        {
            files.Add(fileName, content);
        }
    }

    private static Dictionary<string, string> SplitJavaSourceFiles(string source)
    {
        var packageLine = string.Empty;
        var importLines = new List<string>();
        var declarations = new Dictionary<string, string>(StringComparer.Ordinal);

        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var bodyStartIndex = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("package ", StringComparison.Ordinal))
            {
                packageLine = trimmed;
                continue;
            }

            if (string.IsNullOrEmpty(packageLine))
            {
                continue;
            }

            if (trimmed.StartsWith("import ", StringComparison.Ordinal))
            {
                importLines.Add(trimmed);
                continue;
            }

            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            bodyStartIndex = i;
            break;
        }

        List<string>? currentDeclaration = null;
        List<string>? pendingLines = [];
        string? currentTypeName = null;
        var braceDepth = 0;
        var inBlockComment = false;

        for (var i = bodyStartIndex; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (currentDeclaration is null)
            {
                if (TryGetTopLevelDeclarationName(trimmed, out var declarationName))
                {
                    currentTypeName = declarationName;
                    currentDeclaration = [];

                    if (pendingLines.Count > 0)
                    {
                        currentDeclaration.AddRange(pendingLines);
                        pendingLines.Clear();
                    }

                    currentDeclaration.Add(PromoteTopLevelDeclaration(line));
                    braceDepth = CountBraceDelta(line, ref inBlockComment);
                    continue;
                }

                if (ShouldPreserveTopLevelLine(trimmed))
                {
                    pendingLines.Add(line);
                }
                else if (pendingLines.Count > 0 && string.IsNullOrWhiteSpace(trimmed))
                {
                    pendingLines.Add(line);
                }
                else
                {
                    pendingLines.Clear();
                }

                continue;
            }

            currentDeclaration.Add(line);
            braceDepth += CountBraceDelta(line, ref inBlockComment);

            if (braceDepth == 0)
            {
                declarations.Add(
                    $"{currentTypeName}.java",
                    CreateJavaSourceFile($"{currentTypeName}.java", packageLine, importLines, currentDeclaration));

                currentDeclaration = null;
                currentTypeName = null;
                pendingLines = [];
            }
        }

        return declarations;
    }

    private static bool TryGetTopLevelDeclarationName(string trimmedLine, out string? declarationName)
    {
        declarationName = null;

        if (string.IsNullOrWhiteSpace(trimmedLine))
        {
            return false;
        }

        if (ShouldPreserveTopLevelLine(trimmedLine) || trimmedLine.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        var declarationLine = trimmedLine;
        while (true)
        {
            var updated = declarationLine switch
            {
                _ when declarationLine.StartsWith("public ", StringComparison.Ordinal) => declarationLine["public ".Length..].TrimStart(),
                _ when declarationLine.StartsWith("final ", StringComparison.Ordinal) => declarationLine["final ".Length..].TrimStart(),
                _ when declarationLine.StartsWith("abstract ", StringComparison.Ordinal) => declarationLine["abstract ".Length..].TrimStart(),
                _ when declarationLine.StartsWith("static ", StringComparison.Ordinal) => declarationLine["static ".Length..].TrimStart(),
                _ => declarationLine
            };

            if (ReferenceEquals(updated, declarationLine) || updated == declarationLine)
            {
                break;
            }

            declarationLine = updated;
        }

        foreach (var kind in new[] { "class", "interface", "enum", "record" })
        {
            var kindPrefix = kind + " ";
            if (!declarationLine.StartsWith(kindPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            declarationName = declarationLine[kindPrefix.Length..]
                .Split([' ', '\t', '<'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
            return true;
        }

        return false;
    }

    private static bool ShouldPreserveTopLevelLine(string trimmedLine) =>
        trimmedLine.StartsWith("/**", StringComparison.Ordinal)
        || trimmedLine.StartsWith("/*", StringComparison.Ordinal)
        || trimmedLine.StartsWith("*", StringComparison.Ordinal)
        || trimmedLine.StartsWith("*/", StringComparison.Ordinal)
        || trimmedLine.StartsWith("@", StringComparison.Ordinal);

    private static string PromoteTopLevelDeclaration(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("public ", StringComparison.Ordinal))
        {
            return line;
        }

        var leadingWhitespaceLength = line.Length - trimmed.Length;
        var leadingWhitespace = line[..leadingWhitespaceLength];

        foreach (var declarationPrefix in new[]
        {
            "final class ",
            "abstract class ",
            "static class ",
            "class ",
            "interface ",
            "enum ",
            "record "
        })
        {
            if (trimmed.StartsWith(declarationPrefix, StringComparison.Ordinal))
            {
                return $"{leadingWhitespace}public {trimmed}";
            }
        }

        return line;
    }

    private static int CountBraceDelta(string line, ref bool inBlockComment)
    {
        var delta = 0;
        var inString = false;
        var inChar = false;
        var escaped = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            var next = i + 1 < line.Length ? line[i + 1] : '\0';

            if (inBlockComment)
            {
                if (ch == '*' && next == '/')
                {
                    inBlockComment = false;
                    i++;
                }
                continue;
            }

            if (!inString && !inChar)
            {
                if (ch == '/' && next == '/')
                {
                    break;
                }

                if (ch == '/' && next == '*')
                {
                    inBlockComment = true;
                    i++;
                    continue;
                }
            }

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (inString)
            {
                if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (inChar)
            {
                if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '\'')
                {
                    inChar = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }

            if (ch == '\'')
            {
                inChar = true;
                continue;
            }

            if (ch == '{')
            {
                delta++;
            }
            else if (ch == '}')
            {
                delta--;
            }
        }

        return delta;
    }

    private static string CreateJavaSourceFile(string fileName, string packageLine, List<string> importLines, List<string> declarationLines)
    {
        var builder = new StringBuilder();
        builder.Append("// ");
        builder.Append(fileName);
        builder.AppendLine(" - GENERATED CODE - DO NOT EDIT");
        builder.AppendLine();
        builder.AppendLine(packageLine);
        builder.AppendLine();

        foreach (var importLine in importLines)
        {
            builder.AppendLine(importLine);
        }

        if (importLines.Count > 0)
        {
            builder.AppendLine();
        }

        foreach (var line in declarationLines)
        {
            builder.AppendLine(line);
        }

        return builder.ToString();
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

        _optionsClassesToGenerate.Clear();
        _capabilityOptionsClassMap.Clear();
        CollectOptionsClasses(capabilities);

        var handleTypes = BuildHandleTypes(context);
        var capabilitiesByTarget = GroupCapabilitiesByTarget(capabilities);
        var collectionTypes = CollectListAndDictTypeIds(capabilities);

        WriteHeader();
        GenerateEnumTypes(enumTypes);
        GenerateDtoTypes(dtoTypes);
        GenerateExportedValues(exportedValues, dtoTypes.ToDictionary(dto => dto.TypeId, StringComparer.Ordinal));
        GenerateOptionTypes();
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
            WriteLine($"enum {enumName} implements WireValueEnum {{");
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
            WriteLine($"class {dtoName} implements JsonSerializable {{");
            
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
            WriteLine($"final class {name} {{");
            WriteLine($"    private {name}() {{ }}");
            WriteLine();
            WriteJavaExportedValueChildren(node, dtoTypesById, indentLevel: 1);
            WriteLine("}");
            WriteLine();
        }
    }

    private void WriteJavaExportedValueChildren(
        JavaExportedValueTreeNode node,
        IReadOnlyDictionary<string, AtsDtoTypeInfo> dtoTypesById,
        int indentLevel)
    {
        var indent = new string(' ', indentLevel * 4);

        foreach (var (name, child) in node.Children.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (child.Value is { } valueInfo)
            {
                if (!string.IsNullOrWhiteSpace(valueInfo.Description))
                {
                    WriteLine($"{indent}/** {valueInfo.Description} */");
                }

                var javaType = MapTypeRefToJava(valueInfo.Type, isOptional: false, useBoxedTypes: true);
                var expression = RenderJavaExportedValue(valueInfo.Value, valueInfo.Type, dtoTypesById);
                WriteLine($"{indent}public static final {javaType} {name} = {expression};");
            }
            else
            {
                WriteLine($"{indent}public static final class {name} {{");
                WriteLine($"{indent}    private {name}() {{ }}");
                WriteLine();
                WriteJavaExportedValueChildren(child, dtoTypesById, indentLevel + 1);
                WriteLine($"{indent}}}");
            }

            WriteLine();
        }
    }

    private string RenderJavaExportedValue(
        JsonNode? value,
        AtsTypeRef typeRef,
        IReadOnlyDictionary<string, AtsDtoTypeInfo> dtoTypesById)
    {
        if (value is null)
        {
            return "null";
        }

        return typeRef.Category switch
        {
            AtsTypeCategory.Primitive => value.ToRelaxedJsonString(),
            AtsTypeCategory.Enum => $"{MapTypeRefToJava(typeRef, false)}.fromValue({value.ToRelaxedJsonString()})",
            AtsTypeCategory.Dto when value is JsonObject obj && dtoTypesById.TryGetValue(typeRef.TypeId, out var dtoInfo)
                => RenderJavaDtoValue(obj, dtoInfo, dtoTypesById),
            AtsTypeCategory.Array when value is JsonArray arr
                => $"new {MapTypeRefToJava(typeRef.ElementType, false)}[] {{ {string.Join(", ", arr.Select(item => RenderJavaExportedValue(item, typeRef.ElementType!, dtoTypesById)))} }}",
            AtsTypeCategory.List when value is JsonArray arr
                => $"({MapTypeRefToJava(typeRef, false, useBoxedTypes: true)})(Object)new ArrayList<>(List.of({string.Join(", ", arr.Select(item => RenderJavaExportedValue(item, typeRef.ElementType!, dtoTypesById)))}))",
            AtsTypeCategory.Dict when value is JsonObject obj
                => $"({MapTypeRefToJava(typeRef, false, useBoxedTypes: true)})(Object)new HashMap<>(Map.ofEntries({string.Join(", ", obj.Select(pair => $"Map.entry({AtsJsonCodeWriter.ToRelaxedJsonString(pair.Key)}, {RenderJavaExportedValue(pair.Value, typeRef.ValueType!, dtoTypesById)})"))}))",
            _ => value.ToRelaxedJsonString()
        };
    }

    private string RenderJavaDtoValue(
        JsonObject value,
        AtsDtoTypeInfo dtoInfo,
        IReadOnlyDictionary<string, AtsDtoTypeInfo> dtoTypesById)
    {
        var sb = new StringBuilder();
        sb.Append("new ");
        sb.Append(_dtoNames[dtoInfo.TypeId]);
        sb.Append("() {{ ");

        foreach (var property in dtoInfo.Properties)
        {
            if (!value.TryGetPropertyValue(property.Name, out var propertyValue))
            {
                continue;
            }

            sb.Append("set");
            sb.Append(ToPascalCase(property.Name));
            sb.Append('(');
            sb.Append(RenderJavaExportedValue(propertyValue, property.Type, dtoTypesById));
            sb.Append("); ");
        }

        sb.Append("}}");
        return sb.ToString();
    }

    private static JavaExportedValueTreeNode BuildExportedValueTree(IReadOnlyList<AtsExportedValueInfo> exportedValues)
    {
        var root = new JavaExportedValueTreeNode();

        foreach (var exportedValue in exportedValues)
        {
            var current = root;
            foreach (var segment in exportedValue.PathSegments)
            {
                if (!current.Children.TryGetValue(segment, out var child))
                {
                    child = new JavaExportedValueTreeNode();
                    current.Children[segment] = child;
                }

                current = child;
            }

            current.Value = exportedValue;
        }

        return root;
    }

    private void CollectOptionsClasses(IReadOnlyList<AtsCapabilityInfo> capabilities)
    {
        foreach (var capability in capabilities)
        {
            var targetParamName = capability.TargetParameterName ?? "builder";
            var parameters = capability.Parameters
                .Where(p => !string.Equals(p.Name, targetParamName, StringComparison.Ordinal))
                .ToList();
            var (_, optionalParameters) = SeparateParameters(parameters);
            if (optionalParameters.Count > 1)
            {
                RegisterOptionsClass(capability.CapabilityId, capability.MethodName, optionalParameters);
            }
        }
    }

    private void RegisterOptionsClass(string capabilityId, string methodName, List<AtsParameterInfo> optionalParameters)
    {
        var baseClassName = GetOptionsClassName(methodName);
        if (_optionsClassesToGenerate.TryGetValue(baseClassName, out var existingParameters))
        {
            if (AreOptionsCompatible(existingParameters, optionalParameters))
            {
                _capabilityOptionsClassMap[capabilityId] = baseClassName;
                return;
            }

            for (var suffix = 1; ; suffix++)
            {
                var suffixedName = GetOptionsClassName($"{methodName}{suffix}");
                if (!_optionsClassesToGenerate.TryGetValue(suffixedName, out var suffixedParameters))
                {
                    _optionsClassesToGenerate[suffixedName] = [.. optionalParameters];
                    _capabilityOptionsClassMap[capabilityId] = suffixedName;
                    return;
                }

                if (AreOptionsCompatible(suffixedParameters, optionalParameters))
                {
                    _capabilityOptionsClassMap[capabilityId] = suffixedName;
                    return;
                }
            }
        }

        _optionsClassesToGenerate[baseClassName] = [.. optionalParameters];
        _capabilityOptionsClassMap[capabilityId] = baseClassName;
    }

    private static bool AreOptionsCompatible(List<AtsParameterInfo> existing, List<AtsParameterInfo> candidate)
    {
        if (existing.Count != candidate.Count)
        {
            return false;
        }

        for (var i = 0; i < existing.Count; i++)
        {
            if (!AreParameterTypesEqual(existing[i], candidate[i]) || !string.Equals(existing[i].Name, candidate[i].Name, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AreParameterTypesEqual(AtsParameterInfo left, AtsParameterInfo right)
    {
        if (!string.Equals(left.Type?.TypeId, right.Type?.TypeId, StringComparison.Ordinal))
        {
            return false;
        }

        if (left.IsCallback != right.IsCallback)
        {
            return false;
        }

        if (!left.IsCallback)
        {
            return true;
        }

        var leftCallbackParameters = left.CallbackParameters ?? [];
        var rightCallbackParameters = right.CallbackParameters ?? [];
        if (leftCallbackParameters.Count != rightCallbackParameters.Count)
        {
            return false;
        }

        for (var i = 0; i < leftCallbackParameters.Count; i++)
        {
            if (!string.Equals(leftCallbackParameters[i].Type.TypeId, rightCallbackParameters[i].Type.TypeId, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return string.Equals(left.CallbackReturnType?.TypeId, right.CallbackReturnType?.TypeId, StringComparison.Ordinal);
    }

    private void GenerateOptionTypes()
    {
        if (_optionsClassesToGenerate.Count == 0)
        {
            return;
        }

        WriteLine("// ============================================================================");
        WriteLine("// Options Types");
        WriteLine("// ============================================================================");
        WriteLine();

        foreach (var (className, optionalParameters) in _optionsClassesToGenerate.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            WriteLine($"/** Options for {className[..^"Options".Length]}. */");
            WriteLine($"final class {className} {{");
            foreach (var parameter in optionalParameters)
            {
                var parameterName = ToCamelCase(parameter.Name);
                WriteLine($"    private {MapParameterToJava(parameter)} {parameterName};");
            }
            WriteLine();

            foreach (var parameter in optionalParameters)
            {
                var parameterName = ToCamelCase(parameter.Name);
                var parameterType = MapParameterToJava(parameter);
                WriteLine($"    public {parameterType} {GetOptionGetterName(parameter)}() {{ return {parameterName}; }}");
                WriteLine($"    public {className} {parameterName}({parameterType} value) {{");
                WriteLine($"        this.{parameterName} = value;");
                WriteLine("        return this;");
                WriteLine("    }");
                WriteLine();
            }

            WriteLine("}");
            WriteLine();
        }
    }

    private static (List<AtsParameterInfo> Required, List<AtsParameterInfo> Optional) SeparateParameters(IEnumerable<AtsParameterInfo> parameters)
    {
        var required = new List<AtsParameterInfo>();
        var optional = new List<AtsParameterInfo>();

        foreach (var parameter in parameters)
        {
            if (parameter.IsOptional || parameter.IsNullable)
            {
                optional.Add(parameter);
            }
            else
            {
                required.Add(parameter);
            }
        }

        return (required, optional);
    }

    private string? ResolveOptionsClassName(AtsCapabilityInfo capability) =>
        _capabilityOptionsClassMap.TryGetValue(capability.CapabilityId, out var className) ? className : null;

    private static string GetOptionsClassName(string methodName) =>
        SanitizeIdentifier($"{ToPascalCase(methodName)}Options");

    private static string AppendArgumentList(IEnumerable<string> arguments, string trailingArgument)
    {
        var argumentList = arguments.ToList();
        argumentList.Add(trailingArgument);
        return string.Join(", ", argumentList);
    }

    private List<JavaMethodParameter> CreateMethodParameters(IEnumerable<AtsParameterInfo> parameters)
    {
        var result = new List<JavaMethodParameter>();

        foreach (var parameter in parameters)
        {
            var (resourceWrapperType, resourceWrapperParameterType) = GetResourceBuilderWrapperType(parameter);
            result.Add(new JavaMethodParameter(
                MapParameterToJava(parameter),
                ToCamelCase(parameter.Name),
                resourceWrapperType,
                resourceWrapperParameterType));
        }

        return result;
    }

    private (string? ResourceWrapperType, string? ResourceWrapperParameterType) GetResourceBuilderWrapperType(AtsParameterInfo parameter)
    {
        if (parameter.IsCallback || parameter.Type?.Category != AtsTypeCategory.Handle)
        {
            return (null, null);
        }

        var wrapperType = MapInputTypeToJava(parameter.Type, parameter.IsOptional || parameter.IsNullable);
        return GetResourceBuilderWrapperType(wrapperType);
    }

    private (string? ResourceWrapperType, string? ResourceWrapperParameterType) GetResourceBuilderWrapperType(string wrapperType)
    {
        if (!wrapperType.StartsWith("I", StringComparison.Ordinal))
        {
            return (null, null);
        }

        return _resourceBuilderHandleClasses.Contains(wrapperType)
            ? (wrapperType, "ResourceBuilderBase")
            : (wrapperType, "HandleWrapperBase");
    }

    private void GenerateResourceBuilderOverloads(
        string returnType,
        string methodName,
        IReadOnlyList<JavaMethodParameter> parameters,
        bool hasReturn)
    {
        if (parameters.Count == 0)
        {
            return;
        }

        var convertibleParameters = parameters
            .Select((parameter, index) => new { Parameter = parameter, Index = index })
            .Where(x => x.Parameter.ResourceWrapperType is not null)
            .ToList();

        if (convertibleParameters.Count == 0)
        {
            return;
        }

        var seenSignatures = new HashSet<string>(StringComparer.Ordinal);
        var combinationCount = 1 << convertibleParameters.Count;

        for (var mask = 1; mask < combinationCount; mask++)
        {
            var selectedIndexes = new HashSet<int>(
                convertibleParameters
                    .Where((_, bitIndex) => (mask & (1 << bitIndex)) != 0)
                    .Select(x => x.Index));

            var overloadParameters = new List<string>(parameters.Count);
            var callArguments = new List<string>(parameters.Count);

            for (var i = 0; i < parameters.Count; i++)
            {
                var parameter = parameters[i];
                if (selectedIndexes.Contains(i))
                {
                    overloadParameters.Add($"{parameter.ResourceWrapperParameterType} {parameter.Name}");
                    callArguments.Add($"new {parameter.ResourceWrapperType}({parameter.Name}.getHandle(), {parameter.Name}.getClient())");
                }
                else
                {
                    overloadParameters.Add($"{parameter.Type} {parameter.Name}");
                    callArguments.Add(parameter.Name);
                }
            }

            var signature = string.Join(", ", overloadParameters);
            if (!seenSignatures.Add(signature))
            {
                continue;
            }

            WriteLine($"    public {returnType} {methodName}({signature}) {{");
            if (hasReturn)
            {
                WriteLine($"        return {methodName}({string.Join(", ", callArguments)});");
            }
            else
            {
                WriteLine($"        {methodName}({string.Join(", ", callArguments)});");
            }
            WriteLine("    }");
            WriteLine();
        }
    }

    private static string GetOptionGetterName(AtsParameterInfo parameter)
    {
        var parameterName = ToCamelCase(parameter.Name);
        if (parameterName.StartsWith("is", StringComparison.Ordinal) &&
            parameterName.Length > 2 &&
            char.IsUpper(parameterName[2]))
        {
            return parameterName;
        }

        return $"get{ToPascalCase(parameterName)}";
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
            WriteLine($"/** Wrapper for {handleType.TypeId}. */");
            WriteLine($"class {handleType.ClassName} extends {handleType.BaseClassName} {{");
            WriteLine($"    {handleType.ClassName}(Handle handle, AspireClient client) {{");
            WriteLine("        super(handle, client);");
            WriteLine("    }");
            WriteLine();

            if (capabilitiesByTarget.TryGetValue(handleType.TypeId, out var methods))
            {
                foreach (var method in methods)
                {
                    GenerateCapabilityMethod(handleType, method);
                }
            }

            if (string.Equals(handleType.ClassName, "DistributedApplication", StringComparison.Ordinal))
            {
                GenerateDistributedApplicationBuilderHelpers();
            }

            WriteLine("}");
            WriteLine();
        }
    }

    private void GenerateDistributedApplicationBuilderHelpers()
    {
        var builderClassName = _classNames.TryGetValue(AtsConstants.BuilderTypeId, out var name)
            ? name
            : "DistributedApplicationBuilder";

        WriteLine("    /** Create a new distributed application builder. */");
        WriteLine($"    public static {builderClassName} CreateBuilder() throws Exception {{");
        WriteLine("        return CreateBuilder((String[]) null);");
        WriteLine("    }");
        WriteLine();
        WriteLine("    /** Create a new distributed application builder. */");
        WriteLine($"    public static {builderClassName} CreateBuilder(String[] args) throws Exception {{");
        WriteLine("        CreateBuilderOptions options = new CreateBuilderOptions();");
        WriteLine("        if (args != null) {");
        WriteLine("            options.setArgs(args);");
        WriteLine("        }");
        WriteLine("        return CreateBuilder(options);");
        WriteLine("    }");
        WriteLine();
        WriteLine("    /** Create a new distributed application builder. */");
        WriteLine($"    public static {builderClassName} CreateBuilder(CreateBuilderOptions options) throws Exception {{");
        WriteLine("        return Aspire.createBuilder(options);");
        WriteLine("    }");
        WriteLine();
    }

    private void GenerateCapabilityMethod(JavaHandleType handleType, AtsCapabilityInfo capability)
    {
        var targetParamName = capability.TargetParameterName ?? "builder";
        var methodName = ToCamelCase(capability.MethodName);
        var parameters = capability.Parameters
            .Where(p => !string.Equals(p.Name, targetParamName, StringComparison.Ordinal))
            .ToList();
        var (requiredParameters, optionalParameters) = SeparateParameters(parameters);
        var optionsClassName = ResolveOptionsClassName(capability);
        var useOptionsClass = optionsClassName is not null;
        var returnInfo = GetMethodReturnInfo(handleType, capability);

        if (parameters.Count == 0 && IsListOrDictPropertyGetter(capability.ReturnType))
        {
            GenerateListOrDictProperty(capability, methodName);
            return;
        }

        if (useOptionsClass)
        {
            var implementationMethodName = $"{methodName}Impl";
            GenerateUnionOverloadsWithOptions(returnInfo, methodName, requiredParameters, optionsClassName!);
            GenerateOptionsOverloads(capability, returnInfo, methodName, implementationMethodName, requiredParameters, optionalParameters, optionsClassName!);
            GenerateCapabilityMethodImplementation(capability, returnInfo, implementationMethodName, targetParamName, parameters, isPublic: false);
        }
        else
        {
            GenerateUnionOverloads(returnInfo, methodName, parameters);
            GenerateOptionalOverloads(returnInfo, methodName, parameters);
            GenerateCapabilityMethodImplementation(capability, returnInfo, methodName, targetParamName, parameters, isPublic: true);
        }
    }

    private void GenerateUnionOverloads(JavaCapabilityReturnInfo returnInfo, string methodName, List<AtsParameterInfo> parameters)
    {
        var unionParameters = parameters.Where(p => IsUnionType(p.Type)).ToList();
        if (unionParameters.Count == 0)
        {
            return;
        }

        if (unionParameters.Count == 1)
        {
            GenerateSingleUnionOverloads(returnInfo, methodName, parameters, unionParameters[0]);
            return;
        }

        // Multiple union parameters: generate overloads for each combination of concrete types.
        // E.g. runAsExisting(AspireUnion name, AspireUnion resourceGroup) where both are string|ParameterResource
        // generates 4 overloads: (String,String), (String,ParameterResource), (ParameterResource,String), (ParameterResource,ParameterResource)
        GenerateMultiUnionOverloads(returnInfo, methodName, parameters, unionParameters);
    }

    private void GenerateSingleUnionOverloads(JavaCapabilityReturnInfo returnInfo, string methodName, List<AtsParameterInfo> parameters, AtsParameterInfo unionParameter)
    {
        var unionTypes = unionParameter.Type?.UnionTypes;
        if (unionTypes is null || unionTypes.Count == 0)
        {
            return;
        }

        var unionParamName = ToCamelCase(unionParameter.Name);

        foreach (var unionType in unionTypes
            .Select(type => new { Type = type, JavaType = MapInputTypeToJava(type, unionParameter.IsOptional || unionParameter.IsNullable) })
            .DistinctBy(x => x.JavaType, StringComparer.Ordinal)
            .Select(x => x.Type))
        {
            var overloadParameters = new StringBuilder();
            foreach (var parameter in parameters)
            {
                if (overloadParameters.Length > 0)
                {
                    overloadParameters.Append(", ");
                }

                var parameterType = ReferenceEquals(parameter, unionParameter)
                    ? MapInputTypeToJava(unionType, unionParameter.IsOptional || unionParameter.IsNullable)
                    : MapParameterToJava(parameter);
                overloadParameters.Append(CultureInfo.InvariantCulture, $"{parameterType} {ToCamelCase(parameter.Name)}");
            }

            WriteLine($"    public {returnInfo.ReturnType} {methodName}({overloadParameters}) {{");
            var callArguments = string.Join(", ", parameters.Select(parameter =>
                ReferenceEquals(parameter, unionParameter)
                    ? $"AspireUnion.of({unionParamName})"
                    : ToCamelCase(parameter.Name)));
            if (returnInfo.HasReturn)
            {
                WriteLine($"        return {methodName}({callArguments});");
            }
            else
            {
                WriteLine($"        {methodName}({callArguments});");
            }
            WriteLine("    }");
            WriteLine();

            GenerateResourceBuilderOverloads(
                returnInfo.ReturnType,
                methodName,
                CreateUnionMethodParameters(parameters, unionParameter, unionType),
                returnInfo.HasReturn);
        }
    }

    private void GenerateMultiUnionOverloads(JavaCapabilityReturnInfo returnInfo, string methodName, List<AtsParameterInfo> parameters, List<AtsParameterInfo> unionParameters)
    {
        // Build the list of distinct Java types for each union parameter.
        var unionTypesByParam = unionParameters
            .Select(up => up.Type?.UnionTypes?
                .Select(t => new { Type = t, JavaType = MapInputTypeToJava(t, up.IsOptional || up.IsNullable) })
                .DistinctBy(x => x.JavaType, StringComparer.Ordinal)
                .Select(x => x.Type)
                .ToList() ?? [])
            .ToList();

        // Generate the Cartesian product of all union type combinations.
        var combinations = CartesianProduct(unionTypesByParam);

        foreach (var combination in combinations)
        {
            // Build parameter list for this overload.
            var overloadParameters = new StringBuilder();
            foreach (var parameter in parameters)
            {
                if (overloadParameters.Length > 0)
                {
                    overloadParameters.Append(", ");
                }

                var unionIndex = unionParameters.IndexOf(parameter);
                var parameterType = unionIndex >= 0
                    ? MapInputTypeToJava(combination[unionIndex], parameter.IsOptional || parameter.IsNullable)
                    : MapParameterToJava(parameter);
                overloadParameters.Append(CultureInfo.InvariantCulture, $"{parameterType} {ToCamelCase(parameter.Name)}");
            }

            // Build call arguments, wrapping union parameters with AspireUnion.of().
            var callArguments = string.Join(", ", parameters.Select(parameter =>
                unionParameters.Contains(parameter)
                    ? $"AspireUnion.of({ToCamelCase(parameter.Name)})"
                    : ToCamelCase(parameter.Name)));

            WriteLine($"    public {returnInfo.ReturnType} {methodName}({overloadParameters}) {{");
            if (returnInfo.HasReturn)
            {
                WriteLine($"        return {methodName}({callArguments});");
            }
            else
            {
                WriteLine($"        {methodName}({callArguments});");
            }
            WriteLine("    }");
            WriteLine();
        }
    }

    private static List<List<AtsTypeRef>> CartesianProduct(List<List<AtsTypeRef>> lists)
    {
        var result = new List<List<AtsTypeRef>> { new() };
        foreach (var list in lists)
        {
            var temp = new List<List<AtsTypeRef>>();
            foreach (var existing in result)
            {
                foreach (var item in list)
                {
                    var combined = new List<AtsTypeRef>(existing) { item };
                    temp.Add(combined);
                }
            }
            result = temp;
        }
        return result;
    }

    private void GenerateOptionalOverloads(JavaCapabilityReturnInfo returnInfo, string methodName, List<AtsParameterInfo> parameters)
    {
        var trailingOptionalCount = parameters.AsEnumerable().Reverse().TakeWhile(IsOmittableParameter).Count();
        if (trailingOptionalCount == 0)
        {
            return;
        }

        for (var omitCount = trailingOptionalCount; omitCount >= 1; omitCount--)
        {
            var visibleParameters = parameters.Take(parameters.Count - omitCount).ToList();
            var parameterList = string.Join(", ", visibleParameters.Select(parameter => $"{MapParameterToJava(parameter)} {ToCamelCase(parameter.Name)}"));
            WriteLine($"    public {returnInfo.ReturnType} {methodName}({parameterList}) {{");

            var callArguments = new List<string>(parameters.Count);
            foreach (var parameter in parameters)
            {
                if (visibleParameters.Contains(parameter))
                {
                    callArguments.Add(ToCamelCase(parameter.Name));
                }
                else
                {
                    callArguments.Add(GetOmittedOptionalArgument(parameter));
                }
            }

            if (returnInfo.HasReturn)
            {
                WriteLine($"        return {methodName}({string.Join(", ", callArguments)});");
            }
            else
            {
                WriteLine($"        {methodName}({string.Join(", ", callArguments)});");
            }
            WriteLine("    }");
            WriteLine();

            GenerateResourceBuilderOverloads(
                returnInfo.ReturnType,
                methodName,
                CreateMethodParameters(visibleParameters),
                returnInfo.HasReturn);
        }
    }

    private static string GetOmittedOptionalArgument(AtsParameterInfo parameter)
    {
        return IsUnionType(parameter.Type) ? "(AspireUnion) null" : "null";
    }

    private void GenerateUnionOverloadsWithOptions(
        JavaCapabilityReturnInfo returnInfo,
        string methodName,
        List<AtsParameterInfo> requiredParameters,
        string optionsClassName)
    {
        var unionParameters = requiredParameters.Where(p => IsUnionType(p.Type)).ToList();
        if (unionParameters.Count != 1)
        {
            return;
        }

        var unionParameter = unionParameters[0];
        var unionTypes = unionParameter.Type?.UnionTypes;
        if (unionTypes is null || unionTypes.Count == 0)
        {
            return;
        }

        var unionParamName = ToCamelCase(unionParameter.Name);

        foreach (var unionType in unionTypes
            .Select(type => new { Type = type, JavaType = MapInputTypeToJava(type, unionParameter.IsOptional || unionParameter.IsNullable) })
            .DistinctBy(x => x.JavaType, StringComparer.Ordinal)
            .Select(x => x.Type))
        {
            var overloadParameters = new StringBuilder();
            foreach (var parameter in requiredParameters)
            {
                if (overloadParameters.Length > 0)
                {
                    overloadParameters.Append(", ");
                }

                var parameterType = ReferenceEquals(parameter, unionParameter)
                    ? MapInputTypeToJava(unionType, unionParameter.IsOptional || unionParameter.IsNullable)
                    : MapParameterToJava(parameter);
                overloadParameters.Append(CultureInfo.InvariantCulture, $"{parameterType} {ToCamelCase(parameter.Name)}");
            }

            if (overloadParameters.Length > 0)
            {
                overloadParameters.Append(", ");
            }
            overloadParameters.Append(CultureInfo.InvariantCulture, $"{optionsClassName} options");

            WriteLine($"    public {returnInfo.ReturnType} {methodName}({overloadParameters}) {{");
            var callArguments = string.Join(", ", requiredParameters.Select(parameter =>
                ReferenceEquals(parameter, unionParameter)
                    ? $"AspireUnion.of({unionParamName})"
                    : ToCamelCase(parameter.Name)));
            if (returnInfo.HasReturn)
            {
                WriteLine($"        return {methodName}({callArguments}, options);");
            }
            else
            {
                WriteLine($"        {methodName}({callArguments}, options);");
            }
            WriteLine("    }");
            WriteLine();

            var bridgeParameters = CreateUnionMethodParameters(requiredParameters, unionParameter, unionType);
            bridgeParameters.Add(new JavaMethodParameter(optionsClassName, "options"));
            GenerateResourceBuilderOverloads(
                returnInfo.ReturnType,
                methodName,
                bridgeParameters,
                returnInfo.HasReturn);

            WriteLine($"    public {returnInfo.ReturnType} {methodName}({string.Join(", ", requiredParameters.Select(parameter => ReferenceEquals(parameter, unionParameter) ? $"{MapInputTypeToJava(unionType, unionParameter.IsOptional || unionParameter.IsNullable)} {ToCamelCase(parameter.Name)}" : $"{MapParameterToJava(parameter)} {ToCamelCase(parameter.Name)}"))}) {{");
            if (returnInfo.HasReturn)
            {
                WriteLine($"        return {methodName}({callArguments});");
            }
            else
            {
                WriteLine($"        {methodName}({callArguments});");
            }
            WriteLine("    }");
            WriteLine();
        }
    }

    private List<JavaMethodParameter> CreateUnionMethodParameters(
        List<AtsParameterInfo> parameters,
        AtsParameterInfo unionParameter,
        AtsTypeRef unionType)
    {
        var result = new List<JavaMethodParameter>(parameters.Count);

        foreach (var parameter in parameters)
        {
            var parameterName = ToCamelCase(parameter.Name);

            if (!ReferenceEquals(parameter, unionParameter))
            {
                var (parameterResourceWrapperType, parameterResourceWrapperParameterType) = GetResourceBuilderWrapperType(parameter);
                result.Add(new JavaMethodParameter(
                    MapParameterToJava(parameter),
                    parameterName,
                    parameterResourceWrapperType,
                    parameterResourceWrapperParameterType));
                continue;
            }

            var parameterType = MapInputTypeToJava(unionType, unionParameter.IsOptional || unionParameter.IsNullable);
            var (resourceWrapperType, resourceWrapperParameterType) = GetResourceBuilderWrapperType(parameterType);
            result.Add(new JavaMethodParameter(
                parameterType,
                parameterName,
                resourceWrapperType,
                resourceWrapperParameterType));
        }

        return result;
    }

    private void GenerateOptionsOverloads(
        AtsCapabilityInfo capability,
        JavaCapabilityReturnInfo returnInfo,
        string methodName,
        string implementationMethodName,
        List<AtsParameterInfo> requiredParameters,
        List<AtsParameterInfo> optionalParameters,
        string optionsClassName)
    {
        var requiredParameterList = string.Join(", ", requiredParameters.Select(parameter => $"{MapParameterToJava(parameter)} {ToCamelCase(parameter.Name)}"));
        var publicParameterList = string.IsNullOrEmpty(requiredParameterList)
            ? $"{optionsClassName} options"
            : $"{requiredParameterList}, {optionsClassName} options";

        if (!string.IsNullOrEmpty(capability.Description))
        {
            WriteLine($"    /** {capability.Description} */");
        }

        WriteLine($"    public {returnInfo.ReturnType} {methodName}({publicParameterList}) {{");
        foreach (var parameter in optionalParameters)
        {
            var paramName = ToCamelCase(parameter.Name);
            WriteLine($"        var {paramName} = options == null ? null : options.{GetOptionGetterName(parameter)}();");
        }

        var implementationArguments = requiredParameters
            .Select(parameter => ToCamelCase(parameter.Name))
            .Concat(optionalParameters.Select(parameter => ToCamelCase(parameter.Name)))
            .ToList();

        if (returnInfo.HasReturn)
        {
            WriteLine($"        return {implementationMethodName}({string.Join(", ", implementationArguments)});");
        }
        else
        {
            WriteLine($"        {implementationMethodName}({string.Join(", ", implementationArguments)});");
        }
        WriteLine("    }");
        WriteLine();

        var optionsParameters = CreateMethodParameters(requiredParameters);
        optionsParameters.Add(new JavaMethodParameter(optionsClassName, "options"));
        GenerateResourceBuilderOverloads(
            returnInfo.ReturnType,
            methodName,
            optionsParameters,
            returnInfo.HasReturn);

        WriteLine($"    public {returnInfo.ReturnType} {methodName}({requiredParameterList}) {{");
        if (returnInfo.HasReturn)
        {
            WriteLine($"        return {methodName}({AppendArgumentList(requiredParameters.Select(parameter => ToCamelCase(parameter.Name)), "null")});");
        }
        else
        {
            WriteLine($"        {methodName}({AppendArgumentList(requiredParameters.Select(parameter => ToCamelCase(parameter.Name)), "null")});");
        }
        WriteLine("    }");
        WriteLine();

        GenerateResourceBuilderOverloads(
            returnInfo.ReturnType,
            methodName,
            CreateMethodParameters(requiredParameters),
            returnInfo.HasReturn);
    }

    private void GenerateCapabilityMethodImplementation(AtsCapabilityInfo capability, JavaCapabilityReturnInfo returnInfo, string methodName, string targetParamName, List<AtsParameterInfo> parameters, bool isPublic)
    {
        var paramList = new StringBuilder();
        foreach (var parameter in parameters)
        {
            if (paramList.Length > 0)
            {
                paramList.Append(", ");
            }
            paramList.Append(CultureInfo.InvariantCulture, $"{MapParameterToJava(parameter)} {ToCamelCase(parameter.Name)}");
        }

        if (!string.IsNullOrEmpty(capability.Description))
        {
            WriteLine($"    /** {capability.Description} */");
        }

        var accessibility = isPublic ? "public" : "private";
        WriteLine($"    {accessibility} {returnInfo.ReturnType} {methodName}({paramList}) {{");
        WriteLine("        Map<String, Object> reqArgs = new HashMap<>();");
        WriteLine($"        reqArgs.put(\"{targetParamName}\", AspireClient.serializeValue(getHandle()));");

        foreach (var parameter in parameters)
        {
            var paramName = ToCamelCase(parameter.Name);
            if (parameter.IsCallback)
            {
                GenerateCallbackRegistration(parameter);
                WriteLine($"        if ({paramName}Id != null) {{");
                WriteLine($"            reqArgs.put(\"{parameter.Name}\", {paramName}Id);");
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

            if (IsOmittableParameter(parameter))
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

        if (returnInfo.ReturnsCurrentBuilder)
        {
            WriteLine($"        getClient().invokeCapability(\"{capability.CapabilityId}\", reqArgs);");
            WriteLine("        return this;");
        }
        else if (returnInfo.HasReturn)
        {
            if (IsUnionType(capability.ReturnType))
            {
                WriteLine($"        return AspireUnion.of(getClient().invokeCapability(\"{capability.CapabilityId}\", reqArgs));");
            }
            else
            {
                WriteLine($"        return ({returnInfo.ReturnType}) getClient().invokeCapability(\"{capability.CapabilityId}\", reqArgs);");
            }
        }
        else
        {
            WriteLine($"        getClient().invokeCapability(\"{capability.CapabilityId}\", reqArgs);");
        }

        WriteLine("    }");
        WriteLine();

        if (isPublic)
        {
            GenerateResourceBuilderOverloads(
                returnInfo.ReturnType,
                methodName,
                CreateMethodParameters(parameters),
                returnInfo.HasReturn);
        }
    }

    private JavaCapabilityReturnInfo GetMethodReturnInfo(JavaHandleType handleType, AtsCapabilityInfo capability)
    {
        if (capability.ReturnsBuilder)
        {
            var returnsDifferentBuilder = capability.ReturnType?.TypeId is { } returnTypeId &&
                !string.Equals(returnTypeId, handleType.TypeId, StringComparison.Ordinal) &&
                !string.Equals(returnTypeId, capability.TargetTypeId, StringComparison.Ordinal);

            return returnsDifferentBuilder
                ? new(MapHandleType(capability.ReturnType!.TypeId!), HasReturn: true, ReturnsCurrentBuilder: false)
                : new(handleType.ClassName, HasReturn: true, ReturnsCurrentBuilder: true);
        }

        var hasReturn = capability.ReturnType?.TypeId != AtsConstants.Void;
        return new(hasReturn ? MapTypeRefToJava(capability.ReturnType, false) : "void", hasReturn, ReturnsCurrentBuilder: false);
    }

    private string GenerateCallbackTypeSignature(IReadOnlyList<AtsCallbackParameterInfo>? callbackParameters, AtsTypeRef? callbackReturnType)
    {
        var parameterCount = callbackParameters?.Count ?? 0;
        if (parameterCount > 4)
        {
            return "Function<Object[], Object>";
        }

        var hasReturnType = callbackReturnType != null && callbackReturnType.TypeId != AtsConstants.Void;
        var baseType = hasReturnType ? $"AspireFunc{parameterCount}" : $"AspireAction{parameterCount}";
        if (parameterCount == 0 && !hasReturnType)
        {
            return baseType;
        }

        var typeArguments = new List<string>();
        if (callbackParameters is not null)
        {
            typeArguments.AddRange(callbackParameters.Select(parameter => MapCallbackTypeToJava(parameter.Type)));
        }
        if (hasReturnType)
        {
            typeArguments.Add(MapCallbackTypeToJava(callbackReturnType));
        }

        return $"{baseType}<{string.Join(", ", typeArguments)}>";
    }

    private void GenerateCallbackRegistration(AtsParameterInfo callbackParam)
    {
        var callbackName = ToCamelCase(callbackParam.Name);
        var callbackParameters = callbackParam.CallbackParameters;
        var isOptional = callbackParam.IsOptional || callbackParam.IsNullable;
        var callbackInitializer = isOptional ? $"{callbackName} == null ? null : " : string.Empty;

        WriteLine($"        var {callbackName}Id = {callbackInitializer}getClient().registerCallback(args -> {{");
        GenerateCallbackBody(callbackName, callbackParam, callbackParameters);
        WriteLine("        });");
    }

    private void GenerateCallbackBody(string callbackName, AtsParameterInfo callbackParam, IReadOnlyList<AtsCallbackParameterInfo>? callbackParameters)
    {
        var hasReturnType = callbackParam.CallbackReturnType != null && callbackParam.CallbackReturnType.TypeId != AtsConstants.Void;
        var callArguments = new List<string>();

        if (callbackParameters is not null)
        {
            for (var i = 0; i < callbackParameters.Count; i++)
            {
                var callbackParameter = callbackParameters[i];
                var callbackParameterName = ToCamelCase(callbackParameter.Name);
                WriteLine($"            var {callbackParameterName} = {GetCallbackArgumentExpression(callbackParameter, i)};");
                callArguments.Add(callbackParameterName);
            }
        }

        var callbackInvocation = $"{callbackName}.invoke({string.Join(", ", callArguments)})";
        if (hasReturnType)
        {
            WriteLine($"            return AspireClient.awaitValue({callbackInvocation});");
        }
        else
        {
            WriteLine($"            {callbackInvocation};");
            WriteLine("            return null;");
        }
    }

    private string GetCallbackArgumentExpression(AtsCallbackParameterInfo callbackParameter, int index)
    {
        if (callbackParameter.Type?.TypeId == AtsConstants.CancellationToken)
        {
            return $"CancellationToken.fromValue(args[{index}])";
        }

        if (IsUnionType(callbackParameter.Type))
        {
            return $"AspireUnion.of(args[{index}])";
        }

        return $"({MapCallbackTypeToJava(callbackParameter.Type)}) args[{index}]";
    }

    private string MapCallbackTypeToJava(AtsTypeRef? typeRef)
    {
        if (typeRef is null)
        {
            return "Object";
        }

        if (typeRef.TypeId == AtsConstants.CancellationToken)
        {
            return "CancellationToken";
        }

        if (IsUnionType(typeRef))
        {
            return "AspireUnion";
        }

        return MapTypeRefToJava(typeRef, true, useBoxedTypes: true);
    }

    private static bool IsOmittableParameter(AtsParameterInfo parameter) => parameter.IsOptional || parameter.IsNullable;

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
        WriteLine("        BaseRegistrations.ensureRegistered();");
        WriteLine("        AspireRegistrations.ensureRegistered();");
        WriteLine("        String socketPath = System.getenv(\"REMOTE_APP_HOST_SOCKET_PATH\");");
        WriteLine("        if (socketPath == null || socketPath.isEmpty()) {");
        WriteLine("            throw new RuntimeException(\"REMOTE_APP_HOST_SOCKET_PATH environment variable not set. Run this application using `aspire run`.\");");
        WriteLine("        }");
        WriteLine("        AspireClient client = new AspireClient(socketPath);");
        WriteLine("        client.connect();");
        WriteLine("        String authToken = System.getenv(\"ASPIRE_REMOTE_APPHOST_TOKEN\");");
        WriteLine("        if (authToken == null || authToken.isEmpty()) {");
        WriteLine("            throw new RuntimeException(\"ASPIRE_REMOTE_APPHOST_TOKEN environment variable not set. Run this application using `aspire run`.\");");
        WriteLine("        }");
        WriteLine("        client.authenticate(authToken);");
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
        WriteLine("        if (resolvedOptions.get(\"Args\") == null) {");
        WriteLine("            // Note: Java doesn't have easy access to command line args from here");
        WriteLine("            resolvedOptions.put(\"Args\", new String[0]);");
        WriteLine("        }");
        WriteLine("        if (resolvedOptions.get(\"ProjectDirectory\") == null) {");
        WriteLine("            resolvedOptions.put(\"ProjectDirectory\", System.getProperty(\"user.dir\"));");
        WriteLine("        }");
        WriteLine("        if (resolvedOptions.get(\"AppHostFilePath\") == null) {");
        WriteLine("            String appHostFilePath = System.getenv(\"ASPIRE_APPHOST_FILEPATH\");");
        WriteLine("            if (appHostFilePath != null && !appHostFilePath.isEmpty()) {");
        WriteLine("                resolvedOptions.put(\"AppHostFilePath\", appHostFilePath);");
        WriteLine("            }");
        WriteLine("        }");
        WriteLine("        Map<String, Object> args = new HashMap<>();");
        WriteLine("        args.put(\"argsOrOptions\", resolvedOptions);");
        WriteLine($"        return ({builderClassName}) client.invokeCapability(\"Aspire.Hosting/createBuilder\", args);");
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
        _resourceBuilderHandleClasses.Clear();
        foreach (var typeId in handleTypeIds)
        {
            _classNames[typeId] = CreateClassName(typeId);
        }

        var handleTypeMap = context.HandleTypes
            .GroupBy(t => t.AtsTypeId, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Any(t => t.IsResourceBuilder),
                StringComparer.Ordinal);
        var handleTypeInfoMap = context.HandleTypes
            .GroupBy(t => t.AtsTypeId, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.First(),
                StringComparer.Ordinal);
        var results = new List<JavaHandleType>();
        foreach (var typeId in handleTypeIds)
        {
            var isResourceBuilder = false;
            if (handleTypeMap.TryGetValue(typeId, out var typeInfo))
            {
                isResourceBuilder = typeInfo;
            }

            var className = _classNames[typeId];
            var baseClassName = isResourceBuilder ? "ResourceBuilderBase" : "HandleWrapperBase";

            if (handleTypeInfoMap.TryGetValue(typeId, out var handleTypeInfo))
            {
                var exportedBaseType = handleTypeInfo.BaseTypeHierarchy
                    .FirstOrDefault(baseType => !string.Equals(baseType.TypeId, typeId, StringComparison.Ordinal)
                        && _classNames.ContainsKey(baseType.TypeId));

                if (exportedBaseType is not null)
                {
                    baseClassName = _classNames[exportedBaseType.TypeId];
                }
            }

            results.Add(new JavaHandleType(typeId, className, isResourceBuilder, baseClassName));
            if (isResourceBuilder)
            {
                _resourceBuilderHandleClasses.Add(className);
            }
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

    private string MapTypeRefToJava(AtsTypeRef? typeRef, bool isOptional, bool useBoxedTypes = false)
    {
        if (typeRef is null)
        {
            return "Object";
        }

        if (typeRef.TypeId == AtsConstants.ReferenceExpressionTypeId)
        {
            return "ReferenceExpression";
        }

        return typeRef.Category switch
        {
            AtsTypeCategory.Primitive => MapPrimitiveType(typeRef.TypeId, isOptional || useBoxedTypes),
            AtsTypeCategory.Enum => MapEnumType(typeRef.TypeId),
            AtsTypeCategory.Handle => MapHandleType(typeRef.TypeId),
            AtsTypeCategory.Dto => MapDtoType(typeRef.TypeId),
            AtsTypeCategory.Callback => "Object",
            AtsTypeCategory.Array => $"{MapTypeRefToJava(typeRef.ElementType, false)}[]",
            AtsTypeCategory.List => typeRef.IsReadOnly
                ? $"List<{MapTypeRefToJava(typeRef.ElementType, false, useBoxedTypes: true)}>"
                : $"AspireList<{MapTypeRefToJava(typeRef.ElementType, false, useBoxedTypes: true)}>",
            AtsTypeCategory.Dict => typeRef.IsReadOnly
                ? $"Map<{MapTypeRefToJava(typeRef.KeyType, false, useBoxedTypes: true)}, {MapTypeRefToJava(typeRef.ValueType, false, useBoxedTypes: true)}>"
                : $"AspireDict<{MapTypeRefToJava(typeRef.KeyType, false, useBoxedTypes: true)}, {MapTypeRefToJava(typeRef.ValueType, false, useBoxedTypes: true)}>",
            AtsTypeCategory.Union => "AspireUnion",
            AtsTypeCategory.Unknown => "Object",
            _ => "Object"
        };
    }

    private string MapInputTypeToJava(AtsTypeRef? typeRef, bool isOptional = false)
    {
        if (typeRef is null)
        {
            return "Object";
        }

        if (IsCancellationTokenTypeId(typeRef.TypeId))
        {
            return "CancellationToken";
        }

        if (IsUnionType(typeRef))
        {
            return "AspireUnion";
        }

        return MapTypeRefToJava(typeRef, isOptional);
    }

    private string MapParameterToJava(AtsParameterInfo parameter)
    {
        if (parameter.IsCallback)
        {
            return GenerateCallbackTypeSignature(parameter.CallbackParameters, parameter.CallbackReturnType);
        }

        return MapInputTypeToJava(parameter.Type, parameter.IsOptional || parameter.IsNullable);
    }

    private string MapHandleType(string typeId) =>
        _classNames.TryGetValue(typeId, out var name) ? name : "Handle";

    private string MapDtoType(string typeId) =>
        _dtoNames.TryGetValue(typeId, out var name) ? name : "Map<String, Object>";

    private string MapEnumType(string typeId) =>
        _enumNames.TryGetValue(typeId, out var name) ? name : "String";

    private static string MapPrimitiveType(string typeId, bool useBoxedTypes) => typeId switch
    {
        AtsConstants.String or AtsConstants.Char => "String",
        AtsConstants.Number => useBoxedTypes ? "Double" : "double",
        AtsConstants.Boolean => useBoxedTypes ? "Boolean" : "boolean",
        AtsConstants.Void => "void",
        AtsConstants.Any => "Object",
        AtsConstants.DateTime or AtsConstants.DateTimeOffset or
        AtsConstants.DateOnly or AtsConstants.TimeOnly => "String",
        AtsConstants.TimeSpan => useBoxedTypes ? "Double" : "double",
        AtsConstants.Guid or AtsConstants.Uri => "String",
        AtsConstants.CancellationToken => "CancellationToken",
        _ => "Object"
    };

    private static bool IsUnionType(AtsTypeRef? typeRef) => typeRef?.Category == AtsTypeCategory.Union;

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

    private sealed record JavaHandleType(string TypeId, string ClassName, bool IsResourceBuilder, string BaseClassName);
    private sealed record JavaMethodParameter(
        string Type,
        string Name,
        string? ResourceWrapperType = null,
        string? ResourceWrapperParameterType = null);
    private sealed record JavaCapabilityReturnInfo(string ReturnType, bool HasReturn, bool ReturnsCurrentBuilder);
}
