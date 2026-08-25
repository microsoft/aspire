// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RemoteHost;
using Aspire.TypeSystem;
using Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes;

namespace Aspire.Hosting.CodeGeneration.Java.Tests;

public class AtsJavaCodeGeneratorTests
{
    private readonly AtsJavaCodeGenerator _generator = new();

    // The test types are compiled into this assembly via Compile Include
    private const string TestTypesAssemblyName = "Aspire.Hosting.CodeGeneration.Java.Tests";

    [Fact]
    public void Language_ReturnsJava()
    {
        Assert.Equal("Java", _generator.Language);
    }

    [Fact]
    public async Task GenerateDistributedApplication_WithTestTypes_GeneratesCorrectOutput()
    {
        // Arrange
        var atsContext = CreateContextFromTestAssembly();

        // Act
        var files = _generator.GenerateDistributedApplication(atsContext);

        // Assert
        Assert.Contains("aspire/Aspire.java", files.Keys);
        Assert.Contains("aspire/AspireClient.java", files.Keys);
        Assert.Contains("aspire/HandleWrapperBase.java", files.Keys);
        Assert.Contains("aspire/TestRedisResource.java", files.Keys);
        Assert.Contains("sources.txt", files.Keys);

        await Verify(JoinGeneratedFiles(files), extension: "java")
            .UseFileName("AtsGeneratedAspire");
    }

    [Fact]
    public void GenerateDistributedApplication_DeclaresNumericParametersAsNumber()
    {
        var atsContext = CreateContextFromTestAssembly();

        var files = _generator.GenerateDistributedApplication(atsContext);
        var generated = JoinGeneratedFiles(files);

        // ATS collapses every numeric to one Number type, so a C# int parameter such as a port or an exit
        // code reaches the generator as a floating-point type. Java refuses to convert an int literal to a
        // Double - widening then boxing is not a conversion the language performs - so declaring these as
        // Double makes `targetPort(8080)` and `waitForCompletion(job, 0)` fail to compile with
        // "int cannot be converted to Double". java.lang.Number accepts int, long and double literals,
        // boxed values and null alike.
        // https://docs.oracle.com/javase/specs/jls/se21/html/jls-5.html#jls-5.3
        Assert.Contains("public TestRedisResource addTestRedis(String name, Number port)", generated, StringComparison.Ordinal);
        Assert.Contains("private Map<String, Number> counts;", generated, StringComparison.Ordinal);
        Assert.Contains("public Map<String, Number> getCounts() { return counts; }", generated, StringComparison.Ordinal);

        // Casting the deserialized map to Map<String, Double> was also simply wrong: a JSON integer
        // deserializes to Integer, so the first read of such an entry threw ClassCastException.
        Assert.Contains("value.setCounts((Map<String, Number>) countsValue);", generated, StringComparison.Ordinal);

        // Double.parseDouble in the hand-written JSON reader is the one legitimate use, so the check is
        // scoped to declarations rather than the whole text.
        Assert.Empty(Regex.Matches(generated, @"\bDouble\b(?!\.)"));
    }

    [Fact]
    public void GenerateDistributedApplication_WithTestTypes_IncludesExportedValues()
    {
        var atsContext = CreateContextFromTestAssembly();

        Assert.Contains(atsContext.ExportedValues, value => string.Join(".", value.PathSegments) == "TestConfigs.Default");
        Assert.Contains(atsContext.ExportedValues, value => string.Join(".", value.PathSegments) == "TestConfigs.Profiles.Development");

        var files = _generator.GenerateDistributedApplication(atsContext);
        var testConfigsJava = files["aspire/TestConfigs.java"];

        Assert.Contains("public final class TestConfigs", testConfigsJava);
        Assert.Contains("static final TestConfigDto Default", testConfigsJava);
        Assert.Contains("static final class Profiles", testConfigsJava);
        Assert.Contains("static final TestConfigDto Development", testConfigsJava);
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
    public void Scanner_HostingAssembly_FluentBuilderCapabilities_ReturnBuilder()
    {
        var capabilities = ScanCapabilitiesFromHostingAssembly();

        var withReference = Assert.Single(capabilities, c => c.CapabilityId == "Aspire.Hosting/withReference");
        Assert.True(withReference.ReturnsBuilder);

        var waitFor = Assert.Single(capabilities, c => c.CapabilityId == "Aspire.Hosting/waitFor");
        Assert.True(waitFor.ReturnsBuilder);
    }

    [Fact]
    public void GeneratedCode_HostingAssembly_FluentBuilderMethods_ReturnConcreteBuilderType()
    {
        var atsContext = CreateContextFromBothAssemblies();

        var files = _generator.GenerateDistributedApplication(atsContext);
        var containerResourceJava = files["aspire/ContainerResource.java"];

        Assert.Contains("public ContainerResource withReference(IResource source, WithReferenceOptions options)", containerResourceJava);
        Assert.Contains("public ContainerResource waitFor(IResource dependency)", containerResourceJava);
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
    public void TwoPassScanning_GeneratesDerivedResourceInheritance()
    {
        var atsContext = CreateContextFromBothAssemblies();

        var files = _generator.GenerateDistributedApplication(atsContext);
        var testRedisJava = files["aspire/TestRedisResource.java"];

        Assert.Contains("public class TestRedisResource extends ContainerResource", testRedisJava);
    }

    [Fact]
    public async Task TwoPassScanning_GeneratesWithEnvironmentOnTestRedisBuilder()
    {
        // End-to-end test: verify that withEnvironment appears on TestRedisResource
        // in the generated Java when using 2-pass scanning.
        var atsContext = CreateContextFromBothAssemblies();

        // Generate Java
        var files = _generator.GenerateDistributedApplication(atsContext);
        var testRedisJava = files["aspire/TestRedisResource.java"];

        // Verify withEnvironment appears (method should exist for resources that support it)
        Assert.Contains("withEnvironment", testRedisJava);

        // Snapshot for detailed verification
        await Verify(JoinGeneratedFiles(files), extension: "java")
            .UseFileName("TwoPassScanningGeneratedAspire");
    }

    [Fact]
    public void GeneratedCode_UsesCamelCaseMethodNames()
    {
        // Verify that the generated Java code uses camelCase for method names
        var atsContext = CreateContextFromBothAssemblies();

        var files = _generator.GenerateDistributedApplication(atsContext);
        var builderJava = files["aspire/IDistributedApplicationBuilder.java"];
        var testRedisJava = files["aspire/TestRedisResource.java"];

        // Java uses camelCase for methods
        Assert.Contains("addContainer", builderJava);
        Assert.Contains("withEnvironment", testRedisJava);
    }

    [Fact]
    public void GeneratedCode_HasCreateBuilderMethod()
    {
        // Verify that the generated Java code has a createBuilder method
        var atsContext = CreateContextFromBothAssemblies();

        var files = _generator.GenerateDistributedApplication(atsContext);
        var distributedApplicationJava = files["aspire/DistributedApplication.java"];

        Assert.Contains("createBuilder", distributedApplicationJava);
    }

    [Fact]
    public void GeneratedCode_HasPublicAspireClass()
    {
        // Verify that a public Aspire class is generated
        var atsContext = CreateContextFromBothAssemblies();

        var files = _generator.GenerateDistributedApplication(atsContext);
        var aspireJava = files["aspire/Aspire.java"];

        Assert.Contains("public class Aspire", aspireJava);
    }

    [Fact]
    public void GeneratedTransport_HandlesJsonRpcArrayCallbackParameters()
    {
        var atsContext = CreateContextFromBothAssemblies();

        var files = _generator.GenerateDistributedApplication(atsContext);
        var aspireClientJava = files["aspire/AspireClient.java"];

        Assert.Contains("private String getCallbackId(Object params)", aspireClientJava);
        Assert.Contains("if (params instanceof List<?> list && !list.isEmpty())", aspireClientJava);
        Assert.Contains("var key = \"p\" + i;", aspireClientJava);
    }

    [Fact]
    public void GeneratedDtoValues_AreSerializedAsMaps()
    {
        var atsContext = CreateContextFromTestAssembly();

        var files = _generator.GenerateDistributedApplication(atsContext);
        var aspireClientJava = files["aspire/AspireClient.java"];
        var testConfigDtoJava = files["aspire/TestConfigDto.java"];

        Assert.Contains("interface JsonSerializable", files["aspire/JsonSerializable.java"]);
        Assert.Contains("if (value instanceof JsonSerializable jsonSerializable)", aspireClientJava);
        Assert.Contains("public class TestConfigDto implements JsonSerializable", testConfigDtoJava);
    }

    [Fact]
    public void GeneratedCode_SuppressesWarningsOnEveryGeneratedType()
    {
        var atsContext = CreateContextFromBothAssemblies();

        var files = _generator.GenerateDistributedApplication(atsContext);

        // Every token is load-bearing, because the two compilers that see this code disagree on what
        // "all" covers. ECJ (which the Java language server, and so VS Code, compiles with) honours
        // "all" and never reports it back as unnecessary. javac ignores "all" for its -Xlint
        // categories, so "unchecked" and "serial" have to be named for `gradle build` and
        // `mvn compile` to stay quiet.
        var javaFiles = files.Where(kvp => kvp.Key.EndsWith(".java", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(javaFiles);

        var unsuppressed = javaFiles
            .Where(kvp => !kvp.Value.Contains("@SuppressWarnings({\"all\", \"unchecked\", \"serial\"})", StringComparison.Ordinal))
            .Select(kvp => kvp.Key)
            .ToList();

        Assert.Empty(unsuppressed);
    }

    [Fact]
    public void GeneratedCode_DoesNotEmitWildcardImports()
    {
        var atsContext = CreateContextFromBothAssemblies();

        var files = _generator.GenerateDistributedApplication(atsContext);

        // A wildcard import cannot be filtered per file, so it lands in all of the generated files and
        // is unused in nearly every one. That is what produced 300 "The import java.util is never
        // used" warnings in the user's Problems panel before imports became explicit.
        var withWildcards = files
            .Where(kvp => kvp.Key.EndsWith(".java", StringComparison.Ordinal))
            .Where(kvp => Regex.IsMatch(kvp.Value, @"^import\s+[\w.]+\.\*;", RegexOptions.Multiline))
            .Select(kvp => kvp.Key)
            .ToList();

        Assert.Empty(withWildcards);
    }

    [Fact]
    public void GeneratedCode_OnlyImportsTypesTheFileReferences()
    {
        var atsContext = CreateContextFromBothAssemblies();

        var files = _generator.GenerateDistributedApplication(atsContext);

        // The generator emits one large compilation unit and then splits it per top-level declaration.
        // Without per-file filtering the whole import block is copied into all ~226 files, so this
        // pins the filtering rather than the split.
        var offenders = new List<string>();

        foreach (var (path, content) in files.Where(kvp => kvp.Key.EndsWith(".java", StringComparison.Ordinal)))
        {
            foreach (Match import in Regex.Matches(content, @"^import\s+(?:static\s+)?([\w.]+)\.(\w+);", RegexOptions.Multiline))
            {
                var simpleName = import.Groups[2].Value;
                var body = content[(import.Index + import.Length)..];

                if (!Regex.IsMatch(body, $@"\b{Regex.Escape(simpleName)}\b"))
                {
                    offenders.Add($"{path}: {import.Groups[0].Value}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void GeneratedCode_RegistersCollectionWrappersWithoutRawTypes()
    {
        var atsContext = CreateContextFromBothAssemblies();

        var files = _generator.GenerateDistributedApplication(atsContext);
        var registrations = files["aspire/AspireRegistrations.java"];

        // The factory target is BiFunction<Handle, AspireClient, Object>, so a raw AspireList/AspireDict
        // here raises a rawtypes warning in the consumer's IDE. The diamond infers the erased-equivalent
        // element type instead.
        Assert.DoesNotContain("new AspireList(h, c)", registrations, StringComparison.Ordinal);
        Assert.DoesNotContain("new AspireDict(h, c)", registrations, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedCollectionWrappers_ExposeTheSameOperationsAsTheOtherLanguages()
    {
        var atsContext = CreateContextFromBothAssemblies();

        var files = _generator.GenerateDistributedApplication(atsContext);
        var aspireList = files["aspire/AspireList.java"];
        var aspireDict = files["aspire/AspireDict.java"];

        // The Go and TypeScript SDKs agree on this set, and the host exports every one of them from
        // Aspire.Hosting/Ats/CollectionExports.cs. Java shipped AspireList with no operations at all,
        // which made any list-valued property unusable from a Java AppHost.
        string[] listCapabilities =
        [
            "Aspire.Hosting/List.length",
            "Aspire.Hosting/List.get",
            "Aspire.Hosting/List.add",
            "Aspire.Hosting/List.removeAt",
            "Aspire.Hosting/List.clear",
            "Aspire.Hosting/List.toArray",
        ];

        string[] dictCapabilities =
        [
            "Aspire.Hosting/Dict.count",
            "Aspire.Hosting/Dict.get",
            "Aspire.Hosting/Dict.set",
            "Aspire.Hosting/Dict.remove",
            "Aspire.Hosting/Dict.has",
            "Aspire.Hosting/Dict.keys",
            "Aspire.Hosting/Dict.values",
            "Aspire.Hosting/Dict.clear",
            "Aspire.Hosting/Dict.toObject",
        ];

        Assert.All(listCapabilities, capability => Assert.Contains(capability, aspireList, StringComparison.Ordinal));
        Assert.All(dictCapabilities, capability => Assert.Contains(capability, aspireDict, StringComparison.Ordinal));
    }

    private static string JoinGeneratedFiles(Dictionary<string, string> files)
    {
        return string.Join(
            "\n",
            files
                .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
                .Select(kvp => $"// ===== {kvp.Key} =====\n{kvp.Value}"));
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

    [Theory]
    [InlineData("Default", "default_", "Default_")]
    [InlineData("Package", "package_", "Package_")]
    [InlineData("Class", "class_", "Class_")]
    [InlineData("Native", "native_", "Native_")]
    public void GenerateDistributedApplication_EscapesDtoPropertiesNamedAfterJavaKeywords(
        string propertyName,
        string expectedField,
        string expectedAccessorSuffix)
    {
        // A DTO property whose name lowercases to a Java keyword produces source that javac rejects
        // outright - `private String default;` is not parseable - so the generated SDK would fail to
        // compile the first time any integration exposes such a property. Rust escapes every generated
        // identifier through SanitizeIdentifier (r# prefix); Java only escaped type names, leaving
        // fields, accessors and parameters unescaped.
        //
        // `Class` additionally collides with the final java.lang.Object.getClass(), which cannot be
        // overridden, so the accessor name has to follow the escaped field rather than the raw property.
        var context = CreateContextWithSingleDtoProperty(propertyName);

        var generated = JoinGeneratedFiles(_generator.GenerateDistributedApplication(context));

        Assert.Contains($"private String {expectedField};", generated);
        Assert.Contains($"public String get{expectedAccessorSuffix}() {{ return {expectedField}; }}", generated);
        Assert.Contains($"public void set{expectedAccessorSuffix}(String value) {{ this.{expectedField} = value; }}", generated);

        // The transport key stays the original property name: escaping is a Java source concern and
        // must not change the shape of the wire payload the .NET host reads and writes.
        Assert.Contains($"map.get(\"{propertyName}\")", generated);
        Assert.Contains($"map.put(\"{propertyName}\", AspireClient.serializeValue({expectedField}))", generated);
    }

    [Fact]
    public void DtoPropertyWithDictionaryNestedInArrayCastsToTheFieldType()
    {
        // The field type comes from MapDtoPropertyTypeToJava (Map/List) while the fromMap cast used to
        // fall through to MapTypeRefToJava, which renders a mutable dictionary as AspireDict. AspireDict
        // extends HandleWrapperBase and does not implement Map, so javac rejected the generated SDK
        // outright — and the CLI compiles every generated source in one javac invocation, so a single
        // bad property broke `aspire run` for the whole Java AppHost.
        var context = CreateContextWithSingleDtoProperty(
            "MetadataArray",
            new AtsTypeRef { TypeId = "array", Category = AtsTypeCategory.Array, ElementType = MutableStringDict() });

        var generated = JoinGeneratedFiles(_generator.GenerateDistributedApplication(context));

        Assert.Contains("private Map<String, String>[] metadataArray;", generated);
        Assert.Contains("value.setMetadataArray((Map<String, String>[]) metadataArrayValue);", generated);
    }

    [Fact]
    public void DtoPropertyWithDictionaryNestedInListCastsToTheFieldType()
    {
        var context = CreateContextWithSingleDtoProperty(
            "Entries",
            new AtsTypeRef
            {
                TypeId = "list",
                Category = AtsTypeCategory.List,
                IsReadOnly = false,
                ElementType = MutableStringDict()
            });

        var generated = JoinGeneratedFiles(_generator.GenerateDistributedApplication(context));

        Assert.Contains("private List<Map<String, String>> entries;", generated);
        Assert.Contains("item0 -> (Map<String, String>) item0", generated);
    }

    [Fact]
    public void ExportedDtoValueInitializerCallsTheEscapedSetter()
    {
        // The DTO's setter is generated from the keyword-escaped field (setDefault_), but the exported
        // value initializer derived its call from the raw property name (setDefault). javac rejects the
        // mismatch with `cannot find symbol`, and because the CLI compiles the whole generated SDK in a
        // single javac invocation, one keyword-named property on any exported value breaks `aspire run`
        // for the entire Java AppHost - and the user cannot fix generated code.
        var context = CreateContextWithSingleDtoProperty("Default");
        var exported = new AtsExportedValueInfo
        {
            OwningAssemblyName = TestTypesAssemblyName,
            PathSegments = ["Probes", "Sample"],
            Value = new JsonObject { ["Default"] = "probe" },
            Type = new AtsTypeRef { TypeId = "KeywordProbe", Category = AtsTypeCategory.Dto }
        };
        context = new AtsContext
        {
            Capabilities = context.Capabilities,
            HandleTypes = context.HandleTypes,
            EnumTypes = context.EnumTypes,
            DtoTypes = context.DtoTypes,
            ExportedValues = [exported]
        };

        var generated = JoinGeneratedFiles(_generator.GenerateDistributedApplication(context));

        Assert.Contains("setDefault_(\"probe\")", generated);
    }

    private static AtsContext CreateContextWithSingleDtoProperty(string propertyName)
    {
        return CreateContextWithSingleDtoProperty(
            propertyName,
            new AtsTypeRef { TypeId = "string", Category = AtsTypeCategory.Primitive });
    }

    private static AtsContext CreateContextWithSingleDtoProperty(string propertyName, AtsTypeRef propertyType)
    {
        return new AtsContext
        {
            Capabilities = [],
            HandleTypes = [],
            EnumTypes = [],
            DtoTypes =
            [
                new AtsDtoTypeInfo
                {
                    Name = "KeywordProbe",
                    TypeId = "KeywordProbe",
                    Properties =
                    [
                        new AtsDtoPropertyInfo
                        {
                            Name = propertyName,
                            Type = propertyType
                        }
                    ]
                }
            ]
        };
    }

    private static AtsTypeRef MutableStringDict() => new()
    {
        TypeId = "dict",
        Category = AtsTypeCategory.Dict,
        IsReadOnly = false,
        KeyType = new AtsTypeRef { TypeId = "string", Category = AtsTypeCategory.Primitive },
        ValueType = new AtsTypeRef { TypeId = "string", Category = AtsTypeCategory.Primitive }
    };
}
