// Package aspire provides the ATS transport layer for JSON-RPC, Handle, errors, callbacks
package aspire

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"os"
	"reflect"
	"runtime"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"
)

// AtsErrorCode contains standard ATS error codes.
type AtsErrorCode string

const (
	CapabilityNotFound AtsErrorCode = "CAPABILITY_NOT_FOUND"
	HandleNotFound     AtsErrorCode = "HANDLE_NOT_FOUND"
	TypeMismatch       AtsErrorCode = "TYPE_MISMATCH"
	InvalidArgument    AtsErrorCode = "INVALID_ARGUMENT"
	ArgumentOutOfRange AtsErrorCode = "ARGUMENT_OUT_OF_RANGE"
	CallbackError      AtsErrorCode = "CALLBACK_ERROR"
	InternalError      AtsErrorCode = "INTERNAL_ERROR"
)

// atsErrorDetails Error details for ATS errors.
type atsErrorDetails struct {
	Parameter *string `json:"parameter,omitempty"`
	Expected  *string `json:"expected,omitempty"`
	Actual    *string `json:"actual,omitempty"`
}

// atsError Structured error from ATS capability invocation.
type atsError struct {
	Code       string           `json:"code"`
	Message    string           `json:"message"`
	Capability string           `json:"capability,omitempty"`
	Details    *atsErrorDetails `json:"details,omitempty"`
}

func (e *atsError) Error() string {
	return e.Message
}

// tryGetAtsError safely checks if a value contains an ATS error.
func tryGetAtsError(value any) (bool, atsError) {
	m, ok := value.(map[string]any)
	if !ok {
		return false, atsError{}
	}
	errVal, hasError := m["$error"]
	if !hasError || errVal == nil {
		return false, atsError{}
	}
	switch v := errVal.(type) {
	case atsError:
		return true, v
	case map[string]any:
		var result atsError
		if data, err := json.Marshal(v); err == nil {
			if err := json.Unmarshal(data, &result); err == nil {
				return true, result
			}
		}
	}
	return false, atsError{}
}

// CapabilityError represents an error returned from a capability invocation.
type CapabilityError struct {
	err atsError
}

func (e *CapabilityError) Code() string       { return e.err.Code }
func (e *CapabilityError) Message() string    { return e.err.Message }
func (e *CapabilityError) Capability() string { return e.err.Capability }
func (e *CapabilityError) Error() string      { return e.err.Error() }

// FormatError returns a human-readable rendering of an SDK error. Capability
// errors are expanded with their structured fields (code, capability) so the
// caller can log them directly without unpacking. Useful pattern:
//
//	if err := app.Run(); err != nil {
//	    log.Fatal(aspire.FormatError(err))
//	}
//
// Non-SDK errors are returned via err.Error() unchanged.
func FormatError(err error) string {
	if err == nil {
		return ""
	}
	var capErr *CapabilityError
	if errors.As(err, &capErr) {
		var b strings.Builder
		b.WriteString("Capability Error: ")
		b.WriteString(capErr.Message())
		if code := capErr.Code(); code != "" {
			b.WriteString("\n  Code: ")
			b.WriteString(code)
		}
		if cap := capErr.Capability(); cap != "" {
			b.WriteString("\n  Capability: ")
			b.WriteString(cap)
		}
		if capErr.err.Details != nil {
			d := capErr.err.Details
			if d.Parameter != nil {
				b.WriteString("\n  Parameter: ")
				b.WriteString(*d.Parameter)
			}
			if d.Expected != nil {
				b.WriteString("\n  Expected: ")
				b.WriteString(*d.Expected)
			}
			if d.Actual != nil {
				b.WriteString("\n  Actual: ")
				b.WriteString(*d.Actual)
			}
		}
		return b.String()
	}
	return err.Error()
}

// CancellationToken wraps a context.Context to provide cooperative cancellation.
// Use NewCancellationToken to create one; call Cancel() to cancel it.
// Pass token.Context() to any standard Go library that accepts a context.
type CancellationToken struct {
	ctx    context.Context
	cancel context.CancelFunc
}

// NewCancellationToken creates a new cancellation token backed by a cancellable
// context derived from context.Background().
func NewCancellationToken() *CancellationToken {
	return newCancellationTokenFrom(context.Background())
}

// newCancellationTokenFrom creates a new cancellation token whose lifetime is
// bounded by the supplied parent context.
func newCancellationTokenFrom(parent context.Context) *CancellationToken {
	ctx, cancel := context.WithCancel(parent)
	return &CancellationToken{ctx: ctx, cancel: cancel}
}

// Cancel cancels the token and propagates cancellation to the underlying context.
func (ct *CancellationToken) Cancel() {
	if ct == nil {
		return
	}
	ct.cancel()
}

// IsCancelled returns true if the token has been canceled.
func (ct *CancellationToken) IsCancelled() bool {
	if ct == nil {
		return false
	}
	return ct.ctx.Err() != nil
}

// Context returns the underlying context.Context for use with standard Go APIs.
func (ct *CancellationToken) Context() context.Context {
	if ct == nil {
		return context.Background()
	}
	return ct.ctx
}

// handleType is the ATS type ID carried on a handle envelope.
type handleType = string

// handle represents a reference to a server-side object.
type handle struct {
	HandleID string     `json:"$handle"`
	TypeID   handleType `json:"$type"`
}

// ToJSON returns the handle as a JSON-serializable map.
func (h *handle) ToJSON() map[string]any {
	return map[string]any{
		"$handle": h.HandleID,
		"$type":   h.TypeID,
	}
}

func (h *handle) String() string {
	return fmt.Sprintf("Handle<%s>(%s)", h.TypeID, h.HandleID)
}

// isMarshaledHandle checks if a value is a marshaled handle.
func isMarshaledHandle(value any) bool {
	m, ok := value.(map[string]any)
	if !ok {
		return false
	}
	_, hasHandle := m["$handle"]
	_, hasType := m["$type"]
	return hasHandle && hasType
}

// handleReference is implemented by every handle-wrapper type. The generated
// SDK uses this interface as the universal "extract the underlying handle"
// affordance — both serializeValue and the post-RPC cast in generated bodies
// rely on it. Err() lets the generator propagate failures from a handle
// argument back into the receiver's error chain (the Err()-accumulation
// pattern that maintains fluent chaining).
type handleReference interface {
	getHandle() *handle
	Err() error
}

// handleWrapperFactory creates a wrapper for a handle.
type handleWrapperFactory func(handle *handle, client *client) any

// wrapIfHandle recursively traverses the value to wrap marshaled handles into typed DTOs.
// Lookups consult the per-client wrapper registry; ReferenceExpression and any
// generated handle types must be registered via client.registerHandleWrapper
// before the first capability invocation that could return them.
func wrapIfHandle(value any, c *client) any {
	if isMarshaledHandle(value) {
		m := value.(map[string]any)
		h := &handle{
			HandleID: m["$handle"].(string),
			TypeID:   m["$type"].(string),
		}
		if c != nil {
			if factory := c.lookupWrapper(h.TypeID); factory != nil {
				return factory(h, c)
			}
		}
		return h
	}

	if s, ok := value.([]any); ok {
		for i, v := range s {
			s[i] = wrapIfHandle(v, c)
		}
		return s
	}

	if m, ok := value.(map[string]any); ok {
		for k, v := range m {
			m[k] = wrapIfHandle(v, c)
		}
		return m
	}

	return value
}

// ── connection ────────────────────────────────────────────────────────────────
//
// connection owns all I/O for a single live socket connection. It is created by
// client.connect and torn down by connection.close. client holds a *connection
// pointer and replaces it with nil on disconnect; all other state lives here.

type connection struct {
	rawConn io.ReadWriteCloser
	reader  *bufio.Reader
	client  *client

	writeQueue chan map[string]any
	done       chan struct{}

	mu      sync.Mutex
	pending map[int64]chan map[string]any
	closed  bool

	nextID  atomic.Int64
	onClose func(error)
}

func newConnection(rawConn io.ReadWriteCloser, c *client, onClose func(error)) *connection {
	return &connection{
		rawConn:    rawConn,
		reader:     bufio.NewReader(rawConn),
		client:     c,
		writeQueue: make(chan map[string]any, 64),
		done:       make(chan struct{}),
		pending:    make(map[int64]chan map[string]any),
		onClose:    onClose,
	}
}

func (c *connection) start() {
	go c.readLoop()
	go c.writeLoop()
}

func (c *connection) close(err error) {
	c.mu.Lock()
	if c.closed {
		c.mu.Unlock()
		return
	}
	c.closed = true
	pending := c.pending
	c.pending = nil
	c.mu.Unlock()

	closeErr := c.rawConn.Close()
	close(c.done)

	if err == nil {
		err = errors.New("connection closed")
	}
	if closeErr != nil {
		err = errors.Join(err, closeErr)
	}
	errResp := map[string]any{
		"error": map[string]any{
			"code":    -32000,
			"message": err.Error(),
		},
	}
	for _, ch := range pending {
		ch <- errResp
	}

	c.onClose(err)
}

func (c *connection) writeLoop() {
	for {
		select {
		case msg := <-c.writeQueue:
			if err := writeMessage(c.rawConn, msg); err != nil {
				c.close(err)
				return
			}
		case <-c.done:
			return
		}
	}
}

func (c *connection) readLoop() {
	for {
		msg, err := readMessage(c.reader)
		if err != nil {
			c.close(err)
			return
		}
		c.dispatch(msg)
	}
}

func (c *connection) dispatch(msg map[string]any) {
	if _, hasMethod := msg["method"]; hasMethod {
		go c.handleCallbackRequest(msg)
		return
	}
	if id, ok := msg["id"]; ok {
		if reqID, ok := jsonRPCID(id); ok {
			c.mu.Lock()
			ch := c.pending[reqID]
			delete(c.pending, reqID)
			c.mu.Unlock()
			if ch != nil {
				ch <- msg
			}
		}
	}
}

func (c *connection) enqueue(msg map[string]any) {
	select {
	case c.writeQueue <- msg:
	case <-c.done:
	}
}

func (c *connection) handleCallbackRequest(message map[string]any) {
	method := getString(message, "method")
	requestID := message["id"]

	if method != "invokeCallback" {
		if requestID != nil {
			c.enqueue(map[string]any{
				"jsonrpc": "2.0",
				"id":      requestID,
				"error":   map[string]any{"code": -32601, "message": fmt.Sprintf("Unknown method: %s", method)},
			})
		}
		return
	}

	params, _ := message["params"].([]any)
	var callbackID string
	var args any
	if len(params) > 0 {
		callbackID, _ = params[0].(string)
	}
	if len(params) > 1 {
		args = params[1]
	}

	result, err := c.client.invokeCallback(callbackID, args)
	if err != nil {
		c.enqueue(map[string]any{
			"jsonrpc": "2.0",
			"id":      requestID,
			"error":   map[string]any{"code": -32000, "message": err.Error()},
		})
	} else {
		c.enqueue(map[string]any{
			"jsonrpc": "2.0",
			"id":      requestID,
			"result":  result,
		})
	}
}

// sendRequest sends a JSON-RPC request over the write queue and blocks until
// the matching response arrives or the supplied context is canceled.
//
// The ctx argument is the primary unblock mechanism for the Go caller. The
// existing per-CancellationToken server-notification goroutine spawned by
// client.registerCancellation continues to notify the AppHost, but this
// method does not need to wait for that round-trip — it returns ctx.Err()
// as soon as ctx is done.
func (c *connection) sendRequest(ctx context.Context, method string, params []any) (any, error) {
	if ctx == nil {
		ctx = context.Background()
	}
	if err := ctx.Err(); err != nil {
		return nil, err
	}

	id := c.nextID.Add(1)
	respCh := make(chan map[string]any, 1)
	msg := map[string]any{
		"jsonrpc": "2.0",
		"id":      id,
		"method":  method,
		"params":  params,
	}

	c.mu.Lock()
	if c.closed {
		c.mu.Unlock()
		return nil, errors.New("not connected to AppHost")
	}
	c.pending[id] = respCh
	c.mu.Unlock()

	select {
	case c.writeQueue <- msg:
	case <-ctx.Done():
		c.mu.Lock()
		delete(c.pending, id)
		c.mu.Unlock()
		return nil, ctx.Err()
	case <-c.done:
		c.mu.Lock()
		delete(c.pending, id)
		c.mu.Unlock()
		return nil, errors.New("not connected to AppHost")
	}

	select {
	case resp := <-respCh:
		return extractResult(resp)
	case <-ctx.Done():
		c.mu.Lock()
		delete(c.pending, id)
		c.mu.Unlock()
		return nil, ctx.Err()
	case <-c.done:
		select {
		case resp := <-respCh:
			return extractResult(resp)
		default:
			return nil, errors.New("not connected to AppHost")
		}
	}
}

// ── client ────────────────────────────────────────────────────────────────────
//
// client manages the connection lifecycle to the AppHost server. All registries
// (handle wrappers, callbacks, cancellation tokens) are scoped to the client —
// no package-level globals — so multiple clients can coexist in the same
// process without sharing state.

type client struct {
	socketPath string

	// mu guards conn and disconnectCallbacks.
	mu                  sync.Mutex
	conn                *connection
	disconnectCallbacks []func()

	// wrappersMu guards the per-client handle wrapper registry. The registry
	// is populated by registerWrappers (generated) before the first
	// invokeCapability call.
	wrappersMu sync.RWMutex
	wrappers   map[string]handleWrapperFactory

	// callbacksMu guards the per-client callback registry.
	callbacksMu     sync.RWMutex
	callbacks       map[string]func(...any) any
	callbackCounter atomic.Int64
}

// newClient constructs a client; callers must invoke connect before using it.
func newClient(socketPath string) *client {
	return &client{
		socketPath: socketPath,
		wrappers:   make(map[string]handleWrapperFactory),
		callbacks:  make(map[string]func(...any) any),
	}
}

// registerHandleWrapper registers a factory for wrapping handles of a specific
// type. Called by the generated registerWrappers function from CreateBuilder
// before any capability invocation that could return a wrapped handle.
func (c *client) registerHandleWrapper(typeID string, factory handleWrapperFactory) {
	c.wrappersMu.Lock()
	c.wrappers[typeID] = factory
	c.wrappersMu.Unlock()
}

// lookupWrapper returns the factory for a given type ID or nil.
func (c *client) lookupWrapper(typeID string) handleWrapperFactory {
	c.wrappersMu.RLock()
	defer c.wrappersMu.RUnlock()
	return c.wrappers[typeID]
}

// registerCallback registers a callback on this client and returns its ID.
func (c *client) registerCallback(callback func(...any) any) string {
	if callback == nil {
		return ""
	}
	id := fmt.Sprintf("callback_%d_%d", c.callbackCounter.Add(1), time.Now().UnixMilli())
	c.callbacksMu.Lock()
	c.callbacks[id] = callback
	c.callbacksMu.Unlock()
	return id
}

// unregisterCallback removes a callback by ID.
func (c *client) unregisterCallback(id string) bool {
	c.callbacksMu.Lock()
	defer c.callbacksMu.Unlock()
	_, exists := c.callbacks[id]
	delete(c.callbacks, id)
	return exists
}

func (c *client) invokeCallback(callbackID string, args any) (any, error) {
	if callbackID == "" {
		return nil, errors.New("callback ID missing")
	}

	c.callbacksMu.RLock()
	callback, ok := c.callbacks[callbackID]
	c.callbacksMu.RUnlock()
	if !ok {
		return nil, fmt.Errorf("callback not found: %s", callbackID)
	}

	var positionalArgs []any
	if argsMap, ok := args.(map[string]any); ok {
		for i := 0; ; i++ {
			key := fmt.Sprintf("p%d", i)
			if val, exists := argsMap[key]; exists {
				positionalArgs = append(positionalArgs, wrapIfHandle(val, c))
			} else {
				break
			}
		}
	} else if args != nil {
		positionalArgs = append(positionalArgs, wrapIfHandle(args, c))
	}

	result := callback(positionalArgs...)

	// DTO write-back protocol: if the callback result is nil, return the
	// original args object so the .NET host can detect mutations.
	if result == nil {
		return args, nil
	}

	return result, nil
}

// registerCancellation registers a cancellation token with the server and
// returns its ID. A goroutine is spawned to notify the server when the token
// is canceled.
func (c *client) registerCancellation(token *CancellationToken) string {
	if token == nil {
		return ""
	}
	id := fmt.Sprintf("ct_%d", time.Now().UnixNano())
	go func() {
		<-token.ctx.Done()
		c.cancelToken(id)
	}()
	return id
}

// connect establishes the connection to the AppHost server and starts the
// background reader and writer goroutines.
func (c *client) connect(ctx context.Context, timeout time.Duration) error {
	c.mu.Lock()
	if c.conn != nil {
		c.mu.Unlock()
		return nil
	}

	rawConn, err := openConnection(c.socketPath, timeout)
	if err != nil {
		c.mu.Unlock()
		return fmt.Errorf("failed to connect to AppHost: %w", err)
	}

	authToken := os.Getenv("ASPIRE_REMOTE_APPHOST_TOKEN")
	if authToken == "" {
		cErr := rawConn.Close()
		c.mu.Unlock()
		return errors.Join(errors.New("ASPIRE_REMOTE_APPHOST_TOKEN environment variable is not set"), cErr)
	}

	conn := newConnection(rawConn, c, c.onConnectionClose)
	c.conn = conn
	c.mu.Unlock()

	conn.start()

	if err := c.authenticate(ctx, authToken); err != nil {
		c.disconnect()
		return fmt.Errorf("failed to authenticate to AppHost: %w", err)
	}

	return nil
}

// onDisconnect registers a callback to be invoked exactly once when the
// connection is closed.
func (c *client) onDisconnect(callback func()) {
	c.mu.Lock()
	c.disconnectCallbacks = append(c.disconnectCallbacks, callback)
	c.mu.Unlock()
}

// invokeCapability invokes a capability on the server. The supplied context
// drives both server-side cancellation (if a CancellationToken is registered
// in args) and local short-circuit on cancel.
func (c *client) invokeCapability(ctx context.Context, capabilityID string, args map[string]any) (any, error) {
	if err := validateCapabilityArgs(capabilityID, args); err != nil {
		return nil, err
	}

	result, err := c.sendRequest(ctx, "invokeCapability", []any{capabilityID, args})
	if err != nil {
		return nil, err
	}
	if hasAtsErr, atsErr := tryGetAtsError(result); hasAtsErr {
		return nil, &CapabilityError{err: atsErr}
	}
	return wrapIfHandle(result, c), nil
}

func (c *client) authenticate(ctx context.Context, token string) error {
	result, err := c.sendRequest(ctx, "authenticate", []any{token})
	if err != nil {
		return err
	}
	authenticated, _ := result.(bool)
	if !authenticated {
		return errors.New("failed to authenticate to the AppHost server")
	}
	return nil
}

func (c *client) cancelToken(tokenID string) bool {
	result, err := c.sendRequest(context.Background(), "cancelToken", []any{tokenID})
	if err != nil {
		return false
	}
	b, _ := result.(bool)
	return b
}

func (c *client) ping(ctx context.Context) (string, error) {
	result, err := c.sendRequest(ctx, "ping", nil)
	if err != nil {
		return "", err
	}
	s, _ := result.(string)
	return s, nil
}

// disconnect closes the connection. Safe to call multiple times.
func (c *client) disconnect() {
	c.mu.Lock()
	conn := c.conn
	c.conn = nil
	c.mu.Unlock()
	if conn != nil {
		conn.close(nil)
	}
}

// sendRequest snapshots the active connection and delegates to it.
func (c *client) sendRequest(ctx context.Context, method string, params []any) (any, error) {
	c.mu.Lock()
	conn := c.conn
	c.mu.Unlock()
	if conn == nil {
		return nil, errors.New("not connected to AppHost")
	}
	return conn.sendRequest(ctx, method, params)
}

func (c *client) onConnectionClose(_ error) {
	c.mu.Lock()
	c.conn = nil
	callbacks := c.disconnectCallbacks
	c.disconnectCallbacks = nil
	c.mu.Unlock()
	for _, cb := range callbacks {
		cb()
	}
}

// ── Package-level I/O helpers ─────────────────────────────────────────────────

func extractResult(response map[string]any) (any, error) {
	if errObj, hasErr := response["error"]; hasErr {
		errMap, _ := errObj.(map[string]any)
		return nil, errors.New(getString(errMap, "message"))
	}
	return response["result"], nil
}

func writeMessage(w io.Writer, msg map[string]any) error {
	body, err := json.Marshal(msg)
	if err != nil {
		return err
	}
	header := fmt.Sprintf("Content-Length: %d\r\n\r\n", len(body))
	if _, err = w.Write([]byte(header)); err != nil {
		return err
	}
	_, err = w.Write(body)
	return err
}

func readMessage(reader *bufio.Reader) (map[string]any, error) {
	headers := make(map[string]string)
	for {
		line, err := reader.ReadString('\n')
		if err != nil {
			return nil, err
		}
		line = strings.TrimSpace(line)
		if line == "" {
			break
		}
		parts := strings.SplitN(line, ":", 2)
		if len(parts) == 2 {
			headers[strings.TrimSpace(strings.ToLower(parts[0]))] = strings.TrimSpace(parts[1])
		}
	}

	lengthStr := headers["content-length"]
	length, err := strconv.Atoi(lengthStr)
	if err != nil || length <= 0 {
		return nil, errors.New("invalid content-length")
	}

	body := make([]byte, length)
	_, err = io.ReadFull(reader, body)
	if err != nil {
		return nil, err
	}

	var message map[string]any
	if err := json.Unmarshal(body, &message); err != nil {
		return nil, err
	}
	return message, nil
}

func jsonRPCID(id any) (int64, bool) {
	switch v := id.(type) {
	case float64:
		return int64(v), true
	case int64:
		return v, true
	case json.Number:
		n, err := v.Int64()
		return n, err == nil
	case string:
		n, err := strconv.ParseInt(v, 10, 64)
		return n, err == nil
	default:
		return 0, false
	}
}

func getString(m map[string]any, key string) string {
	if v, ok := m[key]; ok {
		if s, ok := v.(string); ok {
			return s
		}
	}
	return ""
}

func openConnection(socketPath string, timeout time.Duration) (io.ReadWriteCloser, error) {
	if runtime.GOOS == "windows" {
		pipePath := `\\.\pipe\` + socketPath
		return openNamedPipe(pipePath)
	}
	dialer := net.Dialer{}
	if timeout > 0 {
		dialer.Timeout = timeout
	}
	return dialer.Dial("unix", socketPath)
}

func openNamedPipe(path string) (io.ReadWriteCloser, error) {
	f, err := os.OpenFile(path, os.O_RDWR, 0)
	if err != nil {
		return nil, err
	}
	return f, nil
}

// validateCapabilityArgs checks for circular references in arguments before sending to the server.
func validateCapabilityArgs(capabilityID string, args map[string]any) error {
	if args == nil {
		return nil
	}
	ancestors := make(map[uintptr]struct{})
	return validateValue(args, "args", ancestors, capabilityID)
}

func validateValue(value any, path string, ancestors map[uintptr]struct{}, capabilityID string) error {
	if value == nil {
		return nil
	}

	val := reflect.ValueOf(value)
	switch val.Kind() {
	case reflect.Map, reflect.Slice, reflect.Ptr:
		if val.IsNil() {
			return nil
		}
		ptr := val.Pointer()
		if _, ok := ancestors[ptr]; ok {
			return fmt.Errorf("argument '%s' passed to capability '%s' contains a circular reference", path, capabilityID)
		}
		ancestors[ptr] = struct{}{}
		defer delete(ancestors, ptr)
	default:
	}

	switch v := value.(type) {
	case map[string]any:
		for key, nestedValue := range v {
			if err := validateValue(nestedValue, path+"."+key, ancestors, capabilityID); err != nil {
				return err
			}
		}
	case []any:
		for i, item := range v {
			if err := validateValue(item, fmt.Sprintf("%s[%d]", path, i), ancestors, capabilityID); err != nil {
				return err
			}
		}
	}

	return nil
}
