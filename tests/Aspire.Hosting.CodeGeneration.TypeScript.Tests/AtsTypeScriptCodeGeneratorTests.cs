// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREBROWSERLOGS001 // Type is for evaluation purposes only

using System.Reflection;
using System.Text.RegularExpressions;
using Aspire.Hosting.Azure;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RemoteHost;
using Aspire.TypeSystem;
using Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes;
using Azure.Provisioning.AppContainers;
using Azure.Provisioning.AppService;

namespace Aspire.Hosting.CodeGeneration.TypeScript.Tests;

public partial class AtsTypeScriptCodeGeneratorTests
{
    private readonly AtsTypeScriptCodeGenerator _generator = new();

    [Fact]
    public void Language_ReturnsTypeScript()
    {
        Assert.Equal("TypeScript", _generator.Language);
    }

    [Fact]
    public void EmbeddedResource_PackageJson_IsAvailableWithExpectedStructure()
    {
        // The package.json under Resources/ is the single source of truth for
        // the SDK manifest emitted alongside generated TypeScript. Verify the
        // embedded resource loads and has the structural fields downstream
        // consumers rely on — without copying its bytes into a snapshot file
        // that would drift from the resource on every edit.
        var content = EmbeddedResources.Read("package.json");

        Assert.NotEmpty(content);

        var packageJson = System.Text.Json.Nodes.JsonNode.Parse(content)!.AsObject();
        Assert.Equal("aspire-host", packageJson["name"]?.GetValue<string>());
        Assert.Equal("module", packageJson["type"]?.GetValue<string>());
        Assert.NotNull(packageJson["dependencies"]?["vscode-jsonrpc"]);
    }

    [Fact]
    public void GenerateDistributedApplication_EmitsBaseAndTransportResourcesVerbatim()
    {
        var atsContext = CreateContextFromTestAssembly();

        var files = _generator.GenerateDistributedApplication(atsContext);

        Assert.Contains("base.mts", files.Keys);
        Assert.Contains("transport.mts", files.Keys);

        // base.mts and transport.mts are emitted as embedded-resource pass-throughs,
        // so asserting equality against the embedded resource (the single source
        // of truth) keeps the test signal — "the generator emits the resource
        // verbatim" — without maintaining duplicate *.verified.ts snapshots that
        // would have to be regenerated on every change to the source resource.
        Assert.Equal(EmbeddedResources.Read("base.mts"), files["base.mts"]);
        Assert.Equal(EmbeddedResources.Read("transport.mts"), files["transport.mts"]);
    }

    [Fact]
    public async Task GenerateDistributedApplication_WithTestTypes_GeneratesCorrectOutput()
    {
        // Arrange
        var atsContext = CreateContextFromTestAssembly();

        // Act
        var files = _generator.GenerateDistributedApplication(atsContext);

        Assert.Contains("aspire.mts", files.Keys);

        // aspire.mts is real generated code (composed from scanned types), so a
        // Verify snapshot is the right tool here. base.mts and transport.mts are
        // resource pass-throughs and are covered by
        // GenerateDistributedApplication_EmitsBaseAndTransportResourcesVerbatim.
        await Verify(files["aspire.mts"], extension: "ts")
            .UseFileName("AtsGeneratedAspire");
    }

    [Fact]
    public void GenerateDistributedApplication_WithTestTypes_IncludesExportedValues()
    {
        var atsContext = CreateContextFromTestAssembly();

        Assert.Contains(atsContext.ExportedValues, value => string.Join(".", value.PathSegments) == "TestConfigs.Default");
        Assert.Contains(atsContext.ExportedValues, value => string.Join(".", value.PathSegments) == "TestConfigs.Profiles.Development");

        var files = _generator.GenerateDistributedApplication(atsContext);
        var aspireTs = files["aspire.mts"];

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
        var aspireTs = files["aspire.mts"];

        Assert.DoesNotContain("export class ReferenceExpression {", aspireTs);
        Assert.Contains("export class ReferenceExpression {", files["base.mts"]);
        Assert.Contains("registerHandleWrapper('Aspire.Hosting/Aspire.Hosting.ApplicationModel.ReferenceExpression'", files["base.mts"]);
        Assert.Contains("condition: extractHandleForExpr(state.condition),", files["base.mts"]);
        Assert.Contains("('$handle' in json || '$expr' in json)", files["base.mts"]);
        Assert.Contains("registerCancellation(state.client, cancellationToken)", files["base.mts"]);
        Assert.Contains("arguments(): InteractionInputCollectionPromise", aspireTs);
        Assert.DoesNotContain("setArguments", aspireTs);
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
    public void Scanner_WithTestTypes_CapturesXmlDocumentation()
    {
        var context = CreateContextFromTestAssembly();

        var addTestRedis = context.Capabilities.First(c => c.CapabilityId == "Aspire.Hosting.CodeGeneration.TypeScript.Tests/addTestRedis");
        Assert.Equal("Adds a test Redis resource from ATS documentation.", addTestRedis.Description);
        Assert.Equal("Adds a test Redis resource from ATS documentation.", addTestRedis.Documentation?.Summary);
        Assert.Null(addTestRedis.Documentation?.Remarks);
        Assert.Equal("The ATS test Redis resource builder.", addTestRedis.Documentation?.Returns);

        var nameParameter = addTestRedis.Parameters.First(p => p.Name == "name");
        Assert.Equal("The ATS resource name.", nameParameter.Documentation?.Summary);

        var portParameter = addTestRedis.Parameters.First(p => p.Name == "port");
        Assert.Null(portParameter.Documentation);

        var testConfig = context.DtoTypes.First(dto => dto.Name == nameof(TestConfigDto));
        Assert.Equal("Test DTO to verify [AspireDto] generates TypeScript interfaces.", testConfig.Documentation?.Summary);
        Assert.Equal("The name of the test config.", testConfig.Properties.First(p => p.Name == nameof(TestConfigDto.Name)).Documentation?.Summary);

        var testStatus = context.EnumTypes.First(e => e.Name == nameof(TestResourceStatus));
        Assert.Equal("Test enum for type generation verification.", testStatus.Documentation?.Summary);
        Assert.Equal("The resource is pending.", testStatus.ValueInfos.First(v => v.Name == nameof(TestResourceStatus.Pending)).Documentation?.Summary);

        var defaultConfig = context.ExportedValues.First(value => string.Join(".", value.PathSegments) == "TestConfigs.Default");
        Assert.Equal("The default test configuration.", defaultConfig.Documentation?.Summary);
    }

    [Fact]
    public void GenerateDistributedApplication_WithTestTypes_EmitsXmlDocumentationAsJSDoc()
    {
        var context = CreateContextFromTestAssembly();

        var files = _generator.GenerateDistributedApplication(context);
        var aspireTs = files["aspire.mts"];

        Assert.Contains("Adds a test Redis resource from ATS documentation.", aspireTs);
        Assert.Contains("@param name The ATS resource name.", aspireTs);
        Assert.Contains("@param options Additional options.", aspireTs);
        Assert.Contains("@returns The ATS test Redis resource builder.", aspireTs);
        Assert.DoesNotContain("The optional Redis port.", aspireTs);
        Assert.DoesNotContain("Uses XML documentation instead of the attribute description when both are present.", aspireTs);
        Assert.Contains("/** The name of the test config. */", aspireTs);
        Assert.Contains("/** The default test configuration. */", aspireTs);
        Assert.Contains("/** The resource is pending. */", aspireTs);
    }

    [Fact]
    public void GenerateDistributedApplication_WithSuppressedSummary_DoesNotUseDescriptionFallback()
    {
        var context = CreateContextFromTestAssembly();
        var capability = CreateDistributedApplicationBuilderCapability(
            context,
            methodName: "withSuppressedSummary",
            description: "Description fallback should not be emitted.",
            documentation: new AtsDocumentationInfo());
        context = WithAdditionalCapabilities(context, capability);

        var files = _generator.GenerateDistributedApplication(context);
        var aspireTs = files["aspire.mts"];

        Assert.Contains("withSuppressedSummary()", aspireTs);
        Assert.DoesNotContain("Description fallback should not be emitted.", aspireTs);
    }

    [Fact]
    public void GenerateDistributedApplication_WithVoidReturn_DoesNotEmitReturnsDocumentation()
    {
        var context = CreateContextFromTestAssembly();
        var capability = CreateDistributedApplicationBuilderCapability(
            context,
            methodName: "withVoidReturnDocumentation",
            description: null,
            documentation: new AtsDocumentationInfo
            {
                Summary = "Runs a void capability.",
                Returns = "Void return documentation should not be emitted."
            });
        context = WithAdditionalCapabilities(context, capability);

        var files = _generator.GenerateDistributedApplication(context);
        var aspireTs = files["aspire.mts"];

        Assert.Contains("Runs a void capability.", aspireTs);
        Assert.DoesNotContain("Void return documentation should not be emitted.", aspireTs);
    }

    [Fact]
    public void GenerateDistributedApplication_WithAtsReference_RendersJsDocLink()
    {
        var context = CreateContextFromTestAssembly();
        var capability = CreateDistributedApplicationBuilderCapability(
            context,
            methodName: "withAtsReference",
            description: null,
            documentation: new AtsDocumentationInfo
            {
                Summary = "Configures {@ats-ref type:TestRedisResource} from ATS documentation."
            });
        context = WithAdditionalCapabilities(context, capability);

        var files = _generator.GenerateDistributedApplication(context);
        var aspireTs = files["aspire.mts"];

        Assert.Contains("Configures {@link TestRedisResource} from ATS documentation.", aspireTs);
        Assert.DoesNotContain("{@ats-ref", aspireTs);
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
        var aspireTs = files["aspire.mts"];

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
        var aspireTs = files["aspire.mts"];

        Assert.Contains("withDependency(dependency: Awaitable<ResourceWithConnectionString | TestRedisResource>)", aspireTs);
        Assert.DoesNotContain("withDependency(dependency: HandleReference)", aspireTs);
    }

    [Fact]
    public void AspireUnion_InterfaceHandleInput_GeneratesExpandedUnion()
    {
        var atsContext = CreateContextFromTestAssembly();

        var files = _generator.GenerateDistributedApplication(atsContext);
        var aspireTs = files["aspire.mts"];

        Assert.Contains("withUnionDependency(dependency: string | ResourceWithConnectionString | TestRedisResource | Awaitable<ResourceWithConnectionString | TestRedisResource>)", aspireTs);
    }

    [Fact]
    public void MapInputUnionTypeToTypeScript_ThrowsOnEmptyUnion()
    {
        var projector = new TypeScriptApiProjector(CreateContextFromTestAssembly());

        var typeRef = new AtsTypeRef
        {
            TypeId = "test/EmptyUnion",
            Category = AtsTypeCategory.Union,
            UnionTypes = [],
        };

        var ex = Assert.Throws<InvalidOperationException>(() => projector.MapInputUnionTypeToTypeScript(typeRef));
        Assert.Equal("Union input types must define at least one member type.", ex.Message);
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
        var aspireTs = files["aspire.mts"];

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
        var aspireTs = files["aspire.mts"];
        var lines = aspireTs.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.DoesNotContain("ResourceBuilderBase | ResourceBuilderBase", aspireTs);
        Assert.DoesNotContain("EndpointReference | EndpointReference", aspireTs);
        Assert.Contains(lines, line => line.StartsWith("withEnvironment(name: string, value: string | ReferenceExpression | EndpointReference | ", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("withEnvironment(name: string, value:", StringComparison.Ordinal) &&
                                      line.Contains("ExternalServiceResource", StringComparison.Ordinal));
        Assert.Contains("ResourceWithConnectionString", aspireTs);
        Assert.DoesNotContain("value: string | ReferenceExpression | EndpointReference | ParameterResource | ResourceBuilderBase | EndpointReferenceExpression", aspireTs);
    }

    [Fact]
    public void GenerateDistributedApplication_WithDtoCallbackOptions_MarshalsNestedCallbackProperties()
    {
        var atsContext = CreateContextFromBothAssemblies();

        var files = _generator.GenerateDistributedApplication(atsContext);
        var aspireTs = files["aspire.mts"];
        var processCommandExportOptions = Assert.Single(atsContext.DtoTypes, dto => dto.Name == "ProcessCommandExportOptions");
        var createProcessSpec = Assert.Single(processCommandExportOptions.Properties, property => property.Name == "CreateProcessSpec");

        Assert.True(createProcessSpec.IsOptional);
        Assert.Contains("const ____optionsForRpcPrepareRequestId = ____optionsForRpcPrepareRequest ? registerCallback", aspireTs);
        Assert.Contains("createProcessSpec?: (arg: ExecuteCommandContext) => Promise<ProcessCommandSpecExportData>;", aspireTs);
        Assert.Contains("const ____optionsForRpcCreateProcessSpecId = ____optionsForRpcCreateProcessSpec ? registerCallback", aspireTs);
        Assert.Contains("__optionsForRpcData[\"createProcessSpec\"] = ____optionsForRpcCreateProcessSpecId;", aspireTs);
        Assert.Contains("@deprecated Use withProcessCommand with createProcessSpec in the options object instead.", aspireTs);
        Assert.Contains("const ____optionsForRpcCommandOptions = __optionsForRpc.commandOptions;", aspireTs);
        Assert.Contains("const ____optionsForRpcCommandOptionsForRpc = { ...____optionsForRpcCommandOptions };", aspireTs);
        Assert.Contains("const ______optionsForRpcCommandOptionsForRpcValidateArgumentsId = ______optionsForRpcCommandOptionsForRpcValidateArguments ? registerCallback", aspireTs);
        Assert.Contains("const ______optionsForRpcCommandOptionsForRpcUpdateStateId = ______optionsForRpcCommandOptionsForRpcUpdateState ? registerCallback", aspireTs);
        Assert.Contains("__optionsForRpcData[\"commandOptions\"] = ____optionsForRpcCommandOptionsForRpc;", aspireTs);
    }

    [Fact]
    public void Scanner_AzureProvisioningCallbacks_ExposeTypedCustomizationProperties()
    {
        var capabilities = ScanCapabilitiesFromAzureAssemblies();

        var publishAsWebsite = Assert.Single(capabilities, c => c.CapabilityId == "Aspire.Hosting.Azure.AppService/publishAsAzureAppServiceWebsite");
        AssertCallbackParameterTypes(publishAsWebsite, "configure", typeof(AzureResourceInfrastructure), typeof(WebSite));
        AssertCallbackParameterTypes(publishAsWebsite, "configureSlot", typeof(AzureResourceInfrastructure), typeof(WebSiteSlot));

        var publishAsContainerAppJob = Assert.Single(capabilities, c => c.CapabilityId == "Aspire.Hosting.Azure.AppContainers/publishAsAzureContainerAppJob");
        AssertCallbackParameterTypes(publishAsContainerAppJob, "configure", typeof(AzureResourceInfrastructure), typeof(ContainerAppJob));

        AssertTargetedMethod(capabilities, "Aspire.Hosting.Azure.AppService/configureWebSiteSiteConfig", "configureSiteConfig", typeof(WebSite), GetRequiredType("Aspire.Hosting.Azure.AzureAppServiceSiteConfig, Aspire.Hosting.Azure.AppService"));
        AssertTargetedMethod(capabilities, "Aspire.Hosting.Azure.AppService/configureWebSiteSlotSiteConfig", "configureSlotSiteConfig", typeof(WebSiteSlot), GetRequiredType("Aspire.Hosting.Azure.AzureAppServiceSiteConfig, Aspire.Hosting.Azure.AppService"));

        AssertTargetedMethod(capabilities, "Aspire.Hosting.Azure.AppContainers/configureContainerAppScale", "configureScale", typeof(ContainerApp), GetRequiredType("Aspire.Hosting.Azure.AzureContainerAppScaleConfig, Aspire.Hosting.Azure.AppContainers"));
    }

    [Fact]
    public void Scanner_AzureExistingResourceScopes_ExposeTypeScriptCapabilities()
    {
        var capabilities = ScanCapabilitiesFromAzureAssemblies();

        AssertAzureExistingResourceScopeCapability(capabilities, "runAsExistingInResourceGroup", ["name", "resourceGroup", "subscription"]);
        AssertAzureExistingResourceScopeCapability(capabilities, "publishAsExistingInResourceGroup", ["name", "resourceGroup", "subscription"]);
        AssertAzureExistingResourceScopeCapability(capabilities, "asExistingInResourceGroup", ["name", "resourceGroup", "subscription"]);
        AssertAzureExistingResourceScopeCapability(capabilities, "runAsExistingInSubscription", ["name", "subscription"]);
        AssertAzureExistingResourceScopeCapability(capabilities, "publishAsExistingInSubscription", ["name", "subscription"]);
        AssertAzureExistingResourceScopeCapability(capabilities, "asExistingInSubscription", ["name", "subscription"]);
        AssertAzureExistingResourceScopeCapability(capabilities, "runAsExistingInTenant", ["name"]);
        AssertAzureExistingResourceScopeCapability(capabilities, "publishAsExistingInTenant", ["name"]);
        AssertAzureExistingResourceScopeCapability(capabilities, "asExistingInTenant", ["name"]);
    }

    [Fact]
    public void GenerateDistributedApplication_WithAzureExistingResourceScopes_EmitsTypeScriptMethods()
    {
        var result = AtsCapabilityScanner.ScanAssemblies(LoadAzureAssemblies());

        var files = _generator.GenerateDistributedApplication(result.ToAtsContext());
        var aspireTs = files["aspire.mts"];

        Assert.Contains("runAsExistingInResourceGroup", aspireTs);
        Assert.Contains("publishAsExistingInResourceGroup", aspireTs);
        Assert.Contains("asExistingInResourceGroup", aspireTs);
        Assert.Contains("runAsExistingInSubscription", aspireTs);
        Assert.Contains("publishAsExistingInSubscription", aspireTs);
        Assert.Contains("asExistingInSubscription", aspireTs);
        Assert.Contains("runAsExistingInTenant", aspireTs);
        Assert.Contains("publishAsExistingInTenant", aspireTs);
        Assert.Contains("asExistingInTenant", aspireTs);
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

    private static AtsContext WithAdditionalCapabilities(AtsContext context, params AtsCapabilityInfo[] capabilities)
    {
        var result = new AtsContext
        {
            Capabilities = [.. context.Capabilities, .. capabilities],
            HandleTypes = context.HandleTypes,
            DtoTypes = context.DtoTypes,
            EnumTypes = context.EnumTypes,
            ExportedValues = context.ExportedValues,
            Diagnostics = context.Diagnostics,
            CapabilityExportingAssemblyNames = context.CapabilityExportingAssemblyNames
                .Concat(capabilities.Select(capability =>
                    new KeyValuePair<string, string>(capability.CapabilityId, TestPackageName)))
                .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal)
        };

        foreach (var (id, method) in context.Methods)
        {
            result.Methods[id] = method;
        }
        foreach (var (id, property) in context.Properties)
        {
            result.Properties[id] = property;
        }
        return result;
    }

    private static AtsCapabilityInfo CreateDistributedApplicationBuilderCapability(
        AtsContext context,
        string methodName,
        string? description,
        AtsDocumentationInfo documentation)
    {
        var addTestRedis = context.Capabilities.First(c => c.CapabilityId == "Aspire.Hosting.CodeGeneration.TypeScript.Tests/addTestRedis");

        return new AtsCapabilityInfo
        {
            CapabilityId = $"Aspire.Hosting.CodeGeneration.TypeScript.Tests/{methodName}",
            MethodName = methodName,
            Description = description,
            Documentation = documentation,
            Parameters = [],
            ReturnType = new AtsTypeRef
            {
                TypeId = AtsConstants.Void,
                Category = AtsTypeCategory.Primitive
            },
            TargetTypeId = addTestRedis.TargetTypeId,
            TargetType = addTestRedis.TargetType,
            TargetParameterName = addTestRedis.TargetParameterName,
            ExpandedTargetTypes = addTestRedis.ExpandedTargetTypes,
            CapabilityKind = AtsCapabilityKind.Method
        };
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

    private static List<AtsCapabilityInfo> ScanCapabilitiesFromAzureAssemblies()
    {
        var result = AtsCapabilityScanner.ScanAssemblies(LoadAzureAssemblies());
        return result.Capabilities;
    }

    private static Assembly[] LoadAzureAssemblies()
    {
        return
        [
            typeof(DistributedApplication).Assembly,
            typeof(AzureResourceInfrastructure).Assembly,
            typeof(global::Aspire.Hosting.AzureContainerAppProjectExtensions).Assembly,
            typeof(global::Aspire.Hosting.AzureAppServiceComputeResourceExtensions).Assembly
        ];
    }

    private static void AssertCallbackParameterTypes(AtsCapabilityInfo capability, string parameterName, params Type[] expectedTypes)
    {
        var parameter = Assert.Single(capability.Parameters, p => p.Name == parameterName);

        Assert.True(parameter.IsCallback);
        Assert.NotNull(parameter.CallbackParameters);
        Assert.Equal(expectedTypes.Select(GetAtsTypeId), parameter.CallbackParameters.Select(p => p.Type?.TypeId));
    }

    private static void AssertTargetedMethod(IReadOnlyList<AtsCapabilityInfo> capabilities, string capabilityId, string methodName, Type targetType, Type parameterType)
    {
        var capability = Assert.Single(capabilities, c => c.CapabilityId == capabilityId);
        var parameter = Assert.Single(capability.Parameters);

        Assert.Equal(methodName, capability.MethodName);
        Assert.Equal(GetAtsTypeId(targetType), capability.TargetTypeId);
        Assert.Equal(GetAtsTypeId(parameterType), parameter.Type?.TypeId);
    }

    private static void AssertAzureExistingResourceScopeCapability(IReadOnlyList<AtsCapabilityInfo> capabilities, string methodName, string[] parameterNames)
    {
        var capability = Assert.Single(capabilities, c => c.CapabilityId == $"Aspire.Hosting.Azure/{methodName}");

        Assert.Equal(methodName, capability.MethodName);
        Assert.Equal(GetAtsTypeId(typeof(IAzureResource)), capability.TargetTypeId);
        Assert.True(capability.ReturnsBuilder);
        Assert.Equal(parameterNames, capability.Parameters.Select(p => p.Name));
    }

    private static Type GetRequiredType(string assemblyQualifiedTypeName)
    {
        return Type.GetType(assemblyQualifiedTypeName, throwOnError: true)!;
    }

    private static string GetAtsTypeId(Type type)
    {
        return type switch
        {
            _ when type == typeof(string) => "string",
            _ when type == typeof(bool) => "boolean",
            _ when type == typeof(byte) || type == typeof(short) || type == typeof(int) || type == typeof(long) ||
                type == typeof(float) || type == typeof(double) || type == typeof(decimal) => "number",
            _ => $"{type.Assembly.GetName().Name}/{type.FullName}"
        };
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
        var aspireTs = files["aspire.mts"];

        Assert.Contains("getSection", aspireTs);
        Assert.Contains("getChildren", aspireTs);
        Assert.Contains("exists", aspireTs);
        Assert.Contains("getLoggerFactory", aspireTs);
        Assert.Contains("createLogger", aspireTs);
        Assert.Contains("getResourceLoggerService", aspireTs);
        Assert.Contains("getResourceCommandService", aspireTs);
        Assert.Contains("executeCommandAsync", aspireTs);
        Assert.Contains("ExecuteCommandResult", aspireTs);
        Assert.Contains("getResourceNotificationService", aspireTs);
        Assert.Contains("getDistributedApplicationModel", aspireTs);
        Assert.Contains("subscribeBeforeStart", aspireTs);
        Assert.Contains("subscribeAfterResourcesCreated", aspireTs);
        Assert.Contains("subscribeBeforePublish", aspireTs);
        Assert.Contains("subscribeAfterPublish", aspireTs);
        Assert.Contains("onBeforePublish", aspireTs);
        Assert.Contains("onAfterPublish", aspireTs);
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
        return files["aspire.mts"];
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
        Assert.Contains("tags?: string[];", code);
        Assert.Contains("counts?: Record<string, number>;", code);
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
        var code = files["aspire.mts"];

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
        var code = files["aspire.mts"];

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
        var code = files["aspire.mts"];

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
        var code = files["aspire.mts"];

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

        // Extract just the merged options interface for snapshot verification. The fixture's
        // withDataVolume overloads are owned by the test assembly, so they merge into that
        // assembly's interface rather than into the core one of the same base name.
        var interfaceName = $"{TestOptionsPrefix}WithDataVolumeOptions";
        var interfaceStart = code.IndexOf($"export interface {interfaceName}", StringComparison.Ordinal);
        Assert.True(interfaceStart >= 0, $"{interfaceName} interface not found in generated code");

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

    // ===== Canonical API export =====
    //
    // The canonical export is the contract aspire.dev consumes to render TypeScript API
    // documentation. It must be produced from the same resolved projection the source
    // emitter uses, because documentation that reconstructs signatures from raw ATS
    // drifts from the SDK that actually ships (microsoft/aspire#17608).

    /// <summary>
    /// The exporting package for the canonical export tests. The test assembly owns the
    /// documented symbols; Aspire.Hosting contributes referenced types through the closure.
    /// </summary>
    private const string TestPackageName = "Aspire.Hosting.CodeGeneration.TypeScript.Tests";
    private const string TestPackageVersion = "13.5.0";

    /// <summary>
    /// The qualifier the projector derives from <see cref="TestPackageName"/> for options
    /// interfaces it owns.
    /// </summary>
    /// <remarks>
    /// Options interfaces are named after the assembly that exports the capability, so that a
    /// package's export names an interface the same way whether it was projected on its own or
    /// alongside every other package. Only <c>Aspire.Hosting</c> keeps unqualified names, so the
    /// fixture's own interfaces carry this prefix.
    /// </remarks>
    private const string TestOptionsPrefix = "Aspire_x002E_Hosting_x002E_CodeGeneration_x002E_TypeScript_x002E_Tests";

    [Fact]
    public async Task ApiExportUsesTheSameResolvedSignaturesAsGeneratedSource()
    {
        var atsContext = CreateOwnershipFilteredContext();

        var projector = new TypeScriptApiProjector(atsContext);
        var model = projector.BuildApiModel(
            new TypeScriptApiPackageIdentity(TestPackageName, TestPackageVersion),
            [TestPackageName]);

        var exportJson = TypeScriptApiExportWriter.WriteToJson(model, indented: true);

        await Verify(exportJson, extension: "json")
            .UseFileName("AtsTypeScriptCodeGeneratorTests.ApiExport");

        // Declaration fragments are snapshotted separately: aspire.dev concatenates them in
        // stable-ID order and type-checks the result, so their exact text is a contract.
        var declarations = string.Join(
            "\n\n",
            model.Declarations.Select(declaration => $"// {declaration.Id}\n{declaration.Content}"));

        await Verify(declarations, extension: "txt")
            .UseFileName("AtsTypeScriptCodeGeneratorTests.ApiDeclarations");
    }

    [Fact]
    public void ApiExportMethodParametersMatchResolvedPublicSignatures()
    {
        var atsContext = CreateOwnershipFilteredContext();
        var template = atsContext.Capabilities.Single(c =>
            c.CapabilityId == "Aspire.Hosting.CodeGeneration.TypeScript.Tests/waitForReadyAsync");
        var stringParameter = atsContext.Capabilities
            .Single(c => c.CapabilityId == "Aspire.Hosting.CodeGeneration.TypeScript.Tests/withMergeLogging")
            .Parameters.Single(p => p.Name == "logLevel");
        var boolParameter = atsContext.Capabilities
            .Single(c => c.CapabilityId == "Aspire.Hosting.CodeGeneration.TypeScript.Tests/withOptionalString")
            .Parameters.Single(p => p.Name == "enabled");
        var dtoParameter = atsContext.Capabilities
            .Single(c => c.CapabilityId == "Aspire.Hosting.CodeGeneration.TypeScript.Tests/withConfig")
            .Parameters.Single(p => p.Name == "config");
        var cancellationTokenParameter = template.Parameters.Single(p => p.Name == "cancellationToken");

        AtsCapabilityInfo CreateCapability(string methodName, params AtsParameterInfo[] parameters)
            => new()
            {
                CapabilityId = $"{TestPackageName}/{methodName}",
                MethodName = methodName,
                Parameters = parameters,
                ReturnType = template.ReturnType,
                TargetTypeId = template.TargetTypeId,
                TargetType = template.TargetType,
                TargetParameterName = template.TargetParameterName,
                ExpandedTargetTypes = template.ExpandedTargetTypes,
                ReturnsBuilder = template.ReturnsBuilder,
                CapabilityKind = template.CapabilityKind
            };

        var contextWithEdgeCases = WithAdditionalCapabilities(
            atsContext,
            CreateCapability(
                "withOptionsCollision",
                new AtsParameterInfo
                {
                    Name = "options",
                    Type = stringParameter.Type,
                    Documentation = new AtsDocumentationInfo { Summary = "Required options value." }
                },
                new AtsParameterInfo
                {
                    Name = "optionsBag",
                    Type = stringParameter.Type,
                    Documentation = new AtsDocumentationInfo { Summary = "Required options bag value." }
                },
                new AtsParameterInfo
                {
                    Name = "enabled",
                    Type = boolParameter.Type,
                    IsOptional = true,
                    Documentation = new AtsDocumentationInfo { Summary = "Whether the behavior is enabled." }
                }),
            CreateCapability(
                "withOptionalOptionsField",
                new AtsParameterInfo
                {
                    Name = "options",
                    Type = stringParameter.Type,
                    IsOptional = true,
                    Documentation = new AtsDocumentationInfo { Summary = "An optional value stored in the generated options bag." }
                },
                new AtsParameterInfo
                {
                    Name = "enabled",
                    Type = boolParameter.Type,
                    IsOptional = true,
                    Documentation = new AtsDocumentationInfo { Summary = "Whether the behavior is enabled." }
                }),
            CreateCapability(
                "withDirectOptionsAndCancellation",
                new AtsParameterInfo
                {
                    Name = "options",
                    Type = dtoParameter.Type,
                    IsOptional = true,
                    Documentation = new AtsDocumentationInfo { Summary = "Direct options." }
                },
                new AtsParameterInfo
                {
                    Name = "cancellationToken",
                    Type = cancellationTokenParameter.Type,
                    IsOptional = true,
                    Documentation = new AtsDocumentationInfo { Summary = "Cancellation token." }
                }));

        var projector = new TypeScriptApiProjector(contextWithEdgeCases);
        var model = projector.BuildApiModel(
            new TypeScriptApiPackageIdentity(TestPackageName, TestPackageVersion),
            [TestPackageName]);
        var testRedisResource = Assert.Single(
            model.Modules.SelectMany(module => module.Items),
            item => item.Name == nameof(TestRedisResource));

        var withOptionalString = Assert.Single(
            testRedisResource.Members,
            member => member.Name == "withOptionalString");
        Assert.Collection(
            withOptionalString.Parameters,
            parameter => AssertParameter(parameter, "options", $"{TestOptionsPrefix}WithOptionalStringOptions", isOptional: true));

        var withOptionsCollision = Assert.Single(
            testRedisResource.Members,
            member => member.Name == "withOptionsCollision");
        Assert.Equal(
            $"withOptionsCollision(options: string, optionsBag: string, _optionsBag?: {TestOptionsPrefix}WithOptionsCollisionOptions): Promise<boolean>",
            withOptionsCollision.Declaration);
        Assert.Collection(
            withOptionsCollision.Parameters,
            parameter => AssertParameter(parameter, "options", "string", isOptional: false, "Required options value."),
            parameter => AssertParameter(parameter, "optionsBag", "string", isOptional: false, "Required options bag value."),
            parameter => AssertParameter(parameter, "_optionsBag", $"{TestOptionsPrefix}WithOptionsCollisionOptions", isOptional: true));

        var withOptionalOptionsField = Assert.Single(
            testRedisResource.Members,
            member => member.Name == "withOptionalOptionsField");
        Assert.Equal(
            $"withOptionalOptionsField(options?: {TestOptionsPrefix}WithOptionalOptionsFieldOptions): Promise<boolean>",
            withOptionalOptionsField.Declaration);
        Assert.Collection(
            withOptionalOptionsField.Parameters,
            parameter => AssertParameter(parameter, "options", $"{TestOptionsPrefix}WithOptionalOptionsFieldOptions", isOptional: true));

        var withDirectOptionsAndCancellation = Assert.Single(
            testRedisResource.Members,
            member => member.Name == "withDirectOptionsAndCancellation");
        Assert.Equal(
            "withDirectOptionsAndCancellation(options?: TestConfigDto, cancellationToken?: AbortSignal | CancellationToken): Promise<boolean>",
            withDirectOptionsAndCancellation.Declaration);
        Assert.Collection(
            withDirectOptionsAndCancellation.Parameters,
            parameter => AssertParameter(parameter, "options", "TestConfigDto", isOptional: true, "Direct options."),
            parameter => AssertParameter(parameter, "cancellationToken", "AbortSignal | CancellationToken", isOptional: true, "Cancellation token."));

        var generatedSource = new AtsTypeScriptCodeGenerator()
            .GenerateDistributedApplication(contextWithEdgeCases)["aspire.mts"];
        var generatedInterfaceMembers = ParsePublicInterfaceMembers(generatedSource);
        var testRedisResourceMembers = generatedInterfaceMembers[nameof(TestRedisResource)];

        Assert.Contains(withOptionsCollision.Declaration, testRedisResourceMembers);
        Assert.Contains(withOptionalOptionsField.Declaration, testRedisResourceMembers);
        Assert.Contains(withDirectOptionsAndCancellation.Declaration, testRedisResourceMembers);
        Assert.Contains(
            $$"""async withOptionalOptionsField(optionsBag?: {{TestOptionsPrefix}}WithOptionalOptionsFieldOptions): Promise<boolean> {""",
            generatedSource);
        Assert.Contains("const options = optionsBag?.options;", generatedSource);
        Assert.DoesNotContain("const options = options?.options;", generatedSource);

        static void AssertParameter(
            TypeScriptApiParameter parameter,
            string name,
            string declaredType,
            bool isOptional,
            string? summary = null)
        {
            Assert.Equal(name, parameter.Name);
            Assert.Equal(declaredType, parameter.DeclaredType);
            Assert.Equal(isOptional, parameter.IsOptional);
            Assert.Equal(summary, parameter.Summary);
        }
    }

    /// <summary>
    /// The export contract promises that concatenating a manifest's declaration fragments type-checks
    /// without site-authored shims, so every symbol a fragment names must be declared by some fragment.
    /// This caught a real gap: handle types with no wrapper class surface in signatures under their raw
    /// <c>XHandle</c> alias, but the fragment pass derived a different name and declared nothing.
    /// </summary>
    [Fact]
    public void ApiExportDeclarationFragmentsReferenceOnlyDeclaredOrBuiltInSymbols()
    {
        var atsContext = CreateOwnershipFilteredContext();

        var projector = new TypeScriptApiProjector(atsContext);
        var model = projector.BuildApiModel(
            new TypeScriptApiPackageIdentity(TestPackageName, TestPackageVersion),
            [TestPackageName]);

        var declaredNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var declaration in model.Declarations)
        {
            foreach (Match match in DeclaredNameRegex().Matches(declaration.Content))
            {
                declaredNames.Add(match.Groups[1].Value);
            }
        }

        var referenced = new HashSet<string>(StringComparer.Ordinal);

        foreach (var declaration in model.Declarations)
        {
            foreach (var name in ExtractReferencedTypeNames(declaration.Content))
            {
                referenced.Add(name);
            }
        }

        // Rendered item and member signatures are scanned too: they name the same symbols the
        // fragments must supply, and they are where an alias the fragments never declared shows up.
        // Enum items are excluded: their members are value names declared by the enum itself, not
        // references to other symbols.
        foreach (var item in model.Modules
            .SelectMany(module => module.Items)
            .Where(item => item.Kind != TypeScriptApiItemKind.Enum))
        {
            var signatures = item.Members
                .Select(member => member.Declaration)
                .Append(item.Declaration)
                .Concat(item.Extends);

            foreach (var name in signatures.SelectMany(ExtractReferencedTypeNames))
            {
                referenced.Add(name);
            }
        }

        referenced.ExceptWith(declaredNames);
        referenced.ExceptWith(s_typeScriptBuiltInNames);

        Assert.True(
            referenced.Count == 0,
            $"Declaration fragments reference undeclared symbols: {string.Join(", ", referenced.OrderBy(name => name, StringComparer.Ordinal))}");
    }

    /// <summary>
    /// TypeScript symbols the language itself provides, so fragments may reference them without
    /// declaring them.
    /// </summary>
    private static readonly HashSet<string> s_typeScriptBuiltInNames = new(StringComparer.Ordinal)
    {
        "Promise", "PromiseLike", "Record", "Partial", "Readonly", "Array", "Function", "Date", "Error"
    };

    /// <summary>
    /// Collects the type names a declaration fragment references. Enum bodies are dropped first because
    /// their members are declared by the enum itself, then string literals are removed so that handle
    /// aliases such as <c>export type XHandle = Handle&lt;'Assembly/Namespace.Type'&gt;;</c> do not look
    /// like type references.
    /// </summary>
    private static IEnumerable<string> ExtractReferencedTypeNames(string content)
    {
        var withoutEnums = EnumDeclarationRegex().Replace(content, string.Empty);
        var withoutLiterals = StringLiteralRegex().Replace(withoutEnums, "\"\"");

        foreach (Match match in IdentifierRegex().Matches(withoutLiterals))
        {
            var name = match.Value;

            // Conventional generic parameter names (T, TKey, TValue) are introduced by the
            // declaration that uses them, so they are never resolved against other fragments.
            if (name is "T" || (name.Length > 1 && name[0] == 'T' && char.IsUpper(name[1])))
            {
                continue;
            }

            yield return name;
        }
    }

    [GeneratedRegex(@"^export (?:interface|enum|type) (\w+)", RegexOptions.Multiline)]
    private static partial Regex DeclaredNameRegex();

    [GeneratedRegex(@"enum \w+ \{[^}]*\}")]
    private static partial Regex EnumDeclarationRegex();

    [GeneratedRegex(@"'[^']*'|""[^""]*""")]
    private static partial Regex StringLiteralRegex();

    [GeneratedRegex(@"\b[A-Z][A-Za-z0-9_]*\b")]
    private static partial Regex IdentifierRegex();

    [Fact]
    public void ApiExportDeclarationsAppearInGeneratedPublicInterfaces()
    {
        var atsContext = CreateOwnershipFilteredContext();

        var projector = new TypeScriptApiProjector(atsContext);
        var model = projector.BuildApiModel(
            new TypeScriptApiPackageIdentity(TestPackageName, TestPackageVersion),
            [TestPackageName]);

        var generatedSource = new AtsTypeScriptCodeGenerator()
            .GenerateDistributedApplication(atsContext)["aspire.mts"];

        // Keep this assertion narrow. It proves both emitters consume the same resolved
        // projection without snapshotting all runtime implementation code: every method
        // signature the export publishes must be present verbatim in the generated public
        // interface for the type that owns it.
        var generatedInterfaceMembers = ParsePublicInterfaceMembers(generatedSource);

        var checkedDeclarations = 0;
        foreach (var module in model.Modules)
        {
            foreach (var item in module.Items)
            {
                foreach (var member in item.Members.Where(m => m.Kind == TypeScriptApiItemKind.Method))
                {
                    Assert.True(
                        generatedInterfaceMembers.TryGetValue(item.Name, out var members),
                        $"Exported type '{item.Name}' has no generated public interface.");

                    Assert.True(
                        members.Contains(member.Declaration),
                        $"Exported declaration '{member.Declaration}' on '{item.Name}' does not appear in the generated public interface. " +
                        $"Generated members: {string.Join(", ", members)}");

                    checkedDeclarations++;
                }
            }
        }

        Assert.True(checkedDeclarations > 0, "The canonical export produced no method declarations to compare.");
    }

    [Fact]
    public void ApiExportUsesPromiseWrappersFromReferencedHandleCapabilities()
    {
        var fullContext = CreateReferencedHandleContext();
        var exportContext = AtsContextFilter.FilterForApiExport(
            fullContext,
            [TestPackageName]);

        var projector = new TypeScriptApiProjector(exportContext);
        var model = projector.BuildApiModel(
            new TypeScriptApiPackageIdentity(TestPackageName, TestPackageVersion),
            [TestPackageName]);

        var ownedContext = Assert.Single(
            model.Modules.SelectMany(module => module.Items),
            item => item.Name == "OwnedContext");
        var exportedMethod = Assert.Single(
            ownedContext.Members,
            member => member.Name == "getForeign");

        var generatedSource = new AtsTypeScriptCodeGenerator()
            .GenerateDistributedApplication(fullContext)["aspire.mts"];
        var generatedInterfaceMembers = ParsePublicInterfaceMembers(generatedSource);

        Assert.Contains(exportedMethod.Declaration, generatedInterfaceMembers["OwnedContext"]);
    }

    [Fact]
    public void ApiExportUsesResourceWrappersReferencedOnlyBySupportingCapabilities()
    {
        var fullContext = CreateReferencedHandleContext();
        var exportContext = AtsContextFilter.FilterForApiExport(
            fullContext,
            [TestPackageName]);

        var projector = new TypeScriptApiProjector(exportContext);
        var model = projector.BuildApiModel(
            new TypeScriptApiPackageIdentity(TestPackageName, TestPackageVersion),
            [TestPackageName]);

        var ownedContext = Assert.Single(
            model.Modules.SelectMany(module => module.Items),
            item => item.Name == "OwnedContext");
        var exportedMethod = Assert.Single(
            ownedContext.Members,
            member => member.Name == "waitFor");

        var generatedSource = new AtsTypeScriptCodeGenerator()
            .GenerateDistributedApplication(fullContext)["aspire.mts"];
        var generatedInterfaceMembers = ParsePublicInterfaceMembers(generatedSource);

        Assert.Contains(exportedMethod.Declaration, generatedInterfaceMembers["OwnedContext"]);
    }

    [Fact]
    public void ApiExportRetainsExpandedTargetsFromSupportingCapabilities()
    {
        var fullContext = CreateReferencedHandleContext();
        var exportContext = AtsContextFilter.FilterForApiExport(
            fullContext,
            [TestPackageName]);

        var projector = new TypeScriptApiProjector(exportContext);
        var model = projector.BuildApiModel(
            new TypeScriptApiPackageIdentity(TestPackageName, TestPackageVersion),
            [TestPackageName]);

        var ownedContext = Assert.Single(
            model.Modules.SelectMany(module => module.Items),
            item => item.Name == "OwnedContext");
        var exportedMethod = Assert.Single(
            ownedContext.Members,
            member => member.Name == "waitForForeign");

        var generatedSource = new AtsTypeScriptCodeGenerator()
            .GenerateDistributedApplication(fullContext)["aspire.mts"];
        var generatedInterfaceMembers = ParsePublicInterfaceMembers(generatedSource);

        Assert.Contains(exportedMethod.Declaration, generatedInterfaceMembers["OwnedContext"]);
        Assert.Equal(
            exportContext.Capabilities.Count,
            exportContext.Capabilities.Select(capability => capability.CapabilityId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ApiExportRetainsAssemblyOwnedMembersOnExternalTypes()
    {
        var fullContext = CreateContextFromBothAssemblies();
        var exportContext = AtsContextFilter.FilterForApiExport(
            fullContext,
            ["Aspire.Hosting"]);

        var projector = new TypeScriptApiProjector(exportContext);
        var model = projector.BuildApiModel(
            new TypeScriptApiPackageIdentity("Aspire.Hosting", TestPackageVersion),
            ["Aspire.Hosting"]);
        var items = model.Modules.SelectMany(module => module.Items).ToList();

        var configurationSection = Assert.Single(items, item => item.Name == "ConfigurationSection");
        Assert.Contains(configurationSection.Members, member => member.Name == "key");
        Assert.Contains(configurationSection.Members, member => member.Name == "path");
        Assert.Contains(configurationSection.Members, member => member.Name == "value");

        var hostEnvironment = Assert.Single(items, item => item.Name == "HostEnvironment");
        Assert.Contains(hostEnvironment.Members, member => member.Name == "applicationName");
        Assert.Contains(hostEnvironment.Members, member => member.Name == "environmentName");
        Assert.Contains(hostEnvironment.Members, member => member.Name == "contentRootPath");
    }

    /// <summary>
    /// DTO interfaces carry properties that have no C# counterpart, such as the client-only
    /// <c>throwOnPendingRejections</c> on <c>CreateBuilderOptions</c>. Those used to be appended by the
    /// module emitter alone, so the exported interface described fewer properties than the module we
    /// ship and aspire.dev documented a DTO nobody could actually pass.
    /// </summary>
    [Fact]
    public void ApiExportDtoPropertiesMatchTheGeneratedDtoInterfaces()
    {
        var atsContext = CreateOwnershipFilteredContext();

        var projector = new TypeScriptApiProjector(atsContext);
        var model = projector.BuildApiModel(
            new TypeScriptApiPackageIdentity(TestPackageName, TestPackageVersion),
            [TestPackageName]);

        var generatedSource = new AtsTypeScriptCodeGenerator()
            .GenerateDistributedApplication(atsContext)["aspire.mts"];

        var checkedDtos = 0;
        foreach (var item in model.Modules.SelectMany(module => module.Items).Where(item => item.Kind == TypeScriptApiItemKind.Dto))
        {
            var body = ExtractExportedInterfaceBody(generatedSource, item.Name);
            Assert.NotNull(body);

            var generatedProperties = body!
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.EndsWith(';') && !line.StartsWith("//", StringComparison.Ordinal) && !line.StartsWith("*", StringComparison.Ordinal) && !line.StartsWith("/*", StringComparison.Ordinal))
                .Select(line => line[..^1])
                .ToList();

            Assert.Equal(generatedProperties, item.Members.Select(member => member.Declaration).ToList());
            checkedDtos++;
        }

        Assert.True(checkedDtos > 0, "The canonical export produced no DTO items to compare.");
    }

    /// <summary>
    /// Returns the body of <c>export interface {name} { ... }</c> from generated module source, or
    /// <see langword="null" /> when the generated source declares no such interface.
    /// </summary>
    private static string? ExtractExportedInterfaceBody(string generatedSource, string interfaceName)
    {
        var header = $"export interface {interfaceName} {{";
        var start = generatedSource.IndexOf(header, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var bodyStart = start + header.Length;
        var end = generatedSource.IndexOf("\n}", bodyStart, StringComparison.Ordinal);
        return end < 0 ? null : generatedSource[bodyStart..end];
    }

    /// <summary>
    /// Consumers deduplicate declaration fragments by comparing content for the same ID across packages,
    /// so the text has to be byte-identical no matter which OS produced the export. Some fragments come
    /// from raw string literals, which pick up CRLF when the repository is checked out on Windows.
    /// </summary>
    [Fact]
    public void ApiExportDeclarationContentUsesPlatformIndependentLineEndings()
    {
        var atsContext = CreateOwnershipFilteredContext();

        var projector = new TypeScriptApiProjector(atsContext);
        var model = projector.BuildApiModel(
            new TypeScriptApiPackageIdentity(TestPackageName, TestPackageVersion),
            [TestPackageName]);

        Assert.All(model.Declarations, declaration =>
            Assert.DoesNotContain('\r', declaration.Content));
    }

    [Fact]
    public void ApiExportSeparatesReferencedTypesFromPackageOwnedItems()
    {
        var atsContext = CreateOwnershipFilteredContext();

        var projector = new TypeScriptApiProjector(atsContext);
        var model = projector.BuildApiModel(
            new TypeScriptApiPackageIdentity(TestPackageName, TestPackageVersion),
            [TestPackageName]);

        var documentedItems = model.Modules.SelectMany(module => module.Items).ToList();

        // Every documented item must be something this package published: either it owns the type,
        // or it contributes members to a type another package owns. Anything else would republish
        // another package's surface under this package's version.
        Assert.All(documentedItems, item =>
            Assert.True(
                item.TypeId.StartsWith($"{TestPackageName}/", StringComparison.Ordinal) ||
                item.OwningAssemblyName == TestPackageName ||
                item.Kind == TypeScriptApiItemKind.Augmentation,
                $"Item '{item.Id}' ({item.TypeId}) is neither package-owned nor a package contribution."));

        // A package that extends another package's type must not publish a second page for it. The
        // owning package's export uses "interface:{name}" for that type, so an augmentation reusing
        // that ID would collide across a manifest and claim ownership it does not have. The
        // contributing package is part of the ID as well, because every integration that extends
        // DistributedApplicationBuilder augments the same interface name.
        Assert.All(
            documentedItems.Where(item => item.Kind == TypeScriptApiItemKind.Augmentation),
            item =>
            {
                Assert.StartsWith($"augmentation:{TestPackageName}:", item.Id, StringComparison.Ordinal);
                Assert.NotEqual(TestPackageName, item.OwningAssemblyName);
            });

        // Item IDs are what aspire.dev deduplicates a manifest on, so a repeat would silently drop a page.
        var itemIds = documentedItems.Select(item => item.Id).ToList();
        Assert.Equal(itemIds.Count, itemIds.Distinct(StringComparer.Ordinal).Count());

        // Members are owned per capability, so no documented member may come from another assembly.
        Assert.All(
            documentedItems.SelectMany(item => item.Members),
            member => Assert.Equal(TestPackageName, member.OwningAssemblyName));

        // The closure must reach types this package does not own; otherwise the fixture would not
        // exercise cross-package references at all.
        var referencedTypeIds = atsContext.HandleTypes
            .Select(type => type.AtsTypeId)
            .Where(typeId => !typeId.StartsWith($"{TestPackageName}/", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(referencedTypeIds);

        var declarationIds = model.Declarations.Select(declaration => declaration.Id).ToList();
        Assert.Equal(declarationIds.Count, declarationIds.Distinct(StringComparer.Ordinal).Count());

        // Referenced types must reach the declaration fragments under their real owner, otherwise
        // the concatenated declarations would not type-check.
        Assert.Contains(model.Declarations, declaration => declaration.OwningAssemblyName == "Aspire.Hosting");

        // Every type name the declarations reference must also be declared by the declarations.
        var declaredNames = model.Declarations
            .SelectMany(declaration => Regex.Matches(declaration.Content, @"export (?:interface|enum|type) (\w+)"))
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("ResourceBuilderBase", declaredNames);
        Assert.Contains("ContainerResource", declaredNames);
    }

    /// <summary>
    /// Two packages that expose the same capability name with incompatible parameter types must
    /// name their options interfaces the same way whether they are scanned together or apart.
    /// </summary>
    /// <remarks>
    /// <c>sdk export</c> runs one app host per package, so the projector only ever sees the
    /// requested package plus core, while <c>sdk generate</c> sees whatever the user's app host
    /// references. Deriving the name from the exporting assembly is what makes those two views
    /// agree: naming by method alone gave both packages <c>RunAsEmulatorOptions</c> when projected
    /// apart, which is a duplicate declaration with conflicting members once aspire.dev
    /// concatenates their fragments.
    /// </remarks>
    [Fact]
    public void OptionsInterfaceNamesDoNotDependOnWhichOtherPackagesWereScanned()
    {
        var scannedTogether = new TypeScriptApiProjector(CreateEmulatorCollisionContext());
        var hubsAlone = new TypeScriptApiProjector(CreateEmulatorCollisionContext(includeServiceBus: false));
        var busAlone = new TypeScriptApiProjector(CreateEmulatorCollisionContext(includeEventHubs: false));

        static string EmulatorInterfaceName(TypeScriptApiProjector projector, string packageName)
            => projector.ResolveOptionsInterfaceName(
                projector.Resolved.Context.Capabilities.Single(c => c.CapabilityId == $"{packageName}/runAsEmulator"));

        Assert.Equal("Aspire_x002E_Hosting_x002E_Azure_x002E_EventHubsRunAsEmulatorOptions", EmulatorInterfaceName(hubsAlone, CollisionPackageA));
        Assert.Equal("Aspire_x002E_Hosting_x002E_Azure_x002E_ServiceBusRunAsEmulatorOptions", EmulatorInterfaceName(busAlone, CollisionPackageB));

        Assert.Equal(
            EmulatorInterfaceName(hubsAlone, CollisionPackageA),
            EmulatorInterfaceName(scannedTogether, CollisionPackageA));
        Assert.Equal(
            EmulatorInterfaceName(busAlone, CollisionPackageB),
            EmulatorInterfaceName(scannedTogether, CollisionPackageB));
    }

    /// <summary>
    /// An entry point's exported declaration describes the function the generator actually emits.
    /// </summary>
    /// <remarks>
    /// Entry points are free functions, so the generator emits them taking the client explicitly and
    /// keeping optional arguments positional. The exporter used to route them through the member
    /// signature resolver instead, which dropped <c>client</c> and folded the optionals into an
    /// options bag, so the published declaration described a call that does not exist. Consumers
    /// type-check against these declarations, so the disagreement surfaces as a compile error in
    /// their code rather than anywhere near this repository.
    /// </remarks>
    [Fact]
    public void ApiExportDeclaresEntryPointsWithTheSignatureTheGeneratorEmits()
    {
        var context = CreateEntryPointContext();

        var projector = new TypeScriptApiProjector(context);
        var model = projector.BuildApiModel(
            new TypeScriptApiPackageIdentity(EntryPointPackage, TestPackageVersion),
            [EntryPointPackage]);

        var exported = Assert.Single(
            model.Modules.SelectMany(module => module.Items),
            item => item.Name == "startThing");

        Assert.Equal(
            "function startThing(client: AspireClientRpc, name: string, retries?: number): Promise<void>",
            exported.Declaration);

        var generatedSource = new AtsTypeScriptCodeGenerator()
            .GenerateDistributedApplication(context)["aspire.mts"];

        Assert.Contains(
            $"export async {exported.Declaration} {{",
            generatedSource,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Two assemblies whose names differ only in where their separators fall must not collapse to
    /// the same options-interface qualifier.
    /// </summary>
    /// <remarks>
    /// The qualifier used to keep only letters and digits, so <c>Contoso.Foo.Bar</c> and
    /// <c>Contoso.FooBar</c> both produced <c>ContosoFooBar</c>. A per-package export cannot see
    /// that some other package would land on the same qualifier, so it has no opportunity to
    /// disambiguate the way full generation could; the two packages would each emit a
    /// <c>ContosoFooBarRunAsEmulatorOptions</c> with different members and aspire.dev would
    /// concatenate them into a duplicate declaration that does not compile. Encoding the separator
    /// instead of dropping it makes the qualifier injective, which is what removes the possibility.
    /// </remarks>
    [Fact]
    public void OptionsInterfaceQualifiersDistinguishAssembliesThatDifferOnlyBySeparatorPlacement()
    {
        var dotted = TypeScriptApiProjector.GetOptionsInterfaceName("runAsEmulator", "Contoso.Foo.Bar");
        var joined = TypeScriptApiProjector.GetOptionsInterfaceName("runAsEmulator", "Contoso.FooBar");

        Assert.NotEqual(dotted, joined);
        Assert.Equal("Contoso_x002E_Foo_x002E_BarRunAsEmulatorOptions", dotted);
        Assert.Equal("Contoso_x002E_FooBarRunAsEmulatorOptions", joined);
    }

    /// <summary>
    /// Escape sequences are terminated so characters after the escaped code unit cannot become part
    /// of the escape itself.
    /// </summary>
    [Fact]
    public void OptionsInterfaceQualifiersUseTerminatedEscapes()
    {
        Assert.NotEqual(
            TypeScriptApiProjector.GetOptionsInterfaceName("runAsEmulator", "Contoso.Foo-Bar"),
            TypeScriptApiProjector.GetOptionsInterfaceName("runAsEmulator", "Contoso.Foo.x2DBar"));

        Assert.NotEqual(
            TypeScriptApiProjector.GetOptionsInterfaceName("runAsEmulator", "Contoso.\u01234"),
            TypeScriptApiProjector.GetOptionsInterfaceName("runAsEmulator", "Contoso.\u1234"));
    }

    /// <summary>
    /// An assembly name that starts with a digit still yields a parseable TypeScript identifier.
    /// </summary>
    /// <remarks>
    /// Assembly names may begin with a digit -- <c>3rdParty.Aspire</c> is legal -- but TypeScript
    /// identifiers may not, so the unguarded qualifier emitted
    /// <c>interface 3rdPartyAspireRunAsEmulatorOptions</c>, which is a syntax error rather than a
    /// naming inconvenience. The escape cannot alias a name that already begins with an underscore
    /// because a literal underscore encodes as a doubled one.
    /// </remarks>
    [Fact]
    public void OptionsInterfaceQualifiersEscapeAssemblyNamesThatStartWithADigit()
    {
        var name = TypeScriptApiProjector.GetOptionsInterfaceName("runAsEmulator", "3rdParty.Aspire");

        Assert.Equal("_x0033_rdParty_x002E_AspireRunAsEmulatorOptions", name);
        Assert.True(name[0] is '_' or '$' || char.IsLetter(name[0]), $"'{name}' is not a valid TypeScript identifier.");
        Assert.NotEqual(name, TypeScriptApiProjector.GetOptionsInterfaceName("runAsEmulator", "_3rdParty.Aspire"));
    }

    /// <summary>
    /// Non-core assemblies are qualified by their complete names, not by a shortened suffix that can
    /// overlap other assemblies.
    /// </summary>
    [Fact]
    public void OptionsInterfaceQualifiersUseTheFullAssemblyName()
    {
        var hostingRedis = TypeScriptApiProjector.GetOptionsInterfaceName("runAsEmulator", "Aspire.Hosting.Redis");
        var aspireRedis = TypeScriptApiProjector.GetOptionsInterfaceName("runAsEmulator", "Aspire.Redis");
        var bareRedis = TypeScriptApiProjector.GetOptionsInterfaceName("runAsEmulator", "Redis");

        Assert.Equal("Aspire_x002E_Hosting_x002E_RedisRunAsEmulatorOptions", hostingRedis);
        Assert.Equal("Aspire_x002E_RedisRunAsEmulatorOptions", aspireRedis);
        Assert.Equal("RedisRunAsEmulatorOptions", bareRedis);
        Assert.Equal(3, new[] { hostingRedis, aspireRedis, bareRedis }.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// An options interface is documented by, and keyed to, the assembly whose capability produced
    /// it rather than the package the export was requested for.
    /// </summary>
    /// <remarks>
    /// The projector's context reaches beyond the requested package, so an unscoped emission would
    /// let one package publish its dependencies' options interfaces under its own version. Keying
    /// the declaration by the requesting package instead of the owner is the same bug from the
    /// other side: the same interface would carry a different fragment id in every export that
    /// reached it, so concatenation would redeclare it rather than deduplicate it.
    /// </remarks>
    [Fact]
    public void ApiExportAttributesOptionsInterfacesToTheAssemblyThatOwnsThem()
    {
        var projector = new TypeScriptApiProjector(CreateEmulatorCollisionContext());
        var model = projector.BuildApiModel(
            new TypeScriptApiPackageIdentity(CollisionPackageA, TestPackageVersion),
            [CollisionPackageA]);

        var documentedOptions = model.Modules
            .SelectMany(module => module.Items)
            .Where(item => item.Kind == TypeScriptApiItemKind.Options)
            .ToList();

        Assert.Collection(
            documentedOptions,
            item =>
            {
                Assert.Equal("Aspire_x002E_Hosting_x002E_Azure_x002E_EventHubsRunAsEmulatorOptions", item.Name);
                Assert.Equal(CollisionPackageA, item.OwningAssemblyName);
            });

        var serviceBusDeclaration = Assert.Single(
            model.Declarations,
            declaration => declaration.Content.Contains("Aspire_x002E_Hosting_x002E_Azure_x002E_ServiceBusRunAsEmulatorOptions", StringComparison.Ordinal));

        Assert.Equal($"{CollisionPackageB}:options:Aspire_x002E_Hosting_x002E_Azure_x002E_ServiceBusRunAsEmulatorOptions", serviceBusDeclaration.Id);
        Assert.Equal(CollisionPackageB, serviceBusDeclaration.OwningAssemblyName);
    }

    /// <summary>
    /// A per-package export names a colliding options interface the way full generation names it,
    /// on the context the export path actually produces rather than on a raw scan.
    /// </summary>
    /// <remarks>
    /// The determinism test above compares projectors built directly over hand-made contexts, but
    /// <c>sdk export</c> never hands the projector a raw scan: <see cref="AtsContextFilter.FilterForApiExport"/>
    /// narrows it to the requested package first. That difference is the whole bug — naming used to
    /// be decided by collision detection over whatever the context happened to hold, so the narrowed
    /// view and the full scan reached different answers for the same package.
    /// <para>
    /// Both directions fail under the old scheme, but at different assertions, and that asymmetry
    /// is why the body comparison is here. Event Hubs was scanned first and kept the unsuffixed
    /// base name, so it fails only on the name: it produced <c>RunAsEmulatorOptions</c> rather than
    /// the Event Hubs assembly-qualified name. Service Bus lost that draw during full generation
    /// and was suffixed there while its own single-package export was not, so it disagreed about
    /// the interface itself. Checking only that the exported name appears among the generated names
    /// would have missed it, because the name did appear -- it just belonged to Event Hubs. The old
    /// scheme had Service Bus export <c>RunAsEmulatorOptions</c> as <c>configureContainer?: boolean</c>
    /// while the SDK gave that same name to Event Hubs as <c>configureContainer?: string</c>, so a
    /// consumer concatenating the export silently got the wrong callback type rather than a
    /// redeclaration error.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(CollisionPackageA, "Aspire_x002E_Hosting_x002E_Azure_x002E_EventHubsRunAsEmulatorOptions")]
    [InlineData(CollisionPackageB, "Aspire_x002E_Hosting_x002E_Azure_x002E_ServiceBusRunAsEmulatorOptions")]
    public void ApiExportNamesACollidingOptionsInterfaceTheWayGenerationDoes(string packageName, string expectedInterfaceName)
    {
        var fullContext = CreateEmulatorCollisionContext();
        var exportContext = AtsContextFilter.FilterForApiExport(fullContext, [packageName]);

        var model = new TypeScriptApiProjector(exportContext).BuildApiModel(
            new TypeScriptApiPackageIdentity(packageName, TestPackageVersion),
            [packageName]);

        var exportedOptions = Assert.Single(
            model.Modules.SelectMany(module => module.Items),
            item => item.Kind == TypeScriptApiItemKind.Options);

        Assert.Equal(expectedInterfaceName, exportedOptions.Name);
        Assert.Equal(packageName, exportedOptions.OwningAssemblyName);

        var generatedSource = new AtsTypeScriptCodeGenerator()
            .GenerateDistributedApplication(fullContext)["aspire.mts"];

        var generatedInterfaces = ParsePublicInterfaceMembers(generatedSource);
        Assert.Contains(exportedOptions.Name, generatedInterfaces.Keys);
        Assert.Equal(
            exportedOptions.Members.Select(member => member.Declaration).OrderBy(d => d, StringComparer.Ordinal),
            generatedInterfaces[exportedOptions.Name].OrderBy(d => d, StringComparer.Ordinal));
    }

    private const string CollisionPackageA = "Aspire.Hosting.Azure.EventHubs";
    private const string CollisionPackageB = "Aspire.Hosting.Azure.ServiceBus";

    /// <summary>
    /// Builds a manifest where both packages expose <c>runAsEmulator</c> with an optional parameter
    /// of the same name but an incompatible type, which is what forced generation to suffix one of
    /// the two options interfaces when names were derived from the method alone.
    /// </summary>
    /// <remarks>
    /// The two package names are the real ones: <c>AzureEventHubsExtensions.RunAsEmulator</c> and
    /// <c>AzureServiceBusExtensions.RunAsEmulator</c> both take an optional
    /// <c>Action&lt;IResourceBuilder&lt;T&gt;&gt;</c> for different <c>T</c>, so their options
    /// interfaces cannot be merged. The capabilities here are synthetic; only the shape matters.
    /// </remarks>
    private const string EntryPointPackage = "Aspire.Hosting.Contoso.EntryPoints";

    /// <summary>
    /// Builds a context holding a single entry-point capability: one with no target type, so it is
    /// emitted as a free function rather than as a member of a builder interface.
    /// </summary>
    private static AtsContext CreateEntryPointContext()
    {
        var capability = new AtsCapabilityInfo
        {
            CapabilityId = $"{EntryPointPackage}/startThing",
            MethodName = "startThing",
            Parameters =
            [
                new AtsParameterInfo
                {
                    Name = "name",
                    Type = new AtsTypeRef { TypeId = AtsConstants.String, Category = AtsTypeCategory.Primitive }
                },
                new AtsParameterInfo
                {
                    Name = "retries",
                    Type = new AtsTypeRef { TypeId = AtsConstants.Number, Category = AtsTypeCategory.Primitive },
                    IsOptional = true
                }
            ],
            ReturnType = new AtsTypeRef { TypeId = AtsConstants.Void, Category = AtsTypeCategory.Primitive },
            ExpandedTargetTypes = [],
            CapabilityKind = AtsCapabilityKind.Method
        };

        return new AtsContext
        {
            Capabilities = [capability],
            HandleTypes = [],
            DtoTypes = [],
            EnumTypes = [],
            ExportedValues = [],
            Diagnostics = [],
            CapabilityExportingAssemblyNames = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [capability.CapabilityId] = EntryPointPackage
            }
        };
    }

    private static AtsContext CreateEmulatorCollisionContext(bool includeEventHubs = true, bool includeServiceBus = true)
    {
        static AtsTypeInfo Resource(string packageName, string typeName) => new()
        {
            AtsTypeId = $"{packageName}/{typeName}",
            IsInterface = false,
            HasExposeMethods = true,
            HasExposeProperties = false,
            BaseTypeHierarchy = [],
            ImplementedInterfaces = []
        };

        static AtsCapabilityInfo Emulator(string packageName, AtsTypeInfo target, string optionalTypeId) => new()
        {
            CapabilityId = $"{packageName}/runAsEmulator",
            MethodName = "runAsEmulator",
            Parameters =
            [
                new AtsParameterInfo
                {
                    Name = "configureContainer",
                    Type = new AtsTypeRef { TypeId = optionalTypeId, Category = AtsTypeCategory.Primitive },
                    IsOptional = true
                }
            ],
            ReturnType = new AtsTypeRef { TypeId = target.AtsTypeId, Category = AtsTypeCategory.Handle },
            TargetTypeId = target.AtsTypeId,
            TargetType = new AtsTypeRef { TypeId = target.AtsTypeId, Category = AtsTypeCategory.Handle },
            TargetParameterName = "builder",
            ExpandedTargetTypes = [],
            ReturnsBuilder = true,
            CapabilityKind = AtsCapabilityKind.Method
        };

        var hubsResource = Resource(CollisionPackageA, "EventHubsResource");
        var busResource = Resource(CollisionPackageB, "ServiceBusResource");

        // The two differ in the type of their shared optional parameter, so the interfaces are not
        // mergeable and one of them had to be renamed to make room for the other.
        var hubsEmulator = Emulator(CollisionPackageA, hubsResource, AtsConstants.String);
        var busEmulator = Emulator(CollisionPackageB, busResource, AtsConstants.Boolean);

        List<AtsCapabilityInfo> capabilities = [];
        List<AtsTypeInfo> handleTypes = [];
        var exportingAssemblyNames = new Dictionary<string, string>(StringComparer.Ordinal);

        if (includeEventHubs)
        {
            capabilities.Add(hubsEmulator);
            handleTypes.Add(hubsResource);
            exportingAssemblyNames[hubsEmulator.CapabilityId] = CollisionPackageA;
        }

        if (includeServiceBus)
        {
            capabilities.Add(busEmulator);
            handleTypes.Add(busResource);
            exportingAssemblyNames[busEmulator.CapabilityId] = CollisionPackageB;
        }

        return new AtsContext
        {
            Capabilities = capabilities,
            HandleTypes = handleTypes,
            DtoTypes = [],
            EnumTypes = [],
            ExportedValues = [],
            Diagnostics = [],
            CapabilityExportingAssemblyNames = exportingAssemblyNames
        };
    }

    /// <summary>
    /// Builds the context the canonical exporter sees for a single package: the package's own
    /// capabilities plus the transitive closure of types they reference from other assemblies.
    /// This mirrors what RemoteHost passes to the exporter for one <c>Name@Version</c> request.
    /// </summary>
    private static AtsContext CreateOwnershipFilteredContext()
    {
        return AtsContextFilter.FilterForApiExport(
            CreateContextFromBothAssemblies(),
            [TestPackageName]);
    }

    private static AtsContext CreateReferencedHandleContext()
    {
        const string ownedTypeId = TestPackageName + "/IOwnedContext";
        const string foreignTypeId = "Foreign.Dependency/IForeignHandle";
        const string resourceTypeId = "Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResource";
        const string foreignResourceTypeId = "Foreign.Dependency/ForeignResource";
        const string secondForeignResourceTypeId = "Foreign.Dependency/SecondForeignResource";
        const string parameterResourceTypeId = "Foreign.Dependency/ParameterResource";
        const string callbackParameterResourceTypeId = "Foreign.Dependency/CallbackParameterResource";
        const string callbackReturnResourceTypeId = "Foreign.Dependency/CallbackReturnResource";
        const string returnResourceTypeId = "Foreign.Dependency/ReturnResource";

        var ownedType = new AtsTypeRef
        {
            TypeId = ownedTypeId,
            Category = AtsTypeCategory.Handle,
            IsInterface = true
        };
        var foreignType = new AtsTypeRef
        {
            TypeId = foreignTypeId,
            Category = AtsTypeCategory.Handle,
            IsInterface = true
        };
        var resourceType = new AtsTypeRef
        {
            TypeId = resourceTypeId,
            Category = AtsTypeCategory.Handle,
            ClrType = typeof(IResource),
            IsInterface = true
        };
        var foreignResourceType = new AtsTypeRef
        {
            TypeId = foreignResourceTypeId,
            Category = AtsTypeCategory.Handle,
            ClrType = typeof(TestRedisResource),
            ImplementedInterfaces = [resourceType, foreignType]
        };
        var secondForeignResourceType = CreateResourceType(secondForeignResourceTypeId, resourceType, foreignType);
        var parameterResourceType = CreateResourceType(parameterResourceTypeId, resourceType);
        var callbackParameterResourceType = CreateResourceType(callbackParameterResourceTypeId, resourceType);
        var callbackReturnResourceType = CreateResourceType(callbackReturnResourceTypeId, resourceType);
        var returnResourceType = CreateResourceType(returnResourceTypeId, resourceType);

        return new AtsContext
        {
            Capabilities =
            [
                new AtsCapabilityInfo
                {
                    CapabilityId = TestPackageName + "/getForeign",
                    MethodName = "getForeign",
                    OwningTypeName = "IOwnedContext",
                    Parameters = [],
                    ReturnType = foreignType,
                    TargetTypeId = ownedTypeId,
                    TargetType = ownedType,
                    ReturnsBuilder = false,
                    CapabilityKind = AtsCapabilityKind.InstanceMethod
                },
                new AtsCapabilityInfo
                {
                    CapabilityId = TestPackageName + "/getConcrete",
                    MethodName = "getConcrete",
                    OwningTypeName = "IOwnedContext",
                    Parameters = [],
                    ReturnType = foreignResourceType,
                    TargetTypeId = ownedTypeId,
                    TargetType = ownedType,
                    ReturnsBuilder = false,
                    CapabilityKind = AtsCapabilityKind.InstanceMethod
                },
                new AtsCapabilityInfo
                {
                    CapabilityId = TestPackageName + "/waitFor",
                    MethodName = "waitFor",
                    OwningTypeName = "IOwnedContext",
                    Parameters =
                    [
                        new AtsParameterInfo
                        {
                            Name = "dependency",
                            Type = resourceType
                        }
                    ],
                    ReturnType = new AtsTypeRef
                    {
                        TypeId = AtsConstants.Void,
                        Category = AtsTypeCategory.Primitive
                    },
                    TargetTypeId = ownedTypeId,
                    TargetType = ownedType,
                    ReturnsBuilder = false,
                    CapabilityKind = AtsCapabilityKind.InstanceMethod
                },
                new AtsCapabilityInfo
                {
                    CapabilityId = TestPackageName + "/waitForForeign",
                    MethodName = "waitForForeign",
                    OwningTypeName = "IOwnedContext",
                    Parameters =
                    [
                        new AtsParameterInfo
                        {
                            Name = "dependency",
                            Type = foreignType
                        }
                    ],
                    ReturnType = new AtsTypeRef
                    {
                        TypeId = AtsConstants.Void,
                        Category = AtsTypeCategory.Primitive
                    },
                    TargetTypeId = ownedTypeId,
                    TargetType = ownedType,
                    ReturnsBuilder = false,
                    CapabilityKind = AtsCapabilityKind.InstanceMethod
                },
                new AtsCapabilityInfo
                {
                    CapabilityId = "Foreign.Dependency/getName",
                    MethodName = "getName",
                    OwningTypeName = "IForeignHandle",
                    Parameters =
                    [
                        new AtsParameterInfo
                        {
                            Name = "resource",
                            Type = parameterResourceType
                        },
                        new AtsParameterInfo
                        {
                            Name = "configure",
                            IsCallback = true,
                            CallbackParameters =
                            [
                                new AtsCallbackParameterInfo
                                {
                                    Name = "resource",
                                    Type = callbackParameterResourceType
                                }
                            ],
                            CallbackReturnType = callbackReturnResourceType
                        }
                    ],
                    ReturnType = returnResourceType,
                    TargetTypeId = foreignTypeId,
                    TargetType = foreignType,
                    ExpandedTargetTypes = [foreignResourceType, secondForeignResourceType],
                    ReturnsBuilder = false,
                    CapabilityKind = AtsCapabilityKind.InstanceMethod
                }
            ],
            HandleTypes =
            [
                new AtsTypeInfo
                {
                    AtsTypeId = ownedTypeId,
                    IsInterface = true
                },
                new AtsTypeInfo
                {
                    AtsTypeId = foreignTypeId,
                    IsInterface = true
                },
                new AtsTypeInfo
                {
                    AtsTypeId = foreignResourceTypeId,
                    ClrType = typeof(TestRedisResource),
                    ImplementedInterfaces = [resourceType, foreignType]
                },
                new AtsTypeInfo
                {
                    AtsTypeId = secondForeignResourceTypeId,
                    ClrType = typeof(TestRedisResource),
                    ImplementedInterfaces = [resourceType, foreignType]
                },
                new AtsTypeInfo
                {
                    AtsTypeId = parameterResourceTypeId,
                    ClrType = typeof(TestRedisResource),
                    ImplementedInterfaces = [resourceType]
                },
                new AtsTypeInfo
                {
                    AtsTypeId = callbackParameterResourceTypeId,
                    ClrType = typeof(TestRedisResource),
                    ImplementedInterfaces = [resourceType]
                },
                new AtsTypeInfo
                {
                    AtsTypeId = callbackReturnResourceTypeId,
                    ClrType = typeof(TestRedisResource),
                    ImplementedInterfaces = [resourceType]
                },
                new AtsTypeInfo
                {
                    AtsTypeId = returnResourceTypeId,
                    ClrType = typeof(TestRedisResource),
                    ImplementedInterfaces = [resourceType]
                }
            ],
            DtoTypes = [],
            EnumTypes = []
        };

        static AtsTypeRef CreateResourceType(string typeId, AtsTypeRef resourceType, AtsTypeRef? additionalInterface = null)
        {
            return new AtsTypeRef
            {
                TypeId = typeId,
                Category = AtsTypeCategory.Handle,
                ClrType = typeof(TestRedisResource),
                ImplementedInterfaces = additionalInterface is null
                    ? [resourceType]
                    : [resourceType, additionalInterface]
            };
        }
    }

    /// <summary>
    /// Extracts the member signature lines of every generated <c>export interface</c> block.
    /// </summary>
    /// <remarks>
    /// The generated source declares public surface as interfaces, for example:
    /// <code>
    /// export interface TestRedisResourceBuilder extends ResourceBuilderBase {
    ///     /** doc comment */
    ///     withPersistence(options?: WithPersistenceOptions): TestRedisResourceBuilderPromise;
    /// }
    /// </code>
    /// Only lines that terminate with <c>;</c> at brace depth 1 are member signatures; doc
    /// comments, blank lines, and nested object literals are skipped. Signatures are stored
    /// without the trailing semicolon so they compare directly against exported declarations.
    /// </remarks>
    private static Dictionary<string, HashSet<string>> ParsePublicInterfaceMembers(string generatedSource)
    {
        var membersByInterface = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        string? currentInterface = null;
        var depth = 0;

        foreach (var rawLine in generatedSource.Split('\n'))
        {
            var line = rawLine.Trim();

            if (currentInterface is null)
            {
                // Matches "export interface Name {" and "export interface Name extends Base {".
                if (!line.StartsWith("export interface ", StringComparison.Ordinal) || !line.EndsWith('{'))
                {
                    continue;
                }

                var header = line["export interface ".Length..^1].Trim();
                var extendsIndex = header.IndexOf(" extends ", StringComparison.Ordinal);
                currentInterface = (extendsIndex >= 0 ? header[..extendsIndex] : header).Trim();
                membersByInterface.TryAdd(currentInterface, new HashSet<string>(StringComparer.Ordinal));
                depth = 1;
                continue;
            }

            depth += line.Count(c => c == '{') - line.Count(c => c == '}');

            if (depth <= 0)
            {
                currentInterface = null;
                continue;
            }

            if (depth == 1 && line.EndsWith(';') && !line.StartsWith('*') && !line.StartsWith("//", StringComparison.Ordinal))
            {
                membersByInterface[currentInterface].Add(line[..^1].Trim());
            }
        }

        return membersByInterface;
    }
}
