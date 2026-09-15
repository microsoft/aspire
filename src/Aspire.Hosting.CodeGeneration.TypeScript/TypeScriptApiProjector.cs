// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aspire.Shared.CodeGeneration;
using Aspire.Shared.Json;
using Aspire.TypeSystem;

namespace Aspire.Hosting.CodeGeneration.TypeScript;

/// <summary>
/// Resolves an <see cref="AtsContext"/> into the TypeScript-specific decisions that define the
/// public SDK surface: type mapping, options flattening, callback shaping, promise wrapping, and
/// fluent return selection.
/// </summary>
/// <remarks>
/// <para>
/// This type is the single owner of those decisions. <see cref="AtsTypeScriptCodeGenerator"/>
/// consumes it to emit runtime source, and <see cref="TypeScriptApiExportWriter"/> consumes the
/// same resolved model to emit the canonical API export. Documentation that reconstructs
/// signatures from raw ATS instead drifts from the SDK that actually ships, which is the failure
/// mode tracked by microsoft/aspire#17608.
/// </para>
/// <para>
/// Resolution happens in the constructor so the mapping members can never be called before the
/// wrapper class and options interface tables they depend on exist.
/// </para>
/// </remarks>
internal sealed partial class TypeScriptApiProjector
{
    /// <summary>The schema version of the canonical export document this projector produces.</summary>
    public const int ExportSchemaVersion = 1;

    /// <summary>
    /// Base library symbols that generated declarations reference but that the SDK ships by hand in
    /// <c>base.mts</c>/<c>transport.mts</c> rather than generating per package. Each package export
    /// includes these symbols under a well-known package-local declaration ID so its declarations
    /// type-check without site-authored shims.
    /// </summary>
    private const string RuntimeDeclarationId = "aspire:runtime:base";

    private static readonly TypeScriptApiGeneratorIdentity s_generatorIdentity = CreateGeneratorIdentity();

    /// <summary>The symbol names <see cref="RuntimeDeclarationContent"/> already declares.</summary>
    private static readonly HashSet<string> s_runtimeDeclaredNames = new(StringComparer.Ordinal)
    {
        "Awaitable", "MarshalledHandle", "Handle", "HandleReference", "AbortSignal", "CancellationToken",
        "ReferenceExpression", "AspireList", "AspireDict", "ResourceBuilderBase", "InputType",
        "InteractionInput", "InteractionInputCollection", "InteractionInputCollectionPromise",
        // Every exported entry point is a free function that takes the client explicitly
        // (see EntryPointClientParameterType), so a package contributing an entry point names this
        // symbol in a signature. Without it here the fragment would be the only self-contained
        // declaration set that does not compile on its own.
        "AspireClientRpc"
    };

    private const string RuntimeDeclarationContent = """
        export type Awaitable<T> = T | PromiseLike<T>;
        export interface MarshalledHandle { $handle: string; $type: string; }
        export interface Handle<T extends string = string> { readonly $handle: string; readonly $type: T; toJSON(): MarshalledHandle; }
        export interface HandleReference { toJSON(): MarshalledHandle; }
        export interface AbortSignal { readonly aborted: boolean; }
        export interface CancellationToken { readonly aborted: boolean; }
        export enum InputType { Text = 'Text', SecretText = 'SecretText', Choice = 'Choice', Boolean = 'Boolean', Number = 'Number' }
        export interface ReferenceExpression { readonly value: Promise<string>; }
        export interface AspireList<T> extends HandleReference { get(index: number): Promise<T>; }
        export interface AspireDict<TKey, TValue> extends HandleReference { get(key: TKey): Promise<TValue>; }
        export interface ResourceBuilderBase extends HandleReference {}
        export interface InteractionInput { readonly name: string; }
        export interface InteractionInputCollection extends HandleReference {}
        export interface InteractionInputCollectionPromise extends PromiseLike<InteractionInputCollection> {}
        export interface AspireClientRpc { readonly connected: boolean; invokeCapability<TResult = unknown>(capabilityId: string, args?: Record<string, unknown>): Promise<TResult>; }
        """;

    private readonly TypeScriptResolvedModel _resolved;

    /// <summary>The client parameter every entry-point function takes first.</summary>
    private const string EntryPointClientParameterName = "client";

    /// <summary>The declared type of <see cref="EntryPointClientParameterName"/>.</summary>
    private const string EntryPointClientParameterType = "AspireClientRpc";

    public TypeScriptApiProjector(AtsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _resolved = Resolve(context);
    }

    /// <summary>Gets the resolved projection of the context this projector was built from.</summary>
    internal TypeScriptResolvedModel Resolved => _resolved;

    /// <summary>Gets the mapping of ATS type ID to generated wrapper class name.</summary>
    internal Dictionary<string, string> WrapperClassNames => _wrapperClassNames;

    /// <summary>Gets the mapping of ATS type ID to the retained concrete type ID for its wrapper.</summary>
    internal Dictionary<string, string> ConcreteTypeIds => _concreteTypeIds;

    /// <summary>Gets the mapping of ATS type ID to the type reference it was resolved from.</summary>
    internal Dictionary<string, AtsTypeRef> TypeRefsById => _typeRefsById;

    /// <summary>Gets the type IDs that have generated Promise wrappers.</summary>
    internal HashSet<string> TypesWithPromiseWrappers => _typesWithPromiseWrappers;

    /// <summary>Gets the names of options interfaces that have been registered for generation.</summary>
    internal HashSet<string> GeneratedOptionsInterfaces => _generatedOptionsInterfaces;

    /// <summary>Gets the options interfaces to generate, keyed by interface name.</summary>
    internal Dictionary<string, List<AtsParameterInfo>> OptionsInterfacesToGenerate => _optionsInterfacesToGenerate;

    /// <summary>Gets the mapping of capability ID to the options interface name it uses.</summary>
    internal Dictionary<string, string> CapabilityOptionsInterfaceMap => _capabilityOptionsInterfaceMap;

    /// <summary>Gets the mapping of enum type ID to generated TypeScript enum name.</summary>
    internal Dictionary<string, string> EnumTypeNames => _enumTypeNames;

    /// <summary>Gets the XML documentation captured for handle types during ATS scanning.</summary>
    internal Dictionary<string, AtsDocumentationInfo> HandleDocumentationById => _handleDocumentationById;

    /// <summary>Gets the DTO metadata used for generated argument marshalling.</summary>
    internal Dictionary<string, AtsDtoTypeInfo> DtoTypesById => _dtoTypesById;

    private TypeScriptResolvedModel Resolve(AtsContext context)
    {
        var capabilities = context.Capabilities;
        var dtoTypes = context.DtoTypes;
        var directlyReturnedResourceTypesByClassName = capabilities
            .Where(capability => capability.CapabilityKind != AtsCapabilityKind.PropertySetter)
            .Select(capability => capability.ReturnType)
            .Where(typeRef => typeRef?.IsResourceBuilder == true)
            .Select(typeRef => typeRef!)
            .DistinctBy(typeRef => typeRef.TypeId, StringComparer.Ordinal)
            .GroupBy(typeRef => DeriveClassName(typeRef.TypeId), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        var builders = CreateBuilderModels(capabilities);
        var clientMethods = GetEntryPointCapabilities(capabilities)
            .Where(c => string.IsNullOrEmpty(c.TargetTypeId))
            .ToList();

        // Collect all unique type IDs for handle type aliases.
        // Exclude DTO types - they have their own interfaces, not handle aliases.
        var dtoTypeIds = new HashSet<string>(dtoTypes.Select(d => d.TypeId), StringComparer.Ordinal);
        var typeIds = new HashSet<string>(StringComparer.Ordinal);
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

        // Separate builders into categories:
        // 1. Resource builders: IResource*, ContainerResource, etc.
        // 2. Type classes: everything else (context types, wrapper types)
        var resourceBuilders = builders.Where(b => b.TargetType?.IsResourceBuilder == true).ToList();
        var typeClasses = builders.Where(b => b.TargetType?.IsResourceBuilder != true).ToList();

        // Build wrapper class name mapping before anything consumes the mappings so callback
        // properties can reference wrapper classes instead of raw handle aliases.
        _wrapperClassNames.Clear();
        _concreteTypeIds.Clear();
        _typeRefsById.Clear();
        _typesWithPromiseWrappers.Clear();
        _generatedOptionsInterfaces.Clear();
        _optionsInterfacesToGenerate.Clear();
        _capabilityOptionsInterfaceMap.Clear();
        _optionsInterfaceOwningAssemblies.Clear();
        _handleDocumentationById.Clear();
        _dtoTypesById.Clear();
        _enumTypeNames.Clear();

        foreach (var dtoType in dtoTypes)
        {
            _dtoTypesById[dtoType.TypeId] = dtoType;
        }

        foreach (var handleType in context.HandleTypes)
        {
            if (handleType.Documentation is not null)
            {
                _handleDocumentationById[handleType.AtsTypeId] = handleType.Documentation;
            }
        }

        foreach (var builder in resourceBuilders)
        {
            _wrapperClassNames[builder.TypeId] = builder.BuilderClassName;
            _concreteTypeIds[builder.TypeId] = builder.TypeId;
            if (builder.TargetType is { } targetType)
            {
                _typeRefsById[builder.TypeId] = targetType;
            }

            directlyReturnedResourceTypesByClassName.TryGetValue(builder.BuilderClassName, out var directlyReturnedAliases);

            // Builder models are deduplicated by generated class name, so the retained TypeId may
            // differ from a directly returned interface TypeId. Register the retained TypeId to emit
            // one declaration pair and every returned alias so return sites resolve to that pair.
            if (HasChainableMethods(builder) || directlyReturnedAliases is not null)
            {
                _typesWithPromiseWrappers.Add(builder.TypeId);

                if (directlyReturnedAliases is not null)
                {
                    foreach (var alias in directlyReturnedAliases)
                    {
                        _typesWithPromiseWrappers.Add(alias.TypeId);
                        _wrapperClassNames[alias.TypeId] = builder.BuilderClassName;
                        _concreteTypeIds[alias.TypeId] = builder.TypeId;
                        _typeRefsById[alias.TypeId] = builder.TargetType ?? alias;
                    }
                }
            }
        }

        foreach (var typeClass in typeClasses)
        {
            _wrapperClassNames[typeClass.TypeId] = DeriveClassName(typeClass.TypeId);
            _concreteTypeIds[typeClass.TypeId] = typeClass.TypeId;
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

        // InteractionInputCollection is a hand-written base.mts type: its by-name accessors
        // (value/get/required/requiredValue) are client-side conveniences, not ATS capabilities, so
        // it is never registered as a generated type class. Register it as a promise-wrapper type so
        // collection-returning getters (result.inputs(), validationContext.inputs(), command
        // arguments()) emit the fluent InteractionInputCollectionPromise thenable instead of a bare
        // Promise<InteractionInputCollection>. That lets callers chain `await x.inputs().value("c")`
        // without an intermediate await, matching the C#/Go/Java/Python surfaces. The wrapper
        // (InteractionInputCollectionPromise / InteractionInputCollectionPromiseImpl) is hand-written
        // in base.mts; it is intentionally absent from the wrapper class table so the getter impl
        // keeps using the marshaller-based collection construction rather than a handle+Impl wrapper.
        _typesWithPromiseWrappers.Add(InteractionInputCollectionTypeId);
        // Note: ReferenceExpression is intentionally NOT added to the wrapper class table.
        // It is a value type defined in base.mts with a private constructor and static factory,
        // not a handle-based wrapper. It is handled via MapTypeRefToTypeScript instead.

        // Enum names are a resolution decision, not an emission detail: MapEnumType has to resolve
        // them while options interfaces are being registered, which happens before any enum is
        // written out.
        _enumTypeNames[InputTypeTypeId] = GetInputTypeEnumName();
        foreach (var enumType in context.EnumTypes.Where(e => e.TypeId != InputTypeTypeId))
        {
            _enumTypeNames[enumType.TypeId] = enumType.Name;
        }

        // Pre-scan all capabilities to collect options interfaces.
        // This must happen AFTER wrapper class names are populated so types resolve correctly.
        // Options names are public TypeScript API. Allocate collision suffixes after sorting by the
        // stable capability identity so a combined context produces byte-identical output regardless
        // of the order in which package capabilities were discovered.
        foreach (var cap in builders
            .SelectMany(builder => builder.Capabilities)
            .OrderBy(capability => capability.CapabilityId, StringComparer.Ordinal))
        {
            var (_, optionalParams) = SeparateParameters(cap.Parameters);
            if (optionalParams.Count > 0 && !TryGetDirectOptionsParameter(optionalParams, out _))
            {
                RegisterOptionsInterface(cap.CapabilityId, cap.MethodName, optionalParams, GetCapabilityOwningAssemblyName(context, cap));
            }
        }

        return new TypeScriptResolvedModel
        {
            Context = context,
            Builders = builders,
            ResourceBuilders = resourceBuilders,
            TypeClasses = typeClasses,
            ClientMethods = clientMethods,
            HandleTypeIds = typeIds
        };
    }

    /// <summary>
    /// Resolves the public signature of a capability exactly once so the source emitter and the
    /// canonical exporter cannot disagree about parameter shaping or return type selection.
    /// </summary>
    /// <param name="builder">The builder the capability is rendered on, or <see langword="null"/> for a client entry point.</param>
    /// <param name="capability">The capability to resolve.</param>
    /// <remarks>
    /// Resource builders and type classes shape methods differently: they bind a different default
    /// target parameter name, derive the method name differently, and pick fluent return types by
    /// different rules. Both rules live here so neither emitter has to reimplement them.
    /// </remarks>
    internal TypeScriptApiMethodSignature ResolveMethodSignature(BuilderModel? builder, AtsCapabilityInfo capability)
    {
        ArgumentNullException.ThrowIfNull(capability);

        var isTypeClass = builder is not null && builder.TargetType?.IsResourceBuilder != true;
        var targetParamName = capability.TargetParameterName ?? (isTypeClass ? "context" : "builder");
        var userParams = builder is null
            ? [.. capability.Parameters]
            : capability.Parameters.Where(p => p.Name != targetParamName).ToList();

        var (requiredParams, optionalParams) = SeparateParameters(userParams);
        var hasOptionals = optionalParams.Count > 0;
        var hasDirectOptionsParameter = TryGetDirectOptionsParameter(optionalParams, out var directOptionsParam);
        var optionsTypeName = hasDirectOptionsParameter
            ? MapParameterToTypeScript(directOptionsParam!)
            : ResolveOptionsInterfaceName(capability);
        var optionsParameterName = GetPublicOptionsParameterName(userParams, hasOptionals, hasDirectOptionsParameter);
        var trailingCancellationToken = GetTrailingCancellationTokenParameter(optionalParams);
        var publicParameters = requiredParams
            .Select(ProjectPublicParameter)
            .ToList();
        TypeScriptApiParameter? optionsParameter = null;

        if (hasOptionals)
        {
            optionsParameter = new TypeScriptApiParameter
            {
                Name = optionsParameterName,
                DeclaredType = optionsTypeName,
                IsOptional = true,
                Summary = directOptionsParam?.Documentation?.Summary
            };
            publicParameters.Add(optionsParameter);
        }

        TypeScriptApiParameter? publicCancellationToken = null;
        if (trailingCancellationToken is not null)
        {
            publicCancellationToken = ProjectPublicParameter(trailingCancellationToken);
            publicParameters.Add(publicCancellationToken);
        }

        return new TypeScriptApiMethodSignature
        {
            MethodName = isTypeClass ? ResolveTypeClassMethodName(capability) : capability.MethodName,
            ReturnType = isTypeClass
                ? ResolveTypeClassReturnType(builder!, capability)
                : ResolveBuilderReturnType(builder, capability),
            Parameters = publicParameters,
            RequiredParameters = requiredParams,
            OptionsParameter = optionsParameter,
            TrailingCancellationToken = publicCancellationToken
        };

        TypeScriptApiParameter ProjectPublicParameter(AtsParameterInfo parameter)
            => new()
            {
                Name = parameter.Name,
                DeclaredType = MapParameterToTypeScript(parameter),
                IsOptional = parameter.IsOptional || parameter.IsNullable,
                Summary = parameter.Documentation?.Summary
            };
    }

    /// <summary>
    /// Strips the declaring type prefix from an explicitly implemented member.
    /// </summary>
    /// <remarks>
    /// Capabilities on an interface implementation carry the qualified C# name, for example
    /// <c>IValueProvider.GetValueAsync</c>. TypeScript has no explicit interface implementation, so
    /// only the trailing member name is emitted.
    /// </remarks>
    private static string ResolveTypeClassMethodName(AtsCapabilityInfo capability)
        => !string.IsNullOrEmpty(capability.OwningTypeName) && capability.MethodName.Contains('.')
            ? capability.MethodName[(capability.MethodName.LastIndexOf('.') + 1)..]
            : GetTypeScriptMethodName(capability.MethodName);

    /// <summary>
    /// Selects the return type for a method on a resource builder: a promise wrapper when the
    /// non-builder return type has one, a plain <c>Promise&lt;T&gt;</c> when it does not, and the
    /// owning builder's fluent promise interface when the method chains.
    /// </summary>
    private string ResolveBuilderReturnType(BuilderModel? builder, AtsCapabilityInfo capability)
    {
        var hasNonBuilderReturn = !capability.ReturnsBuilder && capability.ReturnType is not null;

        if (hasNonBuilderReturn)
        {
            return TryGetPromiseWrapperType(capability.ReturnType, out var promiseInterfaceName, out _)
                ? promiseInterfaceName
                : $"Promise<{MapTypeRefToTypeScript(capability.ReturnType)}>";
        }

        if (builder is not null)
        {
            return GetBuilderPromiseInterfaceForMethod(builder, capability);
        }

        // Entry points have no owning builder, so the fluent return comes from the return type itself.
        return capability.ReturnType is { TypeId: { } returnTypeId }
            ? GetPublicPromiseInterfaceName(returnTypeId)
            : "Promise<void>";
    }

    /// <summary>
    /// Selects the return type for a method on a type class. Void-returning methods chain on the
    /// owning class rather than resolving to <c>Promise&lt;void&gt;</c>, which is what makes context
    /// types fluent.
    /// </summary>
    private string ResolveTypeClassReturnType(BuilderModel builder, AtsCapabilityInfo capability)
    {
        if (capability.ReturnType is { } returnType && _typesWithPromiseWrappers.Contains(returnType.TypeId))
        {
            return GetPublicPromiseInterfaceName(returnType.TypeId);
        }

        if (capability.ReturnType is null || capability.ReturnType.TypeId == AtsConstants.Void)
        {
            return GetPromiseInterfaceName(DeriveClassName(builder.TypeId));
        }

        return $"Promise<{MapTypeRefToTypeScript(capability.ReturnType)}>";
    }

    /// <summary>
    /// Builds the canonical API export model for one package from the already-resolved projection.
    /// </summary>
    /// <remarks>
    /// Declaration fragment IDs are local to <paramref name="package"/>. Their canonical identity is
    /// <c>(package.name, package.version, declaration.id)</c>; consumers must not flatten declarations
    /// from separate package exports because their package-local TypeScript names can overlap.
    /// </remarks>
    /// <param name="package">The exact package identity the export is produced for.</param>
    /// <param name="ownedAssemblyNames">
    /// The assemblies whose symbols the package owns. Symbols outside this set reached the context
    /// through the referenced-type closure: they contribute declaration fragments so the export
    /// type-checks, but they must not produce documentation pages here.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the export between projected items.</param>
    internal TypeScriptApiModel BuildApiModel(
        TypeScriptApiPackageIdentity package,
        IReadOnlyCollection<string> ownedAssemblyNames,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(ownedAssemblyNames);
        cancellationToken.ThrowIfCancellationRequested();

        var owned = new HashSet<string>(ownedAssemblyNames, StringComparer.OrdinalIgnoreCase);

        var items = new List<TypeScriptApiItem>();
        var declarations = new Dictionary<string, TypeScriptApiDeclaration>(StringComparer.Ordinal)
        {
            [RuntimeDeclarationId] = new TypeScriptApiDeclaration
            {
                Id = RuntimeDeclarationId,
                Content = RuntimeDeclarationContent,
                OwningAssemblyName = "Aspire.Hosting"
            }
        };

        foreach (var builderModel in _resolved.Builders.OrderBy(b => b.BuilderClassName, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (item, builderDeclarations) = ProjectBuilder(package, builderModel, owned);

            foreach (var declaration in builderDeclarations)
            {
                declarations[declaration.Id] = declaration;
            }

            if (item is not null)
            {
                items.Add(item);
            }
        }

        foreach (var entryPoint in _resolved.ClientMethods.OrderBy(c => c.MethodName, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!owned.Contains(GetCapabilityOwningAssemblyName(entryPoint)))
            {
                continue;
            }

            var (item, declaration) = ProjectEntryPoint(entryPoint);
            items.Add(item);
            declarations[declaration.Id] = declaration;
        }

        foreach (var enumType in _resolved.Context.EnumTypes
            .Where(e => e.TypeId != InputTypeTypeId)
            .OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (item, declaration) = ProjectEnum(enumType);

            declarations[declaration.Id] = declaration;

            if (owned.Contains(item.OwningAssemblyName))
            {
                items.Add(item);
            }
        }

        foreach (var dtoType in _resolved.Context.DtoTypes
            .Where(d => d.TypeId != InteractionInputTypeId)
            .OrderBy(d => d.TypeId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (item, declaration) = ProjectDto(dtoType);

            declarations[declaration.Id] = declaration;

            if (owned.Contains(item.OwningAssemblyName))
            {
                items.Add(item);
            }
        }

        var exportedValues = _resolved.Context.ExportedValues
            .Where(value => owned.Contains(value.OwningAssemblyName))
            .ToList();
        foreach (var exportedNamespace in ProjectExportedValues(exportedValues))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = new TypeScriptApiItem
            {
                Id = $"namespace:{exportedNamespace.Name}",
                TypeId = $"namespace:{exportedNamespace.Name}",
                Kind = TypeScriptApiItemKind.Namespace,
                Name = exportedNamespace.Name,
                Declaration = $"export namespace {exportedNamespace.Name}",
                OwningAssemblyName = package.Name,
                Members = exportedNamespace.Members
            };
            var declaration = new TypeScriptApiDeclaration
            {
                Id = $"{package.Name}:namespace:{exportedNamespace.Name}",
                Content = exportedNamespace.Content,
                OwningAssemblyName = package.Name
            };

            items.Add(item);
            declarations[declaration.Id] = declaration;
        }

        // Options interfaces belong to the assembly whose capability produced them, which is what
        // both their fragment ID and their documented-item gate key off. Otherwise, a package could
        // document options interfaces belonging to its dependencies.
        foreach (var (interfaceName, optionalParams) in _optionsInterfacesToGenerate.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var owningAssemblyName = _optionsInterfaceOwningAssemblies.GetValueOrDefault(interfaceName, package.Name);
            var (item, declaration) = ProjectOptionsInterface(owningAssemblyName, interfaceName, optionalParams);

            declarations[declaration.Id] = declaration;

            if (owned.Contains(item.OwningAssemblyName))
            {
                items.Add(item);
            }
        }

        // Types reached through the referenced-type closure are named by generated unions and
        // parameters but have no capabilities of their own in this context, so nothing above
        // declared them. Emit an opaque interface for each so this package's declarations type-check
        // standalone. They deliberately produce no documented item: the package that owns them
        // publishes their real surface.
        // Deduplicate by declared name rather than by type ID: several ATS type IDs can resolve to
        // the same generated interface name, and emitting a stub for one of them would redeclare a
        // type another fragment already declares in full.
        var declaredNames = new HashSet<string>(s_runtimeDeclaredNames, StringComparer.Ordinal);
        foreach (var declaration in declarations.Values)
        {
            foreach (Match match in DeclaredTypeNameRegex().Matches(declaration.Content))
            {
                declaredNames.Add(match.Groups[1].Value);
            }
        }

        foreach (var typeId in _resolved.HandleTypeIds.OrderBy(id => id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wrapperClassName = _wrapperClassNames.GetValueOrDefault(typeId);
            var owningAssembly = GetTypeOwningAssemblyName(typeId);

            // Handle types without a generated wrapper class surface in signatures under their raw
            // handle alias name, so the fragment has to declare that exact alias. Deriving a class
            // name here instead would declare a symbol no signature ever references and leave the
            // referenced one undefined.
            if (wrapperClassName is null)
            {
                var handleName = GetHandleTypeName(typeId);

                if (declaredNames.Add(handleName))
                {
                    declarations[$"{owningAssembly}:handle:{handleName}"] = new TypeScriptApiDeclaration
                    {
                        Id = $"{owningAssembly}:handle:{handleName}",
                        Content = $"export type {handleName} = Handle<'{typeId}'>;",
                        OwningAssemblyName = owningAssembly
                    };
                }

                continue;
            }

            var name = GetInterfaceName(wrapperClassName);

            if (!declaredNames.Add(name))
            {
                continue;
            }

            var baseType = _typeRefsById.GetValueOrDefault(typeId)?.IsResourceBuilder == true
                ? "ResourceBuilderBase"
                : "HandleReference";

            declarations[$"{owningAssembly}:opaque:{name}"] = new TypeScriptApiDeclaration
            {
                Id = $"{owningAssembly}:opaque:{name}",
                Content = $"export interface {name} extends {baseType} {{}}",
                OwningAssemblyName = owningAssembly
            };

            if (!_typesWithPromiseWrappers.Contains(typeId))
            {
                continue;
            }

            var promiseName = GetPromiseInterfaceName(wrapperClassName);
            if (!declaredNames.Add(promiseName))
            {
                continue;
            }

            declarations[$"{owningAssembly}:opaque:{promiseName}"] = new TypeScriptApiDeclaration
            {
                Id = $"{owningAssembly}:opaque:{promiseName}",
                Content = $"export interface {promiseName} extends PromiseLike<{name}> {{}}",
                OwningAssemblyName = owningAssembly
            };
        }

        var module = new TypeScriptApiModule
        {
            Name = package.Name,
            Summary = null,
            Items = [.. items.OrderBy(i => i.Id, StringComparer.Ordinal)]
        };

        return new TypeScriptApiModel
        {
            SchemaVersion = ExportSchemaVersion,
            Language = "typescript",
            Generator = s_generatorIdentity,
            Package = package,
            Modules = [module],
            Declarations = [.. declarations.Values.OrderBy(d => d.Id, StringComparer.Ordinal)]
        };
    }

    /// <summary>
    /// Projects exported values into namespace declarations shared by source generation and API export.
    /// </summary>
    /// <param name="exportedValues">The values to project.</param>
    /// <returns>The rendered top-level namespaces and their canonical members.</returns>
    internal IReadOnlyList<TypeScriptExportedValueNamespace> ProjectExportedValues(
        IReadOnlyList<AtsExportedValueInfo> exportedValues)
    {
        var root = BuildExportedValueTree(exportedValues);
        var namespaces = new List<TypeScriptExportedValueNamespace>();

        foreach (var (name, node) in root.Children.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var content = new StringBuilder();
            var members = new List<TypeScriptApiMember>();
            content.Append("export namespace ").Append(name).Append(" {\n");
            AppendExportedValueChildren(content, node, [name], members, indentLevel: 1);
            content.Append('}');
            namespaces.Add(new TypeScriptExportedValueNamespace
            {
                Name = name,
                Content = content.ToString(),
                Members = members
            });
        }

        return namespaces;
    }

    private void AppendExportedValueChildren(
        StringBuilder content,
        ExportedValueTreeNode node,
        IReadOnlyList<string> parentPath,
        List<TypeScriptApiMember> members,
        int indentLevel)
    {
        var indent = new string(' ', indentLevel * 4);

        foreach (var (name, child) in node.Children.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var path = parentPath.Append(name).ToArray();
            if (child.Value is { } valueInfo)
            {
                foreach (var documentationLine in RenderDocumentationComment(
                    indent,
                    valueInfo.Documentation,
                    valueInfo.Description))
                {
                    content.Append(documentationLine).Append('\n');
                }

                var declaration = $"export const {name} = {RenderTypeScriptExportedValueExpression(valueInfo)}";
                content.Append(indent).Append(declaration).Append(";\n");
                members.Add(new TypeScriptApiMember
                {
                    Id = $"constant:{string.Join(".", path)}",
                    Kind = TypeScriptApiItemKind.Constant,
                    Name = name,
                    Declaration = declaration,
                    Summary = valueInfo.Documentation?.Summary ?? valueInfo.Description,
                    Remarks = valueInfo.Documentation?.Remarks,
                    OwningAssemblyName = valueInfo.OwningAssemblyName
                });
            }
            else
            {
                var declaration = $"export namespace {name}";
                content.Append(indent).Append(declaration).Append(" {\n");
                members.Add(new TypeScriptApiMember
                {
                    Id = $"namespace:{string.Join(".", path)}",
                    Kind = TypeScriptApiItemKind.Namespace,
                    Name = name,
                    Declaration = declaration
                });
                AppendExportedValueChildren(content, child, path, members, indentLevel + 1);
                content.Append(indent).Append("}\n");
            }

            content.Append('\n');
        }
    }

    private string RenderTypeScriptExportedValueExpression(AtsExportedValueInfo exportedValue)
    {
        var literal = RenderTypeScriptExportedValue(exportedValue.Value, exportedValue.Type);
        var exportedType = MapTypeRefToTypeScript(exportedValue.Type);

        return exportedValue.Type.Category is AtsTypeCategory.Primitive
            ? literal
            : $"{literal} as {exportedType}";
    }

    private string RenderTypeScriptExportedValue(JsonNode? value, AtsTypeRef typeRef)
    {
        if (value is null)
        {
            return "null";
        }

        return typeRef.Category switch
        {
            AtsTypeCategory.Dto when value is JsonObject obj && _dtoTypesById.TryGetValue(typeRef.TypeId, out var dtoInfo)
                => RenderTypeScriptDtoValue(obj, dtoInfo),
            AtsTypeCategory.Array or AtsTypeCategory.List when value is JsonArray arr
                => $"[{string.Join(", ", arr.Select(item => RenderTypeScriptExportedValue(item, typeRef.ElementType!)))}]",
            AtsTypeCategory.Dict when value is JsonObject obj
                => "{ " + string.Join(", ", obj.Select(pair => $"{AtsJsonCodeWriter.ToRelaxedJsonString(pair.Key)}: {RenderTypeScriptExportedValue(pair.Value, typeRef.ValueType!)}")) + " }",
            _ => value.ToRelaxedJsonString()
        };
    }

    private string RenderTypeScriptDtoValue(JsonObject value, AtsDtoTypeInfo dtoInfo)
    {
        var members = new List<string>();

        foreach (var property in dtoInfo.Properties)
        {
            if (value.TryGetPropertyValue(property.Name, out var propertyValue))
            {
                members.Add($"{ToCamelCase(property.Name)}: {RenderTypeScriptExportedValue(propertyValue, property.Type)}");
            }
        }

        return "{ " + string.Join(", ", members) + " }";
    }

    private static IReadOnlyList<string> RenderDocumentationComment(
        string indent,
        AtsDocumentationInfo? documentation,
        string? fallbackSummary)
    {
        var lines = new List<string>();
        AddDocumentationLines(lines, documentation?.Summary ?? fallbackSummary);
        AddDocumentationLines(lines, documentation?.Remarks, addBlankLineBefore: lines.Count > 0);
        AddTaggedDocumentationLines(lines, "@returns", documentation?.Returns);

        if (lines.Count == 0)
        {
            return [];
        }

        if (lines.Count == 1 && !lines[0].StartsWith('@'))
        {
            return [$"{indent}/** {lines[0]} */"];
        }

        var comment = new List<string> { $"{indent}/**" };
        comment.AddRange(lines.Select(line => line.Length == 0 ? $"{indent} *" : $"{indent} * {line}"));
        comment.Add($"{indent} */");
        return comment;
    }

    private static void AddTaggedDocumentationLines(List<string> lines, string tag, string? text)
    {
        var tagLines = SplitDocumentationLines(text);
        if (tagLines.Count == 0)
        {
            return;
        }

        lines.Add($"{tag} {tagLines[0]}");
        lines.AddRange(tagLines.Skip(1));
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

    private static TypeScriptApiGeneratorIdentity CreateGeneratorIdentity()
    {
        var assembly = typeof(TypeScriptApiProjector).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? throw new InvalidOperationException(
                $"The '{assembly.GetName().Name}' assembly has no informational version.");

        return new TypeScriptApiGeneratorIdentity(assembly.GetName().Name!, version);
    }

    /// <summary>
    /// Projects one builder into an optional documented item plus the declaration fragments it
    /// contributes.
    /// </summary>
    /// <remarks>
    /// A package can extend a type another package owns. When that happens the type itself is not
    /// documented here — the owning package publishes it — but the members this package contributes
    /// still are. They are emitted as a separate interface augmentation fragment so TypeScript
    /// declaration merging reassembles the referenced stub and this package's contributed surface.
    /// </remarks>
    private (TypeScriptApiItem? Item, List<TypeScriptApiDeclaration> Declarations) ProjectBuilder(
        TypeScriptApiPackageIdentity package,
        BuilderModel builderModel,
        HashSet<string> ownedAssemblyNames)
    {
        var isResourceBuilder = builderModel.TargetType?.IsResourceBuilder == true;
        var interfaceName = GetInterfaceName(isResourceBuilder
            ? builderModel.BuilderClassName
            : DeriveClassName(builderModel.TypeId));
        var members = new List<TypeScriptApiMember>();
        var exportedCapabilities = builderModel.Capabilities
            .Where(capability => ownedAssemblyNames.Contains(GetCapabilityOwningAssemblyName(capability)))
            .ToList();

        var promiseMembers = new List<TypeScriptApiMember>();
        var getters = exportedCapabilities.Where(c => c.CapabilityKind == AtsCapabilityKind.PropertyGetter).ToList();
        var setters = exportedCapabilities.Where(c => c.CapabilityKind == AtsCapabilityKind.PropertySetter).ToList();

        foreach (var property in GroupPropertiesByName(getters, setters))
        {
            var member = ProjectProperty(interfaceName, property.PropertyName, property.Getter, property.Setter);
            members.Add(member);
            if (IsGetterOnlyProperty(property.Getter, property.Setter))
            {
                promiseMembers.Add(member);
            }
        }

        // Type classes only surface instance and static methods; resource builders surface every
        // non-property capability. Mirroring that split keeps the export aligned with the interfaces
        // the generator actually writes.
        var methods = isResourceBuilder
            ? exportedCapabilities.Where(c =>
                c.CapabilityKind != AtsCapabilityKind.PropertyGetter &&
                c.CapabilityKind != AtsCapabilityKind.PropertySetter)
            : exportedCapabilities.Where(c =>
                c.CapabilityKind is AtsCapabilityKind.InstanceMethod or AtsCapabilityKind.Method);

        foreach (var capability in methods)
        {
            var member = ProjectMethod(interfaceName, builderModel, capability);
            members.Add(member);
            promiseMembers.Add(member);
        }

        var documentation = _handleDocumentationById.GetValueOrDefault(builderModel.TypeId);
        string[] extends = isResourceBuilder ? ["ResourceBuilderBase"] : [];
        var typeOwner = GetTypeOwningAssemblyName(builderModel.TypeId);
        var declarations = new List<TypeScriptApiDeclaration>();

        // Every method returns the owning type's fluent promise interface, so the promise interface
        // has to be declared alongside the interface or the fragments cannot type-check.
        var promiseInterfaceName = _typesWithPromiseWrappers.Contains(builderModel.TypeId)
            ? GetPromiseInterfaceName(isResourceBuilder ? builderModel.BuilderClassName : DeriveClassName(builderModel.TypeId))
            : null;

        if (ownedAssemblyNames.Contains(typeOwner))
        {
            declarations.Add(new TypeScriptApiDeclaration
            {
                Id = $"{typeOwner}:interface:{interfaceName}",
                Content = BuildInterfaceBody(interfaceName, extends, members, includeToJson: true),
                OwningAssemblyName = typeOwner
            });

            if (promiseInterfaceName is not null)
            {
                declarations.Add(new TypeScriptApiDeclaration
                {
                    Id = $"{typeOwner}:interface:{promiseInterfaceName}",
                    Content = BuildInterfaceBody(promiseInterfaceName, [$"PromiseLike<{interfaceName}>"], promiseMembers, includeToJson: false),
                    OwningAssemblyName = typeOwner
                });
            }

            return (BuildInterfaceItem(builderModel, $"interface:{interfaceName}", interfaceName, extends, typeOwner, documentation, members, TypeScriptApiItemKind.Interface), declarations);
        }

        // The referenced type gets one opaque stub keyed by its real owner within this package export.
        declarations.Add(new TypeScriptApiDeclaration
        {
            Id = $"{typeOwner}:opaque:{interfaceName}",
            Content = $"export interface {interfaceName} extends {(isResourceBuilder ? "ResourceBuilderBase" : "HandleReference")} {{}}",
            OwningAssemblyName = typeOwner
        });

        if (promiseInterfaceName is not null)
        {
            declarations.Add(new TypeScriptApiDeclaration
            {
                Id = $"{typeOwner}:opaque:{promiseInterfaceName}",
                Content = $"export interface {promiseInterfaceName} extends PromiseLike<{interfaceName}> {{}}",
                OwningAssemblyName = typeOwner
            });
        }

        if (members.Count == 0)
        {
            return (null, declarations);
        }

        declarations.Add(new TypeScriptApiDeclaration
        {
            Id = $"{package.Name}:augment:{interfaceName}",
            Content = BuildInterfaceBody(interfaceName, [], members, includeToJson: false),
            OwningAssemblyName = package.Name
        });

        if (promiseInterfaceName is not null)
        {
            declarations.Add(new TypeScriptApiDeclaration
            {
                Id = $"{package.Name}:augment:{promiseInterfaceName}",
                Content = BuildInterfaceBody(promiseInterfaceName, [], promiseMembers, includeToJson: false),
                OwningAssemblyName = package.Name
            });
        }

        // The item carries the real owner and a distinct ID because it describes only this package's
        // contribution, not a second copy of the referenced type. Include the contributing package
        // because an aggregate export can contain several augmentations for the same interface name.
        return (BuildInterfaceItem(builderModel, $"augmentation:{package.Name}:{interfaceName}", interfaceName, extends, typeOwner, documentation, members, TypeScriptApiItemKind.Augmentation), declarations);
    }

    private static TypeScriptApiItem BuildInterfaceItem(
        BuilderModel builderModel,
        string id,
        string interfaceName,
        string[] extends,
        string owningAssemblyName,
        AtsDocumentationInfo? documentation,
        List<TypeScriptApiMember> members,
        TypeScriptApiItemKind kind)
        => new()
        {
            Id = id,
            TypeId = builderModel.TypeId,
            Kind = kind,
            Name = interfaceName,
            Declaration = BuildInterfaceHeader(interfaceName, extends),
            OwningAssemblyName = owningAssemblyName,
            Summary = documentation?.Summary,
            Remarks = documentation?.Remarks,
            Extends = extends,
            Members = members
        };

    /// <summary>
    /// Matches the name a declaration fragment declares, for example the <c>RedisResource</c> in
    /// <c>export interface RedisResource extends ResourceBuilderBase {</c>.
    /// </summary>
    /// <remarks>
    /// <c>$</c> is matched as well as <c>\w</c> because package-qualified options interfaces embed
    /// it as the qualifier terminator, and capturing only the qualifier would leave the real name
    /// out of the declared set.
    /// </remarks>
    [GeneratedRegex(@"^export (?:interface|enum|type) ([\w$]+)", RegexOptions.Multiline)]
    private static partial Regex DeclaredTypeNameRegex();

    private static string BuildInterfaceBody(
        string interfaceName,
        IReadOnlyList<string> extends,
        List<TypeScriptApiMember> members,
        bool includeToJson)
    {
        var body = new StringBuilder();
        body.Append(BuildInterfaceHeader(interfaceName, extends)).Append(" {\n");

        if (includeToJson)
        {
            body.Append("    toJSON(): MarshalledHandle;\n");
        }

        foreach (var member in members)
        {
            body.Append("    ").Append(member.Declaration).Append(";\n");
        }

        return body.Append('}').ToString();
    }

    private TypeScriptApiMember ProjectMethod(
        string ownerName,
        BuilderModel? builderModel,
        AtsCapabilityInfo capability)
    {
        var signature = ResolveMethodSignature(builderModel, capability);

        return new TypeScriptApiMember
        {
            Id = $"method:{ownerName}.{capability.MethodName}",
            Kind = TypeScriptApiItemKind.Method,
            Name = signature.MethodName,
            Declaration = signature.Declaration,
            Summary = capability.Documentation?.Summary,
            Remarks = capability.Documentation?.Remarks,
            DeprecationMessage = capability.IsObsolete ? capability.ObsoleteMessage ?? string.Empty : null,
            CapabilityId = capability.CapabilityId,
            OwningAssemblyName = GetCapabilityOwningAssemblyName(capability),
            Parameters = signature.Parameters,
            ReturnType = signature.ReturnType
        };
    }

    private TypeScriptApiMember ProjectProperty(
        string ownerName,
        string propertyName,
        AtsCapabilityInfo? getter,
        AtsCapabilityInfo? setter)
    {
        string declaration;

        if (IsGetterOnlyProperty(getter, setter))
        {
            declaration = $"{propertyName}(): {GetGetterOnlyPropertyMethodReturnType(getter!.ReturnType)}";
        }
        else if (getter?.ReturnType is { } returnType && IsDictionaryType(returnType))
        {
            var keyType = returnType.KeyType is not null ? MapTypeRefToTypeScript(returnType.KeyType) : "string";
            var valueType = returnType.ValueType is not null ? MapTypeRefToTypeScript(returnType.ValueType) : "unknown";
            declaration = $"readonly {propertyName}: AspireDict<{keyType}, {valueType}>";
        }
        else if (getter?.ReturnType is { } listReturnType && IsListType(listReturnType))
        {
            var elementType = listReturnType.ElementType is not null ? MapTypeRefToTypeScript(listReturnType.ElementType) : "unknown";
            declaration = $"readonly {propertyName}: AspireList<{elementType}>";
        }
        else
        {
            var accessors = new List<string>();
            if (getter is not null)
            {
                var getReturn = TryGetPromiseWrapperType(getter.ReturnType, out var promiseInterfaceName, out _)
                    ? promiseInterfaceName
                    : $"Promise<{MapTypeRefToTypeScript(getter.ReturnType)}>";
                accessors.Add($"get: () => {getReturn}");
            }

            if (setter?.Parameters.FirstOrDefault(p => p.Name == "value") is { } valueParam)
            {
                accessors.Add($"set: (value: {MapInputTypeToTypeScript(valueParam.Type)}) => Promise<void>");
            }

            declaration = $"{propertyName}: {{ {string.Join("; ", accessors)} }}";
        }

        var documentation = getter?.Documentation ?? setter?.Documentation;

        return new TypeScriptApiMember
        {
            Id = $"property:{ownerName}.{propertyName}",
            Kind = TypeScriptApiItemKind.Property,
            Name = propertyName,
            Declaration = declaration,
            Summary = documentation?.Summary,
            Remarks = documentation?.Remarks,
            DeprecationMessage = (getter ?? setter) is { IsObsolete: true } obsolete ? obsolete.ObsoleteMessage ?? string.Empty : null,
            CapabilityId = (getter ?? setter)?.CapabilityId,
            OwningAssemblyName = (getter ?? setter) is { } capability ? GetCapabilityOwningAssemblyName(capability) : null
        };
    }

    private (TypeScriptApiItem Item, TypeScriptApiDeclaration Declaration) ProjectEntryPoint(AtsCapabilityInfo capability)
    {
        var signature = ResolveEntryPointSignature(capability);
        var owningAssemblyName = GetCapabilityOwningAssemblyName(capability);

        var item = new TypeScriptApiItem
        {
            Id = $"entrypoint:{owningAssemblyName}:{signature.MethodName}",
            TypeId = capability.CapabilityId,
            Kind = TypeScriptApiItemKind.Method,
            Name = signature.MethodName,
            Declaration = $"function {signature.Declaration}",
            OwningAssemblyName = owningAssemblyName,
            Summary = capability.Documentation?.Summary,
            Remarks = capability.Documentation?.Remarks,
            Members = []
        };

        return (item, new TypeScriptApiDeclaration
        {
            Id = $"{owningAssemblyName}:entrypoint:{signature.MethodName}",
            Content = $"export declare {item.Declaration};",
            OwningAssemblyName = owningAssemblyName
        });
    }

    /// <summary>
    /// Resolves the signature of an entry-point capability -- one that hangs off the client rather
    /// than a builder type -- for both the emitted function and the exported declaration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Entry points are shaped unlike every other capability, which is why they cannot share
    /// <see cref="ResolveMethodSignature"/>. They are free functions rather than members, so the
    /// client has to be passed explicitly as the first parameter, and their optional arguments stay
    /// positional instead of collapsing into an options bag.
    /// </para>
    /// <para>
    /// Routing <see cref="ProjectEntryPoint"/> through <see cref="ResolveMethodSignature"/> gave the
    /// export the member shape -- no <c>client</c>, optionals folded into an options interface --
    /// while <c>GenerateEntryPointFunction</c> emitted the free-function shape. Consumers type-check
    /// the exported declarations against the generated SDK, so the two disagreeing produced
    /// declarations that did not describe any callable function.
    /// </para>
    /// </remarks>
    internal TypeScriptApiMethodSignature ResolveEntryPointSignature(AtsCapabilityInfo capability)
    {
        ArgumentNullException.ThrowIfNull(capability);

        var (requiredParameters, _) = SeparateParameters(capability.Parameters);

        var parameters = new List<TypeScriptApiParameter>
        {
            new() { Name = EntryPointClientParameterName, DeclaredType = EntryPointClientParameterType, IsOptional = false }
        };

        foreach (var parameter in capability.Parameters)
        {
            parameters.Add(new TypeScriptApiParameter
            {
                Name = parameter.Name,
                DeclaredType = MapParameterToTypeScript(parameter),
                IsOptional = parameter.IsOptional || parameter.IsNullable,
                Summary = parameter.Documentation?.Summary
            });
        }

        return new TypeScriptApiMethodSignature
        {
            MethodName = capability.MethodName,
            ReturnType = ResolveEntryPointReturnType(capability),
            Parameters = parameters,
            RequiredParameters = requiredParameters
        };
    }

    private string ResolveEntryPointReturnType(AtsCapabilityInfo capability)
    {
        var returnTypeId = capability.ReturnType?.TypeId;

        // A capability that returns a wrapped handle is emitted as a fluent function returning the
        // promise wrapper directly, so it is already thenable and is not wrapped again.
        if (GetPromiseWrapperForReturnType(capability.ReturnType) is { } promiseWrapper && !string.IsNullOrEmpty(returnTypeId))
        {
            return promiseWrapper;
        }

        return $"Promise<{(string.IsNullOrEmpty(returnTypeId) ? "void" : MapTypeRefToTypeScript(capability.ReturnType))}>";
    }

    private static (TypeScriptApiItem Item, TypeScriptApiDeclaration Declaration) ProjectEnum(AtsEnumTypeInfo enumType)
    {
        var owningAssemblyName = GetOwningAssemblyName(enumType.TypeId, enumType.ClrType?.Assembly.GetName().Name);

        var values = enumType.ValueInfos.Count > 0
            ? enumType.ValueInfos
            : [.. enumType.Values.Select(value => new AtsEnumValueInfo { Name = value })];

        var members = values
            .Select(value => new TypeScriptApiMember
            {
                Id = $"enumValue:{enumType.Name}.{value.Name}",
                Kind = TypeScriptApiItemKind.Property,
                Name = value.Name,
                Declaration = $"{value.Name} = \"{value.Name}\"",
                Summary = value.Documentation?.Summary,
                OwningAssemblyName = owningAssemblyName
            })
            .ToList();

        var item = new TypeScriptApiItem
        {
            Id = $"enum:{enumType.Name}",
            TypeId = enumType.TypeId,
            Kind = TypeScriptApiItemKind.Enum,
            Name = enumType.Name,
            Declaration = $"export enum {enumType.Name}",
            OwningAssemblyName = owningAssemblyName,
            Summary = enumType.Documentation?.Summary,
            Remarks = enumType.Documentation?.Remarks,
            Members = members
        };

        var body = new StringBuilder();
        body.Append("export enum ").Append(enumType.Name).Append(" {\n");
        foreach (var member in members)
        {
            body.Append("    ").Append(member.Declaration).Append(",\n");
        }
        body.Append('}');

        return (item, new TypeScriptApiDeclaration
        {
            Id = $"{item.OwningAssemblyName}:enum:{enumType.Name}",
            Content = body.ToString(),
            OwningAssemblyName = item.OwningAssemblyName
        });
    }

    /// <summary>
    /// Properties the TypeScript client adds to a DTO that has no C# counterpart. The emitter used to
    /// own this list, so the exported interface described fewer properties than the module we actually
    /// ship. Both paths read it from here now.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<ClientOnlyDtoProperty>> s_clientOnlyDtoProperties =
        new Dictionary<string, IReadOnlyList<ClientOnlyDtoProperty>>(StringComparer.Ordinal)
        {
            ["CreateBuilderOptions"] =
            [
                new ClientOnlyDtoProperty(
                    "throwOnPendingRejections",
                    "boolean",
                    "When false, pre-flush rejected promises are not re-thrown by build(). Default: true.")
            ]
        };

    internal static IReadOnlyList<ClientOnlyDtoProperty> GetClientOnlyDtoProperties(string interfaceName)
        => s_clientOnlyDtoProperties.TryGetValue(interfaceName, out var properties) ? properties : [];

    private (TypeScriptApiItem Item, TypeScriptApiDeclaration Declaration) ProjectDto(AtsDtoTypeInfo dtoType)
    {
        var interfaceName = GetDtoInterfaceName(dtoType.TypeId);
        var owningAssemblyName = GetOwningAssemblyName(dtoType.TypeId, dtoType.ClrType?.Assembly.GetName().Name);

        var members = dtoType.Properties
            .Select(property =>
            {
                var propertyName = ToCamelCase(property.Name);
                var propertyType = property.IsCallback
                    ? GenerateCallbackTypeSignature(property.CallbackParameters, property.CallbackReturnType)
                    : MapDtoPropertyTypeToTypeScript(property.Type);
                return new TypeScriptApiMember
                {
                    Id = $"property:{interfaceName}.{propertyName}",
                    Kind = TypeScriptApiItemKind.Property,
                    Name = propertyName,
                    Declaration = $"{propertyName}?: {propertyType}",
                    Summary = property.Documentation?.Summary ?? property.Description,
                    OwningAssemblyName = owningAssemblyName
                };
            })
            .ToList();

        members.AddRange(GetClientOnlyDtoProperties(interfaceName).Select(property => new TypeScriptApiMember
        {
            Id = $"property:{interfaceName}.{property.Name}",
            Kind = TypeScriptApiItemKind.Property,
            Name = property.Name,
            Declaration = $"{property.Name}?: {property.Type}",
            Summary = property.Summary,
            OwningAssemblyName = owningAssemblyName
        }));

        var item = new TypeScriptApiItem
        {
            Id = $"dto:{interfaceName}",
            TypeId = dtoType.TypeId,
            Kind = TypeScriptApiItemKind.Dto,
            Name = interfaceName,
            Declaration = $"export interface {interfaceName}",
            OwningAssemblyName = owningAssemblyName,
            Summary = dtoType.Documentation?.Summary,
            Remarks = dtoType.Documentation?.Remarks,
            Members = members
        };

        var body = new StringBuilder();
        body.Append("export interface ").Append(interfaceName).Append(" {\n");
        foreach (var member in members)
        {
            body.Append("    ").Append(member.Declaration).Append(";\n");
        }
        body.Append('}');

        return (item, new TypeScriptApiDeclaration
        {
            Id = $"{item.OwningAssemblyName}:dto:{interfaceName}",
            Content = body.ToString(),
            OwningAssemblyName = item.OwningAssemblyName
        });
    }

    private (TypeScriptApiItem Item, TypeScriptApiDeclaration Declaration) ProjectOptionsInterface(
        string owningAssemblyName,
        string interfaceName,
        List<AtsParameterInfo> optionalParams)
    {
        var members = optionalParams
            .Select(param => new TypeScriptApiMember
            {
                Id = $"property:{interfaceName}.{param.Name}",
                Kind = TypeScriptApiItemKind.Property,
                Name = param.Name,
                Declaration = $"{param.Name}?: {MapParameterToTypeScript(param)}",
                Summary = param.Documentation?.Summary,
                OwningAssemblyName = owningAssemblyName
            })
            .ToList();

        var item = new TypeScriptApiItem
        {
            Id = $"options:{interfaceName}",
            TypeId = $"{owningAssemblyName}/{interfaceName}",
            Kind = TypeScriptApiItemKind.Options,
            Name = interfaceName,
            Declaration = $"export interface {interfaceName}",
            OwningAssemblyName = owningAssemblyName,
            Members = members
        };

        var body = new StringBuilder();
        body.Append("export interface ").Append(interfaceName).Append(" {\n");
        foreach (var member in members)
        {
            body.Append("    ").Append(member.Declaration).Append(";\n");
        }
        body.Append('}');

        return (item, new TypeScriptApiDeclaration
        {
            Id = $"{owningAssemblyName}:options:{interfaceName}",
            Content = body.ToString(),
            OwningAssemblyName = owningAssemblyName
        });
    }

    private static string BuildInterfaceHeader(string interfaceName, IReadOnlyList<string> extends)
        => extends.Count > 0
            ? $"export interface {interfaceName} extends {string.Join(", ", extends)}"
            : $"export interface {interfaceName}";

    /// <summary>
    /// Resolves the owning assembly from the leading segment of an ATS identifier.
    /// </summary>
    /// <remarks>
    /// ATS identifiers are <c>{Prefix}/{FullTypeNameOrMemberName}</c>, for example
    /// <c>Aspire.Hosting.Redis/RedisResource</c> or <c>Aspire.Hosting.Redis/addRedis</c>. The prefix
    /// is usually the assembly name, but instance members carry the declaring namespace instead
    /// (<c>Contoso.Widgets.Model/WidgetContext.name</c>), so this is only a fallback for symbols
    /// that carry no CLR reflection info. Enum type IDs use the <c>enum:</c> prefix and have no
    /// segment at all, so the caller supplies the CLR assembly name.
    /// </remarks>
    private static string GetOwningAssemblyName(string atsId, string? clrAssemblyName = null)
    {
        if (clrAssemblyName is { Length: > 0 })
        {
            return clrAssemblyName;
        }

        var separatorIndex = atsId.IndexOf('/');
        return separatorIndex > 0 ? atsId[..separatorIndex] : string.Empty;
    }

    /// <summary>
    /// Resolves the assembly that owns a capability, preferring CLR reflection info over the
    /// identifier prefix so that instance members — whose IDs are namespace-qualified rather than
    /// assembly-qualified — are attributed to the package that actually declares them.
    /// </summary>
    /// <remarks>
    /// This mirrors <c>AtsContextFilter.IsCapabilityOwnedBySelectedAssembly</c>. The two must agree,
    /// or the exporter would document symbols the filter excluded, or drop symbols it kept.
    /// </remarks>
    private string GetCapabilityOwningAssemblyName(AtsCapabilityInfo capability)
        => GetCapabilityOwningAssemblyName(_resolved.Context, capability);

    /// <inheritdoc cref="GetCapabilityOwningAssemblyName(AtsCapabilityInfo)"/>
    /// <remarks>
    /// Takes the context explicitly so <see cref="Resolve"/> can attribute capabilities while it is
    /// still building the model that <c>_resolved</c> will hold.
    /// </remarks>
    private static string GetCapabilityOwningAssemblyName(AtsContext context, AtsCapabilityInfo capability)
    {
        if (context.Methods.TryGetValue(capability.CapabilityId, out var method))
        {
            return method.DeclaringType?.Assembly.GetName().Name ?? string.Empty;
        }

        if (context.Properties.TryGetValue(capability.CapabilityId, out var property))
        {
            return property.DeclaringType?.Assembly.GetName().Name ?? string.Empty;
        }

        return GetOwningAssemblyName(capability.CapabilityId, capability.TargetType?.ClrType?.Assembly.GetName().Name);
    }

    /// <summary>
    /// Resolves the assembly that owns a handle type, preferring CLR reflection info for the same
    /// reason as <see cref="GetCapabilityOwningAssemblyName(AtsCapabilityInfo)"/>.
    /// </summary>
    private string GetTypeOwningAssemblyName(string typeId)
        => GetOwningAssemblyName(typeId, _typeRefsById.GetValueOrDefault(typeId)?.ClrType?.Assembly.GetName().Name);

    // Mapping of typeId -> wrapper class name for all generated wrapper types
    // Used to resolve parameter types to wrapper classes instead of handle types
    private readonly Dictionary<string, string> _wrapperClassNames = new(StringComparer.Ordinal);

    // Wrapper classes are deduplicated by generated class name, but their handles are branded by
    // TypeId. Keep the retained TypeId so every canonical implementation receives its branded handle.
    private readonly Dictionary<string, string> _concreteTypeIds = new(StringComparer.Ordinal);

    private readonly Dictionary<string, AtsTypeRef> _typeRefsById = new(StringComparer.Ordinal);

    // Set of type IDs that have Promise wrappers (chainable or directly returned resource builders)
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

    // Mapping from options interface name to the assembly that owns it. An interface belongs to the
    // assembly whose capability produced it, which is not necessarily the package an export was
    // requested for: a scan holds several assemblies, and only some of them are being documented.

    private readonly Dictionary<string, string> _optionsInterfaceOwningAssemblies = new(StringComparer.Ordinal);

    // Mapping of enum type IDs to TypeScript enum names

    private readonly Dictionary<string, string> _enumTypeNames = new(StringComparer.Ordinal);

    // Mapping of handle type IDs to XML documentation captured during ATS scanning.

    private readonly Dictionary<string, AtsDocumentationInfo> _handleDocumentationById = new(StringComparer.Ordinal);

    // Mapping of DTO type IDs to DTO metadata for generated argument marshalling.

    private readonly Dictionary<string, AtsDtoTypeInfo> _dtoTypesById = new(StringComparer.Ordinal);

    internal static string GetInterfaceName(string className) => className;

    internal static string GetPromiseInterfaceName(string className) => $"{className}Promise";

    internal static string GetImplementationClassName(string className) => $"{className}Impl";

    internal static string GetImplementationPromiseClassName(string className) => $"{className}PromiseImpl";

    internal static string GetReferenceExpressionInterfaceName() => "ReferenceExpression";

    internal static string GetCancellationTokenInterfaceName() => "CancellationToken";

    internal static string GetHandleReferenceInterfaceName() => "HandleReference";

    internal static string GetInputTypeEnumName() => "InputType";

    internal static string GetInteractionInputInterfaceName() => "InteractionInput";

    internal static string GetInteractionInputCollectionClassName() => "InteractionInputCollection";

    internal const string InputTypeTypeId = "enum:Aspire.Hosting.InputType";
    internal const string InteractionInputTypeId = "Aspire.Hosting/Aspire.Hosting.InteractionInput";

    internal const string InteractionInputCollectionTypeId = "Aspire.Hosting/Aspire.Hosting.InteractionInputCollection";

    internal string GetConcreteClassName(string typeId) => _wrapperClassNames.GetValueOrDefault(typeId)
        ?? DeriveClassName(typeId);

    internal string GetConcreteTypeId(string typeId) => _concreteTypeIds.GetValueOrDefault(typeId)
        ?? typeId;

    internal string GetConcreteHandleTypeName(string typeId) => GetHandleTypeName(GetConcreteTypeId(typeId));

    internal string GetPublicPromiseInterfaceName(string typeId) => GetPromiseInterfaceName(GetConcreteClassName(typeId));

    internal static bool IsHandleType(AtsTypeRef? typeRef) =>
        typeRef is { Category: AtsTypeCategory.Handle };

    /// <summary>
    /// Maps an AtsTypeRef to a TypeScript type using category-based dispatch.
    /// This is the preferred method - uses type metadata rather than string parsing.
    /// </summary>

    internal string MapTypeRefToTypeScript(AtsTypeRef? typeRef)
    {
        if (typeRef is null)
        {
            return "unknown";
        }

        // ReferenceExpression is a value type defined in base.mts, not a handle-based wrapper
        if (typeRef.TypeId == AtsConstants.ReferenceExpressionTypeId)
        {
            return GetReferenceExpressionInterfaceName();
        }

        if (typeRef.TypeId == InputTypeTypeId)
        {
            return GetInputTypeEnumName();
        }

        if (typeRef.TypeId == InteractionInputTypeId)
        {
            return GetInteractionInputInterfaceName();
        }

        if (typeRef.TypeId == InteractionInputCollectionTypeId)
        {
            return GetInteractionInputCollectionClassName();
        }

        // Check for wrapper class first (handles custom types like resource builders)
        if (_wrapperClassNames.TryGetValue(typeRef.TypeId, out var wrapperClassName))
        {
            return GetInterfaceName(wrapperClassName);
        }

        var mappedType = typeRef.Category switch
        {
            AtsTypeCategory.Primitive => MapPrimitiveType(typeRef.TypeId),
            AtsTypeCategory.Enum => MapEnumType(typeRef.TypeId),
            AtsTypeCategory.Handle => GetWrapperOrHandleName(typeRef.TypeId),
            AtsTypeCategory.Dto => GetDtoInterfaceName(typeRef.TypeId),
            AtsTypeCategory.Callback => "Function",  // Callbacks handled separately with full signature
            AtsTypeCategory.Array => FormatArrayType(typeRef.ElementType, MapTypeRefToTypeScript(typeRef.ElementType)),
            AtsTypeCategory.List => $"AspireList<{MapTypeRefToTypeScript(typeRef.ElementType)}>",
            AtsTypeCategory.Dict => typeRef.IsReadOnly
                ? $"Record<{MapTypeRefToTypeScript(typeRef.KeyType)}, {MapTypeRefToTypeScript(typeRef.ValueType)}>"
                : $"AspireDict<{MapTypeRefToTypeScript(typeRef.KeyType)}, {MapTypeRefToTypeScript(typeRef.ValueType)}>",
            AtsTypeCategory.Union => MapUnionTypeToTypeScript(typeRef),
            AtsTypeCategory.Unknown => "any",  // Unknown types use 'any' since they're not in the ATS universe
            _ => "any"  // Fallback for any unhandled categories
        };
        return ApplyNullableType(typeRef, mappedType);
    }

    internal static string ApplyNullableType(AtsTypeRef typeRef, string mappedType)
    {
        if (typeRef.IsNullable != true || typeRef.Category is not (AtsTypeCategory.Primitive or AtsTypeCategory.Enum))
        {
            return mappedType;
        }

        return typeRef.TypeId is AtsConstants.Void or AtsConstants.Any or AtsConstants.CancellationToken
            ? mappedType
            : $"{mappedType} | null";
    }

    internal string MapDtoPropertyTypeToTypeScript(AtsTypeRef? typeRef)
    {
        if (typeRef is null)
        {
            return "unknown";
        }

        return typeRef.Category switch
        {
            AtsTypeCategory.Array or AtsTypeCategory.List => FormatArrayType(typeRef.ElementType, MapDtoPropertyTypeToTypeScript(typeRef.ElementType)),
            AtsTypeCategory.Dict => $"Record<{MapDtoPropertyTypeToTypeScript(typeRef.KeyType)}, {MapDtoPropertyTypeToTypeScript(typeRef.ValueType)}>",
            AtsTypeCategory.Union => MapDtoUnionTypeToTypeScript(typeRef),
            _ => MapTypeRefToTypeScript(typeRef)
        };
    }

    private static string FormatArrayType(AtsTypeRef? elementType, string mappedElementType)
    {
        var requiresGrouping = elementType?.Category == AtsTypeCategory.Union ||
            elementType is { IsNullable: true, Category: AtsTypeCategory.Primitive or AtsTypeCategory.Enum };

        return requiresGrouping ? $"({mappedElementType})[]" : $"{mappedElementType}[]";
    }

    internal string MapDtoUnionTypeToTypeScript(AtsTypeRef typeRef)
    {
        if (typeRef.UnionTypes is null || typeRef.UnionTypes.Count == 0)
        {
            return "unknown";
        }

        var memberTypes = typeRef.UnionTypes
            .Select(MapDtoPropertyTypeToTypeScript)
            .Distinct();

        return string.Join(" | ", memberTypes);
    }

    /// <summary>
    /// Maps primitive type IDs to TypeScript types.
    /// </summary>

    internal static string MapPrimitiveType(string typeId) => typeId switch
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

    internal string MapEnumType(string typeId)
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

    internal string MapUnionTypeToTypeScript(AtsTypeRef typeRef)
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

    internal string GetWrapperOrHandleName(string typeId)
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

    internal static string GetDtoInterfaceName(string typeId)
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

    internal string MapInputTypeToTypeScript(AtsTypeRef? typeRef)
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

        if (typeRef?.TypeId == InteractionInputCollectionTypeId)
        {
            return $"Awaitable<{GetInteractionInputCollectionClassName()}>";
        }

        if (IsCancellationTokenType(typeRef))
        {
            return $"AbortSignal | {GetCancellationTokenInterfaceName()}";
        }

        return MapTypeRefToTypeScript(typeRef);
    }

    internal string MapInputUnionTypeToTypeScript(AtsTypeRef typeRef)
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

    internal string MapParameterToTypeScript(AtsParameterInfo param)
    {
        if (param.IsCallback)
        {
            return GenerateCallbackTypeSignature(param.CallbackParameters, param.CallbackReturnType);
        }

        return MapInputTypeToTypeScript(param.Type);
    }

    internal string? TryMapInterfaceInputTypeToTypeScript(AtsTypeRef typeRef)
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

    internal static bool IsAssignableToInterface(AtsTypeRef candidateTypeRef, string interfaceTypeId)
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

    internal static bool IsInterfaceHandleType(AtsTypeRef? typeRef)
    {
        if (typeRef == null)
        {
            return false;
        }
        return typeRef.Category == AtsTypeCategory.Handle && typeRef.IsInterface;
    }

    internal static bool IsCancellationTokenType(AtsTypeRef? typeRef) => typeRef?.TypeId == AtsConstants.CancellationToken;

    /// <summary>
    /// Gets a valid TypeScript method name from a capability method name.
    /// Handles dotted names like "EnvironmentContext.resource" by extracting just the final part.
    /// </summary>

    internal static string GetTypeScriptMethodName(string methodName)
    {
        var dotIndex = methodName.LastIndexOf('.');
        return dotIndex >= 0 ? methodName[(dotIndex + 1)..] : methodName;
    }

    /// <summary>
    /// Converts a PascalCase name to camelCase.
    /// </summary>

    internal static string ToCamelCase(string name)
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

    internal static string ToPascalCase(string name)
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
    internal static string GetOptionsInterfaceName(string methodName)
    {
        var simpleName = methodName.Contains('.')
            ? methodName[(methodName.LastIndexOf('.') + 1)..]
            : methodName;
        return $"{ToPascalCase(simpleName)}Options";
    }

    /// <summary>
    /// Extracts the trailing segment of a capability ID, for example "withDataVolume" from
    /// "Aspire.Hosting.Azure.Storage/withDataVolume".
    /// </summary>
    /// <remarks>
    /// This only needs to be a better-than-nothing disambiguator, not a uniquely correct name. It is
    /// one rung of a widening ladder that starts at the projected method name and ends at a
    /// numerically suffixed name, so a collision here simply falls through to the next rung.
    /// </remarks>
    private static string GetCapabilityName(string capabilityId)
    {
        var slashIndex = capabilityId.LastIndexOf('/');

        return slashIndex >= 0 ? capabilityId[(slashIndex + 1)..] : capabilityId;
    }

    /// <summary>
    /// Gets the options interface name for a specific capability, accounting for type conflicts.
    /// Falls back to the default name derived from the capability if no specific mapping exists.
    /// </summary>

    internal string ResolveOptionsInterfaceName(AtsCapabilityInfo capability)
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

    internal static (List<AtsParameterInfo> Required, List<AtsParameterInfo> Optional) SeparateParameters(
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

    internal static bool TryGetDirectOptionsParameter(List<AtsParameterInfo> optionalParams, out AtsParameterInfo? directOptionsParam)
        // A trailing cancellation token is rendered as its own parameter (see
        // GetTrailingCancellationTokenParameter), so it is ignored when deciding whether the lone
        // "options" DTO can be threaded directly instead of wrapped in a generated options object.
        => AtsOptionsFlattening.TryGetDirectOptionsParameter(
            optionalParams,
            p => IsCancellationTokenType(p.Type),
            cancellationTokenIsSeparateParameter: true,
            out directOptionsParam);

    /// <summary>
    /// When the options DTO is threaded directly (see <see cref="TryGetDirectOptionsParameter"/>),
    /// returns the trailing cancellation token optional parameter (if any) so it can be appended to
    /// the generated method as its own argument rather than being folded into a generated options bag.
    /// </summary>

    internal static AtsParameterInfo? GetTrailingCancellationTokenParameter(List<AtsParameterInfo> optionalParams)
    {
        if (!TryGetDirectOptionsParameter(optionalParams, out _))
        {
            return null;
        }

        return optionalParams.FirstOrDefault(p => IsCancellationTokenType(p.Type));
    }

    /// <summary>
    /// Registers an options interface to be generated later. The interface name is derived from the
    /// projected method name; when capabilities share a method name but carry different option
    /// shapes, a separate interface is derived from the capability ID instead.
    /// </summary>
    /// <param name="capabilityId">The capability the interface is being registered for.</param>
    /// <param name="methodName">The method name the interface is derived from.</param>
    /// <param name="optionalParams">The optional parameters the interface carries.</param>
    /// <param name="owningAssemblyName">The assembly that exports <paramref name="capabilityId"/>.</param>
    internal void RegisterOptionsInterface(
        string capabilityId,
        string methodName,
        List<AtsParameterInfo> optionalParams,
        string owningAssemblyName)
    {
        if (optionalParams.Count == 0)
        {
            return;
        }

        var baseInterfaceName = GetOptionsInterfaceName(methodName);

        // Check if an existing interface with this name is compatible
        if (_optionsInterfacesToGenerate.TryGetValue(baseInterfaceName, out var existingParams))
        {
            var capabilityName = GetCapabilityName(capabilityId);
            if (!string.Equals(capabilityName, methodName, StringComparison.Ordinal)
                && !AreOptionsExactMatch(existingParams, optionalParams))
            {
                // Capabilities can share a projected method name while accepting different options.
                // Reusing the method-name interface would let callers pass options that the selected
                // capability implementation never reads, so fall back to the capability ID.
                RegisterDisambiguatedOptionsInterface(capabilityId, capabilityName, optionalParams, owningAssemblyName);
                return;
            }

            if (AreOptionsCompatible(existingParams, optionalParams))
            {
                // Compatible - merge any new parameters and share the interface
                AssignOptionsInterface(capabilityId, baseInterfaceName, optionalParams, owningAssemblyName);
                return;
            }

            RegisterDisambiguatedOptionsInterface(capabilityId, capabilityName, optionalParams, owningAssemblyName);
        }
        else
        {
            // First registration - create the interface
            AssignOptionsInterface(capabilityId, baseInterfaceName, optionalParams, owningAssemblyName);
        }
    }

    /// <summary>
    /// Registers an options interface named after the capability rather than the projected method
    /// name, widening to a numeric suffix only when the capability name itself collides.
    /// </summary>
    private void RegisterDisambiguatedOptionsInterface(
        string capabilityId,
        string capabilityName,
        List<AtsParameterInfo> optionalParams,
        string owningAssemblyName)
    {
        var capabilityInterfaceName = GetOptionsInterfaceName(capabilityName);
        if (!_optionsInterfacesToGenerate.TryGetValue(capabilityInterfaceName, out var capabilityParameters)
            || AreOptionsCompatible(capabilityParameters, optionalParams))
        {
            AssignOptionsInterface(capabilityId, capabilityInterfaceName, optionalParams, owningAssemblyName);
            return;
        }

        for (var suffix = 1; ; suffix++)
        {
            var suffixedName = GetOptionsInterfaceName($"{capabilityName}{suffix}");
            if (!_optionsInterfacesToGenerate.TryGetValue(suffixedName, out var suffixedParams)
                || AreOptionsCompatible(suffixedParams, optionalParams))
            {
                AssignOptionsInterface(capabilityId, suffixedName, optionalParams, owningAssemblyName);
                return;
            }
        }
    }

    /// <summary>
    /// Points a capability at a named options interface, creating the interface if this is its first
    /// use and otherwise widening it with any parameters it does not already carry.
    /// </summary>
    private void AssignOptionsInterface(
        string capabilityId,
        string interfaceName,
        List<AtsParameterInfo> optionalParams,
        string owningAssemblyName)
    {
        if (_optionsInterfacesToGenerate.TryGetValue(interfaceName, out var declaredParams))
        {
            foreach (var param in optionalParams)
            {
                var declaredIndex = declaredParams.FindIndex(
                    declared => string.Equals(declared.Name, param.Name, StringComparison.Ordinal));
                if (declaredIndex < 0)
                {
                    declaredParams.Add(param);
                }
                else if (declaredParams[declaredIndex].Documentation is null && param.Documentation is not null)
                {
                    // Compatible overloads can contribute the same option with different metadata.
                    // Keep the documented form regardless of which capability has the lower stable ID.
                    declaredParams[declaredIndex] = param;
                }
            }
        }
        else
        {
            _generatedOptionsInterfaces.Add(interfaceName);
            _optionsInterfacesToGenerate[interfaceName] = [.. optionalParams];
        }

        _capabilityOptionsInterfaceMap[capabilityId] = interfaceName;
        _optionsInterfaceOwningAssemblies[interfaceName] = owningAssemblyName;
    }

    /// <summary>
    /// Checks whether two sets of optional parameters are compatible for sharing an options interface.
    /// Parameters with the same name must have the same type (including callback parameter types).
    /// </summary>

    internal static bool AreOptionsCompatible(List<AtsParameterInfo> existing, List<AtsParameterInfo> candidate)
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
    /// Checks whether two option sets are positionally identical. Used to tell a genuine overload of
    /// the same shape apart from two distinct capabilities that merely project to the same name.
    /// </summary>
    private static bool AreOptionsExactMatch(List<AtsParameterInfo> existing, List<AtsParameterInfo> candidate)
    {
        if (existing.Count != candidate.Count)
        {
            return false;
        }

        for (var i = 0; i < existing.Count; i++)
        {
            if (!string.Equals(existing[i].Name, candidate[i].Name, StringComparison.Ordinal)
                || !AreParameterTypesEqual(existing[i], candidate[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Checks whether two parameter infos have the same type (including callback types).
    /// </summary>

    internal static bool AreParameterTypesEqual(AtsParameterInfo a, AtsParameterInfo b)
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

    internal static string GetTypeDescription(string typeId)
    {
        var typeName = ExtractSimpleTypeName(typeId);
        return $"Handle to {typeName}";
    }

    internal string BuildPublicParameterList(
        List<AtsParameterInfo> requiredParams,
        bool hasOptionals,
        string optionsInterfaceName,
        string optionsParameterName = "options",
        AtsParameterInfo? trailingCancellationToken = null)
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
        if (trailingCancellationToken is not null)
        {
            publicParamDefs.Add($"{trailingCancellationToken.Name}?: {MapParameterToTypeScript(trailingCancellationToken)}");
        }

        return string.Join(", ", publicParamDefs);
    }

    internal static string GetPublicOptionsParameterName(
        IReadOnlyList<AtsParameterInfo> userParams,
        bool hasOptionals,
        bool hasDirectOptionsParameter)
    {
        if (!hasOptionals || hasDirectOptionsParameter)
        {
            return "options";
        }

        var (requiredParams, optionalParams) = SeparateParameters(userParams);
        var trailingCancellationToken = GetTrailingCancellationTokenParameter(optionalParams);

        bool IsPublicParameterName(string name)
            => requiredParams.Any(p => string.Equals(p.Name, name, StringComparison.Ordinal))
                || string.Equals(trailingCancellationToken?.Name, name, StringComparison.Ordinal);

        if (!IsPublicParameterName("options"))
        {
            return "options";
        }

        var candidate = "optionsBag";
        while (IsPublicParameterName(candidate))
        {
            candidate = $"_{candidate}";
        }

        return candidate;
    }

    internal static string GetImplementationOptionsParameterName(
        IReadOnlyList<AtsParameterInfo> userParams,
        bool hasOptionals,
        bool hasDirectOptionsParameter)
    {
        if (!hasOptionals || hasDirectOptionsParameter)
        {
            return "options";
        }

        // Implementation methods destructure every optional field into a local with its source
        // parameter name. Unlike the public interface, their options-bag parameter must therefore
        // avoid optional names too (for example: const options = optionsBag?.options).
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

    internal static bool IsGetterOnlyProperty(AtsCapabilityInfo? getter, AtsCapabilityInfo? setter) => getter is not null && setter is null;

    internal string GetGetterOnlyPropertyReturnType(AtsTypeRef? typeRef)
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

    internal bool TryGetPromiseWrapperType(AtsTypeRef? typeRef, out string promiseInterfaceName, out string promiseImplementationClassName)
    {
        if (typeRef?.TypeId is { } typeId && _typesWithPromiseWrappers.Contains(typeId))
        {
            var className = GetConcreteClassName(typeId);
            promiseInterfaceName = GetPromiseInterfaceName(className);
            promiseImplementationClassName = GetImplementationPromiseClassName(className);
            return true;
        }

        promiseInterfaceName = string.Empty;
        promiseImplementationClassName = string.Empty;
        return false;
    }

    internal string GetGetterOnlyPropertyMethodReturnType(AtsTypeRef? typeRef)
    {
        if (TryGetPromiseWrapperType(typeRef, out var promiseInterfaceName, out _))
        {
            return promiseInterfaceName;
        }

        return $"Promise<{GetGetterOnlyPropertyReturnType(typeRef)}>";
    }

    internal string GetBuilderPromiseInterfaceForMethod(BuilderModel builder, AtsCapabilityInfo capability)
    {
        if (capability.ReturnsBuilder && capability.ReturnType?.TypeId != null &&
            !string.Equals(capability.ReturnType.TypeId, builder.TypeId, StringComparison.Ordinal) &&
            !string.Equals(capability.ReturnType.TypeId, capability.TargetTypeId, StringComparison.Ordinal))
        {
            return GetPublicPromiseInterfaceName(capability.ReturnType.TypeId);
        }

        return GetPromiseInterfaceName(builder.BuilderClassName);
    }

    /// <summary>
    /// Checks if a type was widened to accept Awaitable&lt;T&gt; in input position.
    /// Must match the widening logic in MapInputTypeToTypeScript exactly.
    /// </summary>

    internal bool IsWidenedHandleType(AtsTypeRef? typeRef)
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

        if (typeRef.TypeId == InteractionInputCollectionTypeId)
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
    /// Groups getters and setters by property name.
    /// </summary>

    internal static List<(string PropertyName, AtsCapabilityInfo? Getter, AtsCapabilityInfo? Setter)> GroupPropertiesByName(
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

    internal static string ExtractPropertyName(string methodName)
    {
        // Handle "ClassName.propertyName" format
        if (methodName.Contains('.'))
        {
            return methodName[(methodName.LastIndexOf('.') + 1)..];
        }
        return methodName;
    }

    /// <summary>
    /// Checks if a type reference is a dictionary type.
    /// </summary>

    internal static bool IsDictionaryType(AtsTypeRef? typeRef)
    {
        return typeRef?.Category == AtsTypeCategory.Dict;
    }

    /// <summary>
    /// Checks if a type reference is a list type.
    /// </summary>

    internal static bool IsListType(AtsTypeRef? typeRef)
    {
        return typeRef?.Category == AtsTypeCategory.List;
    }

    /// <summary>
    /// Groups capabilities by ExpandedTargetTypes to create builder models.
    /// Uses expansion to map interface targets to their concrete implementations.
    /// Also creates builders for interface types (for use as return type wrappers).
    /// </summary>

    internal static List<BuilderModel> CreateBuilderModels(IReadOnlyList<AtsCapabilityInfo> capabilities)
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

            // These types are implemented manually in base.mts, including handle wrapper
            // registrations, so they must not also generate duplicate wrappers in aspire.mts.
            if (targetTypeId is AtsConstants.ReferenceExpressionTypeId or InteractionInputCollectionTypeId)
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
            SortOptionsInterfaceCollisionsByCapabilityIdentity(uniqueCapabilities);

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
            SortOptionsInterfaceCollisionsByCapabilityIdentity(uniqueCapabilities);

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

        // Deduplicate a concrete type and its interfaces by class name. Unrelated CLR types can have
        // the same simple name, but treating them as aliases would bind one type's branded handle to
        // the other's wrapper implementation.
        return builders
            .OrderBy(builder => builder.IsInterface)
            .ThenBy(builder => builder.BuilderClassName)
            .GroupBy(builder => builder.BuilderClassName, StringComparer.Ordinal)
            .Select(group =>
            {
                var candidates = group
                    .OrderBy(builder => builder.IsInterface)
                    .ThenBy(builder => builder.TypeId, StringComparer.Ordinal)
                    .ToList();
                var retainedBuilder = candidates[0];
                var unrelatedBuilder = candidates
                    .Skip(1)
                    .FirstOrDefault(candidate => !IsBuilderAlias(retainedBuilder, candidate));

                if (unrelatedBuilder is not null)
                {
                    var collidingTypeIds = candidates
                        .Select(candidate => candidate.TypeId)
                        .Order(StringComparer.Ordinal);
                    throw new InvalidOperationException(
                        $"Resource types {string.Join(", ", collidingTypeIds.Select(typeId => $"'{typeId}'"))} " +
                        $"all map to the generated TypeScript name '{group.Key}', but they are not a concrete type and its interfaces.");
                }

                return retainedBuilder;
            })
            .ToList();
    }

    private static void SortOptionsInterfaceCollisionsByCapabilityIdentity(List<AtsCapabilityInfo> capabilities)
    {
        // Reorder only colliding option-interface slots. Sorting every capability would rewrite
        // long-established source order for methods unrelated to the collision.
        var collisionGroups = capabilities
            .Select((capability, index) => (Capability: capability, Index: index))
            .Where(entry =>
            {
                var (_, optionalParameters) = SeparateParameters(entry.Capability.Parameters);
                return optionalParameters.Count > 0 &&
                    !TryGetDirectOptionsParameter(optionalParameters, out _);
            })
            .GroupBy(
                entry => GetOptionsInterfaceName(entry.Capability.MethodName),
                StringComparer.Ordinal)
            .Where(group => group.Count() > 1);

        foreach (var group in collisionGroups)
        {
            var indexes = group.Select(entry => entry.Index).Order().ToList();
            var orderedCapabilities = group
                .Select(entry => entry.Capability)
                .OrderBy(capability => capability.CapabilityId, StringComparer.Ordinal)
                .ToList();

            for (var i = 0; i < indexes.Count; i++)
            {
                capabilities[indexes[i]] = orderedCapabilities[i];
            }
        }
    }

    private static bool IsBuilderAlias(BuilderModel retainedBuilder, BuilderModel candidate)
    {
        if (string.Equals(retainedBuilder.TypeId, candidate.TypeId, StringComparison.Ordinal))
        {
            return true;
        }

        if (retainedBuilder.IsInterface == candidate.IsInterface ||
            retainedBuilder.TargetType is not { } retainedType ||
            candidate.TargetType is not { } candidateType)
        {
            return false;
        }

        if (retainedType.ClrType is { } retainedClrType && candidateType.ClrType is { } candidateClrType)
        {
            return retainedClrType.IsAssignableFrom(candidateClrType) ||
                candidateClrType.IsAssignableFrom(retainedClrType);
        }

        return IsTypeInHierarchy(retainedType, candidateType.TypeId) ||
            IsTypeInHierarchy(candidateType, retainedType.TypeId);
    }

    private static bool IsTypeInHierarchy(AtsTypeRef typeRef, string typeId)
    {
        if (typeRef.ImplementedInterfaces.Any(interfaceType =>
            string.Equals(interfaceType.TypeId, typeId, StringComparison.Ordinal) ||
            IsTypeInHierarchy(interfaceType, typeId)))
        {
            return true;
        }

        return typeRef.BaseType is { } baseType &&
            (string.Equals(baseType.TypeId, typeId, StringComparison.Ordinal) ||
             IsTypeInHierarchy(baseType, typeId));
    }

    /// <summary>
    /// Collects all type refs referenced in capabilities (return types, parameter types, callback types, etc.)
    /// Returns a dictionary mapping typeId to AtsTypeRef for use in builder creation.
    /// </summary>

    internal static Dictionary<string, AtsTypeRef> CollectAllReferencedTypes(IReadOnlyList<AtsCapabilityInfo> capabilities)
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

    internal static List<AtsCapabilityInfo> GetEntryPointCapabilities(IReadOnlyList<AtsCapabilityInfo> capabilities)
    {
        return capabilities.Where(c => string.IsNullOrEmpty(c.TargetTypeId)).ToList();
    }

    /// <summary>
    /// Derives the class name from an ATS type ID.
    /// For interfaces like IResource, strips the leading 'I'.
    /// </summary>

    internal static string DeriveClassName(string typeId)
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

    internal static string GetHandleTypeName(string typeId)
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

    internal static string ExtractSimpleTypeName(string typeId)
    {
        var slashIndex = typeId.LastIndexOf('/');
        var fullTypeName = slashIndex >= 0 ? typeId[(slashIndex + 1)..] : typeId;

        var dotIndex = fullTypeName.LastIndexOf('.');
        return dotIndex >= 0 ? fullTypeName[(dotIndex + 1)..] : fullTypeName;
    }

    /// <summary>
    /// Determines if a type has generated async members and should have a Promise wrapper.
    /// Types with instance methods, wrapper methods, or getter-only properties get Promise wrappers.
    /// </summary>

    internal static bool HasChainableMethods(BuilderModel model)
    {
        var hasMethods = model.Capabilities.Any(c =>
            c.CapabilityKind == AtsCapabilityKind.InstanceMethod ||
            c.CapabilityKind == AtsCapabilityKind.Method);
        if (hasMethods)
        {
            return true;
        }

        var getters = model.Capabilities.Where(c => c.CapabilityKind == AtsCapabilityKind.PropertyGetter).ToList();
        var setters = model.Capabilities.Where(c => c.CapabilityKind == AtsCapabilityKind.PropertySetter).ToList();

        return GroupPropertiesByName(getters, setters).Any(p => IsGetterOnlyProperty(p.Getter, p.Setter));
    }

    /// <summary>
    /// Gets the Promise wrapper class name for a return type, if one exists.
    /// Returns null if the return type doesn't have a Promise wrapper.
    /// </summary>

    internal string? GetPromiseWrapperForReturnType(AtsTypeRef? returnType)
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

    internal string GenerateCallbackTypeSignature(IReadOnlyList<AtsCallbackParameterInfo>? callbackParameters, AtsTypeRef? callbackReturnType)
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

    private sealed class ExportedValueTreeNode
    {
        public Dictionary<string, ExportedValueTreeNode> Children { get; } = new(StringComparer.Ordinal);

        public AtsExportedValueInfo? Value { get; set; }
    }
}

/// <summary>
/// A DTO property that exists only on the TypeScript side, with the type and summary both the module
/// emitter and the API export render.
/// </summary>
internal sealed record ClientOnlyDtoProperty(string Name, string Type, string Summary);
