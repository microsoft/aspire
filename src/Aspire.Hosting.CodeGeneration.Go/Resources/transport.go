// Package aspire provides the ATS transport layer for JSON-RPC communication.
package aspire

import (
	"bufio"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"os"
	"runtime"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"
)

// AtsErrorCodes contains standard ATS error codes.
var AtsErrorCodes = struct {
	CapabilityNotFound string
	HandleNotFound     string
	TypeMismatch       string
	InvalidArgument    string
	ArgumentOutOfRange string
	CallbackError      string
	InternalError      string
}{
	CapabilityNotFound: "CAPABILITY_NOT_FOUND",
	HandleNotFound:     "HANDLE_NOT_FOUND",
	TypeMismatch:       "TYPE_MISMATCH",
	InvalidArgument:    "INVALID_ARGUMENT",
	ArgumentOutOfRange: "ARGUMENT_OUT_OF_RANGE",
	CallbackError:      "CALLBACK_ERROR",
	InternalError:      "INTERNAL_ERROR",
}

// CapabilityError represents an error returned from a capability invocation.
type CapabilityError struct {
	Code       string `json:"code"`
	Message    string `json:"message"`
	Capability string `json:"capability,omitempty"`
}

func (e *CapabilityError) Error() string {
	return e.Message
}

// Handle represents a reference to a server-side object.
type Handle struct {
	HandleID string `json:"$handle"`
	TypeID   string `json:"$type"`
}

// ToJSON returns the handle as a JSON-serializable map.
func (h *Handle) ToJSON() map[string]string {
	return map[string]string{
		"$handle": h.HandleID,
		"$type":   h.TypeID,
	}
}

func (h *Handle) String() string {
	return fmt.Sprintf("Handle<%s>(%s)", h.TypeID, h.HandleID)
}

// IsMarshalledHandle checks if a value is a marshalled handle.
func IsMarshalledHandle(value any) bool {
	m, ok := value.(map[string]any)
	if !ok {
		return false
	}
	_, hasHandle := m["$handle"]
	_, hasType := m["$type"]
	return hasHandle && hasType
}

// IsAtsError checks if a value is an ATS error.
func IsAtsError(value any) bool {
	m, ok := value.(map[string]any)
	if !ok {
		return false
	}
	_, hasError := m["$error"]
	return hasError
}

// HandleWrapperFactory creates a wrapper for a handle.
type HandleWrapperFactory func(handle *Handle, client *AspireClient) any

var (
	handleWrapperRegistry = make(map[string]HandleWrapperFactory)
	handleWrapperMu       sync.RWMutex
)

// RegisterHandleWrapper registers a factory for wrapping handles of a specific type.
func RegisterHandleWrapper(typeID string, factory HandleWrapperFactory) {
	handleWrapperMu.Lock()
	defer handleWrapperMu.Unlock()
	handleWrapperRegistry[typeID] = factory
}

// WrapIfHandle wraps a value if it's a marshalled handle.
func WrapIfHandle(value any, client *AspireClient) any {
	if !IsMarshalledHandle(value) {
		return value
	}
	m := value.(map[string]any)
	handle := &Handle{
		HandleID: m["$handle"].(string),
		TypeID:   m["$type"].(string),
	}
	if client != nil {
		handleWrapperMu.RLock()
		factory, ok := handleWrapperRegistry[handle.TypeID]
		handleWrapperMu.RUnlock()
		if ok {
			return factory(handle, client)
		}
	}
	return handle
}

// Callback management
var (
	callbackRegistry = make(map[string]func(...any) any)
	callbackMu       sync.RWMutex
	callbackCounter  atomic.Int64
)

// RegisterCallback registers a callback and returns its ID.
func RegisterCallback(callback func(...any) any) string {
	callbackMu.Lock()
	defer callbackMu.Unlock()
	id := fmt.Sprintf("callback_%d_%d", callbackCounter.Add(1), time.Now().UnixMilli())
	callbackRegistry[id] = callback
	return id
}

// UnregisterCallback removes a callback by ID.
func UnregisterCallback(callbackID string) bool {
	callbackMu.Lock()
	defer callbackMu.Unlock()
	_, exists := callbackRegistry[callbackID]
	delete(callbackRegistry, callbackID)
	return exists
}

// CancellationToken provides cooperative cancellation.
type CancellationToken struct {
	cancelled atomic.Bool
	callbacks []func()
	mu        sync.Mutex
}

// NewCancellationToken creates a new cancellation token.
func NewCancellationToken() *CancellationToken {
	return &CancellationToken{}
}

// Cancel cancels the token and invokes all registered callbacks.
func (ct *CancellationToken) Cancel() {
	if ct.cancelled.Swap(true) {
		return // Already cancelled
	}
	ct.mu.Lock()
	callbacks := ct.callbacks
	ct.callbacks = nil
	ct.mu.Unlock()
	for _, cb := range callbacks {
		cb()
	}
}

// IsCancelled returns true if the token has been cancelled.
func (ct *CancellationToken) IsCancelled() bool {
	return ct.cancelled.Load()
}

// Register registers a callback to be invoked when cancelled.
func (ct *CancellationToken) Register(callback func()) func() {
	if ct.IsCancelled() {
		callback()
		return func() {}
	}
	ct.mu.Lock()
	ct.callbacks = append(ct.callbacks, callback)
	ct.mu.Unlock()
	return func() {
		ct.mu.Lock()
		defer ct.mu.Unlock()
		for i, cb := range ct.callbacks {
			if &cb == &callback {
				ct.callbacks = append(ct.callbacks[:i], ct.callbacks[i+1:]...)
				break
			}
		}
	}
}

// RegisterCancellation registers a cancellation token with the client.
func RegisterCancellation(token *CancellationToken, client *AspireClient) string {
	if token == nil {
		return ""
	}
	id := fmt.Sprintf("ct_%d_%d", time.Now().UnixMilli(), time.Now().UnixNano())
	token.Register(func() {
		client.CancelToken(id)
	})
	return id
}

// ── connection ────────────────────────────────────────────────────────────────
//
// connection owns all I/O for a single live socket connection. It is created by
// AspireClient.Connect and torn down by connection.close. AspireClient holds a
// *connection pointer and replaces it with nil on disconnect; all other state
// lives here, not on AspireClient.
//
// Concurrency model:
//   - connection.mu guards only pending and closed. It is never held during a
//     socket read, a socket write, or a send to a pending channel.
//   - The writer goroutine (writeLoop) is the sole writer to rawConn; the reader
//     goroutine (readLoop) is the sole reader. Neither holds connection.mu during
//     I/O.
//   - Callers (InvokeCapability etc.) write to writeQueue (a buffered channel)
//     and block on their per-request respCh. No caller goroutine ever touches
//     the socket directly.

type connection struct {
	rawConn io.ReadWriteCloser
	reader  *bufio.Reader
	client  *AspireClient // passed through to invokeCallback → WrapIfHandle

	writeQueue chan map[string]any // buffered outbound queue; writeLoop drains it
	done       chan struct{}       // closed exactly once when connection closes

	mu      sync.Mutex
	pending map[int64]chan map[string]any
	closed  bool

	nextID  atomic.Int64
	onClose func(error) // called once, outside all locks, after full teardown
}

func newConnection(rawConn io.ReadWriteCloser, client *AspireClient, onClose func(error)) *connection {
	return &connection{
		rawConn:    rawConn,
		reader:     bufio.NewReader(rawConn),
		client:     client,
		writeQueue: make(chan map[string]any, 64),
		done:       make(chan struct{}),
		pending:    make(map[int64]chan map[string]any),
		onClose:    onClose,
	}
}

// start launches the reader and writer goroutines. Call after the connection
// has been stored in AspireClient so that onClose can safely clear it.
func (c *connection) start() {
	go c.readLoop()
	go c.writeLoop()
}

// close tears down the connection exactly once. It is safe to call from
// multiple goroutines concurrently; only the first caller proceeds.
//
// Teardown order (no lock held during I/O):
//  1. Claim ownership under mu (idempotency guard).
//  2. Close rawConn — causes readMessage to return an error → readLoop exits.
//  3. Close done — signals writeLoop and any sendRequest waiters.
//  4. Drain pending channels with a synthetic error response.
//  5. Call onClose outside all locks.
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

	c.rawConn.Close()
	close(c.done)

	if err == nil {
		err = errors.New("connection closed")
	}
	errResp := map[string]any{
		"error": map[string]any{
			"code":    -32000,
			"message": err.Error(),
		},
	}
	for _, ch := range pending {
		ch <- errResp // buffered (cap 1), never blocks
	}

	c.onClose(err)
}

// writeLoop is the sole writer goroutine. It drains writeQueue and writes each
// message to rawConn without holding any lock.
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

// readLoop is the sole reader goroutine. It reads messages from rawConn
// without holding any lock and dispatches them.
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

// dispatch routes an incoming message: server-initiated requests (callbacks)
// are handled in their own goroutines so they can make nested capability calls
// without blocking the read loop; responses are delivered to their pending channel.
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
				ch <- msg // buffered (cap 1), never blocks
			}
		}
		// Unknown id type: ignore rather than panic.
	}
}

// enqueue puts an outbound message on the write queue without blocking. If the
// connection is already closing, the message is silently discarded.
func (c *connection) enqueue(msg map[string]any) {
	select {
	case c.writeQueue <- msg:
	case <-c.done:
	}
}

// handleCallbackRequest processes a server-initiated request. It runs in its
// own goroutine (spawned by dispatch) so it can call sendRequest freely.
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

	result, err := invokeCallback(callbackID, args, c.client)
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
// the matching response arrives. No lock is held during the wait or during I/O.
//
// Registration order: pending[id] is recorded before the message enters
// writeQueue, so the reader goroutine can always find the response channel.
func (c *connection) sendRequest(method string, params []any) (any, error) {
	id := c.nextID.Add(1)
	respCh := make(chan map[string]any, 1)
	msg := map[string]any{
		"jsonrpc": "2.0",
		"id":      id,
		"method":  method,
		"params":  params,
	}

	// Register before enqueuing so the reader can always find the channel.
	c.mu.Lock()
	if c.closed {
		c.mu.Unlock()
		return nil, errors.New("not connected to AppHost")
	}
	c.pending[id] = respCh
	c.mu.Unlock()

	// Send to write queue; abort cleanly if the connection is already closing.
	select {
	case c.writeQueue <- msg:
	case <-c.done:
		c.mu.Lock()
		delete(c.pending, id) // no-op if close() already cleared the map
		c.mu.Unlock()
		return nil, errors.New("not connected to AppHost")
	}

	// Wait for response. If done fires first, drain respCh in case the response
	// arrived in the same scheduler turn before done was noticed.
	select {
	case resp := <-respCh:
		return extractResult(resp)
	case <-c.done:
		select {
		case resp := <-respCh:
			return extractResult(resp)
		default:
			return nil, errors.New("not connected to AppHost")
		}
	}
}

// ── AspireClient ──────────────────────────────────────────────────────────────
//
// AspireClient manages the connection lifecycle. It delegates all I/O to the
// *connection it holds. A single mutex guards the connection pointer and the
// disconnect-callback list; nothing else.

// AspireClient manages the connection to the AppHost server.
type AspireClient struct {
	socketPath string

	// mu guards conn and disconnectCallbacks.
	mu                  sync.Mutex
	conn                *connection
	disconnectCallbacks []func()
}

// NewAspireClient creates a new client for the given socket path.
func NewAspireClient(socketPath string) *AspireClient {
	return &AspireClient{socketPath: socketPath}
}

// Connect establishes the connection to the AppHost server and starts the
// background reader and writer goroutines.
func (c *AspireClient) Connect() error {
	c.mu.Lock()
	if c.conn != nil {
		c.mu.Unlock()
		return nil
	}

	rawConn, err := openConnection(c.socketPath)
	if err != nil {
		c.mu.Unlock()
		return fmt.Errorf("failed to connect to AppHost: %w", err)
	}

	conn := newConnection(rawConn, c, c.onConnectionClose)
	c.conn = conn
	c.mu.Unlock()

	conn.start()
	return nil
}

// OnDisconnect registers a callback to be invoked exactly once when the
// connection is closed, whether by Disconnect or by a read/write error.
func (c *AspireClient) OnDisconnect(callback func()) {
	c.mu.Lock()
	c.disconnectCallbacks = append(c.disconnectCallbacks, callback)
	c.mu.Unlock()
}

// InvokeCapability invokes a capability on the server.
func (c *AspireClient) InvokeCapability(capabilityID string, args map[string]any) (any, error) {
	result, err := c.sendRequest("invokeCapability", []any{capabilityID, args})
	if err != nil {
		return nil, err
	}
	if IsAtsError(result) {
		errMap := result.(map[string]any)["$error"].(map[string]any)
		return nil, &CapabilityError{
			Code:       getString(errMap, "code"),
			Message:    getString(errMap, "message"),
			Capability: getString(errMap, "capability"),
		}
	}
	return WrapIfHandle(result, c), nil
}

// Authenticate authenticates the client session with the AppHost server.
func (c *AspireClient) Authenticate(token string) error {
	result, err := c.sendRequest("authenticate", []any{token})
	if err != nil {
		return err
	}

	authenticated, _ := result.(bool)
	if !authenticated {
		return errors.New("failed to authenticate to the AppHost server")
	}

	return nil
}

// CancelToken cancels a cancellation token on the server.
func (c *AspireClient) CancelToken(tokenID string) bool {
	result, err := c.sendRequest("cancelToken", []any{tokenID})
	if err != nil {
		return false
	}
	b, _ := result.(bool)
	return b
}

// Disconnect closes the connection. Safe to call multiple times and to race
// with the background goroutines — only the first closer runs the teardown.
func (c *AspireClient) Disconnect() {
	c.mu.Lock()
	conn := c.conn
	c.conn = nil
	c.mu.Unlock()
	if conn != nil {
		conn.close(nil)
	}
}

// sendRequest snapshots the active connection and delegates to it.
func (c *AspireClient) sendRequest(method string, params []any) (any, error) {
	c.mu.Lock()
	conn := c.conn
	c.mu.Unlock()
	if conn == nil {
		return nil, errors.New("not connected to AppHost")
	}
	return conn.sendRequest(method, params)
}

// onConnectionClose is the onClose callback passed to each connection. It clears
// the conn pointer (in case Disconnect didn't already) and fires the registered
// disconnect callbacks, all outside any lock.
func (c *AspireClient) onConnectionClose(_ error) {
	c.mu.Lock()
	c.conn = nil // may already be nil if Disconnect() ran concurrently
	callbacks := c.disconnectCallbacks
	c.disconnectCallbacks = nil
	c.mu.Unlock()
	for _, cb := range callbacks {
		cb()
	}
}

// ── Package-level I/O helpers ─────────────────────────────────────────────────

// extractResult interprets a decoded JSON-RPC response map and returns its
// result value or an error. It is called by connection.sendRequest.
func extractResult(response map[string]any) (any, error) {
	if errObj, hasErr := response["error"]; hasErr {
		errMap, _ := errObj.(map[string]any)
		return nil, errors.New(getString(errMap, "message"))
	}
	return response["result"], nil
}

// writeMessage serialises msg as a Content-Length-framed JSON-RPC message and
// writes it to w. Called only from writeLoop — no lock is held.
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

// readMessage reads one Content-Length-framed JSON-RPC message from r. Called
// only from readLoop — no lock is held.
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

// jsonRPCID converts a JSON-RPC id value to an int64 key used to look up the
// pending request. The JSON-RPC spec allows numeric or string IDs; standard
// Go JSON decoding produces float64 for numbers, but servers may also emit
// string IDs that contain a numeric value (e.g. "42"). All four forms are
// handled. Returns false for null, missing, or non-numeric string IDs.
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

func invokeCallback(callbackID string, args any, client *AspireClient) (any, error) {
	if callbackID == "" {
		return nil, errors.New("callback ID missing")
	}

	callbackMu.RLock()
	callback, ok := callbackRegistry[callbackID]
	callbackMu.RUnlock()
	if !ok {
		return nil, fmt.Errorf("callback not found: %s", callbackID)
	}

	// Convert args to positional arguments
	var positionalArgs []any
	if argsMap, ok := args.(map[string]any); ok {
		for i := 0; ; i++ {
			key := fmt.Sprintf("p%d", i)
			if val, exists := argsMap[key]; exists {
				positionalArgs = append(positionalArgs, WrapIfHandle(val, client))
			} else {
				break
			}
		}
	} else if args != nil {
		positionalArgs = append(positionalArgs, WrapIfHandle(args, client))
	}

	return callback(positionalArgs...), nil
}

func getString(m map[string]any, key string) string {
	if v, ok := m[key]; ok {
		if s, ok := v.(string); ok {
			return s
		}
	}
	return ""
}

func openConnection(socketPath string) (io.ReadWriteCloser, error) {
	if runtime.GOOS == "windows" {
		// On Windows, use named pipes
		pipePath := `\\.\pipe\` + socketPath
		return openNamedPipe(pipePath)
	}
	// On Unix, use Unix domain sockets
	return net.Dial("unix", socketPath)
}

// openNamedPipe opens a Windows named pipe.
func openNamedPipe(path string) (io.ReadWriteCloser, error) {
	// Use os.OpenFile for named pipes on Windows
	f, err := os.OpenFile(path, os.O_RDWR, 0)
	if err != nil {
		return nil, err
	}
	return f, nil
}
