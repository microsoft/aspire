// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Frozen;

namespace Aspire.TypeSystem;

/// <summary>
/// Categories of ATS types for serialization and handling.
/// </summary>
public enum AtsTypeCategory
{
    /// <summary>
    /// Primitive types that serialize directly to JSON values.
    /// Examples: string, number, boolean, datetime, guid.
    /// </summary>
    Primitive,

    /// <summary>
    /// Enum types that serialize as string values.
    /// </summary>
    Enum,

    /// <summary>
    /// Handle types that are opaque references to .NET objects.
    /// Serialized as { "$handle": "type:id", "$type": "type" }.
    /// </summary>
    Handle,

    /// <summary>
    /// Data Transfer Objects that serialize as JSON objects.
    /// Must be marked with [AspireDto].
    /// </summary>
    Dto,

    /// <summary>
    /// Callback types (delegates) that are registered and invoked by ID.
    /// </summary>
    Callback,

    /// <summary>
    /// Readonly array/collection types that serialize as JSON arrays (copied).
    /// Examples: T[], IReadOnlyList&lt;T&gt;, IReadOnlyCollection&lt;T&gt;, IEnumerable&lt;T&gt;.
    /// </summary>
    Array,

    /// <summary>
    /// Mutable list types that are handles to .NET List&lt;T&gt;.
    /// </summary>
    List,

    /// <summary>
    /// Dictionary types that serialize as JSON objects.
    /// Mutable dictionaries are handles; readonly dictionaries are copied.
    /// </summary>
    Dict,

    /// <summary>
    /// Union types that can hold one of multiple alternative types.
    /// Serialization depends on the member types.
    /// </summary>
    Union,

    /// <summary>
    /// Unknown types that couldn't be resolved during the first pass of scanning.
    /// In Pass 2, these are either resolved to Handle (if the type is in the universe)
    /// or filtered out (if the type is not a valid ATS type).
    /// </summary>
    Unknown
}

/// <summary>
/// Kinds of ATS capabilities for code generation.
/// </summary>
public enum AtsCapabilityKind
{
    /// <summary>
    /// Regular extension method capability.
    /// </summary>
    Method,

    /// <summary>
    /// Property getter capability (from ExposeProperties=true).
    /// </summary>
    PropertyGetter,

    /// <summary>
    /// Property setter capability (from ExposeProperties=true).
    /// </summary>
    PropertySetter,

    /// <summary>
    /// Instance method capability (from ExposeMethods=true).
    /// </summary>
    InstanceMethod
}

/// <summary>
/// Constants for ATS (Aspire Type System) type IDs and capability IDs.
/// </summary>
public static class AtsConstants
{
    /// <summary>
    /// The Aspire.Hosting assembly name.
    /// </summary>
    public const string AspireHostingAssembly = "Aspire.Hosting";

    #region Primitive Type IDs

    /// <summary>
    /// String type ID. Maps from .NET <see cref="System.String"/>.
    /// </summary>
    public const string String = "string";

    /// <summary>
    /// Char type ID. Maps from .NET <see cref="System.Char"/>.
    /// Serializes to JSON string.
    /// </summary>
    public const string Char = "char";

    /// <summary>
    /// Number type ID. Maps from .NET numeric types (int, long, float, double, decimal, etc.).
    /// Serializes to JSON number.
    /// </summary>
    public const string Number = "number";

    /// <summary>
    /// Boolean type ID. Maps from .NET <see cref="System.Boolean"/>.
    /// </summary>
    public const string Boolean = "boolean";

    /// <summary>
    /// Void type ID. Represents no return value.
    /// </summary>
    public const string Void = "void";

    #endregion

    #region Date/Time Type IDs

    /// <summary>
    /// DateTime type ID. Maps from .NET <see cref="System.DateTime"/>.
    /// Serializes to JSON string (ISO 8601).
    /// </summary>
    public const string DateTime = "datetime";

    /// <summary>
    /// DateTimeOffset type ID. Maps from .NET <see cref="System.DateTimeOffset"/>.
    /// Serializes to JSON string (ISO 8601).
    /// </summary>
    public const string DateTimeOffset = "datetimeoffset";

    /// <summary>
    /// DateOnly type ID. Maps from .NET <see cref="System.DateOnly"/>.
    /// Serializes to JSON string (YYYY-MM-DD).
    /// </summary>
    public const string DateOnly = "dateonly";

    /// <summary>
    /// TimeOnly type ID. Maps from .NET <see cref="System.TimeOnly"/>.
    /// Serializes to JSON string (HH:mm:ss).
    /// </summary>
    public const string TimeOnly = "timeonly";

    /// <summary>
    /// TimeSpan type ID. Maps from .NET <see cref="System.TimeSpan"/>.
    /// Serializes to JSON number (total milliseconds).
    /// </summary>
    public const string TimeSpan = "timespan";

    #endregion

    #region Other Scalar Type IDs

    /// <summary>
    /// Guid type ID. Maps from .NET <see cref="System.Guid"/>.
    /// Serializes to JSON string.
    /// </summary>
    public const string Guid = "guid";

    /// <summary>
    /// Uri type ID. Maps from .NET <see cref="System.Uri"/>.
    /// Serializes to JSON string.
    /// </summary>
    public const string Uri = "uri";

    /// <summary>
    /// Any type ID. Maps from .NET <see cref="System.Object"/>.
    /// Accepts any supported ATS type. Use when a parameter needs to accept
    /// multiple types without explicit union declaration.
    /// </summary>
    public const string Any = "any";

    /// <summary>
    /// CancellationToken type ID. Maps from .NET <see cref="System.Threading.CancellationToken"/>.
    /// In TypeScript, maps to AbortSignal for cancellation support.
    /// </summary>
    public const string CancellationToken = "cancellationToken";

    /// <summary>
    /// Enum type ID prefix. Maps from .NET enum types.
    /// Full format: "enum:{FullTypeName}". Serializes to JSON string (enum name).
    /// </summary>
    public const string EnumPrefix = "enum:";

    #endregion

    #region Well-known Type IDs

    /// <summary>
    /// Type ID for IDistributedApplicationBuilder.
    /// </summary>
    public const string BuilderTypeId = "Aspire.Hosting/Aspire.Hosting.IDistributedApplicationBuilder";

    /// <summary>
    /// Type ID for DistributedApplication.
    /// </summary>
    public const string ApplicationTypeId = "Aspire.Hosting/Aspire.Hosting.DistributedApplication";

    /// <summary>
    /// Type ID for ReferenceExpression.
    /// </summary>
    public const string ReferenceExpressionTypeId = "Aspire.Hosting/Aspire.Hosting.ApplicationModel.ReferenceExpression";

    #endregion

    #region Well-known Capability IDs

    /// <summary>
    /// Capability ID for creating a builder.
    /// </summary>
    public const string CreateBuilderCapability = "Aspire.Hosting/createBuilder";

    /// <summary>
    /// Capability ID for building the application.
    /// </summary>
    public const string BuildCapability = "Aspire.Hosting/build";

    /// <summary>
    /// Capability ID for running the application.
    /// </summary>
    public const string RunCapability = "Aspire.Hosting/run";

    #endregion

    #region Collection Type ID Helpers

    /// <summary>
    /// Creates an array type ID for the given element type.
    /// </summary>
    /// <param name="elementType">The element type ID.</param>
    /// <returns>The array type ID.</returns>
    public static string ArrayTypeId(string elementType) => $"{elementType}[]";

    /// <summary>
    /// Creates a List type ID for the given element type.
    /// </summary>
    /// <param name="elementType">The element type ID.</param>
    /// <returns>The List type ID.</returns>
    public static string ListTypeId(string elementType) => $"{AspireHostingAssembly}/List<{elementType}>";

    /// <summary>
    /// Creates a Dict type ID for the given key and value types.
    /// </summary>
    /// <param name="keyType">The key type ID.</param>
    /// <param name="valueType">The value type ID.</param>
    /// <returns>The Dict type ID.</returns>
    public static string DictTypeId(string keyType, string valueType) => $"{AspireHostingAssembly}/Dict<{keyType},{valueType}>";

    /// <summary>
    /// Creates an enum type ID for the given enum type full name.
    /// </summary>
    /// <param name="enumFullName">The full name of the enum type.</param>
    /// <returns>The enum type ID.</returns>
    public static string EnumTypeId(string enumFullName) => $"{EnumPrefix}{enumFullName}";

    /// <summary>
    /// Checks if a type ID represents an enum type.
    /// </summary>
    /// <param name="typeId">The ATS type ID to check.</param>
    /// <returns>True if the type is an enum.</returns>
    public static bool IsEnum(string? typeId) => typeId?.StartsWith(EnumPrefix, StringComparison.Ordinal) == true;

    /// <summary>
    /// Checks if a type ID represents an array type.
    /// </summary>
    /// <param name="typeId">The ATS type ID to check.</param>
    /// <returns>True if the type is an array.</returns>
    public static bool IsArray(string? typeId) => typeId?.EndsWith("[]", StringComparison.Ordinal) == true;

    /// <summary>
    /// Checks if a type ID represents a List type.
    /// </summary>
    /// <param name="typeId">The ATS type ID to check.</param>
    /// <returns>True if the type is a List.</returns>
    public static bool IsList(string? typeId) => typeId?.Contains("/List<", StringComparison.Ordinal) == true;

    /// <summary>
    /// Checks if a type ID represents a Dict type.
    /// </summary>
    /// <param name="typeId">The ATS type ID to check.</param>
    /// <returns>True if the type is a Dict.</returns>
    public static bool IsDict(string? typeId) => typeId?.Contains("/Dict<", StringComparison.Ordinal) == true;

    #endregion

    #region Type Category Helpers

    /// <summary>
    /// Checks if a type ID represents a primitive type.
    /// </summary>
    /// <param name="typeId">The ATS type ID to check.</param>
    /// <returns>True if the type is a primitive.</returns>
    public static bool IsPrimitive(string? typeId) => typeId switch
    {
        String or Char or Number or Boolean or Void => true,
        DateTime or DateTimeOffset or DateOnly or TimeOnly or TimeSpan => true,
        Guid or Uri or Any or CancellationToken => true,
        _ => false
    };

    /// <summary>
    /// Checks if a type ID represents a handle type (has Assembly/Type format).
    /// </summary>
    /// <param name="typeId">The ATS type ID to check.</param>
    /// <returns>True if the type is a handle.</returns>
    public static bool IsHandle(string? typeId)
    {
        if (string.IsNullOrEmpty(typeId))
        {
            return false;
        }

        // Primitives are not handles
        if (IsPrimitive(typeId))
        {
            return false;
        }

        // Handle types have the format {AssemblyName}/{TypeName}
        return typeId.Contains('/');
    }

    /// <summary>
    /// Gets the type category for a type ID.
    /// Note: This method cannot distinguish DTOs from Handles based on type ID alone.
    /// Use the scanner's type mapping to determine if a handle type is actually a DTO.
    /// </summary>
    /// <param name="typeId">The ATS type ID.</param>
    /// <param name="isCallback">True if this is a callback parameter.</param>
    /// <returns>The type category.</returns>
    public static AtsTypeCategory GetCategory(string? typeId, bool isCallback = false)
    {
        if (isCallback)
        {
            return AtsTypeCategory.Callback;
        }

        if (IsPrimitive(typeId))
        {
            return AtsTypeCategory.Primitive;
        }

        if (IsEnum(typeId))
        {
            return AtsTypeCategory.Enum;
        }

        if (IsArray(typeId))
        {
            return AtsTypeCategory.Array;
        }

        if (IsList(typeId))
        {
            return AtsTypeCategory.List;
        }

        if (IsDict(typeId))
        {
            return AtsTypeCategory.Dict;
        }

        // Union types are explicitly created by the scanner when [AspireUnion] is present.
        // They are not inferred from type ID format.

        // For handle-format types, we default to Handle.
        // The scanner/runtime can override this to Dto if the type has [AspireDto].
        return AtsTypeCategory.Handle;
    }

    #endregion

    #region Type-based Classification

    /// <summary>
    /// Set of CLR types that map to ATS primitive types.
    /// </summary>
    private static readonly FrozenSet<Type> s_primitiveTypes = new HashSet<Type>
    {
        // Core primitives
        typeof(string),
        typeof(char),
        typeof(bool),

        // Numeric types (all map to "number")
        typeof(byte),
        typeof(sbyte),
        typeof(short),
        typeof(ushort),
        typeof(int),
        typeof(uint),
        typeof(long),
        typeof(ulong),
        typeof(float),
        typeof(double),
        typeof(decimal),

        // Date/time types
        typeof(DateTime),
        typeof(DateTimeOffset),
        typeof(DateOnly),
        typeof(TimeOnly),
        typeof(TimeSpan),

        // Other scalar types
        typeof(Guid),
        typeof(Uri),
        typeof(CancellationToken),
    }.ToFrozenSet();

    /// <summary>
    /// Checks if a CLR type is a primitive ATS type.
    /// </summary>
    /// <param name="type">The CLR type to check.</param>
    /// <returns>True if the type is a primitive.</returns>
    public static bool IsPrimitiveType(Type type) => s_primitiveTypes.Contains(type);

    /// <summary>
    /// Gets the type category for a CLR type.
    /// </summary>
    /// <param name="type">The CLR type.</param>
    /// <returns>The type category.</returns>
    public static AtsTypeCategory GetCategory(Type type)
    {
        if (s_primitiveTypes.Contains(type))
        {
            return AtsTypeCategory.Primitive;
        }

        if (type.IsEnum)
        {
            return AtsTypeCategory.Enum;
        }

        if (type.IsArray)
        {
            return AtsTypeCategory.Array;
        }

        if (IsListType(type))
        {
            return AtsTypeCategory.List;
        }

        if (IsReadOnlyListType(type))
        {
            return AtsTypeCategory.Array; // ReadOnly collections serialize as arrays
        }

        if (IsDictType(type))
        {
            return AtsTypeCategory.Dict;
        }

        if (typeof(Delegate).IsAssignableFrom(type))
        {
            return AtsTypeCategory.Callback;
        }

        // Check for [AspireDto] attribute
        if (AttributeDataReader.HasAspireDtoData(type))
        {
            return AtsTypeCategory.Dto;
        }

        // Check for [AspireExport] attribute - these are handle types
        if (AttributeDataReader.GetAspireExportData(type) != null)
        {
            return AtsTypeCategory.Handle;
        }

        // Unknown types - not recognized as valid ATS types
        return AtsTypeCategory.Unknown;
    }

    /// <summary>
    /// Checks if a type is a mutable List type.
    /// </summary>
    private static bool IsListType(Type type)
    {
        if (!type.IsGenericType)
        {
            return false;
        }

        var genericDef = type.GetGenericTypeDefinition();
        return genericDef == typeof(List<>) ||
               genericDef == typeof(IList<>);
    }

    /// <summary>
    /// Checks if a type is a readonly list/collection type.
    /// </summary>
    private static bool IsReadOnlyListType(Type type)
    {
        if (!type.IsGenericType)
        {
            return false;
        }

        var genericDef = type.GetGenericTypeDefinition();
        return genericDef == typeof(IReadOnlyList<>) ||
               genericDef == typeof(IReadOnlyCollection<>) ||
               genericDef == typeof(IEnumerable<>);
    }

    /// <summary>
    /// Checks if a type is a dictionary type.
    /// </summary>
    private static bool IsDictType(Type type)
    {
        if (!type.IsGenericType)
        {
            return false;
        }

        var genericDef = type.GetGenericTypeDefinition();
        return genericDef == typeof(Dictionary<,>) ||
               genericDef == typeof(IDictionary<,>) ||
               genericDef == typeof(IReadOnlyDictionary<,>);
    }

    /// <summary>
    /// Checks if a dictionary type is readonly.
    /// </summary>
    public static bool IsReadOnlyDictType(Type type)
    {
        if (!type.IsGenericType)
        {
            return false;
        }

        var genericDef = type.GetGenericTypeDefinition();
        return genericDef == typeof(IReadOnlyDictionary<,>);
    }

    #endregion
}
