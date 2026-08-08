// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Shared.Json;
using Aspire.TypeSystem;

namespace Aspire.Hosting.CodeGeneration.TypeScript;

/// <summary>
/// Represents a builder class to be generated with its capabilities.
/// Internal type replacing BuilderModel - used only within the generator.
/// </summary>
internal sealed class BuilderModel
{
    public required string TypeId { get; init; }
    public required string BuilderClassName { get; init; }
    public required List<AtsCapabilityInfo> Capabilities { get; init; }
    public bool IsInterface { get; init; }
    public AtsTypeRef? TargetType { get; init; }
}

internal sealed class ExportedValueTreeNode
{
    public Dictionary<string, ExportedValueTreeNode> Children { get; } = new(StringComparer.Ordinal);

    public AtsExportedValueInfo? Value { get; set; }
}

/// <summary>
/// Generates a TypeScript SDK using the ATS (Aspire Type System) capability-based API.
/// Produces typed builder classes with fluent methods that use invokeCapability().
/// </summary>
/// <remarks>
/// <para>
/// <b>ATS to TypeScript Type Mapping</b>
/// </para>
/// <para>
/// The generator maps ATS types to TypeScript types according to the following rules:
/// </para>
/// <para>
/// <b>Primitive Types:</b>
/// <list type="table">
///   <listheader>
///     <term>ATS Type</term>
///     <description>TypeScript Type</description>
///   </listheader>
///   <item><term><c>string</c></term><description><c>string</c></description></item>
///   <item><term><c>number</c></term><description><c>number</c></description></item>
///   <item><term><c>boolean</c></term><description><c>boolean</c></description></item>
///   <item><term><c>any</c></term><description><c>unknown</c></description></item>
/// </list>
/// </para>
/// <para>
/// <b>Handle Types:</b>
/// Type IDs use the format <c>{AssemblyName}/{TypeName}</c>.
/// <list type="table">
///   <listheader>
///     <term>ATS Type ID</term>
///     <description>TypeScript Type</description>
///   </listheader>
///   <item><term><c>Aspire.Hosting/IDistributedApplicationBuilder</c></term><description><c>BuilderHandle</c></description></item>
///   <item><term><c>Aspire.Hosting/DistributedApplication</c></term><description><c>ApplicationHandle</c></description></item>
///   <item><term><c>Aspire.Hosting/DistributedApplicationExecutionContext</c></term><description><c>ExecutionContextHandle</c></description></item>
///   <item><term><c>Aspire.Hosting.Redis/RedisResource</c></term><description><c>RedisResourceBuilderHandle</c></description></item>
///   <item><term><c>Aspire.Hosting/ContainerResource</c></term><description><c>ContainerResourceBuilderHandle</c></description></item>
///   <item><term><c>Aspire.Hosting.ApplicationModel/IResource</c></term><description><c>IResourceHandle</c></description></item>
/// </list>
/// </para>
/// <para>
/// <b>Handle Type Naming Rules:</b>
/// <list type="bullet">
///   <item><description>Core types: Use type name + "Handle"</description></item>
///   <item><description>Interface types: Use interface name + "Handle" (keep the I prefix)</description></item>
///   <item><description>Resource types: Use type name + "BuilderHandle"</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Special Types:</b>
/// <list type="table">
///   <listheader>
///     <term>ATS Type</term>
///     <description>TypeScript Type</description>
///   </listheader>
///   <item><term><c>callback</c></term><description><c>(context: EnvironmentContextHandle) =&gt; Promise&lt;void&gt;</c></description></item>
///   <item><term><c>T[]</c> (array)</term><description><c>T[]</c> (array of mapped type)</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Builder Class Generation:</b>
/// <list type="bullet">
///   <item><description><c>Aspire.Hosting.Redis/RedisResource</c> → <c>RedisResourceBuilder</c> class with <c>RedisResourceBuilderPromise</c> thenable wrapper</description></item>
///   <item><description><c>Aspire.Hosting.ApplicationModel/IResource</c> → <c>ResourceBuilderBase</c> abstract class (interface types get "BuilderBase" suffix)</description></item>
///   <item><description>Concrete builders extend interface builders based on type hierarchy</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Method Naming:</b>
/// <list type="bullet">
///   <item><description>Derived from capability ID: <c>Aspire.Hosting.Redis/addRedis</c> → <c>addRedis</c></description></item>
///   <item><description>Can be overridden via <c>[AspireExport(MethodName = "...")]</c></description></item>
///   <item><description>TypeScript uses camelCase (the canonical form from capability IDs)</description></item>
/// </list>
/// </para>
/// </remarks>
internal sealed class AtsTypeScriptCodeGenerator : ICodeGenerator, IApiReferenceExporter
{
    private TextWriter _writer = null!;

    /// <summary>
    /// Owns every TypeScript-specific resolution decision. Assigned per generation because it is
    /// built from the context being generated; the canonical API exporter builds the same projector
    /// from the same context so documentation cannot drift from emitted source.
    /// </summary>
    private TypeScriptApiProjector _projector = null!;

    private void WriteCapabilityDocComment(
        string indent,
        AtsCapabilityInfo capability,
        IReadOnlyList<AtsParameterInfo>? publicParameters = null,
        string? optionsParameterName = null)
    {
        var parameterDocs = (publicParameters ?? capability.Parameters
                .Where(p => !string.Equals(p.Name, capability.TargetParameterName, StringComparison.Ordinal))
                .ToList())
            .Select(p => (p.Name, Summary: p.Documentation?.Summary))
            .Where(static p => !string.IsNullOrWhiteSpace(p.Summary))
            .ToList();

        if (!string.IsNullOrWhiteSpace(optionsParameterName))
        {
            parameterDocs.Add((optionsParameterName, "Additional options."));
        }

        WriteDocumentationComment(
            indent,
            capability.Documentation,
            capability.Documentation is null ? capability.Description : null,
            parameterDocs,
            capability.ReturnType.TypeId == AtsConstants.Void ? null : capability.Documentation?.Returns,
            suppressReturns: capability.ReturnType.TypeId == AtsConstants.Void,
            isObsolete: capability.IsObsolete,
            obsoleteMessage: capability.ObsoleteMessage);
    }

    private void WritePropertyDocComment(string indent, AtsCapabilityInfo? getter, AtsCapabilityInfo? setter)
    {
        var capability = getter is not null && (getter.Documentation is not null || !string.IsNullOrWhiteSpace(getter.Description) || getter.IsObsolete)
            ? getter
            : setter;

        if (capability is not null)
        {
            WriteCapabilityDocComment(indent, capability);
        }
    }

    private void WriteDocumentationComment(
        string indent,
        AtsDocumentationInfo? documentation,
        string? fallbackSummary = null,
        IReadOnlyList<(string Name, string? Summary)>? parameters = null,
        string? returns = null,
        bool suppressReturns = false,
        bool isObsolete = false,
        string? obsoleteMessage = null)
    {
        var lines = new List<string>();
        AddDocumentationLines(lines, documentation?.Summary ?? fallbackSummary);
        AddDocumentationLines(lines, documentation?.Remarks, addBlankLineBefore: lines.Count > 0);

        foreach (var parameter in parameters ?? [])
        {
            AddTaggedDocumentationLines(lines, $"@param {parameter.Name}", parameter.Summary);
        }

        if (!suppressReturns)
        {
            AddTaggedDocumentationLines(lines, "@returns", returns ?? documentation?.Returns);
        }

        if (isObsolete)
        {
            lines.Add(string.IsNullOrWhiteSpace(obsoleteMessage)
                ? "@deprecated"
                : $"@deprecated {EscapeJSDocText(obsoleteMessage)}");
        }

        if (lines.Count == 0)
        {
            return;
        }

        if (lines.Count == 1 && !lines[0].StartsWith('@'))
        {
            WriteLine($"{indent}/** {lines[0]} */");
            return;
        }

        WriteLine($"{indent}/**");
        foreach (var line in lines)
        {
            WriteLine(line.Length == 0 ? $"{indent} *" : $"{indent} * {line}");
        }
        WriteLine($"{indent} */");
    }

    private static void AddTaggedDocumentationLines(List<string> lines, string tag, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var tagLines = SplitDocumentationLines(text);
        if (tagLines.Count == 0)
        {
            return;
        }

        lines.Add($"{tag} {tagLines[0]}");
        foreach (var line in tagLines.Skip(1))
        {
            lines.Add(line);
        }
    }

    private static void AddDocumentationLines(List<string> lines, string? text, bool addBlankLineBefore = false)
    {
        var textLines = SplitDocumentationLines(text);
        if (textLines.Count == 0)
        {
            return;
        }

        if (addBlankLineBefore)
        {
            lines.Add(string.Empty);
        }

        lines.AddRange(textLines);
    }

    private static List<string> SplitDocumentationLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(EscapeJSDocText)
            .ToList();
    }

    private static string EscapeJSDocText(string text) =>
        ConvertAtsReferencesToJsDocLinks(text).Replace("*/", "* /", StringComparison.Ordinal);

    private static string ConvertAtsReferencesToJsDocLinks(string text)
    {
        const string markerStart = "{@ats-ref ";

        var startIndex = text.IndexOf(markerStart, StringComparison.Ordinal);
        if (startIndex < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var currentIndex = 0;

        while (startIndex >= 0)
        {
            builder.Append(text, currentIndex, startIndex - currentIndex);

            var markerBodyStartIndex = startIndex + markerStart.Length;
            var markerEndIndex = text.IndexOf('}', markerBodyStartIndex);
            if (markerEndIndex < 0)
            {
                builder.Append(text, startIndex, text.Length - startIndex);
                return builder.ToString();
            }

            var markerBody = text[markerBodyStartIndex..markerEndIndex];
            var labelSeparatorIndex = markerBody.IndexOf('|', StringComparison.Ordinal);
            var reference = labelSeparatorIndex < 0 ? markerBody : markerBody[..labelSeparatorIndex];
            var label = labelSeparatorIndex < 0 ? null : markerBody[(labelSeparatorIndex + 1)..];
            var targetSeparatorIndex = reference.IndexOf(':', StringComparison.Ordinal);

            if (targetSeparatorIndex < 0 || targetSeparatorIndex == reference.Length - 1)
            {
                builder.Append(text, startIndex, markerEndIndex - startIndex + 1);
            }
            else
            {
                var target = reference[(targetSeparatorIndex + 1)..];
                builder.Append("{@link ").Append(target);
                if (!string.IsNullOrWhiteSpace(label))
                {
                    builder.Append('|').Append(label);
                }

                builder.Append('}');
            }

            currentIndex = markerEndIndex + 1;
            startIndex = text.IndexOf(markerStart, currentIndex, StringComparison.Ordinal);
        }

        builder.Append(text, currentIndex, text.Length - currentIndex);
        return builder.ToString();
    }

    private static string GetRpcArgumentValueExpression(string parameterName, AtsTypeRef? typeRef)
    {
        if (TypeScriptApiProjector.IsCancellationTokenType(typeRef))
        {
            return $"CancellationToken.fromValue({parameterName})";
        }

        return parameterName;
    }

    private static string GetLocalParameterName(AtsParameterInfo parameter)
    {
        // ES modules are always strict mode, where "arguments" cannot be used as
        // a local binding name. Keep the wire/API name unchanged and only rename
        // the generated implementation variable.
        return parameter.Name == "arguments" ? "argumentsValue" : parameter.Name;
    }

    private static string GetRpcArgumentEntry(string parameterName, AtsTypeRef? typeRef)
    {
        var valueExpression = GetRpcArgumentValueExpression(parameterName, typeRef);
        return valueExpression == parameterName
            ? parameterName
            : $"{parameterName}: {valueExpression}";
    }

    private static string GetRpcArgumentExpression(AtsParameterInfo param, string localParameterName, bool useRegisteredCallback = true)
    {
        if (useRegisteredCallback && param.IsCallback)
        {
            return $"{localParameterName}Id";
        }

        return GetRpcArgumentValueExpression(localParameterName, param.Type);
    }

    private string GetRpcArgumentEntryForParam(AtsParameterInfo param, string localParameterName, bool useRegisteredCallback = true)
    {
        if (useRegisteredCallback && param.IsCallback)
        {
            return $"{param.Name}: {localParameterName}Id";
        }

        var valueExpression = GetRpcArgumentExpressionForParam(param, localParameterName, useRegisteredCallback);
        return valueExpression == param.Name
            ? param.Name
            : $"{param.Name}: {valueExpression}";
    }

    private string GetRpcArgumentExpressionForParam(AtsParameterInfo param, string localParameterName, bool useRegisteredCallback = true)
    {
        if (TryGetDtoCallbackMarshallingProperties(param.Type, out _))
        {
            return GetDtoRpcLocalName(localParameterName);
        }

        return GetRpcArgumentExpression(param, localParameterName, useRegisteredCallback);
    }

    private bool TryGetDtoCallbackMarshallingProperties(AtsTypeRef? typeRef, out List<AtsDtoPropertyInfo> marshallingProperties)
    {
        marshallingProperties = [];

        if (typeRef?.Category != AtsTypeCategory.Dto ||
            !_projector.DtoTypesById.TryGetValue(typeRef.TypeId, out var dtoType))
        {
            return false;
        }

        marshallingProperties = dtoType.Properties
            .Where(p => p.IsCallback || RequiresDtoCallbackMarshalling(p.Type))
            .ToList();

        return marshallingProperties.Count > 0;
    }

    private bool RequiresDtoCallbackMarshalling(AtsTypeRef? typeRef, HashSet<string>? visitedDtoTypeIds = null)
    {
        if (typeRef?.Category != AtsTypeCategory.Dto ||
            !_projector.DtoTypesById.TryGetValue(typeRef.TypeId, out var dtoType))
        {
            return false;
        }

        visitedDtoTypeIds ??= new(StringComparer.Ordinal);
        if (!visitedDtoTypeIds.Add(typeRef.TypeId))
        {
            return false;
        }

        try
        {
            return dtoType.Properties.Any(p => p.IsCallback || RequiresDtoCallbackMarshalling(p.Type, visitedDtoTypeIds));
        }
        finally
        {
            visitedDtoTypeIds.Remove(typeRef.TypeId);
        }
    }

    private static string GetDtoRpcLocalName(string localParameterName) => $"__{localParameterName}ForRpc";

    private static string GetDtoCallbackLocalName(string dtoLocalName, string propertyName) => $"__{dtoLocalName}{propertyName}";

    /// <summary>
    /// Gets the TypeId from a capability's return type.
    /// </summary>
    private static string? GetReturnTypeId(AtsCapabilityInfo capability) => capability.ReturnType?.TypeId;

    /// <inheritdoc />
    public string Language => "TypeScript";

    /// <inheritdoc />
    public Dictionary<string, string> GenerateDistributedApplication(AtsContext context)
    {
        var files = new Dictionary<string, string>();

        // Add embedded resource files (transport.mts, base.mts)
        files["transport.mts"] = EmbeddedResources.Read("transport.mts");
        files["base.mts"] = EmbeddedResources.Read("base.mts");

        // Generate the capability-based aspire.mts SDK
        files["aspire.mts"] = GenerateAspireSdk(context);

        return files;
    }

    /// <inheritdoc />
    public JsonElement ExportApi(AtsContext context, ApiReferenceExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        // Build the projector from the same context the generator would use, so the exported
        // documentation describes the exact signatures generation would emit rather than a
        // second, independently derived reading of the ATS context.
        var projector = new TypeScriptApiProjector(context);
        var model = projector.BuildApiModel(
            new TypeScriptApiPackageIdentity(options.PackageName, options.PackageVersion),
            options.ExportingAssemblyNames);

        // JsonDocument.Parse + Clone rather than JsonSerializer, because this assembly is
        // AOT-compatible and the serializer's reflection-based overloads are not.
        using var document = JsonDocument.Parse(TypeScriptApiExportWriter.WriteToJson(model));
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Generates the aspire.mts SDK file with capability-based API.
    /// </summary>
    private string GenerateAspireSdk(AtsContext context)
    {
        using var stringWriter = new StringWriter(CultureInfo.InvariantCulture);
        _writer = stringWriter;

        // Header
        WriteLine("""
            // aspire.mts - Capability-based Aspire SDK
            // This SDK uses the ATS (Aspire Type System) capability API.
            // Capabilities are endpoints like 'Aspire.Hosting/createBuilder'.
            //
            // GENERATED CODE - DO NOT EDIT

            import {
                AspireClient,
                Handle,
                MarshalledHandle,
                AppHostUsageError,
                CancellationToken,
                CapabilityError,
                registerCallback,
                wrapIfHandle,
                registerHandleWrapper,
                isPromiseLike
            } from './transport.mjs';
            import type { AspireClientRpc } from './transport.mjs';

            import type { HandleReference } from './base.mjs';

            import {
                ResourceBuilderBase,
                ReferenceExpression,
                refExpr,
                AspireDict,
                AspireList,
                InteractionInputCollectionPromiseImpl
            } from './base.mjs';

            export {
                InputType,
                InteractionInputCollection
            } from './base.mjs';

            export type {
                InteractionInput,
                InteractionInputOption,
                InteractionInputCollectionPromise
            } from './base.mjs';

            import type {
                Awaitable,
                InteractionInput,
                InteractionInputCollection,
                InteractionInputCollectionPromise,
                InputType
            } from './base.mjs';
            """);
        WriteLine();

        // Resolve every TypeScript-specific decision once. The canonical API exporter consumes the
        // same projector, so documented signatures cannot drift from the signatures emitted here.
        _projector = new TypeScriptApiProjector(context);
        var resolved = _projector.Resolved;

        var dtoTypes = context.DtoTypes;
        var enumTypes = context.EnumTypes;
        var exportedValues = context.ExportedValues;

        var builders = resolved.Builders;
        var resourceBuilders = resolved.ResourceBuilders;
        var typeClasses = resolved.TypeClasses;
        var clientMethods = resolved.ClientMethods;
        var typeIds = resolved.HandleTypeIds;

        // Generate handle type aliases
        GenerateHandleTypeAliases(typeIds);

        // Generate enum types
        GenerateEnumTypes(enumTypes);

        // Generate DTO interfaces
        GenerateDtoInterfaces(dtoTypes);

        // Generate exported immutable values
        GenerateExportedValues(exportedValues, dtoTypes.ToDictionary(dto => dto.TypeId, StringComparer.Ordinal));

        // Generate collected options interfaces
        GenerateOptionsInterfaces();

        // Generate type classes (context types and wrapper types)
        foreach (var typeClass in typeClasses)
        {
            GenerateTypeClass(typeClass);
        }

        // Generate resource builder classes
        foreach (var builder in resourceBuilders)
        {
            GenerateBuilderClass(builder);
        }

        // Generate AspireClient with remaining entry point methods
        GenerateAspireClient(clientMethods);

        // Generate connection helper
        GenerateConnectionHelper();

        // Generate global error handling
        GenerateGlobalErrorHandling();

        // Generate handle wrapper registrations (after all classes are defined)
        GenerateHandleWrapperRegistrations(typeClasses, resourceBuilders);

        return stringWriter.ToString();
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

    private void Write(string text)
    {
        _writer.Write(text);
    }

    private void GenerateHandleTypeAliases(HashSet<string> typeIds)
    {
        WriteLine("// ============================================================================");
        WriteLine("// Handle Type Aliases (Internal - not exported to users)");
        WriteLine("// ============================================================================");
        WriteLine();

        foreach (var typeId in typeIds.OrderBy(t => t))
        {
            var handleName = TypeScriptApiProjector.GetHandleTypeName(typeId);
            var description = TypeScriptApiProjector.GetTypeDescription(typeId);
            WriteDocumentationComment(string.Empty, GetHandleDocumentation(typeId), description);
            // Internal type alias - not exported (users work with wrapper classes)
            WriteLine($"type {handleName} = Handle<'{typeId}'>;");
            WriteLine();
        }
    }

    private AtsDocumentationInfo? GetHandleDocumentation(string typeId)
    {
        return _projector.HandleDocumentationById.GetValueOrDefault(typeId);
    }

    /// <summary>
    /// Generates TypeScript enums from discovered enum types.
    /// </summary>
    private void GenerateEnumTypes(IReadOnlyList<AtsEnumTypeInfo> enumTypes)
    {
        var generatedEnumTypes = enumTypes
            .Where(enumType => enumType.TypeId != TypeScriptApiProjector.InputTypeTypeId)
            .ToList();

        if (generatedEnumTypes.Count == 0)
        {
            return;
        }

        WriteLine("// ============================================================================");
        WriteLine("// Enum Types");
        WriteLine("// ============================================================================");
        WriteLine();

        foreach (var enumType in generatedEnumTypes.OrderBy(e => e.Name))
        {
            WriteDocumentationComment(string.Empty, enumType.Documentation, $"Enum type for {enumType.Name}");
            WriteLine($"export enum {enumType.Name} {{");

            var enumValues = enumType.ValueInfos.Count > 0
                ? enumType.ValueInfos
                : enumType.Values.Select(value => new AtsEnumValueInfo { Name = value }).ToList();

            foreach (var value in enumValues)
            {
                // Enums serialize as strings in JSON
                WriteDocumentationComment("    ", value.Documentation);
                WriteLine($"    {value.Name} = \"{value.Name}\",");
            }

            WriteLine("}");
            WriteLine();
        }
    }

    /// <summary>
    /// Generates TypeScript interfaces for DTO types marked with [AspireDto].
    /// </summary>
    private void GenerateDtoInterfaces(IReadOnlyList<AtsDtoTypeInfo> dtoTypes)
    {
        var generatedDtoTypes = dtoTypes
            .Where(dto => dto.TypeId != TypeScriptApiProjector.InteractionInputTypeId)
            .ToList();

        if (generatedDtoTypes.Count == 0)
        {
            return;
        }

        WriteLine("// ============================================================================");
        WriteLine("// DTO Interfaces");
        WriteLine("// ============================================================================");
        WriteLine();

        foreach (var dto in generatedDtoTypes.OrderBy(d => d.Name))
        {
            var interfaceName = TypeScriptApiProjector.GetDtoInterfaceName(dto.TypeId);

            WriteDocumentationComment(string.Empty, dto.Documentation, dto.Description ?? $"DTO interface for {dto.Name}");
            WriteLine($"export interface {interfaceName} {{");

            foreach (var prop in dto.Properties)
            {
                var tsType = prop.IsCallback
                    ? _projector.GenerateCallbackTypeSignature(prop.CallbackParameters, prop.CallbackReturnType)
                    : _projector.MapDtoPropertyTypeToTypeScript(prop.Type);
                // All DTO properties are optional in TypeScript to allow partial objects
                // Convert PascalCase to camelCase for TypeScript
                var propName = TypeScriptApiProjector.ToCamelCase(prop.Name);
                WriteDocumentationComment("    ", prop.Documentation, prop.Description);
                WriteLine($"    {propName}?: {tsType};");
            }

            // Client-only properties have no C# counterpart. The list lives on the projector so the
            // exported API surface describes the same interface this emits.
            foreach (var clientOnly in TypeScriptApiProjector.GetClientOnlyDtoProperties(interfaceName))
            {
                WriteLine($"    /** {clientOnly.Summary} */");
                WriteLine($"    {clientOnly.Name}?: {clientOnly.Type};");
            }

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
            WriteLine($"export namespace {name} {{");
            WriteTypeScriptExportedValueChildren(node, dtoTypesById, indentLevel: 1);
            WriteLine("}");
            WriteLine();
        }
    }

    private void WriteTypeScriptExportedValueChildren(
        ExportedValueTreeNode node,
        IReadOnlyDictionary<string, AtsDtoTypeInfo> dtoTypesById,
        int indentLevel)
    {
        var indent = new string(' ', indentLevel * 4);

        foreach (var (name, child) in node.Children.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (child.Value is { } valueInfo)
            {
                WriteDocumentationComment(indent, valueInfo.Documentation, valueInfo.Description);

                var literal = RenderTypeScriptExportedValue(valueInfo.Value, valueInfo.Type, dtoTypesById);
                var exportedType = _projector.MapTypeRefToTypeScript(valueInfo.Type);
                var needsCast = valueInfo.Type.Category is not AtsTypeCategory.Primitive;
                var expression = needsCast ? $"{literal} as {exportedType}" : literal;
                WriteLine($"{indent}export const {name} = {expression};");
            }
            else
            {
                WriteLine($"{indent}export namespace {name} {{");
                WriteTypeScriptExportedValueChildren(child, dtoTypesById, indentLevel + 1);
                WriteLine($"{indent}}}");
            }

            WriteLine();
        }
    }

    private string RenderTypeScriptExportedValue(
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
            AtsTypeCategory.Dto when value is JsonObject obj && dtoTypesById.TryGetValue(typeRef.TypeId, out var dtoInfo)
                => RenderTypeScriptDtoValue(obj, dtoInfo, dtoTypesById),
            AtsTypeCategory.Array or AtsTypeCategory.List when value is JsonArray arr
                => $"[{string.Join(", ", arr.Select(item => RenderTypeScriptExportedValue(item, typeRef.ElementType!, dtoTypesById)))}]",
            AtsTypeCategory.Dict when value is JsonObject obj
                => "{ " + string.Join(", ", obj.Select(pair => $"{RenderTypeScriptPropertyKey(pair.Key)}: {RenderTypeScriptExportedValue(pair.Value, typeRef.ValueType!, dtoTypesById)}")) + " }",
            _ => value.ToRelaxedJsonString()
        };
    }

    private string RenderTypeScriptDtoValue(
        JsonObject value,
        AtsDtoTypeInfo dtoInfo,
        IReadOnlyDictionary<string, AtsDtoTypeInfo> dtoTypesById)
    {
        var members = new List<string>();

        foreach (var property in dtoInfo.Properties)
        {
            if (!value.TryGetPropertyValue(property.Name, out var propertyValue))
            {
                continue;
            }

            members.Add($"{TypeScriptApiProjector.ToCamelCase(property.Name)}: {RenderTypeScriptExportedValue(propertyValue, property.Type, dtoTypesById)}");
        }

        return "{ " + string.Join(", ", members) + " }";
    }

    private static string RenderTypeScriptPropertyKey(string key)
    {
        return AtsJsonCodeWriter.ToRelaxedJsonString(key);
    }

    private static ExportedValueTreeNode BuildExportedValueTree(IReadOnlyList<AtsExportedValueInfo> exportedValues)
    {
        var root = new ExportedValueTreeNode();

        foreach (var exportedValue in exportedValues)
        {
            var current = root;
            foreach (var segment in exportedValue.PathSegments)
            {
                if (!current.Children.TryGetValue(segment, out var child))
                {
                    child = new ExportedValueTreeNode();
                    current.Children[segment] = child;
                }

                current = child;
            }

            current.Value = exportedValue;
        }

        return root;
    }

    /// <summary>
    /// Generates all collected options interfaces.
    /// </summary>
    private void GenerateOptionsInterfaces()
    {
        if (_projector.OptionsInterfacesToGenerate.Count == 0)
        {
            return;
        }

        WriteLine("// ============================================================================");
        WriteLine("// Options Interfaces");
        WriteLine("// ============================================================================");
        WriteLine();

        foreach (var (interfaceName, optionalParams) in _projector.OptionsInterfacesToGenerate.OrderBy(kvp => kvp.Key))
        {
            WriteLine($"export interface {interfaceName} {{");
            foreach (var param in optionalParams)
            {
                var tsType = _projector.MapParameterToTypeScript(param);
                WriteDocumentationComment("    ", param.Documentation);
                WriteLine($"    {param.Name}?: {tsType};");
            }
            WriteLine("}");
            WriteLine();
        }
    }

    private void GenerateGetterOnlyPropertyPromiseSignature(string propertyName, AtsCapabilityInfo getter)
    {
        var returnType = _projector.GetGetterOnlyPropertyMethodReturnType(getter.ReturnType);
        WriteCapabilityDocComment("    ", getter);
        WriteLine($"    {propertyName}(): {returnType};");
    }

    private void GenerateInterfaceProperty(string propertyName, AtsCapabilityInfo? getter, AtsCapabilityInfo? setter)
    {
        if (TypeScriptApiProjector.IsGetterOnlyProperty(getter, setter))
        {
            GenerateGetterOnlyPropertyPromiseSignature(propertyName, getter!);
            return;
        }

        if (getter?.ReturnType is { } returnType)
        {
            if (TypeScriptApiProjector.IsDictionaryType(returnType))
            {
                var keyType = returnType.KeyType != null ? _projector.MapTypeRefToTypeScript(returnType.KeyType) : "string";
                var valueType = returnType.ValueType != null ? _projector.MapTypeRefToTypeScript(returnType.ValueType) : "unknown";
                WritePropertyDocComment("    ", getter, setter);
                WriteLine($"    readonly {propertyName}: AspireDict<{keyType}, {valueType}>;");
                return;
            }

            if (TypeScriptApiProjector.IsListType(returnType))
            {
                var elementType = returnType.ElementType != null ? _projector.MapTypeRefToTypeScript(returnType.ElementType) : "unknown";
                WritePropertyDocComment("    ", getter, setter);
                WriteLine($"    readonly {propertyName}: AspireList<{elementType}>;");
                return;
            }
        }

        WritePropertyDocComment("    ", getter, setter);
        WriteLine($"    {propertyName}: {{");

        if (getter != null)
        {
            if (_projector.TryGetPromiseWrapperType(getter.ReturnType, out var promiseInterfaceName, out _))
            {
                WriteLine($"        get: () => {promiseInterfaceName};");
            }
            else
            {
                var returnTypeName = _projector.MapTypeRefToTypeScript(getter.ReturnType);
                WriteLine($"        get: () => Promise<{returnTypeName}>;");
            }
        }

        if (setter != null)
        {
            var valueParam = setter.Parameters.FirstOrDefault(p => p.Name == "value");
            if (valueParam != null)
            {
                var valueType = _projector.MapInputTypeToTypeScript(valueParam.Type);
                WriteLine($"        set: (value: {valueType}) => Promise<void>;");
            }
        }

        WriteLine("    };");
    }

    private void GenerateBuilderInterface(BuilderModel builder)
    {
        var interfaceName = TypeScriptApiProjector.GetInterfaceName(builder.BuilderClassName);

        WriteLine("// ============================================================================");
        WriteLine($"// {interfaceName}");
        WriteLine("// ============================================================================");
        WriteLine();
        WriteDocumentationComment(string.Empty, GetHandleDocumentation(builder.TypeId));
        WriteLine($"export interface {interfaceName} {{");
        WriteLine("    toJSON(): MarshalledHandle;");

        var getters = builder.Capabilities.Where(c => c.CapabilityKind == AtsCapabilityKind.PropertyGetter).ToList();
        var setters = builder.Capabilities.Where(c => c.CapabilityKind == AtsCapabilityKind.PropertySetter).ToList();
        if (getters.Count > 0 || setters.Count > 0)
        {
            var properties = TypeScriptApiProjector.GroupPropertiesByName(getters, setters);
            foreach (var prop in properties)
            {
                GenerateInterfaceProperty(prop.PropertyName, prop.Getter, prop.Setter);
            }
        }

        foreach (var capability in builder.Capabilities.Where(c =>
            c.CapabilityKind != AtsCapabilityKind.PropertyGetter &&
            c.CapabilityKind != AtsCapabilityKind.PropertySetter))
        {
            var signature = _projector.ResolveMethodSignature(builder, capability);
            var hasNonBuilderReturn = !capability.ReturnsBuilder && capability.ReturnType != null;

            WriteCapabilityDocComment("    ", capability, signature.RequiredParameters, signature.OptionsParameter?.Name);
            if (hasNonBuilderReturn)
            {
                if (_projector.TryGetPromiseWrapperType(capability.ReturnType, out var promiseInterfaceName, out _))
                {
                    WriteLine($"    {capability.MethodName}({signature.ParameterList}): {promiseInterfaceName};");
                }
                else
                {
                    var returnType = _projector.MapTypeRefToTypeScript(capability.ReturnType);
                    WriteLine($"    {capability.MethodName}({signature.ParameterList}): Promise<{returnType}>;");
                }
            }
            else
            {
                WriteLine($"    {capability.MethodName}({signature.ParameterList}): {_projector.GetBuilderPromiseInterfaceForMethod(builder, capability)};");
            }
        }

        WriteLine("}");
        WriteLine();
    }

    private void GenerateBuilderPromiseInterface(BuilderModel builder)
    {
        var capabilities = builder.Capabilities.Where(c =>
            c.CapabilityKind != AtsCapabilityKind.PropertyGetter &&
            c.CapabilityKind != AtsCapabilityKind.PropertySetter).ToList();
        var getters = builder.Capabilities.Where(c => c.CapabilityKind == AtsCapabilityKind.PropertyGetter).ToList();
        var setters = builder.Capabilities.Where(c => c.CapabilityKind == AtsCapabilityKind.PropertySetter).ToList();
        var getterOnlyProperties = TypeScriptApiProjector.GroupPropertiesByName(getters, setters)
            .Where(p => TypeScriptApiProjector.IsGetterOnlyProperty(p.Getter, p.Setter))
            .ToList();

        if (capabilities.Count == 0 && getterOnlyProperties.Count == 0)
        {
            return;
        }

        var interfaceName = TypeScriptApiProjector.GetInterfaceName(builder.BuilderClassName);
        var promiseInterfaceName = TypeScriptApiProjector.GetPromiseInterfaceName(builder.BuilderClassName);

        WriteLine($"export interface {promiseInterfaceName} extends PromiseLike<{interfaceName}> {{");

        foreach (var prop in getterOnlyProperties)
        {
            GenerateGetterOnlyPropertyPromiseSignature(prop.PropertyName, prop.Getter!);
        }

        foreach (var capability in capabilities)
        {
            var signature = _projector.ResolveMethodSignature(builder, capability);
            var hasNonBuilderReturn = !capability.ReturnsBuilder && capability.ReturnType != null;

            WriteCapabilityDocComment("    ", capability, signature.RequiredParameters, signature.OptionsParameter?.Name);
            if (hasNonBuilderReturn)
            {
                if (_projector.TryGetPromiseWrapperType(capability.ReturnType, out var returnPromiseInterfaceName, out _))
                {
                    WriteLine($"    {capability.MethodName}({signature.ParameterList}): {returnPromiseInterfaceName};");
                }
                else
                {
                    var returnType = _projector.MapTypeRefToTypeScript(capability.ReturnType);
                    WriteLine($"    {capability.MethodName}({signature.ParameterList}): Promise<{returnType}>;");
                }
            }
            else
            {
                WriteLine($"    {capability.MethodName}({signature.ParameterList}): {_projector.GetBuilderPromiseInterfaceForMethod(builder, capability)};");
            }
        }

        WriteLine("}");
        WriteLine();
    }

    private void GenerateTypeClassInterfaceMethod(BuilderModel model, string className, AtsCapabilityInfo capability)
    {
        var signature = _projector.ResolveMethodSignature(model, capability);
        var isVoid = capability.ReturnType == null || capability.ReturnType.TypeId == AtsConstants.Void;

        WriteCapabilityDocComment("    ", capability, signature.RequiredParameters, signature.OptionsParameter?.Name);
        if (capability.ReturnType != null && _projector.TypesWithPromiseWrappers.Contains(capability.ReturnType.TypeId))
        {
            WriteLine($"    {signature.MethodName}({signature.ParameterList}): {_projector.GetPublicPromiseInterfaceName(capability.ReturnType.TypeId)};");
        }
        else if (isVoid)
        {
            WriteLine($"    {signature.MethodName}({signature.ParameterList}): {TypeScriptApiProjector.GetPromiseInterfaceName(className)};");
        }
        else
        {
            var returnType = _projector.MapTypeRefToTypeScript(capability.ReturnType);
            WriteLine($"    {signature.MethodName}({signature.ParameterList}): Promise<{returnType}>;");
        }
    }

    private void GenerateTypeClassInterface(BuilderModel model)
    {
        var className = TypeScriptApiProjector.DeriveClassName(model.TypeId);
        var interfaceName = TypeScriptApiProjector.GetInterfaceName(className);

        WriteLine("// ============================================================================");
        WriteLine($"// {interfaceName}");
        WriteLine("// ============================================================================");
        WriteLine();
        WriteDocumentationComment(string.Empty, GetHandleDocumentation(model.TypeId));
        WriteLine($"export interface {interfaceName} {{");
        WriteLine("    toJSON(): MarshalledHandle;");

        var getters = model.Capabilities.Where(c => c.CapabilityKind == AtsCapabilityKind.PropertyGetter).ToList();
        var setters = model.Capabilities.Where(c => c.CapabilityKind == AtsCapabilityKind.PropertySetter).ToList();
        var contextMethods = model.Capabilities.Where(c => c.CapabilityKind == AtsCapabilityKind.InstanceMethod).ToList();
        var otherMethods = model.Capabilities.Where(c => c.CapabilityKind == AtsCapabilityKind.Method).ToList();
        var standardMethods = contextMethods.Concat(otherMethods).ToList();
        var hasMethods = standardMethods.Count > 0;

        var properties = TypeScriptApiProjector.GroupPropertiesByName(getters, setters);
        var getterOnlyProperties = properties
            .Where(p => TypeScriptApiProjector.IsGetterOnlyProperty(p.Getter, p.Setter))
            .ToList();
        foreach (var prop in properties)
        {
            GenerateInterfaceProperty(prop.PropertyName, prop.Getter, prop.Setter);
        }

        foreach (var method in standardMethods)
        {
            GenerateTypeClassInterfaceMethod(model, className, method);
        }

        WriteLine("}");
        WriteLine();

        if (!hasMethods && getterOnlyProperties.Count == 0)
        {
            return;
        }

        var promiseInterfaceName = TypeScriptApiProjector.GetPromiseInterfaceName(className);
        WriteLine($"export interface {promiseInterfaceName} extends PromiseLike<{interfaceName}> {{");
        foreach (var prop in getterOnlyProperties)
        {
            GenerateGetterOnlyPropertyPromiseSignature(prop.PropertyName, prop.Getter!);
        }
        foreach (var method in standardMethods)
        {
            GenerateTypeClassInterfaceMethod(model, className, method);
        }
        WriteLine("}");
        WriteLine();
    }

    private void GenerateBuilderClass(BuilderModel builder)
    {
        GenerateBuilderInterface(builder);
        GenerateBuilderPromiseInterface(builder);

        var implementationClassName = TypeScriptApiProjector.GetImplementationClassName(builder.BuilderClassName);

        WriteLine("// ============================================================================");
        WriteLine($"// {implementationClassName}");
        WriteLine("// ============================================================================");
        WriteLine();

        var handleType = TypeScriptApiProjector.GetHandleTypeName(builder.TypeId);

        // Generate builder class extending ResourceBuilderBase
        WriteDocumentationComment(string.Empty, GetHandleDocumentation(builder.TypeId));
        WriteLine($"class {implementationClassName} extends ResourceBuilderBase<{handleType}> implements {builder.BuilderClassName} {{");

        // Constructor
        WriteLine($"    constructor(handle: {handleType}, client: AspireClientRpc) {{");
        WriteLine($"        super(handle, client);");
        WriteLine("    }");
        WriteLine();

        // Generate property getters/setters for resource types with ExposeProperties
        var getters = builder.Capabilities.Where(c => c.CapabilityKind == AtsCapabilityKind.PropertyGetter).ToList();
        var setters = builder.Capabilities.Where(c => c.CapabilityKind == AtsCapabilityKind.PropertySetter).ToList();
        if (getters.Count > 0 || setters.Count > 0)
        {
            var properties = TypeScriptApiProjector.GroupPropertiesByName(getters, setters);
            foreach (var prop in properties)
            {
                GeneratePropertyLikeObject(prop.PropertyName, prop.Getter, prop.Setter);
            }
        }

        // Generate internal methods and public fluent methods
        // Capabilities are already flattened - no need to collect from parents
        // Filter out property getters and setters - they are not methods
        foreach (var capability in builder.Capabilities.Where(c =>
            c.CapabilityKind != AtsCapabilityKind.PropertyGetter &&
            c.CapabilityKind != AtsCapabilityKind.PropertySetter))
        {
            GenerateBuilderMethod(builder, capability);
        }

        WriteLine("}");
        WriteLine();

        // Generate thenable wrapper class
        GenerateThenableClass(builder);
    }

    /// <summary>
    /// Generates both an internal async method and a public fluent method for a builder capability.
    /// </summary>
    /// <remarks>
    /// <para>Produces a pair of methods: a private <c>_*Internal</c> method that performs the RPC call,
    /// and a public method that wraps it in a thenable promise class for fluent chaining.</para>
    /// <para>Generated TypeScript (example for <c>withEnvironment</c> on <c>RedisResource</c>):</para>
    /// <code>
    /// /** @internal */
    /// private async _withEnvironmentInternal(name: string, value: string): Promise&lt;RedisResource&gt; {
    ///     const rpcArgs: Record&lt;string, unknown&gt; = { builder: this._handle, name, value };
    ///     const result = await this._client.invokeCapability&lt;RedisResourceHandle&gt;('...', rpcArgs);
    ///     return new RedisResourceImpl(result, this._client);
    /// }
    ///
    /// withEnvironment(name: string, value: string): RedisResourcePromise {
    ///     return new RedisResourcePromiseImpl(
    ///         this._withEnvironmentInternal(name, value), this._client);
    /// }
    ///
    /// // For build(), the public wrapper flushes pending promises first:
    /// build(): DistributedApplicationPromise {
    ///     const flushAndBuild = async () =&gt; { await this._client.flushPendingPromises(); return this._buildInternal(); };
    ///     return new DistributedApplicationPromiseImpl(flushAndBuild(), this._client, false);
    /// }
    /// </code>
    /// <para>When a parameter is a handle type, promise resolution is emitted before the RPC args
    /// (e.g. <c>db = isPromiseLike(db) ? await db : db;</c>).</para>
    /// </remarks>
    private void GenerateBuilderMethod(BuilderModel builder, AtsCapabilityInfo capability)
    {
        var methodName = capability.MethodName;
        var internalMethodName = $"_{methodName}Internal";
        var targetParamName = capability.TargetParameterName ?? "builder";
        var userParams = capability.Parameters.Where(p => p.Name != targetParamName).ToList();

        // Separate required and optional parameters
        var (requiredParams, optionalParams) = TypeScriptApiProjector.SeparateParameters(userParams);
        var hasOptionals = optionalParams.Count > 0;
        var hasDirectOptionsParameter = TypeScriptApiProjector.TryGetDirectOptionsParameter(optionalParams, out var directOptionsParam);
        var optionsTypeName = hasDirectOptionsParameter ? _projector.MapParameterToTypeScript(directOptionsParam!) : _projector.ResolveOptionsInterfaceName(capability);
        var publicOptionsParamName = TypeScriptApiProjector.GetImplementationOptionsParameterName(userParams, hasOptionals, hasDirectOptionsParameter);

        // Build parameter list for public method
        var publicParamsString = _projector.BuildPublicParameterList(requiredParams, hasOptionals, optionsTypeName, publicOptionsParamName, TypeScriptApiProjector.GetTrailingCancellationTokenParameter(optionalParams));

        // Build parameter list for internal method (all params positional for callback registration)
        var internalParamDefs = new List<string>();
        foreach (var param in userParams)
        {
            var tsType = _projector.MapParameterToTypeScript(param);
            var optional = param.IsOptional || param.IsNullable ? "?" : "";
            internalParamDefs.Add($"{param.Name}{optional}: {tsType}");
        }
        var internalParamsString = string.Join(", ", internalParamDefs);

        // Determine return type - for factory methods returning a different builder type,
        // use the return type's class name instead of the receiver's.
        // Generic fluent methods (e.g., WithDataVolume<T>()) have ReturnType resolved to
        // the constraint type, which equals TargetTypeId — these stay self-returning.
        // Factory methods (e.g., AddDatabase) return a type different from both the builder
        // AND the target type — these use the actual return type.
        var returnTypeId = builder.TypeId;
        var returnClassName = builder.BuilderClassName;
        if (capability.ReturnsBuilder && capability.ReturnType?.TypeId != null &&
            !string.Equals(capability.ReturnType.TypeId, builder.TypeId, StringComparison.Ordinal) &&
            !string.Equals(capability.ReturnType.TypeId, capability.TargetTypeId, StringComparison.Ordinal))
        {
            returnTypeId = capability.ReturnType.TypeId;
            returnClassName = _projector.WrapperClassNames.GetValueOrDefault(returnTypeId)
                ?? TypeScriptApiProjector.DeriveClassName(returnTypeId);
        }
        var returnHandle = capability.ReturnsBuilder
            ? TypeScriptApiProjector.GetHandleTypeName(returnTypeId)
            : "void";
        var returnsBuilder = capability.ReturnsBuilder;
        var returnImplementationClassName = TypeScriptApiProjector.GetImplementationClassName(returnClassName);

        // Check if this method returns a non-builder, non-void type (e.g., getEndpoint returns EndpointReference)
        var hasNonBuilderReturn = !returnsBuilder && capability.ReturnType != null;
        if (hasNonBuilderReturn)
        {
            if (_projector.TryGetPromiseWrapperType(capability.ReturnType, out var returnPromiseInterfaceName, out var returnPromiseImplementationClassName))
            {
                var wrappedReturnTypeId = capability.ReturnType!.TypeId;
                var wrappedReturnClassName = _projector.GetConcreteClassName(wrappedReturnTypeId);
                var returnImplementationClassNameForWrapper = TypeScriptApiProjector.GetImplementationClassName(wrappedReturnClassName);
                var returnHandleType = TypeScriptApiProjector.GetHandleTypeName(wrappedReturnTypeId);

                WriteCapabilityDocComment("    ", capability, requiredParams, hasOptionals ? publicOptionsParamName : null);
                Write($"    {methodName}(");
                Write(publicParamsString);
                WriteLine($"): {returnPromiseInterfaceName} {{");
                WriteLine("        const promise = (async () => {");

                foreach (var param in hasDirectOptionsParameter ? [] : optionalParams)
                {
                    var localParameterName = GetLocalParameterName(param);
                    WriteLine($"            {(_projector.IsWidenedHandleType(param.Type) ? "let" : "const")} {localParameterName} = {publicOptionsParamName}?.{param.Name};");
                }

                var callbackParamsForPromiseWrapper = userParams.Where(p => p.IsCallback).ToList();
                foreach (var callbackParam in callbackParamsForPromiseWrapper)
                {
                    GenerateCallbackRegistration(callbackParam, "            ");
                }

                GeneratePromiseResolution(userParams, "            ");
                GenerateArgsObjectWithConditionals(targetParamName, requiredParams, optionalParams, useSafeOptionalLocalNames: true, indent: "            ");

                WriteLine($"            const handle = await this._client.invokeCapability<{returnHandleType}>(");
                WriteLine($"                '{capability.CapabilityId}',");
                WriteLine("                rpcArgs");
                WriteLine("            );");
                WriteLine($"            return new {returnImplementationClassNameForWrapper}(handle, this._client);");
                WriteLine("        })();");
                WriteLine($"        return new {returnPromiseImplementationClassName}(promise, this._client);");
                WriteLine("    }");
                WriteLine();
                return;
            }

            // Generate a simple async method that returns the actual type
            var returnType = _projector.MapTypeRefToTypeScript(capability.ReturnType);

            WriteCapabilityDocComment("    ", capability, requiredParams, hasOptionals ? publicOptionsParamName : null);
            Write($"    async {methodName}(");
            Write(publicParamsString);
            WriteLine($"): Promise<{returnType}> {{");

            // Extract optional params from options object
            foreach (var param in hasDirectOptionsParameter ? [] : optionalParams)
            {
                var localParameterName = GetLocalParameterName(param);
                WriteLine($"        {(_projector.IsWidenedHandleType(param.Type) ? "let" : "const")} {localParameterName} = {publicOptionsParamName}?.{param.Name};");
            }

            // Handle callback registration if any
            var callbackParams2 = userParams.Where(p => p.IsCallback).ToList();
            foreach (var callbackParam in callbackParams2)
            {
                GenerateCallbackRegistration(callbackParam);
            }

            // Resolve any promise-like handle parameters before building rpcArgs
            GeneratePromiseResolution(userParams);

            // Build args object with conditional inclusion
            GenerateArgsObjectWithConditionals(targetParamName, requiredParams, optionalParams, useSafeOptionalLocalNames: true);

            if (capability.ReturnType?.TypeId == AtsConstants.CancellationToken)
            {
                WriteLine("        const result = await this._client.invokeCapability<string | null>(");
                WriteLine($"            '{capability.CapabilityId}',");
                WriteLine("            rpcArgs");
                WriteLine("        );");
                WriteLine("        return CancellationToken.fromValue(result);");
            }
            else
            {
                WriteLine($"        return await this._client.invokeCapability<{returnType}>(");
                WriteLine($"            '{capability.CapabilityId}',");
                WriteLine($"            rpcArgs");
                WriteLine("        );");
            }
            WriteLine("    }");
            WriteLine();
            return;
        }

        // Generate internal async method for fluent builder methods
        WriteLine($"    /** @internal */");
        Write($"    private async {internalMethodName}(");
        Write(internalParamsString);
        Write($"): Promise<{returnClassName}> {{");
        WriteLine();

        // Handle callback registration if any
        var callbackParams = userParams.Where(p => p.IsCallback).ToList();
        foreach (var callbackParam in callbackParams)
        {
            GenerateCallbackRegistration(callbackParam);
        }

        // Resolve any promise-like handle parameters before building rpcArgs
        GeneratePromiseResolution(userParams);

        // Build args object with conditional inclusion
        GenerateArgsObjectWithConditionals(targetParamName, requiredParams, optionalParams);

        if (returnsBuilder)
        {
            WriteLine($"        const result = await this._client.invokeCapability<{returnHandle}>(");
            WriteLine($"            '{capability.CapabilityId}',");
            WriteLine($"            rpcArgs");
            WriteLine("        );");
            WriteLine($"        return new {returnImplementationClassName}(result, this._client);");
        }
        else
        {
            WriteLine($"        await this._client.invokeCapability<void>(");
            WriteLine($"            '{capability.CapabilityId}',");
            WriteLine($"            rpcArgs");
            WriteLine("        );");
            WriteLine($"        return this;");
        }
        WriteLine("    }");
        WriteLine();

        // Generate public fluent method (returns thenable wrapper)
        var promiseClass = $"{returnClassName}Promise";
        var promiseImplementationClass = TypeScriptApiProjector.GetImplementationPromiseClassName(returnClassName);
        WriteCapabilityDocComment("    ", capability, requiredParams, hasOptionals ? publicOptionsParamName : null);
        Write($"    {methodName}(");
        Write(publicParamsString);
        Write($"): {promiseClass} {{");
        WriteLine();

        // Extract optional params from options object and forward to internal method
        foreach (var param in hasDirectOptionsParameter ? [] : optionalParams)
        {
            var localParameterName = GetLocalParameterName(param);
            WriteLine($"        {(_projector.IsWidenedHandleType(param.Type) ? "let" : "const")} {localParameterName} = {publicOptionsParamName}?.{param.Name};");
        }

        // Forward all params to internal method
        var allParamNames = userParams.Select(p => optionalParams.Contains(p) ? GetLocalParameterName(p) : p.Name);
        var internalCall = $"this.{internalMethodName}({string.Join(", ", allParamNames)})";

        // For build(), flush pending promises before invoking the internal method.
        // This must happen in the public wrapper (not _buildInternal) to avoid deadlock:
        // the PromiseImpl constructor tracks the build promise, and if _buildInternal
        // awaited flushPendingPromises, the flush would re-await the tracked build promise.
        if (string.Equals(capability.MethodName, "build", StringComparison.OrdinalIgnoreCase))
        {
            WriteLine($"        const flushAndBuild = async () => {{ await this._client.flushPendingPromises(); return {internalCall}; }};");
            // Don't track the build promise — it wraps flushPendingPromises which
            // may throw AggregateError. Tracking it would re-add that error to
            // _rejectedErrors, poisoning subsequent build() calls.
            WriteLine($"        return new {promiseImplementationClass}(flushAndBuild(), this._client, false);");
        }
        else
        {
            WriteLine($"        return new {promiseImplementationClass}({internalCall}, this._client);");
        }
        WriteLine("    }");
        WriteLine();
    }

    /// <summary>
    /// Generates promise resolution code for handle-type parameters that may be PromiseLike.
    /// </summary>
    /// <remarks>
    /// For each parameter whose type is a handle (or union containing handles), emits a line
    /// that awaits it if it is a <c>PromiseLike</c>. Non-handle and callback parameters are skipped.
    /// <code>
    /// // For a handle-type param 'db':
    /// db = isPromiseLike(db) ? await db : db;
    ///
    /// // For a non-handle param 'name' (string): nothing emitted
    /// </code>
    /// </remarks>
    private void GeneratePromiseResolution(IReadOnlyList<AtsParameterInfo> parameters, string indent = "        ")
    {
        foreach (var param in parameters)
        {
            if (param.IsCallback)
            {
                continue;
            }

            if (_projector.IsWidenedHandleType(param.Type))
            {
                WriteLine($"{indent}{param.Name} = isPromiseLike({param.Name}) ? await {param.Name} : {param.Name};");
            }
        }
    }

    /// <summary>
    /// Generates promise resolution for a single named parameter.
    /// </summary>
    /// <remarks>
    /// Used for property setters where only the <c>value</c> parameter needs resolution.
    /// <code>
    /// // For a handle-type 'value' parameter:
    /// value = isPromiseLike(value) ? await value : value;
    /// </code>
    /// </remarks>
    private void GeneratePromiseResolutionForParam(string paramName, AtsTypeRef? paramType, string indent = "        ")
    {
        if (_projector.IsWidenedHandleType(paramType))
        {
            WriteLine($"{indent}{paramName} = isPromiseLike({paramName}) ? await {paramName} : {paramName};");
        }
    }

    /// <summary>
    /// Generates promise resolution and args object construction in one step.
    /// This is the unified helper used by builder methods, type class methods, context methods, and wrapper methods.
    /// </summary>
    /// <remarks>
    /// Combines <see cref="GeneratePromiseResolution"/> with RPC args construction.
    /// Required parameters are inlined in the object literal; optional parameters
    /// are added conditionally.
    /// <code>
    /// // Example output for a method with required 'name', handle-type 'db', and optional 'timeout':
    /// db = isPromiseLike(db) ? await db : db;
    /// const rpcArgs: Record&lt;string, unknown&gt; = { builder: this._handle, name, db };
    /// if (timeout !== undefined) rpcArgs.timeout = timeout;
    /// </code>
    /// </remarks>
    private void GenerateResolveAndBuildArgs(
        string targetParamName,
        IReadOnlyList<AtsParameterInfo> allParams,
        List<AtsParameterInfo> requiredParams,
        List<AtsParameterInfo> optionalParams,
        bool useSafeOptionalLocalNames = false,
        string indent = "        ")
    {
        // Resolve any promise-like handle parameters
        GeneratePromiseResolution(allParams, indent);

        // DTO callback properties are sent over the wire as callback IDs, just like direct
        // callback parameters. Copy the DTO before replacing function-valued properties so
        // callers keep their original options object unchanged.
        GenerateDtoCallbackPropertyMarshalling(requiredParams.Concat(optionalParams), useSafeOptionalLocalNames, indent);

        // Build the required args inline
        var requiredArgs = new List<string> { $"{targetParamName}: this._handle" };
        foreach (var param in requiredParams)
        {
            requiredArgs.Add(GetRpcArgumentEntryForParam(param, param.Name));
        }

        WriteLine($"{indent}const rpcArgs: Record<string, unknown> = {{ {string.Join(", ", requiredArgs)} }};");

        // Conditionally add optional params
        foreach (var param in optionalParams)
        {
            var localParameterName = useSafeOptionalLocalNames ? GetLocalParameterName(param) : param.Name;
            var rpcExpression = GetRpcArgumentExpressionForParam(param, localParameterName);
            WriteLine($"{indent}if ({localParameterName} !== undefined) rpcArgs.{param.Name} = {rpcExpression};");
        }
    }

    /// <summary>
    /// Generates an args object with conditional inclusion of optional parameters.
    /// </summary>
    private void GenerateArgsObjectWithConditionals(
        string targetParamName,
        List<AtsParameterInfo> requiredParams,
        List<AtsParameterInfo> optionalParams,
        bool useSafeOptionalLocalNames = false,
        string indent = "        ")
    {
        // DTO callback properties are sent over the wire as callback IDs, just like direct
        // callback parameters. Copy the DTO before replacing function-valued properties so
        // callers keep their original options object unchanged.
        GenerateDtoCallbackPropertyMarshalling(requiredParams.Concat(optionalParams), useSafeOptionalLocalNames, indent);

        // Build the required args inline
        var requiredArgs = new List<string> { $"{targetParamName}: this._handle" };
        foreach (var param in requiredParams)
        {
            requiredArgs.Add(GetRpcArgumentEntryForParam(param, param.Name));
        }

        WriteLine($"{indent}const rpcArgs: Record<string, unknown> = {{ {string.Join(", ", requiredArgs)} }};");

        // Conditionally add optional params
        foreach (var param in optionalParams)
        {
            var localParameterName = useSafeOptionalLocalNames ? GetLocalParameterName(param) : param.Name;
            var rpcExpression = GetRpcArgumentExpressionForParam(param, localParameterName);
            WriteLine($"{indent}if ({localParameterName} !== undefined) rpcArgs.{param.Name} = {rpcExpression};");
        }
    }

    private void GenerateDtoCallbackPropertyMarshalling(
        IEnumerable<AtsParameterInfo> parameters,
        bool useSafeOptionalLocalNames,
        string indent,
        string clientExpression = "this._client")
    {
        foreach (var param in parameters)
        {
            if (!TryGetDtoCallbackMarshallingProperties(param.Type, out var marshallingProperties))
            {
                continue;
            }

            if (param.Type is not { TypeId: var dtoTypeId })
            {
                continue;
            }

            var localParameterName = useSafeOptionalLocalNames ? GetLocalParameterName(param) : param.Name;
            var dtoRpcLocalName = GetDtoRpcLocalName(localParameterName);
            var visitedDtoTypeIds = new HashSet<string>(StringComparer.Ordinal) { dtoTypeId };
            if (param.IsOptional || param.IsNullable)
            {
                WriteLine($"{indent}const {dtoRpcLocalName} = {localParameterName} === undefined || {localParameterName} === null ? {localParameterName} : {{ ...{localParameterName} }};");
                WriteLine($"{indent}if ({dtoRpcLocalName} !== undefined && {dtoRpcLocalName} !== null) {{");
                GenerateDtoCallbackPropertyAssignments(dtoRpcLocalName, marshallingProperties, visitedDtoTypeIds, $"{indent}    ", clientExpression);
                WriteLine($"{indent}}}");
            }
            else
            {
                WriteLine($"{indent}const {dtoRpcLocalName} = {localParameterName} === null ? {localParameterName} : {{ ...{localParameterName} }};");
                WriteLine($"{indent}if ({dtoRpcLocalName} !== null) {{");
                GenerateDtoCallbackPropertyAssignments(dtoRpcLocalName, marshallingProperties, visitedDtoTypeIds, $"{indent}    ", clientExpression);
                WriteLine($"{indent}}}");
            }
        }
    }

    private void GenerateDtoCallbackPropertyAssignments(
        string dtoRpcLocalName,
        IReadOnlyList<AtsDtoPropertyInfo> marshallingProperties,
        HashSet<string> visitedDtoTypeIds,
        string indent,
        string clientExpression)
    {
        var dtoDataLocalName = $"{dtoRpcLocalName}Data";
        WriteLine($"{indent}const {dtoDataLocalName} = {dtoRpcLocalName} as Record<string, unknown>;");

        foreach (var marshallingProperty in marshallingProperties)
        {
            if (marshallingProperty.IsCallback)
            {
                var propertyName = TypeScriptApiProjector.ToCamelCase(marshallingProperty.Name);
                var callbackLocalName = GetDtoCallbackLocalName(dtoRpcLocalName, marshallingProperty.Name);
                WriteLine($"{indent}const {callbackLocalName} = {dtoRpcLocalName}.{propertyName};");
                WriteLine($"{indent}if ({callbackLocalName} !== undefined) {{");
                GenerateCallbackRegistration(CreateCallbackParameter(marshallingProperty, callbackLocalName), $"{indent}    ", clientExpression);
                WriteLine($"{indent}    {dtoDataLocalName}[\"{propertyName}\"] = {callbackLocalName}Id;");
                WriteLine($"{indent}}}");
                continue;
            }

            GenerateNestedDtoCallbackPropertyAssignments(dtoRpcLocalName, dtoDataLocalName, marshallingProperty, visitedDtoTypeIds, indent, clientExpression);
        }
    }

    private void GenerateNestedDtoCallbackPropertyAssignments(
        string dtoRpcLocalName,
        string dtoDataLocalName,
        AtsDtoPropertyInfo dtoProperty,
        HashSet<string> visitedDtoTypeIds,
        string indent,
        string clientExpression)
    {
        if (!TryGetDtoCallbackMarshallingProperties(dtoProperty.Type, out var nestedMarshallingProperties))
        {
            return;
        }

        var propertyName = TypeScriptApiProjector.ToCamelCase(dtoProperty.Name);
        var dtoPropertyLocalName = GetDtoCallbackLocalName(dtoRpcLocalName, dtoProperty.Name);
        var nestedDtoRpcLocalName = $"{dtoPropertyLocalName}ForRpc";

        if (!visitedDtoTypeIds.Add(dtoProperty.Type.TypeId))
        {
            return;
        }

        try
        {
            WriteLine($"{indent}const {dtoPropertyLocalName} = {dtoRpcLocalName}.{propertyName};");
            WriteLine($"{indent}if ({dtoPropertyLocalName} !== undefined && {dtoPropertyLocalName} !== null) {{");
            WriteLine($"{indent}    const {nestedDtoRpcLocalName} = {{ ...{dtoPropertyLocalName} }};");
            GenerateDtoCallbackPropertyAssignments(nestedDtoRpcLocalName, nestedMarshallingProperties, visitedDtoTypeIds, $"{indent}    ", clientExpression);
            WriteLine($"{indent}    {dtoDataLocalName}[\"{propertyName}\"] = {nestedDtoRpcLocalName};");
            WriteLine($"{indent}}}");
        }
        finally
        {
            visitedDtoTypeIds.Remove(dtoProperty.Type.TypeId);
        }
    }

    private static AtsParameterInfo CreateCallbackParameter(AtsDtoPropertyInfo callbackProperty, string callbackLocalName)
        => new()
        {
            Name = callbackLocalName,
            Type = callbackProperty.Type,
            IsOptional = callbackProperty.IsOptional,
            IsNullable = callbackProperty.Type.IsNullable == true,
            IsCallback = true,
            CallbackParameters = callbackProperty.CallbackParameters,
            CallbackReturnType = callbackProperty.CallbackReturnType
        };

    /// <summary>
    /// Generates a thenable wrapper class for a builder that enables fluent chaining.
    /// </summary>
    /// <remarks>
    /// <para>The generated class implements both <c>PromiseLike</c> (via <c>then()</c>) and
    /// all fluent methods of the builder, forwarding each call through the inner promise.</para>
    /// <para>Generated TypeScript (example for <c>RedisResource</c>):</para>
    /// <code>
    /// class RedisResourcePromiseImpl implements RedisResourcePromise {
    ///     constructor(private _promise: Promise&lt;RedisResource&gt;, private _client: AspireClientRpc) {
    ///         _client.trackPromise(_promise);
    ///     }
    ///     then&lt;T1, T2&gt;(...): PromiseLike&lt;T1 | T2&gt; {
    ///         return this._promise.then(...);
    ///     }
    ///     withEnvironment(name: string, value: string): RedisResourcePromise {
    ///         return new RedisResourcePromiseImpl(
    ///             this._promise.then(obj =&gt; obj.withEnvironment(name, value)), this._client);
    ///     }
    /// }
    /// </code>
    /// </remarks>
    private void GenerateThenableClass(BuilderModel builder)
    {
        var capabilities = builder.Capabilities.Where(c =>
            c.CapabilityKind != AtsCapabilityKind.PropertyGetter &&
            c.CapabilityKind != AtsCapabilityKind.PropertySetter).ToList();
        var getters = builder.Capabilities.Where(c => c.CapabilityKind == AtsCapabilityKind.PropertyGetter).ToList();
        var setters = builder.Capabilities.Where(c => c.CapabilityKind == AtsCapabilityKind.PropertySetter).ToList();
        var getterOnlyProperties = TypeScriptApiProjector.GroupPropertiesByName(getters, setters)
            .Where(p => TypeScriptApiProjector.IsGetterOnlyProperty(p.Getter, p.Setter))
            .ToList();

        if (capabilities.Count == 0 && getterOnlyProperties.Count == 0)
        {
            return;
        }

        var promiseClass = $"{builder.BuilderClassName}Promise";
        var promiseImplementationClass = TypeScriptApiProjector.GetImplementationPromiseClassName(builder.BuilderClassName);

        WriteLine($"/**");
        WriteLine($" * Thenable wrapper for {builder.BuilderClassName} that enables fluent chaining.");
        WriteLine($" * @example");
        WriteLine($" * await builder.addSomething().withX().withY();");
        WriteLine($" */");
        WriteLine($"class {promiseImplementationClass} implements {promiseClass} {{");
        WriteLine($"    constructor(private _promise: Promise<{builder.BuilderClassName}>, private _client: AspireClientRpc, track = true) {{");
        WriteLine($"        if (track) {{ _client.trackPromise(_promise); }}");
        WriteLine($"    }}");
        WriteLine();

        // Generate then() for PromiseLike interface
        WriteLine($"    then<TResult1 = {builder.BuilderClassName}, TResult2 = never>(");
        WriteLine($"        onfulfilled?: ((value: {builder.BuilderClassName}) => TResult1 | PromiseLike<TResult1>) | null,");
        WriteLine("        onrejected?: ((reason: unknown) => TResult2 | PromiseLike<TResult2>) | null");
        WriteLine("    ): PromiseLike<TResult1 | TResult2> {");
        WriteLine("        return this._promise.then(onfulfilled, onrejected);");
        WriteLine("    }");
        WriteLine();

        foreach (var prop in getterOnlyProperties)
        {
            var returnType = _projector.GetGetterOnlyPropertyMethodReturnType(prop.Getter!.ReturnType);
            WriteLine($"    {prop.PropertyName}(): {returnType} {{");
            if (_projector.TryGetPromiseWrapperType(prop.Getter!.ReturnType, out _, out var promiseImplementationClassName))
            {
                WriteLine($"        return new {promiseImplementationClassName}(this._promise.then(obj => obj.{prop.PropertyName}()), this._client, false);");
            }
            else
            {
                WriteLine($"        return this._promise.then(obj => obj.{prop.PropertyName}());");
            }
            WriteLine("    }");
            WriteLine();
        }

        // Generate fluent methods that chain via .then()
        // Capabilities are already flattened - no need to collect from parents
        // Filter out property getters and setters - they are not methods
        foreach (var capability in capabilities)
        {
            var signature = _projector.ResolveMethodSignature(builder, capability);

            // Forward args to underlying object's method (which handles options extraction)
            var forwardArgs = signature.RequiredParameters
                .Select(parameter => parameter.Name)
                .ToList();
            if (signature.OptionsParameter is { } optionsParameter)
            {
                forwardArgs.Add(optionsParameter.Name);
            }
            if (signature.TrailingCancellationToken is { } cancellationToken)
            {
                forwardArgs.Add(cancellationToken.Name);
            }
            var argsString = string.Join(", ", forwardArgs);

            // Check if this method returns a non-builder type
            var hasNonBuilderReturn = !capability.ReturnsBuilder && capability.ReturnType != null;

            if (hasNonBuilderReturn)
            {
                if (_projector.TryGetPromiseWrapperType(capability.ReturnType, out var returnPromiseInterfaceName, out var returnPromiseImplementationClassName))
                {
                    Write($"    {signature.MethodName}(");
                    Write(signature.ParameterList);
                    WriteLine($"): {returnPromiseInterfaceName} {{");
                    Write($"        return new {returnPromiseImplementationClassName}(this._promise.then(obj => obj.{signature.MethodName}(");
                    Write(argsString);
                    WriteLine(")), this._client);");
                    WriteLine("    }");
                    WriteLine();
                    continue;
                }

                // For non-builder returns, call the public method directly
                var returnType = _projector.MapTypeRefToTypeScript(capability.ReturnType);
                Write($"    {signature.MethodName}(");
                Write(signature.ParameterList);
                WriteLine($"): Promise<{returnType}> {{");
                Write($"        return this._promise.then(obj => obj.{signature.MethodName}(");
                Write(argsString);
                WriteLine("));");
                WriteLine("    }");
            }
            else
            {
                // For fluent builder methods, determine the correct promise class.
                // Factory methods returning a different builder type use the return type's promise class.
                var methodPromiseClass = promiseClass;
                var methodPromiseImplementationClass = promiseImplementationClass;
                if (capability.ReturnsBuilder && capability.ReturnType?.TypeId != null &&
                    !string.Equals(capability.ReturnType.TypeId, builder.TypeId, StringComparison.Ordinal) &&
                    !string.Equals(capability.ReturnType.TypeId, capability.TargetTypeId, StringComparison.Ordinal))
                {
                    var returnClass = _projector.WrapperClassNames.GetValueOrDefault(capability.ReturnType.TypeId)
                        ?? TypeScriptApiProjector.DeriveClassName(capability.ReturnType.TypeId);
                    methodPromiseClass = $"{returnClass}Promise";
                    methodPromiseImplementationClass = TypeScriptApiProjector.GetImplementationPromiseClassName(returnClass);
                }

                Write($"    {signature.MethodName}(");
                Write(signature.ParameterList);
                Write($"): {methodPromiseClass} {{");
                WriteLine();
                // Forward to the public method on the underlying object, wrapping result in promise class
                Write($"        return new {methodPromiseImplementationClass}(this._promise.then(obj => obj.{signature.MethodName}(");
                Write(argsString);
                WriteLine($")), this._client);");
                WriteLine("    }");
            }
            WriteLine();
        }

        WriteLine("}");
        WriteLine();
    }

    private void GenerateAspireClient(List<AtsCapabilityInfo> entryPoints)
    {
        // Entry point methods (capabilities with no TargetTypeId) are generated as standalone functions
        // They're generated in GenerateConnectionHelper after the createBuilder() function
        // This method now only handles the comment header
        if (entryPoints.Count > 0)
        {
            WriteLine("// ============================================================================");
            WriteLine("// Entry Point Functions");
            WriteLine("// ============================================================================");
            WriteLine();

            foreach (var capability in entryPoints)
            {
                GenerateEntryPointFunction(capability);
            }
        }
    }

    /// <summary>
    /// Generates an exported entry-point function that creates a builder via an async IIFE.
    /// </summary>
    /// <remarks>
    /// <para>Entry-point functions are standalone exports (not class methods). They wrap the
    /// RPC call in an async IIFE and return a thenable promise class for immediate chaining.</para>
    /// <para>Generated TypeScript (example for <c>createBuilder</c>):</para>
    /// <code>
    /// export function createBuilder(client: AspireClientRpc): DistributedApplicationBuilderPromise {
    ///     const promise = (async () =&gt; {
    ///         const rpcArgs: Record&lt;string, unknown&gt; = { };
    ///         const handle = await client.invokeCapability&lt;DistributedApplicationBuilderHandle&gt;(
    ///             'aspire.capability.createBuilder', rpcArgs);
    ///         return new DistributedApplicationBuilderImpl(handle, client);
    ///     })();
    ///     return new DistributedApplicationBuilderPromiseImpl(promise, client);
    /// }
    /// </code>
    /// </remarks>
    private void GenerateEntryPointFunction(AtsCapabilityInfo capability)
    {
        var methodName = capability.MethodName;

        // Resolved once and shared with the canonical exporter so the emitted function and the
        // declaration that documents it cannot describe different parameter lists.
        var signature = _projector.ResolveEntryPointSignature(capability);
        var paramsString = signature.ParameterList;
        var (requiredParams, optionalParams) = TypeScriptApiProjector.SeparateParameters(capability.Parameters);

        // Determine return type - check if return type has a Promise wrapper
        var capReturnTypeId = GetReturnTypeId(capability);
        var returnPromiseWrapper = _projector.GetPromiseWrapperForReturnType(capability.ReturnType);

        // Generate JSDoc
        WriteCapabilityDocComment(string.Empty, capability);

        // Generate function based on return type
        if (returnPromiseWrapper != null && !string.IsNullOrEmpty(capReturnTypeId))
        {
            // Return type has Promise wrapper - generate fluent function
            var returnWrapperClass = _projector.WrapperClassNames.GetValueOrDefault(capReturnTypeId)
                ?? TypeScriptApiProjector.DeriveClassName(capReturnTypeId);
            var returnWrapperImplementationClass = TypeScriptApiProjector.GetImplementationClassName(returnWrapperClass);
            var returnPromiseImplementationClass = TypeScriptApiProjector.GetImplementationPromiseClassName(returnWrapperClass);
            var handleType = TypeScriptApiProjector.GetHandleTypeName(capReturnTypeId);

            Write($"export function {methodName}(");
            Write(paramsString);
            WriteLine($"): {signature.ReturnType} {{");
            // Use async IIFE to resolve promise-like handle params before RPC
            WriteLine($"    const promise = (async () => {{");
            // Resolve promise-like handle params
            foreach (var param in capability.Parameters)
            {
                if (!param.IsCallback && _projector.IsWidenedHandleType(param.Type))
                {
                    WriteLine($"        {param.Name} = isPromiseLike({param.Name}) ? await {param.Name} : {param.Name};");
                }
            }
            GenerateDtoCallbackPropertyMarshalling(capability.Parameters, useSafeOptionalLocalNames: false, indent: "        ", clientExpression: "client");
            var requiredArgs = requiredParams
                .Select(param => GetRpcArgumentEntryForParam(param, param.Name, useRegisteredCallback: false))
                .ToList();
            WriteLine($"        const rpcArgs: Record<string, unknown> = {{ {string.Join(", ", requiredArgs)} }};");
            foreach (var param in optionalParams)
            {
                WriteLine($"        if ({param.Name} !== undefined) rpcArgs.{param.Name} = {GetRpcArgumentExpressionForParam(param, param.Name, useRegisteredCallback: false)};");
            }
            WriteLine($"        const handle = await client.invokeCapability<{handleType}>(");
            WriteLine($"            '{capability.CapabilityId}',");
            WriteLine("            rpcArgs");
            WriteLine("        );");
            WriteLine($"        return new {returnWrapperImplementationClass}(handle, client);");
            WriteLine($"    }})();");
            WriteLine($"    return new {returnPromiseImplementationClass}(promise, client);");
            WriteLine("}");
        }
        else
        {
            // No Promise wrapper - return plain value
            var returnType = !string.IsNullOrEmpty(capReturnTypeId)
                ? _projector.MapTypeRefToTypeScript(capability.ReturnType)
                : "void";

            Write($"export async function {methodName}(");
            Write(paramsString);
            WriteLine($"): {signature.ReturnType} {{");
            // Resolve promise-like handle params
            foreach (var param in capability.Parameters)
            {
                if (!param.IsCallback && _projector.IsWidenedHandleType(param.Type))
                {
                    WriteLine($"    {param.Name} = isPromiseLike({param.Name}) ? await {param.Name} : {param.Name};");
                }
            }
            GenerateDtoCallbackPropertyMarshalling(capability.Parameters, useSafeOptionalLocalNames: false, indent: "    ", clientExpression: "client");
            var requiredArgs = requiredParams
                .Select(param => GetRpcArgumentEntryForParam(param, param.Name, useRegisteredCallback: false))
                .ToList();
            WriteLine($"    const rpcArgs: Record<string, unknown> = {{ {string.Join(", ", requiredArgs)} }};");
            foreach (var param in optionalParams)
            {
                WriteLine($"    if ({param.Name} !== undefined) rpcArgs.{param.Name} = {GetRpcArgumentExpressionForParam(param, param.Name, useRegisteredCallback: false)};");
            }
            if (returnType == "void")
            {
                WriteLine($"    await client.invokeCapability<void>(");
            }
            else if (capability.ReturnType?.TypeId == AtsConstants.CancellationToken)
            {
                WriteLine("    const result = await client.invokeCapability<string | null>(");
                WriteLine($"        '{capability.CapabilityId}',");
                WriteLine("        rpcArgs");
                WriteLine("    );");
                WriteLine("    return CancellationToken.fromValue(result);");
                WriteLine("}");
                WriteLine();
                return;
            }
            else
            {
                WriteLine($"    return await client.invokeCapability<{returnType}>(");
            }
            WriteLine($"        '{capability.CapabilityId}',");
            WriteLine("        rpcArgs");
            WriteLine("    );");
            WriteLine("}");
        }
        WriteLine();
    }

    private void GenerateCallbackRegistration(AtsParameterInfo callbackParam, string indent = "        ", string clientExpression = "this._client")
    {
        var callbackParameters = callbackParam.CallbackParameters;
        var isOptional = callbackParam.IsOptional || callbackParam.IsNullable;
        var callbackName = callbackParam.Name;

        // Determine parameter signature for registerCallback
        string paramSignature;
        if (callbackParameters is null || callbackParameters.Count == 0)
        {
            paramSignature = "";
        }
        else if (callbackParameters.Count == 1)
        {
            paramSignature = $"{callbackParameters[0].Name}Data: unknown";
        }
        else
        {
            paramSignature = string.Join(", ", callbackParameters.Select(p => $"{p.Name}Data: unknown"));
        }

        // For optional callbacks, wrap the registration in a conditional
        if (isOptional)
        {
            WriteLine($"{indent}const {callbackName}Id = {callbackName} ? registerCallback(async ({paramSignature}) => {{");
        }
        else
        {
            WriteLine($"{indent}const {callbackName}Id = registerCallback(async ({paramSignature}) => {{");
        }

        // Generate the callback body
        GenerateCallbackBody(callbackParam, callbackParameters, indent, clientExpression);

        // Close the callback registration
        if (isOptional)
        {
            WriteLine(indent + "}) : undefined;");
        }
        else
        {
            WriteLine(indent + "});");
        }
    }

    /// <summary>
    /// Generates the body of a callback function.
    /// </summary>
    private void GenerateCallbackBody(AtsParameterInfo callbackParam, IReadOnlyList<AtsCallbackParameterInfo>? callbackParameters, string indent, string clientExpression)
    {
        var callbackName = callbackParam.Name;
        var bodyIndent = $"{indent}    ";

        // Check if callback has a return type - if so, we need to return the value
        var hasReturnType = callbackParam.CallbackReturnType != null
            && callbackParam.CallbackReturnType.TypeId != AtsConstants.Void;
        var returnPrefix = hasReturnType ? "return " : "";

        if (callbackParameters is null || callbackParameters.Count == 0)
        {
            // No parameters - just call the callback
            WriteLine($"{bodyIndent}{returnPrefix}await {callbackName}();");
        }
        else if (callbackParameters.Count == 1)
        {
            // Single parameter callback
            var cbParam = callbackParameters[0];
            GenerateCallbackParameterConversion(cbParam, $"{cbParam.Name}Data", clientExpression, bodyIndent);

            WriteLine($"{bodyIndent}{returnPrefix}await {callbackName}({cbParam.Name});");
        }
        else
        {
            var callArgs = new List<string>();
            for (var i = 0; i < callbackParameters.Count; i++)
            {
                var cbParam = callbackParameters[i];
                var callbackArgName = $"{cbParam.Name}Data";

                GenerateCallbackParameterConversion(cbParam, callbackArgName, clientExpression, bodyIndent);
                callArgs.Add(cbParam.Name);
            }

            WriteLine($"{bodyIndent}{returnPrefix}await {callbackName}({string.Join(", ", callArgs)});");
        }
    }

    private void GenerateCallbackParameterConversion(AtsCallbackParameterInfo callbackParameter, string callbackArgName, string clientExpression, string indent)
    {
        var tsType = _projector.MapTypeRefToTypeScript(callbackParameter.Type);
        var cbTypeId = callbackParameter.Type.TypeId;

        if (cbTypeId == AtsConstants.CancellationToken)
        {
            WriteLine($"{indent}const {callbackParameter.Name} = CancellationToken.fromValue({callbackArgName});");
        }
        else if (TypeScriptApiProjector.IsDictionaryType(callbackParameter.Type) && !callbackParameter.Type.IsReadOnly)
        {
            var keyType = _projector.MapTypeRefToTypeScript(callbackParameter.Type.KeyType);
            var valueType = _projector.MapTypeRefToTypeScript(callbackParameter.Type.ValueType);
            var handleType = TypeScriptApiProjector.GetHandleTypeName(cbTypeId);

            WriteLine($"{indent}const {callbackParameter.Name}Handle = wrapIfHandle({callbackArgName}) as {handleType};");
            WriteLine($"{indent}const {callbackParameter.Name} = new AspireDict<{keyType}, {valueType}>({callbackParameter.Name}Handle, {clientExpression}, '{cbTypeId}');");
        }
        else if (_projector.WrapperClassNames.TryGetValue(cbTypeId, out var wrapperClassName))
        {
            var handleType = TypeScriptApiProjector.GetHandleTypeName(cbTypeId);
            WriteLine($"{indent}const {callbackParameter.Name}Handle = wrapIfHandle({callbackArgName}) as {handleType};");
            WriteLine($"{indent}const {callbackParameter.Name} = new {TypeScriptApiProjector.GetImplementationClassName(wrapperClassName)}({callbackParameter.Name}Handle, {clientExpression});");
        }
        else
        {
            WriteLine($"{indent}const {callbackParameter.Name} = wrapIfHandle({callbackArgName}) as {tsType};");
        }
    }

    private void GenerateConnectionHelper()
    {
        var builderHandle = TypeScriptApiProjector.GetHandleTypeName(AtsConstants.BuilderTypeId);

        WriteLine($$"""
            // ============================================================================
            // Connection Helper
            // ============================================================================

            /**
             * Creates and connects to the Aspire AppHost.
             * Reads connection info from environment variables set by `aspire run`.
             */
            export async function connect(): Promise<AspireClientRpc> {
                const socketPath = process.env.REMOTE_APP_HOST_SOCKET_PATH;
                if (!socketPath) {
                    throw new Error(
                        'REMOTE_APP_HOST_SOCKET_PATH environment variable not set. ' +
                        'Run this application using `aspire run`.'
                    );
                }

                const client = new AspireClient(socketPath);
                await client.connect();

                // Exit the process if the server connection is lost
                client.onDisconnect(() => {
                    console.error('Connection to AppHost lost. Exiting...');
                    process.exit(1);
                });

                return client;
            }

            /**
             * Creates a new distributed application builder.
             * This is the entry point for building Aspire applications.
             *
             * @param options - Optional configuration options for the builder
             * @returns A DistributedApplicationBuilder instance
             *
             * @example
             * const builder = await createBuilder();
             * await builder.addRedis("cache");
             * await builder.addContainer("api", "mcr.microsoft.com/dotnet/samples:aspnetapp");
             * const app = await builder.build();
             * await app.run();
             */
            export async function createBuilder(options?: CreateBuilderOptions): Promise<DistributedApplicationBuilder> {
                const client = await connect();

                // Apply client-side options before any tracking begins
                if (options?.throwOnPendingRejections === false) {
                    client.throwOnPendingRejections = false;
                }

                // Default args, projectDirectory, and appHostFilePath if not provided
                // ASPIRE_APPHOST_FILEPATH is set by the CLI for consistent socket hash computation
                const effectiveOptions: CreateBuilderOptions = {
                    ...options,
                    args: options?.args ?? process.argv.slice(2),
                    projectDirectory: options?.projectDirectory ?? process.env.ASPIRE_PROJECT_DIRECTORY ?? process.cwd(),
                    appHostFilePath: options?.appHostFilePath ?? process.env.ASPIRE_APPHOST_FILEPATH
                };

                // Strip client-only options before sending to the host
                delete effectiveOptions.throwOnPendingRejections;

                const handle = await client.invokeCapability<{{builderHandle}}>(
                    'Aspire.Hosting/createBuilder',
                    { argsOrOptions: effectiveOptions }
                );
                return new DistributedApplicationBuilderImpl(handle, client);
            }

            // Re-export commonly used types
            export { Handle, AppHostUsageError, CancellationToken, CapabilityError, registerCallback } from './transport.mjs';
            export { refExpr, ReferenceExpression } from './base.mjs';
            export type { HandleReference, Awaitable } from './base.mjs';
            """);
        WriteLine();
    }

    private void GenerateGlobalErrorHandling()
    {
        WriteLine("""
            // ============================================================================
            // Global Error Handling
            // ============================================================================

            /**
             * Set up global error handlers to ensure the process exits properly on errors.
             * Node.js doesn't exit on unhandled rejections by default, so we need to handle them.
             */
            process.on('unhandledRejection', (reason: unknown) => {
                const error = reason instanceof Error ? reason : new Error(String(reason));

                if (reason instanceof AppHostUsageError) {
                    console.error(`\n❌ AppHost Error: ${error.message}`);
                } else if (reason instanceof CapabilityError) {
                    console.error(`\n❌ Capability Error: ${error.message}`);
                    console.error(`   Code: ${(reason as CapabilityError).code}`);
                    if ((reason as CapabilityError).capability) {
                        console.error(`   Capability: ${(reason as CapabilityError).capability}`);
                    }
                } else {
                    console.error(`\n❌ Unhandled Error: ${error.message}`);
                    if (error.stack) {
                        console.error(error.stack);
                    }
                }

                process.exit(1);
            });

            process.on('uncaughtException', (error: Error) => {
                if (error instanceof AppHostUsageError) {
                    console.error(`\n❌ AppHost Error: ${error.message}`);
                } else if (error instanceof CapabilityError) {
                    console.error(`\n❌ Capability Error: ${error.message}`);
                    console.error(`   Code: ${error.code}`);
                    if (error.capability) {
                        console.error(`   Capability: ${error.capability}`);
                    }
                } else {
                    console.error(`\n❌ Uncaught Exception: ${error.message}`);
                }
                // Suppress stack traces for structured errors (AppHostUsageError, CapabilityError)
                // to keep polyglot output clean. Use --verbose for full diagnostics.
                if (!(error instanceof AppHostUsageError) && !(error instanceof CapabilityError) && error.stack) {
                    console.error(error.stack);
                }
                process.exit(1);
            });
            """);
    }

    /// <summary>
    /// Generates handle wrapper registrations for all type classes and builder classes.
    /// This allows callback handles to be wrapped as typed instances.
    /// </summary>
    private void GenerateHandleWrapperRegistrations(List<BuilderModel> typeClasses, List<BuilderModel> resourceBuilders)
    {
        WriteLine();
        WriteLine("// ============================================================================");
        WriteLine("// Handle Wrapper Registrations");
        WriteLine("// ============================================================================");
        WriteLine();
        WriteLine("// Register wrapper factories for typed handle wrapping in callbacks");

        // Register type classes (context types like EnvironmentCallbackContext)
        foreach (var typeClass in typeClasses)
        {
            var className = _projector.WrapperClassNames.GetValueOrDefault(typeClass.TypeId) ?? TypeScriptApiProjector.DeriveClassName(typeClass.TypeId);
            var handleType = TypeScriptApiProjector.GetHandleTypeName(typeClass.TypeId);
            WriteLine($"registerHandleWrapper('{typeClass.TypeId}', (handle, client) => new {TypeScriptApiProjector.GetImplementationClassName(className)}(handle as {handleType}, client));");
        }

        // Register resource builder classes
        foreach (var builder in resourceBuilders)
        {
            var className = _projector.WrapperClassNames.GetValueOrDefault(builder.TypeId) ?? TypeScriptApiProjector.DeriveClassName(builder.TypeId);
            var handleType = TypeScriptApiProjector.GetHandleTypeName(builder.TypeId);
            WriteLine($"registerHandleWrapper('{builder.TypeId}', (handle, client) => new {TypeScriptApiProjector.GetImplementationClassName(className)}(handle as {handleType}, client));");
        }

        WriteLine();
    }

    /// <summary>
    /// Generates a type class (context type or wrapper type).
    /// Uses property-like objects for mutable properties and methods for getter-only properties.
    /// For types with generated async members, also generates a Promise wrapper class for fluent chaining.
    /// </summary>
    private void GenerateTypeClass(BuilderModel model)
    {
        var handleType = TypeScriptApiProjector.GetHandleTypeName(model.TypeId);
        var className = TypeScriptApiProjector.DeriveClassName(model.TypeId);
        var implementationClassName = TypeScriptApiProjector.GetImplementationClassName(className);

        GenerateTypeClassInterface(model);

        WriteLine("// ============================================================================");
        WriteLine($"// {implementationClassName}");
        WriteLine("// ============================================================================");
        WriteLine();

        // Separate capabilities by type using CapabilityKind enum
        var getters = model.Capabilities.Where(c => c.CapabilityKind == AtsCapabilityKind.PropertyGetter).ToList();
        var setters = model.Capabilities.Where(c => c.CapabilityKind == AtsCapabilityKind.PropertySetter).ToList();
        var contextMethods = model.Capabilities.Where(c => c.CapabilityKind == AtsCapabilityKind.InstanceMethod).ToList();
        var otherMethods = model.Capabilities.Where(c => c.CapabilityKind == AtsCapabilityKind.Method).ToList();
        var allMethods = contextMethods.Concat(otherMethods).ToList();
        var hasMethods = allMethods.Count > 0;

        WriteDocumentationComment(string.Empty, GetHandleDocumentation(model.TypeId), $"Type class for {className}.");
        WriteLine($"class {implementationClassName} implements {className} {{");
        WriteLine($"    constructor(private _handle: {handleType}, private _client: AspireClientRpc) {{}}");
        WriteLine();
        WriteLine($"    /** Serialize for JSON-RPC transport */");
        WriteLine($"    toJSON(): MarshalledHandle {{ return this._handle.toJSON(); }}");
        WriteLine();

        // Group getters and setters by property name to create property members
        var properties = TypeScriptApiProjector.GroupPropertiesByName(getters, setters);
        var getterOnlyProperties = properties
            .Where(p => TypeScriptApiProjector.IsGetterOnlyProperty(p.Getter, p.Setter))
            .ToList();

        // Generate property access members
        foreach (var prop in properties)
        {
            GeneratePropertyLikeObject(prop.PropertyName, prop.Getter, prop.Setter);
        }

        // Generate methods - use thenable pattern if this type has a Promise wrapper
        if (hasMethods)
        {
            foreach (var method in allMethods)
            {
                GenerateTypeClassMethod(model, method);
            }
        }
        else
        {
            // No Promise wrapper - generate plain async methods
            foreach (var method in contextMethods)
            {
                GenerateContextMethod(method);
            }
            foreach (var method in otherMethods)
            {
                GenerateWrapperMethod(method);
            }
        }

        WriteLine("}");
        WriteLine();

        // Generate thenable wrapper class if this type has generated async members
        if (hasMethods || getterOnlyProperties.Count > 0)
        {
            GenerateTypeClassThenableWrapper(model, allMethods);
        }
    }

    /// <summary>
    /// Generates a property access member.
    /// </summary>
    /// <remarks>
    /// <para>Getter-only properties are emitted as zero-argument async methods.
    /// Mutable properties produce an object with async <c>get</c>/<c>set</c> functions.
    /// Dictionary and list properties delegate to <c>AspireDict</c>/<c>AspireList</c> helpers.
    /// Wrapper-typed mutable properties delegate to <see cref="GenerateWrapperPropertyObject"/>.</para>
    /// <para>Generated TypeScript (example for a string property <c>connectionString</c>):</para>
    /// <code>
    /// connectionString = {
    ///     get: async (): Promise&lt;string&gt; =&gt; {
    ///         return await this._client.invokeCapability&lt;string&gt;(
    ///             'aspire.resource.connectionString.get', { context: this._handle });
    ///     },
    ///     set: async (value: string | PromiseLike&lt;string&gt;): Promise&lt;void&gt; =&gt; {
    ///         value = isPromiseLike(value) ? await value : value;
    ///         await this._client.invokeCapability&lt;void&gt;(
    ///             'aspire.resource.connectionString.set', { context: this._handle, value });
    ///     }
    /// };
    /// </code>
    /// </remarks>
    private void GeneratePropertyLikeObject(string propertyName, AtsCapabilityInfo? getter, AtsCapabilityInfo? setter)
    {
        if (TypeScriptApiProjector.IsGetterOnlyProperty(getter, setter))
        {
            GenerateGetterOnlyPropertyMethod(propertyName, getter!);
            return;
        }

        // Determine the return type from getter
        string returnType = "unknown";

        if (getter != null)
        {
            returnType = _projector.MapTypeRefToTypeScript(getter.ReturnType);

            // Mutable dictionary/list properties stay as property accessors so callers can use
            // wrapper operations (for example, property.get()/set() or list/dict helpers)
            // without switching to the getter-only method shape.
            if (TypeScriptApiProjector.IsDictionaryType(getter.ReturnType))
            {
                GenerateMutableDictionaryProperty(propertyName, getter);
                return;
            }

            if (TypeScriptApiProjector.IsListType(getter.ReturnType))
            {
                GenerateMutableListProperty(propertyName, getter);
                return;
            }

            // Check if return type is a wrapper class - use property-like object returning wrapper
            if (getter.ReturnType?.TypeId != null && _projector.WrapperClassNames.TryGetValue(getter.ReturnType.TypeId, out var wrapperClassName))
            {
                GenerateWrapperPropertyObject(propertyName, getter, setter, wrapperClassName);
                return;
            }
        }

        // Generate property-like object for scalar types
        WriteLine($"    {propertyName} = {{");

        // Generate get method
        if (getter != null)
        {
            WriteLine($"        get: async (): Promise<{returnType}> => {{");
            if (getter.ReturnType?.TypeId == AtsConstants.CancellationToken)
            {
                WriteLine("            const result = await this._client.invokeCapability<string | null>(");
                WriteLine($"                '{getter.CapabilityId}',");
                WriteLine("                { context: this._handle }");
                WriteLine("            );");
                WriteLine("            return CancellationToken.fromValue(result);");
            }
            else
            {
                WriteLine($"            return await this._client.invokeCapability<{returnType}>(");
                WriteLine($"                '{getter.CapabilityId}',");
                WriteLine($"                {{ context: this._handle }}");
                WriteLine("            );");
            }
            WriteLine("        },");
        }

        // Generate set method
        if (setter != null)
        {
            var valueParam = setter.Parameters.FirstOrDefault(p => p.Name == "value");
            if (valueParam != null)
            {
                var valueType = _projector.MapInputTypeToTypeScript(valueParam.Type);
                WriteLine($"        set: async (value: {valueType}): Promise<void> => {{");
                GeneratePromiseResolutionForParam("value", valueParam.Type, "            ");
                WriteLine($"            await this._client.invokeCapability<void>(");
                WriteLine($"                '{setter.CapabilityId}',");
                WriteLine($"                {{ context: this._handle, {GetRpcArgumentEntry("value", valueParam.Type)} }}");
                WriteLine("            );");
                WriteLine("        }");
            }
        }

        WriteLine("    };");
        WriteLine();
    }

    private void GenerateGetterOnlyPropertyMethod(string propertyName, AtsCapabilityInfo getter)
    {
        if (TypeScriptApiProjector.IsDictionaryType(getter.ReturnType))
        {
            GenerateDictionaryProperty(propertyName, getter);
            return;
        }

        if (TypeScriptApiProjector.IsListType(getter.ReturnType))
        {
            GenerateListProperty(propertyName, getter);
            return;
        }

        if (getter.ReturnType?.TypeId != null && _projector.WrapperClassNames.TryGetValue(getter.ReturnType.TypeId, out var wrapperClassName))
        {
            GenerateWrapperGetterOnlyPropertyMethod(propertyName, getter, wrapperClassName);
            return;
        }

        // Promise-wrapper types that are NOT registered as generated wrapper classes (currently only
        // InteractionInputCollection, a hand-written base.mts type) wrap the marshalled collection
        // promise in their hand-written ...Promise thenable so by-name accessors chain without an
        // intermediate await. Awaiting the wrapper still resolves to the plain collection, preserving
        // the existing `await (await x.inputs()).value(...)` form.
        if (_projector.TryGetPromiseWrapperType(getter.ReturnType, out var promiseInterfaceName, out var promiseImplementationClassName))
        {
            var collectionType = _projector.GetGetterOnlyPropertyReturnType(getter.ReturnType);
            WriteLine($"    {propertyName}(): {promiseInterfaceName} {{");
            WriteLine($"        return new {promiseImplementationClassName}(this._client.invokeCapability<{collectionType}>(");
            WriteLine($"            '{getter.CapabilityId}',");
            WriteLine("            { context: this._handle }");
            WriteLine("        ), this._client, false);");
            WriteLine("    }");
            WriteLine();
            return;
        }

        var returnType = _projector.GetGetterOnlyPropertyReturnType(getter.ReturnType);

        WriteLine($"    async {propertyName}(): Promise<{returnType}> {{");
        if (getter.ReturnType?.TypeId == AtsConstants.CancellationToken)
        {
            WriteLine("        const result = await this._client.invokeCapability<string | null>(");
            WriteLine($"            '{getter.CapabilityId}',");
            WriteLine("            { context: this._handle }");
            WriteLine("        );");
            WriteLine("        return CancellationToken.fromValue(result);");
        }
        else
        {
            WriteLine($"        return await this._client.invokeCapability<{returnType}>(");
            WriteLine($"            '{getter.CapabilityId}',");
            WriteLine("            { context: this._handle }");
            WriteLine("        );");
        }
        WriteLine("    }");
        WriteLine();
    }

    private void GenerateWrapperGetterOnlyPropertyMethod(string propertyName, AtsCapabilityInfo getter, string wrapperClassName)
    {
        var handleType = TypeScriptApiProjector.GetHandleTypeName(getter.ReturnType!.TypeId);
        var wrapperImplementationClassName = TypeScriptApiProjector.GetImplementationClassName(wrapperClassName);

        if (_projector.TryGetPromiseWrapperType(getter.ReturnType, out var promiseInterfaceName, out var promiseImplementationClassName))
        {
            WriteLine($"    {propertyName}(): {promiseInterfaceName} {{");
            WriteLine("        const promise = (async () => {");
            WriteLine($"            const handle = await this._client.invokeCapability<{handleType}>(");
            WriteLine($"                '{getter.CapabilityId}',");
            WriteLine("                { context: this._handle }");
            WriteLine("            );");
            WriteLine($"            return new {wrapperImplementationClassName}(handle, this._client);");
            WriteLine("        })();");
            WriteLine($"        return new {promiseImplementationClassName}(promise, this._client, false);");
            WriteLine("    }");
            WriteLine();
            return;
        }

        WriteLine($"    async {propertyName}(): Promise<{wrapperClassName}> {{");
        WriteLine($"        const handle = await this._client.invokeCapability<{handleType}>(");
        WriteLine($"            '{getter.CapabilityId}',");
        WriteLine("            { context: this._handle }");
        WriteLine("        );");
        WriteLine($"        return new {wrapperImplementationClassName}(handle, this._client);");
        WriteLine("    }");
        WriteLine();
    }

    /// <summary>
    /// Generates a property-like object that returns a wrapper class.
    /// </summary>
    /// <remarks>
    /// Similar to <see cref="GeneratePropertyLikeObject"/> but the getter returns a wrapper
    /// class instance instead of a scalar value. The RPC result is a handle that gets
    /// wrapped in the implementation class.
    /// <code>
    /// // Example: a property 'primaryEndpoint' returning EndpointReference
    /// primaryEndpoint = {
    ///     get: async (): Promise&lt;EndpointReference&gt; =&gt; {
    ///         const handle = await this._client.invokeCapability&lt;EndpointReferenceHandle&gt;(
    ///             'aspire.resource.primaryEndpoint.get', { context: this._handle });
    ///         return new EndpointReferenceImpl(handle, this._client);
    ///     },
    ///     set: async (value: EndpointReference | PromiseLike&lt;EndpointReference&gt;): Promise&lt;void&gt; =&gt; {
    ///         value = isPromiseLike(value) ? await value : value;
    ///         await this._client.invokeCapability&lt;void&gt;(
    ///             'aspire.resource.primaryEndpoint.set', { context: this._handle, value });
    ///     }
    /// };
    /// </code>
    /// </remarks>
    private void GenerateWrapperPropertyObject(string propertyName, AtsCapabilityInfo getter, AtsCapabilityInfo? setter, string wrapperClassName)
    {
        var handleType = TypeScriptApiProjector.GetHandleTypeName(getter.ReturnType!.TypeId);
        var wrapperImplementationClassName = TypeScriptApiProjector.GetImplementationClassName(wrapperClassName);

        WriteLine($"    {propertyName} = {{");
        if (_projector.TryGetPromiseWrapperType(getter.ReturnType, out var promiseInterfaceName, out var promiseImplementationClassName))
        {
            WriteLine($"        get: (): {promiseInterfaceName} => {{");
            WriteLine("            const promise = (async () => {");
            WriteLine($"                const handle = await this._client.invokeCapability<{handleType}>(");
            WriteLine($"                    '{getter.CapabilityId}',");
            WriteLine($"                    {{ context: this._handle }}");
            WriteLine("                );");
            WriteLine($"                return new {wrapperImplementationClassName}(handle, this._client);");
            WriteLine("            })();");
            WriteLine($"            return new {promiseImplementationClassName}(promise, this._client, false);");
            WriteLine("        },");
        }
        else
        {
            WriteLine($"        get: async (): Promise<{wrapperClassName}> => {{");
            WriteLine($"            const handle = await this._client.invokeCapability<{handleType}>(");
            WriteLine($"                '{getter.CapabilityId}',");
            WriteLine($"                {{ context: this._handle }}");
            WriteLine("            );");
            WriteLine($"            return new {wrapperImplementationClassName}(handle, this._client);");
            WriteLine("        },");
        }

        if (setter != null)
        {
            var valueParam = setter.Parameters.FirstOrDefault(p => p.Name == "value");
            if (valueParam != null)
            {
                var valueType = _projector.MapInputTypeToTypeScript(valueParam.Type);
                WriteLine($"        set: async (value: {valueType}): Promise<void> => {{");
                GeneratePromiseResolutionForParam("value", valueParam.Type, "            ");
                WriteLine($"            await this._client.invokeCapability<void>(");
                WriteLine($"                '{setter.CapabilityId}',");
                WriteLine($"                {{ context: this._handle, {GetRpcArgumentEntry("value", valueParam.Type)} }}");
                WriteLine("            );");
                WriteLine("        }");
            }
        }

        WriteLine("    };");
        WriteLine();
    }

    /// <summary>
    /// Generates a getter-only method for dictionary types.
    /// </summary>
    private void GenerateDictionaryProperty(string propertyName, AtsCapabilityInfo getter)
    {
        // Determine key and value types
        var keyType = "string";
        var valueType = "unknown";

        // Try to extract key and value types from Dict type
        if (getter.ReturnType?.KeyType != null)
        {
            keyType = _projector.MapTypeRefToTypeScript(getter.ReturnType.KeyType);
        }
        if (getter.ReturnType?.ValueType != null)
        {
            // Union types will be mapped correctly via MapTypeRefToTypeScript
            valueType = _projector.MapTypeRefToTypeScript(getter.ReturnType.ValueType);
        }

        var typeId = $"'{getter.CapabilityId}'";
        var getterCapabilityId = $"'{getter.CapabilityId}'";

        // Pass the getter capability ID so AspireDict can lazily fetch the actual dictionary handle.
        WriteLine($"    private _{propertyName}?: AspireDict<{keyType}, {valueType}>;");
        WriteLine($"    async {propertyName}(): Promise<AspireDict<{keyType}, {valueType}>> {{");
        WriteLine($"        if (!this._{propertyName}) {{");
        WriteLine($"            this._{propertyName} = new AspireDict<{keyType}, {valueType}>(");
        WriteLine($"                this._handle,");
        WriteLine($"                this._client,");
        WriteLine($"                {typeId},");
        WriteLine($"                {getterCapabilityId}");
        WriteLine("            );");
        WriteLine("        }");
        WriteLine($"        return this._{propertyName};");
        WriteLine("    }");
        WriteLine();
    }

    private void GenerateMutableDictionaryProperty(string propertyName, AtsCapabilityInfo getter)
    {
        var keyType = "string";
        var valueType = "unknown";

        if (getter.ReturnType?.KeyType != null)
        {
            keyType = _projector.MapTypeRefToTypeScript(getter.ReturnType.KeyType);
        }

        if (getter.ReturnType?.ValueType != null)
        {
            valueType = _projector.MapTypeRefToTypeScript(getter.ReturnType.ValueType);
        }

        var typeId = $"'{getter.CapabilityId}'";
        var getterCapabilityId = $"'{getter.CapabilityId}'";

        WriteLine($"    private _{propertyName}?: AspireDict<{keyType}, {valueType}>;");
        WriteLine($"    get {propertyName}(): AspireDict<{keyType}, {valueType}> {{");
        WriteLine($"        if (!this._{propertyName}) {{");
        WriteLine($"            this._{propertyName} = new AspireDict<{keyType}, {valueType}>(");
        WriteLine($"                this._handle,");
        WriteLine($"                this._client,");
        WriteLine($"                {typeId},");
        WriteLine($"                {getterCapabilityId}");
        WriteLine("            );");
        WriteLine("        }");
        WriteLine($"        return this._{propertyName};");
        WriteLine("    }");
        WriteLine();
    }

    /// <summary>
    /// Generates a getter-only method for list types.
    /// </summary>
    private void GenerateListProperty(string propertyName, AtsCapabilityInfo getter)
    {
        // Determine element type
        var elementType = "unknown";

        if (getter.ReturnType?.ElementType != null)
        {
            elementType = _projector.MapTypeRefToTypeScript(getter.ReturnType.ElementType);
        }

        var typeId = $"'{getter.CapabilityId}'";
        var getterCapabilityId = $"'{getter.CapabilityId}'";

        // Pass the getter capability ID so AspireList can lazily fetch the actual list handle.
        WriteLine($"    private _{propertyName}?: AspireList<{elementType}>;");
        WriteLine($"    async {propertyName}(): Promise<AspireList<{elementType}>> {{");
        WriteLine($"        if (!this._{propertyName}) {{");
        WriteLine($"            this._{propertyName} = new AspireList<{elementType}>(");
        WriteLine($"                this._handle,");
        WriteLine($"                this._client,");
        WriteLine($"                {typeId},");
        WriteLine($"                {getterCapabilityId}");
        WriteLine("            );");
        WriteLine("        }");
        WriteLine($"        return this._{propertyName};");
        WriteLine("    }");
        WriteLine();
    }

    private void GenerateMutableListProperty(string propertyName, AtsCapabilityInfo getter)
    {
        var elementType = "unknown";

        if (getter.ReturnType?.ElementType != null)
        {
            elementType = _projector.MapTypeRefToTypeScript(getter.ReturnType.ElementType);
        }

        var typeId = $"'{getter.CapabilityId}'";
        var getterCapabilityId = $"'{getter.CapabilityId}'";

        WriteLine($"    private _{propertyName}?: AspireList<{elementType}>;");
        WriteLine($"    get {propertyName}(): AspireList<{elementType}> {{");
        WriteLine($"        if (!this._{propertyName}) {{");
        WriteLine($"            this._{propertyName} = new AspireList<{elementType}>(");
        WriteLine($"                this._handle,");
        WriteLine($"                this._client,");
        WriteLine($"                {typeId},");
        WriteLine($"                {getterCapabilityId}");
        WriteLine("            );");
        WriteLine("        }");
        WriteLine($"        return this._{propertyName};");
        WriteLine("    }");
        WriteLine();
    }

    /// <summary>
    /// Generates a context instance method (from ExposeMethods=true).
    /// </summary>
    /// <remarks>
    /// <para>Context methods are async methods on wrapper classes that pass <c>this._handle</c>
    /// as the context argument. They use <see cref="GenerateResolveAndBuildArgs"/> for parameter
    /// handling.</para>
    /// <para>Generated TypeScript (example for <c>getEndpoint</c> on <c>PostgresResource</c>):</para>
    /// <code>
    /// async getEndpoint(name: string): Promise&lt;EndpointReference&gt; {
    ///     const rpcArgs: Record&lt;string, unknown&gt; = { context: this._handle, name };
    ///     return await this._client.invokeCapability&lt;EndpointReference&gt;(
    ///         'aspire.resource.getEndpoint', rpcArgs);
    /// }
    /// </code>
    /// </remarks>
    private void GenerateContextMethod(AtsCapabilityInfo method)
    {
        // Use OwningTypeName if available to extract method name, otherwise parse from MethodName
        var methodName = !string.IsNullOrEmpty(method.OwningTypeName) && method.MethodName.Contains('.')
            ? method.MethodName[(method.MethodName.LastIndexOf('.') + 1)..]
            : method.MethodName;

        // Filter out target parameter
        var targetParamName = method.TargetParameterName ?? "context";
        var userParams = method.Parameters.Where(p => p.Name != targetParamName).ToList();

        // Separate required and optional parameters
        var (requiredParams, optionalParams) = TypeScriptApiProjector.SeparateParameters(userParams);
        var hasOptionals = optionalParams.Count > 0;
        var hasDirectOptionsParameter = TypeScriptApiProjector.TryGetDirectOptionsParameter(optionalParams, out var directOptionsParam);
        var optionsInterfaceName = hasDirectOptionsParameter ? _projector.MapParameterToTypeScript(directOptionsParam!) : _projector.ResolveOptionsInterfaceName(method);
        var publicOptionsParamName = TypeScriptApiProjector.GetImplementationOptionsParameterName(userParams, hasOptionals, hasDirectOptionsParameter);

        // Build parameter list using options pattern
        var paramsString = _projector.BuildPublicParameterList(requiredParams, hasOptionals, optionsInterfaceName, publicOptionsParamName, TypeScriptApiProjector.GetTrailingCancellationTokenParameter(optionalParams));

        // Determine return type
        var returnType = GetReturnTypeId(method) != null
            ? _projector.MapTypeRefToTypeScript(method.ReturnType)
            : "void";

        if (_projector.TryGetPromiseWrapperType(method.ReturnType, out var returnPromiseInterfaceName, out var returnPromiseImplementationClassName))
        {
            var returnTypeId = method.ReturnType!.TypeId;
            var returnClassName = _projector.GetConcreteClassName(returnTypeId);
            var returnImplementationClassName = TypeScriptApiProjector.GetImplementationClassName(returnClassName);
            var returnHandleType = TypeScriptApiProjector.GetHandleTypeName(returnTypeId);

            WriteCapabilityDocComment("    ", method, requiredParams, hasOptionals ? publicOptionsParamName : null);
            Write($"    {methodName}(");
            Write(paramsString);
            WriteLine($"): {returnPromiseInterfaceName} {{");
            WriteLine("        const promise = (async () => {");

            foreach (var param in hasDirectOptionsParameter ? [] : optionalParams)
            {
                var localParameterName = GetLocalParameterName(param);
                WriteLine($"            {(_projector.IsWidenedHandleType(param.Type) ? "let" : "const")} {localParameterName} = {publicOptionsParamName}?.{param.Name};");
            }

            GenerateResolveAndBuildArgs(targetParamName, userParams, requiredParams, optionalParams, useSafeOptionalLocalNames: true, indent: "            ");

            WriteLine($"            const handle = await this._client.invokeCapability<{returnHandleType}>(");
            WriteLine($"                '{method.CapabilityId}',");
            WriteLine("                rpcArgs");
            WriteLine("            );");
            WriteLine($"            return new {returnImplementationClassName}(handle, this._client);");
            WriteLine("        })();");
            WriteLine($"        return new {returnPromiseImplementationClassName}(promise, this._client);");
            WriteLine("    }");
            WriteLine();
            return;
        }

        // Generate async method
        WriteCapabilityDocComment("    ", method, requiredParams, hasOptionals ? publicOptionsParamName : null);
        Write($"    async {methodName}(");
        Write(paramsString);
        WriteLine($"): Promise<{returnType}> {{");

        // Extract optional params from options object
        foreach (var param in hasDirectOptionsParameter ? [] : optionalParams)
        {
            var localParameterName = GetLocalParameterName(param);
            WriteLine($"        {(_projector.IsWidenedHandleType(param.Type) ? "let" : "const")} {localParameterName} = {publicOptionsParamName}?.{param.Name};");
        }

        // Resolve promise-like params and build args
        GenerateResolveAndBuildArgs(targetParamName, userParams, requiredParams, optionalParams, useSafeOptionalLocalNames: true);

        if (returnType == "void")
        {
            WriteLine($"        await this._client.invokeCapability<void>(");
        }
        else if (method.ReturnType?.TypeId == AtsConstants.CancellationToken)
        {
            WriteLine("        const result = await this._client.invokeCapability<string | null>(");
            WriteLine($"            '{method.CapabilityId}',");
            WriteLine("            rpcArgs");
            WriteLine("        );");
            WriteLine("        return CancellationToken.fromValue(result);");
            WriteLine("    }");
            WriteLine();
            return;
        }
        else
        {
            WriteLine($"        return await this._client.invokeCapability<{returnType}>(");
        }
        WriteLine($"            '{method.CapabilityId}',");
        WriteLine($"            rpcArgs");
        WriteLine("        );");
        WriteLine("    }");
        WriteLine();
    }

    /// <summary>
    /// Generates a method on a wrapper class.
    /// </summary>
    /// <remarks>
    /// <para>Similar to <see cref="GenerateContextMethod"/> but designed for wrapper classes
    /// that expose RPC methods without the thenable/fluent pattern.</para>
    /// <para>Generated TypeScript (example for <c>getExpression</c> on <c>EndpointReference</c>):</para>
    /// <code>
    /// async getExpression(name: string): Promise&lt;string&gt; {
    ///     const rpcArgs: Record&lt;string, unknown&gt; = { builder: this._handle, name };
    ///     return await this._client.invokeCapability&lt;string&gt;(
    ///         'aspire.endpoint.getExpression', rpcArgs);
    /// }
    /// </code>
    /// </remarks>
    private void GenerateWrapperMethod(AtsCapabilityInfo capability)
    {
        var methodName = TypeScriptApiProjector.GetTypeScriptMethodName(capability.MethodName);

        // First arg is the handle (implicit via this._handle) - use metadata instead of string parsing
        var firstParamName = capability.TargetParameterName ?? "builder";

        // Filter out the implicit handle parameter
        var userParams = capability.Parameters.Where(p => p.Name != firstParamName).ToList();

        // Separate required and optional parameters
        var (requiredParams, optionalParams) = TypeScriptApiProjector.SeparateParameters(userParams);
        var hasOptionals = optionalParams.Count > 0;
        var hasDirectOptionsParameter = TypeScriptApiProjector.TryGetDirectOptionsParameter(optionalParams, out var directOptionsParam);
        var optionsInterfaceName = hasDirectOptionsParameter ? _projector.MapParameterToTypeScript(directOptionsParam!) : _projector.ResolveOptionsInterfaceName(capability);
        var publicOptionsParamName = TypeScriptApiProjector.GetImplementationOptionsParameterName(userParams, hasOptionals, hasDirectOptionsParameter);

        // Build parameter list using options pattern
        var paramsString = _projector.BuildPublicParameterList(requiredParams, hasOptionals, optionsInterfaceName, publicOptionsParamName, TypeScriptApiProjector.GetTrailingCancellationTokenParameter(optionalParams));

        // Determine return type
        var returnType = _projector.MapTypeRefToTypeScript(capability.ReturnType);

        if (_projector.TryGetPromiseWrapperType(capability.ReturnType, out var returnPromiseInterfaceName, out var returnPromiseImplementationClassName))
        {
            var returnTypeId = capability.ReturnType!.TypeId;
            var returnClassName = _projector.GetConcreteClassName(returnTypeId);
            var returnImplementationClassName = TypeScriptApiProjector.GetImplementationClassName(returnClassName);
            var returnHandleType = TypeScriptApiProjector.GetHandleTypeName(returnTypeId);

            WriteCapabilityDocComment("    ", capability, requiredParams, hasOptionals ? publicOptionsParamName : null);
            Write($"    {methodName}(");
            Write(paramsString);
            WriteLine($"): {returnPromiseInterfaceName} {{");
            WriteLine("        const promise = (async () => {");

            foreach (var param in hasDirectOptionsParameter ? [] : optionalParams)
            {
                var localParameterName = GetLocalParameterName(param);
                WriteLine($"            {(_projector.IsWidenedHandleType(param.Type) ? "let" : "const")} {localParameterName} = {publicOptionsParamName}?.{param.Name};");
            }

            GenerateResolveAndBuildArgs(firstParamName, userParams, requiredParams, optionalParams, useSafeOptionalLocalNames: true, indent: "            ");

            WriteLine($"            const handle = await this._client.invokeCapability<{returnHandleType}>(");
            WriteLine($"                '{capability.CapabilityId}',");
            WriteLine("                rpcArgs");
            WriteLine("            );");
            WriteLine($"            return new {returnImplementationClassName}(handle, this._client);");
            WriteLine("        })();");
            WriteLine($"        return new {returnPromiseImplementationClassName}(promise, this._client);");
            WriteLine("    }");
            WriteLine();
            return;
        }

        // Generate async method
        WriteCapabilityDocComment("    ", capability, requiredParams, hasOptionals ? publicOptionsParamName : null);
        Write($"    async {methodName}(");
        Write(paramsString);
        WriteLine($"): Promise<{returnType}> {{");

        // Extract optional params from options object
        foreach (var param in hasDirectOptionsParameter ? [] : optionalParams)
        {
            var localParameterName = GetLocalParameterName(param);
            WriteLine($"        {(_projector.IsWidenedHandleType(param.Type) ? "let" : "const")} {localParameterName} = {publicOptionsParamName}?.{param.Name};");
        }

        // Resolve promise-like params and build args
        GenerateResolveAndBuildArgs(firstParamName, userParams, requiredParams, optionalParams, useSafeOptionalLocalNames: true);

        if (returnType == "void")
        {
            WriteLine($"        await this._client.invokeCapability<void>(");
        }
        else
        {
            WriteLine($"        return await this._client.invokeCapability<{returnType}>(");
        }
        WriteLine($"            '{capability.CapabilityId}',");
        WriteLine($"            rpcArgs");
        WriteLine("        );");
        WriteLine("    }");
        WriteLine();
    }

    /// <summary>
    /// Generates a method on a type class using the thenable pattern.
    /// Generates both an internal async method and a public fluent method.
    /// </summary>
    /// <remarks>
    /// <para>Follows the same internal/public pair pattern as <see cref="GenerateBuilderMethod"/>
    /// but operates on type classes (resources exposed via <c>ExposeMethods</c>).</para>
    /// <para>Generated TypeScript (example for <c>withEnvironment</c> on <c>PostgresResource</c>):</para>
    /// <code>
    /// /** @internal */
    /// async _withEnvironmentInternal(name: string, value: string): Promise&lt;PostgresResource&gt; {
    ///     const rpcArgs: Record&lt;string, unknown&gt; = { context: this._handle, name, value };
    ///     await this._client.invokeCapability&lt;void&gt;('...', rpcArgs);
    ///     return this;
    /// }
    ///
    /// withEnvironment(name: string, value: string): PostgresResourcePromise {
    ///     return new PostgresResourcePromiseImpl(
    ///         this._withEnvironmentInternal(name, value), this._client);
    /// }
    /// </code>
    /// <para>For methods returning a different wrapper type, the internal method returns that
    /// wrapper and the public method returns its promise class.</para>
    /// </remarks>
    private void GenerateTypeClassMethod(BuilderModel model, AtsCapabilityInfo capability)
    {
        var className = TypeScriptApiProjector.DeriveClassName(model.TypeId);
        var promiseClass = $"{className}Promise";
        var promiseImplementationClass = TypeScriptApiProjector.GetImplementationPromiseClassName(className);

        // Use OwningTypeName if available to extract method name, otherwise parse from MethodName
        var methodName = !string.IsNullOrEmpty(capability.OwningTypeName) && capability.MethodName.Contains('.')
            ? capability.MethodName[(capability.MethodName.LastIndexOf('.') + 1)..]
            : TypeScriptApiProjector.GetTypeScriptMethodName(capability.MethodName);

        var internalMethodName = $"_{methodName}Internal";

        // Filter out target parameter
        var targetParamName = capability.TargetParameterName ?? "context";
        var userParams = capability.Parameters.Where(p => p.Name != targetParamName).ToList();

        // Separate required and optional parameters
        var (requiredParams, optionalParams) = TypeScriptApiProjector.SeparateParameters(userParams);
        var hasOptionals = optionalParams.Count > 0;
        var hasDirectOptionsParameter = TypeScriptApiProjector.TryGetDirectOptionsParameter(optionalParams, out var directOptionsParam);
        var optionsInterfaceName = hasDirectOptionsParameter ? _projector.MapParameterToTypeScript(directOptionsParam!) : _projector.ResolveOptionsInterfaceName(capability);
        var publicOptionsParamName = TypeScriptApiProjector.GetImplementationOptionsParameterName(userParams, hasOptionals, hasDirectOptionsParameter);

        // Build parameter list for public method
        var publicParamsString = _projector.BuildPublicParameterList(requiredParams, hasOptionals, optionsInterfaceName, publicOptionsParamName, TypeScriptApiProjector.GetTrailingCancellationTokenParameter(optionalParams));

        // Build parameter list for internal method (all params positional)
        var internalParamDefs = new List<string>();
        foreach (var param in userParams)
        {
            var tsType = _projector.MapParameterToTypeScript(param);
            var optional = param.IsOptional || param.IsNullable ? "?" : "";
            internalParamDefs.Add($"{param.Name}{optional}: {tsType}");
        }
        var internalParamsString = string.Join(", ", internalParamDefs);

        // Check if return type has a Promise wrapper
        var returnPromiseWrapper = _projector.GetPromiseWrapperForReturnType(capability.ReturnType);
        var returnType = _projector.MapTypeRefToTypeScript(capability.ReturnType);
        var isVoid = capability.ReturnType == null || capability.ReturnType.TypeId == AtsConstants.Void;

        // If return type has a Promise wrapper, generate internal + fluent pattern
        if (returnPromiseWrapper != null)
        {
            var returnWrapperClass = _projector.WrapperClassNames.GetValueOrDefault(capability.ReturnType!.TypeId)
                ?? TypeScriptApiProjector.DeriveClassName(capability.ReturnType.TypeId);
            var returnWrapperImplementationClass = TypeScriptApiProjector.GetImplementationClassName(returnWrapperClass);
            var returnPromiseImplementationClass = TypeScriptApiProjector.GetImplementationPromiseClassName(returnWrapperClass);
            var returnHandleType = TypeScriptApiProjector.GetHandleTypeName(capability.ReturnType.TypeId);

            // Generate internal async method
            WriteLine($"    /** @internal */");
            Write($"    async {internalMethodName}(");
            Write(internalParamsString);
            WriteLine($"): Promise<{returnWrapperClass}> {{");

            // Handle callback registration if any
            var callbackParams = userParams.Where(p => p.IsCallback).ToList();
            foreach (var callbackParam in callbackParams)
            {
                GenerateCallbackRegistration(callbackParam);
            }

            // Resolve any promise-like handle parameters
            GeneratePromiseResolution(userParams);

            // Build args with conditional inclusion
            GenerateArgsObjectWithConditionals(targetParamName, requiredParams, optionalParams);

            WriteLine($"        const result = await this._client.invokeCapability<{returnHandleType}>(");
            WriteLine($"            '{capability.CapabilityId}',");
            WriteLine($"            rpcArgs");
            WriteLine("        );");
            WriteLine($"        return new {returnWrapperImplementationClass}(result, this._client);");
            WriteLine("    }");
            WriteLine();

            // Generate public fluent method that returns thenable wrapper
            WriteCapabilityDocComment("    ", capability, requiredParams, hasOptionals ? publicOptionsParamName : null);
            Write($"    {methodName}(");
            Write(publicParamsString);
            WriteLine($"): {returnPromiseWrapper} {{");

            // Extract optional params and forward
            foreach (var param in hasDirectOptionsParameter ? [] : optionalParams)
            {
                var localParameterName = GetLocalParameterName(param);
                WriteLine($"        {(_projector.IsWidenedHandleType(param.Type) ? "let" : "const")} {localParameterName} = {publicOptionsParamName}?.{param.Name};");
            }

            var internalCallArgs = userParams.Select(p => optionalParams.Contains(p) ? GetLocalParameterName(p) : p.Name);
            var internalCall = $"this.{internalMethodName}({string.Join(", ", internalCallArgs)})";

            // For build(), flush pending promises before invoking the internal method to avoid deadlock
            if (string.Equals(methodName, "build", StringComparison.OrdinalIgnoreCase))
            {
                WriteLine($"        const flushAndBuild = async () => {{ await this._client.flushPendingPromises(); return {internalCall}; }};");
                WriteLine($"        return new {returnPromiseImplementationClass}(flushAndBuild(), this._client, false);");
            }
            else
            {
                WriteLine($"        return new {returnPromiseImplementationClass}({internalCall}, this._client);");
            }
            WriteLine("    }");
        }
        else if (isVoid)
        {
            // Void return - generate internal + fluent returning this type's Promise wrapper
            // Generate internal async method
            WriteLine($"    /** @internal */");
            Write($"    async {internalMethodName}(");
            Write(internalParamsString);
            WriteLine($"): Promise<{className}> {{");

            // Handle callback registration if any
            var callbackParams = userParams.Where(p => p.IsCallback).ToList();
            foreach (var callbackParam in callbackParams)
            {
                GenerateCallbackRegistration(callbackParam);
            }

            // Resolve any promise-like handle parameters
            GeneratePromiseResolution(userParams);

            // Build args with conditional inclusion
            GenerateArgsObjectWithConditionals(targetParamName, requiredParams, optionalParams);

            WriteLine($"        await this._client.invokeCapability<void>(");
            WriteLine($"            '{capability.CapabilityId}',");
            WriteLine($"            rpcArgs");
            WriteLine("        );");
            WriteLine($"        return this;");
            WriteLine("    }");
            WriteLine();

            // Generate public fluent method
            WriteCapabilityDocComment("    ", capability, requiredParams, hasOptionals ? publicOptionsParamName : null);
            Write($"    {methodName}(");
            Write(publicParamsString);
            WriteLine($"): {promiseClass} {{");

            // Extract optional params and forward
            foreach (var param in hasDirectOptionsParameter ? [] : optionalParams)
            {
                var localParameterName = GetLocalParameterName(param);
                WriteLine($"        {(_projector.IsWidenedHandleType(param.Type) ? "let" : "const")} {localParameterName} = {publicOptionsParamName}?.{param.Name};");
            }

            Write($"        return new {promiseImplementationClass}(this.{internalMethodName}(");
            Write(string.Join(", ", userParams.Select(p => optionalParams.Contains(p) ? GetLocalParameterName(p) : p.Name)));
            WriteLine("), this._client);");
            WriteLine("    }");
        }
        else
        {
            // Non-void, non-wrapper return - plain async method
            WriteCapabilityDocComment("    ", capability, requiredParams, hasOptionals ? publicOptionsParamName : null);
            Write($"    async {methodName}(");
            Write(publicParamsString);
            WriteLine($"): Promise<{returnType}> {{");

            // Extract optional params from options object
            foreach (var param in hasDirectOptionsParameter ? [] : optionalParams)
            {
                var localParameterName = GetLocalParameterName(param);
                WriteLine($"        {(_projector.IsWidenedHandleType(param.Type) ? "let" : "const")} {localParameterName} = {publicOptionsParamName}?.{param.Name};");
            }

            // Handle callback registration if any
            var callbackParams = userParams.Where(p => p.IsCallback).ToList();
            foreach (var callbackParam in callbackParams)
            {
                GenerateCallbackRegistration(callbackParam);
            }

            // Resolve any promise-like handle parameters
            GeneratePromiseResolution(userParams);

            // Build args with conditional inclusion
            GenerateArgsObjectWithConditionals(targetParamName, requiredParams, optionalParams, useSafeOptionalLocalNames: true);

            if (capability.ReturnType?.TypeId == AtsConstants.CancellationToken)
            {
                WriteLine("        const result = await this._client.invokeCapability<string | null>(");
                WriteLine($"            '{capability.CapabilityId}',");
                WriteLine("            rpcArgs");
                WriteLine("        );");
                WriteLine("        return CancellationToken.fromValue(result);");
            }
            else
            {
                WriteLine($"        return await this._client.invokeCapability<{returnType}>(");
                WriteLine($"            '{capability.CapabilityId}',");
                WriteLine($"            rpcArgs");
                WriteLine("        );");
            }
            WriteLine("    }");
        }
        WriteLine();
    }

    /// <summary>
    /// Generates a thenable wrapper class for a type class.
    /// </summary>
    /// <remarks>
    /// <para>Identical in structure to <see cref="GenerateThenableClass"/> but generated for
    /// type classes (resources with <c>ExposeMethods</c>) rather than builder classes.</para>
    /// <para>Generated TypeScript (example for <c>PostgresResource</c>):</para>
    /// <code>
    /// class PostgresResourcePromiseImpl implements PostgresResourcePromise {
    ///     constructor(private _promise: Promise&lt;PostgresResource&gt;, private _client: AspireClientRpc) {
    ///         _client.trackPromise(_promise);
    ///     }
    ///     then&lt;T1, T2&gt;(...): PromiseLike&lt;T1 | T2&gt; {
    ///         return this._promise.then(...);
    ///     }
    ///     withEnvironment(name: string, value: string): PostgresResourcePromise {
    ///         return new PostgresResourcePromiseImpl(
    ///             this._promise.then(obj =&gt; obj.withEnvironment(name, value)), this._client);
    ///     }
    /// }
    /// </code>
    /// </remarks>
    private void GenerateTypeClassThenableWrapper(BuilderModel model, List<AtsCapabilityInfo> methods)
    {
        var className = TypeScriptApiProjector.DeriveClassName(model.TypeId);
        var promiseClass = $"{className}Promise";
        var promiseImplementationClass = TypeScriptApiProjector.GetImplementationPromiseClassName(className);

        WriteLine($"/**");
        WriteLine($" * Thenable wrapper for {className} that enables fluent chaining.");
        WriteLine($" */");
        WriteLine($"class {promiseImplementationClass} implements {promiseClass} {{");
        WriteLine($"    constructor(private _promise: Promise<{className}>, private _client: AspireClientRpc, track = true) {{");
        WriteLine($"        if (track) {{ _client.trackPromise(_promise); }}");
        WriteLine($"    }}");
        WriteLine();

        // Generate then() for PromiseLike interface
        WriteLine($"    then<TResult1 = {className}, TResult2 = never>(");
        WriteLine($"        onfulfilled?: ((value: {className}) => TResult1 | PromiseLike<TResult1>) | null,");
        WriteLine("        onrejected?: ((reason: unknown) => TResult2 | PromiseLike<TResult2>) | null");
        WriteLine("    ): PromiseLike<TResult1 | TResult2> {");
        WriteLine("        return this._promise.then(onfulfilled, onrejected);");
        WriteLine("    }");
        WriteLine();

        var getters = model.Capabilities.Where(c => c.CapabilityKind == AtsCapabilityKind.PropertyGetter).ToList();
        var setters = model.Capabilities.Where(c => c.CapabilityKind == AtsCapabilityKind.PropertySetter).ToList();
        var getterOnlyProperties = TypeScriptApiProjector.GroupPropertiesByName(getters, setters)
            .Where(p => TypeScriptApiProjector.IsGetterOnlyProperty(p.Getter, p.Setter))
            .ToList();

        foreach (var prop in getterOnlyProperties)
        {
            var returnType = _projector.GetGetterOnlyPropertyMethodReturnType(prop.Getter!.ReturnType);
            WriteLine($"    {prop.PropertyName}(): {returnType} {{");
            if (_projector.TryGetPromiseWrapperType(prop.Getter!.ReturnType, out _, out var propertyPromiseImplementationClassName))
            {
                WriteLine($"        return new {propertyPromiseImplementationClassName}(this._promise.then(obj => obj.{prop.PropertyName}()), this._client, false);");
            }
            else
            {
                WriteLine($"        return this._promise.then(obj => obj.{prop.PropertyName}());");
            }
            WriteLine("    }");
            WriteLine();
        }

        // Generate fluent methods that chain via .then()
        foreach (var capability in methods)
        {
            var signature = _projector.ResolveMethodSignature(model, capability);

            // Forward args to underlying object's public method
            var forwardArgs = signature.RequiredParameters
                .Select(parameter => parameter.Name)
                .ToList();
            if (signature.OptionsParameter is { } optionsParameter)
            {
                forwardArgs.Add(optionsParameter.Name);
            }
            if (signature.TrailingCancellationToken is { } cancellationToken)
            {
                forwardArgs.Add(cancellationToken.Name);
            }
            var argsString = string.Join(", ", forwardArgs);

            // Check if return type has a Promise wrapper
            var returnPromiseWrapper = _projector.GetPromiseWrapperForReturnType(capability.ReturnType);
            var returnType = _projector.MapTypeRefToTypeScript(capability.ReturnType);
            var isVoid = capability.ReturnType == null || capability.ReturnType.TypeId == AtsConstants.Void;

            if (returnPromiseWrapper != null)
            {
                var returnPromiseImplementationClass = TypeScriptApiProjector.GetImplementationPromiseClassName(
                    _projector.WrapperClassNames.GetValueOrDefault(capability.ReturnType!.TypeId)
                        ?? TypeScriptApiProjector.DeriveClassName(capability.ReturnType.TypeId));
                // Return type has Promise wrapper - forward to public method, wrap result
                Write($"    {signature.MethodName}(");
                Write(signature.ParameterList);
                WriteLine($"): {returnPromiseWrapper} {{");
                Write($"        return new {returnPromiseImplementationClass}(this._promise.then(obj => obj.{signature.MethodName}(");
                Write(argsString);
                WriteLine($")), this._client);");
                WriteLine("    }");
            }
            else if (isVoid)
            {
                // Void return - forward to public method, wrap result in this class's promise
                Write($"    {signature.MethodName}(");
                Write(signature.ParameterList);
                WriteLine($"): {promiseClass} {{");
                Write($"        return new {promiseImplementationClass}(this._promise.then(obj => obj.{signature.MethodName}(");
                Write(argsString);
                WriteLine($")), this._client);");
                WriteLine("    }");
            }
            else
            {
                // Non-void, non-wrapper return - plain Promise
                Write($"    {signature.MethodName}(");
                Write(signature.ParameterList);
                WriteLine($"): Promise<{returnType}> {{");
                Write($"        return this._promise.then(obj => obj.{signature.MethodName}(");
                Write(argsString);
                WriteLine("));");
                WriteLine("    }");
            }
            WriteLine();
        }

        WriteLine("}");
        WriteLine();
    }

    // ============================================================================
    // Builder Model Helpers (replaces AtsBuilderModelFactory)
    // ============================================================================

}
