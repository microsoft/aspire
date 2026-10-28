// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RemoteHost;
using Aspire.TestUtilities;
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
        Assert.Equal(
            ["go.mod", "transport.go", "base.go", "aspire.go"],
            files.Keys);

        var generatedCode = string.Join(
            "\n",
            files.OrderBy(file => file.Key, StringComparer.Ordinal)
                .Select(file => $"// ===== {file.Key} =====\n{file.Value}"));

        await Verify(generatedCode, extension: "go")
            .UseFileName("AtsGeneratedAspire");
    }

    [Fact]
    public void GenerateDistributedApplication_WritesFilesWithLfLineEndings()
    {
        var files = _generator.GenerateDistributedApplication(CreateContextFromTestAssembly());

        // The files mix raw string literals, StringWriter output and embedded resources, which take the line endings
        // of the machine that built or runs the generator (CRLF on Windows), while gofmt reports CRLF as a
        // formatting difference.
        Assert.Empty(files.Where(file => file.Value.Contains('\r')).Select(file => file.Key));
    }

    [Fact]
    [RequiresTools(["go"])]
    public async Task GeneratedTerminalDto_RoundTripsCallbackWireNames()
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
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "terminal_test.go"), """
                package aspire

                import (
                    "encoding/json"
                    "reflect"
                    "testing"
                )

                func TestTerminalDto(t *testing.T) {
                    var payload map[string]any
                    if err := json.Unmarshal([]byte(`{"p0":{"columns":132,"rows":50}}`), &payload); err != nil {
                        t.Fatal(err)
                    }
                    options := callbackArg[*TerminalOptions]([]any{payload["p0"]}, 0)
                    if options == nil || options.Columns != 132 || options.Rows != 50 {
                        t.Fatalf("unexpected callback argument: %#v", options)
                    }
                    options.Columns = 200
                    result := map[string]any{"p0": serializeValue(options)}
                    expected := map[string]any{"p0": map[string]any{"columns": float64(200), "rows": float64(50)}}
                    if !reflect.DeepEqual(expected, result) {
                        t.Fatalf("expected %#v, got %#v", expected, result)
                    }
                }
                """, timeout.Token);
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("go")
                {
                    WorkingDirectory = directory.FullName,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    ArgumentList = { "test", "-timeout", "30s", "-run", "^TestTerminalDto$", "." }
                }
            };
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                Assert.True(process.ExitCode == 0, $"Go terminal callback test failed:{Environment.NewLine}{await stdout}{await stderr}");
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
    [RequiresTools(["go"])]
    public async Task GeneratedTransport_SupportsDuplexCommunication()
    {
        var directory = Directory.CreateTempSubdirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var process = new Process();
        try
        {
            var files = _generator.GenerateDistributedApplication(new AtsContext
            {
                Capabilities = [],
                HandleTypes = [],
                EnumTypes = [],
                DtoTypes = []
            });
            foreach (var (name, content) in files)
            {
                await File.WriteAllTextAsync(Path.Combine(directory.FullName, name), content, timeout.Token);
            }

            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "transport_test.go"), """
                package aspire

                import (
                    "io"
                    "os"
                    "testing"
                    "time"
                )

                func TestDuplexConnection(t *testing.T) {
                    conn, err := openConnection(os.Getenv("TEST_SOCKET_PATH"), 5*time.Second)
                    if err != nil {
                        t.Fatal(err)
                    }
                    defer conn.Close()

                    // Deadline support proves the Windows handle is pollable rather than
                    // synchronous, without relying on which I/O goroutine runs first.
                    deadlines, ok := conn.(interface { SetDeadline(time.Time) error })
                    if !ok {
                        t.Fatal("connection does not support deadlines")
                    }
                    if err := deadlines.SetDeadline(time.Now().Add(10*time.Second)); err != nil {
                        t.Fatal(err)
                    }

                    received := make(chan error, 1)
                    go func() {
                        response := make([]byte, 4)
                        _, err := io.ReadFull(conn, response)
                        if err == nil && string(response) != "pong" {
                            t.Errorf("expected pong, got %q", response)
                        }
                        received <- err
                    }()
                    if _, err := conn.Write([]byte("ping")); err != nil {
                        t.Fatal(err)
                    }
                    if err := <-received; err != nil {
                        t.Fatal(err)
                    }

                    // Closing a connection must release a pending background read.
                    go func() {
                        _, err := conn.Read(make([]byte, 1))
                        received <- err
                    }()
                    if err := conn.Close(); err != nil {
                        t.Fatal(err)
                    }
                    select {
                    case err := <-received:
                        if err == nil {
                            t.Fatal("expected the read to fail after close")
                        }
                    case <-time.After(5*time.Second):
                        t.Fatal("close did not unblock the reader")
                    }
                }
                """, timeout.Token);

            var socketPath = OperatingSystem.IsWindows()
                ? $"aspire-go-{Guid.NewGuid():N}"
                : Path.Combine(directory.FullName, "socket");
            using var pipe = OperatingSystem.IsWindows()
                ? new NamedPipeServerStream(socketPath, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous)
                : null;
            using var socket = OperatingSystem.IsWindows()
                ? null
                : new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            if (socket is not null)
            {
                socket.Bind(new UnixDomainSocketEndPoint(socketPath));
                socket.Listen(1);
            }

            process.StartInfo = new ProcessStartInfo("go")
            {
                WorkingDirectory = directory.FullName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                ArgumentList = { "test", "-timeout", "30s", "." },
                Environment = { ["TEST_SOCKET_PATH"] = socketPath }
            };
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            var exchange = ExchangeAsync();
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                Assert.True(process.ExitCode == 0, $"Go transport test failed:{Environment.NewLine}{await stdout}{await stderr}");
                await exchange;
            }
            finally
            {
                await timeout.CancelAsync();
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
                await exchange.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            }

            async Task ExchangeAsync()
            {
                using var stream = pipe is not null
                    ? await AcceptPipeAsync()
                    : new NetworkStream(await socket!.AcceptAsync(timeout.Token), ownsSocket: true);
                var request = new byte[4];
                await stream.ReadExactlyAsync(request, timeout.Token);
                Assert.Equal("ping"u8.ToArray(), request);
                await stream.WriteAsync("pong"u8.ToArray(), timeout.Token);

                // Keep the server connected until the client closes its pending read.
                Assert.Equal(0, await stream.ReadAsync(new byte[1], timeout.Token));
            }

            async Task<Stream> AcceptPipeAsync()
            {
                await pipe!.WaitForConnectionAsync(timeout.Token);
                return pipe;
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    [RequiresTools(["go"])]
    public async Task GeneratedRuntime_PassesGoTests()
    {
        var directory = Directory.CreateTempSubdirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            var files = _generator.GenerateDistributedApplication(CreateContextFromTestAssembly());
            foreach (var (name, content) in files)
            {
                await File.WriteAllTextAsync(Path.Combine(directory.FullName, name), content, timeout.Token);
            }

            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "runtime_test.go"), """
                package aspire

                import (
                    "reflect"
                    "strings"
                    "testing"
                )

                func TestDeepUpdateKeepsCallback(t *testing.T) {
                    calls := 0
                    merged := deepUpdate(&WithOptionalCallbackOptions{}, &WithOptionalCallbackOptions{
                        Callback: func(TestCallbackContext) { calls++ },
                    })
                    if merged.Callback == nil {
                        t.Fatal("callback was dropped")
                    }
                    merged.Callback(nil)
                    if calls != 1 {
                        t.Fatalf("expected the callback to run once, ran %d times", calls)
                    }
                }

                func TestDeepUpdateAcceptsNilCallback(t *testing.T) {
                    merged := deepUpdate(&WithOptionalCallbackOptions{}, &WithOptionalCallbackOptions{})
                    if merged.Callback != nil {
                        t.Fatal("unexpected callback")
                    }
                }

                func TestDeepUpdateKeepsCancellationToken(t *testing.T) {
                    token := NewCancellationToken()
                    merged := deepUpdate(&GetStatusAsyncOptions{}, &GetStatusAsyncOptions{CancellationToken: token})
                    if merged.CancellationToken != token {
                        t.Fatal("cancellation token was replaced")
                    }
                }

                func TestDeepUpdateMergesNestedValuesWithoutModifyingInputs(t *testing.T) {
                    dst := &TestNestedDto{Id: "dst", Config: &TestConfigDto{Name: "dst"}, Counts: map[string]float64{"a": 1, "b": 2}}
                    src := &TestNestedDto{Id: "src", Config: &TestConfigDto{Name: "src"}, Counts: map[string]float64{"b": 3, "c": 4}}

                    merged := deepUpdate(dst, src)

                    want := &TestNestedDto{Id: "src", Config: &TestConfigDto{Name: "src"}, Counts: map[string]float64{"a": 1, "b": 3, "c": 4}}
                    if !reflect.DeepEqual(merged, want) {
                        t.Fatalf("got %+v, want %+v", merged, want)
                    }
                    if len(dst.Counts) != 2 || len(src.Counts) != 2 || dst.Id != "dst" || dst.Config.Name != "dst" {
                        t.Fatal("merge modified its inputs")
                    }
                }

                type namedHandle interface{ Name() string }

                type namedValue string

                func (n namedValue) Name() string { return string(n) }

                // workDto and nestedCallbackOptions mirror generated DTOs with callback
                // properties and optional handle parameters, which the test types do not have.
                type workDto struct {
                    Label *string `json:"label,omitempty"`
                    Work  func()  `json:"work,omitempty"`
                }

                type nestedCallbackOptions struct {
                    Progress *workDto     `json:"progress,omitempty"`
                    Handle   *namedHandle `json:"handle,omitempty"`
                }

                func TestDeepUpdateKeepsCallbacksAndHandlesInNestedValues(t *testing.T) {
                    label := "step"
                    ran := false
                    var handle namedHandle = namedValue("resource")
                    src := &nestedCallbackOptions{
                        Progress: &workDto{Label: &label, Work: func() { ran = true }},
                        Handle:   &handle,
                    }

                    merged := deepUpdate(&nestedCallbackOptions{Progress: &workDto{}}, src)

                    merged.Progress.Work()
                    if !ran || *merged.Progress.Label != label || (*merged.Handle).Name() != "resource" {
                        t.Fatalf("nested callback or handle was lost: %+v", merged)
                    }
                }

                // The client has no connection, so a value that passes the union guard fails later with
                // a transport error, while a value that does not pass it fails with the guard's error.
                func scalarFieldError(value any) error {
                    resource := newTestRedisResourceFromHandle(&handle{HandleID: "1", TypeID: "type"}, newClient(""))
                    resource.WithScalarField("spec.maxReplicaCount", value)
                    return resource.Err()
                }

                func TestUnionGuardAcceptsEveryGoNumericType(t *testing.T) {
                    values := []any{
                        "text", true, 1.5, float32(1.5), 3, int8(3), int16(3), int32(3), int64(3),
                        uint(3), uint8(3), uint16(3), uint32(3), uint64(3),
                    }
                    for _, value := range values {
                        err := scalarFieldError(value)
                        if err == nil || !strings.Contains(err.Error(), "not connected to AppHost") {
                            t.Errorf("%T: got %v, want the transport error", value, err)
                        }
                    }
                }

                func TestUnionGuardRejectsOtherTypes(t *testing.T) {
                    for _, value := range []any{nil, []string{"a"}, map[string]any{"a": 1}} {
                        err := scalarFieldError(value)
                        if err == nil || !strings.Contains(err.Error(), "must be one of [string, float64, bool]") {
                            t.Errorf("%T: got %v, want the guard error", value, err)
                        }
                    }
                }
                """, timeout.Token);

            using var process = Process.Start(new ProcessStartInfo("go")
            {
                WorkingDirectory = directory.FullName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                ArgumentList = { "test", "-timeout", "30s", "." }
            })!;
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                Assert.True(process.ExitCode == 0, $"Go runtime test failed:{Environment.NewLine}{await stdout}{await stderr}");
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
    public void GenerateDistributedApplication_NullablePrimitiveArrayElementsUsePointers()
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

        var generated = _generator.GenerateDistributedApplication(atsContext)["aspire.go"];

        Assert.Contains("Values []*float64", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportedNullablePrimitiveArraysUsePointerInitializers()
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

        var generated = _generator.GenerateDistributedApplication(atsContext)["aspire.go"];

        Assert.Contains(
            "[]*float64{func(value float64) *float64 { return &value }(1), nil, func(value float64) *float64 { return &value }(2.5)}",
            generated,
            StringComparison.Ordinal);
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
    public void GeneratedCode_CreateBuilderDefaultsAppHostFilePathFromEnvironment()
    {
        var atsContext = CreateContextFromBothAssemblies();

        var files = _generator.GenerateDistributedApplication(atsContext);
        var aspireGo = files["aspire.go"];

        Assert.Contains("if appHostFilePath, ok := resolved[\"appHostFilePath\"].(string); !ok || appHostFilePath == \"\"", aspireGo);
        Assert.Contains("os.Getenv(\"ASPIRE_APPHOST_FILEPATH\")", aspireGo);
        Assert.Contains("resolved[\"appHostFilePath\"] = appHostFilePath", aspireGo);
    }

    [Fact]
    public void GeneratedCode_CreateBuilderOmitsEmptyDashboardApplicationName()
    {
        var atsContext = CreateContextFromBothAssemblies();

        var files = _generator.GenerateDistributedApplication(atsContext);
        var aspireGo = files["aspire.go"];

        Assert.Contains("if dashboardApplicationName, ok := resolved[\"dashboardApplicationName\"].(string); ok && dashboardApplicationName == \"\"", aspireGo);
        Assert.Contains("delete(resolved, \"dashboardApplicationName\")", aspireGo);
    }

    [Fact]
    public void GeneratedCode_DtoCallbacksReturnMutatedArguments()
    {
        var atsContext = CreateContextFromBothAssemblies();

        var files = _generator.GenerateDistributedApplication(atsContext);
        var aspireGo = files["aspire.go"];

        Assert.Contains("arg0 := callbackArg[*ResourceUrlAnnotation](args, 0)", aspireGo);
        Assert.Contains("cb(arg0)", aspireGo);
        Assert.Contains("\"p0\": serializeValue(arg0)", aspireGo);
    }

    [Fact]
    public void GeneratedCode_CallbackShimsBuildTheDeclaredResourceBuilderWrapper()
    {
        var atsContext = CreateContextFromBothAssemblies();

        var files = _generator.GenerateDistributedApplication(atsContext);
        var aspireGo = files["aspire.go"].ReplaceLineEndings("\n");
        var shim = ExtractGeneratedMethod(
            aspireGo,
            "func (s *executableResource) PublishAsDockerFile(configure func(obj ContainerResource))");

        Assert.Contains(
            "cb(callbackHandleArg(s.client, args, 0, newContainerResourceFromHandle))",
            shim,
            StringComparison.Ordinal);
        Assert.Contains("func callbackHandleArg[T any](", files["base.go"], StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["go"])]
    public async Task GeneratedCallbacks_ReceiveResourceBuilderWrapper()
    {
        var directory = Directory.CreateTempSubdirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var process = new Process();
        try
        {
            var files = _generator.GenerateDistributedApplication(CreateContextFromBothAssemblies());
            foreach (var (name, content) in files)
            {
                await File.WriteAllTextAsync(Path.Combine(directory.FullName, name), content, timeout.Token);
            }

            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "callback_test.go"), """
                package aspire

                import (
                    "bufio"
                    "net"
                    "os"
                    "testing"
                )

                // recordedCall is a capability invoked by the callback under test.
                type recordedCall struct {
                    capability string
                    builder    any
                }

                // connectFakeAppHost connects a client to an in-memory AppHost. When the SDK
                // invokes publishAsDockerFile, the AppHost calls the registered callback with a
                // resource builder handle that carries callbackHandleType, and completes the
                // capability once the callback returns. Every other capability is recorded and
                // answered with its builder argument.
                func connectFakeAppHost(t *testing.T, callbackHandleType string) (*client, <-chan recordedCall) {
                    t.Helper()
                    clientEnd, serverEnd := net.Pipe()
                    c := newClient("")
                    registerWrappers(c)
                    conn := newConnection(clientEnd, c, c.onConnectionClose)
                    c.mu.Lock()
                    c.conn = conn
                    c.mu.Unlock()
                    conn.start()
                    t.Cleanup(c.disconnect)

                    calls := make(chan recordedCall, 8)
                    go func() {
                        defer serverEnd.Close()
                        reader := bufio.NewReader(serverEnd)
                        var publishID, publishBuilder any
                        for {
                            msg, err := readMessage(reader)
                            if err != nil {
                                return
                            }
                            if _, isRequest := msg["method"]; !isRequest {
                                // The callback returned, so the capability that invoked it can complete.
                                writeMessage(serverEnd, map[string]any{"jsonrpc": "2.0", "id": publishID, "result": publishBuilder})
                                continue
                            }
                            params := msg["params"].([]any)
                            capability := params[0].(string)
                            args := params[1].(map[string]any)
                            if capability != "Aspire.Hosting/publishAsDockerFile" {
                                calls <- recordedCall{capability, args["builder"]}
                                writeMessage(serverEnd, map[string]any{"jsonrpc": "2.0", "id": msg["id"], "result": args["builder"]})
                                continue
                            }
                            publishID, publishBuilder = msg["id"], args["builder"]
                            writeMessage(serverEnd, map[string]any{
                                "jsonrpc": "2.0",
                                "id":      1000,
                                "method":  "invokeCallback",
                                "params": []any{
                                    args["configure"],
                                    map[string]any{"p0": map[string]any{"$handle": "callback-resource", "$type": callbackHandleType}},
                                },
                            })
                        }
                    }()
                    return c, calls
                }

                func TestCallbackReceivesResourceBuilderWrapper(t *testing.T) {
                    // The AppHost marshals an IResourceBuilder<T> callback argument with its closed
                    // generic CLR type as the $type, and no wrapper is registered under that id.
                    // An argument whose $type is registered arrives already wrapped.
                    handleTypes := map[string]string{
                        "closed generic type": os.Getenv("TEST_RESOURCE_BUILDER_TYPE"),
                        "registered type":     "Aspire.Hosting/Aspire.Hosting.ApplicationModel.ContainerResource",
                    }
                    for name, handleType := range handleTypes {
                        t.Run(name, func(t *testing.T) {
                            if handleType == "" {
                                t.Fatal("TEST_RESOURCE_BUILDER_TYPE is not set")
                            }
                            c, calls := connectFakeAppHost(t, handleType)
                            root := newExecutableResourceFromHandle(&handle{
                                HandleID: "root-resource",
                                TypeID:   "Aspire.Hosting/Aspire.Hosting.ApplicationModel.ExecutableResource",
                            }, c)

                            received := make(chan ContainerResource, 1)
                            root.PublishAsDockerFile(func(obj ContainerResource) {
                                received <- obj
                                if obj != nil {
                                    obj.WithEntrypoint("/bin/app")
                                }
                            })
                            if err := root.Err(); err != nil {
                                t.Fatalf("PublishAsDockerFile failed: %v", err)
                            }

                            // PublishAsDockerFile returns only after the callback has returned.
                            select {
                            case obj := <-received:
                                if obj == nil {
                                    t.Fatal("the callback received a nil ContainerResource")
                                }
                            default:
                                t.Fatal("the callback was not invoked")
                            }
                            select {
                            case call := <-calls:
                                builder, _ := call.builder.(map[string]any)
                                if call.capability != "Aspire.Hosting/withEntrypoint" || builder["$handle"] != "callback-resource" {
                                    t.Fatalf("unexpected follow-up call %s on %v", call.capability, call.builder)
                                }
                            default:
                                t.Fatal("the callback wrapper did not invoke a capability")
                            }
                        })
                    }
                }
                """, timeout.Token);

            process.StartInfo = new ProcessStartInfo("go")
            {
                WorkingDirectory = directory.FullName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                ArgumentList = { "test", "-timeout", "30s", "." },
                // The same id the AppHost derives for an IResourceBuilder<T> callback parameter.
                Environment = { ["TEST_RESOURCE_BUILDER_TYPE"] = AtsTypeMapping.DeriveTypeId(typeof(IResourceBuilder<ContainerResource>)) }
            };
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                Assert.True(process.ExitCode == 0, $"Go callback test failed:{Environment.NewLine}{await stdout}{await stderr}");
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
    public void GeneratedCode_RequiredNullablePrimitiveAndEnumSettersSendNullValues()
    {
        var atsContext = CreateContextFromBothAssemblies();
        var aspireGo = _generator.GenerateDistributedApplication(atsContext)["aspire.go"].ReplaceLineEndings("\n");
        var primitiveSetter = ExtractGeneratedMethod(
            aspireGo,
            "func (s *endpointUpdateContext) SetPort(value *float64)");
        var enumSetter = ExtractGeneratedMethod(
            aspireGo,
            "func (s *containerBuildOptionsCallbackContext) SetDestination(value *ContainerImageDestination)");

        Assert.Contains("reqArgs[\"value\"] = serializeValue(value)", primitiveSetter, StringComparison.Ordinal);
        Assert.DoesNotContain("if value != nil", primitiveSetter, StringComparison.Ordinal);
        Assert.Contains("reqArgs[\"value\"] = serializeValue(value)", enumSetter, StringComparison.Ordinal);
        Assert.DoesNotContain("if value != nil", enumSetter, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        "func (s *aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource) WithConfig(config *TestConfigDto)",
        "config",
        "config != nil")]
    [InlineData(
        "func (s *testRedisResource) WithConnectionString(connectionString *ReferenceExpression)",
        "connectionString",
        "connectionString != nil")]
    [InlineData(
        "func (s *aspire_Hosting_CodeGeneration_Go_TestsTestVaultResource) WithUrl(url any",
        "url",
        "!isNil(url)")]
    public void GeneratedCode_RequiredNonNullableNilableArgumentsOmitNilValues(
        string signature,
        string parameterName,
        string nilGuard)
    {
        var atsContext = CreateContextFromBothAssemblies();
        var files = _generator.GenerateDistributedApplication(atsContext);
        var aspireGo = files["aspire.go"].ReplaceLineEndings("\n");
        var method = ExtractGeneratedMethod(aspireGo, signature);

        Assert.Contains(
            $"if {nilGuard} {{ reqArgs[\"{parameterName}\"] = serializeValue({parameterName}) }}",
            method,
            StringComparison.Ordinal);
        Assert.Contains("func isNil(value any) bool", files["base.go"], StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedCode_InteractionInputValueHelpersHandleNullableValues()
    {
        var atsContext = CreateContextFromBothAssemblies();
        var aspireGo = _generator.GenerateDistributedApplication(atsContext)["aspire.go"].ReplaceLineEndings("\n");

        Assert.Contains(
            "func (s *interactionInputCollection) Value(name string) (string, error) {\n" +
            "\tinput, err := s.Get(name)\n" +
            "\tif err != nil { return \"\", err }\n" +
            "\tif input == nil || input.Value == nil { return \"\", nil }\n" +
            "\treturn *input.Value, nil\n" +
            "}",
            aspireGo,
            StringComparison.Ordinal);
        Assert.Contains(
            "func (s *interactionInputCollection) RequiredValue(name string) (string, error) {\n" +
            "\tinput, err := s.Required(name)\n" +
            "\tif err != nil { return \"\", err }\n" +
            "\tif input.Value == nil { return \"\", nil }\n" +
            "\treturn *input.Value, nil\n" +
            "}",
            aspireGo,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedCode_CallbackArgsSkipUndecodableStructFields()
    {
        var atsContext = CreateContextFromBothAssemblies();

        var files = _generator.GenerateDistributedApplication(atsContext);
        var baseGo = files["base.go"];

        Assert.Contains("func decodeStructFields[T any](raw any) (T, bool)", baseGo);
        Assert.Contains("fieldInfo.Tag.Get(\"json\")", baseGo);
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

    [Fact]
    public void GeneratedCode_FlattensSingleOptionalDtoOptionsParameter()
    {
        // WithHttpCommand has a single optional "options" DTO, so it flattens: the DTO is threaded
        // directly instead of through a wrapper struct (issue #17664), matching the TypeScript output.
        var atsContext = CreateContextFromBothAssemblies();

        var files = _generator.GenerateDistributedApplication(atsContext);
        var aspireGo = files["aspire.go"];

        // Signature threads the DTO directly; no wrapper struct is emitted.
        Assert.Contains("WithHttpCommand(path string, displayName string, options ...*HttpCommandExportOptions)", aspireGo);

        // The merged DTO is sent under the original "options" arg, but only when a non-nil option
        // was merged, so an all-nil variadic omits the key (matching the old wrapper's ToMap()).
        Assert.Contains("applied := false", aspireGo);
        Assert.Contains("if applied { reqArgs[\"options\"] = serializeValue(merged) }", aspireGo);
    }

    [Fact]
    public void GeneratedCode_DoesNotFlattenWhenOptionsCoexistsWithCancellationToken()
    {
        // PromptInput's only non-cancellation-token optional is the "options" DTO, but Go models a
        // trailing cancellation token as another variadic element, so its single-variadic rule keeps
        // the wrapper. TypeScript threads the token separately and would flatten this capability.
        var atsContext = CreateContextFromBothAssemblies();

        var files = _generator.GenerateDistributedApplication(atsContext);
        var aspireGo = files["aspire.go"];

        Assert.Contains("PromptInput(title string, message string, input InteractionInputBuilder, options ...*PromptInputOptions)", aspireGo);
        Assert.Contains("type PromptInputOptions struct", aspireGo);
        Assert.Contains("Options *InteractionInputsDialogOptions `json:\"options,omitempty\"`", aspireGo);
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

    private static string ExtractGeneratedMethod(string generatedCode, string signature)
    {
        var methodStart = generatedCode.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(methodStart >= 0, $"Generated method not found: {signature}");
        var methodEnd = generatedCode.IndexOf("\n}\n", methodStart, StringComparison.Ordinal);
        Assert.True(methodEnd >= 0, $"Generated method is incomplete: {signature}");

        return generatedCode[methodStart..methodEnd];
    }

}
