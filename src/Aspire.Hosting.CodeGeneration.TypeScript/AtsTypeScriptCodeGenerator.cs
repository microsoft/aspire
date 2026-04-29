// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
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
internal sealed class AtsTypeScriptCodeGenerator : ICodeGenerator
{
    private TextWriter _writer = null!;

    // Mapping of typeId -> wrapper class name for all generated wrapper types
    // Used to resolve parameter types to wrapper classes instead of handle types
    private readonly Dictionary<string, string> _wrapperClassNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AtsTypeRef> _typeRefsById = new(StringComparer.Ordinal);

    // Set of type IDs that have Promise wrappers (types with chainable methods)
    // Used to determine return types for methods
    private readonly HashSet<string> _typesWithPromiseWrappers = new(StringComparer.Ordinal);

    // Set of generated options interfaces to avoid duplicates
    private readonly HashSet<string> _generatedOptionsInterfaces = new(StringComparer.Ordinal);

    // Collected options interfaces to generate (interface name -> list of optional params)
    private readonly Dictionary<string, List<AtsParameterInfo>> _optionsInterfacesToGenerate = new(StringComparer.Ordinal);

    // Mapping from CapabilityId to the options interface name it should use.
    // When methods share a name but have incompatible callback parameter types,
    // separate options interfaces are generated with numeric suffixes.
    private readonly Dictionary<string, string> _capabilityOptionsInterfaceMap = new(StringComparer.Ordinal);

    // Mapping of enum type IDs to TypeScript enum names
    private readonly Dictionary<string, string> _enumTypeNames = new(StringComparer.Ordinal);

    private static string GetInterfaceName(string className) => className;

    private static string GetPromiseInterfaceName(string className) => $"{className}Promise";

    private static string GetImplementationClassName(string className) => $"{className}Impl";

    private static string GetImplementationPromiseClassName(string className) => $"{className}PromiseImpl";

    private static string GetReferenceExpressionInterfaceName() => "ReferenceExpression";

    private static string GetCancellationTokenInterfaceName() => "CancellationToken";

    private static string GetHandleReferenceInterfaceName() => "HandleReference";

    private string GetConcreteClassName(string typeId) => _wrapperClassNames.GetValueOrDefault(typeId)
        ?? DeriveClassName(typeId);

    private string GetPublicPromiseInterfaceName(string typeId) => GetPromiseInterfaceName(GetConcreteClassName(typeId));

    private static bool IsHandleType(AtsTypeRef? typeRef) =>
        typeRef is { Category: AtsTypeCategory.Handle };

    /// <summary>
    /// Maps an AtsTypeRef to a TypeScript type using category-based dispatch.
    /// This is the preferred method - uses type metadata rather than string parsing.
    /// </summary>
    private string MapTypeRefToTypeScript(AtsTypeRef? typeRef)
    {
        if (typeRef == null)
        {
            return "unknown";
        }

        // Check for wrapper class first (handles custom types like resource builders)
        if (_wrapperClassNames.TryGetValue(typeRef.TypeId, out var wrapperClassName))
        {
            return GetInterfaceName(wrapperClassName);
        }

        // ReferenceExpression is a value type defined in base.ts, not a handle-based wrapper
        if (typeRef.TypeId == AtsConstants.ReferenceExpressionTypeId)
        {
            return GetReferenceExpressionInterfaceName();
        }

        return typeRef.Category switch
        {
            AtsTypeCategory.Primitive => MapPrimitiveType(typeRef.TypeId),
            AtsTypeCategory.Enum => MapEnumType(typeRef.TypeId),
            AtsTypeCategory.Handle => GetWrapperOrHandleName(typeRef.TypeId),
            AtsTypeCategory.Dto => GetDtoInterfaceName(typeRef.TypeId),
            AtsTypeCategory.Callback => "Function",  // Callbacks handled separately with full signature
            AtsTypeCategory.Array => $"{MapTypeRefToTypeScript(typeRef.ElementType)}[]",
            AtsTypeCategory.List => $"AspireList<{MapTypeRefToTypeScript(typeRef.ElementType)}>",
            AtsTypeCategory.Dict => typeRef.IsReadOnly
                ? $"Record<{MapTypeRefToTypeScript(typeRef.KeyType)}, {MapTypeRefToTypeScript(typeRef.ValueType)}>"
                : $"AspireDict<{MapTypeRefToTypeScript(typeRef.KeyType)}, {MapTypeRefToTypeScript(typeRef.ValueType)}>",
            AtsTypeCategory.Union => MapUnionTypeToTypeScript(typeRef),
            AtsTypeCategory.Unknown => "any",  // Unknown types use 'any' since they're not in the ATS universe
            _ => "any"  // Fallback for any unhandled categories
        };
    }

    /// <summary>
    /// Maps primitive type IDs to TypeScript types.
    /// </summary>
    private static string MapPrimitiveType(string typeId) => typeId switch
    {
        AtsConstants.String or AtsConstants.Char => "string",
        AtsConstants.Number => "number",
        AtsConstants.Boolean => "boolean",
        AtsConstants.Void => "void",
        AtsConstants.Any => "any",
        AtsConstants.DateTime or AtsConstants.DateTimeOffset or
        AtsConstants.DateOnly or AtsConstants.TimeOnly => "string",
        AtsConstants.TimeSpan => "number",
        AtsConstants.Guid or AtsConstants.Uri => "string",
        AtsConstants.CancellationToken => GetCancellationTokenInterfaceName(),
        _ => typeId
    };

    /// <summary>
    /// Maps an enum type ID to the generated TypeScript enum name.
    /// Throws if the enum type wasn't collected during scanning.
    /// </summary>
    private string MapEnumType(string typeId)
    {
        if (!_enumTypeNames.TryGetValue(typeId, out var enumName))
        {
            throw new InvalidOperationException(
                $"Enum type '{typeId}' was not found in the scanned enum types. " +
                $"This indicates the enum type was not discovered during assembly scanning.");
        }
        return enumName;
    }

    /// <summary>
    /// Maps a union type to TypeScript union syntax (T1 | T2 | ...).
    /// </summary>
    private string MapUnionTypeToTypeScript(AtsTypeRef typeRef)
    {
        if (typeRef.UnionTypes == null || typeRef.UnionTypes.Count == 0)
        {
            return "unknown";
        }

        var memberTypes = typeRef.UnionTypes
            .Select(MapTypeRefToTypeScript)
            .Distinct();

        return string.Join(" | ", memberTypes);
    }

    /// <summary>
    /// Gets the wrapper class name or handle type name for a handle type ID.
    /// Prefers wrapper class if one exists, otherwise generates a handle type name.
    /// </summary>
    private string GetWrapperOrHandleName(string typeId)
    {
        if (_wrapperClassNames.TryGetValue(typeId, out var wrapperClassName))
        {
            return wrapperClassName;
        }
        return GetHandleTypeName(typeId);
    }

    /// <summary>
    /// Gets a TypeScript interface name for a DTO type.
    /// </summary>
    private static string GetDtoInterfaceName(string typeId)
    {
        return ExtractSimpleTypeName(typeId);
    }

    /// <summary>
    /// Maps a user-supplied input type to TypeScript.
    /// For interface handle types, generated APIs accept any handle-bearing wrapper instance.
    /// For cancellation tokens, generated APIs accept either an AbortSignal or a transport-safe CancellationToken.
    /// </summary>
    /// <remarks>
    /// Handle types are widened to accept <c>Awaitable&lt;T&gt;</c> so callers can pass un-awaited
    /// fluent chains directly. Examples:
    /// <code>
    /// // Input: RedisResource handle type
    /// // Output: "Awaitable&lt;RedisResource&gt;"
    ///
    /// // Input: Union of string | RedisResource
    /// // Output: "string | Awaitable&lt;RedisResource&gt;"
    ///
    /// // Input: CancellationToken type
    /// // Output: "AbortSignal | CancellationToken"
    ///
    /// // Input: plain string type
    /// // Output: "string"
    /// </code>
    /// </remarks>
    private string MapInputTypeToTypeScript(AtsTypeRef? typeRef)
    {
        if (typeRef?.Category == AtsTypeCategory.Union)
        {
            return MapInputUnionTypeToTypeScript(typeRef);
        }

        if (IsInterfaceHandleType(typeRef))
        {
            if (TryMapInterfaceInputTypeToTypeScript(typeRef!) is { } interfaceInputType)
            {
                return $"Awaitable<{interfaceInputType}>";
            }

            var handleName = GetHandleReferenceInterfaceName();
            return $"Awaitable<{handleName}>";
        }

        if (IsHandleType(typeRef) && _wrapperClassNames.TryGetValue(typeRef!.TypeId, out var className))
        {
            var ifaceName = GetInterfaceName(className);
            return $"Awaitable<{ifaceName}>";
        }

        if (IsCancellationTokenType(typeRef))
        {
            return $"AbortSignal | {GetCancellationTokenInterfaceName()}";
        }

        return MapTypeRefToTypeScript(typeRef);
    }

    private string MapInputUnionTypeToTypeScript(AtsTypeRef typeRef)
    {
        if (typeRef.UnionTypes == null || typeRef.UnionTypes.Count == 0)
        {
            throw new InvalidOperationException("Union input types must define at least one member type.");
        }

        // Build union structurally: each member is mapped individually.
        // Handle types become Awaitable<T>, non-handle types pass through as-is.
        var nonHandleTypes = new List<string>();
        var handleTypeNames = new List<string>();

        foreach (var memberRef in typeRef.UnionTypes)
        {
            if (IsWidenedHandleType(memberRef))
            {
                // Get the base type name without Awaitable wrapper for combining
                var baseName = IsInterfaceHandleType(memberRef) && TryMapInterfaceInputTypeToTypeScript(memberRef) is { } expanded
                    ? expanded
                    : MapTypeRefToTypeScript(memberRef);
                nonHandleTypes.Add(baseName);
                handleTypeNames.Add(baseName);
            }
            else
            {
                nonHandleTypes.Add(MapInputTypeToTypeScript(memberRef));
            }
        }

        var allBaseTypes = nonHandleTypes
            .SelectMany(t => t.Split(" | ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (handleTypeNames.Count > 0)
        {
            var handleUnion = string.Join(" | ", handleTypeNames
                .SelectMany(t => t.Split(" | ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                .Distinct(StringComparer.Ordinal));
            return string.Join(" | ", allBaseTypes) + $" | Awaitable<{handleUnion}>";
        }

        return string.Join(" | ", allBaseTypes);
    }

    /// <summary>
    /// Maps a parameter to its TypeScript type, handling callbacks specially.
    /// </summary>
    private string MapParameterToTypeScript(AtsParameterInfo param)
    {
        if (param.IsCallback)
        {
            return GenerateCallbackTypeSignature(param.CallbackParameters, param.CallbackReturnType);
        }

        return MapInputTypeToTypeScript(param.Type);
    }

    private void WriteCapabilityDocComment(string indent, AtsCapabilityInfo capability)
    {
        List<string>? lines = null;

        if (!string.IsNullOrWhiteSpace(capability.Description))
        {
            lines = [];
            lines.AddRange(capability.Description
                .Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        }

        if (capability.IsObsolete)
        {
            lines ??= [];
            lines.Add(string.IsNullOrWhiteSpace(capability.ObsoleteMessage)
                ? "@deprecated"
                : $"@deprecated {capability.ObsoleteMessage}");
        }

        if (lines is not { Count: > 0 })
        {
            return;
        }

        if (lines.Count == 1 && !lines[0].StartsWith("@deprecated", StringComparison.Ordinal))
        {
            WriteLine($"{indent}/** {lines[0]} */");
            return;
        }

        WriteLine($"{indent}/**");
        foreach (var line in lines)
        {
            WriteLine($"{indent} * {line}");
        }
        WriteLine($"{indent} */");
    }

    private void WritePropertyDocComment(string indent, AtsCapabilityInfo? getter, AtsCapabilityInfo? setter)
    {
        var capability = getter is not null && (!string.IsNullOrWhiteSpace(getter.Description) || getter.IsObsolete)
            ? getter
            : setter;

        if (capability is not null)
        {
            WriteCapabilityDocComment(indent, capability);
        }
    }

    private string? TryMapInterfaceInputTypeToTypeScript(AtsTypeRef typeRef)
    {
        List<string>? assignableWrapperTypes = null;

        foreach (var candidateTypeRef in _typeRefsById.Values)
        {
            if (!IsAssignableToInterface(candidateTypeRef, typeRef.TypeId) ||
                !_wrapperClassNames.TryGetValue(candidateTypeRef.TypeId, out var wrapperClassName))
            {
                continue;
            }

            assignableWrapperTypes ??= [];
            assignableWrapperTypes.Add(wrapperClassName);
        }

        if (assignableWrapperTypes is not { Count: > 0 })
        {
            return null;
        }

        return string.Join(" | ", assignableWrapperTypes
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static n => n, StringComparer.Ordinal));
    }

    private static bool IsAssignableToInterface(AtsTypeRef candidateTypeRef, string interfaceTypeId)
    {
        if (string.Equals(candidateTypeRef.TypeId, interfaceTypeId, StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var implementedInterface in candidateTypeRef.ImplementedInterfaces)
        {
            if (IsAssignableToInterface(implementedInterface, interfaceTypeId))
            {
                return true;
            }
        }

        return candidateTypeRef.BaseType is not null && IsAssignableToInterface(candidateTypeRef.BaseType, interfaceTypeId);
    }

    /// <summary>
    /// Checks if a type reference is an interface handle type.
    /// Interface handles need union types to accept wrapper classes.
    /// </summary>
    private static bool IsInterfaceHandleType(AtsTypeRef? typeRef)
    {
        if (typeRef == null)
        {
            return false;
        }
        return typeRef.Category == AtsTypeCategory.Handle && typeRef.IsInterface;
    }

    private static bool IsCancellationTokenType(AtsTypeRef? typeRef) => typeRef?.TypeId == AtsConstants.CancellationToken;

    private static string GetRpcArgumentValueExpression(string parameterName, AtsTypeRef? typeRef)
    {
        if (IsCancellationTokenType(typeRef))
        {
            return $"CancellationToken.fromValue({parameterName})";
        }

        return parameterName;
    }

    private static string GetRpcArgumentEntry(string parameterName, AtsTypeRef? typeRef)
    {
        var valueExpression = GetRpcArgumentValueExpression(parameterName, typeRef);
        return valueExpression == parameterName
            ? parameterName
            : $"{parameterName}: {valueExpression}";
    }

    private static string GetRpcArgumentEntry(AtsParameterInfo param, bool useRegisteredCallback = true)
    {
        if (useRegisteredCallback && param.IsCallback)
        {
            return $"{param.Name}: {param.Name}Id";
        }

        return GetRpcArgumentEntry(param.Name, param.Type);
    }

    private static string GetRpcArgumentExpression(AtsParameterInfo param, bool useRegisteredCallback = true)
    {
        if (useRegisteredCallback && param.IsCallback)
        {
            return $"{param.Name}Id";
        }

        return GetRpcArgumentValueExpression(param.Name, param.Type);
    }

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

        // Add embedded resource files (transport.ts, base.ts)
        files["transport.ts"] = GetEmbeddedResource("transport.ts");
        files["base.ts"] = GetEmbeddedResource("base.ts");

        // Generate the capability-based aspire.ts SDK
        files["aspire.ts"] = GenerateAspireSdk(context);

        return files;
    }

    private static string GetEmbeddedResource(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"Aspire.Hosting.CodeGeneration.TypeScript.Resources.{name}";

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{name}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Gets a valid TypeScript method name from a capability method name.
    /// Handles dotted names like "EnvironmentContext.resource" by extracting just the final part.
    /// </summary>
    private static string GetTypeScriptMethodName(string methodName)
    {
        var dotIndex = methodName.LastIndexOf('.');
        return dotIndex >= 0 ? methodName[(dotIndex + 1)..] : methodName;
    }

    /// <summary>
    /// Generates the aspire.ts SDK file with capability-based API.
    /// </summary>
    private string GenerateAspireSdk(AtsContext context)
    {
        using var stringWriter = new StringWriter(CultureInfo.InvariantCulture);
        _writer = stringWriter;

        // Header
        WriteLine("""
            // aspire.ts - Capability-based Aspire SDK
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
            } from './transport.js';
            import type { AspireClientRpc } from './transport.js';

            import type { HandleReference } from './base.js';

            import {
                ResourceBuilderBase,
                ReferenceExpression,
                refExpr,
                AspireDict,
                AspireList
            } from './base.js';

            import type { Awaitable } from './base.js';
            """);
        WriteLine();

        var capabilities = context.Capabilities;
        var dtoTypes = context.DtoTypes;
        var enumTypes = context.EnumTypes;
        var exportedValues = context.ExportedValues;

        // Get builder models (flattened - each builder has all its applicable capabilities)
        var allBuilders = CreateBuilderModels(capabilities);
        var entryPoints = GetEntryPointCapabilities(capabilities);

        // All builders (no special filtering)
        var builders = allBuilders;

        // Entry point methods that don't extend any type go on AspireClient
        var clientMethods = entryPoints
            .Where(c => string.IsNullOrEmpty(c.TargetTypeId))
            .ToList();

        // Collect all unique type IDs for handle type aliases
        // Exclude DTO types - they have their own interfaces, not handle aliases
        var dtoTypeIds = new HashSet<string>(dtoTypes.Select(d => d.TypeId));
        var typeIds = new HashSet<string>();
        foreach (var typeId in CollectAllReferencedTypes(capabilities).Keys)
        {
            if (!dtoTypeIds.Contains(typeId))
            {
                typeIds.Add(typeId);
            }
        }

        // Ensure all builder type IDs have handle type aliases.
        // CreateBuilderModels discovers additional resource types via CollectAllReferencedTypes
        // (e.g. types that appear only in return types or parameters but aren't direct capability targets).
        // Without this, the builder class references a handle type that was never declared.
        foreach (var builder in builders)
        {
            if (!dtoTypeIds.Contains(builder.TypeId))
            {
                typeIds.Add(builder.TypeId);
            }
        }

        // Generate handle type aliases
        GenerateHandleTypeAliases(typeIds);

        // Generate enum types
        GenerateEnumTypes(enumTypes);

        // Generate DTO interfaces
        GenerateDtoInterfaces(dtoTypes);

        // Generate exported immutable values
        GenerateExportedValues(exportedValues, dtoTypes.ToDictionary(dto => dto.TypeId, StringComparer.Ordinal));

        // Separate builders into categories:
        // 1. Resource builders: IResource*, ContainerResource, etc.
        // 2. Type classes: everything else (context types, wrapper types)
        var resourceBuilders = builders.Where(b => b.TargetType?.IsResourceBuilder == true).ToList();
        var typeClasses = builders.Where(b => b.TargetType?.IsResourceBuilder != true).ToList();

        // Build wrapper class name mapping for type resolution BEFORE generating options interfaces
        // This allows parameter types to use wrapper class names instead of handle types
        _wrapperClassNames.Clear();
        _typeRefsById.Clear();
        _typesWithPromiseWrappers.Clear();
        _generatedOptionsInterfaces.Clear();
        _optionsInterfacesToGenerate.Clear();
        _capabilityOptionsInterfaceMap.Clear();

        foreach (var builder in resourceBuilders)
        {
            _wrapperClassNames[builder.TypeId] = builder.BuilderClassName;
            if (builder.TargetType is { } targetType)
            {
                _typeRefsById[builder.TypeId] = targetType;
            }
            // All resource builders get Promise wrappers
            _typesWithPromiseWrappers.Add(builder.TypeId);
        }
        foreach (var typeClass in typeClasses)
        {
            _wrapperClassNames[typeClass.TypeId] = DeriveClassName(typeClass.TypeId);
            if (typeClass.TargetType is { } targetType)
            {
                _typeRefsById[typeClass.TypeId] = targetType;
            }
            // Type classes with methods get Promise wrappers
            if (HasChainableMethods(typeClass))
            {
                _typesWithPromiseWrappers.Add(typeClass.TypeId);
            }
        }
        // Note: ReferenceExpression is intentionally NOT added to _wrapperClassNames.
        // It is a value type defined in base.ts with a private constructor and static factory,
        // not a handle-based wrapper. It is handled via MapTypeRefToTypeScript instead.

        // Pre-scan all capabilities to collect options interfaces
        // This must happen AFTER wrapper class names are populated so types resolve correctly
        foreach (var builder in builders)
        {
            foreach (var cap in builder.Capabilities)
            {
                var (_, optionalParams) = SeparateParameters(cap.Parameters);
                if (optionalParams.Count > 0 && !TryGetDirectOptionsParameter(optionalParams, out _))
                {
                    RegisterOptionsInterface(cap.CapabilityId, cap.MethodName, optionalParams);
                }
            }
        }

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
            var handleName = GetHandleTypeName(typeId);
            var description = GetTypeDescription(typeId);
            WriteLine($"/** {description} */");
            // Internal type alias - not exported (users work with wrapper classes)
            WriteLine($"type {handleName} = Handle<'{typeId}'>;");
            WriteLine();
        }
    }

    /// <summary>
    /// Generates TypeScript enums from discovered enum types.
    /// </summary>
    private void GenerateEnumTypes(IReadOnlyList<AtsEnumTypeInfo> enumTypes)
    {
        if (enumTypes.Count == 0)
        {
            return;
        }

        WriteLine("// ============================================================================");
        WriteLine("// Enum Types");
        WriteLine("// ============================================================================");
        WriteLine();

        foreach (var enumType in enumTypes.OrderBy(e => e.Name))
        {
            // Track enum name for type mapping
            _enumTypeNames[enumType.TypeId] = enumType.Name;

            WriteLine($"/** Enum type for {enumType.Name} */");
            WriteLine($"export enum {enumType.Name} {{");

            foreach (var value in enumType.Values)
            {
                // Enums serialize as strings in JSON
                WriteLine($"    {value} = \"{value}\",");
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
        if (dtoTypes.Count == 0)
        {
            return;
        }

        WriteLine("// ============================================================================");
        WriteLine("// DTO Interfaces");
        WriteLine("// ============================================================================");
        WriteLine();

        foreach (var dto in dtoTypes.OrderBy(d => d.Name))
        {
            var interfaceName = GetDtoInterfaceName(dto.TypeId);

            WriteLine($"/** DTO interface for {dto.Name} */");
            WriteLine($"export interface {interfaceName} {{");

            foreach (var prop in dto.Properties)
            {
                var tsType = MapTypeRefToTypeScript(prop.Type);
                // All DTO properties are optional in TypeScript to allow partial objects
                // Convert PascalCase to camelCase for TypeScript
                var propName = ToCamelCase(prop.Name);
                WriteLine($"    {propName}?: {tsType};");
            }

            // Add client-only properties that don't exist in the C# DTO
            if (dto.Name == "CreateBuilderOptions")
            {
                WriteLine("    /** When false, pre-flush rejected promises are not re-thrown by build(). Default: true. */");
                WriteLine("    throwOnPendingRejections?: boolean;");
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
                if (!string.IsNullOrWhiteSpace(valueInfo.Description))
                {
                    WriteLine($"{indent}/** {valueInfo.Description} */");
                }

                var literal = RenderTypeScriptExportedValue(valueInfo.Value, valueInfo.Type, dtoTypesById);
                var exportedType = MapTypeRefToTypeScript(valueInfo.Type);
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

            members.Add($"{ToCamelCase(property.Name)}: {RenderTypeScriptExportedValue(propertyValue, property.Type, dtoTypesById)}");
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
    /// Converts a PascalCase name to camelCase.
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
    /// Converts a camelCase name to PascalCase.
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
    /// Gets the options interface name for a method.
    /// Strips any type prefix (e.g., "TypeName.methodName" -> "MethodName").
    /// </summary>
    private static string GetOptionsInterfaceName(string methodName)
    {
        // Strip type prefix if present (e.g., "EndpointReference.getExpression" -> "getExpression")
        var simpleName = methodName.Contains('.')
            ? methodName[(methodName.LastIndexOf('.') + 1)..]
            : methodName;
        return $"{ToPascalCase(simpleName)}Options";
    }

    /// <summary>
    /// Gets the options interface name for a specific capability, accounting for type conflicts.
    /// Falls back to the default method-name-based interface if no specific mapping exists.
    /// </summary>
    private string ResolveOptionsInterfaceName(AtsCapabilityInfo capability)
    {
        if (_capabilityOptionsInterfaceMap.TryGetValue(capability.CapabilityId, out var interfaceName))
        {
            return interfaceName;
        }
        return GetOptionsInterfaceName(capability.MethodName);
    }

    /// <summary>
    /// Separates parameters into required and optional lists.
    /// Required = not optional and not nullable.
    /// </summary>
    private static (List<AtsParameterInfo> Required, List<AtsParameterInfo> Optional) SeparateParameters(
        IEnumerable<AtsParameterInfo> parameters)
    {
        var required = new List<AtsParameterInfo>();
        var optional = new List<AtsParameterInfo>();

        foreach (var param in parameters)
        {
            if (param.IsOptional || param.IsNullable)
            {
                optional.Add(param);
            }
            else
            {
                required.Add(param);
            }
        }

        return (required, optional);
    }

    private static bool TryGetDirectOptionsParameter(List<AtsParameterInfo> optionalParams, out AtsParameterInfo? directOptionsParam)
    {
        directOptionsParam = null;

        // When ATS already exposes a single DTO parameter named "options", reuse that DTO type
        // directly so the generated TypeScript API stays flat instead of wrapping it in another
        // generated options object.
        if (optionalParams.Count != 1)
        {
            return false;
        }

        var candidate = optionalParams[0];
        if (!string.Equals(candidate.Name, "options", StringComparison.Ordinal) || candidate.Type?.Category != AtsTypeCategory.Dto)
        {
            return false;
        }

        directOptionsParam = candidate;
        return true;
    }

    /// <summary>
    /// Registers an options interface to be generated later.
    /// Uses method name to create the interface name. When methods share a name but have
    /// incompatible callback parameter types, separate options interfaces are created with
    /// numeric suffixes (e.g., RunAsEmulatorOptions, RunAsEmulator1Options).
    /// </summary>
    private void RegisterOptionsInterface(string capabilityId, string methodName, List<AtsParameterInfo> optionalParams)
    {
        if (optionalParams.Count == 0)
        {
            return;
        }

        var baseInterfaceName = GetOptionsInterfaceName(methodName);

        // Check if an existing interface with this name is compatible
        if (_optionsInterfacesToGenerate.TryGetValue(baseInterfaceName, out var existingParams))
        {
            if (AreOptionsCompatible(existingParams, optionalParams))
            {
                // Compatible - merge any new parameters and share the interface
                var existingNames = new HashSet<string>(existingParams.Select(p => p.Name));
                foreach (var param in optionalParams)
                {
                    if (existingNames.Add(param.Name))
                    {
                        existingParams.Add(param);
                    }
                }
                _capabilityOptionsInterfaceMap[capabilityId] = baseInterfaceName;
                return;
            }

            // Incompatible - find or create a suffixed interface
            for (var suffix = 1; ; suffix++)
            {
                var suffixedName = GetOptionsInterfaceName($"{methodName}{suffix}");
                if (!_optionsInterfacesToGenerate.TryGetValue(suffixedName, out var suffixedParams))
                {
                    // Create a new interface with this suffix
                    _generatedOptionsInterfaces.Add(suffixedName);
                    _optionsInterfacesToGenerate[suffixedName] = [.. optionalParams];
                    _capabilityOptionsInterfaceMap[capabilityId] = suffixedName;
                    return;
                }

                if (AreOptionsCompatible(suffixedParams, optionalParams))
                {
                    // Compatible with this suffixed interface - share it
                    var existingNames2 = new HashSet<string>(suffixedParams.Select(p => p.Name));
                    foreach (var param in optionalParams)
                    {
                        if (existingNames2.Add(param.Name))
                        {
                            suffixedParams.Add(param);
                        }
                    }
                    _capabilityOptionsInterfaceMap[capabilityId] = suffixedName;
                    return;
                }
            }
        }
        else
        {
            // First registration - create the interface
            _generatedOptionsInterfaces.Add(baseInterfaceName);
            _optionsInterfacesToGenerate[baseInterfaceName] = [.. optionalParams];
            _capabilityOptionsInterfaceMap[capabilityId] = baseInterfaceName;
        }
    }

    /// <summary>
    /// Checks whether two sets of optional parameters are compatible for sharing an options interface.
    /// Parameters with the same name must have the same type (including callback parameter types).
    /// </summary>
    private static bool AreOptionsCompatible(List<AtsParameterInfo> existing, List<AtsParameterInfo> candidate)
    {
        foreach (var param in candidate)
        {
            var match = existing.FirstOrDefault(p => p.Name == param.Name);
            if (match is null)
            {
                continue; // New parameter, no conflict
            }

            // Same name - check type compatibility
            if (!AreParameterTypesEqual(match, param))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Checks whether two parameter infos have the same type (including callback types).
    /// </summary>
    private static bool AreParameterTypesEqual(AtsParameterInfo a, AtsParameterInfo b)
    {
        // Compare base type
        var aTypeId = a.Type?.TypeId;
        var bTypeId = b.Type?.TypeId;
        if (!string.Equals(aTypeId, bTypeId, StringComparison.Ordinal))
        {
            return false;
        }

        // Compare callback parameter types
        if (a.IsCallback != b.IsCallback)
        {
            return false;
        }

        if (a.IsCallback && b.IsCallback)
        {
            var aCallbackParams = a.CallbackParameters ?? [];
            var bCallbackParams = b.CallbackParameters ?? [];

            if (aCallbackParams.Count != bCallbackParams.Count)
            {
                return false;
            }

            for (var i = 0; i < aCallbackParams.Count; i++)
            {
                if (!string.Equals(aCallbackParams[i].Type.TypeId, bCallbackParams[i].Type.TypeId, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            // Compare callback return types
            var aReturnTypeId = a.CallbackReturnType?.TypeId;
            var bReturnTypeId = b.CallbackReturnType?.TypeId;
            if (!string.Equals(aReturnTypeId, bReturnTypeId, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Generates all collected options interfaces.
    /// </summary>
    private void GenerateOptionsInterfaces()
    {
        if (_optionsInterfacesToGenerate.Count == 0)
        {
            return;
        }

        WriteLine("// ============================================================================");
        WriteLine("// Options Interfaces");
        WriteLine("// ============================================================================");
        WriteLine();

        foreach (var (interfaceName, optionalParams) in _optionsInterfacesToGenerate.OrderBy(kvp => kvp.Key))
        {
            WriteLine($"export interface {interfaceName} {{");
            foreach (var param in optionalParams)
            {
                var tsType = MapParameterToTypeScript(param);
                WriteLine($"    {param.Name}?: {tsType};");
            }
            WriteLine("}");
            WriteLine();
        }
    }

    private static string GetTypeDescription(string typeId)
    {
        var typeName = ExtractSimpleTypeName(typeId);
        return $"Handle to {typeName}";
    }

    private string BuildPublicParameterList(
        List<AtsParameterInfo> requiredParams,
        bool hasOptionals,
        string optionsInterfaceName,
        string optionsParameterName = "options")
    {
        var publicParamDefs = new List<string>();
        foreach (var param in requiredParams)
        {
            var tsType = MapParameterToTypeScript(param);
            publicParamDefs.Add($"{param.Name}: {tsType}");
        }
        if (hasOptionals)
        {
            publicParamDefs.Add($"{optionsParameterName}?: {optionsInterfaceName}");
        }

        return string.Join(", ", publicParamDefs);
    }

    private static string GetPublicOptionsParameterName(
        IReadOnlyList<AtsParameterInfo> userParams,
        bool hasOptionals,
        bool hasDirectOptionsParameter)
    {
        if (!hasOptionals || hasDirectOptionsParameter)
        {
            return "options";
        }

        if (!userParams.Any(p => string.Equals(p.Name, "options", StringComparison.Ordinal)))
        {
            return "options";
        }

        var candidate = "optionsBag";
        while (userParams.Any(p => string.Equals(p.Name, candidate, StringComparison.Ordinal)))
        {
            candidate = $"_{candidate}";
        }

        return candidate;
    }

    private static bool IsGetterOnlyProperty(AtsCapabilityInfo? getter, AtsCapabilityInfo? setter) => getter is not null && setter is null;

    private string GetGetterOnlyPropertyReturnType(AtsTypeRef? typeRef)
    {
        if (typeRef == null)
        {
            return "unknown";
        }

        if (IsDictionaryType(typeRef))
        {
            var keyType = typeRef.KeyType != null ? MapTypeRefToTypeScript(typeRef.KeyType) : "string";
            var valueType = typeRef.ValueType != null ? MapTypeRefToTypeScript(typeRef.ValueType) : "unknown";
            return $"AspireDict<{keyType}, {valueType}>";
        }

        if (IsListType(typeRef))
        {
            var elementType = typeRef.ElementType != null ? MapTypeRefToTypeScript(typeRef.ElementType) : "unknown";
            return $"AspireList<{elementType}>";
        }

        return MapTypeRefToTypeScript(typeRef);
    }

    private void GenerateGetterOnlyPropertyPromiseSignature(string propertyName, AtsCapabilityInfo getter)
    {
        var returnType = GetGetterOnlyPropertyReturnType(getter.ReturnType);
        WriteCapabilityDocComment("    ", getter);
        WriteLine($"    {propertyName}(): Promise<{returnType}>;");
    }

    private void GenerateInterfaceProperty(string propertyName, AtsCapabilityInfo? getter, AtsCapabilityInfo? setter)
    {
        if (IsGetterOnlyProperty(getter, setter))
        {
            GenerateGetterOnlyPropertyPromiseSignature(propertyName, getter!);
            return;
        }

        if (getter?.ReturnType is { } returnType)
        {
            if (IsDictionaryType(returnType))
            {
                var keyType = returnType.KeyType != null ? MapTypeRefToTypeScript(returnType.KeyType) : "string";
                var valueType = returnType.ValueType != null ? MapTypeRefToTypeScript(returnType.ValueType) : "unknown";
                WritePropertyDocComment("    ", getter, setter);
                WriteLine($"    readonly {propertyName}: AspireDict<{keyType}, {valueType}>;");
                return;
            }

            if (IsListType(returnType))
            {
                var elementType = returnType.ElementType != null ? MapTypeRefToTypeScript(returnType.ElementType) : "unknown";
                WritePropertyDocComment("    ", getter, setter);
                WriteLine($"    readonly {propertyName}: AspireList<{elementType}>;");
                return;
            }
        }

        WritePropertyDocComment("    ", getter, setter);
        WriteLine($"    {propertyName}: {{");

        if (getter != null)
        {
            var returnTypeName = MapTypeRefToTypeScript(getter.ReturnType);
            WriteLine($"        get: () => Promise<{returnTypeName}>;");
        }

        if (setter != null)
        {
            var valueParam = setter.Parameters.FirstOrDefault(p => p.Name == "value");
            if (valueParam != null)
            {
                var valueType = MapInputTypeToTypeScript(valueParam.Type);
                WriteLine($"        set: (value: {valueType}) => Promise<void>;");
            }
        }

        WriteLine("    };");
    }

    private string GetBuilderPromiseInterfaceForMethod(BuilderModel builder, AtsCapabilityInfo capability)
    {
        if (capability.ReturnsBuilder && capability.ReturnType?.TypeId != null &&
            !string.Equals(capability.ReturnType.TypeId, builder.TypeId, StringComparison.Ordinal) &&
            !string.Equals(capability.ReturnType.TypeId, capability.TargetTypeId, StringComparison.Ordinal))
        {
            return GetPublicPromiseInterfaceName(capability.ReturnType.TypeId);
        }

        return GetPromiseInterfaceName(builder.BuilderClassName);
    }

    private void GenerateBuilderInterface(BuilderModel builder)
    {
        var interfaceName = GetInterfaceName(builder.BuilderClassName);

        WriteLine("// ============================================================================");
        WriteLine($"// {interfaceName}");
        WriteLine("// ============================================================================");
        WriteLine();
        WriteLine($"export interface {interfaceName} {{");
        WriteLine("    toJSON(): MarshalledHandle;");

        var getters = builder.Capabilities.Where(c => c.CapabilityKind == AtsCapabilityKind.PropertyGetter).ToList();
        var setters = builder.Capabilities.Where(c => c.CapabilityKind == AtsCapabilityKind.PropertySetter).ToList();
        if (getters.Count > 0 || setters.Count > 0)
        {
            var properties = GroupPropertiesByName(getters, setters);
            foreach (var prop in properties)
            {
                GenerateInterfaceProperty(prop.PropertyName, prop.Getter, prop.Setter);
            }
        }

        foreach (var capability in builder.Capabilities.Where(c =>
            c.CapabilityKind != AtsCapabilityKind.PropertyGetter &&
            c.CapabilityKind != AtsCapabilityKind.PropertySetter))
        {
            var targetParamName = capability.TargetParameterName ?? "builder";
            var userParams = capability.Parameters.Where(p => p.Name != targetParamName).ToList();
            var (requiredParams, optionalParams) = SeparateParameters(userParams);
            var hasOptionals = optionalParams.Count > 0;
            var hasDirectOptionsParameter = TryGetDirectOptionsParameter(optionalParams, out var directOptionsParam);
            var optionsInterfaceName = hasDirectOptionsParameter ? MapParameterToTypeScript(directOptionsParam!) : ResolveOptionsInterfaceName(capability);
            var publicParamsString = BuildPublicParameterList(requiredParams, hasOptionals, optionsInterfaceName);
            var hasNonBuilderReturn = !capability.ReturnsBuilder && capability.ReturnType != null;

            WriteCapabilityDocComment("    ", capability);
            if (hasNonBuilderReturn)
            {
                var returnType = MapTypeRefToTypeScript(capability.ReturnType);
                WriteLine($"    {capability.MethodName}({publicParamsString}): Promise<{returnType}>;");
            }
            else
            {
                WriteLine($"    {capability.MethodName}({publicParamsString}): {GetBuilderPromiseInterfaceForMethod(builder, capability)};");
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
        var getterOnlyProperties = GroupPropertiesByName(getters, setters)
            .Where(p => IsGetterOnlyProperty(p.Getter, p.Setter))
            .ToList();

        if (capabilities.Count == 0 && getterOnlyProperties.Count == 0)
        {
            return;
        }

        var interfaceName = GetInterfaceName(builder.BuilderClassName);
        var promiseInterfaceName = GetPromiseInterfaceName(builder.BuilderClassName);

        WriteLine($"export interface {promiseInterfaceName} extends PromiseLike<{interfaceName}> {{");

        foreach (var prop in getterOnlyProperties)
        {
            GenerateGetterOnlyPropertyPromiseSignature(prop.PropertyName, prop.Getter!);
        }

        foreach (var capability in capabilities)
        {
            var targetParamName = capability.TargetParameterName ?? "builder";
            var userParams = capability.Parameters.Where(p => p.Name != targetParamName).ToList();
            var (requiredParams, optionalParams) = SeparateParameters(userParams);
            var hasOptionals = optionalParams.Count > 0;
            var hasDirectOptionsParameter = TryGetDirectOptionsParameter(optionalParams, out var directOptionsParam);
            var optionsInterfaceName = hasDirectOptionsParameter ? MapParameterToTypeScript(directOptionsParam!) : ResolveOptionsInterfaceName(capability);
            var paramsString = BuildPublicParameterList(requiredParams, hasOptionals, optionsInterfaceName);
            var hasNonBuilderReturn = !capability.ReturnsBuilder && capability.ReturnType != null;

            WriteCapabilityDocComment("    ", capability);
            if (hasNonBuilderReturn)
            {
                var returnType = MapTypeRefToTypeScript(capability.ReturnType);
                WriteLine($"    {capability.MethodName}({paramsString}): Promise<{returnType}>;");
            }
            else
            {
                WriteLine($"    {capability.MethodName}({paramsString}): {GetBuilderPromiseInterfaceForMethod(builder, capability)};");
            }
        }

        WriteLine("}");
        WriteLine();
    }

    private void GenerateTypeClassInterfaceMethod(string className, AtsCapabilityInfo capability)
    {
        var methodName = !string.IsNullOrEmpty(capability.OwningTypeName) && capability.MethodName.Contains('.')
            ? capability.MethodName[(capability.MethodName.LastIndexOf('.') + 1)..]
            : GetTypeScriptMethodName(capability.MethodName);
        var targetParamName = capability.TargetParameterName ?? "context";
        var userParams = capability.Parameters.Where(p => p.Name != targetParamName).ToList();
        var (requiredParams, optionalParams) = SeparateParameters(userParams);
        var hasOptionals = optionalParams.Count > 0;
        var hasDirectOptionsParameter = TryGetDirectOptionsParameter(optionalParams, out var directOptionsParam);
        var optionsInterfaceName = hasDirectOptionsParameter ? MapParameterToTypeScript(directOptionsParam!) : ResolveOptionsInterfaceName(capability);
        var paramsString = BuildPublicParameterList(requiredParams, hasOptionals, optionsInterfaceName);
        var isVoid = capability.ReturnType == null || capability.ReturnType.TypeId == AtsConstants.Void;

        WriteCapabilityDocComment("    ", capability);
        if (capability.ReturnType != null && _typesWithPromiseWrappers.Contains(capability.ReturnType.TypeId))
        {
            WriteLine($"    {methodName}({paramsString}): {GetPublicPromiseInterfaceName(capability.ReturnType.TypeId)};");
        }
        else if (isVoid)
        {
            WriteLine($"    {methodName}({paramsString}): {GetPromiseInterfaceName(className)};");
        }
        else
        {
            var returnType = MapTypeRefToTypeScript(capability.ReturnType);
            WriteLine($"    {methodName}({paramsString}): Promise<{returnType}>;");
        }
    }

    private void GenerateTypeClassInterface(BuilderModel model)
    {
        var className = DeriveClassName(model.TypeId);
        var interfaceName = GetInterfaceName(className);

        WriteLine("// ============================================================================");
        WriteLine($"// {interfaceName}");
        WriteLine("// ============================================================================");
        WriteLine();
        WriteLine($"export interface {interfaceName} {{");
        WriteLine("    toJSON(): MarshalledHandle;");

        var getters = model.Capabilities.Where(c => c.CapabilityKind == AtsCapabilityKind.PropertyGetter).ToList();
        var setters = model.Capabilities.Where(c => c.CapabilityKind == AtsCapabilityKind.PropertySetter).ToList();
        var contextMethods = model.Capabilities.Where(c => c.CapabilityKind == AtsCapabilityKind.InstanceMethod).ToList();
        var otherMethods = model.Capabilities.Where(c => c.CapabilityKind == AtsCapabilityKind.Method).ToList();
        var standardMethods = contextMethods.Concat(otherMethods).ToList();
        var hasMethods = standardMethods.Count > 0;

        var properties = GroupPropertiesByName(getters, setters);
        foreach (var prop in properties)
        {
            GenerateInterfaceProperty(prop.PropertyName, prop.Getter, prop.Setter);
        }

        foreach (var method in standardMethods)
        {
            GenerateTypeClassInterfaceMethod(className, method);
        }

        WriteLine("}");
        WriteLine();

        if (!hasMethods)
        {
            return;
        }

        var promiseInterfaceName = GetPromiseInterfaceName(className);
        WriteLine($"export interface {promiseInterfaceName} extends PromiseLike<{interfaceName}> {{");
        foreach (var prop in properties.Where(p => IsGetterOnlyProperty(p.Getter, p.Setter)))
        {
            GenerateGetterOnlyPropertyPromiseSignature(prop.PropertyName, prop.Getter!);
        }
        foreach (var method in standardMethods)
        {
            GenerateTypeClassInterfaceMethod(className, method);
        }
        WriteLine("}");
        WriteLine();
    }

    private void GenerateBuilderClass(BuilderModel builder)
    {
        GenerateBuilderInterface(builder);
        GenerateBuilderPromiseInterface(builder);

        var implementationClassName = GetImplementationClassName(builder.BuilderClassName);

        WriteLine("// ============================================================================");
        WriteLine($"// {implementationClassName}");
        WriteLine("// ============================================================================");
        WriteLine();

        var handleType = GetHandleTypeName(builder.TypeId);

        // Generate builder class extending ResourceBuilderBase
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
            var properties = GroupPropertiesByName(getters, setters);
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
        var (requiredParams, optionalParams) = SeparateParameters(userParams);
        var hasOptionals = optionalParams.Count > 0;
        var hasDirectOptionsParameter = TryGetDirectOptionsParameter(optionalParams, out var directOptionsParam);
        var optionsTypeName = hasDirectOptionsParameter ? MapParameterToTypeScript(directOptionsParam!) : ResolveOptionsInterfaceName(capability);
        var publicOptionsParamName = GetPublicOptionsParameterName(userParams, hasOptionals, hasDirectOptionsParameter);

        // Build parameter list for public method
        var publicParamsString = BuildPublicParameterList(requiredParams, hasOptionals, optionsTypeName, publicOptionsParamName);

        // Build parameter list for internal method (all params positional for callback registration)
        var internalParamDefs = new List<string>();
        foreach (var param in userParams)
        {
            var tsType = MapParameterToTypeScript(param);
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
            returnClassName = _wrapperClassNames.GetValueOrDefault(returnTypeId)
                ?? DeriveClassName(returnTypeId);
        }
        var returnHandle = capability.ReturnsBuilder
            ? GetHandleTypeName(returnTypeId)
            : "void";
        var returnsBuilder = capability.ReturnsBuilder;
        var returnImplementationClassName = GetImplementationClassName(returnClassName);

        // Check if this method returns a non-builder, non-void type (e.g., getEndpoint returns EndpointReference)
        var hasNonBuilderReturn = !returnsBuilder && capability.ReturnType != null;
        if (hasNonBuilderReturn)
        {
            // Generate a simple async method that returns the actual type
            var returnType = MapTypeRefToTypeScript(capability.ReturnType);

            Write($"    async {methodName}(");
            Write(publicParamsString);
            WriteLine($"): Promise<{returnType}> {{");

            // Extract optional params from options object
            foreach (var param in hasDirectOptionsParameter ? [] : optionalParams)
            {
                WriteLine($"        {(IsWidenedHandleType(param.Type) ? "let" : "const")} {param.Name} = {publicOptionsParamName}?.{param.Name};");
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
            GenerateArgsObjectWithConditionals(targetParamName, requiredParams, optionalParams);

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
        var promiseImplementationClass = GetImplementationPromiseClassName(returnClassName);
        Write($"    {methodName}(");
        Write(publicParamsString);
        Write($"): {promiseClass} {{");
        WriteLine();

        // Extract optional params from options object and forward to internal method
        foreach (var param in hasDirectOptionsParameter ? [] : optionalParams)
        {
            WriteLine($"        {(IsWidenedHandleType(param.Type) ? "let" : "const")} {param.Name} = {publicOptionsParamName}?.{param.Name};");
        }

        // Forward all params to internal method
        var allParamNames = userParams.Select(p => p.Name);
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

            if (IsWidenedHandleType(param.Type))
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
        if (IsWidenedHandleType(paramType))
        {
            WriteLine($"{indent}{paramName} = isPromiseLike({paramName}) ? await {paramName} : {paramName};");
        }
    }

    /// <summary>
    /// Checks if a type was widened to accept Awaitable&lt;T&gt; in input position.
    /// Must match the widening logic in MapInputTypeToTypeScript exactly.
    /// </summary>
    private bool IsWidenedHandleType(AtsTypeRef? typeRef)
    {
        if (typeRef == null)
        {
            return false;
        }

        // Interface handles are always widened
        if (IsInterfaceHandleType(typeRef))
        {
            return true;
        }

        // Concrete handles are only widened if they have a wrapper class name
        // (excludes special types like ReferenceExpression that bypass widening)
        if (IsHandleType(typeRef) && _wrapperClassNames.ContainsKey(typeRef.TypeId))
        {
            return true;
        }

        if (typeRef.Category == AtsTypeCategory.Union && typeRef.UnionTypes is { Count: > 0 })
        {
            return typeRef.UnionTypes.Any(IsWidenedHandleType);
        }

        return false;
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
        string indent = "        ")
    {
        // Resolve any promise-like handle parameters
        GeneratePromiseResolution(allParams, indent);

        // Build the required args inline
        var requiredArgs = new List<string> { $"{targetParamName}: this._handle" };
        foreach (var param in requiredParams)
        {
            requiredArgs.Add(GetRpcArgumentEntry(param));
        }

        WriteLine($"{indent}const rpcArgs: Record<string, unknown> = {{ {string.Join(", ", requiredArgs)} }};");

        // Conditionally add optional params
        foreach (var param in optionalParams)
        {
            var rpcExpression = GetRpcArgumentExpression(param);
            WriteLine($"{indent}if ({param.Name} !== undefined) rpcArgs.{param.Name} = {rpcExpression};");
        }
    }

    /// <summary>
    /// Generates an args object with conditional inclusion of optional parameters.
    /// </summary>
    private void GenerateArgsObjectWithConditionals(
        string targetParamName,
        List<AtsParameterInfo> requiredParams,
        List<AtsParameterInfo> optionalParams,
        string indent = "        ")
    {
        // Build the required args inline
        var requiredArgs = new List<string> { $"{targetParamName}: this._handle" };
        foreach (var param in requiredParams)
        {
            requiredArgs.Add(GetRpcArgumentEntry(param));
        }

        WriteLine($"{indent}const rpcArgs: Record<string, unknown> = {{ {string.Join(", ", requiredArgs)} }};");

        // Conditionally add optional params
        foreach (var param in optionalParams)
        {
            var rpcExpression = GetRpcArgumentExpression(param);
            WriteLine($"{indent}if ({param.Name} !== undefined) rpcArgs.{param.Name} = {rpcExpression};");
        }
    }

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
        var getterOnlyProperties = GroupPropertiesByName(getters, setters)
            .Where(p => IsGetterOnlyProperty(p.Getter, p.Setter))
            .ToList();

        if (capabilities.Count == 0 && getterOnlyProperties.Count == 0)
        {
            return;
        }

        var promiseClass = $"{builder.BuilderClassName}Promise";
        var promiseImplementationClass = GetImplementationPromiseClassName(builder.BuilderClassName);

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
            var returnType = GetGetterOnlyPropertyReturnType(prop.Getter!.ReturnType);
            WriteLine($"    {prop.PropertyName}(): Promise<{returnType}> {{");
            WriteLine($"        return this._promise.then(obj => obj.{prop.PropertyName}());");
            WriteLine("    }");
            WriteLine();
        }

        // Generate fluent methods that chain via .then()
        // Capabilities are already flattened - no need to collect from parents
        // Filter out property getters and setters - they are not methods
        foreach (var capability in capabilities)
        {
            var methodName = capability.MethodName;
            var targetParamName = capability.TargetParameterName ?? "builder";
            var userParams = capability.Parameters.Where(p => p.Name != targetParamName).ToList();

            // Separate required and optional parameters
            var (requiredParams, optionalParams) = SeparateParameters(userParams);
            var hasOptionals = optionalParams.Count > 0;
            var hasDirectOptionsParameter = TryGetDirectOptionsParameter(optionalParams, out var directOptionsParam);
            var optionsTypeName = hasDirectOptionsParameter ? MapParameterToTypeScript(directOptionsParam!) : ResolveOptionsInterfaceName(capability);

            // Build parameter list using options pattern
            var publicParamDefs = new List<string>();
            foreach (var param in requiredParams)
            {
                var tsType = MapParameterToTypeScript(param);
                publicParamDefs.Add($"{param.Name}: {tsType}");
            }
            if (hasOptionals)
            {
                publicParamDefs.Add($"options?: {optionsTypeName}");
            }
            var paramsString = string.Join(", ", publicParamDefs);

            // Forward args to underlying object's method (which handles options extraction)
            var forwardArgs = new List<string>();
            foreach (var param in requiredParams)
            {
                forwardArgs.Add(param.Name);
            }
            if (hasOptionals)
            {
                forwardArgs.Add("options");
            }
            var argsString = string.Join(", ", forwardArgs);

            // Check if this method returns a non-builder type
            var hasNonBuilderReturn = !capability.ReturnsBuilder && capability.ReturnType != null;

            if (hasNonBuilderReturn)
            {
                // For non-builder returns, call the public method directly
                var returnType = MapTypeRefToTypeScript(capability.ReturnType);
                Write($"    {methodName}(");
                Write(paramsString);
                WriteLine($"): Promise<{returnType}> {{");
                Write($"        return this._promise.then(obj => obj.{methodName}(");
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
                    var returnClass = _wrapperClassNames.GetValueOrDefault(capability.ReturnType.TypeId)
                        ?? DeriveClassName(capability.ReturnType.TypeId);
                    methodPromiseClass = $"{returnClass}Promise";
                    methodPromiseImplementationClass = GetImplementationPromiseClassName(returnClass);
                }

                Write($"    {methodName}(");
                Write(paramsString);
                Write($"): {methodPromiseClass} {{");
                WriteLine();
                // Forward to the public method on the underlying object, wrapping result in promise class
                Write($"        return new {methodPromiseImplementationClass}(this._promise.then(obj => obj.{methodName}(");
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

        // Build parameter list
        var paramDefs = new List<string> { "client: AspireClientRpc" };
        foreach (var param in capability.Parameters)
        {
            var tsType = MapParameterToTypeScript(param);
            var optional = param.IsOptional || param.IsNullable ? "?" : "";
            paramDefs.Add($"{param.Name}{optional}: {tsType}");
        }

        var paramsString = string.Join(", ", paramDefs);
        var (requiredParams, optionalParams) = SeparateParameters(capability.Parameters);

        // Determine return type - check if return type has a Promise wrapper
        var capReturnTypeId = GetReturnTypeId(capability);
        var returnPromiseWrapper = GetPromiseWrapperForReturnType(capability.ReturnType);

        // Generate JSDoc
        WriteCapabilityDocComment(string.Empty, capability);

        // Generate function based on return type
        if (returnPromiseWrapper != null && !string.IsNullOrEmpty(capReturnTypeId))
        {
            // Return type has Promise wrapper - generate fluent function
            var returnWrapperClass = _wrapperClassNames.GetValueOrDefault(capReturnTypeId)
                ?? DeriveClassName(capReturnTypeId);
            var returnWrapperImplementationClass = GetImplementationClassName(returnWrapperClass);
            var returnPromiseImplementationClass = GetImplementationPromiseClassName(returnWrapperClass);
            var handleType = GetHandleTypeName(capReturnTypeId);

            Write($"export function {methodName}(");
            Write(paramsString);
            WriteLine($"): {returnPromiseWrapper} {{");
            // Use async IIFE to resolve promise-like handle params before RPC
            WriteLine($"    const promise = (async () => {{");
            // Resolve promise-like handle params
            foreach (var param in capability.Parameters)
            {
                if (!param.IsCallback && IsWidenedHandleType(param.Type))
                {
                    WriteLine($"        {param.Name} = isPromiseLike({param.Name}) ? await {param.Name} : {param.Name};");
                }
            }
            var requiredArgs = requiredParams
                .Select(param => GetRpcArgumentEntry(param, useRegisteredCallback: false))
                .ToList();
            WriteLine($"        const rpcArgs: Record<string, unknown> = {{ {string.Join(", ", requiredArgs)} }};");
            foreach (var param in optionalParams)
            {
                WriteLine($"        if ({param.Name} !== undefined) rpcArgs.{param.Name} = {GetRpcArgumentExpression(param, useRegisteredCallback: false)};");
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
                ? MapTypeRefToTypeScript(capability.ReturnType)
                : "void";

            Write($"export async function {methodName}(");
            Write(paramsString);
            WriteLine($"): Promise<{returnType}> {{");
            // Resolve promise-like handle params
            foreach (var param in capability.Parameters)
            {
                if (!param.IsCallback && IsWidenedHandleType(param.Type))
                {
                    WriteLine($"    {param.Name} = isPromiseLike({param.Name}) ? await {param.Name} : {param.Name};");
                }
            }
            var requiredArgs = requiredParams
                .Select(param => GetRpcArgumentEntry(param, useRegisteredCallback: false))
                .ToList();
            WriteLine($"    const rpcArgs: Record<string, unknown> = {{ {string.Join(", ", requiredArgs)} }};");
            foreach (var param in optionalParams)
            {
                WriteLine($"    if ({param.Name} !== undefined) rpcArgs.{param.Name} = {GetRpcArgumentExpression(param, useRegisteredCallback: false)};");
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

    private string GenerateCallbackTypeSignature(IReadOnlyList<AtsCallbackParameterInfo>? callbackParameters, AtsTypeRef? callbackReturnType)
    {
        // Build parameter list
        var paramList = new List<string>();
        if (callbackParameters is not null)
        {
            foreach (var param in callbackParameters)
            {
                var tsType = MapTypeRefToTypeScript(param.Type);
                paramList.Add($"{param.Name}: {tsType}");
            }
        }

        var paramsString = paramList.Count > 0 ? string.Join(", ", paramList) : "";

        // Determine return type
        var returnType = callbackReturnType == null || callbackReturnType.TypeId == AtsConstants.Void
            ? "void"
            : MapTypeRefToTypeScript(callbackReturnType);

        // Callbacks are always async in TypeScript
        return $"({paramsString}) => Promise<{returnType}>";
    }

    private void GenerateCallbackRegistration(AtsParameterInfo callbackParam, string indent = "        ")
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
        GenerateCallbackBody(callbackParam, callbackParameters, indent);

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
    private void GenerateCallbackBody(AtsParameterInfo callbackParam, IReadOnlyList<AtsCallbackParameterInfo>? callbackParameters, string indent)
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
            GenerateCallbackParameterConversion(cbParam, $"{cbParam.Name}Data");

            WriteLine($"{bodyIndent}{returnPrefix}await {callbackName}({cbParam.Name});");
        }
        else
        {
            var callArgs = new List<string>();
            for (var i = 0; i < callbackParameters.Count; i++)
            {
                var cbParam = callbackParameters[i];
                var callbackArgName = $"{cbParam.Name}Data";

                GenerateCallbackParameterConversion(cbParam, callbackArgName);
                callArgs.Add(cbParam.Name);
            }

            WriteLine($"{bodyIndent}{returnPrefix}await {callbackName}({string.Join(", ", callArgs)});");
        }
    }

    private void GenerateCallbackParameterConversion(AtsCallbackParameterInfo callbackParameter, string callbackArgName)
    {
        var tsType = MapTypeRefToTypeScript(callbackParameter.Type);
        var cbTypeId = callbackParameter.Type.TypeId;

        if (cbTypeId == AtsConstants.CancellationToken)
        {
            WriteLine($"            const {callbackParameter.Name} = CancellationToken.fromValue({callbackArgName});");
        }
        else if (IsDictionaryType(callbackParameter.Type) && !callbackParameter.Type.IsReadOnly)
        {
            var keyType = MapTypeRefToTypeScript(callbackParameter.Type.KeyType);
            var valueType = MapTypeRefToTypeScript(callbackParameter.Type.ValueType);
            var handleType = GetHandleTypeName(cbTypeId);

            WriteLine($"            const {callbackParameter.Name}Handle = wrapIfHandle({callbackArgName}) as {handleType};");
            WriteLine($"            const {callbackParameter.Name} = new AspireDict<{keyType}, {valueType}>({callbackParameter.Name}Handle, this._client, '{cbTypeId}');");
        }
        else if (_wrapperClassNames.TryGetValue(cbTypeId, out var wrapperClassName))
        {
            var handleType = GetHandleTypeName(cbTypeId);
            WriteLine($"            const {callbackParameter.Name}Handle = wrapIfHandle({callbackArgName}) as {handleType};");
            WriteLine($"            const {callbackParameter.Name} = new {GetImplementationClassName(wrapperClassName)}({callbackParameter.Name}Handle, this._client);");
        }
        else
        {
            WriteLine($"            const {callbackParameter.Name} = wrapIfHandle({callbackArgName}) as {tsType};");
        }
    }

    private void GenerateConnectionHelper()
    {
        var builderHandle = GetHandleTypeName(AtsConstants.BuilderTypeId);

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
            export { Handle, AppHostUsageError, CancellationToken, CapabilityError, registerCallback } from './transport.js';
            export { refExpr, ReferenceExpression } from './base.js';
            export type { HandleReference, Awaitable } from './base.js';
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
            var className = _wrapperClassNames.GetValueOrDefault(typeClass.TypeId) ?? DeriveClassName(typeClass.TypeId);
            var handleType = GetHandleTypeName(typeClass.TypeId);
            WriteLine($"registerHandleWrapper('{typeClass.TypeId}', (handle, client) => new {GetImplementationClassName(className)}(handle as {handleType}, client));");
        }

        // Register resource builder classes
        foreach (var builder in resourceBuilders)
        {
            var className = _wrapperClassNames.GetValueOrDefault(builder.TypeId) ?? DeriveClassName(builder.TypeId);
            var handleType = GetHandleTypeName(builder.TypeId);
            WriteLine($"registerHandleWrapper('{builder.TypeId}', (handle, client) => new {GetImplementationClassName(className)}(handle as {handleType}, client));");
        }

        WriteLine();
    }

    /// <summary>
    /// Generates a type class (context type or wrapper type).
    /// Uses property-like objects for mutable properties and methods for getter-only properties.
    /// For types with methods, also generates a Promise wrapper class for fluent chaining.
    /// </summary>
    private void GenerateTypeClass(BuilderModel model)
    {
        var handleType = GetHandleTypeName(model.TypeId);
        var className = DeriveClassName(model.TypeId);
        var implementationClassName = GetImplementationClassName(className);

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

        WriteLine($"/**");
        WriteLine($" * Type class for {className}.");
        WriteLine($" */");
        WriteLine($"class {implementationClassName} implements {className} {{");
        WriteLine($"    constructor(private _handle: {handleType}, private _client: AspireClientRpc) {{}}");
        WriteLine();
        WriteLine($"    /** Serialize for JSON-RPC transport */");
        WriteLine($"    toJSON(): MarshalledHandle {{ return this._handle.toJSON(); }}");
        WriteLine();

        // Group getters and setters by property name to create property members
        var properties = GroupPropertiesByName(getters, setters);

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

        // Generate thenable wrapper class if this type has methods
        if (hasMethods)
        {
            GenerateTypeClassThenableWrapper(model, allMethods);
        }
    }

    /// <summary>
    /// Groups getters and setters by property name.
    /// </summary>
    private static List<(string PropertyName, AtsCapabilityInfo? Getter, AtsCapabilityInfo? Setter)> GroupPropertiesByName(
        List<AtsCapabilityInfo> getters, List<AtsCapabilityInfo> setters)
    {
        var result = new List<(string PropertyName, AtsCapabilityInfo? Getter, AtsCapabilityInfo? Setter)>();
        var processedNames = new HashSet<string>();

        // Process getters
        foreach (var getter in getters)
        {
            var propName = ExtractPropertyName(getter.MethodName);
            if (processedNames.Contains(propName))
            {
                continue;
            }
            processedNames.Add(propName);

            // Find matching setter (setPropertyName for propertyName)
            var setterName = "set" + char.ToUpperInvariant(propName[0]) + propName[1..];
            var setter = setters.FirstOrDefault(s => ExtractPropertyName(s.MethodName).Equals(setterName, StringComparison.OrdinalIgnoreCase));

            result.Add((propName, getter, setter));
        }

        // Process any setters without matching getters
        foreach (var setter in setters)
        {
            var setterMethodName = ExtractPropertyName(setter.MethodName);
            // setPropertyName -> propertyName
            if (setterMethodName.StartsWith("set", StringComparison.OrdinalIgnoreCase) && setterMethodName.Length > 3)
            {
                var propName = char.ToLowerInvariant(setterMethodName[3]) + setterMethodName[4..];
                if (!processedNames.Contains(propName))
                {
                    processedNames.Add(propName);
                    result.Add((propName, null, setter));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Extracts the property name from a method name like "ClassName.propertyName" or "setPropertyName".
    /// </summary>
    private static string ExtractPropertyName(string methodName)
    {
        // Handle "ClassName.propertyName" format
        if (methodName.Contains('.'))
        {
            return methodName[(methodName.LastIndexOf('.') + 1)..];
        }
        return methodName;
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
        if (IsGetterOnlyProperty(getter, setter))
        {
            GenerateGetterOnlyPropertyMethod(propertyName, getter!);
            return;
        }

        // Determine the return type from getter
        string returnType = "unknown";

        if (getter != null)
        {
            returnType = MapTypeRefToTypeScript(getter.ReturnType);

            // Mutable dictionary/list properties stay as property accessors so callers can use
            // wrapper operations (for example, property.get()/set() or list/dict helpers)
            // without switching to the getter-only method shape.
            if (IsDictionaryType(getter.ReturnType))
            {
                GenerateMutableDictionaryProperty(propertyName, getter);
                return;
            }

            if (IsListType(getter.ReturnType))
            {
                GenerateMutableListProperty(propertyName, getter);
                return;
            }

            // Check if return type is a wrapper class - use property-like object returning wrapper
            if (getter.ReturnType?.TypeId != null && _wrapperClassNames.TryGetValue(getter.ReturnType.TypeId, out var wrapperClassName))
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
                var valueType = MapInputTypeToTypeScript(valueParam.Type);
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
        if (IsDictionaryType(getter.ReturnType))
        {
            GenerateDictionaryProperty(propertyName, getter);
            return;
        }

        if (IsListType(getter.ReturnType))
        {
            GenerateListProperty(propertyName, getter);
            return;
        }

        if (getter.ReturnType?.TypeId != null && _wrapperClassNames.TryGetValue(getter.ReturnType.TypeId, out var wrapperClassName))
        {
            GenerateWrapperGetterOnlyPropertyMethod(propertyName, getter, wrapperClassName);
            return;
        }

        var returnType = GetGetterOnlyPropertyReturnType(getter.ReturnType);

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
        var handleType = GetHandleTypeName(getter.ReturnType!.TypeId);
        var wrapperImplementationClassName = GetImplementationClassName(wrapperClassName);

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
        var handleType = GetHandleTypeName(getter.ReturnType!.TypeId);
        var wrapperImplementationClassName = GetImplementationClassName(wrapperClassName);

        WriteLine($"    {propertyName} = {{");
        WriteLine($"        get: async (): Promise<{wrapperClassName}> => {{");
        WriteLine($"            const handle = await this._client.invokeCapability<{handleType}>(");
        WriteLine($"                '{getter.CapabilityId}',");
        WriteLine($"                {{ context: this._handle }}");
        WriteLine("            );");
        WriteLine($"            return new {wrapperImplementationClassName}(handle, this._client);");
        WriteLine("        },");

        if (setter != null)
        {
            var valueParam = setter.Parameters.FirstOrDefault(p => p.Name == "value");
            if (valueParam != null)
            {
                var valueType = MapInputTypeToTypeScript(valueParam.Type);
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
    /// Checks if a type reference is a dictionary type.
    /// </summary>
    private static bool IsDictionaryType(AtsTypeRef? typeRef)
    {
        return typeRef?.Category == AtsTypeCategory.Dict;
    }

    /// <summary>
    /// Checks if a type reference is a list type.
    /// </summary>
    private static bool IsListType(AtsTypeRef? typeRef)
    {
        return typeRef?.Category == AtsTypeCategory.List;
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
            keyType = MapTypeRefToTypeScript(getter.ReturnType.KeyType);
        }
        if (getter.ReturnType?.ValueType != null)
        {
            // Union types will be mapped correctly via MapTypeRefToTypeScript
            valueType = MapTypeRefToTypeScript(getter.ReturnType.ValueType);
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
            keyType = MapTypeRefToTypeScript(getter.ReturnType.KeyType);
        }

        if (getter.ReturnType?.ValueType != null)
        {
            valueType = MapTypeRefToTypeScript(getter.ReturnType.ValueType);
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
            elementType = MapTypeRefToTypeScript(getter.ReturnType.ElementType);
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
            elementType = MapTypeRefToTypeScript(getter.ReturnType.ElementType);
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
        var (requiredParams, optionalParams) = SeparateParameters(userParams);
        var hasOptionals = optionalParams.Count > 0;
        var hasDirectOptionsParameter = TryGetDirectOptionsParameter(optionalParams, out var directOptionsParam);
        var optionsInterfaceName = hasDirectOptionsParameter ? MapParameterToTypeScript(directOptionsParam!) : ResolveOptionsInterfaceName(method);
        var publicOptionsParamName = GetPublicOptionsParameterName(userParams, hasOptionals, hasDirectOptionsParameter);

        // Build parameter list using options pattern
        var paramsString = BuildPublicParameterList(requiredParams, hasOptionals, optionsInterfaceName, publicOptionsParamName);

        // Determine return type
        var returnType = GetReturnTypeId(method) != null
            ? MapTypeRefToTypeScript(method.ReturnType)
            : "void";

        // Generate async method
        Write($"    async {methodName}(");
        Write(paramsString);
        WriteLine($"): Promise<{returnType}> {{");

        // Extract optional params from options object
        foreach (var param in hasDirectOptionsParameter ? [] : optionalParams)
        {
            WriteLine($"        {(IsWidenedHandleType(param.Type) ? "let" : "const")} {param.Name} = {publicOptionsParamName}?.{param.Name};");
        }

        // Resolve promise-like params and build args
        GenerateResolveAndBuildArgs(targetParamName, userParams, requiredParams, optionalParams);

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
        var methodName = GetTypeScriptMethodName(capability.MethodName);

        // First arg is the handle (implicit via this._handle) - use metadata instead of string parsing
        var firstParamName = capability.TargetParameterName ?? "builder";

        // Filter out the implicit handle parameter
        var userParams = capability.Parameters.Where(p => p.Name != firstParamName).ToList();

        // Separate required and optional parameters
        var (requiredParams, optionalParams) = SeparateParameters(userParams);
        var hasOptionals = optionalParams.Count > 0;
        var hasDirectOptionsParameter = TryGetDirectOptionsParameter(optionalParams, out var directOptionsParam);
        var optionsInterfaceName = hasDirectOptionsParameter ? MapParameterToTypeScript(directOptionsParam!) : ResolveOptionsInterfaceName(capability);
        var publicOptionsParamName = GetPublicOptionsParameterName(userParams, hasOptionals, hasDirectOptionsParameter);

        // Build parameter list using options pattern
        var paramsString = BuildPublicParameterList(requiredParams, hasOptionals, optionsInterfaceName, publicOptionsParamName);

        // Determine return type
        var returnType = MapTypeRefToTypeScript(capability.ReturnType);

        // Generate async method
        Write($"    async {methodName}(");
        Write(paramsString);
        WriteLine($"): Promise<{returnType}> {{");

        // Extract optional params from options object
        foreach (var param in hasDirectOptionsParameter ? [] : optionalParams)
        {
            WriteLine($"        {(IsWidenedHandleType(param.Type) ? "let" : "const")} {param.Name} = {publicOptionsParamName}?.{param.Name};");
        }

        // Resolve promise-like params and build args
        GenerateResolveAndBuildArgs(firstParamName, userParams, requiredParams, optionalParams);

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
        var className = DeriveClassName(model.TypeId);
        var promiseClass = $"{className}Promise";
        var promiseImplementationClass = GetImplementationPromiseClassName(className);

        // Use OwningTypeName if available to extract method name, otherwise parse from MethodName
        var methodName = !string.IsNullOrEmpty(capability.OwningTypeName) && capability.MethodName.Contains('.')
            ? capability.MethodName[(capability.MethodName.LastIndexOf('.') + 1)..]
            : GetTypeScriptMethodName(capability.MethodName);

        var internalMethodName = $"_{methodName}Internal";

        // Filter out target parameter
        var targetParamName = capability.TargetParameterName ?? "context";
        var userParams = capability.Parameters.Where(p => p.Name != targetParamName).ToList();

        // Separate required and optional parameters
        var (requiredParams, optionalParams) = SeparateParameters(userParams);
        var hasOptionals = optionalParams.Count > 0;
        var hasDirectOptionsParameter = TryGetDirectOptionsParameter(optionalParams, out var directOptionsParam);
        var optionsInterfaceName = hasDirectOptionsParameter ? MapParameterToTypeScript(directOptionsParam!) : ResolveOptionsInterfaceName(capability);
        var publicOptionsParamName = GetPublicOptionsParameterName(userParams, hasOptionals, hasDirectOptionsParameter);

        // Build parameter list for public method
        var publicParamsString = BuildPublicParameterList(requiredParams, hasOptionals, optionsInterfaceName, publicOptionsParamName);

        // Build parameter list for internal method (all params positional)
        var internalParamDefs = new List<string>();
        foreach (var param in userParams)
        {
            var tsType = MapParameterToTypeScript(param);
            var optional = param.IsOptional || param.IsNullable ? "?" : "";
            internalParamDefs.Add($"{param.Name}{optional}: {tsType}");
        }
        var internalParamsString = string.Join(", ", internalParamDefs);

        // Check if return type has a Promise wrapper
        var returnPromiseWrapper = GetPromiseWrapperForReturnType(capability.ReturnType);
        var returnType = MapTypeRefToTypeScript(capability.ReturnType);
        var isVoid = capability.ReturnType == null || capability.ReturnType.TypeId == AtsConstants.Void;

        // If return type has a Promise wrapper, generate internal + fluent pattern
        if (returnPromiseWrapper != null)
        {
            var returnWrapperClass = _wrapperClassNames.GetValueOrDefault(capability.ReturnType!.TypeId)
                ?? DeriveClassName(capability.ReturnType.TypeId);
            var returnWrapperImplementationClass = GetImplementationClassName(returnWrapperClass);
            var returnPromiseImplementationClass = GetImplementationPromiseClassName(returnWrapperClass);
            var returnHandleType = GetHandleTypeName(capability.ReturnType.TypeId);

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
            Write($"    {methodName}(");
            Write(publicParamsString);
            WriteLine($"): {returnPromiseWrapper} {{");

            // Extract optional params and forward
            foreach (var param in hasDirectOptionsParameter ? [] : optionalParams)
            {
                WriteLine($"        {(IsWidenedHandleType(param.Type) ? "let" : "const")} {param.Name} = {publicOptionsParamName}?.{param.Name};");
            }

            var internalCall = $"this.{internalMethodName}({string.Join(", ", userParams.Select(p => p.Name))})";

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
            Write($"    {methodName}(");
            Write(publicParamsString);
            WriteLine($"): {promiseClass} {{");

            // Extract optional params and forward
            foreach (var param in optionalParams)
            {
                WriteLine($"        {(IsWidenedHandleType(param.Type) ? "let" : "const")} {param.Name} = {publicOptionsParamName}?.{param.Name};");
            }

            Write($"        return new {promiseImplementationClass}(this.{internalMethodName}(");
            Write(string.Join(", ", userParams.Select(p => p.Name)));
            WriteLine("), this._client);");
            WriteLine("    }");
        }
        else
        {
            // Non-void, non-wrapper return - plain async method
            Write($"    async {methodName}(");
            Write(publicParamsString);
            WriteLine($"): Promise<{returnType}> {{");

            // Extract optional params from options object
            foreach (var param in optionalParams)
            {
                WriteLine($"        {(IsWidenedHandleType(param.Type) ? "let" : "const")} {param.Name} = {publicOptionsParamName}?.{param.Name};");
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
            GenerateArgsObjectWithConditionals(targetParamName, requiredParams, optionalParams);

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
        var className = DeriveClassName(model.TypeId);
        var promiseClass = $"{className}Promise";
        var promiseImplementationClass = GetImplementationPromiseClassName(className);

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
        var getterOnlyProperties = GroupPropertiesByName(getters, setters)
            .Where(p => IsGetterOnlyProperty(p.Getter, p.Setter))
            .ToList();

        foreach (var prop in getterOnlyProperties)
        {
            var returnType = GetGetterOnlyPropertyReturnType(prop.Getter!.ReturnType);
            WriteLine($"    {prop.PropertyName}(): Promise<{returnType}> {{");
            WriteLine($"        return this._promise.then(obj => obj.{prop.PropertyName}());");
            WriteLine("    }");
            WriteLine();
        }

        // Generate fluent methods that chain via .then()
        foreach (var capability in methods)
        {
            var methodName = !string.IsNullOrEmpty(capability.OwningTypeName) && capability.MethodName.Contains('.')
                ? capability.MethodName[(capability.MethodName.LastIndexOf('.') + 1)..]
                : GetTypeScriptMethodName(capability.MethodName);

            var targetParamName = capability.TargetParameterName ?? "context";
            var userParams = capability.Parameters.Where(p => p.Name != targetParamName).ToList();

            // Separate required and optional parameters
            var (requiredParams, optionalParams) = SeparateParameters(userParams);
            var hasOptionals = optionalParams.Count > 0;
            var hasDirectOptionsParameter = TryGetDirectOptionsParameter(optionalParams, out var directOptionsParam);
            var optionsInterfaceName = hasDirectOptionsParameter ? MapParameterToTypeScript(directOptionsParam!) : ResolveOptionsInterfaceName(capability);

            // Build parameter list using options pattern
            var publicParamDefs = new List<string>();
            foreach (var param in requiredParams)
            {
                var tsType = MapParameterToTypeScript(param);
                publicParamDefs.Add($"{param.Name}: {tsType}");
            }
            if (hasOptionals)
            {
                publicParamDefs.Add($"options?: {optionsInterfaceName}");
            }
            var paramsString = string.Join(", ", publicParamDefs);

            // Forward args to underlying object's public method
            var forwardArgs = new List<string>();
            foreach (var param in requiredParams)
            {
                forwardArgs.Add(param.Name);
            }
            if (hasOptionals)
            {
                forwardArgs.Add("options");
            }
            var argsString = string.Join(", ", forwardArgs);

            // Check if return type has a Promise wrapper
            var returnPromiseWrapper = GetPromiseWrapperForReturnType(capability.ReturnType);
            var returnType = MapTypeRefToTypeScript(capability.ReturnType);
            var isVoid = capability.ReturnType == null || capability.ReturnType.TypeId == AtsConstants.Void;

            if (returnPromiseWrapper != null)
            {
                var returnPromiseImplementationClass = GetImplementationPromiseClassName(
                    _wrapperClassNames.GetValueOrDefault(capability.ReturnType!.TypeId)
                        ?? DeriveClassName(capability.ReturnType.TypeId));
                // Return type has Promise wrapper - forward to public method, wrap result
                Write($"    {methodName}(");
                Write(paramsString);
                WriteLine($"): {returnPromiseWrapper} {{");
                Write($"        return new {returnPromiseImplementationClass}(this._promise.then(obj => obj.{methodName}(");
                Write(argsString);
                WriteLine($")), this._client);");
                WriteLine("    }");
            }
            else if (isVoid)
            {
                // Void return - forward to public method, wrap result in this class's promise
                Write($"    {methodName}(");
                Write(paramsString);
                WriteLine($"): {promiseClass} {{");
                Write($"        return new {promiseImplementationClass}(this._promise.then(obj => obj.{methodName}(");
                Write(argsString);
                WriteLine($")), this._client);");
                WriteLine("    }");
            }
            else
            {
                // Non-void, non-wrapper return - plain Promise
                Write($"    {methodName}(");
                Write(paramsString);
                WriteLine($"): Promise<{returnType}> {{");
                Write($"        return this._promise.then(obj => obj.{methodName}(");
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

        // Also track interface types and their capabilities (for interface wrapper classes)
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

            // ReferenceExpression is implemented manually in base.ts, including its handle wrapper
            // registration, so it must not also generate a duplicate wrapper class in aspire.ts.
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
            var builderClassName = DeriveClassName(typeId);

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
                BuilderClassName = builderClassName,
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

            var builderClassName = DeriveClassName(interfaceTypeId);

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
                BuilderClassName = builderClassName,
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

            var builderClassName = DeriveClassName(typeId);
            var builder = new BuilderModel
            {
                TypeId = typeId,
                BuilderClassName = builderClassName,
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
            .ThenBy(b => b.BuilderClassName)
            .GroupBy(b => b.BuilderClassName)
            .Select(g => g.First())
            .ToList();
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

    /// <summary>
    /// Gets entry point capabilities (those without TargetTypeId).
    /// </summary>
    private static List<AtsCapabilityInfo> GetEntryPointCapabilities(IReadOnlyList<AtsCapabilityInfo> capabilities)
    {
        return capabilities.Where(c => string.IsNullOrEmpty(c.TargetTypeId)).ToList();
    }

    /// <summary>
    /// Derives the class name from an ATS type ID.
    /// For interfaces like IResource, strips the leading 'I'.
    /// </summary>
    private static string DeriveClassName(string typeId)
    {
        var typeName = ExtractSimpleTypeName(typeId);

        // Strip leading 'I' from interface types
        if (typeName.StartsWith('I') && typeName.Length > 1 && char.IsUpper(typeName[1]))
        {
            return typeName[1..];
        }

        return typeName;
    }

    /// <summary>
    /// Gets the handle type alias name for a type ID.
    /// </summary>
    private static string GetHandleTypeName(string typeId)
    {
        var typeName = ExtractSimpleTypeName(typeId);

        // Sanitize generic types like "Dict<String,Object>" -> "DictStringObject"
        // and array types like "string[]" -> "stringArray"
        typeName = typeName
            .Replace("[]", "Array", StringComparison.Ordinal)
            .Replace("<", "", StringComparison.Ordinal)
            .Replace(">", "", StringComparison.Ordinal)
            .Replace(",", "", StringComparison.Ordinal);

        return $"{typeName}Handle";
    }

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

    /// <summary>
    /// Determines if a type has chainable methods and should have a Promise wrapper.
    /// Types with instance methods or wrapper methods get Promise wrappers.
    /// </summary>
    private static bool HasChainableMethods(BuilderModel model)
    {
        // Check for instance methods (from ExposeMethods=true) or wrapper methods
        return model.Capabilities.Any(c =>
            c.CapabilityKind == AtsCapabilityKind.InstanceMethod ||
            c.CapabilityKind == AtsCapabilityKind.Method);
    }

    /// <summary>
    /// Gets the Promise wrapper class name for a return type, if one exists.
    /// Returns null if the return type doesn't have a Promise wrapper.
    /// </summary>
    private string? GetPromiseWrapperForReturnType(AtsTypeRef? returnType)
    {
        if (returnType == null)
        {
            return null;
        }

        // Check if the return type has a Promise wrapper
        if (_typesWithPromiseWrappers.Contains(returnType.TypeId))
        {
            var className = _wrapperClassNames.GetValueOrDefault(returnType.TypeId)
                ?? DeriveClassName(returnType.TypeId);
            return $"{className}Promise";
        }

        return null;
    }
}
