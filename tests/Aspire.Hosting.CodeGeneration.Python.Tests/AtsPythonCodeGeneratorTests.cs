// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RemoteHost;
using Aspire.TypeSystem;
using Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes;

namespace Aspire.Hosting.CodeGeneration.Python.Tests;

public class AtsPythonCodeGeneratorTests
{
    private readonly AtsPythonCodeGenerator _generator = new();

    // The test types are compiled into this assembly via Compile Include
    private const string TestTypesAssemblyName = "Aspire.Hosting.CodeGeneration.Python.Tests";

    [Fact]
    public void Language_ReturnsPython()
    {
        Assert.Equal("Python", _generator.Language);
    }

    [Fact]
    public async Task GenerateDistributedApplication_WithTestTypes_GeneratesCorrectOutput()
    {
        // Arrange
        var atsContext = CreateContextFromTestAssembly();

        // Act
        var files = _generator.GenerateDistributedApplication(atsContext);

        // Assert
        Assert.Contains("aspire_app.py", files.Keys);
        Assert.Contains("pyproject.toml", files.Keys);

        await Verify(files["aspire_app.py"], extension: "py")
            .UseFileName("AtsGeneratedAspire");
    }

    [Fact]
    public void GenerateDistributedApplication_WithTestTypes_IncludesCapabilities()
    {
        // Arrange
        var capabilities = ScanCapabilitiesFromTestAssembly();

        // Assert that capabilities are discovered
        Assert.NotEmpty(capabilities);

        // Check for specific capabilities (uses AssemblyName/methodName format)
        // The test types are in TypeScript.Tests assembly
        Assert.Contains(capabilities, c => c.CapabilityId == $"{TestTypesAssemblyName}/addTestRedis");
        Assert.Contains(capabilities, c => c.CapabilityId == $"{TestTypesAssemblyName}/withPersistence");
        Assert.Contains(capabilities, c => c.CapabilityId == $"{TestTypesAssemblyName}/withOptionalString");
    }

    [Fact]
    public void GenerateDistributedApplication_WithTestTypes_DeriveCorrectMethodNames()
    {
        // Arrange
        var capabilities = ScanCapabilitiesFromTestAssembly();

        // Assert method names are derived correctly
        var addTestRedis = capabilities.First(c => c.CapabilityId == $"{TestTypesAssemblyName}/addTestRedis");
        Assert.Equal("addTestRedis", addTestRedis.MethodName);

        var withPersistence = capabilities.First(c => c.CapabilityId == $"{TestTypesAssemblyName}/withPersistence");
        Assert.Equal("withPersistence", withPersistence.MethodName);
    }

    [Fact]
    public void GenerateDistributedApplication_WithTestTypes_CapturesParameters()
    {
        // Arrange
        var capabilities = ScanCapabilitiesFromTestAssembly();

        // Assert parameters are captured
        var addTestRedis = capabilities.First(c => c.CapabilityId == $"{TestTypesAssemblyName}/addTestRedis");
        Assert.Equal(2, addTestRedis.Parameters.Count);
        Assert.Equal("Aspire.Hosting/Aspire.Hosting.IDistributedApplicationBuilder", addTestRedis.TargetTypeId);
        Assert.Contains(addTestRedis.Parameters, p => p.Name == "name" && p.Type?.TypeId == "string");
        Assert.Contains(addTestRedis.Parameters, p => p.Name == "port" && p.IsOptional);
    }

    [Fact]
    public void Scanner_ReturnsBuilder_TrueForResourceBuilderReturnTypes()
    {
        // Verify that ReturnsBuilder is correctly set to true for methods
        // that return IResourceBuilder<T>
        var capabilities = ScanCapabilitiesFromTestAssembly();

        // addTestRedis returns IResourceBuilder<TestRedisResource> - should have ReturnsBuilder = true
        var addTestRedis = capabilities.FirstOrDefault(c => c.CapabilityId == $"{TestTypesAssemblyName}/addTestRedis");
        Assert.NotNull(addTestRedis);
        Assert.True(addTestRedis.ReturnsBuilder,
            "addTestRedis returns IResourceBuilder<T> but ReturnsBuilder is false - fluent chaining won't work");

        // withPersistence also returns IResourceBuilder<T>
        var withPersistence = capabilities.FirstOrDefault(c => c.CapabilityId == $"{TestTypesAssemblyName}/withPersistence");
        Assert.NotNull(withPersistence);
        Assert.True(withPersistence.ReturnsBuilder,
            "withPersistence returns IResourceBuilder<T> but ReturnsBuilder is false - fluent chaining won't work");
    }

    [Fact]
    public async Task Scanner_AddTestRedis_HasCorrectTypeMetadata()
    {
        // Verify the entire capability object for addTestRedis
        var capabilities = ScanCapabilitiesFromTestAssembly();

        var addTestRedis = capabilities.FirstOrDefault(c => c.CapabilityId == $"{TestTypesAssemblyName}/addTestRedis");
        Assert.NotNull(addTestRedis);

        await Verify(addTestRedis).UseFileName("AddTestRedisCapability");
    }

    [Fact]
    public async Task Scanner_WithPersistence_HasCorrectExpandedTargets()
    {
        // Verify the entire capability object for withPersistence
        var capabilities = ScanCapabilitiesFromTestAssembly();

        var withPersistence = capabilities.FirstOrDefault(c => c.CapabilityId == $"{TestTypesAssemblyName}/withPersistence");
        Assert.NotNull(withPersistence);

        await Verify(withPersistence).UseFileName("WithPersistenceCapability");
    }

    [Fact]
    public async Task Scanner_WithOptionalString_HasCorrectExpandedTargets()
    {
        // Verify withOptionalString (targets IResource, should expand to TestRedisResource)
        var capabilities = ScanCapabilitiesFromTestAssembly();

        var withOptionalString = capabilities.FirstOrDefault(c => c.CapabilityId == $"{TestTypesAssemblyName}/withOptionalString");
        Assert.NotNull(withOptionalString);

        await Verify(withOptionalString).UseFileName("WithOptionalStringCapability");
    }

    [Fact]
    public async Task Scanner_HostingAssembly_AddContainerCapability()
    {
        // Verify the addContainer capability from the real Aspire.Hosting assembly
        var capabilities = ScanCapabilitiesFromHostingAssembly();

        var addContainer = capabilities.FirstOrDefault(c => c.CapabilityId == "Aspire.Hosting/addContainer");
        Assert.NotNull(addContainer);

        await Verify(addContainer).UseFileName("HostingAddContainerCapability");
    }

    [Fact]
    public void RuntimeType_ContainerResource_IsNotInterface()
    {
        // Verify that ContainerResource.IsInterface returns false using runtime reflection
        var containerResourceType = typeof(ContainerResource);

        Assert.NotNull(containerResourceType);
        Assert.False(containerResourceType.IsInterface, "ContainerResource should NOT be an interface");
    }

    [Fact]
    public void TwoPassScanning_DeduplicatesCapabilities()
    {
        // Verify that when the same capability appears in multiple assemblies,
        // ScanAssemblies deduplicates by CapabilityId.
        var capabilities = ScanCapabilitiesFromBothAssemblies();

        // Each capability ID should appear only once
        var duplicates = capabilities
            .GroupBy(c => c.CapabilityId)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void TwoPassScanning_MergesHandleTypesFromAllAssemblies()
    {
        // Verify that ScanAssemblies collects handle types from all assemblies
        var result = CreateContextFromBothAssemblies();

        // Should have types from Aspire.Hosting (ContainerResource, etc.)
        var containerResourceType = result.HandleTypes
            .FirstOrDefault(t => t.AtsTypeId.Contains("ContainerResource") && !t.AtsTypeId.Contains("IContainer"));
        Assert.NotNull(containerResourceType);

        // Should have types from test assembly (TestRedisResource)
        var testRedisType = result.HandleTypes
            .FirstOrDefault(t => t.AtsTypeId.Contains("TestRedisResource"));
        Assert.NotNull(testRedisType);

        // TestRedisResource should have IResourceWithEnvironment in its interfaces
        // (inherited via ContainerResource)
        var hasEnvironmentInterface = testRedisType.ImplementedInterfaces
            .Any(i => i.TypeId.Contains("IResourceWithEnvironment"));
        Assert.True(hasEnvironmentInterface,
            "TestRedisResource should implement IResourceWithEnvironment via ContainerResource");
    }

    [Fact]
    public async Task TwoPassScanning_GeneratesWithEnvironmentOnTestRedisBuilder()
    {
        // End-to-end test: verify that environment methods appear on resources
        // in the generated Python when using 2-pass scanning.
        var atsContext = CreateContextFromBothAssemblies();

        // Generate Python
        var files = _generator.GenerateDistributedApplication(atsContext);
        var aspirePy = files["aspire_app.py"];

        // Verify environment-related methods appear (method names may vary by generator)
        Assert.Contains("with_env", aspirePy);

        // Snapshot for detailed verification
        await Verify(aspirePy, extension: "py")
            .UseFileName("TwoPassScanningGeneratedAspire");
    }

    [Fact]
    public void GeneratedCode_UsesSnakeCaseMethodNames()
    {
        // Verify that the generated Python code uses snake_case for method names
        var atsContext = CreateContextFromBothAssemblies();

        var files = _generator.GenerateDistributedApplication(atsContext);
        var aspirePy = files["aspire_app.py"];

        // Python should use snake_case, not camelCase
        Assert.Contains("add_test_redis", aspirePy);
        Assert.Contains("with_env", aspirePy);
        Assert.DoesNotContain("addTestRedis(", aspirePy);
        Assert.DoesNotContain("withEnv(", aspirePy);
    }

    [Fact]
    public void GeneratedCode_HasCreateBuilderFunction()
    {
        // Verify that the generated Python code has a create_builder function
        var atsContext = CreateContextFromBothAssemblies();

        var files = _generator.GenerateDistributedApplication(atsContext);
        var aspirePy = files["aspire_app.py"];

        Assert.Contains("def create_builder", aspirePy);
    }

    [Fact]
    public void GeneratedCode_UsesTypeHints()
    {
        // Verify that the generated Python code uses type hints
        var atsContext = CreateContextFromBothAssemblies();

        var files = _generator.GenerateDistributedApplication(atsContext);
        var aspirePy = files["aspire_app.py"];

        // Python type hints use -> for return types and : for parameters
        Assert.Contains("->", aspirePy);
        Assert.Contains(": str", aspirePy);
    }

    [Fact]
    public void GeneratedCode_SanitizesPythonKeywordIdentifiers()
    {
        var files = _generator.GenerateDistributedApplication(CreateContextWithKeywordParameter());
        var aspirePy = files["aspire_app.py"];

        Assert.Contains("with_from", aspirePy);
        Assert.Contains("from_", aspirePy);
        Assert.DoesNotContain("def with_from(self, from: str)", aspirePy);
        Assert.DoesNotContain("\n    from: str", aspirePy);
    }

    [Fact]
    public void GeneratedCode_SanitizesClrGenericNamesInInheritance()
    {
        var files = _generator.GenerateDistributedApplication(CreateContextWithGenericInheritance());
        var aspirePy = files["aspire_app.py"];

        Assert.DoesNotContain("Culture=neutral", aspirePy);
        Assert.DoesNotContain("PublicKeyToken", aspirePy);
        Assert.DoesNotContain("Version=", aspirePy);
    }

    private static List<AtsCapabilityInfo> ScanCapabilitiesFromTestAssembly()
    {
        var testAssembly = LoadTestAssembly();

        // Scan capabilities from the test assembly
        var result = AtsCapabilityScanner.ScanAssembly(testAssembly);
        return result.Capabilities;
    }

    private static AtsContext CreateContextFromTestAssembly()
    {
        var testAssembly = LoadTestAssembly();

        // Scan capabilities from the test assembly
        var result = AtsCapabilityScanner.ScanAssembly(testAssembly);
        return result.ToAtsContext();
    }

    private static Assembly LoadTestAssembly()
    {
        // Get the test assembly at runtime (TypeScript tests assembly has the TestTypes)
        return typeof(TestRedisResource).Assembly;
    }

    private static List<AtsCapabilityInfo> ScanCapabilitiesFromHostingAssembly()
    {
        var hostingAssembly = typeof(DistributedApplication).Assembly;
        var result = AtsCapabilityScanner.ScanAssembly(hostingAssembly);
        return result.Capabilities;
    }

    private static List<AtsCapabilityInfo> ScanCapabilitiesFromBothAssemblies()
    {
        var (testAssembly, hostingAssembly) = LoadBothAssemblies();

        // Use ScanAssemblies for proper cross-assembly expansion
        var result = AtsCapabilityScanner.ScanAssemblies([hostingAssembly, testAssembly]);
        return result.Capabilities;
    }

    private static AtsContext CreateContextFromBothAssemblies()
    {
        var (testAssembly, hostingAssembly) = LoadBothAssemblies();

        // Use ScanAssemblies for proper cross-assembly expansion and enum collection
        var result = AtsCapabilityScanner.ScanAssemblies([hostingAssembly, testAssembly]);
        return result.ToAtsContext();
    }

    private static (Assembly testAssembly, Assembly hostingAssembly) LoadBothAssemblies()
    {
        var testAssembly = typeof(TestRedisResource).Assembly;
        var hostingAssembly = typeof(DistributedApplication).Assembly;
        return (testAssembly, hostingAssembly);
    }

    private static AtsContext CreateContextWithKeywordParameter()
    {
        var resourceType = new AtsTypeRef
        {
            TypeId = "Tests/KeywordResource",
            ClrType = typeof(KeywordResource),
            Category = AtsTypeCategory.Handle
        };

        return new AtsContext
        {
            Capabilities =
            [
                new AtsCapabilityInfo
                {
                    CapabilityId = "Tests/withFrom",
                    MethodName = "withFrom",
                    Parameters =
                    [
                        new AtsParameterInfo
                        {
                            Name = "builder",
                            Type = resourceType
                        },
                        new AtsParameterInfo
                        {
                            Name = "from",
                            Type = new AtsTypeRef
                            {
                                TypeId = AtsConstants.String,
                                Category = AtsTypeCategory.Primitive
                            }
                        }
                    ],
                    ReturnType = resourceType,
                    TargetTypeId = resourceType.TypeId,
                    TargetType = resourceType,
                    TargetParameterName = "builder",
                    ExpandedTargetTypes = [resourceType],
                    ReturnsBuilder = true,
                    CapabilityKind = AtsCapabilityKind.Method
                }
            ],
            HandleTypes =
            [
                new AtsTypeInfo
                {
                    AtsTypeId = resourceType.TypeId,
                    ClrType = typeof(KeywordResource)
                }
            ],
            DtoTypes = [],
            EnumTypes = []
        };
    }

    private static AtsContext CreateContextWithGenericInheritance()
    {
        var genericBaseType = typeof(GenericBaseResource<GenericTypeArgument<int, string>>);
        var genericInterfaceType = typeof(IGenericResource<GenericTypeArgument<int, string>>);

        var genericBaseTypeRef = new AtsTypeRef
        {
            TypeId = genericBaseType.AssemblyQualifiedName!,
            ClrType = genericBaseType,
            Category = AtsTypeCategory.Handle
        };

        var genericInterfaceTypeRef = new AtsTypeRef
        {
            TypeId = genericInterfaceType.AssemblyQualifiedName!,
            ClrType = genericInterfaceType,
            Category = AtsTypeCategory.Handle,
            IsInterface = true
        };

        var resourceType = new AtsTypeRef
        {
            TypeId = "Tests/GenericResource",
            ClrType = typeof(GenericResource),
            Category = AtsTypeCategory.Handle,
            BaseType = genericBaseTypeRef,
            ImplementedInterfaces = [genericInterfaceTypeRef]
        };

        return new AtsContext
        {
            Capabilities =
            [
                new AtsCapabilityInfo
                {
                    CapabilityId = "Tests/configureGenericResource",
                    MethodName = "configureGenericResource",
                    Parameters =
                    [
                        new AtsParameterInfo
                        {
                            Name = "builder",
                            Type = resourceType
                        }
                    ],
                    ReturnType = new AtsTypeRef
                    {
                        TypeId = AtsConstants.Void,
                        Category = AtsTypeCategory.Primitive
                    },
                    TargetTypeId = resourceType.TypeId,
                    TargetType = resourceType,
                    TargetParameterName = "builder",
                    ExpandedTargetTypes = [resourceType],
                    CapabilityKind = AtsCapabilityKind.Method
                }
            ],
            HandleTypes =
            [
                new AtsTypeInfo
                {
                    AtsTypeId = resourceType.TypeId,
                    ClrType = typeof(GenericResource),
                    BaseTypeHierarchy = [genericBaseTypeRef],
                    ImplementedInterfaces = [genericInterfaceTypeRef]
                },
                new AtsTypeInfo
                {
                    AtsTypeId = genericBaseTypeRef.TypeId,
                    ClrType = genericBaseType
                },
                new AtsTypeInfo
                {
                    AtsTypeId = genericInterfaceTypeRef.TypeId,
                    ClrType = genericInterfaceType,
                    IsInterface = true
                }
            ],
            DtoTypes = [],
            EnumTypes = []
        };
    }

    private sealed class KeywordResource;

    private interface IGenericResource<T>;

    private abstract class GenericBaseResource<T>;

    private sealed class GenericTypeArgument<TLeft, TRight>;

    private sealed class GenericResource : GenericBaseResource<GenericTypeArgument<int, string>>, IGenericResource<GenericTypeArgument<int, string>>;
}
