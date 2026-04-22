// Package aspire provides base types and utilities for Aspire Go SDK.
package aspire

import (
	"encoding/json"
	"errors"
	"fmt"
	"sync"
)

// ── Builder context ───────────────────────────────────────────────────────────

// builderContext is created once per CreateBuilder call and shared by all
// resources created from that builder. It mirrors TypeScript's pending-promise
// tracking: goroutines are submitted immediately, and Build() is the flush point
// (analogous to TypeScript's flushPendingPromises / Promise.allSettled).
type builderContext struct {
	wg   sync.WaitGroup
	mu   sync.Mutex
	errs []error
}

// submit fires fn in a new goroutine tracked by the WaitGroup.
// If b is nil (factory-deserialized resource without a builder context), fn runs synchronously.
// Errors returned by fn are accumulated via errors.Join and returned by wait().
func (b *builderContext) submit(fn func() error) {
	if b == nil {
		_ = fn()
		return
	}
	b.wg.Add(1)
	go func() {
		defer b.wg.Done()
		if err := fn(); err != nil {
			b.mu.Lock()
			b.errs = append(b.errs, err)
			b.mu.Unlock()
		}
	}()
}

// wait blocks until all submitted goroutines finish and returns their combined errors.
func (b *builderContext) wait() error {
	b.wg.Wait()
	b.mu.Lock()
	defer b.mu.Unlock()
	return errors.Join(b.errs...)
}

// ── HandleReference ───────────────────────────────────────────────────────────

// HandleReference is implemented by every handle-wrapper type.
// Pass any resource builder directly to methods that accept a HandleReference parameter.
type HandleReference interface {
	Handle() *Handle
}

// ── HandleWrapperBase ─────────────────────────────────────────────────────────

// HandleWrapperBase is the base for all handle-wrapper types.
//
// Lazy resolution: handle and handleErr are set exactly once — after the creation
// RPC completes inside the goroutine that owns this wrapper. The ready channel is
// closed at that moment, unblocking any goroutine waiting in awaitHandle().
//
// Thread safety: handle and handleErr are written under mu; ready is closed exactly
// once (subsequent calls would panic, but setHandle/setHandleErr enforce the invariant).
type HandleWrapperBase struct {
	mu        sync.Mutex
	handle    *Handle
	handleErr error
	ready     chan struct{} // closed when handle or handleErr is set
	client    *AspireClient
	bctx      *builderContext // shared across all resources from the same builder; nil for standalone use
}

// NewHandleWrapperBase creates a pre-resolved wrapper (handle already known).
// Used for the builder itself (CreateBuilder) and for factory-deserialized handles.
func NewHandleWrapperBase(handle *Handle, client *AspireClient, bctx *builderContext) HandleWrapperBase {
	ch := make(chan struct{})
	close(ch) // already resolved
	return HandleWrapperBase{handle: handle, client: client, bctx: bctx, ready: ch}
}

// newLazyHandleWrapper creates an unresolved wrapper.
// The caller must eventually call setHandle or setHandleErr to unblock waiters.
func newLazyHandleWrapper(client *AspireClient, bctx *builderContext) HandleWrapperBase {
	return HandleWrapperBase{client: client, bctx: bctx, ready: make(chan struct{})}
}

// setHandle resolves the wrapper with a valid handle. Must be called exactly once.
func (h *HandleWrapperBase) setHandle(handle *Handle) {
	h.mu.Lock()
	h.handle = handle
	h.mu.Unlock()
	close(h.ready)
}

// setHandleErr resolves the wrapper with an error. Must be called exactly once.
func (h *HandleWrapperBase) setHandleErr(err error) {
	h.mu.Lock()
	h.handleErr = err
	h.mu.Unlock()
	close(h.ready)
}

// awaitHandle blocks until the handle is resolved (or an error is set).
// Call this at the start of every goroutine submitted to bctx before using the handle.
func (h *HandleWrapperBase) awaitHandle() (*Handle, error) {
	<-h.ready
	return h.handle, h.handleErr
}

// Handle blocks until the handle is resolved and returns it.
// Safe to call from any goroutine.
func (h *HandleWrapperBase) Handle() *Handle {
	<-h.ready
	return h.handle
}

// Client returns the AspireClient associated with this wrapper.
func (h *HandleWrapperBase) Client() *AspireClient {
	return h.client
}

// Err returns the handle-resolution error non-blockingly.
// Returns nil if the handle is not yet resolved or if resolution succeeded.
// For complete error collection across all goroutines use builder.Build().
func (h *HandleWrapperBase) Err() error {
	select {
	case <-h.ready:
		return h.handleErr
	default:
		return nil
	}
}

// ── ResourceBuilderBase ───────────────────────────────────────────────────────

// ResourceBuilderBase extends HandleWrapperBase for resource builders.
// It inherits lazy handle resolution and goroutine coordination from HandleWrapperBase.
type ResourceBuilderBase struct {
	HandleWrapperBase
}

// NewResourceBuilderBase creates a pre-resolved resource builder.
func NewResourceBuilderBase(handle *Handle, client *AspireClient, bctx *builderContext) ResourceBuilderBase {
	return ResourceBuilderBase{HandleWrapperBase: NewHandleWrapperBase(handle, client, bctx)}
}

// newLazyResourceBuilder creates a resource builder whose handle is not yet resolved.
// Used by Add* methods: the child is returned before its creation RPC completes.
func newLazyResourceBuilder(client *AspireClient, bctx *builderContext) ResourceBuilderBase {
	return ResourceBuilderBase{HandleWrapperBase: newLazyHandleWrapper(client, bctx)}
}

// NewErroredResourceBuilder creates a pre-errored resource builder.
// Used by handle-wrapper factories and by Add* goroutines that fail.
func NewErroredResourceBuilder(err error) ResourceBuilderBase {
	rb := ResourceBuilderBase{HandleWrapperBase: HandleWrapperBase{ready: make(chan struct{})}}
	rb.setHandleErr(err)
	return rb
}

// ── ReferenceExpression ───────────────────────────────────────────────────────

// ReferenceExpression represents a reference expression.
// Supports value mode (Format + Args) and conditional mode (Condition + WhenTrue + WhenFalse).
type ReferenceExpression struct {
	Format string
	Args   []any

	// Conditional mode fields
	Condition     any
	WhenTrue      *ReferenceExpression
	WhenFalse     *ReferenceExpression
	MatchValue    string

	// Handle mode fields (for server-returned expressions)
	handle *Handle
	client *AspireClient
}

// NewReferenceExpression creates a new reference expression in value mode.
func NewReferenceExpression(format string, args ...any) *ReferenceExpression {
	// isConditional is implicitly false
	return &ReferenceExpression{Format: format, Args: args, handle: nil, client: nil}
}

// NewConditionalReferenceExpression creates a conditional reference expression from its parts.
func NewConditionalReferenceExpression(condition any, matchValue string, whenTrue *ReferenceExpression, whenFalse *ReferenceExpression) *ReferenceExpression {
	if matchValue == "" {
		matchValue = "True"
	}
	return &ReferenceExpression{
		Condition:     condition,
		WhenTrue:  whenTrue,
		WhenFalse: whenFalse,
		MatchValue: matchValue,
		handle:    nil,
		client:    nil,
	}
}

// newHandleBackedReferenceExpression creates a reference expression from a server-returned handle.
func newHandleBackedReferenceExpression(handle *Handle, client *AspireClient) *ReferenceExpression {
	return &ReferenceExpression{handle: handle, client: client}
}
// RefExpr is a convenience alias for NewReferenceExpression.
func RefExpr(format string, args ...any) *ReferenceExpression {
	return NewReferenceExpression(format, args...)
}

// ToJSON returns the reference expression as a JSON-serializable map.
func (r *ReferenceExpression) ToJSON() map[string]any {
	if r.handle != nil {
		// In handle mode, serialize as the handle itself.
		h := r.handle.ToJSON()
		return map[string]any{"$handle": h["$handle"], "$type": h["$type"]}
	}
	if r.Condition != nil {
		return map[string]any{
			"$expr": map[string]any{
				"condition":  SerializeValue(r.Condition),
				"whenTrue":   r.WhenTrue.ToJSON(),
				"whenFalse":  r.WhenFalse.ToJSON(),
				"matchValue": r.MatchValue,
			},
		}
	}
	return map[string]any{
		"$expr": map[string]any{
			"format": r.Format,
			"args":   r.Args,
		},
	}
}

// GetValue resolves the expression to its string value on the server.
// Only available on server-returned ReferenceExpression instances (handle mode).
func (r *ReferenceExpression) GetValue(token *CancellationToken) (string, error) {
	if r.handle == nil || r.client == nil {
		return "", errors.New("aspire: GetValue is only available on server-returned ReferenceExpression instances")
	}

	args := map[string]any{
		"context": r.handle.ToJSON(),
	}
	tokenID := RegisterCancellation(token, r.client)
	if tokenID != "" {
		args["cancellationToken"] = tokenID
	}

	result, err := r.client.InvokeCapability("Aspire.Hosting.ApplicationModel/getValue", args)
	if err != nil {
		return "", err
	}
	// The result can be string or null.
	if s, ok := result.(string); ok {
		return s, nil
	}
	return "", nil // Corresponds to a null result
}

// ── AspireList ────────────────────────────────────────────────────────────────

// AspireList is a handle-backed list with lazy handle resolution.
// Provides full CRUD and enumeration operations matching the TypeScript AspireList.
type AspireList[T any] struct {
	HandleWrapperBase
	getterCapabilityID string
	resolvedHandle     *Handle
}

// NewAspireList creates a new AspireList backed by an already-resolved handle.
func NewAspireList[T any](handle *Handle, client *AspireClient) *AspireList[T] {
	return &AspireList[T]{
		HandleWrapperBase: NewHandleWrapperBase(handle, client, nil),
		resolvedHandle:    handle,
	}
}

// NewAspireListWithGetter creates a new AspireList with lazy handle resolution via a capability.
func NewAspireListWithGetter[T any](contextHandle *Handle, client *AspireClient, getterCapabilityID string) *AspireList[T] {
	return &AspireList[T]{
		HandleWrapperBase:  NewHandleWrapperBase(contextHandle, client, nil),
		getterCapabilityID: getterCapabilityID,
	}
}

// EnsureHandle lazily resolves the list handle via the getter capability if needed.
func (l *AspireList[T]) EnsureHandle() *Handle {
	if l.resolvedHandle != nil {
		return l.resolvedHandle
	}
	if l.getterCapabilityID != "" {
		result, err := l.client.InvokeCapability(l.getterCapabilityID, map[string]any{
			"context": l.handle.ToJSON(),
		})
		if err == nil {
			if handle, ok := result.(*Handle); ok {
				l.resolvedHandle = handle
			}
		}
	}
	if l.resolvedHandle == nil {
		l.resolvedHandle = l.handle
	}
	return l.resolvedHandle
}

// ToJSON returns the list handle as a JSON-serializable map.
// Allows AspireList to be passed to SerializeValue directly.
func (l *AspireList[T]) ToJSON() map[string]string {
	return l.EnsureHandle().ToJSON()
}

// Count returns the number of elements in the list.
func (l *AspireList[T]) Count() (int, error) {
	result, err := l.client.InvokeCapability("Aspire.Hosting/List.length", map[string]any{
		"list": l.EnsureHandle().ToJSON(),
	})
	if err != nil {
		return 0, err
	}
	if n, ok := result.(float64); ok {
		return int(n), nil
	}
	return 0, nil
}

// Get returns the element at the specified index.
func (l *AspireList[T]) Get(index int) (T, error) {
	var zero T
	result, err := l.client.InvokeCapability("Aspire.Hosting/List.get", map[string]any{
		"list":  l.EnsureHandle().ToJSON(),
		"index": index,
	})
	if err != nil {
		return zero, err
	}
	if v, ok := result.(T); ok {
		return v, nil
	}
	return zero, fmt.Errorf("aspire: list.get: unexpected result type %T", result)
}

// Add appends an element to the end of the list.
func (l *AspireList[T]) Add(item T) error {
	_, err := l.client.InvokeCapability("Aspire.Hosting/List.add", map[string]any{
		"list": l.EnsureHandle().ToJSON(),
		"item": SerializeValue(item),
	})
	return err
}

// RemoveAt removes the element at the specified index.
func (l *AspireList[T]) RemoveAt(index int) error {
	_, err := l.client.InvokeCapability("Aspire.Hosting/List.removeAt", map[string]any{
		"list":  l.EnsureHandle().ToJSON(),
		"index": index,
	})
	return err
}

// Clear removes all elements from the list.
func (l *AspireList[T]) Clear() error {
	_, err := l.client.InvokeCapability("Aspire.Hosting/List.clear", map[string]any{
		"list": l.EnsureHandle().ToJSON(),
	})
	return err
}

// ToArray returns all elements as a Go slice.
func (l *AspireList[T]) ToArray() ([]T, error) {
	result, err := l.client.InvokeCapability("Aspire.Hosting/List.toArray", map[string]any{
		"list": l.EnsureHandle().ToJSON(),
	})
	if err != nil {
		return nil, err
	}
	if arr, ok := result.([]any); ok {
		items := make([]T, 0, len(arr))
		for _, item := range arr {
			if v, ok := item.(T); ok {
				items = append(items, v)
			}
		}
		return items, nil
	}
	return nil, nil
}

// ── AspireDict ────────────────────────────────────────────────────────────────

// AspireDict is a handle-backed dictionary with lazy handle resolution.
// Provides full CRUD and enumeration operations matching the TypeScript AspireDict.
type AspireDict[K comparable, V any] struct {
	HandleWrapperBase
	getterCapabilityID string
	resolvedHandle     *Handle
}

// NewAspireDict creates a new AspireDict backed by an already-resolved handle.
func NewAspireDict[K comparable, V any](handle *Handle, client *AspireClient) *AspireDict[K, V] {
	return &AspireDict[K, V]{
		HandleWrapperBase: NewHandleWrapperBase(handle, client, nil),
		resolvedHandle:    handle,
	}
}

// NewAspireDictWithGetter creates a new AspireDict with lazy handle resolution via a capability.
func NewAspireDictWithGetter[K comparable, V any](contextHandle *Handle, client *AspireClient, getterCapabilityID string) *AspireDict[K, V] {
	return &AspireDict[K, V]{
		HandleWrapperBase:  NewHandleWrapperBase(contextHandle, client, nil),
		getterCapabilityID: getterCapabilityID,
	}
}

// EnsureHandle lazily resolves the dict handle via the getter capability if needed.
func (d *AspireDict[K, V]) EnsureHandle() *Handle {
	if d.resolvedHandle != nil {
		return d.resolvedHandle
	}
	if d.getterCapabilityID != "" {
		result, err := d.client.InvokeCapability(d.getterCapabilityID, map[string]any{
			"context": d.handle.ToJSON(),
		})
		if err == nil {
			if handle, ok := result.(*Handle); ok {
				d.resolvedHandle = handle
			}
		}
	}
	if d.resolvedHandle == nil {
		d.resolvedHandle = d.handle
	}
	return d.resolvedHandle
}

// ToJSON returns the dict handle as a JSON-serializable map.
// Allows AspireDict to be passed to SerializeValue directly.
func (d *AspireDict[K, V]) ToJSON() map[string]string {
	return d.EnsureHandle().ToJSON()
}

// Count returns the number of key-value pairs in the dictionary.
func (d *AspireDict[K, V]) Count() (int, error) {
	result, err := d.client.InvokeCapability("Aspire.Hosting/Dict.count", map[string]any{
		"dict": d.EnsureHandle().ToJSON(),
	})
	if err != nil {
		return 0, err
	}
	if n, ok := result.(float64); ok {
		return int(n), nil
	}
	return 0, nil
}

// Get returns the value associated with the specified key.
func (d *AspireDict[K, V]) Get(key K) (V, error) {
	var zero V
	result, err := d.client.InvokeCapability("Aspire.Hosting/Dict.get", map[string]any{
		"dict": d.EnsureHandle().ToJSON(),
		"key":  SerializeValue(key),
	})
	if err != nil {
		return zero, err
	}
	if v, ok := result.(V); ok {
		return v, nil
	}
	return zero, fmt.Errorf("aspire: dict.get: unexpected result type %T", result)
}

// Set sets the value for the specified key.
func (d *AspireDict[K, V]) Set(key K, value V) error {
	_, err := d.client.InvokeCapability("Aspire.Hosting/Dict.set", map[string]any{
		"dict":  d.EnsureHandle().ToJSON(),
		"key":   SerializeValue(key),
		"value": SerializeValue(value),
	})
	return err
}

// Has returns whether the dictionary contains the specified key.
func (d *AspireDict[K, V]) Has(key K) (bool, error) {
	result, err := d.client.InvokeCapability("Aspire.Hosting/Dict.has", map[string]any{
		"dict": d.EnsureHandle().ToJSON(),
		"key":  SerializeValue(key),
	})
	if err != nil {
		return false, err
	}
	if b, ok := result.(bool); ok {
		return b, nil
	}
	return false, nil
}

// Remove deletes the entry with the specified key.
// Returns true if the key was found and removed, false if the key was not found.
func (d *AspireDict[K, V]) Remove(key K) (bool, error) {
	result, err := d.client.InvokeCapability("Aspire.Hosting/Dict.remove", map[string]any{
		"dict": d.EnsureHandle().ToJSON(),
		"key":  SerializeValue(key),
	})
	if err != nil {
		return false, err
	}
	if b, ok := result.(bool); ok {
		return b, nil
	}
	return false, nil
}

// Clear removes all key-value pairs from the dictionary.
func (d *AspireDict[K, V]) Clear() error {
	_, err := d.client.InvokeCapability("Aspire.Hosting/Dict.clear", map[string]any{
		"dict": d.EnsureHandle().ToJSON(),
	})
	return err
}

// Keys returns all keys in the dictionary.
func (d *AspireDict[K, V]) Keys() ([]K, error) {
	result, err := d.client.InvokeCapability("Aspire.Hosting/Dict.keys", map[string]any{
		"dict": d.EnsureHandle().ToJSON(),
	})
	if err != nil {
		return nil, err
	}
	if arr, ok := result.([]any); ok {
		keys := make([]K, 0, len(arr))
		for _, item := range arr {
			if k, ok := item.(K); ok {
				keys = append(keys, k)
			}
		}
		return keys, nil
	}
	return nil, nil
}

// Values returns all values in the dictionary.
func (d *AspireDict[K, V]) Values() ([]V, error) {
	result, err := d.client.InvokeCapability("Aspire.Hosting/Dict.values", map[string]any{
		"dict": d.EnsureHandle().ToJSON(),
	})
	if err != nil {
		return nil, err
	}
	if arr, ok := result.([]any); ok {
		vals := make([]V, 0, len(arr))
		for _, item := range arr {
			if v, ok := item.(V); ok {
				vals = append(vals, v)
			}
		}
		return vals, nil
	}
	return nil, nil
}

// ToObject converts the dictionary to a Go map.
// Only works when K is a string.
func (d *AspireDict[K, V]) ToObject() (map[string]V, error) {
	result, err := d.client.InvokeCapability("Aspire.Hosting/Dict.toObject", map[string]any{
		"dict": d.EnsureHandle().ToJSON(),
	})
	if err != nil {
		return nil, err
	}
	if m, ok := result.(map[string]any); ok {
		obj := make(map[string]V, len(m))
		for k, val := range m {
			if v, ok := val.(V); ok {
				obj[k] = v
			}
		}
		return obj, nil
	}
	return nil, fmt.Errorf("aspire: dict.toObject: unexpected result type %T", result)
}
// ── Pointer helpers ───────────────────────────────────────────────────────────

// StringPtr returns a pointer to s.
func StringPtr(s string) *string { return &s }

// IntPtr returns a pointer to i.
func IntPtr(i int) *int { return &i }

// BoolPtr returns a pointer to b.
func BoolPtr(b bool) *bool { return &b }

// Float64Ptr returns a pointer to f.
func Float64Ptr(f float64) *float64 { return &f }

// ── AspireUnion ───────────────────────────────────────────────────────────────

// AspireUnion holds one value from a union type.
// It mirrors Java's AspireUnion: a simple transport wrapper with no JSON discrimination.
// Use the generated NewUnionXxxFromYyy constructors to create typed values,
// and the AsYyy() accessors to extract them.
type AspireUnion struct {
	Value any
}

// NewAspireUnion wraps v in an AspireUnion.
// If v is already an *AspireUnion, it is returned unchanged (no double-wrapping).
func NewAspireUnion(v any) *AspireUnion {
	if u, ok := v.(*AspireUnion); ok {
		return u
	}
	return &AspireUnion{Value: v}
}

// MarshalJSON serializes the held value for sending to the server.
func (u *AspireUnion) MarshalJSON() ([]byte, error) {
	return json.Marshal(SerializeValue(u.Value))
}

// ── SerializeValue ────────────────────────────────────────────────────────────

// SerializeValue converts a value to its JSON-serializable representation.
// Handles handles, reference expressions, handle-reference types, slices, and maps recursively.
func SerializeValue(value any) any {
	if value == nil {
		return nil
	}

	switch v := value.(type) {
	case *Handle:
		return v.ToJSON()
	case *ReferenceExpression:
		return v.ToJSON()
	case interface{ ToJSON() map[string]any }:
		return v.ToJSON()
	case interface{ Handle() *Handle }:
		return v.Handle().ToJSON()
	case []any:
		result := make([]any, len(v))
		for i, item := range v {
			result[i] = SerializeValue(item)
		}
		return result
	case map[string]any:
		result := make(map[string]any)
		for k, val := range v {
			result[k] = SerializeValue(val)
		}
		return result
	case fmt.Stringer:
		return v.String()
	default:
		return value
	}
}
