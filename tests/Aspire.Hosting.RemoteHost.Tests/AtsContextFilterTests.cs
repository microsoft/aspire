// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.TypeSystem;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

public class AtsContextFilterTests
{
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
            HandleTypes = [selectedHandleType, referencedCoreHandleType, unrelatedCoreHandleType],
            DtoTypes = [selectedDtoType, referencedCoreDtoType, exportedValueOnlyDtoType],
            EnumTypes = [selectedEnumType, referencedCoreEnumType, exportedValueOnlyEnumType],
            ExportedValues = [selectedPrimitiveExportedValue, selectedDtoExportedValue, unrelatedPrimitiveExportedValue]
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
