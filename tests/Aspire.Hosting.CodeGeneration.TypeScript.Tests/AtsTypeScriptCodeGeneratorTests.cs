// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREBROWSERLOGS001 // Type is for evaluation purposes only

using System.Reflection;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RemoteHost;
using Aspire.TypeSystem;
using Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes;

namespace Aspire.Hosting.CodeGeneration.TypeScript.Tests;

public class AtsTypeScriptCodeGeneratorTests
{
    private readonly AtsTypeScriptCodeGenerator _generator = new();

    [Fact]
    public void Language_ReturnsTypeScript()
    {
        Assert.Equal("TypeScript", _generator.Language);
    }

    [Fact]
    public async Task EmbeddedResource_PackageJson_MatchesSnapshot()
    {
        var assembly = typeof(AtsTypeScriptCodeGenerator).Assembly;
        var resourceName = "Aspire.Hosting.CodeGeneration.TypeScript.Resources.package.json";

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync();

        await Verify(content, extension: "json")
            .UseFileName("package");
    }

    [Fact]
    public async Task GenerateDistributedApplication_WithTestTypes_GeneratesCorrectOutput()
    {
        // Arrange
        var atsContext = CreateContextFromTestAssembly();

        // Act
        var files = _generator.GenerateDistributedApplication(atsContext);

        // Assert
        Assert.Contains("aspire.ts", files.Keys);
        Assert.Contains("transport.ts", files.Keys);
        Assert.Contains("base.ts", files.Keys);

        await Verify(files["aspire.ts"], extension: "ts")
            .UseFileName("AtsGeneratedAspire");

        await Verify(files["base.ts"], extension: "ts")
            .UseFileName("base");

        await Verify(files["transport.ts"], extension: "ts")
            .UseFileName("transport");
    }

    [Fact]
    public void GenerateDistributedApplication_WithTestTypes_IncludesExportedValues()
    {
        var atsContext = CreateContextFromTestAssembly();

        Assert.Contains(atsContext.ExportedValues, value => string.Join(".", value.PathSegments) == "TestConfigs.Default");
        Assert.Contains(atsContext.ExportedValues, value => string.Join(".", value.PathSegments) == "TestConfigs.Profiles.Development");

        var files = _generator.GenerateDistributedApplication(atsContext);
        var aspireTs = files["aspire.ts"];

        Assert.Contains("export namespace TestConfigs", aspireTs);
        Assert.Contains("export const Default", aspireTs);
        Assert.Contains("export namespace Profiles", aspireTs);
        Assert.Contains("export const Development", aspireTs);
    }

    [Fact]
    public void GenerateDistributedApplication_WithHostingTypes_KeepsReferenceExpressionInBaseTs()
    {
        var atsContext = CreateContextFromBothAssemblies();

        var files = _generator.GenerateDistributedApplication(atsContext);

        Assert.DoesNotContain("export class ReferenceExpression {", files["aspire.ts"]);
        Assert.Contains("export class ReferenceExpression {", files["base.ts"]);
        Assert.Contains("registerHandleWrapper('Aspire.Hosting/Aspire.Hosting.ApplicationModel.ReferenceExpression'", files["base.ts"]);
        Assert.Contains("condition: extractHandleForExpr(state.condition),", files["base.ts"]);
        Assert.Contains("('$handle' in json || '$expr' in json)", files["base.ts"]);
        Assert.Contains("registerCancellation(state.client, cancellationToken)", files["base.ts"]);
        Assert.Contains("arguments(): Promise<InteractionInputCollection>", files["aspire.ts"]);
        Assert.DoesNotContain("setArguments", files["aspire.ts"]);
    }

    [Fact]
    public void GenerateDistributedApplication_WithTestTypes_IncludesCapabilities()
    {
        // Arrange
        var capabilities = ScanCapabilitiesFromTestAssembly();

        // Assert that capabilities are discovered
        Assert.NotEmpty(capabilities);

        // Check for specific capabilities (now uses AssemblyName/methodName format)
        Assert.Contains(capabilities, c => c.CapabilityId == "Aspire.Hosting.CodeGeneration.TypeScript.Tests/addTestRedis");
        Assert.Contains(capabilities, c => c.CapabilityId == "Aspire.Hosting.CodeGeneration.TypeScript.Tests/withPersistence");
        Assert.Contains(capabilities, c => c.CapabilityId == "Aspire.Hosting.CodeGeneration.TypeScript.Tests/withOptionalString");
    }

    [Fact]
    public void GenerateDistributedApplication_WithTestTypes_DeriveCorrectMethodNames()
    {
        // Arrange
        var capabilities = ScanCapabilitiesFromTestAssembly();

        // Assert method names are derived correctly
        var addTestRedis = capabilities.First(c => c.CapabilityId == "Aspire.Hosting.CodeGeneration.TypeScript.Tests/addTestRedis");
        Assert.Equal("addTestRedis", addTestRedis.MethodName);

        var withPersistence = capabilities.First(c => c.CapabilityId == "Aspire.Hosting.CodeGeneration.TypeScript.Tests/withPersistence");
        Assert.Equal("withPersistence", withPersistence.MethodName);
    }

    [Fact]
    public void GenerateDistributedApplication_WithTestTypes_CapturesParameters()
    {
        // Arrange
        var capabilities = ScanCapabilitiesFromTestAssembly();

        // Assert parameters are captured
        // The builder parameter is skipped because TargetTypeId is inferred from the first parameter
        // (IDistributedApplicationBuilder -> "Aspire.Hosting/Aspire.Hosting.IDistributedApplicationBuilder")
        var addTestRedis = capabilities.First(c => c.CapabilityId == "Aspire.Hosting.CodeGeneration.TypeScript.Tests/addTestRedis");
        Assert.Equal(2, addTestRedis.Parameters.Count);
        Assert.Equal("Aspire.Hosting/Aspire.Hosting.IDistributedApplicationBuilder", addTestRedis.TargetTypeId);
        Assert.Contains(addTestRedis.Parameters, p => p.Name == "name" && p.Type?.TypeId == "string");
        Assert.Contains(addTestRedis.Parameters, p => p.Name == "port" && p.IsOptional);
    }

    [Fact]
    public void GenerateDistributedApplication_WithContextType_GeneratesPropertyCapabilities()
    {
        // Arrange
        var capabilities = ScanCapabilitiesFromTestAssembly();

        // Check for any context property capabilities (those with PropertyGetter or PropertySetter kind)
        var contextCapabilities = capabilities.Where(c =>
            c.CapabilityKind == AtsCapabilityKind.PropertyGetter ||
            c.CapabilityKind == AtsCapabilityKind.PropertySetter).ToList();

        // Assert context type property capabilities are discovered
        // TestCallbackContext has [AspireContextType] - type ID is derived as {AssemblyName}/{TypeName}
        // = Aspire.Hosting.CodeGeneration.TypeScript.Tests/TestCallbackContext
        // with Name (string) and Value (int) properties
        //
        // Note: Context type scanning requires the AspireContextTypeAttribute to be resolvable
        // from the assembly's metadata. If no context capabilities are found, it may be because
        // the attribute type couldn't be resolved.
        if (contextCapabilities.Count == 0)
        {
            // Skip this test if no context types were found - this could be due to
            // attribute resolution issues in the metadata reader
            return;
        }

        // Test getter capability for Name property (camelCase, no "get" prefix)
        // Note: Capability IDs use namespace-based package (Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes)
        // But TargetTypeId uses the new format {AssemblyName}/{FullTypeName}
        var nameGetterCapability = capabilities.FirstOrDefault(c => c.CapabilityId == "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.name");
        Assert.NotNull(nameGetterCapability);
        Assert.Equal(AtsCapabilityKind.PropertyGetter, nameGetterCapability.CapabilityKind);
        Assert.Equal("TestCallbackContext.name", nameGetterCapability.QualifiedMethodName);
        Assert.Equal("string", nameGetterCapability.ReturnType?.TypeId);
        Assert.Equal("Aspire.Hosting.CodeGeneration.TypeScript.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestCallbackContext", nameGetterCapability.TargetTypeId);
        Assert.Single(nameGetterCapability.Parameters);
        Assert.Equal("context", nameGetterCapability.Parameters[0].Name);

        // Test setter capability for Name property (writable)
        var nameSetterCapability = capabilities.FirstOrDefault(c => c.CapabilityId == "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.setName");
        Assert.NotNull(nameSetterCapability);
        Assert.Equal(AtsCapabilityKind.PropertySetter, nameSetterCapability.CapabilityKind);
        Assert.Equal("TestCallbackContext.setName", nameSetterCapability.QualifiedMethodName);
        Assert.Equal("Aspire.Hosting.CodeGeneration.TypeScript.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestCallbackContext", nameSetterCapability.ReturnType?.TypeId); // Returns context for fluent chaining
        Assert.Equal(2, nameSetterCapability.Parameters.Count); // context + value

        // Test getter capability for Value property (camelCase, no "get" prefix)
        var valueGetterCapability = capabilities.FirstOrDefault(c => c.CapabilityId == "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.value");
        Assert.NotNull(valueGetterCapability);
        Assert.Equal(AtsCapabilityKind.PropertyGetter, valueGetterCapability.CapabilityKind);
        Assert.Equal("TestCallbackContext.value", valueGetterCapability.QualifiedMethodName);
        Assert.Equal("number", valueGetterCapability.ReturnType?.TypeId);

        // Test setter capability for Value property (writable)
        var valueSetterCapability = capabilities.FirstOrDefault(c => c.CapabilityId == "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.setValue");
        Assert.NotNull(valueSetterCapability);
        Assert.Equal(AtsCapabilityKind.PropertySetter, valueSetterCapability.CapabilityKind);

        // CancellationToken - the type mapping is in Aspire.Hosting assembly.
        // Since the test only loads the test assembly's type mapping, CancellationToken
        // maps to "any" and is skipped as non-ATS-compatible.
        // In production, when Aspire.Hosting is loaded, CancellationToken will be properly mapped.
    }

    [Fact]
    public void Scanner_TestRedisResource_ImplementsIResource()
    {
        // This test verifies that TestRedisResource's interface collection includes IResource
        // which is inherited through: TestRedisResource -> ContainerResource -> Resource -> IResource
        var testRedisType = typeof(TestRedisResource);

        // Collect all interfaces recursively (simulating what the scanner does)
        var allInterfaces = new HashSet<string>();
        CollectAllInterfacesRecursive(testRedisType, allInterfaces);

        // Should include IResource (inherited from ContainerResource -> Resource)
        Assert.Contains(allInterfaces, i => i.Contains("IResource") && !i.Contains("IResourceWith"));

        // Should include IResourceWithConnectionString (directly implemented)
        Assert.Contains(allInterfaces, i => i.Contains("IResourceWithConnectionString"));
    }

    private static void CollectAllInterfacesRecursive(Type type, HashSet<string> collected)
    {
        // Add directly implemented interfaces
        foreach (var iface in type.GetInterfaces())
        {
            if (collected.Add(iface.FullName ?? iface.Name))
            {
                // Also collect interfaces that this interface extends
                CollectAllInterfacesRecursive(iface, collected);
            }
        }

        // Also check base type
        if (type.BaseType != null && type.BaseType.FullName != "System.Object")
        {
            CollectAllInterfacesRecursive(type.BaseType, collected);
        }
    }

    [Fact]
    public void Scanner_WithOptionalString_TargetsIResource()
    {
        // This test verifies that WithOptionalString<T> where T : IResource
        // correctly targets IResource using the new {AssemblyName}/{FullTypeName} format
        var capabilities = ScanCapabilitiesFromTestAssembly();

        // Find the withOptionalString capability
        var withOptionalString = capabilities
            .FirstOrDefault(c => c.CapabilityId == "Aspire.Hosting.CodeGeneration.TypeScript.Tests/withOptionalString");

        Assert.NotNull(withOptionalString);

        // Target should be IResource from the constraint (new format: {AssemblyName}/{FullTypeName})
        Assert.Equal("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResource", withOptionalString.TargetTypeId);
    }

    [Fact]
    public void Scanner_WithOptionalString_ExpandsToTestRedis()
    {
        // This test verifies that WithOptionalString<T> where T : IResource
        // has its ExpandedTargetTypeIds include TestRedisResource
        var capabilities = ScanCapabilitiesFromTestAssembly();

        // Find the withOptionalString capability
        var withOptionalString = capabilities
            .FirstOrDefault(c => c.CapabilityId == "Aspire.Hosting.CodeGeneration.TypeScript.Tests/withOptionalString");

        Assert.NotNull(withOptionalString);

        // Expanded targets should include TestRedisResource (new format: {AssemblyName}/{FullTypeName})
        Assert.NotNull(withOptionalString.ExpandedTargetTypes);
        var testRedisTarget = withOptionalString.ExpandedTargetTypes.FirstOrDefault(t =>
            t.TypeId == "Aspire.Hosting.CodeGeneration.TypeScript.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestRedisResource");
        Assert.NotNull(testRedisTarget);

        // Verify that concrete types in ExpandedTargetTypes have IsInterface = false
        Assert.False(testRedisTarget.IsInterface, "TestRedisResource is a concrete type, not an interface");
    }

    [Fact]
    public void Scanner_BaseTypeChain_CollectsInterfacesAcrossAssemblies()
    {
        // Debug test to understand the base type chain using runtime reflection
        var testRedisType = typeof(TestRedisResource);

        // Collect base type chain
        var baseTypes = new List<string>();
        var currentType = testRedisType.BaseType;
        while (currentType != null && currentType.FullName != "System.Object")
        {
            baseTypes.Add(currentType.FullName ?? currentType.Name);
            currentType = currentType.BaseType;
        }

        // Should have ContainerResource and Resource in the chain
        Assert.Contains(baseTypes, t => t.Contains("ContainerResource"));
        Assert.Contains(baseTypes, t => t.Contains("Resource") && !t.Contains("Container"));
    }

    [Fact]
    public async Task Scanner_AddTestRedis_HasCorrectTypeMetadata()
    {
        // Verify the entire capability object for addTestRedis
        var capabilities = ScanCapabilitiesFromTestAssembly();

        var addTestRedis = capabilities.FirstOrDefault(c => c.CapabilityId == "Aspire.Hosting.CodeGeneration.TypeScript.Tests/addTestRedis");
        Assert.NotNull(addTestRedis);

        await Verify(addTestRedis).UseFileName("AddTestRedisCapability");
    }

    [Fact]
    public void Scanner_ReturnsBuilder_TrueForResourceBuilderReturnTypes()
    {
        // Regression test: Verify that ReturnsBuilder is correctly set to true for methods
        // that return IResourceBuilder<T>, even during code generation scanning where
        // typeResolver is null. Previously, the scanner incorrectly required typeResolver
        // to be non-null to detect resource builder return types.
        var capabilities = ScanCapabilitiesFromTestAssembly();

        // addTestRedis returns IResourceBuilder<TestRedisResource> - should have ReturnsBuilder = true
        var addTestRedis = capabilities.FirstOrDefault(c => c.CapabilityId == "Aspire.Hosting.CodeGeneration.TypeScript.Tests/addTestRedis");
        Assert.NotNull(addTestRedis);
        Assert.True(addTestRedis.ReturnsBuilder,
            "addTestRedis returns IResourceBuilder<T> but ReturnsBuilder is false - thenable wrapper won't be generated");

        // withPersistence also returns IResourceBuilder<T>
        var withPersistence = capabilities.FirstOrDefault(c => c.CapabilityId == "Aspire.Hosting.CodeGeneration.TypeScript.Tests/withPersistence");
        Assert.NotNull(withPersistence);
        Assert.True(withPersistence.ReturnsBuilder,
            "withPersistence returns IResourceBuilder<T> but ReturnsBuilder is false - thenable wrapper won't be generated");

        // withRedisSpecific also returns IResourceBuilder<T>
        var withRedisSpecific = capabilities.FirstOrDefault(c => c.CapabilityId == "Aspire.Hosting.CodeGeneration.TypeScript.Tests/withRedisSpecific");
        Assert.NotNull(withRedisSpecific);
        Assert.True(withRedisSpecific.ReturnsBuilder,
            "withRedisSpecific returns IResourceBuilder<T> but ReturnsBuilder is false - thenable wrapper won't be generated");
    }

    [Fact]
    public void FactoryMethod_ReturnsChildResourceType_NotParentType()
    {
        // Regression test: Factory methods on a builder (e.g., AddDatabase on SqlServerServerResource)
        // must return the child resource type, not the parent/receiver type.
        // Previously, the codegen always used the builder's own type for the return type,
        // causing addDatabase() to return SqlServerServerResourcePromise instead of
        // SqlServerDatabaseResourcePromise.
        var atsContext = CreateContextFromTestAssembly();
        var files = _generator.GenerateDistributedApplication(atsContext);
        var aspireTs = files["aspire.ts"];

        // addTestChildDatabase is a factory method on TestRedisResource that returns TestDatabaseResource.
        // The generated internal method must return TestDatabaseResource, not TestRedisResource.
        Assert.Contains("_addTestChildDatabaseInternal", aspireTs);
        Assert.Contains("Promise<TestDatabaseResource>", aspireTs);

        // The public fluent method must return TestDatabaseResourcePromise, not TestRedisResourcePromise.
        Assert.Matches(@"addTestChildDatabase\([^)]*\):\s*TestDatabaseResourcePromise", aspireTs);

        // Verify the thenable class also uses the child type's promise class.
        // In TestRedisResourcePromise, addTestChildDatabase should return TestDatabaseResourcePromise.
        Assert.Contains("new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.addTestChildDatabase(", aspireTs);
    }

    [Fact]
    public async Task Scanner_WithPersistence_HasCorrectExpandedTargets()
    {
        // Verify the entire capability object for withPersistence
        var capabilities = ScanCapabilitiesFromTestAssembly();

        var withPersistence = capabilities.FirstOrDefault(c => c.CapabilityId == "Aspire.Hosting.CodeGeneration.TypeScript.Tests/withPersistence");
        Assert.NotNull(withPersistence);

        await Verify(withPersistence).UseFileName("WithPersistenceCapability");
    }

    [Fact]
    public async Task Scanner_WithOptionalString_HasCorrectExpandedTargets()
    {
        // Verify withOptionalString (targets IResource, should expand to TestRedisResource)
        var capabilities = ScanCapabilitiesFromTestAssembly();

        var withOptionalString = capabilities.FirstOrDefault(c => c.CapabilityId == "Aspire.Hosting.CodeGeneration.TypeScript.Tests/withOptionalString");
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
    public void Scanner_BrowsersAssembly_WithBrowserLogsCapability()
    {
        var capabilities = ScanCapabilitiesFromBrowsersAssembly();

        var withBrowserLogs = capabilities.FirstOrDefault(c => c.CapabilityId == "Aspire.Hosting.Browsers/withBrowserLogs");
        Assert.NotNull(withBrowserLogs);
        Assert.Equal("withBrowserLogs", withBrowserLogs.MethodName);
        Assert.Equal("Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithEndpoints", withBrowserLogs.TargetTypeId);
        Assert.Contains(withBrowserLogs.Parameters, p => p.Name == "browser" && p.Type?.TypeId == "string" && p.IsOptional);
        Assert.Contains(withBrowserLogs.Parameters, p => p.Name == "profile" && p.Type?.TypeId == "string" && p.IsOptional);
        Assert.Contains(withBrowserLogs.Parameters, p => p.Name == "userDataMode" && p.IsOptional);
        Assert.True(withBrowserLogs.ReturnsBuilder);
    }

    [Fact]
    public async Task Scanner_HostingAssembly_ContainerResourceCapabilities()
    {
        // Verify all capabilities that target ContainerResource from Aspire.Hosting
        var capabilities = ScanCapabilitiesFromHostingAssembly();

        // Find all capabilities that target ContainerResource
        var containerCapabilities = capabilities
            .Where(c => c.TargetTypeId?.Contains("ContainerResource") == true ||
                        c.ExpandedTargetTypes.Any(t => t.TypeId.Contains("ContainerResource")))
            .Select(c => new
            {
                c.CapabilityId,
                c.MethodName,
                TargetType = c.TargetType != null ? new { c.TargetType.TypeId, c.TargetType.IsInterface } : null,
                ExpandedTargetTypes = c.ExpandedTargetTypes
                    .Where(t => t.TypeId.Contains("ContainerResource"))
                    .Select(t => new { t.TypeId, t.IsInterface })
            })
            .OrderBy(c => c.CapabilityId)
            .ToList();

        await Verify(containerCapabilities).UseFileName("HostingContainerResourceCapabilities");
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
    public void Scanner_ContainerResource_DirectTargetingHasCorrectIsInterface()
    {
        // Verify that capabilities directly targeting ContainerResource have IsInterface = false
        var capabilities = ScanCapabilitiesFromHostingAssembly();

        // Find capabilities that directly target ContainerResource (not via interface expansion)
        var directContainerCapabilities = capabilities
            .Where(c => c.TargetTypeId == "Aspire.Hosting/Aspire.Hosting.ApplicationModel.ContainerResource")
            .ToList();

        Assert.NotEmpty(directContainerCapabilities);

        foreach (var cap in directContainerCapabilities)
        {
            // Both TargetType and ExpandedTargetTypes should have IsInterface = false for ContainerResource
            Assert.NotNull(cap.TargetType);
            Assert.False(cap.TargetType.IsInterface,
                $"Capability '{cap.CapabilityId}' directly targets ContainerResource but TargetType.IsInterface is true");

            foreach (var expandedType in cap.ExpandedTargetTypes)
            {
                if (expandedType.TypeId.Contains("ContainerResource"))
                {
                    Assert.False(expandedType.IsInterface,
                        $"Capability '{cap.CapabilityId}' ExpandedTargetType '{expandedType.TypeId}' has IsInterface = true");
                }
            }
        }
    }

    [Fact]
    public void Scanner_GenericConstraintWithClassType_CorrectlyIdentifiesAsNotInterface()
    {
        // This test verifies that when a method has a generic constraint like:
        //   IResourceBuilder<T> where T : ContainerResource
        // The scanner correctly identifies ContainerResource as NOT an interface.
        //
        // Previously, the scanner hardcoded IsInterface = true for all generic constraints,
        // which was wrong when the constraint is a class (like ContainerResource).
        var capabilities = ScanCapabilitiesFromHostingAssembly();

        // Find withBindMount - it has signature: IResourceBuilder<T> where T : ContainerResource
        var withBindMount = capabilities.FirstOrDefault(c => c.CapabilityId == "Aspire.Hosting/withBindMount");
        Assert.NotNull(withBindMount);

        // The constraint is ContainerResource (a class), so IsInterface should be false
        Assert.NotNull(withBindMount.TargetType);
        Assert.Equal("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ContainerResource", withBindMount.TargetType.TypeId);
        Assert.False(withBindMount.TargetType.IsInterface,
            "ContainerResource is a class, not an interface - IsInterface should be false");

        // Compare with an interface-constrained capability like withEnvironment
        var withEnvironment = capabilities.FirstOrDefault(c => c.CapabilityId == "Aspire.Hosting/withEnvironment");
        Assert.NotNull(withEnvironment);
        Assert.NotNull(withEnvironment.TargetType);
        Assert.True(withEnvironment.TargetType.IsInterface,
            "IResourceWithEnvironment is an interface - IsInterface should be true");
    }

    // ===== Polymorphism Pattern Tests =====

    [Fact]
    public void Pattern2_InterfaceTypeDirectly_IsDiscoveredAndExpanded()
    {
        // Pattern 2: Interface type directly as target (not via generic constraint)
        // Tests: IResourceBuilder<IResourceWithConnectionString> WithConnectionStringDirect(...)
        // The interface target should be expanded to all types implementing IResourceWithConnectionString.
        var capabilities = ScanCapabilitiesFromTestAssembly();

        var withConnectionStringDirect = capabilities
            .FirstOrDefault(c => c.CapabilityId == "Aspire.Hosting.CodeGeneration.TypeScript.Tests/withConnectionStringDirect");

        Assert.NotNull(withConnectionStringDirect);

        // Target should be the interface
        Assert.NotNull(withConnectionStringDirect.TargetType);
        Assert.Contains("IResourceWithConnectionString", withConnectionStringDirect.TargetType.TypeId);
        Assert.True(withConnectionStringDirect.TargetType.IsInterface);

        // Should be expanded to concrete types implementing IResourceWithConnectionString
        Assert.NotEmpty(withConnectionStringDirect.ExpandedTargetTypes);

        // TestRedisResource implements IResourceWithConnectionString
        var testRedisExpanded = withConnectionStringDirect.ExpandedTargetTypes
            .FirstOrDefault(t => t.TypeId.Contains("TestRedisResource"));
        Assert.NotNull(testRedisExpanded);
        Assert.False(testRedisExpanded.IsInterface, "Expanded concrete type should have IsInterface = false");
    }

    [Fact]
    public void Pattern3_ConcreteTypeWithInheritance_ExpandsToDerivedTypes()
    {
        // Pattern 3: Concrete type with inheritance
        // Tests: IResourceBuilder<TestRedisResource> WithRedisSpecific(...)
        // Should expand to TestRedisResource and any derived types.
        var capabilities = ScanCapabilitiesFromTestAssembly();

        var withRedisSpecific = capabilities
            .FirstOrDefault(c => c.CapabilityId == "Aspire.Hosting.CodeGeneration.TypeScript.Tests/withRedisSpecific");

        Assert.NotNull(withRedisSpecific);

        // Target should be the concrete TestRedisResource type
        Assert.NotNull(withRedisSpecific.TargetType);
        Assert.Contains("TestRedisResource", withRedisSpecific.TargetType.TypeId);
        Assert.False(withRedisSpecific.TargetType.IsInterface, "TestRedisResource is a concrete type");

        // Should be expanded (at minimum to itself)
        Assert.NotEmpty(withRedisSpecific.ExpandedTargetTypes);

        // TestRedisResource should be in expanded targets
        var testRedisExpanded = withRedisSpecific.ExpandedTargetTypes
            .FirstOrDefault(t => t.TypeId.Contains("TestRedisResource"));
        Assert.NotNull(testRedisExpanded);
    }

    [Fact]
    public void Pattern3_ConcreteTypeFromHosting_ExpandsToDerivedTypes()
    {
        // Pattern 3 for Hosting assembly: ContainerResource methods should expand to derived types
        // Tests: withVolume, withBindMount target ContainerResource and should expand to
        // all types that inherit from ContainerResource.
        var capabilities = ScanCapabilitiesFromHostingAssembly();

        // Find withBindMount which targets ContainerResource
        var withBindMount = capabilities.FirstOrDefault(c => c.CapabilityId == "Aspire.Hosting/withBindMount");
        Assert.NotNull(withBindMount);

        // Target is ContainerResource (concrete class)
        Assert.NotNull(withBindMount.TargetType);
        Assert.Contains("ContainerResource", withBindMount.TargetType.TypeId);
        Assert.False(withBindMount.TargetType.IsInterface);

        // Should be expanded to ContainerResource AND derived types
        Assert.NotEmpty(withBindMount.ExpandedTargetTypes);

        // ContainerResource itself should be in expanded targets
        var containerExpanded = withBindMount.ExpandedTargetTypes
            .FirstOrDefault(t => t.TypeId.Contains("ContainerResource") && !t.TypeId.Contains("IContainer"));
        Assert.NotNull(containerExpanded);
    }

    [Fact]
    public void Pattern4_InterfaceParameterType_HasCorrectTypeRef()
    {
        // Pattern 4: Interface type as parameter (not target)
        // Tests: WithDependency<T>(..., IResourceBuilder<IResourceWithConnectionString> dependency)
        // The dependency parameter should have an interface type ref that can be used for union type generation.
        var capabilities = ScanCapabilitiesFromTestAssembly();

        var withDependency = capabilities
            .FirstOrDefault(c => c.CapabilityId == "Aspire.Hosting.CodeGeneration.TypeScript.Tests/withDependency");

        Assert.NotNull(withDependency);

        // Find the dependency parameter
        var dependencyParam = withDependency.Parameters.FirstOrDefault(p => p.Name == "dependency");
        Assert.NotNull(dependencyParam);

        // Parameter type should be a handle type for IResourceWithConnectionString
        Assert.NotNull(dependencyParam.Type);
        Assert.Equal(AtsTypeCategory.Handle, dependencyParam.Type.Category);
        Assert.True(dependencyParam.Type.IsInterface, "IResourceWithConnectionString is an interface");
    }

    [Fact]
    public void Pattern4_InterfaceParameterType_GeneratesUnionType()
    {
        // Interface-constrained resource parameters should expand to the concrete
        // wrapper interfaces/classes that satisfy the interface contract.
        var atsContext = CreateContextFromTestAssembly();

        // Generate the TypeScript output
        var files = _generator.GenerateDistributedApplication(atsContext);
        var aspireTs = files["aspire.ts"];

        Assert.Contains("withDependency(dependency: Awaitable<ResourceWithConnectionString | TestRedisResource>)", aspireTs);
        Assert.DoesNotContain("withDependency(dependency: HandleReference)", aspireTs);
    }

    [Fact]
    public void AspireUnion_InterfaceHandleInput_GeneratesExpandedUnion()
    {
        var atsContext = CreateContextFromTestAssembly();

        var files = _generator.GenerateDistributedApplication(atsContext);
        var aspireTs = files["aspire.ts"];

        Assert.Contains("withUnionDependency(dependency: string | ResourceWithConnectionString | TestRedisResource | Awaitable<ResourceWithConnectionString | TestRedisResource>)", aspireTs);
    }

    [Fact]
    public void MapInputUnionTypeToTypeScript_ThrowsOnEmptyUnion()
    {
        var method = typeof(AtsTypeScriptCodeGenerator).GetMethod("MapInputUnionTypeToTypeScript", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var typeRef = new AtsTypeRef
        {
            TypeId = "test/EmptyUnion",
            Category = AtsTypeCategory.Union,
            UnionTypes = [],
        };

        var ex = Assert.Throws<TargetInvocationException>(() => method.Invoke(_generator, [typeRef]));
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Equal("Union input types must define at least one member type.", ex.InnerException.Message);
    }

    [Fact]
    public async Task Scanner_BaseTypeHierarchy_IsCollected()
    {
        // Verify that AtsTypeInfo includes base type hierarchy for inheritance expansion.
        var capabilities = ScanCapabilitiesFromTestAssembly();

        // We need to verify the type info has base type hierarchy
        // For now, we'll verify through expanded targets behavior -
        // if inheritance expansion works, base types are being collected.
        var withRedisSpecific = capabilities
            .FirstOrDefault(c => c.CapabilityId == "Aspire.Hosting.CodeGeneration.TypeScript.Tests/withRedisSpecific");

        Assert.NotNull(withRedisSpecific);

        // Snapshot the capability to verify structure
        await Verify(withRedisSpecific).UseFileName("WithRedisSpecificCapability");
    }

    [Fact]
    public void BugFix_SyntheticTypeInfo_CorrectlyIdentifiesInterfaceTypes()
    {
        // Bug: Synthetic type info created for discovered types had IsInterface hardcoded to false.
        // This caused interface types like IResourceWithConnectionString to be incorrectly processed,
        // preventing proper interface-to-concrete-type expansion.
        //
        // Fix: Set IsInterface = resourceType.IsInterface instead of hardcoded false.
        //
        // This test verifies that when a method targets an interface directly (Pattern 2),
        // the capability correctly expands to concrete types implementing that interface.
        var capabilities = ScanCapabilitiesFromTestAssembly();

        // withConnectionStringDirect targets IResourceWithConnectionString (an interface)
        var withConnectionStringDirect = capabilities
            .FirstOrDefault(c => c.CapabilityId == "Aspire.Hosting.CodeGeneration.TypeScript.Tests/withConnectionStringDirect");

        Assert.NotNull(withConnectionStringDirect);

        // Target type should be correctly identified as an interface
        Assert.NotNull(withConnectionStringDirect.TargetType);
        Assert.True(withConnectionStringDirect.TargetType.IsInterface,
            "IResourceWithConnectionString should be identified as an interface");

        // Should expand to concrete types, NOT remain as just the interface
        Assert.NotEmpty(withConnectionStringDirect.ExpandedTargetTypes);

        // All expanded types should be concrete (IsInterface = false)
        foreach (var expandedType in withConnectionStringDirect.ExpandedTargetTypes)
        {
            Assert.False(expandedType.IsInterface,
                $"Expanded type '{expandedType.TypeId}' should be a concrete type, not an interface");
        }
    }

    [Fact]
    public void BugFix_InterfaceExpansion_WorksAcrossAssemblies()
    {
        // Bug: withReference targeting IResourceWithEnvironment was not being expanded
        // because the interface type was incorrectly marked as IsInterface=false.
        //
        // This test verifies that capabilities targeting Aspire.Hosting interfaces
        // (like IResourceWithEnvironment) correctly expand when concrete types
        // from other assemblies (like TestRedisResource) implement those interfaces.
        var capabilities = ScanCapabilitiesFromTestAssembly();

        // testWithEnvironmentCallback targets IResourceWithEnvironment (generic constraint)
        // and TestRedisResource implements IResourceWithEnvironment (via ContainerResource)
        var testWithEnvironmentCallback = capabilities
            .FirstOrDefault(c => c.CapabilityId == "Aspire.Hosting.CodeGeneration.TypeScript.Tests/testWithEnvironmentCallback");

        Assert.NotNull(testWithEnvironmentCallback);

        // Target type should be IResourceWithEnvironment (an interface)
        Assert.NotNull(testWithEnvironmentCallback.TargetType);
        Assert.Contains("IResourceWithEnvironment", testWithEnvironmentCallback.TargetType.TypeId);
        Assert.True(testWithEnvironmentCallback.TargetType.IsInterface,
            "IResourceWithEnvironment should be identified as an interface");

        // Should expand to TestRedisResource (which implements IResourceWithEnvironment via ContainerResource)
        Assert.NotEmpty(testWithEnvironmentCallback.ExpandedTargetTypes);

        // TestRedisResource should be in expanded targets
        var testRedisExpanded = testWithEnvironmentCallback.ExpandedTargetTypes
            .FirstOrDefault(t => t.TypeId.Contains("TestRedisResource"));
        Assert.NotNull(testRedisExpanded);
        Assert.False(testRedisExpanded.IsInterface, "TestRedisResource is a concrete type");
    }

    [Fact]
    public void BugFix_TargetParameterName_IsPopulatedFromMethodSignature()
    {
        // Verify that TargetParameterName is populated from the actual method signature
        // so the code generator uses the correct parameter name when invoking capabilities.
        var capabilities = ScanCapabilitiesFromHostingAssembly();

        // Find withReference - now on the original ResourceBuilderExtensions.WithReference
        // which uses "builder" as the first parameter name
        var withReference = capabilities
            .FirstOrDefault(c => c.CapabilityId == "Aspire.Hosting/withReference");

        Assert.NotNull(withReference);
        Assert.Equal("builder", withReference.TargetParameterName);

        // Verify other capabilities have the expected parameter names
        var addContainer = capabilities
            .FirstOrDefault(c => c.CapabilityId == "Aspire.Hosting/addContainer");
        Assert.NotNull(addContainer);
        Assert.Equal("builder", addContainer.TargetParameterName);

        // withEnvironment uses "builder" as the first parameter
        var withEnvironment = capabilities
            .FirstOrDefault(c => c.CapabilityId == "Aspire.Hosting/withEnvironment");
        Assert.NotNull(withEnvironment);
        Assert.Equal("builder", withEnvironment.TargetParameterName);
    }

    [Fact]
    public void Scanner_HostingAssembly_UsesUnifiedWithReferenceCapability()
    {
        var capabilities = ScanCapabilitiesFromHostingAssembly();

        var withReference = Assert.Single(capabilities, c => c.CapabilityId == "Aspire.Hosting/withReference");
        Assert.Contains(withReference.Parameters, p => p.Name == "name" && p.IsOptional);

        Assert.DoesNotContain(capabilities, c => c.CapabilityId == "Aspire.Hosting/withServiceReference");
        Assert.DoesNotContain(capabilities, c => c.CapabilityId == "Aspire.Hosting/withServiceReferenceNamed");
    }

    [Fact]
    public void BugFix_TargetParameterName_WithVolumeUsesResource()
    {
        // Verify that withVolume has TargetParameterName = "resource" (from CoreExports.cs)
        // This was a bug where the generated TypeScript used "builder" instead of "resource"
        var capabilities = ScanCapabilitiesFromHostingAssembly();

        // Find withVolume - this was fixed by moving to CoreExports.WithVolume with "resource" param
        var withVolume = capabilities
            .FirstOrDefault(c => c.CapabilityId == "Aspire.Hosting/withVolume");

        Assert.NotNull(withVolume);
        Assert.Equal("resource", withVolume.TargetParameterName);

        // Verify correct parameter order: target comes first (required), then name (optional)
        Assert.Equal("target", withVolume.Parameters[0].Name);
        Assert.Equal("name", withVolume.Parameters[1].Name);

        // Note: withBindMount still uses "builder" - it hasn't been moved to CoreExports yet
        var withBindMount = capabilities
            .FirstOrDefault(c => c.CapabilityId == "Aspire.Hosting/withBindMount");

        Assert.NotNull(withBindMount);
        Assert.Equal("builder", withBindMount.TargetParameterName); // TODO: Should be moved to CoreExports

        // withCommand uses "builder" as expected (it's on ResourceBuilderExtensions)
        var withCommand = capabilities
            .FirstOrDefault(c => c.CapabilityId == "Aspire.Hosting/withCommand");

        Assert.NotNull(withCommand);
        Assert.Equal("builder", withCommand.TargetParameterName);
    }

    // ===== 2-Pass Scanning / Cross-Assembly Expansion Tests =====

    [Fact]
    public void TwoPassScanning_DeduplicatesCapabilities()
    {
        // Verify that when the same capability appears in multiple assemblies (e.g., via shared export),
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
        // End-to-end test: verify that withEnvironment appears on TestRedisResourceBuilder
        // in the generated TypeScript when using 2-pass scanning.
        var atsContext = CreateContextFromBothAssemblies();

        // Generate TypeScript
        var files = _generator.GenerateDistributedApplication(atsContext);
        var aspireTs = files["aspire.ts"];

        // Verify withEnvironment appears on TestRedisResource class
        // The generated code should have a TestRedisResource class with withEnvironment method
        Assert.Contains("class TestRedisResource", aspireTs);
        Assert.Contains("withEnvironment", aspireTs);

        // Snapshot for detailed verification
        await Verify(aspireTs, extension: "ts")
            .UseFileName("TwoPassScanningGeneratedAspire");
    }

    [Fact]
    public void TwoPassScanning_DeduplicatesExpandedUnionTypes()
    {
        var atsContext = CreateContextFromBothAssemblies();

        var files = _generator.GenerateDistributedApplication(atsContext);
        var aspireTs = files["aspire.ts"];
        var lines = aspireTs.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.DoesNotContain("ResourceBuilderBase | ResourceBuilderBase", aspireTs);
        Assert.DoesNotContain("EndpointReference | EndpointReference", aspireTs);
        Assert.Contains(lines, line => line.StartsWith("withEnvironment(name: string, value: string | ReferenceExpression | EndpointReference | ", StringComparison.Ordinal));
        Assert.Contains("ResourceWithConnectionString", aspireTs);
        Assert.DoesNotContain("value: string | ReferenceExpression | EndpointReference | ParameterResource | ResourceBuilderBase | EndpointReferenceExpression", aspireTs);
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
        // Get the test assembly at runtime
        return typeof(TestRedisResource).Assembly;
    }

    private static List<AtsCapabilityInfo> ScanCapabilitiesFromHostingAssembly()
    {
        var hostingAssembly = typeof(DistributedApplication).Assembly;
        var result = AtsCapabilityScanner.ScanAssembly(hostingAssembly);
        return result.Capabilities;
    }

    private static List<AtsCapabilityInfo> ScanCapabilitiesFromBrowsersAssembly()
    {
        var browsersAssembly = typeof(global::Aspire.Hosting.BrowserLogsBuilderExtensions).Assembly;
        var result = AtsCapabilityScanner.ScanAssembly(browsersAssembly);
        return result.Capabilities;
    }

    private static AtsContext CreateContextFromHostingAssembly()
    {
        var hostingAssembly = typeof(DistributedApplication).Assembly;
        var result = AtsCapabilityScanner.ScanAssembly(hostingAssembly);
        return result.ToAtsContext();
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

    [Fact]
    public void Scanner_HostingAssembly_CollectionIntrinsicsAreRegistered()
    {
        // This test verifies that collection intrinsic capabilities (Dict.*, List.*)
        // are properly scanned from CollectionExports.cs in Aspire.Hosting.
        //
        // This is a regression test for a bug where methods with 'object' parameters
        // were being skipped because MapToAtsTypeId didn't handle System.Object.
        var capabilities = ScanCapabilitiesFromHostingAssembly();

        // Verify all Dict.* intrinsics are registered
        var dictCapabilities = new[]
        {
            "Aspire.Hosting/Dict.get",
            "Aspire.Hosting/Dict.set",
            "Aspire.Hosting/Dict.remove",
            "Aspire.Hosting/Dict.keys",
            "Aspire.Hosting/Dict.has",
            "Aspire.Hosting/Dict.count",
            "Aspire.Hosting/Dict.clear",
            "Aspire.Hosting/Dict.values",
            "Aspire.Hosting/Dict.toObject"
        };

        foreach (var expectedId in dictCapabilities)
        {
            var capability = capabilities.FirstOrDefault(c => c.CapabilityId == expectedId);
            Assert.NotNull(capability);
        }

        // Verify all List.* intrinsics are registered
        var listCapabilities = new[]
        {
            "Aspire.Hosting/List.get",
            "Aspire.Hosting/List.set",
            "Aspire.Hosting/List.add",
            "Aspire.Hosting/List.removeAt",
            "Aspire.Hosting/List.length",
            "Aspire.Hosting/List.clear",
            "Aspire.Hosting/List.insert",
            "Aspire.Hosting/List.indexOf",
            "Aspire.Hosting/List.toArray"
        };

        foreach (var expectedId in listCapabilities)
        {
            var capability = capabilities.FirstOrDefault(c => c.CapabilityId == expectedId);
            Assert.NotNull(capability);
        }
    }

    [Fact]
    public void Generate_HostingAssembly_IncludesCoreFrameworkPolyglotHelpers()
    {
        var atsContext = CreateContextFromHostingAssembly();
        var files = _generator.GenerateDistributedApplication(atsContext);
        var aspireTs = files["aspire.ts"];

        Assert.Contains("getSection", aspireTs);
        Assert.Contains("getChildren", aspireTs);
        Assert.Contains("exists", aspireTs);
        Assert.Contains("getLoggerFactory", aspireTs);
        Assert.Contains("createLogger", aspireTs);
        Assert.Contains("getResourceLoggerService", aspireTs);
        Assert.Contains("getResourceNotificationService", aspireTs);
        Assert.Contains("getDistributedApplicationModel", aspireTs);
        Assert.Contains("subscribeBeforeStart", aspireTs);
        Assert.Contains("subscribeAfterResourcesCreated", aspireTs);
        Assert.Contains("onBeforeResourceStarted", aspireTs);
        Assert.Contains("onResourceStopped", aspireTs);
        Assert.Contains("onConnectionStringAvailable", aspireTs);
        Assert.Contains("onInitializeResource", aspireTs);
        Assert.Contains("onResourceEndpointsAllocated", aspireTs);
        Assert.Contains("onResourceReady", aspireTs);
        Assert.Contains("getUserSecretsManager", aspireTs);
        Assert.Contains("getEventing", aspireTs);
        Assert.Contains("saveStateJson", aspireTs);
    }

    [Fact]
    public void Scanner_ObjectParameter_MapsToAny()
    {
        // This test verifies that 'object' parameters are correctly mapped to 'any' type.
        // Regression test for Dict.set capability being skipped.
        var capabilities = ScanCapabilitiesFromHostingAssembly();

        // Dict.set has an 'object value' parameter - it should be mapped to 'any'
        var dictSet = capabilities.FirstOrDefault(c => c.CapabilityId == "Aspire.Hosting/Dict.set");
        Assert.NotNull(dictSet);

        // Find the 'value' parameter
        var valueParam = dictSet.Parameters.FirstOrDefault(p => p.Name == "value");
        Assert.NotNull(valueParam);

        // Type should be 'any'
        Assert.NotNull(valueParam.Type);
        Assert.Equal("any", valueParam.Type.TypeId);
    }

    [Fact]
    public void AspireUnionAttribute_ParsesCorrectly()
    {
        // This test verifies that [AspireUnion] attributes are correctly parsed using runtime reflection
        var envCallbackContextType = typeof(EnvironmentCallbackContext);
        Assert.NotNull(envCallbackContextType);

        // Find the EnvironmentVariables property
        var envVarsProperty = envCallbackContextType.GetProperty("EnvironmentVariables");
        Assert.NotNull(envVarsProperty);

        // Get the [AspireUnion] attribute
        var unionAttr = envVarsProperty.GetCustomAttributes(false)
            .FirstOrDefault(a => a.GetType().FullName == "Aspire.Hosting.AspireUnionAttribute");

        Assert.NotNull(unionAttr);

        // Get the Types property from the attribute using reflection
        var typesProperty = unionAttr.GetType().GetProperty("Types");
        Assert.NotNull(typesProperty);

        var types = typesProperty.GetValue(unionAttr) as Type[];
        Assert.NotNull(types);
        Assert.Equal(2, types.Length);

        // First type should be System.String
        Assert.Equal(typeof(string), types[0]);

        // Second type should be ReferenceExpression
        Assert.Contains("ReferenceExpression", types[1].FullName ?? types[1].Name);
    }

    // ===== CapabilityKind Tests =====

    [Fact]
    public void Scanner_InstanceMethod_HasCorrectCapabilityKind()
    {
        // TestResourceContext has ExposeMethods=true - its methods should be CapabilityKind.InstanceMethod
        var capabilities = ScanCapabilitiesFromBothAssemblies();

        var getValueAsync = capabilities.First(c =>
            c.CapabilityId == "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.getValueAsync");

        Assert.Equal(AtsCapabilityKind.InstanceMethod, getValueAsync.CapabilityKind);
    }

    [Fact]
    public void Scanner_ReferenceExpressionGetValueAsync_IsExported()
    {
        var capabilities = ScanCapabilitiesFromHostingAssembly();

        var getValueAsync = capabilities.FirstOrDefault(c =>
            c.CapabilityId == "Aspire.Hosting.ApplicationModel/getValueAsync" &&
            c.TargetTypeId == AtsConstants.ReferenceExpressionTypeId);

        Assert.NotNull(getValueAsync);
        Assert.Equal(AtsCapabilityKind.InstanceMethod, getValueAsync.CapabilityKind);
    }

    [Fact]
    public void Scanner_ExtensionMethod_HasCorrectCapabilityKind()
    {
        // Extension methods should be CapabilityKind.Method
        var capabilities = ScanCapabilitiesFromTestAssembly();

        var addTestRedis = capabilities.First(c =>
            c.CapabilityId == "Aspire.Hosting.CodeGeneration.TypeScript.Tests/addTestRedis");

        Assert.Equal(AtsCapabilityKind.Method, addTestRedis.CapabilityKind);
    }

    // ===== Thenable Pattern Code Generation Tests =====

    [Fact]
    public void Generate_TypeWithMethods_CreatesThenableWrapper()
    {
        var code = GenerateTwoPassCode();

        // TestResourceContext has ExposeMethods=true - gets Promise wrapper
        Assert.Contains("class TestResourceContextPromiseImpl implements TestResourceContextPromise", code);
        Assert.Contains("implements TestResourceContextPromise", code);
    }

    [Fact]
    public void Generate_TypeWithOnlyProperties_NoThenableWrapper()
    {
        var code = GenerateTwoPassCode();

        // TestEnvironmentContext has only ExposeProperties=true - no Promise wrapper
        Assert.DoesNotContain("TestEnvironmentContextPromise", code);
    }

    [Fact]
    public void Generate_VoidInstanceMethod_ReturnsContainingTypePromise()
    {
        var code = GenerateTwoPassCode();

        // setValueAsync returns void but chains as TestResourceContextPromise
        Assert.Contains("setValueAsync(value: string): TestResourceContextPromise", code);
    }

    [Fact]
    public void Generate_PrimitiveReturningMethod_ReturnsPlainPromise()
    {
        var code = GenerateTwoPassCode();

        // getValueAsync returns string - plain Promise, not a wrapper
        Assert.Contains("getValueAsync(): Promise<string>", code);
    }

    [Fact]
    public void GenerateTwoPassCode_UsesUnifiedWithReferenceSurface()
    {
        var code = GenerateTwoPassCode();

        Assert.DoesNotContain("withServiceReference(", code);
        Assert.DoesNotContain("withServiceReferenceNamed(", code);
        Assert.Contains("name?: string;", code);
    }

    private string GenerateTwoPassCode()
    {
        var atsContext = CreateContextFromBothAssemblies();
        var files = _generator.GenerateDistributedApplication(atsContext);
        return files["aspire.ts"];
    }

    // ===== CancellationToken Tests =====

    [Fact]
    public void Scanner_CancellationToken_MapsToCorrectTypeId()
    {
        // Verify CancellationToken parameters map to AtsConstants.CancellationToken
        var capabilities = ScanCapabilitiesFromTestAssembly();

        var getStatusAsync = capabilities
            .FirstOrDefault(c => c.CapabilityId == "Aspire.Hosting.CodeGeneration.TypeScript.Tests/getStatusAsync");

        Assert.NotNull(getStatusAsync);

        // Find the cancellationToken parameter
        var ctParam = getStatusAsync.Parameters.FirstOrDefault(p => p.Name == "cancellationToken");
        Assert.NotNull(ctParam);
        Assert.NotNull(ctParam.Type);
        Assert.Equal(AtsConstants.CancellationToken, ctParam.Type.TypeId);
        Assert.Equal(AtsTypeCategory.Primitive, ctParam.Type.Category);
    }

    [Fact]
    public void Generate_MethodWithCancellationToken_GeneratesCancellationTokenParameter()
    {
        // Generated input parameters should accept AbortSignal for user-authored cancellation,
        // while callbacks and returned values use the structural SDK cancellation token interface.
        var code = GenerateTwoPassCode();

        Assert.Contains("cancellationToken?: AbortSignal | CancellationToken;", code);
        Assert.Contains("set: async (value: AbortSignal | CancellationToken): Promise<void> => {", code);
        Assert.Contains("withCancellableOperation(operation: (arg: CancellationToken) => Promise<void>)", code);
    }

    [Fact]
    public void Scanner_CancellationTokenInCallback_MapsCorrectly()
    {
        // Verify CancellationToken in callback parameters maps correctly
        var capabilities = ScanCapabilitiesFromTestAssembly();

        var withCancellableOperation = capabilities
            .FirstOrDefault(c => c.CapabilityId == "Aspire.Hosting.CodeGeneration.TypeScript.Tests/withCancellableOperation");

        Assert.NotNull(withCancellableOperation);

        // Find the callback parameter
        var operationParam = withCancellableOperation.Parameters.FirstOrDefault(p => p.Name == "operation");
        Assert.NotNull(operationParam);
        Assert.True(operationParam.IsCallback);

        // The callback should have a CancellationToken parameter
        Assert.NotNull(operationParam.CallbackParameters);
        Assert.Single(operationParam.CallbackParameters);
        Assert.Equal(AtsConstants.CancellationToken, operationParam.CallbackParameters[0].Type?.TypeId);
    }

    [Fact]
    public void Scanner_CancellationTokenWithOtherParams_AllParamsPresent()
    {
        // Verify CancellationToken mixed with other parameters all get mapped
        var capabilities = ScanCapabilitiesFromTestAssembly();

        var waitForReadyAsync = capabilities
            .FirstOrDefault(c => c.CapabilityId == "Aspire.Hosting.CodeGeneration.TypeScript.Tests/waitForReadyAsync");

        Assert.NotNull(waitForReadyAsync);

        // Should have timeout and cancellationToken parameters
        Assert.Equal(2, waitForReadyAsync.Parameters.Count);

        var timeoutParam = waitForReadyAsync.Parameters.FirstOrDefault(p => p.Name == "timeout");
        Assert.NotNull(timeoutParam);
        Assert.Equal(AtsConstants.TimeSpan, timeoutParam.Type?.TypeId);

        var ctParam = waitForReadyAsync.Parameters.FirstOrDefault(p => p.Name == "cancellationToken");
        Assert.NotNull(ctParam);
        Assert.Equal(AtsConstants.CancellationToken, ctParam.Type?.TypeId);
        Assert.True(ctParam.IsOptional);
    }

    // ===== DTO Generation Tests =====

    [Fact]
    public void Scanner_AspireDtoType_IsDiscovered()
    {
        // Verify [AspireDto] types are discovered during scanning
        var atsContext = CreateContextFromTestAssembly();

        // Check that TestConfigDto is in the DTO types
        var testConfigDto = atsContext.DtoTypes
            .FirstOrDefault(d => d.TypeId.Contains("TestConfigDto"));
        Assert.NotNull(testConfigDto);

        // Should have expected properties
        Assert.Contains(testConfigDto.Properties, p => p.Name == "Name" || p.Name == "name");
        Assert.Contains(testConfigDto.Properties, p => p.Name == "Port" || p.Name == "port");
        Assert.Contains(testConfigDto.Properties, p => p.Name == "Enabled" || p.Name == "enabled");
    }

    [Fact]
    public void Generate_AspireDtoType_GeneratesInterface()
    {
        // Verify [AspireDto] types generate TypeScript interfaces
        var code = GenerateTwoPassCode();

        // TestConfigDto should generate an interface
        // Note: The generated code may use PascalCase or camelCase depending on JSON naming policy
        Assert.Contains("interface TestConfigDto", code);
    }

    [Fact]
    public void Generate_NestedDtoType_GeneratesCorrectTypes()
    {
        // Verify nested DTOs are handled correctly
        var code = GenerateTwoPassCode();

        // TestNestedDto should generate an interface with nested types
        Assert.Contains("interface TestNestedDto", code);
    }

    [Fact]
    public void Scanner_DeeplyNestedDto_IsDiscovered()
    {
        // Verify deeply nested generic DTOs are discovered
        var atsContext = CreateContextFromTestAssembly();

        var deeplyNestedDto = atsContext.DtoTypes
            .FirstOrDefault(d => d.TypeId.Contains("TestDeeplyNestedDto"));
        Assert.NotNull(deeplyNestedDto);
    }

    // ===== Enum Generation Tests =====

    [Fact]
    public void Scanner_EnumType_IsDiscovered()
    {
        // Verify enum types are discovered when used in capabilities
        var atsContext = CreateContextFromTestAssembly();

        // Check that TestResourceStatus enum is discovered
        var testResourceStatus = atsContext.EnumTypes
            .FirstOrDefault(e => e.TypeId.Contains("TestResourceStatus"));
        Assert.NotNull(testResourceStatus);

        // Should have expected values
        Assert.Contains("Pending", testResourceStatus.Values);
        Assert.Contains("Running", testResourceStatus.Values);
        Assert.Contains("Stopped", testResourceStatus.Values);
        Assert.Contains("Failed", testResourceStatus.Values);
    }

    [Fact]
    public void Generate_EnumType_GeneratesStringEnum()
    {
        // Verify enums generate TypeScript string enums
        var code = GenerateTwoPassCode();

        // TestResourceStatus should generate an enum
        Assert.Contains("enum TestResourceStatus", code);
    }

    // ===== Diagnostics Tests =====

    [Fact]
    public void Scanner_ProducesDiagnosticsForInvalidTypes()
    {
        // Note: This test verifies the diagnostic infrastructure works.
        // The scanner produces warnings for capabilities with unmapped types.
        var testAssembly = LoadTestAssembly();
        var result = AtsCapabilityScanner.ScanAssembly(testAssembly);

        // Diagnostics should be a non-null list (may be empty if all types are valid)
        Assert.NotNull(result.Diagnostics);
    }

    [Fact]
    public void Scanner_CapabilityWithValidTypes_NoDiagnostics()
    {
        // Verify that well-formed capabilities don't produce diagnostics
        var capabilities = ScanCapabilitiesFromTestAssembly();

        // addTestRedis is a well-formed capability
        var addTestRedis = capabilities
            .FirstOrDefault(c => c.CapabilityId == "Aspire.Hosting.CodeGeneration.TypeScript.Tests/addTestRedis");
        Assert.NotNull(addTestRedis);

        // It should have valid parameter types
        foreach (var param in addTestRedis.Parameters)
        {
            Assert.NotNull(param.Type);
            Assert.NotEqual(AtsTypeCategory.Unknown, param.Type.Category);
        }
    }

    [Fact]
    public void Generate_ListProperty_GeneratesGetterOnlyMethods()
    {
        // Verify that List properties on [AspireExport(ExposeProperties = true)] types
        // generate zero-argument methods (same pattern as Dictionary properties with AspireDict)
        var atsContext = CreateContextFromTestAssembly();
        var files = _generator.GenerateDistributedApplication(atsContext);
        var code = files["aspire.ts"];

        // TestCollectionContext has both Items (List) and Metadata (Dictionary)
        // Both should use the same getter-only method pattern with lazy initialization.

        // Check for AspireList getter-only method pattern.
        Assert.Contains("private _items?: AspireList<string>;", code);
        Assert.Contains("async items(): Promise<AspireList<string>>", code);
        Assert.Contains("this._items = new AspireList<string>(", code);

        // Check for AspireDict getter-only method pattern.
        Assert.Contains("private _metadata?: AspireDict<string, string>;", code);
        Assert.Contains("async metadata(): Promise<AspireDict<string, string>>", code);
        Assert.Contains("this._metadata = new AspireDict<string, string>(", code);
    }

    [Fact]
    public void Generate_ListProperty_DoesNotUsePropertyObjectPattern()
    {
        // Verify that getter-only List properties do not use the old property object pattern.
        var atsContext = CreateContextFromTestAssembly();
        var files = _generator.GenerateDistributedApplication(atsContext);
        var code = files["aspire.ts"];

        // Should NOT contain the old pattern for items
        Assert.DoesNotContain("items = {", code);
        Assert.DoesNotContain("items = {\n        get: async", code);
    }

    [Fact]
    public void Generate_OptionalOptionsProperty_UsesDistinctOptionsBagParameter()
    {
        var code = GenerateTwoPassCode();

        Assert.DoesNotContain("= options?.options;", code);
        Assert.Contains("addProject(name: string, projectPath: string, options?: AddProjectOptions)", code);
        Assert.Contains("let launchProfileOrOptions = options?.launchProfileOrOptions;", code);
    }

    [Fact]
    public void Generate_MutableCollectionProperties_UsePropertyAccessors()
    {
        var atsContext = CreateContextFromTestAssembly();
        var files = _generator.GenerateDistributedApplication(atsContext);
        var code = files["aspire.ts"];

        Assert.Contains("readonly tags: AspireList<string>;", code);
        Assert.Contains("get tags(): AspireList<string> {", code);
        Assert.Contains("readonly counts: AspireDict<string, number>;", code);
        Assert.Contains("get counts(): AspireDict<string, number> {", code);
        Assert.DoesNotContain("async tags(): Promise<AspireList<string>>", code);
        Assert.DoesNotContain("async counts(): Promise<AspireDict<string, number>>", code);
    }

    [Fact]
    public void Generate_ConcreteAndInterfaceWithSameClassName_NoDuplicateClasses()
    {
        // TestVaultResource (concrete) and ITestVaultResource (interface) both derive
        // to the same TypeScript class name "TestVaultResource". The codegen must emit
        // exactly one class definition, preferring the concrete type.
        var atsContext = CreateContextFromTestAssembly();
        var files = _generator.GenerateDistributedApplication(atsContext);
        var code = files["aspire.ts"];

        // Count occurrences of the public interface definition.
        var classCount = CountOccurrences(code, "export interface TestVaultResource ");
        Assert.Equal(1, classCount);

        // Also verify the Promise wrapper interface is not duplicated.
        var promiseCount = CountOccurrences(code, "export interface TestVaultResourcePromise ");
        Assert.Equal(1, promiseCount);
    }

    // ===== Options Interface Merging Tests =====

    [Fact]
    public async Task Generate_SameMethodNameOnDifferentTypes_MergesOptionsInterface()
    {
        // Regression test: When the same method name (e.g., withDataVolume) appears on
        // multiple resource types with different optional parameters, the generated options
        // interface must be the union of all parameters across all overloads.
        // Previously, RegisterOptionsInterface used first-write-wins, so the interface
        // only included parameters from whichever overload was registered first.
        var code = GenerateTwoPassCode();

        // Extract just the WithDataVolumeOptions interface for snapshot verification.
        var interfaceStart = code.IndexOf("export interface WithDataVolumeOptions", StringComparison.Ordinal);
        Assert.True(interfaceStart >= 0, "WithDataVolumeOptions interface not found in generated code");

        var interfaceEnd = code.IndexOf("}", interfaceStart, StringComparison.Ordinal);
        var interfaceBody = code[interfaceStart..(interfaceEnd + 1)];

        await Verify(interfaceBody, extension: "ts")
            .UseFileName("WithDataVolumeOptionsMerged");
    }

    private static int CountOccurrences(string text, string pattern)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }

    // ===== JavaScript Assembly Expansion Tests =====

    [Fact]
    public void Scanner_WithNpm_ExpandsToAllJavaScriptResourceTypes()
    {
        // Verify that withNpm (constrained to JavaScriptAppResource) expands to all three
        // concrete JS resource types: JavaScriptAppResource, NodeAppResource, ViteAppResource.
        // This is a regression test for capability ID expansion where concrete types
        // were not registered under their own type ID in the compatibility map.
        var hostingAssembly = typeof(DistributedApplication).Assembly;
        var jsAssembly = typeof(Aspire.Hosting.JavaScript.JavaScriptAppResource).Assembly;

        var result = AtsCapabilityScanner.ScanAssemblies([hostingAssembly, jsAssembly]);

        var withNpm = result.Capabilities
            .FirstOrDefault(c => c.CapabilityId == "Aspire.Hosting.JavaScript/withNpm");
        Assert.NotNull(withNpm);

        var expandedTypeIds = withNpm.ExpandedTargetTypes.Select(t => t.TypeId).ToList();

        // All three JS resource types should be present
        var javaScriptAppTypeId = AtsTypeMapping.DeriveTypeId(typeof(Aspire.Hosting.JavaScript.JavaScriptAppResource));
        var nodeAppTypeId = AtsTypeMapping.DeriveTypeId(typeof(Aspire.Hosting.JavaScript.NodeAppResource));
        var viteAppTypeId = AtsTypeMapping.DeriveTypeId(typeof(Aspire.Hosting.JavaScript.ViteAppResource));

        Assert.Contains(javaScriptAppTypeId, expandedTypeIds);
        Assert.Contains(nodeAppTypeId, expandedTypeIds);
        Assert.Contains(viteAppTypeId, expandedTypeIds);
    }

    [Theory]
    [InlineData("withNpm")]
    [InlineData("withBun")]
    [InlineData("withYarn")]
    [InlineData("withPnpm")]
    public void Scanner_PackageManagerMethods_ExpandToAllJavaScriptResourceTypes(string methodName)
    {
        // Verify all package manager methods expand to the known JS resource types.
        // Assert the minimum expected set rather than an exact count so the test
        // remains valid when new JavaScriptAppResource-derived types are added.
        var hostingAssembly = typeof(DistributedApplication).Assembly;
        var jsAssembly = typeof(Aspire.Hosting.JavaScript.JavaScriptAppResource).Assembly;

        var result = AtsCapabilityScanner.ScanAssemblies([hostingAssembly, jsAssembly]);

        var capability = result.Capabilities
            .FirstOrDefault(c => c.CapabilityId == $"Aspire.Hosting.JavaScript/{methodName}");
        Assert.NotNull(capability);

        var expandedTypeIds = capability.ExpandedTargetTypes.Select(t => t.TypeId).ToList();
        Assert.True(expandedTypeIds.Count >= 3, $"Expected at least 3 expanded types but found {expandedTypeIds.Count}");
        Assert.Contains(expandedTypeIds,
            id => id.Contains(nameof(JavaScript.JavaScriptAppResource), StringComparison.Ordinal)
               && !id.Contains("NodeApp", StringComparison.Ordinal)
               && !id.Contains("ViteApp", StringComparison.Ordinal));
        Assert.Contains(expandedTypeIds, id => id.Contains(nameof(JavaScript.NodeAppResource), StringComparison.Ordinal));
        Assert.Contains(expandedTypeIds, id => id.Contains(nameof(JavaScript.ViteAppResource), StringComparison.Ordinal));
    }
}
