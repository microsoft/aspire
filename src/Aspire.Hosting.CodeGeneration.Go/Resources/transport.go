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
	ctx        context.Context
	cancel     context.CancelFunc
	cancelOnce sync.Once
	notify     func()
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

func newRemoteCancellationToken(c *client, id string) *CancellationToken {
	token := newCancellationTokenFrom(context.Background())
	token.notify = func() {
		go c.cancelToken(id)
	}
	return token
}

// Cancel cancels the token and propagates cancellation to the underlying context.
func (ct *CancellationToken) Cancel() {
	if ct == nil {
		return
	}
	ct.cancelOnce.Do(func() {
		ct.cancel()
		if ct.notify != nil {
			ct.notify()
		}
	})
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
// The value comes off the wire, so a handle envelope whose $handle or $type is
// not a string is reported as an error instead of being trusted.
func wrapIfHandle(value any, c *client) (any, error) {
	if m, ok := value.(map[string]any); ok && isMarshaledHandle(m) {
		handleID, idIsString := m["$handle"].(string)
		typeID, typeIsString := m["$type"].(string)
		if !idIsString || !typeIsString {
			return nil, fmt.Errorf("aspire: malformed handle in AppHost message: $handle is %T and $type is %T, both must be strings", m["$handle"], m["$type"])
		}
		h := &handle{HandleID: handleID, TypeID: typeID}
		if c != nil {
			if factory := c.lookupWrapper(h.TypeID); factory != nil {
				return factory(h, c), nil
			}
		}
		return h, nil
	}

	if s, ok := value.([]any); ok {
		for i, v := range s {
			wrapped, err := wrapIfHandle(v, c)
			if err != nil {
				return nil, err
			}
			s[i] = wrapped
		}
		return s, nil
	}

	if m, ok := value.(map[string]any); ok {
		for k, v := range m {
			wrapped, err := wrapIfHandle(v, c)
			if err != nil {
				return nil, err
			}
			m[k] = wrapped
		}
		return m, nil
	}

	return value, nil
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

	writeQueue chan []byte
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
		writeQueue: make(chan []byte, 64),
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
			if err := writeMessageBody(c.rawConn, msg); err != nil {
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
	body, err := json.Marshal(msg)
	if err != nil {
		// A user callback can return an unencodable value. Fail that callback,
		// not every unrelated pending call on the connection.
		body, err = json.Marshal(map[string]any{
			"jsonrpc": "2.0",
			"id":      msg["id"],
			"error":   map[string]any{"code": -32603, "message": fmt.Sprintf("Callback result encoding failed: %v", err)},
		})
		if err != nil {
			c.close(err)
			return
		}
	}
	select {
	case c.writeQueue <- body:
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

	// Callbacks run on their own goroutine, so a panic in user code would
	// otherwise terminate the whole process. Answer the AppHost with the error
	// the other runtimes send for a throwing callback (-32603, "Callback
	// execution failed: <message>"). Only the panic value is reported; the stack
	// stays inside the process.
	completed := false
	defer func() {
		if r := recover(); r != nil {
			code := -32603
			message := fmt.Sprintf("Callback execution failed: %v", r)
			if err, ok := r.(*callbackArgumentError); ok {
				code = -32602
				message = err.Error()
			}
			c.enqueue(map[string]any{
				"jsonrpc": "2.0",
				"id":      requestID,
				"error":   map[string]any{"code": code, "message": message},
			})
		} else if !completed {
			// runtime.Goexit runs defers without a recoverable panic value.
			c.enqueue(map[string]any{
				"jsonrpc": "2.0",
				"id":      requestID,
				"error":   map[string]any{"code": -32603, "message": "Callback execution failed: goroutine exited without returning"},
			})
		}
	}()

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
	completed = true
}

// sendRequest sends a JSON-RPC request over the write queue and blocks until
// the matching response arrives or the supplied context is canceled.
//
// The ctx argument is the primary unblock mechanism for the Go caller. The
// watcher that client.registerCancellation attaches to a CancellationToken
// also notifies the AppHost, but this method does not need to wait for that
// round-trip — it returns ctx.Err() as soon as ctx is done.
func (c *connection) sendRequest(ctx context.Context, method string, params []any) (any, error) {
	return c.sendRequestQueued(ctx, method, params, nil)
}

func (c *connection) sendRequestQueued(ctx context.Context, method string, params []any, onQueued func()) (any, error) {
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
	body, err := json.Marshal(msg)
	if err != nil {
		return nil, fmt.Errorf("aspire: encode %s request: %w", method, err)
	}

	c.mu.Lock()
	if c.closed {
		c.mu.Unlock()
		return nil, errors.New("not connected to AppHost")
	}
	c.pending[id] = respCh
	c.mu.Unlock()

	select {
	case c.writeQueue <- body:
		if onQueued != nil {
			onQueued()
		}
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

	// cancellationsMu guards the per-client cancellation registry, which maps
	// each cancellation ID handed to the AppHost to the function that detaches
	// its watcher from the token's context.
	cancellationsMu     sync.Mutex
	cancellations       map[string]*cancellationRegistration
	cancellationCounter atomic.Int64

	// errMu guards firstErr.
	errMu    sync.Mutex
	firstErr error
}

type cancellationRegistration struct {
	token *CancellationToken
	stop  func() bool
}

// newClient constructs a client; callers must invoke connect before using it.
func newClient(socketPath string) *client {
	return &client{
		socketPath:    socketPath,
		wrappers:      make(map[string]handleWrapperFactory),
		callbacks:     make(map[string]func(...any) any),
		cancellations: make(map[string]*cancellationRegistration),
	}
}

// recordErr remembers the first error reported by any handle of this client,
// so the builder can surface a failure that only ever reached a child handle.
// It is safe to call concurrently and on a nil client.
func (c *client) recordErr(err error) {
	if c == nil || err == nil {
		return
	}
	c.errMu.Lock()
	defer c.errMu.Unlock()
	if c.firstErr == nil {
		c.firstErr = err
	}
}

// firstError returns the first error passed to recordErr, or nil.
func (c *client) firstError() error {
	if c == nil {
		return nil
	}
	c.errMu.Lock()
	defer c.errMu.Unlock()
	return c.firstErr
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
				wrapped, err := wrapIfHandle(val, c)
				if err != nil {
					return nil, err
				}
				positionalArgs = append(positionalArgs, wrapped)
			} else {
				break
			}
		}
	} else if args != nil {
		wrapped, err := wrapIfHandle(args, c)
		if err != nil {
			return nil, err
		}
		positionalArgs = append(positionalArgs, wrapped)
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
// returns its ID. The server is notified when the token is canceled, until
// releaseCancellations detaches the watcher once the capability call returns.
//
// The ID combines a per-client counter with the clock, like callback IDs. The
// AppHost keys its cancellation registry by this ID and hands every request
// that reuses an ID the same CancellationTokenSource, so two tokens registered
// in the same clock tick must not share one.
func (c *client) registerCancellation(token *CancellationToken) string {
	if token == nil {
		return ""
	}
	id := fmt.Sprintf("ct_%d_%d", c.cancellationCounter.Add(1), time.Now().UnixMilli())
	c.cancellationsMu.Lock()
	c.cancellations[id] = &cancellationRegistration{token: token}
	c.cancellationsMu.Unlock()
	return id
}

// Arm watchers only after the request carrying their IDs is on the FIFO write
// queue. Otherwise an already canceled token can reach the host before the
// invocation that registers it, and the host ignores unknown cancellation IDs.
func (c *client) startCancellations(args map[string]any) {
	c.cancellationsMu.Lock()
	defer c.cancellationsMu.Unlock()
	for _, v := range args {
		if id, ok := v.(string); ok {
			if registration := c.cancellations[id]; registration != nil && registration.stop == nil {
				registration.stop = context.AfterFunc(registration.token.ctx, func() { c.cancelToken(id) })
			}
		}
	}
}

// releaseCancellations detaches the watchers of the cancellation IDs carried
// in args. invokeCapability calls it when the call returns, so a long-lived
// token used for many calls does not keep one watcher per call. Generated code
// and ReferenceExpression.GetValue only ever put these IDs in top-level args.
func (c *client) releaseCancellations(args map[string]any) {
	c.cancellationsMu.Lock()
	defer c.cancellationsMu.Unlock()
	for _, v := range args {
		id, ok := v.(string)
		if !ok {
			continue
		}
		if registration, found := c.cancellations[id]; found {
			// Cancellation closes Done before running AfterFunc. Do not stop a
			// canceled token's notification when its local call returns first.
			if registration.stop != nil && registration.token.ctx.Err() == nil {
				registration.stop()
			}
			delete(c.cancellations, id)
		}
	}
}

// connect establishes the connection to the AppHost server and starts the
// background reader and writer goroutines.
func (c *client) connect(ctx context.Context, timeout time.Duration) error {
	c.mu.Lock()
	connected := c.conn != nil
	c.mu.Unlock()
	if connected {
		return nil
	}

	// Dial without holding c.mu: the dial can take up to timeout, and
	// sendRequest, disconnect and onDisconnect all wait on the same lock.
	rawConn, err := openConnection(c.socketPath, timeout)
	if err != nil {
		return fmt.Errorf("failed to connect to AppHost: %w", err)
	}

	authToken := os.Getenv("ASPIRE_REMOTE_APPHOST_TOKEN")
	if authToken == "" {
		cErr := rawConn.Close()
		return errors.Join(errors.New("ASPIRE_REMOTE_APPHOST_TOKEN environment variable is not set"), cErr)
	}

	c.mu.Lock()
	if c.conn != nil {
		// A concurrent connect installed its connection while this one was dialing.
		c.mu.Unlock()
		_ = rawConn.Close()
		return nil
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
	defer c.releaseCancellations(args)

	if err := validateCapabilityArgs(capabilityID, args); err != nil {
		return nil, err
	}

	c.mu.Lock()
	conn := c.conn
	c.mu.Unlock()
	if conn == nil {
		return nil, errors.New("not connected to AppHost")
	}
	result, err := conn.sendRequestQueued(ctx, "invokeCapability", []any{capabilityID, c.marshalTransportValue(args)}, func() {
		c.startCancellations(args)
	})
	if err != nil {
		return nil, err
	}
	if hasAtsErr, atsErr := tryGetAtsError(result); hasAtsErr {
		return nil, &CapabilityError{err: atsErr}
	}
	return wrapIfHandle(result, c)
}

func (c *client) marshalTransportValue(value any) any {
	if callback, ok := value.(func(...any) any); ok {
		return c.registerCallback(callback)
	}

	serialized := serializeValue(value)
	switch v := serialized.(type) {
	case func(...any) any:
		return c.registerCallback(v)
	case map[string]any:
		result := make(map[string]any, len(v))
		for key, nestedValue := range v {
			result[key] = c.marshalTransportValue(nestedValue)
		}
		return result
	case []any:
		result := make([]any, len(v))
		for i, item := range v {
			result[i] = c.marshalTransportValue(item)
		}
		return result
	default:
		return serialized
	}
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
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	result, err := c.sendRequest(ctx, "cancelToken", []any{tokenID})
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

// rpcError is the error object of a JSON-RPC error response. Error returns the
// message alone; the code and the optional data stay reachable for callers that
// need them:
//
//	var rpcErr interface{ Code() int }
//	if errors.As(err, &rpcErr) { ... }
type rpcError struct {
	code    int
	message string
	data    any
}

func (e *rpcError) Error() string { return e.message }
func (e *rpcError) Code() int     { return e.code }
func (e *rpcError) Data() any     { return e.data }

func extractResult(response map[string]any) (any, error) {
	if errObj, hasErr := response["error"]; hasErr {
		errMap, _ := errObj.(map[string]any)
		rpcErr := &rpcError{message: getString(errMap, "message"), data: errMap["data"]}
		// A decoded response carries the code as float64; the error that
		// connection.close synthesizes for pending requests carries an int.
		switch code := errMap["code"].(type) {
		case float64:
			rpcErr.code = int(code)
		case int:
			rpcErr.code = code
		}
		if rpcErr.message == "" {
			rpcErr.message = fmt.Sprintf("AppHost returned a JSON-RPC error (code %d)", rpcErr.code)
		}
		return nil, rpcErr
	}
	return response["result"], nil
}

func writeMessage(w io.Writer, msg map[string]any) error {
	body, err := json.Marshal(msg)
	if err != nil {
		return err
	}
	return writeMessageBody(w, body)
}

func writeMessageBody(w io.Writer, body []byte) error {
	header := fmt.Sprintf("Content-Length: %d\r\n\r\n", len(body))
	if _, err := w.Write([]byte(header)); err != nil {
		return err
	}
	_, err := w.Write(body)
	return err
}

// maxMessageSize caps the Content-Length accepted from the AppHost so a corrupt
// or hostile stream cannot make readMessage allocate an arbitrarily large body.
// It matches the cap in the Python runtime.
const maxMessageSize = 64 * 1024 * 1024
const maxHeaderSize = 8 * 1024
const maxHeaderCount = 16

func readMessage(reader *bufio.Reader) (map[string]any, error) {
	headers := make(map[string]string)
	headerSize := 0
	headerCount := 0
	for {
		// Accumulate fragments only within the total budget, including a line
		// with no newline. Example: "Content-Length: 42\r\n\r\n{...}".
		var lineBytes []byte
		for {
			fragment, err := reader.ReadSlice('\n')
			headerSize += len(fragment)
			if headerSize > maxHeaderSize {
				return nil, errors.New("AppHost message headers exceed 8192 bytes")
			}
			lineBytes = append(lineBytes, fragment...)
			if err == bufio.ErrBufferFull {
				continue
			}
			if err != nil {
				return nil, err
			}
			break
		}
		line := strings.TrimSpace(string(lineBytes))
		if line == "" {
			break
		}
		headerCount++
		if headerCount > maxHeaderCount {
			return nil, errors.New("AppHost message has more than 16 headers")
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
	if length > maxMessageSize {
		return nil, fmt.Errorf("content-length %d exceeds the maximum message size of %d bytes", length, maxMessageSize)
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
	// Go 1.26 supports Windows FILE_FLAG_* bits in os.OpenFile.
	// Overlapped I/O lets the background read and authentication write run
	// concurrently and allows Close to cancel a pending read.
	// https://go.dev/doc/go1.26#os
	const fileFlagOverlapped = 0x40000000
	f, err := os.OpenFile(path, os.O_RDWR|fileFlagOverlapped, 0)
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
