// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.CodeGeneration.Go;

/// <summary>
/// The kinds of top-level items in the Go API export.
/// </summary>
internal enum GoApiItemKind
{
    /// <summary>
    /// A generated handle interface, including marker interfaces that only embed the handle contract.
    /// </summary>
    Interface,

    /// <summary>
    /// The members a package contributes to a handle interface that another package owns.
    /// </summary>
    Augmentation,

    /// <summary>
    /// A generated struct for an ATS data-transfer object.
    /// </summary>
    Dto,

    /// <summary>
    /// A generated <c>XxxOptions</c> struct that carries a capability's optional parameters.
    /// </summary>
    Options,

    /// <summary>
    /// A generated string-backed enum and its constants.
    /// </summary>
    Enum,

    /// <summary>
    /// A package-level variable that exposes an exported value tree.
    /// </summary>
    Variable,

    /// <summary>
    /// A package-level function, such as <c>CreateBuilder</c>.
    /// </summary>
    Function,

    /// <summary>
    /// A type from the hand-written runtime (<c>base.go</c> and <c>transport.go</c>).
    /// </summary>
    Type
}

/// <summary>
/// The kinds of members an item can carry.
/// </summary>
internal enum GoApiMemberKind
{
    /// <summary>
    /// A method on a handle interface, DTO or options struct.
    /// </summary>
    Method,

    /// <summary>
    /// A struct field, or a leaf of an exported value tree.
    /// </summary>
    Field,

    /// <summary>
    /// A constant, such as an enum value.
    /// </summary>
    Constant
}

/// <summary>
/// How a generated method reports failure to its caller.
/// </summary>
internal enum GoApiErrorModel
{
    /// <summary>
    /// The method cannot fail through its own result.
    /// </summary>
    None,

    /// <summary>
    /// The failure is part of the result: <c>error</c> or <c>(T, error)</c>.
    /// </summary>
    Returned,

    /// <summary>
    /// The method returns a handle immediately and the failure is read later through <c>Err()</c>.
    /// </summary>
    Deferred
}

/// <summary>
/// The package an export was produced for.
/// </summary>
/// <param name="Name">The package name.</param>
/// <param name="Version">The exact package version.</param>
/// <param name="ImportPath">The Go module path of the generated SDK that the declarations belong to.</param>
internal sealed record GoApiPackageIdentity(string Name, string Version, string ImportPath);

/// <summary>
/// The generator that produced an export.
/// </summary>
/// <param name="Name">The generator assembly name.</param>
/// <param name="Version">The generator assembly informational version.</param>
internal sealed record GoApiGeneratorIdentity(string Name, string Version);

/// <summary>
/// A parameter of a generated method, or a field of an options struct.
/// </summary>
internal sealed record GoApiParameter
{
    /// <summary>
    /// Gets the Go identifier of the parameter or field.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the Go type exactly as the generator renders it.
    /// </summary>
    public required string DeclaredType { get; init; }

    /// <summary>
    /// Gets a value indicating whether the value is optional. Optional parameters live in the trailing
    /// <c>options</c> variadic rather than in the positional parameter list.
    /// </summary>
    public bool IsOptional { get; init; }

    /// <summary>
    /// Gets a value indicating whether the value is a callback, which Go renders as a function type.
    /// </summary>
    public bool IsCallback { get; init; }

    /// <summary>
    /// Gets the name used on the wire, when it differs from <see cref="Name"/>.
    /// </summary>
    public string? JsonName { get; init; }

    /// <summary>
    /// Gets the documentation summary.
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>
    /// Gets the Go types a union parameter accepts, as the generated guard lists them. A handle type that has
    /// derived types is listed as its <c>Any&lt;Type&gt;</c> interface. The generated parameter itself is <c>any</c>.
    /// </summary>
    public IReadOnlyList<string> AllowedTypes { get; init; } = [];
}

/// <summary>
/// The trailing <c>options ...*T</c> variadic of a generated method.
/// </summary>
internal sealed record GoApiOptions
{
    /// <summary>
    /// Gets the Go type name of the options value.
    /// </summary>
    public required string TypeName { get; init; }

    /// <summary>
    /// Gets a value indicating whether the variadic carries a DTO directly instead of a generated
    /// <c>XxxOptions</c> struct. Go allows a single trailing variadic, so only a lone optional DTO
    /// named <c>options</c> can be flattened this way.
    /// </summary>
    public bool IsDirect { get; init; }

    /// <summary>
    /// Gets the options fields. Empty for direct options, whose fields are the DTO's own.
    /// </summary>
    public IReadOnlyList<GoApiParameter> Fields { get; init; } = [];
}

/// <summary>
/// A method, field or constant on an <see cref="GoApiItem"/>.
/// </summary>
internal sealed record GoApiMember
{
    /// <summary>
    /// Gets the member identifier, unique within its item.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the member kind.
    /// </summary>
    public required GoApiMemberKind Kind { get; init; }

    /// <summary>
    /// Gets the Go identifier.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the declaration exactly as the generator renders it, without a doc comment or a body.
    /// </summary>
    public required string Declaration { get; init; }

    /// <summary>
    /// Gets the documentation summary.
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>
    /// Gets the documentation remarks.
    /// </summary>
    public string? Remarks { get; init; }

    /// <summary>
    /// Gets the documentation of the returned value.
    /// </summary>
    public string? Returns { get; init; }

    /// <summary>
    /// Gets a value indicating whether the member is experimental.
    /// </summary>
    public bool IsExperimental { get; init; }

    /// <summary>
    /// Gets the deprecation message, or <see langword="null"/> when the member is not deprecated.
    /// </summary>
    public string? DeprecationMessage { get; init; }

    /// <summary>
    /// Gets the ATS capability behind the member. Members with no capability behind them are
    /// <see cref="IsSynthesized"/>.
    /// </summary>
    public string? CapabilityId { get; init; }

    /// <summary>
    /// Gets the ATS type the capability targets before it is expanded to concrete handle types.
    /// </summary>
    public string? TargetTypeId { get; init; }

    /// <summary>
    /// Gets how the capability is exposed: <c>method</c>, <c>instanceMethod</c>,
    /// <c>propertyGetter</c> or <c>propertySetter</c>.
    /// </summary>
    public string? CapabilityKind { get; init; }

    /// <summary>
    /// Gets the assembly that owns the capability.
    /// </summary>
    public string? OwningAssemblyName { get; init; }

    /// <summary>
    /// Gets the positional parameters, excluding the receiver and the trailing options.
    /// </summary>
    public IReadOnlyList<GoApiParameter> Parameters { get; init; } = [];

    /// <summary>
    /// Gets the trailing options variadic, when the capability has optional parameters.
    /// </summary>
    public GoApiOptions? Options { get; init; }

    /// <summary>
    /// Gets the Go result type exactly as the generator renders it, such as <c>error</c>,
    /// <c>(string, error)</c> or a handle interface name.
    /// </summary>
    public string? ReturnType { get; init; }

    /// <summary>
    /// Gets how the method reports failure.
    /// </summary>
    public GoApiErrorModel? ErrorModel { get; init; }

    /// <summary>
    /// Gets a value indicating whether the method returns its receiver for fluent chaining.
    /// </summary>
    public bool ReturnsReceiver { get; init; }

    /// <summary>
    /// Gets a value indicating whether the capability returns a resource builder.
    /// </summary>
    public bool ReturnsBuilder { get; init; }

    /// <summary>
    /// Gets a value indicating whether the member exists only in the Go SDK: it has no ATS capability
    /// behind it. Examples are <c>Build</c>, <c>Err</c>, <c>ToMap</c> and the
    /// <c>InteractionInputCollection</c> by-name helpers.
    /// </summary>
    public bool IsSynthesized { get; init; }

    /// <summary>
    /// Gets the JSON property name of a field.
    /// </summary>
    public string? JsonName { get; init; }

    /// <summary>
    /// Gets the value of a constant, or the Go literal of an exported value.
    /// </summary>
    public string? Value { get; init; }
}

/// <summary>
/// A top-level documented symbol: a handle interface, struct, enum, variable, function or runtime type.
/// </summary>
internal sealed record GoApiItem
{
    /// <summary>
    /// Gets the item identifier, unique within the export.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the ATS identifier the item was generated from, or an item-scoped identifier when no ATS
    /// type backs it.
    /// </summary>
    public required string TypeId { get; init; }

    /// <summary>
    /// Gets the item kind.
    /// </summary>
    public required GoApiItemKind Kind { get; init; }

    /// <summary>
    /// Gets the Go identifier.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the declaration header, such as <c>type Redis interface</c>.
    /// </summary>
    public required string Declaration { get; init; }

    /// <summary>
    /// Gets the assembly that owns the symbol. For an augmentation this is the owner of the augmented
    /// type, not the package that contributes the members.
    /// </summary>
    public required string OwningAssemblyName { get; init; }

    /// <summary>
    /// Gets the documentation summary.
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>
    /// Gets the documentation remarks.
    /// </summary>
    public string? Remarks { get; init; }

    /// <summary>
    /// Gets a value indicating whether the item is a marker interface, which embeds the handle
    /// contract and declares no capability methods of its own. The <c>Any&lt;Type&gt;</c> interfaces that
    /// accept a handle type or any type derived from it are markers too.
    /// </summary>
    public bool IsMarker { get; init; }

    /// <summary>
    /// Gets the Go names of the interfaces the handle type implements.
    /// </summary>
    public IReadOnlyList<string> Implements { get; init; } = [];

    /// <summary>
    /// Gets the capabilities that use an options struct. Present only for options items.
    /// </summary>
    public IReadOnlyList<string> UsedBy { get; init; } = [];

    /// <summary>
    /// Gets the members.
    /// </summary>
    public IReadOnlyList<GoApiMember> Members { get; init; } = [];
}

/// <summary>
/// A group of items. The Go export has one module, named after the package.
/// </summary>
internal sealed record GoApiModule
{
    /// <summary>
    /// Gets the module name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the module summary.
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>
    /// Gets the items, ordered by identifier.
    /// </summary>
    public required IReadOnlyList<GoApiItem> Items { get; init; }
}

/// <summary>
/// A signature-only Go fragment that declares a symbol the export's signatures refer to.
/// </summary>
/// <remarks>
/// Declaration identifiers are scoped to the package export and are not unique across exports: two
/// packages can name different types the same. A fragment is identified by
/// <c>(package.name, package.version, declaration.id)</c>.
/// </remarks>
internal sealed record GoApiDeclaration
{
    private readonly string _content = string.Empty;

    /// <summary>
    /// Gets the fragment identifier, unique within the export.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the Go source of the fragment. Line endings are normalized to LF so the export is
    /// identical on every platform.
    /// </summary>
    public required string Content
    {
        get => _content;
        init => _content = value.ReplaceLineEndings("\n");
    }

    /// <summary>
    /// Gets the assembly that owns the declared symbol.
    /// </summary>
    public required string OwningAssemblyName { get; init; }
}

/// <summary>
/// The complete Go API export for one package.
/// </summary>
internal sealed record GoApiModel
{
    /// <summary>
    /// Gets the version of the export document schema.
    /// </summary>
    public required int SchemaVersion { get; init; }

    /// <summary>
    /// Gets the language identifier.
    /// </summary>
    public required string Language { get; init; }

    /// <summary>
    /// Gets the generator that produced the export.
    /// </summary>
    public required GoApiGeneratorIdentity Generator { get; init; }

    /// <summary>
    /// Gets the package the export was produced for.
    /// </summary>
    public required GoApiPackageIdentity Package { get; init; }

    /// <summary>
    /// Gets the modules.
    /// </summary>
    public required IReadOnlyList<GoApiModule> Modules { get; init; }

    /// <summary>
    /// Gets the declaration fragments, ordered by identifier.
    /// </summary>
    public required IReadOnlyList<GoApiDeclaration> Declarations { get; init; }
}
