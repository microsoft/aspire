// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RemoteHost;
using Aspire.TestUtilities;
using Aspire.TypeSystem;
using Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes;

namespace Aspire.Hosting.CodeGeneration.Rust.Tests;

public class AtsRustCodeGeneratorTests
{
    private readonly AtsRustCodeGenerator _generator = new();

    // The test types are compiled into this assembly via Compile Include
    private const string TestTypesAssemblyName = "Aspire.Hosting.CodeGeneration.Rust.Tests";

    [Fact]
    public void Language_ReturnsRust()
    {
        Assert.Equal("Rust", _generator.Language);
    }

    [Fact]
    public async Task GenerateDistributedApplication_WithTestTypes_GeneratesCorrectOutput()
    {
        // Arrange
        var atsContext = CreateContextFromTestAssembly();

        // Act
        var files = _generator.GenerateDistributedApplication(atsContext);

        // Assert
        Assert.Contains("aspire.rs", files.Keys);
        Assert.Contains("transport.rs", files.Keys);
        Assert.Contains("base.rs", files.Keys);
        Assert.Contains("mod.rs", files.Keys);

        await Verify(files["aspire.rs"], extension: "rs")
            .UseFileName("AtsGeneratedAspire");
    }

    [Fact]
    [RequiresTools(["cargo"])]
    public async Task GeneratedTerminalCallback_SupportsDefaultsAndCamelCaseDtos()
    {
        var directory = Directory.CreateTempSubdirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            var files = _generator.GenerateDistributedApplication(CreateContextFromBothAssemblies());
            foreach (var (name, content) in files)
            {
                await File.WriteAllTextAsync(Path.Combine(directory.FullName, name), content, timeout.Token);
            }
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "Cargo.toml"), """
                [package]
                name = "terminal_callback_probe"
                version = "0.1.0"
                edition = "2021"

                [lib]
                path = "mod.rs"

                [[test]]
                name = "terminal"
                path = "terminal.rs"

                [dependencies]
                serde = { version = "1.0", features = ["derive"] }
                serde_json = "1.0"
                lazy_static = "1.4"
                """, timeout.Token);
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "terminal.rs"), """
                use terminal_callback_probe::{ExecutableResource, TerminalOptions};
                use serde_json::json;

                // Compile default-preserving and configured callbacks without a running AppHost.
                fn configure_resource(resource: &ExecutableResource) {
                    let _ = resource.with_terminal(|_| serde_json::Value::Null);
                    let _ = resource.with_terminal(|args| {
                        let mut options: TerminalOptions = serde_json::from_value(args[0].clone()).unwrap();
                        options.columns = 200.0;
                        json!({"p0": options.to_map()})
                    });
                }

                #[test]
                fn terminal_dto_round_trip() {
                    let mut options: TerminalOptions = serde_json::from_value(
                        json!({"columns": 132, "rows": 50})
                    ).unwrap();
                    assert_eq!(options.columns, 132.0);
                    assert_eq!(options.rows, 50.0);
                    options.columns = 200.0;
                    let expected = json!({"columns": 200.0, "rows": 50.0});
                    assert_eq!(serde_json::to_value(options.to_map()).unwrap(), expected);
                    assert_eq!(serde_json::to_value(&options).unwrap(), expected);

                    // Exported-value metadata still uses CLR names.
                    let exported: TerminalOptions = serde_json::from_value(
                        json!({"Columns": 132, "Rows": 50})
                    ).unwrap();
                    assert_eq!(exported.rows, 50.0);
                }
                """, timeout.Token);
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("cargo")
                {
                    WorkingDirectory = directory.FullName,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    ArgumentList = { "test", "--quiet", "--test", "terminal" }
                }
            };
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                Assert.True(process.ExitCode == 0, $"Rust terminal callback test failed:{Environment.NewLine}{await stdout}{await stderr}");
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void GenerateDistributedApplication_NullablePrimitiveArrayElementsUseOptions()
    {
        var nullableNumbers = Assert.IsType<AtsTypeRef>(AtsCapabilityScanner.CreateTypeRef(typeof(double?[])));
        var atsContext = new AtsContext
        {
            Capabilities = [],
            HandleTypes = [],
            EnumTypes = [],
            DtoTypes =
            [
                new AtsDtoTypeInfo
                {
                    TypeId = "Tests/NullableArrayDto",
                    Name = "NullableArrayDto",
                    Properties =
                    [
                        new AtsDtoPropertyInfo
                        {
                            Name = "Values",
                            Type = nullableNumbers
                        }
                    ]
                }
            ]
        };

        var generated = _generator.GenerateDistributedApplication(atsContext)["aspire.rs"];

        Assert.Contains("pub values: Vec<Option<f64>>", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportedNullablePrimitiveArraysUseOptionElements()
    {
        var nullableNumbers = Assert.IsType<AtsTypeRef>(AtsCapabilityScanner.CreateTypeRef(typeof(double?[])));
        var atsContext = new AtsContext
        {
            Capabilities = [],
            HandleTypes = [],
            EnumTypes = [],
            DtoTypes = [],
            ExportedValues =
            [
                new AtsExportedValueInfo
                {
                    OwningAssemblyName = TestTypesAssemblyName,
                    PathSegments = ["NullableArrays", "Numbers"],
                    Value = JsonNode.Parse("[1,null,2.5]"),
                    Type = nullableNumbers
                }
            ]
        };

        var generated = _generator.GenerateDistributedApplication(atsContext)["aspire.rs"];

        Assert.Contains("pub fn numbers() -> Vec<Option<f64>>", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateDistributedApplication_WithTestTypes_IncludesExportedValues()
    {
        var atsContext = CreateContextFromTestAssembly();

        Assert.Contains(atsContext.ExportedValues, value => string.Join(".", value.PathSegments) == "TestConfigs.Default");
        Assert.Contains(atsContext.ExportedValues, value => string.Join(".", value.PathSegments) == "TestConfigs.Profiles.Development");

        var files = _generator.GenerateDistributedApplication(atsContext);
        var aspireRs = files["aspire.rs"];

        Assert.Contains("pub mod test_configs", aspireRs);
        Assert.Contains("pub fn default() -> TestConfigDto", aspireRs);
        Assert.Contains("pub mod profiles", aspireRs);
        Assert.Contains("pub fn development() -> TestConfigDto", aspireRs);
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
        // End-to-end test: verify that with_environment appears on TestRedisResource
        // in the generated Rust when using 2-pass scanning.
        var atsContext = CreateContextFromBothAssemblies();

        // Generate Rust
        var files = _generator.GenerateDistributedApplication(atsContext);
        var aspireRs = files["aspire.rs"];

        // Verify with_environment appears (method should exist for resources that support it)
        Assert.Contains("with_environment", aspireRs);

        // Snapshot for detailed verification
        await Verify(aspireRs, extension: "rs")
            .UseFileName("TwoPassScanningGeneratedAspire");
    }

    [Fact]
    public void GeneratedCode_UsesSnakeCaseMethodNames()
    {
        // Verify that the generated Rust code uses snake_case for method names
        var atsContext = CreateContextFromBothAssemblies();

        var files = _generator.GenerateDistributedApplication(atsContext);
        var aspireRs = files["aspire.rs"];

        // Rust uses snake_case for methods
        Assert.Contains("add_container", aspireRs);
        Assert.Contains("with_environment", aspireRs);
        Assert.DoesNotContain("addContainer(", aspireRs);
        Assert.DoesNotContain("withEnvironment(", aspireRs);
    }

    [Fact]
    public void GeneratedCode_HasCreateBuilderFunction()
    {
        // Verify that the generated Rust code has a create_builder function
        var atsContext = CreateContextFromBothAssemblies();

        var files = _generator.GenerateDistributedApplication(atsContext);
        var aspireRs = files["aspire.rs"];

        Assert.Contains("pub fn create_builder", aspireRs);
    }

    [Fact]
    public void GeneratedCode_HasModRsFile()
    {
        // Verify that mod.rs file is generated
        var atsContext = CreateContextFromBothAssemblies();

        var files = _generator.GenerateDistributedApplication(atsContext);

        Assert.Contains("mod.rs", files.Keys);
        Assert.Contains("pub mod aspire", files["mod.rs"]);
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
}
