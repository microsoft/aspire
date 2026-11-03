// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Reflection;
using System.Text;
using Aspire.Shared.CodeGeneration;
using Aspire.TypeSystem;

namespace Aspire.Hosting.CodeGeneration.Go;

// The API export projection. It is part of the generator rather than a second reader of the ATS context
// because the Go names, type mappings, signatures and flattened options all come from the generator's
// private helpers. Calling them, and reading back the text the generator emits for the declarations whose
// shape is fixed (enums, DTOs, options structs, Build, CreateBuilder and the InteractionInputCollection
// helpers), keeps the export from drifting away from the generated SDK.
internal sealed partial class AtsGoCodeGenerator
{
    private const int ApiExportSchemaVersion = 1;

    // The module path GenerateDistributedApplication writes to go.mod.
    private const string ApiExportImportPath = "apphost/modules/aspire";

    // The hand-written runtime (base.go, transport.go) and the builder entry point are part of the core
    // SDK package, so the assembly that owns them is Aspire.Hosting.
    private const string ApiExportRuntimeOwner = "Aspire.Hosting";

    private const string ApiExportRuntimeDeclarationId = "aspire:runtime:base";

    /// <summary>
    /// Projects the Go API surface of <paramref name="context"/> that the exporting assemblies own.
    /// </summary>
    /// <remarks>
    /// The generator keeps per-run name state, so call this on a fresh instance and only once.
    /// </remarks>
    internal GoApiModel BuildApiModel(
        AtsContext context,
        string packageName,
        string packageVersion,
        IReadOnlyCollection<string> exportingAssemblyNames,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrEmpty(packageName);
        ArgumentException.ThrowIfNullOrEmpty(packageVersion);
        ArgumentNullException.ThrowIfNull(exportingAssemblyNames);

        BuildNameMaps(context);
        var scope = new ApiExportScope(context, packageName, exportingAssemblyNames);

        ProjectRuntime(scope);
        ProjectBuilderEntryPoint(scope);
        cancellationToken.ThrowIfCancellationRequested();

        ProjectGeneratedTypes(scope);
        ProjectExportedValues(scope);
        cancellationToken.ThrowIfCancellationRequested();

        ProjectHandleInterfaces(scope);
        ProjectAcceptingInterfaces(scope);

        return new GoApiModel
        {
            SchemaVersion = ApiExportSchemaVersion,
            Language = "go",
            Generator = CreateApiGeneratorIdentity(),
            Package = new GoApiPackageIdentity(packageName, packageVersion, ApiExportImportPath),
            Modules =
            [
                new GoApiModule
                {
                    Name = packageName,
                    Items = scope.Items.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToList()
                }
            ],
            Declarations = scope.Declarations.Values.OrderBy(declaration => declaration.Id, StringComparer.Ordinal).ToList()
        };
    }

    private static GoApiGeneratorIdentity CreateApiGeneratorIdentity()
    {
        var assembly = typeof(AtsGoCodeGenerator).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? throw new InvalidOperationException($"The '{assembly.GetName().Name}' assembly has no informational version.");

        return new GoApiGeneratorIdentity(assembly.GetName().Name!, version);
    }

    // Runs one of the Emit*/Generate* methods against a scratch writer and returns what it wrote. The
    // generator writes through _writer, so swapping it is the only way to reuse those methods without
    // changing them. Normalized to LF because the default NewLine is the platform's.
    private string CaptureEmitted(Action emit)
    {
        var previous = _writer;
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        _writer = writer;
        try
        {
            emit();
        }
        finally
        {
            _writer = previous;
        }

        return writer.ToString().ReplaceLineEndings("\n");
    }

    // ── Hand-written runtime ─────────────────────────────────────────────────

    private static void ProjectRuntime(ApiExportScope scope)
    {
        // The runtime is hand-written, so a construct the scanner cannot read has to fail the export
        // instead of going missing from it.
        var transport = GetEmbeddedResource("transport.go");
        var declarations = GoDeclarationScanner.ScanRuntime(transport)
            .Concat(GoDeclarationScanner.ScanRuntime(GetEmbeddedResource("base.go")))
            .ToList();

        // Every handle interface embeds handleReference, which requires Err(). The method is documented
        // once, on the unexported base type the wrappers share.
        scope.ErrMethod = declarations.FirstOrDefault(declaration =>
                declaration is { Kind: GoDeclarationKind.Method, Name: "Err", Owner: "handleWrapperBase" })
            ?? throw new InvalidOperationException("The Go runtime declares no handleWrapperBase.Err method.");

        // The scan skips unexported types, but every handle interface embeds handleReference and signatures
        // can refer to it, so the fragment has to declare it and the types it refers to: getHandle returns
        // a *handle, whose TypeID is a handleType. They are read from the source so that they cannot drift,
        // and they are not items because they are not part of the documented surface.
        var unexportedTypes = new[] { "handleReference", "handle", "handleType" }
            .Select(name => GoDeclarationScanner.ReadTypeDeclaration(transport, name))
            .ToList();

        var membersByOwner = declarations
            .Where(declaration => declaration.Kind is GoDeclarationKind.Method or GoDeclarationKind.Field or GoDeclarationKind.Constant)
            .ToLookup(declaration => declaration.Owner, StringComparer.Ordinal);

        // Signatures anywhere in the export can refer to these types, so the fragment is always present.
        scope.AddDeclaration(new GoApiDeclaration
        {
            Id = ApiExportRuntimeDeclarationId,
            Content = RenderRuntimeFragment(declarations, membersByOwner, unexportedTypes),
            OwningAssemblyName = ApiExportRuntimeOwner
        });

        if (!scope.IsOwned(ApiExportRuntimeOwner))
        {
            return;
        }

        foreach (var declaration in declarations)
        {
            switch (declaration.Kind)
            {
                case GoDeclarationKind.Type:
                    scope.AddItem(new GoApiItem
                    {
                        Id = $"type:{declaration.Name}",
                        TypeId = $"aspire:runtime:{declaration.Name}",
                        Kind = GoApiItemKind.Type,
                        Name = declaration.Name,
                        Declaration = declaration.Signature,
                        OwningAssemblyName = ApiExportRuntimeOwner,
                        Summary = declaration.Summary,
                        Remarks = declaration.Remarks,
                        Members = ProjectMembers(declaration.Name, membersByOwner[declaration.Name], methodErrorModel: null)
                    });
                    break;
                case GoDeclarationKind.Function:
                    scope.AddItem(new GoApiItem
                    {
                        Id = $"function:{declaration.Name}",
                        TypeId = $"aspire:runtime:{declaration.Name}",
                        Kind = GoApiItemKind.Function,
                        Name = declaration.Name,
                        Declaration = declaration.Signature,
                        OwningAssemblyName = ApiExportRuntimeOwner,
                        Summary = declaration.Summary,
                        Remarks = declaration.Remarks
                    });
                    break;
            }
        }
    }

    private static string RenderRuntimeFragment(
        IReadOnlyList<GoDeclaration> declarations,
        ILookup<string, GoDeclaration> membersByOwner,
        IReadOnlyList<string> unexportedTypes)
    {
        var blocks = new List<string>();
        foreach (var declaration in declarations)
        {
            if (declaration.Kind == GoDeclarationKind.Type)
            {
                blocks.Add(RenderTypeFragment(declaration, membersByOwner[declaration.Name].ToList()));
            }
            else if (declaration.Kind == GoDeclarationKind.Function)
            {
                blocks.Add(declaration.Signature);
            }
        }

        blocks.AddRange(unexportedTypes);

        return string.Join("\n\n", blocks);
    }

    // ── Builder entry point ──────────────────────────────────────────────────

    // CreateBuilder, Build and the builder and application interfaces the generator declares itself
    // when the model has no handle type for them have no ATS capability to project from.
    private void ProjectBuilderEntryPoint(ApiExportScope scope)
    {
        var declarations = GoDeclarationScanner.Scan(
            CaptureEmitted(() => GenerateCreateBuilder(scope.Context)),
            includeBodyDocs: false);

        scope.BuildMethod = declarations.FirstOrDefault(declaration =>
                declaration is { Kind: GoDeclarationKind.Method, Name: "Build" })
            ?? throw new InvalidOperationException("The Go generator emitted no Build method for the application builder.");
        var createBuilder = declarations.FirstOrDefault(declaration =>
                declaration is { Kind: GoDeclarationKind.Function, Name: "CreateBuilder" })
            ?? throw new InvalidOperationException("The Go generator emitted no CreateBuilder function.");

        if (scope.IsOwned(ApiExportRuntimeOwner))
        {
            scope.AddItem(new GoApiItem
            {
                Id = "function:CreateBuilder",
                TypeId = AtsConstants.CreateBuilderCapability,
                Kind = GoApiItemKind.Function,
                Name = createBuilder.Name,
                Declaration = createBuilder.Signature,
                OwningAssemblyName = ApiExportRuntimeOwner,
                Summary = createBuilder.Summary,
                Remarks = createBuilder.Remarks
            });
        }

        foreach (var type in declarations.Where(declaration =>
            declaration.Kind == GoDeclarationKind.Type && declaration.Signature.EndsWith(" interface", StringComparison.Ordinal)))
        {
            // CreateBuilder returns `(Builder, error)` and Build returns `(Application, error)`.
            var isBuilder = createBuilder.Signature.EndsWith($"({type.Name}, error)", StringComparison.Ordinal);
            var typeId = isBuilder ? AtsConstants.BuilderTypeId : AtsConstants.ApplicationTypeId;

            AddInterface(
                scope,
                typeId,
                type.Name,
                ApiExportRuntimeOwner,
                isMarker: !isBuilder,
                summary: type.Summary,
                remarks: type.Remarks,
                acceptingMarkerMembers: [],
                capabilityMembers: [],
                synthesizedMembers: ProjectSynthesizedMembers(scope, typeId, type.Name));
        }
    }

    // ── Enums, DTOs and options structs ──────────────────────────────────────

    private void ProjectGeneratedTypes(ApiExportScope scope)
    {
        var context = scope.Context;
        var declarations = GoDeclarationScanner.Scan(
            CaptureEmitted(() =>
            {
                GenerateEnumTypes(context.EnumTypes);
                GenerateDtoTypes(context.DtoTypes);
                GenerateOptionsStructs(context.Capabilities);
            }),
            includeBodyDocs: true);

        var membersByOwner = declarations
            .Where(declaration => declaration.Kind != GoDeclarationKind.Type)
            .ToLookup(declaration => declaration.Owner, StringComparer.Ordinal);

        var enumsByName = new Dictionary<string, AtsEnumTypeInfo>(StringComparer.Ordinal);
        foreach (var enumType in context.EnumTypes)
        {
            if (_enumNames.TryGetValue(enumType.TypeId, out var name))
            {
                enumsByName.TryAdd(name, enumType);
            }
        }

        var dtosByName = new Dictionary<string, AtsDtoTypeInfo>(StringComparer.Ordinal);
        foreach (var dtoType in context.DtoTypes)
        {
            if (_dtoNames.TryGetValue(dtoType.TypeId, out var name))
            {
                dtosByName.TryAdd(name, dtoType);
            }
        }

        var optionsByName = CollectOptionsProducers(context);

        foreach (var type in declarations.Where(declaration => declaration.Kind == GoDeclarationKind.Type))
        {
            var members = membersByOwner[type.Name].ToList();

            if (enumsByName.TryGetValue(type.Name, out var enumType))
            {
                var owner = GetOwningAssemblyName(enumType.TypeId, enumType.ClrType?.Assembly.GetName().Name);
                AddGeneratedType(scope, GoApiItemKind.Enum, "enum", enumType.TypeId, owner, type, members, usedBy: []);
            }
            else if (dtosByName.TryGetValue(type.Name, out var dtoType))
            {
                var owner = GetOwningAssemblyName(dtoType.TypeId, dtoType.ClrType?.Assembly.GetName().Name);
                AddGeneratedType(scope, GoApiItemKind.Dto, "dto", dtoType.TypeId, owner, type, members, usedBy: []);
            }
            else if (optionsByName.TryGetValue(type.Name, out var producer))
            {
                var owner = GetCapabilityOwningAssemblyName(context, producer.Capability);
                AddGeneratedType(scope, GoApiItemKind.Options, "options", $"{owner}/{type.Name}", owner, type, members, producer.UsedBy);
            }
        }
    }

    // Declarations are published for every type a signature can mention, owned or not, so a consumer can
    // resolve them. The documented item is published only by the package that owns the type.
    private static void AddGeneratedType(
        ApiExportScope scope,
        GoApiItemKind kind,
        string slug,
        string typeId,
        string owner,
        GoDeclaration type,
        List<GoDeclaration> members,
        IReadOnlyList<string> usedBy)
    {
        scope.AddDeclaration(new GoApiDeclaration
        {
            Id = $"{owner}:{slug}:{type.Name}",
            Content = RenderTypeFragment(type, members),
            OwningAssemblyName = owner
        });

        if (!scope.IsOwned(owner))
        {
            return;
        }

        scope.AddItem(new GoApiItem
        {
            Id = $"{slug}:{type.Name}",
            TypeId = typeId,
            Kind = kind,
            Name = type.Name,
            Declaration = type.Signature,
            OwningAssemblyName = owner,
            Summary = type.Summary,
            Remarks = type.Remarks,
            UsedBy = usedBy,
            Members = ProjectMembers(type.Name, members, GoApiErrorModel.None)
        });
    }

    // GenerateOptionsStructs emits one struct per name, for the first capability that needs it, so that
    // capability decides which package owns the struct. Capabilities that flatten a direct options DTO
    // have no struct.
    private Dictionary<string, (AtsCapabilityInfo Capability, List<string> UsedBy)> CollectOptionsProducers(AtsContext context)
    {
        var producers = new Dictionary<string, (AtsCapabilityInfo Capability, List<string> UsedBy)>(StringComparer.Ordinal);
        foreach (var capability in context.Capabilities)
        {
            var (_, optional) = SplitParameters(GetNonTargetParameters(capability));
            if (optional.Count == 0 || TryGetDirectOptionsParameter(optional, out _))
            {
                continue;
            }

            var name = GetOptionsTypeName(capability);
            if (producers.TryGetValue(name, out var producer))
            {
                producer.UsedBy.Add(capability.CapabilityId);
            }
            else
            {
                producers[name] = (capability, [capability.CapabilityId]);
            }
        }

        foreach (var (_, usedBy) in producers.Values)
        {
            usedBy.Sort(StringComparer.Ordinal);
        }

        return producers;
    }

    // ── Exported values ──────────────────────────────────────────────────────

    private void ProjectExportedValues(ApiExportScope scope)
    {
        var exportedValues = scope.Context.ExportedValues
            .Where(value => scope.IsOwned(value.OwningAssemblyName))
            .ToList();
        if (exportedValues.Count == 0)
        {
            return;
        }

        var dtoTypesById = scope.Context.DtoTypes.ToDictionary(dto => dto.TypeId, StringComparer.Ordinal);
        var root = BuildExportedValueTree(exportedValues);

        foreach (var (name, node) in root.Children.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var leaves = new List<(string Path, AtsExportedValueInfo Value)>();
            CollectExportedValueLeaves(node, string.Empty, leaves);
            if (leaves.Count == 0)
            {
                continue;
            }

            var owner = leaves[0].Value.OwningAssemblyName;
            // The API reference lists the declaration's shape only, so it leaves out the godoc comments that the
            // generated SDK writes on each field.
            var typeText = RenderGoExportedValueType(node, indentLevel: 0, includeDocs: false);

            scope.AddDeclaration(new GoApiDeclaration
            {
                Id = $"{owner}:variable:{name}",
                Content = $"var {name} {typeText}",
                OwningAssemblyName = owner
            });

            scope.AddItem(new GoApiItem
            {
                Id = $"variable:{name}",
                TypeId = $"{owner}/{name}",
                Kind = GoApiItemKind.Variable,
                Name = name,
                Declaration = $"var {name} = {typeText}{RenderGoExportedValueValue(node, dtoTypesById, indentLevel: 0)}".ReplaceLineEndings("\n"),
                OwningAssemblyName = owner,
                Members = leaves
                    .Select(leaf => new GoApiMember
                    {
                        Id = $"{name}.{leaf.Path}",
                        Kind = GoApiMemberKind.Field,
                        Name = leaf.Path,
                        Declaration = $"{leaf.Path} {MapTypeRefToGo(leaf.Value.Type, isOptional: false)}",
                        Summary = leaf.Value.Documentation?.Summary ?? leaf.Value.Description,
                        Value = RenderGoExportedValue(leaf.Value.Value, leaf.Value.Type, dtoTypesById),
                        OwningAssemblyName = leaf.Value.OwningAssemblyName
                    })
                    .ToList()
            });
        }
    }

    private static void CollectExportedValueLeaves(
        GoExportedValueTreeNode node,
        string prefix,
        List<(string Path, AtsExportedValueInfo Value)> leaves)
    {
        foreach (var (name, child) in node.Children.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (child.Value is { } value)
            {
                leaves.Add(($"{prefix}{name}", value));
            }
            else
            {
                CollectExportedValueLeaves(child, $"{prefix}{name}.", leaves);
            }
        }
    }

    // ── Handle interfaces ────────────────────────────────────────────────────

    private void ProjectHandleInterfaces(ApiExportScope scope)
    {
        var capabilitiesByConcreteImpl = GroupCapabilitiesByConcreteImpl(scope.Context.Capabilities);

        // Concrete types first, then the marker interfaces, each in the order the generator emits them.
        var typeIds = _implNames.Keys
            .OrderBy(typeId => _interfaceNames[typeId], StringComparer.Ordinal)
            .Concat(_interfaceNames
                .Where(pair => _isInterfaceType.TryGetValue(pair.Key, out var isInterface) && isInterface && !_implNames.ContainsKey(pair.Key))
                .OrderBy(pair => pair.Value, StringComparer.Ordinal)
                .Select(pair => pair.Key))
            .ToList();

        foreach (var typeId in typeIds)
        {
            var interfaceName = _interfaceNames[typeId];
            var isMarker = !_implNames.ContainsKey(typeId);
            var handleType = scope.GetHandleType(typeId);

            AddInterface(
                scope,
                typeId,
                interfaceName,
                scope.GetTypeOwner(typeId),
                isMarker,
                summary: handleType?.Documentation?.Summary,
                remarks: handleType?.Documentation?.Remarks,
                acceptingMarkerMembers: _acceptingMarkers.TryGetValue(typeId, out var markers)
                    ? ProjectAcceptingMarkerMembers(interfaceName, markers)
                    : [],
                capabilityMembers: isMarker ? [] : ProjectCapabilityMembers(scope, typeId, interfaceName, capabilitiesByConcreteImpl),
                synthesizedMembers: ProjectSynthesizedMembers(scope, typeId, interfaceName));
        }
    }

    // The Any<Type> accepting interfaces are public types in the parameters of the generated methods, so
    // they are exported like the handle interfaces they sit beside. Besides Err, which handleReference
    // requires, their only members are the unexported marker methods that restrict them to one family of
    // handle types.
    private void ProjectAcceptingInterfaces(ApiExportScope scope)
    {
        foreach (var (typeId, name) in _acceptingNames.OrderBy(pair => pair.Value, StringComparer.Ordinal))
        {
            var owner = scope.GetTypeOwner(typeId);

            // A package that does not own the base type only needs the name to resolve in its signatures.
            if (!scope.IsOwned(owner))
            {
                scope.AddDeclaration(new GoApiDeclaration
                {
                    Id = $"{owner}:opaque:{name}",
                    Content = RenderInterfaceFragment(name, []),
                    OwningAssemblyName = owner
                });

                continue;
            }

            var members = ProjectAcceptingMarkerMembers(name, _acceptingMarkers[typeId])
                .Concat(ProjectMembers(name, [scope.ErrMethod], GoApiErrorModel.None))
                .ToList();

            scope.AddDeclaration(new GoApiDeclaration
            {
                Id = $"{owner}:interface:{name}",
                Content = RenderInterfaceFragment(name, members),
                OwningAssemblyName = owner
            });

            scope.AddItem(new GoApiItem
            {
                Id = $"interface:{name}",
                TypeId = typeId,
                Kind = GoApiItemKind.Interface,
                Name = name,
                Declaration = $"type {name} interface",
                OwningAssemblyName = owner,
                Summary = $"{name} accepts {_interfaceNames[typeId]} or a handle of any type derived from it.",
                IsMarker = true,
                Members = members
            });
        }
    }

    private static List<GoApiMember> ProjectAcceptingMarkerMembers(string interfaceName, IEnumerable<string> markers)
        => ProjectMembers(
            interfaceName,
            markers.Select(marker => new GoDeclaration { Kind = GoDeclarationKind.Method, Name = marker, Signature = $"{marker}()" }),
            GoApiErrorModel.None);

    // An owning package documents the interface. A package that only adds capabilities to a type another
    // package owns contributes an augmentation, and the type is declared as an opaque stub so signatures
    // that mention it still resolve.
    private void AddInterface(
        ApiExportScope scope,
        string typeId,
        string interfaceName,
        string owner,
        bool isMarker,
        string? summary,
        string? remarks,
        IReadOnlyList<GoApiMember> acceptingMarkerMembers,
        IReadOnlyList<GoApiMember> capabilityMembers,
        IReadOnlyList<GoApiMember> synthesizedMembers)
    {
        var declaration = $"type {interfaceName} interface";

        if (!scope.IsOwned(owner))
        {
            scope.AddDeclaration(new GoApiDeclaration
            {
                Id = $"{owner}:opaque:{interfaceName}",
                Content = RenderInterfaceFragment(interfaceName, []),
                OwningAssemblyName = owner
            });

            if (capabilityMembers.Count > 0)
            {
                scope.AddItem(new GoApiItem
                {
                    Id = $"augmentation:{scope.PackageName}:{interfaceName}",
                    TypeId = typeId,
                    Kind = GoApiItemKind.Augmentation,
                    Name = interfaceName,
                    Declaration = declaration,
                    OwningAssemblyName = owner,
                    Members = capabilityMembers
                });
            }

            return;
        }

        // The generator writes the accepting markers right after handleReference, before the methods.
        var members = acceptingMarkerMembers.Concat(capabilityMembers).Concat(synthesizedMembers).ToList();

        scope.AddDeclaration(new GoApiDeclaration
        {
            Id = $"{owner}:interface:{interfaceName}",
            Content = RenderInterfaceFragment(interfaceName, members),
            OwningAssemblyName = owner
        });

        scope.AddItem(new GoApiItem
        {
            Id = $"interface:{interfaceName}",
            TypeId = typeId,
            Kind = GoApiItemKind.Interface,
            Name = interfaceName,
            Declaration = declaration,
            OwningAssemblyName = owner,
            Summary = summary,
            Remarks = remarks,
            IsMarker = isMarker,
            Implements = GetImplementedInterfaceNames(scope, typeId, interfaceName),
            Members = members
        });
    }

    private List<string> GetImplementedInterfaceNames(ApiExportScope scope, string typeId, string interfaceName)
    {
        if (scope.GetHandleType(typeId) is not { } handleType)
        {
            return [];
        }

        return handleType.ImplementedInterfaces
            .Select(implemented => _interfaceNames.GetValueOrDefault(implemented.TypeId))
            .OfType<string>()
            .Where(name => !string.Equals(name, interfaceName, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static string RenderInterfaceFragment(string interfaceName, IReadOnlyList<GoApiMember> members)
    {
        var builder = new StringBuilder();
        builder.Append("type ").Append(interfaceName).Append(" interface {\n");
        builder.Append("\thandleReference\n");
        foreach (var member in members)
        {
            builder.Append('\t').Append(member.Declaration).Append('\n');
        }

        builder.Append('}');
        return builder.ToString();
    }

    // The methods the generator writes on the interface for the type: capabilities in method-name order,
    // then the list and dictionary getters, as in EmitConcreteInterface.
    private List<GoApiMember> ProjectCapabilityMembers(
        ApiExportScope scope,
        string typeId,
        string interfaceName,
        Dictionary<string, List<AtsCapabilityInfo>> capabilitiesByConcreteImpl)
    {
        var members = new List<GoApiMember>();
        if (!capabilitiesByConcreteImpl.TryGetValue(typeId, out var capabilities))
        {
            return members;
        }

        var ordered = capabilities
            .Where(capability => !IsSpecialCasedCapability(capability.CapabilityId))
            .OrderBy(capability => capability.MethodName, StringComparer.Ordinal)
            .ThenBy(capability => capability.CapabilityId, StringComparer.Ordinal)
            .ToList();

        var getterCapabilityIds = CollectListDictGetters(ordered)
            .Select(getter => getter.CapabilityId)
            .ToHashSet(StringComparer.Ordinal);
        var getterMembers = new List<GoApiMember>();

        foreach (var capability in ordered)
        {
            // Capabilities of other packages that expand onto this type are documented by their own
            // package. A support capability the export filter adds for a foreign type resolves to
            // that foreign package too, so it never becomes a member.
            var owner = GetCapabilityOwningAssemblyName(scope.Context, capability);
            if (!scope.IsOwned(owner))
            {
                continue;
            }

            if (getterCapabilityIds.Contains(capability.CapabilityId))
            {
                getterMembers.Add(ProjectListDictGetter(interfaceName, capability, owner));
            }
            else
            {
                members.Add(ProjectCapabilityMember(typeId, interfaceName, capability, owner));
            }
        }

        members.AddRange(getterMembers);
        return members;
    }

    private GoApiMember ProjectCapabilityMember(
        string typeId,
        string interfaceName,
        AtsCapabilityInfo capability,
        string owner)
    {
        var (required, optional) = SplitParameters(GetNonTargetParameters(capability));
        var unionParameters = GetUnionParameters(capability);

        var returnType = capability.ReturnType;
        var hasReturn = returnType is not null && returnType.TypeId != AtsConstants.Void;
        var returnsHandle = hasReturn && returnType!.Category == AtsTypeCategory.Handle;

        GoApiOptions? options = null;
        if (optional.Count > 0)
        {
            options = TryGetDirectOptionsParameter(optional, out var directOptionsParameter)
                ? new GoApiOptions { TypeName = MapDtoType(directOptionsParameter.Type!.TypeId), IsDirect = true }
                : new GoApiOptions
                {
                    TypeName = GetOptionsTypeName(capability),
                    Fields = optional.Select(ProjectOptionsField).ToList()
                };
        }

        return CreateCapabilityMember(interfaceName, capability, owner, RenderMethodSignature(typeId, interfaceName, capability)) with
        {
            Parameters = required.Select(parameter => ProjectParameter(parameter, unionParameters)).ToList(),
            Options = options,
            ReturnType = RenderReturnSignature(typeId, interfaceName, capability).Trim(),
            // A handle is returned immediately and its failure is read later through Err(); every
            // other result carries the error itself.
            ErrorModel = returnsHandle ? GoApiErrorModel.Deferred : GoApiErrorModel.Returned,
            ReturnsReceiver = returnsHandle && IsSameType(returnType!.TypeId, capability, typeId)
        };
    }

    private GoApiMember ProjectListDictGetter(string interfaceName, AtsCapabilityInfo capability, string owner)
    {
        var (wrapperType, typeArguments) = ListDictTypeArgs(capability.ReturnType!);
        var returnType = $"*{wrapperType}{typeArguments}";

        return CreateCapabilityMember(interfaceName, capability, owner, $"{ToPascalCase(capability.MethodName)}() {returnType}") with
        {
            ReturnType = returnType,
            ErrorModel = GoApiErrorModel.None
        };
    }

    private static GoApiMember CreateCapabilityMember(
        string interfaceName,
        AtsCapabilityInfo capability,
        string owner,
        string declaration)
    {
        var name = ToPascalCase(capability.MethodName);

        return new GoApiMember
        {
            Id = $"{interfaceName}.{name}",
            Kind = GoApiMemberKind.Method,
            Name = name,
            Declaration = declaration,
            Summary = capability.Documentation?.Summary ?? capability.Description,
            Remarks = capability.Documentation?.Remarks,
            Returns = capability.Documentation?.Returns,
            IsExperimental = AtsTypeSystemCompatibility.IsExperimental(capability),
            DeprecationMessage = capability.IsObsolete ? capability.ObsoleteMessage ?? string.Empty : null,
            CapabilityId = capability.CapabilityId,
            TargetTypeId = capability.TargetTypeId,
            CapabilityKind = GetCapabilityKindName(capability.CapabilityKind),
            OwningAssemblyName = owner,
            ReturnsBuilder = capability.ReturnsBuilder
        };
    }

    private GoApiParameter ProjectParameter(AtsParameterInfo parameter, IReadOnlyList<AtsParameterInfo> unionParameters)
    {
        var name = GetLocalIdentifier(parameter.Name);

        return new GoApiParameter
        {
            Name = name,
            DeclaredType = RenderParameterType(parameter),
            IsCallback = parameter.IsCallback,
            JsonName = string.Equals(parameter.Name, name, StringComparison.Ordinal) ? null : parameter.Name,
            Summary = parameter.Documentation?.Summary,
            // A union member is a value the caller supplies, so it maps as an input: a base handle type is
            // listed as its Any<Type> interface, the type the emitted guard and godoc accept. The generator's
            // own helper keeps the two from drifting apart.
            AllowedTypes = unionParameters.Contains(parameter) ? GetUnionMemberTypes(parameter.Type!) : []
        };
    }

    private GoApiParameter ProjectOptionsField(AtsParameterInfo parameter)
    {
        // Callbacks and cancellation tokens never cross the wire as values; the generated body registers
        // them separately, so they have no JSON name.
        var isWireValue = !parameter.IsCallback && !IsCancellationToken(parameter);

        return new GoApiParameter
        {
            Name = ToPascalCase(parameter.Name),
            DeclaredType = RenderOptionsFieldType(parameter),
            IsOptional = true,
            IsCallback = parameter.IsCallback,
            JsonName = isWireValue ? parameter.Name : null,
            Summary = parameter.Documentation?.Summary
        };
    }

    private static List<AtsParameterInfo> GetNonTargetParameters(AtsCapabilityInfo capability)
    {
        var targetParameterName = capability.TargetParameterName ?? "builder";

        return capability.Parameters
            .Where(parameter => !string.Equals(parameter.Name, targetParameterName, StringComparison.Ordinal))
            .ToList();
    }

    private static string GetCapabilityKindName(AtsCapabilityKind kind) => kind switch
    {
        AtsCapabilityKind.Method => "method",
        AtsCapabilityKind.InstanceMethod => "instanceMethod",
        AtsCapabilityKind.PropertyGetter => "propertyGetter",
        AtsCapabilityKind.PropertySetter => "propertySetter",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    // The members the generator writes by hand around the capability methods: Build on the builder, the
    // by-name helpers on InteractionInputCollection, and Err on every handle interface.
    private List<GoApiMember> ProjectSynthesizedMembers(ApiExportScope scope, string typeId, string interfaceName)
    {
        var members = new List<GoApiMember>();

        if (string.Equals(typeId, AtsConstants.BuilderTypeId, StringComparison.Ordinal))
        {
            members.AddRange(ProjectMembers(interfaceName, [scope.BuildMethod], GoApiErrorModel.Returned)
                .Select(build => build with
                {
                    CapabilityId = AtsConstants.BuildCapability,
                    TargetTypeId = AtsConstants.BuilderTypeId,
                    OwningAssemblyName = ApiExportRuntimeOwner
                }));
        }

        if (string.Equals(typeId, InteractionInputCollectionTypeId, StringComparison.Ordinal))
        {
            var helpers = GoDeclarationScanner.Scan(
                CaptureEmitted(() => EmitInteractionInputCollectionAccessors("impl")),
                includeBodyDocs: false);
            members.AddRange(ProjectMembers(interfaceName, helpers.Where(helper => helper.Kind == GoDeclarationKind.Method), GoApiErrorModel.Returned));
        }

        members.AddRange(ProjectMembers(interfaceName, [scope.ErrMethod], GoApiErrorModel.None));
        return members;
    }

    // ── Scanned declarations ─────────────────────────────────────────────────

    // Every member produced here exists only in the Go SDK. It comes from hand-written or generator-written
    // text, not from an ATS capability, so it is marked synthesized.
    private static List<GoApiMember> ProjectMembers(
        string itemName,
        IEnumerable<GoDeclaration> declarations,
        GoApiErrorModel? methodErrorModel)
    {
        var members = new List<GoApiMember>();
        foreach (var declaration in declarations)
        {
            var member = new GoApiMember
            {
                Id = $"{itemName}.{declaration.Name}",
                Kind = declaration.Kind switch
                {
                    GoDeclarationKind.Field => GoApiMemberKind.Field,
                    GoDeclarationKind.Constant => GoApiMemberKind.Constant,
                    GoDeclarationKind.Method => GoApiMemberKind.Method,
                    _ => throw new InvalidOperationException($"A {declaration.Kind} declaration cannot be a member.")
                },
                Name = declaration.Name,
                Declaration = declaration.Kind == GoDeclarationKind.Field ? RenderFieldDeclaration(declaration) : declaration.Signature,
                Summary = declaration.Summary,
                Remarks = declaration.Remarks
            };

            members.Add(declaration.Kind switch
            {
                GoDeclarationKind.Field => member with { JsonName = ParseJsonName(declaration.Value) },
                GoDeclarationKind.Constant => member with { Value = declaration.Value },
                _ => member with
                {
                    ReturnType = GoDeclarationScanner.GetReturnType(declaration.Signature),
                    ErrorModel = methodErrorModel ?? InferErrorModel(declaration),
                    IsSynthesized = true
                }
            });
        }

        return members;
    }

    // The hand-written runtime has no ATS capability to take the model from, so it follows from the results
    // of the method: `error` and `(T, error)` hand the failure to the caller. Err is the exception because
    // it reports a failure that a handle deferred; as on a handle interface, calling it cannot fail.
    private static GoApiErrorModel InferErrorModel(GoDeclaration method)
    {
        var results = GoDeclarationScanner.GetReturnType(method.Signature);

        if (results is null || method.Signature == "Err() error")
        {
            return GoApiErrorModel.None;
        }

        return results == "error" || results.EndsWith(" error)", StringComparison.Ordinal)
            ? GoApiErrorModel.Returned
            : GoApiErrorModel.None;
    }

    private static string RenderFieldDeclaration(GoDeclaration field)
        => field.Value is null ? field.Signature : $"{field.Signature} `{field.Value}`";

    // Struct tags the generator writes:
    //   json:"name,omitempty"   -> name
    //   json:"-"                -> no JSON name (the field is not serialized)
    private static string? ParseJsonName(string? tag)
    {
        const string Prefix = "json:\"";

        var start = tag?.IndexOf(Prefix, StringComparison.Ordinal) ?? -1;
        if (start < 0)
        {
            return null;
        }

        start += Prefix.Length;
        var end = tag!.IndexOf('"', start);
        if (end < 0)
        {
            return null;
        }

        var name = tag[start..end];
        var comma = name.IndexOf(',');
        if (comma >= 0)
        {
            name = name[..comma];
        }

        return name.Length == 0 || name == "-" ? null : name;
    }

    // The signature-only declaration of a type and the constants, fields and methods that belong to it.
    private static string RenderTypeFragment(GoDeclaration type, IReadOnlyList<GoDeclaration> members)
    {
        var builder = new StringBuilder(type.Signature);

        if (type.Signature.EndsWith(" struct", StringComparison.Ordinal))
        {
            builder.Append(" {\n");
            foreach (var field in members.Where(member => member.Kind == GoDeclarationKind.Field))
            {
                builder.Append('\t').Append(RenderFieldDeclaration(field)).Append('\n');
            }

            builder.Append('}');
        }

        var constants = members.Where(member => member.Kind == GoDeclarationKind.Constant).ToList();
        if (constants.Count > 0)
        {
            builder.Append("\n\nconst (\n");
            foreach (var constant in constants)
            {
                builder.Append('\t').Append(constant.Signature).Append('\n');
            }

            builder.Append(')');
        }

        foreach (var method in members.Where(member => member.Kind == GoDeclarationKind.Method))
        {
            builder.Append("\n\nfunc (").Append(method.Receiver).Append(") ").Append(method.Signature);
        }

        return builder.ToString();
    }

    // ── Ownership ────────────────────────────────────────────────────────────

    // ATS identifiers are `{Prefix}/{FullTypeNameOrMemberName}`, for example
    // `Aspire.Hosting.Redis/RedisResource`. The prefix is usually the assembly name, but instance
    // members carry the declaring namespace instead, so it is only a fallback for symbols with no CLR
    // reflection info. Enum type IDs use an `enum:` prefix and have no segment at all.
    private static string GetOwningAssemblyName(string atsId, string? clrAssemblyName = null)
    {
        if (clrAssemblyName is { Length: > 0 })
        {
            return clrAssemblyName;
        }

        var separatorIndex = atsId.IndexOf('/');
        return separatorIndex > 0 ? atsId[..separatorIndex] : string.Empty;
    }

    // Must agree with AtsContextFilter.IsCapabilityOwnedBySelectedAssembly: the filter decides which
    // capabilities reach the export, and this decides which of them are documented.
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

    private sealed class ApiExportScope
    {
        private readonly HashSet<string> _ownedAssemblyNames;
        private readonly Dictionary<string, AtsTypeInfo> _handleTypesById = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _typeOwners = new(StringComparer.Ordinal);

        public ApiExportScope(AtsContext context, string packageName, IReadOnlyCollection<string> exportingAssemblyNames)
        {
            Context = context;
            PackageName = packageName;
            _ownedAssemblyNames = new HashSet<string>(exportingAssemblyNames, StringComparer.OrdinalIgnoreCase);

            foreach (var handleType in context.HandleTypes)
            {
                _handleTypesById.TryAdd(handleType.AtsTypeId, handleType);
                _typeOwners.TryAdd(
                    handleType.AtsTypeId,
                    GetOwningAssemblyName(handleType.AtsTypeId, handleType.ClrType?.Assembly.GetName().Name));
            }

            // Handle types that are only ever referenced have no AtsTypeInfo, but the type references that
            // mention them carry the CLR type.
            foreach (var capability in context.Capabilities)
            {
                RegisterTypeOwners(capability.TargetType);
                RegisterTypeOwners(capability.ReturnType);
                foreach (var expandedTargetType in capability.ExpandedTargetTypes)
                {
                    RegisterTypeOwners(expandedTargetType);
                }

                foreach (var parameter in capability.Parameters)
                {
                    RegisterTypeOwners(parameter.Type);
                    RegisterTypeOwners(parameter.CallbackReturnType);
                    foreach (var callbackParameter in parameter.CallbackParameters ?? [])
                    {
                        RegisterTypeOwners(callbackParameter.Type);
                    }
                }
            }
        }

        public AtsContext Context { get; }

        public string PackageName { get; }

        public Dictionary<string, GoApiItem> Items { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, GoApiDeclaration> Declarations { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Gets or sets the hand-written <c>handleWrapperBase.Err</c> method every handle interface inherits.
        /// </summary>
        public GoDeclaration ErrMethod { get; set; } = null!;

        /// <summary>
        /// Gets or sets the <c>Build</c> method the generator writes for the application builder.
        /// </summary>
        public GoDeclaration BuildMethod { get; set; } = null!;

        public bool IsOwned(string assemblyName) => _ownedAssemblyNames.Contains(assemblyName);

        public AtsTypeInfo? GetHandleType(string typeId) => _handleTypesById.GetValueOrDefault(typeId);

        public string GetTypeOwner(string typeId)
            => _typeOwners.TryGetValue(typeId, out var owner) ? owner : GetOwningAssemblyName(typeId);

        // First registration wins, so the declaring type of a handle type decides its owner rather than a
        // later reference to it.
        public void AddItem(GoApiItem item) => Items.TryAdd(item.Id, item);

        public void AddDeclaration(GoApiDeclaration declaration) => Declarations.TryAdd(declaration.Id, declaration);

        private void RegisterTypeOwners(AtsTypeRef? typeRef)
        {
            if (typeRef is null)
            {
                return;
            }

            if (typeRef.Category == AtsTypeCategory.Handle && typeRef.ClrType is { } clrType)
            {
                _typeOwners.TryAdd(typeRef.TypeId, GetOwningAssemblyName(typeRef.TypeId, clrType.Assembly.GetName().Name));
            }

            RegisterTypeOwners(typeRef.ElementType);
            RegisterTypeOwners(typeRef.KeyType);
            RegisterTypeOwners(typeRef.ValueType);
            foreach (var unionType in typeRef.UnionTypes ?? [])
            {
                RegisterTypeOwners(unionType);
            }
        }
    }
}
