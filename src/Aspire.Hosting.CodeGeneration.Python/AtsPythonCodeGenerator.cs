// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Reflection;
using Aspire.TypeSystem;

namespace Aspire.Hosting.CodeGeneration.Python;

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

/// <summary>
/// Generates a Python SDK using the ATS (Aspire Type System) capability-based API.
/// Produces typed wrapper classes with fluent methods that use invoke_capability().
/// </summary>
/// <remarks>
/// <para>
/// <b>ATS to Python Type Mapping</b>
/// </para>
/// <para>
/// The generator maps ATS types to Python types according to the following rules:
/// </para>
/// <para>
/// <b>Primitive Types:</b>
/// <list type="table">
///   <listheader>
///     <term>ATS Type</term>
///     <description>Python Type</description>
///   </listheader>
///   <item><term><c>string</c></term><description><c>str</c></description></item>
///   <item><term><c>number</c></term><description><c>float</c></description></item>
///   <item><term><c>boolean</c></term><description><c>bool</c></description></item>
///   <item><term><c>any</c></term><description><c>Any</c></description></item>
/// </list>
/// </para>
/// <para>
/// <b>Handle Types:</b>
/// Type IDs use the format <c>{AssemblyName}/{TypeName}</c>.
/// Handle types are wrapped in Python classes that provide typed access to capabilities.
/// </para>
/// <para>
/// <b>Method Naming:</b>
/// <list type="bullet">
///   <item><description>Derived from capability ID using snake_case conversion</description></item>
///   <item><description><c>addRedis</c> → <c>add_redis</c></description></item>
///   <item><description><c>withEnvironment</c> → <c>with_environment</c></description></item>
/// </list>
/// </para>
/// </remarks>
internal sealed class AtsPythonCodeGenerator : ICodeGenerator
{
    private static readonly HashSet<string> s_pythonKeywords = new(StringComparer.Ordinal)
    {
        "False",
        "None",
        "True",
        "and",
        "as",
        "assert",
        "async",
        "await",
        "break",
        "class",
        "continue",
        "def",
        "del",
        "elif",
        "else",
        "except",
        "finally",
        "for",
        "from",
        "global",
        "if",
        "import",
        "in",
        "is",
        "lambda",
        "match",
        "nonlocal",
        "not",
        "or",
        "pass",
        "raise",
        "return",
        "try",
        "while",
        "with",
        "yield"
    };

    private sealed record OptionVariation(
        string OptionType,
        List<AtsParameterInfo> RequiredParameters,
        List<AtsParameterInfo> OptionalParameters,
        string? Experimental);

    /// <summary>
    /// Tracks the alternate capability ID for merged capabilities.
    /// Key: the merged capability ID (the "short" one without the extra param).
    /// Value: (alternateCapabilityId, discriminatingParamName) - the capability to invoke when the extra param is provided.
    /// </summary>
    private sealed record MergedCapabilityDispatch(string AlternateCapabilityId, string DiscriminatingParamName);
    private readonly Dictionary<string, MergedCapabilityDispatch> _mergedCapabilityDispatches = new(StringComparer.Ordinal);

    private PythonModuleBuilder _moduleBuilder = null!;

    // Mapping of typeId -> wrapper class name for all generated wrapper types
    // Used to resolve parameter types to wrapper classes instead of handle types
    private readonly Dictionary<string, string> _wrapperClassNames = new(StringComparer.Ordinal);

    // Mapping of enum type IDs to Python enum names
    private readonly Dictionary<string, string> _enumTypeNames = new(StringComparer.Ordinal);

    // List of type IDs to ignore when generating handle type aliases
    private readonly List<string> _ignoreTypes = new()
    {
        AtsConstants.ReferenceExpressionTypeId,
        "System.Private.CoreLib/System.IAsyncDisposable",
        "System.Private.CoreLib/System.IDisposable",
        "Microsoft.Extensions.Hosting.Abstractions/Microsoft.Extensions.Hosting.IHost"
    };

    /// <summary>
    /// Checks if an AtsTypeRef represents a handle type.
    /// </summary>
    private static bool IsHandleType(AtsTypeRef? typeRef) =>
        typeRef != null && typeRef.Category == AtsTypeCategory.Handle;

    /// <summary>
    /// Checks if the capability's target type is already covered by the builder's base class hierarchy.
    /// Returns true if the target type is a base class of the builder's type, or an interface
    /// that's implemented by any class in the builder's base class hierarchy.
    /// </summary>
    private static bool IsTargetTypeCoveredByBaseHierarchy(AtsTypeRef? capabilityTargetType, AtsTypeRef? builderTargetType)
    {
        if (capabilityTargetType == null || builderTargetType == null)
        {
            return false;
        }

        // If the capability targets the builder's own type, it's not covered by base
        if (capabilityTargetType.TypeId == builderTargetType.TypeId)
        {
            return false;
        }

        // Check if the capability's target type is in the base class hierarchy
        var currentBase = builderTargetType.BaseType;
        while (currentBase != null)
        {
            // Check if capability targets this base class
            if (capabilityTargetType.TypeId == currentBase.TypeId)
            {
                return true;
            }

            // Check if capability targets an interface implemented by this base class
            if (currentBase.ImplementedInterfaces != null)
            {
                if (currentBase.ImplementedInterfaces.Any(i => i.TypeId == capabilityTargetType.TypeId))
                {
                    return true;
                }
            }

            currentBase = currentBase.BaseType;
        }

        return false;
    }

    /// <summary>
    /// Maps an AtsTypeRef to a Python type using category-based dispatch.
    /// This is the preferred method - uses type metadata rather than string parsing.
    /// </summary>
    private string MapTypeRefToPython(AtsTypeRef? typeRef)
    {
        if (typeRef == null)
        {
            return "typing.Any";
        }

        // Check for wrapper class first (handles custom types like ReferenceExpression)
        if (_wrapperClassNames.TryGetValue(typeRef.TypeId, out var wrapperClassName))
        {
            return wrapperClassName;
        }

        return typeRef.Category switch
        {
            AtsTypeCategory.Primitive => MapPrimitiveType(typeRef.TypeId),
            AtsTypeCategory.Enum => MapEnumType(typeRef.TypeId),
            AtsTypeCategory.Handle => GetWrapperOrHandleName(typeRef.TypeId),
            AtsTypeCategory.Dto => GetDtoClassName(typeRef.TypeId),
            AtsTypeCategory.Callback => "typing.Callable",  // Callbacks handled separately with full signature
            AtsTypeCategory.Array => $"typing.Iterable[{MapTypeRefToPython(typeRef.ElementType)}]",
            AtsTypeCategory.List => $"AspireList[{MapTypeRefToPython(typeRef.ElementType)}]",
            AtsTypeCategory.Dict => typeRef.IsReadOnly
                ? $"typing.Mapping[{MapTypeRefToPython(typeRef.KeyType)}, {MapTypeRefToPython(typeRef.ValueType)}]"
                : $"AspireDict[{MapTypeRefToPython(typeRef.KeyType)}, {MapTypeRefToPython(typeRef.ValueType)}]",
            AtsTypeCategory.Union => MapUnionTypeToPython(typeRef),
            AtsTypeCategory.Unknown => "typing.Any",  // Unknown types use 'Any' since they're not in the ATS universe
            _ => "typing.Any"  // Fallback for any unhandled categories
        };
    }

    /// <summary>
    /// Maps primitive type IDs to Python types.
    /// </summary>
    private static string MapPrimitiveType(string typeId) => typeId switch
    {
        AtsConstants.String or AtsConstants.Char => "str",
        AtsConstants.Number => "int",
        AtsConstants.Boolean => "bool",
        AtsConstants.Void => "None",
        AtsConstants.Any => "typing.Any",
        AtsConstants.DateTime => "datetime.datetime",
        AtsConstants.DateTimeOffset => "datetime.datetime",
        AtsConstants.DateOnly => "datetime.date",
        AtsConstants.TimeOnly => "datetime.time",
        AtsConstants.TimeSpan => "float",
        AtsConstants.Guid or AtsConstants.Uri => "str",
        AtsConstants.CancellationToken => "CancellationToken",
        _ => typeId
    };

    private static string GetParamHandler(AtsParameterInfo param, string paramName)
    {
        if (param.IsCallback)
        {
            return $"self._client.register_callback({paramName})";
        }
        if (param.Type?.TypeId == AtsConstants.CancellationToken)
        {
            return $"self._client.register_cancellation_token({paramName})";
        }
        return paramName;
    }

    private static string GetConstructorParamHandler(AtsParameterInfo param, string paramName)
    {
        if (param.IsCallback)
        {
            return $"client.register_callback({paramName})";
        }
        if (param.Type?.TypeId == AtsConstants.CancellationToken)
        {
            return $"client.register_cancellation_token({paramName})";
        }
        return paramName;
    }

    /// <summary>
    /// Merges capabilities that share the same <see cref="AtsCapabilityInfo.SourceLocation"/> and differ
    /// by exactly one required parameter. The merged capability uses the shortest method name and makes
    /// the differing parameter optional.
    /// </summary>
    private List<AtsCapabilityInfo> MergeCapabilitiesBySourceLocation(List<AtsCapabilityInfo> capabilities)
    {
        var result = new List<AtsCapabilityInfo>();
        var groups = capabilities.GroupBy(c => c.SourceLocation ?? string.Empty);

        foreach (var group in groups)
        {
            if (string.IsNullOrEmpty(group.Key) || group.Count() <= 1)
            {
                result.AddRange(group);
                continue;
            }

            var items = group.ToList();

            // Find parameter names common to all capabilities in this group
            var commonParamNames = new HashSet<string>(
                items[0].Parameters.Select(p => p.Name), StringComparer.Ordinal);
            foreach (var item in items.Skip(1))
            {
                commonParamNames.IntersectWith(item.Parameters.Select(p => p.Name));
            }

            // Find the union of all parameter names
            var allParamNames = items
                .SelectMany(c => c.Parameters.Select(p => p.Name))
                .ToHashSet(StringComparer.Ordinal);

            var extraParamNames = allParamNames.Except(commonParamNames).ToHashSet(StringComparer.Ordinal);

            // Only merge when exactly one parameter differs
            if (extraParamNames.Count != 1)
            {
                result.AddRange(items);
                continue;
            }

            var extraParamName = extraParamNames.Single();

            // Get the extra parameter info from whichever capability has it
            var capWithExtra = items.First(c => c.Parameters.Any(p => string.Equals(p.Name, extraParamName, StringComparison.Ordinal)));
            var extraParam = capWithExtra.Parameters.First(p => string.Equals(p.Name, extraParamName, StringComparison.Ordinal));

            // Only merge if the extra param is required (not already optional)
            if (extraParam.IsOptional || extraParam.IsNullable)
            {
                result.AddRange(items);
                continue;
            }

            // Use the capability with the shortest MethodName as the base
            var shortest = items.OrderBy(c => c.MethodName.Length).ThenBy(c => c.MethodName, StringComparer.Ordinal).First();

            // Build merged params from the capability with the most parameters
            var fullCap = items.OrderByDescending(c => c.Parameters.Count).First();
            var mergedParams = new List<AtsParameterInfo>();
            foreach (var p in fullCap.Parameters)
            {
                if (string.Equals(p.Name, extraParamName, StringComparison.Ordinal))
                {
                    mergedParams.Add(new AtsParameterInfo
                    {
                        Name = p.Name,
                        Type = p.Type,
                        IsOptional = true,
                        IsNullable = true,
                        IsCallback = p.IsCallback,
                        CallbackParameters = p.CallbackParameters,
                        CallbackReturnType = p.CallbackReturnType,
                        DefaultValue = p.DefaultValue
                    });
                }
                else
                {
                    mergedParams.Add(p);
                }
            }

            // Determine which capability ID to use when the extra param IS vs IS NOT provided.
            // The "short" capability (without the extra param) is the default.
            // The "long" capability (with the extra param) is the alternate.
            var capWithoutExtra = items.FirstOrDefault(c => !c.Parameters.Any(p => string.Equals(p.Name, extraParamName, StringComparison.Ordinal)));
            var alternateCapabilityId = capWithExtra.CapabilityId;
            var baseCapabilityId = capWithoutExtra?.CapabilityId ?? shortest.CapabilityId;

            var merged = new AtsCapabilityInfo
            {
                CapabilityId = baseCapabilityId,
                MethodName = shortest.MethodName,
                OwningTypeName = shortest.OwningTypeName,
                Description = shortest.Description ?? items.FirstOrDefault(c => c.Description is not null)?.Description,
                Parameters = mergedParams,
                ReturnType = shortest.ReturnType,
                TargetTypeId = shortest.TargetTypeId,
                TargetType = shortest.TargetType,
                TargetParameterName = shortest.TargetParameterName,
                ReturnsBuilder = shortest.ReturnsBuilder,
                CapabilityKind = shortest.CapabilityKind,
                SourceLocation = shortest.SourceLocation,
                RunSyncOnBackgroundThread = shortest.RunSyncOnBackgroundThread,
                ExpandedTargetTypes = shortest.ExpandedTargetTypes
            };

            // Track the alternate dispatch so GenerateBuilderMethod can emit conditional logic
            if (!string.Equals(baseCapabilityId, alternateCapabilityId, StringComparison.Ordinal))
            {
                _mergedCapabilityDispatches[baseCapabilityId] = new MergedCapabilityDispatch(alternateCapabilityId, extraParamName);
            }

            result.Add(merged);
        }

        return result;
    }

    /// <summary>
    /// Filters capability parameters for Python code generation.
    /// Removes the target parameter (e.g., "builder", "context") if specified,
    /// and removes cancellationToken when a separate timeout parameter already exists,
    /// since cancellationToken maps to "timeout" in Python and would cause a naming conflict.
    /// </summary>
    private static List<AtsParameterInfo> FilterMethodParameters(IReadOnlyList<AtsParameterInfo> parameters, string? targetParameterName = null)
    {
        var filtered = targetParameterName is not null
            ? parameters.Where(p => p.Name != targetParameterName)
            : parameters.AsEnumerable();

        if (parameters.Any(p => string.Equals(p.Name, "timeout", StringComparison.Ordinal)))
        {
            filtered = filtered.Where(p => p.Type?.TypeId != AtsConstants.CancellationToken);
        }

        return filtered.ToList();
    }

    /// <summary>
    /// Gets the Python parameter name for a capability parameter.
    /// Converts camelCase to snake_case, and renames cancellationToken to timeout.
    /// </summary>
    private static string GetParamName(AtsParameterInfo param)
    {
        if (param.Type?.TypeId == AtsConstants.CancellationToken)
        {
            return "timeout";
        }
        return ToSnakeCase(param.Name);
    }

    /// <summary>
    /// Gets the Python representation of a parameter's default value.
    /// Returns "None" if the default value is null or not set.
    /// </summary>
    private static string GetPythonDefaultValue(AtsParameterInfo param)
    {
        var defaultValue = param.DefaultValue;

        if (defaultValue is null)
        {
            return "None";
        }

        return defaultValue switch
        {
            bool b => b ? "True" : "False",
            string s => $"\"{s.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"",
            char c => $"\"{c}\"",
            int i => i.ToString(CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture),
            float f => float.IsPositiveInfinity(f) ? "float('inf')" : float.IsNegativeInfinity(f) ? "float('-inf')" : f.ToString(CultureInfo.InvariantCulture),
            double d => double.IsPositiveInfinity(d) ? "float('inf')" : double.IsNegativeInfinity(d) ? "float('-inf')" : d.ToString(CultureInfo.InvariantCulture),
            Enum e => $"\"{e}\"",
            _ => "None"
        };
    }

    /// <summary>
    /// Gets the Python type annotation suffix and default value for an optional parameter.
    /// Uses the actual default value when available instead of always defaulting to None.
    /// </summary>
    private string GetOptionalParamSuffix(AtsParameterInfo param)
    {
        var pythonDefault = GetPythonDefaultValue(param);
        if (pythonDefault == "None")
        {
            var paramType = MapParameterToPython(param);
            return $"{paramType} | None = None";
        }

        var type = MapParameterToPython(param);
        // When we have a real default, the type doesn't need "| None" unless the param is also nullable
        if (param.IsNullable)
        {
            return $"{type} | None = {pythonDefault}";
        }
        return $"{type} = {pythonDefault}";
    }

    /// <summary>
    /// Maps an enum type ID to the generated Python enum name.
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
    /// Maps a union type to Python union syntax (T1 | T2 | ...).
    /// </summary>
    private string MapUnionTypeToPython(AtsTypeRef typeRef)
    {
        if (typeRef.UnionTypes == null || typeRef.UnionTypes.Count == 0)
        {
            return "typing.Any";
        }

        var memberTypes = typeRef.UnionTypes
            .Select(MapTypeRefToPython)
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
    /// Gets a Python class name for a DTO type.
    /// </summary>
    private static string GetDtoClassName(string typeId)
    {
        // Extract simple type name and use as class name
        var simpleTypeName = ExtractSimpleTypeName(typeId);
        return simpleTypeName;
    }

    /// <summary>
    /// Maps a parameter to its Python type, handling callbacks specially.
    /// For interface handle types, uses ResourceBuilderBase as the parameter type.
    /// </summary>
    private string MapParameterToPython(AtsParameterInfo param)
    {
        if (param.IsCallback)
        {
            return GenerateCallbackTypeSignature(param.CallbackParameters, param.CallbackReturnType);
        }

        if (param.Type?.TypeId == AtsConstants.CancellationToken)
        {
            return "int";
        }

        var baseType = MapTypeRefToPython(param.Type);
        return baseType;
    }

    // /// <summary>
    // /// Checks if a type reference is an interface handle type.
    // /// Interface handles need base class types to accept wrapper classes.
    // /// </summary>
    // private static bool IsInterfaceHandleType(AtsTypeRef? typeRef)
    // {
    //     if (typeRef == null)
    //     {
    //         return false;
    //     }
    //     return typeRef.Category == AtsTypeCategory.Handle && typeRef.IsInterface;
    // }

    /// <summary>
    /// Gets the TypeId from a capability's return type.
    /// </summary>
    private static string? GetReturnTypeId(AtsCapabilityInfo capability) => capability.ReturnType?.TypeId;

    /// <inheritdoc />
    public string Language => "Python";

    /// <inheritdoc />
    public Dictionary<string, string> GenerateDistributedApplication(AtsContext context)
    {
        var files = new Dictionary<string, string>();

        // Generate the capability-based aspire.py SDK
        files["aspire_app.py"] = GenerateAspireSdk(context);
        files["pyproject.toml"] = GetEmbeddedResource("pyproject.toml");

        return files;
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

    /// <summary>
    /// Gets a valid Python method name from a capability method name.
    /// Converts camelCase to snake_case.
    /// Handles dotted names like "EnvironmentContext.resource" by extracting just the final part.
    /// </summary>
    private static string GetPythonMethodName(string methodName)
    {
        // Extract last component if dotted (e.g., "Type.method" -> "method")
        var lastDot = methodName.LastIndexOf('.');
        if (lastDot >= 0)
        {
            methodName = methodName[(lastDot + 1)..];
        }

        // Convert camelCase to snake_case
        var snakeName = ToSnakeCase(methodName);
        if (snakeName.EndsWith("_async"))
        {
            snakeName = snakeName[..^6];
        }
        return snakeName;
    }

    private static string GetMethodAsOptionName(string methodName)
    {
        if (methodName.StartsWith("with_"))
        {
            return methodName[5..];
        }
        return methodName;
    }
    
    private static string GetMethodParametersName(string methodName)
    {
        methodName = char.ToUpper(methodName[0]) + methodName.Substring(1);
        if (methodName.StartsWith("With"))
        {
            methodName = methodName[4..];
        }
        return methodName + "Parameters";
    }
    /// <summary>
    /// Generates the aspire.py SDK file with capability-based API.
    /// </summary>
    private string GenerateAspireSdk(AtsContext context)
    {
        _moduleBuilder = new PythonModuleBuilder();

        var capabilities = context.Capabilities;
        var dtoTypes = context.DtoTypes;
        var enumTypes = context.EnumTypes;

        // Get builder models (flattened - each builder has all its applicable capabilities)
        var allBuilders = CreateBuilderModels(capabilities);
        var entryPoints = GetEntryPointCapabilities(capabilities);

        // All builders (no special filtering)
        var builders = allBuilders;

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
            if (IsHandleType(cap.ReturnType) && !dtoTypeIds.Contains(cap.ReturnType!.TypeId))
            {
                typeIds.Add(GetReturnTypeId(cap)!);
            }
            // Add parameter type IDs (for types like IResourceBuilder<IResource>)
            foreach (var param in cap.Parameters)
            {
                if (IsHandleType(param.Type) && !dtoTypeIds.Contains(param.Type!.TypeId))
                {
                    typeIds.Add(param.Type!.TypeId);
                }
                // Also collect callback parameter types
                if (param.IsCallback && param.CallbackParameters != null)
                {
                    foreach (var cbParam in param.CallbackParameters)
                    {
                        if (IsHandleType(cbParam.Type) && !dtoTypeIds.Contains(cbParam.Type.TypeId))
                        {
                            typeIds.Add(cbParam.Type.TypeId);
                        }
                    }
                }
            }
        }

        // Collect enum type names
        foreach (var enumType in enumTypes)
        {
            if (!_enumTypeNames.ContainsKey(enumType.TypeId))
            {
                _enumTypeNames[enumType.TypeId] = ExtractSimpleTypeName(enumType.TypeId);
            }
        }

        // Separate builders into categories:
        // 1. Resource builders: IResource*, ContainerResource, etc.
        // 2. Type classes: everything else (context types, wrapper types)
        var resources = builders.Where(b => b.TargetType?.IsResourceBuilder == true).ToList();
        var typeClasses = builders.Where(b => b.TargetType?.IsResourceBuilder != true).ToList();
        var interfaceClasses = resources.Where(b => b.IsInterface).ToList();

        // Build wrapper class name mapping for type resolution BEFORE generating code
        // This allows parameter types to use wrapper class names instead of handle types
        _wrapperClassNames.Clear();

        foreach (var resource in resources)
        {
            _wrapperClassNames[resource.TypeId] = resource.BuilderClassName;
        }

        // Add ReferenceExpression (defined in base.py, not generated)
        //_wrapperClassNames[AtsConstants.ReferenceExpressionTypeId] = "ReferenceExpression";

        // Generate enum types
        GenerateEnumTypes(enumTypes);

        // Generate DTO classes
        GenerateDtoClasses(dtoTypes);

        // Generate type classes (context types and wrapper types)
        foreach (var typeClass in typeClasses.Where(t => !_ignoreTypes.Contains(t.TypeId)))
        {
            GenerateTypeClass(typeClass);
        }

        // Generate interface ABC classes
        foreach (var interfaceClass in interfaceClasses)
        {
            GenerateInterfaceClass(interfaceClass);
        }
        // Generate resource builder classes
        foreach (var resource in resources.Where(b => !b.IsInterface))
        {
            GenerateBuilderClass(resource);
        }

        // Generate entry point functions
        GenerateEntryPointFunctions(_moduleBuilder.EntryPoints, entryPoints);

        return _moduleBuilder.Write();
    }

    /// <summary>
    /// Generates Python enums from discovered enum types.
    /// </summary>
    private void GenerateEnumTypes(IReadOnlyList<AtsEnumTypeInfo> enumTypes)
    {
        var sb = _moduleBuilder.Enums;
        if (enumTypes.Count == 0)
        {
            return;
        }

        foreach (var enumType in enumTypes.OrderBy(e => e.Name))
        {
            var enumName = _enumTypeNames[enumType.TypeId];
            sb.AppendLine(CultureInfo.InvariantCulture, $"{enumName} = typing.Literal[{string.Join(", ", enumType.Values.Select(v => $"\"{v}\""))}]");
            sb.AppendLine();
        }
    }

    /// <summary>
    /// Generates Python classes for DTO types marked with [AspireDto].
    /// </summary>
    private void GenerateDtoClasses(IReadOnlyList<AtsDtoTypeInfo> dtoTypes)
    {
        var sb = _moduleBuilder.DtoClasses;
        if (dtoTypes.Count == 0)
        {
            return;
        }

        foreach (var dtoType in dtoTypes.OrderBy(d => d.Name))
        {
            var className = GetDtoClassName(dtoType.TypeId);

            // All DTO properties are optional in Python to allow partial objects
            sb.AppendLine(CultureInfo.InvariantCulture, $"class {className}(typing.TypedDict, total=False):");
            foreach (var prop in dtoType.Properties)
            {
                var propType = MapTypeRefToPython(prop.Type);
                sb.AppendLine(CultureInfo.InvariantCulture, $"    {prop.Name}: {propType}");
            }
            sb.AppendLine();
        }
    }

    /// <summary>
    /// Converts a camelCase name to snake_case.
    /// </summary>
    private static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var result = new System.Text.StringBuilder();
        result.Append(char.ToLowerInvariant(name[0]));

        for (int i = 1; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                result.Append('_');
                result.Append(char.ToLowerInvariant(c));
            }
            else
            {
                result.Append(c);
            }
        }
        var resultStr = result.ToString();
        resultStr = resultStr.Replace("environment", "env");
        resultStr = resultStr.Replace("configuration", "config");
        resultStr = resultStr.Replace("application", "app");
        resultStr = resultStr.Replace("variable", "var");
        resultStr = resultStr.Replace("directory", "dir");
        return SanitizePythonIdentifier(resultStr);
    }

    /// <summary>
    /// Generates a type class (context type or wrapper type).
    /// Uses property-like pattern for exposed properties.
    /// </summary>
    private void GenerateTypeClass(BuilderModel model)
    {
        var className = DeriveClassName(model.TypeId);
        var sb = new System.Text.StringBuilder();
        _moduleBuilder.TypeClasses[className] = sb;

        // Separate capabilities by type using CapabilityKind enum
        var getters = model.Capabilities.Where(c => c.CapabilityKind == AtsCapabilityKind.PropertyGetter).ToList();
        var setters = model.Capabilities.Where(c => c.CapabilityKind == AtsCapabilityKind.PropertySetter).ToList();
        var contextMethods = model.Capabilities.Where(c => c.CapabilityKind == AtsCapabilityKind.InstanceMethod).ToList();
        var otherMethods = model.Capabilities.Where(c => c.CapabilityKind == AtsCapabilityKind.Method).ToList();

        // Combine methods
        var allMethods = contextMethods.Concat(otherMethods).ToList();
        if (className == "AbstractDistributedApplicationBuilder")
        {
            sb.Append(PythonModuleBuilder.DistributedApplicationBuilder);
            sb.AppendLine();
        }
        else
        {
            if (model.IsInterface && model.Capabilities.Count == 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"class {className}(abc.ABC):");
                sb.AppendLine(CultureInfo.InvariantCulture, $"    \"\"\"Abstract base class for {className}.\"\"\"");
            }
            else
            {
                _moduleBuilder.HandleRegistrations[model.TypeId] = className;
                sb.AppendLine(CultureInfo.InvariantCulture, $"class {className}:");
                sb.AppendLine(CultureInfo.InvariantCulture, $"    \"\"\"Type class for {className}.\"\"\"");
                sb.AppendLine();
                sb.AppendLine("    def __init__(self, handle: Handle, client: AspireClient) -> None:");
                sb.AppendLine("        self._handle = handle");
                sb.AppendLine("        self._client = client");
                sb.AppendLine();
                sb.AppendLine("    def __repr__(self) -> str:");
                sb.AppendLine(CultureInfo.InvariantCulture, $"        return f\"{className}(handle={{self._handle.handle_id}})\"");
                sb.AppendLine();
                sb.AppendLine("    @_uncached_property");
                sb.AppendLine("    def handle(self) -> Handle:");
                sb.AppendLine("        \"\"\"The underlying object reference handle.\"\"\"");
                sb.AppendLine("        return self._handle");
                sb.AppendLine();
            }
        }

        // Group getters and setters by property name to create properties
        var properties = GroupPropertiesByName(getters, setters);

        // Generate properties
        foreach (var prop in properties)
        {
            GeneratePropertyMethods(sb, prop.PropertyName, prop.Getter, prop.Setter);
        }

        // Generate methods
        foreach (var method in allMethods)
        {
            GenerateTypeClassMethod(sb, method);
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
    /// Generates getter and setter methods for a property.
    /// </summary>
    private void GeneratePropertyMethods(System.Text.StringBuilder sb, string propertyName, AtsCapabilityInfo? getter, AtsCapabilityInfo? setter, bool isInterface = false)
    {
        var snakeName = ToSnakeCase(propertyName);

        // Generate getter
        if (getter != null)
        {
            if (propertyName == "cancellationToken")
            {
                // TODO: Replace this with handling for a CancelCallback exception.
                // or maybe a cancel() method.
                sb.AppendLine(CultureInfo.InvariantCulture, $"    def cancel(self) -> None:");
                sb.AppendLine(CultureInfo.InvariantCulture, $"        \"\"\"Cancel the operation.\"\"\"");
                if (isInterface)
                {
                    return;
                }
                sb.AppendLine(CultureInfo.InvariantCulture, $"        token: CancellationToken = self._client.invoke_capability(");
                sb.AppendLine(CultureInfo.InvariantCulture, $"            '{getter.CapabilityId}',");
                sb.AppendLine(CultureInfo.InvariantCulture, $"            {{'context': self._handle}}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"        )");
                sb.AppendLine(CultureInfo.InvariantCulture, $"        token.cancel()");
                sb.AppendLine();
                return;
            }
            var returnType = MapTypeRefToPython(getter.ReturnType);
            var propertyType = setter != null ? "@_uncached_property" : "@_cached_property";
            if (!string.IsNullOrEmpty(getter.Description))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    {propertyType}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"    def {snakeName}(self) -> {returnType}:");
                sb.AppendLine(CultureInfo.InvariantCulture, $"        \"\"\"{getter.Description}\"\"\"");
            }
            else
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    {propertyType}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"    def {snakeName}(self) -> {returnType}:");
                sb.AppendLine(CultureInfo.InvariantCulture, $"        \"\"\"{propertyName}\"\"\"");
            }
            if (!isInterface)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"        result = self._client.invoke_capability(");
                sb.AppendLine(CultureInfo.InvariantCulture, $"            '{getter.CapabilityId}',");
                sb.AppendLine(CultureInfo.InvariantCulture, $"            {{'context': self._handle}}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"        )");
                sb.AppendLine(CultureInfo.InvariantCulture, $"        return typing.cast({returnType}, result)");
            }
            sb.AppendLine();

        }

        // Generate setter
        if (setter != null)
        {
            var valueParam = setter.Parameters.FirstOrDefault(p => p.Name == "value");
            if (valueParam != null)
            {
                var valueType = MapTypeRefToPython(valueParam.Type);

                if (!string.IsNullOrEmpty(setter.Description))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    @{snakeName}.setter");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    def {snakeName}(self, value: {valueType}) -> None:");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"        \"\"\"{setter.Description}\"\"\"");
                }
                else
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    @{snakeName}.setter");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    def {snakeName}(self, value: {valueType}) -> None:");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"        \"\"\"Set {propertyName}\"\"\"");
                }
                if (!isInterface)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"        self._client.invoke_capability(");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"            '{setter.CapabilityId}',");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"            {{'context': self._handle, 'value': value}}");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"        )");
                }
                sb.AppendLine();
            }
        }
    }

    /// <summary>
    /// Generates a method on a type class.
    /// </summary>
    private void GenerateTypeClassMethod(System.Text.StringBuilder sb, AtsCapabilityInfo capability)
    {
        // Use OwningTypeName if available to extract method name, otherwise parse from MethodName
        var methodName = !string.IsNullOrEmpty(capability.OwningTypeName) && capability.MethodName.Contains('.')
            ? capability.MethodName[(capability.MethodName.LastIndexOf('.') + 1)..]
            : capability.MethodName;

        var pythonMethodName = GetPythonMethodName(methodName);

        var targetParamName = capability.TargetParameterName ?? "context";
        var userParams = FilterMethodParameters(capability.Parameters, targetParamName);
        var requiredParams = userParams.Where(p => !p.IsOptional && !p.IsNullable).ToList();
        var optionalParams = userParams.Where(p => !requiredParams.Contains(p)).ToList();

        // Determine return type
        var returnType = GetReturnTypeId(capability) != null
            ? MapTypeRefToPython(capability.ReturnType)
            : "None";
        var isResourceBuilder = capability.ReturnType != null && capability.ReturnType.Category == AtsTypeCategory.Handle &&
            capability.ReturnType.IsResourceBuilder && !capability.ReturnType.IsInterface;

        // Generate method signature
        sb.Append(CultureInfo.InvariantCulture, $"    def {pythonMethodName}(self");
        foreach (var param in requiredParams)
        {
            var paramName = GetParamName(param);
            var paramType = MapParameterToPython(param);
            sb.Append(CultureInfo.InvariantCulture, $", {paramName}: {paramType}");
        }
        if (optionalParams.Count > 0)
        {
            sb.Append(", *");
        }
        foreach (var param in optionalParams)
        {
            var paramName = GetParamName(param);
            var suffix = GetOptionalParamSuffix(param);
            sb.Append(CultureInfo.InvariantCulture, $", {paramName}: {suffix}");
        }
        if (isResourceBuilder)
        {
            sb.Append(CultureInfo.InvariantCulture, $", **kwargs: typing.Unpack[\"{returnType}Kwargs\"]");
        }
        sb.AppendLine(CultureInfo.InvariantCulture, $") -> {returnType}:{(isResourceBuilder ? "  # type: ignore" : string.Empty)}");
        // Generate docstring
        if (!string.IsNullOrEmpty(capability.Description))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"        \"\"\"{capability.Description}\"\"\"");
        }

        // Build args dict
        sb.AppendLine(CultureInfo.InvariantCulture, $"        rpc_args: dict[str, typing.Any] = {{'{targetParamName}': self._handle}}");
        foreach (var param in userParams)
        {
            var paramName = GetParamName(param);
            var paramHandler = GetParamHandler(param, paramName);

            if (param.IsOptional || param.IsNullable)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"        if {paramName} is not None:");
                sb.AppendLine(CultureInfo.InvariantCulture, $"            rpc_args['{param.Name}'] = {paramHandler}");
            }
            else
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"        rpc_args['{param.Name}'] = {paramHandler}");
            }
        }

        // Invoke capability
        if (returnType == "None")
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"        self._client.invoke_capability(");
            sb.AppendLine(CultureInfo.InvariantCulture, $"            '{capability.CapabilityId}',");
            sb.AppendLine(CultureInfo.InvariantCulture, $"            rpc_args");
            sb.AppendLine(CultureInfo.InvariantCulture, $"        )");
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"        result = self._client.invoke_capability(");
            sb.AppendLine(CultureInfo.InvariantCulture, $"            '{capability.CapabilityId}',");
            sb.AppendLine(CultureInfo.InvariantCulture, $"            rpc_args,");
            if (isResourceBuilder)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"            kwargs,");
            }
            sb.AppendLine(CultureInfo.InvariantCulture, $"        )");
            if (capability.ReturnType != null && (capability.ReturnType.Category == AtsTypeCategory.Handle || capability.ReturnType.Category == AtsTypeCategory.Dto))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"        return typing.cast({returnType}, result)");
            }
            else
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"        return result");
            }
        }
        sb.AppendLine();
    }

    private void GenerateInterfaceClass(BuilderModel builder)
    {
        var sb = new System.Text.StringBuilder();
        _moduleBuilder.InterfaceClasses[builder.BuilderClassName] = sb;

        var baseClass = "abc.ABC";
        var implementedInterfaces = builder.TargetType?.ImplementedInterfaces.ToList();
        if (implementedInterfaces is { Count: > 0 })
        {
            // Remove interfaces that are already implemented by another interface in the list
            var transitivelyImplemented = new HashSet<string>(
                implementedInterfaces
                    .SelectMany(i => i.ImplementedInterfaces ?? [])
                    .Select(i => i.TypeId),
                StringComparer.Ordinal);
            implementedInterfaces = implementedInterfaces
                .Where(i => !transitivelyImplemented.Contains(i.TypeId))
                .ToList();
            baseClass = string.Join(", ", implementedInterfaces.Select(i => DeriveClassName(i.TypeId)));
        }
        if (builder.TargetType?.ClrType!.IsGenericType == true)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"T_{builder.BuilderClassName} = typing.TypeVar('T_{builder.BuilderClassName}')");
            sb.AppendLine(CultureInfo.InvariantCulture, $"class {builder.BuilderClassName}({baseClass}, typing.Generic[T_{builder.BuilderClassName}]):");
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"class {builder.BuilderClassName}({baseClass}):");
        }
        sb.AppendLine(CultureInfo.InvariantCulture, $"    \"\"\"Abstract base class for {builder.BuilderClassName} interface.\"\"\"");
        sb.AppendLine();

        // Group getters and setters by property name to create properties
        var getters = builder.Capabilities.Where(c => c.CapabilityKind == AtsCapabilityKind.PropertyGetter).ToList();
        var setters = builder.Capabilities.Where(c => c.CapabilityKind == AtsCapabilityKind.PropertySetter).ToList();
        var properties = GroupPropertiesByName(getters, setters);

        // Generate properties
        foreach (var prop in properties)
        {
            GeneratePropertyMethods(sb, prop.PropertyName, prop.Getter, prop.Setter, true);
        }

        // Generate methods for each capability
        // Filter out property getters and setters - they are not methods
        var methods = builder.Capabilities.Where(c =>
            c.CapabilityKind != AtsCapabilityKind.PropertyGetter &&
            c.CapabilityKind != AtsCapabilityKind.PropertySetter).ToList();

        methods = MergeCapabilitiesBySourceLocation(methods);

        foreach (var capability in methods)
        {
            GenerateBuilderMethod(sb, capability, builder.TargetType!.IsResourceBuilder);
            sb.AppendLine();
        }
    }
    
    private void GenerateBuilderClass(BuilderModel builder)
    {
        var sb = new System.Text.StringBuilder();
        var sbOptions = new System.Text.StringBuilder();
        var sbConstructor = new System.Text.StringBuilder();
        _moduleBuilder.ResourceClasses[builder.BuilderClassName] = sb;
        _moduleBuilder.ResourceOptions[builder.BuilderClassName] = sbOptions;
        _moduleBuilder.HandleRegistrations[builder.TypeId] = builder.BuilderClassName;

        var optionsBaseClass = "_BaseResourceKwargs";
        var baseClass = "_BaseResource";
        var isBaseResource = builder.BuilderClassName == baseClass;
        var baseBuilderClassName = baseClass;
        if (!isBaseResource)
        {
            var baseType = builder.TargetType?.BaseType;
            var baseTypeInterfaces = new List<string>();
            
                if (baseType != null)
                {
                    baseTypeInterfaces = baseType.ImplementedInterfaces.Select(i => i.TypeId).ToList();
                    var baseTypeName = DeriveClassName(baseType);
                    if (baseTypeName != "AbstractResource")
                    {
                        baseClass = baseTypeName;
                        optionsBaseClass = $"{baseTypeName}Kwargs";
                    }
            }
            baseBuilderClassName = baseClass;
            var implementedInterfaces = builder.TargetType?.ImplementedInterfaces.Where(i => !baseTypeInterfaces.Contains(i.TypeId)).ToList();
            if (implementedInterfaces is { Count: > 0 })
            {
                // Remove interfaces that are already implemented by another interface in the list
                var transitivelyImplemented = new HashSet<string>(
                    implementedInterfaces
                        .SelectMany(i => i.ImplementedInterfaces ?? [])
                        .Select(i => i.TypeId),
                    StringComparer.Ordinal);
                implementedInterfaces = implementedInterfaces
                    .Where(i => !transitivelyImplemented.Contains(i.TypeId))
                    .ToList();

                foreach (var i in implementedInterfaces)
                {
                    if (i.ClrType!.IsGenericType)
                    {
                        if (i.ClrType.GenericTypeArguments == null || i.ClrType.GenericTypeArguments.Length != 1)
                        {
                            throw new InvalidOperationException("Cannot support a generic interface that doesn't have exactly 1 argument.");
                        }
                        var genericSubType = i.ClrType.GenericTypeArguments[0];
                        baseClass += $", {DeriveClassName(i)}[\"{DeriveClassName(genericSubType)}\"]";
                    }
                    else
                    {
                        baseClass += ", " + DeriveClassName(i);
                    }
                }
            }
        
            sb.AppendLine(CultureInfo.InvariantCulture, $"class {builder.BuilderClassName}({baseClass}):");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    \"\"\"{builder.BuilderClassName} resource.\"\"\"");
            sb.AppendLine();
            sb.AppendLine("    def __repr__(self) -> str:");
            sb.AppendLine(CultureInfo.InvariantCulture, $"        return \"{builder.BuilderClassName}(handle={{self._handle.handle_id}})\"");
            sb.AppendLine();
            sbOptions.AppendLine(CultureInfo.InvariantCulture, $"class {builder.BuilderClassName}Kwargs({optionsBaseClass}, total=False):");
            sbOptions.AppendLine(CultureInfo.InvariantCulture, $"    \"\"\"{builder.BuilderClassName} options.\"\"\"");
            sbOptions.AppendLine();
            sbConstructor.AppendLine(CultureInfo.InvariantCulture, $"    def __init__(self, handle: Handle, client: AspireClient, **kwargs: typing.Unpack[{builder.BuilderClassName}Kwargs]) -> None:");

        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"class {builder.BuilderClassName}(AbstractResource):");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    \"\"\"Base resource class.\"\"\"");
            sb.AppendLine();
            sb.AppendLine("    def _wrap_builder(self, builder: typing.Any) -> Handle:");
            sb.AppendLine("        if isinstance(builder, Handle):");
            sb.AppendLine("            return builder");
            sb.AppendLine("        return typing.cast(typing.Self, builder).handle");
            sb.AppendLine();
            sb.AppendLine("    @_uncached_property");
            sb.AppendLine("    def handle(self) -> Handle:");
            sb.AppendLine("        \"\"\"The underlying object reference handle.\"\"\"");
            sb.AppendLine("        return self._handle");
            sb.AppendLine();
            sbOptions.AppendLine(CultureInfo.InvariantCulture, $"class {optionsBaseClass}(typing.TypedDict, total=False):");
            sbOptions.AppendLine("    \"\"\"Base resource options.\"\"\"");
            sbOptions.AppendLine();
            sbConstructor.AppendLine(CultureInfo.InvariantCulture, $"    def __init__(self, handle: Handle, client: AspireClient, **kwargs: typing.Unpack[{optionsBaseClass}]) -> None:");
        }

        // Initialize option names, inheriting from base class
        var optionNames = new List<string>();
        if (!isBaseResource && _moduleBuilder.ResourceOptionNames.TryGetValue(baseBuilderClassName, out var baseOptionNames))
        {
            optionNames.AddRange(baseOptionNames);
        }
        _moduleBuilder.ResourceOptionNames[builder.BuilderClassName] = optionNames;

        // Group getters and setters by property name to create properties
        // Only include properties that are not covered by base class hierarchy
        var getters = builder.Capabilities.Where(c =>
            c.CapabilityKind == AtsCapabilityKind.PropertyGetter &&
            !IsTargetTypeCoveredByBaseHierarchy(c.TargetType, builder.TargetType)).ToList();
        var setters = builder.Capabilities.Where(c =>
            c.CapabilityKind == AtsCapabilityKind.PropertySetter &&
            !IsTargetTypeCoveredByBaseHierarchy(c.TargetType, builder.TargetType)).ToList();
        var properties = GroupPropertiesByName(getters, setters);

        // Generate properties
        foreach (var prop in properties)
        {
            GeneratePropertyMethods(sb, prop.PropertyName, prop.Getter, prop.Setter);
        }

        // Generate methods for each capability
        // Filter out property getters and setters - they are not methods
        // Also filter out capabilities whose TargetType is already covered by a base class
        var methods = builder.Capabilities.Where(c =>
            c.CapabilityKind != AtsCapabilityKind.PropertyGetter &&
            c.CapabilityKind != AtsCapabilityKind.PropertySetter &&
            !IsTargetTypeCoveredByBaseHierarchy(c.TargetType, builder.TargetType)).ToList();

        methods = MergeCapabilitiesBySourceLocation(methods);

        foreach (var capability in methods)
        {
            GenerateBuilderMethod(sb, capability, false, sbOptions, sbConstructor, builder.BuilderClassName);
            sb.AppendLine();
        }

        if (isBaseResource)
        {
            sbConstructor.AppendLine("        self._handle = handle");
            sbConstructor.AppendLine("        self._client = client");
            sbConstructor.AppendLine("        if kwargs:");
            sbConstructor.AppendLine("            raise TypeError(f\"Unexpected keyword arguments: {list(kwargs.keys())}\")");
        }
        else
        {
            sbConstructor.AppendLine("        super().__init__(handle, client, **kwargs)");
        }
        sb.AppendLine(sbConstructor.ToString());
    }

    private void GenerateBuilderMethod(System.Text.StringBuilder sb, AtsCapabilityInfo capability, bool isInterface,
        System.Text.StringBuilder? options = null, System.Text.StringBuilder? constructor = null, string? builderClassName = null)
    {
        var methodName = GetPythonMethodName(capability.MethodName);
        // Use the actual target parameter name from the capability
        var targetParamName = capability.TargetParameterName ?? "builder";
        var userParams = FilterMethodParameters(capability.Parameters, targetParamName);

        // Determine return type - use the builder's own type for fluent methods
        var returnsBuilder = capability.ReturnsBuilder && capability.ReturnType!.TypeId == capability.TargetTypeId;
        var returnsChildBuilder = capability.ReturnsBuilder && capability.ReturnType != null && IsHandleType(capability.ReturnType) && capability.ReturnType.TypeId != capability.TargetTypeId;
        var returnType = returnsBuilder ? "typing.Self" : MapTypeRefToPython(capability.ReturnType);
        var requiredParams = userParams.Where(p => !p.IsOptional && !p.IsNullable).ToList();
        var optionalParams = userParams.Where(p => !requiredParams.Contains(p)).ToList();

        if (isInterface)
        {
            sb.AppendLine("    @abc.abstractmethod");
        }
        else if (returnType == "typing.Self" && options != null && constructor != null)
        {
            var optionName = GetMethodAsOptionName(methodName);
            var optionTypeVariations = CreateOptionVariations(capability, userParams, requiredParams, optionalParams);
            var formattedOtions = string.Join(" | ", optionTypeVariations.Select(v => v.OptionType.Replace("(", "tuple[").Replace(")", "]")));
            if (optionTypeVariations[0].Experimental != null)
            {
                formattedOtions = $"Annotated[{options}, Warnings(experimental=\"{optionTypeVariations[0].Experimental}\")]";
            }
            options.AppendLine(CultureInfo.InvariantCulture, $"    {optionName}: {formattedOtions}");
            _mergedCapabilityDispatches.TryGetValue(capability.CapabilityId, out var optionMergedDispatch);
            BuildOptionConstructor(constructor, capability, optionName, optionTypeVariations, optionMergedDispatch);

            // Track option name for conflict detection
            if (builderClassName != null && _moduleBuilder.ResourceOptionNames.TryGetValue(builderClassName, out var optionNamesList))
            {
                optionNamesList.Add(optionName);
            }
        }

        // Generate method signature
        sb.Append(CultureInfo.InvariantCulture, $"    def {methodName}(self");
        foreach (var param in requiredParams)
        {
            var paramName = GetParamName(param);
            var paramType = MapParameterToPython(param);
            sb.Append(CultureInfo.InvariantCulture, $", {paramName}: {paramType}");
        }
        if (optionalParams.Count > 0)
        {
            sb.Append(", *");
        }
        foreach (var param in optionalParams)
        {
            var paramName = GetParamName(param);
            var suffix = GetOptionalParamSuffix(param);
            sb.Append(CultureInfo.InvariantCulture, $", {paramName}: {suffix}");
        }

        if (returnType == "None")
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $") -> None:");
        }
        else
        {
            if (returnsChildBuilder)
            {
                sb.Append(CultureInfo.InvariantCulture, $", **kwargs: typing.Unpack[{returnType}Kwargs]");
            }
            sb.AppendLine(CultureInfo.InvariantCulture, $") -> {returnType}:{(returnsChildBuilder ? "  # type: ignore" : string.Empty)}");
        }

        // Generate docstring
        if (!string.IsNullOrEmpty(capability.Description))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"        \"\"\"{capability.Description}\"\"\"");
        }

        if (isInterface)
        {
            return;
        }

        // Build args dict
        sb.AppendLine(CultureInfo.InvariantCulture, $"        rpc_args: dict[str, typing.Any] = {{'{targetParamName}': self._handle}}");
        foreach (var param in userParams)
        {
            var paramName = GetParamName(param);
            var paramHandler = GetParamHandler(param, paramName);

            if (param.IsOptional || param.IsNullable)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"        if {paramName} is not None:");
                sb.AppendLine(CultureInfo.InvariantCulture, $"            rpc_args['{param.Name}'] = {paramHandler}");
            }
            else
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"        rpc_args['{param.Name}'] = {paramHandler}");
            }
        }

        // Check if this is a merged capability that needs conditional dispatch
        _mergedCapabilityDispatches.TryGetValue(capability.CapabilityId, out var mergedDispatch);
        var discriminatingPythonParam = mergedDispatch is not null ? GetParamName(
            userParams.First(p => string.Equals(p.Name, mergedDispatch.DiscriminatingParamName, StringComparison.Ordinal))) : null;

        if (mergedDispatch is not null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"        capability_id = '{mergedDispatch.AlternateCapabilityId}' if {discriminatingPythonParam} is not None else '{capability.CapabilityId}'");
        }

        var capabilityIdExpr = mergedDispatch is not null ? "capability_id" : $"'{capability.CapabilityId}'";

        if (returnType == "None")
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"        self._client.invoke_capability(");
            sb.AppendLine(CultureInfo.InvariantCulture, $"            {capabilityIdExpr},");
            sb.AppendLine(CultureInfo.InvariantCulture, $"            rpc_args");
            sb.AppendLine(CultureInfo.InvariantCulture, $"        )");
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"        result = self._client.invoke_capability(");
            sb.AppendLine(CultureInfo.InvariantCulture, $"            {capabilityIdExpr},");
            sb.AppendLine(CultureInfo.InvariantCulture, $"            rpc_args,");
            if (returnsChildBuilder)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"            kwargs,");
            }
            sb.AppendLine(CultureInfo.InvariantCulture, $"        )");
            if (returnsBuilder)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"        self._handle = self._wrap_builder(result)");
                sb.AppendLine(CultureInfo.InvariantCulture, $"        return self");
            }
            else
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"        return typing.cast({returnType}, result)");
            }
        }
    }

    /// <summary>
    /// Generates entry point functions.
    /// </summary>
    private void GenerateEntryPointFunctions(System.Text.StringBuilder sb, List<AtsCapabilityInfo> entryPoints)
    {
        if (entryPoints.Count == 0)
        {
            return;
        }

        foreach (var capability in entryPoints)
        {
            GenerateEntryPointFunction(sb, capability);
        }
    }

    private void GenerateEntryPointFunction(System.Text.StringBuilder sb, AtsCapabilityInfo capability)
    {
        var methodName = GetPythonMethodName(capability.MethodName);

        // Build parameter list
        var paramDefs = new List<string> { "client: AspireClient" };
        var paramArgs = new List<string>();

        var userParams = FilterMethodParameters(capability.Parameters);
        foreach (var param in userParams)
        {
            var paramName = GetParamName(param);
            if (param.IsOptional || param.IsNullable)
            {
                paramDefs.Add($"{paramName}: {GetOptionalParamSuffix(param)}");
            }
            else
            {
                var paramType = MapParameterToPython(param);
                paramDefs.Add($"{paramName}: {paramType}");
            }
            paramArgs.Add(param.Name);
        }

        var paramsString = string.Join(", ", paramDefs);

        // Determine return type
        var capReturnTypeId = GetReturnTypeId(capability);
        var returnType = !string.IsNullOrEmpty(capReturnTypeId)
            ? MapTypeRefToPython(capability.ReturnType)
            : "None";

        // Generate JSDoc equivalent
        if (!string.IsNullOrEmpty(capability.Description))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"def {methodName}({paramsString}) -> {returnType}:");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    \"\"\"{capability.Description}\"\"\"");
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"def {methodName}({paramsString}) -> {returnType}:");
        }

        // Build args dict
        sb.AppendLine(CultureInfo.InvariantCulture, $"    rpc_args: dict[str, typing.Any] = {{}}");
        foreach (var param in userParams)
        {
            var paramName = GetParamName(param);
            if (param.IsOptional || param.IsNullable)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    if {paramName} is not None:");
                sb.AppendLine(CultureInfo.InvariantCulture, $"        rpc_args['{param.Name}'] = {paramName}");
            }
            else
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    rpc_args['{param.Name}'] = {paramName}");
            }
        }

        // Invoke capability
        if (returnType == "None")
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"    client.invoke_capability(");
            sb.AppendLine(CultureInfo.InvariantCulture, $"        '{capability.CapabilityId}',");
            sb.AppendLine(CultureInfo.InvariantCulture, $"        rpc_args");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    )");
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"    result = client.invoke_capability(");
            sb.AppendLine(CultureInfo.InvariantCulture, $"        '{capability.CapabilityId}',");
            sb.AppendLine(CultureInfo.InvariantCulture, $"        rpc_args");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    )");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    return result");
        }
        sb.AppendLine();
    }

    // ============================================================================
    // Builder Model Helpers
    // ============================================================================

    /// <summary>
    /// Groups capabilities by ExpandedTargetTypes to create builder models.
    /// Uses expansion to map interface targets to their concrete implementations.
    /// Also creates builders for interface types (for use as return type wrappers).
    /// </summary>
    private List<BuilderModel> CreateBuilderModels(IReadOnlyList<AtsCapabilityInfo> capabilities)
    {
        // Group capabilities by expanded target type IDs
        var capabilitiesByTypeId = new Dictionary<string, List<AtsCapabilityInfo>>();

        // Track the AtsTypeRef for each typeId
        var typeRefsByTypeId = new Dictionary<string, AtsTypeRef>();

        // Also track interface types and their capabilities
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

            if (targetTypeRef.Category != AtsTypeCategory.Handle)
            {
                continue;
            }

            if (targetTypeRef.IsInterface)
            {
                if (!interfaceCapabilities.TryGetValue(targetTypeId, out var interfaceList))
                {
                    interfaceList = [];
                    interfaceCapabilities[targetTypeId] = interfaceList;
                    typeRefsByTypeId[targetTypeId] = targetTypeRef;
                }
                interfaceList.Add(cap);

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
                            typeRefsByTypeId[expandedType.TypeId] = expandedType;
                        }
                        list.Add(cap);
                    }
                }
            }
            else
            {
                // No expansion - use original target (concrete type)
                if (!capabilitiesByTypeId.TryGetValue(targetTypeId, out var list))
                {
                    list = [];
                    capabilitiesByTypeId[targetTypeId] = list;
                    typeRefsByTypeId[targetTypeId] = targetTypeRef;
                }
                list.Add(cap);
            }
        }

        // Create a builder for each concrete type with its specific capabilities
        var builders = new List<BuilderModel>();
        foreach (var (typeId, typeCapabilities) in capabilitiesByTypeId)
        {
            var typeRef = typeRefsByTypeId.GetValueOrDefault(typeId);
            var builderClassName = typeRef is not null ? DeriveClassName(typeRef) : DeriveClassName(typeId);

            // Deduplicate capabilities by CapabilityId
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

        // Also create builders for interface types
        foreach (var (interfaceTypeId, caps) in interfaceCapabilities)
        {
            if (capabilitiesByTypeId.ContainsKey(interfaceTypeId))
            {
                continue;
            }

            var typeRef = typeRefsByTypeId.GetValueOrDefault(interfaceTypeId);
            var builderClassName = typeRef is not null ? DeriveClassName(typeRef) : DeriveClassName(interfaceTypeId);

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
        var allReferencedTypeRefs = CollectAllReferencedTypes(capabilities);
        var existingBuilderTypeIds = new HashSet<string>(capabilitiesByTypeId.Keys);
        foreach (var (interfaceTypeId, _) in interfaceCapabilities)
        {
            existingBuilderTypeIds.Add(interfaceTypeId);
        }

        foreach (var (typeId, typeRef) in allReferencedTypeRefs)
        {   
            var typeIdReference = typeRef.ClrType!.IsGenericType ? typeId.Split('`')[0] + "T" : typeId;
            if (existingBuilderTypeIds.Contains(typeIdReference))
            {
                continue;
            }

            var builderClassName = DeriveClassName(typeRef);

            // For non-interface resource builder types, find capabilities that target this type or an interface it implements
            // This is essentially here to make sure we can move common capabilities onto the _ResourceBase class.
            var applicableCapabilities = new List<AtsCapabilityInfo>();
            if (typeRef.IsResourceBuilder && !typeRef.IsInterface)
            {
                var implementedInterfaceIds = typeRef.ImplementedInterfaces?
                    .Select(i => i.TypeId)
                    .ToHashSet(StringComparer.Ordinal) ?? [];

                applicableCapabilities = capabilities
                    .Where(c => c.TargetTypeId == typeId ||
                                (c.TargetType?.IsInterface == true && implementedInterfaceIds.Contains(c.TargetTypeId!)))
                    .GroupBy(c => c.CapabilityId)
                    .Select(g => g.First())
                    .ToList();
            }

            var builder = new BuilderModel
            {
                TypeId = typeIdReference,
                BuilderClassName = builderClassName,
                Capabilities = applicableCapabilities,
                IsInterface = typeRef.IsInterface,
                TargetType = typeRef
            };
            builders.Add(builder);
            existingBuilderTypeIds.Add(typeIdReference);
        }

        // Topological sort: base types and interfaces must come before types that depend on them
        return TopologicalSortBuilders(builders);
    }

    /// <summary>
    /// Performs a topological sort on builders to ensure base types and interfaces
    /// come before types that extend or implement them.
    /// </summary>
    private static List<BuilderModel> TopologicalSortBuilders(List<BuilderModel> builders)
    {
        builders = builders
            .GroupBy(b => b.TypeId, StringComparer.Ordinal)
            .Select(g =>
            {
                if (g.Count() == 1)
                {
                    return g.First();
                }

                var first = g.First();
                return new BuilderModel
                {
                    TypeId = g.Key,
                    BuilderClassName = first.BuilderClassName,
                    Capabilities = g.SelectMany(b => b.Capabilities)
                        .GroupBy(c => c.CapabilityId, StringComparer.Ordinal)
                        .Select(cg => cg.First())
                        .ToList(),
                    IsInterface = first.IsInterface,
                    TargetType = g.Select(b => b.TargetType).FirstOrDefault(t => t is not null)
                };
            })
            .ToList();

        var buildersByTypeId = builders.ToDictionary(b => b.TypeId, StringComparer.Ordinal);
        var result = new List<BuilderModel>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal); // For cycle detection

        void Visit(BuilderModel builder)
        {
            if (visited.Contains(builder.TypeId))
            {
                return;
            }

            if (visiting.Contains(builder.TypeId))
            {
                // Cycle detected - skip to avoid infinite recursion
                return;
            }

            visiting.Add(builder.TypeId);

            // Visit base type first
            if (builder.TargetType?.BaseType != null)
            {
                var baseTypeId = GetBuilderTypeId(builder.TargetType.BaseType);
                if (buildersByTypeId.TryGetValue(baseTypeId, out var baseBuilder))
                {
                    Visit(baseBuilder);
                }
            }

            // Visit implemented interfaces
            if (builder.TargetType?.ImplementedInterfaces != null)
            {
                foreach (var iface in builder.TargetType.ImplementedInterfaces)
                {
                    if (buildersByTypeId.TryGetValue(GetBuilderTypeId(iface), out var ifaceBuilder))
                    {
                        Visit(ifaceBuilder);
                    }
                }
            }

            visiting.Remove(builder.TypeId);
            visited.Add(builder.TypeId);
            result.Add(builder);
        }

        // Visit all builders, sorting by name for deterministic output
        foreach (var builder in builders.OrderBy(b => b.BuilderClassName))
        {
            Visit(builder);
        }

        return result;
    }

    /// <summary>
    /// Collects all type refs referenced in capabilities.
    /// </summary>
    private Dictionary<string, AtsTypeRef> CollectAllReferencedTypes(IReadOnlyList<AtsCapabilityInfo> capabilities)
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
            if (!string.IsNullOrEmpty(typeRef.TypeId) && typeRef.Category == AtsTypeCategory.Dict)
            {
                _moduleBuilder.HandleRegistrations.TryAdd(typeRef.TypeId, "AspireDict");
            }
            if (!string.IsNullOrEmpty(typeRef.TypeId) && typeRef.Category == AtsTypeCategory.List)
            {
                _moduleBuilder.HandleRegistrations.TryAdd(typeRef.TypeId, "AspireList");
            }

            CollectFromTypeRef(typeRef.BaseType);
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
            if (typeRef.ImplementedInterfaces != null)
            {
                foreach (var iface in typeRef.ImplementedInterfaces)
                {
                    CollectFromTypeRef(iface);
                }
            }
        }

        foreach (var cap in capabilities)
        {
            CollectFromTypeRef(cap.ReturnType);

            foreach (var param in cap.Parameters)
            {
                CollectFromTypeRef(param.Type);

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

        return DeriveClassNameFromTypeName(typeName);
    }

    private static string DeriveClassName(AtsTypeRef typeRef)
    {
        if (typeRef.ClrType is { } clrType)
        {
            if (clrType.IsGenericType)
            {
                return DeriveClassName(clrType.GetGenericTypeDefinition()) + "T";
            }

            return DeriveClassName(clrType);
        }

        return DeriveClassName(typeRef.TypeId);
    }

    private static string DeriveClassName(Type clrType)
    {
        var typeName = GetClrTypeName(clrType);

        return DeriveClassNameFromTypeName(typeName);
    }

    private static string DeriveClassNameFromTypeName(string typeName)
    {

        if (typeName == "Resource")
        {
            return "_BaseResource";
        }

        // Strip leading 'I' from interface types
        if (typeName.StartsWith('I') && typeName.Length > 1 && char.IsUpper(typeName[1]))
        {
            return "Abstract" + typeName[1..];
        }

        return typeName;
    }

    /// <summary>
    /// Gets the handle type alias name for a type ID.
    /// </summary>
    private static string GetHandleTypeName(string typeId)
    {
        var typeName = ExtractSimpleTypeName(typeId);

        // Strip leading 'I' from interface types
        if (typeName.StartsWith('I') && typeName.Length > 1 && char.IsUpper(typeName[1]))
        {
            return "Abstract" + typeName[1..];
        }

        return $"{typeName}";
    }

    /// <summary>
    /// Extracts the simple type name from a type ID (e.g., "Aspire.Hosting/RedisResource" -> "RedisResource").
    /// </summary>
    private static string ExtractSimpleTypeName(string typeId)
    {
        var slashIndex = typeId.LastIndexOf('/');
        var fullTypeName = slashIndex >= 0 ? typeId[(slashIndex + 1)..] : typeId;

        var bracketIndex = fullTypeName.IndexOf('[');
        if (bracketIndex >= 0)
        {
            fullTypeName = fullTypeName[..bracketIndex];
        }

        var commaIndex = fullTypeName.IndexOf(',');
        if (commaIndex >= 0)
        {
            fullTypeName = fullTypeName[..commaIndex];
        }

        var dotIndex = fullTypeName.LastIndexOf('.');
        var simpleTypeName = dotIndex >= 0 ? fullTypeName[(dotIndex + 1)..] : fullTypeName;

        return SanitizePythonTypeName(StripGenericArity(simpleTypeName));
    }

    private static string GetClrTypeName(Type clrType)
    {
        if (clrType.IsArray)
        {
            return GetClrTypeName(clrType.GetElementType()!) + "Array";
        }

        var typeName = StripGenericArity(clrType.Name);
        if (clrType.IsGenericType)
        {
            return SanitizePythonTypeName(typeName + string.Concat(clrType.GetGenericArguments().Select(GetClrTypeName)));
        }

        return SanitizePythonTypeName(typeName);
    }

    private static string StripGenericArity(string typeName)
    {
        var backtickIndex = typeName.IndexOf('`');
        return backtickIndex >= 0 ? typeName[..backtickIndex] : typeName;
    }

    private static string SanitizePythonIdentifier(string identifier)
    {
        if (string.IsNullOrEmpty(identifier))
        {
            return identifier;
        }

        var sb = new System.Text.StringBuilder(identifier.Length + 1);
        var previousWasSeparator = false;

        foreach (var c in identifier)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                if (sb.Length == 0 && char.IsDigit(c))
                {
                    sb.Append('_');
                }

                sb.Append(c);
                previousWasSeparator = false;
            }
            else if (!previousWasSeparator && sb.Length > 0)
            {
                sb.Append('_');
                previousWasSeparator = true;
            }
        }

        var sanitized = sb.ToString().TrimEnd('_');
        if (string.IsNullOrEmpty(sanitized))
        {
            sanitized = "_";
        }

        if (s_pythonKeywords.Contains(sanitized))
        {
            sanitized += "_";
        }

        return sanitized;
    }

    private static string SanitizePythonTypeName(string typeName)
    {
        if (string.IsNullOrEmpty(typeName))
        {
            return typeName;
        }

        var sb = new System.Text.StringBuilder(typeName.Length + 1);

        foreach (var c in typeName)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                if (sb.Length == 0 && char.IsDigit(c))
                {
                    sb.Append('_');
                }

                sb.Append(c);
            }
        }

        var sanitized = sb.Length > 0 ? sb.ToString() : "_";
        if (s_pythonKeywords.Contains(sanitized))
        {
            sanitized += "_";
        }

        return sanitized;
    }

    private static string GetBuilderTypeId(AtsTypeRef typeRef)
    {
        if (typeRef.ClrType?.IsGenericType == true)
        {
            var tickIndex = typeRef.TypeId.IndexOf('`');
            if (tickIndex >= 0)
            {
                return typeRef.TypeId[..tickIndex] + "T";
            }

            return typeRef.TypeId + "T";
        }

        return typeRef.TypeId;
    }

    private List<OptionVariation> CreateOptionVariations(
        AtsCapabilityInfo capability,
        List<AtsParameterInfo> parameters,
        List<AtsParameterInfo> requiredParameters,
        List<AtsParameterInfo> optionalParameters)
    {
        var requiredParamsTypes = string.Join(", ", requiredParameters.Select(MapParameterToPython));
        var optionalParamsTypes = string.Join(", ", optionalParameters.Select(MapParameterToPython));
        var parameterMappingName = GetMethodParametersName(capability.MethodName);
        string? experimental = null; // TODO: get experimental tag
        var variations = new List<OptionVariation>();
        
        if (parameters.Count == 0)
        {
            variations.Add(new OptionVariation("typing.Literal[True]", requiredParameters, optionalParameters, experimental));
        }
        else if (parameters.Count == 1)
        {
            if (requiredParameters.Count == 1)
            {
                variations.Add(new OptionVariation(requiredParamsTypes, requiredParameters, optionalParameters, experimental));
            }
            else
            {
                variations.Add(new OptionVariation(optionalParamsTypes, requiredParameters, optionalParameters, experimental));
                variations.Add(new OptionVariation("typing.Literal[True]", requiredParameters, optionalParameters, experimental));
            }
        }
        else if (requiredParameters.Count == 1)
        {
            if (optionalParameters.Count == 1)
            {
                variations.Add(new OptionVariation(requiredParamsTypes, requiredParameters, optionalParameters, experimental));
                variations.Add(new OptionVariation($"({requiredParamsTypes}, {optionalParamsTypes})", requiredParameters, optionalParameters, experimental));
            }
            else
            {
                AddParameterMapping(parameterMappingName, requiredParameters, optionalParameters);
                variations.Add(new OptionVariation(requiredParamsTypes, requiredParameters, optionalParameters, experimental));
                variations.Add(new OptionVariation(parameterMappingName, requiredParameters, optionalParameters, experimental));
            }
        }
        else if (requiredParameters.Count > 0 && requiredParameters.Count <= 3)
        {
            if (optionalParameters.Count > 0)
            {
                AddParameterMapping(parameterMappingName, requiredParameters, optionalParameters);
                variations.Add(new OptionVariation("(" + requiredParamsTypes + ")", requiredParameters, optionalParameters, experimental));
                variations.Add(new OptionVariation(parameterMappingName, requiredParameters, optionalParameters, experimental));
            }
            else
            {
                variations.Add(new OptionVariation("(" + requiredParamsTypes + ")", requiredParameters, optionalParameters, experimental));
            }
        }
        else
        {
            AddParameterMapping(parameterMappingName, requiredParameters, optionalParameters);
            if (requiredParameters.Count == 0)
            {
                variations.Add(new OptionVariation(parameterMappingName, requiredParameters, optionalParameters, experimental));
                variations.Add(new OptionVariation("typing.Literal[True]", requiredParameters, optionalParameters, experimental));
            }
            else
            {
                variations.Add(new OptionVariation(parameterMappingName, requiredParameters, optionalParameters, experimental));
            }
        }

        return variations;
    }

    private void AddParameterMapping(string methodName, List<AtsParameterInfo> requiredParameters, List<AtsParameterInfo> optionalParameters)
    {
        if (_moduleBuilder.MethodParameters.ContainsKey(methodName))
        {
            return;
        }
        var parameters = new System.Text.StringBuilder();
        parameters.AppendLine();
        parameters.AppendLine(CultureInfo.InvariantCulture, $"class {methodName}(typing.TypedDict, total=False):");
        foreach (var requiredParam in requiredParameters)
        {
            parameters.AppendLine(CultureInfo.InvariantCulture, $"    {ToSnakeCase(requiredParam.Name!)}: typing.Required[{MapParameterToPython(requiredParam)}]");
        }
        foreach (var optionalParam in optionalParameters)
        {
            parameters.AppendLine(CultureInfo.InvariantCulture, $"    {ToSnakeCase(optionalParam.Name!)}: {MapParameterToPython(optionalParam)}");
        }
        _moduleBuilder.MethodParameters[methodName] = parameters;
    }

    /// <summary>
    /// Determines whether a specific option variation format will actually include
    /// the named parameter in the generated rpc_args dictionary.
    /// </summary>
    /// <remarks>
    /// All variations share the same requiredParameters/optionalParameters lists,
    /// but each format only populates a subset into rpc_args:
    /// <list type="bullet">
    ///   <item><c>typing.Literal[True]</c> — no params sent</item>
    ///   <item>Single type — only 1 param (first required, or first optional if none required)</item>
    ///   <item>Tuple — required params + optional if tuple has an extra slot</item>
    ///   <item>Parameters dict — all required and optional params</item>
    /// </list>
    /// </remarks>
    private static bool VariationIncludesParam(
        string optionType,
        List<AtsParameterInfo> requiredParams,
        List<AtsParameterInfo> optionalParams,
        string paramName)
    {
        if (optionType == "typing.Literal[True]")
        {
            return false;
        }

        // Required params are always included regardless of variation format
        if (requiredParams.Any(p => string.Equals(p.Name, paramName, StringComparison.Ordinal)))
        {
            return true;
        }

        // Dict/Parameters format includes all params (required + optional)
        if (optionType.EndsWith("Parameters"))
        {
            return optionalParams.Any(p => string.Equals(p.Name, paramName, StringComparison.Ordinal));
        }

        // Tuple format includes optional params only when there's exactly 1 optional
        // and the tuple has an extra slot for it
        if (optionType.StartsWith("("))
        {
            var tupleSlots = SplitTupleTypes(optionType.Trim('(', ')'));
            if (tupleSlots.Count == requiredParams.Count + 1 && optionalParams.Count == 1)
            {
                return optionalParams.Any(p => string.Equals(p.Name, paramName, StringComparison.Ordinal));
            }
            return false;
        }

        // Single param format: only 1 param is sent
        var singleParam = requiredParams.Count > 0 ? requiredParams[0] : optionalParams[0];
        return string.Equals(singleParam.Name, paramName, StringComparison.Ordinal);
    }

    private static void BuildOptionConstructor(
        System.Text.StringBuilder builder,
        AtsCapabilityInfo capability,
        string optionName,
        List<OptionVariation> variations,
        MergedCapabilityDispatch? mergedDispatch,
        int optionIndex = 0)
    {
        var variation = variations[optionIndex];
        var targetParamName = capability.TargetParameterName ?? "builder";
        var currentOption = variation.OptionType;
        var requiredParameters = variation.RequiredParameters;
        var optionalParameters = variation.OptionalParameters;
        var clause = "elif";
        var last = optionIndex == variations.Count - 1;
        if (optionIndex == 0)
        {
            // This will be a big if/elif/else clause, so we start with if on the first type variation.
            clause = "if";
            builder.AppendLine(CultureInfo.InvariantCulture, $"        if _{optionName} := kwargs.pop(\"{optionName}\", None):");
        }

        // Resolve the correct capability ID for this variation.
        // We must check whether this specific variation format will actually include the
        // discriminating parameter in rpc_args. Since the discriminating param is always
        // optional (made so by merging), we need to check whether the variation format
        // carries optional parameters.
        var variationCapabilityId = capability.CapabilityId;
        if (mergedDispatch is not null)
        {
            variationCapabilityId = VariationIncludesParam(currentOption, requiredParameters, optionalParameters, mergedDispatch.DiscriminatingParamName)
                ? mergedDispatch.AlternateCapabilityId
                : capability.CapabilityId;
        }

        if (currentOption == "typing.Literal[True]")
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"            {clause} _{optionName} is True:");
            builder.AppendLine(CultureInfo.InvariantCulture, $"                rpc_args: dict[str, typing.Any] = {{\"{targetParamName}\": handle}}");
            builder.AppendLine(CultureInfo.InvariantCulture, $"                handle = self._wrap_builder(client.invoke_capability('{variationCapabilityId}', rpc_args))");
        }
        else if (currentOption.EndsWith("Parameters"))
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"            {clause} _validate_dict_types(_{optionName}, {currentOption}):");
            builder.AppendLine(CultureInfo.InvariantCulture, $"                rpc_args: dict[str, typing.Any] = {{\"{targetParamName}\": handle}}");
            foreach (var param in requiredParameters)
            {
                var paramHandler = GetConstructorParamHandler(param, $"typing.cast({currentOption}, _{optionName})[\"{ToSnakeCase(param.Name!)}\"]");
                builder.AppendLine(CultureInfo.InvariantCulture, $"                rpc_args[\"{param.Name}\"] = {paramHandler}");
            }
            foreach (var param in optionalParameters)
            {
                var paramHandler = GetConstructorParamHandler(param, $"typing.cast({currentOption}, _{optionName}).get(\"{ToSnakeCase(param.Name!)}\")");
                builder.AppendLine(CultureInfo.InvariantCulture, $"                rpc_args[\"{param.Name}\"] = {paramHandler}");
            }
            // For dict variations with merged dispatch, emit a runtime check for the
            // discriminating parameter since the dict may or may not contain it.
            if (mergedDispatch is not null)
            {
                var snakeDiscrimParam = ToSnakeCase(mergedDispatch.DiscriminatingParamName);
                builder.AppendLine(CultureInfo.InvariantCulture, $"                capability_id = '{mergedDispatch.AlternateCapabilityId}' if \"{snakeDiscrimParam}\" in _{optionName} else '{capability.CapabilityId}'");
                builder.AppendLine(CultureInfo.InvariantCulture, $"                handle = self._wrap_builder(client.invoke_capability(capability_id, rpc_args))");
            }
            else
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"                handle = self._wrap_builder(client.invoke_capability('{variationCapabilityId}', rpc_args))");
            }
        }
        else if (currentOption.StartsWith("(")) // Tuple of parameters
        {
            var paramTypes = SplitTupleTypes(currentOption.Trim('(', ')'));
            builder.AppendLine(CultureInfo.InvariantCulture, $"            {clause} _validate_tuple_types(_{optionName}, {currentOption}):");
            builder.AppendLine(CultureInfo.InvariantCulture, $"                rpc_args: dict[str, typing.Any] = {{\"{targetParamName}\": handle}}");
            foreach (var (param, index) in requiredParameters.Select((item, index) => (item, index)))
            {
                var paramHandler = GetConstructorParamHandler(param, $"typing.cast(tuple[{currentOption.Trim('(', ')')}], _{optionName})[{index}]");
                builder.AppendLine(CultureInfo.InvariantCulture, $"                rpc_args[\"{param.Name}\"] = {paramHandler}");
            }
            if (paramTypes.Count == requiredParameters.Count + 1 && optionalParameters.Count == 1)
            {
                var param = optionalParameters[0];
                var paramHandler = GetConstructorParamHandler(param, $"typing.cast(tuple[{currentOption.Trim('(', ')')}], _{optionName})[{paramTypes.Count - 1}]");
                builder.AppendLine(CultureInfo.InvariantCulture, $"                rpc_args[\"{param.Name}\"] = {paramHandler}");
            }
            builder.AppendLine(CultureInfo.InvariantCulture, $"                handle = self._wrap_builder(client.invoke_capability('{variationCapabilityId}', rpc_args))");
        }
        else // Single parameter
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"            {clause} _validate_type(_{optionName}, {currentOption}):");
            builder.AppendLine(CultureInfo.InvariantCulture, $"                rpc_args: dict[str, typing.Any] = {{\"{targetParamName}\": handle}}");
            var singleParam = requiredParameters.Count > 0
                ? requiredParameters[0]
                : optionalParameters[0];
            var paramHandler = GetConstructorParamHandler(singleParam, $"typing.cast({currentOption}, _{optionName})");
            builder.AppendLine(CultureInfo.InvariantCulture, $"                rpc_args[\"{singleParam.Name}\"] = {paramHandler}");
            builder.AppendLine(CultureInfo.InvariantCulture, $"                handle = self._wrap_builder(client.invoke_capability('{variationCapabilityId}', rpc_args))");
        }
        if (last)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"            else:");
            builder.AppendLine(CultureInfo.InvariantCulture, $"                raise TypeError(\"Invalid type for option '{optionName}'. Expected: {String.Join(" or ", variations.Select(v => v.OptionType.Replace("typing.", "")))}\")");
        }
        else
        {
            BuildOptionConstructor(builder, capability, optionName, variations, mergedDispatch, optionIndex + 1);
        }

    }

    /// <summary>
    /// Generates a callback type signature for Python.
    /// </summary>
    private string GenerateCallbackTypeSignature(IReadOnlyList<AtsCallbackParameterInfo>? parameters, AtsTypeRef? returnType)
    {
        var paramTypes = parameters?.Select(p => MapTypeRefToPython(p.Type)).ToList() ?? [];
        var returnTypeStr = returnType != null ? MapTypeRefToPython(returnType) : "None";

        if (paramTypes.Count == 0)
        {
            return $"typing.Callable[[], {returnTypeStr}]";
        }

        return $"typing.Callable[[{string.Join(", ", paramTypes)}], {returnTypeStr}]";
    }

    /// <summary>
    /// Splits a tuple type string on ", " but respects brackets [ ] to avoid splitting inside nested types.
    /// For example: "(str, Callable[[], str])" splits into ["str", "Callable[[], str]"]
    /// </summary>
    private static List<string> SplitTupleTypes(string tupleContent)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        int bracketDepth = 0;

        for (int i = 0; i < tupleContent.Length; i++)
        {
            char c = tupleContent[i];

            if (c == '[')
            {
                bracketDepth++;
                current.Append(c);
            }
            else if (c == ']')
            {
                bracketDepth--;
                current.Append(c);
            }
            else if (c == ',' && bracketDepth == 0)
            {
                // Found a comma at the top level, check if followed by space
                if (i + 1 < tupleContent.Length && tupleContent[i + 1] == ' ')
                {
                    // This is our delimiter
                    result.Add(current.ToString());
                    current.Clear();
                    i++; // Skip the space after comma
                }
                else
                {
                    current.Append(c);
                }
            }
            else
            {
                current.Append(c);
            }
        }

        // Add the last segment
        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }
}
