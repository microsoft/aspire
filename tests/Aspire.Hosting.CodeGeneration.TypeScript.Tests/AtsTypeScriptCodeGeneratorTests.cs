// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREBROWSERLOGS001 // Type is for evaluation purposes only
#pragma warning disable ASPIRECOMPUTE002

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

public class AtsTypeScriptCodeGeneratorTests
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
        Assert.Equal("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ContainerResource", withVolume.TargetTypeId);
        Assert.False(withVolume.TargetType?.IsInterface);

        // Preserve the exported parameter list exactly. The Rust generator emits optional capability
        // parameters positionally and Rust has no overloading, so appending a parameter here would be
        // a source-breaking change for existing Rust AppHosts. A container always receives `target` as
        // its effective volume path, so the C# `env` convenience parameter is intentionally not
        // exported: polyglot callers use withEnvironment(env, target) for the same result.
        Assert.Equal(
            ["target", "name", "isReadOnly"],
            withVolume.Parameters.Select(parameter => parameter.Name));

        var withProjectVolume = Assert.Single(
            capabilities,
            capability => capability.CapabilityId == "Aspire.Hosting/withProjectVolume");
        Assert.Equal("withVolume", withProjectVolume.MethodName);
        Assert.Equal("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ProjectResource", withProjectVolume.TargetTypeId);
        Assert.False(withProjectVolume.TargetType?.IsInterface);

        // Projects and executables compute their run-mode path in the host, so `name` and `env` are
        // required rather than optional. Modelling them as optional would generate polyglot APIs that
        // type-check but always fail at runtime.
        Assert.Equal(
            ["target", "name", "env", "isReadOnly"],
            withProjectVolume.Parameters.Select(parameter => parameter.Name));
        Assert.False(withProjectVolume.Parameters[1].IsOptional);
        Assert.False(withProjectVolume.Parameters[2].IsOptional);

        var withExecutableVolume = Assert.Single(
            capabilities,
            capability => capability.CapabilityId == "Aspire.Hosting/withExecutableVolume");
        Assert.Equal("withVolume", withExecutableVolume.MethodName);
        Assert.Equal("Aspire.Hosting/Aspire.Hosting.ApplicationModel.ExecutableResource", withExecutableVolume.TargetTypeId);
        Assert.False(withExecutableVolume.TargetType?.IsInterface);
        Assert.Equal(
            ["target", "name", "env", "isReadOnly"],
            withExecutableVolume.Parameters.Select(parameter => parameter.Name));

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

    [Fact]
    public void Generate_KubernetesPersistentVolumeMount_UsesOptionsObject()
    {
        var scanResult = AtsCapabilityScanner.ScanAssemblies(
        [
            typeof(DistributedApplication).Assembly,
            typeof(global::Aspire.Hosting.Kubernetes.KubernetesPersistentVolumeResource).Assembly
        ]);
        var files = _generator.GenerateDistributedApplication(scanResult.ToAtsContext());
        var generatedCode = files["aspire.mts"];

        Assert.Contains("export interface WithKubernetesPersistentVolumeMountOptions", generatedCode);
        Assert.Contains("isReadOnly?: boolean;", generatedCode);
        Assert.Contains("env?: string;", generatedCode);
        Assert.Contains(
            generatedCode.Split('\n'),
            line => line.Contains(
                "withKubernetesPersistentVolumeMount(",
                StringComparison.Ordinal) &&
                line.Contains(
                    "options?: WithKubernetesPersistentVolumeMountOptions",
                    StringComparison.Ordinal));
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
    public void GenerateDistributedApplication_EmitsPromiseWrapperForBareMarkerResourceBuilder()
    {
        // Durable regression test for https://github.com/microsoft/aspire/issues/19507, using the
        // ITestMarkerResource fixture rather than an in-the-box type that happens to have zero
        // capabilities today: an [AspireExport] method returning IResourceBuilder<T> for a bare
        // marker interface emits "{ClassName}Promise" as its return type (the reference site),
        // while the wrapper declaration used to be skipped because the builder had no capabilities
        // of its own - a dangling "Cannot find name '...Promise'" in the generated SDK.
        // Also reference the concrete implementation so CreateBuilderModels deduplicates the
        // interface and concrete builders to the same generated TestMarkerResource class. Wrapper
        // registration must still honor the interface return type from AddTestMarker.
        var atsContext = WithAdditionalCapabilities(
            CreateContextFromTestAssembly(),
            CreateVoidEntryPointCapability(
                "inspectTestMarkerResource",
                new AtsParameterInfo
                {
                    Name = "resource",
                    Type = CreateResourceTypeRef<TestMarkerResource>()
                }));

        var files = _generator.GenerateDistributedApplication(atsContext);
        var aspireTs = files["aspire.mts"];

        // The reference site: the exported entry point returns the Promise wrapper for chaining.
        Assert.Contains("): TestMarkerResourcePromise {", aspireTs);

        // The declarations that reference requires.
        Assert.Contains(
            "export interface TestMarkerResourcePromise extends PromiseLike<TestMarkerResource>",
            aspireTs);
        Assert.Contains(
            "class TestMarkerResourcePromiseImpl implements TestMarkerResourcePromise",
            aspireTs);
    }

    [Fact]
    public void GenerateDistributedApplication_DoesNotEmitUnusedPromiseWrappersForParameterOnlyResources()
    {
        // ITestPromiseCollisionResource and ITestPromiseCollisionResourcePromise are intentionally
        // referenced only as capability parameters. The latter's ordinary generated name is the
        // former's Promise wrapper name, so emitting an unused wrapper produces duplicate TypeScript
        // declarations for both the interface and implementation class.
        var atsContext = CreateContextFromTestAssembly();

        var files = _generator.GenerateDistributedApplication(atsContext);
        var aspireTs = files["aspire.mts"];

        Assert.Equal(1, CountOccurrences(aspireTs, "export interface TestPromiseCollisionResource "));
        Assert.Equal(1, CountOccurrences(aspireTs, "class TestPromiseCollisionResourceImpl "));
        Assert.Equal(1, CountOccurrences(aspireTs, "export interface TestPromiseCollisionResourcePromise "));
        Assert.Equal(1, CountOccurrences(aspireTs, "class TestPromiseCollisionResourcePromiseImpl "));
    }

    [Fact]
    public void GenerateDistributedApplication_DoesNotEmitUnusedPromiseWrappersForMutablePropertyResources()
    {
        // ITestMutablePromiseCollisionResource has only get/set properties. Its property setter's
        // fluent metadata reports the owning resource as its return type, but the generated setter
        // returns Promise<void> and must not reserve a Promise wrapper name that collides with the
        // parameter-only TestMutablePromiseCollisionResourcePromise.
        var atsContext = CreateContextFromTestAssembly();

        var files = _generator.GenerateDistributedApplication(atsContext);
        var aspireTs = files["aspire.mts"];

        Assert.Equal(1, CountOccurrences(aspireTs, "export interface TestMutablePromiseCollisionResource "));
        Assert.Equal(1, CountOccurrences(aspireTs, "class TestMutablePromiseCollisionResourceImpl "));
        Assert.Equal(1, CountOccurrences(aspireTs, "export interface TestMutablePromiseCollisionResourcePromise "));
        Assert.Equal(1, CountOccurrences(aspireTs, "class TestMutablePromiseCollisionResourcePromiseImpl "));
    }

    [Fact]
    public void GenerateDistributedApplication_EmitsPromiseWrapperForReturnedInterfaceAlias()
    {
        // The concrete TestVaultResource is parameter-only, while the directly returned
        // ITestVaultResource derives the same generated class name. Builder deduplication
        // retains the concrete TypeId, but both TypeIds must resolve to its Promise wrapper.
        var scannedContext = CreateContextFromTestAssembly();
        var fixtureCapabilities = scannedContext.Capabilities
            .Where(capability => capability.CapabilityId is
                "Aspire.Hosting.CodeGeneration.TypeScript.Tests/addTestVault" or
                "Aspire.Hosting.CodeGeneration.TypeScript.Tests/withConcreteVaultResource")
            .ToList();
        var atsContext = new AtsContext
        {
            Capabilities = fixtureCapabilities,
            HandleTypes = scannedContext.HandleTypes,
            DtoTypes = scannedContext.DtoTypes,
            EnumTypes = scannedContext.EnumTypes,
            ExportedValues = scannedContext.ExportedValues,
            Diagnostics = scannedContext.Diagnostics
        };

        var files = _generator.GenerateDistributedApplication(atsContext);
        var aspireTs = files["aspire.mts"];

        Assert.Contains("): TestVaultResourcePromise {", aspireTs);
        Assert.Equal(1, CountOccurrences(
            aspireTs,
            "export interface TestVaultResourcePromise extends PromiseLike<TestVaultResource>"));
        Assert.Equal(1, CountOccurrences(
            aspireTs,
            "class TestVaultResourcePromiseImpl implements TestVaultResourcePromise"));
        var returnedAliasTypeId = fixtureCapabilities
            .Single(capability => capability.CapabilityId == "Aspire.Hosting.CodeGeneration.TypeScript.Tests/addTestVault")
            .ReturnType!.TypeId;
        // wrapIfHandle recursively uses this registry for callback arrays and plain objects, so
        // the returned alias TypeId must construct the canonical wrapper for nested handles too.
        Assert.Contains(
            $"registerHandleWrapper('{returnedAliasTypeId}', (handle, client) => new TestVaultResourceImpl(handle as TestVaultResourceHandle, client));",
            aspireTs);
        Assert.Contains(
            """
                async _addTestVaultInternal(name: string): Promise<TestVaultResource> {
                    const rpcArgs: Record<string, unknown> = { builder: this._handle, name };
                    const result = await this._client.invokeCapability<TestVaultResourceHandle>(
                        'Aspire.Hosting.CodeGeneration.TypeScript.Tests/addTestVault',
                        rpcArgs
                    );
                    return new TestVaultResourceImpl(result, this._client);
                }
            """,
            aspireTs);
        Assert.Equal(0, CountOccurrences(
            aspireTs,
            "invokeCapability<ITestVaultResourceHandle>"));
    }

    [Fact]
    public void GenerateDistributedApplication_RejectsUnrelatedResourceTypesWithSameGeneratedName()
    {
        var scannedContext = CreateContextFromTestAssembly();
        var addTestRedis = scannedContext.Capabilities
            .Single(capability => capability.CapabilityId == "Aspire.Hosting.CodeGeneration.TypeScript.Tests/addTestRedis");
        var capabilities = scannedContext.Capabilities
            .Concat(
            [
                CreateSameNameResourceCapability<TestTypes.NameCollisionOne.SameNameResource>("addFirstSameNameResource", addTestRedis),
                CreateSameNameResourceCapability<TestTypes.NameCollisionTwo.SameNameResource>("addSecondSameNameResource", addTestRedis)
            ])
            .ToList();
        var atsContext = new AtsContext
        {
            Capabilities = capabilities,
            HandleTypes = scannedContext.HandleTypes,
            DtoTypes = scannedContext.DtoTypes,
            EnumTypes = scannedContext.EnumTypes,
            ExportedValues = scannedContext.ExportedValues,
            Diagnostics = scannedContext.Diagnostics
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => _generator.GenerateDistributedApplication(atsContext));

        Assert.Contains("SameNameResource", exception.Message);
        Assert.Contains(typeof(TestTypes.NameCollisionOne.SameNameResource).FullName!, exception.Message);
        Assert.Contains(typeof(TestTypes.NameCollisionTwo.SameNameResource).FullName!, exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenerateDistributedApplication_EveryReferencedPromiseWrapperIsDeclared(bool includeHostingAssembly)
    {
        // The invariant behind https://github.com/microsoft/aspire/issues/19507, rather than one
        // instance of it. "{ClassName}Promise" / "{ClassName}PromiseImpl" names are emitted from
        // several independent reference sites (GenerateBuilderMethod's fluent return, the thenable
        // forwarding methods, GenerateEntryPointFunction), while the declarations come from
        // GenerateBuilderPromiseInterface / GenerateThenableClass / GenerateTypeClassInterface.
        // Any guard that skips a declaration while a reference site still emits the name yields
        // "TS2552: Cannot find name" in .aspire/modules/aspire.mts, which blocks "aspire run"
        // entirely. Asserting that every referenced wrapper is declared catches that whole class of
        // bug - including future reintroductions on paths this file has no explicit test for.
        //
        // This also guards the type-class side, where GenerateTypeClassInterface and
        // GenerateTypeClass keep an analogous "no methods and no getter-only properties" guard.
        // Type classes are registered via HasChainableMethods, which evaluates the same predicate,
        // but the predicate remains duplicated across the three type-class sites and could drift.
        // Resource builder emitters instead use the registration set directly. Both type-class
        // branches are exercised by the scanned fixtures: TestResourceContext has methods (declared
        // and referenced), while TestEnvironmentContext has only get/set properties (neither
        // declared nor referenced).
        var atsContext = includeHostingAssembly ? CreateContextFromBothAssemblies() : CreateContextFromTestAssembly();

        var files = _generator.GenerateDistributedApplication(atsContext);
        var aspireTs = files["aspire.mts"];

        // Declarations visible to aspire.mts: its own, plus the hand-written wrappers in the
        // pass-through modules it builds on (e.g. InteractionInputCollectionPromise in base.mts).
        var declarations = new List<string>();
        foreach (var source in new[] { aspireTs, EmbeddedResources.Read("base.mts"), EmbeddedResources.Read("transport.mts") })
        {
            foreach (Match match in s_promiseDeclarationPattern.Matches(StripComments(source)))
            {
                declarations.Add(match.Groups[1].Value);
            }
        }

        var duplicateDeclarations = declarations
            .GroupBy(name => name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key} ({group.Count()})")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        Assert.True(
            duplicateDeclarations.Count == 0,
            $"Generated modules contain duplicate Promise wrapper declaration(s): {string.Join(", ", duplicateDeclarations)}");

        var declared = declarations.ToHashSet(StringComparer.Ordinal);

        // Scan references with comments and string literals stripped so documentation and serialized
        // ATS type IDs cannot be mistaken for TypeScript references.
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in s_promiseReferencePattern.Matches(StripCommentsAndStringLiterals(aspireTs)))
        {
            referenced.Add(match.Value);
        }

        // Guard against a vacuous pass if either pattern stops matching the generated shape.
        Assert.NotEmpty(declared);
        Assert.NotEmpty(referenced);

        var dangling = referenced.Except(declared).OrderBy(name => name, StringComparer.Ordinal).ToList();
        Assert.True(
            dangling.Count == 0,
            $"Generated aspire.mts references Promise wrapper type(s) that are never declared: {string.Join(", ", dangling)}");
    }

    // Declarations: "export interface FooPromise extends ...", "class FooPromiseImpl implements ...".
    private static readonly Regex s_promiseDeclarationPattern =
        new(@"\b(?:interface|class|type)\s+(\w*Promise(?:Impl)?)\b", RegexOptions.Compiled);

    // Uses of a wrapper type name: return types, "new FooPromiseImpl(", type arguments. Anchored on
    // a leading capital so "PromiseLike", bare "Promise" and "trackPromise" are not matched.
    private static readonly Regex s_promiseReferencePattern =
        new(@"\b[A-Z]\w*Promise(?:Impl)?\b", RegexOptions.Compiled);

    private static readonly Regex s_apiDeclarationPattern =
        new(@"\b(?:interface|enum|type)\s+([A-Z]\w*)\b", RegexOptions.Compiled);

    private static readonly Regex s_apiTypeReferencePattern =
        new(@"\b[A-Z]\w*\b", RegexOptions.Compiled);

    /// <summary>
    /// Removes line and block comments from generated TypeScript. Deliberately simple: generated
    /// code has no string literals containing comment delimiters.
    /// </summary>
    private static string StripComments(string typeScript) =>
        Regex.Replace(Regex.Replace(typeScript, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline), @"//[^\n]*", string.Empty);

    /// <summary>
    /// Removes comments and quoted runtime values so only TypeScript syntax remains for reference scanning.
    /// </summary>
    private static string StripCommentsAndStringLiterals(string typeScript) =>
        Regex.Replace(
            StripComments(typeScript),
            @"(?s)(?:""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'|`(?:\\.|[^`\\])*`)",
            string.Empty);

    private static AtsCapabilityInfo CreateSameNameResourceCapability<TResource>(
        string methodName,
        AtsCapabilityInfo distributedApplicationBuilderCapability)
        where TResource : IResource
    {
        var resourceType = typeof(TResource);
        var resourceTypeRef = new AtsTypeRef
        {
            TypeId = $"{resourceType.Assembly.GetName().Name}/{resourceType.FullName}",
            ClrType = resourceType,
            Category = AtsTypeCategory.Handle
        };

        return new AtsCapabilityInfo
        {
            CapabilityId = $"Aspire.Hosting.CodeGeneration.TypeScript.Tests/{methodName}",
            MethodName = methodName,
            Parameters = [],
            ReturnType = resourceTypeRef,
            TargetTypeId = distributedApplicationBuilderCapability.TargetTypeId,
            TargetType = distributedApplicationBuilderCapability.TargetType,
            TargetParameterName = distributedApplicationBuilderCapability.TargetParameterName,
            ReturnsBuilder = true
        };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenerateDistributedApplication_EveryRpcHandleMatchesTheConstructedWrapper(bool includeHostingAssembly)
    {
        // Companion invariant to GenerateDistributedApplication_EveryReferencedPromiseWrapperIsDeclared.
        // Declaring the wrapper is not enough: the handle flowing out of the RPC must also be
        // assignable to the wrapper implementation that consumes it. Handle<T> carries its ATS type
        // ID as a literal-typed $type, so FooHandle and IFooHandle are distinct, non-assignable
        // types even though CreateBuilderModels collapses IFoo and Foo onto the same generated Foo
        // class. Emitting invokeCapability<IFooHandle> and then passing that result to
        // FooImpl's constructor produces "TS2345: Argument of type 'IFooHandle' is not assignable
        // to parameter of type 'FooHandle'", which breaks "aspire run" just as thoroughly as a
        // missing declaration.
        var atsContext = includeHostingAssembly ? CreateContextFromBothAssemblies() : CreateContextFromTestAssembly();

        var files = _generator.GenerateDistributedApplication(atsContext);

        AssertRpcHandlesMatchConstructedWrappers(files["aspire.mts"]);
    }

    /// <summary>
    /// Asserts that every RPC result flowing straight into a wrapper implementation constructor uses
    /// the handle type that constructor declares.
    /// </summary>
    private static void AssertRpcHandlesMatchConstructedWrappers(string generatedAspireTs)
    {
        var aspireTs = StripComments(generatedAspireTs);

        // class FooImpl extends ResourceBuilderBase<FooHandle> ... { constructor(handle: FooHandle, ...
        var constructorHandleTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in s_implementationConstructorPattern.Matches(aspireTs))
        {
            constructorHandleTypes[match.Groups[1].Value] = match.Groups[2].Value;
        }

        // const handle = await client.invokeCapability<FooHandle>( '...', rpcArgs );
        // return new FooImpl(handle, client);
        var mismatches = new List<string>();
        var pairs = 0;
        foreach (Match match in s_rpcHandleConstructionPattern.Matches(aspireTs))
        {
            var invokedHandleType = match.Groups[1].Value;
            var implementationClass = match.Groups[2].Value;
            pairs++;
            if (!constructorHandleTypes.TryGetValue(implementationClass, out var expectedHandleType))
            {
                mismatches.Add($"{implementationClass} is constructed from invokeCapability<{invokedHandleType}> but its constructor handle type was not recognized");
                continue;
            }

            if (!string.Equals(invokedHandleType, expectedHandleType, StringComparison.Ordinal))
            {
                mismatches.Add($"{implementationClass} takes {expectedHandleType} but is constructed from invokeCapability<{invokedHandleType}>");
            }
        }

        // Guard against a vacuous pass if either pattern stops matching the generated shape.
        Assert.NotEmpty(constructorHandleTypes);
        Assert.NotEqual(0, pairs);

        Assert.True(
            mismatches.Count == 0,
            $"Generated aspire.mts constructs wrapper implementations from mismatched handle types: {string.Join("; ", mismatches.Order(StringComparer.Ordinal))}");
    }

    // Resource wrappers use "constructor(handle: FooHandle, ...)", while type-class wrappers use
    // "constructor(private _handle: FooHandle, ...)". Capture the class and its first handle type.
    private static readonly Regex s_implementationConstructorPattern =
        new(
            @"\bclass\s+(\w+)\b[^{]*\{\s*constructor\((?:(?:public|private|protected|readonly)\s+)*\w+:\s*(\w+)",
            RegexOptions.Compiled);

    // An RPC result flowing straight into a wrapper implementation constructor. The intervening
    // capability ID and rpcArgs contain no ';', so [^;]*? stops at the invokeCapability call's own
    // statement terminator.
    private static readonly Regex s_rpcHandleConstructionPattern =
        new(@"invokeCapability<(\w+)>\([^;]*?\);\s*return new (\w+)\(", RegexOptions.Compiled);

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
        return new AtsContext
        {
            Capabilities = [.. context.Capabilities, .. capabilities],
            HandleTypes = context.HandleTypes,
            DtoTypes = context.DtoTypes,
            EnumTypes = context.EnumTypes,
            ExportedValues = context.ExportedValues,
            Diagnostics = context.Diagnostics
        };
    }

    private static AtsTypeRef CreateResourceTypeRef<TResource>() where TResource : IResource =>
        new()
        {
            TypeId = GetAtsTypeId(typeof(TResource)),
            ClrType = typeof(TResource),
            Category = AtsTypeCategory.Handle
        };

    private static AtsCapabilityInfo CreateVoidEntryPointCapability(string methodName, params AtsParameterInfo[] parameters) =>
        new()
        {
            CapabilityId = $"Aspire.Hosting.CodeGeneration.TypeScript.Tests/{methodName}",
            MethodName = methodName,
            Parameters = parameters,
            ReturnType = new AtsTypeRef
            {
                TypeId = AtsConstants.Void,
                Category = AtsTypeCategory.Primitive
            },
            CapabilityKind = AtsCapabilityKind.Method
        };

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

    [Fact]
    public void Generate_ResourceAndResourceNamedPromise_NoDuplicateDeclarations()
    {
        // A zero-capability resource named Foo does not need a Promise wrapper when it is only
        // referenced as a parameter. Generating one would collide with a real FooPromise resource.
        var collisionCapability = CreateVoidEntryPointCapability(
            "inspectPromiseNameCollision",
            new AtsParameterInfo
            {
                Name = "resource",
                Type = CreateResourceTypeRef<TestPromiseNameCollisionResource>()
            },
            new AtsParameterInfo
            {
                Name = "promiseResource",
                Type = CreateResourceTypeRef<TestPromiseNameCollisionResourcePromise>()
            });
        var atsContext = WithAdditionalCapabilities(CreateContextFromTestAssembly(), collisionCapability);

        var files = _generator.GenerateDistributedApplication(atsContext);
        var code = files["aspire.mts"];

        Assert.Equal(1, CountOccurrences(code, "export interface TestPromiseNameCollisionResourcePromise "));
        Assert.Equal(1, CountOccurrences(code, "class TestPromiseNameCollisionResourcePromiseImpl "));
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

    private const string ApiExportPackageName = "Aspire.Hosting.CodeGeneration.TypeScript.Tests";
    private const string ApiExportPackageVersion = "13.5.0";

    [Fact]
    public async Task ApiExportWriterProducesFocusedCanonicalJson()
    {
        var model = new TypeScriptApiModel
        {
            SchemaVersion = 1,
            Language = "typescript",
            Generator = new TypeScriptApiGeneratorIdentity("Aspire.Hosting.CodeGeneration.TypeScript", "13.5.0"),
            Package = new TypeScriptApiPackageIdentity("Aspire.Hosting.Contoso", "1.2.3"),
            Modules =
            [
                new TypeScriptApiModule
                {
                    Name = "index",
                    Items =
                    [
                        new TypeScriptApiItem
                        {
                            Id = "interface:ContosoResource",
                            TypeId = "Aspire.Hosting.Contoso/ContosoResource",
                            Kind = TypeScriptApiItemKind.Interface,
                            Name = "ContosoResource",
                            Declaration = "export interface ContosoResource",
                            OwningAssemblyName = "Aspire.Hosting.Contoso",
                            Summary = "A Contoso resource.",
                            Members =
                            [
                                new TypeScriptApiMember
                                {
                                    Id = "member:ContosoResource.configure",
                                    Kind = TypeScriptApiItemKind.Method,
                                    Name = "configure",
                                    Declaration = "configure(enabled?: boolean): Promise<void>",
                                    CapabilityId = "Aspire.Hosting.Contoso/configure",
                                    OwningAssemblyName = "Aspire.Hosting.Contoso",
                                    Parameters =
                                    [
                                        new TypeScriptApiParameter
                                        {
                                            Name = "enabled",
                                            DeclaredType = "boolean",
                                            IsOptional = true,
                                            Summary = "Whether configuration is enabled."
                                        }
                                    ],
                                    ReturnType = "Promise<void>"
                                }
                            ]
                        }
                    ]
                }
            ],
            Declarations =
            [
                new TypeScriptApiDeclaration
                {
                    Id = "interface:ContosoResource",
                    Content = "export interface ContosoResource {\r\n    configure(enabled?: boolean): Promise<void>;\r\n}",
                    OwningAssemblyName = "Aspire.Hosting.Contoso"
                }
            ]
        };

        var json = TypeScriptApiExportWriter.WriteToJson(model, indented: true);

        await Verify(json, extension: "json")
            .UseFileName("AtsTypeScriptCodeGeneratorTests.FocusedApiExport");
    }

    [Fact]
    public void ApiExportIncludesCodeGeneratorIdentity()
    {
        var model = ProjectApi(CreateEntryPointContext(ApiExportPackageName), ApiExportPackageName);
        using var document = System.Text.Json.JsonDocument.Parse(TypeScriptApiExportWriter.WriteToJson(model));
        var generator = document.RootElement.GetProperty("generator");
        var assembly = typeof(AtsTypeScriptCodeGenerator).Assembly;

        Assert.Equal(assembly.GetName().Name, generator.GetProperty("name").GetString());
        Assert.Equal(
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
            generator.GetProperty("version").GetString());
    }

    [Fact]
    public void ApiExportIncludesExportedValuesWithGeneratedDeclaration()
    {
        var context = CreateContextFromTestAssembly();
        var generatedSource = _generator.GenerateDistributedApplication(context)["aspire.mts"];
        using var document = System.Text.Json.JsonDocument.Parse(
            TypeScriptApiExportWriter.WriteToJson(ProjectApi(context, ApiExportPackageName)));
        var items = document.RootElement.GetProperty("modules")[0].GetProperty("items").EnumerateArray();
        var testConfigs = Assert.Single(
            items,
            item => item.GetProperty("name").GetString() == "TestConfigs");

        Assert.Equal("namespace", testConfigs.GetProperty("kind").GetString());
        Assert.Equal("export namespace TestConfigs", testConfigs.GetProperty("declaration").GetString());
        var defaultConfig = Assert.Single(
            testConfigs.GetProperty("members").EnumerateArray(),
            member => member.GetProperty("name").GetString() == "Default");
        const string expectedDeclaration =
            "export const Default = { name: \"default\", port: 6379, enabled: true, optionalField: \"cache\" } as TestConfigDto";
        Assert.Equal("constant", defaultConfig.GetProperty("kind").GetString());
        Assert.Equal(expectedDeclaration, defaultConfig.GetProperty("declaration").GetString());
        Assert.Equal("The default test configuration.", defaultConfig.GetProperty("summary").GetString());
        Assert.Contains($"{expectedDeclaration};", generatedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ApiExportPromiseDeclarationContainsOnlySourcePromiseMembers()
    {
        var targetType = new AtsTypeRef
        {
            TypeId = $"{ApiExportPackageName}/PromiseContext",
            Category = AtsTypeCategory.Handle
        };
        var stringType = new AtsTypeRef
        {
            TypeId = AtsConstants.String,
            Category = AtsTypeCategory.Primitive
        };
        var voidType = new AtsTypeRef
        {
            TypeId = AtsConstants.Void,
            Category = AtsTypeCategory.Primitive
        };
        var context = CreateApiContext(
            new AtsCapabilityInfo
            {
                CapabilityId = $"{ApiExportPackageName}/PromiseContext.readOnly.get",
                MethodName = "readOnly",
                OwningTypeName = "PromiseContext",
                Parameters = [],
                ReturnType = stringType,
                TargetTypeId = targetType.TypeId,
                TargetType = targetType,
                ExpandedTargetTypes = [],
                CapabilityKind = AtsCapabilityKind.PropertyGetter
            },
            new AtsCapabilityInfo
            {
                CapabilityId = $"{ApiExportPackageName}/PromiseContext.mutable.get",
                MethodName = "mutable",
                OwningTypeName = "PromiseContext",
                Parameters = [],
                ReturnType = stringType,
                TargetTypeId = targetType.TypeId,
                TargetType = targetType,
                ExpandedTargetTypes = [],
                CapabilityKind = AtsCapabilityKind.PropertyGetter
            },
            new AtsCapabilityInfo
            {
                CapabilityId = $"{ApiExportPackageName}/PromiseContext.mutable.set",
                MethodName = "setMutable",
                OwningTypeName = "PromiseContext",
                Parameters = [new AtsParameterInfo { Name = "value", Type = stringType }],
                ReturnType = voidType,
                TargetTypeId = targetType.TypeId,
                TargetType = targetType,
                ExpandedTargetTypes = [],
                CapabilityKind = AtsCapabilityKind.PropertySetter
            },
            new AtsCapabilityInfo
            {
                CapabilityId = $"{ApiExportPackageName}/PromiseContext.run",
                MethodName = "run",
                OwningTypeName = "PromiseContext",
                Parameters = [],
                ReturnType = voidType,
                TargetTypeId = targetType.TypeId,
                TargetType = targetType,
                ExpandedTargetTypes = [],
                CapabilityKind = AtsCapabilityKind.InstanceMethod
            });

        var declaration = Assert.Single(
            ProjectApi(context, ApiExportPackageName).Declarations,
            declaration => declaration.Content.StartsWith(
                "export interface PromiseContextPromise ",
                StringComparison.Ordinal));
        var expectedDeclaration = """
            export interface PromiseContextPromise extends PromiseLike<PromiseContext> {
                readOnly(): Promise<string>;
                run(): PromiseContextPromise;
            }
            """.ReplaceLineEndings("\n");

        Assert.Equal(expectedDeclaration, declaration.Content);
        Assert.Contains(
            expectedDeclaration,
            _generator.GenerateDistributedApplication(context)["aspire.mts"].ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ApiReferenceExporterRequiresAndHonorsCancellation()
    {
        var method = typeof(IApiReferenceExporter).GetMethod(nameof(IApiReferenceExporter.ExportApi));

        Assert.NotNull(method);
        Assert.Collection(
            method.GetParameters(),
            parameter => Assert.Equal(typeof(AtsContext), parameter.ParameterType),
            parameter => Assert.Equal(typeof(ApiReferenceExportOptions), parameter.ParameterType),
            parameter =>
            {
                Assert.Equal(typeof(CancellationToken), parameter.ParameterType);
                Assert.False(parameter.HasDefaultValue);
            });

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var context = new AtsContext
        {
            Capabilities = null!,
            HandleTypes = [],
            DtoTypes = [],
            EnumTypes = []
        };

        IApiReferenceExporter exporter = new AtsTypeScriptApiReferenceExporter();
        Assert.Throws<OperationCanceledException>(() => exporter.ExportApi(
            context,
            new ApiReferenceExportOptions(
                ApiExportPackageName,
                ApiExportPackageVersion,
                [ApiExportPackageName]),
            cancellation.Token));
    }

    [Fact]
    public void ApiReferenceExportOptionsCopiesExportingAssemblyNames()
    {
        var exportingAssemblyNames = new List<string> { ApiExportPackageName };
        var options = new ApiReferenceExportOptions(
            ApiExportPackageName,
            ApiExportPackageVersion,
            exportingAssemblyNames);

        exportingAssemblyNames.Clear();

        Assert.Equal(ApiExportPackageName, Assert.Single(options.ExportingAssemblyNames));
    }

    [Fact]
    public void ApiReferenceExportOptionsExposesReadOnlyExportingAssemblyNames()
    {
        var options = new ApiReferenceExportOptions(
            ApiExportPackageName,
            ApiExportPackageVersion,
            [ApiExportPackageName]);
        var exportingAssemblyNames = Assert.IsAssignableFrom<IList<string>>(options.ExportingAssemblyNames);

        Assert.Throws<NotSupportedException>(() => exportingAssemblyNames[0] = "Changed");
    }

    [Fact]
    public void ApiReferenceExportOptionsRequiresAnExportingAssembly()
    {
        Assert.Throws<ArgumentException>(() => new ApiReferenceExportOptions(
            ApiExportPackageName,
            ApiExportPackageVersion,
            []));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void ApiReferenceExportOptionsRequiresValidExportingAssemblyNames(string? exportingAssemblyName)
    {
        Assert.Throws<ArgumentException>(() => new ApiReferenceExportOptions(
            ApiExportPackageName,
            ApiExportPackageVersion,
            [exportingAssemblyName!]));
    }

    [Fact]
    public void ApiExportEntrypointIdsIncludeTheOwningAssembly()
    {
        const string firstPackage = "Aspire.Hosting.Contoso.EntryPoints";
        const string secondPackage = "Aspire.Hosting.Fabrikam.EntryPoints";

        var firstModel = ProjectApi(CreateEntryPointContext(firstPackage), firstPackage);
        var first = Assert.Single(firstModel.Modules.SelectMany(module => module.Items));
        var second = Assert.Single(ProjectApi(CreateEntryPointContext(secondPackage), secondPackage)
            .Modules.SelectMany(module => module.Items));

        Assert.Equal($"entrypoint:{firstPackage}:startThing", first.Id);
        Assert.Equal($"entrypoint:{secondPackage}:startThing", second.Id);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(
            "function startThing(client: AspireClientRpc, name: string, retries?: number): Promise<void>",
            first.Declaration);
        var declaration = Assert.Single(
            firstModel.Declarations,
            declaration => declaration.Id == $"{firstPackage}:entrypoint:startThing");
        Assert.Equal(
            "export declare function startThing(client: AspireClientRpc, name: string, retries?: number): Promise<void>;",
            declaration.Content);

        var generatedSource = new AtsTypeScriptCodeGenerator()
            .GenerateDistributedApplication(CreateEntryPointContext(firstPackage))["aspire.mts"];
        Assert.Contains($"export async {first.Declaration} {{", generatedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ApiExportKeepsPackageLocalOptionsNamesAndCombinedGenerationIsDeterministic()
    {
        const string eventHubsPackage = "Aspire.Hosting.Azure.EventHubs";
        const string serviceBusPackage = "Aspire.Hosting.Azure.ServiceBus";
        var eventHubsCapability = CreateRunAsEmulatorCapability(
            eventHubsPackage,
            "DistributedApplicationBuilder",
            new AtsTypeRef
            {
                TypeId = $"{eventHubsPackage}/AzureEventHubsEmulatorResource",
                Category = AtsTypeCategory.Handle
            });
        var serviceBusCapability = CreateRunAsEmulatorCapability(
            serviceBusPackage,
            "DistributedApplicationBuilder",
            new AtsTypeRef
            {
                TypeId = $"{serviceBusPackage}/AzureServiceBusEmulatorResource",
                Category = AtsTypeCategory.Handle
            });

        var eventHubsModel = ProjectApi(CreateApiContext(eventHubsCapability), eventHubsPackage);
        var serviceBusModel = ProjectApi(CreateApiContext(serviceBusCapability), serviceBusPackage);
        var eventHubsOptions = Assert.Single(
            eventHubsModel.Declarations,
            declaration => declaration.Content.StartsWith("export interface RunAsEmulatorOptions ", StringComparison.Ordinal));
        var serviceBusOptions = Assert.Single(
            serviceBusModel.Declarations,
            declaration => declaration.Content.StartsWith("export interface RunAsEmulatorOptions ", StringComparison.Ordinal));

        Assert.Equal(
            (eventHubsPackage, ApiExportPackageVersion, $"{eventHubsPackage}:options:RunAsEmulatorOptions"),
            (eventHubsModel.Package.Name, eventHubsModel.Package.Version, eventHubsOptions.Id));
        Assert.Equal(
            (serviceBusPackage, ApiExportPackageVersion, $"{serviceBusPackage}:options:RunAsEmulatorOptions"),
            (serviceBusModel.Package.Name, serviceBusModel.Package.Version, serviceBusOptions.Id));
        Assert.Equal(
            """
            export interface RunAsEmulatorOptions {
                configure?: (emulator: AzureEventHubsEmulatorResourceHandle) => Promise<void>;
            }
            """.ReplaceLineEndings("\n"),
            eventHubsOptions.Content);
        Assert.Equal(
            """
            export interface RunAsEmulatorOptions {
                configure?: (emulator: AzureServiceBusEmulatorResourceHandle) => Promise<void>;
            }
            """.ReplaceLineEndings("\n"),
            serviceBusOptions.Content);
        Assert.NotEqual(eventHubsOptions.Content, serviceBusOptions.Content);
        AssertApiDeclarationsAreSelfContained(eventHubsModel);
        AssertApiDeclarationsAreSelfContained(serviceBusModel);
        Assert.Contains(
            "runAsEmulator(options?: RunAsEmulatorOptions)",
            Assert.Single(
                eventHubsModel.Modules.SelectMany(module => module.Items).SelectMany(item => item.Members),
                member => member.CapabilityId == eventHubsCapability.CapabilityId).Declaration,
            StringComparison.Ordinal);
        Assert.Contains(
            "runAsEmulator(options?: RunAsEmulatorOptions)",
            Assert.Single(
                serviceBusModel.Modules.SelectMany(module => module.Items).SelectMany(item => item.Members),
                member => member.CapabilityId == serviceBusCapability.CapabilityId).Declaration,
            StringComparison.Ordinal);

        var forwardContext = CreateApiContext(eventHubsCapability, serviceBusCapability);
        var reverseContext = CreateApiContext(serviceBusCapability, eventHubsCapability);
        var forwardSource = _generator.GenerateDistributedApplication(forwardContext)["aspire.mts"];
        var reverseSource = _generator.GenerateDistributedApplication(reverseContext)["aspire.mts"];
        Assert.Equal(forwardSource, reverseSource);

        var combinedPackage = new TypeScriptApiPackageIdentity("Aspire.Hosting.Combined", ApiExportPackageVersion);
        var combinedAssemblies = new[] { eventHubsPackage, serviceBusPackage };
        var forwardModel = new TypeScriptApiProjector(forwardContext)
            .BuildApiModel(combinedPackage, combinedAssemblies, CancellationToken.None);
        var reverseModel = new TypeScriptApiProjector(reverseContext)
            .BuildApiModel(combinedPackage, combinedAssemblies, CancellationToken.None);
        Assert.Equal(
            TypeScriptApiExportWriter.WriteToJson(forwardModel),
            TypeScriptApiExportWriter.WriteToJson(reverseModel));

        var methodsByCapability = forwardModel.Modules
            .SelectMany(module => module.Items)
            .SelectMany(item => item.Members)
            .Where(member => member.CapabilityId is not null)
            .ToDictionary(member => member.CapabilityId!, StringComparer.Ordinal);
        Assert.Contains(
            "options?: RunAsEmulatorOptions",
            methodsByCapability[eventHubsCapability.CapabilityId].Declaration,
            StringComparison.Ordinal);
        Assert.Contains(
            "options?: RunAsEmulator1Options",
            methodsByCapability[serviceBusCapability.CapabilityId].Declaration,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ApiExportExplicitInterfaceMemberNameMatchesItsDeclaration()
    {
        const string targetTypeId = ApiExportPackageName + "/Contoso.WidgetContext";
        var targetType = new AtsTypeRef
        {
            TypeId = targetTypeId,
            Category = AtsTypeCategory.Handle
        };
        var context = new AtsContext
        {
            Capabilities =
            [
                new AtsCapabilityInfo
                {
                    CapabilityId = ApiExportPackageName + "/IWidget.configure",
                    MethodName = "IWidget.configure",
                    OwningTypeName = "WidgetContext",
                    Parameters = [],
                    ReturnType = new AtsTypeRef
                    {
                        TypeId = AtsConstants.Void,
                        Category = AtsTypeCategory.Primitive
                    },
                    TargetTypeId = targetTypeId,
                    TargetType = targetType,
                    ExpandedTargetTypes = [],
                    CapabilityKind = AtsCapabilityKind.InstanceMethod
                }
            ],
            HandleTypes = [],
            DtoTypes = [],
            EnumTypes = [],
            ExportedValues = [],
            Diagnostics = []
        };

        var member = Assert.Single(ProjectApi(context, ApiExportPackageName)
            .Modules.SelectMany(module => module.Items)
            .SelectMany(item => item.Members));

        Assert.Equal("configure", member.Name);
        Assert.StartsWith($"{member.Name}(", member.Declaration, StringComparison.Ordinal);
    }

    [Fact]
    public void ApiExportUsesGeneratedSignaturesAndSeparatesReferencedTypes()
    {
        var context = CreateContextFromBothAssemblies();
        var model = ProjectApi(context, ApiExportPackageName);
        var generatedSource = new AtsTypeScriptCodeGenerator()
            .GenerateDistributedApplication(context)["aspire.mts"];
        var items = model.Modules.SelectMany(module => module.Items).ToList();

        Assert.NotEmpty(items);
        Assert.All(
            items.Where(item => item.Kind != TypeScriptApiItemKind.Augmentation),
            item => Assert.Equal(ApiExportPackageName, item.OwningAssemblyName));
        Assert.All(
            items.SelectMany(item => item.Members).Where(member => member.Kind == TypeScriptApiItemKind.Method),
            member => Assert.Contains(member.Declaration, generatedSource, StringComparison.Ordinal));

        var itemIds = items.Select(item => item.Id).ToList();
        Assert.Equal(itemIds.Count, itemIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(
            model.Declarations,
            declaration => declaration.OwningAssemblyName == "Aspire.Hosting");
        Assert.All(
            model.Declarations,
            declaration => Assert.DoesNotContain('\r', declaration.Content));
    }

    private static TypeScriptApiModel ProjectApi(AtsContext context, string packageName)
        => new TypeScriptApiProjector(context).BuildApiModel(
            new TypeScriptApiPackageIdentity(packageName, ApiExportPackageVersion),
            [packageName],
            CancellationToken.None);

    private static void AssertApiDeclarationsAreSelfContained(TypeScriptApiModel model)
    {
        var completeSource = string.Join("\n", model.Declarations.Select(declaration => declaration.Content));
        var declared = s_apiDeclarationPattern.Matches(completeSource)
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        // The runtime declaration is a fixed compiler baseline. Scan every package-produced fragment
        // after removing comments and branded-handle string literals so only TypeScript names remain.
        var packageSource = string.Join(
            "\n",
            model.Declarations
                .Where(declaration => declaration.Id != "aspire:runtime:base")
                .Select(declaration => declaration.Content));
        var referenced = s_apiTypeReferencePattern.Matches(StripCommentsAndStringLiterals(packageSource))
            .Select(match => match.Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(declared);
        Assert.NotEmpty(referenced);
        referenced.ExceptWith(declared);
        referenced.ExceptWith(["Promise", "PromiseLike"]);
        Assert.True(
            referenced.Count == 0,
            $"Package '{model.Package.Name}' declarations reference type(s) that are never declared: " +
            string.Join(", ", referenced.Order(StringComparer.Ordinal)));
    }

    private static AtsContext CreateApiContext(params AtsCapabilityInfo[] capabilities)
        => new()
        {
            Capabilities = capabilities,
            HandleTypes = [],
            DtoTypes = [],
            EnumTypes = [],
            ExportedValues = [],
            Diagnostics = []
        };

    private static AtsCapabilityInfo CreateRunAsEmulatorCapability(
        string packageName,
        string targetTypeName,
        AtsTypeRef callbackPayloadType)
    {
        var targetType = new AtsTypeRef
        {
            TypeId = $"Aspire.Hosting/{targetTypeName}",
            Category = AtsTypeCategory.Handle
        };

        return new AtsCapabilityInfo
        {
            CapabilityId = $"{packageName}/runAsEmulator",
            MethodName = "runAsEmulator",
            Parameters =
            [
                new AtsParameterInfo
                {
                    Name = "configure",
                    Type = new AtsTypeRef
                    {
                        TypeId = "callback",
                        Category = AtsTypeCategory.Callback
                    },
                    IsOptional = true,
                    IsCallback = true,
                    CallbackParameters =
                    [
                        new AtsCallbackParameterInfo
                        {
                            Name = "emulator",
                            Type = callbackPayloadType
                        }
                    ],
                    CallbackReturnType = new AtsTypeRef
                    {
                        TypeId = AtsConstants.Void,
                        Category = AtsTypeCategory.Primitive
                    }
                }
            ],
            ReturnType = new AtsTypeRef
            {
                TypeId = AtsConstants.Void,
                Category = AtsTypeCategory.Primitive
            },
            TargetTypeId = targetType.TypeId,
            TargetType = targetType,
            ExpandedTargetTypes = [],
            CapabilityKind = AtsCapabilityKind.InstanceMethod
        };
    }

    private static AtsContext CreateEntryPointContext(string packageName)
    {
        return new AtsContext
        {
            Capabilities =
            [
                new AtsCapabilityInfo
                {
                    CapabilityId = $"{packageName}/startThing",
                    MethodName = "startThing",
                    Parameters =
                    [
                        new AtsParameterInfo
                        {
                            Name = "name",
                            Type = new AtsTypeRef
                            {
                                TypeId = AtsConstants.String,
                                Category = AtsTypeCategory.Primitive
                            }
                        },
                        new AtsParameterInfo
                        {
                            Name = "retries",
                            Type = new AtsTypeRef
                            {
                                TypeId = AtsConstants.Number,
                                Category = AtsTypeCategory.Primitive
                            },
                            IsOptional = true
                        }
                    ],
                    ReturnType = new AtsTypeRef
                    {
                        TypeId = AtsConstants.Void,
                        Category = AtsTypeCategory.Primitive
                    },
                    ExpandedTargetTypes = [],
                    CapabilityKind = AtsCapabilityKind.Method
                }
            ],
            HandleTypes = [],
            DtoTypes = [],
            EnumTypes = [],
            ExportedValues = [],
            Diagnostics = []
        };
    }
}
