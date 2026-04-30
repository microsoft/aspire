// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.TypeSystem;

/// <summary>
/// Lightweight type reference with category and interface flag.
/// Used for parameter types and return types in capabilities.
/// </summary>
public sealed class AtsTypeRef
{
    /// <summary>
    /// Gets or sets the ATS type ID (e.g., "string", "Aspire.Hosting/RedisResource").
    /// </summary>
    public required string TypeId { get; init; }

    /// <summary>
    /// Gets or sets the CLR type reference for direct type access.
    /// </summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)]
    public Type? ClrType { get; init; }

    /// <summary>
    /// Gets or sets the type category (Primitive, Handle, Dto, Callback, Array, List, Dict, Unknown).
    /// Note: This is mutable to allow Pass 2 resolution of Unknown types to Handle.
    /// </summary>
    public AtsTypeCategory Category { get; set; }

    /// <summary>
    /// Gets or sets whether this is an interface type.
    /// Only meaningful for Handle category types.
    /// </summary>
    public bool IsInterface { get; init; }

    /// <summary>
    /// Gets or sets the element type reference for Array/List types.
    /// </summary>
    public AtsTypeRef? ElementType { get; init; }

    /// <summary>
    /// Gets or sets the key type reference for Dict types.
    /// </summary>
    public AtsTypeRef? KeyType { get; init; }

    /// <summary>
    /// Gets or sets the value type reference for Dict types.
    /// </summary>
    public AtsTypeRef? ValueType { get; init; }

    /// <summary>
    /// Gets or sets whether this is a readonly collection (copied, not a handle).
    /// Only meaningful for Array/Dict categories.
    /// </summary>
    public bool IsReadOnly { get; init; }

    /// <summary>
    /// Gets whether this type represents a resource builder target type.
    /// Computed from ClrType - true for types that implement the Aspire resource contract.
    /// </summary>
    public bool IsResourceBuilder => HostingTypeHelpers.IsResourceType(ClrType);

    /// <summary>
    /// Gets whether this type is IDistributedApplicationBuilder.
    /// </summary>
    public bool IsDistributedApplicationBuilder => HostingTypeHelpers.IsDistributedApplicationBuilderType(ClrType);

    /// <summary>
    /// Gets whether this type is DistributedApplication.
    /// </summary>
    public bool IsDistributedApplication => HostingTypeHelpers.IsDistributedApplicationType(ClrType);

    /// <summary>
    /// Gets or sets the member types for Union category.
    /// When Category = Union, this contains the alternative types.
    /// </summary>
    public IReadOnlyList<AtsTypeRef>? UnionTypes { get; init; }

    /// <summary>
    /// Gets or sets the interfaces this type implements.
    /// </summary>
    /// <remarks>
    /// Pre-computed during type scanning so generators can consume interface information from the shared ATS model
    /// without performing additional reflection.
    /// Currently used by Python code generation for interface inheritance and capability placement.
    /// Only meaningful for Handle category types.
    /// </remarks>
    public IReadOnlyList<AtsTypeRef> ImplementedInterfaces { get; init; } = [];

    /// <summary>
    /// Gets or sets the immediate base type of this type.
    /// </summary>
    /// <remarks>
    /// Pre-computed during type scanning so generators can traverse the type hierarchy from the shared ATS model
    /// without performing additional reflection.
    /// Currently used by Python code generation when mapping inherited members.
    /// Returns <see langword="null"/> for interface types or for types with no relevant base class.
    /// </remarks>
    public AtsTypeRef? BaseType { get; init; }
}

/// <summary>
/// Represents the severity of an ATS scanner diagnostic.
/// </summary>
public enum AtsDiagnosticSeverity
{
    /// <summary>
    /// Information - verbose details about scanning (shown with --debug).
    /// </summary>
    Info,

    /// <summary>
    /// Warning - the item was skipped but scanning continues.
    /// </summary>
    Warning,

    /// <summary>
    /// Error - a type validation error (e.g., object type without [AspireUnion]).
    /// </summary>
    Error
}

/// <summary>
/// Represents a diagnostic message from the ATS capability scanner.
/// </summary>
public sealed class AtsDiagnostic
{
    /// <summary>
    /// Gets the severity of the diagnostic.
    /// </summary>
    public AtsDiagnosticSeverity Severity { get; init; }

    /// <summary>
    /// Gets the diagnostic message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Gets the source location (e.g., type name, method name).
    /// </summary>
    public string? Location { get; init; }

    /// <summary>
    /// Creates an error diagnostic.
    /// </summary>
    public static AtsDiagnostic Error(string message, string? location = null) =>
        new() { Severity = AtsDiagnosticSeverity.Error, Message = message, Location = location };

    /// <summary>
    /// Creates a warning diagnostic.
    /// </summary>
    public static AtsDiagnostic Warning(string message, string? location = null) =>
        new() { Severity = AtsDiagnosticSeverity.Warning, Message = message, Location = location };

    /// <summary>
    /// Creates an info diagnostic (verbose output shown with --debug).
    /// </summary>
    public static AtsDiagnostic Info(string message, string? location = null) =>
        new() { Severity = AtsDiagnosticSeverity.Info, Message = message, Location = location };
}

/// <summary>
/// Represents a discovered [AspireExport] capability.
/// </summary>
/// <remarks>
/// <para>
/// This model is shared between code generation (using RoMethod/RoType metadata reflection)
/// and the runtime CapabilityDispatcher (using System.Reflection).
/// </para>
/// <para>
/// The capability represents a single exportable method that can be invoked via
/// <c>invokeCapability(capabilityId, args)</c> from polyglot clients.
/// </para>
/// </remarks>
public sealed class AtsCapabilityInfo
{
    /// <summary>
    /// Gets or sets the capability ID (e.g., "Aspire.Hosting/addRedis").
    /// </summary>
    public required string CapabilityId { get; init; }

    /// <summary>
    /// Gets or sets the simple method name for generated SDKs (e.g., "addRedis", "isRunMode").
    /// For context type capabilities, this is just the property/method name without the type prefix.
    /// </summary>
    public required string MethodName { get; set; }

    /// <summary>
    /// Gets or sets the owning type name for property/method capabilities.
    /// </summary>
    /// <remarks>
    /// For PropertyGetter, PropertySetter, and InstanceMethod capabilities, this is the
    /// type name that owns the property/method (e.g., "ExecutionContext").
    /// For regular Method capabilities, this is null.
    /// </remarks>
    public string? OwningTypeName { get; init; }

    /// <summary>
    /// Gets the qualified method name combining OwningTypeName and MethodName.
    /// </summary>
    /// <remarks>
    /// Returns "ExecutionContext.isRunMode" for property capabilities,
    /// or just "addRedis" for regular method capabilities.
    /// </remarks>
    public string QualifiedMethodName => OwningTypeName is not null
        ? $"{OwningTypeName}.{MethodName}"
        : MethodName;

    /// <summary>
    /// Gets or sets the description of what this capability does.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets or sets whether this capability is obsolete.
    /// </summary>
    public bool IsObsolete { get; init; }

    /// <summary>
    /// Gets or sets the obsolete message for this capability.
    /// </summary>
    public string? ObsoleteMessage { get; init; }

    /// <summary>
    /// Gets or sets the parameters for this capability.
    /// </summary>
    public required IReadOnlyList<AtsParameterInfo> Parameters { get; init; }

    /// <summary>
    /// Gets or sets the return type reference with full type metadata.
    /// Use <see cref="AtsConstants.Void"/> TypeId for void return types.
    /// </summary>
    public required AtsTypeRef ReturnType { get; init; }

    /// <summary>
    /// Gets or sets the original (declared) ATS type ID that this capability targets.
    /// May be an interface type (e.g., "Aspire.Hosting/IResourceWithEnvironment").
    /// </summary>
    public string? TargetTypeId { get; init; }

    /// <summary>
    /// Gets or sets the target type reference with full type metadata.
    /// </summary>
    public AtsTypeRef? TargetType { get; init; }

    /// <summary>
    /// Gets or sets the name of the target parameter (e.g., "builder", "resource").
    /// This is the first parameter of the method that represents the target/receiver.
    /// </summary>
    public string? TargetParameterName { get; init; }

    /// <summary>
    /// Gets or sets the expanded list of concrete types this capability applies to.
    /// Pre-computed during scanning by resolving interface targets to all implementing types.
    /// </summary>
    /// <remarks>
    /// For flat codegen (Go, C): use this to put methods on each concrete builder.
    /// For inheritance codegen (TypeScript, Java): use <see cref="TargetTypeId"/> instead.
    /// </remarks>
    public IReadOnlyList<AtsTypeRef> ExpandedTargetTypes { get; set; } = [];

    /// <summary>
    /// Gets or sets whether the return type is a builder type.
    /// </summary>
    public bool ReturnsBuilder { get; init; }

    /// <summary>
    /// Gets or sets the kind of capability (Method, PropertyGetter, PropertySetter, InstanceMethod).
    /// </summary>
    public AtsCapabilityKind CapabilityKind { get; init; }

    /// <summary>
    /// Gets or sets the source location where this capability is defined.
    /// </summary>
    /// <remarks>
    /// Format: "TypeName.MethodName" or "TypeName.PropertyName" for diagnostics.
    /// </remarks>
    public string? SourceLocation { get; init; }

    /// <summary>
    /// Gets or sets whether synchronous invocations of this capability should run on a background thread.
    /// </summary>
    public bool RunSyncOnBackgroundThread { get; init; }
}

/// <summary>
/// Represents a parameter in an ATS capability.
/// </summary>
public sealed class AtsParameterInfo
{
    /// <summary>
    /// Gets or sets the parameter name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets or sets the type reference with full type metadata.
    /// </summary>
    public AtsTypeRef? Type { get; init; }

    /// <summary>
    /// Gets or sets whether this parameter is optional.
    /// </summary>
    public bool IsOptional { get; init; }

    /// <summary>
    /// Gets or sets whether this parameter is nullable.
    /// </summary>
    public bool IsNullable { get; init; }

    /// <summary>
    /// Gets or sets whether this parameter is a callback delegate.
    /// Callbacks are inferred from delegate types (Func, Action, custom delegates).
    /// </summary>
    public bool IsCallback { get; init; }

    /// <summary>
    /// Gets or sets the parameters of the callback delegate.
    /// Only populated when <see cref="IsCallback"/> is true.
    /// </summary>
    public IReadOnlyList<AtsCallbackParameterInfo>? CallbackParameters { get; init; }

    /// <summary>
    /// Gets or sets the return type for the callback delegate.
    /// Only populated when <see cref="IsCallback"/> is true.
    /// </summary>
    public AtsTypeRef? CallbackReturnType { get; init; }

    /// <summary>
    /// Gets or sets the default value for optional parameters.
    /// </summary>
    public object? DefaultValue { get; init; }
}

/// <summary>
/// Represents a parameter in a callback delegate signature.
/// </summary>
public sealed class AtsCallbackParameterInfo
{
    /// <summary>
    /// Gets or sets the parameter name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets or sets the type reference for this parameter.
    /// </summary>
    public required AtsTypeRef Type { get; init; }
}

/// <summary>
/// Represents type information discovered from [AspireExport(AtsTypeId = "...")].
/// </summary>
public sealed class AtsTypeInfo
{
    /// <summary>
    /// Gets or sets the ATS type ID.
    /// </summary>
    public required string AtsTypeId { get; init; }

    /// <summary>
    /// Gets or sets the CLR type reference for direct type access.
    /// </summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)]
    public Type? ClrType { get; init; }

    /// <summary>
    /// Gets or sets whether this type is an interface.
    /// </summary>
    public bool IsInterface { get; init; }

    /// <summary>
    /// Gets or sets the interfaces this type implements.
    /// Only populated for concrete (non-interface) types.
    /// </summary>
    public IReadOnlyList<AtsTypeRef> ImplementedInterfaces { get; init; } = [];

    /// <summary>
    /// Gets or sets the base type hierarchy (from immediate base up to Resource/Object).
    /// Only populated for concrete (non-interface) types.
    /// Used for expanding capabilities targeting base types to derived types.
    /// </summary>
    public IReadOnlyList<AtsTypeRef> BaseTypeHierarchy { get; init; } = [];

    /// <summary>
    /// Gets or sets whether this type has [AspireExport(ExposeProperties = true)].
    /// Types with this flag will have their properties exposed as capabilities.
    /// </summary>
    public bool HasExposeProperties { get; init; }

    /// <summary>
    /// Gets or sets whether this type has [AspireExport(ExposeMethods = true)].
    /// Types with this flag will have their methods exposed as capabilities.
    /// </summary>
    public bool HasExposeMethods { get; init; }

    /// <summary>
    /// Gets whether this handle type represents an Aspire resource type.
    /// </summary>
    public bool IsResourceBuilder => HostingTypeHelpers.IsResourceType(ClrType);
}

/// <summary>
/// Represents a DTO type discovered from [AspireDto] attributes.
/// Used for generating TypeScript interfaces for DTOs.
/// </summary>
public sealed class AtsDtoTypeInfo
{
    /// <summary>
    /// Gets or sets the ATS type ID for this DTO.
    /// </summary>
    public required string TypeId { get; init; }

    /// <summary>
    /// Gets or sets the simple type name (for interface name generation).
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets or sets a description of this DTO type.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets or sets the CLR type reference for direct type access.
    /// </summary>
    public Type? ClrType { get; init; }

    /// <summary>
    /// Gets or sets the properties of this DTO.
    /// </summary>
    public required IReadOnlyList<AtsDtoPropertyInfo> Properties { get; init; }
}

/// <summary>
/// Represents a property of a DTO type.
/// </summary>
public sealed class AtsDtoPropertyInfo
{
    /// <summary>
    /// Gets or sets the property name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets or sets the property type reference.
    /// </summary>
    public required AtsTypeRef Type { get; init; }

    /// <summary>
    /// Gets or sets a description of this property.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets or sets whether this property is optional (nullable or has default).
    /// </summary>
    public bool IsOptional { get; init; }
}

/// <summary>
/// Represents an enum type discovered during scanning.
/// Used for generating TypeScript enums.
/// </summary>
public sealed class AtsEnumTypeInfo
{
    /// <summary>
    /// Gets or sets the ATS type ID for this enum (e.g., "enum:Aspire.Hosting.ApplicationModel.ContainerLifetime").
    /// </summary>
    public required string TypeId { get; init; }

    /// <summary>
    /// Gets or sets the simple type name (for enum name generation).
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets or sets the CLR type reference for direct type access.
    /// </summary>
    public Type? ClrType { get; init; }

    /// <summary>
    /// Gets or sets the enum member names.
    /// </summary>
    public required IReadOnlyList<string> Values { get; init; }
}
