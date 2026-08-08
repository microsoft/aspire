// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.TypeSystem;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

public class AtsContextFilterTests
{
    /// <summary>
    /// A NuGet package id is case-insensitive, but an API export records it verbatim as the identity
    /// consumers key on, so a document published under the caller's spelling names a package nobody
    /// looks up. The loaded assembly is the authority on how it is spelled.
    /// </summary>
    [Theory]
    [InlineData(NameCasing.Lower)]
    [InlineData(NameCasing.Upper)]
    [InlineData(NameCasing.AsDeclared)]
    public void TryResolveCanonicalAssemblyName_ReturnsTheSpellingTheAssemblyCarries(NameCasing casing)
    {
        var context = CreateContext();
        var canonicalName = typeof(AtsContextFilterTests).Assembly.GetName().Name!;
        var requestedName = casing switch
        {
            NameCasing.Lower => canonicalName.ToLowerInvariant(),
            NameCasing.Upper => canonicalName.ToUpperInvariant(),
            _ => canonicalName
        };

        Assert.True(AtsContextFilter.TryResolveCanonicalAssemblyName(context, requestedName, out var resolvedName));
        Assert.Equal(canonicalName, resolvedName);
    }

    /// <summary>
    /// A package whose CLR types did not resolve survives only as the prefix of its capability and
    /// type ids, and the filter still matches it there. Canonicalization has to reach the same names
    /// the filter does, or that package is the one case that returns a populated document under a
    /// name consumers cannot look up.
    /// </summary>
    [Fact]
    public void TryResolveCanonicalAssemblyName_ReachesAPackageThatSurvivesOnlyInItsIds()
    {
        var context = CreateContext();

        Assert.All(
            context.HandleTypes.Where(type => type.AtsTypeId.StartsWith("Aspire.Hosting.Redis/", StringComparison.Ordinal)),
            type => Assert.Null(type.ClrType));

        Assert.True(AtsContextFilter.TryResolveCanonicalAssemblyName(context, "aspire.hosting.redis", out var resolvedName));
        Assert.Equal("Aspire.Hosting.Redis", resolvedName);
        Assert.NotEmpty(AtsContextFilter.FilterByExportingAssemblies(context, ["aspire.hosting.redis"]).HandleTypes);
    }

    /// <summary>
    /// A name no loaded assembly carries belongs to a package whose assembly is named differently.
    /// The candidates canonicalization searches are a superset of what the filter matches on, so
    /// that package exports nothing -- which the caller has to be told rather than left to publish
    /// an empty document under a name it never confirmed.
    /// </summary>
    [Fact]
    public void TryResolveCanonicalAssemblyName_ReportsAnUnmatchedName()
    {
        var context = CreateContext();

        Assert.False(AtsContextFilter.TryResolveCanonicalAssemblyName(context, "contoso.not.loaded", out var resolvedName));
        Assert.Null(resolvedName);
        Assert.Empty(AtsContextFilter.FilterByExportingAssemblies(context, ["contoso.not.loaded"]).HandleTypes);
    }

    /// <summary>Casing variants exercised by <see cref="TryResolveCanonicalAssemblyName_ReturnsTheSpellingTheAssemblyCarries"/>.</summary>
    public enum NameCasing
    {
        Lower,
        Upper,
        AsDeclared
    }

    /// <summary>
    /// An assembly whose capabilities were all removed by scan-time filtering exports nothing, so
    /// canonicalization has to reject it. Resolving it instead is the silent failure: the export
    /// filters to nothing and <c>sdk export</c> publishes an empty document under a successful exit
    /// code rather than telling the caller the package contributed no API.
    /// </summary>
    [Fact]
    public void TryResolveCanonicalAssemblyName_RejectsAnAssemblyWhoseCapabilitiesWereAllFiltered()
    {
        var losingAssembly = CreateCollidingCapabilityAssembly("ZzzFilterCollisionLoser");
        var context = AtsCapabilityScanner.ScanAssemblies(
            [
                typeof(IDistributedApplicationBuilder).Assembly,
                CreateCollidingCapabilityAssembly("AaaFilterCollisionWinner"),
                losingAssembly
            ]).ToAtsContext();
        var losingAssemblyName = losingAssembly.GetName().Name!;

        Assert.False(AtsContextFilter.TryResolveCanonicalAssemblyName(context, losingAssemblyName, out var resolvedName));
        Assert.Null(resolvedName);
        Assert.Empty(AtsContextFilter.FilterByExportingAssemblies(context, [losingAssemblyName]).Capabilities);
    }

    /// <summary>
    /// A dynamic assembly whose single capability collides with an identically named export on the
    /// same target, so the scan keeps only the ordinally first one and the other assembly is left
    /// contributing nothing.
    /// </summary>
    private static Assembly CreateCollidingCapabilityAssembly(string assemblyNamePrefix)
    {
        var assemblyName = new AssemblyName($"{assemblyNamePrefix}_{Guid.NewGuid():N}");
        var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
        var moduleBuilder = assemblyBuilder.DefineDynamicModule(assemblyName.Name!);
        var exportsTypeBuilder = moduleBuilder.DefineType(
            "Generated.CollidingExports",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        var methodBuilder = exportsTypeBuilder.DefineMethod(
            "Collides",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(void),
            [typeof(IDistributedApplicationBuilder), typeof(string)]);
        methodBuilder.DefineParameter(1, ParameterAttributes.None, "builder");
        methodBuilder.DefineParameter(2, ParameterAttributes.None, "value");
        methodBuilder.SetCustomAttribute(
            new CustomAttributeBuilder(
                typeof(AspireExportAttribute).GetConstructor([typeof(string)])!,
                ["collidingExport"]));
        methodBuilder.GetILGenerator().Emit(OpCodes.Ret);

        _ = exportsTypeBuilder.CreateType();

        return assemblyBuilder;
    }

    [Fact]
    public void FilterByExportingAssemblies_StrictFilterKeepsOnlySelectedAssemblyExports()
    {
        var context = CreateContext();

        var filteredContext = AtsContextFilter.FilterByExportingAssemblies(
            context,
            [typeof(AtsContextFilterTests).Assembly.GetName().Name!]);

        Assert.Collection(
            filteredContext.Capabilities,
            capability => Assert.Equal("Aspire.Hosting.RemoteHost.Tests/addTestResource", capability.CapabilityId));

        Assert.Collection(
            filteredContext.HandleTypes,
            type => Assert.Equal("Aspire.Hosting.RemoteHost.Tests/TestResource", type.AtsTypeId));

        Assert.Collection(
            filteredContext.DtoTypes,
            type => Assert.Equal("Aspire.Hosting.RemoteHost.Tests/TestOptions", type.TypeId));

        Assert.Collection(
            filteredContext.EnumTypes,
            type => Assert.Equal(AtsConstants.EnumTypeId(typeof(TestMode).FullName!), type.TypeId));

        Assert.Collection(
            filteredContext.ExportedValues.OrderBy(value => string.Join(".", value.PathSegments), StringComparer.Ordinal),
            value => Assert.Equal("Aspire.Hosting.RemoteHost.Tests.SelectedValues.Default", string.Join(".", value.PathSegments)),
            value => Assert.Equal("Aspire.Hosting.RemoteHost.Tests.SelectedValues.Metadata", string.Join(".", value.PathSegments)));

        Assert.Contains(filteredContext.Diagnostics, diagnostic => diagnostic.Location == "Aspire.Hosting.RemoteHost.Tests.TestType.Method");
        Assert.DoesNotContain(filteredContext.Diagnostics, diagnostic => diagnostic.Location == "Aspire.Hosting.UnrelatedType.Method");
        Assert.DoesNotContain(filteredContext.Diagnostics, diagnostic => diagnostic.Location == "Aspire.Hosting.Redis.RedisType.Method");

        Assert.Contains("Aspire.Hosting.RemoteHost.Tests/addTestResource", filteredContext.Methods.Keys);
        Assert.DoesNotContain("Aspire.Hosting/createBuilder", filteredContext.Methods.Keys);
    }

    [Fact]
    public void FilterByExportingAssemblies_CodeGenerationFilterIncludesReferencedSupportingTypes()
    {
        var context = CreateContext();

        var filteredContext = AtsContextFilter.FilterByExportingAssembliesWithReferences(
            context,
            [typeof(AtsContextFilterTests).Assembly.GetName().Name!]);

        Assert.Contains(filteredContext.HandleTypes, type => type.AtsTypeId == "Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceBuilder`1");
        Assert.Contains(filteredContext.DtoTypes, type => type.TypeId == "Aspire.TypeSystem/AtsContext");
        Assert.Contains(filteredContext.DtoTypes, type => type.TypeId == "Aspire.TypeSystem/ExportedValueMetadata");
        Assert.Contains(filteredContext.EnumTypes, type => type.TypeId == AtsConstants.EnumTypeId(typeof(DistributedApplicationOperation).FullName!));
        Assert.Contains(filteredContext.EnumTypes, type => type.TypeId == AtsConstants.EnumTypeId("Aspire.TypeSystem.ExportedValueMode"));
        Assert.Collection(
            filteredContext.ExportedValues.OrderBy(value => string.Join(".", value.PathSegments), StringComparer.Ordinal),
            value => Assert.Equal("Aspire.Hosting.RemoteHost.Tests.SelectedValues.Default", string.Join(".", value.PathSegments)),
            value => Assert.Equal("Aspire.Hosting.RemoteHost.Tests.SelectedValues.Metadata", string.Join(".", value.PathSegments)));
        Assert.DoesNotContain(filteredContext.Capabilities, capability => capability.CapabilityId == "Aspire.Hosting/createBuilder");
        Assert.DoesNotContain(filteredContext.HandleTypes, type => type.AtsTypeId == "Aspire.Hosting/Aspire.Hosting.DistributedApplication");
    }

    [Fact]
    public void FilterForApiExport_IncludesOnlyReferencedHandleCapabilityShape()
    {
        var context = CreateContext();
        var referencedHandleType = Assert.Single(
            context.HandleTypes,
            type => type.AtsTypeId == "Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceBuilder`1");
        var supportingCapability = new AtsCapabilityInfo
        {
            CapabilityId = "Aspire.Hosting/getResourceName",
            MethodName = "getResourceName",
            Parameters =
            [
                new AtsParameterInfo
                {
                    Name = "unused",
                    Type = new AtsTypeRef
                    {
                        TypeId = "Aspire.TypeSystem/AtsContext",
                        Category = AtsTypeCategory.Dto
                    }
                }
            ],
            ReturnType = new AtsTypeRef
            {
                TypeId = "Aspire.Hosting/Aspire.Hosting.DistributedApplication",
                Category = AtsTypeCategory.Handle
            },
            TargetTypeId = referencedHandleType.AtsTypeId,
            TargetType = new AtsTypeRef
            {
                TypeId = referencedHandleType.AtsTypeId,
                ClrType = referencedHandleType.ClrType,
                Category = AtsTypeCategory.Handle,
                IsInterface = true
            },
            CapabilityKind = AtsCapabilityKind.InstanceMethod
        };
        context = new AtsContext
        {
            Capabilities = [.. context.Capabilities, supportingCapability],
            HandleTypes = context.HandleTypes,
            DtoTypes = context.DtoTypes,
            EnumTypes = context.EnumTypes,
            ExportedValues = context.ExportedValues,
            Diagnostics = context.Diagnostics
        };

        var filteredContext = AtsContextFilter.FilterForApiExport(
            context,
            [typeof(AtsContextFilterTests).Assembly.GetName().Name!]);

        var filteredSupport = Assert.Single(
            filteredContext.Capabilities,
            capability => capability.CapabilityId == supportingCapability.CapabilityId);
        var supportParameter = Assert.Single(filteredSupport.Parameters);
        Assert.False(supportParameter.IsOptional);
        Assert.Equal("Aspire.Hosting/Aspire.Hosting.DistributedApplication", supportParameter.Type?.TypeId);
        Assert.Equal(AtsConstants.Void, filteredSupport.ReturnType.TypeId);
        Assert.Equal(referencedHandleType.AtsTypeId, filteredSupport.TargetTypeId);
        Assert.DoesNotContain(
            filteredContext.Capabilities,
            capability => capability.CapabilityId == "Aspire.Hosting/createBuilder");
    }

    [Fact]
    public void FilterByExportingAssemblies_CodeGenerationFilterExpandsOwnedDtoPropertyTypes()
    {
        // An owned DTO is seeded into the included set up front rather than discovered by walking a
        // capability signature, so its own property types used to be skipped entirely. That dropped
        // types the generated SDK still emits — in the real context, HealthStatus from
        // Microsoft.Extensions.Diagnostics.HealthChecks — and code generation then failed on the
        // dangling reference. See https://github.com/microsoft/aspire/issues/17608.
        var foreignEnum = new AtsEnumTypeInfo
        {
            TypeId = AtsConstants.EnumTypeId("Some.Foreign.Dependency.ForeignMode"),
            Name = "ForeignMode",
            ClrType = typeof(DistributedApplicationOperation),
            Values = Enum.GetNames<DistributedApplicationOperation>()
        };

        var foreignCallbackEnum = new AtsEnumTypeInfo
        {
            TypeId = AtsConstants.EnumTypeId("Some.Foreign.Dependency.ForeignCallbackMode"),
            Name = "ForeignCallbackMode",
            ClrType = typeof(DistributedApplicationOperation),
            Values = Enum.GetNames<DistributedApplicationOperation>()
        };

        // Owned by the test assembly and referenced by no capability, so only the ownership seed
        // pulls it in.
        var ownedDtoType = new AtsDtoTypeInfo
        {
            TypeId = "Aspire.Hosting.RemoteHost.Tests/UnreferencedOptions",
            Name = "UnreferencedOptions",
            ClrType = typeof(TestOptions),
            Properties =
            [
                new AtsDtoPropertyInfo
                {
                    Name = "Mode",
                    Type = new AtsTypeRef
                    {
                        TypeId = foreignEnum.TypeId,
                        ClrType = foreignEnum.ClrType,
                        Category = AtsTypeCategory.Enum
                    },
                    IsOptional = false
                },
                new AtsDtoPropertyInfo
                {
                    Name = "OnConfigure",
                    Type = new AtsTypeRef { TypeId = AtsConstants.Void, Category = AtsTypeCategory.Primitive },
                    IsCallback = true,
                    CallbackParameters =
                    [
                        new AtsCallbackParameterInfo
                        {
                            Name = "mode",
                            Type = new AtsTypeRef
                            {
                                TypeId = foreignCallbackEnum.TypeId,
                                ClrType = foreignCallbackEnum.ClrType,
                                Category = AtsTypeCategory.Enum
                            }
                        }
                    ],
                    IsOptional = true
                }
            ]
        };

        var context = new AtsContext
        {
            Capabilities = [],
            HandleTypes = [],
            DtoTypes = [ownedDtoType],
            EnumTypes = [foreignEnum, foreignCallbackEnum],
            ExportedValues = [],
            Diagnostics = []
        };

        var filteredContext = AtsContextFilter.FilterByExportingAssembliesWithReferences(
            context,
            [typeof(AtsContextFilterTests).Assembly.GetName().Name!]);

        Assert.Contains(filteredContext.DtoTypes, type => type.TypeId == ownedDtoType.TypeId);
        Assert.Contains(filteredContext.EnumTypes, type => type.TypeId == foreignEnum.TypeId);
        Assert.Contains(filteredContext.EnumTypes, type => type.TypeId == foreignCallbackEnum.TypeId);
    }

    [Fact]
    public void FilterByExportingAssemblies_ScannedAssemblies_OnlyReturnsSpecifiedAssemblyExports()
    {
        // End-to-end: scan real assemblies through the capability scanner, then filter
        // to a single assembly and verify only that assembly's capabilities appear.
        var hostingAssembly = typeof(DistributedApplication).Assembly;
        var testAssembly = typeof(AtsContextFilterTests).Assembly;
        var testAssemblyName = testAssembly.GetName().Name!;

        var scanResult = AtsCapabilityScanner.ScanAssemblies([hostingAssembly, testAssembly]);
        var unfilteredContext = scanResult.ToAtsContext();

        // Precondition: the unfiltered context has capabilities from both assemblies
        Assert.Contains(unfilteredContext.Capabilities, c => c.CapabilityId.StartsWith("Aspire.Hosting/", StringComparison.Ordinal));
        Assert.Contains(unfilteredContext.Capabilities, c => c.CapabilityId.StartsWith(testAssemblyName + "/", StringComparison.Ordinal));

        var filteredContext = AtsContextFilter.FilterByExportingAssembliesWithReferences(
            unfilteredContext,
            [testAssemblyName]);

        // Only the test assembly's capabilities should remain
        Assert.All(filteredContext.Capabilities, c =>
            Assert.StartsWith(testAssemblyName + "/", c.CapabilityId));

        // No Aspire.Hosting capabilities should be present
        Assert.DoesNotContain(filteredContext.Capabilities,
            c => c.CapabilityId.StartsWith("Aspire.Hosting/", StringComparison.Ordinal));

        // The test assembly should still have at least one capability
        Assert.NotEmpty(filteredContext.Capabilities);

        // Referenced types from Aspire.Hosting used by the test assembly's capabilities
        // should be included (WithReferences), but no standalone Aspire.Hosting capabilities
        Assert.True(filteredContext.HandleTypes.Count > 0);
    }

    [Fact]
    public void FilterByExportingAssemblies_DiagnosticsUseMostSpecificKnownAssemblyPrefix()
    {
        var context = CreateContext();

        var filteredContext = AtsContextFilter.FilterByExportingAssemblies(
            context,
            ["Aspire.Hosting"]);

        Assert.Contains(filteredContext.Diagnostics, diagnostic => diagnostic.Location == "Aspire.Hosting.UnrelatedType.Method");
        Assert.DoesNotContain(filteredContext.Diagnostics, diagnostic => diagnostic.Location == "Aspire.Hosting.Redis.RedisType.Method");
    }

    private static AtsContext CreateContext()
    {
        const string selectedCapabilityId = "Aspire.Hosting.RemoteHost.Tests/addTestResource";
        const string unrelatedCapabilityId = "Aspire.Hosting/createBuilder";

        var selectedHandleType = new AtsTypeInfo
        {
            AtsTypeId = "Aspire.Hosting.RemoteHost.Tests/TestResource",
            ClrType = typeof(TestResource),
            IsInterface = false,
            HasExposeMethods = true,
            HasExposeProperties = false,
            BaseTypeHierarchy = [],
            ImplementedInterfaces = []
        };

        var referencedCoreHandleType = new AtsTypeInfo
        {
            AtsTypeId = "Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceBuilder`1",
            ClrType = typeof(IResourceBuilder<IResource>),
            IsInterface = true,
            HasExposeMethods = false,
            HasExposeProperties = false,
            BaseTypeHierarchy = [],
            ImplementedInterfaces = []
        };

        var unrelatedCoreHandleType = new AtsTypeInfo
        {
            AtsTypeId = "Aspire.Hosting/Aspire.Hosting.DistributedApplication",
            ClrType = typeof(DistributedApplication),
            IsInterface = false,
            HasExposeMethods = true,
            HasExposeProperties = false,
            BaseTypeHierarchy = [],
            ImplementedInterfaces = []
        };

        var siblingHandleType = new AtsTypeInfo
        {
            AtsTypeId = "Aspire.Hosting.Redis/Aspire.Hosting.Redis.RedisResource",
            ClrType = null,
            IsInterface = false,
            HasExposeMethods = true,
            HasExposeProperties = false,
            BaseTypeHierarchy = [],
            ImplementedInterfaces = []
        };

        var selectedDtoType = new AtsDtoTypeInfo
        {
            TypeId = "Aspire.Hosting.RemoteHost.Tests/TestOptions",
            Name = nameof(TestOptions),
            ClrType = typeof(TestOptions),
            Properties =
            [
                new AtsDtoPropertyInfo
                {
                    Name = nameof(TestOptions.Mode),
                    Type = new AtsTypeRef
                    {
                        TypeId = AtsConstants.EnumTypeId(typeof(TestMode).FullName!),
                        ClrType = typeof(TestMode),
                        Category = AtsTypeCategory.Enum
                    },
                    IsOptional = false
                }
            ]
        };

        var referencedCoreDtoType = new AtsDtoTypeInfo
        {
            TypeId = "Aspire.TypeSystem/AtsContext",
            Name = nameof(AtsContext),
            ClrType = typeof(AtsContext),
            Properties = []
        };

        var selectedEnumType = new AtsEnumTypeInfo
        {
            TypeId = AtsConstants.EnumTypeId(typeof(TestMode).FullName!),
            Name = nameof(TestMode),
            ClrType = typeof(TestMode),
            Values = Enum.GetNames<TestMode>()
        };

        var exportedValueOnlyEnumType = new AtsEnumTypeInfo
        {
            TypeId = AtsConstants.EnumTypeId("Aspire.TypeSystem.ExportedValueMode"),
            Name = "ExportedValueMode",
            ClrType = typeof(DistributedApplicationOperation),
            Values = Enum.GetNames<DistributedApplicationOperation>()
        };

        var referencedCoreEnumType = new AtsEnumTypeInfo
        {
            TypeId = AtsConstants.EnumTypeId(typeof(DistributedApplicationOperation).FullName!),
            Name = nameof(DistributedApplicationOperation),
            ClrType = typeof(DistributedApplicationOperation),
            Values = Enum.GetNames<DistributedApplicationOperation>()
        };

        var exportedValueOnlyDtoType = new AtsDtoTypeInfo
        {
            TypeId = "Aspire.TypeSystem/ExportedValueMetadata",
            Name = "ExportedValueMetadata",
            ClrType = typeof(AtsContext),
            Properties =
            [
                new AtsDtoPropertyInfo
                {
                    Name = "Mode",
                    Type = new AtsTypeRef
                    {
                        TypeId = exportedValueOnlyEnumType.TypeId,
                        ClrType = exportedValueOnlyEnumType.ClrType,
                        Category = AtsTypeCategory.Enum
                    },
                    IsOptional = false
                }
            ]
        };

        var selectedCapability = new AtsCapabilityInfo
        {
            CapabilityId = selectedCapabilityId,
            MethodName = "addTestResource",
            Parameters =
            [
                new AtsParameterInfo
                {
                    Name = "builder",
                    Type = new AtsTypeRef
                    {
                        TypeId = selectedHandleType.AtsTypeId,
                        ClrType = selectedHandleType.ClrType,
                        Category = AtsTypeCategory.Handle
                    }
                },
                new AtsParameterInfo
                {
                    Name = "options",
                    Type = new AtsTypeRef
                    {
                        TypeId = referencedCoreDtoType.TypeId,
                        ClrType = referencedCoreDtoType.ClrType,
                        Category = AtsTypeCategory.Dto
                    }
                },
                new AtsParameterInfo
                {
                    Name = "operation",
                    Type = new AtsTypeRef
                    {
                        TypeId = referencedCoreEnumType.TypeId,
                        ClrType = referencedCoreEnumType.ClrType,
                        Category = AtsTypeCategory.Enum
                    }
                }
            ],
            ReturnType = new AtsTypeRef
            {
                TypeId = referencedCoreHandleType.AtsTypeId,
                ClrType = referencedCoreHandleType.ClrType,
                Category = AtsTypeCategory.Handle,
                IsInterface = true
            },
            TargetTypeId = selectedHandleType.AtsTypeId,
            TargetType = new AtsTypeRef
            {
                TypeId = selectedHandleType.AtsTypeId,
                ClrType = selectedHandleType.ClrType,
                Category = AtsTypeCategory.Handle
            },
            TargetParameterName = "builder",
            ReturnsBuilder = true,
            CapabilityKind = AtsCapabilityKind.Method,
            ExpandedTargetTypes = []
        };

        var unrelatedCapability = new AtsCapabilityInfo
        {
            CapabilityId = unrelatedCapabilityId,
            MethodName = "addRedis",
            Parameters = [],
            ReturnType = new AtsTypeRef
            {
                TypeId = unrelatedCoreHandleType.AtsTypeId,
                ClrType = unrelatedCoreHandleType.ClrType,
                Category = AtsTypeCategory.Handle
            },
            ReturnsBuilder = true,
            CapabilityKind = AtsCapabilityKind.Method,
            ExpandedTargetTypes = []
        };

        var selectedAssemblyName = typeof(AtsContextFilterTests).Assembly.GetName().Name!;
        var unrelatedAssemblyName = typeof(DistributedApplication).Assembly.GetName().Name!;

        var selectedPrimitiveExportedValue = new AtsExportedValueInfo
        {
            OwningAssemblyName = selectedAssemblyName,
            PathSegments = ["Aspire.Hosting.RemoteHost.Tests", "SelectedValues", "Default"],
            Type = new AtsTypeRef
            {
                TypeId = "System/String",
                ClrType = typeof(string),
                Category = AtsTypeCategory.Primitive
            },
            Value = JsonValue.Create("selected")
        };

        var selectedDtoExportedValue = new AtsExportedValueInfo
        {
            OwningAssemblyName = selectedAssemblyName,
            PathSegments = ["Aspire.Hosting.RemoteHost.Tests", "SelectedValues", "Metadata"],
            Type = new AtsTypeRef
            {
                TypeId = exportedValueOnlyDtoType.TypeId,
                ClrType = exportedValueOnlyDtoType.ClrType,
                Category = AtsTypeCategory.Dto
            },
            Value = JsonNode.Parse("""{"mode":"Run"}""")
        };

        var unrelatedPrimitiveExportedValue = new AtsExportedValueInfo
        {
            OwningAssemblyName = unrelatedAssemblyName,
            PathSegments = ["Aspire.Hosting", "CoreValues", "Default"],
            Type = new AtsTypeRef
            {
                TypeId = "System/String",
                ClrType = typeof(string),
                Category = AtsTypeCategory.Primitive
            },
            Value = JsonValue.Create("unrelated")
        };

        var context = new AtsContext
        {
            Capabilities = [selectedCapability, unrelatedCapability],
            HandleTypes = [selectedHandleType, referencedCoreHandleType, unrelatedCoreHandleType, siblingHandleType],
            DtoTypes = [selectedDtoType, referencedCoreDtoType, exportedValueOnlyDtoType],
            EnumTypes = [selectedEnumType, referencedCoreEnumType, exportedValueOnlyEnumType],
            ExportedValues = [selectedPrimitiveExportedValue, selectedDtoExportedValue, unrelatedPrimitiveExportedValue],
            Diagnostics =
            [
                AtsDiagnostic.Warning("Selected warning", "Aspire.Hosting.RemoteHost.Tests.TestType.Method"),
                AtsDiagnostic.Warning("Unrelated warning", "Aspire.Hosting.UnrelatedType.Method"),
                AtsDiagnostic.Warning("Sibling warning", "Aspire.Hosting.Redis.RedisType.Method")
            ]
        };

        var testMethod = typeof(AtsContextFilterTests).GetMethod(nameof(TestCapability), BindingFlags.Static | BindingFlags.NonPublic)!;
        context.Methods[selectedCapabilityId] = testMethod;
        context.Methods[unrelatedCapabilityId] = typeof(DistributedApplication)
            .GetMethods(BindingFlags.Static | BindingFlags.Public)
            .Single(method => method.Name == nameof(DistributedApplication.CreateBuilder) && method.GetParameters().Length == 0);

        return context;
    }

    private static void TestCapability()
    {
    }

    private sealed class TestResource
    {
    }

    private sealed class TestOptions
    {
        public TestMode Mode { get; init; }
    }

    private enum TestMode
    {
        Basic,
        Advanced
    }
}
