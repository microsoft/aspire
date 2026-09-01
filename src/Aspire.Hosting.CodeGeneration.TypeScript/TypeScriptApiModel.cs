// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TypeSystem;

namespace Aspire.Hosting.CodeGeneration.TypeScript;

/// <summary>
/// The kind of a symbol in the canonical TypeScript API export.
/// </summary>
internal enum TypeScriptApiItemKind
{
    /// <summary>A generated wrapper interface for a handle type.</summary>
    Interface,

    /// <summary>A generated enum.</summary>
    Enum,

    /// <summary>A generated interface for an <c>[AspireDto]</c> type.</summary>
    Dto,

    /// <summary>A generated options bag interface for a method's optional parameters.</summary>
    Options,

    /// <summary>A namespace containing immutable exported values.</summary>
    Namespace,

    /// <summary>An immutable exported value.</summary>
    Constant,

    /// <summary>
    /// The members this package contributes to an interface another package owns. The owning package
    /// publishes the type itself, so this is deliberately not a second page for that type.
    /// </summary>
    Augmentation,

    /// <summary>A method on a generated interface, or a module-level entry point function.</summary>
    Method,

    /// <summary>A property on a generated interface.</summary>
    Property,
}

/// <summary>
/// The exact package identity a canonical export was produced for.
/// </summary>
/// <param name="Name">The package name, for example <c>Aspire.Hosting.Redis</c>.</param>
/// <param name="Version">The exact package version, for example <c>13.5.0</c>.</param>
internal sealed record TypeScriptApiPackageIdentity(string Name, string Version);

/// <summary>
/// Identifies the code generator that produced a canonical export.
/// </summary>
/// <param name="Name">The code-generation assembly name.</param>
/// <param name="Version">The code-generation assembly informational version.</param>
internal sealed record TypeScriptApiGeneratorIdentity(string Name, string Version);

/// <summary>
/// A single parameter of a resolved TypeScript signature.
/// </summary>
internal sealed record TypeScriptApiParameter
{
    /// <summary>Gets the parameter name as it appears in the generated signature.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the final TypeScript type text for the parameter.</summary>
    public required string DeclaredType { get; init; }

    /// <summary>Gets a value indicating whether the parameter is optional.</summary>
    public required bool IsOptional { get; init; }

    /// <summary>Gets the documentation summary for the parameter, if any.</summary>
    public string? Summary { get; init; }
}

/// <summary>
/// A documented member of an exported item.
/// </summary>
internal sealed record TypeScriptApiMember
{
    /// <summary>Gets the stable, generator-owned identifier for the member.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the member kind.</summary>
    public required TypeScriptApiItemKind Kind { get; init; }

    /// <summary>Gets the member name.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the final TypeScript declaration string, for example
    /// <c>withPersistence(options?: WithPersistenceOptions): TestRedisResourceBuilderPromise</c>.
    /// </summary>
    public required string Declaration { get; init; }

    /// <summary>Gets the documentation summary.</summary>
    public string? Summary { get; init; }

    /// <summary>Gets the documentation remarks.</summary>
    public string? Remarks { get; init; }

    /// <summary>Gets the documentation examples.</summary>
    public IReadOnlyList<string> Examples { get; init; } = [];

    /// <summary>Gets the deprecation message, or <see langword="null"/> when the member is not deprecated.</summary>
    public string? DeprecationMessage { get; init; }

    /// <summary>Gets the ATS capability that produced this member, used as source metadata.</summary>
    public string? CapabilityId { get; init; }

    /// <summary>
    /// Gets the assembly that declares this member, which is not always the assembly that owns the
    /// type it hangs off: a package can add extension methods to another package's resource.
    /// </summary>
    public string? OwningAssemblyName { get; init; }

    /// <summary>Gets the resolved parameters of the member.</summary>
    public IReadOnlyList<TypeScriptApiParameter> Parameters { get; init; } = [];

    /// <summary>Gets the final TypeScript return type text, if the member has one.</summary>
    public string? ReturnType { get; init; }
}

/// <summary>
/// A documented, package-owned top-level symbol.
/// </summary>
internal sealed record TypeScriptApiItem
{
    /// <summary>Gets the stable, generator-owned identifier for the item.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the ATS type identifier the item was projected from, when it has one.</summary>
    public required string TypeId { get; init; }

    /// <summary>Gets the item kind.</summary>
    public required TypeScriptApiItemKind Kind { get; init; }

    /// <summary>Gets the generated TypeScript name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the final TypeScript declaration header for the item.</summary>
    public required string Declaration { get; init; }

    /// <summary>Gets the assembly that owns the item.</summary>
    public required string OwningAssemblyName { get; init; }

    /// <summary>Gets the documentation summary.</summary>
    public string? Summary { get; init; }

    /// <summary>Gets the documentation remarks.</summary>
    public string? Remarks { get; init; }

    /// <summary>Gets the documentation examples.</summary>
    public IReadOnlyList<string> Examples { get; init; } = [];

    /// <summary>Gets the interfaces this item extends, for relationship rendering.</summary>
    public IReadOnlyList<string> Extends { get; init; } = [];

    /// <summary>Gets the documented members of the item.</summary>
    public IReadOnlyList<TypeScriptApiMember> Members { get; init; } = [];
}

/// <summary>
/// A module of package-owned documentation symbols.
/// </summary>
internal sealed record TypeScriptApiModule
{
    /// <summary>Gets the module name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the module summary.</summary>
    public string? Summary { get; init; }

    /// <summary>Gets the package-owned items in the module.</summary>
    public required IReadOnlyList<TypeScriptApiItem> Items { get; init; }
}

/// <summary>
/// A fully rendered exported-value namespace shared by source generation and canonical projection.
/// </summary>
internal sealed record TypeScriptExportedValueNamespace
{
    /// <summary>Gets the namespace name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the complete TypeScript namespace declaration.</summary>
    public required string Content { get; init; }

    /// <summary>Gets the namespace and constant members exposed for canonical documentation.</summary>
    public required IReadOnlyList<TypeScriptApiMember> Members { get; init; }
}

/// <summary>
/// A generator-owned TypeScript declaration fragment.
/// </summary>
/// <remarks>
/// Declaration IDs are scoped to the containing package export. The canonical identity of a
/// declaration is the tuple <c>(package.name, package.version, declaration.id)</c>; declarations
/// from separate package exports cannot be flattened into one global declaration set because
/// package-local TypeScript names may intentionally overlap. The complete declaration list in one
/// export must type-check on its own.
/// </remarks>
internal sealed record TypeScriptApiDeclaration
{
    private readonly string _content = string.Empty;

    /// <summary>Gets the stable, generator-owned identifier within the containing package export.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the TypeScript declaration text.</summary>
    /// <remarks>
    /// Line endings are normalized to <c>\n</c>. Some fragments come from raw string literals, which
    /// carry whatever line endings the source file was checked out with, so the same package export
    /// would otherwise differ between a CLI built on Windows and one built on Linux.
    /// </remarks>
    public required string Content
    {
        get => _content;
        init => _content = value.ReplaceLineEndings("\n");
    }

    /// <summary>Gets the assembly that owns the declared symbol.</summary>
    public required string OwningAssemblyName { get; init; }
}

/// <summary>
/// The canonical TypeScript API export model for one package.
/// </summary>
internal sealed record TypeScriptApiModel
{
    /// <summary>Gets the export schema version.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Gets the export language, always <c>typescript</c>.</summary>
    public required string Language { get; init; }

    /// <summary>Gets the code generator identity that produced this export.</summary>
    public required TypeScriptApiGeneratorIdentity Generator { get; init; }

    /// <summary>Gets the exact package identity this export was produced for.</summary>
    public required TypeScriptApiPackageIdentity Package { get; init; }

    /// <summary>Gets the package-owned documentation modules.</summary>
    public required IReadOnlyList<TypeScriptApiModule> Modules { get; init; }

    /// <summary>
    /// Gets the package-scoped declaration fragments needed to type-check the exported surface.
    /// </summary>
    public required IReadOnlyList<TypeScriptApiDeclaration> Declarations { get; init; }
}

/// <summary>
/// A method signature resolved once and shared by the source emitter and the canonical exporter.
/// </summary>
/// <remarks>
/// Both emitters must render the same text. Reconstructing signatures separately is what caused
/// documented TypeScript signatures to drift from the generated SDK (microsoft/aspire#17608).
/// </remarks>
internal sealed record TypeScriptApiMethodSignature
{
    /// <summary>Gets the generated method name.</summary>
    public required string MethodName { get; init; }

    /// <summary>Gets the final TypeScript return type text.</summary>
    public required string ReturnType { get; init; }

    /// <summary>Gets the parameters exactly as they appear in the public TypeScript signature.</summary>
    public required IReadOnlyList<TypeScriptApiParameter> Parameters { get; init; }

    /// <summary>Gets the rendered public parameter list, without the surrounding parentheses.</summary>
    public string ParameterList => string.Join(
        ", ",
        Parameters.Select(parameter =>
            $"{parameter.Name}{(parameter.IsOptional ? "?" : string.Empty)}: {parameter.DeclaredType}"));

    /// <summary>Gets the required parameters, in declaration order.</summary>
    public required IReadOnlyList<AtsParameterInfo> RequiredParameters { get; init; }

    /// <summary>Gets the resolved options bag parameter, when the method exposes one.</summary>
    public TypeScriptApiParameter? OptionsParameter { get; init; }

    /// <summary>Gets the cancellation token emitted separately after a direct options DTO.</summary>
    public TypeScriptApiParameter? TrailingCancellationToken { get; init; }

    /// <summary>Gets the full declaration string, for example <c>addRedis(name: string): RedisResourceBuilderPromise</c>.</summary>
    public string Declaration => $"{MethodName}({ParameterList}): {ReturnType}";
}

/// <summary>
/// The result of resolving an <see cref="AtsContext"/> into TypeScript-specific decisions.
/// </summary>
internal sealed record TypeScriptResolvedModel
{
    /// <summary>Gets the ATS context the model was resolved from.</summary>
    public required AtsContext Context { get; init; }

    /// <summary>Gets every builder model discovered from the context.</summary>
    public required List<BuilderModel> Builders { get; init; }

    /// <summary>Gets the builders that represent resource builders.</summary>
    public required List<BuilderModel> ResourceBuilders { get; init; }

    /// <summary>Gets the builders that represent context and wrapper type classes.</summary>
    public required List<BuilderModel> TypeClasses { get; init; }

    /// <summary>Gets the entry point capabilities that hang off the client rather than a type.</summary>
    public required List<AtsCapabilityInfo> ClientMethods { get; init; }

    /// <summary>Gets the type IDs that need generated handle aliases.</summary>
    public required HashSet<string> HandleTypeIds { get; init; }
}
