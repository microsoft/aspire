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
	CapabilityNotFound  string
	HandleNotFound      string
	TypeMismatch        string
	InvalidArgument     string
	ArgumentOutOfRange  string
	CallbackError       string
	InternalError       string
}{
	CapabilityNotFound:  "CAPABILITY_NOT_FOUND",
	HandleNotFound:      "HANDLE_NOT_FOUND",
	TypeMismatch:        "TYPE_MISMATCH",
	InvalidArgument:     "INVALID_ARGUMENT",
	ArgumentOutOfRange:  "ARGUMENT_OUT_OF_RANGE",
	CallbackError:       "CALLBACK_ERROR",
	InternalError:       "INTERNAL_ERROR",
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

// AspireClient manages the connection to the AppHost server.
type AspireClient struct {
	socketPath          string
	conn                io.ReadWriteCloser
	reader              *bufio.Reader
	nextID              atomic.Int64
	disconnectCallbacks []func()
	connected           bool
	ioMu                sync.Mutex
}

// NewAspireClient creates a new client for the given socket path.
func NewAspireClient(socketPath string) *AspireClient {
	return &AspireClient{
		socketPath: socketPath,
	}
}

// Connect establishes the connection to the AppHost server.
func (c *AspireClient) Connect() error {
	if c.connected {
		return nil
	}

	conn, err := openConnection(c.socketPath)
	if err != nil {
		return fmt.Errorf("failed to connect to AppHost: %w", err)
	}

	c.conn = conn
	c.reader = bufio.NewReader(conn)
	c.connected = true
	return nil
}

// OnDisconnect registers a callback for disconnection.
func (c *AspireClient) OnDisconnect(callback func()) {
	c.disconnectCallbacks = append(c.disconnectCallbacks, callback)
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

// Disconnect closes the connection.
func (c *AspireClient) Disconnect() {
	c.connected = false
	if c.conn != nil {
		c.conn.Close()
		c.conn = nil
	}
	for _, cb := range c.disconnectCallbacks {
		cb()
	}
}

func (c *AspireClient) sendRequest(method string, params []any) (any, error) {
	c.ioMu.Lock()
	defer c.ioMu.Unlock()

	requestID := c.nextID.Add(1)
	message := map[string]any{
		"jsonrpc": "2.0",
		"id":      requestID,
		"method":  method,
		"params":  params,
	}

	if err := c.writeMessage(message); err != nil {
		return nil, err
	}

	// Read messages until we get our response
	for {
		response, err := c.readMessage()
		if err != nil {
			return nil, fmt.Errorf("connection closed while waiting for response: %w", err)
		}

		// Check if this is a callback request from the server
		if _, hasMethod := response["method"]; hasMethod {
			c.handleCallbackRequest(response)
			continue
		}

		// This is a response - check if it's our response
		if respID, ok := response["id"].(float64); ok && int64(respID) == requestID {
			if errObj, hasErr := response["error"]; hasErr {
				errMap := errObj.(map[string]any)
				return nil, errors.New(getString(errMap, "message"))
			}
			return response["result"], nil
		}
	}
}

func (c *AspireClient) writeMessage(message map[string]any) error {
	if c.conn == nil {
		return errors.New("not connected to AppHost")
	}
	body, err := json.Marshal(message)
	if err != nil {
		return err
	}
	header := fmt.Sprintf("Content-Length: %d\r\n\r\n", len(body))
	_, err = c.conn.Write([]byte(header))
	if err != nil {
		return err
	}
	_, err = c.conn.Write(body)
	return err
}

func (c *AspireClient) handleCallbackRequest(message map[string]any) {
	method := getString(message, "method")
	requestID := message["id"]

	if method != "invokeCallback" {
		if requestID != nil {
			c.writeMessage(map[string]any{
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

	result, err := invokeCallback(callbackID, args, c)
	if err != nil {
		c.writeMessage(map[string]any{
			"jsonrpc": "2.0",
			"id":      requestID,
			"error":   map[string]any{"code": -32000, "message": err.Error()},
		})
		return
	}
	c.writeMessage(map[string]any{
		"jsonrpc": "2.0",
		"id":      requestID,
		"result":  result,
	})
}

func (c *AspireClient) readMessage() (map[string]any, error) {
	if c.reader == nil {
		return nil, errors.New("not connected")
	}

	headers := make(map[string]string)
	for {
		line, err := c.reader.ReadString('\n')
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
	_, err = io.ReadFull(c.reader, body)
	if err != nil {
		return nil, err
	}

	var message map[string]any
	if err := json.Unmarshal(body, &message); err != nil {
		return nil, err
	}
	return message, nil
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
