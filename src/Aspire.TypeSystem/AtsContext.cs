// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;

namespace Aspire.TypeSystem;

/// <summary>
/// Contains all scanned types, capabilities, and metadata from ATS assembly scanning.
/// </summary>
/// <remarks>
/// <para>
/// The ATS type system has three distinct categories of types, each with different
/// serialization and code generation behavior:
/// </para>
/// <list type="bullet">
///   <item>
///     <term>Handle Types (<see cref="HandleTypes"/>)</term>
///     <description>
///     Types marked with [AspireExport]. These are passed by reference using opaque handles.
///     The TypeScript SDK generates wrapper classes that hold handles and proxy method calls
///     back to .NET. Examples: IDistributedApplicationBuilder, ContainerResource, EndpointReference.
///     </description>
///   </item>
///   <item>
///     <term>DTO Types (<see cref="DtoTypes"/>)</term>
///     <description>
///     Types marked with [AspireDto]. These are serialized as JSON objects and passed by value.
///     The TypeScript SDK generates interfaces matching the DTO's properties.
///     Examples: CreateBuilderOptions, ResourceSnapshot.
///     </description>
///   </item>
///   <item>
///     <term>Enum Types (<see cref="EnumTypes"/>)</term>
///     <description>
///     Enum types discovered in capability signatures. These are serialized as strings.
///     The TypeScript SDK generates TypeScript enums with string values.
///     </description>
///   </item>
/// </list>
/// </remarks>
public sealed class AtsContext
{
    private HashSet<Type>? _dtoTypes;
    private HashSet<Type>? _handleTypes;

    /// <summary>
    /// Gets the capabilities discovered during scanning.
    /// Capabilities are methods or properties marked with [AspireExport] that can be invoked via RPC.
    /// </summary>
    public required IReadOnlyList<AtsCapabilityInfo> Capabilities { get; init; }

    /// <summary>
    /// Gets the handle types discovered during scanning.
    /// These are types marked with [AspireExport] that are passed by reference using opaque handles.
    /// Code generators create wrapper classes for these types.
    /// </summary>
    public required IReadOnlyList<AtsTypeInfo> HandleTypes { get; init; }

    /// <summary>
    /// Gets the DTO types discovered during scanning.
    /// These are types marked with [AspireDto] that are serialized as JSON objects.
    /// Code generators create interfaces for these types.
    /// </summary>
    public required IReadOnlyList<AtsDtoTypeInfo> DtoTypes { get; init; }

    /// <summary>
    /// Gets the enum types discovered during scanning.
    /// These are enum types found in capability signatures, serialized as strings.
    /// Code generators create enum definitions for these types.
    /// </summary>
    public required IReadOnlyList<AtsEnumTypeInfo> EnumTypes { get; init; }

    /// <summary>
    /// Gets any diagnostics (warnings/errors) generated during scanning.
    /// </summary>
    public IReadOnlyList<AtsDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>
    /// Runtime registry mapping capability IDs to methods.
    /// </summary>
    public Dictionary<string, MethodInfo> Methods { get; } = new();

    /// <summary>
    /// Runtime registry mapping capability IDs to properties.
    /// </summary>
    public Dictionary<string, PropertyInfo> Properties { get; } = new();

    /// <summary>
    /// Gets the type category for a CLR type based on scanned data.
    /// Used at runtime for marshalling.
    /// </summary>
    /// <param name="type">The CLR type to classify.</param>
    /// <returns>The type category.</returns>
    public AtsTypeCategory GetCategory(Type type)
    {
        // Check primitives
        if (AtsConstants.IsPrimitiveType(type))
        {
            return AtsTypeCategory.Primitive;
        }

        // Check enums
        if (type.IsEnum)
        {
            return AtsTypeCategory.Enum;
        }

        // Check arrays
        if (type.IsArray)
        {
            return AtsTypeCategory.Array;
        }

        // Check generic collections
        if (type.IsGenericType)
        {
            var genericDef = type.GetGenericTypeDefinition();

            if (genericDef == typeof(List<>) || genericDef == typeof(IList<>))
            {
                return AtsTypeCategory.List;
            }

            if (genericDef == typeof(IReadOnlyList<>) || genericDef == typeof(IReadOnlyCollection<>) || genericDef == typeof(IEnumerable<>))
            {
                return AtsTypeCategory.Array;
            }

            if (genericDef == typeof(Dictionary<,>) || genericDef == typeof(IDictionary<,>) ||
                genericDef == typeof(IReadOnlyDictionary<,>))
            {
                return AtsTypeCategory.Dict;
            }
        }

        // Check delegates
        if (typeof(Delegate).IsAssignableFrom(type))
        {
            return AtsTypeCategory.Callback;
        }

        // Check scanned DTOs
        _dtoTypes ??= new HashSet<Type>(DtoTypes.Where(d => d.ClrType != null).Select(d => d.ClrType!));
        if (_dtoTypes.Contains(type))
        {
            return AtsTypeCategory.Dto;
        }

        // Check scanned handle types ([AspireExport] types passed by reference)
        _handleTypes ??= new HashSet<Type>(HandleTypes.Where(t => t.ClrType != null).Select(t => t.ClrType!));
        if (_handleTypes.Contains(type))
        {
            return AtsTypeCategory.Handle;
        }

        // Fallback to Handle for unknown reference types at runtime
        return AtsTypeCategory.Handle;
    }
}
