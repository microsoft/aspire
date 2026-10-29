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
    public async Task GeneratedRuntime_PassesGoRuntimeHardeningTests()
    {
        var directory = Directory.CreateTempSubdirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            // The tests only exercise transport.go and base.go, so an empty model is enough.
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

            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "transport_hardening_test.go"), """
                package aspire

                import (
                    "bufio"
                    "bytes"
                    "context"
                    "errors"
                    "net"
                    "runtime"
                    "strconv"
                    "strings"
                    "sync"
                    "testing"
                    "time"
                )

                // hostTimeout bounds one read or write by the fake host, so a stuck exchange fails
                // quickly instead of waiting for the go test timeout. It is applied per operation:
                // a single deadline set at construction would also cut off a long loop on a slow runner.
                const hostTimeout = 10 * time.Second

                // fakeHost is the AppHost end of an in-memory connection to a client.
                type fakeHost struct {
                    t      *testing.T
                    peer   net.Conn
                    reader *bufio.Reader
                }

                func newFakeHost(t *testing.T) (*client, *fakeHost) {
                    t.Helper()
                    c := newClient("")
                    server, peer := net.Pipe()
                    conn := newConnection(server, c, c.onConnectionClose)
                    c.conn = conn
                    conn.start()
                    t.Cleanup(func() {
                        c.disconnect()
                        peer.Close()
                    })
                    return c, &fakeHost{t: t, peer: peer, reader: bufio.NewReader(peer)}
                }

                func (h *fakeHost) read() map[string]any {
                    h.t.Helper()
                    if err := h.peer.SetReadDeadline(time.Now().Add(hostTimeout)); err != nil {
                        h.t.Fatal(err)
                    }
                    msg, err := readMessage(h.reader)
                    if err != nil {
                        h.t.Fatalf("reading from the client: %v", err)
                    }
                    return msg
                }

                func (h *fakeHost) write(msg map[string]any) {
                    h.t.Helper()
                    if err := h.peer.SetWriteDeadline(time.Now().Add(hostTimeout)); err != nil {
                        h.t.Fatal(err)
                    }
                    if err := writeMessage(h.peer, msg); err != nil {
                        h.t.Fatalf("writing to the client: %v", err)
                    }
                }

                func callbackRequest(id int, callbackID string, args any) map[string]any {
                    return map[string]any{"jsonrpc": "2.0", "id": id, "method": "invokeCallback", "params": []any{callbackID, args}}
                }

                func TestPanickingCallbackIsAnsweredWithError(t *testing.T) {
                    c, host := newFakeHost(t)
                    var nilMap map[string]int
                    cases := []struct {
                        name     string
                        callback func(...any) any
                        want     string
                    }{
                        {"string", func(...any) any { panic("boom") }, "Callback execution failed: boom"},
                        {"error", func(...any) any { panic(errors.New("bad input")) }, "Callback execution failed: bad input"},
                        {"runtime error", func(...any) any { nilMap["k"] = 1; return nil }, "Callback execution failed: assignment to entry in nil map"},
                    }
                    for i, tc := range cases {
                        host.write(callbackRequest(i+1, c.registerCallback(tc.callback), nil))
                        response := host.read()
                        errObj, _ := response["error"].(map[string]any)
                        if response["id"] != float64(i+1) || errObj == nil || response["result"] != nil {
                            t.Fatalf("%s: got %v, want an error response", tc.name, response)
                        }
                        if errObj["code"] != float64(-32603) || errObj["message"] != tc.want {
                            t.Errorf("%s: got error %v, want code -32603 and message %q", tc.name, errObj, tc.want)
                        }
                    }

                    healthy := c.registerCallback(func(...any) any { return "still alive" })
                    host.write(callbackRequest(100, healthy, nil))
                    if response := host.read(); response["result"] != "still alive" {
                        t.Fatalf("callback after a panic: got %v", response)
                    }
                }

                func TestCallbackWithMalformedHandleArgumentIsAnsweredWithError(t *testing.T) {
                    c, host := newFakeHost(t)
                    called := false
                    id := c.registerCallback(func(...any) any { called = true; return nil })

                    host.write(callbackRequest(1, id, map[string]any{"p0": map[string]any{"$handle": 1, "$type": nil}}))
                    response := host.read()
                    errObj, _ := response["error"].(map[string]any)
                    message, _ := errObj["message"].(string)
                    if !strings.Contains(message, "malformed handle") {
                        t.Fatalf("got %v, want a malformed handle error", response)
                    }
                    if called {
                        t.Fatal("the callback ran with a malformed argument")
                    }
                }

                func TestRegisterCancellationIDsAreUnique(t *testing.T) {
                    c := newClient("")
                    const count = 5000
                    ids := make([]string, count)
                    var wg sync.WaitGroup
                    for i := range count {
                        wg.Add(1)
                        go func() {
                            defer wg.Done()
                            ids[i] = c.registerCancellation(NewCancellationToken())
                        }()
                    }
                    wg.Wait()

                    seen := make(map[string]bool, count)
                    for _, id := range ids {
                        if id == "" || seen[id] {
                            t.Fatalf("cancellation ID %q is empty or was handed out twice", id)
                        }
                        seen[id] = true
                    }
                }

                func TestRegisterCancellationDoesNotParkGoroutinePerToken(t *testing.T) {
                    c := newClient("")
                    before := runtime.NumGoroutine()
                    for range 1000 {
                        c.registerCancellation(NewCancellationToken())
                    }
                    if grown := runtime.NumGoroutine() - before; grown > 50 {
                        t.Fatalf("registering 1000 tokens left %d more goroutines running", grown)
                    }
                }

                func TestCancellationNotifiesHostUntilCallReturns(t *testing.T) {
                    c, host := newFakeHost(t)

                    token := NewCancellationToken()
                    id := c.registerCancellation(token)
                    token.Cancel()
                    request := host.read()
                    params, _ := request["params"].([]any)
                    if request["method"] != "cancelToken" || len(params) != 1 || params[0] != id {
                        t.Fatalf("got %v, want a cancelToken request for %s", request, id)
                    }
                    host.write(map[string]any{"jsonrpc": "2.0", "id": request["id"], "result": true})

                    // Once the capability call that carried the ID has returned, canceling
                    // the token must no longer notify the host.
                    token = NewCancellationToken()
                    id = c.registerCancellation(token)
                    done := make(chan error, 1)
                    go func() {
                        _, err := c.invokeCapability(context.Background(), "Test/capability", map[string]any{"cancellationToken": id})
                        done <- err
                    }()
                    request = host.read()
                    if request["method"] != "invokeCapability" {
                        t.Fatalf("got %v, want an invokeCapability request", request)
                    }
                    host.write(map[string]any{"jsonrpc": "2.0", "id": request["id"], "result": nil})
                    if err := <-done; err != nil {
                        t.Fatal(err)
                    }

                    c.cancellationsMu.Lock()
                    _, watching := c.cancellations[id]
                    c.cancellationsMu.Unlock()
                    if watching {
                        t.Fatal("the cancellation watcher outlived the call")
                    }

                    token.Cancel()
                    if err := host.peer.SetReadDeadline(time.Now().Add(200 * time.Millisecond)); err != nil {
                        t.Fatal(err)
                    }
                    if msg, err := readMessage(host.reader); err == nil {
                        t.Fatalf("the host received %v after the call returned", msg)
                    }
                }

                func TestCancellationDuringCallReturnsNotifiesHostAndReleasesRegistration(t *testing.T) {
                    c, host := newFakeHost(t)

                    // Like generated code, every call uses its token's own context, and the host
                    // never answers the capability request, so only the cancellation can end a call.
                    // The returning call races the watcher that notifies the host and only some
                    // interleavings lose the notification, hence the repetitions.
                    for i := range 2000 {
                        token := NewCancellationToken()
                        id := c.registerCancellation(token)
                        done := make(chan error, 1)
                        go func() {
                            _, err := c.invokeCapability(token.Context(), "Test/capability", map[string]any{"cancellationToken": id})
                            done <- err
                        }()
                        request := host.read()
                        if request["method"] != "invokeCapability" {
                            t.Fatalf("pass %d: got %v, want an invokeCapability request", i, request)
                        }

                        token.Cancel()
                        select {
                        case err := <-done:
                            if !errors.Is(err, context.Canceled) {
                                t.Fatalf("pass %d: got %v, want context.Canceled", i, err)
                            }
                        case <-time.After(5 * time.Second):
                            t.Fatalf("pass %d: the call did not return after the token was canceled", i)
                        }

                        c.cancellationsMu.Lock()
                        registered := len(c.cancellations)
                        c.cancellationsMu.Unlock()
                        if registered != 0 {
                            t.Fatalf("pass %d: %d cancellation registrations outlived the call", i, registered)
                        }

                        if err := host.peer.SetReadDeadline(time.Now().Add(5 * time.Second)); err != nil {
                            t.Fatal(err)
                        }
                        request, err := readMessage(host.reader)
                        if err != nil {
                            t.Fatalf("pass %d: the host was not told about the cancellation: %v", i, err)
                        }
                        params, _ := request["params"].([]any)
                        if request["method"] != "cancelToken" || len(params) != 1 || params[0] != id {
                            t.Fatalf("pass %d: got %v, want a cancelToken request for %s", i, request, id)
                        }
                        host.write(map[string]any{"jsonrpc": "2.0", "id": request["id"], "result": true})
                    }
                }

                func TestReadMessageRejectsInvalidContentLength(t *testing.T) {
                    for _, length := range []string{
                        strconv.Itoa(maxMessageSize + 1),
                        "9223372036854775807",
                        "9223372036854775808",
                        "-1",
                        "0",
                        "abc",
                        "",
                    } {
                        var before, after runtime.MemStats
                        runtime.ReadMemStats(&before)
                        _, err := readMessage(bufio.NewReader(strings.NewReader("Content-Length: " + length + "\r\n\r\n{}")))
                        runtime.ReadMemStats(&after)
                        if err == nil {
                            t.Errorf("Content-Length %q: expected an error", length)
                        }
                        if grown := after.TotalAlloc - before.TotalAlloc; grown > 1<<20 {
                            t.Errorf("Content-Length %q: allocated %d bytes before rejecting the frame", length, grown)
                        }
                    }

                    var frame bytes.Buffer
                    if err := writeMessage(&frame, map[string]any{"jsonrpc": "2.0", "id": 1, "result": "ok"}); err != nil {
                        t.Fatal(err)
                    }
                    msg, err := readMessage(bufio.NewReader(&frame))
                    if err != nil || msg["result"] != "ok" {
                        t.Fatalf("a valid frame: got %v, %v", msg, err)
                    }
                }

                func TestWrapIfHandleRejectsMalformedHandle(t *testing.T) {
                    c := newClient("")
                    malformed := map[string]any{"$handle": float64(1), "$type": nil}
                    for name, value := range map[string]any{
                        "direct":   malformed,
                        "in slice": []any{malformed},
                        "in map":   map[string]any{"nested": malformed},
                    } {
                        if _, err := wrapIfHandle(value, c); err == nil {
                            t.Errorf("%s: expected an error", name)
                        }
                    }

                    wrapped, err := wrapIfHandle(map[string]any{"$handle": "h1", "$type": "Test/Type"}, c)
                    if h, ok := wrapped.(*handle); err != nil || !ok || h.HandleID != "h1" || h.TypeID != "Test/Type" {
                        t.Fatalf("a valid handle: got %v, %v", wrapped, err)
                    }
                }

                func TestExtractResultKeepsErrorCodeAndData(t *testing.T) {
                    // Decode through the wire format so the code arrives as float64, as it does from the host.
                    var frame bytes.Buffer
                    if err := writeMessage(&frame, map[string]any{
                        "jsonrpc": "2.0",
                        "id":      1,
                        "error":   map[string]any{"code": -32601, "message": "Method not found", "data": map[string]any{"method": "nope"}},
                    }); err != nil {
                        t.Fatal(err)
                    }
                    response, err := readMessage(bufio.NewReader(&frame))
                    if err != nil {
                        t.Fatal(err)
                    }

                    _, err = extractResult(response)
                    if err == nil || err.Error() != "Method not found" {
                        t.Fatalf("got %v, want the message alone", err)
                    }
                    var coded interface{ Code() int }
                    if !errors.As(err, &coded) || coded.Code() != -32601 {
                        t.Fatalf("the code was not preserved: %v", err)
                    }
                    var withData interface{ Data() any }
                    if !errors.As(err, &withData) {
                        t.Fatalf("the data was not preserved: %v", err)
                    }
                    if data, _ := withData.Data().(map[string]any); data["method"] != "nope" {
                        t.Fatalf("got data %v", withData.Data())
                    }

                    cases := []struct {
                        name     string
                        response map[string]any
                        message  string
                        code     int
                    }{
                        {"code from connection.close", map[string]any{"error": map[string]any{"code": -32000, "message": "connection closed"}}, "connection closed", -32000},
                        {"missing message", map[string]any{"error": map[string]any{"code": float64(-32603)}}, "AppHost returned a JSON-RPC error (code -32603)", -32603},
                        {"error is not an object", map[string]any{"error": "oops"}, "AppHost returned a JSON-RPC error (code 0)", 0},
                    }
                    for _, tc := range cases {
                        _, err := extractResult(tc.response)
                        if err == nil || err.Error() != tc.message {
                            t.Errorf("%s: got %v, want %q", tc.name, err, tc.message)
                        }
                        if !errors.As(err, &coded) || coded.Code() != tc.code {
                            t.Errorf("%s: got code %v, want %d", tc.name, coded, tc.code)
                        }
                    }

                    if result, err := extractResult(map[string]any{"result": "ok"}); err != nil || result != "ok" {
                        t.Errorf("success response: got %v, %v", result, err)
                    }
                }

                func TestRequestErrorKeepsHostErrorCode(t *testing.T) {
                    c, host := newFakeHost(t)
                    done := make(chan error, 1)
                    go func() {
                        _, err := c.ping(context.Background())
                        done <- err
                    }()
                    request := host.read()
                    host.write(map[string]any{"jsonrpc": "2.0", "id": request["id"], "error": map[string]any{"code": -32601, "message": "Method not found"}})

                    err := <-done
                    var coded interface{ Code() int }
                    if !errors.As(err, &coded) || coded.Code() != -32601 {
                        t.Fatalf("got %v, want an error carrying code -32601", err)
                    }
                }
                """, timeout.Token);

            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "decode_test.go"), """
                package aspire

                import (
                    "encoding/json"
                    "reflect"
                    "strconv"
                    "strings"
                    "testing"
                )

                const decodeTestTypeID = "Aspire.Hosting/Aspire.Hosting.ApplicationModel.ContainerResource"

                // decodeTestResource stands in for a generated handle interface such as Resource.
                type decodeTestResource interface {
                    handleReference
                }

                // decodeTestWrapper stands in for the generated wrapper registered for decodeTestTypeID.
                type decodeTestWrapper struct{ *resourceBuilderBase }

                type decodeTestKey string

                type decodeTestDto struct {
                    Name  string `json:"name"`
                    Count int    `json:"count"`
                }

                func newDecodeTestClient() *client {
                    c := newClient("")
                    c.registerHandleWrapper(decodeTestTypeID, func(h *handle, c *client) any {
                        return &decodeTestWrapper{newResourceBuilderBase(h, c)}
                    })
                    return c
                }

                func decodeTestHandle(id string) string {
                    return `{"$handle":"` + id + `","$type":"` + decodeTestTypeID + `"}`
                }

                func decodeTestHandles(ids ...string) string {
                    handles := make([]string, len(ids))
                    for i, id := range ids {
                        handles[i] = decodeTestHandle(id)
                    }
                    return "[" + strings.Join(handles, ",") + "]"
                }

                // decodeTestIDs lists the handle ids of items in order, with <nil> for a nil item.
                func decodeTestIDs(items []decodeTestResource) string {
                    ids := make([]string, len(items))
                    for i, item := range items {
                        if item == nil {
                            ids[i] = "<nil>"
                            continue
                        }
                        ids[i] = item.getHandle().HandleID
                    }
                    return strings.Join(ids, ",")
                }

                // decodeWire takes a JSON-RPC result through the steps a capability result takes:
                // parse it, wrap its handles, decode it into T.
                func decodeWire[T any](t *testing.T, c *client, payload string) (T, error) {
                    t.Helper()
                    var raw any
                    if err := json.Unmarshal([]byte(payload), &raw); err != nil {
                        t.Fatal(err)
                    }
                    wrapped, err := wrapIfHandle(raw, c)
                    if err != nil {
                        t.Fatal(err)
                    }
                    return decodeAs[T](wrapped)
                }

                func decodeTestExpectError(t *testing.T, name string, err error, want ...string) {
                    t.Helper()
                    if err == nil {
                        t.Errorf("%s: got no error, want one mentioning %q", name, want)
                        return
                    }
                    for _, fragment := range want {
                        if !strings.Contains(err.Error(), fragment) {
                            t.Errorf("%s: error %q does not mention %q", name, err, fragment)
                        }
                    }
                }

                func TestDecodeListOfHandles(t *testing.T) {
                    c := newDecodeTestClient()
                    cases := []struct{ name, payload, ids string }{
                        {"one handle", decodeTestHandles("1"), "1"},
                        {"several handles", decodeTestHandles("1", "2", "3"), "1,2,3"},
                        {"null element", "[" + decodeTestHandle("1") + ",null]", "1,<nil>"},
                    }
                    for _, tc := range cases {
                        got, err := decodeWire[[]decodeTestResource](t, c, tc.payload)
                        if err != nil {
                            t.Errorf("%s: %v", tc.name, err)
                            continue
                        }
                        if ids := decodeTestIDs(got); ids != tc.ids {
                            t.Errorf("%s: got handles %s, want %s", tc.name, ids, tc.ids)
                        }
                        if _, ok := got[0].(*decodeTestWrapper); !ok {
                            t.Errorf("%s: first element is %T, want the registered wrapper", tc.name, got[0])
                        }
                    }
                }

                func TestDecodeListsOfValues(t *testing.T) {
                    c := newDecodeTestClient()

                    dtos, err := decodeWire[[]decodeTestDto](t, c, `[{"name":"a","count":1},{"name":"b","count":2}]`)
                    if err != nil || !reflect.DeepEqual(dtos, []decodeTestDto{{"a", 1}, {"b", 2}}) {
                        t.Errorf("DTOs: got %v, %v", dtos, err)
                    }

                    pointers, err := decodeWire[[]*decodeTestDto](t, c, `[{"name":"a","count":1},null]`)
                    if err != nil || !reflect.DeepEqual(pointers, []*decodeTestDto{{"a", 1}, nil}) {
                        t.Errorf("DTO pointers: got %v, %v", pointers, err)
                    }

                    names, err := decodeWire[[]string](t, c, `["a","b"]`)
                    if err != nil || !reflect.DeepEqual(names, []string{"a", "b"}) {
                        t.Errorf("strings: got %v, %v", names, err)
                    }

                    numbers, err := decodeWire[[]int](t, c, `[1,2,3]`)
                    if err != nil || !reflect.DeepEqual(numbers, []int{1, 2, 3}) {
                        t.Errorf("ints: got %v, %v", numbers, err)
                    }

                    floats, err := decodeWire[[]float64](t, c, `[1.5,2]`)
                    if err != nil || !reflect.DeepEqual(floats, []float64{1.5, 2}) {
                        t.Errorf("floats: got %v, %v", floats, err)
                    }

                    mixed, err := decodeWire[[]any](t, c, "["+decodeTestHandle("1")+`,{"name":"a"},7,"x",null]`)
                    if err != nil || len(mixed) != 5 {
                        t.Fatalf("mixed list: got %v, %v", mixed, err)
                    }
                    if _, ok := mixed[0].(*decodeTestWrapper); !ok {
                        t.Errorf("mixed list: first element is %T, want the registered wrapper", mixed[0])
                    }
                    if want := []any{map[string]any{"name": "a"}, float64(7), "x", nil}; !reflect.DeepEqual(mixed[1:], want) {
                        t.Errorf("mixed list: got %v, want %v", mixed[1:], want)
                    }
                }

                func TestDecodeEmptyList(t *testing.T) {
                    c := newDecodeTestClient()

                    list, err := decodeWire[[]decodeTestResource](t, c, `[]`)
                    if err != nil || list == nil || len(list) != 0 {
                        t.Errorf("empty list: got %#v, %v, want an empty slice", list, err)
                    }

                    entries, err := decodeWire[map[string]decodeTestResource](t, c, `{}`)
                    if err != nil || entries == nil || len(entries) != 0 {
                        t.Errorf("empty map: got %#v, %v, want an empty map", entries, err)
                    }
                }

                func TestDecodeNilResult(t *testing.T) {
                    if got, err := decodeAs[[]decodeTestResource](nil); got != nil || err != nil {
                        t.Errorf("nil list: got %#v, %v, want nil", got, err)
                    }
                    if got, err := decodeAs[map[string]decodeTestResource](nil); got != nil || err != nil {
                        t.Errorf("nil map: got %#v, %v, want nil", got, err)
                    }

                    // Typed nils are not nil interfaces, so they reach the element loop and must not panic.
                    if got, err := decodeAs[[]decodeTestResource]([]any(nil)); err != nil || len(got) != 0 {
                        t.Errorf("nil []any: got %#v, %v, want an empty list", got, err)
                    }
                    if got, err := decodeAs[map[string]decodeTestResource](map[string]any(nil)); err != nil || len(got) != 0 {
                        t.Errorf("nil map[string]any: got %#v, %v, want an empty map", got, err)
                    }
                    if got, err := decodeAs[[]decodeTestResource]([]any{(*decodeTestWrapper)(nil)}); err != nil || len(got) != 1 {
                        t.Errorf("nil wrapper element: got %#v, %v, want one element", got, err)
                    }
                }

                func TestDecodeMapOfHandles(t *testing.T) {
                    c := newDecodeTestClient()
                    payload := `{"a":` + decodeTestHandle("1") + `,"b":` + decodeTestHandle("2") + `}`

                    got, err := decodeWire[map[string]decodeTestResource](t, c, payload)
                    if err != nil || len(got) != 2 {
                        t.Fatalf("map of handles: got %v, %v", got, err)
                    }
                    for key, id := range map[string]string{"a": "1", "b": "2"} {
                        if item, ok := got[key]; !ok || item.getHandle().HandleID != id {
                            t.Errorf("map of handles: got %v for %q, want handle %s", item, key, id)
                        }
                    }

                    named, err := decodeWire[map[decodeTestKey]decodeTestResource](t, c, payload)
                    if err != nil || len(named) != 2 || named["a"] == nil || named["b"] == nil {
                        t.Errorf("map with a named key type: got %v, %v", named, err)
                    }

                    dtos, err := decodeWire[map[string]decodeTestDto](t, c, `{"x":{"name":"a","count":1}}`)
                    if err != nil || !reflect.DeepEqual(dtos, map[string]decodeTestDto{"x": {"a", 1}}) {
                        t.Errorf("map of DTOs: got %v, %v", dtos, err)
                    }

                    counts, err := decodeWire[map[string]int](t, c, `{"x":1,"y":2}`)
                    if err != nil || !reflect.DeepEqual(counts, map[string]int{"x": 1, "y": 2}) {
                        t.Errorf("map of ints: got %v, %v", counts, err)
                    }
                }

                func TestDecodeNestedLists(t *testing.T) {
                    c := newDecodeTestClient()

                    nested, err := decodeWire[[][]decodeTestResource](t, c, "["+decodeTestHandles("1", "2")+",[],"+decodeTestHandles("3")+"]")
                    if err != nil || len(nested) != 3 || decodeTestIDs(nested[0]) != "1,2" || len(nested[1]) != 0 || decodeTestIDs(nested[2]) != "3" {
                        t.Errorf("list of lists: got %v, %v", nested, err)
                    }

                    grouped, err := decodeWire[map[string][]decodeTestResource](t, c, `{"x":`+decodeTestHandles("1", "2")+`}`)
                    if err != nil || decodeTestIDs(grouped["x"]) != "1,2" {
                        t.Errorf("map of lists: got %v, %v", grouped, err)
                    }

                    maps, err := decodeWire[[]map[string]decodeTestResource](t, c, `[{"a":`+decodeTestHandle("1")+`}]`)
                    if err != nil || len(maps) != 1 || maps[0]["a"] == nil {
                        t.Errorf("list of maps: got %v, %v", maps, err)
                    }
                }

                func TestDecodeNamesTheElementThatFailed(t *testing.T) {
                    c := newDecodeTestClient()
                    unregistered := `{"$handle":"9","$type":"Aspire.Hosting/Aspire.Hosting.Unregistered"}`

                    _, err := decodeWire[[]decodeTestResource](t, c, "["+decodeTestHandle("1")+",7]")
                    decodeTestExpectError(t, "number in a handle list", err, "element 1 of []aspire.decodeTestResource")

                    _, err = decodeWire[[]decodeTestResource](t, c, "["+decodeTestHandle("1")+","+unregistered+"]")
                    decodeTestExpectError(t, "handle without a wrapper", err, "element 1 of []aspire.decodeTestResource")

                    _, err = decodeWire[[]string](t, c, `["a","b",3]`)
                    decodeTestExpectError(t, "number in a string list", err, "element 2 of []string")

                    _, err = decodeWire[map[string]decodeTestResource](t, c, `{"a":`+decodeTestHandle("1")+`,"b":7}`)
                    decodeTestExpectError(t, "number in a handle map", err, `entry "b" of map[string]aspire.decodeTestResource`)

                    _, err = decodeWire[[][]decodeTestResource](t, c, "["+decodeTestHandles("1")+",[7]]")
                    decodeTestExpectError(t, "number in a nested list", err, "element 1 of [][]aspire.decodeTestResource", "element 0 of []aspire.decodeTestResource")

                    _, err = decodeWire[[][]string](t, c, `[[["a"]]]`)
                    decodeTestExpectError(t, "data nested deeper than the type", err, "element 0 of [][]string")
                }

                func TestDecodeSingleValuesAreUnchanged(t *testing.T) {
                    c := newDecodeTestClient()

                    resource, err := decodeWire[decodeTestResource](t, c, decodeTestHandle("1"))
                    if err != nil || resource == nil || resource.getHandle().HandleID != "1" {
                        t.Errorf("single handle: got %v, %v", resource, err)
                    }

                    dto, err := decodeWire[decodeTestDto](t, c, `{"name":"a","count":1}`)
                    if err != nil || dto != (decodeTestDto{"a", 1}) {
                        t.Errorf("DTO: got %v, %v", dto, err)
                    }

                    pointer, err := decodeWire[*decodeTestDto](t, c, `{"name":"a","count":1}`)
                    if err != nil || pointer == nil || *pointer != (decodeTestDto{"a", 1}) {
                        t.Errorf("DTO pointer: got %v, %v", pointer, err)
                    }

                    // A DTO field of the wrong type is skipped instead of failing the whole value.
                    partial, err := decodeWire[decodeTestDto](t, c, `{"name":5,"count":3}`)
                    if err != nil || partial != (decodeTestDto{"", 3}) {
                        t.Errorf("DTO with a bad field: got %v, %v", partial, err)
                    }

                    // []byte arrives as a base64 string and a map with integer keys is not a string-keyed
                    // map, so both keep the JSON round trip.
                    data, err := decodeWire[[]byte](t, c, `"aGk="`)
                    if err != nil || string(data) != "hi" {
                        t.Errorf("bytes: got %q, %v", data, err)
                    }
                    byNumber, err := decodeWire[map[int]string](t, c, `{"1":"a"}`)
                    if err != nil || !reflect.DeepEqual(byNumber, map[int]string{1: "a"}) {
                        t.Errorf("map with integer keys: got %v, %v", byNumber, err)
                    }

                    if _, err := decodeWire[[]string](t, c, `"oops"`); err == nil {
                        t.Error("a string decoded into a list without an error")
                    }
                    if _, err := decodeWire[map[string]string](t, c, `["a"]`); err == nil {
                        t.Error("a list decoded into a map without an error")
                    }
                }

                func TestDecodeListOfHandlesDoesNotRoundTripThroughJSON(t *testing.T) {
                    c := newDecodeTestClient()
                    ids := make([]string, 100)
                    for i := range ids {
                        ids[i] = strconv.Itoa(i)
                    }
                    var raw any
                    if err := json.Unmarshal([]byte(decodeTestHandles(ids...)), &raw); err != nil {
                        t.Fatal(err)
                    }
                    wrapped, err := wrapIfHandle(raw, c)
                    if err != nil {
                        t.Fatal(err)
                    }

                    allocs := testing.AllocsPerRun(10, func() {
                        if _, err := decodeAs[[]decodeTestResource](wrapped); err != nil {
                            t.Error(err)
                        }
                    })
                    // Wrappers are assigned as they are, so the result slice is the only allocation that
                    // grows with the list. Re-encoding each element would take hundreds.
                    if allocs > 10 {
                        t.Errorf("decoding 100 handles allocated %v times, want a handful", allocs)
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
                Assert.True(process.ExitCode == 0, $"Go runtime hardening test failed:{Environment.NewLine}{await stdout}{await stderr}");
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
    [RequiresTools(["go"])]
    public async Task GeneratedRuntime_DecodesResourcesTaggedWithTheDeclaredInterface()
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

            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "declared_interface_test.go"), """
                package aspire

                import (
                    "bufio"
                    "net"
                    "os"
                    "testing"
                )

                // connectResourceHost connects a client to an in-memory AppHost that answers the
                // capabilities returning resources. The AppHost tags each handle it returns with the
                // type its capability declares, and Resource has no generated implementation.
                func connectResourceHost(t *testing.T, resourceType string) *client {
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

                    resource := func(id string) map[string]any {
                        return map[string]any{"$handle": id, "$type": resourceType}
                    }
                    go func() {
                        defer serverEnd.Close()
                        reader := bufio.NewReader(serverEnd)
                        for {
                            msg, err := readMessage(reader)
                            if err != nil {
                                return
                            }
                            reply := map[string]any{"jsonrpc": "2.0", "id": msg["id"]}
                            params, _ := msg["params"].([]any)
                            switch {
                            case len(params) > 0 && params[0] == "Aspire.Hosting/getResources":
                                reply["result"] = []any{resource("resource-1"), resource("resource-2")}
                            case len(params) > 0 && params[0] == "Aspire.Hosting/findResourceByName":
                                reply["result"] = resource("resource-1")
                            default:
                                reply["error"] = map[string]any{"code": -32601, "message": "unexpected request"}
                            }
                            writeMessage(serverEnd, reply)
                        }
                    }()
                    return c
                }

                func newTestModel(t *testing.T) DistributedApplicationModel {
                    t.Helper()
                    resourceType := os.Getenv("TEST_RESOURCE_TYPE")
                    if resourceType == "" {
                        t.Fatal("TEST_RESOURCE_TYPE is not set")
                    }
                    return newDistributedApplicationModelFromHandle(&handle{
                        HandleID: "model",
                        TypeID:   "Aspire.Hosting/Aspire.Hosting.ApplicationModel.DistributedApplicationModel",
                    }, connectResourceHost(t, resourceType))
                }

                func TestListOfResourcesDecodesIntoTheMarkerInterface(t *testing.T) {
                    resources, err := newTestModel(t).GetResources()
                    if err != nil {
                        t.Fatalf("GetResources failed: %v", err)
                    }
                    if len(resources) != 2 {
                        t.Fatalf("GetResources returned %d resources, want 2", len(resources))
                    }
                    for i, want := range []string{"resource-1", "resource-2"} {
                        if resources[i] == nil || resources[i].getHandle().HandleID != want {
                            t.Errorf("resource %d: got %v, want a wrapper for handle %s", i, resources[i], want)
                        }
                    }
                }

                func TestSingleResourceDecodesIntoTheMarkerInterface(t *testing.T) {
                    model := newTestModel(t)
                    found := model.FindResourceByName("api")
                    if err := model.Err(); err != nil {
                        t.Fatalf("FindResourceByName failed: %v", err)
                    }
                    if found == nil || found.getHandle().HandleID != "resource-1" {
                        t.Fatalf("FindResourceByName returned %v, want a wrapper for handle resource-1", found)
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
                // The same id the AppHost derives for the IResource type that GetResources declares.
                Environment = { ["TEST_RESOURCE_TYPE"] = AtsTypeMapping.DeriveTypeId(typeof(IResource)) }
            };
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                Assert.True(process.ExitCode == 0, $"Go declared-interface test failed:{Environment.NewLine}{await stdout}{await stderr}");
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
