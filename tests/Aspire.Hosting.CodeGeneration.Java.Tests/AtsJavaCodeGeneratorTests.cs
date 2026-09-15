// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Diagnostics;
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

    [Fact]
    public async Task GeneratedTransport_ConcurrentCallsRouteReverseResponsesToTheCorrectCaller()
    {
        using var workspace = await CreateJavaProbeWorkspaceAsync();
        workspace.WriteSource(
            "aspire/TransportConcurrencyProbe.java",
            """
            package aspire;

            import java.io.ByteArrayOutputStream;
            import java.io.InputStream;
            import java.io.PipedInputStream;
            import java.io.PipedOutputStream;
            import java.lang.reflect.Field;
            import java.nio.charset.StandardCharsets;
            import java.util.Map;
            import java.util.concurrent.CompletableFuture;
            import java.util.concurrent.TimeUnit;

            public class TransportConcurrencyProbe {
                public static void main(String[] args) throws Exception {
                    var clientInput = new PipedInputStream(32768);
                    var serverOutput = new PipedOutputStream(clientInput);
                    var serverInput = new PipedInputStream(32768);
                    var clientOutput = new PipedOutputStream(serverInput);

                    var client = new AspireClient("ignored");
                    setField(client, "inputStream", clientInput);
                    setField(client, "outputStream", clientOutput);

                    var first = CompletableFuture.supplyAsync(() -> client.invokeCapability("cap.first", Map.of()));
                    var second = CompletableFuture.supplyAsync(() -> client.invokeCapability("cap.second", Map.of()));

                    var firstRequest = readMessage(serverInput);
                    var secondRequest = readMessage(serverInput);

                    int firstId = extractIdForCapability(firstRequest, secondRequest, "cap.first");
                    int secondId = extractIdForCapability(firstRequest, secondRequest, "cap.second");

                    writeResponse(serverOutput, secondId, "second-result");
                    writeResponse(serverOutput, firstId, "first-result");

                    var firstResult = first.get(2, TimeUnit.SECONDS);
                    var secondResult = second.get(2, TimeUnit.SECONDS);

                    if (!"first-result".equals(firstResult)) {
                        throw new IllegalStateException("first call received wrong result: " + firstResult);
                    }
                    if (!"second-result".equals(secondResult)) {
                        throw new IllegalStateException("second call received wrong result: " + secondResult);
                    }

                    System.out.println("OK");
                }

                private static void setField(AspireClient client, String name, Object value) throws Exception {
                    Field field = AspireClient.class.getDeclaredField(name);
                    field.setAccessible(true);
                    field.set(client, value);
                }

                private static int extractId(String json) {
                    int marker = json.indexOf("\"id\":");
                    if (marker < 0) {
                        throw new IllegalStateException("No request id in payload: " + json);
                    }

                    int start = marker + 5;
                    int end = start;
                    while (end < json.length() && Character.isDigit(json.charAt(end))) {
                        end++;
                    }

                    if (end == start) {
                        throw new IllegalStateException("Request id was not numeric: " + json);
                    }

                    return Integer.parseInt(json.substring(start, end));
                }

                private static int extractIdForCapability(String firstRequest, String secondRequest, String capabilityId) {
                    String marker = "\"capabilityId\":\"" + capabilityId + "\"";
                    if (firstRequest.contains(marker)) {
                        return extractId(firstRequest);
                    }
                    if (secondRequest.contains(marker)) {
                        return extractId(secondRequest);
                    }
                    throw new IllegalStateException("No request found for capability " + capabilityId);
                }

                private static void writeResponse(PipedOutputStream output, int id, String result) throws Exception {
                    String payload = "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":\"" + result + "\"}";
                    byte[] body = payload.getBytes(StandardCharsets.UTF_8);
                    String header = "Content-Length: " + body.length + "\r\n\r\n";
                    output.write(header.getBytes(StandardCharsets.UTF_8));
                    output.write(body);
                    output.flush();
                }

                private static String readMessage(InputStream input) throws Exception {
                    int contentLength = -1;
                    while (true) {
                        String line = readLine(input);
                        if (line.isEmpty()) {
                            break;
                        }
                        if (line.startsWith("Content-Length:")) {
                            contentLength = Integer.parseInt(line.substring(15).trim());
                        }
                    }

                    if (contentLength < 0) {
                        throw new IllegalStateException("Missing Content-Length header");
                    }

                    byte[] body = input.readNBytes(contentLength);
                    return new String(body, StandardCharsets.UTF_8);
                }

                private static String readLine(InputStream input) throws Exception {
                    ByteArrayOutputStream buffer = new ByteArrayOutputStream();
                    while (true) {
                        int ch = input.read();
                        if (ch == -1) {
                            break;
                        }
                        if (ch == '\r') {
                            int next = input.read();
                            if (next == '\n') {
                                break;
                            }
                            buffer.write(ch);
                            if (next != -1) {
                                buffer.write(next);
                            }
                            continue;
                        }
                        if (ch == '\n') {
                            break;
                        }
                        buffer.write(ch);
                    }

                    return buffer.toString(StandardCharsets.UTF_8);
                }
            }
            """);

        await workspace.CompileAsync();
        var run = await workspace.RunClassAsync("aspire.TransportConcurrencyProbe", TimeSpan.FromSeconds(6));

        Assert.True(run.TimedOut is false, $"Probe timed out. stdout:{Environment.NewLine}{run.StdOut}{Environment.NewLine}stderr:{Environment.NewLine}{run.StdErr}");
        Assert.True(
            run.ExitCode == 0,
            $"Probe failed with exit code {run.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{run.StdOut}{Environment.NewLine}stderr:{Environment.NewLine}{run.StdErr}");
        Assert.Contains("OK", run.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratedTransport_CallbackCanInvokeNestedCapability()
    {
        using var workspace = await CreateJavaProbeWorkspaceAsync();
        workspace.WriteSource(
            "aspire/TransportReentrancyProbe.java",
            """
            package aspire;

            import java.io.ByteArrayOutputStream;
            import java.io.InputStream;
            import java.io.PipedInputStream;
            import java.io.PipedOutputStream;
            import java.lang.reflect.Field;
            import java.lang.reflect.Method;
            import java.nio.charset.StandardCharsets;
            import java.util.Map;
            import java.util.concurrent.CompletableFuture;
            import java.util.concurrent.ConcurrentHashMap;
            import java.util.concurrent.CountDownLatch;
            import java.util.concurrent.TimeUnit;
            import java.util.concurrent.atomic.AtomicInteger;
            import java.util.function.BiFunction;
            import java.util.function.Function;

            public class TransportReentrancyProbe {
                public static void main(String[] args) throws Exception {
                    var clientInput = new PipedInputStream(32768);
                    var serverOutput = new PipedOutputStream(clientInput);
                    var serverInput = new PipedInputStream(32768);
                    var clientOutput = new PipedOutputStream(serverInput);

                    var client = new AspireClient("ignored");
                    setField(client, "inputStream", clientInput);
                    setField(client, "outputStream", clientOutput);

                    String callbackId = client.registerCallback(callbackArgs ->
                        client.invokeCapability("cap.nested", Map.of()));
                    startReader(client);

                    writeMessage(
                        serverOutput,
                        "{\"jsonrpc\":\"2.0\",\"id\":9001,\"method\":\"invokeCallback\",\"params\":{\"callbackId\":\""
                            + callbackId
                            + "\",\"args\":[]}}");

                    String nestedRequest = readMessage(serverInput);
                    int nestedId = extractId(nestedRequest);
                    if (!nestedRequest.contains("\"capabilityId\":\"cap.nested\"")) {
                        throw new IllegalStateException("unexpected nested request: " + nestedRequest);
                    }

                    writeMessage(
                        serverOutput,
                        "{\"jsonrpc\":\"2.0\",\"id\":" + nestedId + ",\"result\":\"nested-result\"}");

                    String callbackResponse = readMessage(serverInput);
                    if (!callbackResponse.contains("\"id\":9001")
                        || !callbackResponse.contains("\"result\":\"nested-result\"")) {
                        throw new IllegalStateException("unexpected callback response: " + callbackResponse);
                    }

                    System.out.println("OK");
                }

                private static void setField(AspireClient client, String name, Object value) throws Exception {
                    Field field = AspireClient.class.getDeclaredField(name);
                    field.setAccessible(true);
                    field.set(client, value);
                }

                private static void startReader(AspireClient client) throws Exception {
                    Method method = AspireClient.class.getDeclaredMethod("ensureReaderLoopStarted");
                    method.setAccessible(true);
                    method.invoke(client);
                }

                private static int extractId(String json) {
                    int marker = json.indexOf("\"id\":");
                    int start = marker + 5;
                    int end = start;
                    while (end < json.length() && Character.isDigit(json.charAt(end))) {
                        end++;
                    }
                    return Integer.parseInt(json.substring(start, end));
                }

                private static void writeMessage(PipedOutputStream output, String payload) throws Exception {
                    byte[] body = payload.getBytes(StandardCharsets.UTF_8);
                    String header = "Content-Length: " + body.length + "\r\n\r\n";
                    output.write(header.getBytes(StandardCharsets.UTF_8));
                    output.write(body);
                    output.flush();
                }

                private static String readMessage(InputStream input) throws Exception {
                    int contentLength = -1;
                    while (true) {
                        String line = readLine(input);
                        if (line.isEmpty()) {
                            break;
                        }
                        if (line.startsWith("Content-Length:")) {
                            contentLength = Integer.parseInt(line.substring(15).trim());
                        }
                    }

                    if (contentLength < 0) {
                        throw new IllegalStateException("Missing Content-Length header");
                    }

                    return new String(input.readNBytes(contentLength), StandardCharsets.UTF_8);
                }

                private static String readLine(InputStream input) throws Exception {
                    ByteArrayOutputStream buffer = new ByteArrayOutputStream();
                    while (true) {
                        int ch = input.read();
                        if (ch == -1) {
                            break;
                        }
                        if (ch == '\r') {
                            int next = input.read();
                            if (next == '\n') {
                                break;
                            }
                            buffer.write(ch);
                            if (next != -1) {
                                buffer.write(next);
                            }
                            continue;
                        }
                        if (ch == '\n') {
                            break;
                        }
                        buffer.write(ch);
                    }
                    return buffer.toString(StandardCharsets.UTF_8);
                }
            }
            """);

        await workspace.CompileAsync();
        var run = await workspace.RunClassAsync("aspire.TransportReentrancyProbe", TimeSpan.FromSeconds(6));

        Assert.True(run.TimedOut is false, $"Probe timed out. stdout:{Environment.NewLine}{run.StdOut}{Environment.NewLine}stderr:{Environment.NewLine}{run.StdErr}");
        Assert.True(
            run.ExitCode == 0,
            $"Probe failed with exit code {run.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{run.StdOut}{Environment.NewLine}stderr:{Environment.NewLine}{run.StdErr}");
        Assert.Contains("OK", run.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratedTransport_MalformedResponseFailsPendingRequest()
    {
        using var workspace = await CreateJavaProbeWorkspaceAsync();
        workspace.WriteSource(
            "aspire/TransportMalformedResponseProbe.java",
            """
            package aspire;

            import java.io.ByteArrayOutputStream;
            import java.io.InputStream;
            import java.io.PipedInputStream;
            import java.io.PipedOutputStream;
            import java.lang.reflect.Field;
            import java.nio.charset.StandardCharsets;
            import java.util.Map;
            import java.util.concurrent.CompletableFuture;
            import java.util.concurrent.ExecutionException;
            import java.util.concurrent.TimeUnit;

            public class TransportMalformedResponseProbe {
                public static void main(String[] args) throws Exception {
                    assertPayloadFails("{not-json}");
                    assertPayloadFails("{\"jsonrpc\":\"2.0\",\"result\":42}");
                    assertPayloadFails("{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":42}");
                    assertPayloadFails("{\"jsonrpc\":\"2.0\",\"id\":1.5,\"result\":42}");
                    assertPayloadFails("{\"jsonrpc\":\"2.0\",\"id\":1.0000000000000001,\"result\":42}");
                    assertPayloadFails("{\"jsonrpc\":\"2.0\",\"id\":4294967297,\"result\":42}");
                    System.out.println("OK");
                }

                private static void assertPayloadFails(String payload) throws Exception {
                    var clientInput = new PipedInputStream(32768);
                    var serverOutput = new PipedOutputStream(clientInput);
                    var serverInput = new PipedInputStream(32768);
                    var clientOutput = new PipedOutputStream(serverInput);

                    var client = new AspireClient("ignored");
                    setField(client, "inputStream", clientInput);
                    setField(client, "outputStream", clientOutput);

                    var pending = CompletableFuture.supplyAsync(() ->
                        client.invokeCapability("cap.malformed", Map.of()));
                    readMessage(serverInput);
                    writeMessage(serverOutput, payload);

                    try {
                        pending.get(2, TimeUnit.SECONDS);
                        throw new IllegalStateException("malformed response unexpectedly succeeded");
                    } catch (ExecutionException expected) {
                        if (!String.valueOf(expected.getCause().getMessage()).contains("Disconnected from AppHost")) {
                            throw new IllegalStateException("unexpected failure", expected);
                        }
                    }
                }

                private static void setField(AspireClient client, String name, Object value) throws Exception {
                    Field field = AspireClient.class.getDeclaredField(name);
                    field.setAccessible(true);
                    field.set(client, value);
                }

                private static void writeMessage(PipedOutputStream output, String payload) throws Exception {
                    byte[] body = payload.getBytes(StandardCharsets.UTF_8);
                    String header = "Content-Length: " + body.length + "\r\n\r\n";
                    output.write(header.getBytes(StandardCharsets.UTF_8));
                    output.write(body);
                    output.flush();
                }

                private static String readMessage(InputStream input) throws Exception {
                    int contentLength = -1;
                    while (true) {
                        String line = readLine(input);
                        if (line.isEmpty()) {
                            break;
                        }
                        if (line.startsWith("Content-Length:")) {
                            contentLength = Integer.parseInt(line.substring(15).trim());
                        }
                    }
                    return new String(input.readNBytes(contentLength), StandardCharsets.UTF_8);
                }

                private static String readLine(InputStream input) throws Exception {
                    ByteArrayOutputStream buffer = new ByteArrayOutputStream();
                    while (true) {
                        int ch = input.read();
                        if (ch == -1) {
                            break;
                        }
                        if (ch == '\r') {
                            int next = input.read();
                            if (next == '\n') {
                                break;
                            }
                            buffer.write(ch);
                            if (next != -1) {
                                buffer.write(next);
                            }
                            continue;
                        }
                        if (ch == '\n') {
                            break;
                        }
                        buffer.write(ch);
                    }
                    return buffer.toString(StandardCharsets.UTF_8);
                }
            }
            """);

        await workspace.CompileAsync();
        var run = await workspace.RunClassAsync("aspire.TransportMalformedResponseProbe", TimeSpan.FromSeconds(6));

        Assert.True(run.TimedOut is false, $"Probe timed out. stdout:{Environment.NewLine}{run.StdOut}{Environment.NewLine}stderr:{Environment.NewLine}{run.StdErr}");
        Assert.True(
            run.ExitCode == 0,
            $"Probe failed with exit code {run.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{run.StdOut}{Environment.NewLine}stderr:{Environment.NewLine}{run.StdErr}");
        Assert.Contains("OK", run.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratedTransport_DisconnectCannotRacePastRequestRegistration()
    {
        using var workspace = await CreateJavaProbeWorkspaceAsync();
        workspace.WriteSource(
            "aspire/TransportDisconnectRaceProbe.java",
            """
            package aspire;

            import java.io.ByteArrayOutputStream;
            import java.io.PipedInputStream;
            import java.io.PipedOutputStream;
            import java.lang.reflect.Field;
            import java.lang.reflect.Method;
            import java.util.Map;
            import java.util.concurrent.CompletableFuture;
            import java.util.concurrent.ConcurrentHashMap;
            import java.util.concurrent.CountDownLatch;
            import java.util.concurrent.ExecutionException;
            import java.util.concurrent.TimeUnit;

            public class TransportDisconnectRaceProbe {
                public static void main(String[] args) throws Exception {
                    var clientInput = new PipedInputStream(32768);
                    var serverOutput = new PipedOutputStream(clientInput);
                    var client = new AspireClient("ignored");
                    setField(client, "inputStream", clientInput);
                    setField(client, "outputStream", new ByteArrayOutputStream());
                    setField(client, "pendingRequests", new DisconnectingMap(client));

                    var pending = CompletableFuture.supplyAsync(() ->
                        client.invokeCapability("cap.race", Map.of()));

                    try {
                        pending.get(2, TimeUnit.SECONDS);
                        throw new IllegalStateException("request unexpectedly succeeded");
                    } catch (ExecutionException expected) {
                        if (!String.valueOf(expected.getCause().getMessage()).contains("Disconnected from AppHost")) {
                            throw new IllegalStateException("unexpected failure", expected);
                        }
                    }

                    serverOutput.close();
                    System.out.println("OK");
                }

                private static void setField(AspireClient client, String name, Object value) throws Exception {
                    Field field = AspireClient.class.getDeclaredField(name);
                    field.setAccessible(true);
                    field.set(client, value);
                }

                private static final class DisconnectingMap extends ConcurrentHashMap<Integer, CompletableFuture<Object>> {
                    private final AspireClient client;
                    private boolean scheduled;

                    DisconnectingMap(AspireClient client) {
                        this.client = client;
                    }

                    @Override
                    public CompletableFuture<Object> put(Integer id, CompletableFuture<Object> response) {
                        if (!scheduled) {
                            scheduled = true;
                            CountDownLatch started = new CountDownLatch(1);
                            Thread disconnect = new Thread(() -> {
                                started.countDown();
                                invokeDisconnect(client);
                            });
                            disconnect.start();

                            try {
                                if (!started.await(1, TimeUnit.SECONDS)) {
                                    throw new IllegalStateException("disconnect thread did not start");
                                }
                                disconnect.join(250);
                            } catch (InterruptedException e) {
                                throw new RuntimeException(e);
                            }
                        }

                        return super.put(id, response);
                    }

                    private static void invokeDisconnect(AspireClient client) {
                        try {
                            Method method = AspireClient.class.getDeclaredMethod("handleDisconnect");
                            method.setAccessible(true);
                            method.invoke(client);
                        } catch (ReflectiveOperationException e) {
                            throw new RuntimeException(e);
                        }
                    }
                }
            }
            """);

        await workspace.CompileAsync();
        var run = await workspace.RunClassAsync("aspire.TransportDisconnectRaceProbe", TimeSpan.FromSeconds(6));

        Assert.True(run.TimedOut is false, $"Probe timed out. stdout:{Environment.NewLine}{run.StdOut}{Environment.NewLine}stderr:{Environment.NewLine}{run.StdErr}");
        Assert.True(
            run.ExitCode == 0,
            $"Probe failed with exit code {run.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{run.StdOut}{Environment.NewLine}stderr:{Environment.NewLine}{run.StdErr}");
        Assert.Contains("OK", run.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratedTransport_LateDisconnectHandlerStillRuns()
    {
        using var workspace = await CreateJavaProbeWorkspaceAsync();
        workspace.WriteSource(
            "aspire/TransportLateDisconnectHandlerProbe.java",
            """
            package aspire;

            import java.lang.reflect.Method;
            import java.util.concurrent.atomic.AtomicInteger;

            public class TransportLateDisconnectHandlerProbe {
                public static void main(String[] args) throws Exception {
                    var client = new AspireClient("ignored");
                    Method disconnect = AspireClient.class.getDeclaredMethod("handleDisconnect");
                    disconnect.setAccessible(true);
                    disconnect.invoke(client);

                    var calls = new AtomicInteger();
                    client.onDisconnect(calls::incrementAndGet);
                    if (calls.get() != 1) {
                        throw new IllegalStateException("late disconnect handler was not invoked");
                    }

                    System.out.println("OK");
                }
            }
            """);

        await workspace.CompileAsync();
        var run = await workspace.RunClassAsync("aspire.TransportLateDisconnectHandlerProbe", TimeSpan.FromSeconds(6));

        Assert.True(run.TimedOut is false, $"Probe timed out. stdout:{Environment.NewLine}{run.StdOut}{Environment.NewLine}stderr:{Environment.NewLine}{run.StdErr}");
        Assert.True(
            run.ExitCode == 0,
            $"Probe failed with exit code {run.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{run.StdOut}{Environment.NewLine}stderr:{Environment.NewLine}{run.StdErr}");
        Assert.Contains("OK", run.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratedTransport_DisconnectContinuesAfterCancellationListenerFailure()
    {
        using var workspace = await CreateJavaProbeWorkspaceAsync();
        workspace.WriteSource(
            "aspire/TransportDisconnectCancellationFailureProbe.java",
            """
            package aspire;

            import java.lang.reflect.Field;
            import java.lang.reflect.Method;
            import java.util.Map;
            import java.util.concurrent.atomic.AtomicInteger;

            public class TransportDisconnectCancellationFailureProbe {
                @SuppressWarnings("unchecked")
                public static void main(String[] args) throws Exception {
                    var client = new AspireClient("ignored");
                    var cancellationCalls = new AtomicInteger();
                    var disconnectCalls = new AtomicInteger();
                    client.onDisconnect(disconnectCalls::incrementAndGet);

                    var firstToken = createThrowingToken(client, "first", cancellationCalls);
                    var secondToken = createThrowingToken(client, "second", cancellationCalls);

                    Field tokensField = AspireClient.class.getDeclaredField("remoteCancellationTokens");
                    tokensField.setAccessible(true);
                    Map<String, CancellationToken> tokens =
                        (Map<String, CancellationToken>) tokensField.get(client);
                    tokens.put("first", firstToken);
                    tokens.put("second", secondToken);

                    Method disconnect = AspireClient.class.getDeclaredMethod("handleDisconnect");
                    disconnect.setAccessible(true);
                    disconnect.invoke(client);

                    if (cancellationCalls.get() != 2
                        || disconnectCalls.get() != 1
                        || !firstToken.isCancelled()
                        || !secondToken.isCancelled()) {
                        throw new IllegalStateException(
                            "disconnect propagation was interrupted: cancellationCalls="
                                + cancellationCalls.get()
                                + ", disconnectCalls=" + disconnectCalls.get()
                                + ", firstCancelled=" + firstToken.isCancelled()
                                + ", secondCancelled=" + secondToken.isCancelled());
                    }

                    System.out.println("OK");
                }

                private static CancellationToken createThrowingToken(
                    AspireClient client,
                    String id,
                    AtomicInteger calls) {
                    var token = new CancellationToken(id, client);
                    token.onCancel(() -> {
                        calls.incrementAndGet();
                        throw new IllegalStateException("expected listener failure for " + id);
                    });
                    return token;
                }
            }
            """);

        await workspace.CompileAsync();
        var run = await workspace.RunClassAsync("aspire.TransportDisconnectCancellationFailureProbe", TimeSpan.FromSeconds(6));

        Assert.True(run.TimedOut is false, $"Probe timed out. stdout:{Environment.NewLine}{run.StdOut}{Environment.NewLine}stderr:{Environment.NewLine}{run.StdErr}");
        Assert.True(
            run.ExitCode == 0,
            $"Probe failed with exit code {run.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{run.StdOut}{Environment.NewLine}stderr:{Environment.NewLine}{run.StdErr}");
        Assert.Contains("OK", run.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratedTransport_CancellationTokenApi_IsAccessibleFromExternalPackage()
    {
        using var workspace = await CreateJavaProbeWorkspaceAsync();
        workspace.WriteSource(
            "external/CancellationApiProbe.java",
            """
            package external;

            import aspire.CancellationToken;
            import java.util.concurrent.atomic.AtomicInteger;

            public class CancellationApiProbe {
                public static void main(String[] args) {
                    CancellationToken token = new CancellationToken();
                    var calls = new AtomicInteger();
                    token.onCancel(calls::incrementAndGet);

                    if (token.isCancelled()) {
                        throw new IllegalStateException("token should start active");
                    }

                    token.cancel();

                    if (!token.isCancelled()) {
                        throw new IllegalStateException("token should be cancelled");
                    }

                    token.cancel();
                    if (calls.get() != 1) {
                        throw new IllegalStateException("cancel listener ran more than once: " + calls.get());
                    }

                    token.onCancel(calls::incrementAndGet);
                    if (calls.get() != 2) {
                        throw new IllegalStateException("late cancel listener did not run exactly once: " + calls.get());
                    }

                    System.out.println("OK");
                }
            }
            """);

        await workspace.CompileAsync();
        var run = await workspace.RunClassAsync("external.CancellationApiProbe", TimeSpan.FromSeconds(6));

        Assert.True(run.TimedOut is false, $"Probe timed out. stdout:{Environment.NewLine}{run.StdOut}{Environment.NewLine}stderr:{Environment.NewLine}{run.StdErr}");
        Assert.True(
            run.ExitCode == 0,
            $"Probe failed with exit code {run.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{run.StdOut}{Environment.NewLine}stderr:{Environment.NewLine}{run.StdErr}");
        Assert.Contains("OK", run.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratedTransport_CancelsStreamJsonRpcCallbackRequest()
    {
        using var workspace = await CreateJavaProbeWorkspaceAsync();
        workspace.WriteSource(
            "aspire/StreamJsonRpcCallbackCancellationProbe.java",
            """
            package aspire;

            import java.io.ByteArrayOutputStream;
            import java.io.InputStream;
            import java.io.PipedInputStream;
            import java.io.PipedOutputStream;
            import java.lang.reflect.Field;
            import java.lang.reflect.Method;
            import java.nio.charset.StandardCharsets;
            import java.util.Map;
            import java.util.concurrent.CompletableFuture;
            import java.util.concurrent.TimeUnit;

            public class StreamJsonRpcCallbackCancellationProbe {
                public static void main(String[] args) throws Exception {
                    var clientInput = new PipedInputStream(32768);
                    var serverOutput = new PipedOutputStream(clientInput);
                    var serverInput = new PipedInputStream(32768);
                    var clientOutput = new PipedOutputStream(serverInput);
                    var client = new AspireClient("ignored");
                    setField(client, "inputStream", clientInput);
                    setField(client, "outputStream", clientOutput);
                    startReader(client);

                    var callbackToken = new CompletableFuture<CancellationToken>();
                    var callbackCompletion = new CompletableFuture<Void>();
                    String callbackId = client.registerCallback(callbackArgs -> {
                        CancellationToken token = CancellationToken.fromValue(callbackArgs[0]);
                        token.onCancel(() -> callbackCompletion.complete(null));
                        callbackToken.complete(token);
                        return callbackCompletion;
                    });

                    writeMessage(
                        serverOutput,
                        "{\"jsonrpc\":\"2.0\",\"id\":\"41\",\"method\":\"invokeCallback\","
                            + "\"params\":{\"callbackId\":\"" + callbackId + "\","
                            + "\"args\":{\"p0\":\"callback-ct\",\"$cancellationToken\":\"callback-ct\"}}}");
                    CancellationToken token = callbackToken.get(1, TimeUnit.SECONDS);

                    // StreamJsonRpc sends request cancellation as this notification shape. A numeric
                    // id must not match the callback request's string id.
                    writeMessage(
                        serverOutput,
                        "{\"jsonrpc\":\"2.0\",\"method\":\"$/cancelRequest\",\"params\":{\"id\":41}}");
                    String numericCancellationOutput = waitForMessage(serverInput, 250);
                    if (numericCancellationOutput != null || token.isCancelled()) {
                        throw new IllegalStateException(
                            "numeric cancellation matched a string request id: " + numericCancellationOutput);
                    }

                    writeMessage(
                        serverOutput,
                        "{\"jsonrpc\":\"2.0\",\"method\":\"$/cancelRequest\",\"params\":{\"id\":\"41\"}}");
                    String callbackResponse = requireMessage(serverInput);
                    if (!token.isCancelled()
                        || !callbackResponse.contains("\"id\":\"41\"")
                        || !callbackResponse.contains("\"result\":null")
                        || callbackResponse.contains("\"error\"")) {
                        throw new IllegalStateException("unexpected callback cancellation response: " + callbackResponse);
                    }

                    awaitNoActiveCallbackRequests(client);
                    String notificationResponse = waitForMessage(serverInput, 250);
                    if (notificationResponse != null) {
                        throw new IllegalStateException(
                            "$/cancelRequest notification received a response: " + notificationResponse);
                    }

                    System.out.println("OK");
                }

                private static void awaitNoActiveCallbackRequests(AspireClient client) throws Exception {
                    Field activeRequestsField = AspireClient.class.getDeclaredField("activeCallbackRequests");
                    activeRequestsField.setAccessible(true);
                    Map<?, ?> activeRequests = (Map<?, ?>) activeRequestsField.get(client);
                    long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(1);
                    while (!activeRequests.isEmpty() && System.nanoTime() < deadline) {
                        TimeUnit.MILLISECONDS.sleep(5);
                    }
                    if (!activeRequests.isEmpty()) {
                        throw new IllegalStateException("completed callback retained request ids: " + activeRequests);
                    }
                }

                private static void setField(AspireClient client, String name, Object value) throws Exception {
                    Field field = AspireClient.class.getDeclaredField(name);
                    field.setAccessible(true);
                    field.set(client, value);
                }

                private static void startReader(AspireClient client) throws Exception {
                    Method method = AspireClient.class.getDeclaredMethod("ensureReaderLoopStarted");
                    method.setAccessible(true);
                    method.invoke(client);
                }

                private static void writeMessage(PipedOutputStream output, String payload) throws Exception {
                    byte[] body = payload.getBytes(StandardCharsets.UTF_8);
                    output.write(("Content-Length: " + body.length + "\r\n\r\n").getBytes(StandardCharsets.UTF_8));
                    output.write(body);
                    output.flush();
                }

                private static String waitForMessage(InputStream input, long timeoutMs) throws Exception {
                    long deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(timeoutMs);
                    while (System.nanoTime() < deadline) {
                        if (input.available() > 0) {
                            return readMessage(input);
                        }
                        TimeUnit.MILLISECONDS.sleep(5);
                    }
                    return null;
                }

                private static String requireMessage(InputStream input) throws Exception {
                    String message = waitForMessage(input, 1000);
                    if (message == null) {
                        throw new IllegalStateException("Timed out waiting for callback response");
                    }
                    return message;
                }

                private static String readMessage(InputStream input) throws Exception {
                    int contentLength = -1;
                    while (true) {
                        String line = readLine(input);
                        if (line.isEmpty()) {
                            break;
                        }
                        if (line.startsWith("Content-Length:")) {
                            contentLength = Integer.parseInt(line.substring(15).trim());
                        }
                    }
                    if (contentLength < 0) {
                        throw new IllegalStateException("Missing Content-Length header");
                    }
                    return new String(input.readNBytes(contentLength), StandardCharsets.UTF_8);
                }

                private static String readLine(InputStream input) throws Exception {
                    ByteArrayOutputStream buffer = new ByteArrayOutputStream();
                    while (true) {
                        int ch = input.read();
                        if (ch == '\r') {
                            if (input.read() == '\n') {
                                break;
                            }
                        } else if (ch == '\n' || ch == -1) {
                            break;
                        } else {
                            buffer.write(ch);
                        }
                    }
                    return buffer.toString(StandardCharsets.UTF_8);
                }
            }
            """);

        await workspace.CompileAsync();
        var run = await workspace.RunClassAsync("aspire.StreamJsonRpcCallbackCancellationProbe", TimeSpan.FromSeconds(6));

        Assert.True(run.TimedOut is false, $"Probe timed out. stdout:{Environment.NewLine}{run.StdOut}{Environment.NewLine}stderr:{Environment.NewLine}{run.StdErr}");
        Assert.True(
            run.ExitCode == 0,
            $"Probe failed with exit code {run.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{run.StdOut}{Environment.NewLine}stderr:{Environment.NewLine}{run.StdErr}");
        Assert.Contains("OK", run.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratedTransport_CancelListenerCanReenterClient()
    {
        using var workspace = await CreateJavaProbeWorkspaceAsync();
        workspace.WriteSource(
            "aspire/CancelListenerReentrancyProbe.java",
            """
            package aspire;

            import java.io.ByteArrayOutputStream;
            import java.io.InputStream;
            import java.io.PipedInputStream;
            import java.io.PipedOutputStream;
            import java.lang.reflect.Field;
            import java.lang.reflect.Method;
            import java.nio.charset.StandardCharsets;
            import java.util.Map;
            import java.util.concurrent.CompletableFuture;
            import java.util.concurrent.TimeUnit;
            import java.util.concurrent.atomic.AtomicBoolean;
            import java.util.concurrent.atomic.AtomicInteger;

            public class CancelListenerReentrancyProbe {
                public static void main(String[] args) throws Exception {
                    var clientInput = new PipedInputStream(32768);
                    var serverOutput = new PipedOutputStream(clientInput);
                    var serverInput = new PipedInputStream(32768);
                    var clientOutput = new PipedOutputStream(serverInput);
                    var client = new AspireClient("ignored");
                    setField(client, "inputStream", clientInput);
                    setField(client, "outputStream", clientOutput);

                    var disconnected = new AtomicBoolean();
                    client.onDisconnect(() -> disconnected.set(true));
                    startReader(client);

                    var callbackToken = new CompletableFuture<CancellationToken>();
                    var callbackCompletion = new CompletableFuture<Void>();
                    var throwingListenerCalls = new AtomicInteger();
                    var workingListenerCalls = new AtomicInteger();
                    String callbackId = client.registerCallback(callbackArgs -> {
                        CancellationToken token = CancellationToken.fromValue(callbackArgs[0]);
                        token.onCancel(() -> {
                            throwingListenerCalls.incrementAndGet();
                            throw new IllegalStateException("expected listener failure");
                        });
                        token.onCancel(() -> {
                            Object nestedResult = client.invokeCapability("cap.cancel-listener", Map.of());
                            if (!"nested-result".equals(nestedResult)) {
                                throw new IllegalStateException("unexpected nested result: " + nestedResult);
                            }
                            workingListenerCalls.incrementAndGet();
                            callbackCompletion.complete(null);
                        });
                        callbackToken.complete(token);
                        return callbackCompletion;
                    });

                    writeMessage(
                        serverOutput,
                        "{\"jsonrpc\":\"2.0\",\"id\":\"reentrant-callback\",\"method\":\"invokeCallback\","
                            + "\"params\":{\"callbackId\":\"" + callbackId + "\","
                            + "\"args\":{\"p0\":\"callback-ct\",\"$cancellationToken\":\"callback-ct\"}}}");
                    CancellationToken token = callbackToken.get(1, TimeUnit.SECONDS);

                    writeMessage(
                        serverOutput,
                        "{\"jsonrpc\":\"2.0\",\"method\":\"$/cancelRequest\","
                            + "\"params\":{\"id\":\"reentrant-callback\"}}");

                    String nestedRequest = requireMessage(serverInput, "nested capability request");
                    if (!nestedRequest.contains("\"method\":\"invokeCapability\"")
                        || !nestedRequest.contains("\"capabilityId\":\"cap.cancel-listener\"")) {
                        throw new IllegalStateException("unexpected nested request: " + nestedRequest);
                    }
                    writeMessage(
                        serverOutput,
                        "{\"jsonrpc\":\"2.0\",\"id\":" + extractNumericId(nestedRequest)
                            + ",\"result\":\"nested-result\"}");

                    String callbackResponse = requireMessage(serverInput, "callback response");
                    if (!token.isCancelled()
                        || !callbackCompletion.isDone()
                        || throwingListenerCalls.get() != 1
                        || workingListenerCalls.get() != 1
                        || disconnected.get()) {
                        throw new IllegalStateException(
                            "cancellation did not complete cleanly: cancelled=" + token.isCancelled()
                                + ", callbackComplete=" + callbackCompletion.isDone()
                                + ", throwingListenerCalls=" + throwingListenerCalls.get()
                                + ", workingListenerCalls=" + workingListenerCalls.get()
                                + ", disconnected=" + disconnected.get());
                    }
                    if (!callbackResponse.contains("\"id\":\"reentrant-callback\"")
                        || !callbackResponse.contains("\"result\":null")
                        || callbackResponse.contains("\"error\"")) {
                        throw new IllegalStateException("unexpected callback response: " + callbackResponse);
                    }

                    awaitNoActiveCallbackRequests(client);
                    String notificationResponse = waitForMessage(serverInput, 250);
                    if (notificationResponse != null) {
                        throw new IllegalStateException(
                            "$/cancelRequest notification received a response: " + notificationResponse);
                    }

                    var followUp = CompletableFuture.supplyAsync(() ->
                        client.invokeCapability("cap.after-cancel", Map.of()));
                    String followUpRequest = requireMessage(serverInput, "follow-up capability request");
                    if (!followUpRequest.contains("\"capabilityId\":\"cap.after-cancel\"")) {
                        throw new IllegalStateException("unexpected follow-up request: " + followUpRequest);
                    }
                    writeMessage(
                        serverOutput,
                        "{\"jsonrpc\":\"2.0\",\"id\":" + extractNumericId(followUpRequest)
                            + ",\"result\":\"still-connected\"}");
                    if (!"still-connected".equals(followUp.get(1, TimeUnit.SECONDS))
                        || disconnected.get()) {
                        throw new IllegalStateException("transport disconnected after listener failure");
                    }

                    System.out.println("OK");
                }

                private static void awaitNoActiveCallbackRequests(AspireClient client) throws Exception {
                    Field activeRequestsField = AspireClient.class.getDeclaredField("activeCallbackRequests");
                    activeRequestsField.setAccessible(true);
                    Map<?, ?> activeRequests = (Map<?, ?>) activeRequestsField.get(client);
                    long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(1);
                    while (!activeRequests.isEmpty() && System.nanoTime() < deadline) {
                        TimeUnit.MILLISECONDS.sleep(5);
                    }
                    if (!activeRequests.isEmpty()) {
                        throw new IllegalStateException("completed callback retained request ids: " + activeRequests);
                    }
                }

                private static void setField(AspireClient client, String name, Object value) throws Exception {
                    Field field = AspireClient.class.getDeclaredField(name);
                    field.setAccessible(true);
                    field.set(client, value);
                }

                private static void startReader(AspireClient client) throws Exception {
                    Method method = AspireClient.class.getDeclaredMethod("ensureReaderLoopStarted");
                    method.setAccessible(true);
                    method.invoke(client);
                }

                // Capability requests have a JSON-RPC shape such as:
                //   {"jsonrpc":"2.0","id":1,"method":"invokeCapability","params":{...}}
                private static int extractNumericId(String json) {
                    int start = json.indexOf("\"id\":") + 5;
                    int end = start;
                    while (end < json.length() && Character.isDigit(json.charAt(end))) {
                        end++;
                    }
                    return Integer.parseInt(json.substring(start, end));
                }

                private static void writeMessage(PipedOutputStream output, String payload) throws Exception {
                    byte[] body = payload.getBytes(StandardCharsets.UTF_8);
                    output.write(("Content-Length: " + body.length + "\r\n\r\n").getBytes(StandardCharsets.UTF_8));
                    output.write(body);
                    output.flush();
                }

                private static String waitForMessage(InputStream input, long timeoutMs) throws Exception {
                    long deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(timeoutMs);
                    while (System.nanoTime() < deadline) {
                        if (input.available() > 0) {
                            return readMessage(input);
                        }
                        TimeUnit.MILLISECONDS.sleep(5);
                    }
                    return null;
                }

                private static String requireMessage(InputStream input, String description) throws Exception {
                    String message = waitForMessage(input, 1000);
                    if (message == null) {
                        throw new IllegalStateException("Timed out waiting for " + description);
                    }
                    return message;
                }

                // Messages are framed as:
                //   Content-Length: <UTF-8 byte count>\r\n\r\n<JSON payload>
                private static String readMessage(InputStream input) throws Exception {
                    int contentLength = -1;
                    while (true) {
                        String line = readLine(input);
                        if (line.isEmpty()) {
                            break;
                        }
                        if (line.startsWith("Content-Length:")) {
                            contentLength = Integer.parseInt(line.substring(15).trim());
                        }
                    }
                    if (contentLength < 0) {
                        throw new IllegalStateException("Missing Content-Length header");
                    }
                    return new String(input.readNBytes(contentLength), StandardCharsets.UTF_8);
                }

                private static String readLine(InputStream input) throws Exception {
                    ByteArrayOutputStream buffer = new ByteArrayOutputStream();
                    while (true) {
                        int ch = input.read();
                        if (ch == '\r') {
                            if (input.read() == '\n') {
                                break;
                            }
                        } else if (ch == '\n' || ch == -1) {
                            break;
                        } else {
                            buffer.write(ch);
                        }
                    }
                    return buffer.toString(StandardCharsets.UTF_8);
                }
            }
            """);

        await workspace.CompileAsync();
        var run = await workspace.RunClassAsync("aspire.CancelListenerReentrancyProbe", TimeSpan.FromSeconds(6));

        Assert.True(run.TimedOut is false, $"Probe timed out. stdout:{Environment.NewLine}{run.StdOut}{Environment.NewLine}stderr:{Environment.NewLine}{run.StdErr}");
        Assert.True(
            run.ExitCode == 0,
            $"Probe failed with exit code {run.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{run.StdOut}{Environment.NewLine}stderr:{Environment.NewLine}{run.StdErr}");
        Assert.Equal("OK", run.StdOut.Trim());
    }

    [Fact]
    public async Task GeneratedReferenceExpression_CancellationIsRequestScoped()
    {
        using var workspace = await CreateJavaProbeWorkspaceAsync();
        workspace.WriteSource(
            "aspire/ReferenceExpressionCancellationProbe.java",
            """
            package aspire;

            import java.io.ByteArrayOutputStream;
            import java.io.InputStream;
            import java.io.PipedInputStream;
            import java.io.PipedOutputStream;
            import java.lang.reflect.Field;
            import java.nio.charset.StandardCharsets;
            import java.util.List;
            import java.util.Map;
            import java.util.concurrent.CompletableFuture;
            import java.util.concurrent.TimeUnit;
            import java.util.regex.Matcher;
            import java.util.regex.Pattern;

            public class ReferenceExpressionCancellationProbe {
                public static void main(String[] args) throws Exception {
                    var clientInput = new PipedInputStream(32768);
                    var serverOutput = new PipedOutputStream(clientInput);
                    var serverInput = new PipedInputStream(32768);
                    var clientOutput = new PipedOutputStream(serverInput);
                    var client = new AspireClient("ignored");
                    setField(client, "inputStream", clientInput);
                    setField(client, "outputStream", clientOutput);
                    var expression = new ReferenceExpression(new Handle("expr", "ReferenceExpression"), client);
                    var token = new SlowPreCancelledToken();
                    token.cancel();

                    invokeAndComplete(expression, token, client, serverInput, serverOutput, "first");
                    invokeAndComplete(expression, token, client, serverInput, serverOutput, "second");
                    assertMarshallingFailureCleansEarlierRegistration(client, serverInput);

                    System.out.println("OK");
                }

                private static void invokeAndComplete(
                    ReferenceExpression expression,
                    CancellationToken token,
                    AspireClient client,
                    InputStream serverInput,
                    PipedOutputStream serverOutput,
                    String expectedResult) throws Exception {
                    var invocation = CompletableFuture.supplyAsync(() -> expression.getValue(token));
                    String invokeRequest = requireMessage(serverInput);
                    if (!invokeRequest.contains("\"method\":\"invokeCapability\"")
                        || !invokeRequest.contains("\"capabilityId\":\"Aspire.Hosting.ApplicationModel/getValue\"")) {
                        throw new IllegalStateException("cancellation preceded ReferenceExpression request: " + invokeRequest);
                    }

                    String cancellationRequest = requireMessage(serverInput);
                    String cancellationId = extractCancellationId(cancellationRequest);
                    if (!invokeRequest.contains("\"cancellationToken\":\"" + cancellationId + "\"")) {
                        throw new IllegalStateException(
                            "ReferenceExpression request did not marshal its cancellation token: "
                                + invokeRequest + cancellationRequest);
                    }

                    int cancellationRequestId = extractNumericId(cancellationRequest);
                    int invokeRequestId = extractNumericId(invokeRequest);
                    writeMessage(
                        serverOutput,
                        "{\"jsonrpc\":\"2.0\",\"id\":" + cancellationRequestId + ",\"result\":true}");
                    writeMessage(
                        serverOutput,
                        "{\"jsonrpc\":\"2.0\",\"id\":" + invokeRequestId + ",\"result\":\"" + expectedResult + "\"}");

                    String result = invocation.get(1, TimeUnit.SECONDS);
                    if (!expectedResult.equals(result)) {
                        throw new IllegalStateException("unexpected ReferenceExpression result: " + result);
                    }
                    assertCancellationRegistrationsEmpty(client, "completed " + expectedResult + " request");
                }

                private static void assertMarshallingFailureCleansEarlierRegistration(
                    AspireClient client,
                    InputStream serverInput) throws Exception {
                    var firstToken = new CancellationToken();
                    try {
                        client.invokeCapability(
                            "cap.marshallingFailure",
                            Map.of("tokens", List.of(firstToken, new FailingCancellationToken())));
                        throw new IllegalStateException("expected argument marshalling to fail");
                    } catch (IllegalStateException exception) {
                        if (!"expected marshalling failure".equals(exception.getMessage())) {
                            throw exception;
                        }
                    }

                    assertCancellationRegistrationsEmpty(client, "failed marshaling");
                    String output = waitForMessage(serverInput, 250);
                    if (output != null) {
                        throw new IllegalStateException("failed marshaling wrote a transport message: " + output);
                    }
                }

                private static void assertCancellationRegistrationsEmpty(AspireClient client, String operation) throws Exception {
                    Field registrationsField = AspireClient.class.getDeclaredField("cancellationRegistrations");
                    registrationsField.setAccessible(true);
                    Map<?, ?> registrations = (Map<?, ?>) registrationsField.get(client);
                    if (!registrations.isEmpty()) {
                        throw new IllegalStateException(operation + " retained registrations: " + registrations.keySet());
                    }
                }

                private static String extractCancellationId(String json) {
                    Matcher matcher = Pattern.compile("\\\"params\\\":\\[\\\"([^\\\"]+)\\\"\\]").matcher(json);
                    if (!matcher.find() || !json.contains("\"method\":\"cancelToken\"")) {
                        throw new IllegalStateException("unexpected cancellation request: " + json);
                    }
                    return matcher.group(1);
                }

                private static int extractNumericId(String json) {
                    Matcher matcher = Pattern.compile("\\\"id\\\":(\\d+)").matcher(json);
                    if (!matcher.find()) {
                        throw new IllegalStateException("missing numeric request id: " + json);
                    }
                    return Integer.parseInt(matcher.group(1));
                }

                private static void setField(AspireClient client, String name, Object value) throws Exception {
                    Field field = AspireClient.class.getDeclaredField(name);
                    field.setAccessible(true);
                    field.set(client, value);
                }

                private static void writeMessage(PipedOutputStream output, String payload) throws Exception {
                    byte[] body = payload.getBytes(StandardCharsets.UTF_8);
                    output.write(("Content-Length: " + body.length + "\r\n\r\n").getBytes(StandardCharsets.UTF_8));
                    output.write(body);
                    output.flush();
                }

                private static String waitForMessage(InputStream input, long timeoutMs) throws Exception {
                    long deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(timeoutMs);
                    while (System.nanoTime() < deadline) {
                        if (input.available() > 0) {
                            return readMessage(input);
                        }
                        TimeUnit.MILLISECONDS.sleep(5);
                    }
                    return null;
                }

                private static String requireMessage(InputStream input) throws Exception {
                    String message = waitForMessage(input, 1000);
                    if (message == null) {
                        throw new IllegalStateException("Timed out waiting for transport message");
                    }
                    return message;
                }

                private static String readMessage(InputStream input) throws Exception {
                    int contentLength = -1;
                    while (true) {
                        String line = readLine(input);
                        if (line.isEmpty()) {
                            break;
                        }
                        if (line.startsWith("Content-Length:")) {
                            contentLength = Integer.parseInt(line.substring(15).trim());
                        }
                    }
                    if (contentLength < 0) {
                        throw new IllegalStateException("Missing Content-Length header");
                    }
                    return new String(input.readNBytes(contentLength), StandardCharsets.UTF_8);
                }

                private static String readLine(InputStream input) throws Exception {
                    ByteArrayOutputStream buffer = new ByteArrayOutputStream();
                    while (true) {
                        int ch = input.read();
                        if (ch == '\r') {
                            if (input.read() == '\n') {
                                break;
                            }
                        } else if (ch == '\n' || ch == -1) {
                            break;
                        } else {
                            buffer.write(ch);
                        }
                    }
                    return buffer.toString(StandardCharsets.UTF_8);
                }

                private static final class SlowPreCancelledToken extends CancellationToken {
                    @Override
                    public void onCancel(Runnable listener) {
                        super.onCancel(listener);
                        try {
                            TimeUnit.MILLISECONDS.sleep(250);
                        } catch (InterruptedException exception) {
                            Thread.currentThread().interrupt();
                            throw new RuntimeException(exception);
                        }
                    }
                }

                private static final class FailingCancellationToken extends CancellationToken {
                    @Override
                    String getRemoteTokenId() {
                        throw new IllegalStateException("expected marshalling failure");
                    }
                }
            }
            """);

        await workspace.CompileAsync();
        var run = await workspace.RunClassAsync("aspire.ReferenceExpressionCancellationProbe", TimeSpan.FromSeconds(8));

        Assert.True(run.TimedOut is false, $"Probe timed out. stdout:{Environment.NewLine}{run.StdOut}{Environment.NewLine}stderr:{Environment.NewLine}{run.StdErr}");
        Assert.True(
            run.ExitCode == 0,
            $"Probe failed with exit code {run.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{run.StdOut}{Environment.NewLine}stderr:{Environment.NewLine}{run.StdErr}");
        Assert.Contains("OK", run.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratedTransport_SerializesPrimitiveArraysAsLists()
    {
        using var workspace = await CreateJavaProbeWorkspaceAsync();
        workspace.WriteSource(
            "aspire/PrimitiveArraySerializationProbe.java",
            """
            package aspire;

            import java.util.List;

            public class PrimitiveArraySerializationProbe {
                public static void main(String[] args) {
                    Object booleans = AspireClient.serializeValue(new boolean[] { true, false });
                    Object doubles = AspireClient.serializeValue(new double[] { 1.5, 2.5 });

                    if (!List.of(true, false).equals(booleans)) {
                        throw new IllegalStateException("boolean array was not serialized as a list: " + booleans);
                    }
                    if (!List.of(1.5, 2.5).equals(doubles)) {
                        throw new IllegalStateException("double array was not serialized as a list: " + doubles);
                    }

                    System.out.println("OK");
                }
            }
            """);

        await workspace.CompileAsync();
        var run = await workspace.RunClassAsync("aspire.PrimitiveArraySerializationProbe", TimeSpan.FromSeconds(6));

        Assert.True(run.TimedOut is false, $"Probe timed out. stdout:{Environment.NewLine}{run.StdOut}{Environment.NewLine}stderr:{Environment.NewLine}{run.StdErr}");
        Assert.True(
            run.ExitCode == 0,
            $"Probe failed with exit code {run.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{run.StdOut}{Environment.NewLine}stderr:{Environment.NewLine}{run.StdErr}");
        Assert.Contains("OK", run.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratedTransport_ReleasesRemoteTokensAfterCallbackCompletion()
    {
        using var workspace = await CreateJavaProbeWorkspaceAsync();
        workspace.WriteSource(
            "aspire/RemoteCancellationLifetimeProbe.java",
            """
            package aspire;

            import java.io.ByteArrayOutputStream;
            import java.io.InputStream;
            import java.io.PipedInputStream;
            import java.io.PipedOutputStream;
            import java.lang.reflect.Field;
            import java.lang.reflect.Method;
            import java.nio.charset.StandardCharsets;
            import java.util.Map;
            import java.util.concurrent.CompletableFuture;
            import java.util.concurrent.ConcurrentHashMap;
            import java.util.concurrent.CountDownLatch;
            import java.util.concurrent.TimeUnit;
            import java.util.concurrent.atomic.AtomicInteger;
            import java.util.function.BiFunction;
            import java.util.function.Function;

            public class RemoteCancellationLifetimeProbe {
                public static void main(String[] args) throws Exception {
                    var clientInput = new PipedInputStream(32768);
                    var serverOutput = new PipedOutputStream(clientInput);
                    var serverInput = new PipedInputStream(32768);
                    var clientOutput = new PipedOutputStream(serverInput);
                    var client = new AspireClient("ignored");
                    setField(client, "inputStream", clientInput);
                    setField(client, "outputStream", clientOutput);
                    startReader(client);

                    String callbackId = client.registerCallback(callbackArgs -> {
                        CancellationToken.fromValue(callbackArgs[0]);
                        return null;
                    });
                    writeCallbackRequest(serverOutput, 1, callbackId, "remote-ct");
                    requireMessage(serverInput);

                    Field tokensField = AspireClient.class.getDeclaredField("remoteCancellationTokens");
                    tokensField.setAccessible(true);
                    Map<?, ?> tokens = (Map<?, ?>) tokensField.get(client);
                    if (!tokens.isEmpty()) {
                        throw new IllegalStateException("completed callback retained remote tokens: " + tokens.keySet());
                    }
                    awaitNoActiveCallbacks(client);
                    writeCancelRequest(serverOutput, 2, "remote-ct");
                    String lateCancellation = requireMessage(serverInput);
                    if (!lateCancellation.contains("\"result\":false")) {
                        throw new IllegalStateException("late cancellation was retained: " + lateCancellation);
                    }
                    assertCancellationRegistriesEmpty(client, "late cancellation retained remote state");

                    assertOverlappingCallbacksKeepTokenRegistered();
                    assertCancellationBeforeAcquireIsPreserved();
                    assertDisconnectBeforeAcquireIsPreserved();
                    System.out.println("OK");
                }

                private static void assertOverlappingCallbacksKeepTokenRegistered() throws Exception {
                    var clientInput = new PipedInputStream(32768);
                    var serverOutput = new PipedOutputStream(clientInput);
                    var serverInput = new PipedInputStream(32768);
                    var clientOutput = new PipedOutputStream(serverInput);
                    var client = new AspireClient("ignored");
                    var tokens = new RacingTokenMap();
                    setField(client, "inputStream", clientInput);
                    setField(client, "outputStream", clientOutput);
                    setField(client, "remoteCancellationTokens", tokens);
                    startReader(client);

                    var firstCompletion = new CompletableFuture<Void>();
                    var secondCompletion = new CompletableFuture<Void>();
                    var firstToken = new CompletableFuture<CancellationToken>();
                    var secondToken = new CompletableFuture<CancellationToken>();
                    String firstCallbackId = client.registerCallback(callbackArgs -> {
                        CancellationToken token = CancellationToken.fromValue(callbackArgs[0]);
                        firstToken.complete(token);
                        return firstCompletion;
                    });
                    String secondCallbackId = client.registerCallback(callbackArgs -> {
                        CancellationToken token = CancellationToken.fromValue(callbackArgs[0]);
                        token.onCancel(() -> secondCompletion.complete(null));
                        secondToken.complete(token);
                        return secondCompletion;
                    });

                    writeCallbackRequest(serverOutput, 1, firstCallbackId, "shared-ct");
                    CancellationToken first = firstToken.get(1, TimeUnit.SECONDS);
                    writeCallbackRequest(serverOutput, 2, secondCallbackId, "shared-ct");
                    tokens.awaitSecondLookup();
                    firstCompletion.complete(null);
                    requireMessage(serverInput);
                    tokens.resumeSecondLookup();
                    CancellationToken second = secondToken.get(1, TimeUnit.SECONDS);

                    if (first != second || !tokens.containsKey("shared-ct")) {
                        throw new IllegalStateException("overlapping callback lost its active remote token");
                    }

                    writeCancelRequest(serverOutput, 3, "shared-ct");
                    String responses = requireMessage(serverInput) + requireMessage(serverInput);
                    if (!responses.contains("\"id\":3") || !responses.contains("\"result\":true") || !second.isCancelled()) {
                        throw new IllegalStateException("active overlapping callback was not cancelled: " + responses);
                    }
                    if (!tokens.isEmpty()) {
                        throw new IllegalStateException("cancelled overlapping callbacks retained remote tokens");
                    }
                }

                private static void assertCancellationBeforeAcquireIsPreserved() throws Exception {
                    var clientInput = new PipedInputStream(32768);
                    var serverOutput = new PipedOutputStream(clientInput);
                    var serverInput = new PipedInputStream(32768);
                    var clientOutput = new PipedOutputStream(serverInput);
                    var client = new AspireClient("ignored");
                    setField(client, "inputStream", clientInput);
                    setField(client, "outputStream", clientOutput);
                    startReader(client);

                    var acquiredToken = new CompletableFuture<CancellationToken>();
                    var callbackEntered = new CountDownLatch(1);
                    var resumeAcquisition = new CountDownLatch(1);
                    String callbackId = client.registerCallback(callbackArgs -> {
                        callbackEntered.countDown();
                        try {
                            if (!resumeAcquisition.await(1, TimeUnit.SECONDS)) {
                                throw new IllegalStateException("Timed out waiting to acquire early-cancelled token");
                            }
                        } catch (InterruptedException e) {
                            throw new RuntimeException(e);
                        }
                        acquiredToken.complete(CancellationToken.fromValue(callbackArgs[0]));
                        return null;
                    });
                    writeCallbackRequest(serverOutput, 1, callbackId, "early-ct");
                    if (!callbackEntered.await(1, TimeUnit.SECONDS)) {
                        throw new IllegalStateException("Timed out waiting for callback dispatch");
                    }
                    writeCancelRequest(serverOutput, 2, "early-ct");
                    String cancellationResponse = requireMessage(serverInput);
                    if (!cancellationResponse.contains("\"result\":true")) {
                        throw new IllegalStateException("routed callback cancellation was not retained: " + cancellationResponse);
                    }
                    resumeAcquisition.countDown();
                    CancellationToken token = acquiredToken.get(1, TimeUnit.SECONDS);
                    requireMessage(serverInput);
                    if (!token.isCancelled()) {
                        throw new IllegalStateException("cancellation before token acquisition was lost");
                    }
                    assertCancellationRegistriesEmpty(client, "completed early cancellation retained remote state");
                }

                private static void assertDisconnectBeforeAcquireIsPreserved() throws Exception {
                    var client = new AspireClient("ignored");
                    setField(client, "outputStream", new ByteArrayOutputStream());
                    Method disconnect = AspireClient.class.getDeclaredMethod("handleDisconnect");
                    disconnect.setAccessible(true);
                    disconnect.invoke(client);

                    var acquiredToken = new CompletableFuture<CancellationToken>();
                    String callbackId = client.registerCallback(callbackArgs -> {
                        acquiredToken.complete(CancellationToken.fromValue(callbackArgs[0]));
                        return null;
                    });
                    Method handleRequest = AspireClient.class.getDeclaredMethod("handleServerRequest", Map.class);
                    handleRequest.setAccessible(true);
                    handleRequest.invoke(client, Map.of(
                        "jsonrpc", "2.0",
                        "id", 1,
                        "method", "invokeCallback",
                        "params", Map.of("callbackId", callbackId, "args", java.util.List.of("disconnect-ct"))));

                    CancellationToken token = acquiredToken.get(1, TimeUnit.SECONDS);
                    if (!token.isCancelled()) {
                        throw new IllegalStateException("disconnect before token acquisition was lost");
                    }
                    assertCancellationRegistriesEmpty(client, "disconnect-before-acquire retained remote state");
                }

                private static void assertCancellationRegistriesEmpty(AspireClient client, String message) throws Exception {
                    Field tokensField = AspireClient.class.getDeclaredField("remoteCancellationTokens");
                    tokensField.setAccessible(true);
                    Field pendingField = AspireClient.class.getDeclaredField("pendingRemoteCancellations");
                    pendingField.setAccessible(true);
                    Map<?, ?> tokens = (Map<?, ?>) tokensField.get(client);
                    Map<?, ?> pending = (Map<?, ?>) pendingField.get(client);
                    if (!tokens.isEmpty() || !pending.isEmpty()) {
                        throw new IllegalStateException(message + ": active=" + tokens.keySet() + ", pending=" + pending.keySet());
                    }
                }

                private static void awaitNoActiveCallbacks(AspireClient client) throws Exception {
                    Field callbacksField = AspireClient.class.getDeclaredField("activeServerCallbacks");
                    callbacksField.setAccessible(true);
                    long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(1);
                    while (System.nanoTime() < deadline) {
                        if (callbacksField.getInt(client) == 0) {
                            return;
                        }
                        TimeUnit.MILLISECONDS.sleep(5);
                    }
                    throw new IllegalStateException("Timed out waiting for callback dispatch completion");
                }

                private static void setField(AspireClient client, String name, Object value) throws Exception {
                    Field field = AspireClient.class.getDeclaredField(name);
                    field.setAccessible(true);
                    field.set(client, value);
                }

                private static void startReader(AspireClient client) throws Exception {
                    Method method = AspireClient.class.getDeclaredMethod("ensureReaderLoopStarted");
                    method.setAccessible(true);
                    method.invoke(client);
                }

                private static void writeCallbackRequest(PipedOutputStream output, int id, String callbackId, String tokenId) throws Exception {
                    String payload = "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"invokeCallback\",\"params\":{\"callbackId\":\"" + callbackId + "\",\"args\":[\"" + tokenId + "\"]}}";
                    writeMessage(output, payload);
                }

                private static void writeCancelRequest(PipedOutputStream output, int id, String tokenId) throws Exception {
                    String payload = "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"cancel\",\"params\":{\"cancellationId\":\"" + tokenId + "\"}}";
                    writeMessage(output, payload);
                }

                private static void writeMessage(PipedOutputStream output, String payload) throws Exception {
                    byte[] body = payload.getBytes(StandardCharsets.UTF_8);
                    output.write(("Content-Length: " + body.length + "\r\n\r\n").getBytes(StandardCharsets.UTF_8));
                    output.write(body);
                    output.flush();
                }

                private static final class RacingTokenMap extends ConcurrentHashMap<String, CancellationToken> {
                    private final AtomicInteger lookups = new AtomicInteger();
                    private final CountDownLatch secondLookup = new CountDownLatch(1);
                    private final CountDownLatch resumeSecondLookup = new CountDownLatch(1);

                    @Override
                    public CancellationToken computeIfAbsent(String key, Function<? super String, ? extends CancellationToken> mappingFunction) {
                        CancellationToken token = super.computeIfAbsent(key, mappingFunction);
                        if (lookups.incrementAndGet() == 2) {
                            secondLookup.countDown();
                            try {
                                if (!resumeSecondLookup.await(1, TimeUnit.SECONDS)) {
                                    throw new IllegalStateException("Timed out waiting to resume second token lookup");
                                }
                            } catch (InterruptedException e) {
                                throw new RuntimeException(e);
                            }
                        }
                        return token;
                    }

                    @Override
                    public CancellationToken compute(String key, BiFunction<? super String, ? super CancellationToken, ? extends CancellationToken> remappingFunction) {
                        CancellationToken token = super.compute(key, remappingFunction);
                        if (lookups.incrementAndGet() == 2) {
                            secondLookup.countDown();
                        }
                        return token;
                    }

                    void awaitSecondLookup() throws Exception {
                        if (!secondLookup.await(1, TimeUnit.SECONDS)) {
                            throw new IllegalStateException("Timed out waiting for second token lookup");
                        }
                    }

                    void resumeSecondLookup() {
                        resumeSecondLookup.countDown();
                    }
                }

                private static String requireMessage(InputStream input) throws Exception {
                    long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(1);
                    while (System.nanoTime() < deadline) {
                        if (input.available() > 0) {
                            return readMessage(input);
                        }
                        TimeUnit.MILLISECONDS.sleep(5);
                    }
                    throw new IllegalStateException("Timed out waiting for transport response");
                }

                private static String readMessage(InputStream input) throws Exception {
                    int contentLength = -1;
                    while (true) {
                        String line = readLine(input);
                        if (line.isEmpty()) {
                            break;
                        }
                        if (line.startsWith("Content-Length:")) {
                            contentLength = Integer.parseInt(line.substring(15).trim());
                        }
                    }
                    return new String(input.readNBytes(contentLength), StandardCharsets.UTF_8);
                }

                private static String readLine(InputStream input) throws Exception {
                    ByteArrayOutputStream buffer = new ByteArrayOutputStream();
                    while (true) {
                        int ch = input.read();
                        if (ch == -1) {
                            break;
                        }
                        if (ch == '\r') {
                            int next = input.read();
                            if (next == '\n') {
                                break;
                            }
                            buffer.write(ch);
                            if (next != -1) {
                                buffer.write(next);
                            }
                            continue;
                        }
                        if (ch == '\n') {
                            break;
                        }
                        buffer.write(ch);
                    }
                    return buffer.toString(StandardCharsets.UTF_8);
                }
            }
            """);

        await workspace.CompileAsync();
        var run = await workspace.RunClassAsync("aspire.RemoteCancellationLifetimeProbe", TimeSpan.FromSeconds(6));

        Assert.True(run.TimedOut is false, $"Probe timed out. stdout:{Environment.NewLine}{run.StdOut}{Environment.NewLine}stderr:{Environment.NewLine}{run.StdErr}");
        Assert.True(
            run.ExitCode == 0,
            $"Probe failed with exit code {run.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{run.StdOut}{Environment.NewLine}stderr:{Environment.NewLine}{run.StdErr}");
        Assert.Contains("OK", run.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratedTransport_CancellationIsDirectionalAndLocalCancelNotifiesHostOnce()
    {
        using var workspace = await CreateJavaProbeWorkspaceAsync();
        workspace.WriteSource(
            "aspire/CancellationDirectionProbe.java",
            """
            package aspire;

            import java.io.ByteArrayOutputStream;
            import java.io.InputStream;
            import java.io.PipedInputStream;
            import java.io.PipedOutputStream;
            import java.lang.reflect.Field;
            import java.nio.charset.StandardCharsets;
            import java.util.LinkedHashMap;
            import java.util.Map;
            import java.util.concurrent.CompletableFuture;
            import java.util.concurrent.CountDownLatch;
            import java.util.concurrent.TimeUnit;
            import java.util.regex.Matcher;
            import java.util.regex.Pattern;

            public class CancellationDirectionProbe {
                public static void main(String[] args) throws Exception {
                    var clientInput = new PipedInputStream(32768);
                    var serverOutput = new PipedOutputStream(clientInput);
                    var serverInput = new PipedInputStream(32768);
                    var clientOutput = new PipedOutputStream(serverInput);

                    var client = new AspireClient("ignored");
                    setField(client, "inputStream", clientInput);
                    setField(client, "outputStream", clientOutput);

                    CancellationToken local = new CancellationToken();
                    String localId = client.registerCancellation(local);
                    local.cancel();
                    local.cancel();

                    String outbound = readMessage(serverInput);
                    assertMethodAndToken(outbound, "cancelToken", localId);

                    if (waitForMessage(serverInput, 250) != null) {
                        throw new IllegalStateException("local cancel emitted more than one cancellation request");
                    }

                    assertInvocationPrecedesCancellation();

                    var secondClientInput = new PipedInputStream(32768);
                    var secondServerOutput = new PipedOutputStream(secondClientInput);
                    var secondServerInput = new PipedInputStream(32768);
                    var secondClientOutput = new PipedOutputStream(secondServerInput);
                    var secondClient = new AspireClient("ignored");
                    setField(secondClient, "inputStream", secondClientInput);
                    setField(secondClient, "outputStream", secondClientOutput);
                    startReader(secondClient);

                    CancellationToken[] remoteTokens = new CancellationToken[2];
                    CompletableFuture<Void>[] callbackCompletions = new CompletableFuture[] {
                        new CompletableFuture<>(),
                        new CompletableFuture<>()
                    };
                    String firstCallbackId = client.registerCallback(callbackArgs -> {
                        remoteTokens[0] = CancellationToken.fromValue(callbackArgs[0]);
                        remoteTokens[0].onCancel(() -> callbackCompletions[0].complete(null));
                        return callbackCompletions[0];
                    });
                    String secondCallbackId = secondClient.registerCallback(callbackArgs -> {
                        remoteTokens[1] = CancellationToken.fromValue(callbackArgs[0]);
                        remoteTokens[1].onCancel(() -> callbackCompletions[1].complete(null));
                        return callbackCompletions[1];
                    });

                    writeCallbackRequest(serverOutput, 9000, firstCallbackId, "shared-remote-ct");
                    writeCallbackRequest(secondServerOutput, 9000, secondCallbackId, "shared-remote-ct");
                    waitForToken(remoteTokens, 0);
                    waitForToken(remoteTokens, 1);

                    if (remoteTokens[0] == null || remoteTokens[1] == null || remoteTokens[0] == remoteTokens[1]) {
                        throw new IllegalStateException("remote cancellation tokens were not isolated by client");
                    }

                    writeCancelRequest(serverOutput, 9001, "shared-remote-ct");

                    assertCancellationResponses(serverInput);
                    if (!remoteTokens[0].isCancelled() || remoteTokens[1].isCancelled()) {
                        throw new IllegalStateException("remote cancellation crossed client boundaries");
                    }

                    writeCancelRequest(secondServerOutput, 9001, "shared-remote-ct");
                    assertCancellationResponses(secondServerInput);
                    if (!remoteTokens[1].isCancelled()) {
                        throw new IllegalStateException("second client's remote token was not cancelled");
                    }

                    if (waitForMessage(serverInput, 250) != null) {
                        throw new IllegalStateException("remote cancellation echoed back to host");
                    }

                    System.out.println("OK");
                }

                private static void assertInvocationPrecedesCancellation() throws Exception {
                    var clientInput = new PipedInputStream(32768);
                    var serverOutput = new PipedOutputStream(clientInput);
                    var serverInput = new PipedInputStream(32768);
                    var clientOutput = new PipedOutputStream(serverInput);
                    var client = new AspireClient("ignored");
                    setField(client, "inputStream", clientInput);
                    setField(client, "outputStream", clientOutput);

                    var token = new CancellationToken();
                    token.cancel();
                    var marshallingStarted = new CountDownLatch(1);
                    var resumeMarshalling = new CountDownLatch(1);
                    JsonSerializable blockingArgument = () -> {
                        marshallingStarted.countDown();
                        try {
                            if (!resumeMarshalling.await(1, TimeUnit.SECONDS)) {
                                throw new IllegalStateException("Timed out waiting to resume argument marshalling");
                            }
                        } catch (InterruptedException exception) {
                            throw new RuntimeException(exception);
                        }
                        return Map.of();
                    };

                    var invocationArgs = new LinkedHashMap<String, Object>();
                    invocationArgs.put("token", token);
                    invocationArgs.put("blocking", blockingArgument);
                    var invocation = CompletableFuture.supplyAsync(() ->
                        client.invokeCapability("cap.cancelled", invocationArgs));

                    if (!marshallingStarted.await(1, TimeUnit.SECONDS)) {
                        throw new IllegalStateException("Timed out waiting for argument marshalling");
                    }
                    String earlyMessage = waitForMessage(serverInput, 250);
                    resumeMarshalling.countDown();
                    if (earlyMessage != null) {
                        throw new IllegalStateException("cancellation preceded invocation: " + earlyMessage);
                    }

                    String invokeRequest = requireMessage(serverInput);
                    String cancelRequest = requireMessage(serverInput);
                    if (!invokeRequest.contains("\"method\":\"invokeCapability\"")
                        || !cancelRequest.contains("\"method\":\"cancelToken\"")) {
                        throw new IllegalStateException("unexpected request order: " + invokeRequest + cancelRequest);
                    }

                    Matcher tokenMatcher = Pattern.compile("\\\"params\\\":\\[\\\"([^\\\"]+)\\\"\\]").matcher(cancelRequest);
                    if (!tokenMatcher.find() || !invokeRequest.contains("\"token\":\"" + tokenMatcher.group(1) + "\"")) {
                        throw new IllegalStateException("cancellation token did not match invocation: " + invokeRequest + cancelRequest);
                    }

                    int invokeId = extractNumericId(invokeRequest);
                    int cancelId = extractNumericId(cancelRequest);
                    writeMessage(serverOutput, "{\"jsonrpc\":\"2.0\",\"id\":" + cancelId + ",\"result\":true}");
                    writeMessage(serverOutput, "{\"jsonrpc\":\"2.0\",\"id\":" + invokeId + ",\"result\":null}");
                    invocation.get(1, TimeUnit.SECONDS);
                }

                private static int extractNumericId(String json) {
                    Matcher matcher = Pattern.compile("\\\"id\\\":(\\d+)").matcher(json);
                    if (!matcher.find()) {
                        throw new IllegalStateException("missing request id: " + json);
                    }
                    return Integer.parseInt(matcher.group(1));
                }

                private static void setField(AspireClient client, String name, Object value) throws Exception {
                    Field field = AspireClient.class.getDeclaredField(name);
                    field.setAccessible(true);
                    field.set(client, value);
                }

                private static void startReader(AspireClient client) throws Exception {
                    var method = AspireClient.class.getDeclaredMethod("ensureReaderLoopStarted");
                    method.setAccessible(true);
                    method.invoke(client);
                }

                private static void writeCallbackRequest(PipedOutputStream output, int id, String callbackId, String tokenId) throws Exception {
                    String payload = "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"invokeCallback\",\"params\":{\"callbackId\":\"" + callbackId + "\",\"args\":[\"" + tokenId + "\"]}}";
                    writeMessage(output, payload);
                }

                private static void writeCancelRequest(PipedOutputStream output, int id, String tokenId) throws Exception {
                    String payload = "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"cancel\",\"params\":{\"cancellationId\":\"" + tokenId + "\"}}";
                    writeMessage(output, payload);
                }

                private static void writeMessage(PipedOutputStream output, String payload) throws Exception {
                    byte[] body = payload.getBytes(StandardCharsets.UTF_8);
                    String header = "Content-Length: " + body.length + "\r\n\r\n";
                    output.write(header.getBytes(StandardCharsets.UTF_8));
                    output.write(body);
                    output.flush();
                }

                private static void waitForToken(CancellationToken[] tokens, int index) throws Exception {
                    long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(1);
                    while (tokens[index] == null && System.nanoTime() < deadline) {
                        TimeUnit.MILLISECONDS.sleep(5);
                    }
                    if (tokens[index] == null) {
                        throw new IllegalStateException("Timed out waiting for callback token " + index);
                    }
                }

                private static void assertCancellationResponses(InputStream input) throws Exception {
                    String first = requireMessage(input);
                    String second = requireMessage(input);
                    String combined = first + second;
                    if (!combined.contains("\"id\":9001") || !combined.contains("\"result\":true")) {
                        throw new IllegalStateException("unexpected cancellation responses: " + combined);
                    }
                    if (combined.contains("\"method\":\"cancelToken\"")) {
                        throw new IllegalStateException("remote cancellation echoed back to host: " + combined);
                    }
                }

                private static void assertMethodAndToken(String json, String expectedMethod, String expectedToken) {
                    if (!json.contains("\"method\":\"" + expectedMethod + "\"")) {
                        throw new IllegalStateException("unexpected method payload: " + json);
                    }

                    if (json.contains("\"params\":[")) {
                        if (!json.contains("\"params\":[\"" + expectedToken + "\"]")) {
                            throw new IllegalStateException("unexpected cancel token payload: " + json);
                        }
                        return;
                    }

                    if (!json.contains("\"tokenId\":\"" + expectedToken + "\"")) {
                        throw new IllegalStateException("unexpected cancel token payload: " + json);
                    }
                }

                private static String waitForMessage(InputStream input, long timeoutMs) throws Exception {
                    long deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(timeoutMs);
                    while (System.nanoTime() < deadline) {
                        if (input.available() > 0) {
                            return readMessage(input);
                        }
                        TimeUnit.MILLISECONDS.sleep(5);
                    }
                    return null;
                }

                private static String requireMessage(InputStream input) throws Exception {
                    String message = waitForMessage(input, 1000);
                    if (message == null) {
                        throw new IllegalStateException("Timed out waiting for transport response");
                    }
                    return message;
                }

                private static String readMessage(InputStream input) throws Exception {
                    int contentLength = -1;
                    while (true) {
                        String line = readLine(input);
                        if (line.isEmpty()) {
                            break;
                        }
                        if (line.startsWith("Content-Length:")) {
                            contentLength = Integer.parseInt(line.substring(15).trim());
                        }
                    }

                    if (contentLength < 0) {
                        throw new IllegalStateException("Missing Content-Length header");
                    }

                    byte[] body = input.readNBytes(contentLength);
                    return new String(body, StandardCharsets.UTF_8);
                }

                private static String readLine(InputStream input) throws Exception {
                    ByteArrayOutputStream buffer = new ByteArrayOutputStream();
                    while (true) {
                        int ch = input.read();
                        if (ch == -1) {
                            break;
                        }
                        if (ch == '\r') {
                            int next = input.read();
                            if (next == '\n') {
                                break;
                            }
                            buffer.write(ch);
                            if (next != -1) {
                                buffer.write(next);
                            }
                            continue;
                        }
                        if (ch == '\n') {
                            break;
                        }
                        buffer.write(ch);
                    }

                    return buffer.toString(StandardCharsets.UTF_8);
                }
            }
            """);

        await workspace.CompileAsync();
        var run = await workspace.RunClassAsync("aspire.CancellationDirectionProbe", TimeSpan.FromSeconds(6));

        Assert.True(run.TimedOut is false, $"Probe timed out. stdout:{Environment.NewLine}{run.StdOut}{Environment.NewLine}stderr:{Environment.NewLine}{run.StdErr}");
        Assert.True(
            run.ExitCode == 0,
            $"Probe failed with exit code {run.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{run.StdOut}{Environment.NewLine}stderr:{Environment.NewLine}{run.StdErr}");
        Assert.Contains("OK", run.StdOut, StringComparison.Ordinal);
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
    public void DtoPropertyWithDictionaryNestedInArrayRebuildsTheFieldType()
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
        Assert.Contains("(Map<String, String>[]) AspireClient.convertArray(metadataArrayValue, Map[].class.getComponentType()", generated);
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
    public async Task GeneratedDto_TypedArraysAreRebuiltRecursivelyFromTransportLists()
    {
        var stringType = new AtsTypeRef { TypeId = "string", Category = AtsTypeCategory.Primitive };
        var innerArray = new AtsTypeRef { TypeId = "array", Category = AtsTypeCategory.Array, ElementType = stringType };
        var context = CreateContextWithSingleDtoProperty(
            "Matrix",
            new AtsTypeRef { TypeId = "array", Category = AtsTypeCategory.Array, ElementType = innerArray });

        using var workspace = await CreateJavaProbeWorkspaceAsync(context, "aspire/KeywordProbe.java");
        workspace.WriteSource(
            "aspire/TypedArrayProbe.java",
            """
            package aspire;

            import java.util.List;
            import java.util.Map;

            public class TypedArrayProbe {
                public static void main(String[] args) {
                    var value = KeywordProbe.fromMap(Map.of(
                        "Matrix",
                        List.of(List.of("one", "two"), List.of("three"))));
                    String[][] matrix = value.getMatrix();

                    if (matrix.length != 2
                        || matrix[0].length != 2
                        || !"two".equals(matrix[0][1])
                        || !"three".equals(matrix[1][0])) {
                        throw new IllegalStateException("unexpected typed array contents");
                    }

                    System.out.println("OK");
                }
            }
            """);

        await workspace.CompileAsync();
        var run = await workspace.RunClassAsync("aspire.TypedArrayProbe", TimeSpan.FromSeconds(6));

        Assert.True(run.TimedOut is false, $"Probe timed out. stdout:{Environment.NewLine}{run.StdOut}{Environment.NewLine}stderr:{Environment.NewLine}{run.StdErr}");
        Assert.True(
            run.ExitCode == 0,
            $"Probe failed with exit code {run.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{run.StdOut}{Environment.NewLine}stderr:{Environment.NewLine}{run.StdErr}");
        Assert.Contains("OK", run.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedCapabilitiesAndCallbacks_RebuildTypedArraysFromTransportLists()
    {
        var resourceType = new AtsTypeRef { TypeId = "Tests/ProbeResource", Category = AtsTypeCategory.Handle };
        var stringType = new AtsTypeRef { TypeId = AtsConstants.String, Category = AtsTypeCategory.Primitive };
        var innerArray = new AtsTypeRef { TypeId = "array", Category = AtsTypeCategory.Array, ElementType = stringType };
        var matrixType = new AtsTypeRef { TypeId = "array", Category = AtsTypeCategory.Array, ElementType = innerArray };
        var callback = new AtsParameterInfo
        {
            Name = "callback",
            IsCallback = true,
            Type = new AtsTypeRef { TypeId = "callback", Category = AtsTypeCategory.Callback },
            CallbackParameters = [new AtsCallbackParameterInfo { Name = "matrix", Type = matrixType }],
            CallbackReturnType = new AtsTypeRef { TypeId = AtsConstants.Void, Category = AtsTypeCategory.Primitive }
        };
        var callbackCapability = new AtsCapabilityInfo
        {
            CapabilityId = "Tests/withMatrix",
            MethodName = "withMatrix",
            Parameters = [callback],
            ReturnType = new AtsTypeRef { TypeId = AtsConstants.Void, Category = AtsTypeCategory.Primitive },
            TargetTypeId = resourceType.TypeId,
            TargetType = resourceType,
            TargetParameterName = "resource",
            ExpandedTargetTypes = [resourceType],
            CapabilityKind = AtsCapabilityKind.Method
        };
        var context = CreateContextWithProbeCapabilities(
            resourceType,
            CreateProbeCapability(resourceType, "getMatrix", matrixType),
            callbackCapability);

        var generated = _generator.GenerateDistributedApplication(context)["aspire/ProbeResource.java"];

        Assert.Contains("AspireClient.convertArray(result, String[][].class.getComponentType()", generated);
        Assert.Contains("AspireClient.convertArray(args[0], String[][].class.getComponentType()", generated);
    }

    [Fact]
    public void GeneratedCancellationParameters_AreMarshalledByInvokeCapability()
    {
        var resourceType = new AtsTypeRef { TypeId = "Tests/ProbeResource", Category = AtsTypeCategory.Handle };
        var cancellationParameter = new AtsParameterInfo
        {
            Name = "cancellationToken",
            Type = new AtsTypeRef { TypeId = AtsConstants.CancellationToken, Category = AtsTypeCategory.Dto }
        };
        var capability = new AtsCapabilityInfo
        {
            CapabilityId = "Tests/cancellable",
            MethodName = "cancellable",
            Parameters = [cancellationParameter],
            ReturnType = new AtsTypeRef { TypeId = AtsConstants.Void, Category = AtsTypeCategory.Primitive },
            TargetTypeId = resourceType.TypeId,
            TargetType = resourceType,
            TargetParameterName = "resource",
            ExpandedTargetTypes = [resourceType],
            CapabilityKind = AtsCapabilityKind.Method
        };
        var generated = _generator.GenerateDistributedApplication(
            CreateContextWithProbeCapabilities(resourceType, capability))["aspire/ProbeResource.java"];

        Assert.Contains("reqArgs.put(\"cancellationToken\", cancellationToken);", generated);
        Assert.DoesNotContain("registerCancellation(cancellationToken)", generated);
    }

    [Fact]
    public void GeneratedPropertySetters_BoxTypeLevelNullableNumericParameters()
    {
        var resourceType = new AtsTypeRef { TypeId = "Tests/ProbeResource", Category = AtsTypeCategory.Handle };
        var capability = new AtsCapabilityInfo
        {
            CapabilityId = "Tests/ProbeResource.setAmount",
            MethodName = "setAmount",
            Parameters =
            [
                new AtsParameterInfo
                {
                    Name = "context",
                    Type = resourceType,
                    IsNullable = false
                },
                new AtsParameterInfo
                {
                    Name = "value",
                    Type = new AtsTypeRef
                    {
                        TypeId = AtsConstants.Number,
                        Category = AtsTypeCategory.Primitive,
                        IsNullable = true
                    },
                    IsNullable = false
                }
            ],
            ReturnType = resourceType,
            TargetTypeId = resourceType.TypeId,
            TargetType = resourceType,
            TargetParameterName = "context",
            ExpandedTargetTypes = [resourceType],
            CapabilityKind = AtsCapabilityKind.PropertySetter
        };

        var generated = _generator.GenerateDistributedApplication(
            CreateContextWithProbeCapabilities(resourceType, capability))["aspire/ProbeResource.java"];

        Assert.Contains("public ProbeResource setAmount(Number value)", generated, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratedCapabilities_NullablePrimitiveResultsReturnNull()
    {
        var resourceType = new AtsTypeRef
        {
            TypeId = "Tests/ProbeResource",
            Category = AtsTypeCategory.Handle
        };
        var context = CreateContextWithProbeCapabilities(
            resourceType,
            CreateProbeCapability(
                resourceType,
                "nullableFlag",
                new AtsTypeRef { TypeId = AtsConstants.Boolean, Category = AtsTypeCategory.Primitive, IsNullable = true }),
            CreateProbeCapability(
                resourceType,
                "nullableNumber",
                new AtsTypeRef { TypeId = AtsConstants.Number, Category = AtsTypeCategory.Primitive, IsNullable = true }));

        using var workspace = await CreateJavaProbeWorkspaceAsync(context, "aspire/ProbeResource.java");
        workspace.WriteSource(
            "aspire/NullablePrimitiveProbe.java",
            """
            package aspire;

            import java.io.ByteArrayOutputStream;
            import java.io.InputStream;
            import java.io.PipedInputStream;
            import java.io.PipedOutputStream;
            import java.lang.reflect.Field;
            import java.nio.charset.StandardCharsets;
            import java.util.concurrent.CompletableFuture;
            import java.util.concurrent.TimeUnit;

            public class NullablePrimitiveProbe {
                public static void main(String[] args) throws Exception {
                    var clientInput = new PipedInputStream(32768);
                    var serverOutput = new PipedOutputStream(clientInput);
                    var serverInput = new PipedInputStream(32768);
                    var clientOutput = new PipedOutputStream(serverInput);

                    var client = new AspireClient("ignored");
                    setField(client, "inputStream", clientInput);
                    setField(client, "outputStream", clientOutput);
                    var resource = new ProbeResource(new Handle("probe", "Tests/ProbeResource"), client);

                    var flagCall = CompletableFuture.supplyAsync(resource::nullableFlag);
                    writeNullResponse(serverOutput, extractId(readMessage(serverInput)));
                    Boolean flag = flagCall.get(2, TimeUnit.SECONDS);
                    if (flag != null) {
                        throw new IllegalStateException("nullable Boolean was not null");
                    }

                    var numberCall = CompletableFuture.supplyAsync(resource::nullableNumber);
                    writeNullResponse(serverOutput, extractId(readMessage(serverInput)));
                    Number number = numberCall.get(2, TimeUnit.SECONDS);
                    if (number != null) {
                        throw new IllegalStateException("nullable Number was not null");
                    }

                    System.out.println("OK");
                }

                private static void setField(AspireClient client, String name, Object value) throws Exception {
                    Field field = AspireClient.class.getDeclaredField(name);
                    field.setAccessible(true);
                    field.set(client, value);
                }

                private static int extractId(String json) {
                    int start = json.indexOf("\"id\":") + 5;
                    int end = start;
                    while (end < json.length() && Character.isDigit(json.charAt(end))) {
                        end++;
                    }
                    return Integer.parseInt(json.substring(start, end));
                }

                private static void writeNullResponse(PipedOutputStream output, int id) throws Exception {
                    writeMessage(output, "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":null}");
                }

                private static void writeMessage(PipedOutputStream output, String payload) throws Exception {
                    byte[] body = payload.getBytes(StandardCharsets.UTF_8);
                    output.write(("Content-Length: " + body.length + "\r\n\r\n").getBytes(StandardCharsets.UTF_8));
                    output.write(body);
                    output.flush();
                }

                private static String readMessage(InputStream input) throws Exception {
                    int contentLength = -1;
                    while (true) {
                        String line = readLine(input);
                        if (line.isEmpty()) {
                            break;
                        }
                        if (line.startsWith("Content-Length:")) {
                            contentLength = Integer.parseInt(line.substring(15).trim());
                        }
                    }
                    return new String(input.readNBytes(contentLength), StandardCharsets.UTF_8);
                }

                private static String readLine(InputStream input) throws Exception {
                    ByteArrayOutputStream buffer = new ByteArrayOutputStream();
                    while (true) {
                        int ch = input.read();
                        if (ch == '\r') {
                            if (input.read() == '\n') {
                                break;
                            }
                        } else if (ch == '\n' || ch == -1) {
                            break;
                        } else {
                            buffer.write(ch);
                        }
                    }
                    return buffer.toString(StandardCharsets.UTF_8);
                }
            }
            """);

        await workspace.CompileAsync();
        var run = await workspace.RunClassAsync("aspire.NullablePrimitiveProbe", TimeSpan.FromSeconds(6));

        Assert.True(run.TimedOut is false, $"Probe timed out. stdout:{Environment.NewLine}{run.StdOut}{Environment.NewLine}stderr:{Environment.NewLine}{run.StdErr}");
        Assert.True(
            run.ExitCode == 0,
            $"Probe failed with exit code {run.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{run.StdOut}{Environment.NewLine}stderr:{Environment.NewLine}{run.StdErr}");
        Assert.Contains("OK", run.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratedNullablePrimitiveArrays_UseBoxedComponents()
    {
        var resourceType = new AtsTypeRef { TypeId = "Tests/ProbeResource", Category = AtsTypeCategory.Handle };
        var nullableNumber = new AtsTypeRef
        {
            TypeId = AtsConstants.Number,
            Category = AtsTypeCategory.Primitive,
            IsNullable = true
        };
        var nullableBoolean = new AtsTypeRef
        {
            TypeId = AtsConstants.Boolean,
            Category = AtsTypeCategory.Primitive,
            IsNullable = true
        };
        var numberArray = new AtsTypeRef
        {
            TypeId = "numberArray",
            Category = AtsTypeCategory.Array,
            ElementType = nullableNumber
        };
        var booleanArray = new AtsTypeRef
        {
            TypeId = "booleanArray",
            Category = AtsTypeCategory.Array,
            ElementType = nullableBoolean
        };
        var booleanMatrix = new AtsTypeRef
        {
            TypeId = "booleanMatrix",
            Category = AtsTypeCategory.Array,
            ElementType = booleanArray
        };
        var callback = new AtsParameterInfo
        {
            Name = "callback",
            IsCallback = true,
            Type = new AtsTypeRef { TypeId = "callback", Category = AtsTypeCategory.Callback },
            CallbackParameters =
            [
                new AtsCallbackParameterInfo { Name = "numbers", Type = numberArray },
                new AtsCallbackParameterInfo { Name = "flags", Type = booleanMatrix }
            ],
            CallbackReturnType = new AtsTypeRef { TypeId = AtsConstants.Void, Category = AtsTypeCategory.Primitive }
        };
        var callbackCapability = new AtsCapabilityInfo
        {
            CapabilityId = "Tests/withArrays",
            MethodName = "withArrays",
            Parameters = [callback],
            ReturnType = new AtsTypeRef { TypeId = AtsConstants.Void, Category = AtsTypeCategory.Primitive },
            TargetTypeId = resourceType.TypeId,
            TargetType = resourceType,
            TargetParameterName = "resource",
            ExpandedTargetTypes = [resourceType],
            CapabilityKind = AtsCapabilityKind.Method
        };
        var context = new AtsContext
        {
            Capabilities =
            [
                CreateProbeCapability(resourceType, "nullableNumbers", numberArray),
                CreateProbeCapability(resourceType, "nullableFlagMatrix", booleanMatrix),
                callbackCapability
            ],
            HandleTypes = [new AtsTypeInfo { AtsTypeId = resourceType.TypeId! }],
            DtoTypes =
            [
                new AtsDtoTypeInfo
                {
                    Name = "ArrayDto",
                    TypeId = "Tests/ArrayDto",
                    Properties =
                    [
                        new AtsDtoPropertyInfo { Name = "Numbers", Type = numberArray },
                        new AtsDtoPropertyInfo { Name = "Flags", Type = booleanMatrix }
                    ]
                }
            ],
            EnumTypes = []
        };

        using var workspace = await CreateJavaProbeWorkspaceAsync(
            context,
            "aspire/ProbeResource.java",
            "aspire/ArrayDto.java",
            "aspire/AspireAction2.java");
        workspace.WriteSource(
            "aspire/NullablePrimitiveArrayProbe.java",
            """
            package aspire;

            import java.io.ByteArrayOutputStream;
            import java.io.InputStream;
            import java.io.PipedInputStream;
            import java.io.PipedOutputStream;
            import java.lang.reflect.Field;
            import java.nio.charset.StandardCharsets;
            import java.util.Arrays;
            import java.util.HashMap;
            import java.util.List;
            import java.util.Map;
            import java.util.concurrent.CompletableFuture;
            import java.util.concurrent.TimeUnit;

            public class NullablePrimitiveArrayProbe {
                public static void main(String[] args) throws Exception {
                    var clientInput = new PipedInputStream(32768);
                    var serverOutput = new PipedOutputStream(clientInput);
                    var serverInput = new PipedInputStream(32768);
                    var clientOutput = new PipedOutputStream(serverInput);

                    var client = new AspireClient("ignored");
                    setField(client, "inputStream", clientInput);
                    setField(client, "outputStream", clientOutput);
                    var resource = new ProbeResource(new Handle("probe", "Tests/ProbeResource"), client);

                    var numbersCall = CompletableFuture.supplyAsync(resource::nullableNumbers);
                    writeResult(serverOutput, extractNumericId(readMessage(serverInput)), "[1,null,2.5]");
                    assertNumbers(numbersCall.get(2, TimeUnit.SECONDS));

                    var flagsCall = CompletableFuture.supplyAsync(resource::nullableFlagMatrix);
                    writeResult(serverOutput, extractNumericId(readMessage(serverInput)), "[[true,null],[false]]");
                    assertFlags(flagsCall.get(2, TimeUnit.SECONDS));

                    var callbackCall = CompletableFuture.runAsync(() ->
                        resource.withArrays((numbers, flags) -> {
                            assertNumbers(numbers);
                            assertFlags(flags);
                        }));
                    String callbackRequest = readMessage(serverInput);
                    int callbackRequestId = extractNumericId(callbackRequest);
                    String callbackId = extractStringProperty(callbackRequest, "callback");
                    writeMessage(
                        serverOutput,
                        "{\"jsonrpc\":\"2.0\",\"id\":9001,\"method\":\"invokeCallback\","
                            + "\"params\":{\"callbackId\":\"" + callbackId + "\","
                            + "\"args\":{\"p0\":[1,null,2.5],\"p1\":[[true,null],[false]]}}}");
                    String callbackResponse = readMessage(serverInput);
                    if (!callbackResponse.contains("\"id\":9001")
                        || !callbackResponse.contains("\"result\":{\"p0\":[1.0,null,2.5],\"p1\":[[true,null],[false]]}")) {
                        throw new IllegalStateException("unexpected callback response: " + callbackResponse);
                    }
                    writeResult(serverOutput, callbackRequestId, "null");
                    callbackCall.get(2, TimeUnit.SECONDS);

                    Map<String, Object> dtoMap = new HashMap<>();
                    dtoMap.put("Numbers", Arrays.asList(1.0, null, 2.5));
                    dtoMap.put("Flags", List.of(Arrays.asList(true, null), List.of(false)));
                    var dto = ArrayDto.fromMap(dtoMap);
                    assertNumbers(dto.getNumbers());
                    assertFlags(dto.getFlags());

                    System.out.println("OK");
                }

                private static void assertNumbers(Number[] values) {
                    if (values.length != 3
                        || values[0].doubleValue() != 1.0
                        || values[1] != null
                        || values[2].doubleValue() != 2.5) {
                        throw new IllegalStateException("unexpected nullable number array");
                    }
                }

                private static void assertFlags(Boolean[][] values) {
                    if (values.length != 2
                        || values[0].length != 2
                        || values[0][0] != true
                        || values[0][1] != null
                        || values[1][0] != false) {
                        throw new IllegalStateException("unexpected nullable boolean matrix");
                    }
                }

                private static void setField(AspireClient client, String name, Object value) throws Exception {
                    Field field = AspireClient.class.getDeclaredField(name);
                    field.setAccessible(true);
                    field.set(client, value);
                }

                // Capability requests have a JSON-RPC shape such as:
                //   {"jsonrpc":"2.0","id":1,"method":"invokeCapability","params":{...}}
                // Callback registrations are nested in params as string-valued properties.
                private static int extractNumericId(String json) {
                    int start = json.indexOf("\"id\":") + 5;
                    int end = start;
                    while (end < json.length() && Character.isDigit(json.charAt(end))) {
                        end++;
                    }
                    return Integer.parseInt(json.substring(start, end));
                }

                private static String extractStringProperty(String json, String property) {
                    String marker = "\"" + property + "\":\"";
                    int start = json.indexOf(marker) + marker.length();
                    return json.substring(start, json.indexOf('\"', start));
                }

                private static void writeResult(PipedOutputStream output, int id, String result) throws Exception {
                    writeMessage(output, "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":" + result + "}");
                }

                private static void writeMessage(PipedOutputStream output, String payload) throws Exception {
                    byte[] body = payload.getBytes(StandardCharsets.UTF_8);
                    output.write(("Content-Length: " + body.length + "\r\n\r\n").getBytes(StandardCharsets.UTF_8));
                    output.write(body);
                    output.flush();
                }

                // Messages are framed as:
                //   Content-Length: <UTF-8 byte count>\r\n\r\n<JSON payload>
                private static String readMessage(InputStream input) throws Exception {
                    int contentLength = -1;
                    while (true) {
                        String line = readLine(input);
                        if (line.isEmpty()) {
                            break;
                        }
                        if (line.startsWith("Content-Length:")) {
                            contentLength = Integer.parseInt(line.substring(15).trim());
                        }
                    }
                    return new String(input.readNBytes(contentLength), StandardCharsets.UTF_8);
                }

                private static String readLine(InputStream input) throws Exception {
                    ByteArrayOutputStream buffer = new ByteArrayOutputStream();
                    while (true) {
                        int ch = input.read();
                        if (ch == '\r') {
                            if (input.read() == '\n') {
                                break;
                            }
                        } else if (ch == '\n' || ch == -1) {
                            break;
                        } else {
                            buffer.write(ch);
                        }
                    }
                    return buffer.toString(StandardCharsets.UTF_8);
                }
            }
            """);

        await workspace.CompileAsync();
        var run = await workspace.RunClassAsync("aspire.NullablePrimitiveArrayProbe", TimeSpan.FromSeconds(6));

        Assert.True(run.TimedOut is false, $"Probe timed out. stdout:{Environment.NewLine}{run.StdOut}{Environment.NewLine}stderr:{Environment.NewLine}{run.StdErr}");
        Assert.True(
            run.ExitCode == 0,
            $"Probe failed with exit code {run.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{run.StdOut}{Environment.NewLine}stderr:{Environment.NewLine}{run.StdErr}");
        Assert.Contains("OK", run.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratedDtoCallbacks_VoidCallbackReturnsMutatedPositionalArguments()
    {
        var contextType = new AtsTypeRef { TypeId = "Tests/MutableContext", Category = AtsTypeCategory.Dto };
        var context = new AtsContext
        {
            Capabilities = [],
            HandleTypes = [],
            DtoTypes =
            [
                new AtsDtoTypeInfo
                {
                    Name = "MutableContext",
                    TypeId = contextType.TypeId!,
                    Properties =
                    [
                        new AtsDtoPropertyInfo
                        {
                            Name = "Value",
                            Type = new AtsTypeRef { TypeId = AtsConstants.String, Category = AtsTypeCategory.Primitive }
                        }
                    ]
                },
                new AtsDtoTypeInfo
                {
                    Name = "CallbackOptions",
                    TypeId = "Tests/CallbackOptions",
                    Properties =
                    [
                        new AtsDtoPropertyInfo
                        {
                            Name = "Callback",
                            Type = new AtsTypeRef { TypeId = "callback", Category = AtsTypeCategory.Callback },
                            IsCallback = true,
                            CallbackParameters =
                            [
                                new AtsCallbackParameterInfo { Name = "context", Type = contextType }
                            ],
                            CallbackReturnType = new AtsTypeRef
                            {
                                TypeId = AtsConstants.Void,
                                Category = AtsTypeCategory.Primitive
                            }
                        }
                    ]
                }
            ],
            EnumTypes = []
        };

        using var workspace = await CreateJavaProbeWorkspaceAsync(
            context,
            "aspire/MutableContext.java",
            "aspire/CallbackOptions.java");
        workspace.WriteSource(
            "aspire/DtoCallbackWriteBackProbe.java",
            """
            package aspire;

            import java.util.Map;
            import java.util.function.Function;

            public class DtoCallbackWriteBackProbe {
                @SuppressWarnings("unchecked")
                public static void main(String[] args) {
                    var options = new CallbackOptions();
                    options.setCallback(context -> context.setValue("after"));

                    var callback = (Function<Object, Object>) options.toMap().get("Callback");
                    var result = (Map<String, Object>) callback.apply(Map.of("Value", "before"));
                    var serializedResult = (Map<String, Object>) AspireClient.serializeValue(result);
                    var context = (Map<String, Object>) serializedResult.get("p0");
                    if (serializedResult.size() != 1 || !"after".equals(context.get("Value"))) {
                        throw new IllegalStateException("unexpected callback result: " + serializedResult);
                    }

                    System.out.println("OK");
                }
            }
            """);

        await workspace.CompileAsync();
        var run = await workspace.RunClassAsync("aspire.DtoCallbackWriteBackProbe", TimeSpan.FromSeconds(6));

        Assert.True(run.TimedOut is false, $"Probe timed out. stdout:{Environment.NewLine}{run.StdOut}{Environment.NewLine}stderr:{Environment.NewLine}{run.StdErr}");
        Assert.True(
            run.ExitCode == 0,
            $"Probe failed with exit code {run.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{run.StdOut}{Environment.NewLine}stderr:{Environment.NewLine}{run.StdErr}");
        Assert.Contains("OK", run.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratedCallbacks_VoidCallbackReturnsMutatedPositionalArguments()
    {
        var resourceType = new AtsTypeRef { TypeId = "Tests/ProbeResource", Category = AtsTypeCategory.Handle };
        var contextType = new AtsTypeRef { TypeId = "Tests/MutableContext", Category = AtsTypeCategory.Dto };
        var callback = new AtsParameterInfo
        {
            Name = "callback",
            IsCallback = true,
            Type = new AtsTypeRef { TypeId = "callback", Category = AtsTypeCategory.Callback },
            CallbackParameters =
            [
                new AtsCallbackParameterInfo { Name = "context", Type = contextType }
            ],
            CallbackReturnType = new AtsTypeRef { TypeId = AtsConstants.Void, Category = AtsTypeCategory.Primitive }
        };
        var capability = new AtsCapabilityInfo
        {
            CapabilityId = "Tests/withCallback",
            MethodName = "withCallback",
            Parameters = [callback],
            ReturnType = new AtsTypeRef { TypeId = AtsConstants.Void, Category = AtsTypeCategory.Primitive },
            TargetTypeId = resourceType.TypeId,
            TargetType = resourceType,
            TargetParameterName = "resource",
            ExpandedTargetTypes = [resourceType],
            CapabilityKind = AtsCapabilityKind.Method
        };
        var context = new AtsContext
        {
            Capabilities = [capability],
            HandleTypes = [new AtsTypeInfo { AtsTypeId = resourceType.TypeId! }],
            DtoTypes =
            [
                new AtsDtoTypeInfo
                {
                    Name = "MutableContext",
                    TypeId = contextType.TypeId!,
                    Properties =
                    [
                        new AtsDtoPropertyInfo
                        {
                            Name = "Value",
                            Type = new AtsTypeRef { TypeId = AtsConstants.String, Category = AtsTypeCategory.Primitive }
                        }
                    ]
                }
            ],
            EnumTypes = []
        };

        using var workspace = await CreateJavaProbeWorkspaceAsync(
            context,
            "aspire/ProbeResource.java",
            "aspire/MutableContext.java");
        workspace.WriteSource(
            "aspire/CallbackWriteBackProbe.java",
            """
            package aspire;

            import java.io.ByteArrayOutputStream;
            import java.io.InputStream;
            import java.io.PipedInputStream;
            import java.io.PipedOutputStream;
            import java.lang.reflect.Field;
            import java.nio.charset.StandardCharsets;
            import java.util.concurrent.CompletableFuture;
            import java.util.concurrent.TimeUnit;

            public class CallbackWriteBackProbe {
                public static void main(String[] args) throws Exception {
                    var clientInput = new PipedInputStream(32768);
                    var serverOutput = new PipedOutputStream(clientInput);
                    var serverInput = new PipedInputStream(32768);
                    var clientOutput = new PipedOutputStream(serverInput);

                    var client = new AspireClient("ignored");
                    setField(client, "inputStream", clientInput);
                    setField(client, "outputStream", clientOutput);
                    var resource = new ProbeResource(new Handle("probe", "Tests/ProbeResource"), client);

                    var invocation = CompletableFuture.runAsync(() ->
                        resource.withCallback(context -> context.setValue("after")));
                    String request = readMessage(serverInput);
                    int requestId = extractNumericId(request);
                    String callbackId = extractStringProperty(request, "callback");

                    writeMessage(serverOutput,
                        "{\"jsonrpc\":\"2.0\",\"id\":9001,\"method\":\"invokeCallback\","
                            + "\"params\":{\"callbackId\":\"" + callbackId + "\","
                            + "\"args\":{\"p0\":{\"Value\":\"before\"}}}}" );

                    String callbackResponse = readMessage(serverInput);
                    if (!callbackResponse.contains("\"id\":9001")
                        || !callbackResponse.contains("\"result\":{\"p0\":{\"Value\":\"after\"}}") ) {
                        throw new IllegalStateException("callback did not return mutated arguments: " + callbackResponse);
                    }

                    writeMessage(serverOutput,
                        "{\"jsonrpc\":\"2.0\",\"id\":" + requestId + ",\"result\":null}");
                    invocation.get(2, TimeUnit.SECONDS);
                    System.out.println("OK");
                }

                private static void setField(AspireClient client, String name, Object value) throws Exception {
                    Field field = AspireClient.class.getDeclaredField(name);
                    field.setAccessible(true);
                    field.set(client, value);
                }

                private static int extractNumericId(String json) {
                    int start = json.indexOf("\"id\":") + 5;
                    int end = start;
                    while (end < json.length() && Character.isDigit(json.charAt(end))) {
                        end++;
                    }
                    return Integer.parseInt(json.substring(start, end));
                }

                private static String extractStringProperty(String json, String property) {
                    String marker = "\"" + property + "\":\"";
                    int start = json.indexOf(marker) + marker.length();
                    return json.substring(start, json.indexOf('\"', start));
                }

                private static void writeMessage(PipedOutputStream output, String payload) throws Exception {
                    byte[] body = payload.getBytes(StandardCharsets.UTF_8);
                    output.write(("Content-Length: " + body.length + "\r\n\r\n").getBytes(StandardCharsets.UTF_8));
                    output.write(body);
                    output.flush();
                }

                private static String readMessage(InputStream input) throws Exception {
                    int contentLength = -1;
                    while (true) {
                        String line = readLine(input);
                        if (line.isEmpty()) {
                            break;
                        }
                        if (line.startsWith("Content-Length:")) {
                            contentLength = Integer.parseInt(line.substring(15).trim());
                        }
                    }
                    return new String(input.readNBytes(contentLength), StandardCharsets.UTF_8);
                }

                private static String readLine(InputStream input) throws Exception {
                    ByteArrayOutputStream buffer = new ByteArrayOutputStream();
                    while (true) {
                        int ch = input.read();
                        if (ch == '\r') {
                            if (input.read() == '\n') {
                                break;
                            }
                        } else if (ch == '\n' || ch == -1) {
                            break;
                        } else {
                            buffer.write(ch);
                        }
                    }
                    return buffer.toString(StandardCharsets.UTF_8);
                }
            }
            """);

        await workspace.CompileAsync();
        var run = await workspace.RunClassAsync("aspire.CallbackWriteBackProbe", TimeSpan.FromSeconds(6));

        Assert.True(run.TimedOut is false, $"Probe timed out. stdout:{Environment.NewLine}{run.StdOut}{Environment.NewLine}stderr:{Environment.NewLine}{run.StdErr}");
        Assert.True(
            run.ExitCode == 0,
            $"Probe failed with exit code {run.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{run.StdOut}{Environment.NewLine}stderr:{Environment.NewLine}{run.StdErr}");
        Assert.Contains("OK", run.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratedSupportApis_AreAccessibleFromExternalPackages()
    {
        using var workspace = await CreateJavaProbeWorkspaceAsync();
        workspace.WriteSource(
            "external/SupportApiProbe.java",
            """
            package external;

            import aspire.AspireUnion;
            import aspire.CapabilityError;

            public class SupportApiProbe {
                public static void main(String[] args) {
                    AspireUnion union = AspireUnion.of("value");
                    AspireUnion transported = AspireUnion.fromValue(union);
                    if (!transported.is(String.class)
                        || !"value".equals(transported.getValue())
                        || !"value".equals(transported.getValueAs(String.class))) {
                        throw new IllegalStateException("union API returned unexpected values");
                    }

                    System.out.println("OK");
                }

                static String inspect(CapabilityError error) {
                    return error.getCode() + ":" + error.getData();
                }
            }
            """);

        await workspace.CompileAsync();
        var run = await workspace.RunClassAsync("external.SupportApiProbe", TimeSpan.FromSeconds(6));

        Assert.True(run.TimedOut is false, $"Probe timed out. stdout:{Environment.NewLine}{run.StdOut}{Environment.NewLine}stderr:{Environment.NewLine}{run.StdErr}");
        Assert.True(
            run.ExitCode == 0,
            $"Probe failed with exit code {run.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{run.StdOut}{Environment.NewLine}stderr:{Environment.NewLine}{run.StdErr}");
        Assert.Contains("OK", run.StdOut, StringComparison.Ordinal);
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

    [Fact]
    public async Task ExportedNullablePrimitiveArraysUseBoxedInitializers()
    {
        var nullableNumbers = Assert.IsType<AtsTypeRef>(AtsCapabilityScanner.CreateTypeRef(typeof(double?[])));
        var nullableFlags = Assert.IsType<AtsTypeRef>(AtsCapabilityScanner.CreateTypeRef(typeof(bool?[])));
        var context = new AtsContext
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
                },
                new AtsExportedValueInfo
                {
                    OwningAssemblyName = TestTypesAssemblyName,
                    PathSegments = ["NullableArrays", "Flags"],
                    Value = JsonNode.Parse("[true,null,false]"),
                    Type = nullableFlags
                }
            ]
        };

        var generated = _generator.GenerateDistributedApplication(context)["aspire/NullableArrays.java"];

        Assert.Contains("Number[] Numbers = new Number[] { 1, null, 2.5 }", generated, StringComparison.Ordinal);
        Assert.Contains("Boolean[] Flags = new Boolean[] { true, null, false }", generated, StringComparison.Ordinal);

        using var workspace = await CreateJavaProbeWorkspaceAsync(context, "aspire/NullableArrays.java");
        await workspace.CompileAsync();
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

    private static AtsContext CreateContextWithProbeCapabilities(AtsTypeRef resourceType, params AtsCapabilityInfo[] capabilities)
    {
        return new AtsContext
        {
            Capabilities = capabilities,
            HandleTypes = [new AtsTypeInfo { AtsTypeId = resourceType.TypeId! }],
            DtoTypes = [],
            EnumTypes = []
        };
    }

    private static AtsCapabilityInfo CreateProbeCapability(AtsTypeRef resourceType, string methodName, AtsTypeRef returnType)
    {
        return new AtsCapabilityInfo
        {
            CapabilityId = $"Tests/{methodName}",
            MethodName = methodName,
            Parameters = [],
            ReturnType = returnType,
            TargetTypeId = resourceType.TypeId,
            TargetType = resourceType,
            TargetParameterName = "resource",
            ExpandedTargetTypes = [resourceType],
            CapabilityKind = AtsCapabilityKind.Method
        };
    }

    private static async Task<JavaProbeWorkspace> CreateJavaProbeWorkspaceAsync(
        AtsContext? context = null,
        params string[] additionalFiles)
    {
        var workspace = new JavaProbeWorkspace();
        var files = new AtsJavaCodeGenerator().GenerateDistributedApplication(context ?? new AtsContext
        {
            Capabilities = [],
            HandleTypes = [],
            EnumTypes = [],
            DtoTypes = [],
            ExportedValues = []
        });

        string[] transportFiles =
        [
            "aspire/AspireClient.java",
            "aspire/Handle.java",
            "aspire/CapabilityError.java",
            "aspire/CancellationToken.java",
            "aspire/JsonSerializable.java",
            "aspire/HandleWrapperBase.java",
            "aspire/ReferenceExpression.java",
            "aspire/AspireUnion.java",
            "aspire/AspireAction1.java",
            "aspire/WireValueEnum.java",
        ];

        foreach (var path in transportFiles.Concat(additionalFiles))
        {
            if (files.TryGetValue(path, out var content))
            {
                workspace.WriteSource(path, content);
            }
        }

        await Task.CompletedTask;
        return workspace;
    }

    private sealed class JavaProbeWorkspace : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("aspire-java-transport-probe-");
        private readonly DirectoryInfo _classes;

        public JavaProbeWorkspace()
        {
            _classes = Directory.CreateDirectory(Path.Combine(_root.FullName, "classes"));
        }

        public void WriteSource(string relativePath, string content)
        {
            var path = Path.Combine(_root.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public async Task CompileAsync()
        {
            var sourceFiles = Directory
                .EnumerateFiles(_root.FullName, "*.java", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(_root.FullName, path))
                .Order(StringComparer.Ordinal)
                .ToArray();

            var sourcesFile = Path.Combine(_root.FullName, "sources.txt");
            await File.WriteAllLinesAsync(sourcesFile, sourceFiles);

            // These probes target the minimum supported Java API level, independently of the Java 25 single-file AppHost contract.
            var compile = await RunProcessAsync(
                "javac",
                ["--release", "21", "-d", _classes.FullName, "@sources.txt"],
                TimeSpan.FromSeconds(30));

            Assert.True(compile.TimedOut is false, $"javac timed out. stdout:{Environment.NewLine}{compile.StdOut}{Environment.NewLine}stderr:{Environment.NewLine}{compile.StdErr}");
            Assert.True(
                compile.ExitCode == 0,
                $"javac failed with exit code {compile.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{compile.StdOut}{Environment.NewLine}stderr:{Environment.NewLine}{compile.StdErr}");
        }

        public Task<ProcessResult> RunClassAsync(string className, TimeSpan timeout)
            => RunProcessAsync("java", ["-cp", _classes.FullName, className], timeout);

        public void Dispose()
        {
            try
            {
                _root.Delete(recursive: true);
            }
            catch (IOException)
            {
                // Best effort temp cleanup.
            }
        }

        private async Task<ProcessResult> RunProcessAsync(string fileName, string[] arguments, TimeSpan timeout)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = _root.FullName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            foreach (var arg in arguments)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var stdOutTask = process.StandardOutput.ReadToEndAsync();
            var stdErrTask = process.StandardError.ReadToEndAsync();

            using var timeoutCts = new CancellationTokenSource(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
                return new ProcessResult(process.ExitCode, await stdOutTask, await stdErrTask, TimedOut: false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Ignore failures while attempting to kill timed-out process.
                }

                return new ProcessResult(-1, await stdOutTask, await stdErrTask, TimedOut: true);
            }
        }
    }

    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);
}
