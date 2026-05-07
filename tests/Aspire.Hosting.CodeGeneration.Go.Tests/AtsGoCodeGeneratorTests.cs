// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RemoteHost;
using Aspire.TypeSystem;
using Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes;

namespace Aspire.Hosting.CodeGeneration.Go.Tests;

public class AtsGoCodeGeneratorTests
{
    private readonly AtsGoCodeGenerator _generator = new();

    // The test types are compiled into this assembly via Compile Include
    private const string TestTypesAssemblyName = "Aspire.Hosting.CodeGeneration.Go.Tests";

    [Fact]
    public void Language_ReturnsGo()
    {
        Assert.Equal("Go", _generator.Language);
    }

    [Fact]
    public async Task GenerateDistributedApplication_WithTestTypes_GeneratesCorrectOutput()
    {
        // Arrange
        var atsContext = CreateContextFromTestAssembly();

        // Act
        var files = _generator.GenerateDistributedApplication(atsContext);

        // Assert
        Assert.Contains("aspire.go", files.Keys);
        Assert.Contains("transport.go", files.Keys);
        Assert.Contains("base.go", files.Keys);
        Assert.Contains("go.mod", files.Keys);

        await Verify(files["aspire.go"], extension: "go")
            .UseFileName("AtsGeneratedAspire");
    }

    [Fact]
    public void GenerateDistributedApplication_WithTestTypes_IncludesExportedValues()
    {
        var atsContext = CreateContextFromTestAssembly();

        Assert.Contains(atsContext.ExportedValues, value => string.Join(".", value.PathSegments) == "TestConfigs.Default");
        Assert.Contains(atsContext.ExportedValues, value => string.Join(".", value.PathSegments) == "TestConfigs.Profiles.Development");

        var files = _generator.GenerateDistributedApplication(atsContext);
        var aspireGo = files["aspire.go"];

        Assert.Contains("var TestConfigs = struct {", aspireGo);
        Assert.Contains("Default *TestConfigDto", aspireGo);
        Assert.Contains("Profiles struct {", aspireGo);
        Assert.Contains("Development *TestConfigDto", aspireGo);
        Assert.Matches(@"Profiles struct \{\r?\n\t\tDevelopment \*TestConfigDto\r?\n\t\}\r?\n\tSecure \*TestConfigDto", aspireGo);
    }

    [Fact]
    public void GenerateDistributedApplication_WithTestTypes_IncludesCapabilities()
    {
        // Arrange
        var capabilities = ScanCapabilitiesFromTestAssembly();

        // Assert that capabilities are discovered
        Assert.NotEmpty(capabilities);

        // Check for specific capabilities (uses AssemblyName/methodName format)
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
        // End-to-end test: verify that AddEnvironment appears on TestRedisResource
        // in the generated Go when using 2-pass scanning.
        var atsContext = CreateContextFromBothAssemblies();

        // Generate Go
        var files = _generator.GenerateDistributedApplication(atsContext);
        var aspireGo = files["aspire.go"];

        // Verify AddEnvironment appears (method should exist for resources that support it)
        Assert.Contains("WithEnvironment", aspireGo);

        // Snapshot for detailed verification
        await Verify(aspireGo, extension: "go")
            .UseFileName("TwoPassScanningGeneratedAspire");
    }

    [Fact]
    public void GeneratedCode_UsesPascalCaseMethodNames()
    {
        // Verify that the generated Go code uses PascalCase for exported method names
        var atsContext = CreateContextFromBothAssemblies();

        var files = _generator.GenerateDistributedApplication(atsContext);
        var aspireGo = files["aspire.go"];

        // Go exported methods should use PascalCase
        Assert.Contains("AddContainer", aspireGo);
        Assert.Contains("WithEnvironment", aspireGo);
    }

    [Fact]
    public void GeneratedCode_HasCreateBuilderFunction()
    {
        // Verify that the generated Go code has a CreateBuilder function
        var atsContext = CreateContextFromBothAssemblies();

        var files = _generator.GenerateDistributedApplication(atsContext);
        var aspireGo = files["aspire.go"];

        Assert.Contains("func CreateBuilder", aspireGo);
    }

    [Fact]
    public void GeneratedCode_HasGoModFile()
    {
        // Verify that go.mod file is generated
        var atsContext = CreateContextFromBothAssemblies();

        var files = _generator.GenerateDistributedApplication(atsContext);

        Assert.Contains("go.mod", files.Keys);
        Assert.Contains("module apphost/modules/aspire", files["go.mod"]);
    }

    [Fact]
    public void GenerateDistributedApplication_HostingAssembly_SanitizesGoKeywordParameters()
    {
        var atsContext = CreateContextFromBothAssemblies();

        var files = _generator.GenerateDistributedApplication(atsContext);
        var aspireGo = files["aspire.go"];

        Assert.Matches(@"func \(s \*[^\)]*\) WithRelationship\([^)]*type_ string\)", aspireGo);
        Assert.DoesNotMatch(@"func \(s \*[^\)]*\) WithRelationship\([^)]*\btype string\)", aspireGo);
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

    // ---- Aspire.Hosting.Go capability scanning ----------------------------

    [Fact]
    public void GoHostingAssembly_ExposesAddGoAppCapability()
    {
        var capabilities = ScanCapabilitiesFromGoHostingAssembly();

        Assert.Contains(capabilities, c => c.CapabilityId == "Aspire.Hosting.Go/addGoApp");
    }

    [Fact]
    public void GoHostingAssembly_ExposesWithAppArgsCapability()
    {
        var capabilities = ScanCapabilitiesFromGoHostingAssembly();

        Assert.Contains(capabilities, c => c.CapabilityId == "Aspire.Hosting.Go/withAppArgs");
    }

    [Fact]
    public void GoHostingAssembly_ExposesWithBuildTagsCapability()
    {
        var capabilities = ScanCapabilitiesFromGoHostingAssembly();

        Assert.Contains(capabilities, c => c.CapabilityId == "Aspire.Hosting.Go/withBuildTags");
    }

    [Fact]
    public void GoHostingAssembly_ExposesWithLdFlagsCapability()
    {
        var capabilities = ScanCapabilitiesFromGoHostingAssembly();

        Assert.Contains(capabilities, c => c.CapabilityId == "Aspire.Hosting.Go/withLdFlags");
    }

    [Fact]
    public void GoHostingAssembly_ExposesWithDelveServerCapability()
    {
        var capabilities = ScanCapabilitiesFromGoHostingAssembly();

        Assert.Contains(capabilities, c => c.CapabilityId == "Aspire.Hosting.Go/withDelveServer");
    }

    [Fact]
    public void GoHostingAssembly_ExposesWithRaceDetectorCapability()
    {
        var capabilities = ScanCapabilitiesFromGoHostingAssembly();

        Assert.Contains(capabilities, c => c.CapabilityId == "Aspire.Hosting.Go/withRaceDetector");
    }

    [Fact]
    public void GoHostingAssembly_ExposesWithGcFlagsCapability()
    {
        var capabilities = ScanCapabilitiesFromGoHostingAssembly();

        Assert.Contains(capabilities, c => c.CapabilityId == "Aspire.Hosting.Go/withGcFlags");
    }

    [Fact]
    public void GoHostingAssembly_ExposesWithTidyCapability()
    {
        var capabilities = ScanCapabilitiesFromGoHostingAssembly();

        Assert.Contains(capabilities, c => c.CapabilityId == "Aspire.Hosting.Go/withTidy");
    }

    [Fact]
    public void GoHostingAssembly_ExposesWithVendorCapability()
    {
        var capabilities = ScanCapabilitiesFromGoHostingAssembly();

        Assert.Contains(capabilities, c => c.CapabilityId == "Aspire.Hosting.Go/withVendor");
    }

    [Fact]
    public void GoHostingAssembly_ExposesWithVetCapability()
    {
        var capabilities = ScanCapabilitiesFromGoHostingAssembly();

        Assert.Contains(capabilities, c => c.CapabilityId == "Aspire.Hosting.Go/withVet");
    }

    [Fact]
    public void GoHostingAssembly_AddGoApp_ReturnsBuilder()
    {
        var capabilities = ScanCapabilitiesFromGoHostingAssembly();
        var addGoApp = capabilities.First(c => c.CapabilityId == "Aspire.Hosting.Go/addGoApp");

        Assert.True(addGoApp.ReturnsBuilder, "addGoApp should return IResourceBuilder for fluent chaining");
    }

    [Fact]
    public void GoHostingAssembly_AddGoApp_HasCorrectParameters()
    {
        var capabilities = ScanCapabilitiesFromGoHostingAssembly();
        var addGoApp = capabilities.First(c => c.CapabilityId == "Aspire.Hosting.Go/addGoApp");

        Assert.Contains(addGoApp.Parameters, p => p.Name == "name");
        Assert.Contains(addGoApp.Parameters, p => p.Name == "appDirectory");
    }

    [Fact]
    public void GoHostingAssembly_WithDelveServer_PortParamIsOptional()
    {
        var capabilities = ScanCapabilitiesFromGoHostingAssembly();
        var withDelveServer = capabilities.First(c => c.CapabilityId == "Aspire.Hosting.Go/withDelveServer");

        var portParam = withDelveServer.Parameters.FirstOrDefault(p => p.Name == "port");
        Assert.NotNull(portParam);
        Assert.True(portParam.IsOptional, "port should be optional (defaults to 2345)");
    }

    [Fact]
    public async Task GoHostingAssembly_GeneratesGoSdkWithGoAppMethods()
    {
        var goHostingAssembly = typeof(GoHostingExtensions).Assembly;
        var hostingAssembly = typeof(DistributedApplication).Assembly;

        var result = AtsCapabilityScanner.ScanAssemblies([hostingAssembly, goHostingAssembly]);
        var atsContext = result.ToAtsContext();

        var files = _generator.GenerateDistributedApplication(atsContext);
        var aspireGo = files["aspire.go"];

        Assert.Contains("AddGoApp", aspireGo);
        Assert.Contains("WithAppArgs", aspireGo);
        Assert.Contains("WithBuildTags", aspireGo);
        Assert.Contains("WithGcFlags", aspireGo);
        Assert.Contains("WithLdFlags", aspireGo);
        Assert.Contains("WithDelveServer", aspireGo);
        Assert.Contains("WithRaceDetector", aspireGo);
        Assert.Contains("WithTidy", aspireGo);
        Assert.Contains("WithVendor", aspireGo);
        Assert.Contains("WithVet", aspireGo);

        await Verify(aspireGo, extension: "go")
            .UseFileName("GoHostingGeneratedAspire");
    }

    private static List<AtsCapabilityInfo> ScanCapabilitiesFromGoHostingAssembly()
    {
        var goHostingAssembly = typeof(GoHostingExtensions).Assembly;
        var result = AtsCapabilityScanner.ScanAssembly(goHostingAssembly);
        return result.Capabilities;
    }
}
